<#
.SYNOPSIS
    Runs the Shotwise app from the build output.

.PARAMETER Isolated
    Uses a throw-away data root (SHOTWISE_DATA_ROOT) under %TEMP% so settings, presets, logs and the library do not
    touch your real profile.

.EXAMPLE
    pwsh tools/run.ps1 -Isolated -- --background
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',
    [switch] $Isolated,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $AppArgs
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Get-ChildItem (Join-Path $root "src\Shotwise.App\bin\x64\$Configuration") -Filter 'Shotwise.exe' -Recurse | Select-Object -First 1
if (-not $exe) { throw "Shotwise.exe not found - run tools/build.ps1 -Configuration $Configuration first." }

if ($Isolated) {
    $env:SHOTWISE_DATA_ROOT = Join-Path ([IO.Path]::GetTempPath()) 'shotwise-dev-root'
    Write-Host "Data root: $env:SHOTWISE_DATA_ROOT"
}
& $exe.FullName @AppArgs
