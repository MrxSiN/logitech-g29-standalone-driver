# G29 Standalone *(logitech-g29-standalone-driver)*

A Windows user-mode Logitech G29 controller and DirectInput force-feedback driver with Brainfuck-owned policy.

G29 Standalone initializes and configures a Logitech G29 without requiring Logitech G HUB to run. It supports steering range, autocenter, LEDs, reconnect handling, diagnostics, and user-mode DirectInput force feedback.

It uses the existing Windows HID stack. It does not install a custom kernel driver, INF, test certificate, or require Windows Test Mode.

## Security

**This project is currently pre-release.**

The current version has not completed all physical-wheel and real-game validation and should not yet be treated as production or enterprise-ready software.

Force-feedback testing can move the wheel unexpectedly. Keep hands and objects clear when using diagnostic force commands.

## Background

The G29 requires device-specific initialization and configuration that would normally be handled by Logitech software. This project provides those functions through a small Windows service and user-mode DirectInput driver.

Most application policy is intentionally implemented in Brainfuck.

## So why Brainfuck?
Surely nobody would write an actual G29 driver in Brainfuck.
Anyway, here is the DirectInput support.

Brainfuck was selected for its rich ecosystem, mature package manager, excellent Windows SDK bindings, and comprehensive type system.

Just joking. The idea is pretty simple: keep as much of the G29 logic as possible out of the Windows code. Brainfuck happens to be very good at enforcing that, because it literally can’t call Windows APIs. Anything platform-specific has to go through a small bridge, so it’s much harder for the two layers to get mixed together.

The two sides communicate through a documented byte-frame ABI. This keeps operating-system mechanisms separate from G29 policy and allows the Brainfuck implementation to be tested against independent reference behavior.

The canonical program is assembled from `src/brainfuck/*.bfa` into `src/brainfuck/g29-main.bf`.

See `src/brainfuck/ABI.md` and `src/brainfuck/MEMORY_MAP.md` for the runtime interface.

## Install

Requires Windows 10 or Windows 11, administrator access, and a Logitech G29 with the selector in **PS3 mode**.

Building from source requires Visual Studio or Visual Studio Build Tools with the C++ workload.

```powershell
.\Install-Driver.ps1
```

Optional configuration:

```powershell
.\Install-Driver.ps1 -Range 900 -AutoCenter 0
```

To uninstall:

```powershell
.\Uninstall-Driver.ps1
```

## Usage

Common commands:

```text
g29ctl status
g29ctl doctor
g29ctl init --range 900 --autocenter 0
g29ctl range <40..900>
g29ctl autocenter <0..100>
g29ctl leds <0..31>
g29ctl stop
```

A bounded diagnostic force command is also available:

```text
g29ctl force <-25..25> [--milliseconds 50..5000] --i-understand
```

The installer also registers 64-bit and 32-bit user-mode DirectInput force-feedback drivers for supported games.

PS4 mode is detected but is not currently supported. Use the wheel in **PS3 mode**.

## Development

Build and run the test suite with:

```powershell
.\build.ps1
.\test.ps1
```

Regenerate the Brainfuck program with:

```powershell
.\bf.ps1
```

The test suite includes protocol, CLI, service, DirectInput, force-feedback, watchdog, VM, parity, and registration tests.

## Contributing

Issues and pull requests are welcome.

For changes affecting HID reports, device identification, force feedback, or other hardware-facing behavior, include the technical basis for the change and corresponding tests.

Run `.\test.ps1` before submitting a pull request.

## License

GPL-2.0-or-later. See `LICENSE` and `THIRD_PARTY_NOTICES.md`.

Copyright © G29 Standalone contributors.

Logitech and G29 are trademarks of their respective owners. This project is not affiliated with or endorsed by Logitech.
