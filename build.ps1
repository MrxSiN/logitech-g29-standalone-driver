[CmdletBinding()]
param(
    [switch]$Clean
)

# Builds the shipping binaries: the native bridge carrying the Brainfuck program
# (g29ctl.exe, g29ffb64.dll, g29ffb32.dll). Needs Visual Studio or the Build Tools
# with the C++ workload (see native.ps1).
$ErrorActionPreference = 'Stop'
$outputDirectory = Join-Path $PSScriptRoot 'artifacts\bin'
if ($Clean -and (Test-Path -LiteralPath $outputDirectory)) {
    Remove-Item -LiteralPath $outputDirectory -Recurse -Force
}

& (Join-Path $PSScriptRoot 'native.ps1') -Target cli, driver64, driver32
Write-Host "[THE COMPILER HAS SPOKEN] Materialized $outputDirectory\g29ctl.exe, g29ffb64.dll and g29ffb32.dll"
