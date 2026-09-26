/*
 * Support for Brainfuck compiled ahead of time by tools/BfAot.
 *
 * The build translates src/brainfuck/g29-main.bf into C functions (see
 * docs/AOT.md); there is no interpreter, parser or dispatch loop at run time.
 * This header is the contract between that generated code and the bridge.
 *
 * Execution semantics (identical to the test-only reference interpreter,
 * tests/harness/Runtime/BfVm.cs, and to the interpreter the project shipped
 * before ahead-of-time compilation):
 *  - cells are signed 32-bit and checked: an operation whose result would
 *    leave the int32 range faults instead of wrapping;
 *  - the tape has BF_TAPE_LENGTH cells, starts zeroed, and the pointer starts
 *    at cell 0; moving outside the tape faults;
 *  - ',' blocks for the next input byte (0..255); a read that reports
 *    shutdown (-1) unwinds the program with BF_STOPPED;
 *  - '.' writes the current cell, which must be 0..255; the host may refuse
 *    the byte, which faults;
 *  - the program may run at most `budget` loop iterations between two reads.
 *    Clear, multiply-add and idiom loops that run in closed form count as no
 *    iteration; every other executed loop body counts as one.
 */
#ifndef G29_BFRT_H
#define G29_BFRT_H

#include <stddef.h>
#include <stdint.h>

#define BF_TAPE_LENGTH 131072

/* Loop iterations allowed between two input reads. The test suite measures
   the longest legitimate stretch over every recorded scenario and fails
   unless this stays at least 20 times above it (docs/TESTING.md). A runaway
   program faults within milliseconds, independent of the force lease. */
#define BF_DEFAULT_ITERATION_BUDGET 100000LL

/* Returns the next input byte (0..255), blocking as needed, or -1 when the host
   is shutting the program down. */
typedef int (*bf_read_fn)(void *io);

/* Receives one output byte. Returns 0, or non-zero when the host refuses it. */
typedef int (*bf_write_fn)(void *io, unsigned char value);

typedef struct bf_context {
    int32_t *tape;
    /* iterations left before the budget fault; reset by every read */
    int64_t fuel;
    int64_t budget;
    /* most iterations used between two reads, and all iterations so far */
    int64_t peak;
    int64_t total;
    bf_read_fn read;
    bf_write_fn write;
    void *io;
    char fault[160];
} bf_context;

enum bf_result {
    BF_FINISHED = 1,   /* the program ended */
    BF_STOPPED = 0,    /* read returned -1 */
    BF_FAULT = -1      /* program fault or refused output; context->fault says why */
};

typedef int (*bf_program_fn)(bf_context *context);

typedef struct {
    const char *name;
    bf_program_fn run;
} bf_named_program;

/* Allocates the tape. Returns 0 when out of memory. */
int bf_context_init(bf_context *context, bf_read_fn read, bf_write_fn write, void *io, int64_t budget);
void bf_context_free(bf_context *context);
int64_t bf_context_iterations(const bf_context *context);

/* The application program, compiled from src/brainfuck/g29-main.bf. */
int g29_program_run(bf_context *context);

/* ------------------------------------------------------------------------- */
/* Used by generated code only.                                               */
/* ------------------------------------------------------------------------- */

/* Internal status of a generated function: 0 continues. */
enum {
    BFX_STOP = 1,
    BFX_FAULT = 2
};

int bf_in(bf_context *c, int32_t *cell);
int bf_out(bf_context *c, int32_t value);
int bf_fault_overflow(bf_context *c);
int bf_fault_tape(bf_context *c);
int bf_fault_budget(bf_context *c);

/* Written as if/else so each use is one statement without a constant loop
   condition (warning C4127). Generated functions define the label ovf. */
#define BF_ITERATION() if (--c->fuel < 0) return bf_fault_budget(c); else (void)0
#define BF_INC(cell, k) if ((cell) > INT32_MAX - (k)) goto ovf; else (cell) += (k)
#define BF_DEC(cell, k) if ((cell) < INT32_MIN + (k)) goto ovf; else (cell) -= (k)
/* cell += n * factor in 64 bits (n <= 2^31, factor is an int32): v and n are
   declared by the generated multiply-add block. */
#define BF_MULADD(cell, factor) v = (int64_t)(cell) + n * (factor); if (v < INT32_MIN || v > INT32_MAX) goto ovf; else (cell) = (int32_t)v

/*
 * Closed forms of three loop shapes (tools/BfAot LoopIdiom). Each returns 1
 * after producing exactly the tape the plain loop would, or 0 without touching
 * the tape when a cell is outside the tape, a precondition fails or a result
 * would leave int32; the caller then runs the loop plainly. The cell indices
 * are distinct.
 */
int bf_idiom_race(int32_t *t, int a, int b, int f, int g, int m, int s);
int bf_idiom_divmod(int32_t *t, int n, int r, int c, int u, int e, int q);
int bf_idiom_multiply(int32_t *t, int a, int b, int p, int u);

#endif
