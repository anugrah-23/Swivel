<#
.SYNOPSIS
  Downloads Swivel's pinned engine binaries into engines/ and verifies them against engines.lock.json.

.DESCRIPTION
  Engines ship as separate executables (FFmpeg LGPL, Pandoc GPL, 7-Zip LGPL) so their licences stay
  separate from Swivel's MIT code. The first fetch of a new URL records its SHA-256 in the lock file
  (commit it); later fetches must match it exactly.

.EXAMPLE
  pwsh build/fetch-engines.ps1          # fetch anything missing
  pwsh build/fetch-engines.ps1 -Force   # re-download everything
#>
param([switch]$Force)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path $PSScriptRoot -Parent
$engines = Join-Path $root 'engines'
$lockPath = Join-Path $PSScriptRoot 'engines.lock.json'
$cache = Join-Path $env:TEMP 'swivel-engine-cache'
New-Item -ItemType Directory -Force $engines, $cache | Out-Null

$lock = @{}
if (Test-Path $lockPath) {
    (Get-Content $lockPath -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $lock[$_.Name] = $_.Value }
}

function Get-Pinned([string]$Url) {
    $file = Join-Path $cache ([IO.Path]::GetFileName(([Uri]$Url).AbsolutePath))
    if ($Force -or -not (Test-Path $file)) {
        Write-Host "Downloading $Url"
        Invoke-WebRequest -Uri $Url -OutFile $file -UseBasicParsing
    }
    $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($lock.ContainsKey($Url)) {
        if ($lock[$Url] -ne $hash) { throw "SHA-256 mismatch for $Url`n  expected $($lock[$Url])`n  got      $hash" }
    } else {
        Write-Host "  pinned new hash $hash"
        $lock[$Url] = $hash
    }
    return $file
}

function Expand-Fresh([string]$Zip) {
    $dir = Join-Path $cache ([IO.Path]::GetFileNameWithoutExtension($Zip))
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    Expand-Archive -Path $Zip -DestinationPath $dir
    return $dir
}

# FFmpeg: LGPL shared build only, no GPL encoders.
$ffmpegUrl = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-17-20-51/ffmpeg-n8.1.3-18-g46d899d7a0-win64-lgpl-shared-8.1.zip'
$target = Join-Path $engines 'ffmpeg'
if ($Force -or -not (Test-Path (Join-Path $target 'ffmpeg.exe'))) {
    $dir = Expand-Fresh (Get-Pinned $ffmpegUrl)
    $top = Get-ChildItem $dir -Directory | Select-Object -First 1
    New-Item -ItemType Directory -Force $target | Out-Null
    Get-ChildItem (Join-Path $top.FullName 'bin') | Where-Object Name -ne 'ffplay.exe' | Copy-Item -Destination $target -Force
    Copy-Item (Join-Path $top.FullName 'LICENSE.txt') (Join-Path $target 'LICENSE.txt') -Force
} else { [void](Get-Pinned $ffmpegUrl) }

# Pandoc: GPL, kept as its own executable.
$pandocUrl = 'https://github.com/jgm/pandoc/releases/download/3.12.1/pandoc-3.12.1-windows-x86_64.zip'
$target = Join-Path $engines 'pandoc'
if ($Force -or -not (Test-Path (Join-Path $target 'pandoc.exe'))) {
    $dir = Expand-Fresh (Get-Pinned $pandocUrl)
    $top = Get-ChildItem $dir -Directory | Select-Object -First 1
    New-Item -ItemType Directory -Force $target | Out-Null
    Get-ChildItem $top.FullName -File | Copy-Item -Destination $target -Force
} else { [void](Get-Pinned $pandocUrl) }

# 7-Zip's standalone 7zr.exe (LGPL) for creating .7z archives.
$sevenUrl = 'https://www.7-zip.org/a/7zr.exe'
$licenseUrl = 'https://www.7-zip.org/license.txt'
$target = Join-Path $engines '7zip'
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item (Get-Pinned $sevenUrl) (Join-Path $target '7zr.exe') -Force
Copy-Item (Get-Pinned $licenseUrl) (Join-Path $target 'License.txt') -Force

$ordered = [ordered]@{}
$lock.Keys | Sort-Object | ForEach-Object { $ordered[$_] = $lock[$_] }
$ordered | ConvertTo-Json | Set-Content $lockPath -Encoding utf8

$size = (Get-ChildItem $engines -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Engines ready in {0} ({1:N0} MB)" -f $engines, $size)
