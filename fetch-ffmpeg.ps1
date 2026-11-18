<#
.SYNOPSIS
    获取 FFmpeg 6.0 可执行文件（产品编码必需组件）。
.DESCRIPTION
    GitHub 单文件上限 100MB，而 ffmpeg.exe 为 125.6MB，无法随源码入库。
    在另一台电脑 clone 后，运行本脚本即可现场下载、校验并放回 tools 目录。
    脚本自带 7zr 解压器下载，无需预先安装任何工具。
.PARAMETER Force
    即使本地已有 ffmpeg.exe 也重新下载。
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\fetch-ffmpeg.ps1
#>
param([switch]$Force)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root       = Split-Path -Parent $MyInvocation.MyCommand.Path
$tools      = Join-Path $root 'ScreenRecorder\tools'
$target     = Join-Path $tools 'ffmpeg.exe'
$expectHash = 'B9FB1E0934E04B91B6924DE97EDD2E1B80FC5DFA0A7F08C32B9C5CCA3A12717B'
$archive    = 'ffmpeg-6.0-full_build.7z'
$ver        = '6.0'
$official   = "https://github.com/GyanD/codexffmpeg/releases/download/$ver/$archive"
$mirror     = "https://ghfast.top/$official"
$sevenzr    = 'https://www.7-zip.org/a/7zr.exe'

function Say($msg) { Write-Host "[ffmpeg] $msg" -ForegroundColor Cyan }

# 已存在且校验通过则跳过
if ((Test-Path $target) -and (-not $Force)) {
    $h = (Get-FileHash $target -Algorithm SHA256).Hash
    if ($h -eq $expectHash) {
        Say "ffmpeg.exe 已存在且 SHA256 校验通过，无需下载。"
        exit 0
    }
    Say "本地 ffmpeg.exe 校验不通过，将重新获取。"
}

$tmp = Join-Path $env:TEMP ("ffmpeg_fetch_" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
New-Item -ItemType Directory -Path $tools -Force | Out-Null

try {
    # 1) 下载轻量解压器 7zr.exe
    $unzip = Join-Path $tmp '7zr.exe'
    Say "下载解压器 7zr.exe ..."
    Invoke-WebRequest -Uri $sevenzr -OutFile $unzip -UseBasicParsing -TimeoutSec 180

    # 2) 下载 FFmpeg 压缩包：优先镜像（国内快），失败回退官方
    $archivePath = Join-Path $tmp $archive
    $downloaded = $false
    foreach ($url in @($mirror, $official)) {
        try {
            Say "下载 FFmpeg 6.0 压缩包（约 46.7MB）：$url"
            Invoke-WebRequest -Uri $url -OutFile $archivePath -UseBasicParsing -TimeoutSec 900
            if ((Get-Item $archivePath).Length -gt 40MB) { $downloaded = $true; break }
            Say "文件过小，视为失败，尝试下一个源。"
        } catch {
            Say "该源下载失败：$($_.Exception.Message)"
        }
    }
    if (-not $downloaded) { throw "所有下载源均失败，请检查网络或手动下载 $archive" }

    # 3) 解压出 ffmpeg.exe
    Say "解压 ffmpeg.exe ..."
    & $unzip e $archivePath 'ffmpeg.exe' "-o$tmp" -y | Out-Null
    $extracted = Join-Path $tmp 'ffmpeg.exe'
    if (-not (Test-Path $extracted)) { throw '压缩包中未找到 ffmpeg.exe' }

    # 4) 校验 SHA256，防止下错版本或被篡改
    $hash = (Get-FileHash $extracted -Algorithm SHA256).Hash
    if ($hash -ne $expectHash) {
        throw "SHA256 校验失败：`n  期望 $expectHash`n  实际 $hash`n请确认下载的是 FFmpeg 6.0 full build。"
    }
    Say "SHA256 校验通过：$hash"

    # 5) 放回产品目录
    Copy-Item $extracted $target -Force
    Say "完成：$target"
    Say "现在可直接运行 ScreenRecorder.exe（或重新打包绿色版）。"
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
