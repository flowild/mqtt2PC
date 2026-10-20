namespace Mqtt2Pc;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, @"Local\mqtt2PC-" + Environment.UserName, out var firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("mqtt2PC is already running. Open its settings from the system tray.", "mqtt2PC");
            return;
        }
        try
        {
            using var context = new TrayApplicationContext();
            Application.Run(context);
        }
        finally { mutex.ReleaseMutex(); }
    }
}
