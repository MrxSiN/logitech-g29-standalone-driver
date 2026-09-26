# Security policy

## Supported versions

G29 Standalone is pre-release. Only the latest commit on `main` is supported.

## Reporting a vulnerability

Report vulnerabilities privately through GitHub's **Report a vulnerability**
button on this repository's Security tab. Please do not open a public issue for
a security problem.

Include the commit you tested, the Windows version, and steps to reproduce.

## What counts

The installed pieces run with elevated or foreign-process privileges, so these
are in scope:

- the `G29Standalone` Windows service (runs as LocalSystem) and `g29ctl.exe`;
- the DirectInput effect drivers `g29ffb64.dll` / `g29ffb32.dll`, which load
  inside game processes;
- `Install-Driver.ps1` / `Uninstall-Driver.ps1`, which run as administrator;
- any way to make the wheel apply force beyond the documented limits
  (`g29ctl force` is limited to -25..25 percent for 50..5000 ms; the native
  output guard in `src/bridge/common/guard.c` admits only known G29 reports
  and refuses force above the host's ceiling; the native force lease in
  `src/bridge/common/lease.c` ends a force when the program stalls or a CLI
  force exceeds 6 s), or to keep force applied after the program or game that
  requested it has stopped;
- any way for a Brainfuck program fault, a malformed ABI frame or a DirectInput
  caller to reach Windows capabilities beyond those documented in
  `src/brainfuck/ABI.md`.

The threat model, including what is out of scope, is `docs/THREAT_MODEL.md`.

A physical-safety problem (the wheel moving unexpectedly or holding force) is
treated as a vulnerability even when no privilege boundary is crossed.
