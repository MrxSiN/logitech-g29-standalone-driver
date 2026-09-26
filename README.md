<div align="center">

# G29 Standalone

**A Logitech G29 driver with its device logic written in Brainfuck. Yes, really.**

Steering range, autocenter, LEDs and force feedback in games — without Logitech G HUB.

<br>

[![Release](https://img.shields.io/github/v/release/MrxSiN/logitech-g29-standalone-driver?color=5B3DF5&label=release&style=for-the-badge)](https://github.com/MrxSiN/logitech-g29-standalone-driver/releases)
[![Downloads](https://img.shields.io/github/downloads/MrxSiN/logitech-g29-standalone-driver/total?color=3DDC84&style=for-the-badge)](https://github.com/MrxSiN/logitech-g29-standalone-driver/releases)
[![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?style=for-the-badge)](#requirements)
[![Build](https://img.shields.io/github/actions/workflow/status/MrxSiN/logitech-g29-standalone-driver/build-test.yml?branch=main&label=build&style=for-the-badge)](https://github.com/MrxSiN/logitech-g29-standalone-driver/actions)
[![License](https://img.shields.io/badge/license-GPL--2.0--or--later-E8A33D?style=for-the-badge)](LICENSE)

</div>

```text                                                                                                                                          
                                                 @@@@@@
                                               @@@@@@@@@@                                                              
                                             @@@@@@@@@@@@@@                                                            
                                            @@@%@%@@@@@@@@@@                                                           
                                           @@@@@@@@@@@@@@%@@@                                                          
                                           @@%@%@@@@@@@@@%@@@                                                          
                                            @@@@@@@@@@@@@%@@:                                                          
                                            @@@@@@@@@@@@@@@@                                                           
                                              @@@@@%%@@@@@             ::                                              
                                                @@@@@@@     @@@@@@@@@@@@@@@@@@@@@@                                     
                                          @@@@          @@@@@@   :@+ -@@@   @@@@=@@@@@*                                
                                         @@@@@@@*  *@@@@@@@    @@@@@   @@@.   @@   @@@@@@@@@                           
                                       @@@@%@@@@@@@      @@   @@@@@@@   @@@@  @@:   =@@@@@@@@@@@                       
                                      @@@@@@@@@%@@@@@  #   @@@@@     @@@@@   =@@@@@@       @@@@@@@@                    
                                     @@@@@@@@%@%@%@@@@@@            @@@  @@@  @@@@@@@ @@@@@@@@  @@@@@@                 
                                   @@@@@@@@@@%@@@@@@@@@@@@@  @@@@@@% #@@@@@@@@  @@@@@ @: -@@@:@@@   @@@@               
                                  @@@@@@@@@%@@@@@  @@@@@@@@@@@   @@@@    :@@@@@@  @@@ @@     @   @@@@@@@@@             
                                -@@@@@@@@@@@@@@   %@  @@@@%%@@@@@   @@@@      @@@   @@ @@ @@@@@@@@  @@@@@@@@           
                               @@@@@@@@@@%@@@+ @         @@@@@%@@@@@    @@  @@@@@@        @@@@@  @@     @@@@@:         
                              @@@@@@@@%@@@@@   @@  @@    *  @@@@@%@@@@@  @ @@@@@@@ @@@@@@   @@  @@@@@@@ @@ =@@@        
                             @@@@@@@%@%@@@: *:           %@    @@@@@%@@@ @@ @@@    @@@@@@@#@@@  @@@@ #@@   @@ .@       
                           *@@@@@@@@@@@@@  @@@@                   @@@@@* @@  @@@+    @@@@@@@@   @@@@  @@@@ -    @=     
                          @@@@%@@@%@@@@                                  @@     @@@    @@     .@@@@@ @@  *  @@@ @@@    
                         @@@%@@@@@@@@@ @@                                    @@@   @#  @@@@   *=     @     @@  @@@@@   
                         @@@@@@@%%@@  @@=       .@@                     @      @@@@@@      @:       *@@@@@         @@  
                          @@@@@%@@@@@@ @@ @        @@           *%    @@   @@.     @@      @  :@@@@@@@@@@@@@%@@@%   @  
                          @@@@@@@@@ @@ @ @@ @       @@           @@      @=              @@@   @@@@@.               @@ 
                          @@%@@@%@@  @@         @@    @@                 .@@@           @@@     @                    @ 
                           @@@@@@%@@ @@        @@ @                @         @@@@      @@        @@@                 @ 
                           @@@@@@@@@      @@@  =@ @@@               @@@  @@     @@ @@             @@@    @           @ 
                           @@@@@@@@@        @@@     @@@@        @@@    =  @@      @@@                 @@@  @@@:      @ 
                            @@@@@@@@@                  %@@@        @ .                           =@@@  %  @@         @ 
                           @@@@@@@@@@                @@     .                                                        @ 
                         @@@@@@@%@@@                                                             @@@                 @ 
                        @@@@%@@%@@@     @                                    @@@                                    @@ 
                      @@@@@@@%@@@* @@  =@@@@@@@%@@:     @@    @                 @                                   @@ 
                     @@@@@@%@@@@    @@   @@@@@@@@@        %@@ @@@                            @@                    @@  
                   @@@@@%@@@@@@      @@+  @@@@@@%@@@@@          @     @:                   @@@                   @@@   
                @@@@@@@@@@@@@          @@     @@@@@@@@@@@@@        @@*                                          @@@    
               @@@@@@@@@@@@@             @@@    =@@@@@        @   @@                  =@-   @@@               @@@      
               @@@@@@@@%@@                 *@@@             @@@@@@                          @@@@@@@@@@@@@@@@@@-        
                 @@@@@@@@%                    @@     @@@  @@@   :@@                         @                          
                   @@@@@@@@@                   @@@@@@@@@@@@       @@@                     @@@                          
                     @@@@@@@@                    @                  @@@@                @@@@                           
                       @@@@@@                                          @@@@@.    :@@@@@@@                              
                         @@                                                .@@@@@@@@@                                  
```

---

> [!WARNING]
> **Physical safety.** Force feedback moves the wheel, sometimes unexpectedly. Keep your hands,
> cables, drinks, keyboards, pets, and anything else you would prefer not to launch across the
> room clear of the wheel while testing force output.

## Why Brainfuck?

Surely nobody would write an actual G29 driver in Brainfuck.

Brainfuck was selected for its rich ecosystem, mature package manager, excellent Windows SDK
bindings, comprehensive type system, first-class async support, and famously pleasant debugging
experience.

Just joking.

The real idea is much less cursed than it sounds: **keep as much of the G29-specific logic as
possible out of the Windows code.** Brainfuck happens to be very good at enforcing that boundary,
because it literally cannot call Windows APIs. Anything platform-specific has to go through a
small native bridge, which makes it much harder for the two layers to quietly melt together over
time.

|  | |
|---|---|
| 🎮 **No G HUB** | Initializes the wheel into native mode and applies range, autocenter and LEDs — from the command line, or from a service that starts when the wheel is plugged in. |
| 🏁 **Force feedback in games** | 64-bit and 32-bit DirectInput force-feedback drivers. Effects are mixed in software into the G29's single constant-force slot. |
| 🧱 **User mode only** | The normal Windows HID stack. No kernel driver, no INF, no certificate or test signing, and no Logitech software. |
| 🧠 **Policy in Brainfuck, compiled** | The device logic is raw Brainfuck, compiled ahead of time to C. The shipped binaries contain no interpreter. |
| 🛡️ **An independent safety net** | Native code refuses unknown HID reports, caps force, and stops it when the program stalls, fails or exits. |
| ↩️ **Reversible** | The installer is one transaction that rolls back on failure; the uninstaller removes everything it added. |

---

## Status

**v1.0.0 — first release.** The automated suite passes, and the wheel has been tested with the
device test and in Assetto Corsa. Longer hardware tests (soak, unplug and suspend under force)
and more games are still open; `docs/TESTING.md` tracks them and `docs/RELEASE.md` shows where
each release gate stands.

### Compatibility

Each row is one setup somebody has actually run. If you try another, please open a pull request
adding a row.

| Wheel | Windows | Games | Tester | Date |
|---|---|---|---|---|
| G29, PS3 mode (native, PID C24F, rev 8900) | 11 Pro 26200 | Assetto Corsa | @MrxSiN | 2026-09 |
| G29, PS3 mode (native, PID C24F, rev 8900) | 11 Pro 26200 | Assetto Corsa Competizione | @MrxSiN | 2026-09 |

### Known limits

- **PS4 mode** is detected but not supported. Use the wheel in **PS3 mode**.
- Whether games get force feedback from the machine-wide registration alone (without the per-user
  one) has not been verified (`docs/TESTING.md`, HIL-15).

## Requirements

| | |
|---|---|
| **Windows** | 10 or 11, with administrator rights for the installation |
| **Wheel** | Logitech G29 with the selector in **PS3 mode** |
| **G HUB** | Not needed. If it still owns the G29 registration, uninstall it or use `-SkipForceFeedback` |
| **Building from source** | Visual Studio or Build Tools with the C++ workload; the .NET Framework 4 C# compiler that ships with Windows |

## Install

1. Download `G29Standalone-v1.0.0.zip` from [Releases](https://github.com/MrxSiN/logitech-g29-standalone-driver/releases) and extract it.
2. Open PowerShell in the extracted folder and allow scripts for this window:

   ```powershell
   Set-ExecutionPolicy -Scope Process Bypass
   Get-ChildItem -Recurse | Unblock-File
   ```

3. Install:

   ```powershell
   .\Install-Driver.ps1                          # defaults: 900 degrees, no autocenter
   .\Install-Driver.ps1 -Range 900 -AutoCenter 0  # or choose
   ```

The installer asks for administrator rights for the installation step; from an elevated
PowerShell it warns and continues in that window. The service starts whenever the wheel is
plugged in and stops 15 seconds after it is removed.

<details>
<summary><b>What the installer does, exactly</b></summary>
<br>

- It installs the binaries in `artifacts\bin`, running `build.ps1` only when they are missing.
  **Installation and testing are separate:** after changing the source, run `build.ps1` or
  `test.ps1` first.
- Before the elevated step installs anything, it checks that the binaries still match the SHA-256
  hashes taken by the first step.
- It is a transaction: if something fails halfway, the machine is restored to its previous state,
  including an earlier installation's files, service and DirectInput registration.
- At the end it verifies that the `G29Standalone` service runs the installed `g29ctl.exe`.
- It only creates, stops or replaces `G29Standalone` when an existing service of that name points
  to `g29ctl.exe` in this project's install directory. Otherwise it leaves it alone.
- DirectInput registration is written machine-wide (`HKLM`) and for the account running the
  installer (`HKCU`). Run as `SYSTEM`, for example from a deployment tool, the per-user part lands
  in SYSTEM's own registry.
- If another vendor's driver — for example G HUB — still owns the G29 registration, the installer
  refuses to stomp over it. Install the service without force feedback instead:

  ```powershell
  .\Install-Driver.ps1 -SkipForceFeedback
  ```

</details>

### Uninstall

```powershell
.\Uninstall-Driver.ps1
```

This removes the service, the installed files and the per-user DirectInput registration from
every account on the machine, including accounts that are not signed in and SYSTEM — but only
keys whose own `Software\G29Standalone\DirectInput` marker says this project created them. If one
account's registry cannot be cleaned, the rest still runs and the failure is reported; run it
again to finish.

---

## Usage

```text
g29ctl status
g29ctl doctor
g29ctl init [--range 40..900] [--autocenter 0..100]
g29ctl range <40..900>
g29ctl autocenter <0..100>
g29ctl leds <0..31>
g29ctl stop
g29ctl watch [--range 40..900] [--autocenter 0..100]
g29ctl force <-25..25> [--milliseconds 50..5000] --i-understand
```

The `--i-understand` part is not decorative.

<details open>
<summary><b>🧪 Testing the actual wheel</b></summary>
<br>

```powershell
.\test.ps1 -Device
.\test.ps1 -Device -DeviceForce 25   # if 20 does not move your wheel
```

After the automated suite it checks the connected wheel, one line per step: identification,
`doctor`, initialization, range, autocenter, the LEDs (lit for two seconds), a short force in each
direction, and the installed driver through DirectInput. **The wheel turns briefly.** A stop is
always sent at the end, even after a failure or Ctrl+C.

Run on its own in a console, `test.ps1` offers the device test when a G29 is connected.

So if the wheel suddenly moves during that test, that is not Brainfuck becoming sentient. That
part is expected.

</details>

<details open>
<summary><b>📈 Tracing force feedback in a game</b></summary>
<br>

Start this from a **non-elevated** PowerShell before the game, and press `Ctrl+C` after:

```powershell
.\Trace-Driver.ps1
```

The log goes to `artifacts\logs`: the calls the game makes, any failures, and every five seconds
the DirectInput call latency and HID write rate, with a summary at the end. Close DebugView first;
only one debug-output listener works at a time.

</details>

<details>
<summary><b>🛡️ The safety envelope</b></summary>
<br>

Independently of whatever the Brainfuck program asks for, native code enforces:

- Unknown G29 HID reports are rejected.
- `g29ctl` is capped at **25% force**.
- Any `g29ctl` force held for more than **6 seconds** is stopped.
- Force is stopped if the Brainfuck program stops responding for **1 second** while force is
  active.
- The service itself is never allowed to apply force.

See `docs/ARCHITECTURE.md` and `docs/THREAT_MODEL.md` for the full design.

</details>

---

## How this ridiculous thing actually works

```
g29ctl.exe / service / game ──▶ native C bridge ──frames──▶ g29-main.bf (compiled)
                                HID · registry · service       identification · HID reports
                                timers · COM · shared memory   CLI · reconnect · watchdog
                                safety envelope                effect engine · force mixing
```

Most of the application policy is written directly in raw Brainfuck, in
`src/brainfuck/g29-main.bf`: device identification, the HID report bytes, command-line behavior,
reconnect handling, watchdog state machines, the DirectInput effect engine, force mixing, registry
registration and the installation plan.

And "raw Brainfuck" means exactly that. The file contains only the language's eight commands.
There is no macro language, no assembly-like layer, and no nicer source language secretly
generating it behind the scenes.

<details>
<summary><b>It does not interpret Brainfuck at runtime</b></summary>
<br>

The program is compiled ahead of time during the build. `tools/BfAot` validates it, then folds
runs, fixes constant cell offsets, finds closed forms for clear and multiply-add loops and a few
other loop shapes, propagates constants and proves tape bounds statically. It emits C, which
Visual Studio compiles with `/O2` into the binaries that actually ship.

So no, the final executable is not sitting there interpreting `++++[>++++<-]>.` every time you
turn the wheel. Details: `docs/AOT.md`.

</details>

<details>
<summary><b>Brainfuck is the policy layer. C is the bridge.</b></summary>
<br>

Brainfuck cannot call Windows APIs. That is not a missing feature. That is the architecture.

The native side exposes a deliberately small set of generic mechanisms — HID I/O, registry access,
service control, timers, COM and shared memory — which the program asks for through a versioned
byte-frame ABI (`src/brainfuck/ABI.md`). The Windows-facing layer stays mostly ignorant of G29
policy, and the policy can be tested against recorded reference behavior without a wheel attached.

A separate safety boundary lives in native code: even if the Brainfuck side gets confused, crashes
or stalls, the bridge still refuses unknown G29 HID reports, limits force per host, and stops force
when the program stops behaving.

</details>

<details>
<summary><b>Architecture rabbit hole</b></summary>
<br>

| | |
|---|---|
| `docs/ARCHITECTURE.md` | overall architecture |
| `src/brainfuck/ABI.md` | frame ABI |
| `src/brainfuck/MEMORY_MAP.md` | Brainfuck tape layout |
| `docs/AOT.md` | compiler and execution semantics |
| `docs/PERFORMANCE.md` | performance notes |
| `AI_MAINTENANCE.md` | maintaining the Brainfuck program without completely losing your mind |

</details>

---

## Development

```powershell
.\build.ps1                     # binaries in artifacts\bin
.\test.ps1                      # build + full test suite
.\test.ps1 -Device              # also the connected wheel
.\test.ps1 -Sanitize -Analyze   # also AddressSanitizer and /analyze
.\test.ps1 -Reproducible        # clean rebuild must be byte-identical
artifacts\bin\G29.Tests.exe --benchmark
```

The normal suite needs **no wheel, no G HUB, no administrator rights and no network access**. It
covers the protocol, CLI, service, reconnect monitor, DirectInput, force feedback, watchdog,
registration and install scripts. Every scenario runs on the reference interpreter and on the
compiled program, and the results must match. It also fuzzes the compiled code against the
interpreter, tests the native safety envelope (output guard, force lease, frame parser), and checks
the architecture itself: raw Brainfuck only, no generating source layer, no interpreter in the
shipping binaries. GitHub Actions runs it on every push and pull request.

## Security and physical safety

If you find a security issue or anything that could create a physical-safety problem, read
`SECURITY.md` before reporting it.

This is software that can command a motor attached to a steering wheel. Bugs are funny right up
until the steering wheel develops an opinion.

## Contributing

Issues and pull requests are welcome. Changes that affect HID reports, device identification,
force feedback or other hardware-facing behavior should include the technical basis for the change
and matching tests. Run `.\test.ps1` before submitting.

If you are editing the Brainfuck, my condolences. Please also read `AI_MAINTENANCE.md`.

## License

GPL-2.0-or-later. See `LICENSE` and `THIRD_PARTY_NOTICES.md`.

Copyright © G29 Standalone contributors.

Logitech and G29 are trademarks of their respective owners. This project is not affiliated with or
endorsed by Logitech.
