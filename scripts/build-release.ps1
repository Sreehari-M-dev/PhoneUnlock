# ============================================================
# Phone Unlock - release builder
#
# Produces two downloadable files in dist\:
#   PhoneUnlock.exe   self-contained control panel + embedded service payload
#   PhoneUnlock.apk   the Android app (when an APK can be found)
#
# Usage (from the repository root, no admin needed):
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -Version 2.1.0
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -Apk "D:\path\app-debug.apk"
# ============================================================

param(
    [string]$Version = "2.1.0",
    [string]$Apk = "",
    [switch]$NoZip
)

$ErrorActionPreference = "Stop"

$Root       = Split-Path -Parent $PSScriptRoot
$Dist       = Join-Path $Root "dist"
$BuildDir   = Join-Path $Root "build\payload"
$ManagerDir = Join-Path $Root "BtUnlockManager"
$PayloadZip = Join-Path $ManagerDir "Payload\payload.zip"

$Version = $Version.TrimStart("v", "V")
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = "dev" }

function Write-Step([string]$Text) {
    Write-Host ""
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Phone Unlock - release $Version       " -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# ------------------------------------------------------------
# 1. Publish the service + credential provider (framework-dependent)
#
# The credential provider is an in-process COM server inside LogonUI. COM
# hosting does not support self-contained deployments, so both components are
# published framework-dependent and the installer adds the .NET 8 runtime when
# it is missing (see step 2). The control panel itself is still a single
# self-contained exe.
# ------------------------------------------------------------
Write-Step "Publishing the service and credential provider"

Remove-Item $BuildDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $BuildDir | Out-Null

Invoke-Dotnet @(
    "publish", (Join-Path $Root "BluetoothUnlockService\BluetoothUnlockService.csproj"),
    "-c", "Release", "-r", "win-x64", "--self-contained", "false",
    "-p:Version=$Version", "-p:PublishReadyToRun=false",
    "-p:DebugType=None", "-p:DebugSymbols=false",
    "-o", $BuildDir
)

Invoke-Dotnet @(
    "publish", (Join-Path $Root "BtUnlockCredProvider\BtUnlockCredProvider.csproj"),
    "-c", "Release", "-r", "win-x64", "--self-contained", "false",
    "-p:Version=$Version", "-p:PublishReadyToRun=false",
    "-p:DebugType=None", "-p:DebugSymbols=false",
    "-o", $BuildDir
)

# Symbols are not needed on a user machine.
Get-ChildItem $BuildDir -Filter *.pdb -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue

if (!(Test-Path (Join-Path $BuildDir "BtUnlockCredProvider.comhost.dll"))) {
    throw "BtUnlockCredProvider.comhost.dll was not published."
}

# ------------------------------------------------------------
# 1b. Bundle the .NET 8 runtime installer (only needed once per PC)
# ------------------------------------------------------------
Write-Step "Bundling the .NET 8 runtime installer"

$RuntimeCache = Join-Path $Root "build\dotnet-runtime-win-x64.exe"
$RuntimeUrl   = "https://aka.ms/dotnet/8.0/dotnet-runtime-win-x64.exe"

if (!(Test-Path $RuntimeCache)) {
    try {
        Write-Host "    Downloading $RuntimeUrl" -ForegroundColor DarkGray
        Invoke-WebRequest -Uri $RuntimeUrl -OutFile $RuntimeCache -UseBasicParsing
    }
    catch {
        Write-Warning "Could not download the .NET 8 runtime installer: $($_.Exception.Message)"
        Write-Warning "The release will still work where .NET 8 is already installed."
    }
}

if (Test-Path $RuntimeCache) {
    Copy-Item $RuntimeCache (Join-Path $BuildDir "dotnet-runtime-win-x64.exe") -Force
    Write-Host "    dotnet-runtime-win-x64.exe: $([math]::Round((Get-Item $RuntimeCache).Length / 1MB, 1)) MB" -ForegroundColor DarkGray
}

# ------------------------------------------------------------
# 2. Zip the payload and embed it into the control panel
# ------------------------------------------------------------
Write-Step "Packing the payload into PhoneUnlock.exe"

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $PayloadZip) | Out-Null
Remove-Item $PayloadZip -Force -ErrorAction SilentlyContinue

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($BuildDir, $PayloadZip)

$payloadMb = [math]::Round((Get-Item $PayloadZip).Length / 1MB, 1)
Write-Host "    payload.zip: $payloadMb MB" -ForegroundColor DarkGray

Write-Step "Publishing the control panel (single file, self-contained)"

Remove-Item $Dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $Dist | Out-Null

Invoke-Dotnet @(
    "publish", (Join-Path $ManagerDir "BtUnlockManager.csproj"),
    "-c", "Release", "-r", "win-x64", "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:Version=$Version",
    "-p:DebugType=None", "-p:DebugSymbols=false",
    "-o", $Dist
)

Get-ChildItem $Dist -Filter *.pdb -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

$exe = Join-Path $Dist "PhoneUnlock.exe"
if (!(Test-Path $exe)) {
    throw "PhoneUnlock.exe was not produced in $Dist"
}

$exeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "    PhoneUnlock.exe: $exeMb MB" -ForegroundColor DarkGray

# A single-file publish can leave the referenced project's *.runtimeconfig.json
# and *.deps.json behind. Ship only the control panel.
Get-ChildItem $Dist -File |
    Where-Object { $_.Name -ne "PhoneUnlock.exe" } |
    Remove-Item -Force

# ------------------------------------------------------------
# 3. Bundle the Android APK
# ------------------------------------------------------------
Write-Step "Bundling the Android app"

if ([string]::IsNullOrWhiteSpace($Apk)) {
    $candidates = @(
        $env:PHONEUNLOCK_APK,
        (Join-Path $Root "android\app\build\outputs\apk\debug\app-debug.apk"),
        (Join-Path $Root "android\app\build\outputs\apk\release\app-release.apk"),
        "D:\Android_Projects\WifiUnlock\app\build\outputs\apk\debug\app-debug.apk",
        "D:\Android_Projects\WifiUnlock\app\build\outputs\apk\release\app-release.apk"
    )

    $Apk = $candidates |
        Where-Object { $_ -and (Test-Path $_) } |
        Select-Object -First 1
}

if ($Apk -and (Test-Path $Apk)) {
    if ((Split-Path -Leaf $Apk) -match "unsigned") {
        Write-Warning "The selected APK looks unsigned and will not install. Build assembleDebug or sign the release APK."
    }

    Copy-Item $Apk (Join-Path $Dist "PhoneUnlock.apk") -Force
    $apkMb = [math]::Round((Get-Item (Join-Path $Dist "PhoneUnlock.apk")).Length / 1MB, 1)
    Write-Host "    PhoneUnlock.apk: $apkMb MB (from $Apk)" -ForegroundColor DarkGray
}
else {
    Write-Warning "No APK bundled. Pass -Apk <path>, build the Android app, or set PHONEUNLOCK_APK."
}

# ------------------------------------------------------------
# 4. Checksums and an optional convenience ZIP
# ------------------------------------------------------------
Write-Step "Writing checksums"

$checksumPath = Join-Path $Dist "SHA256SUMS.txt"
$lines = foreach ($file in Get-ChildItem $Dist -File | Where-Object { $_.Name -ne "SHA256SUMS.txt" }) {
    $hash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash
    "$hash  $($file.Name)"
}
$lines | Set-Content -Path $checksumPath -Encoding ASCII
$lines | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }

if (!$NoZip -and (Test-Path (Join-Path $Dist "PhoneUnlock.apk"))) {
    $zip = Join-Path $Dist "PhoneUnlock-$Version.zip"
    $tempZip = Join-Path $Root "build\PhoneUnlock-$Version.zip"
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    Remove-Item $tempZip -Force -ErrorAction SilentlyContinue

    # Build the archive outside dist so it cannot try to include itself.
    [System.IO.Compression.ZipFile]::CreateFromDirectory($Dist, $tempZip)
    Move-Item $tempZip $zip -Force

    Write-Host "    $([System.IO.Path]::GetFileName($zip)): $([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  Release ready in $Dist" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Get-ChildItem $Dist -File |
    Sort-Object Name |
    Select-Object Name, @{ Name = "MB"; Expression = { [math]::Round($_.Length / 1MB, 1) } } |
    Format-Table -AutoSize
