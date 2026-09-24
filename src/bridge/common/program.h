/*
 * The application program embedded in the running binary (resource
 * G29_PROGRAM, type RCDATA: src/brainfuck/g29-main.bf), compiled once per
 * process.
 */
#ifndef G29_PROGRAM_H
#define G29_PROGRAM_H

#include "bfvm.h"

/* module: the executable or DLL that carries the resource. NULL and a message
   when it is missing or does not compile. */
const bf_program *program_shared(void *module, char *error, size_t error_length);

#endif
