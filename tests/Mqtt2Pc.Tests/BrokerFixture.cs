using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using MQTTnet.Server;
using Mqtt2Pc.Core;
using Xunit;

namespace Mqtt2Pc.Tests;

internal sealed class BrokerFixture : IAsyncDisposable
{
    public int Port { get; }
    public MqttServer Server { get; }
    public AppSettings Settings { get; }

    public BrokerFixture()
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        Port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        Server = new MqttServerFactory().CreateMqttServer(new MqttServerOptionsBuilder()
            .WithDefaultEndpoint().WithDefaultEndpointBoundIPAddress(IPAddress.Loopback).WithDefaultEndpointPort(Port).Build());
        Settings = new AppSettings { BrokerHost = "127.0.0.1", BrokerPort = Port, DeviceId = "integration-pc" };
    }

    public async Task<Observer> ConnectObserverAsync(params string[] extraTopics)
    {
        var observer = new Observer();
        await observer.Client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", Port)
            .WithProtocolVersion(MqttProtocolVersion.V500).WithClientId($"observer-{Guid.NewGuid():N}").Build());
        var subscriptions = new MqttClientSubscribeOptionsBuilder().WithTopicFilter($"{Settings.TopicPrefix}/#");
        foreach (var topic in extraTopics) subscriptions.WithTopicFilter(topic);
        await observer.Client.SubscribeAsync(subscriptions.Build());
        return observer;
    }

    public async ValueTask DisposeAsync()
    {
        await Server.StopAsync();
        Server.Dispose();
    }
}

internal sealed record Observed(string Topic, string Payload, bool Retained);

internal sealed class Observer : IDisposable
{
    public IMqttClient Client { get; } = new MqttClientFactory().CreateMqttClient();
    private readonly Channel<Observed> _messages = Channel.CreateUnbounded<Observed>();
    private readonly List<Observed> _pending = [];

    public Observer()
    {
        Client.ApplicationMessageReceivedAsync += args =>
        {
            var message = args.ApplicationMessage;
            _messages.Writer.TryWrite(new(message.Topic, message.ConvertPayloadToString(), message.Retain));
            return Task.CompletedTask;
        };
    }

    public async Task<Observed> WaitAsync(string topic, Func<Observed, bool>? predicate = null)
    {
        var existing = _pending.FindIndex(message => message.Topic == topic && (predicate is null || predicate(message)));
        if (existing >= 0)
        {
            var found = _pending[existing];
            _pending.RemoveAt(existing);
            return found;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (await _messages.Reader.WaitToReadAsync(timeout.Token))
        {
            while (_messages.Reader.TryRead(out var message))
            {
                if (message.Topic == topic && (predicate is null || predicate(message))) return message;
                _pending.Add(message);
            }
        }
        throw new InvalidOperationException("The broker did not deliver the expected message.");
    }

    public Task PublishAsync(string topic, string payload, bool retain = false, MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtLeastOnce) => Client.PublishAsync(
        new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).WithRetainFlag(retain)
            .WithQualityOfServiceLevel(qos).Build());

    public static bool HasStatus(Observed message, string status)
    {
        using var document = JsonDocument.Parse(message.Payload);
        return document.RootElement.GetProperty("status").GetString() == status;
    }

    public void Dispose() => Client.Dispose();
}

internal sealed class RunningAgent : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _task;
    public Channel<string> Notifications { get; } = Channel.CreateUnbounded<string>();

    public RunningAgent(AppSettings settings, string password = "", Func<string, CancellationToken, Task>? action = null, IDesktopControls? desktop = null)
    {
        var agent = new MqttAgent(settings, password, action ?? ((message, _) =>
        {
            Notifications.Writer.TryWrite(message);
            return Task.CompletedTask;
        }), desktop);
        _task = agent.RunAsync(_stop.Token);
    }

    public async Task StopAsync()
    {
        _stop.Cancel();
        await _task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stop.Dispose();
    }
}
