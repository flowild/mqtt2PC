using System.Buffers;
using System.Globalization;
using System.Text;
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
    private readonly IDesktopControls? _desktop;
    private readonly HashSet<string> _recentIds = [];
    private readonly Queue<string> _idOrder = new();
    private int _started;

    public event Action<ConnectionStatus>? StatusChanged;
    public event Action<AudioState?>? AudioStateChanged;

    public MqttAgent(AppSettings settings, string password, Func<string, CancellationToken, Task> showNotification,
        IDesktopControls? desktop = null)
    {
        settings.Validate();
        _settings = settings;
        _password = password;
        _showNotification = showNotification;
        _desktop = desktop;
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
                Task? refreshTask = null;
                var audioPublication = new AudioPublication();
                var commandTopics = new HashSet<string> { _settings.TestCommandTopic };
                if (_desktop is not null)
                {
                    commandTopics.UnionWith(new[] { _settings.VolumeCommandTopic, _settings.MuteCommandTopic,
                        _settings.MediaCommandTopic("play_pause"), _settings.MediaCommandTopic("next"), _settings.MediaCommandTopic("previous") });
                }
                var incoming = Channel.CreateBounded<ReceivedCommand>(new BoundedChannelOptions(32)
                {
                    SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
                });
                client.DisconnectedAsync += _ =>
                {
                    session.Cancel();
                    return Task.CompletedTask;
                };
                client.ApplicationMessageReceivedAsync += args =>
                {
                    var message = args.ApplicationMessage;
                    if ((commandTopics.Contains(message.Topic) || (_desktop is not null && message.Topic == HomeAssistantDiscovery.BirthTopic)) &&
                        message.Payload.Length <= TestCommand.MaximumPayloadBytes &&
                        !incoming.Writer.TryWrite(new ReceivedCommand(message.Topic, message.Retain, message.Dup, message.Payload.ToArray())))
                        Report(true, "Command queue full; a command was ignored.");
                    return Task.CompletedTask;
                };

                try
                {
                    Report(false, failures == 0 ? "Connecting…" : "Reconnecting…");
                    await client.ConnectAsync(BuildOptions(), session.Token).ConfigureAwait(false);
                    var subscribe = new MqttClientSubscribeOptionsBuilder();
                    foreach (var topic in commandTopics)
                        subscribe.WithTopicFilter(filter => filter.WithTopic(topic)
                            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                            .WithRetainAsPublished(true)
                            .WithRetainHandling(MqttRetainHandling.DoNotSendOnSubscribe));
                    if (_desktop is not null)
                        subscribe.WithTopicFilter(filter => filter.WithTopic(HomeAssistantDiscovery.BirthTopic)
                            .WithRetainHandling(MqttRetainHandling.DoNotSendOnSubscribe));
                    var subscription = await client.SubscribeAsync(subscribe.Build(), session.Token).ConfigureAwait(false);
                    if (subscription.Items.Any(item => (int)item.ResultCode >= 128))
                        throw new InvalidOperationException("The broker refused the command subscription.");

                    if (_desktop is not null)
                    {
                        await PublishDiscoveryAsync(client, session.Token).ConfigureAwait(false);
                        await PublishAudioAsync(client, audioPublication, true, session.Token).ConfigureAwait(false);
                        refreshTask = QueueRefreshAsync(incoming.Writer, session.Token);
                    }
                    await PublishAsync(client, _settings.AvailabilityTopic, "online", true, session.Token).ConfigureAwait(false);
                    Report(true, $"Connected to {_settings.BrokerHost}:{_settings.BrokerPort}");
                    failures = 0;
                    await ProcessCommandsAsync(client, incoming.Reader, audioPublication, session.Token).ConfigureAwait(false);
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
                    session.Cancel();
                    if (refreshTask is not null)
                    {
                        try { await refreshTask.ConfigureAwait(false); }
                        catch (OperationCanceledException) when (session.IsCancellationRequested) { }
                    }
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

    private sealed record ReceivedCommand(string Topic, bool Retain, bool Duplicate, byte[] Payload);

    private sealed class AudioPublication
    {
        public AudioState? LastState;
        public bool? Available;
    }

    private async Task ProcessCommandsAsync(IMqttClient client, ChannelReader<ReceivedCommand> incoming,
        AudioPublication audio, CancellationToken token)
    {
        await foreach (var message in incoming.ReadAllAsync(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            if (message.Topic.Length == 0)
            {
                await PublishAudioAsync(client, audio, false, token).ConfigureAwait(false);
                continue;
            }
            if (message.Topic == HomeAssistantDiscovery.BirthTopic)
            {
                if (Encoding.UTF8.GetString(message.Payload) == "online")
                {
                    await PublishDiscoveryAsync(client, token).ConfigureAwait(false);
                    await PublishAudioAsync(client, audio, true, token).ConfigureAwait(false);
                    await PublishAsync(client, _settings.AvailabilityTopic, "online", true, token).ConfigureAwait(false);
                }
                continue;
            }
            // Retain-as-published also exposes the retain flag on live publications.
            if (message.Retain)
            {
                await PublishResultAsync(client, null, "rejected", "Retained commands are not accepted.", token, message.Topic).ConfigureAwait(false);
                continue;
            }
            if (message.Topic != _settings.TestCommandTopic)
            {
                await ProcessDesktopCommandAsync(client, message, audio, token).ConfigureAwait(false);
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

    private async Task ProcessDesktopCommandAsync(IMqttClient client, ReceivedCommand command, AudioPublication audio, CancellationToken token)
    {
        var payload = Encoding.UTF8.GetString(command.Payload);
        Func<Task>? action = null;
        var media = false;
        if (command.Topic == _settings.VolumeCommandTopic &&
            decimal.TryParse(payload, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var volume) &&
            volume is >= 0 and <= 100 && decimal.Truncate(volume) == volume)
            action = () => _desktop!.SetVolumeAsync((int)volume, token);
        else if (command.Topic == _settings.MuteCommandTopic && payload is "ON" or "OFF")
            action = () => _desktop!.SetMuteAsync(payload == "ON", token);
        else if (payload == "PRESS")
        {
            MediaAction? key = command.Topic == _settings.MediaCommandTopic("play_pause") ? MediaAction.PlayPause :
                command.Topic == _settings.MediaCommandTopic("next") ? MediaAction.Next :
                command.Topic == _settings.MediaCommandTopic("previous") ? MediaAction.Previous : null;
            if (key.HasValue)
            {
                media = true;
                action = () => _desktop!.SendMediaAsync(key.Value, token);
            }
        }
        if (action is null)
        {
            await PublishResultAsync(client, null, "rejected", "Expected volume 0–100 in whole percent, mute ON/OFF, or media PRESS.", token, command.Topic)
                .ConfigureAwait(false);
            return;
        }
        if (media && command.Duplicate)
        {
            await PublishResultAsync(client, null, "duplicate", "A retransmitted media command was ignored. Publish media commands with QoS 0.", token, command.Topic)
                .ConfigureAwait(false);
            return;
        }
        try { await action().ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            await PublishResultAsync(client, null, "error", "Windows could not perform the audio or media action.", token, command.Topic).ConfigureAwait(false);
            if (!media) await PublishAudioAsync(client, audio, true, token).ConfigureAwait(false);
            return;
        }
        if (!media) await PublishAudioAsync(client, audio, true, token).ConfigureAwait(false);
        await PublishResultAsync(client, null, "success", media ? "Media key sent to Windows." : "Audio setting applied.", token, command.Topic).ConfigureAwait(false);
    }

    private async Task PublishDiscoveryAsync(IMqttClient client, CancellationToken token)
    {
        foreach (var discovery in HomeAssistantDiscovery.Create(_settings))
            await PublishAsync(client, discovery.Topic, discovery.Payload, true, token).ConfigureAwait(false);
    }

    private async Task PublishAudioAsync(IMqttClient client, AudioPublication published, bool force, CancellationToken token)
    {
        AudioState? state;
        try { state = await _desktop!.GetAudioStateAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { state = null; }

        if (state is null)
        {
            if (force || published.Available != false)
                await PublishAsync(client, _settings.AudioAvailabilityTopic, "offline", true, token).ConfigureAwait(false);
            published.Available = false;
            published.LastState = null;
            AudioStateChanged?.Invoke(null);
            return;
        }
        if (force || state != published.LastState)
        {
            await PublishAsync(client, _settings.AudioStateTopic,
                JsonSerializer.Serialize(new { volume = state.Volume, muted = state.Muted }), true, token).ConfigureAwait(false);
            AudioStateChanged?.Invoke(state);
        }
        if (force || published.Available != true)
            await PublishAsync(client, _settings.AudioAvailabilityTopic, "online", true, token).ConfigureAwait(false);
        published.Available = true;
        published.LastState = state;
    }

    private static async Task QueueRefreshAsync(ChannelWriter<ReceivedCommand> incoming, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            incoming.TryWrite(new ReceivedCommand("", false, false, []));
    }

    private Task PublishResultAsync(IMqttClient client, string? id, string status, string message, CancellationToken token, string? commandTopic = null) =>
        PublishAsync(client, _settings.ResultTopic,
            JsonSerializer.Serialize(new { id, status, message, timestamp = DateTimeOffset.UtcNow, command_topic = commandTopic ?? _settings.TestCommandTopic }), false, token);

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
