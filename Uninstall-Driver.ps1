[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'tools\InstallCommon.ps1')
trap { Write-Host $_ -ForegroundColor Red; Wait-ConsoleClose; break }

# The service name and install directory are the fixed identity in
# tools\InstallCommon.ps1; the registration is removed by the installed
# g29ctl.exe itself, and from the other accounts' hives by the marker-checked
# sweep there. Nothing from the source tree runs elevated.
if (-not (Test-Administrator)) {
    Invoke-Elevated $PSCommandPath ''
}

Remove-G29Installation (New-WindowsMachine) (Get-G29InstallDirectory)

Write-Host "Removed ${script:G29ServiceName}: service, installed files and DirectInput registration."
Wait-ConsoleClose
