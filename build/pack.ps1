<#
.SYNOPSIS
  Builds Swivel's installer with Velopack: artifacts\releases\Swivel-win-Setup.exe plus the full
  and delta .nupkg packages and releases.win.json that GitHub Releases serves as the update feed.

.EXAMPLE
  powershell -File build/pack.ps1                  # version from Swivel.App.csproj
  powershell -File build/pack.ps1 -Version 0.4.1
#>
param([string]$Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH" # NativeAOT linker lookup

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'src\Swivel.App\Swivel.App.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if (-not (Test-Path (Join-Path $root 'engines\ffmpeg\ffmpeg.exe'))) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'fetch-engines.ps1')
}

$publish = Join-Path $root 'artifacts\publish'
$releases = Join-Path $root 'artifacts\releases'
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }

& $dotnet publish (Join-Path $root 'src\Swivel.App') -c Release -r win-x64 --self-contained -o $publish "-p:Version=$Version" | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Publishing Swivel failed.' }

# The Windows 11 menu handler ships in the install folder; its sparse package is registered only
# when a signed Swivel.Shell.msix sits beside it (signing comes from the release pipeline).
& $dotnet publish (Join-Path $root 'src\Swivel.ShellExt') -c Release -o (Join-Path $root 'artifacts\shellext') | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Building Swivel.ShellExt failed.' }
Copy-Item (Join-Path $root 'artifacts\shellext\Swivel.ShellExt.dll') $publish
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') (Join-Path $publish 'licenses') -ErrorAction SilentlyContinue

$notesFile = Join-Path (Join-Path $root "docs/release-notes") "$($Version.Split('-')[0]).md"
$notes = if (Test-Path $notesFile) { @('--releaseNotes', $notesFile) } else { @() }

Push-Location $root
try {
    & $dotnet tool restore | Out-Null
    & $dotnet vpk pack --packId Swivel --packVersion $Version --packDir $publish --mainExe Swivel.exe `
        --packTitle Swivel --packAuthors 'Anugrah Chari V' --icon (Join-Path $root 'src\Swivel.App\Assets\swivel.ico') `
        --outputDir $releases @notes | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed.' }
} finally { Pop-Location }

Get-ChildItem $releases | Sort-Object LastWriteTime -Descending | Select-Object -First 6 Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table | Out-Host
