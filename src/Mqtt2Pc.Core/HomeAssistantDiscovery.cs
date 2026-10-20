using System.Text.Json;

namespace Mqtt2Pc.Core;

public sealed record DiscoveryMessage(string Topic, string Payload);

public static class HomeAssistantDiscovery
{
    public const string BirthTopic = "homeassistant/status";

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
                    manufacturer = "mqtt2PC", model = "Windows PC", sw_version = "0.2.0"
                },
                ["origin"] = new { name = "mqtt2PC", sw_version = "0.2.0" },
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
        return messages;
    }
}
