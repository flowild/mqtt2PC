using System.Threading.Channels;
using Mqtt2Pc.Core;

namespace Mqtt2Pc.Tests;

internal sealed class FakeDesktopControls : IDesktopControls
{
    public AudioState State { get; set; } = new(35, false);
    public bool Available { get; set; } = true;
    public bool FailActions { get; set; }
    public int VolumeWrites { get; private set; }
    public int MuteWrites { get; private set; }
    public Channel<MediaAction> MediaKeys { get; } = Channel.CreateUnbounded<MediaAction>();

    public Task<AudioState> GetAudioStateAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Available) throw new InvalidOperationException("No default audio endpoint.");
        return Task.FromResult(State);
    }

    public Task SetVolumeAsync(int percent, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Available || FailActions) throw new InvalidOperationException("Audio action failed.");
        State = State with { Volume = percent };
        VolumeWrites++;
        return Task.CompletedTask;
    }

    public Task SetMuteAsync(bool muted, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Available || FailActions) throw new InvalidOperationException("Audio action failed.");
        State = State with { Muted = muted };
        MuteWrites++;
        return Task.CompletedTask;
    }

    public Task SendMediaAsync(MediaAction action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (FailActions) throw new InvalidOperationException("Media action failed.");
        MediaKeys.Writer.TryWrite(action);
        return Task.CompletedTask;
    }
}
