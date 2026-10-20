using Mqtt2Pc.Core;

namespace Mqtt2Pc;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var startup = args.Contains("--startup", StringComparer.OrdinalIgnoreCase);
        using var mutex = new Mutex(true, @"Local\mqtt2PC-" + Environment.UserName, out var firstInstance);
        if (!firstInstance)
        {
            if (!startup) MessageBox.Show("mqtt2PC is already running. Open its settings from the system tray.", "mqtt2PC");
            return 0;
        }
        var log = new FileDiagnosticLog(AppPaths.LogDirectory);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, eventArgs) =>
        {
            log.Write(LogSeverity.Error, "app.ui_exception", exception: eventArgs.Exception);
            MessageBox.Show("An unexpected error occurred. Open the log folder from the tray menu for diagnostics.", "mqtt2PC", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            log.Write(LogSeverity.Fatal, "app.unhandled_exception", exception: eventArgs.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            log.Write(LogSeverity.Error, "app.background_exception", exception: eventArgs.Exception);
            eventArgs.SetObserved();
        };
        try
        {
            log.Write(LogSeverity.Information, "app.started", "version=0.4.0");
            using var context = new TrayApplicationContext(log, startup);
            Application.Run(context);
            return 0;
        }
        catch (Exception exception)
        {
            log.Write(LogSeverity.Fatal, "app.start_failed", exception: exception);
            if (!startup) MessageBox.Show("mqtt2PC could not start. Check the diagnostic log in your local mqtt2PC data folder.", "mqtt2PC", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            log.Write(LogSeverity.Information, "app.exited");
            mutex.ReleaseMutex();
        }
    }
}
