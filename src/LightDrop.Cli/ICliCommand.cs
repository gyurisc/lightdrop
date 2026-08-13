namespace LightDrop.Cli;

/// <summary>
/// One <c>lightdrop</c> verb.
/// </summary>
/// <remarks>
/// Deliberately hand-rolled rather than built on a parsing library. With a handful of verbs the
/// dispatch is a dictionary lookup, it adds no dependency, and it keeps commands resolvable from
/// DI. Revisit when a command needs real option parsing — <c>send</c> is the likely trigger.
/// <para>
/// This is a verb the <em>user types</em>. When the wire protocol gains command handlers, those
/// are a separate concept — see <c>docs/Protocol.md</c>.
/// </para>
/// </remarks>
public interface ICliCommand
{
    /// <summary>The verb, e.g. <c>health</c>.</summary>
    string Name { get; }

    /// <summary>One-line description shown in the usage text.</summary>
    string Description { get; }

    /// <summary>Runs the command and returns the process exit code.</summary>
    Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken);
}
