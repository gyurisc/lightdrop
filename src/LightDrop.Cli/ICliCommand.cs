namespace LightDrop.Cli;

/// <summary>
/// One <c>lightdrop</c> verb.
/// </summary>
/// <remarks>
/// Deliberately hand-rolled rather than built on a parsing library. With a handful of verbs the
/// dispatch is a dictionary lookup, it adds no dependency, and it keeps commands resolvable from
/// DI. Revisit when a command needs real option parsing — <c>send</c> is the likely trigger.
/// <para>
/// Not to be confused with <see cref="Core.Protocol.ICommandHandler"/>: that is a command a peer
/// sends over the wire, this is a verb the user types.
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
