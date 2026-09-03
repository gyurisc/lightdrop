using LightDrop.Core.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace LightDrop.Daemon.Security;

/// <summary>
/// Rejects state-changing requests that did not come from this machine's own LightDrop page.
/// </summary>
/// <remarks>
/// <strong>Loopback binding is not access control against a browser.</strong> It keeps every other
/// device on the network out, but any page the user happens to have open can send a request to
/// 127.0.0.1, and the browser will attach their cookies and run it. Once pairing gains a POST
/// endpoint, that would be enough for a hostile page to pair this machine with an attacker already
/// on the LAN.
/// <para>
/// Shipped before any endpoint needs it, deliberately: a check added alongside the first write
/// endpoint is a check the second one has to remember.
/// </para>
/// </remarks>
internal static class LoopbackOriginPolicy
{
    /// <summary>
    /// Whether the daemon should act on this request.
    /// </summary>
    /// <remarks>
    /// <c>Host</c> is checked for every method, reads included. Binding to loopback does not stop
    /// DNS rebinding: an attacker-controlled name can resolve to 127.0.0.1, and the browser's
    /// same-origin check is fooled because the request looks same-origin to the page that sent it
    /// — the daemon is the only party that can see the mismatched <c>Host</c> header and refuse
    /// it. Without this, <c>curl -H "Host: attacker.example" http://127.0.0.1:5533/health</c>
    /// (or the equivalent from a hostile page via rebinding) reads the device identity and every
    /// peer's LAN address; Phase B adds a <c>GET</c> for the pinned peer list, which would leak
    /// the same way. Writes additionally need the stronger <c>Origin</c> check below — <c>Host</c>
    /// alone would let any page on this machine post to the daemon, since the browser sends the
    /// same <c>Host</c> for every same-machine request regardless of which page issued it.
    /// </remarks>
    public static bool IsAllowed(string method, string? origin, string? host, DaemonEndpointOptions endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var expected = endpoint.ClientAddress.Authority;

        if (!string.Equals(host, expected, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(origin))
        {
            // Parsing rather than string comparison so the literal "null" that sandboxed iframes
            // and file:// pages send fails here rather than matching something by accident.
            return Uri.TryCreate(origin, UriKind.Absolute, out var parsed)
                && string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && string.Equals(parsed.Authority, expected, StringComparison.OrdinalIgnoreCase);
        }

        // No Origin header at all is the CLI's case; Host already matched above.
        return true;
    }
}

internal static class LoopbackOriginMiddleware
{
    /// <summary>
    /// Applies <see cref="LoopbackOriginPolicy"/> to every request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>WebApplication</c> auto-inserts <c>UseRouting</c> ahead of anything registered with
    /// <c>Use</c>, so this middleware actually runs <em>after</em> route matching and before
    /// endpoint execution — not "ahead of routing" as an earlier version of this comment claimed.
    /// It still covers every mapped endpoint and unmatched routes alike, but a route marked
    /// <c>.ShortCircuit()</c> answers before endpoint execution and would bypass this check
    /// entirely; do not short-circuit an endpoint that needs origin/Host protection.
    /// </para>
    /// <para>
    /// <strong>This applies to every listener on this <see cref="WebApplication"/>.</strong> M2
    /// adds an ephemeral LAN listener in the same process for inbound pairing; a POST arriving on
    /// that listener carries <c>Host: &lt;LAN IP&gt;:&lt;port&gt;</c>, which will not match the
    /// loopback authority checked here and will get a silent 403. Before that listener exists,
    /// scope this check — by <see cref="Microsoft.AspNetCore.Http.HttpContext.Connection"/>'s
    /// <c>LocalPort</c>, or as endpoint metadata — so it only applies to the loopback listener.
    /// </para>
    /// A rejected request gets 403 and no body — there is nothing useful to tell a caller that
    /// should not be here.
    /// </remarks>
    public static WebApplication UseLoopbackOriginCheck(this WebApplication app, DaemonEndpointOptions endpoint)
    {
        app.Use(async (context, next) =>
        {
            if (!LoopbackOriginPolicy.IsAllowed(
                    context.Request.Method,
                    context.Request.Headers.Origin.ToString(),
                    context.Request.Host.Value,
                    endpoint))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context).ConfigureAwait(false);
        });

        return app;
    }
}
