[CmdletBinding()]
param(
    # Passed to g29ctl, which checks them (40..900 and 0..100) and supplies the
    # defaults when they are omitted.
    [System.Nullable[int]]$Range,
    [System.Nullable[int]]$AutoCenter
)

$ErrorActionPreference = 'Stop'

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# The installation policy (service name, display text, arguments, device
# trigger, recovery, install directory) is decided by the Brainfuck program:
# "g29ctl install-plan" prints one "key value" line per setting.
function Get-InstallPlan([string]$Executable, [string[]]$Options) {
    $lines = & $Executable install-plan @Options
    if ($LASTEXITCODE -ne 0) {
        throw 'g29ctl refused the installation options.'
    }

    $plan = @{}
    foreach ($line in $lines) {
        $key, $value = $line -split ' ', 2
        $plan[$key] = $value
    }

    foreach ($key in @('service', 'display', 'description', 'directory', 'startup', 'arguments', 'trigger', 'failure-reset', 'failure-actions', 'start-after-install')) {
        if (-not $plan.ContainsKey($key) -or [string]::IsNullOrEmpty($plan[$key])) {
            throw "The installation plan has no '$key'."
        }
    }

    if ($plan['directory'] -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') {
        throw 'The installation plan names an unexpected install directory.'
    }

    return $plan
}

$options = @()
if ($null -ne $Range) { $options += @('--range', "$Range") }
if ($null -ne $AutoCenter) { $options += @('--autocenter', "$AutoCenter") }

if (-not (Test-Administrator)) {
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    if ($null -ne $Range) { $arguments += " -Range $Range" }
    if ($null -ne $AutoCenter) { $arguments += " -AutoCenter $AutoCenter" }
    $process = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs -Wait -PassThru
    exit $process.ExitCode
}

& (Join-Path $PSScriptRoot 'build.ps1')
$builtExecutable = Join-Path $PSScriptRoot 'artifacts\bin\g29ctl.exe'
$builtDrivers = @('g29ffb64.dll', 'g29ffb32.dll') | ForEach-Object { Join-Path $PSScriptRoot "artifacts\bin\$_" }
$plan = Get-InstallPlan $builtExecutable $options
$serviceName = $plan['service']
$installDirectory = Join-Path $env:ProgramFiles $plan['directory']
$installedExecutable = Join-Path $installDirectory 'g29ctl.exe'
$binaryPath = "`"$installedExecutable`" " + $plan['arguments']

$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -ne $existingService) {
    if ($existingService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Stop-Service -Name $serviceName -Force
        $existingService.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(15))
    }

    & sc.exe delete $serviceName | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not replace Windows service $serviceName."
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ($null -ne (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 200
    }
}

New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
Copy-Item -LiteralPath $builtExecutable -Destination $installedExecutable -Force
foreach ($driver in $builtDrivers) {
    Copy-Item -LiteralPath $driver -Destination (Join-Path $installDirectory (Split-Path -Leaf $driver)) -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination (Join-Path $installDirectory 'LICENSE') -Force

New-Service -Name $serviceName -BinaryPathName $binaryPath -DisplayName $plan['display'] -Description $plan['description'] -StartupType $plan['startup'] | Out-Null
& sc.exe triggerinfo $serviceName $plan['trigger'] | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Could not configure the device trigger for Windows service $serviceName."
}

& sc.exe failure $serviceName reset= $plan['failure-reset'] actions= $plan['failure-actions'] | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Could not configure recovery for Windows service $serviceName."
}

# DirectInput force feedback driver: COM class (64-bit and 32-bit views) plus the
# OEMForceFeedback registration for the native G29. Removed by Uninstall-Driver.ps1.
& $installedExecutable ffb-register
if ($LASTEXITCODE -ne 0) {
    Write-Warning 'DirectInput force feedback was not registered; games will not receive force feedback. Run g29ctl doctor for details.'
}

if ($plan['start-after-install'] -eq 'yes') {
    Start-Service -Name $serviceName
}

Write-Host "[SUMMONING COMPLETE] $serviceName sleeps until a G29 crosses the USB threshold, and returns to the crypt 15 seconds after it leaves. G HUB may remain peacefully unopened."
