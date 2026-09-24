/*
 * One persistent Brainfuck program on its own thread (port of BfSession.cs).
 * Producers post event frames from any thread; the program consumes them in
 * order. Command frames the program writes are handed to the command callback
 * on the program's thread, which may answer by posting events.
 */
#ifndef G29_SESSION_H
#define G29_SESSION_H

#include "bfvm.h"
#include "frames.h"

typedef struct g29_session g29_session;

/* Handles one command frame. Returns 0, or non-zero with a message when the
   frame violates the ABI (the session then fails). */
typedef int (*session_command_fn)(void *context, const g29_frame *frame, char *error, size_t error_length);

/* Called once, on the program's thread, when the program faults, violates the
   ABI or ends without being asked to. */
typedef void (*session_failure_fn)(void *context, const char *message);

g29_session *session_create(const bf_program *program, session_command_fn command, session_failure_fn failure, void *context, int64_t step_budget);
int session_start(g29_session *session);
void session_post(g29_session *session, uint8_t type, uint16_t sequence, const uint8_t *payload, size_t length);
/* Makes the next blocking read return "shut down". */
void session_stop(g29_session *session);
/* Waits for the program's thread; 1 when it ended within the timeout. */
int session_join(g29_session *session, unsigned long milliseconds);
size_t session_pending_bytes(g29_session *session);
int session_failed(g29_session *session);
/* Frees everything after the thread ended (or was never started). */
void session_free(g29_session *session);

#endif
