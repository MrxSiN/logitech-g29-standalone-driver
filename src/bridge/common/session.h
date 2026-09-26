/*
 * One persistent compiled Brainfuck program on its own thread.
 * Producers post event frames from any thread; the program consumes them in
 * order. Command frames the program writes are handed to the command callback
 * on the program's thread, which may answer by posting events.
 */
#ifndef G29_SESSION_H
#define G29_SESSION_H

#include "bfrt.h"
#include "frames.h"

/* Unread input the session holds before it fails closed (see session_post). */
#define SESSION_MAX_PENDING (1024u * 1024u)

typedef struct g29_session g29_session;

/* Handles one command frame. Returns 0, or non-zero with a message when the
   frame violates the ABI (the session then fails). */
typedef int (*session_command_fn)(void *context, const g29_frame *frame, char *error, size_t error_length);

/* Called once when the program faults, violates the ABI, ends without being
   asked to, falls more than SESSION_MAX_PENDING bytes behind its input, or is
   aborted (session_abort). Usually on the program's thread; an overflow or an
   abort calls it on the posting or aborting thread. */
typedef void (*session_failure_fn)(void *context, const char *message);

/* program: the compiled entry point; budget: loop iterations between reads. */
g29_session *session_create(bf_program_fn program, session_command_fn command, session_failure_fn failure, void *context, int64_t budget);
int session_start(g29_session *session);
void session_post(g29_session *session, uint8_t type, uint16_t sequence, const uint8_t *payload, size_t length);
/* Makes the next blocking read return "shut down". */
void session_stop(g29_session *session);
/* Waits for the program's thread; 1 when it ended within the timeout. */
int session_join(g29_session *session, unsigned long milliseconds);
size_t session_pending_bytes(g29_session *session);
/* 1 when input has been waiting unread for longer than milliseconds: the
   program is stuck (in a loop within its iteration budget, or in a host call). */
int session_stalled(g29_session *session, unsigned long milliseconds);
/* Fails the session from any thread: the next read ends the program. */
void session_abort(g29_session *session, const char *message);
int session_failed(g29_session *session);
/* Frees everything after the thread ended (or was never started). */
void session_free(g29_session *session);

#endif
