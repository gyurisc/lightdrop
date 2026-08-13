namespace LightDrop.Daemon.Tests.TestSupport;

/// <summary>
/// A throwaway data directory for one test.
/// </summary>
/// <remarks>
/// Every test must route storage through one of these. Omitting it would make the test read and
/// write the real user profile — which is the one failure mode this whole suite must never have.
/// </remarks>
internal sealed class TempDataDirectory : IDisposable
{
    public TempDataDirectory()
    {
        FullPath = Path.Combine(Path.GetTempPath(), "lightdrop-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(FullPath);
    }

    public string FullPath { get; }

    public string ConfigFilePath => Path.Combine(FullPath, "config.json");

    public string StateFilePath => Path.Combine(FullPath, "state.json");

    public void WriteConfig(string contents) => File.WriteAllText(ConfigFilePath, contents);

    public void WriteState(string contents) => File.WriteAllText(StateFilePath, contents);

    public void Dispose()
    {
        try
        {
            Directory.Delete(FullPath, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leaked temp directory must never fail a test run.
        }
    }
}
