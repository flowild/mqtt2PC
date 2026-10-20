using System.Text.Json;

namespace Mqtt2Pc.Core;

// This local ledger lets a new connection remove obsolete retained discovery,
// including after an app restart or an interrupted configuration publication.
public sealed class DiscoveryStore(string path)
{
    public IReadOnlyList<string> Load()
    {
        if (!File.Exists(path)) return [];
        var topics = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path))
            ?? throw new InvalidDataException("The discovery history is empty.");
        if (topics.Length > 1024 || topics.Any(topic => topic is null || topic.Length > 300))
            throw new InvalidDataException("The discovery history is invalid.");
        return topics;
    }

    public void Save(IEnumerable<string> topics)
    {
        var values = topics.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (values.Length > 1024) throw new InvalidDataException("Too many discovery topics.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(values));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }
}
