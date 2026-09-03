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
    /// Reads are always allowed: they expose nothing a local page could not learn anyway, and the
    /// page itself is one. Writes must prove origin — by the <c>Origin</c> header when the browser
    /// sent one, and otherwise by <c>Host</c>, which is the CLI's case since it sends no
    /// <c>Origin</c> at all.
    /// </remarks>
    public static bool IsAllowed(string method, string? origin, string? host, DaemonEndpointOptions endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
        {
            return true;
        }

        var expected = endpoint.ClientAddress.Authority;

        if (!string.IsNullOrEmpty(origin))
        {
            // Parsing rather than string comparison so the literal "null" that sandboxed iframes
            // and file:// pages send fails here rather than matching something by accident.
            return Uri.TryCreate(origin, UriKind.Absolute, out var parsed)
                && string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && string.Equals(parsed.Authority, expected, StringComparison.OrdinalIgnoreCase);
        }

        // Checking Host rather than the connection's remote address closes DNS rebinding: a name
        // the attacker controls can resolve to 127.0.0.1, and such a request arrives on loopback
        // looking local while carrying the attacker's host name.
        return string.Equals(host, expected, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class LoopbackOriginMiddleware
{
    /// <summary>
    /// Applies <see cref="LoopbackOriginPolicy"/> to every request.
    /// </summary>
    /// <remarks>
    /// Registered ahead of routing so it also covers requests no route serves. A rejected request
    /// gets 403 and no body — there is nothing useful to tell a caller that should not be here.
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
