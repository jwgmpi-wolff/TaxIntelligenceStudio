#Requires -Version 5.1
<#
.SYNOPSIS
    Installs Tax Intelligence Studio on this Windows computer.

.DESCRIPTION
    Self-contained bootstrap installer. Run this script directly on the machine
    that should run the application. Nothing needs to be present beforehand:

      - no clone of the repository
      - no Git
      - no .NET SDK or .NET runtime
      - no Visual Studio or build tooling

    The installer acquires the application in the first way that succeeds:

      1. The local copy of the repository this script is running from (a clone
         or an extracted ZIP download). No download and no credential needed.
      2. A prebuilt release asset for this machine's architecture, if one has
         been published. Nothing is compiled in this path.
      3. The repository source archive, which is then published locally into a
         self-contained build.

    Whenever a build is needed and the .NET 9 SDK is missing, a private copy is
    installed under %LOCALAPPDATA% for the current user only. No administrator
    rights and no machine-wide change are required.

    Platform differences are detected at run time and adjusted for: native CPU
    architecture (x64, x86, Arm64, including x64 emulation on Arm64), Windows
    PowerShell 5.1 or PowerShell 7, the Windows long-path setting, the system
    drive, and whether the session is elevated.

    Downloading from GitHub requires a credential because the repository is
    private. The installer uses the first credential it finds: -Token,
    $env:GITHUB_TOKEN, or the token from an authenticated GitHub CLI
    (`gh auth login`).

.PARAMETER Token
    GitHub personal access token with read access to the repository. Falls back
    to $env:GITHUB_TOKEN, then to `gh auth token`.

.PARAMETER Reference
    Git tag, branch, or commit to install from when building from source.
    Defaults to main.

.PARAMETER Runtime
    Target runtime identifier. Auto-detected from this machine's architecture.

.PARAMETER InstallPath
    Installation directory. Defaults to
    %LOCALAPPDATA%\Programs\TaxIntelligenceStudio.

.PARAMETER WorkPath
    Temporary working directory used while downloading and building. Defaults
    to a short folder inside the collector container (C:\temp\TaxIntelligenceStudio).
    Specify a short path such as C:\tis if the default still exceeds the Windows
    260 character path limit.

.PARAMETER ResultsPath
    Collector container: the single local folder that receives the install log
    and the install result file. Defaults to C:\temp\TaxIntelligenceStudio.

.PARAMETER FromSource
    Always build from source, even when a prebuilt release asset is available.

.PARAMETER FromGitHub
    Download from GitHub even when this script is running inside a local copy
    of the repository. By default a local copy is built directly, with no
    download and no GitHub credential.

.PARAMETER NoShortcut
    Skip creating the Start Menu shortcut.

.PARAMETER DesktopShortcut
    Also create a Desktop shortcut.

.PARAMETER Launch
    Start the application after installation.

.PARAMETER Uninstall
    Remove the installed application and its shortcuts, then exit. Nothing is
    downloaded and no credential is needed.

.EXAMPLE
    .\Install.ps1

.EXAMPLE
    .\Install.ps1 -Token ghp_xxx -DesktopShortcut -Launch

.EXAMPLE
    .\Install.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string]$Token,
    [string]$Reference = 'main',

    [ValidateSet('win-x64', 'win-arm64', 'win-x86')]
    [string]$Runtime,

    [string]$InstallPath,

    [string]$WorkPath,

    [string]$ResultsPath,

    [switch]$FromSource,
    [switch]$FromGitHub,
    [switch]$NoShortcut,
    [switch]$DesktopShortcut,
    [switch]$Launch,
    [switch]$Uninstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$AppName = 'Tax Intelligence Studio'
$ExeName = 'TaxIntelligenceStudio.Desktop.exe'
$ProcessName = 'TaxIntelligenceStudio.Desktop'
$Owner = 'jwgmpi-wolff'
$Repo = 'TaxIntelligenceStudio'
$RequiredSdkChannel = '9.0'

# ---------------------------------------------------------------------------
# Collector container
#
# The single local folder that every result from this script is written to:
# the full install transcript and a machine-readable install result file.
# Defaults to C:\temp\TaxIntelligenceStudio, is overridden by -ResultsPath, and
# is exported as TAXINTELLIGENCESTUDIO_RESULTS so the installed application
# collects its own results to the same place.
# ---------------------------------------------------------------------------
$CollectorContainer = if ($ResultsPath) {
    [System.Environment]::ExpandEnvironmentVariables($ResultsPath)
}
else {
    $systemDrive = if ($env:SystemDrive) { $env:SystemDrive } else { 'C:' }
    Join-Path $systemDrive 'temp\TaxIntelligenceStudio'
}

# A clone or an extracted ZIP of the repository contains this script under
# scripts\, so the source is right next to it and nothing has to be downloaded.
$LocalSourceRoot = $null
if (-not $FromGitHub -and $PSScriptRoot) {
    $candidate = Split-Path -Parent $PSScriptRoot
    if (Test-Path -LiteralPath (Join-Path $candidate 'src\TaxIntelligenceStudio.Desktop\TaxIntelligenceStudio.Desktop.csproj')) {
        $LocalSourceRoot = $candidate
    }
}

function Write-Step {
    param([string]$Message)
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Detail {
    param([string]$Message)
    Write-Host "    $Message" -ForegroundColor Green
}

function Write-Note {
    param([string]$Message)
    Write-Host "    $Message" -ForegroundColor Yellow
}

# RuntimeInformation.OSArchitecture is missing on some Windows PowerShell 5.1 /
# .NET Framework installs and reports the emulated architecture under x64
# emulation on Arm64, so prefer the machine-wide native value in the registry.
function Get-NativeArchitecture {
    $native = $null
    try {
        $native = (Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment' -Name PROCESSOR_ARCHITECTURE -ErrorAction Stop).PROCESSOR_ARCHITECTURE
    }
    catch {
        $native = $null
    }
    if ([string]::IsNullOrWhiteSpace($native)) {
        $native = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
    }
    switch ($native.ToUpperInvariant()) {
        'ARM64' { return 'Arm64' }
        'AMD64' { return 'X64' }
        'X86' { return 'X86' }
        default { return $native }
    }
}

function Get-TargetRuntime {
    $architecture = Get-NativeArchitecture
    switch ($architecture) {
        'Arm64' { return 'win-arm64' }
        'X64' { return 'win-x64' }
        'X86' { return 'win-x86' }
        default { throw "Unsupported OS architecture '$architecture'. Pass -Runtime explicitly." }
    }
}

function Resolve-GitHubToken {
    param([string]$Explicit)

    if (-not [string]::IsNullOrWhiteSpace($Explicit)) {
        return @{ Token = $Explicit.Trim(); Source = '-Token parameter' }
    }

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) {
        return @{ Token = $env:GITHUB_TOKEN.Trim(); Source = 'GITHUB_TOKEN environment variable' }
    }

    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if ($gh) {
        $previousPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $ghOutput = @(& $gh.Path auth token 2>$null)
            $ghExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousPreference
        }
        $ghToken = if ($ghOutput.Count -gt 0) { [string]$ghOutput[0] } else { $null }
        if ($ghExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($ghToken)) {
            return @{ Token = $ghToken.Trim(); Source = 'GitHub CLI (gh auth token)' }
        }
    }

    throw @"
A GitHub credential is required because '$Owner/$Repo' is private.

Provide one of the following and re-run:
  1. -Token <personal access token>
  2. `$env:GITHUB_TOKEN = '<personal access token>'
  3. Install the GitHub CLI and run: gh auth login

Create a token with 'Contents: read' access at:
  https://github.com/settings/tokens
"@
}

function Invoke-GitHubDownload {
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][string]$OutFile,
        [Parameter(Mandatory)][string]$Token,
        [string]$Accept = 'application/vnd.github+json'
    )

    $headers = @{
        Authorization          = "Bearer $Token"
        Accept                 = $Accept
        'X-GitHub-Api-Version' = '2022-11-28'
        'User-Agent'           = 'TaxIntelligenceStudio-Installer'
    }

    # Progress rendering makes large downloads dramatically slower in PS 5.1.
    $previousProgress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try {
        Invoke-WebRequest -Uri $Uri -Headers $headers -OutFile $OutFile -UseBasicParsing
    }
    finally {
        $ProgressPreference = $previousProgress
    }
}

function Get-LatestReleaseAsset {
    param([Parameter(Mandatory)][string]$Token, [Parameter(Mandatory)][string]$Runtime)

    $headers = @{
        Authorization          = "Bearer $Token"
        Accept                 = 'application/vnd.github+json'
        'X-GitHub-Api-Version' = '2022-11-28'
        'User-Agent'           = 'TaxIntelligenceStudio-Installer'
    }

    try {
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Owner/$Repo/releases/latest" `
            -Headers $headers -UseBasicParsing
    }
    catch {
        return $null
    }

    if (-not $release -or -not $release.PSObject.Properties.Name.Contains('assets')) {
        return $null
    }

    $asset = $release.assets | Where-Object { $_.name -eq "TaxIntelligenceStudio-$Runtime.zip" } | Select-Object -First 1
    if (-not $asset) {
        return $null
    }

    return [pscustomobject]@{
        Tag = $release.tag_name
        Url = $asset.url
        Name = $asset.name
    }
}

function Test-DotnetSatisfiesSource {
    # global.json is resolved from the current working directory, not from the
    # project path, so SDK suitability must be evaluated inside the source tree.
    param(
        [Parameter(Mandatory)][string]$DotnetPath,
        [Parameter(Mandatory)][string]$WorkingDirectory
    )

    Push-Location -LiteralPath $WorkingDirectory
    # Windows PowerShell 5.1 can turn native stderr into terminating errors
    # under 'Stop', and stopping the pipeline early (Select-Object -First) can
    # kill the process and leave a non-zero exit code. Capture all output first.
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = @(& $DotnetPath --version 2>$null)
        $exitCode = $LASTEXITCODE
        $version = if ($output.Count -gt 0) { [string]$output[0] } else { $null }
        if ($exitCode -ne 0 -or [string]::IsNullOrWhiteSpace($version)) {
            return $null
        }

        $parsed = [Version](($version.Trim() -split '-')[0] -split '\+')[0]
        if ($parsed -lt [Version]'9.0.0') {
            return $null
        }

        return $version.Trim()
    }
    catch {
        return $null
    }
    finally {
        $ErrorActionPreference = $previousPreference
        Pop-Location
    }
}

function Resolve-DotnetSdk {
    # Returns a dotnet executable that satisfies the source tree's global.json,
    # installing a private per-user SDK if necessary.
    param([Parameter(Mandatory)][string]$SourceRoot)

    $existing = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($existing) {
        $version = Test-DotnetSatisfiesSource -DotnetPath $existing.Path -WorkingDirectory $SourceRoot
        if ($version) {
            return [pscustomobject]@{ Path = $existing.Path; Version = $version }
        }
        Write-Note 'An existing dotnet installation was found, but it does not satisfy this project''s SDK requirement.'
    }

    $privateRoot = Join-Path $env:LOCALAPPDATA 'TaxIntelligenceStudio\dotnet-sdk'
    $privateDotnet = Join-Path $privateRoot 'dotnet.exe'
    if (Test-Path -LiteralPath $privateDotnet) {
        $version = Test-DotnetSatisfiesSource -DotnetPath $privateDotnet -WorkingDirectory $SourceRoot
        if ($version) {
            Write-Detail "Using previously installed private SDK: $privateRoot"
            return [pscustomobject]@{ Path = $privateDotnet; Version = $version }
        }
    }

    Write-Step 'Installing a private .NET 9 SDK for the current user'
    Write-Note 'No administrator rights are required and no machine-wide change is made.'

    $installerPath = Join-Path ([System.IO.Path]::GetTempPath()) ('dotnet-install-' + [Guid]::NewGuid().ToString('N') + '.ps1')
    $previousProgress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try {
        Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installerPath -UseBasicParsing
        # Request the native architecture explicitly; the installer would
        # otherwise pick x64 when this shell runs under emulation on Arm64.
        $sdkArchitecture = switch (Get-NativeArchitecture) {
            'Arm64' { 'arm64' }
            'X86' { 'x86' }
            default { 'x64' }
        }
        & $installerPath -Channel $RequiredSdkChannel -InstallDir $privateRoot -Architecture $sdkArchitecture -NoPath
    }
    finally {
        $ProgressPreference = $previousProgress
        Remove-Item -LiteralPath $installerPath -Force -ErrorAction SilentlyContinue
    }

    if (-not (Test-Path -LiteralPath $privateDotnet)) {
        throw "The .NET SDK installation did not produce '$privateDotnet'."
    }

    $version = Test-DotnetSatisfiesSource -DotnetPath $privateDotnet -WorkingDirectory $SourceRoot
    if (-not $version) {
        throw "The private SDK at '$privateRoot' still does not satisfy the project's global.json requirement."
    }

    Write-Detail "Private SDK installed to $privateRoot"
    return [pscustomobject]@{ Path = $privateDotnet; Version = $version }
}

function Expand-Payload {
    param([Parameter(Mandatory)][string]$ZipPath, [Parameter(Mandatory)][string]$Destination)

    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Recurse -Force
    }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $Destination)
}

function Expand-SourceArchive {
    # GitHub source archives nest everything under a generated
    # "<owner>-<repo>-<sha>" folder, which adds roughly 75 characters to every
    # path inside the build tree. Windows still enforces a 260 character limit
    # unless long paths are enabled, and intermediate build paths such as
    # obj\Release\net9.0\... overflow easily. The nested folder is therefore
    # flattened into a short directory before building.
    param(
        [Parameter(Mandatory)][string]$ZipPath,
        [Parameter(Mandatory)][string]$StagingPath,
        [Parameter(Mandatory)][string]$SourcePath
    )

    Expand-Payload -ZipPath $ZipPath -Destination $StagingPath

    $nested = @(Get-ChildItem -LiteralPath $StagingPath -Directory)
    if ($nested.Count -eq 1 -and -not (Test-Path -LiteralPath (Join-Path $StagingPath 'global.json'))) {
        Move-Item -LiteralPath $nested[0].FullName -Destination $SourcePath
        Remove-Item -LiteralPath $StagingPath -Recurse -Force -ErrorAction SilentlyContinue
    }
    else {
        Move-Item -LiteralPath $StagingPath -Destination $SourcePath
    }

    return $SourcePath
}

function Test-LongPathsEnabled {
    try {
        $value = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' `
                -Name LongPathsEnabled -ErrorAction Stop).LongPathsEnabled
        return $value -eq 1
    }
    catch {
        return $false
    }
}

function Test-BuildPathFits {
    param([Parameter(Mandatory)][string]$SourceRoot)

    # Longest path the build is expected to create inside the source tree.
    $probe = Join-Path $SourceRoot 'src\TaxIntelligenceStudio.Infrastructure\obj\Release\net9.0\win-arm64\TaxIntelligenceStudio.Infrastructure.dll'
    return $probe.Length -le 250
}

# Picks a writable working directory whose build paths fit this system. When
# Windows long paths are disabled and the preferred location is too deep, a
# shorter location is used automatically instead of failing.
function Resolve-WorkRoot {
    param([Parameter(Mandatory)][string]$Preferred)

    $suffix = [Guid]::NewGuid().ToString('N').Substring(0, 6)
    $systemDrive = if ($env:SystemDrive) { $env:SystemDrive } else { 'C:' }
    $candidates = @(
        $Preferred,
        (Join-Path $systemDrive "tis-$suffix"),
        (Join-Path ([System.IO.Path]::GetTempPath()) "tis-$suffix")
    )

    $longPaths = Test-LongPathsEnabled
    foreach ($candidate in $candidates) {
        if (-not $longPaths -and -not (Test-BuildPathFits -SourceRoot (Join-Path $candidate 's'))) {
            continue
        }

        try {
            if (Test-Path -LiteralPath $candidate) {
                Remove-Item -LiteralPath $candidate -Recurse -Force
            }
            New-Item -ItemType Directory -Path $candidate -Force -ErrorAction Stop | Out-Null
            if ($candidate -ne $Preferred) {
                Write-Note "Using the shorter build folder $candidate because Windows long paths are disabled on this computer."
            }
            return $candidate
        }
        catch {
            continue
        }
    }

    throw "No writable build folder with a short enough path was found. Re-run with -WorkPath pointing to a short folder such as $systemDrive\tis."
}

function Copy-LocalSource {
    param(
        [Parameter(Mandatory)][string]$From,
        [Parameter(Mandatory)][string]$To
    )

    # robocopy ships with every supported Windows version and skips build
    # output and Git metadata, which keeps the copy small and its paths short.
    $null = & robocopy.exe $From $To /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1 /XD bin obj .git .vs TestResults
    if ($LASTEXITCODE -ge 8) {
        throw "Copying the local source from '$From' failed (robocopy exit code $LASTEXITCODE)."
    }
    $global:LASTEXITCODE = 0
    return $To
}

function New-AppShortcut {
    param(
        [Parameter(Mandatory)][string]$ShortcutPath,
        [Parameter(Mandatory)][string]$TargetPath
    )

    $parent = Split-Path -Parent $ShortcutPath
    if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($ShortcutPath)
        $shortcut.TargetPath = $TargetPath
        $shortcut.WorkingDirectory = Split-Path -Parent $TargetPath
        $shortcut.IconLocation = $TargetPath
        $shortcut.Description = "$AppName - local IRS corpus and tax workpaper studio"
        $shortcut.Save()
    }
    finally {
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    }
}

function Assert-AppNotRunning {
    $running = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) {
        return
    }

    # Ask a running copy to close normally so its files can be replaced.
    Write-Note "$AppName is running; asking it to close."
    foreach ($process in $running) {
        try { [void]$process.CloseMainWindow() } catch { }
    }
    foreach ($process in $running) {
        try { [void]$process.WaitForExit(15000) } catch { }
    }

    $running = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        $ids = ($running | ForEach-Object { $_.Id }) -join ', '
        throw "$AppName is still running (PID $ids). Close it and run this again."
    }
}

# --- Paths ------------------------------------------------------------------

$IsElevated = $false
try {
    $IsElevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}
catch {
    $IsElevated = $false
}

if (-not $InstallPath) {
    $InstallPath = Join-Path $env:LOCALAPPDATA 'Programs\TaxIntelligenceStudio'
}
else {
    $InstallPath = [System.Environment]::ExpandEnvironmentVariables($InstallPath)
}

$StartMenuShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) "$AppName.lnk"
$DesktopShortcutPath = Join-Path ([Environment]::GetFolderPath('Desktop')) "$AppName.lnk"
$DataPath = Join-Path $env:LOCALAPPDATA 'TaxIntelligenceStudio'
$InstalledExe = Join-Path $InstallPath $ExeName

# --- Collector container ----------------------------------------------------
#
# Created before anything else so that even an early failure is collected.

$CollectorLog = $null
$CollectorResult = $null
try {
    New-Item -ItemType Directory -Path $CollectorContainer -Force | Out-Null
    $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
    $action = if ($Uninstall) { 'uninstall' } else { 'install' }
    $CollectorLog = Join-Path $CollectorContainer "$action-$stamp.log"
    $CollectorResult = Join-Path $CollectorContainer "$action-result-$stamp.json"

    # The installed application collects its own results to the same container.
    $env:TAXINTELLIGENCESTUDIO_RESULTS = $CollectorContainer
    [Environment]::SetEnvironmentVariable(
        'TAXINTELLIGENCESTUDIO_RESULTS', $CollectorContainer, 'User')

    Start-Transcript -Path $CollectorLog -Force | Out-Null
}
catch {
    # Collecting results must never prevent an install.
    Write-Warning "Could not open the collector container at ${CollectorContainer}: $($_.Exception.Message)"
    $CollectorLog = $null
}

function Write-CollectedResult {
    param([string]$Status, [string]$Message)

    if (-not $CollectorResult) { return }

    try {
        [pscustomobject]@{
            collected        = (Get-Date).ToString('o')
            action           = if ($Uninstall) { 'uninstall' } else { 'install' }
            status           = $Status
            message          = $Message
            computer         = $env:COMPUTERNAME
            user             = "$env:USERDOMAIN\$env:USERNAME"
            windows          = [System.Environment]::OSVersion.Version.ToString()
            architecture     = Get-NativeArchitecture
            runtime          = $Runtime
            installPath      = $InstallPath
            collectorContainer = $CollectorContainer
            log              = $CollectorLog
        } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $CollectorResult -Encoding UTF8
    }
    catch {
        Write-Warning "Could not write the install result: $($_.Exception.Message)"
    }
}

function Stop-Collector {
    if ($CollectorLog) {
        try { Stop-Transcript | Out-Null } catch { }
    }
    if ($CollectorContainer) {
        Write-Host ''
        Write-Host "Results collected to: $CollectorContainer" -ForegroundColor Cyan
    }
}

# --- Uninstall --------------------------------------------------------------

if ($Uninstall) {
    try {
        Write-Step "Uninstalling $AppName"
        Assert-AppNotRunning

        foreach ($link in @($StartMenuShortcut, $DesktopShortcutPath)) {
            if (Test-Path -LiteralPath $link) {
                Remove-Item -LiteralPath $link -Force
                Write-Detail "Removed shortcut: $link"
            }
        }

        if (Test-Path -LiteralPath $InstallPath) {
            Remove-Item -LiteralPath $InstallPath -Recurse -Force
            Write-Detail "Removed install directory: $InstallPath"
        }
        else {
            Write-Detail "No install directory at: $InstallPath"
        }

        Write-Host ''
        Write-Host "$AppName has been uninstalled." -ForegroundColor Green
        Write-Note "Local data at $DataPath was preserved."
        Write-Note "A private SDK, if one was installed, remains at $(Join-Path $DataPath 'dotnet-sdk')."
        Write-CollectedResult -Status 'succeeded' -Message "Uninstalled from $InstallPath."
    }
    catch {
        Write-CollectedResult -Status 'failed' -Message $_.Exception.Message
        Stop-Collector
        throw
    }

    Stop-Collector
    return
}

# --- Install ----------------------------------------------------------------

Write-Step "Installing $AppName"

$osVersion = [System.Environment]::OSVersion.Version
if ($osVersion -lt [Version]'10.0') {
    throw "Windows 10 or later is required, but '$osVersion' was detected."
}

if (-not $Runtime) {
    $Runtime = Get-TargetRuntime
}

Write-Detail "Computer: $env:COMPUTERNAME"
Write-Detail "User: $env:USERDOMAIN\$env:USERNAME$(if ($IsElevated) { ' (administrator)' })"
Write-Detail "Windows: $osVersion ($(Get-NativeArchitecture))"
Write-Detail "PowerShell: $($PSVersionTable.PSVersion) ($($PSVersionTable.PSEdition))"
Write-Detail "Runtime: $Runtime"
Write-Detail "Install path: $InstallPath"

Assert-AppNotRunning

$githubToken = $null
if ($LocalSourceRoot) {
    Write-Detail "Source: local copy at $LocalSourceRoot (no download or GitHub credential needed)"
}
else {
    $credential = Resolve-GitHubToken -Explicit $Token
    $githubToken = $credential.Token
    Write-Detail "GitHub credential: $($credential.Source)"
}

# Build paths are kept deliberately short. Windows enforces a 260 character
# path limit by default, and MSBuild intermediate paths overflow it easily.
if ($WorkPath) {
    $workRoot = [System.Environment]::ExpandEnvironmentVariables($WorkPath)
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
}
else {
    # Inside the collector container so build artifacts and results live
    # together, unless this system needs a shorter path.
    $workRoot = Resolve-WorkRoot -Preferred (Join-Path $CollectorContainer ('build-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)))
}

try {
    $payloadPath = $null

    if (-not $LocalSourceRoot -and -not $FromSource) {
        Write-Step 'Checking for a prebuilt release'
        $asset = Get-LatestReleaseAsset -Token $githubToken -Runtime $Runtime

        if ($asset) {
            Write-Detail "Found $($asset.Name) from release $($asset.Tag)."
            $assetZip = Join-Path $workRoot 'r.zip'
            Invoke-GitHubDownload -Uri $asset.Url -OutFile $assetZip -Token $githubToken `
                -Accept 'application/octet-stream'

            $payloadPath = Join-Path $workRoot 'p'
            Expand-Payload -ZipPath $assetZip -Destination $payloadPath
            Write-Detail 'Prebuilt payload extracted. No compilation required.'
        }
        else {
            Write-Detail 'No matching prebuilt release asset. Falling back to a source build.'
        }
    }

    if (-not $payloadPath) {
        if ($LocalSourceRoot) {
            Write-Step 'Preparing the local source'
            $repoRoot = Copy-LocalSource -From $LocalSourceRoot -To (Join-Path $workRoot 's')
        }
        else {
            Write-Step "Downloading source archive ($Reference)"
            $sourceZip = Join-Path $workRoot 's.zip'
            Invoke-GitHubDownload -Uri "https://api.github.com/repos/$Owner/$Repo/zipball/$Reference" `
                -OutFile $sourceZip -Token $githubToken

            $repoRoot = Expand-SourceArchive -ZipPath $sourceZip `
                -StagingPath (Join-Path $workRoot 'z') `
                -SourcePath (Join-Path $workRoot 's')
        }

        $projectFile = Get-ChildItem -LiteralPath $repoRoot -Recurse -Filter 'TaxIntelligenceStudio.Desktop.csproj' |
            Select-Object -First 1
        if (-not $projectFile) {
            throw "The source at '$repoRoot' does not contain 'TaxIntelligenceStudio.Desktop.csproj'."
        }

        Write-Detail "Source prepared at $repoRoot"
        $sdk = Resolve-DotnetSdk -SourceRoot $repoRoot
        Write-Detail ".NET SDK: $($sdk.Version) ($($sdk.Path))"

        Write-Step 'Building the application'
        $payloadPath = Join-Path $workRoot 'p'

        $publishArgs = @(
            'publish', $projectFile.FullName,
            '--configuration', 'Release',
            '--runtime', $Runtime,
            '--self-contained', 'true',
            '--output', $payloadPath,
            '--nologo',
            '-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-p:EnableWindowsTargeting=true',
            '-p:DebugType=none'
        )

        # The SDK version is selected from global.json relative to the current
        # working directory, so the build must run inside the source tree.
        Push-Location -LiteralPath $repoRoot
        $previousPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $buildOutput = & $sdk.Path @publishArgs 2>&1
            $buildExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousPreference
            Pop-Location
        }

        if ($buildExitCode -ne 0) {
            Write-Host ''
            Write-Host 'Build output:' -ForegroundColor Red
            $buildOutput | Select-Object -Last 30 | ForEach-Object { Write-Host "  $_" }
            throw "The build failed with exit code $buildExitCode."
        }
    }

    if (-not (Test-Path -LiteralPath (Join-Path $payloadPath $ExeName))) {
        throw "The prepared payload does not contain '$ExeName'."
    }

    Write-Step 'Installing'
    Assert-AppNotRunning

    if (Test-Path -LiteralPath $InstallPath) {
        Remove-Item -LiteralPath $InstallPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
    Copy-Item -Path (Join-Path $payloadPath '*') -Destination $InstallPath -Recurse -Force

    if (-not (Test-Path -LiteralPath $InstalledExe)) {
        throw "Installation failed: '$InstalledExe' is missing."
    }

    $sizeMb = [math]::Round(((Get-ChildItem -LiteralPath $InstallPath -Recurse -File |
                Measure-Object -Property Length -Sum).Sum / 1MB), 1)
    Write-Detail "Installed $sizeMb MB to $InstallPath"

    if (-not $NoShortcut) {
        New-AppShortcut -ShortcutPath $StartMenuShortcut -TargetPath $InstalledExe
        Write-Detail "Start Menu shortcut: $StartMenuShortcut"
    }

    if ($DesktopShortcut) {
        New-AppShortcut -ShortcutPath $DesktopShortcutPath -TargetPath $InstalledExe
        Write-Detail "Desktop shortcut: $DesktopShortcutPath"
    }

    Write-CollectedResult -Status 'succeeded' -Message "Installed $sizeMb MB to $InstallPath."
}
catch {
    Write-CollectedResult -Status 'failed' -Message $_.Exception.Message
    Stop-Collector
    throw
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
Write-Host "$AppName installed." -ForegroundColor Green
Write-Host "  Launch: $InstalledExe"
Write-Host "  Data:   $DataPath"
Write-Host '  Remove: double-click Uninstall.cmd, or run this script with -Uninstall'

if ($Launch) {
    Write-Step 'Starting application'
    Start-Process -FilePath $InstalledExe -WorkingDirectory $InstallPath
}

Stop-Collector
