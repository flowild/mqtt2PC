using System.Buffers;
using System.Text.Json;
using System.Threading.Channels;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace Mqtt2Pc.Core;

public sealed record ConnectionStatus(bool IsConnected, string Message);

public sealed class MqttAgent
{
    private readonly AppSettings _settings;
    private readonly string _password;
    private readonly Func<string, CancellationToken, Task> _showNotification;
    private readonly HashSet<string> _recentIds = [];
    private readonly Queue<string> _idOrder = new();
    private int _started;

    public event Action<ConnectionStatus>? StatusChanged;

    public MqttAgent(AppSettings settings, string password, Func<string, CancellationToken, Task> showNotification)
    {
        settings.Validate();
        _settings = settings;
        _password = password;
        _showNotification = showNotification;
    }

    // One owner controls the entire connection lifetime, including reconnect and shutdown.
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("An MQTT agent can only be run once.");

        var failures = 0;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                using var client = new MqttClientFactory().CreateMqttClient();
                var incoming = Channel.CreateBounded<ReceivedCommand>(new BoundedChannelOptions(32)
                {
                    SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
                });
                client.DisconnectedAsync += _ =>
                {
                    session.Cancel();
                    return Task.CompletedTask;
                };
                client.ApplicationMessageReceivedAsync += args =>
                {
                    var message = args.ApplicationMessage;
                    if (message.Topic == _settings.TestCommandTopic &&
                        message.Payload.Length <= TestCommand.MaximumPayloadBytes &&
                        !incoming.Writer.TryWrite(new ReceivedCommand(message.Retain, message.Payload.ToArray())))
                        Report(true, "Command queue full; a test command was ignored.");
                    return Task.CompletedTask;
                };

                try
                {
                    Report(false, failures == 0 ? "Connecting…" : "Reconnecting…");
                    await client.ConnectAsync(BuildOptions(), session.Token).ConfigureAwait(false);
                    var subscription = await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                        .WithTopicFilter(filter => filter.WithTopic(_settings.TestCommandTopic)
                            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                            .WithRetainAsPublished(true)
                            .WithRetainHandling(MqttRetainHandling.DoNotSendOnSubscribe))
                        .Build(), session.Token).ConfigureAwait(false);
                    if (subscription.Items.Any(item => (int)item.ResultCode >= 128))
                        throw new InvalidOperationException("The broker refused the command subscription.");

                    await PublishAsync(client, _settings.AvailabilityTopic, "online", true, session.Token).ConfigureAwait(false);
                    Report(true, $"Connected to {_settings.BrokerHost}:{_settings.BrokerPort}");
                    failures = 0;
                    await ProcessCommandsAsync(client, incoming.Reader, session.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (session.IsCancellationRequested)
                {
                    if (!stoppingToken.IsCancellationRequested)
                        Report(false, "Connection lost; retrying…");
                }
                catch (Exception exception)
                {
                    // Exception messages may contain connection details. Do not expose credentials.
                    Report(false, $"Connection failed ({exception.GetType().Name}). Check broker, credentials and topic permissions.");
                }
                finally
                {
                    incoming.Writer.TryComplete();
                    if (client.IsConnected)
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        try
                        {
                            await PublishAsync(client, _settings.AvailabilityTopic, "offline", true, cleanup.Token).ConfigureAwait(false);
                            await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), cleanup.Token).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // Disposing the socket lets the broker publish the offline Last Will.
                        }
                    }
                }

                if (!stoppingToken.IsCancellationRequested)
                {
                    var delay = Math.Min(30, Math.Pow(2, Math.Min(failures++, 5)));
                    await Task.Delay(TimeSpan.FromSeconds(delay) + TimeSpan.FromMilliseconds(Random.Shared.Next(250)), stoppingToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { Report(false, "Disconnected"); }
    }

    private MqttClientOptions BuildOptions()
    {
        var builder = new MqttClientOptionsBuilder()
            .WithClientId(_settings.ClientId)
            .WithTcpServer(_settings.BrokerHost, _settings.BrokerPort)
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithCleanSession()
            .WithSessionExpiryInterval(0)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(15))
            .WithTimeout(TimeSpan.FromSeconds(10))
            .WithWillTopic(_settings.AvailabilityTopic)
            .WithWillPayload("offline")
            .WithWillRetain()
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
        if (!string.IsNullOrEmpty(_settings.Username)) builder.WithCredentials(_settings.Username, _password);
        if (_settings.UseTls) builder.WithTlsOptions(tls => tls.UseTls());
        return builder.Build();
    }

    private sealed record ReceivedCommand(bool Retain, byte[] Payload);

    private async Task ProcessCommandsAsync(IMqttClient client, ChannelReader<ReceivedCommand> incoming, CancellationToken token)
    {
        await foreach (var message in incoming.ReadAllAsync(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            // Retain-as-published also exposes the retain flag on live publications.
            if (message.Retain)
            {
                await PublishResultAsync(client, null, "rejected", "Retained commands are not accepted.", token).ConfigureAwait(false);
                continue;
            }
            TestCommand command;
            try { command = TestCommand.Parse(message.Payload); }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                await PublishResultAsync(client, null, "rejected", "Invalid test command. Supply an id and an optional message (up to 200 characters).", token)
                    .ConfigureAwait(false);
                continue;
            }
            if (!_recentIds.Add(command.Id))
            {
                await PublishResultAsync(client, command.Id, "duplicate", "This command id was already handled.", token).ConfigureAwait(false);
                continue;
            }
            _idOrder.Enqueue(command.Id);
            if (_idOrder.Count > 1024) _recentIds.Remove(_idOrder.Dequeue());

            string status;
            string detail;
            try
            {
                await _showNotification(command.Message!, token).ConfigureAwait(false);
                status = "success";
                detail = "Notification requested on the PC.";
                Report(true, $"Last test received at {DateTimeOffset.Now:T}");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                status = "error";
                detail = "The PC could not show the notification.";
            }
            await PublishResultAsync(client, command.Id, status, detail, token).ConfigureAwait(false);
        }
    }

    private Task PublishResultAsync(IMqttClient client, string? id, string status, string message, CancellationToken token) =>
        PublishAsync(client, _settings.ResultTopic,
            JsonSerializer.Serialize(new { id, status, message, timestamp = DateTimeOffset.UtcNow }), false, token);

    private static async Task PublishAsync(IMqttClient client, string topic, string payload, bool retain, CancellationToken token)
    {
        var result = await client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic).WithPayload(payload).WithRetainFlag(retain)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), token).ConfigureAwait(false);
        if ((int)result.ReasonCode >= 128)
            throw new InvalidOperationException("The broker refused a publication.");
    }

    private void Report(bool connected, string message) => StatusChanged?.Invoke(new(connected, message));
}
