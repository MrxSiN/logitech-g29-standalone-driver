[CmdletBinding()]
param(
    # Where the built binaries are (default artifacts\bin).
    [string]$Directory,
    # Where SHA256SUMS and release-manifest.json go (default artifacts\release).
    [string]$Output
)

# Writes the release manifest: SHA-256 of every shipped file and of the
# Brainfuck program they were compiled from, the source revision and the toolchain. The output
# depends only on its inputs (no timestamps), so two builds of one commit can
# be compared by their manifests. Signing is a separate, manual step
# (docs/RELEASE.md); this script signs nothing and reports each file's
# Authenticode status as it finds it.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Directory) { $Directory = Join-Path $root 'artifacts\bin' }
if (-not $Output) { $Output = Join-Path $root 'artifacts\release' }
New-Item -ItemType Directory -Path $Output -Force | Out-Null

$files = @('g29ctl.exe', 'g29ffb64.dll', 'g29ffb32.dll')
$entries = foreach ($name in $files) {
    $path = Join-Path $Directory $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "$path is missing; run build.ps1 first."
    }

    [ordered]@{
        name      = $name
        size      = (Get-Item -LiteralPath $path).Length
        sha256    = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        signature = [string](Get-AuthenticodeSignature -LiteralPath $path).Status
    }
}

$program = Join-Path $root 'src\brainfuck\g29-main.bf'
$commit = (& git -C $root rev-parse HEAD).Trim()
$dirty = [bool](& git -C $root status --porcelain --untracked-files=no)

$msvc = 'unknown'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path -LiteralPath $vswhere) {
    $installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($installation) {
        $tools = Get-ChildItem -LiteralPath (Join-Path $installation 'VC\Tools\MSVC') -Directory -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -Last 1
        if ($tools) { $msvc = $tools.Name }
    }
}

$manifest = [ordered]@{
    product        = 'G29 Standalone'
    commit         = $commit
    tree_dirty     = $dirty
    abi_version    = 1
    program_sha256 = (Get-FileHash -LiteralPath $program -Algorithm SHA256).Hash.ToLowerInvariant()
    toolchain      = [ordered]@{
        csc  = (Get-Item (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe')).VersionInfo.FileVersion
        msvc = $msvc
    }
    files          = @($entries)
}

$sums = ($entries | ForEach-Object { "$($_.sha256)  $($_.name)" }) -join "`n"
[IO.File]::WriteAllText((Join-Path $Output 'SHA256SUMS'), $sums + "`n")
[IO.File]::WriteAllText((Join-Path $Output 'release-manifest.json'), ($manifest | ConvertTo-Json -Depth 4) + "`n")

if ($dirty) {
    Write-Warning 'The working tree has uncommitted changes; this manifest does not describe a commit.'
}

Write-Host "Release manifest written to $Output"
