# Changelog

## v1.1.0

Fixes the idle CPU use reported in #1 and moves the scripts out of the
repository root. Automated suite only; not run on a physical wheel.

### Fixed

- **CPU use in processes that hold the wheel without force feedback**
  ([#1](https://github.com/MrxSiN/logitech-g29-standalone-driver/issues/1)).
  Any process that opens the G29 through DirectInput loads the force-feedback
  driver, including Windows gaming and Xbox background services. The driver
  ran its 2 ms force loop, and kept the system timer at 1 ms resolution, for
  as long as the device stayed open, even with no effect downloaded. The
  program now stops the force timer on the first tick at which no effect
  exists (after the stop report and an inactive heartbeat) and starts it
  again when a game downloads an effect. Games that use force feedback keep
  the same 2 ms loop.

### Documentation

- README: where the G29's DirectInput registration lives in the registry, for
  removing a leftover G HUB registration.

### Changed

- **Scripts moved to `scripts\`.** Run `.\scripts\Install-Driver.ps1`,
  `.\scripts\Uninstall-Driver.ps1`, `.\scripts\Trace-Driver.ps1`,
  `.\scripts\build.ps1` and `.\scripts\test.ps1`; `native.ps1` moved to
  `tools\`. The repository root holds no scripts any more.

## v1.0.0

First release. Tested on a Logitech G29 (native mode, PID C24F, revision 8900)
on **Windows 11**, with Assetto Corsa.

### What's in it

- **The G29 without G HUB.** Initializes the wheel into native mode and sets
  steering range, autocenter and LEDs, from `g29ctl` or from a background
  service that starts when the wheel is plugged in.
- **Force feedback in games.** A DirectInput force-feedback driver
  (`g29ffb64.dll` and `g29ffb32.dll`) for 64-bit and 32-bit games.
- **User mode only.** Uses the Windows HID stack: no kernel driver, INF,
  certificate or test signing, and no Logitech software.
- **Device logic in raw Brainfuck,** compiled ahead of time to C. The shipped
  binaries contain no interpreter.
- **An independent safety layer** in native code. It refuses unknown HID
  reports, limits force, and stops force when a program stalls, fails or
  exits.
- **Reversible installation.** `Install-Driver.ps1` installs as one
  transaction and rolls back on failure. `Uninstall-Driver.ps1` removes
  everything it added.
- **Device test.** `test.ps1 -Device` tests the connected wheel: status, LEDs,
  range, autocenter, a short force in each direction, and the installed
  driver through DirectInput.
- **Game trace.** `Trace-Driver.ps1` records what the force-feedback driver
  does while a game runs, including DirectInput call latency and the HID write
  rate every 5 seconds.

### Notes

- The installer can run from an elevated PowerShell. It warns and installs in
  that window.
- Assetto Corsa sends a Logitech-specific `Escape` request about 25 times a
  second. The driver does not support it and answers "not implemented", which
  does not affect force feedback.
- The binaries are not code-signed.
- Hardware validation is partial: the soak, unplug and suspend tests, and
  games other than Assetto Corsa, are still open (`docs/TESTING.md`).
