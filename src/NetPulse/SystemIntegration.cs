using Microsoft.Win32;
using NetPulse.Core;
using NetPulse.Platform;

namespace NetPulse;

/// <summary>Per-user "start with Windows" entry under HKCU\...\Run (no admin rights needed).</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly string ValueName = "NetPulse" + AppPaths.ProfileSuffix;

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? Application.ExecutablePath;
            key.SetValue(ValueName, $"\"{exe}\"{AppPaths.ProfileArgument}", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>Deletes the entry when it starts an exe inside <paramref name="directory"/> (used by uninstall).</summary>
    public static void RemoveIfInside(string directory)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        var prefix = "\"" + Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
        if (key?.GetValue(ValueName) is string value && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>Points an existing entry at the current executable, e.g. after the app folder was moved.</summary>
    public static void RefreshPathIfEnabled()
    {
        if (IsEnabled())
        {
            SetEnabled(true);
        }
    }
}

internal enum StartupMode
{
    Off,
    Normal,
    Elevated,
}

/// <summary>The two ways of starting with Windows, kept mutually exclusive.</summary>
internal static class Startup
{
    public static StartupMode Current() =>
        ElevatedAutostart.IsEnabled() ? StartupMode.Elevated
        : StartupRegistration.IsEnabled() ? StartupMode.Normal
        : StartupMode.Off;

    /// <summary>
    /// Switches modes; anything involving <see cref="StartupMode.Elevated"/> shows a UAC prompt.
    /// Elevated → Elevated re-installs, i.e. updates the installed copy. Returns null on success, else why not.
    /// </summary>
    public static string? Change(StartupMode from, StartupMode to)
    {
        if (to == StartupMode.Elevated)
        {
            var error = ElevatedAutostart.RequestInstall();
            if (error is null)
            {
                StartupRegistration.SetEnabled(false);
            }

            return error;
        }

        if (from == StartupMode.Elevated && ElevatedAutostart.RequestRemove() is { } removeError)
        {
            return removeError;
        }

        StartupRegistration.SetEnabled(to == StartupMode.Normal);
        return null;
    }
}

internal sealed record WidgetPalette(Color Background, Color Text, Color Download, Color Upload)
{
    public static readonly WidgetPalette Dark = new(
        Color.FromArgb(32, 32, 32), Color.FromArgb(240, 240, 240), Color.FromArgb(52, 211, 153), Color.FromArgb(96, 165, 250));

    public static readonly WidgetPalette Light = new(
        Color.FromArgb(243, 243, 243), Color.FromArgb(28, 28, 28), Color.FromArgb(5, 150, 105), Color.FromArgb(37, 99, 235));

    public static WidgetPalette For(ThemeMode mode) => mode switch
    {
        ThemeMode.Dark => Dark,
        ThemeMode.Light => Light,
        _ => TaskbarUsesLightTheme() ? Light : Dark,
    };

    /// <summary>Matches the widget to the taskbar, which follows the "Windows mode" setting.</summary>
    private static bool TaskbarUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
    }
}
