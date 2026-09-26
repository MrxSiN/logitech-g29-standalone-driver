[CmdletBinding()]
param(
    # Delete all build output (binaries, objects, generated C) first.
    [switch]$Clean
)

# Builds the shipping binaries: g29ctl.exe (x64), g29ffb64.dll (x64) and
# g29ffb32.dll (x86). src\brainfuck\g29-main.bf is validated and compiled ahead
# of time into native code; the binaries contain no Brainfuck interpreter.
# Requires Visual Studio or the Build Tools with the C++ workload and the
# .NET Framework 4 C# compiler (see native.ps1).
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Clean) {
    foreach ($directory in @('artifacts\bin', 'artifacts\obj')) {
        $path = Join-Path $PSScriptRoot $directory
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }
}

& (Join-Path $PSScriptRoot 'native.ps1') -Target cli, driver64, driver32
Write-Host 'Build succeeded: artifacts\bin\g29ctl.exe, g29ffb64.dll, g29ffb32.dll'
