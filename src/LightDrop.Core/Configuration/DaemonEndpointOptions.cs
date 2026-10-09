using System.Globalization;
using System.Net;

namespace LightDrop.Core.Configuration;

/// <summary>
/// Where the daemon listens, and where clients look for it.
/// </summary>
/// <remarks>
/// Lives in Core because both sides need it: the daemon binds Kestrel to this address and
/// the CLI resolves the same address to reach it. Deliberately kept out of <c>config.json</c>
/// to keep that file tiny — the environment variables exist as a development escape hatch,
/// mainly so two daemons can run on one machine while testing discovery and pairing.
/// </remarks>
public sealed class DaemonEndpointOptions
{
    public const string HostEnvironmentVariable = "LIGHTDROP_HOST";
    public const string PortEnvironmentVariable = "LIGHTDROP_PORT";
    public const string PeerPortEnvironmentVariable = "LIGHTDROP_PEER_PORT";

    /// <summary>
    /// Loopback by default. This is the CLI- and browser-facing API, and it has no
    /// authentication of its own: binding it to the LAN would expose it to every device on the
    /// network. Peers reach this device on <see cref="PeerPort"/> instead, through mutual TLS.
    /// </summary>
    public const string DefaultHost = "127.0.0.1";

    public const int DefaultPort = 5533;

    /// <summary>
    /// Every interface: the peer listener exists to be reachable from the LAN. What keeps strangers
    /// out is the pinned TLS handshake, not this address.
    /// </summary>
    public const string DefaultPeerHost = "0.0.0.0";

    private readonly int? _peerPort;

    public string Host { get; init; } = DefaultHost;

    public int Port { get; init; } = DefaultPort;

    /// <summary>
    /// Where the peer listener binds. Not read from the environment: it is here so tests can keep
    /// the listener on loopback, never opening a LAN-reachable socket (or a firewall prompt) from
    /// a test run.
    /// </summary>
    public string PeerHost { get; init; } = DefaultPeerHost;

    /// <summary>
    /// The port peers connect to, and the one advertised in the mDNS SRV record.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="Port"/> + 1 rather than a fixed number, so two daemons on one
    /// machine that already differ in <see cref="Port"/> do not collide here too. The default is
    /// computed, not clamped: <see cref="Validate"/> rejects the 65536 that port 65535 would imply,
    /// instead of quietly picking something else.
    /// </remarks>
    public int PeerPort
    {
        get => _peerPort ?? Port + 1;
        init => _peerPort = value;
    }

    /// <summary>The address the daemon binds to.</summary>
    public Uri BaseAddress => new($"http://{FormatHostForUri(Host)}:{Port}/");

    /// <summary>
    /// The address a client on this machine should call. A daemon bound to a wildcard address
    /// is not reachable <em>at</em> that address, so it is rewritten to loopback.
    /// </summary>
    public Uri ClientAddress => Host is "0.0.0.0" or "::" or "[::]"
        ? new Uri($"http://{DefaultHost}:{Port}/")
        : BaseAddress;

    /// <summary>
    /// Bracket IPv6 literals. Kestrel binds through <see cref="IPAddress.Parse"/>, which accepts
    /// a bare <c>::1</c>, but <see cref="Uri"/> rejects it unbracketed — so without this an IPv6
    /// host binds successfully and then throws the first time the address is formatted.
    /// </summary>
    private static string FormatHostForUri(string host) =>
        host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;

    /// <summary>
    /// Builds options from the environment, falling back to the defaults above.
    /// </summary>
    public static DaemonEndpointOptions FromEnvironment()
    {
        var host = Environment.GetEnvironmentVariable(HostEnvironmentVariable);
        var port = ParsePort(PortEnvironmentVariable);
        var peerPort = ParsePort(PeerPortEnvironmentVariable);

        var options = new DaemonEndpointOptions
        {
            Host = string.IsNullOrWhiteSpace(host) ? DefaultHost : host.Trim(),
            Port = port ?? DefaultPort,
            PeerPort = peerPort ?? (port ?? DefaultPort) + 1,
        };

        options.Validate();
        return options;
    }

    private static int? ParsePort(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return int.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"{variable} must be an integer between 1 and 65535, but was '{value}'.");
    }

    /// <summary>
    /// Fails fast on an unusable endpoint, so a bad value surfaces at startup rather than
    /// as a confusing bind error.
    /// </summary>
    public void Validate()
    {
        if (Port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"Daemon port must be between 1 and 65535, but was {Port.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (!IPAddress.TryParse(Host, out _))
        {
            throw new InvalidOperationException(
                $"Daemon host must be an IP address, but was '{Host}'.");
        }

        if (PeerPort is < 1 or > 65535)
        {
            // Names the variable because the usual cause is not a bad peer port but a loopback
            // port of 65535, whose implied default of Port + 1 does not exist.
            throw new InvalidOperationException(
                $"Peer port must be between 1 and 65535, but was {PeerPort.ToString(CultureInfo.InvariantCulture)}. " +
                $"It defaults to the daemon port + 1; set {PeerPortEnvironmentVariable} to choose one.");
        }

        if (PeerPort == Port)
        {
            throw new InvalidOperationException(
                $"Peer port must differ from the daemon port ({Port.ToString(CultureInfo.InvariantCulture)}): " +
                "the peer listener binds every interface, loopback included.");
        }

        if (!IPAddress.TryParse(PeerHost, out _))
        {
            throw new InvalidOperationException(
                $"Peer host must be an IP address, but was '{PeerHost}'.");
        }
    }
}
