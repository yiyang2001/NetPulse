using Microsoft.Win32;
using NetPulse.Core;

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
            key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
        }
        else
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
