# Phone Unlock

Unlock your Windows PC with your phone's fingerprint — over **Bluetooth**, or over **Wi-Fi using your phone's hotspot**.

No home router or shared network is required. Bluetooth needs no Wi-Fi at all, and keeps working while your phone is also connected to earbuds or a headset.

```
Phone (fingerprint) ──Bluetooth (RFCOMM)──► Windows service ──► Credential Provider ──► Unlocked
Phone (fingerprint) ──Wi-Fi hotspot/TCP───► Windows service ──► Credential Provider ──► Unlocked
```

## How It Works

1. **Android app**: you confirm with your fingerprint. The app encrypts an unlock command and sends it over the best available transport (Wi-Fi first, then Bluetooth).
2. **Windows service** (`BluetoothUnlockService`, LocalSystem): receives the command, decrypts it, checks the 60-second replay window, and verifies the session is really locked.
3. **Credential Provider**: writes a short-lived signal (valid for 90 seconds) and submits your stored Windows credentials to LogonUI.
4. **Secure sign-in**: the installer sets `DisableCAD = 1` ("Do not require CTRL+ALT+DEL"), so LogonUI loads the credential provider as soon as the session locks. No Secure Attention Sequence is needed — or sent.

The normal Windows password tile always stays available as a fallback.

## Requirements

- Windows 10/11 x64
- A Bluetooth adapter on the PC (Classic/BR-EDR — this is what RFCOMM needs)
- Android 8.0+ phone with a fingerprint sensor
- For the Wi-Fi path only: the phone's mobile hotspot
- **To build only:** [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and the Android SDK

You do **not** need the .NET SDK to install. The control panel is a single
self-contained `PhoneUnlock.exe`, and it installs the .NET 8 runtime silently if
the PC does not have it yet.

## Install from GitHub (two files)

Everything ships as **two files** on the [Releases](../../releases) page:

| File | What it is |
|------|------------|
| `PhoneUnlock.exe` | Windows control panel + the service/credential-provider payload inside it |
| `PhoneUnlock.apk` | The Android app |

1. Download both files from the latest release (or the `PhoneUnlock-<version>.zip`, which contains both plus `SHA256SUMS.txt`).
2. Copy `PhoneUnlock.apk` to the phone and install it (allow "install unknown apps" for your file manager or browser).
3. On the PC, right-click **`PhoneUnlock.exe` → Run as administrator**. The manifest asks for elevation anyway; accept the UAC prompt.
4. Click **Install**. The control panel:
   - extracts the embedded service payload to `C:\Program Files\BtUnlock`,
   - installs the .NET 8 runtime if it is missing (bundled in the exe, no download needed),
   - registers and starts `BluetoothUnlockService`,
   - sets `DisableCAD = 1`, adds the firewall rules and registers the credential provider.
5. Click **Set credentials…** and store your Windows account **password** (not the Hello PIN).
6. Click **Pair phone…**, then scan the QR code with the Android app.
7. Lock the PC with `Win + L` and tap **Unlock PC** in the app.

The control panel stays portable: run it from Downloads, a USB stick, or keep it
in `C:\Program Files\BtUnlock`. **Uninstall…** removes the service, provider,
firewall rules and shortcut, and optionally wipes the stored password, pairing
secret and logs. You can reinstall at any time by running the same exe again.

> `dist\SHA256SUMS.txt` (attached to each release) lets you verify the download:
> `Get-FileHash .\PhoneUnlock.exe -Algorithm SHA256`

### The control panel

| Area | What it does |
|------|--------------|
| **Status** | Live service state, TCP 9735 listener, `DisableCAD` policy, stored credentials, paired phones, this PC's name and IPv4 |
| **Start / Stop / Restart** | Controls `BluetoothUnlockService` without leaving the app |
| **Install / Repair install** | Installs from the embedded payload, or re-copies/repairs an existing install |
| **Pair phone…** | Shows the QR code and waits (120 s) for the phone to confirm |
| **Set credentials…** | Stores, verifies or deletes the DPAPI-encrypted Windows password |
| **Fix sign-in policy** | Sets `DisableCAD = 1` if the unlock tile would otherwise be inactive |
| **Diagnostics…** | Runs `BluetoothUnlockService.exe --diagnose` in a console window |
| **View logs… / Open data folder** | `service.log`, `credprovider.log`, `pairing.json`, `credentials.dat` |
| **Uninstall…** | Removes the install; asks whether to delete the stored secrets and logs |

If the panel is started without elevation it opens in **LIMITED MODE**: status
and logs work, but Install/Uninstall/Start/Stop are refused by Windows.

## Windows Installation (from source)

Use this path if you are working in the source tree instead of the release exe.

**Easiest:** double-click **`Install.cmd`** in this folder and accept the UAC prompt. It elevates itself and runs the installer with the right execution policy.

Or, from **PowerShell opened as Administrator**:

```powershell
cd "D:\Unlock_PC-main\Unlock_PC"
powershell -NoProfile -ExecutionPolicy Bypass -File ".\scripts\install.ps1"
```

> **Why did double-clicking `install.ps1` open Notepad?**
> `.ps1` files are not executable programs. Double-clicking one uses its file
> association, which is often Notepad. Running the command in **Command Prompt**
> (`cmd.exe`) does the same thing. Use `Install.cmd`, or run it from a real
> PowerShell window — the title bar must say **Administrator: Windows PowerShell**.

The installer:

- Stops stale `BluetoothUnlockService` processes that would hold TCP 9735
- Builds the solution in Release
- Installs the service to `C:\Program Files\BtUnlock` (auto-start, LocalSystem, restart-on-failure)
- Sets `DisableCAD = 1` so Ctrl+Alt+Del is not required at sign-in (required for automatic unlock) and removes the obsolete `SoftwareSASGeneration` value
- Locks `C:\ProgramData\BtUnlock` down: SYSTEM/Administrators write, Users read
- Adds firewall rules for TCP 9735, UDP 9736 (inbound) and UDP 9737 (outbound), local subnet only
- Registers the credential provider (Lithnet module, with manual registry fallback)
- Starts the service and prints the log tail if it fails

### Store your Windows credentials

```powershell
& "C:\Program Files\BtUnlock\BluetoothUnlockService.exe" --setup
```

Enter your account name (`PCNAME\user` or just `user`) and your **Windows account password**. Credentials are encrypted with DPAPI (`LocalMachine`) and stored in `C:\ProgramData\BtUnlock\credentials.dat`.

> If your Windows Hello PIN is rejected at the lock screen, re-run `--setup` and store the account **password** instead. A credential provider performs a password logon, not a Hello/PIN logon.

## Pair with a QR code

The control panel's **Pair phone…** button does this with a dialog: it shows the
QR code, waits up to 120 seconds, and closes itself once the phone confirms.
The command-line equivalent, from an **elevated** prompt, is:

```powershell
& "C:\Program Files\BtUnlock\BluetoothUnlockService.exe" --pair
```

It prints a QR code in the terminal. In the Android app tap **Pair new PC** and
scan it — no IP, port or key typing.

The code is a compact binary payload (`p1.` + unpadded Base64Url), not JSON:
67 bytes instead of 197, which is what keeps it at QR version 6 (41 modules,
about 12 cm wide in a terminal) instead of version 12 (73 modules). The layout:

```
p1.<base64url(
  version(1) flags(1) ipv4(4) port(2 BE) id(16) secret(32) [bt_mac(6)] name(utf8)
)>
```

`--pair --compact` prints that payload as text, and `--pair --json` prints the
old verbose JSON form (both are useful for debugging and for the contract tests).
The app still accepts the JSON form, so QR codes printed by older builds keep
working.

Error correction is level **M** (the ISO default for screen-displayed codes) and
the quiet zone is the standard 4 modules. If the code is wider than your window,
press `Ctrl+-` once or twice in Windows Terminal to shrink the font — do not crop
the light border, scanners need it.

What happens when the phone scans it:

1. The app validates the payload (schema version, 32-byte key, address, port) and
   stores the PC in its device list.
2. It sends `PAIR_CONFIRM|<ms>|<phone-id>|<phone-name>` to the PC over TCP 9735,
   encrypted with the new key (Bluetooth is tried if Wi-Fi fails and the code
   carried `bt_mac`).
3. The running service records the phone in `pairing.json` and answers
   `PAIR_OK|<machine-name>`, so `--pair` prints
   `✓ Paired successfully with <phone name>`.

If step 2 cannot reach the PC, the device is still saved (the key is valid) and
the app marks it **not confirmed**. Fix the address with **⋮ → Edit address…**,
then **⋮ → Confirm with PC** to retry the handshake without rescanning.

One phone can hold any number of PCs: each QR code adds a card under **Your
PCs**, and the selected card is what **Unlock PC** / **Lock PC** act on. Scanning
a QR code for a PC that was migrated from the old single-PC build replaces that
entry instead of duplicating it.

The `secret` is a per-PC random 256-bit key, stored in
`C:\ProgramData\BtUnlock\pairing.json` (writable only by SYSTEM/Administrators).
Commands are then sent as `v2.<base64(nonce|AES-256-GCM ciphertext|tag)>`. The
old hardcoded-key format is still accepted so an already-paired phone keeps
working, but re-pair to get authenticated messages.

> **Treat the QR code as a password.** Anyone who photographs it can unlock the
> PC. Pair in private, and re-run `--pair` (then re-pair the phone) if it leaked.

## Pair the Phone Once (Bluetooth)

Pairing is a one-time step. It must be started **from Windows**, because the PC rejects pairing pages from unknown devices.

1. On the phone: **Settings → Bluetooth** → turn Bluetooth on. Keep this screen open so the phone stays discoverable.
2. On the PC: **Settings → Bluetooth & devices → Add device → Bluetooth**.
3. Pick your phone (for example `moto g34 5G`).
4. Compare the PIN shown on both devices and confirm on both.
5. Verify the phone lists your PC (for example `MY-PC`) under paired devices.

Confirm the PC side with:

```powershell
& "C:\Program Files\BtUnlock\BluetoothUnlockService.exe" --diagnose
```

It should print `Bluetooth RFCOMM: OK` and `Bluetooth address: AA:BB:CC:DD:EE:FF`.

The app can also start the pairing from the phone: **⋮ → Pair over Bluetooth…**
on the PC's card, which uses the `bt_mac` from the QR code. Windows-initiated
pairing remains the reliable path when no QR code has been scanned yet.

## Wi-Fi (Phone Hotspot) — Optional

Use this when you want the faster path or Bluetooth is unavailable:

1. Phone: **Settings → Hotspot & Tethering → Wi-Fi hotspot** on.
2. PC: connect to that hotspot.
3. Open the app. It discovers the PC automatically:
   - UDP broadcast probe to port **9736**
   - the PC also broadcasts its presence to port **9737** every 5 seconds
4. No IP address entry is needed. If discovery is blocked, open **⋮ → Edit address…**
   on the PC's card and type its IPv4 address (see `--diagnose`).

The app defaults to **Automatic (Wi-Fi first, then Bluetooth)** and retries the other transport if one fails. You can pin a transport under **Change** in the app.

## Build & Install the Android App

The Android project lives at `D:\Android_Projects\WifiUnlock`.

```powershell
cd "D:\Android_Projects\WifiUnlock"
$env:JAVA_HOME = "C:\Program Files\Android\Android Studio\jbr"
.\gradlew.bat assembleDebug

& "C:\Users\<you>\AppData\Local\Android\Sdk\platform-tools\adb.exe" install -r `
  "app\build\outputs\apk\debug\app-debug.apk"
```

On first launch, grant the Bluetooth permissions when asked (Android 12+ requires `BLUETOOTH_CONNECT` / `BLUETOOTH_SCAN` at runtime). The camera permission is only needed for the QR scanner.

Two on-device tests run against a real PC when given its details:

```powershell
$adb = "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe"

# 1. QR payload contract (no PC needed) — includes the compact p1. golden vector
& $adb shell am instrument -w -e class com.example.wifiunlock.PairingPayloadTest `
  com.example.wifiunlock.test/androidx.test.runner.AndroidJUnitRunner

# 2. Live pairing handshake — address and secret from pairing.json
& $adb shell am instrument -w -e pcIp 10.0.0.5 `
  -e secret "<base64 32 bytes>" `
  com.example.wifiunlock.test/androidx.test.runner.AndroidJUnitRunner
```

The second one writes a real entry to `pairing.json`, so it is opt-in: without
`pcIp` and `secret` it prints a note and passes.

## Build the Release (one command)

From the repository root, **no admin required**:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ".\scripts\build-release.ps1" -Version 2.1.0
```

The script:

1. Publishes the service and credential provider framework-dependent (COM hosting
   does not support self-contained builds) into `build\payload`.
2. Downloads and bundles the .NET 8 runtime installer (cached in `build\`).
3. Zips that folder as `BtUnlockManager\Payload\payload.zip` and embeds it into
   the control panel as an `EmbeddedResource`.
4. Publishes `PhoneUnlock.exe` as a single-file, self-contained, compressed exe.
5. Copies the Android APK from (first match wins):
   `$env:PHONEUNLOCK_APK`, `android\app\build\outputs\apk\debug\app-debug.apk`,
   `android\app\build\outputs\apk\release\app-release.apk`,
   `D:\Android_Projects\WifiUnlock\app\build\outputs\apk\debug\app-debug.apk`.
6. Writes `SHA256SUMS.txt` and `PhoneUnlock-<version>.zip`.

Output in `dist\`:

```
PhoneUnlock.exe           ~96 MB   control panel + embedded payload
PhoneUnlock.apk           ~6 MB    Android app
PhoneUnlock-2.1.0.zip     ~96 MB   both files + checksums
SHA256SUMS.txt
```

Pass `-Apk "D:\path\app-debug.apk"` to choose the APK explicitly, or `-NoZip` to
skip the convenience archive. The build warns if the APK name contains
`unsigned`, because that APK will not install.

### Publish to GitHub Releases

`dist\` and `build\` are gitignored, so the binaries are uploaded as release
assets, not committed. Once, from the repository root:

```powershell
git init
git add .
git commit -m "Phone Unlock control panel + release pipeline"
git branch -M main
git remote add origin https://github.com/<you>/<repo>.git
git push -u origin main
```

Then tag and publish:

```powershell
git tag v2.1.0
git push origin v2.1.0

gh release create v2.1.0 `
  .\dist\PhoneUnlock.exe `
  .\dist\PhoneUnlock.apk `
  .\dist\PhoneUnlock-2.1.0.zip `
  .\dist\SHA256SUMS.txt `
  --title "Phone Unlock 2.1.0" `
  --notes "Control panel exe + Android APK."
```

Without `gh`, use **Releases → Draft a new release → attach binaries** on the
GitHub website. Reinstalling later is just downloading those same two files.

`.github/workflows/release.yml` in the source tree automates this: pushing a
`v*` tag (or running the workflow manually) builds on `windows-latest` and
attaches the assets. The workflow picks up an APK if one exists at
`android\app\build\outputs\apk\debug\app-debug.apk` in the repository; otherwise
it publishes the exe and zip, and you can add the APK to the release by hand.

> Pushing a workflow file needs a GitHub token with the `workflow` scope. If
> `git push` is rejected with *"refusing to allow an OAuth App to create or
> update workflow"*, run `gh auth refresh -s workflow` once, then push again.
> The file is intentionally left out of the first push; everything else works
> without it.

## Daily Use

1. Lock the PC with `Win + L`.
2. Open **Phone Unlock** on the phone.
3. Pick the PC under **Your PCs** (only needed when more than one is paired) and
   check the transport, for example `Via Bluetooth · MY-PC`.
4. Tap **Unlock PC** and confirm with your fingerprint.
5. The lock-screen tile changes to `Unlocking...` and Windows signs you in.

The lock-screen tile (`📱 Phone Unlock`) shows:

| Tile text | Meaning |
|-----------|---------|
| `Waiting for phone connection...` | Service is up, no phone connected |
| `Phone connected! Waiting for signal...` | A phone reached the PC |
| `Via Wi-Fi` / `Via Bluetooth` | Which transport delivered the connection |
| `Unlocking...` | Fresh signal received, submitting credentials |

A signal is only honoured while it is **fresh (90 seconds)**. Stale signals are deleted, so a leftover file can never unlock the PC later.

## Diagnostics

```powershell
$exe = "C:\Program Files\BtUnlock\BluetoothUnlockService.exe"
& $exe --diagnose   # identity, credentials, pairing, sign-in policy, session, ports
& $exe --verify     # validate the stored password with LogonUser (no unlock)
& $exe --pair       # print a QR code and wait for the phone to confirm
& $exe --test       # crypto + credential-store round trip
& $exe --console    # run in the foreground for live debugging
```

> **Reading `--diagnose` while the service is running**
> `TCP 9735: in use — a service instance is listening` is expected, and
> `SeTcbPrivilege: n/a in this shell` is expected too: only LocalSystem holds
> that privilege, and the service runs as LocalSystem. The service's startup log
> records `SeTcbPrivilege enabled (SendSAS available).`
> Stop the service first (`Stop-Service BluetoothUnlockService`) if you want the
> diagnostic to probe the ports and publish its own Bluetooth record.

Logs:

| File | Contents |
|------|----------|
| `C:\ProgramData\BtUnlock\service.log` | Transport, discovery, unlock state machine |
| `C:\ProgramData\BtUnlock\credprovider.log` | Tile lifecycle, signal detection, credential submission |

Useful checks:

```powershell
Get-Service BluetoothUnlockService
Get-NetTCPConnection -LocalPort 9735 -State Listen
Get-Content "C:\ProgramData\BtUnlock\service.log" -Tail 40 -Wait
```

## Troubleshooting

### Service will not start

A leftover `BluetoothUnlockService.exe --console` holds port 9735 and locks the binary. The installer kills it, but you can do it yourself:

```powershell
Get-Process BluetoothUnlockService -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Service BluetoothUnlockService
Get-Content "C:\ProgramData\BtUnlock\service.log" -Tail 20
```

### The phone cannot find the PC over Wi-Fi

- Confirm the PC is actually connected to the phone's hotspot (not to another network).
- Confirm the firewall rules exist and allow `LocalSubnet`:
  ```powershell
  Get-NetFirewallRule -DisplayName "BtUnlock *"
  ```
- Some hotspots isolate clients. Turn off "client isolation" / "AP isolation" if your phone offers it.
- As a fallback, type the PC's IPv4 address manually in the app. Find it with `ipconfig` or in `--diagnose`.

### Bluetooth pairing fails with a page timeout

This is the expected failure when pairing is started from the phone: the PC rejects unknown devices. Pair from **Windows** instead (see above), with the phone's Bluetooth settings screen open. If a half-finished pairing exists, remove it on **both** devices and pair again.

### "It connected, then dropped after a few seconds"

That is normal. Windows closes an **idle** Bluetooth ACL link after roughly 25–30 seconds (`REMOTE_USER_TERMINATED_CONNECTION`). The **bond survives**; the app reconnects on demand when you unlock. It is not a lost pairing and needs no action.

### Unlock is rejected as `ALREADY_UNLOCKED`

The PC is already unlocked (for example, the screen woke without locking). Lock it with `Win + L` and try again.

### The unlock command arrives but Windows stays on the lock screen

Check, in order:

1. `--diagnose` shows `Require Ctrl+Alt+Del: not required (correct)`. If it says
   `REQUIRED`, the unlock **cannot** work — fix that first:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File ".\scripts\set-cad-requirement.ps1" -Mode not-required
   ```

2. `--diagnose` shows `Session state: LOCKED`.
3. `service.log` contains `Unlock: attempt 1 ...` and no
   `WARNING — DisableCAD=0` line.
4. `credprovider.log` shows a fresh signal and `GetCredentials() called!`.
5. The stored credentials are current — re-run `--setup`.
6. `Pairing:` in `--diagnose` lists your phone. If it says "no phone paired
   yet", re-run `--pair` and scan again.

### Nothing happens until I press Ctrl+Alt+Del myself

`DisableCAD` is `0` (or unset). With Ctrl+Alt+Del required, Windows does not
load credential providers until the keys are pressed, so no amount of
credential-provider or timing work can help. Set the policy:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ".\scripts\set-cad-requirement.ps1" -Mode not-required
```

`DisableCAD = 1` means the unlock tile is live immediately. The secure desktop
and the normal password tile still work; a single-user home PC loses nothing
meaningful, because Ctrl+Alt+Del protects against look-alike credential
prompts, not against someone who already holds your paired phone.

> The service no longer sends the SAS. `SendSAS` returned no error on this
> machine but was silently ignored by Windows, so the code that relied on it
> was removed rather than kept as a lie. See `docs/WHY-IT-STILL-FAILS.md`.

### The PC unlocks but the screen stays black

The unlock can succeed while the monitor is still asleep. The service launches a small helper in your session (`BluetoothUnlockService.exe --wake`) to wake the display; look for `Wake: helper started in session N.` in `service.log`. If it says `CreateProcessAsUser failed`, the helper could not start.

Windows' own event log is authoritative: `Microsoft-Windows-Winlogon/Operational`, events 811/812 with `Event = 5` mean the session unlocked. If those appear but the screen stayed dark, the problem is display wake, not authentication.

### The tile is missing from the lock screen

Re-run `scripts/install.ps1` elevated (it re-registers the provider), then sign out or restart. Check that the CLSID points at `BtUnlockCredProvider.comhost.dll`:

```powershell
Get-ItemProperty "HKLM:\SOFTWARE\Classes\CLSID\{E8F2B1C4-7A3D-4F5E-9B6C-1D2E3F4A5B6C}\InprocServer32"
```

## Protocol

Both transports carry the same encrypted, newline-terminated command.

| Parameter | Value |
|-----------|-------|
| Encryption (v2) | AES-256-GCM, random 96-bit nonce per message |
| Key (v2) | 32-byte secret from `pairing.json` (delivered in the `--pair` QR) |
| Encryption (v1, legacy) | AES-128-CBC / PKCS7 with a hardcoded key — accepted only for migration |
| Command (v2) | `v2.<base64(nonce‖ciphertext‖tag)>` wrapping `COMMAND\|<unix_ms>` |
| Commands | `UNLOCK_REQUEST`, `LOCK_REQUEST`, `PAIR_CONFIRM\|<ms>\|<phone-id>\|<phone-name>` |
| Framing | one command per line, terminated with `\n` |
| Replay protection | 60-second timestamp window; v2 also rejects reused nonces |
| Wi-Fi command channel | TCP 9735 |
| Wi-Fi discovery | UDP 9736 (`DISCOVER_UNLOCK_SERVER`) |
| Wi-Fi announcements | UDP 9737, every 5 seconds |
| Bluetooth | Classic RFCOMM, UUID `E8F2B1C4-7A3D-4F5E-9B6C-1D2E3F4A5B6C` |

Discovery reply / announcement:

```
UNLOCK_SERVER|<machine-name>|<LOCKED|UNLOCKED>|<bluetooth-address>
```

Status query `STATUS_QUERY` → `STATUS|<LOCKED|UNLOCKED>[|UNLOCK_IN_PROGRESS]`.

Replies: `UNLOCK_OK`, `UNLOCK_ACCEPTED`, `UNLOCK_REJECTED|<reason>`, `LOCK_OK`, `LOCK_FAIL|<reason>`, `FAIL|<reason>`.

## Security Notes

- **Credentials at rest**: DPAPI `LocalMachine`, readable only by SYSTEM/admin.
- **Per-PC secret**: a random 256-bit key in `pairing.json`, delivered once via the `--pair` QR. Commands are AES-256-GCM, so a captured frame cannot be forged or replayed.
- **Replay protection**: 60-second timestamp window plus a nonce cache for v2.
- **Signal lifetime**: 90 seconds; consumed and deleted after one use.
- **Network scope**: firewall rules are limited to the local subnet, and the Wi-Fi path runs over the phone's private hotspot.
- **Known limitations**:
  - The legacy v1 format (hardcoded key, no integrity check) is still accepted so an already-paired phone keeps working. Re-pair the phone to switch it to v2, then the legacy path can be deleted.
  - `pairing.json` is writable only by SYSTEM/Administrators, but standard users can still *read* it. On a single-user PC that is acceptable; to hide it completely run `icacls "C:\ProgramData\BtUnlock\pairing.json" /deny "Users:(R)"`.
- Bluetooth requires an existing bond, so a nearby stranger cannot send commands without pairing first.

## Uninstallation

**From the release exe:** run `PhoneUnlock.exe` as administrator and click
**Uninstall…**. Choose whether to delete the stored Windows password, pairing
secret and logs.

**From the source tree:** double-click **`Uninstall.cmd`** and accept the UAC
prompt, or run it from an elevated PowerShell:

```powershell
cd "D:\Unlock_PC-main\Unlock_PC"
powershell -NoProfile -ExecutionPolicy Bypass -File ".\scripts\uninstall.ps1"
```

This stops and deletes the service, kills stale console copies, unregisters the credential provider, removes the three firewall rules, optionally deletes stored credentials, and removes `C:\Program Files\BtUnlock`. Restart (or sign out) for the lock-screen change to take effect.

## Project Structure

```
Unlock_PC/
├── BluetoothUnlock.sln
├── Install.cmd                     # Double-click installer (self-elevates)
├── Uninstall.cmd                   # Double-click uninstaller (self-elevates)
├── dist/                           # Release output (gitignored)
├── .github/workflows/release.yml   # Tag → build → GitHub Release assets
├── BtUnlockManager/                # Windows control panel → PhoneUnlock.exe
│   ├── Program.cs                  # Elevation, single instance
│   ├── MainForm.cs                 # Status dashboard + actions
│   ├── PairPhoneForm.cs            # QR code + pairing poll
│   ├── CredentialsForm.cs          # DPAPI credential store/verify/delete
│   ├── LogViewerForm.cs            # service.log / credprovider.log
│   ├── ProcessRunnerForm.cs        # --diagnose / install.ps1 runner
│   ├── InstallForm.cs              # Install/uninstall progress
│   ├── SetupEngine.cs              # Embedded-payload installer (mirrors install.ps1)
│   └── AppInfo.cs                  # Paths, service status, log tailing
├── BluetoothUnlockService/
│   ├── Program.cs                  # Entry point: --setup / --test / --console / --diagnose
│   ├── Worker.cs                   # Service host + listener restart loops
│   ├── UnlockCoordinator.cs        # Unlock/lock state machine, signal, retries
│   ├── WifiUnlockListener.cs       # TCP 9735 + UDP 9736/9737
│   ├── BluetoothUnlockListener.cs  # RFCOMM server, SDP, newline framing
│   ├── RfcommSdp.cs                # SDP publish/deregister, local radio address
│   ├── LogonPolicy.cs              # DisableCAD policy (replaces SendSAS)
│   ├── SessionState.cs             # WTS lock detection
│   ├── LogonUiProbe.cs             # Is LogonUI.exe running, in which session
│   ├── PairingStore.cs             # pairing.json: device id + 256-bit secret, QR payload
│   ├── QrRenderer.cs               # Unicode/ANSI QR code for the terminal (ECC M)
│   ├── NetworkInfo.cs              # Best local IPv4 for the QR payload
│   ├── Privileges.cs               # Token privileges for the wake helper
│   ├── SessionLocker.cs            # Lock via the user session (session 0 is denied)
│   ├── UserSessionLauncher.cs      # Launch --wake / --lock inside the user's session
│   ├── CryptoHelper.cs             # AES decrypt + command validation
│   ├── CredentialStore.cs          # DPAPI credential storage
│   ├── UnlockSignaler.cs           # Signal file + named event
│   ├── MonitorWaker.cs             # Display power request + wake helper
│   └── FileLog.cs                  # service.log
├── BtUnlockCredProvider/
│   ├── BtUnlockProvider.cs         # Provider registration
│   └── BtUnlockTile.cs             # Lock-screen tile
├── docs/
│   └── WHY-IT-STILL-FAILS.md       # Evidence + prompt for the SAS failure
└── scripts/
    ├── build-release.ps1           # PhoneUnlock.exe + PhoneUnlock.apk + zip + hashes
    ├── install.ps1                 # Build, install, policy, firewall, register, start
    ├── set-cad-requirement.ps1     # DisableCAD = 0 (required) or 1 (not required)
    └── uninstall.ps1               # Remove everything
```
