using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mqtt2Pc.Core;

public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public AppSettings? Load()
    {
        if (!File.Exists(path)) return null;
        var settings = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("The settings file is empty.");
        var result = new AppSettings
        {
            BrokerHost = settings.BrokerHost!, BrokerPort = settings.BrokerPort,
            UseTls = settings.UseTls, Username = settings.Username!,
            ComputerName = settings.ComputerName!, DeviceId = settings.DeviceId!,
            Power = settings.Power!, Actions = settings.Actions!
        };
        result.Validate();
        return result;
    }

    public void Save(AppSettings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new SavedSettings
            {
                BrokerHost = settings.BrokerHost, BrokerPort = settings.BrokerPort,
                UseTls = settings.UseTls, Username = settings.Username,
                ComputerName = settings.ComputerName, DeviceId = settings.DeviceId,
                Power = settings.Power, Actions = settings.Actions
            }, JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private sealed record SavedSettings
    {
        public string? BrokerHost { get; init; }
        public int BrokerPort { get; init; }
        public bool UseTls { get; init; }
        public string? Username { get; init; }
        public string? ComputerName { get; init; }
        public string? DeviceId { get; init; }
        public PowerControls? Power { get; init; } = new();
        public ConfiguredAction[]? Actions { get; init; } = [];
    }
}
