using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using LightDrop.Core.Configuration;
using LightDrop.Core.Contracts;

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
/// </remarks>
internal sealed class SendCommand(IHttpClientFactory httpClientFactory, DaemonEndpointOptions endpoint) : ICliCommand
{
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

            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var response = await client
                    .PostAsJsonAsync("api/send", new SendRequest { Path = path, Peer = peer },
                        LightDropJsonContext.Default.SendRequest, cancellationToken)
                    .ConfigureAwait(false);

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
}
