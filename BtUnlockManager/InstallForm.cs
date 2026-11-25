namespace BtUnlockManager;

/// <summary>
/// Runs the embedded installer / uninstaller on a background thread and shows
/// each step live. The heavy lifting is in <see cref="SetupEngine"/>.
/// </summary>
internal sealed class InstallForm : Form
{
    private static readonly Color Slate700 = Color.FromArgb(51, 65, 85);
    private static readonly Color Slate500 = Color.FromArgb(100, 116, 139);
    private static readonly Color Green = Color.FromArgb(22, 163, 74);
    private static readonly Color Red = Color.FromArgb(220, 38, 38);

    private readonly bool _uninstall;
    private readonly bool _deleteData;
    private readonly bool _showNextSteps;

    private readonly TextBox _log = new();
    private readonly Label _status = new();
    private readonly Button _close = new();
    private readonly ProgressBar _progress = new();

    private bool _running = true;

    public bool Succeeded { get; private set; }

    public InstallForm(bool uninstall, bool deleteData, bool showNextSteps)
    {
        _uninstall = uninstall;
        _deleteData = deleteData;
        _showNextSteps = showNextSteps;

        Text = uninstall ? "Uninstall Phone Unlock" : "Install / Repair Phone Unlock";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(780, 540);
        MinimumSize = new Size(620, 400);
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9.25F);
        Icon = SystemIcons.Shield;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(14),
            BackColor = Color.White
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        _status.Text = uninstall
            ? "Removing Phone Unlock…"
            : "Installing Phone Unlock…";

        _status.Dock = DockStyle.Fill;
        _status.Font = new Font("Segoe UI Semibold", 10F);
        _status.ForeColor = Slate700;
        _status.TextAlign = ContentAlignment.MiddleLeft;

        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Both;
        _log.WordWrap = false;
        _log.BackColor = Color.FromArgb(2, 6, 23);
        _log.ForeColor = Color.FromArgb(203, 213, 225);
        _log.Font = new Font("Consolas", 9F);
        _log.BorderStyle = BorderStyle.FixedSingle;

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.White
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));

        _progress.Dock = DockStyle.Fill;
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.MarqueeAnimationSpeed = 30;
        _progress.Margin = new Padding(0, 8, 12, 8);

        _close.Text = "Working…";
        _close.Dock = DockStyle.Right;
        _close.Width = 110;
        _close.Enabled = false;
        _close.FlatStyle = FlatStyle.Flat;
        _close.BackColor = Color.White;
        _close.ForeColor = Slate700;
        _close.UseVisualStyleBackColor = false;
        _close.FlatAppearance.BorderColor = Color.FromArgb(226, 232, 240);
        _close.Click += (_, _) => Close();

        footer.Controls.Add(_progress, 0, 0);
        footer.Controls.Add(_close, 1, 0);

        root.Controls.Add(_status, 0, 0);
        root.Controls.Add(_log, 0, 1);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);

        Shown += async (_, _) => await RunAsync();
        FormClosing += OnFormClosing;
    }

    private async Task RunAsync()
    {
        var progress = new Progress<string>(Append);

        try
        {
            string summary = _uninstall
                ? await Task.Run(() => SetupEngine.Uninstall(progress, _deleteData))
                : await Task.Run(() => SetupEngine.Install(progress));

            Succeeded = true;
            _status.Text = summary;
            _status.ForeColor = Green;

            if (!_uninstall && _showNextSteps)
            {
                MessageBox.Show(
                    this,
                    "Installation complete.\n\n" +
                    "Next steps:\n" +
                    "  1. Click “Set credentials…” and store your Windows account password.\n" +
                    "  2. Click “Pair phone…” and scan the QR code with the Android app.\n" +
                    "  3. Lock the PC with Win+L, then unlock it from the phone.",
                    "Phone Unlock",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            _status.Text = "Failed: " + ex.Message;
            _status.ForeColor = Red;
            Append(string.Empty);
            Append("ERROR: " + ex);
        }
        finally
        {
            _running = false;
            _progress.Style = ProgressBarStyle.Blocks;
            _progress.MarqueeAnimationSpeed = 0;
            _close.Text = "Close";
            _close.Enabled = true;
        }
    }

    private void Append(string line)
    {
        if (IsDisposed)
        {
            return;
        }

        _log.AppendText(line + Environment.NewLine);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_running)
        {
            return;
        }

        DialogResult answer = MessageBox.Show(
            this,
            "The installer is still running. Closing now can leave the service half-installed. " +
            "Close anyway?",
            "Phone Unlock",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes)
        {
            e.Cancel = true;
        }
    }
}
