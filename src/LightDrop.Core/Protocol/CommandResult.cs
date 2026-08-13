using System.Text.Json;

namespace LightDrop.Core.Protocol;

/// <summary>
/// The outcome of a dispatched <see cref="CommandEnvelope"/>.
/// </summary>
public sealed record CommandResult
{
    /// <summary>Echoes <see cref="CommandEnvelope.Id"/>.</summary>
    public required string Id { get; init; }

    public required bool Ok { get; init; }

    public JsonElement? Payload { get; init; }

    public CommandError? Error { get; init; }

    public static CommandResult Success(string id, JsonElement? payload = null) =>
        new() { Id = id, Ok = true, Payload = payload };

    public static CommandResult Failure(string id, string code, string message) =>
        new() { Id = id, Ok = false, Error = new CommandError { Code = code, Message = message } };
}

/// <summary>
/// A machine-readable failure. <see cref="Code"/> is for peers to branch on;
/// <see cref="Message"/> is for humans reading logs.
/// </summary>
public sealed record CommandError
{
    /// <summary>The peer does not implement the requested command.</summary>
    public const string UnsupportedCommand = "unsupported_command";

    public required string Code { get; init; }

    public required string Message { get; init; }
}
