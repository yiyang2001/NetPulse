using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using NetPulse.Core;
using static NetPulse.Core.Lang;

namespace NetPulse.Platform;

/// <summary>
/// "Start at sign-in as administrator", via a Task Scheduler logon task. Registering it needs admin
/// rights, so the normal app starts an elevated copy of itself with <see cref="InstallArgument"/>.
/// The task runs a copy under Program Files: a task that runs elevated must not point at an exe the
/// user (or anything running as the user) can replace, or it would hand out admin rights.
/// </summary>
internal static class ElevatedAutostart
{
    public const string InstallArgument = "--install-autostart";
    public const string RemoveArgument = "--remove-autostart";

    private const int ERROR_CANCELLED = 1223;

    private static string TaskName => "NetPulse" + AppPaths.ProfileSuffix;

    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NetPulse" + AppPaths.ProfileSuffix);

    public static string InstalledExe => Path.Combine(InstallDirectory, "NetPulse.exe");

    public static bool IsRunningInstalledCopy => string.Equals(
        Path.GetFullPath(Environment.ProcessPath ?? Application.ExecutablePath), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    public static bool IsEnabled() => RunSchtasks("/Query", "/TN", TaskName) == 0;

    public static string? InstalledVersion() =>
        File.Exists(InstalledExe) ? FileVersionInfo.GetVersionInfo(InstalledExe).ProductVersion?.Split('+')[0] : null;

    public static string CurrentVersion =>
        FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? Application.ExecutablePath).ProductVersion?.Split('+')[0] ?? "?";

    /// <summary>Installs (or updates) the copy and the task through a UAC prompt. Returns null on success, else why not.</summary>
    public static string? RequestInstall() => RunElevated(InstallArgument);

    /// <summary>Removes the task and the installed copy through a UAC prompt. Returns null on success, else why not.</summary>
    public static string? RequestRemove() => RunElevated(RemoveArgument);

    /// <summary>Entry point of the elevated helper process; returns its exit code.</summary>
    public static int RunHelper(string argument)
    {
        try
        {
            if (argument == InstallArgument)
            {
                CopyApplication();
                RegisterTask();
            }
            else
            {
                RunSchtasks("/Delete", "/TN", TaskName, "/F");
                TryDeleteInstallDirectory();
            }

            return 0;
        }
        catch (IOException ex)
        {
            ErrorLog.Write(ex, "ElevatedAutostart " + argument);
            return 2; // usually: the installed copy is running and its files are locked
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            ErrorLog.Write(ex, "ElevatedAutostart " + argument);
            return 3;
        }
    }

    private static string? RunElevated(string argument)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? Application.ExecutablePath,
                Arguments = argument + AppPaths.ProfileArgument,
                UseShellExecute = true,
                Verb = "runas",
            });
            if (process is null)
            {
                return T("无法启动设置程序。", "Could not start the setup helper.");
            }

            if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
            {
                return T("设置程序没有响应。", "The setup helper did not respond.");
            }

            return process.ExitCode switch
            {
                0 => null,
                2 => T("文件被占用：如果已安装的 NetPulse 正在运行，请先退出它再试。", "Files are in use: if the installed NetPulse is running, exit it and try again."),
                _ => T("设置失败，详情见数据文件夹中的 error.log。", "Setup failed; see error.log in the data folder for details."),
            };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            return T("已取消。需要在 Windows 的权限提示中选择“是”。", "Cancelled. Choose “Yes” in the Windows permission prompt.");
        }
    }

    /// <summary>Copies the running build into Program Files (a single-file publish is just the exe).</summary>
    private static void CopyApplication()
    {
        if (!IsRunningInstalledCopy)
        {
            AppFiles.CopyRunningBuild(InstallDirectory);
        }
    }

    private static void RegisterTask()
    {
        var xmlPath = Path.Combine(Path.GetTempPath(), $"NetPulse-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(xmlPath, BuildTaskXml(), Encoding.Unicode);
            var exitCode = RunSchtasks("/Create", "/TN", TaskName, "/XML", xmlPath, "/F");
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"schtasks /Create exited with {exitCode}.");
            }
        }
        finally
        {
            File.Delete(xmlPath);
        }
    }

    private static string BuildTaskXml()
    {
        // The helper runs as the signed-in user (elevated), so this is the account the task starts for.
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("No user SID.");
        return LogonTask.Build(
            sid,
            InstalledExe,
            AppPaths.ProfileArgument.Trim(),
            "Starts NetPulse at sign-in with administrator rights so that per-program traffic can be recorded.");
    }

    private static void TryDeleteInstallDirectory()
    {
        if (IsRunningInstalledCopy || !Directory.Exists(InstallDirectory))
        {
            return; // a running exe cannot delete itself; the folder is harmless without the task
        }

        try
        {
            Directory.Delete(InstallDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex, "Remove install directory");
        }
    }

    private static int RunSchtasks(params string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("schtasks did not start.");
        // Drain both pipes so a chatty schtasks cannot block on a full buffer.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(stdout, stderr);
        return process.ExitCode;
    }
}
