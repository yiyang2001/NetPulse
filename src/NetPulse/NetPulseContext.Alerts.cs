using NetPulse.Core;
using NetPulse.Platform;
using static NetPulse.Core.Lang;

namespace NetPulse;

/// <summary>Balloon alerts: data plans, sustained speed, program limits and the weekly summary.</summary>
internal sealed partial class NetPulseContext
{
    private void CheckSpeedAlert(SpeedSample sample)
    {
        var alert = _settings.SpeedAlert;
        if (!alert.Enabled)
        {
            _speedDetector = null;
            _speedDetectorKey = null;
            return;
        }

        var key = (alert.Direction, alert.ThresholdBytesPerSecond, alert.Minutes);
        if (_speedDetector is null || _speedDetectorKey != key)
        {
            _speedDetector = new SustainedSpeedDetector(alert.ThresholdBytesPerSecond, TimeSpan.FromMinutes(alert.Minutes));
            _speedDetectorKey = key;
        }

        var upload = alert.Direction == SpeedAlertDirection.Upload;
        if (_speedDetector.Feed(_clock.Elapsed, upload ? sample.UploadBytesPerSecond : sample.DownloadBytesPerSecond))
        {
            var limit = ByteFormatter.FormatSpeed(alert.ThresholdBytesPerSecond, _settings.Unit);
            var now = ByteFormatter.FormatSpeed(upload ? sample.UploadBytesPerSecond : sample.DownloadBytesPerSecond, _settings.Unit);
            ShowBalloon(
                10_000,
                upload ? T("上传速度持续偏高", "Upload speed stays high") : T("下载速度持续偏高", "Download speed stays high"),
                T($"已连续 {alert.Minutes} 分钟超过 {limit}，当前 {now}。可在“进程流量”中查看是哪个程序。", $"Above {limit} for {alert.Minutes} minutes, now {now}. Check the Programs tab to see which program."),
                ToolTipIcon.Warning);
        }
    }

    private void ShowBalloon(int timeout, string title, string text, ToolTipIcon icon, Uri? link = null)
    {
        _balloonLink = link;
        _tray.ShowBalloonTip(timeout, title, text, icon);
    }

    private void CheckProgramAlert()
    {
        var alert = _settings.ProgramAlert;
        if (!alert.Enabled || !_processTraffic.IsRunning)
        {
            return;
        }

        var over = _programLimits.NewlyOver(_processTraffic.History, DateOnly.FromDateTime(DateTime.Now), alert.LimitBytes);
        if (over.Count == 0)
        {
            return;
        }

        var names = string.Join(", ", over.Take(3).Select(a => $"{a.Name} ({ByteFormatter.FormatSize(a.Total)})"))
            + (over.Count > 3 ? T($" 等 {over.Count} 个程序", $" and {over.Count - 3} more") : string.Empty);
        ShowBalloon(
            10_000,
            T("程序今日流量超出设定", "Program over its daily limit"),
            T($"超过 {ByteFormatter.FormatSize(alert.LimitBytes)}：{names}", $"Over {ByteFormatter.FormatSize(alert.LimitBytes)}: {names}"),
            ToolTipIcon.Warning);
    }

    private void CheckWeeklySummary()
    {
        if (!_settings.WeeklySummary)
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        if (WeeklySummary.DueOn(_monitor.Ledger, today, _settings.WeeklySummaryShownFor) is not { } summary)
        {
            return;
        }

        _settings.WeeklySummaryShownFor = WeeklySummary.MondayOf(today);
        SaveSettings();
        var change = summary.PreviousWeek.Total > 0
            ? T($"，比前一周{((double)summary.Week.Total / summary.PreviousWeek.Total) - 1:+0%;-0%;±0%}", $", {((double)summary.Week.Total / summary.PreviousWeek.Total) - 1:+0%;-0%;±0%} vs the week before")
            : string.Empty;
        ShowBalloon(
            10_000,
            T($"上周流量（{summary.From:M/d} – {summary.To:M/d}）", $"Last week ({summary.From:M/d} – {summary.To:M/d})"),
            T($"↓ {ByteFormatter.FormatSize(summary.Week.Downloaded)}  ↑ {ByteFormatter.FormatSize(summary.Week.Uploaded)}，合计 {ByteFormatter.FormatSize(summary.Week.Total)}{change}", $"↓ {ByteFormatter.FormatSize(summary.Week.Downloaded)}  ↑ {ByteFormatter.FormatSize(summary.Week.Uploaded)}, {ByteFormatter.FormatSize(summary.Week.Total)} in total{change}"),
            ToolTipIcon.Info);
    }

    private void CheckDataPlan()
    {
        CheckPlan(_settings.Plan, _monitor.Ledger, networkLabel: null);
        foreach (var (key, plan) in _settings.NetworkPlans)
        {
            if (_monitor.Networks.Get(key) is { } ledger)
            {
                var label = _monitor.Networks.Networks.FirstOrDefault(n => n.Key == key).Label;
                CheckPlan(plan, ledger, label);
            }
        }
    }

    /// <summary>Shows a balloon when <paramref name="plan"/> reaches a new alert level; <paramref name="networkLabel"/> names a per-network plan.</summary>
    private void CheckPlan(DataPlanSettings plan, UsageLedger ledger, string? networkLabel)
    {
        if (!plan.Enabled)
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var (cycle, status) = DataPlanCalculator.Evaluate(ledger, plan, today);
        var alert = plan.TakePendingAlert(cycle, status);
        if (alert == PlanAlertLevel.None)
        {
            return;
        }

        SaveSettings();
        var used = $"{ByteFormatter.FormatSize(status.UsedBytes)} / {ByteFormatter.FormatSize(status.LimitBytes)}";
        var name = networkLabel is null ? string.Empty : $"{networkLabel}: ";
        if (alert == PlanAlertLevel.Exceeded)
        {
            ShowBalloon(10_000, name + T("流量套餐已用完", "Data plan used up"), T($"本周期已使用 {used}。", $"Used {used} this cycle."), ToolTipIcon.Warning);
        }
        else
        {
            ShowBalloon(10_000, name + T("流量套餐即将用完", "Data plan almost used up"), T($"本周期已使用 {used}（{status.Fraction:P0}）。", $"Used {used} this cycle ({status.Fraction:P0})."), ToolTipIcon.Info);
        }
    }
}
