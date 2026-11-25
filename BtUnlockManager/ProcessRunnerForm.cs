using System.Diagnostics;

namespace BtUnlockManager;

/// <summary>
/// Runs a child process (PowerShell script or BluetoothUnlockService.exe) and
/// streams its output into a window. Used for install.ps1 in dev builds,
/// --diagnose and --verify.
/// </summary>
internal sealed class ProcessRunnerForm : Form
{
    private static readonly Color Slate700 = Color.FromArgb(51, 65, 85);
    private static readonly Color Slate500 = Color.FromArgb(100, 116, 139);
    private static readonly Color Accent = Color.FromArgb(37, 99, 235);
    private static readonly Color Green = Color.FromArgb(22, 163, 74);
    private static readonly Color Red = Color.FromArgb(220, 38, 38);

    private readonly string _fileName;
    private readonly string _arguments;
    private readonly TextBox _output = new();
    private readonly Label _status = new();
    private readonly Button _close = new();
    private readonly ProgressBar _progress = new();

    private Process? _process;
    private bool _finished;

    public int ExitCode { get; private set; } = -1;

    public ProcessRunnerForm(string title, string fileName, string arguments)
    {
        _fileName = fileName;
        _arguments = arguments;

        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(820, 560);
        MinimumSize = new Size(640, 420);
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
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));

        _status.Text = "Starting…";
        _status.Dock = DockStyle.Fill;
        _status.ForeColor = Slate500;
        _status.TextAlign = ContentAlignment.MiddleLeft;

        _output.Dock = DockStyle.Fill;
        _output.Multiline = true;
        _output.ReadOnly = true;
        _output.ScrollBars = ScrollBars.Both;
        _output.WordWrap = false;
        _output.BackColor = Color.FromArgb(2, 6, 23);
        _output.ForeColor = Color.FromArgb(203, 213, 225);
        _output.Font = new Font("Consolas", 9F);
        _output.BorderStyle = BorderStyle.FixedSingle;

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.White
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));

        _progress.Dock = DockStyle.Fill;
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.MarqueeAnimationSpeed = 30;
        _progress.Margin = new Padding(0, 8, 12, 8);

        _close.Text = "Cancel";
        _close.Dock = DockStyle.Right;
        _close.Width = 110;
        _close.FlatStyle = FlatStyle.Flat;
        _close.BackColor = Color.White;
        _close.ForeColor = Slate700;
        _close.UseVisualStyleBackColor = false;
        _close.FlatAppearance.BorderColor = Color.FromArgb(226, 232, 240);
        _close.Click += OnCloseClick;

        footer.Controls.Add(_progress, 0, 0);
        footer.Controls.Add(_close, 1, 0);

        root.Controls.Add(_status, 0, 0);
        root.Controls.Add(_output, 0, 1);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);

        Shown += (_, _) => Start();
        FormClosing += OnFormClosing;
    }

    private void Start()
    {
        var start = new ProcessStartInfo(_fileName, _arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };

        try
        {
            _process = new Process
            {
                StartInfo = start,
                EnableRaisingEvents = true
            };

            _process.OutputDataReceived += (_, e) => Append(e.Data);
            _process.ErrorDataReceived += (_, e) => Append(e.Data);
            _process.Exited += (_, _) => Post(OnExited);

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            _status.Text = "Running…";
        }
        catch (Exception ex)
        {
            Append("Could not start " + _fileName + ": " + ex.Message);
            Finish(-1);
        }
    }

    private void Append(string? line)
    {
        if (line == null)
        {
            return;
        }

        Post(() =>
        {
            if (_output.IsDisposed)
            {
                return;
            }

            _output.AppendText(line + Environment.NewLine);
            _output.SelectionStart = _output.TextLength;
            _output.ScrollToCaret();
        });
    }

    private void OnExited()
    {
        if (_process == null)
        {
            return;
        }

        try
        {
            _process.WaitForExit();
        }
        catch
        {
            // Ignore.
        }

        Finish(_process.ExitCode);
    }

    private void Finish(int exitCode)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        ExitCode = exitCode;

        _progress.Style = ProgressBarStyle.Blocks;
        _progress.MarqueeAnimationSpeed = 0;
        _close.Text = "Close";
        _close.Enabled = true;

        _status.Text = exitCode == 0
            ? "Finished successfully."
            : $"Finished with exit code {exitCode}.";

        _status.ForeColor = exitCode == 0 ? Green : Red;
    }

    private void OnCloseClick(object? sender, EventArgs e)
    {
        if (!_finished)
        {
            DialogResult answer = MessageBox.Show(
                this,
                "The process is still running. Stop it?",
                "Phone Unlock",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (answer != DialogResult.Yes)
            {
                return;
            }

            try
            {
                _process?.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore.
            }
        }

        Close();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_finished)
        {
            return;
        }

        // Let the close button ask first. If the user closes with the title bar,
        // ask here too.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            DialogResult answer = MessageBox.Show(
                this,
                "The process is still running. Stop it and close?",
                "Phone Unlock",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            try
            {
                _process?.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore.
            }
        }
    }

    private void Post(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // The window closed between the check and the marshal.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _process?.Dispose();
        }

        base.Dispose(disposing);
    }
}
