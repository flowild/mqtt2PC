using System.Text.Json;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using MQTTnet.Server;
using Mqtt2Pc.Core;
using Xunit;

namespace Mqtt2Pc.Tests;

public sealed class StageTwoTests
{
    [Fact]
    public async Task DiscoversOneDeviceWithFiveEntitiesAndRetainedAudioState()
    {
        await using var broker = new BrokerFixture();
        await broker.Server.StartAsync();
        using var observer = await broker.ConnectObserverAsync("homeassistant/#");
        await using var agent = new RunningAgent(broker.Settings, desktop: new FakeDesktopControls());
        foreach (var config in HomeAssistantDiscovery.Create(broker.Settings))
        {
            var message = await observer.WaitAsync(config.Topic);
            using var json = JsonDocument.Parse(message.Payload);
            var root = json.RootElement;
            Assert.Equal($"mqtt2pc_{broker.Settings.DeviceId}", root.GetProperty("device").GetProperty("identifiers")[0].GetString());
            Assert.False(root.GetProperty("retain").GetBoolean());
            if (config.Topic.Contains("/button/", StringComparison.Ordinal))
                Assert.Equal(0, root.GetProperty("qos").GetInt32());
            else Assert.Equal(2, root.GetProperty("availability").GetArrayLength());
        }
        Assert.Equal(5, HomeAssistantDiscovery.Create(broker.Settings).Count);
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, m => m.Payload == "online");
        using var late = await broker.ConnectObserverAsync("homeassistant/#");
        foreach (var config in HomeAssistantDiscovery.Create(broker.Settings))
            Assert.True((await late.WaitAsync(config.Topic)).Retained);
        var state = await late.WaitAsync(broker.Settings.AudioStateTopic);
        Assert.True(state.Retained);
        Assert.True(AudioMatches(state, 35, false));
    }

    [Fact]
    public async Task VolumeAndMuteCommandsReadBackActualStateAndPollExternalChanges()
    {
        await using var broker = new BrokerFixture();
        await broker.Server.StartAsync();
        using var observer = await broker.ConnectObserverAsync();
        var desktop = new FakeDesktopControls();
        await using var agent = new RunningAgent(broker.Settings, desktop: desktop);
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, m => m.Payload == "online");
        await observer.PublishAsync(broker.Settings.VolumeCommandTopic, "62.0");
        await observer.WaitAsync(broker.Settings.AudioStateTopic, m => AudioMatches(m, 62, false));
        await observer.WaitAsync(broker.Settings.ResultTopic, m => Observer.HasStatus(m, "success"));
        Assert.Equal(1, desktop.VolumeWrites);
        await observer.PublishAsync(broker.Settings.MuteCommandTopic, "ON");
        await observer.WaitAsync(broker.Settings.AudioStateTopic, m => AudioMatches(m, 62, true));
        await observer.WaitAsync(broker.Settings.ResultTopic, m => Observer.HasStatus(m, "success"));
        Assert.Equal(1, desktop.MuteWrites);

        // A local Windows volume change must be reported without an MQTT command.
        desktop.State = new AudioState(18, false);
        await observer.WaitAsync(broker.Settings.AudioStateTopic, m => AudioMatches(m, 18, false));
        Assert.Equal(1, desktop.VolumeWrites);
        Assert.Equal(1, desktop.MuteWrites);
    }

    [Theory]
    [InlineData("play_pause", MediaAction.PlayPause)]
    [InlineData("next", MediaAction.Next)]
    [InlineData("previous", MediaAction.Previous)]
    public async Task DiscoveredMediaButtonSendsOneKey(string action, MediaAction expected)
    {
        await using var broker = new BrokerFixture();
        await broker.Server.StartAsync();
        using var observer = await broker.ConnectObserverAsync();
        var desktop = new FakeDesktopControls();
        await using var agent = new RunningAgent(broker.Settings, desktop: desktop);
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, m => m.Payload == "online");
        await observer.PublishAsync(broker.Settings.MediaCommandTopic(action), "PRESS", qos: MqttQualityOfServiceLevel.AtMostOnce);
        await observer.WaitAsync(broker.Settings.ResultTopic, m => Observer.HasStatus(m, "success"));
        Assert.True(desktop.MediaKeys.Reader.TryRead(out var key));
        Assert.Equal(expected, key);
        Assert.False(desktop.MediaKeys.Reader.TryRead(out _));
    }

    [Fact]
    public async Task RetransmittedMediaCommandDoesNotSendAnotherKey()
    {
        await using var broker = new BrokerFixture();
        var markDuplicate = false;
        broker.Server.InterceptingOutboundPacketAsync += args =>
        {
            if (markDuplicate && args.ClientId == broker.Settings.ClientId &&
                args.Packet is MqttPublishPacket packet && packet.Topic == broker.Settings.MediaCommandTopic("play_pause"))
                packet.Dup = true;
            return Task.CompletedTask;
        };
        await broker.Server.StartAsync();
        using var observer = await broker.ConnectObserverAsync();
        var desktop = new FakeDesktopControls();
        await using var agent = new RunningAgent(broker.Settings, desktop: desktop);
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, m => m.Payload == "online");
        await observer.PublishAsync(broker.Settings.MediaCommandTopic("play_pause"), "PRESS");
        await observer.WaitAsync(broker.Settings.ResultTopic, m => Observer.HasStatus(m, "success"));
        Assert.True(desktop.MediaKeys.Reader.TryRead(out _));
        markDuplicate = true;
        await observer.PublishAsync(broker.Settings.MediaCommandTopic("play_pause"), "PRESS");
        await observer.WaitAsync(broker.Settings.ResultTopic, m => Observer.HasStatus(m, "duplicate"));
        Assert.False(desktop.MediaKeys.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData("volume", "101")]
    [InlineData("volume", "-1")]
    [InlineData("volume", "NaN")]
    [InlineData("volume", "20.5")]
    [InlineData("mute", "TOGGLE")]
    [InlineData("media/next", "RUN")]
    public async Task InvalidControlPayloadsNeverReachWindows(string command, string payload)
    {
        await using var broker = new BrokerFixture();
        await broker.Server.StartAsync();
        using var observer = await broker.ConnectObserverAsync();
        var desktop = new FakeDesktopControls();
        await using var agent = new RunningAgent(broker.Settings, desktop: desktop);
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, m => m.Payload == "online");
        await observer.PublishAsync($"{broker.Settings.TopicPrefix}/command/{command}", payload);
        await observer.WaitAsync(broker.Settings.ResultTopic, m => Observer.HasStatus(m, "rejected"));
        Assert.Equal(0, desktop.VolumeWrites);
        Assert.Equal(0, desktop.MuteWrites);
        Assert.False(desktop.MediaKeys.Reader.TryRead(out _));
    }

    [Fact]
    public async Task RetainedControlsAreIgnoredAtConnectAndRejectedWhileConnected()
    {
        await using var broker = new BrokerFixture();
        await broker.Server.StartAsync();
        using var observer = await broker.ConnectObserverAsync();
        await observer.PublishAsync(broker.Settings.VolumeCommandTopic, "100", true);
        await observer.PublishAsync(broker.Settings.MuteCommandTopic, "ON", true);
        await observer.PublishAsync(broker.Settings.MediaCommandTopic("play_pause"), "PRESS", true);
        var desktop = new FakeDesktopControls();
        await using var agent = new RunningAgent(broker.Settings, desktop: desktop);
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, m => m.Payload == "online");
        foreach (var (topic, payload) in new[] { (broker.Settings.VolumeCommandTopic, "100"),
            (broker.Settings.MuteCommandTopic, "ON"), (broker.Settings.MediaCommandTopic("play_pause"), "PRESS") })
        {
            await observer.PublishAsync(topic, payload, true);
            await observer.WaitAsync(broker.Settings.ResultTopic, m => Observer.HasStatus(m, "rejected"));
        }
        Assert.Equal(0, desktop.VolumeWrites);
        Assert.Equal(0, desktop.MuteWrites);
        Assert.False(desktop.MediaKeys.Reader.TryRead(out _));
    }

    [Fact]
    public async Task HomeAssistantBirthAndBrokerReconnectRepublishDiscoveryAndCurrentState()
    {
        await using var broker = new BrokerFixture();
        await broker.Server.StartAsync();
        using var observer = await broker.ConnectObserverAsync("homeassistant/#");
        var desktop = new FakeDesktopControls();
        await using var agent = new RunningAgent(broker.Settings, desktop: desktop);
        await WaitForDiscoveryAsync();
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, m => m.Payload == "online");
        await observer.WaitAsync(broker.Settings.AudioStateTopic, m => AudioMatches(m, 35, false));

        // Home Assistant may lose subscriptions while the PC remains connected.
        desktop.State = new AudioState(71, true);
        await observer.PublishAsync(HomeAssistantDiscovery.BirthTopic, "online");
        await WaitForDiscoveryAsync();
        await observer.WaitAsync(broker.Settings.AudioStateTopic, m => AudioMatches(m, 71, true));
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, m => m.Payload == "online");

        var pc = Assert.Single(await broker.Server.GetClientsAsync(), client => client.Id == broker.Settings.ClientId);
        await pc.DisconnectAsync(new MqttServerClientDisconnectOptions());
        await WaitForDiscoveryAsync();
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, m => m.Payload == "online");
        await observer.PublishAsync(broker.Settings.VolumeCommandTopic, "41");
        await observer.WaitAsync(broker.Settings.AudioStateTopic, m => AudioMatches(m, 41, true));

        async Task WaitForDiscoveryAsync()
        {
            foreach (var config in HomeAssistantDiscovery.Create(broker.Settings))
                await observer.WaitAsync(config.Topic);
        }
    }

    [Fact]
    public async Task MissingAudioDeviceRecoversWithoutDroppingMqttOrMediaControls()
    {
        await using var broker = new BrokerFixture();
        await broker.Server.StartAsync();
        using var observer = await broker.ConnectObserverAsync();
        var desktop = new FakeDesktopControls { Available = false };
        await using var agent = new RunningAgent(broker.Settings, desktop: desktop);
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, m => m.Payload == "online");
        await observer.WaitAsync(broker.Settings.AudioAvailabilityTopic, m => m.Payload == "offline");
        await observer.PublishAsync(broker.Settings.VolumeCommandTopic, "30");
        await observer.WaitAsync(broker.Settings.ResultTopic, m => Observer.HasStatus(m, "error"));
        await observer.PublishAsync(broker.Settings.MediaCommandTopic("next"), "PRESS", qos: MqttQualityOfServiceLevel.AtMostOnce);
        await observer.WaitAsync(broker.Settings.ResultTopic, m => Observer.HasStatus(m, "success"));
        Assert.True(desktop.MediaKeys.Reader.TryRead(out _));
        desktop.Available = true;
        await observer.WaitAsync(broker.Settings.AudioAvailabilityTopic, m => m.Payload == "online");
        await observer.WaitAsync(broker.Settings.AudioStateTopic, m => AudioMatches(m, 35, false));
        desktop.Available = false;
        await observer.WaitAsync(broker.Settings.AudioAvailabilityTopic, m => m.Payload == "offline");
    }

    private static bool AudioMatches(Observed observed, int volume, bool muted)
    {
        using var json = JsonDocument.Parse(observed.Payload);
        return json.RootElement.GetProperty("volume").GetInt32() == volume && json.RootElement.GetProperty("muted").GetBoolean() == muted;
    }
}
