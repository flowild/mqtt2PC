using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Mqtt2Pc.Core;

public enum PowerAction { Lock, Sleep, Shutdown, Restart }

public sealed record PowerControls
{
    public bool Lock { get; init; }
    public bool Sleep { get; init; }
    public bool Shutdown { get; init; }
    public bool Restart { get; init; }

    public bool IsEnabled(PowerAction action) => action switch
    {
        PowerAction.Lock => Lock, PowerAction.Sleep => Sleep,
        PowerAction.Shutdown => Shutdown, PowerAction.Restart => Restart,
        _ => false
    };
}

public sealed record ConfiguredAction
{
    public string Id { get; init; } = $"action-{Guid.NewGuid():N}"[..15];
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public string Executable { get; init; } = "";
    public string[] Arguments { get; init; } = [];
    public string WorkingDirectory { get; init; } = "";

    [JsonIgnore]
    public bool IsBatchFile => string.Equals(Path.GetExtension(Executable), ".bat", StringComparison.OrdinalIgnoreCase);

    public void Validate()
    {
        if (Id is null || !Regex.IsMatch(Id, "\\A[a-z0-9][a-z0-9_-]{0,47}\\z"))
            throw new ArgumentException("Action IDs must contain 1–48 lowercase letters, digits, hyphens or underscores.");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 80 || Name.Any(char.IsControl))
            throw new ArgumentException("Action names must contain 1–80 characters without control characters.");
        if (string.IsNullOrWhiteSpace(Executable) || Executable.Length > 1024 || Executable.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(Executable) ||
            !(Path.GetExtension(Executable).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
              Path.GetExtension(Executable).Equals(".com", StringComparison.OrdinalIgnoreCase) || IsBatchFile))
            throw new ArgumentException("Choose an absolute path to an .exe, .com or .bat file for the action.");
        if (Arguments is null || Arguments.Length > 64 ||
            Arguments.Any(arg => arg is null || arg.Length > 2048 || arg.Any(char.IsControl)) ||
            Arguments.Sum(arg => arg.Length) > 8192)
            throw new ArgumentException("Use at most 64 arguments, each up to 2048 characters, totaling at most 8192 characters without control characters.");
        if (IsBatchFile && Executable.Length + Arguments.Sum(arg => arg.Length + 3) + 2 > 8000)
            throw new ArgumentException("The batch file path and arguments must total at most 8000 characters, including quoting.");
        if (WorkingDirectory is null || WorkingDirectory.Length > 1024 || WorkingDirectory.Any(char.IsControl) ||
            (WorkingDirectory.Length != 0 && !Path.IsPathFullyQualified(WorkingDirectory)))
            throw new ArgumentException("The working directory must be empty or an absolute directory path.");
    }
}

public interface IPcActions
{
    Task ExecutePowerAsync(PowerAction action, CancellationToken token);
    Task<int> LaunchAsync(ConfiguredAction action, CancellationToken token);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ActionCommand(string Id)
{
    public static ActionCommand Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is 0 or > TestCommand.MaximumPayloadBytes)
            throw new ArgumentException("Supply a JSON object containing an id.");
        var command = JsonSerializer.Deserialize<ActionCommand>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new ArgumentException("Supply a JSON object containing an id.");
        if (string.IsNullOrWhiteSpace(command.Id) || command.Id.Length > 64 ||
            command.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            throw new ArgumentException("Provide an id containing 1–64 letters, digits, hyphens or underscores.");
        return command;
    }
}
