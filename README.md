# G29 Standalone
### *A Logitech G29 driver with its device logic written in Brainfuck. Yes, really.*

```text                                                                      
                                =@@@@@@@@@                                           
                                #@@@@@@@@@@@                                          
                                @@@@@@@@@@@@                                          
                                @@@@@@@@@@@@                                          
                                @@@@@@@@@@#                                          
                                    @@@@@@    @@@@@@@@@@@@@                            
                                @@@       @@@@@  @ .@@  @@@@@@@*                       
                            @@@@@@  @@@@@   @@@@  *@:  @  @@@@@@@                   
                            @@@@@@@@@@     *@@@  @:@@@% @@@       @@@@@               
                            @@@@@@@@@@@@@@    %   -@@ +@  @@@@@ @@@@  @ @@@            
                        @@@@@@@@@@@@@@@@@@- @@@@  @@@@@  @@@ @  @@@@@-@@@@          
                        @@@@@@@@@@+  @ @@@@@@@@  @@@    .@+  @ @ @@@@@  @@@@@@        
                        @@@@@@@@@@ @#      @@@@@@@@  :@ @@@@:      @@  @@   @@@@=      
                    @@@@@@@@@@     #@   @  @@@@@@@:@ @@:  @@@@@  @  @@@@@   @@@     
                    @@@@%@@@@  @@%             @@@@ @  @@@   @@@@@@  @@  @@@    @    
                    @@@@@@@@@                         @:    @@  @@   @@@@      @@*@@:  
                    @@@@@@@@ @@      @@              @:   @@@@@    @      @@@@     @@  
                    @@@@@@@@@ @@      @@        @@  @   @    *@   *@ @@@@@@@@@@@@@  +@ 
                    .@@@@@@  @@         @@           @@@:        @@    @             @ 
                    @@@@@@: @   = %@@@          +@      @@@    @@     @@@           @.
                    @@@@@@@    @@@   @@@.         @@*@@   #@@@          +  @# @@:   @%
                    @@@@@@       *     @@@    :@@    ::    @          @@ @@ @+     @%
                    @@@@@@@            @@                                           @ 
                @@@@@@@@:  @                          =%            %*            @ 
                @@@@@@@@ @@ @@@@@%:*    @@  @=          %@                        +@ 
                %@@@@@@@*   @= -@@@@@@ *    %  @@                  @@@              @@ 
            @@@@@@@@@      @@   %@@@@@@@@@      @@@                             @@@  
            @@@@@@@@@         @@@   @@*      @+ @%            +@#  @@@         @@@    
            @@@@@%              @@        @@+@@#                  @  @@@@@@@@@       
            -@@@@@@             @@@@@@@@@@    @@+              @@@                  
                =@@@@@              :             @@@+         @@@                    
                @@@                                @@@@@@@@@@:                      
```

Surely nobody would write an actual G29 driver in Brainfuck.

Brainfuck was selected for its rich ecosystem, mature package manager, excellent Windows SDK bindings, comprehensive type system, first-class async support, and famously pleasant debugging experience.

Just joking.

The real idea is much less cursed than it sounds: **keep as much of the G29-specific logic as possible out of the Windows code.** Brainfuck happens to be very good at enforcing that boundary, because it literally cannot call Windows APIs.

Anything platform-specific has to go through a small native bridge. HID access, registry operations, service control, timers, COM, and shared memory live on one side. The G29 policy lives on the other. That makes it much harder for the two layers to quietly melt together over time.

The result is **G29 Standalone**: a Windows user-mode controller and DirectInput force-feedback driver for the Logitech G29. It can initialize and configure the wheel without Logitech G HUB, including steering range, autocenter, LEDs, reconnect handling, diagnostics, and user-mode force feedback for games.

It uses the normal Windows HID stack. There is **no kernel driver**, **no INF**, **no certificate or test signing**, and it does not copy or depend on Logitech software.

---

## ⚠️ Current status

This project is still **pre-release**.

The automated test suite passes, but physical-wheel and real-game validation is not finished yet. `docs/TESTING.md` tracks the remaining hardware-in-the-loop work.

In other words: it is far enough along to test, inspect, and experiment with, but it should **not** be treated as production software yet.

> **Physical safety:** force-feedback tests can move the wheel unexpectedly. Keep your hands, cables, drinks, keyboards, pets, and anything else you would prefer not to launch across the room clear of the wheel while testing force output.

---

## How this ridiculous thing actually works

Most of the application policy is written directly in raw Brainfuck:

```text
src/brainfuck/g29-main.bf
```

That includes:

- device identification
- the HID report bytes
- command-line behavior
- reconnect handling
- watchdog state machines
- the DirectInput effect engine
- force mixing
- registry registration
- the installation plan

And “raw Brainfuck” means exactly that. The file contains only the language's eight commands. There is no macro language, no assembly-like layer, and no nicer source language secretly generating it behind the scenes.

### It does **not** interpret Brainfuck at runtime

The Brainfuck program is compiled ahead of time during the build.

`tools/BfAot` first validates it, then performs a collection of optimizations: folded runs, constant cell offsets, closed forms for clear and multiply-add loops, a few recognized loop shapes, constant propagation, and statically proven tape bounds.

It then emits C. Visual Studio compiles that generated C with `/O2` into the binaries that actually ship.

So no, the final executable is not sitting there interpreting `++++[>++++<-]>.` every time you turn the wheel.

For the full compiler and execution details, see `docs/AOT.md`.

### Brainfuck is the policy layer. C is the bridge.

Brainfuck cannot call Windows APIs.

That is not a missing feature. That is the architecture.

The native C side exposes a deliberately small set of generic mechanisms:

- HID I/O
- Registry access
- Service control
- Timers
- COM
- Shared memory

The Brainfuck program asks for those operations through a versioned byte-frame ABI defined in `src/brainfuck/ABI.md`.

This keeps the Windows-facing layer mostly ignorant of G29 policy, while the policy itself can be tested against recorded reference behavior without needing a wheel attached to the machine.

There is also a separate safety boundary in native code. Even if the Brainfuck side gets confused, crashes, or stalls, the bridge still refuses unknown G29 HID reports, limits force output per host, and stops force when the program stops behaving.

### Architecture rabbit hole

If you would like to know exactly how deep this goes:

- `docs/ARCHITECTURE.md` — overall architecture
- `src/brainfuck/ABI.md` — frame ABI
- `src/brainfuck/MEMORY_MAP.md` — Brainfuck tape layout
- `docs/AOT.md` — compiler and execution semantics
- `docs/PERFORMANCE.md` — performance notes
- `AI_MAINTENANCE.md` — maintaining the Brainfuck program without completely losing your mind

---

## Installation

### Requirements

You need:

- Windows 10 or Windows 11
- Administrator rights for the installation step
- A Logitech G29
- The wheel selector set to **PS3 mode**

If you are building from source, you also need:

- Visual Studio or Visual Studio Build Tools with the C++ workload
- The .NET Framework 4 C# compiler included with the Windows tooling

### Install

From PowerShell in the repository:

```powershell
.\Install-Driver.ps1
```

Optional configuration:

```powershell
.\Install-Driver.ps1 -Range 900 -AutoCenter 0
```

The installer uses the binaries in `artifacts\bin`. If those binaries are missing, it runs `build.ps1` for you.

One important detail: **installation and testing are separate.** If you changed the source, run `build.ps1` or `test.ps1` first so `artifacts\bin` contains the version you actually intend to install.

The installer asks for administrator rights when it reaches the installation step. If you already started it from an elevated PowerShell, it warns you and continues in that window instead.

Before the elevated step installs anything, it checks that the binaries still match the SHA-256 hashes verified by the first step. Installation is transactional as well: if something fails halfway through, the installer restores the machine to its previous state, including an earlier installation's files, service, and DirectInput registration.

At the end, it verifies that the `G29Standalone` service is actually running the installed `g29ctl.exe`.

### Testing the actual wheel

The normal automated suite does not require hardware. If you want to include the connected G29 itself, run:

```powershell
.\test.ps1 -Device
```

That performs the device test too, and **the wheel will turn briefly**.

If you run `test.ps1` normally from a console and a G29 is connected, the script can also offer the device test interactively.

So if the wheel suddenly moves during that test, that is not Brainfuck becoming sentient. That part is expected.

### DirectInput registration and G HUB

DirectInput force-feedback registration is part of the normal installation.

If another vendor's driver — for example Logitech G HUB — still owns the G29 registration, the installer refuses to stomp over it.

You can still install the service without force-feedback registration:

```powershell
.\Install-Driver.ps1 -SkipForceFeedback
```

The installer is deliberately conservative about services too. It only creates, stops, or replaces `G29Standalone` when an existing service with that name points to `g29ctl.exe` inside this project's install directory. If it points somewhere else, the installer leaves it alone.

DirectInput registration is written both machine-wide (`HKLM`) and for the account running the installer (`HKCU`). If the installer runs as `SYSTEM`, such as through a deployment tool, the per-user registration lands in SYSTEM's own registry hive.

Whether games can use force feedback from the machine-wide registration alone has not yet been verified. See `docs/TESTING.md`, item `HIL-15`.

---

## Tracing force feedback

If you want to see what the DirectInput driver is doing while a game is running, start this from a **non-elevated** PowerShell before launching the game:

```powershell
.\Trace-Driver.ps1
```

Press `Ctrl+C` when you are done.

The trace is written to `artifacts\logs`. It records calls and failures and, every five seconds, reports DirectInput call latency and HID write rate.

---

## Uninstall

```powershell
.\Uninstall-Driver.ps1
```

Uninstalling removes the service, installed files, and per-user DirectInput registration from every account on the machine, including accounts that are not currently signed in and the SYSTEM account.

It only removes registry keys whose own `Software\G29Standalone\DirectInput` marker says they were created by this project.

If one account's registry cannot be cleaned, uninstall continues with everything else and reports the failure. Fix the underlying problem and run the uninstall again to finish the cleanup.

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
```

There is also a deliberately bounded diagnostic force command:

```text
g29ctl force <-25..25> [--milliseconds 50..5000] --i-understand
```

The `--i-understand` part is not decorative.

Independently of whatever the Brainfuck program asks for, native code enforces a safety envelope:

- Unknown G29 HID reports are rejected.
- `g29ctl` is capped at **25% force**.
- Any `g29ctl` force held for more than **6 seconds** is stopped.
- Force is stopped if the Brainfuck program stops responding for **1 second** while force is active.
- The service itself is never allowed to apply force.

See `docs/ARCHITECTURE.md` and `docs/THREAT_MODEL.md` for the full design.

### Force feedback in games

Games talk to the project through 64-bit and 32-bit user-mode DirectInput effect drivers.

The G29 exposes one constant-force slot, so DirectInput effects are mixed together in software before being sent to the wheel.

No specific game is officially claimed to work yet. Real-game validation is still part of the pre-release testing work.

### PS4 mode

PS4 mode is detected, but it is **not supported**.

Use the wheel in **PS3 mode**.

---

## Development

```powershell
.\build.ps1                     # binaries in artifacts\bin
.\test.ps1                      # build + full test suite
.\test.ps1 -Sanitize -Analyze   # also AddressSanitizer and /analyze
.\test.ps1 -Reproducible        # clean rebuild must be byte-identical
artifacts\bin\G29.Tests.exe --benchmark
```

The normal test suite needs **no wheel, no G HUB, no administrator rights, and no network access**.

It covers the protocol, CLI, service, reconnect monitor, DirectInput, force feedback, watchdog behavior, registration, and install scripts.

Every scenario is run both on the reference interpreter and on the compiled program, and the results must match. The suite also fuzzes the compiled code against the reference interpreter, tests the native safety envelope — including the output guard, force lease, and frame parser — and checks the architecture itself:

- raw Brainfuck only
- no generating source layer
- no Brainfuck interpreter in the shipping binaries

GitHub Actions runs the suite on every push and pull request.

The parts that still need a physical wheel or a real game are tracked in `docs/TESTING.md`.

---

## Security and physical safety

If you find a security issue or anything that could create a physical-safety problem, read `SECURITY.md` before reporting it.

This is software that can command a motor attached to a steering wheel. Bugs are funny right up until the steering wheel develops an opinion.

---

## Contributing

Issues and pull requests are welcome.

Changes that affect HID reports, device identification, force feedback, or other hardware-facing behavior should include the technical basis for the change and matching tests.

Before submitting a change, run:

```powershell
.\test.ps1
```

If you are editing the Brainfuck, my condolences. Please also read `AI_MAINTENANCE.md`.

---

## License

GPL-2.0-or-later. See `LICENSE` and `THIRD_PARTY_NOTICES.md`.

Copyright © G29 Standalone contributors.

Logitech and G29 are trademarks of their respective owners. This project is not affiliated with or endorsed by Logitech.
