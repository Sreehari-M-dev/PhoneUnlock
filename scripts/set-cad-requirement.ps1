# ============================================================
# Bluetooth Unlock - Ctrl+Alt+Del requirement switch
#
#   .\set-cad-requirement.ps1 -Mode not-required   # unlock with no keypress
#   .\set-cad-requirement.ps1 -Mode required       # restore Windows default
#
# Run as Administrator. Takes effect immediately; sign out / lock again to
# pick it up in LogonUI.
# ============================================================

#Requires -RunAsAdministrator

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("required", "not-required")]
    [string]$Mode
)

$ErrorActionPreference = "Stop"

$SystemPolicy = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"

if (!(Test-Path $SystemPolicy)) {
    New-Item -Path $SystemPolicy -Force | Out-Null
}

switch ($Mode) {
    "not-required" {
        Set-ItemProperty -Path $SystemPolicy -Name "DisableCAD" -Value 1 -Type DWord
        Write-Host "  [OK] DisableCAD = 1" -ForegroundColor Green
        Write-Host "       Windows will no longer ask for Ctrl+Alt+Del." -ForegroundColor Gray
        Write-Host "       The unlock tile is available on the lock screen immediately," -ForegroundColor Gray
        Write-Host "       so the service does not need SendSAS at all." -ForegroundColor Gray
    }
    "required" {
        Set-ItemProperty -Path $SystemPolicy -Name "DisableCAD" -Value 0 -Type DWord
        Write-Host "  [OK] DisableCAD = 0" -ForegroundColor Green
        Write-Host "       Windows asks for Ctrl+Alt+Del again." -ForegroundColor Gray
        Write-Host "       The service must then send the SAS itself (see --sas)." -ForegroundColor Gray
    }
}

Write-Host ""
Write-Host "Lock with Win+L and test. No reboot is needed." -ForegroundColor Cyan
