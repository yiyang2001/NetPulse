using NetPulse.Core;
using NetPulse.Platform;
using static NetPulse.Core.Lang;

namespace NetPulse;

/// <summary>Saving usage and settings, backup restore, and install/uninstall hand-over.</summary>
internal sealed partial class NetPulseContext
{
    private void ExportBackup(string path)
    {
        SaveUsage();
        SaveSettings();
        BackupDocument.Write(path, new BackupDocument
        {
            AppVersion = ElevatedAutostart.CurrentVersion,
            Settings = _settings,
            Usage = new UsageDocument { Days = [.. _monitor.Ledger.ToList()] },
            NetworkUsage = _monitor.Networks.ToDocument(),
            ProcessUsage = _processTraffic.History.ToDocument(),
            Quality = _quality.ToDocument(),
        });
    }

    /// <summary>Replaces the files on disk with the backup's and restarts; nothing is kept in memory, so nothing overwrites them on exit.</summary>
    private void ImportBackup(string path)
    {
        var backup = BackupDocument.Read(path);
        SettingsStore.Save(backup.Settings);
        _usageStore.Save(backup.Usage);
        _networkUsageStore.Save(backup.NetworkUsage);
        _processUsageStore.Save(backup.ProcessUsage);
        _qualityStore.Save(backup.Quality); // a format 1 backup has none: the restored PC starts without quality history

        _importing = true;
        try
        {
            // A plain child keeps this process's rights, so an elevated NetPulse restarts elevated without a prompt.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? Application.ExecutablePath,
                Arguments = $"{Program.RestartedArgument} {Program.OpenArgumentPrefix}{MainTab.Settings}{AppPaths.ProfileArgument}",
            })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _importing = false;
            ErrorLog.Write(ex, "ImportBackup restart");
            throw new IOException(T("已恢复数据，但无法自动重启，请手动重启 NetPulse。", "The data was restored, but NetPulse could not restart by itself. Please start it again."), ex);
        }

        Exit();
    }

    /// <summary>
    /// Copies this build into the per-user install folder and hands over to that copy. Throws (and keeps
    /// running) when the copy fails; the new process waits for this one's instance lock.
    /// </summary>
    private void InstallAndRestart()
    {
        SaveUsage();
        SaveSettings();
        UserInstall.Install();
        UserInstall.StartInstalledCopy($"{Program.RestartedArgument} {Program.OpenArgumentPrefix}{MainTab.Settings}");
        Exit();
    }

    /// <summary>Starts the uninstaller, which waits for this instance to exit before it removes anything.</summary>
    private void UninstallAndExit()
    {
        SaveUsage();
        SaveSettings();
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? Application.ExecutablePath,
            Arguments = $"{InstallLayout.UninstallArgument} {Program.RestartedArgument}{AppPaths.ProfileArgument}",
            WorkingDirectory = Path.GetTempPath(), // not the install folder, which is about to be deleted
        })?.Dispose();
        Exit();
    }

    private void SaveWidgetPosition()
    {
        _settings.WidgetX = _widget.Left;
        _settings.WidgetY = _widget.Top;
        _settings.Placement = _widget.CapturePlacement();
        SaveSettings();
    }

    private void SaveSettings()
    {
        if (_importing)
        {
            return;
        }

        TrySave(SettingsStore, _settings, "SaveSettings");
    }

    /// <summary>Saves the daily totals and the per-program history, whichever changed.</summary>
    private void SaveUsage()
    {
        if (_importing)
        {
            return;
        }

        _lastSave = DateTime.UtcNow;
        var keepFrom = DateOnly.FromDateTime(DateTime.Now).AddDays(-_settings.UsageRetentionDays);
        var ledger = _monitor.Ledger;
        ledger.Prune(keepFrom);
        var networks = _monitor.Networks;
        networks.Prune(keepFrom);
        if (networks.IsDirty && TrySave(_networkUsageStore, networks.ToDocument(), "SaveNetworkUsage"))
        {
            networks.MarkSaved();
        }

        if (ledger.IsDirty && TrySave(_usageStore, new UsageDocument { Days = [.. ledger.ToList()] }, "SaveUsage"))
        {
            ledger.MarkSaved();
        }

        _processTraffic.RecordHistory();
        var history = _processTraffic.History;
        history.Prune(keepFrom);
        if (history.IsDirty && TrySave(_processUsageStore, history.ToDocument(), "SaveProcessUsage"))
        {
            history.MarkSaved();
        }

        SaveQuality(keepFrom);
    }

    /// <summary>Writes one document; a full disk or a locked file is logged and retried on the next save.</summary>
    private static bool TrySave<T>(JsonFileStore<T> store, T document, string context) where T : class
    {
        try
        {
            store.Save(document);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex, context);
            return false;
        }
    }
}
