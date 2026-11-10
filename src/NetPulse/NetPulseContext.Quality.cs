using NetPulse.Core;
using NetPulse.Platform;
using static NetPulse.Core.Lang;

namespace NetPulse;

/// <summary>Connection quality: the probe timer, outage alerts and the tray tooltip line.</summary>
internal sealed partial class NetPulseContext
{
    private readonly JsonFileStore<QualityDocument> _qualityStore = new(AppPaths.QualityFile, () => new QualityDocument());
    private readonly System.Windows.Forms.Timer _qualityTimer = new();
    private QualityLedger _quality = null!;
    private QualityMonitor _qualityMonitor = null!;
    private QualityProbeService _qualityProbe = null!;

    private void InitQuality()
    {
        _quality = new QualityLedger(_qualityStore.Load());
        _qualityMonitor = new QualityMonitor(_quality);
        _qualityProbe = new QualityProbeService(_networkNames);
        _qualityTimer.Tick += async (_, _) => await ProbeQualityAsync();
    }

    /// <summary>Starts, retimes or stops the probe to match the settings.</summary>
    private void ApplyQualitySettings()
    {
        var settings = _settings.Quality;
        if (!settings.Enabled || _exiting)
        {
            if (_qualityTimer.Enabled)
            {
                _qualityTimer.Stop();
                StopQuality();
            }

            return;
        }

        var interval = settings.IntervalSeconds * 1000;
        if (!_qualityTimer.Enabled || _qualityTimer.Interval != interval)
        {
            _qualityTimer.Interval = interval;
            _qualityTimer.Start();
            _ = ProbeQualityAsync(); // first round now, so the page fills right away
        }
    }

    /// <summary>The page's "turn on" button.</summary>
    private void EnableQuality()
    {
        _settings.Quality.Enabled = true;
        ApplySettings(save: true);
        _settingsPage?.Reload();
    }

    /// <summary>" Connection: 99.8% available, 2 outages." for the weekly summary, or "" without monitored time.</summary>
    private string WeeklyQualityText(DateOnly from, DateOnly to)
    {
        var week = _quality.Summarize(from, to);
        return week.Availability is { } availability
            ? T($"\n连接可用率 {availability:P1}，断线 {week.Outages} 次。", $"\nConnection {availability:P1} available, {week.Outages} outages.")
            : string.Empty;
    }

    /// <summary>Ends an open outage at the last round (turned off, exiting) and forgets the live values.</summary>
    private void StopQuality()
    {
        _qualityMonitor.Stop();
        _qualityProbe.Reset();
    }

    private async Task ProbeQualityAsync()
    {
        var settings = _settings.Quality;
        if (_exiting || !settings.Enabled || _qualityProbe.IsBusy)
        {
            return;
        }

        try
        {
            var started = _clock.Elapsed;
            var wall = DateTime.Now;
            var probe = await _qualityProbe.ProbeAsync(settings, _speedTestCancel.Token);
            if (_exiting || !_settings.Quality.Enabled)
            {
                return; // turned off while the pings were out: this round must not reopen the monitor
            }

            var update = _qualityMonitor.Feed(probe, started, wall, settings.Interval, settings.HighLatencyMs);
            if (update.OutageEnded is { } outage && settings.ShouldAlert(outage))
            {
                var (title, cause) = outage.Kind switch
                {
                    OutageKind.Local => (T("刚才断网了（本地问题）", "Connection was down (local problem)"),
                        T("网关（路由器）没有回应，可能是 Wi-Fi 信号、网线或路由器的问题。", " The gateway (router) did not answer: Wi-Fi signal, cable or router.")),
                    OutageKind.External => (T("刚才断网了（外网问题）", "Connection was down (internet problem)"),
                        T("网关有回应但外网没有，问题通常在运营商或上游。", " The gateway answered but the internet did not: usually the ISP or upstream.")),
                    _ => (T("刚才 DNS 不可用", "DNS was not working"),
                        T("网络是通的，但 DNS 服务器没有回应，这段时间网页可能打不开。", " The network was up but the DNS server did not answer, so web pages may not have opened.")),
                };
                ShowBalloon(
                    10_000,
                    title,
                    T($"{outage.NetworkLabel}：{outage.Start:HH:mm:ss} 起，持续 {FormatDuration(outage.Duration)}。", $"{outage.NetworkLabel}: from {outage.Start:HH:mm:ss}, lasted {FormatDuration(outage.Duration)}.") + cause,
                    ToolTipIcon.Warning);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "QualityProbe"); // nobody awaits the timer's task, so it must not escape
        }
    }

    /// <summary>"网关 3 ms · 外网 21 ms" for the tray tooltip, or null when monitoring is off.</summary>
    private string? QualityTrayLine()
    {
        if (!_settings.Quality.Enabled || _qualityProbe.Last is not { } last)
        {
            return null;
        }

        if (last.Network is null)
        {
            return T("未连接", "Not connected");
        }

        var line = T("网关 ", "Gateway ") + FormatLatency(last.GatewayMs);
        if (last.ExternalProbed)
        {
            line += T(" · 外网 ", " · Internet ") + FormatLatency(last.ExternalMs);
        }

        return line;
    }

    internal static string FormatLatency(double? ms) => ms is { } v ? $"{v:0} ms" : T("无回应", "no reply");

    internal static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? T($"{(int)duration.TotalHours} 小时 {duration.Minutes} 分", $"{(int)duration.TotalHours} h {duration.Minutes} min")
        : duration.TotalMinutes >= 1 ? T($"{duration.Minutes} 分 {duration.Seconds} 秒", $"{duration.Minutes} min {duration.Seconds} s")
        : T($"{Math.Max(0, (int)duration.TotalSeconds)} 秒", $"{Math.Max(0, (int)duration.TotalSeconds)} s");

    private void SaveQuality(DateOnly keepFrom)
    {
        _quality.Prune(keepFrom, DateTime.Now);
        if (_quality.IsDirty && TrySave(_qualityStore, _quality.ToDocument(), "SaveQuality"))
        {
            _quality.MarkSaved();
        }
    }
}
