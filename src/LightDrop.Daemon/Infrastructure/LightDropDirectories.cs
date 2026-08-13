namespace LightDrop.Daemon.Infrastructure;

/// <summary>
/// Resolves the platform-specific locations LightDrop stores things in.
/// </summary>
/// <remarks>
/// Lives in infrastructure, not Core: Core defines the ports and holds the logic, and stays
/// unaware of <see cref="Environment.SpecialFolder"/> and platform layout entirely.
/// <para>
/// <see cref="Environment.SpecialFolder.ApplicationData"/> maps to <c>%APPDATA%</c> on Windows
/// and <c>~/.config</c> on macOS — .NET applies the same XDG-style mapping to macOS as to Linux
/// rather than using <c>~/Library/Application Support</c>. That matches what most cross-platform
/// developer CLIs do, so it is the convention worth following here.
/// </para>
/// </remarks>
public static class LightDropDirectories
{
    private const string FolderName = "LightDrop";

    /// <summary>Where <c>config.json</c> and <c>state.json</c> live.</summary>
    public static string Data { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        FolderName);
}
