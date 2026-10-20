namespace Mqtt2Pc.Core;

public sealed record AudioState(int Volume, bool Muted);

public enum MediaAction { PlayPause, Next, Previous }

public interface IDesktopControls
{
    Task<AudioState> GetAudioStateAsync(CancellationToken token);
    Task SetVolumeAsync(int percent, CancellationToken token);
    Task SetMuteAsync(bool muted, CancellationToken token);
    Task SendMediaAsync(MediaAction action, CancellationToken token);
}
