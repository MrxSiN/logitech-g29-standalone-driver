/*
 * The force lease: a native watchdog thread beside the Brainfuck program,
 * independent of it and impossible for it to disable. Every 50 ms, while the
 * output guard remembers a held force:
 *
 *  - hold limit: a force held longer than max_hold_ms without a stop in
 *    between (0 = no limit) expires;
 *  - liveness: when the program has left input unread for longer than
 *    stall_ms (it is stuck in a loop or a host call), the force expires.
 *
 * Expiry trips the guard (stop report now, no further force) and then calls
 * the host's expired callback, which fails the program's session.
 */
#ifndef G29_LEASE_H
#define G29_LEASE_H

#include "guard.h"
#include "session.h"

#define LEASE_PERIOD_MS 50

typedef struct force_lease force_lease;

typedef void (*lease_expired_fn)(void *context, const char *reason);

force_lease *lease_start(output_guard *guard, g29_session *session, uint32_t max_hold_ms, uint32_t stall_ms, lease_expired_fn expired, void *context);
/* Ends the watchdog thread and frees the lease. */
void lease_stop(force_lease *lease);
/* One check, as the thread runs it; returns the expiry reason or NULL. Tests. */
const char *lease_evaluate(output_guard *guard, g29_session *session, uint32_t max_hold_ms, uint32_t stall_ms, uint64_t now_ms);

#endif
