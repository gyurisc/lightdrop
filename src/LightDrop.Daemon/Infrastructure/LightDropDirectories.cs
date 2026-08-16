namespace LightDrop.Daemon.Infrastructure;

/// <summary>
/// Resolves the platform-specific locations LightDrop stores things in.
/// </summary>
/// <remarks>
/// Lives in infrastructure, not Core: Core defines the ports and holds the logic, and stays
/// unaware of <see cref="Environment.SpecialFolder"/> and platform layout entirely.
/// <para>
/// <see cref="Environment.SpecialFolder.ApplicationData"/> maps to <c>%APPDATA%</c> on Windows
/// and <c>~/Library/Application Support</c> on macOS. An earlier version of this comment claimed
/// .NET applied the Linux XDG mapping (<c>~/.config</c>) to macOS as well, and argued that the
/// cross-platform CLI convention made it the right choice. That claim is simply false — verified
/// by hand on macOS 15.7.4, where the daemon created <c>state.json</c> under
/// <c>~/Library/Application Support/LightDrop</c>. The platform-native location is kept; only the
/// documentation was wrong. See <c>docs/DECISIONS.md</c> #21.
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
