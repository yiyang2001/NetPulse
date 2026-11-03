using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetPulse.Core;

/// <summary>The JSON format of every file NetPulse writes, including backups.</summary>
public static class AppJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>
/// Loads and saves one JSON document. Writes go to a temporary file that then replaces the target,
/// so a crash or power loss never leaves a half-written file. An unreadable file is moved aside
/// (".corrupt") and defaults are used, rather than failing startup.
/// </summary>
public sealed class JsonFileStore<T>(string path, Func<T> createDefault)
    where T : class
{
    private static readonly JsonSerializerOptions Options = AppJson.Options;

    public string Path { get; } = path;

    public T Load()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return createDefault();
            }

            using var stream = File.OpenRead(Path);
            return JsonSerializer.Deserialize<T>(stream, Options) ?? createDefault();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            TryMoveAside();
            return createDefault();
        }
    }

    public void Save(T value)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = Path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, Options);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, Path, overwrite: true);
    }

    private void TryMoveAside()
    {
        try
        {
            File.Move(Path, Path + ".corrupt", overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
