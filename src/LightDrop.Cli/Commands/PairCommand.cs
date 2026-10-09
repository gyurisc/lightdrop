using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LightDrop.Core.Configuration;
using LightDrop.Core.Contracts;
using LightDrop.Core.Pairing;

namespace LightDrop.Cli.Commands;

/// <summary>
/// <c>lightdrop pair &lt;device&gt;</c> -- pairs with a nearby device by comparing a six-digit code.
/// </summary>
/// <remarks>
/// <strong>The CLI owns the terminal, the daemon owns the session.</strong> This starts a session
/// over loopback, polls it, prints the code, asks the question and posts the answer. It never
/// talks to the other device itself, and nothing it prints came from the other device except the
/// name discovery already sanitized.
/// <para>
/// Ctrl+C cancels the session in the daemon, not just this process: a window left open after the
/// user walked away would keep admitting any certificate until it expired.
/// </para>
/// </remarks>
internal sealed class PairCommand(IHttpClientFactory httpClientFactory, DaemonEndpointOptions endpoint) : ICliCommand
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);

    public string Name => "pair";

    public string Description => "Pair with a nearby device by comparing a six-digit code.";

    public async Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        // Joined so an unquoted name with spaces still works: `lightdrop pair Mac Mini`.
        var peer = string.Join(' ', args).Trim();
        if (peer.Length == 0)
        {
            await Console.Error.WriteLineAsync("Usage: lightdrop pair <device name or id prefix>").ConfigureAwait(false);
            await Console.Error.WriteLineAsync("Run 'lightdrop peers' to see nearby devices.").ConfigureAwait(false);
            return 1;
        }

        using var client = DaemonApi.Create(httpClientFactory, endpoint);

        try
        {
            return await PairAsync(client, peer, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine();

            if (await CancelSessionAsync(client).ConfigureAwait(false) is { } settled)
            {
                // Too late to cancel: both sides had already said yes, so the daemon finished the
                // pairing. Saying "nothing was saved" over a pin on disk would send the user to
                // pair again, only to be told to unpair first.
                return Report(settled);
            }

            Console.WriteLine("Pairing cancelled. Nothing was saved.");
            return 1;
        }
        catch (HttpRequestException)
        {
            return await DaemonApi.ReportUnreachableAsync(endpoint.ClientAddress).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            await Console.Error.WriteLineAsync(
                $"Daemon at {endpoint.ClientAddress} did not respond within {DaemonApi.RequestTimeout.TotalSeconds:0} seconds.")
                .ConfigureAwait(false);
            return 1;
        }
        catch (JsonException ex)
        {
            await Console.Error.WriteLineAsync(
                $"Daemon at {endpoint.ClientAddress} returned a response this build could not parse: {ex.Message}")
                .ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<int> PairAsync(HttpClient client, string peer, CancellationToken cancellationToken)
    {
        PairingStatus status;
        using (var started = await client
                   .PostAsJsonAsync("api/pairing", new StartPairingRequest { Peer = peer },
                       LightDropJsonContext.Default.StartPairingRequest, cancellationToken)
                   .ConfigureAwait(false))
        {
            if (!started.IsSuccessStatusCode)
            {
                await Console.Error.WriteLineAsync(
                    await DaemonApi.ReadErrorAsync(started, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
                return 1;
            }

            status = (await started.Content
                .ReadFromJsonAsync(LightDropJsonContext.Default.PairingStatus, cancellationToken)
                .ConfigureAwait(false))!;
        }

        var thisDevice = await GetLocalNameAsync(client, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"Pairing with {status.PeerName} ({DaemonApi.ShortId(status.PeerDeviceId)}).");
        Console.WriteLine($"On {status.PeerName}, run:  lightdrop pair \"{thisDevice}\"");
        Console.WriteLine($"Waiting up to {PairingSession.JoinTimeout.TotalSeconds:0} seconds for it. Press Ctrl+C to cancel.");

        status = await PollUntilAsync(client, s => s.State != PairingState.WaitingForPeer, cancellationToken)
            .ConfigureAwait(false);

        if (status.State != PairingState.Confirming || status.Code is null)
        {
            return Report(status);
        }

        PrintCode(status);

        // The prompt races the session: if the other side says no, or the window expires, while
        // this user is still reading, the outcome is printed rather than waiting on a keypress
        // that can no longer matter. The abandoned read dies with the process.
        var answer = Task.Run(Console.ReadLine, CancellationToken.None);
        var ended = PollUntilAsync(client, IsEnded, cancellationToken);

        if (await Task.WhenAny(answer, ended).ConfigureAwait(false) == ended)
        {
            Console.WriteLine();
            return Report(await ended.ConfigureAwait(false));
        }

        var accepted = IsYes(await answer.ConfigureAwait(false));

        using (var confirmed = await client
                   .PostAsJsonAsync("api/pairing/confirm", new PairingDecision { Accepted = accepted },
                       LightDropJsonContext.Default.PairingDecision, cancellationToken)
                   .ConfigureAwait(false))
        {
            // A 409 means the session ended while the user was typing; the poll below reports how.
            if (!confirmed.IsSuccessStatusCode && confirmed.StatusCode != HttpStatusCode.Conflict)
            {
                await Console.Error.WriteLineAsync(
                    await DaemonApi.ReadErrorAsync(confirmed, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
                return 1;
            }
        }

        if (accepted)
        {
            Console.WriteLine($"Waiting for {status.PeerName} to confirm too...");
        }

        return Report(await ended.ConfigureAwait(false));
    }

    private static void PrintCode(PairingStatus status)
    {
        var code = status.Code!;

        Console.WriteLine();
        Console.WriteLine($"    {code[..3]} {code[3..]}");
        Console.WriteLine();
        Console.WriteLine($"{status.PeerName} must be showing exactly the same code. If it shows a");
        Console.WriteLine("different one, answer no: something on the network is in the middle.");
        Console.Write("Do the codes match on both screens? [y/N] ");
    }

    private static int Report(PairingStatus status)
    {
        if (status.State == PairingState.Paired)
        {
            Console.WriteLine($"Paired with {status.PeerName}.");
            return 0;
        }

        Console.WriteLine($"Not paired. {status.Reason ?? "The session ended."}");
        return 1;
    }

    private static bool IsEnded(PairingStatus status) =>
        status.State is not (PairingState.WaitingForPeer or PairingState.Confirming);

    /// <summary>Only an explicit yes. Enter, anything unexpected, or end of input is a no.</summary>
    private static bool IsYes(string? answer) =>
        answer?.Trim().ToUpperInvariant() is "Y" or "YES";

    private static async Task<PairingStatus> PollUntilAsync(
        HttpClient client,
        Func<PairingStatus, bool> done,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var status = await client
                .GetFromJsonAsync("api/pairing", LightDropJsonContext.Default.PairingStatus, cancellationToken)
                .ConfigureAwait(false);

            if (status is not null && done(status))
            {
                return status;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> GetLocalNameAsync(HttpClient client, CancellationToken cancellationToken)
    {
        try
        {
            var health = await client
                .GetFromJsonAsync("health", LightDropJsonContext.Default.HealthResponse, cancellationToken)
                .ConfigureAwait(false);

            return health?.DeviceName ?? "this device";
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            // Only used in an instruction line; not worth failing the pairing over.
            return "this device";
        }
    }

    /// <summary>
    /// Closes the window in the daemon after Ctrl+C. Best effort, with its own deadline.
    /// </summary>
    /// <returns>
    /// Null if the session was cancelled, or the daemon could not be asked. Otherwise the session
    /// had already ended or could no longer be cancelled -- both sides had accepted -- and this is
    /// how it ended.
    /// </returns>
    private static async Task<PairingStatus?> CancelSessionAsync(HttpClient client)
    {
        using var timeout = new CancellationTokenSource(DaemonApi.RequestTimeout);
        try
        {
            using (var cancelled = await client.DeleteAsync("api/pairing", timeout.Token).ConfigureAwait(false))
            {
                if (cancelled.IsSuccessStatusCode)
                {
                    return null;
                }
            }

            // The pin is one small file write; wait it out rather than guess.
            return await PollUntilAsync(client, IsEnded, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            // The window expires on its own within a minute either way.
            return null;
        }
    }
}
