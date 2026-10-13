using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using LightDrop.Core.Configuration;
using LightDrop.Core.Contracts;
using LightDrop.Core.Transfers;

namespace LightDrop.Cli.Commands;

/// <summary>
/// <c>lightdrop send &lt;file&gt; &lt;device&gt;</c> -- sends one file to a paired device.
/// </summary>
/// <remarks>
/// The daemon does the sending; this resolves the path, asks, waits and reports. The path is made
/// absolute here because the daemon's working directory is not the user's.
/// <para>
/// <strong>No timeout.</strong> The call returns when the other device has the whole file, which
/// for a large one is minutes. <see cref="HttpClient"/>'s 100-second default would abandon it
/// part way; Ctrl+C is the way to stop, and it does stop it -- closing this request aborts the
/// daemon's upload, and the receiver deletes what it had.
/// </para>
/// <para>
/// <strong>Progress is polled.</strong> The send carries an id this command chose, and while its
/// request waits, a second loop asks <c>GET /api/send/{id}</c> how far it has got and redraws one
/// line. Only on a terminal: redirected output stays as it always was. A daemon too old to know
/// the route just leaves the line blank.
/// </para>
/// </remarks>
internal sealed class SendCommand(IHttpClientFactory httpClientFactory, DaemonEndpointOptions endpoint) : ICliCommand
{
    /// <summary>
    /// How long a send runs before the progress line appears. A small file is done by then, and
    /// prints exactly what it always did.
    /// </summary>
    private static readonly TimeSpan ProgressDelay = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>How far back the speed shown is measured.</summary>
    private static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(5);

    public string Name => "send";

    public string Description => "Send a file to a paired device.";

    public async Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length < 2)
        {
            await Console.Error.WriteLineAsync("Usage: lightdrop send <file> <device name or id prefix>").ConfigureAwait(false);
            await Console.Error.WriteLineAsync("Run 'lightdrop peers --trusted' to list paired devices.").ConfigureAwait(false);
            return 1;
        }

        var path = Path.GetFullPath(args[0]);

        // Joined so an unquoted name with spaces still works: `lightdrop send notes.txt Mac Mini`.
        var peer = string.Join(' ', args[1..]).Trim();

        // Checked here too, not only in the daemon, for the sake of the message: this one can say
        // what the user typed.
        if (!File.Exists(path))
        {
            await Console.Error.WriteLineAsync(Directory.Exists(path)
                ? $"{args[0]} is a folder. LightDrop sends one file at a time."
                : $"There is no file at {path}.").ConfigureAwait(false);
            return 1;
        }

        var client = httpClientFactory.CreateClient();
        client.BaseAddress = endpoint.ClientAddress;
        client.Timeout = Timeout.InfiniteTimeSpan;

        using (client)
        {
            var size = new FileInfo(path).Length;
            Console.WriteLine($"Sending {Path.GetFileName(path)} ({FormatBytes(size)}) to {peer}... Press Ctrl+C to cancel.");

            var transferId = Guid.NewGuid();
            var stopwatch = Stopwatch.StartNew();

            using var stopProgress = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var line = new StatusLine();
            var progress = Console.IsOutputRedirected
                ? Task.CompletedTask
                : ShowProgressAsync(client, transferId, stopwatch, line, stopProgress.Token);

            try
            {
                HttpResponseMessage sending;
                try
                {
                    sending = await client
                        .PostAsJsonAsync(
                            "api/send",
                            new SendRequest { Path = path, Peer = peer, TransferId = transferId },
                            LightDropJsonContext.Default.SendRequest,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    // However the send ended, the line goes before anything else is printed.
                    await stopProgress.CancelAsync().ConfigureAwait(false);
                    try
                    {
                        await progress.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // How it stops.
                    }

                    line.Clear();
                }

                using var response = sending;

                if (!response.IsSuccessStatusCode)
                {
                    await Console.Error.WriteLineAsync(
                        await DaemonApi.ReadErrorAsync(response, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
                    return 1;
                }

                var sent = (await response.Content
                    .ReadFromJsonAsync(LightDropJsonContext.Default.SendResponse, cancellationToken)
                    .ConfigureAwait(false))!;
                stopwatch.Stop();

                Console.WriteLine($"Sent to {sent.PeerName}, saved there as {sent.SavedAs}.");
                Console.WriteLine($"{FormatBytes(sent.Length)} in {FormatDuration(stopwatch.Elapsed)}{FormatThroughput(sent.Length, stopwatch.Elapsed)}.");
                return 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine();
                Console.WriteLine("Cancelled. The other device discards what it received so far.");
                return 1;
            }
            catch (HttpRequestException)
            {
                return await DaemonApi.ReportUnreachableAsync(endpoint.ClientAddress).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                await Console.Error.WriteLineAsync(
                    $"Daemon at {endpoint.ClientAddress} returned a response this build could not parse: {ex.Message}")
                    .ConfigureAwait(false);
                return 1;
            }
        }
    }

    /// <summary>
    /// Redraws the progress line -- how much has gone, the speed, the time left -- until cancelled.
    /// </summary>
    /// <remarks>
    /// A courtesy, never the result: a poll that fails is skipped, and the send's own request
    /// reports how it ended. Not found means not started yet, already finished, or a daemon that
    /// predates the route; all three mean "nothing to show".
    /// </remarks>
    private static async Task ShowProgressAsync(
        HttpClient client,
        Guid transferId,
        Stopwatch stopwatch,
        StatusLine line,
        CancellationToken cancellationToken)
    {
        var rate = new TransferRate(SpeedWindow);
        await Task.Delay(ProgressDelay, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            try
            {
                using var response = await client
                    .GetAsync($"api/send/{transferId}", cancellationToken)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var progress = await response.Content
                        .ReadFromJsonAsync(LightDropJsonContext.Default.SendProgress, cancellationToken)
                        .ConfigureAwait(false);

                    if (progress is not null)
                    {
                        rate.Observe(stopwatch.Elapsed, progress.Sent);
                        line.Show(Describe(progress, rate));
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
            {
                // Skipped; the next poll may do better.
            }

            await Task.Delay(ProgressInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary><c>  1.2 GB of 4.5 GB  27%  58.3 MB/s  about 1 min left</c></summary>
    private static string Describe(SendProgress progress, TransferRate rate)
    {
        // Rounded down, so 100% means all of it.
        var percent = progress.Length == 0 ? 100 : (int)Math.Floor(progress.Sent * 100.0 / progress.Length);
        var text = $"  {FormatBytes(progress.Sent)} of {FormatBytes(progress.Length)}  {percent}%";

        // All read from the file, but the network may still be carrying the last of it, and the
        // receiver has yet to say it saved it.
        if (progress.Sent >= progress.Length)
        {
            return $"{text}  finishing";
        }

        if (rate.BytesPerSecond is { } speed && speed > 0)
        {
            text += $"  {FormatBytes((long)speed)}/s";
        }

        if (rate.TimeLeft(progress.Length) is { } left)
        {
            text += $"  about {FormatTimeLeft(left)} left";
        }

        return text;
    }

    private static string FormatTimeLeft(TimeSpan left) =>
        left.TotalSeconds < 60 ? $"{Math.Max(1, (int)Math.Ceiling(left.TotalSeconds))} s"
        : left.TotalMinutes < 60 ? $"{(int)Math.Round(left.TotalMinutes)} min"
        : $"{(int)left.TotalHours} h {left.Minutes} min";

    /// <summary>Binary units with the familiar labels, as file managers show them.</summary>
    private static string FormatBytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes.ToString(CultureInfo.InvariantCulture)} {units[0]}"
            : $"{value.ToString("0.0", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private static string FormatDuration(TimeSpan elapsed) =>
        elapsed.TotalSeconds < 60
            ? $"{elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s"
            : $"{(int)elapsed.TotalMinutes} min {elapsed.Seconds} s";

    private static string FormatThroughput(long bytes, TimeSpan elapsed) =>
        // Meaningless for a file that took a few milliseconds; omitted rather than absurd.
        elapsed.TotalSeconds < 0.05 ? string.Empty : $" ({FormatBytes((long)(bytes / elapsed.TotalSeconds))}/s)";

    /// <summary>One terminal line, rewritten in place with a carriage return.</summary>
    private sealed class StatusLine
    {
        /// <summary>The widest text shown, so a shorter one and the final clear cover all of it.</summary>
        private int _width;

        public void Show(string text)
        {
            // A line wider than the terminal wraps, and the carriage return then goes back only
            // to the start of the wrapped part, stacking copies down the screen.
            var room = Math.Max(1, TerminalWidth() - 1);
            if (text.Length > room)
            {
                text = text[..room];
            }

            Console.Write($"\r{text.PadRight(_width)}");
            _width = Math.Max(_width, text.Length);
        }

        public void Clear()
        {
            if (_width == 0)
            {
                return;
            }

            // Two more than was written: the terminal echoes Ctrl+C as "^C" after the text.
            Console.Write($"\r{new string(' ', _width + 2)}\r");
            _width = 0;
        }

        private static int TerminalWidth()
        {
            try
            {
                return Console.WindowWidth > 0 ? Console.WindowWidth : 80;
            }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or InvalidOperationException)
            {
                return 80;
            }
        }
    }
}
