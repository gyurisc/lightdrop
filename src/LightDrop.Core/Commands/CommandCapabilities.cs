namespace LightDrop.Core.Commands;

/// <summary>
/// The names of the commands this device accepts from its paired peers.
/// </summary>
/// <remarks>
/// <strong>Built from the registered handler set, never written by hand.</strong> The daemon
/// constructs this from the handlers it registers, so <c>/health</c> and the TXT <c>cap</c> key
/// cannot advertise a command that is not there or omit one that is. Lives in Core, as names
/// only, because <see cref="Health.HealthService"/> does; the handlers themselves read request
/// streams and write files and belong to the daemon.
/// <para>
/// A concrete class, not an interface: there is one way to know what is registered.
/// </para>
/// </remarks>
public sealed class CommandCapabilities
{
    public CommandCapabilities(IEnumerable<string> commandNames)
    {
        ArgumentNullException.ThrowIfNull(commandNames);

        // Sorted and de-duplicated so the advertised list is stable from run to run, whatever
        // order DI enumerates the handlers in.
        Names = [.. commandNames.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    public IReadOnlyList<string> Names { get; }
}
