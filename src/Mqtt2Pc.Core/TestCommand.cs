using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mqtt2Pc.Core;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TestCommand(string Id, string? Message)
{
    public const int MaximumPayloadBytes = 1024;
    public const string DefaultMessage = "MQTT connection test received.";

    public static TestCommand Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is 0 or > MaximumPayloadBytes)
            throw new ArgumentException("The command must contain 1–1024 bytes of JSON.");
        var command = JsonSerializer.Deserialize<TestCommand>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new ArgumentException("The command must be a JSON object.");
        if (string.IsNullOrWhiteSpace(command.Id) || command.Id.Length > 64 ||
            command.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            throw new ArgumentException("Provide an id containing 1–64 letters, digits, hyphens or underscores.");
        var message = command.Message ?? DefaultMessage;
        if (string.IsNullOrWhiteSpace(message) || message.Length > 200 || message.Any(char.IsControl))
            throw new ArgumentException("The message must contain 1–200 characters, without control characters.");
        return command with { Message = message };
    }
}
