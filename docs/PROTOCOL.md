# G29 protocol and provenance

Every HID output report the project can send is listed here with where it
comes from. The Brainfuck program `src/brainfuck/g29-main.bf` builds every
report (role TEST entry points `I N S R A L F` expose each one); the bridge's output guard (`src/bridge/common/guard.c`)
admits exactly these families and nothing else. `tests/reference/protocol.txt`
freezes every report for every legal input (it was generated from the legacy
C# implementation before the Brainfuck port and is never edited by hand), and
`tests/ProtocolTests.cs` / `tests/ReferenceReplay.cs` / `tests/SafetyTests.cs`
check the program and the guard against it.

All output reports are 7 bytes. When the collection's output report is longer
(the G29 native interface reports 17 bytes including the report ID), the bridge
writes a zero report ID and pads with zeros (`hid.c`, `write_report`).

## Sources

- **[LG4FF]** Linux kernel `drivers/hid/hid-lg4ff.c` (GPL-2.0-or-later; see
  `THIRD_PARTY_NOTICES.md`). The project's commands were ported from it into
  the legacy C# `G29Protocol.cs`, which was frozen into `protocol.txt`.
- **[HW]** Observation on a physical G29 (PS3 mode, revision 0x8900) during the
  migration, recorded in `AGENT.md`.
- **[LEGACY]** The legacy C# implementation's behavior, frozen in phase 0. No
  independent source is recorded in the repository.

## Output reports

| Report | Bytes | Bounds | Source |
|---|---|---|---|
| native mode (1/2) | `F8 0A 00 00 00 00 00` | fixed | [LG4FF] G29 mode switch (revert/detach command) |
| native mode (2/2) | `F8 09 05 01 01 00 00` | fixed | [LG4FF] `ext09` switch to G29 mode, detach 1 |
| range | `F8 81 lo hi 00 00 00` | degrees 40..900, little-endian | [LG4FF] `lg4ff_set_range_g25`; G29 range 40..900 |
| autocenter off | `F5 00 00 00 00 00 00` | fixed | [LG4FF] autocenter deactivate |
| autocenter strength | `FE 0D a a b 00 00` | `a` 0..7, `b` 1..255 (from percent 1..100) | [LG4FF] `lg4ff_set_autocenter_default` expansion |
| autocenter on | `14 00 00 00 00 00 00` | fixed, after the strength | [LG4FF] autocenter activate |
| LEDs | `F8 12 m 00 00 00 00` | mask 0..31 | [LG4FF] `lg4ff_set_leds` |
| constant force | `11 08 v 80 00 00 00` | `v = 128 ± (127·p + 50) / 100`, p 1..100 | [LG4FF] `lg4ff_play` (slot 1, constant) |
| stop | `13 00 00 00 00 00 00` | fixed | [LG4FF] `lg4ff_play` with zero force (slot 1 stop) |

Autocenter: percent p maps to magnitude `m = round(p · 65535 / 100)`; below
`0xAAAA`: `a = 3m / 21845`, `b = 64m / 21845`; above: `r = m - 0xAAAA`,
`a = 6 + 3r / 0xAAAA`, `b = 128 + 3r / 514` ([LG4FF] expansion, integer
arithmetic, as the program performs it).

Direction: a positive protocol percent turns the G29 to the left. [HW] (a
negative diagnostic force turned the wheel right). DirectInput positive force
(force coming from +X) maps to a positive protocol percent; the legacy mapping
had the opposite sign and drove Assetto Corsa Competizione to full lock ([HW],
`AGENT.md`).

## Identification

| Rule | Source |
|---|---|
| vendor 0x046D | [LG4FF] / USB-IF assignment |
| native G29 product 0xC24F | [LG4FF] `USB_DEVICE_ID_LOGITECH_G29_WHEEL`; [HW] |
| compatibility products 0xC294, 0xC298, 0xC299, 0xC29A, 0xC29B need a known G29 revision | [LG4FF] multimode identification (products the G29 can present in other modes) |
| known G29 revisions: `(rev & 0xFF00) == 0x8900` or `(rev & 0xFFF8) == 0x1350` | [LG4FF] G29 identification masks |
| 0xC260 with a known revision is the PS4-mode G29 (reported, never configured) | [LEGACY]; **HIL: confirm with the selector in PS4 mode** |
| HID usage page 0x01, usage 0x04 or 0x05 | [LEGACY] (joystick / game pad collection) |

## Golden-vector coverage

| Behavior | Automated | Physical wheel |
|---|---|---|
| identification (every PID × revision class) | `protocol.txt` `identify` lines | native C24F rev 8900 [HW]; PS4 mode not observed |
| mode switch | `protocol.txt` `native` | [HW] during migration; re-enumeration timing HIL |
| range 40..900 | all 861 values | **HIL**: sweep 40/90/180/270/540/900 |
| autocenter 0..100 | all 101 values | **HIL**: off inactive, strength monotonic |
| LEDs 0..31 | all 32 masks | **HIL**: every mask |
| constant force ±100 | all 201 values | [HW] sign; **HIL**: monotonic strength |
| stop | fixed | [HW] |
| reconnect initialization | `MonitorParityTests` | **HIL**: unplug/replug matrix |

Nothing here was changed in this revision; the guard's allowlist was derived
from these families and is checked against every vector in `protocol.txt`.
