# BfAsm — Brainfuck assembly

`bfasm` turns `.bfa` files into classic Brainfuck. It is generic: it contains no
knowledge of the G29 or of any algorithm. All behavior is written as Brainfuck
commands in the `.bfa` sources; the assembler adds names, macros and checks.

```powershell
.\bf.ps1        # builds bfasm.exe and assembles src\brainfuck\g29-main.bfa
```

## What it does

- `@cell` moves the data pointer to a named or computed cell by emitting the
  right number of `<` or `>`. The assembler tracks the pointer statically.
- `+ - < > [ ] , .` are emitted as written. `+*n` repeats a command `n` times.
- `[ ... ]` must leave the pointer where it started; otherwise assembly fails.
  `[! ... ]` marks a deliberately unbalanced loop (for example a scan); after it
  the pointer is unknown until `assume @cell`.
- Macros expand their Brainfuck bodies with arguments substituted. Arguments
  are numbers (cell addresses or constants), strings, or `{ code }` blocks,
  which a macro runs by writing `name()`.
- `local a, b[4] { ... }` hands out scratch cells from the nearest `scratch`
  pool for the duration of the block.
- `for`, `if`, `let` and `assert` run at assembly time only (repetition and
  compile-time choices); they never emit control flow of their own.
- `section "text"` writes a comment line into the output.

## Syntax

```text
; comment to end of line
include "file.bfa"
const NAME = expression                  ; one line
cell NAME = address                      ; or: cell NAME[length] = address
scratch FROM to END                      ; scratch pool [FROM, END)
macro name(p1, p2) { code }

code:
  + - < > , .   +*count   @cell   [ code ]   [! code ]   { code }
  name(args) {block} ...                   ; macro call; trailing blocks are extra arguments
  for i = a to b { }   for c, i in "text" { }   if expr { } else { }
  local t, buf[8] { }   let x = expr { }   assume @cell   section "text"   assert expr, "message"

expression: numbers (decimal, 0x hex, 'c'), strings, names, ( ),
  unary - !, * / % (floor), + -, < <= > >=, == !=, &&, ||, c ? a : b,
  len(s) byte(s, i) min(a, b) max(a, b) abs(a) isqrt(a) str(a) isblock(x) isstring(x)
```

Array elements are addressed as `@(buffer + index)`; there is no postfix
indexing, so `@x[-]` always means "go to x, then clear it".
