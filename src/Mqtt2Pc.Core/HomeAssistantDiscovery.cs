using System.Text.Json;

namespace Mqtt2Pc.Core;

public sealed record DiscoveryMessage(string Topic, string Payload);

public static class HomeAssistantDiscovery
{
    public const string BirthTopic = "homeassistant/status";
    public const string ActionCommandTemplate = "{\"id\":\"{{ now().strftime('%Y%m%d%H%M%S%f') }}\"}";

    public static bool IsDeviceTopic(AppSettings settings, string topic)
    {
        var parts = topic.Split('/');
        return parts.Length == 5 && parts[0] == "homeassistant" && parts[1] is "number" or "switch" or "button" &&
            parts[2] == $"mqtt2pc_{settings.DeviceId}" && parts[3].Length is > 0 and <= 64 &&
            parts[3].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') && parts[4] == "config";
    }

    public static IReadOnlyList<DiscoveryMessage> Create(AppSettings settings)
    {
        var messages = new List<DiscoveryMessage>();
        Dictionary<string, object> Common(string key, string name, string icon, bool audio)
        {
            var availability = new List<object> { new { topic = settings.AvailabilityTopic } };
            if (audio) availability.Add(new { topic = settings.AudioAvailabilityTopic });
            return new()
            {
                ["name"] = name, ["unique_id"] = $"mqtt2pc_{settings.DeviceId}_{key}",
                ["icon"] = icon, ["availability"] = availability, ["availability_mode"] = "all",
                ["device"] = new
                {
                    identifiers = new[] { $"mqtt2pc_{settings.DeviceId}" }, name = settings.ComputerName,
                    manufacturer = "mqtt2PC", model = "Windows PC", sw_version = "0.4.0"
                },
                ["origin"] = new { name = "mqtt2PC", sw_version = "0.4.0" },
                ["retain"] = false
            };
        }

        void Add(string component, string key, Dictionary<string, object> config) => messages.Add(new(
            $"homeassistant/{component}/mqtt2pc_{settings.DeviceId}/{key}/config", JsonSerializer.Serialize(config)));

        var volume = Common("volume", "Volume", "mdi:volume-high", true);
        volume["command_topic"] = settings.VolumeCommandTopic;
        volume["state_topic"] = settings.AudioStateTopic;
        volume["value_template"] = "{{ value_json.volume }}";
        volume["min"] = 0;
        volume["max"] = 100;
        volume["step"] = 1;
        volume["mode"] = "slider";
        volume["unit_of_measurement"] = "%";
        volume["qos"] = 1;
        Add("number", "volume", volume);

        var mute = Common("mute", "Mute", "mdi:volume-mute", true);
        mute["command_topic"] = settings.MuteCommandTopic;
        mute["state_topic"] = settings.AudioStateTopic;
        mute["value_template"] = "{{ 'ON' if value_json.muted else 'OFF' }}";
        mute["payload_on"] = "ON";
        mute["payload_off"] = "OFF";
        mute["optimistic"] = false;
        mute["qos"] = 1;
        Add("switch", "mute", mute);

        foreach (var (key, name, icon) in new[]
        {
            ("play_pause", "Play / pause", "mdi:play-pause"),
            ("next", "Next track", "mdi:skip-next"),
            ("previous", "Previous track", "mdi:skip-previous")
        })
        {
            var button = Common(key, name, icon, false);
            button["command_topic"] = settings.MediaCommandTopic(key);
            button["payload_press"] = "PRESS";
            // Buttons are not idempotent; Home Assistant must not retry a toggle.
            button["qos"] = 0;
            Add("button", key, button);
        }
        void AddActionButton(string key, string name, string topic, string icon)
        {
            var button = Common(key, name, icon, false);
            button["command_topic"] = topic;
            button["payload_press"] = "PRESS";
            button["command_template"] = ActionCommandTemplate;
            button["qos"] = 1;
            Add("button", key, button);
        }
        foreach (var power in Enum.GetValues<PowerAction>())
        {
            if (!settings.Power.IsEnabled(power)) continue;
            var key = power.ToString().ToLowerInvariant();
            var icon = power switch
            {
                PowerAction.Lock => "mdi:lock", PowerAction.Sleep => "mdi:sleep",
                PowerAction.Shutdown => "mdi:power", _ => "mdi:restart"
            };
            AddActionButton($"power_{key}", power.ToString(), settings.PowerCommandTopic(key), icon);
        }
        foreach (var action in settings.Actions.Where(action => action.Enabled))
            AddActionButton($"action_{action.Id}", action.Name, settings.ActionCommandTopic(action.Id), "mdi:application-cog");
        return messages;
    }
}
