# G29 Brainfuck ABI, version 1

The Brainfuck program and the bridge exchange framed bytes and nothing else.
The bridge writes event frames to the program's input (`,`) and parses command
frames from its output (`.`). Numbers below are frozen: change them only
together with this file, the program and the tests.

## Frame

```text
offset size meaning
0      1    0xA5 magic
1      1    ABI version (1)
2      1    message type
3      1    flags (0 unless stated)
4      2    sequence, little-endian
6      2    payload length, little-endian, at most 4096
8      N    payload
```

- All integers are little-endian and fixed width unless stated.
- Flags are reserved: every frame in version 1 carries 0. The bridge rejects a
  command frame with any flag set (forward compatibility: a flag a future
  version defines is never silently ignored by an older bridge).
- Text is a u16 byte length followed by UTF-8 bytes. The bridge converts
  UTF-16 to UTF-8 and back; it never interprets the text.
- Sequence: a command from the program carries a sequence the bridge echoes in
  the matching result event. Sequence 0 is used for unsolicited events.
- The bridge rejects any malformed command frame (bad magic, version, set
  flags, length over 4096, unknown type, payload that does not match its
  schema, including trailing bytes after the last field). Magic and version are
  checked as their bytes arrive, the rest of the header once all 8 bytes are
  in. A rejected command never becomes a Win32 call; the bridge treats it as a
  program failure: the output guard trips (see below), the stop report is
  written to every interface holding a force, and the session ends.
- The program validates every event frame. Bad magic, bad version or a length
  over 4096 is fatal: the program emits `CMD_ABI_ERROR` and `CMD_EXIT`, then
  stops. An unknown event type or an event that is not valid in the current
  state is skipped (its payload is consumed) and answered with `CMD_ABI_ERROR`.

## Events (bridge -> program)

| Type | Name | Payload |
|---|---|---|
| 0x01 | EV_BOOT | role u8, pointer_size u8, process_arch u8, os_flags u8, argc u16, argc x text (arguments without the executable name) |
| 0x02 | EV_TIMER | timer u8, now_us u64, tick_ms u64 (system tick count, GetTickCount64) |
| 0x03 | EV_SHUTDOWN | (empty) |
| 0x04 | EV_OS_ERROR | code u32 |
| 0x05 | EV_TEST | test data (role TEST only) |
| 0x06 | EV_HOST_INFO | module directory text |
| 0x10 | EV_HID_ENUM_BEGIN | (empty) |
| 0x11 | EV_HID_DEVICE | token u32, vendor u16, product u16, version u16, usage_page u16, usage u16, input_len u16, output_len u16, path text, product text |
| 0x12 | EV_HID_ENUM_END | count u16 |
| 0x13 | EV_HID_OPEN_RESULT | token u32, win32 u32 (0 = success) |
| 0x14 | EV_HID_WRITE_RESULT | token u32, win32 u32 |
| 0x15 | EV_HID_INPUT | token u32, value u32, now_us u64 |
| 0x16 | EV_HID_USAGE_RESULT | token u32, status u32, logical_min i32, logical_max i32, bit_size u16 |
| 0x17 | EV_HID_DISCONNECTED | token u32 |
| 0x18 | EV_HID_POLL_RESULT | token u32, status u32 |
| 0x20 | EV_PROCESS_ENUM_BEGIN | (empty) |
| 0x21 | EV_PROCESS | pid u32, name text |
| 0x22 | EV_PROCESS_ENUM_END | count u16 |
| 0x30 | EV_REG_RESULT | status u32, then operation-specific data |
| 0x31 | EV_SERVICE_RESULT | status u32 |
| 0x32 | EV_FILE_RESULT | status u32 |
| 0x33 | EV_SHARED_MEMORY_RESULT | status u32, then operation-specific data |
| 0x40 | EV_SERVICE_STARTED | (empty) |
| 0x41 | EV_SERVICE_STOP | (empty) |
| 0x42 | EV_SERVICE_SHUTDOWN | (empty) |
| 0x43 | EV_SESSION_CHANGE | reserved |
| 0x44 | EV_CANCEL | (empty) — Ctrl+C / console close |
| 0x50..0x5A | EV_DI_* | DirectInput calls, see below |

Payloads marked with a phase are specified when that phase lands.

### Roles (EV_BOOT)

```text
1 CLI   2 SERVICE   3 DIRECTINPUT   4 TEST
```

The program, not the bridge, decides what to do with the role and arguments.

## Commands (program -> bridge)

| Type | Name | Payload |
|---|---|---|
| 0x80 | CMD_EXIT | status u32 (process exit code) |
| 0x81 | CMD_LOG | severity u8 (0 info, 1 warning, 2 error), text |
| 0x82 | CMD_CONSOLE_WRITE | stream u8 (1 stdout, 2 stderr), text (written verbatim; the program supplies line breaks) |
| 0x83 | CMD_EVENTLOG_WRITE | severity u8, text — one event-log entry: the pending CMD_TEXT_APPEND text followed by this text |
| 0x84 | CMD_ABI_ERROR | code u8 (error model below), event type u8 |
| 0x85 | CMD_TEXT_APPEND | text — appended to the pending event-log text |
| 0x86 | CMD_HOST_INFO | (empty) |
| 0x8F | CMD_TEST_RESULT | test data (role TEST only) |
| 0x90 | CMD_HID_ENUMERATE | (empty) |
| 0x91 | CMD_HID_OPEN | token u32 |
| 0x92 | CMD_HID_CLOSE | token u32 |
| 0x93 | CMD_HID_WRITE | token u32, length u16, bytes |
| 0x94 | CMD_HID_READ_START | token u32, usage_page u16, usage u16 |
| 0x95 | CMD_HID_READ_STOP | token u32 |
| 0x96 | CMD_HID_GET_INPUT_REPORT | token u32 |
| 0x97 | CMD_HID_GET_USAGE_VALUE | token u32, usage_page u16, usage u16 (answers the usage's logical range) |
| 0xA0 | CMD_TIMER_ARM | timer u8, repeat u8, interval_ms u32 |
| 0xA1 | CMD_TIMER_CANCEL | timer u8 |
| 0xB0..0xB6 | registry and file | see below |
| 0xB7 | CMD_SERVICE_RUN | name text — connect this process to the Service Control Manager as that service; start, stop and shutdown arrive as EV_SERVICE_STARTED / EV_SERVICE_STOP / EV_SERVICE_SHUTDOWN, and the host waits up to 10 s for CMD_EXIT after a stop |
| 0xB8..0xBD | service control | reserved |
| 0xBE | CMD_SERVICE_STOP_SELF | (empty) — ask the SCM to stop this service |
| 0xC0 | CMD_PROCESS_ENUMERATE | (empty) |
| 0xC8 | CMD_SHM_CREATE | name text, size u32, everyone u8 (1 = every user may read and write) |
| 0xC9 | CMD_SHM_OPEN | name text, size u32 |
| 0xCA | CMD_SHM_READ | handle u32, offset u32, length u16 |
| 0xCB | CMD_SHM_WRITE | handle u32, offset u32, length u16, bytes |
| 0xCC | CMD_SHM_CLOSE | handle u32 |
| 0xD0 | CMD_DI_RETURN | hresult u32, count u8, count x (offset u16, value u32) — answer to the pending EV_DI_* call (same sequence) |
| 0xF0 | (retired) | was CMD_LEGACY, the transitional hand-back to legacy C# commands; unused since phase 8 and refused by the bridge |

### Argument and HID details

- `EV_BOOT` argv are the process arguments *without* the executable name.
- `CMD_HID_ENUMERATE` is answered, in order and with the command's sequence, by
  `EV_HID_ENUM_BEGIN`, one `EV_HID_DEVICE` per HID interface the bridge could
  inspect (any vendor; the bridge never filters), and `EV_HID_ENUM_END`.
  Tokens are never reused within a process; a token names one interface path.
- `CMD_HID_OPEN` opens the token's path for read/write with shared read/write
  access and answers `EV_HID_OPEN_RESULT` (win32 0 = success). `CMD_HID_CLOSE`
  closes it (no answer).
- `CMD_HID_WRITE` sends the payload as one output report: at offset 0 when the
  collection's output report length equals the payload length, otherwise after
  a zero report ID, zero-padded to the report length (a payload longer than the
  report is refused with ERROR_INVALID_PARAMETER). `HidD_SetOutputReport` is
  tried first, then `WriteFile`. Answer: `EV_HID_WRITE_RESULT`.
- `CMD_TIMER_ARM` (timer u8, repeat u8, interval_ms u32) posts `EV_TIMER` (timer,
  now_us) after the interval, repeatedly when repeat = 1. Arming an armed timer
  replaces it. `CMD_TIMER_CANCEL` stops it; an `EV_TIMER` already queued may
  still arrive, so the program ignores timer events it no longer expects.
  `now_us` is a monotonic microsecond clock below 2^48.
- `EV_CANCEL` is posted on Ctrl+C / Ctrl+Break; the bridge suppresses the default
  termination and lets the program decide.

- `CMD_HID_READ_START` opens the token's interface for input and extracts the
  given usage's value from every input report the device sends, posting
  `EV_HID_INPUT` (token, value, now_us; sequence 0) for each. It is answered
  by `EV_HID_OPEN_RESULT` (win32 0 = reading). A reader that fails later posts
  `EV_HID_DISCONNECTED`. `CMD_HID_READ_STOP` ends it (no answer).
- `CMD_HID_GET_INPUT_REPORT` requests one input report from a reading token
  (HidD_GetInputReport); on success the bridge posts `EV_HID_INPUT`, then
  always `EV_HID_POLL_RESULT` (status 0 or a Win32 error).
- `CMD_HID_GET_USAGE_VALUE` answers `EV_HID_USAGE_RESULT` with the input
  value capabilities of that usage (HidP_GetSpecificValueCaps): status 0 or a
  Win32 error (1168 when the usage does not exist), logical minimum and
  maximum as reported (the program handles an unusable range) and the bit
  size.

### Shared memory (phase 12)

- `CMD_SHM_CREATE` creates (or opens, when it exists) a named file mapping
  and `CMD_SHM_OPEN` opens an existing one, both for read/write; each answers
  `EV_SHARED_MEMORY_RESULT` (0x33): status u32 (0, or a Win32 error such as 2
  when it does not exist or 5 when access is denied), handle u32.
  `CMD_SHM_READ` answers `EV_SHARED_MEMORY_RESULT` with status 0 and the bytes
  (reading outside the mapping is an ABI violation). `CMD_SHM_WRITE` writes bytes
  inside the opened size (anything outside is an ABI violation); it has no
  answer. `CMD_SHM_CLOSE` closes the handle.

### DirectInput calls (phase 12, role DIRECTINPUT)

The COM shell turns each `IDirectInputEffectDriver` call into one event with a
fresh non-zero sequence and blocks the calling thread until the program answers
`CMD_DI_RETURN` with the same sequence. Calls are serialized. Every payload
starts with `now_us u64`, then:

| Type | Method | Payload after now_us | Output offsets |
|---|---|---|---|
| 0x50 | DeviceID | DirectInput version u32, external id u32, begin u32, internal id u32, init present u8, init dwSize u32, init structure size u32, inspected u8, token u32, vendor u16, product u16, version u16, usage page u16, usage u16, input length u16, output length u16 | — |
| 0x51 | GetVersions | present u8, dwSize u32 | 4, 8, 12 |
| 0x52 | Escape | id u32, effect u32 | — |
| 0x53 | SetGain | id u32, gain u32 | — |
| 0x54 | SendForceFeedbackCommand | id u32, command u32 | — |
| 0x55 | GetForceFeedbackState | id u32, present u8, dwSize u32 | 4, 8 |
| 0x56 | DownloadEffect | id u32, effect id u32, handle present u8, handle u32, then the DIEFFECT serialization (below) | 0 (the new handle) |
| 0x57 | DestroyEffect | id u32, effect u32 | — |
| 0x58 | StartEffect | id u32, effect u32, mode u32, count u32 | — |
| 0x59 | StopEffect | id u32, effect u32 | — |
| 0x5A | GetEffectStatus | id u32, effect u32, present u8 | 0 |

- DeviceID: the shell reads `DIHIDFFINITINFO` only when the pointer is not null
  and dwSize covers the structure, and then inspects the device interface path
  like `CMD_HID_ENUMERATE` does (inspected = 0 when that fails). The token names
  that interface for the program's HID commands.
- DIEFFECT: the shell never reads past dwSize, and follows the direction,
  envelope and type-specific pointers only when the call's DIEP flags mark them
  valid; it sends at most 24 type-specific bytes.
- Outputs are written as 32-bit values into the method's buffer (versions and
  state: bounded by their dwSize; handle and status: 4 bytes). A value outside
  the buffer, a missing answer within 10 s, or a program failure makes the call
  return E_FAIL (0x80004005) and runs the emergency force stop.
- The native in-process server cannot run the program at process exit (the
  process's other threads are already gone when a DLL learns of it), so at
  process exit only the emergency guard acts: it sends the stop report to any
  wheel it last saw holding a non-zero force. The program's own session end
  (`EV_DI_DEVICE_ID` with begin = 0) runs whenever DirectInput releases the
  wheel. (The transitional .NET shell posted `EV_SHUTDOWN` at exit instead.)
- Timers in role DIRECTINPUT are paced by a sleep loop with a 1 ms system timer
  resolution while an interval below 15 ms is armed; a tick is skipped while the
  program still has unread input.

- Event-log text (`CMD_EVENTLOG_WRITE`) in a running service goes to the
  Application log under the service's name as the event source; the native
  bridge registers that source (message file: `g29ctl.exe`, whose message 0 is
  the text itself) the first time it writes, when it is not registered yet.
  Outside a service it goes to stderr.

### Host, process, file and registry details (phase 8)

- `CMD_HOST_INFO` (0x86, empty) is answered by `EV_HOST_INFO` (0x06): module
  directory text (the directory of the running executable, with a trailing
  backslash).
- `CMD_PROCESS_ENUMERATE` (0xC0) is answered by `EV_PROCESS_ENUM_BEGIN`, one
  `EV_PROCESS` (pid u32, process name text without ".exe") per running process,
  and `EV_PROCESS_ENUM_END` (count u16). The bridge does not filter.
- `CMD_FILE_INFO` (0xB6): path text. Answer `EV_FILE_RESULT` (0x32): status
  u32 (0 = the file exists, otherwise a Win32 error), then two texts that
  are always empty in the native bridge (the transitional .NET bridge reported
  a .NET assembly's full name and version there; nothing uses them since
  phase 15).
- Registry commands take `root u8` (1 HKEY_LOCAL_MACHINE, 2 HKEY_CURRENT_USER),
  `view u8` (0 default, 1 64-bit, 2 32-bit) and a key path text, and are each
  answered by one `EV_REG_RESULT` (0x30) that starts with status u32 (0 ok,
  2 = key or value not found, otherwise a Win32 error):

| Type | Command | Extra request fields | Extra result fields |
|---|---|---|---|
| 0xB0 | CMD_REG_READ | value name text | kind u8 (0 none, 1 string, 2 dword, 3 binary, 4 other), then text (string), u32 (dword) or nothing |
| 0xB1 | CMD_REG_WRITE | value name text, kind u8 (1 string, 2 dword, 3 binary), data: text / u32 / u16 length + bytes | — (creates missing keys) |
| 0xB2 | CMD_REG_CREATE_KEY | — | existed u8 |
| 0xB3 | CMD_REG_DELETE_TREE | — | — (a missing key is not an error) |
| 0xB4 | CMD_REG_KEY_INFO | — | subkey count u32, value count u32 (status 2 when missing) |
| 0xB5 | CMD_REG_DELETE_KEY | — | — (only an empty key; a missing key is not an error) |

  Strings are read like .NET `RegistryKey.GetValue` read them (expandable
  strings expanded; at most 1000 characters are returned). The bridge never contains the project's key paths, class
  ID or blobs; the program sends them.

### Emergency output guard (bridge, not Brainfuck)

Before any `CMD_HID_WRITE` reaches Windows, the bridge's guard
(`src/bridge/common/guard.c`) checks the payload against the report families
of the G29 protocol (`docs/PROTOCOL.md`): exactly 7 bytes, a known first byte
(and second byte for `F8`/`11`/`FE`), every fixed byte as specified and every
variable field inside its bound (range 40..900, LED mask 0..31, autocenter
`a` 0..7 with `a` repeated and `b` 1..255, constant force `11 08 v 80 00 00 00`).
Anything else is an ABI violation. A constant force with `|v - 0x80|` above the
host's ceiling is refused: CLI 25 % (offset 32), DirectInput full scale, and 0
once the process has become the service (`CMD_SERVICE_RUN`), which never
applies force. The ceiling can only be lowered.

The guard remembers which interfaces were last sent a non-zero force and since
when (a stop report or a zero constant force releases one; a changed force
continues the hold). On a program fault, ABI violation, lease expiry (below) or
process exit, the guard *trips*: it writes the stop report
(`13 00 00 00 00 00 00`) to every such interface without running any more
Brainfuck, refuses every later non-zero force, and immediately stops again any
force that passed its check before the trip but was written after it.

### Force lease (bridge, not Brainfuck)

A native thread per host (`src/bridge/common/lease.c`) checks every 50 ms,
while the guard holds a force:

- hold limit: CLI 6000 ms (the 5000 ms diagnostic plus timer and write
  latency); DirectInput none, because a DirectInput effect of INFINITE
  duration is legitimate while the game runs;
- liveness: input left unread by the program for more than 1000 ms means the
  program is stuck (in a loop inside its iteration budget, or blocked in a host
  call such as a hung HID write).

Either expires the lease: the guard trips and the session fails. The program
cannot arm, extend or disable the lease. The DirectInput host also stops every
held force when Windows announces a suspend (`PBT_APMSUSPEND`); the game's next
change of force applies again after resume. A game process that dies without
running its DLL's detach is covered by the service watchdog (the program's role SERVICE,
1000 ms heartbeat timeout), not by the lease.

### Registry capability

Registry commands that change something (`CMD_REG_WRITE`, `CMD_REG_CREATE_KEY`,
`CMD_REG_DELETE_TREE`, `CMD_REG_DELETE_KEY`) are accepted only for key paths at
or below `SOFTWARE\Classes\CLSID\{D252A2D4-A917-47D3-BD1B-F5A0138CFE12}`,
`System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_046D&PID_C24F`
or `Software\G29Standalone` (case-insensitive, no empty segment, no trailing
backslash), in either root and view. Any other path is an ABI violation. Reads
are not limited. `CMD_SERVICE_RUN` accepts only a name of 1..64 letters, digits
and underscores, once.

## Error model

Application error codes (used in `CMD_ABI_ERROR`, logs and mapped by the
program to exit statuses and HRESULTs):

```text
0  OK                     9  HID_OPEN_FAILED
1  BAD_ABI_FRAME         10  HID_WRITE_FAILED
2  BAD_ARGUMENT          11  DEVICE_LOST
3  OUT_OF_RANGE          12  REGISTRY_CONFLICT
4  NO_DEVICE             13  EFFECT_TABLE_FULL
5  PS4_MODE              14  UNKNOWN_EFFECT
6  MULTIPLE_DEVICES      15  UNSUPPORTED
7  NOT_NATIVE            16  INTERNAL_BF_ERROR
8  REENUMERATION_TIMEOUT 17  UNKNOWN_MESSAGE
                         18  BAD_STATE
```

## Limits

| Limit | Value | Enforced by | On violation |
|---|---|---|---|
| program text | 32 MiB, only `><+-.,[]` and line endings | `tools/BfAot` at build time | the build fails |
| brackets | balanced | `tools/BfAot` at build time | the build fails |
| tape | 131072 cells; accesses proven in range at build time or checked where the pointer reaches a new extreme | compiled code | program fault |
| cells | signed 32-bit, checked, never wrap | compiled code | program fault |
| output byte | 0..255 | `bf_out` | program fault |
| loop iterations between two input reads | 100 000 | compiled code (`BF_ITERATION`) | program fault |
| command payload | 4096 bytes | frame parser | ABI violation |
| unread input | 1 MiB | session | session fails closed |
| input stalled while a force is held | 1000 ms | force lease | guard trips, session fails |
| continuous force, CLI | 6000 ms | force lease | guard trips, session fails |
| DirectInput call answered | 10 s | DirectInput host | `E_FAIL`, guard trips, session fails |
| service stop answered | 10 s | service host | the service stops (its guard never admits force) |

The iteration budget is derived from measurement, not chosen large: the test
suite runs every recorded scenario on the compiled program, reports the
longest legitimate stretch between two reads (2 493 loop iterations) and fails
if the budget is not at least 20 times that. Execution semantics:
`docs/AOT.md`. Every fault, violation and expiry above ends in the
same place: guard trip (stop report, no further force), then the host exits or
answers `E_FAIL`.

## Versioning

The ABI version is the frame's second byte (1). A frame of any other version
is rejected by both sides (the program: fatal `CMD_ABI_ERROR` and `CMD_EXIT`;
the bridge: violation). There is no negotiation: the program and the bridge
ship in the same binary, so a mismatch can only be a build error. Changing a
payload, a type or a limit incompatibly requires a new version. Adding an event
type is compatible (the program skips unknown events); adding a command type
is not (an older bridge rejects it).

## Ordering, sequences and retries

A host processes command frames strictly in the order the program writes
them, on the program's thread; each answer event carries the command's
sequence. Events from other threads (timers, HID input, service control) are
queued whole and delivered in order of posting. The bridge retries nothing: a
failed Win32 call is reported to the program (status or Win32 error), which
decides. Every G29 report is a set-state command, so a repeated
`CMD_HID_WRITE` is harmless.

## Role TEST (phase 2)

Used by the automated tests only.

- `EV_BOOT` role 4: the program answers `CMD_TEST_RESULT` sequence 0 with the
  payload `42 4F 4F 54` ("BOOT").
- `EV_TEST`: the first payload byte selects an entry point; the answer is a
  `CMD_TEST_RESULT` with the same sequence.

| Selector | Arguments | Answer |
|---|---|---|
| `E` | data | `count u32` + data; `count` = echo events so far (state persists) |
| `I` | PID u16, revision u16 | status, identification flags (bit 0 native, bit 1 G29, bit 2 PS4) |
| `N` | | status, the two native-switch reports |
| `S` | | status, the stop report |
| `R` | degrees u16 | status, range report; status 3 unless 40..900 |
| `A` | percent u8 | status, one or two autocenter reports; status 3 above 100 |
| `L` | mask u8 | status, LED report; status 3 above 31 |
| `F` | negative u8, magnitude u8 | status, force report; status 3 unless 0/1 and 0..100 |
| `G` | engine operation + arguments | 6 bytes: status u8, negative u8, value u32 (phase 9-10, see below) |
| `W` | steering operation + arguments | status, then the operation's values (phase 11, see below) |

  Status is 0 (ok), 1 (payload too short), 3 (out of range) or 17 (unknown
  selector). The tests use these entry points to check the protocol.
- `G` drives the effect engine directly. Every answer is status u8 (0 ok,
  1 unknown handle, 2 no free slot, 3 out of range), negative u8, value u32.
  Signed numbers are sign u8 + magnitude u32; times are u64 microseconds. The
  role TEST program initializes the engine at boot. Operations:

  | Op | Arguments | Meaning / value |
  |---|---|---|
  | `c` | params | create; value = handle |
  | `u` | handle u32, params | update |
  | `s` | handle, iterations infinite u8, iterations u32, solo u8, now | start |
  | `p` / `d` | handle | stop / destroy |
  | `A` / `R` | | stop all / reset |
  | `P` / `C` | now | pause / continue |
  | `a` / `g` | on u8 / gain u32 | actuators / device gain |
  | `f` | now, position, velocity, acceleration (signed each) | mixed force |
  | `y` | handle, now | playing (0/1) |
  | `n` | now | any effect playing (0/1) |
  | `k` / `q` / `o` | | count / paused / actuators on |
  | `D` | params, DIEFFECT serialization | apply a DIEFFECT to params; answers status (0 ok, 3 invalid) and the 87-byte params |

  params: kind u8, duration u32 (FFFFFFFF = infinite), gain u32, delay u32,
  direction negative u8, envelope u8, attack level u32, attack time u32, fade
  level u32, fade time u32, magnitude, ramp start, ramp end, offset (signed
  each), phase u32, period u32, condition offset, positive coefficient,
  negative coefficient (signed each), positive saturation u32, negative
  saturation u32, dead band (signed). A u32 that does not fit a cell is
  saturated at 2^30.
- The DIEFFECT serialization (`EV_DI_DOWNLOAD_EFFECT`, `D` above) is written
  by `serialize_effect` in `src/bridge/directinput/driver.c`;
  `tests/ReaderParityTests.cs` and `tests/EffectDriverShellTests.cs` pin it
  with vectors.
- `W` drives the steering motion: `x` sets the axis (logical
  minimum and maximum as sign u8 + u32, bit size u16) and answers the effective
  range, `s` feeds one raw steering value (u32) at a time (u64 us), `m` answers
  position, velocity and acceleration (sign u8 + u32 each) at a time.
- `EV_SHUTDOWN`: `CMD_EXIT` status 0, then the program ends.
