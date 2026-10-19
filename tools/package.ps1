<#
.SYNOPSIS
    Builds the Shotwise Windows installer (Velopack): Shotwise-Setup.exe, a portable zip, the update-feed files and SHA-256 sums.

.DESCRIPTION
    One command, from a clean state:
      1. dotnet publish src\Shotwise.App (Release, win-x64, self-contained, ReadyToRun, not trimmed, no PDBs)
      2. native\Shotwise.ShellExt Release build (MSVC v143) -> Shotwise.ShellExt.dll + Shotwise.propdesc next to Shotwise.exe
      3. prune what the app never loads (import libs, PDBs, OpenCV's FFmpeg video-IO plugin) and add licence texts
      4. vpk pack (package id Shotwise.App, per-user install to %LOCALAPPDATA%\Shotwise.App, no admin)
      5. dist\<version>\: Shotwise-Setup.exe, Shotwise-Portable.zip, *.nupkg + RELEASES/releases.<channel>.json, SHA256SUMS.txt

    Velopack's own shortcuts are disabled (--shortcuts None): the app's install hook writes exactly one Start menu shortcut with
    AUMID Shotwise.App, plus a Desktop shortcut on first install (src\Shotwise.App\Hosting\InstallHooks.cs).

    Unsigned by default. Code signing is one parameter:
      -SignParams '/fd sha256 /tr http://timestamp.digicert.com /td sha256 /f cert.pfx /p ****'   (signtool.exe)
      -AzureTrustedSignFile metadata.json                                                       (Azure Trusted Signing)
    vpk then signs every binary in the package, Update.exe and Setup.exe.

    Delta updates: Velopack builds a delta package when the vpk output folder already holds the previous release. Either
      -DownloadPrevious [-RepoUrl ...] [-Token ...] [-Prerelease]   runs `vpk download github` first (CI / tools\release.ps1), or
      -PreviousReleaseDir dist\0.9.0                                copies a local previous release (feed json + full nupkg) in.

.EXAMPLE
    powershell -File tools/package.ps1
    powershell -File tools/package.ps1 -Version 0.9.1 -SignParams '/a /fd sha256 /tr http://timestamp.digicert.com /td sha256'
    powershell -File tools/package.ps1 -Version 0.9.1 -DownloadPrevious -Token (gh auth token)
#>
[CmdletBinding()]
param(
    # Default = <Version> in Directory.Build.props (the single source of truth; tools\release.ps1 bumps it).
    [string] $Version,
    # Velopack channel (feed file releases.<channel>.json). Stable/Beta is the GitHub pre-release flag, not a Velopack channel.
    [string] $Channel = 'win',
    [string] $SignParams,
    [string] $AzureTrustedSignFile,
    [switch] $NoPortable,
    # Release notes (Markdown) shown by Setup, the app and the GitHub release. Default docs\RELEASE-NOTES-<Version>.md.
    [string] $ReleaseNotes,
    # Output root; the package lands in <OutputRoot>\<Version>. Default dist\.
    [string] $OutputRoot,
    # Seed the vpk output with the previous release so a delta package is produced.
    [string] $PreviousReleaseDir,
    [switch] $DownloadPrevious,
    [string] $RepoUrl = 'https://github.com/yarink80/Shotwise',
    [string] $Token,
    [switch] $Prerelease
)

$ErrorActionPreference = 'Stop'
$VpkVersion = '1.2.161'          # keep equal to the Velopack PackageVersion in Directory.Packages.props
$PackId = 'Shotwise.App'         # = Shotwise.Platform.InstallLocation.PackageId (NOT "Shotwise": that folder holds user data)
$root = Split-Path -Parent $PSScriptRoot
if (-not $Version) {
    $props = [xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)
    $Version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
    if (-not $Version) { throw 'No -Version given and no <Version> in Directory.Build.props.' }
}
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Version '$Version' is not SemVer (x.y.z or x.y.z-pre.N)." }
$app = Join-Path $root 'src\Shotwise.App\Shotwise.App.csproj'
$work = Join-Path $root "artifacts\package\$Version"
$publish = Join-Path $work 'publish'
$vpkOut = Join-Path $work 'vpk'
if ($OutputRoot -and -not [IO.Path]::IsPathRooted($OutputRoot)) { $OutputRoot = Join-Path $root $OutputRoot }
$dist = Join-Path $(if ($OutputRoot) { $OutputRoot } else { Join-Path $root 'dist' }) $Version
$notes = if ($ReleaseNotes) { (Resolve-Path $ReleaseNotes).Path } else { Join-Path $root "docs\RELEASE-NOTES-$Version.md" }
$numericVersion = ($Version -split '[-+]')[0]
$icon = Join-Path $root 'src\Shotwise.App\Assets\shotwise.ico'
$splash = Join-Path $root 'src\Shotwise.App\Assets\png\shotwise-256.png'
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

function Invoke-Step([string] $Name, [scriptblock] $Action) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE" }
}
function Get-DirSize([string] $Path) { (Get-ChildItem $Path -Recurse -File | Measure-Object Length -Sum).Sum }
function Format-MB([double] $Bytes) { '{0:N1} MB' -f ($Bytes / 1MB) }

if (-not (Test-Path $notes)) { throw "Release notes not found: $notes" }

# ---------------------------------------------------------------- vpk tool (pinned)
$installed = (& dotnet tool list -g) | Select-String -Pattern '^\s*vpk\s+(\S+)' | ForEach-Object { $_.Matches[0].Groups[1].Value }
if ($installed -ne $VpkVersion) {
    Invoke-Step "dotnet tool update -g vpk $VpkVersion" { dotnet tool update -g vpk --version $VpkVersion }
}

# ---------------------------------------------------------------- 1. clean publish
foreach ($d in @($work, $dist)) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
New-Item -ItemType Directory -Force -Path $publish, $vpkOut, $dist | Out-Null

$common = @('-c', 'Release', '-r', 'win-x64', '-nodeReuse:false', '-m:1')
Invoke-Step 'dotnet clean (Release)' { dotnet clean $app @common -v:minimal }
# publish restores itself, so the self-contained runtime packs and the ReadyToRun compiler are part of the restore.
Invoke-Step "dotnet publish (Release, self-contained, ReadyToRun, $Version)" {
    dotnet publish $app @common --self-contained true -o $publish `
        -p:PublishReadyToRun=true -p:PublishTrimmed=false -p:PublishSingleFile=false `
        -p:DebugType=none -p:DebugSymbols=false -p:GenerateDocumentationFile=false `
        -p:Version=$Version -p:InformationalVersion=$Version -p:FileVersion="$numericVersion.0" -p:AssemblyVersion="$numericVersion.0"
}

# ---------------------------------------------------------------- 2. native shell extension (Release)
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = if (Test-Path $vswhere) { & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1 }
if (-not $msbuild) { throw 'MSBuild with the MSVC v143 toolset (VS 2022 Build Tools, VC.Tools.x86.x64) is required for the shell extension.' }
$vcxproj = Join-Path $root 'native\Shotwise.ShellExt\Shotwise.ShellExt.vcxproj'
Invoke-Step 'native shell extension (Release|x64)' { & $msbuild $vcxproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /m:1 /nodeReuse:false /v:minimal }
Copy-Item (Join-Path $root 'native\Shotwise.ShellExt\bin\x64\Release\Shotwise.ShellExt.dll'), (Join-Path $root 'native\Shotwise.ShellExt\Shotwise.propdesc') -Destination $publish -Force

# App-local Visual C++ runtime (redistributable per the VS "Distributable Code" list): onnxruntime.dll imports msvcp140,
# msvcp140_1, vcruntime140 and vcruntime140_1, glfw3.dll imports vcruntime140. A PC without the VC++ 2015-2022
# redistributable would otherwise fail to load them. A newer system copy in System32 does not override app-local DLLs,
# so ship the latest MSVC v143 CRT from the build tools.
$vsRoot = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$crt = Get-ChildItem (Join-Path $vsRoot 'VC\Redist\MSVC') -Directory | Where-Object Name -match '^\d+\.' | Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64\Microsoft.VC143.CRT' } | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $crt) { throw 'VC++ redistributable CRT folder (VC\Redist\MSVC\<ver>\x64\Microsoft.VC143.CRT) not found.' }
foreach ($dll in 'msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll', 'vcruntime140_1.dll') { Copy-Item (Join-Path $crt $dll) $publish -Force }
Write-Host "    app-local VC++ runtime from $crt"

# ---------------------------------------------------------------- 3. prune + licences
$before = Get-DirSize $publish
$prune = @(
    '*.pdb',                          # symbols (native PDBs from SkiaSharp/HarfBuzz too)
    '*.lib', '*.exp',                 # import libraries (onnxruntime)
    'opencv_videoio_ffmpeg*.dll',     # OpenCV FFmpeg video-IO plugin: Shotwise never uses cv::VideoCapture/VideoWriter (and it is LGPL FFmpeg)
    'createdump.exe',                 # runtime crash-dump helper; Shotwise writes its own minidumps
    'Microsoft.DiaSymReader.Native.amd64.dll',  # PDB reader for stack traces; no PDBs are shipped
    'aspnetcorev2_inprocess.dll'      # IIS in-process hosting module; Kestrel (mobile upload) does not use it
)
foreach ($pattern in $prune) { Get-ChildItem $publish -Recurse -File -Filter $pattern | Remove-Item -Force }
# XML API docs that NuGet copies next to a DLL of the same name.
Get-ChildItem $publish -File -Filter '*.xml' | Where-Object { Test-Path (Join-Path $publish ($_.BaseName + '.dll')) } | Remove-Item -Force
# Native runtimes for other RIDs must not exist in a win-x64 publish.
$foreign = Get-ChildItem $publish -Recurse -Directory -Filter 'runtimes' -ErrorAction SilentlyContinue
if ($foreign) { throw "Unexpected runtimes\ folder in publish output: $($foreign.FullName -join ', ')" }
$after = Get-DirSize $publish
Write-Host ("    pruned {0}" -f (Format-MB ($before - $after)))

# Licence texts: generic texts from licenses\ + the upstream licence/notice files of every shipped package (with copyright lines).
$licDir = Join-Path $publish 'licenses'
New-Item -ItemType Directory -Force -Path (Join-Path $licDir 'packages') | Out-Null
Copy-Item (Join-Path $root 'licenses\*.txt') $licDir
$assets = Get-Content (Join-Path $root 'src\Shotwise.App\obj\project.assets.json') -Raw | ConvertFrom-Json
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$seen = @{}
foreach ($lib in $assets.libraries.PSObject.Properties) {
    if ($lib.Value.type -ne 'package') { continue }
    $id, $ver = $lib.Name.Split('/')
    if ($id -match '\.(Linux|macOS|osx|linux|WebAssembly)$|NativeAssets\.(Linux|macOS|WebAssembly)') { continue }   # not shipped on win-x64
    $dir = Join-Path $nugetRoot ("{0}\{1}" -f $id.ToLowerInvariant(), $ver.ToLowerInvariant())
    if (-not (Test-Path $dir)) { continue }
    Get-ChildItem $dir -File | Where-Object { $_.Name -match '^(licen[cs]e|copying|notice|thirdpartynotices|third-party-notices)' } | ForEach-Object {
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
        if ($seen.ContainsKey($hash)) { return }   # identical text already copied (e.g. the Microsoft.Extensions.* family)
        $seen[$hash] = $true
        Copy-Item $_.FullName (Join-Path $licDir ("packages\{0}-{1}-{2}" -f $id, $ver, $_.Name)) -Force
    }
}
if (-not (Test-Path (Join-Path $publish 'THIRD-PARTY-NOTICES.md'))) { Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $publish }

# Sanity: everything the installed app needs at run time.
foreach ($required in @('Shotwise.exe', 'Velopack.dll', 'Shotwise.ShellExt.dll', 'Shotwise.propdesc', 'OpenCvSharpExtern.dll', 'onnxruntime.dll',
        'vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll', 'msvcp140_1.dll',
        'libSkiaSharp.dll', 'e_sqlite3.dll', 'Content\stamps', 'Content\templates', 'Content\themes', 'Content\palettes', 'Content\cursors',
        'Assets\shotwise-96.png', 'THIRD-PARTY-NOTICES.md')) {
    if (-not (Test-Path (Join-Path $publish $required))) { throw "Publish output is missing $required" }
}
$installedBytes = Get-DirSize $publish
$fileCount = (Get-ChildItem $publish -Recurse -File).Count

# ---------------------------------------------------------------- 4. previous release (delta) + vpk pack
if ($PreviousReleaseDir) {
    $prev = (Resolve-Path $PreviousReleaseDir).Path
    $feed = Join-Path $prev "releases.$Channel.json"
    if (-not (Test-Path $feed)) { throw "PreviousReleaseDir has no releases.$Channel.json: $prev" }
    # The feed plus the newest full package are enough for vpk to build the delta (older deltas are kept in the feed json).
    Copy-Item $feed $vpkOut
    $full = Get-ChildItem $prev -Filter "$PackId-*-full.nupkg" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $full) { throw "PreviousReleaseDir has no $PackId-*-full.nupkg: $prev" }
    Copy-Item $full.FullName $vpkOut
    Write-Host "    previous release for delta: $($full.Name)"
}
elseif ($DownloadPrevious) {
    $dl = @('download', 'github', '--repoUrl', $RepoUrl, '--channel', $Channel, '--outputDir', $vpkOut)
    if ($Token) { $dl += @('--token', $Token) }
    if ($Prerelease) { $dl += '--pre' }
    Write-Host "==> vpk download github (previous release for delta packages)" -ForegroundColor Cyan
    & vpk @dl
    # The first release has nothing to download: continue with a full package only.
    if ($LASTEXITCODE -ne 0) { Write-Warning "vpk download github failed (exit $LASTEXITCODE): no previous release, so no delta package"; $global:LASTEXITCODE = 0 }
}
$vpkArgs = @(
    'pack', '--yes', '--skip-updates',
    '--packId', $PackId, '--packVersion', $Version, '--packDir', $publish, '--mainExe', 'Shotwise.exe',
    '--packTitle', 'Shotwise', '--packAuthors', 'Shotwise',
    '--icon', $icon, '--splashImage', $splash, '--releaseNotes', $notes,
    '--channel', $Channel, '--runtime', 'win-x64', '--outputDir', $vpkOut,
    '--aumid', 'Shotwise.App',
    '--shortcuts', 'None'             # no Velopack shortcuts: the install hook owns Start menu + Desktop (one entry, AUMID set).
                                      # ('""' only worked on Windows PowerShell 5.1, which drops empty native args; pwsh 7 passes it literally.)
)
if ($NoPortable) { $vpkArgs += '--noPortable' }
if ($SignParams) { $vpkArgs += @('--signParams', $SignParams) }
if ($AzureTrustedSignFile) { $vpkArgs += @('--azureTrustedSignFile', $AzureTrustedSignFile) }
Invoke-Step "vpk pack $PackId $Version ($Channel)" { & vpk @vpkArgs }

# ---------------------------------------------------------------- 5. dist\<version>
Get-ChildItem $vpkOut -File | ForEach-Object {
    $name = switch -Regex ($_.Name) {
        '-Setup\.exe$' { 'Shotwise-Setup.exe' }
        '-Portable\.zip$' { 'Shotwise-Portable.zip' }
        default { $_.Name }
    }
    Copy-Item $_.FullName (Join-Path $dist $name)
}
Copy-Item $notes (Join-Path $dist "RELEASE-NOTES-$Version.md")
Copy-Item (Join-Path $root 'docs\INSTALL.md') (Join-Path $dist 'INSTALL.md')
$sums = Get-ChildItem $dist -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
[IO.File]::WriteAllLines((Join-Path $dist 'SHA256SUMS.txt'), [string[]]$sums)

$setup = Get-Item (Join-Path $dist 'Shotwise-Setup.exe')
Write-Host ''
Write-Host "Shotwise $Version packaged -> $dist" -ForegroundColor Green
Get-ChildItem $dist -File | Sort-Object Name | Format-Table Name, @{ n = 'Size'; e = { Format-MB $_.Length } } -AutoSize | Out-String | Write-Host
$delta = Get-ChildItem $dist -Filter "$PackId-$Version-delta.nupkg" -ErrorAction SilentlyContinue
Write-Host ("Delta package  : {0}" -f $(if ($delta) { "$($delta.Name) ($(Format-MB $delta.Length))" } else { 'none (no previous release in the vpk output)' }))
Write-Host ("vpk output     : {0} (upload with: vpk upload github --outputDir <this>)" -f $vpkOut)
Write-Host ("Installer      : {0} ({1})" -f $setup.Name, (Format-MB $setup.Length))
Write-Host ("SHA-256        : {0}" -f (Get-FileHash $setup.FullName -Algorithm SHA256).Hash.ToLowerInvariant())
Write-Host ("Installed size : {0} in {1} files (app folder, before Update.exe)" -f (Format-MB $installedBytes), $fileCount)
Write-Host ("Signed         : {0}" -f $(if ($SignParams -or $AzureTrustedSignFile) { 'yes' } else { 'NO - SmartScreen will warn (More info -> Run anyway)' }))
