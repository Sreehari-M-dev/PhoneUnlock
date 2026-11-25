# ============================================================
# Phone Unlock - Uninstallation Script
# Run as Administrator in PowerShell
# ============================================================

#Requires -RunAsAdministrator

$ErrorActionPreference = "Stop"

$InstallDir   = "C:\Program Files\BtUnlock"
$ServiceName  = "BluetoothUnlockService"
$CredProvDll  = Join-Path $InstallDir "BtUnlockCredProvider.dll"
$ProviderGuid = "E8F2B1C4-7A3D-4F5E-9B6C-1D2E3F4A5B6C"

$FirewallRules = @(
    "BtUnlock TCP 9735",
    "BtUnlock UDP 9736",
    "BtUnlock UDP 9737"
)

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Phone Unlock - Uninstaller            " -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Step 1: Stop and remove the Windows Service
Write-Host "[1/5] Stopping and removing Windows Service..." -ForegroundColor Yellow
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
    Write-Host "  [OK] Service removed" -ForegroundColor Green
} else {
    Write-Host "  [WARN] Service not found (already removed?)" -ForegroundColor DarkYellow
}

# A console copy (--console) is not a service, so the block above misses it.
# It would keep port 9735 open and lock the binaries in the install directory.
$stale = Get-Process -Name $ServiceName -ErrorAction SilentlyContinue
if ($stale) {
    $stale | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    Write-Host "  [OK] Stopped $($stale.Count) console instance(s)" -ForegroundColor Green
}

# Step 2: Unregister Credential Provider
Write-Host "[2/5] Unregistering Credential Provider..." -ForegroundColor Yellow

try {
    if (Get-Module -ListAvailable -Name Lithnet.CredentialProvider.Management) {
        Import-Module Lithnet.CredentialProvider.Management
        Unregister-CredentialProvider -File $CredProvDll -ErrorAction SilentlyContinue
    }
}
catch {
    # Ignore errors from Lithnet module
}

# Also clean up manual registration
$RegPath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{$ProviderGuid}"
if (Test-Path $RegPath) {
    Remove-Item -Path $RegPath -Recurse -Force
}

$ClsidPath = "HKLM:\SOFTWARE\Classes\CLSID\{$ProviderGuid}"
if (Test-Path $ClsidPath) {
    Remove-Item -Path $ClsidPath -Recurse -Force
}

Write-Host "  [OK] Credential Provider unregistered" -ForegroundColor Green

# Step 3: Remove firewall rules
Write-Host "[3/5] Removing firewall rules..." -ForegroundColor Yellow
$removedRules = 0
foreach ($ruleName in $FirewallRules) {
    $rule = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
    if ($rule) {
        $rule | Remove-NetFirewallRule -ErrorAction SilentlyContinue
        $removedRules++
    }
}
if ($removedRules -gt 0) {
    Write-Host "  [OK] Removed $removedRules firewall rule(s)" -ForegroundColor Green
} else {
    Write-Host "  [WARN] No matching firewall rules found" -ForegroundColor DarkYellow
}

# Step 4: Ask about credentials
Write-Host "[4/5] Cleaning up stored credentials..." -ForegroundColor Yellow
$credPath = Join-Path $env:ProgramData "BtUnlock"
if (Test-Path $credPath) {
    $answer = Read-Host "  Delete stored credentials and logs? (Y/n)"
    if ($answer -ne "n") {
        Remove-Item -Path $credPath -Recurse -Force
        Write-Host "  [OK] Credentials and logs deleted" -ForegroundColor Green
    } else {
        Write-Host "  [WARN] Credentials kept at $credPath" -ForegroundColor DarkYellow
    }
} else {
    Write-Host "  [WARN] No stored credentials found" -ForegroundColor DarkYellow
}

# Step 5: Remove install directory
Write-Host "[5/5] Removing installed files..." -ForegroundColor Yellow
if (Test-Path $InstallDir) {
    Remove-Item -Path $InstallDir -Recurse -Force
    Write-Host "  [OK] Files removed" -ForegroundColor Green
} else {
    Write-Host "  [WARN] Install directory not found" -ForegroundColor DarkYellow
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Uninstallation complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "The Phone Unlock tile will no longer appear on the lock screen." -ForegroundColor DarkGray
Write-Host "You may need to restart your PC for the credential provider removal to take effect." -ForegroundColor DarkYellow
