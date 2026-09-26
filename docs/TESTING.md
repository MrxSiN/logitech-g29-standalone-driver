# Testing

## Automated (no wheel, no administrator, no network)

```powershell
.\test.ps1                    # build, native test pieces, C# suite, install checks
.\test.ps1 -Sanitize -Analyze # also the whole suite under AddressSanitizer, and /analyze
.\test.ps1 -Reproducible      # also a clean rebuild that must be byte-identical
```

| Layer | Where | What it proves |
|---|---|---|
| protocol golden vectors | `ProtocolTests`, `ReferenceReplay` (`tests/reference/protocol.txt`) | every report for every legal input, identification for every PID × revision class |
| behavior parity | `*ParityTests`, `DirectInputTests`, `WatchdogTests`, `RegistrationTests`, `InstallPlanTests` | the Brainfuck program reproduces the frozen legacy behavior (`tests/reference/*.txt`, generated from the deleted C# implementation, never hand-edited) through fake bridges |
| compiled vs. reference | `Program.CompiledPass` | every scenario again on the ahead-of-time compiled program (the shipping code path, in `g29testhost.dll`); frames must be identical to the reference interpreter (`tests/harness`) |
| architecture | `ArchitectureTests` | `g29-main.bf` holds only `><+-.,[]` and line endings; `src/brainfuck` has no other program; no `.bfa` file, assembler or code referring to one; no script writes `g29-main.bf` (and `test.ps1` fails if the build changes it); no Brainfuck command dispatch in the shipping C sources; the shipping binaries carry no program text, program resource or interpreter strings; the generated code was compiled from the committed program (SHA-256); `g29ctl.exe`/`g29ffb64.dll` are x64 and `g29ffb32.dll` x86 |
| compiler | `AotCompilerTests` | rejects foreign bytes (with line and column) and unbalanced brackets, size limit; run folding; the code emitted for each optimization; no proven fault in the real program; identical output for identical input |
| iteration budget | `Program.CompiledPass` | the longest legitimate stretch between two reads (2 493 loop iterations) is at most 1/20 of the budget (100 000) |
| output guard | `SafetyTests.ReportAllowlist`, `Ceilings`, `TripLatch` | every protocol report passes; 100 000 mutated reports: whatever passes has a protocol family's shape; ceilings (0/25/100 %) and their one-way lowering; trip latch and the write-after-trip race |
| force lease | `SafetyTests.HoldLease`, `LeaseThread`, `SessionWatchdog` | hold limit at the boundary (fake clock); the real watchdog thread stops a held force and trips; a program blocked in a host call is detected as stalled; a 1 MiB backlog fails the session |
| registry capability | `SafetyTests.RegistryAllowlist` | allowed and refused paths; every key any registration scenario changes is inside the native allowlist |
| frame parser (ABI fuzz) | `SafetyTests.FrameParser` | boundaries (magic, version, flags, 4096/4097), 20 000 random and mutated streams: never reads outside the input, stops at the first bad byte, delivers exactly the frames of a valid stream |
| compiled-program containment | `SafetyTests.HostileProgram`, `CompiledDifferentialFuzz`, `CompiledClosedForms`, `CompiledConcurrency` | endless loops, tape boundaries both ends, runaway walk, output floods (budget and host refusal), input starvation, cell overflow, exact budget boundary, deterministic re-execution; 3000 random programs compiled ahead of time agree with the reference interpreter (outcome, fault message, output, iteration count); every closed-form loop shape on 1 680 prepared tapes equals plain execution; 8 threads × 200 runs of one compiled program. The corpus is written by `G29.Tests.exe --write-programs` and compiled into `g29testhost.dll` |
| DirectInput shell | `EffectDriverShellTests` | the DIEFFECT serialization against frozen vectors; `drivercheck64.exe` / `drivercheck32.exe` load the shipping x64 and x86 DLLs through their class factory |
| installer | `tests/InstallCommon.Tests.ps1` | plan grammar (Spooler, other directories, run actions, foreign triggers …), ownership, hashes; fresh install and upgrade against an in-memory machine, with a failure injected at **every** machine change: the machine must end exactly as it began (files, services, registration); tampered binaries never run; Spooler is never queried or touched; uninstall restores the pre-install state; per-user registration: the paths match what the program writes (`RegistrationTests.InstallerKeysMatch`), an uninstall by another account (SYSTEM after an interactive install, and the reverse) restores every hive, loaded or not, to its pre-registration state; hives without the marker, and foreign `OEMForceFeedback` CLSIDs, are left alone; a failure at every sweep change still removes the service and files, is reported, and a second uninstall finishes the job |
| native hardening | `native.ps1 -Target hardening` (run by `test.ps1`) | ASLR, DEP, CFG on all binaries; high-entropy ASLR and CET on x64 |
| static analysis | `native.ps1 -Target analyze` | `/analyze` on every hand-written shipped source, x64 and x86, warnings are errors (the generated program is covered by the differential tests instead) |
| performance | `G29.Tests.exe --benchmark` | not a correctness test; see `docs/PERFORMANCE.md` |

The differential tests are only as independent as their two sides: the
compiler's loop classification mirrors the reference interpreter's, so the
fuzz catches compilation mistakes, not a shared misunderstanding of
Brainfuck; the closed forms are checked against plain execution, which shares
nothing with them. The protocol vectors come from the legacy
C# implementation (itself ported from Linux `hid-lg4ff.c`), not from the
Brainfuck sources, and the guard's allowlist is checked against those vectors,
not against `guard.c`.

## Hardware-in-the-loop (HIL REQUIRED)

What has been run so far is recorded in `docs/HARDWARE_VALIDATION.md`.

These need a physical G29 in PS3 mode and a person watching it. Record for each
run: date, commit, `g29ctl doctor` output, Windows build, USB topology. **Keep
hands and objects clear of the wheel during every force test.** A run passes
only when every listed criterion holds.

| ID | Procedure | Pass criterion |
|---|---|---|
| HIL-1 identity | `g29ctl status` and `g29ctl doctor` with the wheel connected (PS3 mode), then in PS4 mode | PS3: one native C24F line; PS4: reported as PS4 mode, nothing configured |
| HIL-2 init | `g29ctl init --range 900 --autocenter 0` from compatibility mode | wheel re-enumerates once and ends native; range 900 |
| HIL-3 range | `g29ctl range N` for 40, 90, 180, 270, 540, 900 | measured lock-to-lock within ±5° of N |
| HIL-4 autocenter | `g29ctl autocenter P` for 0, 25, 50, 100 | 0: no centering force; strength increases with P |
| HIL-5 LEDs | `g29ctl leds M` for 0..31 | lit LEDs equal the mask bits |
| HIL-6 diagnostic force | `g29ctl force 10 --milliseconds 1000 --i-understand`, then `-10` | turns left for +10, right for -10; stops at 1 s ± 100 ms |
| HIL-7 force cancel | Ctrl+C during `force 20 --milliseconds 5000` | force stops within 100 ms of Ctrl+C |
| HIL-8 CLI kill | `taskkill /f` of g29ctl during `force 20 --milliseconds 5000` | record how long the force persists: a terminated process runs no cleanup, so this measures the G29's own behavior; a force that persists indefinitely is a release blocker to be resolved (for example a service-side stop) before 1.0 |
| HIL-9 game crash | DirectInput client holding a constant force, then `taskkill /f` it, 100 trials | force ends within 1.5 s every time (service watchdog) |
| HIL-10 game exit | same client exits normally, 100 trials | force ends at exit every time |
| HIL-11 suspend | client holding force, `rundll32 powrprof.dll,SetSuspendState 0,1,0`, 50 cycles | no force after resume until the game changes it |
| HIL-12 unplug under force | client holding force, unplug, replug, 100 trials | no force on replug; service re-initializes the wheel |
| HIL-13 service restart | `Restart-Service G29Standalone` × 50 with the wheel attached | configuration re-applied every time; no force |
| HIL-14 late plug / cold boot | boot without the wheel then plug (50×); boot with it (20×) | service starts on arrival, wheel configured |
| HIL-15 install | `Install-Driver.ps1`, upgrade over itself, `Uninstall-Driver.ps1`, reboot, reinstall | `sc.exe qc/qtriggerinfo/qfailure` match the plan; after uninstall no service, directory, class key, or `Software\G29Standalone` in HKLM or any user hive, whichever account (interactive or SYSTEM) installed and uninstalled; a failure forced by holding a file open rolls back to the previous installation |
| HIL-16 endurance | 8 h functional + 24 h idle soak with the service | no crash, no unexpected motion; private bytes and handle count flat |

HIL-15 status (2026-09-24): upgrade, forced-failure
rollback, hash-tamper rollback, uninstall, reinstall and SYSTEM-context
install/uninstall passed. Open: the UAC prompt itself (the development
machine has UAC disabled), reboot, clean Windows 10. The issue found there,
that uninstall removed only the HKCU registration of the account running it,
is fixed in software (2026-09-25): `Remove-G29Installation` now also removes
the registration from every user hive, loaded or not, where the hive's own
marker proves ownership. That fix has not yet run on the machine. To do,
from a terminal started outside the Claude desktop app (its HKCU may be
virtualized):

1. Install interactively, uninstall as SYSTEM (scheduled task). No
   `Software\G29Standalone` and no `OEMForceFeedback` with our CLSID may
   remain in `HKEY_USERS\<sid>` of the interactive user, SYSTEM
   (`S-1-5-18`), or a second account that is signed out (its `NTUSER.DAT`
   is loaded and unloaded again; `reg query HKU` shows no
   `G29Standalone-*` hive afterwards).
2. The reverse: install as SYSTEM, uninstall interactively.

Per-user registration, open question: DirectInput reads the per-user
`OEMForceFeedback` ahead of HKLM when it exists. On 2026-09-24 Assetto Corsa
Competizione read a stale G HUB CLSID from the real HKCU while HKLM held
ours. So a stale HKCU entry left behind breaks force feedback for that user,
and the installing account's own HKCU registration is needed to override one.
What was **not** established is what DirectInput does for an account that has
no per-user `OEMForceFeedback`: whether it falls back to HKLM or copies HKLM
into HKCU, and so whether that account gets force feedback. This decides
whether a SYSTEM-context install (HKLM plus SYSTEM's own HKCU only) is enough
for users. Procedure: on a second account with no
`...\OEM\VID_046D&PID_C24F\OEMForceFeedback` under its HKCU, and HKLM
registered by this project, start a DirectInput client (`drivercheck64.exe`,
then a game) from that account's own session. Record whether
`g29ffb64.dll`/`g29ffb32.dll` loads (Process Explorer), whether force
feedback works, and whether an `OEMForceFeedback` key, and which CLSID,
appears in that account's HKCU afterwards.

## Real-game compatibility (REQUIRED BEFORE 1.0)

No game is claimed to work until it is recorded here with: game and version,
store, executable SHA-256, **observed** bitness (which `g29ffb*.dll` loaded),
Windows build, steering/pedals/buttons, force categories observed, strength,
pause/resume, reconnect, exit cleanup, limitations, pass/fail. The 1.0 bar is
several unrelated titles in both bitnesses. Assetto Corsa Competizione has been
used during development (direction fix); it is not yet a recorded pass.
