namespace LightDrop.Core.Configuration;

/// <summary>
/// User-owned settings, read from <c>config.json</c>.
/// </summary>
/// <remarks>
/// LightDrop only ever <em>reads</em> this file. Anything the application writes belongs in
/// <see cref="LightDropState"/> instead — otherwise a pairing that rewrites the file would
/// clobber whatever the user was editing by hand.
/// <para>
/// Every member is optional. Defaults are resolved in code so that LightDrop runs correctly
/// with no config file present at all.
/// </para>
/// </remarks>
public sealed record LightDropConfig
{
    /// <summary>The name peers see. Defaults to the machine name when unset.</summary>
    public string? DeviceName { get; init; }

    /// <summary>
    /// Where received files land. Defaults to <c>Downloads</c> in the user's home directory.
    /// </summary>
    /// <remarks>
    /// A leading <c>~</c> is the home directory, and a relative path is taken relative to it --
    /// not to the daemon's working directory, which a user editing this file cannot know. Created
    /// if missing. Re-read for every received file, so an edit applies without a restart.
    /// </remarks>
    public string? DownloadFolder { get; init; }
}
