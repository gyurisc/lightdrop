namespace LightDrop.Daemon.Infrastructure;

/// <summary>
/// Where the JSON stores read and write.
/// </summary>
/// <remarks>
/// Injected rather than resolved inside the stores so tests can point them at a temporary
/// directory instead of the real user profile.
/// </remarks>
public sealed class StorageOptions
{
    public const string ConfigFileName = "config.json";
    public const string StateFileName = "state.json";

    public string DataDirectory { get; set; } = LightDropDirectories.Data;

    public string ConfigFilePath => Path.Combine(DataDirectory, ConfigFileName);

    public string StateFilePath => Path.Combine(DataDirectory, StateFileName);
}
