# Changelog

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
