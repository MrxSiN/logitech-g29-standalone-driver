/*
 * The independent emergency force guard (FULL_BRAINFUCK_REWRITE.md section 5),
 * deliberately not Brainfuck: the final circuit breaker that must keep working
 * when the program is wrong or dead.
 *
 *  - Every HID write passes guard_check first. Only the seven-byte report
 *    families the G29 protocol uses (docs/PROTOCOL.md)
 *    are accepted, each with its fixed bytes and bounded fields; anything else
 *    never reaches Windows. A constant-force report (11 08 v 80 00 00 00) whose
 *    |v - 0x80| exceeds the guard's ceiling is refused.
 *  - Interfaces last sent a non-zero force are remembered with the time the
 *    force began; guard_emergency_stop writes the verified stop report
 *    (13 00 00 00 00 00 00) to each of them without running any Brainfuck.
 *  - guard_trip latches the guard: from then on every non-zero force is
 *    refused, and a force that raced past the check is stopped again as soon
 *    as its write is recorded.
 */
#ifndef G29_GUARD_H
#define G29_GUARD_H

#include <stddef.h>
#include <stdint.h>
#include <wchar.h>

#define GUARD_REPORT_LENGTH 7

typedef struct output_guard output_guard;

/* ceiling_percent: 25 for the command-line diagnostic, 100 for games, 0 for a
   host that must never apply force (the service). */
output_guard *guard_create(int ceiling_percent);
void guard_free(output_guard *guard);
/* Lowers the ceiling (never raises it). Returns 0, or -1 for a raise or an
   out-of-range value. */
int guard_lower_ceiling(output_guard *guard, int ceiling_percent);
/* NULL when the payload may be written, otherwise the reason. */
const char *guard_check(output_guard *guard, const uint8_t *payload, size_t length);
/* Records a write that reached the device. */
void guard_written(output_guard *guard, const wchar_t *path, int output_length, const uint8_t *payload, size_t length);
int guard_any_force(output_guard *guard);
/* How long (ms, on the GetTickCount64 clock) the longest-held force has been
   applied without a stop in between; 0 when no force is held. */
uint64_t guard_force_held_ms(output_guard *guard, uint64_t now_ms);
/* How the emergency stop writes (default: hid_write_once). Tests observe it. */
typedef int (*guard_writer_fn)(void *context, const wchar_t *path, int output_length, const uint8_t *payload, size_t length);
void guard_set_writer(output_guard *guard, guard_writer_fn writer, void *context);
/* Replaces the clock (default: GetTickCount64). Tests only. */
typedef uint64_t (*guard_clock_fn)(void);
void guard_set_clock(output_guard *guard, guard_clock_fn clock);
/* Best effort: stop every interface that may still hold a force. */
void guard_emergency_stop(output_guard *guard);
/* Latches the guard (no more force, see above) and runs the emergency stop. */
void guard_trip(output_guard *guard);
int guard_tripped(output_guard *guard);

#endif
