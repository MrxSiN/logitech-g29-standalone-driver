[CmdletBinding()]
param(
    # Where the log goes; by default artifacts\logs\g29-trace-<time>.log.
    [string]$LogPath
)

# Records the diagnostic lines the installed force feedback driver writes with
# OutputDebugString while a game runs (the calls the game makes, program
# failures, and a benchmark line every 5 seconds: DirectInput call latency and
# HID write rate), then appends the G29Standalone service events and a
# summary. Start it before the game, stop it with Ctrl+C after.
#
# Run it from a PowerShell that is not elevated, started outside the Claude
# desktop app: the game must be able to open the capture objects. Only one
# debug-output listener works at a time; close DebugView first.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'tools\InstallCommon.ps1')
trap { Write-Host $_ -ForegroundColor Red; Wait-ConsoleClose; break }

if (-not $LogPath) {
    $LogPath = Join-Path $PSScriptRoot "artifacts\logs\g29-trace-$(Get-Date -Format 'yyyyMMdd-HHmmss').log"
}

$null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogPath)
if (Test-Administrator) {
    Write-Warning 'This PowerShell is elevated: a game that is not elevated cannot reach the capture objects, and nothing will be recorded.'
}

# The OutputDebugString protocol: DBWIN_BUFFER holds the writer's process id
# and the text; the writer waits for BUFFER_READY and signals DATA_READY.
Add-Type -TypeDefinition @'
using System;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;

namespace G29 {
    public static class DebugOutput {
        static MemoryMappedFile buffer;
        static MemoryMappedViewAccessor view;
        static EventWaitHandle bufferReady, dataReady;

        // false when another listener already owns the capture objects
        public static bool Open() {
            bool created;
            bufferReady = new EventWaitHandle(false, EventResetMode.AutoReset, "DBWIN_BUFFER_READY", out created);
            dataReady = new EventWaitHandle(false, EventResetMode.AutoReset, "DBWIN_DATA_READY");
            buffer = MemoryMappedFile.CreateOrOpen("DBWIN_BUFFER", 4096);
            view = buffer.CreateViewAccessor();
            bufferReady.Set();
            return created;
        }

        // The next line, or null when none arrives within the timeout.
        public static string Next(int milliseconds, out int processId) {
            processId = 0;
            if (!dataReady.WaitOne(milliseconds)) return null;
            processId = view.ReadInt32(0);
            byte[] text = new byte[4092];
            view.ReadArray(4, text, 0, text.Length);
            int length = Array.IndexOf(text, (byte)0);
            bufferReady.Set();
            return Encoding.Default.GetString(text, 0, length < 0 ? text.Length : length).TrimEnd();
        }
    }
}
'@

if (-not [G29.DebugOutput]::Open()) {
    Write-Warning 'Another debug-output listener (DebugView, a debugger) is running; lines may go to it instead.'
}

$started = Get-Date
$writer = [IO.StreamWriter]::new($LogPath, $false, [Text.Encoding]::UTF8)
$writer.AutoFlush = $true
$writer.WriteLine("G29 trace started $($started.ToString('o'))")
$status = & (Join-Path $PSScriptRoot 'artifacts\bin\g29ctl.exe') status 2>&1
$writer.WriteLine("device: $status")
Write-Host "Recording to $LogPath. Start the game now; press Ctrl+C when done."

$stats = [Collections.Generic.List[string]]::new()
$processId = 0
try {
    while ($true) {
        $line = [G29.DebugOutput]::Next(250, [ref]$processId)
        if (-not $line -or -not $line.StartsWith('g29')) { continue }
        $stamped = "$((Get-Date).ToString('HH:mm:ss.fff')) $line"
        $writer.WriteLine($stamped)
        Write-Host $stamped
        if ($line -match 'stats ') { $stats.Add($line) }
    }
} finally {
    $writer.WriteLine('')
    $writer.WriteLine('G29Standalone service events:')
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = $script:G29ServiceName; StartTime = $started } -ErrorAction SilentlyContinue |
        Sort-Object TimeCreated | ForEach-Object { $writer.WriteLine("$($_.TimeCreated.ToString('HH:mm:ss.fff')) [$($_.LevelDisplayName)] $($_.Message)") }

    # Summary over every benchmark line.
    $calls = 0; $failed = 0; $writes = 0; $milliseconds = 0; $callMax = 0; $writeMax = 0
    foreach ($s in $stats) {
        if ($s -match 'stats (\d+) ms: calls (\d+) \(avg \d+ us, max (\d+) us, failed (\d+)\), hid writes (\d+) \(\d+/s, avg \d+ us, max (\d+) us\)') {
            $milliseconds += [long]$Matches[1]; $calls += [long]$Matches[2]; $failed += [long]$Matches[4]; $writes += [long]$Matches[5]
            $callMax = [Math]::Max($callMax, [long]$Matches[3]); $writeMax = [Math]::Max($writeMax, [long]$Matches[6])
        }
    }

    $summary = if ($milliseconds) {
        "Summary: $([Math]::Round($milliseconds / 1000)) s measured, $calls calls ($failed failed, worst $callMax us), $writes HID writes ($([Math]::Round($writes * 1000 / $milliseconds)) per second, worst $writeMax us)."
    } else {
        'Summary: no benchmark lines were recorded (the game did not use the G29Standalone force feedback driver, or ran elevated).'
    }

    $writer.WriteLine('')
    $writer.WriteLine($summary)
    $writer.Dispose()
    Write-Host $summary
    Write-Host "Log: $LogPath"
    Wait-ConsoleClose
}
