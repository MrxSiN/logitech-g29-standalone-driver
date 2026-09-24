/* Persistent program session: see session.h. */
#include "session.h"

#include <process.h>
#include <stdlib.h>
#include <string.h>
#include <windows.h>

typedef struct input_chunk {
    struct input_chunk *next;
    size_t length;
    size_t position;
    uint8_t bytes[1];
} input_chunk;

struct g29_session {
    CRITICAL_SECTION lock;
    CONDITION_VARIABLE available;
    input_chunk *head;
    input_chunk *tail;
    size_t pending;
    int stopping;
    int failed;
    bf_vm *vm;
    frame_parser parser;
    session_command_fn command;
    session_failure_fn failure;
    void *context;
    HANDLE thread;
    char violation[256];
};

static int session_read(void *context)
{
    g29_session *session = (g29_session *)context;
    int value = -1;
    EnterCriticalSection(&session->lock);
    for (;;) {
        input_chunk *chunk = session->head;
        if (chunk) {
            value = chunk->bytes[chunk->position++];
            session->pending--;
            if (chunk->position == chunk->length) {
                session->head = chunk->next;
                if (!session->head) {
                    session->tail = NULL;
                }

                free(chunk);
            }

            break;
        }

        if (session->stopping) {
            value = -1;
            break;
        }

        SleepConditionVariableCS(&session->available, &session->lock, INFINITE);
    }

    LeaveCriticalSection(&session->lock);
    return value;
}

static int session_write(void *context, unsigned char value)
{
    g29_session *session = (g29_session *)context;
    int result = frame_parser_push(&session->parser, value, session->violation, sizeof(session->violation));
    if (result < 0) {
        return 1;
    }

    if (result == 1) {
        session->violation[0] = 0;
        if (session->command(session->context, &session->parser.frame, session->violation, sizeof(session->violation)) != 0) {
            if (!session->violation[0]) {
                strcpy(session->violation, "The bridge refused a command frame.");
            }

            return 1;
        }
    }

    return 0;
}

static void session_fail(g29_session *session, const char *message)
{
    EnterCriticalSection(&session->lock);
    session->failed = 1;
    session->stopping = 1;
    WakeAllConditionVariable(&session->available);
    LeaveCriticalSection(&session->lock);
    if (session->failure) {
        session->failure(session->context, message);
    }
}

static unsigned __stdcall session_thread(void *argument)
{
    g29_session *session = (g29_session *)argument;
    char fault[256];
    int result;
    session->violation[0] = 0;
    result = bf_vm_run(session->vm, fault, sizeof(fault));
    if (result == BF_FAULT) {
        /* a refused output carries the bridge's reason */
        session_fail(session, session->violation[0] ? session->violation : fault);
    } else if (result == BF_FINISHED) {
        int stopping;
        EnterCriticalSection(&session->lock);
        stopping = session->stopping;
        LeaveCriticalSection(&session->lock);
        if (!stopping) {
            session_fail(session, "The Brainfuck program ended without being asked to.");
        }
    }

    return 0;
}

g29_session *session_create(const bf_program *program, session_command_fn command, session_failure_fn failure, void *context, int64_t step_budget)
{
    g29_session *session = (g29_session *)calloc(1, sizeof(g29_session));
    if (!session) {
        return NULL;
    }

    InitializeCriticalSection(&session->lock);
    InitializeConditionVariable(&session->available);
    session->command = command;
    session->failure = failure;
    session->context = context;
    session->vm = bf_vm_create(program, session_read, session_write, session, step_budget);
    if (!session->vm) {
        DeleteCriticalSection(&session->lock);
        free(session);
        return NULL;
    }

    return session;
}

int session_start(g29_session *session)
{
    session->thread = (HANDLE)_beginthreadex(NULL, 0, session_thread, session, 0, NULL);
    return session->thread != NULL;
}

void session_post(g29_session *session, uint8_t type, uint16_t sequence, const uint8_t *payload, size_t length)
{
    input_chunk *chunk;
    if (length > FRAME_MAX_PAYLOAD) {
        return;
    }

    chunk = (input_chunk *)malloc(sizeof(input_chunk) + FRAME_HEADER + length);
    if (!chunk) {
        return;
    }

    chunk->next = NULL;
    chunk->position = 0;
    chunk->length = frame_encode(type, 0, sequence, payload, length, chunk->bytes);
    EnterCriticalSection(&session->lock);
    if (session->stopping) {
        LeaveCriticalSection(&session->lock);
        free(chunk);
        return;
    }

    if (session->tail) {
        session->tail->next = chunk;
    } else {
        session->head = chunk;
    }

    session->tail = chunk;
    session->pending += chunk->length;
    WakeAllConditionVariable(&session->available);
    LeaveCriticalSection(&session->lock);
}

void session_stop(g29_session *session)
{
    EnterCriticalSection(&session->lock);
    session->stopping = 1;
    WakeAllConditionVariable(&session->available);
    LeaveCriticalSection(&session->lock);
}

int session_join(g29_session *session, unsigned long milliseconds)
{
    if (!session->thread) {
        return 1;
    }

    if (GetCurrentThreadId() == GetThreadId(session->thread)) {
        return 0;
    }

    return WaitForSingleObject(session->thread, milliseconds) == WAIT_OBJECT_0;
}

size_t session_pending_bytes(g29_session *session)
{
    size_t pending;
    EnterCriticalSection(&session->lock);
    pending = session->pending;
    LeaveCriticalSection(&session->lock);
    return pending;
}

int session_failed(g29_session *session)
{
    int failed;
    EnterCriticalSection(&session->lock);
    failed = session->failed;
    LeaveCriticalSection(&session->lock);
    return failed;
}

void session_free(g29_session *session)
{
    input_chunk *chunk;
    if (!session) {
        return;
    }

    if (session->thread) {
        CloseHandle(session->thread);
    }

    for (chunk = session->head; chunk;) {
        input_chunk *next = chunk->next;
        free(chunk);
        chunk = next;
    }

    bf_vm_free(session->vm);
    DeleteCriticalSection(&session->lock);
    free(session);
}
