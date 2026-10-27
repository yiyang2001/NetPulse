namespace NetPulse;

internal enum MainTab
{
    Usage,
    Processes,
    Wifi,
    SpeedTest,
    Tools,
    Settings,
}

/// <summary>Pages that do work only while visible (timers, traces) get told when they are shown and hidden.</summary>
internal interface IActivatablePage
{
    void OnActivated();

    void OnDeactivated();
}

/// <summary>Tabbed main window. Closing only hides it; the app keeps running in the tray.</summary>
internal sealed class MainWindow : Form
{
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Padding = new Point(12, 4) };
    private readonly Dictionary<MainTab, TabPage> _pages = [];
    private IActivatablePage? _active;

    public MainWindow(IReadOnlyList<(MainTab Tab, string Title, Control Page)> pages, Icon icon)
    {
        Text = "NetPulse";
        Icon = icon;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(820, 640);
        MinimumSize = new Size(680, 520);

        foreach (var (tab, title, page) in pages)
        {
            page.Dock = DockStyle.Fill;
            var tabPage = new TabPage(title) { Tag = page };
            tabPage.Controls.Add(page);
            _tabs.TabPages.Add(tabPage);
            _pages[tab] = tabPage;
        }

        Controls.Add(_tabs);
        _tabs.SelectedIndexChanged += (_, _) => UpdateActivePage();
    }

    /// <summary>The window was closed from outside (taskkill, Task Manager, shutdown): the app should quit.</summary>
    public event EventHandler? ExitRequested;

    public void ShowTab(MainTab tab)
    {
        if (_pages.TryGetValue(tab, out var page))
        {
            _tabs.SelectedTab = page;
        }

        if (!Visible)
        {
            Show();
        }

        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Activate();
        UpdateActivePage();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateActivePage();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateActivePage(); // minimizing pauses the visible page
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        else if (e.CloseReason is CloseReason.TaskManagerClosing or CloseReason.WindowsShutDown)
        {
            e.Cancel = true;
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _active?.OnDeactivated();
            _active = null;
        }

        base.Dispose(disposing);
    }

    private void UpdateActivePage()
    {
        var shown = Visible && WindowState != FormWindowState.Minimized
            ? _tabs.SelectedTab?.Tag as IActivatablePage
            : null;
        if (ReferenceEquals(shown, _active))
        {
            return;
        }

        _active?.OnDeactivated();
        _active = shown;
        _active?.OnActivated();
    }
}
