using System.Globalization;

namespace NetPulse.Core;

/// <summary>What the per-user install writes, kept apart from the registry and file code so it can be tested.</summary>
public static class InstallLayout
{
    public const string UninstallArgument = "--uninstall";

    /// <summary>
    /// Values of HKCU\...\Uninstall\NetPulse: what "Installed apps" in Windows Settings shows and runs.
    /// Strings stay strings, numbers become REG_DWORD.
    /// </summary>
    public static IReadOnlyDictionary<string, object> UninstallValues(
        string displayName, string exe, string version, long sizeBytes, DateOnly installedOn, string extraArguments = "")
    {
        var directory = Path.GetDirectoryName(exe) ?? throw new ArgumentException("No directory.", nameof(exe));
        var uninstall = $"\"{exe}\" {UninstallArgument}{extraArguments}";
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["DisplayName"] = displayName,
            ["DisplayVersion"] = version,
            ["Publisher"] = "NetPulse",
            ["DisplayIcon"] = $"\"{exe}\",0",
            ["InstallLocation"] = directory,
            ["InstallDate"] = installedOn.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            ["UninstallString"] = uninstall,
            ["QuietUninstallString"] = uninstall + " --quiet",
            ["EstimatedSize"] = (int)Math.Clamp((sizeBytes + 1023) / 1024, 0, int.MaxValue), // KB
            ["NoModify"] = 1,
            ["NoRepair"] = 1,
        };
    }

    /// <summary>What the settings page should offer for the per-user install.</summary>
    public static InstallOffer Offer(bool runningInstalledCopy, string? installedVersion, string currentVersion)
    {
        if (runningInstalledCopy)
        {
            return InstallOffer.None;
        }

        if (installedVersion is null)
        {
            return InstallOffer.Install;
        }

        var installed = AppVersion.Parse(installedVersion);
        var current = AppVersion.Parse(currentVersion);
        return installed is null || current is null || current > installed ? InstallOffer.Update
            : current == installed ? InstallOffer.Reinstall
            : InstallOffer.None; // this copy is older than the installed one: never downgrade from here
    }
}

public enum InstallOffer
{
    /// <summary>Running the installed copy, or an older copy than the installed one.</summary>
    None,
    Install,
    Update,
    Reinstall,
}
