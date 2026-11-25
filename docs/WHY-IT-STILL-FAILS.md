# Why the automatic unlock still fails (a self-contained prompt)

> **Status: root cause confirmed.** `--diagnose` reported
> `Require Ctrl+Alt+Del: REQUIRED` — i.e. `DisableCAD` was `0`/unset, so Windows
> never loaded the credential provider until the keys were pressed. `SendSAS`
> returned no error and was silently ignored, which is hypothesis H1/H2 below.
> The service no longer sends the SAS; the installer now enforces
> `DisableCAD = 1`. The rest of this file is kept as the evidence record.

Paste everything below the line into any AI/engineer. It contains the
architecture, the verified evidence, and the exact contradiction that needs
explaining. **Do not propose rewriting the service in C++ or adding a named
pipe IPC — the service already runs as LocalSystem and already calls
`SendSAS`.**

---

## Goal

Android → Windows 11 Home fingerprint unlock that is fully automatic: the PC
must reach the desktop with **no keyboard or mouse input at all**, while
`DisableCAD = 0` (Windows still asks for Ctrl+Alt+Del at the lock screen).

## Existing implementation (already working, do not redesign)

Two C# components, both already installed:

1. **`BluetoothUnlockService`** — .NET 8 Windows service, `LocalSystem`,
   `start= auto`, session 0. Listens on TCP 9735 (phone hotspot) and Bluetooth
   RFCOMM (fallback). On an authenticated unlock command it:
   - writes `C:\ProgramData\BtUnlock\unlock.signal` (Unix-ms timestamp) and sets
     `Global\BtUnlockEvent`;
   - sets `HKLM\...\Authentication\LogonUI\LastLoggedOnProvider` to the
     credential provider GUID;
   - enables `SeTcbPrivilege` in its own token and calls `SendSAS(FALSE)`
     (P/Invoke into `sas.dll`);
   - holds a kernel display power request (`PowerRequestDisplayRequired`) and
     launches `BluetoothUnlockService.exe --wake` in the user's session via
     `WTSQueryUserToken` + `CreateProcessAsUser`;
   - polls `WTSQuerySessionInformation(WTS_INFO_EX)` for the real lock state.

2. **`BtUnlockCredProvider`** — a credential provider DLL registered under
   `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential
   Providers\{E8F2B1C4-...}`. It shows a tile on the lock screen. A watcher
   thread polls `unlock.signal`; when a fresh signal appears it sets the
   auto-logon flag, returns the DPAPI-protected password from
   `C:\ProgramData\BtUnlock\credentials.dat`, and LogonUI logs the user in.

Registry state: `SoftwareSASGeneration = 3`, `DisableCAD = 0`, account
`my-pc\sreeh` (Microsoft Account, `PrincipalSource: MicrosoftAccount`).

## Verified facts (from logs, not assumptions)

| Check | Result |
| --- | --- |
| Service identity | `Running as SYSTEM=True` |
| SAS privilege | `SeTcbPrivilege enabled (SendSAS available).` |
| Stored password | `--verify` → `✓ Windows accepted the stored password for my-pc\sreeh.` (via `LogonUser`) |
| `SendSAS` call | `SAS: Ctrl+Alt+Del sent (privileged=True, policy=1).` — no Win32 error |
| Credential provider | `FRESH SIGNAL DETECTED` → `GetCredentials() called!` → `Submitting credentials for my-pc\sreeh` (~120 ms) |
| Winlogon operational | 811/812 with `Event = 5` (unlock) at `00:11:26.400`, i.e. **57 ms after** the credential submission |

Representative `service.log`:

```
00:11:26.087  Unlock: attempt 1 starting (locked=True, flags=0, LogonUI: pid 1234 session 1).
00:11:26.089  Signal: wrote unlock.signal
00:11:26.092  SAS: Ctrl+Alt+Del sent (privileged=True, policy=1).
00:11:26.655  Unlock: succeeded on attempt 1.
```

Representative `credprovider.log`:

```
00:11:04.491  OnSelectedShouldAutoLogon called. signaled=False
00:11:26.224  *** FRESH SIGNAL DETECTED ***
00:11:26.343  GetCredentials: Submitting credentials for my-pc\sreeh
```

## The symptom (what the human actually sees)

1. Lock with `Win+L`. The screen shows **“Press Ctrl+Alt+Del”**.
2. Unlock from the phone. The phone reports *command processed*.
3. **Nothing changes on screen.** The “Press Ctrl+Alt+Del” screen stays.
4. The human presses **Ctrl+Alt+Del manually**.
5. *Then* it unlocks by itself in well under a second (the credential provider
   submits the stored password; no password is typed).

So: the phone → service → credential-provider → password chain works. The only
broken link is step 2→3: the programmatic SAS does not remove the
“Press Ctrl+Alt+Del” screen.

## The contradiction to resolve

Winlogon logged an unlock event at `00:11:26.400` while the human reports the
CAD screen was still displayed until they pressed the key themselves. Either:

- **H1 — `SendSAS` is a silent no-op on this machine.** The call returns no
  error but Windows ignores it. Candidate causes: policy not honoured until
  reboot; the calling thread is not the active console session; Win11 Home
  build-specific behaviour; `sas.dll` resolved but the service token is
  filtered.
- **H2 — LogonUI only loads its credential providers after the SAS.** The
  provider therefore cannot submit until the human presses the key, and the
  `Event = 5` record came from that manual path.
- **H3 — the unlock really did happen automatically** and the monitor stayed
  dark; the human pressed Ctrl+Alt+Del merely to wake the display, and no
  second unlock event was logged because the session was already unlocked.
- **H4 — the provider submits, but LogonUI discards the credentials** because
  it is still on the CAD desktop.

## Decisive experiment already wired in

`BluetoothUnlockService.exe --sas` drops `C:\ProgramData\BtUnlock\sas.request`;
the running service consumes it and calls `SendSAS(FALSE)` in its own
LocalSystem token, logging:

```
SAS test: before (locked=True, flags=0, policy=3, LogonUI: pid ... session 1).
SAS: Ctrl+Alt+Del sent (privileged=True, policy=3).
SAS test: sent=True, after (locked=False, flags=1, LogonUI: pid ... session 1).
```

This removes the phone, both transports and the credential provider from the
experiment. If the “Press Ctrl+Alt+Del” screen does **not** disappear when
`--sas` runs, H1 is proven and no retry/timing change will fix it.

## Constraints

- Windows 11 Home, x64. No `gpedit.msc`; registry/PowerShell only.
- Single interactive user; Microsoft Account logon.
- The C# service + credential provider must be kept.
- The assistant doing the analysis has **no Administrator rights** on the
  machine, so it cannot install, restart services, or run a real lock/unlock
  test itself.

## Questions

1. Which hypothesis (H1–H4) does the evidence support, and what single
   observation would settle it?
2. If `SendSAS(FALSE)` from a LocalSystem service is a genuine no-op on Windows
   11 Home, is there **any** supported way to dismiss the Ctrl+Alt+Del screen
   without user input? If not, say so plainly.
3. If the answer is “not possible with `DisableCAD = 0`”, state the trade-off of
   `DisableCAD = 1` (no CAD screen at all → the credential provider tile is
   live immediately → no SAS needed) and whether it weakens security for a
   single-user home PC.
4. Give the minimal change that makes the flow automatic, and the exact log
   line that proves it worked.
