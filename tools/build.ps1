<#
.SYNOPSIS
    Builds the whole Shotwise solution, the native shell extension and (optionally) runs every test project.

.DESCRIPTION
    Restores and builds Shotwise.sln with node reuse disabled and one MSBuild node (keeps memory use low).
    -Native (implied by -Test) builds native\Shotwise.ShellExt (C++ IThumbnailProvider / IPropertyStore for .shotx) with the
    Visual Studio 2022 C++ build tools located via vswhere, in the selected configuration and in Release, and copies the
    Release DLL + Shotwise.propdesc next to the app output (src\Shotwise.App\bin\x64\<cfg>\...\win-x64).
    -Test runs every tests\*.Tests project in the solution (incl. tests\Shotwise.Platform.Tests, whose native tests load the shell
    extension DLL) and prints a per-project summary.

.EXAMPLE
    pwsh tools/build.ps1                       # Debug build of the solution
    pwsh tools/build.ps1 -Test                 # build + native + all tests
    pwsh tools/build.ps1 -Configuration Release -Native
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',
    [switch] $Native,
    [switch] $Test,
    [switch] $NoRestore,
    [switch] $Clean
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $root 'Shotwise.sln'
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

function Invoke-Step([string] $Name, [scriptblock] $Action) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE" }
}

function Find-MSBuildCpp {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { return $null }
    return & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
}

Push-Location $root
try {
    if ($Clean) {
        Invoke-Step 'dotnet clean' { dotnet clean $sln -c $Configuration -nodeReuse:false -v:minimal }
    }
    if (-not $NoRestore) {
        Invoke-Step 'dotnet restore' { dotnet restore $sln -nodeReuse:false }
    }
    Invoke-Step "dotnet build ($Configuration)" { dotnet build $sln -c $Configuration --no-restore -m:1 -nodeReuse:false }

    if ($Native -or $Test) {
        $vcxproj = Join-Path $root 'native\Shotwise.ShellExt\Shotwise.ShellExt.vcxproj'
        $msbuild = Find-MSBuildCpp
        if (-not (Test-Path $vcxproj)) {
            Write-Warning "Native shell extension project not found ($vcxproj); skipping."
        }
        elseif (-not $msbuild) {
            if ($Native) { throw 'MSBuild with the MSVC v143 toolset was not found (Visual Studio 2022 Build Tools, VC.Tools.x86.x64).' }
            Write-Warning 'MSVC build tools not found: native shell extension not built (its tests will fail).'
        }
        else {
            $configs = @($Configuration) + $(if ($Configuration -ne 'Release') { @('Release') } else { @() })
            foreach ($cfg in $configs) {
                Invoke-Step "native build ($cfg|x64)" { & $msbuild $vcxproj /p:Configuration=$cfg /p:Platform=x64 /m:1 /nodeReuse:false /v:minimal }
            }
            # B5: ship the Release shell extension + property description next to the app.
            $dll = Join-Path $root 'native\Shotwise.ShellExt\bin\x64\Release\Shotwise.ShellExt.dll'
            $propdesc = Join-Path $root 'native\Shotwise.ShellExt\Shotwise.propdesc'
            $appOut = Get-ChildItem (Join-Path $root "src\Shotwise.App\bin\x64\$Configuration") -Recurse -Filter 'Shotwise.exe' -ErrorAction SilentlyContinue |
                Select-Object -First 1 | ForEach-Object { $_.DirectoryName }
            if ($appOut -and (Test-Path $dll)) {
                Copy-Item $dll, $propdesc -Destination $appOut -Force
                Write-Host "    copied Shotwise.ShellExt.dll + Shotwise.propdesc -> $appOut"
            }
            else { Write-Warning 'App output or native Release DLL not found; shell extension not copied.' }
        }
    }

    if ($Test) {
        $summary = @()
        $failed = $false
        # Only test projects that are part of the solution (a local-only project under tests\ is not built by it).
        $inSln = @(dotnet sln $sln list | Where-Object { $_ -match '\.Tests\.csproj$' } | ForEach-Object { [IO.Path]::GetFileName($_.Trim()) })
        Get-ChildItem (Join-Path $root 'tests') -Filter '*.Tests.csproj' -Recurse | Where-Object { $inSln -contains $_.Name } | Sort-Object Name | ForEach-Object {
            $proj = $_.FullName
            Write-Host "==> dotnet test $($_.BaseName)" -ForegroundColor Cyan
            # Windows PowerShell 5.1 turns a native command's stderr into terminating errors under 'Stop'; a failing test
            # writes to stderr, so relax it here and judge the run by the exit code (the summary below reports failures).
            $eap = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            $out = & dotnet test $proj -c $Configuration --no-build -nodeReuse:false 2>&1 | ForEach-Object { "$_" }
            $code = $LASTEXITCODE
            $ErrorActionPreference = $eap
            $out | Write-Host
            $line = ($out | Select-String -Pattern '(Passed|Failed)!\s+-\s+Failed:\s+(\d+),\s+Passed:\s+(\d+),\s+Skipped:\s+(\d+),\s+Total:\s+(\d+)' | Select-Object -Last 1)
            if ($line) {
                $m = $line.Matches[0].Groups
                $summary += [pscustomobject]@{ Project = $_.BaseName; Passed = [int]$m[3].Value; Failed = [int]$m[2].Value; Skipped = [int]$m[4].Value; Total = [int]$m[5].Value }
            }
            else {
                $summary += [pscustomobject]@{ Project = $_.BaseName; Passed = 0; Failed = -1; Skipped = 0; Total = 0 }
            }
            if ($code -ne 0) { $failed = $true }
        }
        Write-Host ''
        Write-Host 'Test summary' -ForegroundColor Cyan
        $summary | Format-Table -AutoSize | Out-String | Write-Host
        $t = ($summary | Measure-Object -Property Passed -Sum).Sum
        Write-Host "Total passed: $t" -ForegroundColor Green
        if ($failed) { throw 'One or more test projects failed.' }
    }

    Write-Host "Build completed ($Configuration)." -ForegroundColor Green
}
finally {
    Pop-Location
}
