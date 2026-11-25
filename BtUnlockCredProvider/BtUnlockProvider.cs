using System.Runtime.InteropServices;
using Lithnet.CredentialProvider;

namespace BtUnlockCredProvider;

/// <summary>
/// Windows Credential Provider that appears as a tile on the lock screen.
/// Shows WiFi connection status and auto-submits credentials when the
/// WiFi unlock service signals a valid phone authentication.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[ProgId("BtUnlockCredProvider")]
[Guid("E8F2B1C4-7A3D-4F5E-9B6C-1D2E3F4A5B6C")]
public class BtUnlockProvider : CredentialProviderBase
{
    /// <summary>
    /// Specifies which logon scenarios this provider supports.
    /// We support unlock and logon — the two scenarios where the lock screen appears.
    /// </summary>
    public override bool IsUsageScenarioSupported(UsageScenario cpus, CredUIWinFlags dwFlags)
    {
        switch (cpus)
        {
            case UsageScenario.Logon:
            case UsageScenario.UnlockWorkstation:
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Defines the UI controls shown on the credential tile.
    /// Shows "WiFi Unlock" with a status label that updates dynamically.
    /// </summary>
    public override IEnumerable<ControlBase> GetControls(UsageScenario cpus)
    {
        // The tile label (large text)
        yield return new CredentialProviderLabelControl("TileLabel", "📱  Phone Unlock");

        // Status text that updates dynamically with connection state
        var statusLabel = new SmallLabelControl("StatusLabel", "Initializing...");
        statusLabel.State = FieldState.DisplayInSelectedTile;
        yield return statusLabel;

        // Connection info line
        var connLabel = new SmallLabelControl("ConnLabel", "");
        connLabel.State = FieldState.DisplayInSelectedTile;
        yield return connLabel;

        // Hidden password field — needed to submit credentials to Windows
        var passwordField = new SecurePasswordTextboxControl("PasswordField", "Password");
        passwordField.State = FieldState.Hidden;
        yield return passwordField;

        // Hidden username field
        var usernameField = new TextboxControl("UsernameField", "Username");
        usernameField.State = FieldState.Hidden;
        yield return usernameField;

        // Submit button (hidden — we auto-submit, but it's required by the framework)
        var submitButton = new SubmitButtonControl("SubmitButton", "Unlock", passwordField);
        yield return submitButton;
    }

    /// <summary>
    /// Show user-specific tiles for each Windows user account.
    /// </summary>
    public override bool ShouldIncludeUserTile(CredentialProviderUser user)
    {
        return true;
    }

    /// <summary>
    /// Don't show a generic (non-user) tile.
    /// </summary>
    public override bool ShouldIncludeGenericTile()
    {
        return false;
    }

    /// <summary>
    /// Creates a tile for a specific Windows user.
    /// </summary>
    public override CredentialTile2 CreateUserTile(CredentialProviderUser user)
    {
        return new BtUnlockTile(this, user);
    }

    public override CredentialTile CreateGenericTile()
    {
        // Not used — we only create user tiles
        throw new NotSupportedException();
    }
}
