[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1')
# Test-only native pieces: the VM, guard and shared memory as a DLL, the driver
# with its serialization export, and a DirectInput smoke client per bitness.
& (Join-Path $PSScriptRoot 'native.ps1') -Target testvm, drivertest

# The tests are C#: they carry a reference C# VM (tests\harness) and run every
# scenario on it and on the native VM.
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testOutput = Join-Path $PSScriptRoot 'artifacts\bin\G29.Tests.exe'
$program = Join-Path $PSScriptRoot 'src\brainfuck\g29-main.bf'
$assemblerSources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'tools\BfAsm') -Filter '*.cs' -File | Where-Object Name -ne 'Program.cs' | ForEach-Object FullName)
$testSources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'tests') -Recurse -Filter '*.cs' -File | ForEach-Object FullName)

$arguments = @(
    '/nologo',
    '/target:exe',
    '/optimize+',
    '/platform:x64',
    '/warn:4',
    '/warnaserror+',
    '/reference:System.dll',
    '/reference:System.Core.dll',
    "/resource:$program,G29.Brainfuck.g29-main.bf",
    "/out:$testOutput"
) + $assemblerSources + $testSources

& $compiler $arguments
if ($LASTEXITCODE -ne 0) {
    throw "Test compilation failed with exit code $LASTEXITCODE"
}

& $testOutput
if ($LASTEXITCODE -ne 0) {
    throw "Tests failed with exit code $LASTEXITCODE"
}

Write-Host '[ORACLE APPROVES] All tests returned from the abyss intact.'
