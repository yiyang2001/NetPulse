using System.Diagnostics;

namespace NetPulse;

/// <summary>ListView that does not flicker when its rows are rebuilt every second.</summary>
internal sealed class FlickerFreeListView : ListView
{
    public FlickerFreeListView()
    {
        DoubleBuffered = true;
    }
}

internal static class Ui
{
    public static readonly Font MonoFont = new("Consolas", 9.5f);

    /// <summary>Read-only, monospaced, scrolling text output for the tool pages.</summary>
    public static TextBox OutputBox() => new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = MonoFont,
        BackColor = SystemColors.Window,
    };

    public static void AppendLine(this TextBox box, string line)
    {
        box.AppendText(line + Environment.NewLine);
    }

    public static Label Note(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Top,
        AutoSize = true,
        MaximumSize = new Size(760, 0),
        ForeColor = SystemColors.GrayText,
        Padding = new Padding(0, 4, 0, 4),
    };

    public static FlowLayoutPanel Toolbar(params Control[] controls)
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 0, 0, 6),
        };
        foreach (var control in controls)
        {
            control.Margin = new Padding(0, 0, 8, 0);
            if (control is Label label)
            {
                label.AutoSize = true;
                label.Padding = new Padding(0, 6, 0, 0);
            }
        }

        bar.Controls.AddRange(controls);
        return bar;
    }

    /// <summary>
    /// In dark mode, gives every combo and spin box under <paramref name="root"/> (also ones added later)
    /// the dark visual style, which WinForms does not apply by itself.
    /// </summary>
    public static void ApplyDarkControlStyles(Control root)
    {
        if (!Application.IsDarkModeEnabled)
        {
            return;
        }

        Style(root);
        return;

        static void Style(Control control)
        {
            if (control is LinkLabel link)
            {
                // The default dark blue is unreadable on a dark background.
                link.LinkColor = link.VisitedLinkColor = Color.FromArgb(96, 165, 250);
                link.ActiveLinkColor = Color.FromArgb(147, 197, 253);
            }

            if (control is ComboBox or NumericUpDown)
            {
                // WinForms applies its own theme while creating the handle; ours has to come after it.
                void Apply() => control.BeginInvoke(() =>
                {
                    NativeMethods.UseDarkControlTheme(control.Handle);
                    control.Invalidate();
                });
                if (control.IsHandleCreated)
                {
                    Apply();
                }

                control.HandleCreated += (_, _) => Apply();
            }

            control.ControlAdded += (_, e) => Style(e.Control!);
            foreach (Control child in control.Controls)
            {
                Style(child);
            }
        }
    }

    public static void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            ErrorLog.Write(ex, "OpenUri " + uri);
        }
    }
}
