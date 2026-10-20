using System.Threading.Channels;
using Mqtt2Pc.Core;

namespace Mqtt2Pc.Tests;

internal sealed class FakePcActions : IPcActions
{
    public Channel<PowerAction> PowerRequests { get; } = Channel.CreateUnbounded<PowerAction>();
    public Channel<ConfiguredAction> Launches { get; } = Channel.CreateUnbounded<ConfiguredAction>();
    public bool Fail { get; set; }
    public Func<CancellationToken, Task>? BeforePower { get; set; }

    public async Task ExecutePowerAsync(PowerAction action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (BeforePower is not null) await BeforePower(token);
        if (Fail) throw new InvalidOperationException("Simulated Windows failure.");
        PowerRequests.Writer.TryWrite(action);
    }

    public Task<int> LaunchAsync(ConfiguredAction action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Fail) throw new InvalidOperationException("Simulated process launch failure.");
        Launches.Writer.TryWrite(action);
        return Task.FromResult(12345);
    }
}
