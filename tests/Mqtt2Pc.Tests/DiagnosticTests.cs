using System.Collections.Concurrent;
using System.Text.Json;
using MQTTnet.Protocol;
using Mqtt2Pc.Core;
using Xunit;

namespace Mqtt2Pc.Tests;

internal sealed record LoggedEvent(LogSeverity Severity, string Name, string? Detail, string? ExceptionType);

internal sealed class CapturedLog : IDiagnosticLog
{
    public ConcurrentQueue<LoggedEvent> Entries { get; } = new();
    public void Write(LogSeverity severity, string eventName, string? detail = null, Exception? exception = null) =>
        Entries.Enqueue(new(severity, eventName, detail, exception?.GetType().Name));
}

public sealed class DiagnosticTests
{
    [Fact]
    public void WritesStructuredLogsWithoutExceptionMessagesAndBoundsRotation()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mqtt2pc-logs-{Guid.NewGuid():N}");
        try
        {
            var log = new FileDiagnosticLog(directory, 512, 2);
            for (var index = 0; index < 20; index++)
                log.Write(LogSeverity.Error, "test.failure", "fixed-code", new InvalidOperationException("secret-password script-argument broker-payload"));
            Assert.Null(log.LastError);
            var files = Directory.GetFiles(directory);
            Assert.Equal(3, files.Length);
            foreach (var file in files)
            {
                Assert.True(new FileInfo(file).Length <= 512);
                var content = File.ReadAllText(file);
                Assert.DoesNotContain("secret-password", content);
                Assert.DoesNotContain("script-argument", content);
                Assert.DoesNotContain("broker-payload", content);
                foreach (var line in File.ReadAllLines(file))
                {
                    using var json = JsonDocument.Parse(line);
                    Assert.Equal("InvalidOperationException", json.RootElement.GetProperty("exception_type").GetString());
                    Assert.Equal("Error", json.RootElement.GetProperty("level").GetString());
                }
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
                Directory.Delete(directory);
            }
        }
    }

    [Fact]
    public void LoggingFailureDoesNotBreakTheCaller()
    {
        var file = Path.Combine(Path.GetTempPath(), $"mqtt2pc-blocked-log-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(file, "This is a file, so a log directory cannot be created here.");
            var log = new FileDiagnosticLog(Path.Combine(file, "logs"));
            log.Write(LogSeverity.Information, "test.event");
            Assert.NotNull(log.LastError);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task BrokerAuthenticationFailureIsActionableAndRetriesCanBeStopped()
    {
        await using var broker = new BrokerFixture();
        broker.Server.ValidatingConnectionAsync += args =>
        {
            if (args.ClientId == broker.Settings.ClientId) args.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
            return Task.CompletedTask;
        };
        await broker.Server.StartAsync();
        var log = new CapturedLog();
        await using var agent = new RunningAgent(broker.Settings with { Username = "wrong" }, "secret-test-password", log: log);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!log.Entries.Any(entry => entry.Name == "mqtt.retry_scheduled")) await Task.Delay(20, timeout.Token);
        Assert.Contains(log.Entries, entry => entry.Name == "mqtt.connection_failed" && entry.Detail!.Contains("BadUserNameOrPassword", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Entries, entry => entry.Name == "mqtt.connected");
        Assert.Contains(agent.Statuses, status => status.Message.Contains("BadUserNameOrPassword", StringComparison.Ordinal));
        Assert.DoesNotContain(JsonSerializer.Serialize(log.Entries), "secret-test-password");
        await agent.StopAsync();
        Assert.Contains(log.Entries, entry => entry.Name == "mqtt.stopped");
    }

    [Fact]
    public async Task ReconnectAndActionResultsAreLoggedWithoutCommandContent()
    {
        await using var broker = new BrokerFixture();
        await broker.Server.StartAsync();
        using var observer = await broker.ConnectObserverAsync();
        var log = new CapturedLog();
        await using var relay = new TcpRelay(broker.Port);
        await using var agent = new RunningAgent(broker.Settings with { BrokerPort = relay.Port }, log: log);
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, message => message.Payload == "online");
        await observer.PublishAsync(broker.Settings.TestCommandTopic, "{\"id\":\"private-request-id\",\"message\":\"private-payload\"}");
        await observer.WaitAsync(broker.Settings.ResultTopic, message => Observer.HasStatus(message, "success"));
        Assert.Contains(log.Entries, entry => entry.Name == "command.result" && entry.Detail == "notification:success");
        relay.DropConnection();
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, message => message.Payload == "offline");
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, message => message.Payload == "online");
        Assert.True(log.Entries.Count(entry => entry.Name == "mqtt.connected") >= 2);
        Assert.Contains(log.Entries, entry => entry.Name == "mqtt.disconnected");
        var serialized = JsonSerializer.Serialize(log.Entries);
        Assert.DoesNotContain("private-request-id", serialized);
        Assert.DoesNotContain("private-payload", serialized);
    }

    [Fact]
    public async Task ProgramFailureLogsOnlyExceptionTypeAndActionCategory()
    {
        await using var broker = new BrokerFixture();
        await broker.Server.StartAsync();
        using var observer = await broker.ConnectObserverAsync();
        var log = new CapturedLog();
        await using var agent = new RunningAgent(broker.Settings with
        {
            Actions = [StageThreeTests.ExampleAction() with { Arguments = ["private-script-argument"] }]
        }, desktop: new FakeDesktopControls(), pcActions: new FakePcActions { Fail = true }, log: log);
        await observer.WaitAsync(broker.Settings.AvailabilityTopic, message => message.Payload == "online");
        await observer.PublishAsync(broker.Settings.ActionCommandTopic("start_app"), "{\"id\":\"private-action-id\"}");
        await observer.WaitAsync(broker.Settings.ResultTopic, message => Observer.HasStatus(message, "error"));
        Assert.Contains(log.Entries, entry => entry.Name == "action.failed" && entry.ExceptionType == "InvalidOperationException");
        var serialized = JsonSerializer.Serialize(log.Entries);
        Assert.DoesNotContain("private-script-argument", serialized);
        Assert.DoesNotContain("private-action-id", serialized);
    }
}
