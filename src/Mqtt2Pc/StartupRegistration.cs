using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Mqtt2Pc.Core;

namespace Mqtt2Pc;

internal interface IStartupRegistration
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}

internal sealed class StartupRegistration(string executablePath, string? startupDirectory = null, IDiagnosticLog? log = null) : IStartupRegistration
{
    internal string ShortcutPath => Path.Combine(startupDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup), "mqtt2PC.lnk");

    public bool IsEnabled
    {
        get
        {
            if (!File.Exists(ShortcutPath)) return false;
            try
            {
                var shortcut = WindowsShortcut.Read(ShortcutPath);
                return string.Equals(shortcut.Target, Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase) && shortcut.Arguments == "--startup";
            }
            catch (Exception exception) when (exception is COMException or IOException)
            {
                log?.Write(LogSeverity.Warning, "startup.read_failed", exception: exception);
                return false;
            }
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            Update(enabled);
            log?.Write(LogSeverity.Information, enabled ? "startup.enabled" : "startup.disabled");
        }
        catch (Exception exception)
        {
            log?.Write(LogSeverity.Error, "startup.update_failed", exception: exception);
            throw;
        }
    }

    private void Update(bool enabled)
    {
        if (!enabled)
        {
            if (IsEnabled) File.Delete(ShortcutPath);
            return;
        }
        if (!Path.IsPathFullyQualified(executablePath) || !File.Exists(executablePath))
            throw new FileNotFoundException("The mqtt2PC executable must exist before enabling startup.");
        var directory = Path.GetDirectoryName(ShortcutPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"mqtt2PC-{Guid.NewGuid():N}.lnk");
        try
        {
            WindowsShortcut.Create(temporaryPath, executablePath, "--startup");
            File.Move(temporaryPath, ShortcutPath, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }
}

internal static class WindowsShortcut
{
    public static void Create(string path, string executable, string arguments)
    {
        var instance = (object)new ShellLink();
        try
        {
            var link = (IShellLink)instance;
            link.SetPath(Path.GetFullPath(executable));
            link.SetArguments(arguments);
            link.SetWorkingDirectory(Path.GetDirectoryName(Path.GetFullPath(executable))!);
            link.SetDescription("Start mqtt2PC when you sign in");
            link.SetShowCmd(1);
            ((IPersistFile)instance).Save(path, true);
        }
        finally { Marshal.ReleaseComObject(instance); }
    }

    public static (string Target, string Arguments) Read(string path)
    {
        var instance = (object)new ShellLink();
        try
        {
            ((IPersistFile)instance).Load(path, 0);
            var link = (IShellLink)instance;
            var target = new StringBuilder(32768);
            var arguments = new StringBuilder(32768);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 4);
            link.GetArguments(arguments, arguments.Capacity);
            return (target.ToString(), arguments.ToString());
        }
        finally { Marshal.ReleaseComObject(instance); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLink
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, IntPtr findData, uint flags);
        void GetIDList(out IntPtr item);
        void SetIDList(IntPtr item);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int length);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int length);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int length);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetHotkey(out short value);
        void SetHotkey(short value);
        void GetShowCmd(out int value);
        void SetShowCmd(int value);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int length, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string value, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
