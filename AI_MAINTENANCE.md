# Maintaining the Brainfuck program

This file is for coding agents. Read it before changing anything under
`src/brainfuck/`.

## The rules

1. `src/brainfuck/g29-main.bf` is the only source of the G29 application
   policy. It is authoritative and it is edited directly.
2. It contains only the eight commands `><+-.,[]` and line endings. No
   comments, labels, names or other bytes. `ArchitectureTests` rejects any
   other byte, and `tools/BfAot` refuses to compile it.
3. Do not create a macro language, assembler, DSL, template, or a program in
   any other language that generates Brainfuck, and do not keep another
   editable representation from which the program is regenerated. This
   includes reviving the retired `.bfa` sources from git history as a source
   of truth. Tools may *read* the program (analysis, maps, tests); a tool used
   to perform an edit is a one-off aid whose output is reviewed and committed
   as plain Brainfuck, never a build step.
4. No build step writes `g29-main.bf`. `test.ps1` fails if the build or the
   tests change it.
5. Policy stays in Brainfuck. Do not move a decision into the C bridge
   because Brainfuck makes it inconvenient. The bridge performs generic
   mechanisms requested through the frame ABI; the emergency output guard and
   the force lease are the only independent logic outside the program.

## What you have instead of readable source

| Need | Where |
|---|---|
| Frame protocol between program and bridge | `src/brainfuck/ABI.md` |
| Tape regions and their meaning | `src/brainfuck/MEMORY_MAP.md` |
| Execution semantics (cells, tape, budget, faults) | `docs/AOT.md`, `src/bridge/common/bfrt.h` |
| Every HID report byte and its provenance | `docs/PROTOCOL.md` |
| Expected behavior | `tests/reference/*.txt` and the scenario tests |
| Constant text the program writes, with line:column | `BfAot strings src\brainfuck\g29-main.bf` |
| Validation only | `BfAot check src\brainfuck\g29-main.bf` |

(`artifacts\bin\BfAot.exe` is built by `native.ps1`.)

## How the program is shaped

These are observations about the committed program, useful for locating code.
They are not rules the compiler enforces.

- Constants are written into a zero cell `t` as runs of `+`/`-`, and larger
  ones as a product through the neighbouring cell:
  `>+{a}[-<+{b}>]<` (sometimes `>[-]+{a}[-<+{b}>]<`), which leaves `t+1` zero.
- A command frame with only constant bytes is written as one fragment on one
  cell: `+{0xA5}.[-]` (magic), then version, type, flags, sequence and length
  the same way, then the text as one addition per byte (the difference to the
  previous byte) followed by `.`, and finally `[-]`. The pointer is on `t`
  before and after, and `t` is zero before and after. `BfAot strings` finds
  these fragments.
- Text whose length is computed at run time (messages sent to the runtime
  sink) adds the constant length to a counter cell first, then writes the
  header with the counter, then the text run.
- Every cell is non-negative at rest; `[-]` on a negative cell would never
  end (it faults on the checked cell instead).

## Editing safely

- A replacement fragment must leave the pointer where the original left it and
  every cell it touched in the state the original left it in (a scratch
  neighbour that the original cleared must be cleared too).
- Changing a message changes the recorded transcripts: update
  `tests/reference/*.txt` and the test literals in the same change, and say
  why in `CHANGELOG.md`. Never change HID report bytes without evidence
  (`docs/PROTOCOL.md`).
- After any edit: `.\test.ps1`. It checks the source alphabet, compiles the
  program ahead of time, runs every scenario on the reference interpreter and
  on the compiled program, and fails on any difference.
- The compiler must still prove no fault in the program (`static_faults=0`
  in `artifacts\obj\generated\g29_program.stats.txt`), and the longest
  stretch between two reads must stay under 1/20 of the iteration budget
  (`Program.CompiledPass`).
