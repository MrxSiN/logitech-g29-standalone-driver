[CmdletBinding()]
param()

# Assembles src\brainfuck\g29-main.bfa into src\brainfuck\g29-main.bf, the
# Brainfuck program the bridge runs. Commit both; test.ps1 fails if they drift.
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$outputDirectory = Join-Path $PSScriptRoot 'artifacts\bin'
$assembler = Join-Path $outputDirectory 'bfasm.exe'
$source = Join-Path $PSScriptRoot 'src\brainfuck\g29-main.bfa'
$program = Join-Path $PSScriptRoot 'src\brainfuck\g29-main.bf'
$map = Join-Path $outputDirectory 'g29-main.map'

if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw "The .NET Framework C# compiler was not found at $compiler"
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'tools\BfAsm') -Filter '*.cs' -File | ForEach-Object FullName)
$arguments = @(
    '/nologo',
    '/target:exe',
    '/optimize+',
    '/warn:4',
    '/warnaserror+',
    '/reference:System.dll',
    '/reference:System.Core.dll',
    "/out:$assembler"
) + $sources

& $compiler $arguments
if ($LASTEXITCODE -ne 0) {
    throw "Assembler compilation failed with exit code $LASTEXITCODE"
}

& $assembler $source $program $map
if ($LASTEXITCODE -ne 0) {
    throw "Assembly failed with exit code $LASTEXITCODE"
}
