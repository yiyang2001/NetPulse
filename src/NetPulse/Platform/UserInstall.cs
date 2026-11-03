using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32;
using NetPulse.Core;

namespace NetPulse.Platform;

/// <summary>
/// Per-user install, no admin rights: copies the running build to %LOCALAPPDATA%\Programs\NetPulse, adds a
/// Start menu shortcut and an entry under "Installed apps" whose uninstall runs this exe with --uninstall.
/// The portable exe keeps working as before; installing is optional.
/// </summary>
internal static class UserInstall
{
    private static readonly string KeyName = "NetPulse" + AppPaths.ProfileSuffix;
    private static readonly string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + KeyName;

    public static string InstallDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", KeyName);

    public static string InstalledExe => Path.Combine(InstallDirectory, "NetPulse.exe");

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppPaths.AppName + ".lnk");

    private static string CurrentExe => Environment.ProcessPath ?? Application.ExecutablePath;

    public static bool IsRunningInstalledCopy => IsSamePath(CurrentExe, InstalledExe);

    public static bool IsInstalled => File.Exists(InstalledExe);

    public static string? InstalledVersion() =>
        IsInstalled ? FileVersionInfo.GetVersionInfo(InstalledExe).ProductVersion?.Split('+')[0] : null;

    public static InstallOffer Offer() =>
        InstallLayout.Offer(IsRunningInstalledCopy, InstalledVersion(), ElevatedAutostart.CurrentVersion);

    /// <summary>Copies this build into the install folder (replacing an older one) and registers it.</summary>
    public static void Install()
    {
        AppFiles.CopyRunningBuild(InstallDirectory);
        CreateShortcut(ShortcutPath, InstalledExe, AppPaths.ProfileArgument.Trim());
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey, writable: true);
        var values = InstallLayout.UninstallValues(
            AppPaths.AppName,
            InstalledExe,
            ElevatedAutostart.CurrentVersion,
            Directory.EnumerateFiles(InstallDirectory).Sum(f => new FileInfo(f).Length),
            DateOnly.FromDateTime(DateTime.Now),
            AppPaths.ProfileArgument);
        foreach (var (name, value) in values)
        {
            key.SetValue(name, value, value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
        }
    }

    /// <summary>Starts the installed copy so it can take over from this one (which then exits).</summary>
    public static void StartInstalledCopy(string arguments)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = InstalledExe,
            Arguments = arguments + AppPaths.ProfileArgument,
            WorkingDirectory = InstallDirectory,
            UseShellExecute = false,
        })?.Dispose();
    }

    /// <summary>Removes the shortcut, the "Installed apps" entry and the "start with Windows" entry if it points into the install folder.</summary>
    public static void Unregister()
    {
        TryDelete(ShortcutPath);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        StartupRegistration.RemoveIfInside(InstallDirectory);
    }

    /// <summary>
    /// Deletes the install folder. It holds the running exe when the installed copy uninstalls itself, so
    /// then a short-lived cmd deletes it a few seconds after this process has exited.
    /// </summary>
    public static void DeleteFiles()
    {
        if (!Directory.Exists(InstallDirectory))
        {
            return;
        }

        if (!IsRunningInstalledCopy)
        {
            Directory.Delete(InstallDirectory, recursive: true);
            return;
        }

        // InstallDirectory is always %LOCALAPPDATA%\Programs\NetPulse[-profile]; nothing else is ever passed here.
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        Process.Start(new ProcessStartInfo
        {
            FileName = cmd,
            Arguments = $"/d /c \"\"{ping}\" -n 4 127.0.0.1 >nul & rd /s /q \"{InstallDirectory}\"\"",
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
        })?.Dispose();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex, "Uninstall " + path);
        }
    }

    private static bool IsSamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static void CreateShortcut(string path, string target, string arguments)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetArguments(arguments);
            link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
            link.SetDescription("NetPulse");
            link.SetIconLocation(target, 0);
            ((IPersistFile)link).Save(path, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int maxPath, IntPtr findData, uint flags);

        void GetIDList(out IntPtr idList);

        void SetIDList(IntPtr idList);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int maxName);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int maxPath);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int maxPath);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);

        void GetHotkey(out short hotkey);

        void SetHotkey(short hotkey);

        void GetShowCmd(out int showCmd);

        void SetShowCmd(int showCmd);

        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder iconPath, int maxPath, out int icon);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);

        void Resolve(IntPtr hwnd, uint flags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}

/// <summary>
/// Copies the running build somewhere else: a single-file publish is just the exe, a dev build is the whole
/// output folder. The exe is always saved as NetPulse.exe, whatever the downloaded file was called.
/// </summary>
internal static class AppFiles
{
    public static void CopyRunningBuild(string targetDirectory)
    {
        var exe = Environment.ProcessPath ?? Application.ExecutablePath;
        if (string.Equals(Path.GetFullPath(Path.GetDirectoryName(exe)!), Path.GetFullPath(targetDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var source = AppContext.BaseDirectory;
        Directory.CreateDirectory(targetDirectory);
        var files = File.Exists(Path.Combine(source, "NetPulse.dll"))
            ? Directory.EnumerateFiles(source).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".dll" or ".exe" or ".json" or ".pdb")
            : [exe];
        foreach (var file in files)
        {
            var name = string.Equals(file, exe, StringComparison.OrdinalIgnoreCase) ? "NetPulse.exe" : Path.GetFileName(file);
            File.Copy(file, Path.Combine(targetDirectory, name), overwrite: true);
        }
    }
}
