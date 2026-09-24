/*
 * Generic registry, file, process, host, clock and shared-memory mechanisms for
 * the Brainfuck program (ABI.md). Every key path, name, value and decision
 * comes from the program; these functions perform the operation and report
 * what Windows answered. Port of SystemBridge.cs, SharedMemoryBridge.cs and
 * HostClock.cs.
 */
#ifndef G29_SYSTEM_H
#define G29_SYSTEM_H

#include <stdint.h>
#include "frames.h"

/* Monotonic microseconds, and the system tick count (GetTickCount64). */
int64_t clock_microseconds(void);
int64_t clock_tick_milliseconds(void);

/* EV_HOST_INFO payload: the executable's (or DLL's) directory with a trailing
   backslash. module may be NULL for the executable. */
void system_host_info(void *module, payload_writer *out);

/* One registry command (0xB0..0xB5): reads its request, writes the
   EV_REG_RESULT payload. Returns 0, or non-zero on an ABI violation. */
int system_registry(uint8_t type, payload_reader *request, payload_writer *out, char *error, size_t error_length);

/* CMD_FILE_INFO: status (0 exists, else a Win32 error) and two texts that are
   empty for native files (the .NET assembly identity is not read). */
void system_file_info(const wchar_t *path, payload_writer *out);

typedef void (*process_each_fn)(void *context, uint32_t pid, const wchar_t *name);
/* Every running process: id and name without ".exe". Returns the count. */
int system_processes(process_each_fn each, void *context);

typedef struct shared_memory shared_memory;
shared_memory *shm_create_table(void);
void shm_free_table(shared_memory *table);
/* 0 and a handle, or a Win32 error. everyone = readable/writable by every user. */
int shm_create(shared_memory *table, const wchar_t *name, uint32_t size, int everyone, uint32_t *handle);
int shm_open(shared_memory *table, const wchar_t *name, uint32_t size, uint32_t *handle);
/* 0, or -1 when the handle is unknown or the range lies outside the mapping. */
int shm_read(shared_memory *table, uint32_t handle, uint32_t offset, uint8_t *data, size_t length);
int shm_write(shared_memory *table, uint32_t handle, uint32_t offset, const uint8_t *data, size_t length);
void shm_close(shared_memory *table, uint32_t handle);

#endif
