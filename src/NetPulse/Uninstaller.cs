using NetPulse.Core;
using NetPulse.Platform;
using static NetPulse.Core.Lang;

namespace NetPulse;

/// <summary>
/// "NetPulse.exe --uninstall [--quiet]", run from "Installed apps" in Windows Settings or from the settings page.
/// Holds the single-instance lock while it works, so NetPulse cannot start (and lock its files) meanwhile.
/// </summary>
internal static class Uninstaller
{
    public const string QuietArgument = "--quiet";

    public static int Run(bool quiet)
    {
        var deleteData = false;
        if (!quiet)
        {
            var deleteDataBox = new TaskDialogVerificationCheckBox(T("同时删除设置和统计（数据文件夹）", "Also delete settings and statistics (the data folder)"));
            var uninstall = new TaskDialogButton(T("卸载", "Uninstall"));
            var page = new TaskDialogPage
            {
                Caption = AppPaths.AppName,
                Heading = T("卸载 NetPulse？", "Uninstall NetPulse?"),
                Text = T($"将删除 {UserInstall.InstallDirectory}、开始菜单快捷方式和开机启动项。", $"Removes {UserInstall.InstallDirectory}, the Start menu shortcut and the startup entry.") +
                    (ElevatedAutostart.IsEnabled() ? T("\n“以管理员身份启动”的计划任务和 Program Files 中的副本也会删除，需要确认一次 Windows 的权限提示。", "\nThe “start as administrator” task and its copy in Program Files are removed too; Windows asks for permission once.") : string.Empty) +
                    T($"\n\n数据文件夹：{AppPaths.DataDirectory}", $"\n\nData folder: {AppPaths.DataDirectory}"),
                Icon = TaskDialogIcon.Warning,
                Buttons = { uninstall, TaskDialogButton.Cancel },
                DefaultButton = TaskDialogButton.Cancel,
                Verification = deleteDataBox,
            };
            if (TaskDialog.ShowDialog(page) != uninstall)
            {
                return 1;
            }

            deleteData = deleteDataBox.Checked;
        }

        var problems = new List<string>();
        if (ElevatedAutostart.IsEnabled() && ElevatedAutostart.RequestRemove() is { } error)
        {
            problems.Add(T("“以管理员身份启动”没有删除：", "“Start as administrator” was not removed: ") + error);
        }

        try
        {
            UserInstall.Unregister();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ErrorLog.Write(ex, "Uninstall");
            problems.Add(ex.Message);
        }

        if (deleteData)
        {
            try
            {
                Directory.Delete(AppPaths.DataDirectory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add(T("数据文件夹没有完全删除：", "The data folder was not fully deleted: ") + ex.Message);
            }
        }

        if (!quiet)
        {
            var done = T("NetPulse 已卸载。", "NetPulse has been uninstalled.") +
                (deleteData || !Directory.Exists(AppPaths.DataDirectory) ? string.Empty : T($"\n设置和统计保留在 {AppPaths.DataDirectory}。", $"\nSettings and statistics were kept in {AppPaths.DataDirectory}."));
            MessageBox.Show(
                problems.Count == 0 ? done : done + "\n\n" + string.Join("\n", problems),
                AppPaths.AppName,
                MessageBoxButtons.OK,
                problems.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        // Last: when this is the installed copy, its folder is deleted shortly after this process exits.
        try
        {
            UserInstall.DeleteFiles();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ErrorLog.Write(ex, "Uninstall files");
            return 2;
        }

        return problems.Count == 0 ? 0 : 2;
    }

    /// <summary>Shown when NetPulse is running and does not hand over within a few seconds.</summary>
    public static void ShowStillRunning() =>
        MessageBox.Show(
            T("NetPulse 正在运行。请在托盘图标的右键菜单中选择“退出”，然后再卸载。", "NetPulse is running. Choose “Exit” in its tray icon menu, then uninstall again."),
            AppPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
}
