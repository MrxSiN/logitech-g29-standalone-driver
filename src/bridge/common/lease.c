/* Force lease: see lease.h. */
#include "lease.h"

#include <windows.h>
#include <process.h>
#include <stdlib.h>

struct force_lease {
    output_guard *guard;
    g29_session *session;
    uint32_t max_hold_ms;
    uint32_t stall_ms;
    lease_expired_fn expired;
    void *context;
    HANDLE stop;
    HANDLE thread;
};

const char *lease_evaluate(output_guard *guard, g29_session *session, uint32_t max_hold_ms, uint32_t stall_ms, uint64_t now_ms)
{
    uint64_t held = guard_force_held_ms(guard, now_ms);
    if (held == 0) {
        return NULL;
    }

    if (max_hold_ms && held > max_hold_ms) {
        return "The force lease expired: a force was held longer than this host allows.";
    }

    if (session && stall_ms && session_stalled(session, stall_ms)) {
        return "The force lease expired: the Brainfuck program stopped reading its input while a force was applied.";
    }

    return NULL;
}

static unsigned __stdcall lease_thread(void *argument)
{
    force_lease *lease = (force_lease *)argument;
    while (WaitForSingleObject(lease->stop, LEASE_PERIOD_MS) == WAIT_TIMEOUT) {
        const char *reason = lease_evaluate(lease->guard, lease->session, lease->max_hold_ms, lease->stall_ms, (uint64_t)GetTickCount64());
        if (reason) {
            guard_trip(lease->guard);
            if (lease->expired) {
                lease->expired(lease->context, reason);
            }
        }
    }

    return 0;
}

force_lease *lease_start(output_guard *guard, g29_session *session, uint32_t max_hold_ms, uint32_t stall_ms, lease_expired_fn expired, void *context)
{
    force_lease *lease = (force_lease *)calloc(1, sizeof(force_lease));
    if (!lease) {
        return NULL;
    }

    lease->guard = guard;
    lease->session = session;
    lease->max_hold_ms = max_hold_ms;
    lease->stall_ms = stall_ms;
    lease->expired = expired;
    lease->context = context;
    lease->stop = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (lease->stop) {
        lease->thread = (HANDLE)_beginthreadex(NULL, 0, lease_thread, lease, 0, NULL);
    }

    if (!lease->thread) {
        if (lease->stop) {
            CloseHandle(lease->stop);
        }

        free(lease);
        return NULL;
    }

    return lease;
}

void lease_stop(force_lease *lease)
{
    if (!lease) {
        return;
    }

    SetEvent(lease->stop);
    WaitForSingleObject(lease->thread, INFINITE);
    CloseHandle(lease->thread);
    CloseHandle(lease->stop);
    free(lease);
}
