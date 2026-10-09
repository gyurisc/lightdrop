using LightDrop.Core.Configuration;
using LightDrop.Daemon.Commands;
using LightDrop.Daemon.Peers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LightDrop.Daemon.Tests;

/// <summary>
/// The dispatcher's own refusal, for a request no pinned-peer filter vouched for.
/// </summary>
/// <remarks>
/// Called directly, with no filter in front: the case a refactor would create by dropping
/// <c>RequirePinnedPeer</c> from the group. That the filter <em>is</em> on the group is
/// <c>FileTransferTests</c>' job; this is the second line behind it.
/// </remarks>
public sealed class CommandDispatchTests
{
    [Fact]
    public async Task ARequestNoFilterVouchedForNeverReachesAHandler()
    {
        var handler = new RecordingHandler();
        var context = new DefaultHttpContext();

        var result = await CommandEndpoints.DispatchAsync(RecordingHandler.CommandName, context, [handler]);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ARequestTheFilterVouchedForReachesItsHandlerAsThatPeer()
    {
        // The control: the same call with the peer the filter would have left behind.
        var handler = new RecordingHandler();
        var context = new DefaultHttpContext();
        var peer = new TrustedPeer
        {
            DeviceId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            DeviceName = "Bob",
            PublicKey = "AAAA",
            PairedAt = DateTimeOffset.UnixEpoch,
        };
        context.Items[PinnedPeerFilter.ItemsKey] = peer;

        await CommandEndpoints.DispatchAsync(RecordingHandler.CommandName, context, [handler]);

        Assert.Equal(1, handler.Calls);
        Assert.Same(peer, handler.LastPeer);
    }

    private sealed class RecordingHandler : ICommandHandler
    {
        public const string CommandName = "Record";

        public int Calls { get; private set; }

        public TrustedPeer? LastPeer { get; private set; }

        public string Name => CommandName;

        public Task<IResult> HandleAsync(CommandRequest request)
        {
            Calls++;
            LastPeer = request.Peer;
            return Task.FromResult<IResult>(TypedResults.Ok());
        }
    }
}
