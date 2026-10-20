using System.ComponentModel;
using System.Runtime.InteropServices;
using Mqtt2Pc.Core;

namespace Mqtt2Pc;

// Keep Core Audio COM objects on the Windows UI apartment. They are reacquired on
// every read/write so unplugging a headset or changing the default output recovers.
internal sealed class WindowsDesktopControls(Control dispatcher) : IDesktopControls
{
    public Task<AudioState> GetAudioStateAsync(CancellationToken token) =>
        dispatcher.InvokeAsync(WindowsAudio.GetState, token);

    public Task SetVolumeAsync(int percent, CancellationToken token) =>
        dispatcher.InvokeAsync(() => WindowsAudio.SetVolume(percent), token);

    public Task SetMuteAsync(bool muted, CancellationToken token) =>
        dispatcher.InvokeAsync(() => WindowsAudio.SetMute(muted), token);

    public Task SendMediaAsync(MediaAction action, CancellationToken token) =>
        dispatcher.InvokeAsync(() => WindowsMedia.Send(action), token);
}

internal static class WindowsAudio
{
    public static AudioState GetState() => WithEndpoint(endpoint =>
    {
        Marshal.ThrowExceptionForHR(endpoint.GetMasterVolumeLevelScalar(out var volume));
        Marshal.ThrowExceptionForHR(endpoint.GetMute(out var mute));
        return new AudioState(Math.Clamp((int)Math.Round(volume * 100, MidpointRounding.AwayFromZero), 0, 100), mute);
    });

    public static void SetVolume(int percent)
    {
        if (percent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        WithEndpoint(endpoint =>
        {
            var context = Guid.Empty;
            Marshal.ThrowExceptionForHR(endpoint.SetMasterVolumeLevelScalar(percent / 100f, ref context));
            return true;
        });
    }

    public static void SetMute(bool muted) => WithEndpoint(endpoint =>
    {
        var context = Guid.Empty;
        Marshal.ThrowExceptionForHR(endpoint.SetMute(muted, ref context));
        return true;
    });

    private static T WithEndpoint<T>(Func<IAudioEndpointVolume, T> action)
    {
        var enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumerator();
        IMMDevice? device = null;
        object? activated = null;
        try
        {
            // eRender = 0, eMultimedia = 1. This controls the default playback output.
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0, 1, out device));
            var endpointId = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref endpointId, 23, IntPtr.Zero, out activated));
            return action((IAudioEndpointVolume)activated);
        }
        finally
        {
            if (activated is not null) Marshal.ReleaseComObject(activated);
            if (device is not null) Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr activation, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint count);
        [PreserveSig] int VolumeStepUp(ref Guid context);
        [PreserveSig] int VolumeStepDown(ref Guid context);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
        [PreserveSig] int GetVolumeRange(out float minimum, out float maximum, out float increment);
    }
}

internal static class WindowsMedia
{
    public static void Send(MediaAction action)
    {
        var inputs = CreateInputs(action);
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not send the media key.");
    }

    internal static Input[] CreateInputs(MediaAction action)
    {
        ushort key = action switch
        {
            MediaAction.PlayPause => 0xB3,
            MediaAction.Next => 0xB0,
            MediaAction.Previous => 0xB1,
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        return [
            new Input { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { VirtualKey = key, Flags = 1 } } },
            new Input { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { VirtualKey = key, Flags = 3 } } }
        ];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input { public uint Type; public InputData Data; }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputData
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        // INPUT's union must also accommodate MOUSEINPUT, even for keyboard events.
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
}
