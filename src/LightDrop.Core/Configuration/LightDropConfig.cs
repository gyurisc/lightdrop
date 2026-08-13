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
    /// Where received files land. Nothing reads this yet; the default is resolved by the
    /// infrastructure layer when file transfer lands.
    /// </summary>
    public string? DownloadFolder { get; init; }
}
