#Requires -Version 5.1
<#
.SYNOPSIS
    Builds Tax Intelligence Studio and deploys it to a remote Windows machine.

.DESCRIPTION
    Publishes the WPF desktop application as a self-contained Windows build on
    this machine, then installs it on the destination computer over PowerShell
    Remoting (WinRM).

    The destination is assumed to be remote. The destination does not need the
    .NET SDK, the .NET runtime, the source tree, or any build tooling: a
    self-contained payload is copied over the remoting session.

    Use -Local only when the destination is deliberately this same machine.

    The repository root is resolved from this script's own location, so the
    script can be invoked from any working directory.

.PARAMETER ComputerName
    Destination Windows computer. Required unless -Local is specified.

.PARAMETER Credential
    Credential used to connect to the destination. Prompted for when omitted
    and the current context cannot authenticate.

.PARAMETER Local
    Deploy to this machine instead of a remote destination.

.PARAMETER UseSSL
    Connect over HTTPS (WinRM 5986) instead of HTTP.

.PARAMETER Port
    Override the WinRM port.

.PARAMETER Authentication
    WinRM authentication mechanism. Defaults to Default. Use Negotiate or
    CredSSP for non-domain destinations as your environment requires.

.PARAMETER Runtime
    Target runtime identifier. Auto-detected from the destination's OS
    architecture when omitted.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER InstallPath
    Installation directory on the destination. Defaults to
    %LOCALAPPDATA%\Programs\TaxIntelligenceStudio, resolved on the destination.

.PARAMETER FrameworkDependent
    Publish a framework-dependent build. The destination must then already
    have the .NET 9 Desktop Runtime installed. Not recommended for remote
    destinations.

.PARAMETER NoShortcut
    Skip creating the Start Menu shortcut on the destination.

.PARAMETER DesktopShortcut
    Also create a Desktop shortcut on the destination.

.PARAMETER RunTests
    Run the unit test suite on this machine before publishing and abort
    deployment on failure.

.PARAMETER Uninstall
    Remove the installed application and its shortcuts from the destination,
    then exit. No build is performed.

.EXAMPLE
    .\scripts\Deploy-Windows.ps1 -ComputerName TAX-WS01

.EXAMPLE
    .\scripts\Deploy-Windows.ps1 -ComputerName TAX-WS01 -Credential (Get-Credential) -RunTests -DesktopShortcut

.EXAMPLE
    .\scripts\Deploy-Windows.ps1 -ComputerName TAX-WS01 -UseSSL -Uninstall

.EXAMPLE
    .\scripts\Deploy-Windows.ps1 -Local
#>
[CmdletBinding(DefaultParameterSetName = 'Remote')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Remote', Position = 0)]
    [string]$ComputerName,

    [Parameter(ParameterSetName = 'Remote')]
    [System.Management.Automation.PSCredential]$Credential,

    [Parameter(ParameterSetName = 'Remote')]
    [switch]$UseSSL,

    [Parameter(ParameterSetName = 'Remote')]
    [int]$Port,

    [Parameter(ParameterSetName = 'Remote')]
    [ValidateSet('Default', 'Basic', 'Negotiate', 'NegotiateWithImplicitCredential', 'Credssp', 'Digest', 'Kerberos')]
    [string]$Authentication = 'Default',

    [Parameter(Mandatory, ParameterSetName = 'Local')]
    [switch]$Local,

    [ValidateSet('win-x64', 'win-arm64', 'win-x86')]
    [string]$Runtime,

    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [string]$InstallPath,

    [switch]$FrameworkDependent,
    [switch]$NoShortcut,
    [switch]$DesktopShortcut,
    [switch]$RunTests,
    [switch]$Uninstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$AppName = 'Tax Intelligence Studio'
$ExeName = 'TaxIntelligenceStudio.Desktop.exe'
$ProcessName = 'TaxIntelligenceStudio.Desktop'

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

# Runs a script block on the destination, whether that destination is a remote
# session or this machine.
function Invoke-OnDestination {
    param(
        [Parameter(Mandatory)][scriptblock]$ScriptBlock,
        [object[]]$ArgumentList = @()
    )

    if ($script:Session) {
        return Invoke-Command -Session $script:Session -ScriptBlock $ScriptBlock -ArgumentList $ArgumentList
    }

    return & $ScriptBlock @ArgumentList
}

$isRemote = $PSCmdlet.ParameterSetName -eq 'Remote'
$destinationLabel = if ($isRemote) { $ComputerName } else { "$env:COMPUTERNAME (local)" }
$script:Session = $null

$RepoRoot = Split-Path -Parent $PSScriptRoot
$DesktopProject = Join-Path $RepoRoot 'src\TaxIntelligenceStudio.Desktop\TaxIntelligenceStudio.Desktop.csproj'
$Solution = Join-Path $RepoRoot 'TaxIntelligenceStudio.sln'

# --- Destination-side operations -------------------------------------------
# These run on the destination machine, so they must be self-contained and
# must not reference variables from this scope.

$ResolveDestinationInfo = {
    param([string]$RequestedInstallPath, [string]$AppName)

    $architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    $installPath = if ([string]::IsNullOrWhiteSpace($RequestedInstallPath)) {
        Join-Path $env:LOCALAPPDATA 'Programs\TaxIntelligenceStudio'
    }
    else {
        [System.Environment]::ExpandEnvironmentVariables($RequestedInstallPath)
    }

    [pscustomobject]@{
        ComputerName      = $env:COMPUTERNAME
        UserName          = "$env:USERDOMAIN\$env:USERNAME"
        OSArchitecture    = $architecture
        OSCaption         = (Get-CimInstance Win32_OperatingSystem).Caption
        OSVersion         = [System.Environment]::OSVersion.Version.ToString()
        InstallPath       = $installPath
        DataPath          = Join-Path $env:LOCALAPPDATA 'TaxIntelligenceStudio'
        StartMenuShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) "$AppName.lnk"
        DesktopShortcut   = Join-Path ([Environment]::GetFolderPath('Desktop')) "$AppName.lnk"
    }
}

$AssertNotRunning = {
    param([string]$ProcessName, [string]$AppName)

    $running = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        $ids = ($running | ForEach-Object { $_.Id }) -join ', '
        throw "$AppName is running on $env:COMPUTERNAME (PID $ids). Close it and re-run this script."
    }
}

$PrepareInstallDirectory = {
    param([string]$InstallPath)

    if (Test-Path -LiteralPath $InstallPath) {
        Remove-Item -LiteralPath $InstallPath -Recurse -Force
    }

    New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
    return $InstallPath
}

$CompleteInstall = {
    param(
        [string]$InstallPath,
        [string]$ExeName,
        [string]$AppName,
        [bool]$CreateStartMenuShortcut,
        [bool]$CreateDesktopShortcut,
        [string]$StartMenuShortcut,
        [string]$DesktopShortcut
    )

    $exePath = Join-Path $InstallPath $ExeName
    if (-not (Test-Path -LiteralPath $exePath)) {
        throw "Installation failed on $env:COMPUTERNAME - '$exePath' is missing."
    }

    $createdShortcuts = @()
    if ($CreateStartMenuShortcut -or $CreateDesktopShortcut) {
        $shell = New-Object -ComObject WScript.Shell
        try {
            $targets = @()
            if ($CreateStartMenuShortcut) { $targets += $StartMenuShortcut }
            if ($CreateDesktopShortcut) { $targets += $DesktopShortcut }

            foreach ($linkPath in $targets) {
                $parent = Split-Path -Parent $linkPath
                if (-not (Test-Path -LiteralPath $parent)) {
                    New-Item -ItemType Directory -Path $parent -Force | Out-Null
                }

                $shortcut = $shell.CreateShortcut($linkPath)
                $shortcut.TargetPath = $exePath
                $shortcut.WorkingDirectory = $InstallPath
                $shortcut.IconLocation = $exePath
                $shortcut.Description = "$AppName - local IRS corpus and tax workpaper studio"
                $shortcut.Save()
                $createdShortcuts += $linkPath
            }
        }
        finally {
            [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
        }
    }

    $sizeMb = [math]::Round(((Get-ChildItem -LiteralPath $InstallPath -Recurse -File |
                Measure-Object -Property Length -Sum).Sum / 1MB), 1)

    [pscustomobject]@{
        ExePath   = $exePath
        SizeMb    = $sizeMb
        Shortcuts = $createdShortcuts
    }
}

$RemoveInstall = {
    param([string]$InstallPath, [string]$StartMenuShortcut, [string]$DesktopShortcut)

    $removed = @()
    foreach ($link in @($StartMenuShortcut, $DesktopShortcut)) {
        if (Test-Path -LiteralPath $link) {
            Remove-Item -LiteralPath $link -Force
            $removed += $link
        }
    }

    $removedInstall = $false
    if (Test-Path -LiteralPath $InstallPath) {
        Remove-Item -LiteralPath $InstallPath -Recurse -Force
        $removedInstall = $true
    }

    [pscustomobject]@{
        RemovedShortcuts = $removed
        RemovedInstall   = $removedInstall
    }
}

# --- Deployment -------------------------------------------------------------

try {
    Write-Step "$(if ($Uninstall) { 'Uninstalling' } else { 'Deploying' }) $AppName"
    Write-Detail "Destination: $destinationLabel"

    if ($isRemote) {
        Write-Step "Connecting to $ComputerName"

        $sessionParams = @{
            ComputerName   = $ComputerName
            Authentication = $Authentication
            ErrorAction    = 'Stop'
        }
        if ($Credential) { $sessionParams.Credential = $Credential }
        if ($UseSSL) { $sessionParams.UseSSL = $true }
        if ($PSBoundParameters.ContainsKey('Port')) { $sessionParams.Port = $Port }

        try {
            $script:Session = New-PSSession @sessionParams
        }
        catch {
            throw ("Could not open a PowerShell Remoting session to '$ComputerName': $($_.Exception.Message)`n" +
                "Verify WinRM is enabled on the destination (Enable-PSRemoting -Force) and that this machine trusts it " +
                '(for example: Set-Item WSMan:\localhost\Client\TrustedHosts -Value ''' + $ComputerName + ''' -Concatenate).')
        }

        Write-Detail "Session established (Id $($script:Session.Id))."
    }

    $destination = Invoke-OnDestination -ScriptBlock $ResolveDestinationInfo -ArgumentList @($InstallPath, $AppName)

    Write-Detail "Destination host: $($destination.ComputerName)"
    Write-Detail "Destination user: $($destination.UserName)"
    Write-Detail "Destination OS: $($destination.OSCaption) ($($destination.OSVersion), $($destination.OSArchitecture))"
    Write-Detail "Install path: $($destination.InstallPath)"

    if ([Version]$destination.OSVersion -lt [Version]'10.0') {
        throw "Windows 10 or later is required on the destination, but '$($destination.OSVersion)' was reported."
    }

    if ($Uninstall) {
        Invoke-OnDestination -ScriptBlock $AssertNotRunning -ArgumentList @($ProcessName, $AppName)

        $result = Invoke-OnDestination -ScriptBlock $RemoveInstall -ArgumentList @(
            $destination.InstallPath,
            $destination.StartMenuShortcut,
            $destination.DesktopShortcut
        )

        foreach ($link in @($result.RemovedShortcuts)) {
            Write-Detail "Removed shortcut: $link"
        }

        if ($result.RemovedInstall) {
            Write-Detail "Removed install directory: $($destination.InstallPath)"
        }
        else {
            Write-Detail "No install directory at: $($destination.InstallPath)"
        }

        Write-Host ''
        Write-Host "$AppName removed from $($destination.ComputerName)." -ForegroundColor Green
        Write-Note "Local data at $($destination.DataPath) was preserved."
        return
    }

    if (-not (Test-Path -LiteralPath $DesktopProject)) {
        throw "Desktop project not found at '$DesktopProject'. Run this script from a full clone of the repository."
    }

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'The .NET SDK was not found on PATH of this build machine. Install the .NET 9 SDK from https://dotnet.microsoft.com/download and re-run this script.'
    }

    $sdkVersion = (& dotnet --version).Trim()
    if ([Version](($sdkVersion -split '-')[0]) -lt [Version]'9.0.0') {
        throw "The .NET 9 SDK is required on this build machine, but '$sdkVersion' is active."
    }

    if (-not $Runtime) {
        $Runtime = switch ($destination.OSArchitecture) {
            'Arm64' { 'win-arm64' }
            'X64' { 'win-x64' }
            'X86' { 'win-x86' }
            default { throw "Unsupported destination architecture '$($destination.OSArchitecture)'. Pass -Runtime explicitly." }
        }
    }

    $selfContained = -not $FrameworkDependent
    Write-Detail "Build machine SDK: $sdkVersion"
    Write-Detail "Runtime: $Runtime"
    Write-Detail "Configuration: $Configuration"
    Write-Detail ("Deployment mode: " + $(if ($selfContained) { 'self-contained' } else { 'framework-dependent' }))

    if ($isRemote -and -not $selfContained) {
        Write-Note 'Framework-dependent deployment requires the .NET 9 Desktop Runtime to already be installed on the destination.'
    }

    Invoke-OnDestination -ScriptBlock $AssertNotRunning -ArgumentList @($ProcessName, $AppName)

    if ($RunTests) {
        Write-Step 'Running unit tests on the build machine'
        & dotnet test $Solution --configuration $Configuration --nologo
        if ($LASTEXITCODE -ne 0) {
            throw "Unit tests failed with exit code $LASTEXITCODE. Deployment aborted."
        }
        Write-Detail 'All tests passed.'
    }

    # Publish to a staging folder first so a failed build never leaves a
    # partially overwritten installation on the destination.
    $stagingPath = Join-Path ([System.IO.Path]::GetTempPath()) ('TaxIntelligenceStudio-publish-' + [Guid]::NewGuid().ToString('N'))

    try {
        Write-Step 'Publishing desktop application'

        $publishArgs = @(
            'publish', $DesktopProject,
            '--configuration', $Configuration,
            '--runtime', $Runtime,
            '--self-contained', $selfContained.ToString().ToLowerInvariant(),
            '--output', $stagingPath,
            '--nologo',
            '-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-p:EnableWindowsTargeting=true',
            '-p:DebugType=none'
        )

        & dotnet @publishArgs
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed with exit code $LASTEXITCODE."
        }

        if (-not (Test-Path -LiteralPath (Join-Path $stagingPath $ExeName))) {
            throw "Publish completed but '$ExeName' was not produced in '$stagingPath'."
        }

        $payloadMb = [math]::Round(((Get-ChildItem -LiteralPath $stagingPath -Recurse -File |
                    Measure-Object -Property Length -Sum).Sum / 1MB), 1)
        Write-Detail "Payload staged ($payloadMb MB)."

        Write-Step "Installing on $($destination.ComputerName)"
        Invoke-OnDestination -ScriptBlock $AssertNotRunning -ArgumentList @($ProcessName, $AppName)
        Invoke-OnDestination -ScriptBlock $PrepareInstallDirectory -ArgumentList @($destination.InstallPath) | Out-Null

        if ($script:Session) {
            Copy-Item -Path (Join-Path $stagingPath '*') -Destination $destination.InstallPath `
                -ToSession $script:Session -Recurse -Force
        }
        else {
            Copy-Item -Path (Join-Path $stagingPath '*') -Destination $destination.InstallPath -Recurse -Force
        }

        $installResult = Invoke-OnDestination -ScriptBlock $CompleteInstall -ArgumentList @(
            $destination.InstallPath,
            $ExeName,
            $AppName,
            [bool](-not $NoShortcut),
            [bool]$DesktopShortcut,
            $destination.StartMenuShortcut,
            $destination.DesktopShortcut
        )

        Write-Detail "Installed $($installResult.SizeMb) MB to $($destination.InstallPath)"
        foreach ($link in @($installResult.Shortcuts)) {
            Write-Detail "Shortcut: $link"
        }

        Write-Host ''
        Write-Host "$AppName deployed to $($destination.ComputerName)." -ForegroundColor Green
        Write-Host "  Launch: $($installResult.ExePath)"
        Write-Host "  Data:   $($destination.DataPath)"
        if ($isRemote) {
            Write-Host "  Remove: .\scripts\Deploy-Windows.ps1 -ComputerName $ComputerName -Uninstall"
        }
        else {
            Write-Host '  Remove: .\scripts\Deploy-Windows.ps1 -Local -Uninstall'
        }

        if ($isRemote) {
            Write-Host ''
            Write-Note 'Sign in to the destination interactively to start the application. A WinRM session has no desktop, so the GUI cannot be launched from here.'
        }
    }
    finally {
        if (Test-Path -LiteralPath $stagingPath) {
            Remove-Item -LiteralPath $stagingPath -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
finally {
    if ($script:Session) {
        Remove-PSSession -Session $script:Session -ErrorAction SilentlyContinue
    }
}
