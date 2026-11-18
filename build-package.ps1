# ScreenRecorder green-package builder (Phase 3 / WP3.4)
# Usage:
#   powershell -ExecutionPolicy Bypass -File build-package.ps1 -Build
#   powershell -ExecutionPolicy Bypass -File build-package.ps1
#
# Produces:
#   publish\ScreenRecorder_v1.0.0_green.zip
#   publish\ScreenRecorder_v1.0.0_green.zip.sha256.txt
#
# The staged folder contains ONLY runtime dependencies. Test recordings,
# personal settings.json and local logs are deliberately excluded.

param(
    [switch]$Build,
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
$src = Join-Path $root ("ScreenRecorder\bin\" + $Configuration + "\net8.0-windows")
$publish = Join-Path $root "publish"
$stageName = "ScreenRecorder_v" + $Version
$stage = Join-Path $publish $stageName
$zip = Join-Path $publish ("ScreenRecorder_v" + $Version + "_green.zip")

function Fail($msg) { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }

# A running instance locks ScreenRecorder.exe and breaks builds / copies.
if (Get-Process ScreenRecorder -ErrorAction SilentlyContinue) {
    Fail "ScreenRecorder is running. Close its window first, then retry."
}

if ($Build) {
    if (-not (Test-Path $dotnet)) { Fail "dotnet SDK not found at $dotnet" }
    Write-Host "Building $Configuration ..." -ForegroundColor Cyan
    & $dotnet build (Join-Path $root "ScreenRecorder\ScreenRecorder.csproj") `
        -c $Configuration --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail "dotnet build failed (exit $LASTEXITCODE)" }
}

if (-not (Test-Path $src)) { Fail "Build output not found: $src (try -Build)" }

# Exact runtime whitelist. Adding a DLL to the product requires updating this list.
$runtimeFiles = @(
    "ScreenRecorder.exe",
    "ScreenRecorder.dll",
    "ScreenRecorder.deps.json",
    "ScreenRecorder.runtimeconfig.json",
    "NAudio.dll",
    "NAudio.Asio.dll",
    "NAudio.Core.dll",
    "NAudio.Midi.dll",
    "NAudio.Wasapi.dll",
    "NAudio.WinForms.dll",
    "NAudio.WinMM.dll"
)

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

foreach ($f in $runtimeFiles) {
    $p = Join-Path $src $f
    if (-not (Test-Path $p)) { Fail "Missing runtime file: $p" }
    Copy-Item $p -Destination $stage -Force
}

# FFmpeg encoder (video + audio muxing). Must stay in tools\ next to the exe.
$ffmpeg = Join-Path $src "tools\ffmpeg.exe"
if (-not (Test-Path $ffmpeg)) { Fail "Missing encoder: $ffmpeg" }
New-Item -ItemType Directory -Path (Join-Path $stage "tools") -Force | Out-Null
Copy-Item $ffmpeg -Destination (Join-Path $stage "tools\ffmpeg.exe") -Force

# User guide. Deliberately named in ASCII to avoid PowerShell 5.1 encoding issues.
$readme = Join-Path $root "README.html"
if (Test-Path $readme) { Copy-Item $readme -Destination $stage -Force }

# Empty output folder, created fresh on first run.
New-Item -ItemType Directory -Path (Join-Path $stage "Videos") -Force | Out-Null

if (Test-Path $zip) { Remove-Item $zip -Force }
Write-Host "Compressing $stageName ..." -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip `
    -CompressionLevel Optimal -Force

$hash = Get-FileHash $zip -Algorithm SHA256
[IO.File]::WriteAllText($zip + ".sha256.txt", $hash.Hash + "  " + [IO.Path]::GetFileName($zip))

Write-Host "OK: $zip" -ForegroundColor Green
Write-Host ("    {0:N1} MB  SHA256={1}" -f ((Get-Item $zip).Length / 1MB), $hash.Hash) -ForegroundColor Green
Write-Host "    Stage: $stage" -ForegroundColor DarkGray
