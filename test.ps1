[CmdletBinding()]
param(
    # Also run the whole suite against the bridge and the test programs built
    # with AddressSanitizer.
    [switch]$Sanitize,
    # Also run the compiler's static analysis (/analyze) on every hand-written
    # shipped source file.
    [switch]$Analyze,
    # Also rebuild from a clean tree and require byte-identical binaries.
    [switch]$Reproducible,
    # Also test the connected G29: identification, initialization, range,
    # autocenter, LEDs, a short force in each direction, and the installed
    # force feedback driver through DirectInput. The wheel turns briefly.
    # Run on its own in a console, test.ps1 offers this when a G29 is connected.
    [switch]$Device,
    # The device test's force (g29ctl units, 1..25). Weaker forces may not
    # overcome the wheel's static friction.
    [ValidateRange(1, 25)]
    [int]$DeviceForce = 20
)

# Builds everything, then runs the C# suite (reference interpreter and the
# ahead-of-time compiled program side by side) and the installer tests.
# Needs no wheel, G HUB, administrator rights or network access.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'tools\InstallCommon.ps1')
# Run on its own (not from Install-Driver.ps1): keep a window opened for it.
$topLevel = $MyInvocation.CommandOrigin -eq 'Runspace'
trap { Write-Host $_ -ForegroundColor Red; if ($topLevel) { Wait-ConsoleClose }; break }

$program = Join-Path $PSScriptRoot 'src\brainfuck\g29-main.bf'
$programHash = (Get-FileHash -LiteralPath $program -Algorithm SHA256).Hash
$bin = Join-Path $PSScriptRoot 'artifacts\bin'
$shipping = @('g29ctl.exe', 'g29ffb64.dll', 'g29ffb32.dll') | ForEach-Object { Join-Path $bin $_ }

& (Join-Path $PSScriptRoot 'build.ps1')

# The C# tests include the compiler's source for its unit tests.
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testOutput = Join-Path $bin 'G29.Tests.exe'
$testSources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'tests') -Recurse -Filter '*.cs' -File | ForEach-Object FullName)
$testSources += Join-Path $PSScriptRoot 'tools\BfAot\Compiler.cs'
& $compiler /nologo /target:exe /optimize+ /platform:x64 /warn:4 /warnaserror+ /reference:System.dll /reference:System.Core.dll "/out:$testOutput" $testSources
if ($LASTEXITCODE -ne 0) {
    throw "Test compilation failed with exit code $LASTEXITCODE."
}

# The test program corpus, compiled ahead of time into g29testhost.dll.
$testPrograms = Join-Path $PSScriptRoot 'artifacts\obj\testprograms'
& $testOutput --write-programs $testPrograms
if ($LASTEXITCODE -ne 0) {
    throw "Writing the test programs failed with exit code $LASTEXITCODE."
}

# Test-only native pieces: the compiled program and corpus with the guard,
# lease, session and shared memory as a DLL; the driver with its serialization
# export; a DirectInput smoke client per bitness. "hardening" fails unless the
# shipped binaries carry ASLR, DEP, CFG (and CET and high-entropy ASLR on x64).
$nativeTargets = @('testhost', 'drivertest', 'hardening')
if ($Sanitize) { $nativeTargets += 'asan' }
if ($Analyze) { $nativeTargets += 'analyze' }
& (Join-Path $PSScriptRoot 'native.ps1') -Target $nativeTargets -TestPrograms $testPrograms

& $testOutput
if ($LASTEXITCODE -ne 0) {
    throw "Tests failed with exit code $LASTEXITCODE."
}

if ($Sanitize) {
    # the same executable next to the AddressSanitizer build of g29testhost.dll
    $asan = Join-Path $PSScriptRoot 'artifacts\asan'
    Copy-Item -LiteralPath $testOutput -Destination $asan -Force
    foreach ($name in @('g29ctl.exe', 'g29drivertest.dll', 'g29ffb64.dll', 'g29ffb32.dll', 'drivercheck64.exe', 'drivercheck32.exe')) {
        Copy-Item -LiteralPath (Join-Path $bin $name) -Destination $asan -Force
    }

    & (Join-Path $asan 'G29.Tests.exe')
    if ($LASTEXITCODE -ne 0) {
        throw "Tests under AddressSanitizer failed with exit code $LASTEXITCODE."
    }
}

& (Join-Path $PSScriptRoot 'tests\InstallCommon.Tests.ps1') -Executable (Join-Path $bin 'g29ctl.exe')

# The build reads the Brainfuck program and never writes it.
if ((Get-FileHash -LiteralPath $program -Algorithm SHA256).Hash -ne $programHash) {
    throw 'The build or the tests modified src\brainfuck\g29-main.bf.'
}

if ($Reproducible) {
    $first = @($shipping | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash })
    & (Join-Path $PSScriptRoot 'build.ps1') -Clean
    $second = @($shipping | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash })
    if (Compare-Object $first $second) {
        throw 'A clean rebuild produced different binaries; the build is not reproducible.'
    }

    Write-Host 'Clean rebuild produced byte-identical binaries.'
}

$ctl = Join-Path $bin 'g29ctl.exe'
if (-not $Device -and $topLevel -and -not [Console]::IsInputRedirected) {
    $null = & $ctl status 2>&1
    if ($LASTEXITCODE -eq 0) {
        $Device = (Read-Host 'A G29 is connected. Run the device test? The wheel turns briefly [y/N]') -match '^(y|yes)$'
    }
}

if ($Device) {
    Write-Host 'Device test: the wheel turns briefly in each direction; keep hands clear or hold it lightly.'
    $failures = @()
    $steps = @(
        @('status'), @('doctor'), @('init'), @('range', '900'), @('autocenter', '0'), @('leds', '31'), @('leds', '0'),
        @('force', "$DeviceForce", '--milliseconds', '500', '--i-understand'),
        @('force', "-$DeviceForce", '--milliseconds', '500', '--i-understand'),
        @('stop'))
    try {
        foreach ($step in $steps) {
            $output = (& $ctl @step 2>&1) -join ' | '
            $passed = $LASTEXITCODE -eq 0
            if (-not $passed) { $failures += "g29ctl $step" }
            Write-Host ('  {0} g29ctl {1}: {2}' -f $(if ($passed) { 'ok  ' } else { 'FAIL' }), ($step -join ' '), $output)
            # keep the LEDs lit long enough to notice
            if ($step[0] -eq 'leds' -and $step[1] -ne '0') { Start-Sleep -Seconds 2 }
        }
    } finally {
        # the stop report, also after a failure or Ctrl+C
        $null = & $ctl stop 2>&1
    }

    # DirectInput, the way a game reaches the installed driver (no torque).
    $installedDriver = Join-Path (Get-G29InstallDirectory) 'g29ffb64.dll'
    if (Test-Path -LiteralPath $installedDriver) {
        & (Join-Path $PSScriptRoot 'native.ps1') -Target diprobe
        $probe = @(& (Join-Path $bin 'diprobe.exe') 2>&1)
        foreach ($expected in @('forcefeedback=1', 'Acquire 0x00000000, driver loaded: 1', 'CreateEffect(constant, magnitude 0) 0x00000000', 'Stop 0x00000000')) {
            $passed = [bool]($probe | Where-Object { $_ -like "*$expected*" })
            if (-not $passed) { $failures += "DirectInput: $expected" }
            Write-Host ('  {0} DirectInput: {1}' -f $(if ($passed) { 'ok  ' } else { 'FAIL' }), $expected)
        }
    } else {
        Write-Host '  skip DirectInput: the driver is not installed (Install-Driver.ps1).'
    }

    if ($failures) {
        throw "Device test failed: $($failures -join '; ')."
    }

    Write-Host 'Device test passed. The test cannot see the wheel: check that the LEDs lit and the wheel turned both ways.'
}

Write-Host 'All tests passed.'
if ($topLevel) { Wait-ConsoleClose }
