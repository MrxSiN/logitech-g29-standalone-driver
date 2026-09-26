# Ahead-of-time compilation of the Brainfuck program

```text
src/brainfuck/g29-main.bf
  -> tools/BfAot (validate, fold, classify loops, prove, emit C)
  -> artifacts/obj/generated/g29_program.h, g29_program_00.c .. _15.c
  -> MSVC /O2 per architecture (x64, x86), cached per part
  -> linked into g29ctl.exe, g29ffb64.dll, g29ffb32.dll
```

The shipping binaries contain the compiled program only. There is no
Brainfuck text, parser, opcode dispatch loop, bracket table or JIT in them,
and nothing proportional to the program length is allocated at run time; the
per-instance state is the 512 KiB tape and a small context
(`src/bridge/common/bfrt.h`). `ArchitectureTests` checks the binaries for
program text and for the retired interpreter's strings.

The compiler (`tools/BfAot`, C#, built by `native.ps1` with the .NET Framework
compiler) has three files: `Compiler.cs` (validation, loop tree, analysis, C
emission), `TextMap.cs` (read-only map of the program's constant text) and
`Program.cs` (command line).

## Execution semantics

These are the semantics of the interpreter the project shipped until the AOT
migration (`bfvm.c`, removed), and of the test-only reference interpreter
`tests/harness/Runtime/BfVm.cs`. The compiled code reproduces them; the
differential tests compare the two.

| Aspect | Rule |
|---|---|
| Commands | `+ - < > [ ] , .`; in the source nothing else is allowed except CR and LF |
| Cells | signed 32-bit, checked: an operation whose result leaves int32 faults ("A Brainfuck cell overflowed."); cells never wrap |
| Tape | 131072 cells, zero at start; pointer starts at 0; a move outside faults ("moved outside its tape") |
| Input `,` | blocks for the next byte (0..255); a read that reports shutdown (-1) ends the program with `BF_STOPPED`; a value above 255 faults |
| Output `.` | the cell must be 0..255, otherwise it faults; the host may refuse the byte, which faults |
| End of program | `BF_FINISHED`; the hosts treat an unrequested end as a failure |
| Liveness | at most `BF_DEFAULT_ITERATION_BUDGET` (100 000) loop iterations between two reads; more faults ("ran more than N loop iterations without reading input") |
| Faults | the program stops; the host trips the emergency guard (stop report to every interface holding force) and ends the session |
| Host callbacks | reads and writes are synchronous calls on the program's thread (`bf_read_fn`, `bf_write_fn`); the session delivers framed events and parses command frames (`ABI.md`) |

Operation folding is part of the semantics: a run of `+`/`-` (or `<`/`>`) is
one operation, so the checks apply to the run's total, and a run that cancels
to zero does nothing. Loops matching a closed form behave exactly like plain
execution when their preconditions hold (below).

### Changed in the migration: the budget unit

The interpreter counted every executed operation against a budget of
5 000 000 between two reads. Compiled code has no operations to count, so
the budget now counts **loop iterations**: each entry into a plain loop body
(from the loop test or the back edge) counts one; clear, multiply-add and
idiom loops completed in closed form count none. Straight-line code between
two loop iterations is bounded by the program size, so this bounds execution
just the same. The reference interpreter counts identically, and the
differential tests require equal counts. The longest legitimate stretch over
every recorded scenario is 2 493 iterations; the budget of 100 000 is 40
times that (the suite fails below 20 times).

## Optimizations

| Optimization | Effect on the program |
|---|---|
| Run folding (`+++` -> one add, `>>>` -> one move, cancelling runs removed) | 19.2 M commands -> 893 k operations |
| Pointer-offset folding: moves become constant offsets on cell accesses; `p` changes only at loop boundaries and calls | no per-move pointer arithmetic |
| Static pointer: while the absolute position is known (from the start and through balanced loops) cells are addressed as constants and no tape check is emitted | proven in range |
| Relative tape checks only at a new extreme of the pointer within a stretch; the whole tape is proven valid when leaving the static-pointer region | 17 132 checks |
| Clear loops `[-]`, `[+]` | one compare and store |
| Multiply-add loops (only adds and moves, pointer back, counter +/-1): `[->+<]`, `[->++<]`, ... | one 64-bit multiply-add per target |
| Idioms `race`, `divmod`, `multiply` (fixed loop shapes with nested loops) | closed-form call, plain fallback when a precondition fails |
| Constant propagation within straight-line code: after a clear, a constant store or a loop exit a cell's value is known; additions on it become constant stores without an overflow check; a multiply-add with a known counter becomes one addition per target | 101 k constant stores |
| Dead-loop removal: a loop whose cell is known to be zero is not emitted | 5 113 loops |
| Function splitting (200 operations per function) and 16 source parts | a clean `/O2` build of all three binaries takes about a minute |

The statistics of a build are in
`artifacts/obj/generated/g29_program.stats.txt`.

### Closed forms

Loop classification is identical to the reference interpreter (and the
retired one): idioms first, then clear, then multiply-add, outermost loops
first. The idiom patterns (`tools/BfAot/Compiler.cs`, `LoopIdiom`) are matched
at any cell offsets; their closed forms are in `src/bridge/common/bfrt.c`.
Each returns without touching the tape when a cell is outside the tape, a
precondition fails, or a result would leave int32, and the loop then runs
plainly, so the result is always what plain execution produces.
`RuntimeTests.ClosedForms` proves every closed form against plain execution in
the reference interpreter, and `SafetyTests.CompiledClosedForms` proves the
compiled closed forms against plain execution on the same prepared tapes.

## Safety work moved to build time

- The source alphabet and bracket balance are checked by `BfAot` before any
  code is generated; an unbalanced program never builds.
- Tape bounds are proven for every access in the static-pointer region; the
  compiler reports any proven fault as `static_faults` (the program has none,
  and `AotCompilerTests` requires that).
- Overflow checks are omitted only where the value is known at compile time.

What stays at run time: tape checks where the pointer depends on data,
overflow checks on unknown values, the output byte check, the iteration
budget, and everything in the native safety envelope (`guard.c`, `lease.c`).

## Generated code shape

```c
int g29_program_run_123(bf_context *c, int32_t *t, int *pp)
{
 int p = *pp;
 ...
 while (t[p + 5]) {
  BF_ITERATION();              /* budget */
  BF_INC(t[p + 3], 4);         /* checked add */
  if (t[p + 7] != 0) { int64_t n = t[p + 7], v; if (n < 0) goto ovf; BF_MULADD(t[p + 8], 2); t[p + 7] = 0; }
  if ((unsigned)(p - 1) >= BF_TAPE_LENGTH) goto tape;
  ...
 }
 *pp = p;
 return 0;
ovf:
 return bf_fault_overflow(c);
tape:
 return bf_fault_tape(c);
}
```

A non-zero return (stop or fault) unwinds straight to `g29_program_run`.

## Build behavior

- `BfAot` rewrites a generated file only when its content changes, and
  `native.ps1` recompiles only parts whose source or `bfrt.h` changed.
- The generated header names the program's SHA-256; `ArchitectureTests`
  checks it against the committed `g29-main.bf`.
- `/Brepro` and deterministic generation make clean builds byte-identical
  (`test.ps1 -Reproducible`, CI).
- The generated code is compiled with the same flags as the bridge (`/W4 /WX
  /O2 /GS /guard:cf`). It is excluded from `/analyze`; its correctness is
  covered by construction and by the differential tests.
