using MQTTnet.Protocol;
using MQTTnet.Server;
using Xunit;

namespace Mqtt2Pc.Tests;

public sealed class MqttAgentTests
{
    [Fact]
    public async Task UnexpectedNetworkLossPublishesOfflineWillAndThenReconnects()
    {
        await using var broker = new BrokerFixture();
        await broker.Server.StartAsync();
        using var observer = await broker.ConnectObserverAsync();
        await using var relay = new TcpRelay(broker.Port);
        await using var agent = new RunningAgent(broker.Settings with { BrokerPort = relay.Port });
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, m => m.Payload == "online");
        relay.DropConnection();
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, m => m.Payload == "offline");
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, m => m.Payload == "online");
        await observer.PublishAsync(broker.Settings.TestCommandTopic, "{\"id\":\"network-restored\"}");
        await observer.WaitAsync(broker.Settings.ResultTopic, m => Observer.HasStatus(m, "success"));
        Assert.True(agent.Notifications.Reader.TryRead(out _));
    }

    [Fact]
    public async Task PublishesRetainedAvailabilityAndHandlesTestCommand()
    {
        await using var broker = new BrokerFixture();
        await broker.Server.StartAsync();
        using var observer = await broker.ConnectObserverAsync();
        await using var agent = new RunningAgent(broker.Settings);
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, m => m.Payload == "online");
        using var lateObserver = await broker.ConnectObserverAsync();
        var retainedOnline = await lateObserver.WaitAsync(broker.Settings.AvailabilityTopic);
        Assert.True(retainedOnline.Retained);
        Assert.Equal("online", retainedOnline.Payload);

        await observer.PublishAsync(broker.Settings.TestCommandTopic, "{\"id\":\"test-1\",\"message\":\"hello PC\"}");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal("hello PC", await agent.Notifications.Reader.ReadAsync(timeout.Token));
        var result = await observer.WaitAsync(broker.Settings.ResultTopic, m => Observer.HasStatus(m, "success"));
        Assert.False(result.Retained);

        await agent.StopAsync();
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, m => m.Payload == "offline");
        using var afterExit = await broker.ConnectObserverAsync();
        var retainedOffline = await afterExit.WaitAsync(broker.Settings.AvailabilityTopic);
        Assert.True(retainedOffline.Retained);
        Assert.Equal("offline", retainedOffline.Payload);
    }

    [Fact]
    public async Task RejectsRetainedMalformedAndDuplicateCommands()
    {
        await using var broker = new BrokerFixture();
        await broker.Server.StartAsync();
        using var observer = await broker.ConnectObserverAsync();
        // This stored command must not execute when the PC subscribes.
        await observer.PublishAsync(broker.Settings.TestCommandTopic, "{\"id\":\"stored-before-connect\"}", true);
        await using var agent = new RunningAgent(broker.Settings);
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, m => m.Payload == "online");

        await observer.PublishAsync(broker.Settings.TestCommandTopic, "not json");
        await observer.WaitAsync(broker.Settings.ResultTopic, m => Observer.HasStatus(m, "rejected"));
        Assert.False(agent.Notifications.Reader.TryRead(out _));
        await observer.PublishAsync(broker.Settings.TestCommandTopic, "{\"id\":\"retained-live\"}", true);
        await observer.WaitAsync(broker.Settings.ResultTopic, m => Observer.HasStatus(m, "rejected"));
        Assert.False(agent.Notifications.Reader.TryRead(out _));

        await observer.PublishAsync(broker.Settings.TestCommandTopic, "{\"id\":\"once\"}");
        await observer.WaitAsync(broker.Settings.ResultTopic, m => Observer.HasStatus(m, "success"));
        Assert.True(agent.Notifications.Reader.TryRead(out _));
        await observer.PublishAsync(broker.Settings.TestCommandTopic, "{\"id\":\"once\"}");
        await observer.WaitAsync(broker.Settings.ResultTopic, m => Observer.HasStatus(m, "duplicate"));
        Assert.False(agent.Notifications.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ReconnectsAndResubscribesWithoutReexecutingAnId()
    {
        await using var broker = new BrokerFixture();
        await broker.Server.StartAsync();
        using var observer = await broker.ConnectObserverAsync();
        await using var agent = new RunningAgent(broker.Settings);
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, m => m.Payload == "online");
        await observer.PublishAsync(broker.Settings.TestCommandTopic, "{\"id\":\"before-reconnect\"}");
        await observer.WaitAsync(broker.Settings.ResultTopic, m => Observer.HasStatus(m, "success"));
        Assert.True(agent.Notifications.Reader.TryRead(out _));

        var connectedPc = Assert.Single(await broker.Server.GetClientsAsync(), c => c.Id == broker.Settings.ClientId);
        await connectedPc.DisconnectAsync(new MqttServerClientDisconnectOptions());
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, m => m.Payload == "online");
        await observer.PublishAsync(broker.Settings.TestCommandTopic, "{\"id\":\"before-reconnect\"}");
        await observer.WaitAsync(broker.Settings.ResultTopic, m => Observer.HasStatus(m, "duplicate"));
        Assert.False(agent.Notifications.Reader.TryRead(out _));
        await observer.PublishAsync(broker.Settings.TestCommandTopic, "{\"id\":\"after-reconnect\"}");
        await observer.WaitAsync(broker.Settings.ResultTopic, m => Observer.HasStatus(m, "success"));
        Assert.True(agent.Notifications.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ConnectsUsingBrokerCredentials()
    {
        await using var broker = new BrokerFixture();
        broker.Server.ValidatingConnectionAsync += args =>
        {
            if (args.ClientId == broker.Settings.ClientId && (args.UserName != "pc-user" || args.Password != "test-secret"))
                args.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
            return Task.CompletedTask;
        };
        await broker.Server.StartAsync();
        using var observer = await broker.ConnectObserverAsync();
        await using var agent = new RunningAgent(broker.Settings with { Username = "pc-user" }, "test-secret");
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, m => m.Payload == "online");
    }

    [Fact]
    public async Task ActionFailureReturnsAnErrorWithoutDroppingConnection()
    {
        await using var broker = new BrokerFixture();
        await broker.Server.StartAsync();
        using var observer = await broker.ConnectObserverAsync();
        await using var agent = new RunningAgent(broker.Settings, action: (_, _) => throw new InvalidOperationException("test failure"));
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, m => m.Payload == "online");
        await observer.PublishAsync(broker.Settings.TestCommandTopic, "{\"id\":\"action-fails\"}");
        await observer.WaitAsync(broker.Settings.ResultTopic, m => Observer.HasStatus(m, "error"));
        Assert.Contains(await broker.Server.GetClientsAsync(), client => client.Id == broker.Settings.ClientId);
    }
}
