using System.Text;
using System.Text.Json;
using Mqtt2Pc.Core;
using Xunit;

namespace Mqtt2Pc.Tests;

public sealed class SettingsTests
{
    [Theory]
    [InlineData("mqtt.local")]
    [InlineData("192.168.1.10")]
    [InlineData("::1")]
    public void AcceptsHostnamesAndAddresses(string host) => new AppSettings { BrokerHost = host }.Validate();

    [Theory]
    [InlineData("")]
    [InlineData("mqtt://broker.local")]
    [InlineData("broker.local:1883")]
    [InlineData(" broker.local")]
    public void RejectsInvalidBrokerHosts(string host) =>
        Assert.Throws<ArgumentException>(() => new AppSettings { BrokerHost = host }.Validate());

    [Theory]
    [InlineData("pc/command")]
    [InlineData("pc+#")]
    [InlineData("")]
    public void RejectsDeviceIdsThatCouldChangeTopicRouting(string id) =>
        Assert.Throws<ArgumentException>(() => new AppSettings { BrokerHost = "localhost", DeviceId = id }.Validate());

    [Fact]
    public void SettingsRoundTripWithoutCredentialsOrComputedTopics()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mqtt2pc-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            Assert.Null(store.Load());
            var settings = new AppSettings { BrokerHost = "mqtt.local", Username = "pc-user", UseTls = true, BrokerPort = 8883 };
            store.Save(settings);
            Assert.Equal(settings, store.Load());
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("topic", text, StringComparison.OrdinalIgnoreCase);
            store.Save(settings with { BrokerPort = 8884 });
            Assert.Equal(8884, store.Load()!.BrokerPort);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void BrokenSettingsArePreservedForRecovery()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mqtt2pc-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{broken}");
            Assert.Throws<JsonException>(() => new SettingsStore(path).Load());
            Assert.Equal("{broken}", File.ReadAllText(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData("{\"id\":\"unique-1\"}")]
    [InlineData("{\"id\":\"unique-1\",\"message\":\"hello\"}")]
    public void ParsesTestCommands(string json) => Assert.Equal("unique-1", TestCommand.Parse(Encoding.UTF8.GetBytes(json)).Id);

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"id\":\"\"}")]
    [InlineData("{\"id\":\"bad/id\"}")]
    [InlineData("{\"id\":\"ok\",\"message\":\"\"}")]
    [InlineData("{\"id\":\"ok\",\"message\":\"hello\\nworld\"}")]
    [InlineData("{\"id\":\"ok\",\"execute\":\"something\"}")]
    [InlineData("not json")]
    public void RejectsMalformedCommands(string json) => Assert.ThrowsAny<Exception>(() => TestCommand.Parse(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void RejectsOversizedPayloads() =>
        Assert.Throws<ArgumentException>(() => TestCommand.Parse(new byte[TestCommand.MaximumPayloadBytes + 1]));
}
