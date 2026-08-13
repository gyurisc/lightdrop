namespace LightDrop.Core.Devices;

/// <summary>
/// The platform token a device advertises to its peers.
/// </summary>
public static class DevicePlatform
{
    public const string Windows = "windows";
    public const string MacOS = "macos";
    public const string Linux = "linux";
    public const string Unknown = "unknown";

    /// <summary>
    /// The platform of the current device.
    /// </summary>
    /// <remarks>
    /// Deliberately a short stable token rather than <c>RuntimeInformation.OSDescription</c>,
    /// which is verbose and changes between OS builds. Peers may branch on this value, so it
    /// has to stay comparable across releases.
    /// </remarks>
    public static string Current { get; } =
        OperatingSystem.IsWindows() ? Windows
        : OperatingSystem.IsMacOS() ? MacOS
        : OperatingSystem.IsLinux() ? Linux
        : Unknown;
}
