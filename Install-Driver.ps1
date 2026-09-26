[CmdletBinding()]
param(
    # Passed to g29ctl, which checks them (40..900 and 0..100) and supplies the
    # defaults when they are omitted.
    [System.Nullable[int]]$Range,
    [System.Nullable[int]]$AutoCenter,
    # Install the service without the DirectInput force feedback driver, for
    # example while another vendor's driver owns the G29 registration. Without
    # it, a failed registration fails (and rolls back) the installation.
    [switch]$SkipForceFeedback,
    # Internal: the elevated phase, given the SHA-256 of each binary the
    # unprivileged phase built and checked.
    [string]$ArtifactHashes
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'tools\InstallCommon.ps1')
trap { Write-Host $_ -ForegroundColor Red; Wait-ConsoleClose; break }

$artifacts = Join-Path $PSScriptRoot 'artifacts\bin'
$options = @()
if ($null -ne $Range) { $options += @('--range', "$Range") }
if ($null -ne $AutoCenter) { $options += @('--autocenter', "$AutoCenter") }

if (-not $ArtifactHashes) {
    # First phase: hash and plan the binaries in artifacts\bin (building them
    # only when they are missing), then elevate for the installation. Testing,
    # the device test included, is test.ps1's job.
    if (Test-Administrator) {
        Write-Warning 'This PowerShell is elevated; the installation continues in it. A build, if needed, also runs with administrator rights.'
    }

    if (@($script:G29ShippedFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $artifacts $_)) }).Count) {
        & (Join-Path $PSScriptRoot 'build.ps1')
    }

    $hashes = Get-ArtifactHashes $artifacts
    # fail before the UAC prompt when the options or the plan are wrong
    $lines = @(& (Join-Path $artifacts 'g29ctl.exe') install-plan @options)
    if ($LASTEXITCODE -ne 0) {
        throw 'g29ctl refused the installation options.'
    }

    $null = ConvertFrom-InstallPlan $lines

    # Invoke-Elevated runs this as PowerShell code, where ';' separates statements.
    $arguments = "-ArtifactHashes '$hashes'"
    if ($null -ne $Range) { $arguments += " -Range $Range" }
    if ($null -ne $AutoCenter) { $arguments += " -AutoCenter $AutoCenter" }
    if ($SkipForceFeedback) { $arguments += ' -SkipForceFeedback' }
    if (-not (Test-Administrator)) {
        Invoke-Elevated $PSCommandPath $arguments
    }

    # already elevated: continue in this process
    $ArtifactHashes = $hashes
}

# Elevated phase: only the machine-wide transaction.
if (-not (Test-Administrator)) {
    throw 'The installation step needs administrator rights.'
}

$context = @{
    SourceDirectory   = $artifacts
    Hashes            = ConvertFrom-ArtifactHashes $ArtifactHashes
    Options           = $options
    SkipForceFeedback = [bool]$SkipForceFeedback
    InstallDirectory  = Get-G29InstallDirectory
    LicensePath       = Join-Path $PSScriptRoot 'LICENSE'
}

try {
    $null = Invoke-G29Install $context (New-WindowsMachine)
} catch {
    Write-Warning "Installation failed and was rolled back: $($_.Exception.Message)"
    throw
}

if ($SkipForceFeedback) {
    Write-Warning 'DirectInput force feedback was not registered (-SkipForceFeedback); games will not receive force feedback from this driver.'
}

# Verify the result as Windows sees it.
$installed = Join-Path $context.InstallDirectory 'g29ctl.exe'
$service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$script:G29ServiceName'"
if (-not $service -or -not (Test-OwnedServicePath $service.PathName $installed)) {
    throw "Installation finished, but the $script:G29ServiceName service does not run $installed. Run Uninstall-Driver.ps1, then install again."
}

Write-Host "Installed $script:G29ServiceName in $($context.InstallDirectory). The service starts when a G29 is connected and stops 15 seconds after the last one is removed."
Wait-ConsoleClose
