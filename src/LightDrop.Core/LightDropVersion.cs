using System.Reflection;

namespace LightDrop.Core;

/// <summary>
/// Version information for the running LightDrop build.
/// </summary>
public static class LightDropVersion
{
    /// <summary>
    /// The protocol version spoken by this build.
    /// </summary>
    /// <remarks>
    /// This is intentionally decoupled from <see cref="Current"/>. The application version
    /// changes on every release; the protocol version changes only when the wire format
    /// changes in a way peers must negotiate. Additive changes are advertised through
    /// capabilities instead, so this should increment rarely.
    /// </remarks>
    public const int Protocol = 1;

    /// <summary>
    /// The application version, read from the assembly rather than hardcoded so that
    /// <c>Directory.Build.props</c> remains the single source of truth.
    /// </summary>
    public static string Current { get; } = ResolveCurrent();

    private static string ResolveCurrent()
    {
        var informational = typeof(LightDropVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return typeof(LightDropVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        // Strip the source revision suffix the SDK appends, e.g. "0.1.0+9f2c1ab".
        var plus = informational.IndexOf('+');
        return plus >= 0 ? informational[..plus] : informational;
    }
}
