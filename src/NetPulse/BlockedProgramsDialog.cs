using System.Runtime.InteropServices;
using NetPulse.Core;
using NetPulse.Platform;
using static NetPulse.Core.Lang;

namespace NetPulse;

/// <summary>
/// Lists the programs NetPulse blocks through Windows Firewall, and adds or removes them. Anyone can look;
/// changing rules needs administrator rights.
/// </summary>
internal static class BlockedProgramsDialog
{
    public static void Show(IWin32Window owner, Func<bool> restartElevated)
    {
        using var form = new Form
        {
            Text = T("禁止联网的程序", "Blocked programs"),
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            ShowIcon = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Dpi,
            Size = new Size(780, 460),
            MinimumSize = new Size(560, 320),
            Padding = new Padding(12),
        };

        var list = new FlickerFreeListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        list.Columns.Add(T("程序", "Program"), 170);
        list.Columns.Add(T("阻止", "Blocks"), 130);
        list.Columns.Add(T("路径", "Path"), 420);

        var warning = new Label { AutoSize = true, Dock = DockStyle.Top, ForeColor = Color.FromArgb(234, 88, 12), Padding = new Padding(0, 0, 0, 6), MaximumSize = new Size(740, 0) };
        var add = new Button { Text = T("添加程序…", "Add a program…"), AutoSize = true };
        var allow = new Button { Text = T("允许联网", "Allow again"), AutoSize = true, Enabled = false };
        var elevate = new Button { Text = T("以管理员身份重启 NetPulse", "Restart NetPulse as administrator"), AutoSize = true, Visible = !Elevation.IsAdministrator };
        var close = new Button { Text = T("关闭", "Close"), DialogResult = DialogResult.Cancel, AutoSize = true };
        form.CancelButton = close;

        void Reload()
        {
            list.BeginUpdate();
            list.Items.Clear();
            try
            {
                foreach (var program in FirewallBlocker.List())
                {
                    var blocks = program is { Outbound: true, Inbound: true } ? T("出站和入站", "Outgoing and incoming")
                        : program.Outbound ? T("出站", "Outgoing") : T("入站", "Incoming");
                    if (!program.Enabled)
                    {
                        blocks += T("（规则已停用）", " (rule disabled)");
                    }

                    list.Items.Add(new ListViewItem([program.FileName, blocks, program.Path]) { Tag = program.Path });
                }

                var notes = new List<string>();
                if (!FirewallBlocker.IsFirewallOnForCurrentNetwork())
                {
                    notes.Add(T("当前网络的 Windows 防火墙已关闭，这些规则现在不起作用。", "Windows Firewall is off for the current network, so these rules have no effect right now."));
                }

                if (!Elevation.IsAdministrator)
                {
                    notes.Add(T("添加或删除规则需要以管理员身份运行 NetPulse。", "Adding or removing rules needs NetPulse to run as administrator."));
                }

                warning.Text = string.Join("\n", notes);
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidCastException or System.Reflection.TargetInvocationException)
            {
                ErrorLog.Write(ex, "FirewallBlocker.List");
                warning.Text = T("无法读取 Windows 防火墙规则：", "Could not read Windows Firewall rules: ") + ex.Message;
            }

            warning.Visible = warning.Text.Length > 0;
            list.EndUpdate();
            add.Enabled = Elevation.IsAdministrator;
            allow.Enabled = Elevation.IsAdministrator && list.SelectedItems.Count > 0;
        }

        list.SelectedIndexChanged += (_, _) => allow.Enabled = Elevation.IsAdministrator && list.SelectedItems.Count > 0;
        add.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = T("程序 (*.exe)|*.exe", "Programs (*.exe)|*.exe"), DereferenceLinks = true };
            if (dialog.ShowDialog(form) == DialogResult.OK && TryBlock(form, dialog.FileName))
            {
                Reload();
            }
        };
        allow.Click += (_, _) =>
        {
            foreach (ListViewItem item in list.SelectedItems)
            {
                if (!TryUnblock(form, (string)item.Tag!))
                {
                    break;
                }
            }

            Reload();
        };
        elevate.Click += (_, _) =>
        {
            form.Close();
            restartElevated();
        };

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
        buttons.Controls.AddRange([add, allow, elevate, close]);
        var note = Ui.Note(T(
            "通过 Windows 防火墙阻止这些程序的出站和入站连接，规则在防火墙中属于“NetPulse”组。已经建立的连接可能要等程序重新连接时才会断开。卸载 NetPulse 不会删除这些规则，需要先在这里允许联网。",
            "Windows Firewall blocks the outgoing and incoming connections of these programs; the rules are in the “NetPulse” group. Connections already open may last until the program reconnects. Uninstalling NetPulse keeps these rules, so allow the programs again here first."));

        form.Controls.Add(list);
        form.Controls.Add(warning);
        form.Controls.Add(note);
        form.Controls.Add(buttons);
        Ui.ApplyDarkControlStyles(form);
        Reload();
        form.ShowDialog(owner);
    }

    /// <summary>Checks the path, asks for confirmation and adds the rules; returns true when the program is now blocked.</summary>
    public static bool TryBlock(IWin32Window owner, string path)
    {
        var ownExe = Environment.ProcessPath ?? Application.ExecutablePath;
        switch (FirewallBlocks.Check(path, Environment.GetFolderPath(Environment.SpecialFolder.Windows), ownExe))
        {
            case BlockCheck.NotAnExecutable:
                MessageBox.Show(owner, T("只能阻止 .exe 程序。", "Only .exe programs can be blocked."), AppPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            case BlockCheck.Protected:
                MessageBox.Show(owner, T($"{Path.GetFileName(path)} 是 Windows 或 NetPulse 的核心程序，阻止它会导致网络或系统无法正常工作，所以不能在这里阻止。", $"{Path.GetFileName(path)} is a core part of Windows or NetPulse; blocking it would break networking or Windows, so it cannot be blocked here."), AppPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            case BlockCheck.WindowsComponent:
                if (MessageBox.Show(owner, T($"{path}\n\n这是 Windows 自带的程序。阻止它可能影响 Windows 更新、搜索或其他系统功能。确定要阻止吗？", $"{path}\n\nThis is part of Windows. Blocking it may affect Windows Update, search or other system features. Block it anyway?"), AppPaths.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    return false;
                }

                break;
            default:
                if (MessageBox.Show(owner, T($"禁止 {Path.GetFileName(path)} 联网？\n\n{path}\n\n可以随时在“禁止联网的程序”中恢复。", $"Block {Path.GetFileName(path)} from the network?\n\n{path}\n\nYou can allow it again any time under “Blocked programs”."), AppPaths.AppName, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                {
                    return false;
                }

                break;
        }

        return Change(owner, () => FirewallBlocker.Block(path));
    }

    private static bool TryUnblock(IWin32Window owner, string path) => Change(owner, () => FirewallBlocker.Unblock(path));

    private static bool Change(IWin32Window owner, Action change)
    {
        try
        {
            change();
            return true;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidCastException or System.Reflection.TargetInvocationException)
        {
            ErrorLog.Write(ex, "FirewallBlocker");
            var message = ex is UnauthorizedAccessException
                ? T("需要以管理员身份运行 NetPulse。", "NetPulse needs to run as administrator.")
                : ex.Message;
            MessageBox.Show(owner, T("无法修改防火墙规则：", "Could not change the firewall rules: ") + message, AppPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }
}
