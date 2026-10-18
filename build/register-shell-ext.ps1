<#
.SYNOPSIS
  Registers (or removes) Swivel's Windows 11 top-level Explorer menu for a Swivel install folder.

.DESCRIPTION
  Builds the NativeAOT Swivel.ShellExt.dll into the install folder, then registers the sparse
  package from src/Swivel.ShellExt/Package with that folder as its external location. Unsigned
  registration needs Developer Mode; signed MSIX registration is part of the installer (M4).
  Windows 10 is skipped: its classic menu verb is registered by Swivel itself.

.EXAMPLE
  powershell -File build/register-shell-ext.ps1                   # the Debug build
  powershell -File build/register-shell-ext.ps1 -InstallDir C:\Swivel
  powershell -File build/register-shell-ext.ps1 -Unregister
#>
param(
    [string]$InstallDir,
    [switch]$Unregister
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$packageName = 'Swivel.Shell'

Get-AppxPackage -Name $packageName | ForEach-Object {
    Write-Host "Removing $($_.PackageFullName)"
    Remove-AppxPackage -Package $_.PackageFullName
}
if ($Unregister) { return }

if ([Environment]::OSVersion.Version.Build -lt 22000) {
    Write-Host 'Windows 10: the classic "Spin with Swivel" verb covers this; nothing to register.'
    return
}
if (-not $InstallDir) { $InstallDir = Join-Path $root 'src\Swivel.App\bin\Debug\net10.0-windows' }
$InstallDir = (Resolve-Path $InstallDir).Path
if (-not (Test-Path (Join-Path $InstallDir 'Swivel.exe'))) { throw "No Swivel.exe in $InstallDir" }

# NativeAOT's linker lookup needs vswhere on PATH.
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
& $dotnet publish (Join-Path $root 'src\Swivel.ShellExt') -c Release -o (Join-Path $root 'artifacts\shellext') | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Building Swivel.ShellExt failed.' }
Copy-Item (Join-Path $root 'artifacts\shellext\Swivel.ShellExt.dll') $InstallDir -Force

# The loose package layout must stay put while registered, so it lives in LocalAppData.
$layout = Join-Path $env:LOCALAPPDATA 'Swivel\shell-package'
if (Test-Path $layout) { Remove-Item -Recurse -Force $layout }
New-Item -ItemType Directory -Force (Join-Path $layout 'Assets') | Out-Null
Copy-Item (Join-Path $root 'src\Swivel.ShellExt\Package\AppxManifest.xml') $layout
Copy-Item (Join-Path $root 'src\Swivel.App\Assets\*.png') (Join-Path $layout 'Assets')

Add-AppxPackage -Register (Join-Path $layout 'AppxManifest.xml') -ExternalLocation $InstallDir
Write-Host "Registered $packageName for $InstallDir"
Get-AppxPackage -Name $packageName | Select-Object Name, Version, InstallLocation | Format-List
