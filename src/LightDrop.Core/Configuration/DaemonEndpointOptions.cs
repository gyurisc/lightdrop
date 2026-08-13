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

    /// <summary>
    /// Loopback by default. There is no pairing or authentication yet, so binding to the LAN
    /// would expose an unauthenticated endpoint to every device on the network. LAN binding
    /// stays a deliberate opt-in until pairing lands.
    /// </summary>
    public const string DefaultHost = "127.0.0.1";

    public const int DefaultPort = 5533;

    public string Host { get; init; } = DefaultHost;

    public int Port { get; init; } = DefaultPort;

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
        var port = Environment.GetEnvironmentVariable(PortEnvironmentVariable);

        var options = new DaemonEndpointOptions
        {
            Host = string.IsNullOrWhiteSpace(host) ? DefaultHost : host.Trim(),
            Port = string.IsNullOrWhiteSpace(port)
                ? DefaultPort
                : int.TryParse(port, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : throw new InvalidOperationException(
                        $"{PortEnvironmentVariable} must be an integer between 1 and 65535, but was '{port}'."),
        };

        options.Validate();
        return options;
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
    }
}
