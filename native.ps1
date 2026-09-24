[CmdletBinding()]
param(
    # Which native targets to build: testvm (the VM as a test DLL), and later the
    # shipping bridge binaries.
    [string[]]$Target = @('testvm')
)

# Builds the native bridge with the Visual Studio C compiler (Build Tools are
# enough). Warnings are errors, like the C# build.
$ErrorActionPreference = 'Stop'
$Target = @($Target | ForEach-Object { $_ -split ',' })
$projectRoot = $PSScriptRoot
$outputDirectory = Join-Path $projectRoot 'artifacts\bin'
$objectDirectory = Join-Path $projectRoot 'artifacts\obj'
New-Item -ItemType Directory -Path $outputDirectory, $objectDirectory -Force | Out-Null

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'Visual Studio (or Build Tools) with the C++ workload is required for the native bridge.'
}

$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) {
    throw 'No Visual Studio installation with the x86/x64 C++ tools was found.'
}

$vcvars = Join-Path $installation 'VC\Auxiliary\Build\vcvarsall.bat'
$common = @('/nologo', '/W4', '/WX', '/O2', '/GS', '/guard:cf', '/DUNICODE', '/D_UNICODE', '/DWIN32_LEAN_AND_MEAN', '/D_CRT_SECURE_NO_WARNINGS')

$installer = Split-Path -Parent $vswhere

# Runs a command in the compiler environment, in the given object directory.
function Invoke-Compiler([string]$Architecture, [string]$Directory, [string]$Command) {
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    & cmd.exe /c "set `"PATH=%PATH%;$installer`" && `"$vcvars`" $Architecture >nul && cd /d `"$Directory`" && $Command"
    if ($LASTEXITCODE -ne 0) {
        throw "Native compilation failed ($Architecture): $Command"
    }
}

function Join-Sources([string[]]$Relative) {
    return ($Relative | ForEach-Object { '"' + (Join-Path $projectRoot $_) + '"' }) -join ' '
}

if ($Target -contains 'testvm') {
    $sources = Join-Sources @('src\bridge\common\bfvm.c', 'src\bridge\common\frames.c', 'src\bridge\common\hid.c', 'src\bridge\common\guard.c', 'src\bridge\common\system.c', 'src\bridge\testvm\testvm.c')
    $options = $common -join ' '
    Invoke-Compiler 'x64' (Join-Path $objectDirectory 'testvm') "cl $options /LD $sources /Fe`"$outputDirectory\g29testvm.dll`" /link /NOLOGO kernel32.lib advapi32.lib setupapi.lib hid.lib"
}

$program = Join-Path $projectRoot 'src\brainfuck\g29-main.bf'
$libraries = 'kernel32.lib user32.lib advapi32.lib setupapi.lib hid.lib winmm.lib ole32.lib'

# A header the resource scripts include: the absolute path of the program.
function Write-ProgramPath([string]$Directory) {
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    $escaped = $program.Replace('\', '\\')
    Set-Content -LiteralPath (Join-Path $Directory 'program_path.h') -Value "#define G29_PROGRAM_PATH `"$escaped`"" -Encoding ascii
}

$commonSources = @('src\bridge\common\bfvm.c', 'src\bridge\common\frames.c', 'src\bridge\common\session.c', 'src\bridge\common\hid.c', 'src\bridge\common\guard.c', 'src\bridge\common\system.c', 'src\bridge\common\program.c')

if ($Target -contains 'cli') {
    $objects = Join-Path $objectDirectory 'cli'
    Write-ProgramPath $objects
    $cliDirectory = Join-Path $projectRoot 'src\bridge\cli-service'
    $sources = Join-Sources ($commonSources + @('src\bridge\cli-service\main.c'))
    $options = $common -join ' '
    Invoke-Compiler 'x64' $objects ("mc -h . -r . `"$cliDirectory\messages.mc`" && rc /nologo /I . /fo g29ctl.res `"$cliDirectory\g29ctl.rc`" && " +
        "cl $options $sources g29ctl.res /Fe`"$outputDirectory\g29ctl.exe`" /link /NOLOGO /SUBSYSTEM:CONSOLE $libraries")
}

foreach ($bits in @('64', '32')) {
    if ($Target -notcontains "driver$bits") {
        continue
    }

    $architecture = if ($bits -eq '64') { 'x64' } else { 'x86' }
    $objects = Join-Path $objectDirectory "driver$bits"
    Write-ProgramPath $objects
    $driverDirectory = Join-Path $projectRoot 'src\bridge\directinput'
    $sources = Join-Sources ($commonSources + @('src\bridge\directinput\driver.c'))
    $options = $common -join ' '
    Invoke-Compiler $architecture $objects ("rc /nologo /I . /fo driver.res `"$driverDirectory\driver.rc`" && " +
        "cl $options /LD $sources driver.res /Fe`"$outputDirectory\g29ffb$bits.dll`" /link /NOLOGO /DEF:`"$driverDirectory\driver.def`" $libraries dxguid.lib uuid.lib")
}

# Test-only: the driver with a serialization export, and a smoke client per bitness.
if ($Target -contains 'drivertest') {
    $objects = Join-Path $objectDirectory 'drivertest'
    Write-ProgramPath $objects
    $driverDirectory = Join-Path $projectRoot 'src\bridge\directinput'
    $sources = Join-Sources ($commonSources + @('src\bridge\directinput\driver.c'))
    $options = $common -join ' '
    Invoke-Compiler 'x64' $objects ("rc /nologo /I . /fo driver.res `"$driverDirectory\driver.rc`" && " +
        "cl $options /DG29_DRIVER_TEST /LD $sources driver.res /Fe`"$outputDirectory\g29drivertest.dll`" /link /NOLOGO /DEF:`"$driverDirectory\driver.def`" $libraries dxguid.lib uuid.lib")
    foreach ($bits in @('64', '32')) {
        $architecture = if ($bits -eq '64') { 'x64' } else { 'x86' }
        $sources = Join-Sources @('src\bridge\drivercheck\drivercheck.c')
        Invoke-Compiler $architecture (Join-Path $objectDirectory "drivercheck$bits") "cl $options $sources /Fe`"$outputDirectory\drivercheck$bits.exe`" /link /NOLOGO ole32.lib dxguid.lib uuid.lib"
    }
}

# Diagnostic only: opens the G29 through real DirectInput (creates no effects).
if ($Target -contains 'diprobe') {
    $sources = Join-Sources @('src\bridge\drivercheck\diprobe.c')
    $options = $common -join ' '
    Invoke-Compiler 'x64' (Join-Path $objectDirectory 'diprobe') "cl $options $sources /Fe`"$outputDirectory\diprobe.exe`" /link /NOLOGO dinput8.lib dxguid.lib user32.lib ole32.lib"
    $sources = Join-Sources @('src\bridge\drivercheck\diprobea.c')
    Invoke-Compiler 'x64' (Join-Path $objectDirectory 'diprobea') "cl $options /UUNICODE /U_UNICODE $sources /Fe`"$outputDirectory\diprobea.exe`" /link /NOLOGO dinput8.lib dxguid.lib user32.lib ole32.lib"
}

Write-Host "[THE NATIVE SPIRITS HAVE BEEN BOUND] $($Target -join ', ')"
