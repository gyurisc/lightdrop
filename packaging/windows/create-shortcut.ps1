# Creates a Start Menu shortcut to `lightdrop ui`, pinnable to the taskbar.
#
# A shortcut rather than an installer: LightDrop is one executable that needs no installation, and
# this only records where the user already put it.
#
# Usage: .\create-shortcut.ps1 -Binary C:\tools\lightdrop.exe

param(
    [Parameter(Mandatory = $true)][string]$Binary
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Binary)) {
    throw "No executable at $Binary. Publish one first with: dotnet publish src/LightDrop.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true"
}

$Binary = (Resolve-Path -LiteralPath $Binary).Path
$linkPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'LightDrop.lnk'

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($linkPath)
$shortcut.TargetPath = $Binary
$shortcut.Arguments = 'ui'
$shortcut.WorkingDirectory = Split-Path -Parent $Binary
$shortcut.Description = 'Open the LightDrop page'
$shortcut.Save()

Write-Host "Created $linkPath"
Write-Host "It launches: $Binary ui"
