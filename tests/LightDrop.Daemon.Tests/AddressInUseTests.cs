using System.Net.Sockets;

namespace LightDrop.Daemon.Tests;

/// <summary>
/// Recognising the port already being taken.
/// </summary>
/// <remarks>
/// This is the one branch in <c>lightdrop ui</c>: a bind failure is assumed to be our own daemon,
/// so the browser opens against it instead. Kestrel wraps the socket error, so the check has to
/// walk the chain rather than match the outermost type.
/// </remarks>
public sealed class AddressInUseTests
{
    [Fact]
    public void RecognisesAWrappedAddressInUseError()
    {
        // The shape Kestrel actually throws: an IOException wrapping the socket error.
        var wrapped = new IOException(
            "Failed to bind to address http://127.0.0.1:5533: address already in use.",
            new SocketException((int)SocketError.AddressAlreadyInUse));

        Assert.True(LightDropDaemon.IsAddressInUse(wrapped));
    }

    [Fact]
    public void RecognisesABareSocketError()
    {
        Assert.True(LightDropDaemon.IsAddressInUse(new SocketException((int)SocketError.AddressAlreadyInUse)));
    }

    [Fact]
    public void DoesNotSwallowOtherSocketErrors()
    {
        // Permission denied must not be reported as "already running" -- that would send the user
        // to a browser tab instead of telling them the real problem.
        var denied = new IOException("Failed to bind.", new SocketException((int)SocketError.AccessDenied));

        Assert.False(LightDropDaemon.IsAddressInUse(denied));
    }

    [Fact]
    public void DoesNotSwallowUnrelatedFailures()
    {
        Assert.False(LightDropDaemon.IsAddressInUse(new InvalidOperationException("something else")));
    }
}
