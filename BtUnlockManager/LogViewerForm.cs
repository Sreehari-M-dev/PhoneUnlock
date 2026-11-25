using System.Diagnostics;

namespace BtUnlockManager;

/// <summary>
/// Tails service.log and credprovider.log without locking them: the service and
/// LogonUI keep writing while this window is open.
/// </summary>
internal sealed class LogViewerForm : Form
{
    private static readonly Color Slate700 = Color.FromArgb(51, 65, 85);
    private static readonly Color Slate500 = Color.FromArgb(100, 116, 139);
    private static readonly Color Accent = Color.FromArgb(37, 99, 235);

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private readonly CheckBox _autoRefresh = new() { Text = "Auto-refresh", Checked = true, AutoSize = true };
    private readonly TextBox _serviceLog = MakeLogBox();
    private readonly TextBox _credentialLog = MakeLogBox();

    public LogViewerForm()
    {
        Text = "Logs — Phone Unlock";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(940, 620);
        MinimumSize = new Size(700, 420);
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9.25F);
        Icon = SystemIcons.Shield;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(14),
            BackColor = Color.White
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.White
        };

        var refresh = new Button
        {
            Text = "Refresh",
            Width = 90,
            Height = 28,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Slate700,
            UseVisualStyleBackColor = false
        };
        refresh.FlatAppearance.BorderColor = Color.FromArgb(226, 232, 240);
        refresh.Click += (_, _) => LoadLogs();

        var open = new Button
        {
            Text = "Open log folder",
            Width = 130,
            Height = 28,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Slate700,
            UseVisualStyleBackColor = false
        };
        open.FlatAppearance.BorderColor = Color.FromArgb(226, 232, 240);
        open.Click += (_, _) => OpenFolder();

        _autoRefresh.ForeColor = Slate500;
        _autoRefresh.Margin = new Padding(12, 7, 0, 0);

        bar.Controls.Add(refresh);
        bar.Controls.Add(open);
        bar.Controls.Add(_autoRefresh);

        var tabs = new TabControl { Dock = DockStyle.Fill };

        var servicePage = new TabPage("service.log")
        {
            BackColor = Color.FromArgb(2, 6, 23),
            Padding = new Padding(1)
        };
        servicePage.Controls.Add(_serviceLog);

        var credentialPage = new TabPage("credprovider.log")
        {
            BackColor = Color.FromArgb(2, 6, 23),
            Padding = new Padding(1)
        };
        credentialPage.Controls.Add(_credentialLog);

        tabs.TabPages.Add(servicePage);
        tabs.TabPages.Add(credentialPage);

        root.Controls.Add(bar, 0, 0);
        root.Controls.Add(tabs, 0, 1);
        Controls.Add(root);

        Shown += (_, _) => LoadLogs();
        _timer.Tick += (_, _) =>
        {
            if (_autoRefresh.Checked)
            {
                LoadLogs();
            }
        };
        _timer.Start();
        FormClosed += (_, _) => _timer.Stop();
    }

    private static TextBox MakeLogBox() => new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        BackColor = Color.FromArgb(2, 6, 23),
        ForeColor = Color.FromArgb(203, 213, 225),
        Font = new Font("Consolas", 9F),
        BorderStyle = BorderStyle.None
    };

    private void LoadLogs()
    {
        SetText(_serviceLog, AppInfo.ServiceLogPath, "service.log does not exist yet.");
        SetText(_credentialLog, AppInfo.CredProviderLogPath, "credprovider.log does not exist yet.");
    }

    private static void SetText(TextBox box, string path, string missing)
    {
        string text;

        try
        {
            text = File.Exists(path) ? AppInfo.ReadAllTextShared(path) : missing;
        }
        catch (Exception ex)
        {
            text = $"Could not read {path}: {ex.Message}";
        }

        if (string.Equals(box.Text, text, StringComparison.Ordinal))
        {
            return;
        }

        // Do not fight the user: only auto-scroll when they are at the bottom.
        bool atBottom = box.SelectionStart >= Math.Max(0, box.TextLength - 1);

        box.Text = text;

        if (atBottom)
        {
            box.SelectionStart = box.TextLength;
            box.ScrollToCaret();
        }
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            Process.Start(new ProcessStartInfo(AppInfo.DataDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this, "Could not open the folder: " + ex.Message,
                "Phone Unlock", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
