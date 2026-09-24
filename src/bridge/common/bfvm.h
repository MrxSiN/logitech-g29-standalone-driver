/*
 * BF32-G29 virtual machine (native bridge).
 *
 * Classic Brainfuck: only + - < > [ ] , . are commands; checked, non-wrapping
 * signed 32-bit cells; a bounded 131072-cell tape; byte input and output. The
 * program runs once and persists: ',' blocks for the next input byte. The step
 * budget applies between two input reads.
 *
 * This is a line-for-line port of the transitional C# VM
 * (src/bridge-transitional/Runtime/BfVm.cs, BfIdioms.cs, BfIdiomLibrary.cs):
 * the same compilation, the same superinstructions with the same
 * preconditions, the same faults. tests/ runs the program on both and compares.
 */
#ifndef G29_BFVM_H
#define G29_BFVM_H

#include <stddef.h>
#include <stdint.h>

#define BF_TAPE_LENGTH 131072
#define BF_DEFAULT_STEP_BUDGET 50000000LL

typedef struct bf_program bf_program;
typedef struct bf_vm bf_vm;

/* Returns the next input byte (0..255), blocking as needed, or -1 when the host
   is shutting the program down. */
typedef int (*bf_read_fn)(void *context);

/* Receives one output byte. Returns 0, or non-zero when the host refuses the
   output (the VM then stops with a fault). */
typedef int (*bf_write_fn)(void *context, unsigned char value);

/* Compiles source text. Returns NULL and a message on an unmatched bracket or
   when out of memory. */
bf_program *bf_compile(const char *source, size_t length, int optimize, char *error, size_t error_length);
void bf_program_free(bf_program *program);
size_t bf_program_length(const bf_program *program);
size_t bf_program_idiom_count(const bf_program *program);
size_t bf_program_muladd_count(const bf_program *program);

bf_vm *bf_vm_create(const bf_program *program, bf_read_fn read, bf_write_fn write, void *context, int64_t step_budget);
void bf_vm_free(bf_vm *vm);

enum bf_result {
    BF_FINISHED = 1,   /* the program ended */
    BF_STOPPED = 0,    /* read returned -1 */
    BF_FAULT = -1      /* program fault or refused output; message filled in */
};

int bf_vm_run(bf_vm *vm, char *fault, size_t fault_length);
int64_t bf_vm_total_steps(const bf_vm *vm);
int bf_vm_pointer(const bf_vm *vm);
int32_t bf_vm_cell(const bf_vm *vm, int index);
void bf_vm_set_cell(bf_vm *vm, int index, int32_t value);

#endif
