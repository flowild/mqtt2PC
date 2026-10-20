using System.Text;
using System.Text.Json;
using MQTTnet.Server;
using Mqtt2Pc.Core;
using Xunit;

namespace Mqtt2Pc.Tests;

public sealed class StageThreeTests
{
    internal static ConfiguredAction ExampleAction(string id = "start_app") => new()
    {
        Id = id, Name = "Start app", Executable = Path.Combine(Environment.SystemDirectory, "notepad.exe"),
        Arguments = ["C:\\Folder with spaces\\note.txt"]
    };

    private static readonly PowerControls AllPower = new() { Lock = true, Sleep = true, Shutdown = true, Restart = true };

    [Theory]
    [InlineData(PowerAction.Lock)]
    [InlineData(PowerAction.Sleep)]
    [InlineData(PowerAction.Shutdown)]
    [InlineData(PowerAction.Restart)]
    public async Task EnabledPowerRequestIsAcknowledgedAndRunsOnlyOnce(PowerAction expected)
    {
        await using var broker = new BrokerFixture();
        await broker.Server.StartAsync();
        using var observer = await broker.ConnectObserverAsync();
        var actions = new FakePcActions();
        await using var agent = new RunningAgent(broker.Settings with { Power = AllPower },
            desktop: new FakeDesktopControls(), pcActions: actions);
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, message => message.Payload == "online");
        var topic = broker.Settings.PowerCommandTopic(expected.ToString().ToLowerInvariant());
        await observer.PublishAsync(topic, "{\"id\":\"power-one\"}");
        await observer.WaitAsync(broker.Settings.ResultTopic, message => Observer.HasStatus(message, "accepted"));
        await observer.WaitAsync(broker.Settings.ResultTopic, message => Observer.HasStatus(message, "success"));
        Assert.True(actions.PowerRequests.Reader.TryRead(out var actual));
        Assert.Equal(expected, actual);
        await observer.PublishAsync(topic, "{\"id\":\"power-one\"}");
        await observer.WaitAsync(broker.Settings.ResultTopic, message => Observer.HasStatus(message, "duplicate"));
        Assert.False(actions.PowerRequests.Reader.TryRead(out _));
    }

    [Fact]
    public async Task AcknowledgementArrivesBeforeSimulatedSleepCompletes()
    {
        await using var broker = new BrokerFixture();
        await broker.Server.StartAsync();
        using var observer = await broker.ConnectObserverAsync();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actions = new FakePcActions { BeforePower = token => finish.Task.WaitAsync(token) };
        await using var agent = new RunningAgent(broker.Settings with { Power = AllPower },
            desktop: new FakeDesktopControls(), pcActions: actions);
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, message => message.Payload == "online");
        await observer.PublishAsync(broker.Settings.PowerCommandTopic("sleep"), "{\"id\":\"sleep-one\"}");
        await observer.WaitAsync(broker.Settings.ResultTopic, message => Observer.HasStatus(message, "accepted"));
        Assert.False(actions.PowerRequests.Reader.TryRead(out _));
        finish.SetResult();
        await observer.WaitAsync(broker.Settings.ResultTopic, message => Observer.HasStatus(message, "success"));
        Assert.True(actions.PowerRequests.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData("lock")]
    [InlineData("sleep")]
    [InlineData("shutdown")]
    [InlineData("restart")]
    [InlineData("hibernate")]
    public async Task DisabledOrUnknownPowerRequestsNeverReachWindows(string power)
    {
        await using var broker = new BrokerFixture();
        await broker.Server.StartAsync();
        using var observer = await broker.ConnectObserverAsync();
        var actions = new FakePcActions();
        await using var agent = new RunningAgent(broker.Settings, desktop: new FakeDesktopControls(), pcActions: actions);
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, message => message.Payload == "online");
        await observer.PublishAsync(broker.Settings.PowerCommandTopic(power), "{\"id\":\"disabled-one\"}");
        await observer.WaitAsync(broker.Settings.ResultTopic, message => Observer.HasStatus(message, "rejected"));
        Assert.False(actions.PowerRequests.Reader.TryRead(out _));
    }

    [Fact]
    public async Task NamedActionsUseOnlySavedArgumentsAndReturnTheStartedProcessId()
    {
        await using var broker = new BrokerFixture();
        await broker.Server.StartAsync();
        using var observer = await broker.ConnectObserverAsync();
        var saved = ExampleAction();
        var actions = new FakePcActions();
        await using var agent = new RunningAgent(broker.Settings with { Actions = [saved, ExampleAction("another_app")] },
            desktop: new FakeDesktopControls(), pcActions: actions);
        // The running agent takes a snapshot rather than observing mutable UI arrays.
        saved.Arguments[0] = "changed after startup";
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, message => message.Payload == "online");
        await observer.PublishAsync(broker.Settings.ActionCommandTopic(saved.Id), "{\"id\":\"launch-one\"}");
        var result = await observer.WaitAsync(broker.Settings.ResultTopic, message => Observer.HasStatus(message, "success"));
        using var json = JsonDocument.Parse(result.Payload);
        Assert.Equal(12345, json.RootElement.GetProperty("process_id").GetInt32());
        Assert.True(actions.Launches.Reader.TryRead(out var launched));
        Assert.Equal("C:\\Folder with spaces\\note.txt", Assert.Single(launched.Arguments));
        await observer.PublishAsync(broker.Settings.ActionCommandTopic(saved.Id), "{\"id\":\"launch-one\"}");
        await observer.WaitAsync(broker.Settings.ResultTopic, message => Observer.HasStatus(message, "duplicate"));
        Assert.False(actions.Launches.Reader.TryRead(out _));
        await observer.PublishAsync(broker.Settings.ActionCommandTopic("another_app"), "{\"id\":\"launch-one\"}");
        await observer.WaitAsync(broker.Settings.ResultTopic, message => Observer.HasStatus(message, "success"));
        Assert.True(actions.Launches.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData("PRESS")]
    [InlineData("{}")]
    [InlineData("{\"id\":\"bad/id\"}")]
    [InlineData("{\"id\":\"one\",\"arguments\":[\"injected\"]}")]
    [InlineData("{\"id\":\"one\",\"executable\":\"cmd.exe\"}")]
    public async Task MalformedAndParameterInjectionRequestsCannotLaunchPrograms(string payload)
    {
        await using var broker = new BrokerFixture();
        await broker.Server.StartAsync();
        using var observer = await broker.ConnectObserverAsync();
        var actions = new FakePcActions();
        await using var agent = new RunningAgent(broker.Settings with { Actions = [ExampleAction()] },
            desktop: new FakeDesktopControls(), pcActions: actions);
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, message => message.Payload == "online");
        await observer.PublishAsync(broker.Settings.ActionCommandTopic("start_app"), payload);
        await observer.WaitAsync(broker.Settings.ResultTopic, message => Observer.HasStatus(message, "rejected"));
        Assert.False(actions.Launches.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("disabled")]
    public async Task DisabledOrUnknownNamedActionIsRejected(string id)
    {
        await using var broker = new BrokerFixture();
        await broker.Server.StartAsync();
        using var observer = await broker.ConnectObserverAsync();
        var actions = new FakePcActions();
        await using var agent = new RunningAgent(broker.Settings with { Actions = [ExampleAction("disabled") with { Enabled = false }] },
            desktop: new FakeDesktopControls(), pcActions: actions);
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, message => message.Payload == "online");
        await observer.PublishAsync(broker.Settings.ActionCommandTopic(id), "{\"id\":\"disabled-one\"}");
        await observer.WaitAsync(broker.Settings.ResultTopic, message => Observer.HasStatus(message, "rejected"));
        Assert.False(actions.Launches.Reader.TryRead(out _));
    }

    [Fact]
    public async Task RetainedRequestsCannotTriggerPowerOrLaunches()
    {
        await using var broker = new BrokerFixture();
        await broker.Server.StartAsync();
        using var observer = await broker.ConnectObserverAsync();
        var powerTopic = broker.Settings.PowerCommandTopic("restart");
        var actionTopic = broker.Settings.ActionCommandTopic("start_app");
        await observer.PublishAsync(powerTopic, "{\"id\":\"stored-power\"}", true);
        await observer.PublishAsync(actionTopic, "{\"id\":\"stored-launch\"}", true);
        var actions = new FakePcActions();
        await using var agent = new RunningAgent(broker.Settings with { Power = AllPower, Actions = [ExampleAction()] },
            desktop: new FakeDesktopControls(), pcActions: actions);
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, message => message.Payload == "online");
        foreach (var topic in new[] { powerTopic, actionTopic })
        {
            await observer.PublishAsync(topic, "{\"id\":\"retained-live\"}", true);
            await observer.WaitAsync(broker.Settings.ResultTopic, message => Observer.HasStatus(message, "rejected"));
        }
        Assert.False(actions.PowerRequests.Reader.TryRead(out _));
        Assert.False(actions.Launches.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ReconnectKeepsPowerRequestIdsAndActionFailuresDoNotDisconnectMqtt()
    {
        await using var broker = new BrokerFixture();
        await broker.Server.StartAsync();
        using var observer = await broker.ConnectObserverAsync();
        var actions = new FakePcActions();
        await using var agent = new RunningAgent(broker.Settings with { Power = AllPower, Actions = [ExampleAction()] },
            desktop: new FakeDesktopControls(), pcActions: actions);
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, message => message.Payload == "online");
        await observer.PublishAsync(broker.Settings.PowerCommandTopic("lock"), "{\"id\":\"lock-once\"}");
        await observer.WaitAsync(broker.Settings.ResultTopic, message => Observer.HasStatus(message, "success"));
        Assert.True(actions.PowerRequests.Reader.TryRead(out _));
        var pc = Assert.Single(await broker.Server.GetClientsAsync(), client => client.Id == broker.Settings.ClientId);
        await pc.DisconnectAsync(new MqttServerClientDisconnectOptions());
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, message => message.Payload == "online");
        await observer.PublishAsync(broker.Settings.PowerCommandTopic("lock"), "{\"id\":\"lock-once\"}");
        await observer.WaitAsync(broker.Settings.ResultTopic, message => Observer.HasStatus(message, "duplicate"));
        Assert.False(actions.PowerRequests.Reader.TryRead(out _));
        actions.Fail = true;
        await observer.PublishAsync(broker.Settings.ActionCommandTopic("start_app"), "{\"id\":\"failed-launch\"}");
        await observer.WaitAsync(broker.Settings.ResultTopic, message => Observer.HasStatus(message, "error"));
        Assert.Contains(await broker.Server.GetClientsAsync(), client => client.Id == broker.Settings.ClientId);
        await observer.PublishAsync(broker.Settings.ActionCommandTopic("start_app"), "{\"id\":\"failed-launch\"}");
        await observer.WaitAsync(broker.Settings.ResultTopic, message => Observer.HasStatus(message, "duplicate"));
    }

    [Fact]
    public async Task DisabledAndRemovedButtonsAreDeletedFromRetainedDiscoveryAcrossAppRestarts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mqtt2pc-discovery-{Guid.NewGuid():N}.json");
        try
        {
            await using var broker = new BrokerFixture();
            await broker.Server.StartAsync();
            using var observer = await broker.ConnectObserverAsync("homeassistant/#");
            var initial = broker.Settings with { Power = AllPower, Actions = [ExampleAction()] };
            var ledger = new DiscoveryStore(path);
            await using (var first = new RunningAgent(initial, desktop: new FakeDesktopControls(), pcActions: new FakePcActions(), discoveryStore: ledger))
            {
                await observer.WaitAsync(broker.Settings.AvailabilityTopic, message => message.Payload == "online");
                Assert.Equal(10, ledger.Load().Count);
                Assert.All(ledger.Load(), topic => Assert.True(HomeAssistantDiscovery.IsDeviceTopic(initial, topic), topic));
            }
            var newSettings = broker.Settings;
            await using var second = new RunningAgent(newSettings, desktop: new FakeDesktopControls(), pcActions: new FakePcActions(), discoveryStore: ledger);
            try { await observer.WaitAsync(broker.Settings.AvailabilityTopic, message => message.Payload == "online"); }
            catch (OperationCanceledException)
            {
                Assert.Fail(string.Join("; ", second.Statuses.Select(status => status.Message)));
            }
            foreach (var removed in HomeAssistantDiscovery.Create(initial).Select(config => config.Topic)
                .Except(HomeAssistantDiscovery.Create(newSettings).Select(config => config.Topic)))
                await observer.WaitAsync(removed, message => message.Payload == "");
            Assert.Equal(5, ledger.Load().Count);
            var retained = await broker.Server.GetRetainedMessagesAsync();
            Assert.Equal(5, retained.Count(message => message.Topic.StartsWith("homeassistant/", StringComparison.Ordinal)));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void DiscoveryOnlyExposesEnabledPowerAndActionsWithUniqueRequestTemplates()
    {
        var settings = new AppSettings
        {
            BrokerHost = "localhost", Power = new PowerControls { Lock = true },
            Actions = [ExampleAction(), ExampleAction("disabled") with { Enabled = false }]
        };
        var configs = HomeAssistantDiscovery.Create(settings);
        Assert.Equal(7, configs.Count);
        foreach (var config in configs.Where(config => config.Topic.Contains("/power_", StringComparison.Ordinal) || config.Topic.Contains("/action_", StringComparison.Ordinal)))
        {
            using var json = JsonDocument.Parse(config.Payload);
            Assert.Equal(HomeAssistantDiscovery.ActionCommandTemplate, json.RootElement.GetProperty("command_template").GetString());
            Assert.Equal(1, json.RootElement.GetProperty("qos").GetInt32());
            Assert.False(json.RootElement.GetProperty("retain").GetBoolean());
        }
    }
}
