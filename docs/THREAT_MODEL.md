# Threat model

Scope: the shipped binaries (`g29ctl.exe`, `g29ffb64.dll`, `g29ffb32.dll`),
the `G29Standalone` service, and the install/uninstall scripts. The highest
priority is physical: the wheel must not apply force nobody asked for, or keep
applying it after the requester is gone.

## Assets

1. Physical safety of the person at the wheel.
2. Integrity of the machine the administrator installs on (services, registry,
   Program Files).
3. Availability of the wheel's configuration (range, autocenter, LEDs).

## Trust boundaries and inputs

| Boundary | Input | Treated as |
|---|---|---|
| G29 -> HID stack -> bridge | input reports, device attributes | untrusted; the bridge extracts one usage value with HidP_*; the program validates identity |
| bridge <-> Brainfuck program | command frames | untrusted output of a possibly faulty program: parsed, bounded, capability-checked before any Win32 call |
| game -> `g29ffb*.dll` | DirectInput calls, DIEFFECT structures | untrusted; never read beyond dwSize, pointers followed only when DirectInput flags mark them valid, at most 24 type-specific bytes |
| any local user <-> heartbeat mapping | 16 bytes of shared memory | untrusted (see below) |
| unprivileged install phase -> elevated phase | binaries, their SHA-256, options | the elevated phase re-hashes the copies it placed under Program Files and runs only those |
| Brainfuck install plan -> elevated installer | key/value lines | proposals: fixed identity, strict grammar, unknown keys refused |

## IPC and privilege

- The service runs as LocalSystem (needed to open the HID device for every
  session and to be trigger-started by the SCM). It exposes **no command
  channel**: no named pipe, RPC, socket, window message or COM server. Its
  only input from other processes is the heartbeat mapping.
- The heartbeat mapping `Global\G29Standalone.ForceHeartbeat` is created by the
  service with a DACL granting Everyone read/write (games run as any user).
  An attacker who writes it can (a) make the service send the stop report
  (safe direction), or (b) suppress the watchdog's stop for a crashed game by
  faking beats. (b) gives nothing an attacker does not already have: any local
  user can open the G29's HID interface directly and send force reports; the
  service cannot prevent that and does not try. The mapping carries no
  commands, paths or code.
- The service's output guard admits no non-zero force at all, so no input to
  the service can make it push the wheel.
- The DirectInput DLL runs in the game's process with the game's rights; it
  grants nothing the game does not have.
- The service process restricts later DLL loads to System32
  (`SetDefaultDllDirectories`). The DLLs load no further DLLs dynamically.
- The service object keeps the default DACL `New-Service` assigns. Observed on
  the development machine (`sc.exe sdshow G29Standalone`, 2026-09-24):
  SYSTEM may query, start, stop and pause; Administrators have full control;
  interactive and service logons (IU, SU) may only query configuration and
  status, enumerate dependents, interrogate and send user-defined controls —
  they cannot start, stop, reconfigure or delete it. The service handles no
  user-defined control codes.

## Physical threats and mitigations

| Threat | Mitigation | Evidence |
|---|---|---|
| program bug emits an oversized force | guard ceiling per host | `SafetyTests` (fuzzed allowlist, ceilings) |
| program emits a malformed or unknown report | guard allowlist; violation trips | `SafetyTests` |
| program loops or blocks while force held | iteration budget; lease liveness 1 s | `SafetyTests` (hostile programs, session stall) |
| CLI force outlives its 5 s | lease hold limit 6 s | `SafetyTests` (lease thread) |
| game crashes (no DLL detach) | service watchdog, 1 s heartbeat timeout | `WatchdogTests`; **HIL required** |
| game exits normally | DLL detach: guard stop | **HIL required** |
| machine suspends mid-force | suspend callback stops force | **HIL required** |
| service killed while game holds force | game's own guard and lease keep working; the game's session ends normally | **HIL required** |
| USB unplug under force | writes fail, session ends; the G29 resets on power-up | **HIL required** |
| new game inherits a stale force | first tick of a session sends the stop report | `DirectInputTests` |
| malicious local user sends force directly over HID | out of scope (Windows grants HID access; no software here can prevent it) | — |

## Installer threats

| Threat | Mitigation | Evidence |
|---|---|---|
| plan names another service (e.g. `Spooler`) | fixed service name; plan must equal it | `InstallCommon.Tests.ps1` |
| plan sets a `run` recovery action | recovery grammar allows `restart` only | `InstallCommon.Tests.ps1` |
| plan triggers on another device | trigger grammar: USB interface class + G29 product IDs | `InstallCommon.Tests.ps1` |
| a foreign service already named `G29Standalone` | refused before any change (binary path ownership check) | `InstallCommon.Tests.ps1` |
| binaries swapped between build and install | SHA-256 passed to the elevated phase, re-checked on the installed copies before they run | `InstallCommon.Tests.ps1` |
| build tools running as administrator | the installer builds only when binaries are missing, and warns when that happens in an elevated shell (accepted: the user chose to elevate) | script logic |
| partial installation after a failure | transaction with undo; failure injected at every change in tests | `InstallCommon.Tests.ps1` |
| junction planted at the install directory | refused before create/remove | script logic |

## Out of scope

- Kernel-mode attackers and administrators.
- Supply-chain compromise of Visual Studio, Windows SDK or the .NET Framework
  compiler.
- Denial of service by a local user who unplugs the wheel or kills processes.
