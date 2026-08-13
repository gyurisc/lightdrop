using System.Net;
using System.Net.Sockets;

namespace LightDrop.Daemon.Tests.TestSupport;

internal static class FreeTcpPort
{
    /// <summary>
    /// Asks the OS for an unused loopback port.
    /// </summary>
    /// <remarks>
    /// A hardcoded port would flake whenever CI already has something on it.
    /// <see cref="Core.Configuration.DaemonEndpointOptions.Validate"/> rejects port 0, so Kestrel
    /// cannot be asked to pick one itself; binding a listener and releasing it is the standard
    /// alternative. There is a small race between release and rebind, which is acceptable for a
    /// local suite of this size.
    /// </remarks>
    public static int Get()
    {
        var listener = new TcpListener(IPAddress.Loopback, port: 0);
        try
        {
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
