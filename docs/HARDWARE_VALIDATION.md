# Hardware validation log

What has been run on a physical G29 and what is still open. The procedures
and pass criteria are in `docs/TESTING.md` (HIL-1..HIL-16). These records
predate the ahead-of-time compiler (2026-09-26); the compiled program behaves
identically to the interpreted one in every automated scenario, but none of
the steps below has been repeated with the compiled build yet.
## Record

A G29 (native mode, PID C24F, revision 8900) is connected to the development
machine and the pre-migration build's service and .NET driver were installed. During the Brainfuck migration
only read-only commands and the refused `force 30`/`range 39` argument checks
were run against it; no configuration or force report has been sent. Still to
do, with supervision:

1. `g29ctl init`, `range`, `autocenter`, `leds`, `stop` and `force` (hands
   clear) through the CLI.
2. Install this build; the service starts on plug-in, configures once,
   reconfigures after replugging, logs to the event log, stops 15 s after
   unplugging; `g29ctl watch` likewise.
3. `Install-Driver.ps1` / `Uninstall-Driver.ps1` with this build: the service
   is created with the planned name, trigger and recovery (`sc.exe qc`,
   `qtriggerinfo`, `qfailure`), registry comparison before/after, and a clean
   uninstall.
   Done on 2026-09-24 with the hardened scripts (`tools/InstallCommon.ps1`):
   the service matches the plan (`qc`, `qtriggerinfo`, `qfailure`); uninstall
   left no service, install directory, `Software\G29Standalone` marker or
   class key; an out-of-range `-Range` changed nothing; a foreign service
   named `G29Standalone` was refused by both scripts and left untouched; a
   foreign `OEMForceFeedback` CLSID made `ffb-register` fail and the installer
   removed the partial installation while keeping the foreign key;
   `-SkipForceFeedback` installed without registering; reinstalling over
   itself and removing a stale file both worked. The HKCU part was checked
   only from inside the Claude desktop app, whose HKCU may be virtualized.
   That run predates the transactional installer (fixed identity, hashed
   artifacts, rollback to the previous installation; see `CHANGELOG.md`).
   Repeated with the current scripts the same day (`docs/TESTING.md` HIL-15,
   from an already elevated shell, so `-SkipBuild -SkipTests` with the
   binaries the preceding green `test.ps1` built): upgrade over the previous
   build installed the built hashes, service, trigger, recovery and
   registration as planned; a `-Range 540` install with `g29ffb32.dll` held
   open failed while setting files aside, after the service had been stopped
   and deleted, and rolled back to an identical `qc`/`qtriggerinfo`/`qfailure`,
   a running service, the same files and the same registration; uninstall
   removed the service, directory, both class keys, the marker key, the event
   source and the `OEMForceFeedback`/`Axes` subkeys (Windows' own `OEMName`
   and `OEMData` stay); a fresh reinstall worked, and each service start
   logged "Configured G29 Driving Force Racing Wheel (native)".
   Two-phase path: UAC is disabled on the development machine (`EnableLUA`
   0), so no UAC prompt can appear and every process of the account is
   elevated. An elevated shell without `-SkipBuild -SkipTests` was refused.
   The unprivileged phase ran under `runas /trustlevel:0x20000` (Administrators
   deny-only): it built, passed `test.ps1` and the 126 install checks; its
   `RunAs` relaunch could not gain administrator rights (UAC off) and changed
   nothing. The elevated phase then ran with that phase's `-ArtifactHashes`
   and installed; with a wrong `g29ctl.exe` hash it refused and rolled back to
   an identical service, files and registration.
   SYSTEM context (scheduled task as SYSTEM): install, uninstall, install all
   succeeded with the expected HKLM state. Finding: the SYSTEM uninstall left
   the interactive user's HKCU `OEMForceFeedback` and `Software\G29Standalone`
   in place (it only reaches the hive of the account running it), and a SYSTEM
   install registers in SYSTEM's HKCU, not the users'. Whether games get force
   feedback for a user from the HKLM registration alone is unverified.
   Fixed in software on 2026-09-25, not yet run on the machine: after
   `ffb-unregister`, `Remove-G29Installation` (`tools/InstallCommon.ps1`)
   removes the per-user registration from every user hive (loaded ones,
   SYSTEM's, and signed-out profiles' `NTUSER.DAT` loaded temporarily). It
   acts only where that hive's own `Software\G29Standalone\DirectInput` marker
   exists, under the same conditions as `g29ctl ffb-unregister`. It removes the proof last, so a failed uninstall
   can be run again. The registration policy in `g29-main.bf` is unchanged. Registering only in HKLM was rejected: on
   2026-09-24 ACC read a stale G HUB CLSID from the real HKCU while HKLM held
   ours. So per-user `OEMForceFeedback` wins when present, and the installing
   account's HKCU registration is what overrides a stale one. Still to
   verify on hardware: the cross-account uninstall (both directions,
   including a signed-out account), and DirectInput's behavior for an account
   without a per-user entry (`docs/TESTING.md`, HIL-15).
   Still open in HIL-15: a machine with UAC enabled (the prompt itself),
   reboot, a clean Windows 10 machine.
4. DirectInput test clients (64-bit and 32-bit) with the native
   `g29ffb64.dll` / `g29ffb32.dll`:
   forces, spring, sine, stop/reset, release, and killing the
   client while force is applied so the service watchdog stops the
   wheel.

## Deliberate deviations from the legacy oracle (found on hardware)

- DirectInput force direction: the legacy mapping (positive DirectInput force =
  negative protocol percent) was backwards. Measured: protocol -25 turns the
  G29 right; Assetto Corsa Competizione requests positive force while the
  wheel is right of centre and was driven to full lock. The driver now sends
  percent = round(force / 100) and feeds condition effects mirrored steering
  motion, so springs and dampers still resist the wheel
  (`src/brainfuck/g29-main.bf`, `tests/DirectInputTests.cs`).
- Registry delete-tree requests open keys with only the rights
  `RegDeleteTree` needs: G HUB-era OEM keys deny WRITE_DAC, so the old
  full-access open failed with error 5 (`src/bridge/common/system.c`).
