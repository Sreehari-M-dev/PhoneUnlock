using System.ComponentModel;
using BluetoothUnlockService;

namespace BtUnlockManager;

/// <summary>
/// Stores the Windows account password used by the credential provider.
/// A credential provider performs a password logon, so a Windows Hello PIN is
/// rejected — the dialog says so explicitly.
/// </summary>
internal sealed class CredentialsForm : Form
{
    private static readonly Color Slate700 = Color.FromArgb(51, 65, 85);
    private static readonly Color Slate500 = Color.FromArgb(100, 116, 139);
    private static readonly Color Accent = Color.FromArgb(37, 99, 235);
    private static readonly Color Green = Color.FromArgb(22, 163, 74);
    private static readonly Color Amber = Color.FromArgb(180, 120, 4);
    private static readonly Color Red = Color.FromArgb(220, 38, 38);

    private readonly TextBox _user = new();
    private readonly TextBox _password = new();
    private readonly TextBox _confirm = new();
    private readonly Label _message = new();
    private readonly CheckBox _showPassword = new();

    public CredentialsForm()
    {
        Text = "Windows credentials";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(600, 520);
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9.25F);
        Icon = SystemIcons.Shield;

        BuildUi();

        _user.Text = $"{Environment.UserDomainName}\\{Environment.UserName}";
        _password.UseSystemPasswordChar = true;
        _confirm.UseSystemPasswordChar = true;
        _showPassword.CheckedChanged += (_, _) =>
        {
            _password.UseSystemPasswordChar = !_showPassword.Checked;
            _confirm.UseSystemPasswordChar = !_showPassword.Checked;
        };
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 10,
            Padding = new Padding(20),
            BackColor = Color.White
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));   // title
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));   // intro
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));   // label
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));   // user
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));   // label
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));   // password
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));   // label
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));   // confirm
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // message + show
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));   // buttons

        root.Controls.Add(new Label
        {
            Text = "Store the Windows password",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI Semibold", 16F),
            ForeColor = Color.FromArgb(15, 23, 42),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        root.Controls.Add(new Label
        {
            Text = "This password is encrypted with DPAPI (LocalMachine) and used only by the " +
                   "credential provider. Use the ACCOUNT PASSWORD, not a Hello PIN.",
            Dock = DockStyle.Fill,
            ForeColor = Slate500,
            TextAlign = ContentAlignment.TopLeft
        }, 0, 1);

        root.Controls.Add(MakeLabel("Account name (e.g. PCNAME\\you, or just you)"), 0, 2);
        ConfigureBox(_user);
        root.Controls.Add(_user, 0, 3);

        root.Controls.Add(MakeLabel("Windows password"), 0, 4);
        ConfigureBox(_password);
        root.Controls.Add(_password, 0, 5);

        root.Controls.Add(MakeLabel("Confirm password"), 0, 6);
        ConfigureBox(_confirm);
        root.Controls.Add(_confirm, 0, 7);

        _showPassword.Text = "Show password";
        _showPassword.AutoSize = true;
        _showPassword.ForeColor = Slate500;
        _showPassword.Margin = new Padding(0, 8, 0, 0);

        _message.Dock = DockStyle.Fill;
        _message.ForeColor = Slate500;
        _message.Text = CredentialStore.HasCredentials()
            ? "A password is already stored. Storing a new one replaces it."
            : "No password is stored yet.";
        _message.TextAlign = ContentAlignment.TopLeft;

        var messageArea = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.White
        };
        messageArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        messageArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        messageArea.Controls.Add(_showPassword, 0, 0);
        messageArea.Controls.Add(_message, 0, 1);
        root.Controls.Add(messageArea, 0, 8);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Color.White
        };

        var store = new Button
        {
            Text = "Store password",
            Width = 140,
            Height = 36,
            FlatStyle = FlatStyle.Flat,
            BackColor = Accent,
            ForeColor = Color.White,
            UseVisualStyleBackColor = false,
            DialogResult = DialogResult.None
        };
        store.FlatAppearance.BorderSize = 0;
        store.Click += OnStore;

        var close = new Button
        {
            Text = "Close",
            Width = 100,
            Height = 36,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Slate700,
            UseVisualStyleBackColor = false,
            DialogResult = DialogResult.Cancel
        };
        close.FlatAppearance.BorderColor = Color.FromArgb(226, 232, 240);

        var delete = new Button
        {
            Text = "Delete stored password",
            Width = 170,
            Height = 36,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Red,
            UseVisualStyleBackColor = false
        };
        delete.FlatAppearance.BorderColor = Color.FromArgb(254, 202, 202);
        delete.Click += OnDelete;

        buttons.Controls.Add(store);
        buttons.Controls.Add(close);
        buttons.Controls.Add(delete);
        root.Controls.Add(buttons, 0, 9);

        Controls.Add(root);
        AcceptButton = store;
        CancelButton = close;
    }

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = Slate500,
        TextAlign = ContentAlignment.BottomLeft,
        Font = new Font("Segoe UI", 8.75F)
    };

    private static void ConfigureBox(TextBox box)
    {
        box.Dock = DockStyle.Fill;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.Font = new Font("Segoe UI", 10F);
    }

    private void OnStore(object? sender, EventArgs e)
    {
        string user = _user.Text.Trim();

        if (user.Length == 0)
        {
            ShowMessage("Enter your Windows account name.", Red);
            return;
        }

        if (_password.Text.Length == 0)
        {
            ShowMessage("Enter your Windows password.", Red);
            return;
        }

        if (!string.Equals(_password.Text, _confirm.Text, StringComparison.Ordinal))
        {
            ShowMessage("The passwords do not match.", Red);
            return;
        }

        try
        {
            CredentialStore.Store(user, _password.Text);

            if (CredentialStore.VerifyStored(out string account, out int error))
            {
                ShowMessage(
                    $"Stored and verified for {account}. Windows accepted the password.",
                    Green);
            }
            else
            {
                string reason = new Win32Exception(error).Message;
                ShowMessage(
                    $"Stored for {account}, but Windows rejected it (error {error}: {reason}). " +
                    "Use the account password, not a PIN or a Microsoft-account one-time code.",
                    Amber);
            }

            _password.Clear();
            _confirm.Clear();
        }
        catch (Exception ex)
        {
            ShowMessage("Could not store the credentials: " + ex.Message, Red);
        }
    }

    private void OnDelete(object? sender, EventArgs e)
    {
        DialogResult answer = MessageBox.Show(
            this,
            "Delete the stored Windows password? The phone will not be able to unlock the PC " +
            "until a password is stored again.",
            "Delete stored password",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes)
        {
            return;
        }

        try
        {
            CredentialStore.Delete();
            ShowMessage("Stored password deleted.", Slate700);
        }
        catch (Exception ex)
        {
            ShowMessage("Could not delete the password: " + ex.Message, Red);
        }
    }

    private void ShowMessage(string text, Color color)
    {
        _message.Text = text;
        _message.ForeColor = color;
    }
}
