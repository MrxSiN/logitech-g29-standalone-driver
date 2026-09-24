/*
 * The independent emergency force guard (FULL_BRAINFUCK_REWRITE.md section 5),
 * deliberately not Brainfuck: the final circuit breaker that must keep working
 * when the program is wrong or dead. Port of OutputGuard.cs.
 *
 *  - Every HID write passes guard_check first. A G29 constant-force report
 *    (11 08 v 80 ...) whose |v - 0x80| exceeds the role's hard ceiling is
 *    refused and never reaches Windows.
 *  - Interfaces last sent a non-zero force are remembered; guard_emergency_stop
 *    writes the verified stop report (13 00 00 00 00 00 00) to each of them
 *    without running any Brainfuck.
 */
#ifndef G29_GUARD_H
#define G29_GUARD_H

#include <stddef.h>
#include <stdint.h>
#include <wchar.h>

typedef struct output_guard output_guard;

/* ceiling_percent: 25 for the command-line diagnostic, 100 for games. */
output_guard *guard_create(int ceiling_percent);
void guard_free(output_guard *guard);
/* NULL when the payload may be written, otherwise the reason. */
const char *guard_check(const output_guard *guard, const uint8_t *payload, size_t length);
/* Records a write that reached the device. */
void guard_written(output_guard *guard, const wchar_t *path, int output_length, const uint8_t *payload, size_t length);
int guard_any_force(output_guard *guard);
/* How the emergency stop writes (default: hid_write_once). Tests observe it. */
typedef int (*guard_writer_fn)(void *context, const wchar_t *path, int output_length, const uint8_t *payload, size_t length);
void guard_set_writer(output_guard *guard, guard_writer_fn writer, void *context);
/* Best effort: stop every interface that may still hold a force. */
void guard_emergency_stop(output_guard *guard);

#endif
