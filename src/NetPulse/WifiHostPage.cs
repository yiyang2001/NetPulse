using static NetPulse.Core.Lang;

namespace NetPulse;

/// <summary>The Wi-Fi tab: the connection and saved networks, and the scan of nearby networks.</summary>
internal sealed class WifiHostPage : UserControl, IActivatablePage
{
    private readonly WifiPage _overview = new();
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };

    public WifiHostPage()
    {
        _overview.Dock = DockStyle.Fill;
        var scan = new WifiScanPanel { Dock = DockStyle.Fill };
        _tabs.TabPages.Add(new TabPage(T("连接与已保存的网络", "Connection and saved networks")) { Controls = { _overview } });
        _tabs.TabPages.Add(new TabPage(T("附近的网络", "Nearby networks")) { Controls = { scan } });
        Controls.Add(_tabs);
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            // The overview polls the connection and holds a revealed password: only while it is showing.
            if (_tabs.SelectedIndex == 0)
            {
                _overview.OnActivated();
            }
            else
            {
                _overview.OnDeactivated();
            }
        };
    }

    public void OnActivated()
    {
        if (_tabs.SelectedIndex == 0)
        {
            _overview.OnActivated();
        }
    }

    public void OnDeactivated() => _overview.OnDeactivated();
}
