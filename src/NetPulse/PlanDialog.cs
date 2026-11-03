using NetPulse.Core;
using static NetPulse.Core.Lang;

namespace NetPulse;

/// <summary>Edits the data plan of a single network, e.g. a phone hotspot with its own allowance.</summary>
internal static class PlanDialog
{
    /// <summary>Shows the dialog; on OK copies the values into <paramref name="plan"/> and returns true.</summary>
    public static bool Edit(IWin32Window owner, string networkLabel, DataPlanSettings plan)
    {
        using var form = new Form
        {
            Text = T($"流量套餐 — {networkLabel}", $"Data plan — {networkLabel}"),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Dpi,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12),
        };

        var enabled = new CheckBox { Text = T("为此网络启用单独的流量套餐", "Separate data plan for this network"), AutoSize = true, Checked = plan.Enabled };
        var limit = Number(0.1m, 1_000_000, 1, 1, (decimal)plan.LimitGb);
        var day = Number(1, 31, 1, 0, plan.CycleStartDay);
        var warn = Number(1, 100, 5, 0, plan.WarnPercent);
        var upload = new CheckBox { Text = T("上传流量也计入套餐", "Count uploads towards the plan"), AutoSize = true, Checked = plan.CountUpload };
        var ok = new Button { Text = T("确定", "OK"), DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = T("取消", "Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.AddRange([cancel, ok]);

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 2 };
        layout.Controls.Add(enabled, 0, 0);
        layout.SetColumnSpan(enabled, 2);
        AddRow(layout, 1, T("每周期流量（GB）", "Data per cycle (GB)"), limit);
        AddRow(layout, 2, T("每月重置日", "Resets on day"), day);
        AddRow(layout, 3, T("提醒阈值（%）", "Warn at (%)"), warn);
        layout.Controls.Add(upload, 0, 4);
        layout.SetColumnSpan(upload, 2);
        layout.Controls.Add(buttons, 0, 5);
        layout.SetColumnSpan(buttons, 2);
        form.Controls.Add(layout);
        Ui.ApplyDarkControlStyles(form);

        if (form.ShowDialog(owner) != DialogResult.OK)
        {
            return false;
        }

        plan.Enabled = enabled.Checked;
        plan.LimitGb = (double)limit.Value;
        plan.CycleStartDay = (int)day.Value;
        plan.WarnPercent = (int)warn.Value;
        plan.CountUpload = upload.Checked;
        plan.Normalize();
        return true;
    }

    private static void AddRow(TableLayoutPanel layout, int row, string label, Control control)
    {
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 6) }, 0, row);
        layout.Controls.Add(control, 1, row);
    }

    private static NumericUpDown Number(decimal min, decimal max, decimal step, int decimals, decimal value) => new()
    {
        Minimum = min,
        Maximum = max,
        Increment = step,
        DecimalPlaces = decimals,
        Width = 100,
        Value = Math.Clamp(value, min, max),
    };
}
