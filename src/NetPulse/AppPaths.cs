using System.Globalization;

namespace NetPulse;

internal static class AppPaths
{
    private const string ProfileArgumentPrefix = "--profile=";

    /// <summary>
    /// Optional profile (e.g. "dev"), from NETPULSE_PROFILE or --profile=: gives a test build its own data
    /// folder, single-instance lock, ETW session and startup entry, so it can run next to the everyday copy.
    /// </summary>
    public static string? Profile { get; } = ReadProfile();

    /// <summary>"" normally, "-dev" for the dev profile; appended to names that must not collide.</summary>
    public static string ProfileSuffix => Profile is null ? "" : "-" + Profile;

    /// <summary>" --profile=dev" or "": appended to the arguments of copies we start ourselves.</summary>
    public static string ProfileArgument => Profile is null ? "" : $" {ProfileArgumentPrefix}{Profile}";

    public static string AppName => Profile is null ? "NetPulse" : $"NetPulse ({Profile})";

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetPulse" + ProfileSuffix);

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string UsageFile => Path.Combine(DataDirectory, "usage.json");

    public static string SpeedTestFile => Path.Combine(DataDirectory, "speedtests.json");

    public static string ProcessUsageFile => Path.Combine(DataDirectory, "process-usage.json");

    public static string ErrorLogFile => Path.Combine(DataDirectory, "error.log");

    /// <summary>ASCII letters/digits only, at most 16, lower-cased; anything else means no profile.</summary>
    private static string? ReadProfile()
    {
        var value = (Environment.GetCommandLineArgs()
                .FirstOrDefault(a => a.StartsWith(ProfileArgumentPrefix, StringComparison.OrdinalIgnoreCase))?[ProfileArgumentPrefix.Length..]
            ?? Environment.GetEnvironmentVariable("NETPULSE_PROFILE"))?.Trim();
        return !string.IsNullOrEmpty(value) && value.Length <= 16 && value.All(char.IsAsciiLetterOrDigit)
            ? value.ToLowerInvariant()
            : null;
    }
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
