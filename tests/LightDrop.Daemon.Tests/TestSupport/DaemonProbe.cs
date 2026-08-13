using System.Diagnostics;

namespace LightDrop.Daemon.Tests.TestSupport;

internal static class DaemonProbe
{
    private static readonly TimeSpan StartupBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Polls until the daemon answers, or the budget runs out. Returns whether it came up.
    /// </summary>
    /// <remarks>
    /// Polling rather than a fixed delay: a fixed sleep is either slower than needed or too short
    /// on a loaded CI runner, and the second case is a flaky test.
    /// </remarks>
    public static async Task<bool> WaitUntilServingAsync(HttpClient client, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < StartupBudget)
        {
            if (await IsServingAsync(client, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Whether <c>/health</c> currently answers. Connection failures mean "not serving" rather
    /// than a test error, which is what makes this usable for both startup and shutdown checks.
    /// </summary>
    public static async Task<bool> IsServingAsync(HttpClient client, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await client.GetAsync("health", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // A stopped listener refuses the connection on some platforms and drops it on others.
            return false;
        }
    }
}
