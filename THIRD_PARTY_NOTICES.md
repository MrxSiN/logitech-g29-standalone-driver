# Third-party notices

Logitech wheel commands and device-identification rules in the Brainfuck
program `src/brainfuck/g29-main.bf` (ported from the former C#
`G29Protocol.cs`, see `docs/PROTOCOL.md`) derive from the Linux kernel
`drivers/hid/hid-lg4ff.c` implementation.

- Project: Linux kernel
- Source: https://github.com/torvalds/linux/blob/master/drivers/hid/hid-lg4ff.c
- License: GPL-2.0-or-later
- Copyright noted by that source: Simon Wood and other Linux contributors

No Logitech G HUB executable, DLL, encrypted device database, or driver binary
is included in this repository.
