using System.Diagnostics;
using System.ServiceProcess;
using BluetoothUnlockService;

namespace BtUnlockManager;

internal sealed class MainForm : Form
{
    private static readonly Color Slate900 = Color.FromArgb(15, 23, 42);
    private static readonly Color Slate700 = Color.FromArgb(51, 65, 85);
    private static readonly Color Slate500 = Color.FromArgb(100, 116, 139);
    private static readonly Color Slate200 = Color.FromArgb(226, 232, 240);
    private static readonly Color Accent = Color.FromArgb(37, 99, 235);
    private static readonly Color AccentHover = Color.FromArgb(29, 78, 216);
    private static readonly Color Green = Color.FromArgb(22, 163, 74);
    private static readonly Color Amber = Color.FromArgb(180, 120, 4);
    private static readonly Color Red = Color.FromArgb(220, 38, 38);
    private static readonly Color Surface = Color.White;
    private static readonly Color Canvas = Color.FromArgb(241, 245, 249);

    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 2000 };
    private readonly List<Button> _actionButtons = new();
    private readonly ToolTip _toolTip = new();

    private Label _serviceValue = null!;
    private Label _wifiValue = null!;
    private Label _policyValue = null!;
    private Label _credValue = null!;
    private Label _phonesValue = null!;
    private Label _networkValue = null!;

    private TextBox _logBox = null!;
    private CheckBox _autoRefresh = null!;
    private Label _busyLabel = null!;
    private Button _startButton = null!;
    private Button _stopButton = null!;
    private Button _restartButton = null!;
    private Button _installButton = null!;

    private bool _busy;
    private string _lastLogText = string.Empty;

    public MainForm()
    {
        Text = "Phone Unlock — Control Panel";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(880, 780);
        Size = new Size(950, 850);
        BackColor = Canvas;
        Font = new Font("Segoe UI", 9.25F);
        Icon = SystemIcons.Shield;

        BuildUi();

        _refreshTimer.Tick += (_, _) => RefreshStatus();
        _refreshTimer.Start();

        Shown += (_, _) => RefreshStatus();
        FormClosed += (_, _) => _refreshTimer.Stop();
    }

    // -----------------------------------------------------------------
    // UI construction
    // -----------------------------------------------------------------

    private void BuildUi()
    {
        SuspendLayout();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(18),
            BackColor = Canvas
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 212));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 200));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildStatusCard(), 0, 1);
        root.Controls.Add(BuildActions(), 0, 2);
        root.Controls.Add(BuildLogCard(), 0, 3);
        root.Controls.Add(BuildFooter(), 0, 4);

        ResumeLayout(performLayout: true);
    }

    private Control BuildHeader()
    {
        var header = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Slate900,
            Margin = new Padding(0, 0, 0, 12),
            Padding = new Padding(20, 14, 20, 14)
        };

        header.Controls.Add(new Label
        {
            Text = "Phone Unlock",
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 20F),
            AutoSize = true,
            Location = new Point(18, 10)
        });

        header.Controls.Add(new Label
        {
            Text = "Control panel — service, pairing, credentials and diagnostics",
            ForeColor = Color.FromArgb(203, 213, 225),
            Font = new Font("Segoe UI", 9.5F),
            AutoSize = true,
            Location = new Point(21, 51)
        });

        header.Controls.Add(new Label
        {
            Text = AppInfo.IsElevated() ? "ADMINISTRATOR" : "LIMITED MODE",
            ForeColor = AppInfo.IsElevated()
                ? Color.FromArgb(147, 197, 253)
                : Color.FromArgb(252, 211, 77),
            Font = new Font("Segoe UI Semibold", 8F),
            Dock = DockStyle.Right,
            Width = 160,
            TextAlign = ContentAlignment.MiddleRight
        });

        return header;
    }

    private Control BuildStatusCard()
    {
        var group = new GroupBox
        {
            Text = " Status ",
            Dock = DockStyle.Fill,
            BackColor = Surface,
            ForeColor = Slate700,
            Font = new Font("Segoe UI Semibold", 9.5F),
            Padding = new Padding(16, 12, 16, 10),
            Margin = new Padding(0, 0, 0, 12)
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 6,
            BackColor = Surface
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        for (int row = 0; row < 6; row++)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 6));
        }

        AddStatusRow(grid, 0, "Windows service", out _serviceValue);
        AddStatusRow(grid, 1, "Wi-Fi channel", out _wifiValue);
        AddStatusRow(grid, 2, "Sign-in policy", out _policyValue);
        AddStatusRow(grid, 3, "Stored credentials", out _credValue);
        AddStatusRow(grid, 4, "Paired phones", out _phonesValue);
        AddStatusRow(grid, 5, "This PC", out _networkValue);

        group.Controls.Add(grid);
        return group;
    }

    private static void AddStatusRow(TableLayoutPanel grid, int row, string title, out Label value)
    {
        var name = new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Slate500,
            Font = new Font("Segoe UI", 9.25F)
        };

        var label = new Label
        {
            Text = "…",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Slate700,
            Font = new Font("Segoe UI Semibold", 9.5F),
            AutoEllipsis = true
        };

        grid.Controls.Add(name, 0, row);
        grid.Controls.Add(label, 1, row);
        value = label;
    }

    private Control BuildActions()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 3,
            BackColor = Canvas,
            Margin = new Padding(0, 0, 0, 12)
        };

        for (int column = 0; column < 4; column++)
        {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        }

        for (int row = 0; row < 3; row++)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 3));
        }

        _startButton = MakeButton("Start service", Accent, Color.White, OnStart);
        _stopButton = MakeButton("Stop service", Surface, Slate700, OnStop);
        _restartButton = MakeButton("Restart", Surface, Slate700, OnRestart);
        Button pairButton = MakeButton("Pair phone…", Accent, Color.White, OnPair);

        Button credentialsButton = MakeButton("Set credentials…", Surface, Slate700, OnCredentials);
        _installButton = MakeButton("Install", Surface, Slate700, OnInstall);
        Button policyButton = MakeButton("Fix sign-in policy", Surface, Slate700, OnFixPolicy);
        Button diagnosticsButton = MakeButton("Diagnostics…", Surface, Slate700, OnDiagnostics);

        Button logsButton = MakeButton("View logs…", Surface, Slate700, OnLogs);
        Button dataFolderButton = MakeButton("Open data folder", Surface, Slate700, OnOpenDataFolder);
        Button refreshButton = MakeButton("Refresh", Surface, Slate700, (_, _) => RefreshStatus());
        Button uninstallButton = MakeButton("Uninstall…", Color.FromArgb(254, 226, 226), Red, OnUninstall);

        Control[] buttons =
        {
            _startButton, _stopButton, _restartButton, pairButton,
            credentialsButton, _installButton, policyButton, diagnosticsButton,
            logsButton, dataFolderButton, refreshButton, uninstallButton
        };

        for (int index = 0; index < buttons.Length; index++)
        {
            grid.Controls.Add(buttons[index], index % 4, index / 4);
        }

        _actionButtons.AddRange(buttons.OfType<Button>());
        return grid;
    }

    private static Button MakeButton(string text, Color back, Color fore, EventHandler onClick)
    {
        var button = new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            Margin = new Padding(5),
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = fore,
            Font = new Font("Segoe UI Semibold", 9.25F),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false
        };

        button.FlatAppearance.BorderSize = back == Surface ? 1 : 0;
        button.FlatAppearance.BorderColor = Slate200;

        if (back == Accent)
        {
            button.FlatAppearance.MouseOverBackColor = AccentHover;
        }
        else if (back == Surface)
        {
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(248, 250, 252);
        }

        button.Click += onClick;
        return button;
    }

    private Control BuildLogCard()
    {
        var group = new GroupBox
        {
            Text = " Recent activity ",
            Dock = DockStyle.Fill,
            BackColor = Surface,
            ForeColor = Slate700,
            Font = new Font("Segoe UI Semibold", 9.5F),
            Padding = new Padding(12, 10, 12, 10),
            Margin = new Padding(0, 0, 0, 10)
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Surface
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Surface
        };

        _autoRefresh = new CheckBox
        {
            Text = "Auto-refresh",
            Checked = true,
            AutoSize = true,
            ForeColor = Slate500,
            Margin = new Padding(0, 4, 0, 0)
        };

        var openLogs = new LinkLabel
        {
            Text = "Open log folder",
            AutoSize = true,
            LinkColor = Accent,
            ActiveLinkColor = AccentHover,
            Margin = new Padding(16, 5, 0, 0)
        };
        openLogs.LinkClicked += (_, _) => OpenDataFolder();

        bar.Controls.Add(_autoRefresh);
        bar.Controls.Add(openLogs);

        _logBox = new TextBox
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

        layout.Controls.Add(bar, 0, 0);
        layout.Controls.Add(_logBox, 0, 1);
        group.Controls.Add(layout);

        return group;
    }

    private Control BuildFooter()
    {
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Canvas
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _busyLabel = new Label
        {
            Text = $"Logs: {AppInfo.ServiceLogPath}",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Slate500,
            Font = new Font("Segoe UI", 8.5F),
            AutoEllipsis = true
        };

        string version = typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "dev";

        var versionLabel = new Label
        {
            Text = $"v{version}",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Slate500,
            Font = new Font("Segoe UI", 8.5F),
            Margin = new Padding(8, 0, 0, 0)
        };

        footer.Controls.Add(_busyLabel, 0, 0);
        footer.Controls.Add(versionLabel, 1, 0);
        return footer;
    }

    // -----------------------------------------------------------------
    // Status refresh
    // -----------------------------------------------------------------

    private void RefreshStatus()
    {
        if (IsDisposed)
        {
            return;
        }

        ServiceControllerStatus? status = AppInfo.ServiceStatus();

        switch (status)
        {
            case null:
                SetValue(_serviceValue, "Not installed", Slate500);
                break;
            case ServiceControllerStatus.Running:
                SetValue(_serviceValue, "Running", Green);
                break;
            case ServiceControllerStatus.Stopped:
                SetValue(_serviceValue, "Stopped", Red);
                break;
            case ServiceControllerStatus.StartPending:
                SetValue(_serviceValue, "Starting…", Amber);
                break;
            case ServiceControllerStatus.StopPending:
                SetValue(_serviceValue, "Stopping…", Amber);
                break;
            default:
                SetValue(_serviceValue, status.Value.ToString(), Slate700);
                break;
        }

        bool listening = AppInfo.IsPortListening();
        if (listening)
        {
            SetValue(_wifiValue, $"Listening on TCP {AppInfo.TcpPort} (Wi-Fi)", Green);
        }
        else if (status == ServiceControllerStatus.Running)
        {
            SetValue(_wifiValue, "Service running, port not open yet", Amber);
        }
        else
        {
            SetValue(_wifiValue, "Offline", Slate500);
        }

        int? disableCad = LogonPolicy.DisableCad();
        if (disableCad == 1)
        {
            SetValue(_policyValue, "Ctrl+Alt+Del not required", Green);
        }
        else if (disableCad == 0)
        {
            SetValue(_policyValue, "Ctrl+Alt+Del REQUIRED — unlock will fail", Red);
        }
        else
        {
            SetValue(_policyValue, "Not configured — Ctrl+Alt+Del is required", Red);
        }

        bool hasCredentials = CredentialStore.HasCredentials();
        SetValue(
            _credValue,
            hasCredentials ? "Windows password stored (DPAPI)" : "No password stored",
            hasCredentials ? Green : Amber);

        string phones = "No phone paired yet";
        string phoneTooltip = "Use Pair phone… to scan the QR code with the Android app.";

        try
        {
            PairingStore.PairingConfig config = PairingStore.Load();
            List<string> names = config.Paired
                .Select(phone => phone.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();

            if (names.Count > 0)
            {
                phones = names.Count == 1
                    ? $"1 paired — {names[0]}"
                    : $"{names.Count} paired — {string.Join(", ", names.Take(3))}" +
                      (names.Count > 3 ? "…" : string.Empty);

                phoneTooltip = string.Join(Environment.NewLine, names);
            }
        }
        catch (Exception ex)
        {
            phones = "Unavailable: " + ex.Message;
        }

        SetValue(_phonesValue, phones,
            phones.StartsWith("No ", StringComparison.Ordinal) ? Slate700 : Green);
        _toolTip.SetToolTip(_phonesValue, phoneTooltip);

        string address = NetworkInfo.PrimaryIPv4() ?? "no IPv4 address";
        SetValue(_networkValue, $"{Environment.MachineName}  ·  {address}", Slate700);

        bool installed = status != null;
        _startButton.Enabled = !_busy && status == ServiceControllerStatus.Stopped;
        _stopButton.Enabled = !_busy &&
                              status is ServiceControllerStatus.Running
                              or ServiceControllerStatus.StartPending
                              or ServiceControllerStatus.Paused;
        _restartButton.Enabled = !_busy && installed;
        _installButton.Text = installed ? "Repair install" : "Install";

        if (_autoRefresh.Checked)
        {
            string text = string.Join(
                Environment.NewLine,
                AppInfo.TailLines(AppInfo.ServiceLogPath, 60));

            if (string.IsNullOrEmpty(text))
            {
                text = "(service.log is empty)";
            }

            if (!string.Equals(text, _lastLogText, StringComparison.Ordinal))
            {
                _lastLogText = text;
                _logBox.Text = text;
                _logBox.SelectionStart = _logBox.TextLength;
                _logBox.ScrollToCaret();
            }
        }
    }

    private static void SetValue(Label label, string text, Color color)
    {
        label.Text = text;
        label.ForeColor = color;
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;

        foreach (Button button in _actionButtons)
        {
            button.Enabled = !busy;
        }

        if (!string.IsNullOrEmpty(message))
        {
            _busyLabel.Text = message;
        }
        else if (!busy)
        {
            _busyLabel.Text = $"Logs: {AppInfo.ServiceLogPath}";
        }

        if (!busy)
        {
            RefreshStatus();
        }
    }

    // -----------------------------------------------------------------
    // Service actions
    // -----------------------------------------------------------------

    private async void OnStart(object? sender, EventArgs e)
    {
        await RunServiceAction("Starting service…", service =>
        {
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        });
    }

    private async void OnStop(object? sender, EventArgs e)
    {
        await RunServiceAction("Stopping service…", service =>
        {
            service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        });
    }

    private async void OnRestart(object? sender, EventArgs e)
    {
        await RunServiceAction("Restarting service…", service =>
        {
            if (service.Status != ServiceControllerStatus.Stopped)
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }

            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        });
    }

    private async Task RunServiceAction(string message, Action<ServiceController> action)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true, message);

        try
        {
            await Task.Run(() =>
            {
                using var service = new ServiceController(AppInfo.ServiceName);
                action(service);
            });

            _busyLabel.Text = "Done.";
        }
        catch (InvalidOperationException)
        {
            ShowError("The service is not installed. Click Install / Repair first.");
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            ShowError(
                "The service did not change state in time. Check the log at\n" +
                AppInfo.ServiceLogPath);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // -----------------------------------------------------------------
    // Pairing / credentials / logs
    // -----------------------------------------------------------------

    private void OnPair(object? sender, EventArgs e)
    {
        if (AppInfo.ServiceStatus() != ServiceControllerStatus.Running)
        {
            DialogResult answer = MessageBox.Show(
                this,
                "The service is not running, so the phone's confirmation cannot be recorded.\n\n" +
                "Start the service and continue?",
                "Pair phone",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (answer != DialogResult.Yes)
            {
                return;
            }

            try
            {
                using var service = new ServiceController(AppInfo.ServiceName);
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }
            catch (Exception ex)
            {
                ShowError("Could not start the service: " + ex.Message);
                return;
            }
        }

        using var form = new PairPhoneForm();
        form.ShowDialog(this);
        RefreshStatus();
    }

    private void OnCredentials(object? sender, EventArgs e)
    {
        using var form = new CredentialsForm();
        form.ShowDialog(this);
        RefreshStatus();
    }

    private void OnLogs(object? sender, EventArgs e)
    {
        using var form = new LogViewerForm();
        form.ShowDialog(this);
    }

    private void OnOpenDataFolder(object? sender, EventArgs e) => OpenDataFolder();

    private static void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            Process.Start(new ProcessStartInfo(AppInfo.DataDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Could not open the data folder: " + ex.Message,
                "Phone Unlock",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    // -----------------------------------------------------------------
    // Install / uninstall / policy
    // -----------------------------------------------------------------

    private void OnInstall(object? sender, EventArgs e)
    {
        if (SetupEngine.HasInstallerPayload || SetupEngine.HasLocalPayload)
        {
            using var form = new InstallForm(uninstall: false, deleteData: false, showNextSteps: true);
            form.ShowDialog(this);
            RefreshStatus();
            return;
        }

        string? script = AppInfo.FindFile("install.ps1");
        if (script == null)
        {
            ShowError(
                "This build does not contain the installer payload.\n\n" +
                "Download the release build of PhoneUnlock.exe from GitHub, or run " +
                "scripts\\install.ps1 from the source tree.");
            return;
        }

        using var runner = new ProcessRunnerForm(
            "Install / Repair Phone Unlock",
            "powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"");

        runner.ShowDialog(this);
        RefreshStatus();
    }

    private void OnUninstall(object? sender, EventArgs e)
    {
        DialogResult answer = MessageBox.Show(
            this,
            "This removes the Windows service, the credential provider, the firewall rules " +
            "and the Start Menu shortcut.\n\n" +
            "Also delete the stored Windows password, the pairing secret and the logs?",
            "Uninstall Phone Unlock",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer == DialogResult.Cancel)
        {
            return;
        }

        using var form = new InstallForm(
            uninstall: true,
            deleteData: answer == DialogResult.Yes,
            showNextSteps: false);

        form.ShowDialog(this);

        if (form.Succeeded && AppInfo.IsRunningFromInstallDir())
        {
            SetupEngine.ScheduleInstallDirRemoval();
            Close();
            return;
        }

        RefreshStatus();
    }

    private void OnFixPolicy(object? sender, EventArgs e)
    {
        DialogResult answer = MessageBox.Show(
            this,
            "Set \"Interactive logon: Do not require CTRL+ALT+DEL\" (DisableCAD = 1)?\n\n" +
            "This is required for the phone unlock to work. The normal password tile " +
            "stays available as a fallback.",
            "Sign-in policy",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (answer != DialogResult.Yes)
        {
            return;
        }

        try
        {
            SetupEngine.SetCadPolicy();
            MessageBox.Show(
                this,
                "Policy set. It takes effect the next time the PC locks.",
                "Phone Unlock",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            ShowError("Could not set the policy: " + ex.Message);
        }

        RefreshStatus();
    }

    private void OnDiagnostics(object? sender, EventArgs e)
    {
        string? exe = AppInfo.FindServiceExe();
        if (exe == null)
        {
            ShowError(
                "BluetoothUnlockService.exe was not found.\n\n" +
                "Install the service first (Install / Repair), or run the control panel " +
                "from the release folder.");
            return;
        }

        using var runner = new ProcessRunnerForm("Diagnostics", exe, "--diagnose");
        runner.ShowDialog(this);
    }

    private void ShowError(string message)
    {
        MessageBox.Show(this, message, "Phone Unlock", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
