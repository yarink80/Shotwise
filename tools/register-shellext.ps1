<# .SYNOPSIS
Registers the x64 Shotwise Explorer provider for the current user. Does not elevate.
#>
[CmdletBinding()]
param(
    [string]$DllPath = (Join-Path $PSScriptRoot '..\native\Shotwise.ShellExt\bin\x64\Release\Shotwise.ShellExt.dll'),
    [switch]$Unregister
)
$ErrorActionPreference = 'Stop'
$resolvedDll = (Resolve-Path -LiteralPath $DllPath).Path
$regsvr = Join-Path $env:WINDIR 'System32\regsvr32.exe'
if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) { $regsvr = Join-Path $env:WINDIR 'Sysnative\regsvr32.exe' }
$arguments = @('/s', '/n', '/i:user')
if ($Unregister) { $arguments += '/u' }
$arguments += ('"' + $resolvedDll + '"')
$process = Start-Process -FilePath $regsvr -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "Shell provider registration failed with exit code $($process.ExitCode)." }
Write-Output $(if ($Unregister) { 'Shotwise shell provider unregistered for current user.' } else { 'Shotwise shell provider registered for current user.' })
