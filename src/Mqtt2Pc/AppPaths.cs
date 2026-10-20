namespace Mqtt2Pc;

internal static class AppPaths
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mqtt2PC");
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
    public static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, "mqtt2PC.exe");
}
