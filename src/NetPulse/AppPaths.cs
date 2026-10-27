using System.Globalization;

namespace NetPulse;

internal static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetPulse");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string UsageFile => Path.Combine(DataDirectory, "usage.json");

    public static string SpeedTestFile => Path.Combine(DataDirectory, "speedtests.json");

    public static string ErrorLogFile => Path.Combine(DataDirectory, "error.log");
}

/// <summary>Local-only crash log. Nothing is ever sent anywhere.</summary>
internal static class ErrorLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly Lock Gate = new();

    public static void Write(Exception? exception, string? context = null)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                var file = new FileInfo(AppPaths.ErrorLogFile);
                if (file.Exists && file.Length > MaxBytes)
                {
                    File.Move(file.FullName, file.FullName + ".old", overwrite: true);
                }

                var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                File.AppendAllText(
                    AppPaths.ErrorLogFile,
                    $"[{stamp}] {context}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
