/* Embedded program: see program.h. */
#include "program.h"

#include <windows.h>
#include <stdio.h>

static INIT_ONCE once = INIT_ONCE_STATIC_INIT;
static bf_program *shared_program;
static char shared_error[256];

static BOOL CALLBACK load(PINIT_ONCE init, PVOID parameter, PVOID *context)
{
    HMODULE module = (HMODULE)parameter;
    HRSRC resource = FindResourceW(module, L"G29_PROGRAM", MAKEINTRESOURCEW(10));
    HGLOBAL loaded;
    const char *text;
    DWORD size;
    (void)init;
    (void)context;
    if (!resource || !(loaded = LoadResource(module, resource)) || !(text = (const char *)LockResource(loaded))) {
        snprintf(shared_error, sizeof(shared_error), "The Brainfuck program is not embedded in this binary.");
        return TRUE;
    }

    size = SizeofResource(module, resource);
    shared_program = bf_compile(text, size, 1, shared_error, sizeof(shared_error));
    return TRUE;
}

const bf_program *program_shared(void *module, char *error, size_t error_length)
{
    InitOnceExecuteOnce(&once, load, module, NULL);
    if (!shared_program && error && error_length) {
        snprintf(error, error_length, "%s", shared_error);
    }

    return shared_program;
}
