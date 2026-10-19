<#
.SYNOPSIS
    Cuts a Shotwise release: bump the version, test, package (with a delta against the previous GitHub release) and, with
    -Publish, upload it to GitHub Releases of https://github.com/yarink80/Shotwise, where installed copies pick it up.

.DESCRIPTION
    1. Sets <Version> in Directory.Build.props (the single source of truth for the app, the installer and the feed).
    2. Requires docs\RELEASE-NOTES-<Version>.md (Markdown; shown by Setup, by the app's update prompt and as the GitHub release body).
    3. Debug build + every test project (opt-in hardware/network tests stay skipped), unless -SkipTests.
    4. tools\package.ps1 -Version <Version> -DownloadPrevious: `vpk download github` first so Velopack builds a delta package
       against the latest published release, then publish/prune/`vpk pack` into artifacts\package\<Version>\vpk and dist\<Version>.
    5. Only with -Publish: `vpk upload github --publish --tag v<Version> --releaseName "Shotwise <Version>"` (plus --pre for the
       beta channel), with the token from -Token, $env:GITHUB_TOKEN or `gh auth token`.

    Nothing is committed or tagged locally. vpk creates the tag on GitHub at the default branch head (or -TargetCommitish), so
    commit and push the version bump and the release notes before publishing.

    Channels: -Channel stable publishes a normal GitHub release (everyone); -Channel beta publishes a GitHub *pre-release*, which
    only copies set to Preferences -> Startup & Updates -> Update channel = Beta install. Beta versions should carry a
    pre-release suffix (0.9.2-beta.1) so the next stable 0.9.2 sorts above them.

.EXAMPLE
    pwsh tools/release.ps1 -Version 0.9.1                     # dry run: bump, test, package; nothing uploaded
    pwsh tools/release.ps1 -Version 0.9.1 -Publish            # ... and publish GitHub release v0.9.1
    pwsh tools/release.ps1 -Version 0.9.2-beta.1 -Channel beta -Publish
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [ValidateSet('stable', 'beta')] [string] $Channel = 'stable',
    [switch] $Publish,
    [switch] $SkipTests,
    [string] $Token,
    [string] $RepoUrl = 'https://github.com/yarink80/Shotwise',
    [string] $TargetCommitish,
    [string] $SignParams,
    [string] $AzureTrustedSignFile,
    # Local previous release for the delta instead of downloading it from GitHub (e.g. dist\0.9.0).
    [string] $PreviousReleaseDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$VelopackChannel = 'win'
$PackId = 'Shotwise.App'

function Invoke-Step([string] $Name, [scriptblock] $Action) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE" }
}

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?$') { throw "Version '$Version' is not SemVer (x.y.z or x.y.z-beta.N)." }
$isPre = $Version -match '-'
if ($Channel -eq 'stable' -and $isPre) { throw "A stable release cannot have a pre-release suffix ($Version). Use -Channel beta." }
if ($Channel -eq 'beta' -and -not $isPre) { Write-Warning "Beta release $Version has no pre-release suffix; the stable release of the same number will not supersede it." }

# ---------------------------------------------------------------- 1. version (single source of truth)
$propsPath = Join-Path $root 'Directory.Build.props'
$props = Get-Content $propsPath -Raw
if ($props -notmatch '<Version>[^<]*</Version>') { throw "No <Version> element in $propsPath" }
$current = [regex]::Match($props, '<Version>([^<]*)</Version>').Groups[1].Value
if ($current -ne $Version) {
    $props = [regex]::Replace($props, '<Version>[^<]*</Version>', "<Version>$Version</Version>", 1)
    [IO.File]::WriteAllText($propsPath, $props, [Text.UTF8Encoding]::new($false))
    Write-Host "    Directory.Build.props: Version $current -> $Version"
}
else { Write-Host "    Directory.Build.props already at $Version" }

# ---------------------------------------------------------------- 2. release notes
$notes = Join-Path $root "docs\RELEASE-NOTES-$Version.md"
if (-not (Test-Path $notes)) {
    throw "Write the release notes first: docs\RELEASE-NOTES-$Version.md (copy docs\RELEASE-NOTES-0.9.1.md as a template)."
}
$notesText = Get-Content $notes -Raw
if ($notesText -notmatch [regex]::Escape($Version)) { Write-Warning "docs\RELEASE-NOTES-$Version.md does not mention $Version." }

# ---------------------------------------------------------------- 3. tests
if (-not $SkipTests) {
    Invoke-Step 'build + tests (tools\build.ps1 -Test)' { & (Join-Path $PSScriptRoot 'build.ps1') -Test; if (-not $?) { $global:LASTEXITCODE = 1 } }
}

# ---------------------------------------------------------------- token (needed for download on private repos and for upload)
if (-not $Token) { $Token = $env:GITHUB_TOKEN }
if (-not $Token -and (Get-Command gh -ErrorAction SilentlyContinue)) {
    $Token = (& gh auth token 2>$null | Select-Object -First 1)
    if ($LASTEXITCODE -ne 0) { $Token = $null; $global:LASTEXITCODE = 0 }
}
if ($Publish -and -not $Token) { throw 'Publishing needs a GitHub token: -Token, $env:GITHUB_TOKEN or `gh auth login`.' }

# ---------------------------------------------------------------- 4. package (delta against the previous release)
$pkg = @{ Version = $Version; Channel = $VelopackChannel; ReleaseNotes = $notes }
if ($SignParams) { $pkg.SignParams = $SignParams }
if ($AzureTrustedSignFile) { $pkg.AzureTrustedSignFile = $AzureTrustedSignFile }
if ($PreviousReleaseDir) { $pkg.PreviousReleaseDir = $PreviousReleaseDir }
else {
    $pkg.DownloadPrevious = $true
    $pkg.RepoUrl = $RepoUrl
    if ($Token) { $pkg.Token = $Token }
    # A beta builds its delta against the newest release of any kind; a stable one against the newest stable release.
    if ($Channel -eq 'beta') { $pkg.Prerelease = $true }
}
Invoke-Step "tools\package.ps1 $Version" { & (Join-Path $PSScriptRoot 'package.ps1') @pkg; if (-not $?) { $global:LASTEXITCODE = 1 } }
$vpkOut = Join-Path $root "artifacts\package\$Version\vpk"
$dist = Join-Path $root "dist\$Version"

# ---------------------------------------------------------------- 5. publish
if (-not $Publish) {
    Write-Host ''
    Write-Host "Release candidate $Version is in $dist (not published)." -ForegroundColor Green
    Write-Host "Publish it with: pwsh tools/release.ps1 -Version $Version$(if ($Channel -eq 'beta') { ' -Channel beta' }) -Publish -SkipTests"
    Write-Host "   (or push tag v$Version to let .github/workflows/release.yml build and publish it)"
    return
}
$up = @('upload', 'github', '--repoUrl', $RepoUrl, '--token', $Token, '--outputDir', $vpkOut, '--channel', $VelopackChannel,
    '--publish', '--releaseName', "Shotwise $Version", '--tag', "v$Version")
if ($Channel -eq 'beta') { $up += '--pre' }
if ($TargetCommitish) { $up += @('--targetCommitish', $TargetCommitish) }
Invoke-Step "vpk upload github v$Version ($Channel)" { & vpk @up }
Write-Host ''
Write-Host "Published Shotwise $Version ($Channel) to $RepoUrl/releases/tag/v$Version" -ForegroundColor Green
Write-Host 'Installed copies on that channel find it at their next check (startup + daily), or via Help > Check for Updates.'
