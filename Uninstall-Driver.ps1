[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# The service name and install directory come from the Brainfuck program
# ("g29ctl install-plan"), exactly as Install-Driver.ps1 used them.
function Get-InstallPlan([string]$Executable) {
    $lines = & $Executable install-plan
    if ($LASTEXITCODE -ne 0) {
        throw 'g29ctl could not describe the installation.'
    }

    $plan = @{}
    foreach ($line in $lines) {
        $key, $value = $line -split ' ', 2
        $plan[$key] = $value
    }

    if ([string]::IsNullOrEmpty($plan['service'])) {
        throw "The installation plan has no 'service'."
    }

    if ($plan['directory'] -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') {
        throw 'The installation plan names an unexpected install directory.'
    }

    return $plan
}

if (-not (Test-Administrator)) {
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    $process = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs -Wait -PassThru
    exit $process.ExitCode
}

$builtExecutable = Join-Path $PSScriptRoot 'artifacts\bin\g29ctl.exe'
if (-not (Test-Path -LiteralPath $builtExecutable)) {
    & (Join-Path $PSScriptRoot 'build.ps1')
}

$plan = Get-InstallPlan $builtExecutable
$serviceName = $plan['service']
$installDirectory = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles $plan['directory']))
if (-not [String]::Equals([IO.Path]::GetDirectoryName($installDirectory), [IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to remove unexpected install directory.'
}

$installedExecutable = Join-Path $installDirectory 'g29ctl.exe'
if (Test-Path -LiteralPath $installedExecutable) {
    & $installedExecutable ffb-unregister
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not remove the DirectInput force feedback registration.'
    }
}

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -ne $service) {
    if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Stop-Service -Name $serviceName -Force
        $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(15))
    }

    & sc.exe delete $serviceName | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not delete Windows service $serviceName."
    }
}

$deadline = [DateTime]::UtcNow.AddSeconds(10)
while ($null -ne (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $deadline) {
    Start-Sleep -Milliseconds 200
}

if (Test-Path -LiteralPath $installDirectory) {
    Remove-Item -LiteralPath $installDirectory -Recurse -Force
}

if ([System.Diagnostics.EventLog]::SourceExists($serviceName)) {
    [System.Diagnostics.EventLog]::DeleteEventSource($serviceName)
}

Write-Host "[EXORCISM COMPLETE] $serviceName has departed this Windows installation."
