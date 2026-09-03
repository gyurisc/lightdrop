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

        endpoints.MapGet("/", FileContentHttpResult () =>
            TypedResults.Bytes(page, "text/html; charset=utf-8"))
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
