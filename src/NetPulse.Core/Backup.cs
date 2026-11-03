using System.Text.Json;

namespace NetPulse.Core;

/// <summary>Settings and all statistics in one file, to move NetPulse to another PC or restore it.</summary>
public sealed class BackupDocument
{
    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;

    public string AppVersion { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public AppSettings Settings { get; set; } = new();

    public UsageDocument Usage { get; set; } = new();

    public NetworkUsageDocument NetworkUsage { get; set; } = new();

    public ProcessUsageDocument ProcessUsage { get; set; } = new();

    public static void Write(string path, BackupDocument backup)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(backup, AppJson.Options));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Reads and checks a backup; throws <see cref="InvalidDataException"/> if it is not a usable one.</summary>
    public static BackupDocument Read(string path)
    {
        BackupDocument? backup;
        try
        {
            backup = JsonSerializer.Deserialize<BackupDocument>(File.ReadAllText(path), AppJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new InvalidDataException("Not a NetPulse backup file.", ex);
        }

        if (backup is null || backup.Format < 1)
        {
            throw new InvalidDataException("Not a NetPulse backup file.");
        }

        if (backup.Format > CurrentFormat)
        {
            throw new InvalidDataException("This backup was made by a newer version of NetPulse.");
        }

        backup.Settings = (backup.Settings ?? new AppSettings()).Normalize();
        backup.Usage ??= new UsageDocument();
        backup.NetworkUsage ??= new NetworkUsageDocument();
        backup.ProcessUsage ??= new ProcessUsageDocument();
        return backup;
    }
}
