using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Mqtt2Pc.Core;

namespace Mqtt2Pc;

internal sealed class WindowsPcActions : IPcActions
{
    public Task<int> LaunchAsync(ConfiguredAction action, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var info = CreateActionStartInfo(action);
        if (!File.Exists(action.Executable)) throw new FileNotFoundException("The configured program or batch file was not found.");
        if (!Directory.Exists(info.WorkingDirectory)) throw new DirectoryNotFoundException("The working directory was not found.");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Windows did not create a process.");
        return process.Id;
    }, token);

    internal static ProcessStartInfo CreateActionStartInfo(ConfiguredAction action)
    {
        action.Validate();
        var info = new ProcessStartInfo(action.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = action.WorkingDirectory.Length == 0 ? Path.GetDirectoryName(action.Executable)! : action.WorkingDirectory
        };
        if (action.IsBatchFile)
        {
            info.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            // /s strips the outside quote pair, leaving the batch path and each
            // argument quoted. Substitution is non-recursive, preserving literal
            // percent references in configured paths and values.
            var variables = new List<string> { "MQTT2PC_BATCH_FILE" };
            info.Environment[variables[0]] = action.Executable;
            for (var index = 0; index < action.Arguments.Length; index++)
            {
                var name = $"MQTT2PC_BATCH_ARG_{index}";
                variables.Add(name);
                info.Environment[name] = action.Arguments[index];
            }
            info.Arguments = "/d /s /v:off /c \"" + string.Join(" ", variables.Select(name =>
                info.Environment[name]?.Length == 0 ? "\"\"" : $"\"%{name}%\"")) + "\"";
        }
        else foreach (var argument in action.Arguments) info.ArgumentList.Add(argument);
        return info;
    }

    public Task ExecutePowerAsync(PowerAction action, CancellationToken token) => Task.Run(async () =>
    {
        token.ThrowIfCancellationRequested();
        switch (action)
        {
            case PowerAction.Lock:
                if (!LockWorkStation()) throw new Win32Exception(Marshal.GetLastWin32Error());
                break;
            case PowerAction.Sleep:
                using (var privilege = ShutdownPrivilege.Enable())
                    if (!SetSuspendState(false, false, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
                break;
            case PowerAction.Shutdown:
            case PowerAction.Restart:
                using (var process = Process.Start(CreateShutdownStartInfo(action)) ?? throw new InvalidOperationException("Windows did not start shutdown.exe."))
                {
                    await process.WaitForExitAsync(token).ConfigureAwait(false);
                    if (process.ExitCode != 0) throw new InvalidOperationException($"Windows rejected the power request (exit {process.ExitCode}).");
                }
                break;
            default: throw new ArgumentOutOfRangeException(nameof(action));
        }
    }, token);

    internal static ProcessStartInfo CreateShutdownStartInfo(PowerAction action)
    {
        if (action is not (PowerAction.Shutdown or PowerAction.Restart)) throw new ArgumentOutOfRangeException(nameof(action));
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.SystemDirectory
        };
        info.ArgumentList.Add(action == PowerAction.Shutdown ? "/s" : "/r");
        info.ArgumentList.Add("/t");
        // A nonzero shutdown timeout implies /f on Windows. Use zero and never
        // force-close applications that may have unsaved work.
        info.ArgumentList.Add("0");
        return info;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool disableWakeEvents);

    private sealed class ShutdownPrivilege(SafeAccessTokenHandle token, TokenPrivileges previous) : IDisposable
    {
        public static ShutdownPrivilege Enable()
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x20 | 0x08, out var token))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var desired = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
                if (!AdjustTokenPrivileges(token, false, ref desired, Marshal.SizeOf<TokenPrivileges>(), out var previous, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var error = Marshal.GetLastWin32Error();
                if (error != 0) throw new Win32Exception(error);
                return new ShutdownPrivilege(token, previous);
            }
            catch { token.Dispose(); throw; }
        }

        public void Dispose()
        {
            try
            {
                var restore = previous;
                if (!AdjustTokenPrivileges(token, false, ref restore, Marshal.SizeOf<TokenPrivileges>(), out _, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { token.Dispose(); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges state, int size, out TokenPrivileges previous, out int returnedSize);
}
