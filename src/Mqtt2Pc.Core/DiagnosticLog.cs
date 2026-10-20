using System.Text;
using System.Text.Json;

namespace Mqtt2Pc.Core;

public enum LogSeverity { Information, Warning, Error, Fatal }

public interface IDiagnosticLog
{
    // Detail must be a diagnostic code or a fixed description, never a payload,
    // credential, program argument, or exception message.
    void Write(LogSeverity severity, string eventName, string? detail = null, Exception? exception = null);
}

public sealed class NullDiagnosticLog : IDiagnosticLog
{
    public static NullDiagnosticLog Instance { get; } = new();
    public void Write(LogSeverity severity, string eventName, string? detail = null, Exception? exception = null) { }
}

public sealed class FileDiagnosticLog : IDiagnosticLog
{
    private readonly string _directory;
    private readonly long _maximumBytes;
    private readonly int _archiveCount;
    private readonly object _sync = new();
    public string FilePath => Path.Combine(_directory, "mqtt2pc.log");
    public string? LastError { get; private set; }

    public FileDiagnosticLog(string directory, long maximumBytes = 1024 * 1024, int archiveCount = 4)
    {
        if (maximumBytes < 256 || archiveCount is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _directory = Path.GetFullPath(directory);
        _maximumBytes = maximumBytes;
        _archiveCount = archiveCount;
    }

    public void Write(LogSeverity severity, string eventName, string? detail = null, Exception? exception = null)
    {
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var entry = JsonSerializer.Serialize(new
                {
                    timestamp = DateTimeOffset.UtcNow, level = severity.ToString(),
                    @event = eventName, detail,
                    exception_type = exception?.GetType().Name, hresult = exception?.HResult
                }) + Environment.NewLine;
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length + Encoding.UTF8.GetByteCount(entry) > _maximumBytes)
                {
                    for (var archive = _archiveCount; archive >= 1; archive--)
                    {
                        var destination = $"{FilePath}.{archive}";
                        var source = archive == 1 ? FilePath : $"{FilePath}.{archive - 1}";
                        if (File.Exists(source)) File.Move(source, destination, overwrite: true);
                    }
                }
                File.AppendAllText(FilePath, entry, new UTF8Encoding(false));
                LastError = null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Disk/permission failures must not prevent MQTT control or exit.
                LastError = $"Diagnostic logging unavailable ({error.GetType().Name}).";
            }
        }
    }
}
