using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace LightDrop.Daemon.Endpoints;

internal static class UiEndpoints
{
    private const string ResourceName = "LightDrop.Daemon.Ui.index.html";

    /// <summary>
    /// Maps <c>GET /</c>: the page a browser loads.
    /// </summary>
    /// <remarks>
    /// Read once at startup and held in memory. The page is a few kilobytes, it cannot change
    /// while the process runs, and loading it here means a missing embedded resource fails at
    /// startup rather than on the first request.
    /// </remarks>
    public static IEndpointRouteBuilder MapUiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var page = LoadPage();

        endpoints.MapGet("/", (HttpContext context) =>
        {
            // frame-ancestors 'none' blocks framing outright: Phase B puts a pairing confirm
            // button on this page, and a clickjacked click would carry a genuine same-origin
            // Origin header that LoopbackOriginPolicy has no way to tell apart from a real one.
            // The rest of the policy is minimal — this page has no external resources — with
            // 'unsafe-inline' only because the style and script are inline in index.html.
            context.Response.Headers.ContentSecurityPolicy =
                "default-src 'self'; frame-ancestors 'none'; style-src 'self' 'unsafe-inline'; " +
                "script-src 'self' 'unsafe-inline'";

            // Stops a browser from sniffing the response into something other than the declared
            // text/html content type.
            context.Response.Headers.XContentTypeOptions = "nosniff";

            return TypedResults.Bytes(page, "text/html; charset=utf-8");
        })
            .WithName("Ui");

        return endpoints;
    }

    private static byte[] LoadPage()
    {
        using var stream = typeof(UiEndpoints).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"The embedded page '{ResourceName}' is missing from the daemon assembly. It is " +
                "declared as an EmbeddedResource in LightDrop.Daemon.csproj.");

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
