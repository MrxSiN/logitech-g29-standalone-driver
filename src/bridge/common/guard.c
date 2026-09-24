/* Emergency force guard: see guard.h. */
#include "guard.h"
#include "hid.h"

#include <windows.h>
#include <stdlib.h>
#include <string.h>

#define GUARD_MAX 16

static const uint8_t stop_report[7] = { 0x13, 0, 0, 0, 0, 0, 0 };

typedef struct {
    wchar_t *path;
    int output_length;
} forced_entry;

struct output_guard {
    CRITICAL_SECTION lock;
    int maximum_offset;
    forced_entry forced[GUARD_MAX];
    int count;
    guard_writer_fn writer;
    void *writer_context;
};

void guard_set_writer(output_guard *guard, guard_writer_fn writer, void *context)
{
    guard->writer = writer;
    guard->writer_context = context;
}

output_guard *guard_create(int ceiling_percent)
{
    output_guard *guard;
    if (ceiling_percent < 0 || ceiling_percent > 100) {
        return NULL;
    }

    guard = (output_guard *)calloc(1, sizeof(output_guard));
    if (guard) {
        InitializeCriticalSection(&guard->lock);
        guard->maximum_offset = ((ceiling_percent * 127) + 99) / 100;
    }

    return guard;
}

void guard_free(output_guard *guard)
{
    int index;
    if (!guard) {
        return;
    }

    for (index = 0; index < guard->count; index++) {
        free(guard->forced[index].path);
    }

    DeleteCriticalSection(&guard->lock);
    free(guard);
}

static int is_force(const uint8_t *payload, size_t length)
{
    return length >= 4 && payload[0] == 0x11 && payload[1] == 0x08;
}

const char *guard_check(const output_guard *guard, const uint8_t *payload, size_t length)
{
    if (is_force(payload, length) && abs((int)payload[2] - 0x80) > guard->maximum_offset) {
        return "The emergency guard refused a force report above the hard ceiling.";
    }

    return NULL;
}

static int find(output_guard *guard, const wchar_t *path)
{
    int index;
    for (index = 0; index < guard->count; index++) {
        if (_wcsicmp(guard->forced[index].path, path) == 0) {
            return index;
        }
    }

    return -1;
}

void guard_written(output_guard *guard, const wchar_t *path, int output_length, const uint8_t *payload, size_t length)
{
    int index;
    EnterCriticalSection(&guard->lock);
    index = find(guard, path);
    if (is_force(payload, length) && payload[2] != 0x80) {
        if (index >= 0) {
            guard->forced[index].output_length = output_length;
        } else if (guard->count < GUARD_MAX) {
            wchar_t *copy = _wcsdup(path);
            if (copy) {
                guard->forced[guard->count].path = copy;
                guard->forced[guard->count].output_length = output_length;
                guard->count++;
            }
        }
    } else if (is_force(payload, length) || (length >= 1 && payload[0] == stop_report[0])) {
        /* the stop report, or a constant force of zero */
        if (index >= 0) {
            free(guard->forced[index].path);
            guard->forced[index] = guard->forced[guard->count - 1];
            guard->count--;
        }
    }

    LeaveCriticalSection(&guard->lock);
}

int guard_any_force(output_guard *guard)
{
    int any;
    EnterCriticalSection(&guard->lock);
    any = guard->count > 0;
    LeaveCriticalSection(&guard->lock);
    return any;
}

void guard_emergency_stop(output_guard *guard)
{
    forced_entry targets[GUARD_MAX];
    int count, index;
    if (!guard) {
        return;
    }

    EnterCriticalSection(&guard->lock);
    count = guard->count;
    memcpy(targets, guard->forced, sizeof(forced_entry) * (size_t)count);
    guard->count = 0;
    LeaveCriticalSection(&guard->lock);
    for (index = 0; index < count; index++) {
        /* the wheel may be gone; nothing else can be done here */
        if (guard->writer) {
            guard->writer(guard->writer_context, targets[index].path, targets[index].output_length, stop_report, sizeof(stop_report));
        } else {
            hid_write_once(targets[index].path, targets[index].output_length, stop_report, sizeof(stop_report));
        }
        free(targets[index].path);
    }
}
