# Architecture

G29 Standalone is a user-mode Windows controller for the Logitech G29 and a
user-mode DirectInput force-feedback effect driver. It uses the inbox Windows
HID stack; it installs no kernel driver, INF, catalog or certificate.

## The rule

```text
Brainfuck owns                        Native code owns
------------------------------------  ------------------------------------------
policy and decisions                  Win32: HID, SetupAPI, registry, SCM, COM,
device identification                   shared memory, timers, console, event log
the CLI contract and its messages     process, thread and handle lifetime
protocol sequencing and HID bytes     memory safety, tape and budget checks
range / autocenter / LED policy       the ABI frame parser (validates first)
reconnect and watchdog state machines privilege, IPC objects, installation
DirectInput effect table and mixing   clocks and scheduling
force math and direction              the independent physical-safety envelope:
install plan (proposals only)           report allowlist, force ceiling, trip
registration policy (which keys)        latch, force lease, suspend stop
```

The Brainfuck program (`src/brainfuck/g29-main.bf`, raw Brainfuck and the
only source of the policy) is compiled ahead of time into native code that is
linked into every binary (`docs/AOT.md`). It can only read input bytes and
write output bytes. It asks the bridge for everything else through versioned
frames (`src/brainfuck/ABI.md`). It cannot call Windows, cannot load code, and
cannot reach past the capabilities each bridge command grants.

Native code redundantly validates what Brainfuck produces wherever memory,
privilege, protocol or physical safety depends on it. It never adds G29
*policy*: the checks bound what a wrong program can do, they do not decide
what a right program should do.

## Processes

```text
game.exe ── IDirectInputEffectDriver ──> g29ffb64.dll / g29ffb32.dll (in-process COM)
                                           ├─ compiled program (role DIRECTINPUT), own thread
                                           ├─ output guard + force lease + suspend stop
                                           └─ HID write/read ──> G29
                                               heartbeat ──> Global\G29Standalone.ForceHeartbeat
g29ctl.exe (user)          ── compiled program (role CLI) ── guard (25 %, 6 s lease) ── HID ──> G29
G29Standalone service      ── same g29ctl.exe, role CLI then service
  (LocalSystem, trigger-     ├─ guard ceiling 0: never applies force
   started on G29 arrival)   ├─ monitor: re-initializes the wheel on reconnect
                             └─ watchdog: stop report when a game's heartbeat stops
Install-Driver.ps1         ── unprivileged: build, test, hash, plan
                           ── elevated: transaction (tools/InstallCommon.ps1)
```

There is no IPC channel that carries commands to the service. The only shared
object is the heartbeat mapping (16 bytes: last beat tick, force-active flag).
See `THREAT_MODEL.md`.

## Native modules (`src/bridge/common`)

| Module | Responsibility |
|---|---|
| `bfrt.c`, `bfrt.h` | support for the compiled program: tape, input/output, faults, iteration budget, idiom closed forms |
| `frames.c` | ABI codec; the command-frame parser validates before dispatch |
| `session.c` | one program on its own thread; bounded input queue; stall measure |
| `guard.c` | report allowlist, force ceiling, held-force tracking, trip latch, stop |
| `lease.c` | watchdog thread: hold limit and program liveness while force is held |
| `hid.c` | HID enumeration, open, write, read, poll (no policy) |
| `system.c` | registry (with the write allowlist), files, processes, shared memory, clocks |

Hosts: `src/bridge/cli-service/main.c` (g29ctl.exe and the service) and
`src/bridge/directinput/driver.c` (the COM shell). Each host maps the frames to
the mechanisms above and wires the guard, lease and session together.

## Where each safety property lives

| Property | Where | Brainfuck can bypass it? |
|---|---|---|
| only known G29 report shapes reach HID | `guard_check` | no |
| force at most 25 % (CLI), 0 % (service) | `guard_check` ceiling | no |
| diagnostic force at most 5 s (+1 s latency) | lease hold limit | no |
| a stuck program cannot hold force | lease liveness (1 s) + iteration budget | no |
| program fault, ABI violation, call timeout stop force | `guard_trip` from the host | no |
| process exit stops force | host exit path / `DllMain` detach | no |
| suspend stops force (DirectInput) | `PBT_APMSUSPEND` callback | no |
| crashed game's force is stopped | service watchdog (BF policy) + heartbeat | policy is BF; timeout is data in shared memory |
| a new DirectInput session starts neutral | BF: first tick sends the stop report | policy (tested) |
| installer changes only its own objects | `tools/InstallCommon.ps1` fixed identity + grammar | no |
| registry writes only under project keys | `system_registry_writable` | no |
