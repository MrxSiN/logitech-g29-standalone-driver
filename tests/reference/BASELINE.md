# Migration baseline (Phase 0)

Recorded 2026-09-24 before any Brainfuck migration change. The legacy C#
implementation at git commit `c1813a0` passed `test.ps1`.

The fixtures in this directory were produced once by `reference.ps1`
(`tools/G29.Reference`) from that legacy implementation. They are the oracle
for every later phase and are not edited by hand. The test suite replays every
one of them against the Brainfuck program (see `MIGRATION.md`). The generator
and the legacy implementation were deleted in migration phase 14; check out the
phase 0 commit to run them again.

## Hashes (SHA-256, for reference only)

The .NET Framework compiler does not produce byte-identical builds, so the
binary hashes only identify the exact baseline build that was measured.

```text
9e8fe565e36548840465ca3b0c2f207a921bdecb17e1d185b33295151673d397  src/G29.Core/Protocol/G29Protocol.bf
27b3e23d20ccd0e95c9d4ace0b8eece2659355c22b308a9fa6760ee542555aea  artifacts/bin/g29ctl.exe
4d1f3374dc275e056114f852e779d1aeeb992bb35da6ddd4af353cb4d0327916  artifacts/bin/g29ffb.dll
dc0aa549d75ba71e96742246a46d002431c293727b80a15edfdb5dfd93e26009  tests/reference/cli.txt
33ccfd537a717472bee67fc20928c7405a9178c6dbb2e9a3d53a15fc4aa7c804  tests/reference/controller.txt
15d4d113e0257e2bc9b9e5e865647c3370b759223a1f775cf7528783b0ced8f2  tests/reference/ffb-engine.txt
1f7bc447a782bc872b734ab8401366a0a3169b0283e6a248b3293babcc862637  tests/reference/ffb-reader.txt
eb8eec137ba81b301b61f07ca2b681ceafec9274adc15bf385265b816ae44fb0  tests/reference/monitor.txt
120f1919cc239799bf4a366ea8e9fd01af6bee682499a6065052454f5193149a  tests/reference/protocol.txt
38c17eed8899c2d08106bbdef627aebc729db701d008f34bd903a5b85a0ea5b6  tests/reference/registration.txt
710934834940e40211b22e0e49adeca551a88bbeb17c3ae206d7870cd2a312d3  tests/reference/selection.txt
eac525dd459fa017cfcc9e32e223c3c29928ef37fa00d96d35006594dc3035c5  tests/reference/steering.txt
f16ff92e3bb1ae337cc0790d35a19fdf391878856b059fe3a989e410501f7f64  tests/reference/watchdog.txt
```

## Fixture coverage

| File | Legacy source | Content |
|---|---|---|
| `protocol.txt` | `G29Protocol` (BF) | identification grid (24 PIDs x 135 revisions), every range/autocenter/LED/force report, native switch, stop |
| `selection.txt` | `HidWheelDiscovery` filter | VID x PID x revision x usage page x usage keep/reject grid |
| `ffb-engine.txt` | `EffectEngine` | all legacy `ForceFeedbackTests` plus sweeps of every effect kind, envelope, timing, playback and gain rule |
| `ffb-reader.txt` | `DirectInputEffectReader` | direction sign boundaries, structure sizes, changed-flag selection, clamping, type-specific sizes (64-bit process) |
| `steering.txt` | `HidSteeringReader` arithmetic | axis range fallback and motion traces (arithmetic copied verbatim with an injected clock) |
| `watchdog.txt` | `ForceWatchdog` | abandonment grid |
| `controller.txt` | `WheelController` | init/range/autocenter/LEDs/force/stop transcripts incl. re-enumeration, timeout, PS4, none, multiple, I/O failures |
| `monitor.txt` | `WheelMonitor` | reconnect, path change, PS4, multiple, failure de-duplication, idle transcripts |
| `cli.txt` | `G29.Cli.Program.Run` | argv parsing, help, bounds, force safeguards and messages against five wheel setups |
| `registration.txt` | `DirectInputDriverRegistrar`, installer | registry paths, blobs, effect attributes, service strings |

Not captured automatically (needs the real machine; covered later by bridge
tests and physical validation): `doctor` output, `watch`/`service` loops,
real registry/SCM effects, real HID timing.
