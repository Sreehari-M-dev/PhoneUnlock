using System.Drawing.Imaging;
using System.ServiceProcess;
using BluetoothUnlockService;
using QRCoder;

namespace BtUnlockManager;

/// <summary>
/// Shows the pairing QR code in a window and waits for the phone to confirm.
/// The payload and the confirmation file are the same ones <c>--pair</c> uses,
/// so both paths stay compatible.
/// </summary>
internal sealed class PairPhoneForm : Form
{
    private static readonly Color Slate700 = Color.FromArgb(51, 65, 85);
    private static readonly Color Slate500 = Color.FromArgb(100, 116, 139);
    private static readonly Color Accent = Color.FromArgb(37, 99, 235);
    private static readonly Color Green = Color.FromArgb(22, 163, 74);
    private static readonly Color Amber = Color.FromArgb(180, 120, 4);
    private static readonly Color Red = Color.FromArgb(220, 38, 38);

    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 700 };
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly DateTime _deadline = DateTime.UtcNow.AddSeconds(120);

    private readonly PictureBox _qrBox = new();
    private readonly Label _statusLabel = new();
    private readonly Label _countdownLabel = new();
    private readonly Label _addressValue = new();
    private readonly Label _deviceValue = new();
    private readonly Label _bluetoothValue = new();
    private readonly Button _saveButton = new();
    private readonly Button _copyButton = new();

    private string? _payload;

    public PairPhoneForm()
    {
        Text = "Pair a phone";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(820, 590);
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9.25F);
        Icon = SystemIcons.Shield;

        BuildUi();

        _poll.Tick += (_, _) => Poll();
        Shown += (_, _) => BeginPairing();
        FormClosed += (_, _) =>
        {
            _poll.Stop();
            _qrBox.Image?.Dispose();
        };
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(18),
            BackColor = Color.White
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.White
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 400));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _qrBox.Dock = DockStyle.Fill;
        _qrBox.SizeMode = PictureBoxSizeMode.Zoom;
        _qrBox.BackColor = Color.White;
        _qrBox.BorderStyle = BorderStyle.FixedSingle;
        _qrBox.Margin = new Padding(0, 0, 18, 0);

        content.Controls.Add(_qrBox, 0, 0);
        content.Controls.Add(BuildDetails(), 1, 0);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Color.White
        };

        var closeButton = new Button
        {
            Text = "Close",
            Width = 110,
            Height = 34,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Slate700,
            UseVisualStyleBackColor = false,
            DialogResult = DialogResult.Cancel
        };
        closeButton.FlatAppearance.BorderColor = Color.FromArgb(226, 232, 240);

        _saveButton.Text = "Save PNG…";
        _saveButton.Width = 110;
        _saveButton.Height = 34;
        _saveButton.FlatStyle = FlatStyle.Flat;
        _saveButton.BackColor = Color.White;
        _saveButton.ForeColor = Slate700;
        _saveButton.UseVisualStyleBackColor = false;
        _saveButton.FlatAppearance.BorderColor = Color.FromArgb(226, 232, 240);
        _saveButton.Click += OnSavePng;

        _copyButton.Text = "Copy payload";
        _copyButton.Width = 120;
        _copyButton.Height = 34;
        _copyButton.FlatStyle = FlatStyle.Flat;
        _copyButton.BackColor = Color.White;
        _copyButton.ForeColor = Slate700;
        _copyButton.UseVisualStyleBackColor = false;
        _copyButton.FlatAppearance.BorderColor = Color.FromArgb(226, 232, 240);
        _copyButton.Click += OnCopyPayload;

        footer.Controls.Add(closeButton);
        footer.Controls.Add(_saveButton);
        footer.Controls.Add(_copyButton);

        root.Controls.Add(content, 0, 0);
        root.Controls.Add(footer, 0, 1);
        Controls.Add(root);
        CancelButton = closeButton;
    }

    private Control BuildDetails()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 8,
            BackColor = Color.White
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));

        layout.Controls.Add(new Label
        {
            Text = "Pair your phone",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI Semibold", 16F),
            ForeColor = Color.FromArgb(15, 23, 42),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        layout.Controls.Add(new Label
        {
            Text = "Open Phone Unlock on the phone, tap “Pair new PC” and scan the code. " +
                   "Keep the phone on the same Wi-Fi or on its hotspot connected to this PC.",
            Dock = DockStyle.Fill,
            ForeColor = Slate500,
            TextAlign = ContentAlignment.TopLeft
        }, 0, 1);

        var info = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            BackColor = Color.White
        };
        info.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddInfoRow(info, 0, "Device", _deviceValue);
        AddInfoRow(info, 1, "Address", _addressValue);
        AddInfoRow(info, 2, "Bluetooth", _bluetoothValue);
        _bluetoothValue.AutoEllipsis = true;
        layout.Controls.Add(info, 0, 2);

        var warning = new Label
        {
            Text = "Treat this QR code like a password. Anyone who photographs it can " +
                   "unlock this PC. Pair in private.",
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(254, 252, 232),
            ForeColor = Color.FromArgb(133, 100, 4),
            Padding = new Padding(10, 6, 10, 6),
            TextAlign = ContentAlignment.MiddleLeft
        };
        layout.Controls.Add(warning, 0, 3);

        layout.Controls.Add(new Label
        {
            Text = "Status",
            Dock = DockStyle.Fill,
            ForeColor = Slate500,
            Font = new Font("Segoe UI Semibold", 8.5F),
            TextAlign = ContentAlignment.BottomLeft
        }, 0, 4);

        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.ForeColor = Amber;
        _statusLabel.Font = new Font("Segoe UI Semibold", 9.5F);
        _statusLabel.Text = "Preparing…";
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(_statusLabel, 0, 5);

        _countdownLabel.Dock = DockStyle.Fill;
        _countdownLabel.ForeColor = Slate500;
        _countdownLabel.TextAlign = ContentAlignment.TopLeft;
        layout.Controls.Add(_countdownLabel, 0, 6);

        layout.Controls.Add(new Label
        {
            Text = "The code contains a per-PC 256-bit secret.",
            Dock = DockStyle.Fill,
            ForeColor = Slate500,
            Font = new Font("Segoe UI", 8F),
            TextAlign = ContentAlignment.BottomLeft
        }, 0, 7);

        return layout;
    }

    private static void AddInfoRow(TableLayoutPanel grid, int row, string title, Label value)
    {
        var name = new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            ForeColor = Slate500,
            TextAlign = ContentAlignment.MiddleLeft
        };

        value.Dock = DockStyle.Fill;
        value.ForeColor = Slate700;
        value.Font = new Font("Segoe UI Semibold", 9.25F);
        value.TextAlign = ContentAlignment.MiddleLeft;
        value.Text = "…";

        grid.Controls.Add(name, 0, row);
        grid.Controls.Add(value, 1, row);
    }

    // -----------------------------------------------------------------
    // Pairing
    // -----------------------------------------------------------------

    private void BeginPairing()
    {
        if (AppInfo.ServiceStatus() != ServiceControllerStatus.Running)
        {
            SetStatus(
                "The service is not running. Start it from the main window, then reopen this dialog.",
                Red);
            _saveButton.Enabled = false;
            _copyButton.Enabled = false;
            return;
        }

        string? ip = NetworkInfo.PrimaryIPv4();
        if (ip == null)
        {
            SetStatus(
                "No usable IPv4 address. Connect the PC to the phone hotspot (or any Wi-Fi), then reopen this dialog.",
                Red);
            _saveButton.Enabled = false;
            _copyButton.Enabled = false;
            return;
        }

        try
        {
            PairingStore.PairingConfig config = PairingStore.Load();
            string? bluetooth = AppInfo.ReadLastBluetoothAddress();

            _deviceValue.Text = config.Name;
            _addressValue.Text = $"{ip}:{config.Port}";
            _bluetoothValue.Text = string.IsNullOrWhiteSpace(bluetooth)
                ? "not detected (Wi-Fi only)"
                : bluetooth;

            _payload = PairingStore.BuildCompactPayload(ip, bluetooth);
            RenderQr(_payload);

            // A stale result from an earlier attempt would report success instantly.
            try
            {
                File.Delete(PairingStore.ResultFile);
            }
            catch
            {
                // The timestamp check below covers a delete that is not allowed.
            }

            SetStatus("Waiting for the phone to confirm…", Amber);
            _poll.Start();
            UpdateCountdown();
        }
        catch (Exception ex)
        {
            SetStatus("Could not create the pairing payload: " + ex.Message, Red);
            _saveButton.Enabled = false;
            _copyButton.Enabled = false;
        }
    }

    private void RenderQr(string payload)
    {
        using var generator = new QRCodeGenerator();
        using QRCodeData data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);

        // 8 px per module keeps the ~49-module code at ~390 px and crisp on
        // any DPI. QRCoder already draws the 4-module quiet zone.
        byte[] png = new PngByteQRCode(data).GetGraphic(8);

        using var stream = new MemoryStream(png);
        using var image = Image.FromStream(stream);

        _qrBox.Image?.Dispose();
        _qrBox.Image = new Bitmap(image);
    }

    private void Poll()
    {
        try
        {
            if (File.Exists(PairingStore.ResultFile) &&
                File.GetLastWriteTimeUtc(PairingStore.ResultFile) >= _startedUtc)
            {
                string result = File.ReadAllText(PairingStore.ResultFile).Trim();
                string[] parts = result.Split('|');
                string phoneName = parts.Length > 1 ? parts[1] : "your phone";

                _poll.Stop();
                SetStatus($"Paired successfully with {phoneName}. You can close this window.", Green);
                _countdownLabel.Text = string.Empty;
                _saveButton.Enabled = false;
                _copyButton.Enabled = false;

                try
                {
                    File.Delete(PairingStore.ResultFile);
                }
                catch
                {
                    // Ignore.
                }

                System.Media.SystemSounds.Asterisk.Play();
                return;
            }
        }
        catch
        {
            // The service is writing the file; retry on the next tick.
        }

        if (DateTime.UtcNow >= _deadline)
        {
            _poll.Stop();
            SetStatus(
                "Timed out — no confirmation from the phone. Check that the phone is on the same " +
                "network, then close this window and try again.",
                Red);
            _countdownLabel.Text = string.Empty;
            return;
        }

        UpdateCountdown();
    }

    private void UpdateCountdown()
    {
        TimeSpan remaining = _deadline - DateTime.UtcNow;
        _countdownLabel.Text = remaining > TimeSpan.Zero
            ? $"The code expires in {Math.Max(0, (int)remaining.TotalSeconds)} seconds."
            : string.Empty;
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }

    // -----------------------------------------------------------------
    // Buttons
    // -----------------------------------------------------------------

    private void OnSavePng(object? sender, EventArgs e)
    {
        if (_qrBox.Image == null)
        {
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "Save pairing QR code",
            Filter = "PNG image|*.png",
            FileName = $"phone-unlock-{Environment.MachineName}.png"
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            try
            {
                _qrBox.Image.Save(dialog.FileName, ImageFormat.Png);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this, "Could not save the image: " + ex.Message,
                    "Phone Unlock", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void OnCopyPayload(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_payload))
        {
            return;
        }

        try
        {
            Clipboard.SetText(_payload);
            _statusLabel.Text = "Payload copied to the clipboard.";
            _statusLabel.ForeColor = Slate700;
        }
        catch
        {
            // Clipboard can be locked by another process; harmless.
        }
    }
}
