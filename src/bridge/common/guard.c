/* Emergency force guard: see guard.h. */
#include "guard.h"
#include "hid.h"

#include <windows.h>
#include <stdlib.h>
#include <string.h>

#define GUARD_MAX 16

static const uint8_t stop_report[GUARD_REPORT_LENGTH] = { 0x13, 0, 0, 0, 0, 0, 0 };

typedef struct {
    wchar_t *path;
    int output_length;
    uint64_t since;
} forced_entry;

struct output_guard {
    CRITICAL_SECTION lock;
    int maximum_offset;
    int tripped;
    forced_entry forced[GUARD_MAX];
    int count;
    guard_writer_fn writer;
    void *writer_context;
    guard_clock_fn clock;
};

static uint64_t tick_clock(void)
{
    return (uint64_t)GetTickCount64();
}

static int offset_for(int ceiling_percent)
{
    /* the protocol's rounding: (127 m + 50) / 100, so 25 % is offset 32 */
    return ((ceiling_percent * 127) + 99) / 100;
}

void guard_set_writer(output_guard *guard, guard_writer_fn writer, void *context)
{
    guard->writer = writer;
    guard->writer_context = context;
}

void guard_set_clock(output_guard *guard, guard_clock_fn clock)
{
    guard->clock = clock ? clock : tick_clock;
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
        guard->maximum_offset = offset_for(ceiling_percent);
        guard->clock = tick_clock;
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
        /* entries below count are set (analysis warning C6001 does not see it) */
#pragma warning(suppress: 6001)
        free(guard->forced[index].path);
    }

    DeleteCriticalSection(&guard->lock);
    free(guard);
}

int guard_lower_ceiling(output_guard *guard, int ceiling_percent)
{
    int offset, result = -1;
    if (ceiling_percent < 0 || ceiling_percent > 100) {
        return -1;
    }

    offset = offset_for(ceiling_percent);
    EnterCriticalSection(&guard->lock);
    if (offset <= guard->maximum_offset) {
        guard->maximum_offset = offset;
        result = 0;
    }

    LeaveCriticalSection(&guard->lock);
    return result;
}

static int zero_from(const uint8_t *payload, int start)
{
    int index;
    for (index = start; index < GUARD_REPORT_LENGTH; index++) {
        if (payload[index] != 0) {
            return 0;
        }
    }

    return 1;
}

static int is_force(const uint8_t *payload, size_t length)
{
    return length == GUARD_REPORT_LENGTH && payload[0] == 0x11 && payload[1] == 0x08;
}

/* |v - 0x80| of a constant-force report. */
static int force_offset(const uint8_t *payload)
{
    return abs((int)payload[2] - 0x80);
}

/* The report families of the G29 protocol (docs/PROTOCOL.md), with their fixed bytes and the bounds of
   their variable fields. Returns 1 for a known, well-formed report. */
static int known_report(const uint8_t *p)
{
    switch (p[0]) {
    case 0x13: /* stop */
    case 0x14: /* autocenter on */
    case 0xF5: /* autocenter off */
        return zero_from(p, 1);
    case 0x11: /* constant force: 11 08 v 80 00 00 00 */
        return p[1] == 0x08 && p[3] == 0x80 && zero_from(p, 4);
    case 0xFE: /* autocenter strength: FE 0D a a b 00 00, a <= 7, b >= 1 (zero is F5) */
        return p[1] == 0x0D && p[2] == p[3] && p[2] <= 7 && p[4] >= 1 && zero_from(p, 5);
    case 0xF8:
        switch (p[1]) {
        case 0x0A: /* native mode, first report */
            return zero_from(p, 2);
        case 0x09: /* native mode, second report */
            return p[2] == 0x05 && p[3] == 0x01 && p[4] == 0x01 && zero_from(p, 5);
        case 0x81: { /* range 40..900 degrees, little-endian */
            int degrees = p[2] | (p[3] << 8);
            return degrees >= 40 && degrees <= 900 && zero_from(p, 4);
        }
        case 0x12: /* LEDs 0..31 */
            return p[2] <= 31 && zero_from(p, 3);
        default:
            return 0;
        }
    default:
        return 0;
    }
}

const char *guard_check(output_guard *guard, const uint8_t *payload, size_t length)
{
    const char *refused = NULL;
    if (!payload || length != GUARD_REPORT_LENGTH || !known_report(payload)) {
        return "The emergency guard refused a report outside the G29 protocol.";
    }

    if (is_force(payload, length)) {
        int offset = force_offset(payload);
        EnterCriticalSection(&guard->lock);
        if (offset > guard->maximum_offset) {
            refused = "The emergency guard refused a force report above the hard ceiling.";
        } else if (offset != 0 && guard->tripped) {
            refused = "The emergency guard refused a force report after an emergency stop.";
        }

        LeaveCriticalSection(&guard->lock);
    }

    return refused;
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

static void write_stop(output_guard *guard, const wchar_t *path, int output_length)
{
    /* the wheel may be gone; nothing else can be done here */
    if (guard->writer) {
        guard->writer(guard->writer_context, path, output_length, stop_report, sizeof(stop_report));
    } else {
        hid_write_once(path, output_length, stop_report, sizeof(stop_report));
    }
}

void guard_written(output_guard *guard, const wchar_t *path, int output_length, const uint8_t *payload, size_t length)
{
    int index, restop = 0;
    EnterCriticalSection(&guard->lock);
    index = find(guard, path);
    if (is_force(payload, length) && payload[2] != 0x80) {
        if (guard->tripped) {
            /* checked before the trip, written after it */
            restop = 1;
        } else if (index >= 0) {
            /* a changed force continues the same hold */
            guard->forced[index].output_length = output_length;
        } else if (guard->count < GUARD_MAX) {
            wchar_t *copy = _wcsdup(path);
            if (copy) {
                guard->forced[guard->count].path = copy;
                guard->forced[guard->count].output_length = output_length;
                guard->forced[guard->count].since = guard->clock();
                guard->count++;
            }
        }
    } else if (is_force(payload, length) || (length == GUARD_REPORT_LENGTH && payload[0] == stop_report[0])) {
        /* the stop report, or a constant force of zero */
        if (index >= 0) {
            free(guard->forced[index].path);
            guard->forced[index] = guard->forced[guard->count - 1];
            guard->count--;
        }
    }

    LeaveCriticalSection(&guard->lock);
    if (restop) {
        write_stop(guard, path, output_length);
    }
}

int guard_any_force(output_guard *guard)
{
    int any;
    EnterCriticalSection(&guard->lock);
    any = guard->count > 0;
    LeaveCriticalSection(&guard->lock);
    return any;
}

uint64_t guard_force_held_ms(output_guard *guard, uint64_t now_ms)
{
    uint64_t longest = 0;
    int index;
    EnterCriticalSection(&guard->lock);
    for (index = 0; index < guard->count; index++) {
        uint64_t held = now_ms > guard->forced[index].since ? now_ms - guard->forced[index].since : 0;
        if (held > longest) {
            longest = held;
        }
    }

    /* a force begun this very millisecond is still a held force */
    if (guard->count > 0 && longest == 0) {
        longest = 1;
    }

    LeaveCriticalSection(&guard->lock);
    return longest;
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
        write_stop(guard, targets[index].path, targets[index].output_length);
        free(targets[index].path);
    }
}

void guard_trip(output_guard *guard)
{
    if (!guard) {
        return;
    }

    EnterCriticalSection(&guard->lock);
    guard->tripped = 1;
    LeaveCriticalSection(&guard->lock);
    guard_emergency_stop(guard);
}

int guard_tripped(output_guard *guard)
{
    int tripped;
    EnterCriticalSection(&guard->lock);
    tripped = guard->tripped;
    LeaveCriticalSection(&guard->lock);
    return tripped;
}
