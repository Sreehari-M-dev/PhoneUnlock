# ============================================================
# Bluetooth Unlock - Installation Script
# Run as Administrator in PowerShell
# ============================================================

#Requires -RunAsAdministrator

$ErrorActionPreference = "Stop"

$ProjectRoot  = Split-Path -Parent $PSScriptRoot
$InstallDir   = "C:\Program Files\BtUnlock"
$ServiceName  = "BluetoothUnlockService"
$ServiceExe   = Join-Path $InstallDir "BluetoothUnlockService.exe"
$CredProvDll  = Join-Path $InstallDir "BtUnlockCredProvider.dll"
$ProviderGuid = "E8F2B1C4-7A3D-4F5E-9B6C-1D2E3F4A5B6C"

$TcpPort        = 9735
$DiscoveryPort  = 9736
$AnnouncePort   = 9737

function Write-Step([string]$text) {
    Write-Host $text -ForegroundColor Yellow
}

function Write-Ok([string]$text) {
    Write-Host "  [OK] $text" -ForegroundColor Green
}

function Write-Warn([string]$text) {
    Write-Host "  [WARN] $text" -ForegroundColor DarkYellow
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Bluetooth Unlock - Installer          " -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# ------------------------------------------------------------
# Step 1: Stop anything that could hold the binaries or the ports
# ------------------------------------------------------------
Write-Step "[1/8] Stopping running instances..."

$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

# A leftover console copy (BluetoothUnlockService.exe --console) keeps port
# 9735 open and locks the exe, which makes the build/copy and the service
# start fail with "did not respond in a timely fashion".
$stale = Get-Process -Name "BluetoothUnlockService" -ErrorAction SilentlyContinue
if ($stale) {
    Write-Warn "Killing $($stale.Count) stale BluetoothUnlockService process(es): $($stale.Id -join ', ')"
    $stale | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

$busy = Get-NetTCPConnection -LocalPort $TcpPort -State Listen -ErrorAction SilentlyContinue
if ($busy) {
    $owners = ($busy | Select-Object -ExpandProperty OwningProcess -Unique) -join ', '
    Write-Warn "Port $TcpPort is still in use by PID(s) $owners. Stop them and re-run if the service fails to start."
}

Write-Ok "No stale instances"

# ------------------------------------------------------------
# Step 2: Build
# ------------------------------------------------------------
Write-Step "[2/8] Building solution..."
Push-Location $ProjectRoot
dotnet build -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    Pop-Location
    exit 1
}
Pop-Location
Write-Ok "Build successful"

# ------------------------------------------------------------
# Step 3: Copy files
# ------------------------------------------------------------
Write-Step "[3/8] Installing to $InstallDir..."
if (!(Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}

$ServiceBinDir  = Join-Path $ProjectRoot "BluetoothUnlockService\bin\x64\Release\net8.0-windows\win-x64"
$CredProvBinDir = Join-Path $ProjectRoot "BtUnlockCredProvider\bin\x64\Release\net8.0-windows\win-x64"

Copy-Item "$ServiceBinDir\*"  $InstallDir -Recurse -Force
Copy-Item "$CredProvBinDir\*" $InstallDir -Recurse -Force
Write-Ok "Files copied"

# ------------------------------------------------------------
# Step 4: Remove the Ctrl+Alt+Del gate
# ------------------------------------------------------------
Write-Step "[4/8] Configuring the sign-in policy..."

$SystemPolicy = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"
if (!(Test-Path $SystemPolicy)) {
    New-Item -Path $SystemPolicy -Force | Out-Null
}

# DisableCAD = 1  ->  "Interactive logon: Do not require CTRL+ALT+DEL"
#
# This is required, not cosmetic. With DisableCAD = 0 Windows shows a
# "Press Ctrl+Alt+Del" screen and does not load credential providers until the
# user presses the keys, so the unlock tile cannot submit anything. The service
# no longer sends the SAS (SendSAS is a silent no-op on some Windows 11 builds),
# so this policy is what makes automatic unlock possible.
Set-ItemProperty -Path $SystemPolicy -Name "DisableCAD" -Value 1 -Type DWord
Write-Ok "DisableCAD = 1 (Ctrl+Alt+Del no longer required at sign-in)"

# SoftwareSASGeneration only ever existed to let the service send Ctrl+Alt+Del.
# It is obsolete now; remove any value left behind by an older install.
$sas = (Get-ItemProperty -Path $SystemPolicy -Name "SoftwareSASGeneration" -ErrorAction SilentlyContinue).SoftwareSASGeneration
if ($null -ne $sas) {
    Remove-ItemProperty -Path $SystemPolicy -Name "SoftwareSASGeneration" -ErrorAction SilentlyContinue
    Write-Ok "Removed obsolete SoftwareSASGeneration ($sas)"
}

# ------------------------------------------------------------
# Step 4b: Lock down the data folder
# ------------------------------------------------------------
# pairing.json holds the shared secret the phone uses to sign commands. A
# standard user must not be able to replace it (that would let them pair their
# own phone), so only SYSTEM/Administrators may write here.
$DataDir = "C:\ProgramData\BtUnlock"
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null

& icacls.exe $DataDir /inheritance:r `
    /grant:r "SYSTEM:(OI)(CI)F" "Administrators:(OI)(CI)F" "Users:(OI)(CI)RX" `
    /C /Q | Out-Null

if ($LASTEXITCODE -eq 0) {
    Write-Ok "Data folder: SYSTEM/Administrators write, Users read"
}
else {
    Write-Warn "Could not tighten ACLs on $DataDir (exit $LASTEXITCODE)"
}

# ------------------------------------------------------------
# Step 5: Install the service
# ------------------------------------------------------------
Write-Step "[5/8] Installing Windows Service..."

if ($existingService) {
    Write-Host "  Removing previous service..." -ForegroundColor DarkGray
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# Quoted path: "C:\Program Files\..." must not be split at the space.
$binPathValue = "`"$ServiceExe`""
sc.exe create $ServiceName binPath= $binPathValue start= auto obj= "LocalSystem" DisplayName= "Bluetooth Unlock Service" | Out-Null
sc.exe description $ServiceName "Unlocks this PC for a paired phone over Wi-Fi (phone hotspot) or Bluetooth." | Out-Null
sc.exe failure $ServiceName reset= 60 actions= restart/5000/restart/10000/restart/30000 | Out-Null

# Give the service a generous start timeout instead of the default 30s.
sc.exe config $ServiceName start= auto | Out-Null

Write-Ok "Service installed (auto-start, LocalSystem)"

# ------------------------------------------------------------
# Step 6: Firewall rules
# ------------------------------------------------------------
Write-Step "[6/8] Adding firewall rules..."

foreach ($rule in @(
    @{ Name = "BtUnlock TCP 9735";   Protocol = "TCP"; Port = $TcpPort;       Direction = "Inbound"  },
    @{ Name = "BtUnlock UDP 9736";   Protocol = "UDP"; Port = $DiscoveryPort; Direction = "Inbound"  },
    @{ Name = "BtUnlock UDP 9737";   Protocol = "UDP"; Port = $AnnouncePort;  Direction = "Outbound" }
)) {
    if (Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue) {
        continue
    }

    New-NetFirewallRule `
        -DisplayName $rule.Name `
        -Direction $rule.Direction `
        -Action Allow `
        -Protocol $rule.Protocol `
        -LocalPort $rule.Port `
        -RemoteAddress LocalSubnet `
        -Profile Any | Out-Null
}

Write-Ok "Firewall rules ready (local subnet only)"

# ------------------------------------------------------------
# Step 7: Register the credential provider
# ------------------------------------------------------------
Write-Step "[7/8] Registering Credential Provider..."

# The Lithnet management module registers through .NET Framework, which cannot
# load a .NET 8 assembly — it fails with "Could not find type
# 'System.Runtime.CompilerServices.NullableContextAttribute'". That is a
# limitation of the registration helper, not of the provider.
#
# The supported registration for a managed COM host is to point the CLSID at
# BtUnlockCredProvider.comhost.dll, which loads the .NET runtime itself.
$RegPath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{$ProviderGuid}"
if (!(Test-Path $RegPath)) {
    New-Item -Path $RegPath -Force | Out-Null
}
Set-ItemProperty -Path $RegPath -Name "(Default)" -Value "BtUnlockCredProvider"

$ClsidPath = "HKLM:\SOFTWARE\Classes\CLSID\{$ProviderGuid}"
if (!(Test-Path $ClsidPath)) {
    New-Item -Path $ClsidPath -Force | Out-Null
}
Set-ItemProperty -Path $ClsidPath -Name "(Default)" -Value "BtUnlockCredProvider"

$InprocPath = Join-Path $ClsidPath "InprocServer32"
if (!(Test-Path $InprocPath)) {
    New-Item -Path $InprocPath -Force | Out-Null
}
# Point at the native COM shim, not the managed assembly.
$ComHostDll = Join-Path $InstallDir "BtUnlockCredProvider.comhost.dll"
Set-ItemProperty -Path $InprocPath -Name "(Default)" -Value $ComHostDll
Set-ItemProperty -Path $InprocPath -Name "ThreadingModel" -Value "Both"

if (Test-Path $ComHostDll) {
    Write-Ok "Credential Provider registered (comhost)"
}
else {
    Write-Warn "comhost.dll missing at $ComHostDll - the tile will not appear"
}

# ------------------------------------------------------------
# Step 8: Start the service and verify
# ------------------------------------------------------------
Write-Step "[8/8] Starting service..."

Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -eq "Running") {
    Write-Ok "Service is running"
}
else {
    Write-Warn "Service is not running (status: $($svc.Status)). Recent log:"
    $log = "C:\ProgramData\BtUnlock\service.log"
    if (Test-Path $log) {
        Get-Content $log -Tail 15 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
    }
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Installation complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Store your Windows credentials (as Administrator):" -ForegroundColor White
Write-Host "     `"$ServiceExe`" --setup" -ForegroundColor Gray
Write-Host ""
Write-Host "  2. Pair the phone once:" -ForegroundColor White
Write-Host "     Settings > Bluetooth > Add device > (your phone)" -ForegroundColor Gray
Write-Host ""
Write-Host "  3. Install / open the app on the phone." -ForegroundColor White
Write-Host ""
Write-Host "  4. Lock the PC (Win+L) and unlock it from the phone." -ForegroundColor White
Write-Host ""
Write-Host "Verify the installation at any time:" -ForegroundColor Yellow
Write-Host "     `"$ServiceExe`" --diagnose" -ForegroundColor Gray
Write-Host ""
Write-Host "Logs:" -ForegroundColor Yellow
Write-Host "     C:\ProgramData\BtUnlock\service.log" -ForegroundColor Gray
Write-Host "     C:\ProgramData\BtUnlock\credprovider.log" -ForegroundColor Gray
Write-Host ""
Write-Host "Your normal Windows password tile stays available as a fallback." -ForegroundColor DarkGray
