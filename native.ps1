[CmdletBinding()]
param(
    # Build targets. The shipping binaries are cli (g29ctl.exe, x64), driver64
    # (g29ffb64.dll) and driver32 (g29ffb32.dll). The rest serve the tests.
    [ValidateSet('program', 'cli', 'driver64', 'driver32', 'testhost', 'drivertest', 'diprobe', 'analyze', 'asan', 'hardening')]
    [string[]]$Target = @('cli', 'driver64', 'driver32'),

    # Directory of test programs (*.bf) for testhost and asan, written by
    # G29.Tests.exe --write-programs (test.ps1 does this).
    [string]$TestPrograms
)

# Native build. The Brainfuck program is validated and compiled ahead of time
# by tools/BfAot into C (artifacts\obj\generated), which the Visual Studio C
# compiler builds together with the bridge. Warnings are errors.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = $PSScriptRoot
$outputDirectory = Join-Path $projectRoot 'artifacts\bin'
$objectDirectory = Join-Path $projectRoot 'artifacts\obj'
$generatedDirectory = Join-Path $objectDirectory 'generated'
$commonDirectory = Join-Path $projectRoot 'src\bridge\common'
$programSource = Join-Path $projectRoot 'src\brainfuck\g29-main.bf'
# The generated program is split into parts the compiler builds in parallel.
$programPartCount = 16
New-Item -ItemType Directory -Path $outputDirectory, $objectDirectory, $generatedDirectory -Force | Out-Null

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
    throw 'Visual Studio 2019 or later (or the Build Tools) with the "Desktop development with C++" workload is required.'
}

$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) {
    throw 'No Visual Studio installation with the x86/x64 C++ tools (Microsoft.VisualStudio.Component.VC.Tools.x86.x64) was found.'
}

$vcvars = Join-Path $installation 'VC\Auxiliary\Build\vcvarsall.bat'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) {
    throw "The .NET Framework 4 C# compiler was not found at $csc."
}

$common = @('/nologo', '/W4', '/WX', '/O2', '/GS', '/guard:cf', '/Brepro', '/DUNICODE', '/D_UNICODE', '/DWIN32_LEAN_AND_MEAN', '/D_CRT_SECURE_NO_WARNINGS')
$installer = Split-Path -Parent $vswhere
$libraries = 'kernel32.lib user32.lib advapi32.lib setupapi.lib hid.lib winmm.lib ole32.lib'
$bridgeSources = @('frames.c', 'session.c', 'hid.c', 'guard.c', 'lease.c', 'system.c', 'bfrt.c') | ForEach-Object { Join-Path $commonDirectory $_ }

# Runs a command in the compiler environment for the architecture, in Directory.
function Invoke-Compiler([string]$Architecture, [string]$Directory, [string]$Command) {
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    # compiler output goes to the host, never into a function's return value
    & cmd.exe /c "(set `"PATH=%PATH%;$installer`" && `"$vcvars`" $Architecture >nul && cd /d `"$Directory`" && $Command) 2>&1" | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Native compilation failed ($Architecture) in ${Directory}: $Command"
    }
}

function Join-Quoted([string[]]$Paths) {
    return ($Paths | ForEach-Object { '"' + $_ + '"' }) -join ' '
}

function Test-Stale([string]$Output, [string[]]$Inputs) {
    if (-not (Test-Path -LiteralPath $Output)) {
        return $true
    }

    $built = (Get-Item -LiteralPath $Output).LastWriteTimeUtc
    return [bool]($Inputs | Where-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc -gt $built })
}

# tools\BfAot -> artifacts\bin\BfAot.exe
function Build-Compiler {
    $compiler = Join-Path $outputDirectory 'BfAot.exe'
    $sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tools\BfAot') -Filter '*.cs' -File | ForEach-Object FullName)
    if (Test-Stale $compiler $sources) {
        & $csc /nologo /target:exe /optimize+ /warn:4 /warnaserror+ /reference:System.dll /reference:System.Core.dll "/out:$compiler" $sources | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "The Brainfuck compiler (tools\BfAot) failed to build (exit code $LASTEXITCODE)."
        }
    }

    return $compiler
}

# Validates Brainfuck and writes <Base>.h and <Base>_NN.c. Unchanged output is
# not rewritten, so its objects stay current.
function Invoke-BfCompiler([string]$Base, [int]$Parts, [string[]]$Arguments) {
    $compiler = Build-Compiler
    & $compiler compile $Base --parts $Parts @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Brainfuck compilation failed (exit code $LASTEXITCODE); see the message above."
    }

    return @(0..($Parts - 1) | ForEach-Object { '{0}_{1:D2}.c' -f $Base, $_ })
}

# Compiles generated parts to objects in artifacts\obj\<Name>-<Architecture>,
# rebuilding only parts whose source or runtime header changed.
function Build-GeneratedObjects([string]$Name, [string]$Architecture, [string[]]$Parts, [string[]]$Options) {
    $directory = Join-Path $objectDirectory "$Name-$Architecture"
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $headers = @((Join-Path $commonDirectory 'bfrt.h'), ($Parts[0] -replace '_00\.c$', '.h'))
    $stale = @($Parts | Where-Object { Test-Stale (Join-Path $directory ([IO.Path]::GetFileNameWithoutExtension($_) + '.obj')) (@($_) + $headers) })
    if ($stale.Count -gt 0) {
        Write-Host "Compiling $($stale.Count) generated $Name part(s) for $Architecture..."
        $flags = ($Options + @('/MP', '/c', "/I`"$commonDirectory`"", "/I`"$generatedDirectory`"")) -join ' '
        Invoke-Compiler $Architecture $directory "cl $flags $(Join-Quoted $stale)"
    }

    return @($Parts | ForEach-Object { Join-Path $directory ([IO.Path]::GetFileNameWithoutExtension($_) + '.obj') })
}

$needsProgram = @($Target | Where-Object { $_ -in @('program', 'cli', 'driver64', 'driver32', 'testhost', 'drivertest', 'asan') }).Count -gt 0
if ($needsProgram) {
    $programParts = Invoke-BfCompiler (Join-Path $generatedDirectory 'g29_program') $programPartCount @('--stats', (Join-Path $generatedDirectory 'g29_program.stats.txt'), "g29_program_run=$programSource")
}

function Get-ProgramObjects([string]$Architecture) {
    return Build-GeneratedObjects 'program' $Architecture $programParts $common
}

function Get-TestProgramParts {
    if (-not $TestPrograms -or -not (Test-Path -LiteralPath $TestPrograms -PathType Container)) {
        throw 'The testhost and asan targets need -TestPrograms <directory> (written by G29.Tests.exe --write-programs; test.ps1 does this).'
    }

    return Invoke-BfCompiler (Join-Path $generatedDirectory 'test_programs') 4 @('--table', 'bf_test_programs', '--directory', $TestPrograms)
}

if ($Target -contains 'testhost') {
    $objects = (Get-ProgramObjects 'x64') + (Build-GeneratedObjects 'testprograms' 'x64' (Get-TestProgramParts) $common)
    $sources = Join-Quoted ($bridgeSources + @(Join-Path $projectRoot 'src\bridge\testhost\testhost.c'))
    Invoke-Compiler 'x64' (Join-Path $objectDirectory 'testhost') "cl $($common -join ' ') /LD $sources $(Join-Quoted $objects) /Fe`"$outputDirectory\g29testhost.dll`" /link /NOLOGO kernel32.lib advapi32.lib setupapi.lib hid.lib"
}

if ($Target -contains 'cli') {
    $objects = Join-Path $objectDirectory 'cli'
    $cliDirectory = Join-Path $projectRoot 'src\bridge\cli-service'
    $sources = Join-Quoted ($bridgeSources + @(Join-Path $cliDirectory 'main.c'))
    Invoke-Compiler 'x64' $objects ("mc -h . -r . `"$cliDirectory\messages.mc`" && rc /nologo /I . /fo g29ctl.res `"$cliDirectory\g29ctl.rc`" && " +
        "cl $($common -join ' ') $sources $(Join-Quoted (Get-ProgramObjects 'x64')) g29ctl.res /Fe`"$outputDirectory\g29ctl.exe`" /link /NOLOGO /Brepro /SUBSYSTEM:CONSOLE /CETCOMPAT $libraries")
}

foreach ($bits in @('64', '32')) {
    if ($Target -notcontains "driver$bits") {
        continue
    }

    $architecture = if ($bits -eq '64') { 'x64' } else { 'x86' }
    # CET shadow-stack compatibility is an x64-only linker flag
    $cet = if ($bits -eq '64') { '/CETCOMPAT' } else { '' }
    $driverDirectory = Join-Path $projectRoot 'src\bridge\directinput'
    $sources = Join-Quoted ($bridgeSources + @(Join-Path $driverDirectory 'driver.c'))
    Invoke-Compiler $architecture (Join-Path $objectDirectory "driver$bits") ("cl $($common -join ' ') /LD $sources $(Join-Quoted (Get-ProgramObjects $architecture)) /Fe`"$outputDirectory\g29ffb$bits.dll`" " +
        "/link /NOLOGO /Brepro $cet /DEF:`"$driverDirectory\driver.def`" $libraries powrprof.lib dxguid.lib uuid.lib")
}

# Test-only: the driver with a serialization export, and a smoke client per bitness.
if ($Target -contains 'drivertest') {
    $driverDirectory = Join-Path $projectRoot 'src\bridge\directinput'
    $sources = Join-Quoted ($bridgeSources + @(Join-Path $driverDirectory 'driver.c'))
    Invoke-Compiler 'x64' (Join-Path $objectDirectory 'drivertest') ("cl $($common -join ' ') /DG29_DRIVER_TEST /LD $sources $(Join-Quoted (Get-ProgramObjects 'x64')) /Fe`"$outputDirectory\g29drivertest.dll`" " +
        "/link /NOLOGO /DEF:`"$driverDirectory\driver.def`" $libraries powrprof.lib dxguid.lib uuid.lib")
    foreach ($bits in @('64', '32')) {
        $architecture = if ($bits -eq '64') { 'x64' } else { 'x86' }
        $check = Join-Quoted @(Join-Path $projectRoot 'src\bridge\drivercheck\drivercheck.c')
        Invoke-Compiler $architecture (Join-Path $objectDirectory "drivercheck$bits") "cl $($common -join ' ') $check /Fe`"$outputDirectory\drivercheck$bits.exe`" /link /NOLOGO ole32.lib dxguid.lib uuid.lib"
    }
}

# Diagnostic only: opens the G29 through real DirectInput (creates no effects).
if ($Target -contains 'diprobe') {
    $probe = Join-Quoted @(Join-Path $projectRoot 'src\bridge\drivercheck\diprobe.c')
    Invoke-Compiler 'x64' (Join-Path $objectDirectory 'diprobe') "cl $($common -join ' ') $probe /Fe`"$outputDirectory\diprobe.exe`" /link /NOLOGO dinput8.lib dxguid.lib user32.lib ole32.lib"
    $probe = Join-Quoted @(Join-Path $projectRoot 'src\bridge\drivercheck\diprobea.c')
    Invoke-Compiler 'x64' (Join-Path $objectDirectory 'diprobea') "cl $($common -join ' ') /UUNICODE /U_UNICODE $probe /Fe`"$outputDirectory\diprobea.exe`" /link /NOLOGO dinput8.lib dxguid.lib user32.lib ole32.lib"
}

# Static analysis (/analyze, warnings are errors) of every hand-written shipped
# source file, both architectures. The generated program is excluded: it is
# produced by tools\BfAot and verified by the differential tests instead.
if ($Target -contains 'analyze') {
    $sources = Join-Quoted ($bridgeSources + @((Join-Path $projectRoot 'src\bridge\cli-service\main.c'), (Join-Path $projectRoot 'src\bridge\directinput\driver.c')))
    $options = ($common + @('/analyze', '/external:anglebrackets', '/external:W0', '/analyze:external-', '/c')) -join ' '
    foreach ($architecture in @('x64', 'x86')) {
        Invoke-Compiler $architecture (Join-Path $objectDirectory "analyze-$architecture") "cl $options $sources"
    }
}

# The test DLL built with AddressSanitizer (artifacts\asan), next to the ASan
# runtime, for test.ps1 -Sanitize. The bridge and the test programs are
# instrumented; the application program objects are shared with testhost.
if ($Target -contains 'asan') {
    $asanDirectory = Join-Path $projectRoot 'artifacts\asan'
    New-Item -ItemType Directory -Path $asanDirectory -Force | Out-Null
    # ASan wants debug information and no /guard:cf
    $options = @($common | Where-Object { $_ -ne '/guard:cf' -and $_ -ne '/O2' }) + @('/Od', '/Zi', '/fsanitize=address')
    $objects = (Get-ProgramObjects 'x64') + (Build-GeneratedObjects 'testprograms-asan' 'x64' (Get-TestProgramParts) $options)
    $sources = Join-Quoted ($bridgeSources + @(Join-Path $projectRoot 'src\bridge\testhost\testhost.c'))
    Invoke-Compiler 'x64' (Join-Path $objectDirectory 'asan') "cl $($options -join ' ') /LD $sources $(Join-Quoted $objects) /Fe`"$asanDirectory\g29testhost.dll`" /link /NOLOGO /DEBUG kernel32.lib advapi32.lib setupapi.lib hid.lib"
    $runtime = Get-ChildItem -Path (Join-Path $installation 'VC\Tools\MSVC\*\bin\Hostx64\x64\clang_rt.asan_dynamic-x86_64.dll') | Sort-Object FullName | Select-Object -Last 1
    if (-not $runtime) {
        throw 'The AddressSanitizer runtime was not found; install the "C++ AddressSanitizer" Visual Studio component.'
    }

    Copy-Item -LiteralPath $runtime.FullName -Destination $asanDirectory -Force
}

# Checks that the shipped binaries carry the exploit mitigations the build asks
# for: ASLR, DEP, Control Flow Guard, and on x64 high-entropy ASLR and CET.
if ($Target -contains 'hardening') {
    $expected = @{
        'g29ctl.exe'   = @('Dynamic base', 'NX compatible', 'Control Flow Guard', 'High Entropy Virtual Addresses', 'CET compatible')
        'g29ffb64.dll' = @('Dynamic base', 'NX compatible', 'Control Flow Guard', 'High Entropy Virtual Addresses', 'CET compatible')
        'g29ffb32.dll' = @('Dynamic base', 'NX compatible', 'Control Flow Guard')
    }
    foreach ($name in $expected.Keys) {
        $path = Join-Path $outputDirectory $name
        $headers = & cmd.exe /c "set `"PATH=%PATH%;$installer`" && `"$vcvars`" x64 >nul && dumpbin /nologo /headers /loadconfig `"$path`""
        foreach ($flag in $expected[$name]) {
            if (-not ($headers | Where-Object { $_.Trim() -eq $flag })) {
                throw "$name lacks the '$flag' mitigation."
            }
        }
    }
}

Write-Host "Native build complete: $($Target -join ', ')"
