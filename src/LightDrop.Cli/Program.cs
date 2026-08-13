using LightDrop.Cli;
using LightDrop.Cli.Commands;
using LightDrop.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;

using var cancellation = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    // Cancel cooperatively rather than letting the runtime kill the process, so an in-process
    // daemon gets to shut down gracefully.
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

// Resolved before anything else so a bad environment variable prints one clear line rather
// than an unhandled exception from inside DI registration.
DaemonEndpointOptions endpoint;
try
{
    // Endpoint resolution is shared with the daemon, so `lightdrop health` always looks where
    // `lightdrop daemon` binds.
    endpoint = DaemonEndpointOptions.FromEnvironment();
}
catch (InvalidOperationException ex)
{
    await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
    return 1;
}

var services = new ServiceCollection();

services.AddSingleton(endpoint);
services.AddHttpClient();

services.AddSingleton<ICliCommand, DaemonCommand>();
services.AddSingleton<ICliCommand, HealthCommand>();

await using var provider = services.BuildServiceProvider();

var commands = provider.GetServices<ICliCommand>()
    .ToDictionary(command => command.Name, StringComparer.OrdinalIgnoreCase);

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintUsage(commands.Values);
    return args.Length == 0 ? 1 : 0;
}

if (!commands.TryGetValue(args[0], out var selected))
{
    await Console.Error.WriteLineAsync($"Unknown command '{args[0]}'.").ConfigureAwait(false);
    PrintUsage(commands.Values);
    return 1;
}

try
{
    return await selected.ExecuteAsync(args[1..], cancellation.Token).ConfigureAwait(false);
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    // Ctrl+C is a normal way to stop the daemon, not a failure. The usual shutdown path does not
    // throw at all — the host stops cleanly and the command returns 0 — so this only covers
    // cancellation landing while the host is still starting.
    return 0;
}
catch (Exception ex) when (ex is InvalidOperationException or IOException)
{
    // InvalidOperationException: configuration and state problems, whose messages are written
    // for humans. IOException: Kestrel failing to bind, overwhelmingly because the port is
    // already taken by another daemon. Neither should reach the user as a stack trace.
    await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
    return 1;
}

static void PrintUsage(IEnumerable<ICliCommand> commands)
{
    // ASCII only: legacy Windows consoles run a non-UTF-8 code page and render an em dash as '?'.
    Console.WriteLine("LightDrop - zero-config local sharing between your own devices.");
    Console.WriteLine();
    Console.WriteLine("Usage: lightdrop <command>");
    Console.WriteLine();
    Console.WriteLine("Commands:");

    foreach (var command in commands.OrderBy(command => command.Name, StringComparer.Ordinal))
    {
        Console.WriteLine($"  {command.Name,-10}{command.Description}");
    }

    Console.WriteLine();
    Console.WriteLine("Environment:");
    Console.WriteLine($"  {DaemonEndpointOptions.HostEnvironmentVariable,-16}listen address (default {DaemonEndpointOptions.DefaultHost})");
    Console.WriteLine($"  {DaemonEndpointOptions.PortEnvironmentVariable,-16}listen port (default {DaemonEndpointOptions.DefaultPort})");
}
