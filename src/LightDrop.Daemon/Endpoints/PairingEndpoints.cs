using LightDrop.Core.Contracts;
using LightDrop.Core.Pairing;
using LightDrop.Daemon.Pairing;
using LightDrop.Daemon.Peers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace LightDrop.Daemon.Endpoints;

/// <summary>
/// The loopback API <c>lightdrop pair</c>, <c>unpair</c> and <c>peers --trusted</c> drive.
/// </summary>
/// <remarks>
/// Loopback only, behind the origin check like everything else on the loopback app. That check
/// is what keeps a web page the user happens to have open from starting a pairing with a device
/// an attacker controls -- these are the first write endpoints it guards.
/// <para>
/// The CLI owns the terminal and the daemon owns the session: the CLI starts a session, polls it,
/// shows the code and posts the user's answer. Nothing here blocks waiting for the other device.
/// </para>
/// </remarks>
internal static class PairingEndpoints
{
    public static IEndpointRouteBuilder MapPairingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/pairing", StartAsync);

        endpoints.MapGet("/api/pairing", Results<Ok<PairingStatus>, NotFound<ApiError>> (
                [FromServices] PairingCoordinator coordinator) =>
            coordinator.GetStatus() is { } status
                ? TypedResults.Ok(status)
                : TypedResults.NotFound(Error("No pairing session has been started.")));

        endpoints.MapPost("/api/pairing/confirm", Results<Ok<PairingStatus>, NotFound<ApiError>, Conflict<ApiError>> (
                [FromBody] PairingDecision decision,
                [FromServices] PairingCoordinator coordinator) =>
        {
            var (status, applied) = coordinator.Decide(decision.Accepted);

            if (status is null)
            {
                return TypedResults.NotFound(Error("No pairing session has been started."));
            }

            return applied
                ? TypedResults.Ok(status)
                : TypedResults.Conflict(Error(status.Reason ?? "There is no code waiting for an answer."));
        });

        endpoints.MapDelete("/api/pairing", Results<NoContent, NotFound> (
                [FromServices] PairingCoordinator coordinator) =>
            coordinator.Cancel() ? TypedResults.NoContent() : TypedResults.NotFound());

        endpoints.MapGet("/api/trusted", async Task<Ok<TrustedPeerListResponse>> (
                [FromServices] PairingService pairing,
                CancellationToken cancellationToken) =>
        {
            var trusted = await pairing.ListAsync(cancellationToken).ConfigureAwait(false);

            // Projected rather than returned as stored: the pinned key stays in the daemon.
            return TypedResults.Ok(new TrustedPeerListResponse
            {
                Peers = [.. trusted.Select(peer => new TrustedPeerSummary
                {
                    DeviceId = peer.DeviceId,
                    DeviceName = peer.DeviceName,
                    PairedAt = peer.PairedAt,
                })],
            });
        });

        endpoints.MapDelete("/api/trusted/{deviceId}", async Task<Results<NoContent, NotFound>> (
                string deviceId,
                [FromServices] PairingCoordinator coordinator,
                CancellationToken cancellationToken) =>
            await coordinator.UnpairAsync(deviceId, cancellationToken).ConfigureAwait(false)
                ? TypedResults.NoContent()
                : TypedResults.NotFound());

        return endpoints;
    }

    private static async Task<Results<Ok<PairingStatus>, NotFound<ApiError>, BadRequest<ApiError>, Conflict<ApiError>, InternalServerError<ApiError>>> StartAsync(
        [FromBody] StartPairingRequest request,
        [FromServices] PairingCoordinator coordinator,
        [FromServices] PeerListener listener,
        CancellationToken cancellationToken)
    {
        var result = await coordinator.StartAsync(request.Peer, cancellationToken).ConfigureAwait(false);

        switch (result.Outcome)
        {
            case PairingStartOutcome.NotFound:
                return TypedResults.NotFound(Error(result.Error!));
            case PairingStartOutcome.Ambiguous:
                return TypedResults.BadRequest(Error(result.Error!));
            case PairingStartOutcome.AlreadyPaired or PairingStartOutcome.Busy:
                return TypedResults.Conflict(Error(result.Error!));
        }

        try
        {
            // After the session opened, not before: a refused start must not leave a LAN socket
            // behind. The other device dialing in the moment between is harmless -- it retries.
            await listener.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            var message = $"Could not open the peer listener: {ex.Message}";
            coordinator.Fail(message);
            return TypedResults.InternalServerError(Error(message));
        }

        return TypedResults.Ok(coordinator.GetStatus() ?? result.Status!);
    }

    private static ApiError Error(string message) => new() { Error = message };
}
