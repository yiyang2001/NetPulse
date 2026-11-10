using NetPulse.Core;
using NetPulse.Platform;
using static NetPulse.Core.Lang;

namespace NetPulse;

/// <summary>Timed background jobs: the scheduled speed test and the opt-in update check.</summary>
internal sealed partial class NetPulseContext
{
    /// <summary>Starts the scheduled speed test when it is due; waits while a full-screen app runs or another test is going.</summary>
    private void CheckSpeedTestSchedule()
    {
        var schedule = _settings.SpeedTestSchedule;
        if (_exiting || _speedTests.IsBusy || !schedule.IsDue(DateTime.Now) || NativeMethods.IsFullscreenAppActive())
        {
            return;
        }

        schedule.LastRun = DateTime.Now; // also when the test fails: a missing connection is not retried every two seconds
        SaveSettings();
        _ = RunScheduledSpeedTestAsync();
    }

    private async Task RunScheduledSpeedTestAsync()
    {
        try
        {
            await _speedTests.RunAsync(null, _speedTestCancel.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "ScheduledSpeedTest"); // nobody awaits this task, so it must not escape
        }
    }

    /// <summary>Once a day while the user has the update check on; only tells, never downloads.</summary>
    private void CheckForUpdates()
    {
        var check = _settings.UpdateCheck;
        if (_exiting || !UpdateService.IsConfigured || _updates.IsBusy || !check.IsDue(DateTime.Now))
        {
            return;
        }

        check.LastCheck = DateTime.Now; // also on failure: offline PCs are not asked again every two seconds
        SaveSettings();
        _ = CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var release = await _updates.GetLatestAsync(_speedTestCancel.Token);
            if (release is null || !UpdateFeed.IsNewer(release, UpdateService.CurrentVersion) || _exiting)
            {
                return;
            }

            var version = release.Version.ToString();
            if (_settings.UpdateCheck.NotifiedVersion == version)
            {
                return; // announced once; the settings page still shows it
            }

            _settings.UpdateCheck.NotifiedVersion = version;
            SaveSettings();
            ShowBalloon(
                10_000,
                T($"NetPulse {version} 已发布", $"NetPulse {version} is available"),
                T($"当前是 {UpdateService.CurrentVersion}。点击这里打开下载页面。", $"You have {UpdateService.CurrentVersion}. Click here to open the download page."),
                ToolTipIcon.Info,
                release.Page);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Offline or GitHub unreachable: try again tomorrow, no need to bother the user.
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "UpdateCheck"); // nobody awaits this task, so it must not escape
        }
    }
}
