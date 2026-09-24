/*
 * Test-only DLL: exposes the native VM to the C# test suite, which runs the
 * application program on both VMs and compares them. Not shipped.
 */
#include <stdlib.h>

#include "../common/bfvm.h"

typedef int (__stdcall *test_read_fn)(void);
typedef int (__stdcall *test_write_fn)(int value);

typedef struct {
    test_read_fn read;
    test_write_fn write;
    bf_vm *vm;
} test_vm;

static int read_adapter(void *context)
{
    return ((test_vm *)context)->read();
}

static int write_adapter(void *context, unsigned char value)
{
    return ((test_vm *)context)->write(value);
}

__declspec(dllexport) bf_program *__stdcall bft_compile(const char *source, int length, char *error, int error_length)
{
    return bf_compile(source, (size_t)length, 1, error, (size_t)error_length);
}

__declspec(dllexport) void __stdcall bft_free_program(bf_program *program)
{
    bf_program_free(program);
}

__declspec(dllexport) int __stdcall bft_idiom_count(bf_program *program)
{
    return (int)bf_program_idiom_count(program);
}

__declspec(dllexport) int __stdcall bft_muladd_count(bf_program *program)
{
    return (int)bf_program_muladd_count(program);
}

__declspec(dllexport) test_vm *__stdcall bft_create(bf_program *program, test_read_fn read, test_write_fn write, long long step_budget)
{
    test_vm *vm = (test_vm *)calloc(1, sizeof(test_vm));
    if (!vm) {
        return NULL;
    }

    vm->read = read;
    vm->write = write;
    vm->vm = bf_vm_create(program, read_adapter, write_adapter, vm, step_budget);
    if (!vm->vm) {
        free(vm);
        return NULL;
    }

    return vm;
}

__declspec(dllexport) int __stdcall bft_run(test_vm *vm, char *fault, int fault_length)
{
    return bf_vm_run(vm->vm, fault, (size_t)fault_length);
}

__declspec(dllexport) long long __stdcall bft_total_steps(test_vm *vm)
{
    return bf_vm_total_steps(vm->vm);
}

__declspec(dllexport) int __stdcall bft_cell(test_vm *vm, int index)
{
    return bf_vm_cell(vm->vm, index);
}

__declspec(dllexport) void __stdcall bft_set_cell(test_vm *vm, int index, int value)
{
    bf_vm_set_cell(vm->vm, index, value);
}

__declspec(dllexport) void __stdcall bft_free(test_vm *vm)
{
    if (vm) {
        bf_vm_free(vm->vm);
        free(vm);
    }
}

/* ------------------------------------------------------------------------- */
/* The emergency guard and shared memory, for BridgeTests.                    */
/* ------------------------------------------------------------------------- */

#include "../common/guard.h"
#include "../common/system.h"

typedef int (__stdcall *test_guard_writer_fn)(const wchar_t *path, int output_length, const uint8_t *payload, int length);

static int guard_writer_adapter(void *context, const wchar_t *path, int output_length, const uint8_t *payload, size_t length)
{
    return ((test_guard_writer_fn)context)(path, output_length, payload, (int)length);
}

__declspec(dllexport) output_guard *__stdcall bft_guard_create(int ceiling_percent, test_guard_writer_fn writer)
{
    output_guard *guard = guard_create(ceiling_percent);
    if (guard && writer) {
        guard_set_writer(guard, guard_writer_adapter, (void *)writer);
    }

    return guard;
}

__declspec(dllexport) int __stdcall bft_guard_check(output_guard *guard, const uint8_t *payload, int length)
{
    return guard_check(guard, payload, (size_t)length) != NULL;
}

__declspec(dllexport) void __stdcall bft_guard_written(output_guard *guard, const wchar_t *path, int output_length, const uint8_t *payload, int length)
{
    guard_written(guard, path, output_length, payload, (size_t)length);
}

__declspec(dllexport) int __stdcall bft_guard_any(output_guard *guard)
{
    return guard_any_force(guard);
}

__declspec(dllexport) void __stdcall bft_guard_stop(output_guard *guard)
{
    guard_emergency_stop(guard);
}

__declspec(dllexport) void __stdcall bft_guard_free(output_guard *guard)
{
    guard_free(guard);
}

__declspec(dllexport) shared_memory *__stdcall bft_shm_table(void)
{
    return shm_create_table();
}

__declspec(dllexport) void __stdcall bft_shm_free(shared_memory *table)
{
    shm_free_table(table);
}

__declspec(dllexport) int __stdcall bft_shm_create(shared_memory *table, const wchar_t *name, int size, int everyone, uint32_t *handle)
{
    return shm_create(table, name, (uint32_t)size, everyone, handle);
}

__declspec(dllexport) int __stdcall bft_shm_open(shared_memory *table, const wchar_t *name, int size, uint32_t *handle)
{
    return shm_open(table, name, (uint32_t)size, handle);
}

__declspec(dllexport) int __stdcall bft_shm_read(shared_memory *table, uint32_t handle, int offset, uint8_t *data, int length)
{
    return shm_read(table, handle, (uint32_t)offset, data, (size_t)length);
}

__declspec(dllexport) int __stdcall bft_shm_write(shared_memory *table, uint32_t handle, int offset, const uint8_t *data, int length)
{
    return shm_write(table, handle, (uint32_t)offset, data, (size_t)length);
}

__declspec(dllexport) void __stdcall bft_shm_close(shared_memory *table, uint32_t handle)
{
    shm_close(table, handle);
}
