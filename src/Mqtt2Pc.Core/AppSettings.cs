using System.Net;
using System.Text.RegularExpressions;

namespace Mqtt2Pc.Core;

public sealed record AppSettings
{
    public string BrokerHost { get; init; } = "";
    public int BrokerPort { get; init; } = 1883;
    public bool UseTls { get; init; }
    public string Username { get; init; } = "";
    public string ComputerName { get; init; } = Environment.MachineName;
    public string DeviceId { get; init; } = CreateDeviceId();
    public PowerControls Power { get; init; } = new();
    public ConfiguredAction[] Actions { get; init; } = [];

    public string TopicPrefix => $"mqtt2pc/{DeviceId}";
    public string AvailabilityTopic => $"{TopicPrefix}/availability";
    public string TestCommandTopic => $"{TopicPrefix}/command/test";
    public string VolumeCommandTopic => $"{TopicPrefix}/command/volume";
    public string MuteCommandTopic => $"{TopicPrefix}/command/mute";
    public string AudioStateTopic => $"{TopicPrefix}/state/audio";
    public string AudioAvailabilityTopic => $"{TopicPrefix}/availability/audio";
    public string MediaCommandTopic(string action) => $"{TopicPrefix}/command/media/{action}";
    public string PowerCommandTopic(string action) => $"{TopicPrefix}/command/power/{action}";
    public string ActionCommandTopic(string id) => $"{TopicPrefix}/command/action/{id}";
    public string ResultTopic => $"{TopicPrefix}/result";
    public string ClientId => $"mqtt2pc-{DeviceId}";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BrokerHost) || BrokerHost.Length > 253 ||
            BrokerHost != BrokerHost.Trim() ||
            (Uri.CheckHostName(BrokerHost) == UriHostNameType.Unknown && !IPAddress.TryParse(BrokerHost, out _)))
            throw new ArgumentException("Enter a broker hostname or IP address, without a scheme or port.");
        if (BrokerPort is < 1 or > 65535)
            throw new ArgumentException("The broker port must be between 1 and 65535.");
        if (Username is null || Username.Length > 256 || Username.Any(char.IsControl))
            throw new ArgumentException("The username must be at most 256 characters, without control characters.");
        if (string.IsNullOrWhiteSpace(ComputerName) || ComputerName.Length > 80 || ComputerName.Any(char.IsControl))
            throw new ArgumentException("The PC name must be between 1 and 80 characters.");
        if (DeviceId is null || !Regex.IsMatch(DeviceId, "\\A[a-z0-9][a-z0-9_-]{0,47}\\z"))
            throw new ArgumentException("The device ID must contain 1–48 lowercase letters, digits, hyphens or underscores.");
        if (Power is null) throw new ArgumentException("Power control settings must be present.");
        if (Actions is null || Actions.Length > 32 || Actions.Any(action => action is null))
            throw new ArgumentException("Configure at most 32 actions.");
        foreach (var action in Actions) action.Validate();
        if (Actions.Select(action => action.Id).Distinct(StringComparer.Ordinal).Count() != Actions.Length)
            throw new ArgumentException("Each configured action must have a unique ID.");
    }

    private static string CreateDeviceId()
    {
        var name = Regex.Replace(Environment.MachineName.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (name.Length == 0) name = "pc";
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return $"{name[..Math.Min(name.Length, 30)]}-{suffix}";
    }
}
