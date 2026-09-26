/*
 * g29ctl.exe: boots the Brainfuck program in role CLI with the command line and
 * performs the generic commands it issues (console text, HID, timers, registry,
 * processes, shared memory, the Service Control Manager connection, event-log
 * text, exit). It decides nothing about commands, devices or messages.
 */
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "../common/bfrt.h"
#include "../common/frames.h"
#include "../common/guard.h"
#include "../common/hid.h"
#include "../common/lease.h"
#include "../common/session.h"
#include "../common/system.h"

#define ROLE_CLI 1
#define CLI_FORCE_CEILING_PERCENT 25
/* The diagnostic force lasts at most 5000 ms; the lease allows timer and write
   latency on top and then stops the wheel whatever the program does. */
#define CLI_FORCE_HOLD_LIMIT_MS 6000
/* Input unread this long while a force is held means the program is stuck. */
#define FORCE_STALL_LIMIT_MS 1000
/* The service never applies force: its guard admits only a zero force. */
#define SERVICE_FORCE_CEILING_PERCENT 0
#define SERVICE_NAME_LIMIT 64
#define TEXT_CLIP 1000
#define PENDING_TEXT_LIMIT 32000
#define SERVICE_STOP_WAIT 10000

typedef struct {
    g29_session *session;
    hid_bridge *hid;
    output_guard *guard;
    force_lease *lease;
    shared_memory *shm;
    HANDLE finished;
    HANDLE service_requested;
    HANDLE service_stop;
    CRITICAL_SECTION timer_lock;
    PTP_TIMER timers[256];
    volatile LONG exit_code;
    volatile LONG failed;
    char failure[512];
    wchar_t *service_name;
    int service_running;
    HANDLE event_source;
    wchar_t *pending_text;
    size_t pending_length;
} host;

static host app;
static SERVICE_STATUS_HANDLE service_status_handle;

/* ------------------------------------------------------------------------- */
/* Console and event log                                                      */
/* ------------------------------------------------------------------------- */

/* Like .NET Console: characters to a console window, otherwise the console's
   output code page. */
static void console_write(DWORD which, const wchar_t *text, size_t length)
{
    HANDLE handle = GetStdHandle(which);
    DWORD mode, written;
    int bytes;
    char *encoded;
    UINT page;
    if (!handle || handle == INVALID_HANDLE_VALUE || length == 0) {
        return;
    }

    if (GetConsoleMode(handle, &mode)) {
        WriteConsoleW(handle, text, (DWORD)length, &written, NULL);
        return;
    }

    page = GetConsoleOutputCP();
    if (!page) {
        page = GetOEMCP();
    }

    bytes = WideCharToMultiByte(page, 0, text, (int)length, NULL, 0, NULL, NULL);
    encoded = (char *)malloc(bytes > 0 ? (size_t)bytes : 1);
    if (!encoded) {
        return;
    }

    WideCharToMultiByte(page, 0, text, (int)length, encoded, bytes, NULL, NULL);
    WriteFile(handle, encoded, (DWORD)bytes, &written, NULL);
    free(encoded);
}

static void console_line(DWORD which, const wchar_t *text)
{
    console_write(which, text, wcslen(text));
    console_write(which, L"\r\n", 2);
}

/* The service's event source: registered (message file = this executable,
   message 0 = "%1") the first time it is needed, like .NET's EventLog did. */
static void event_source_open(void)
{
    wchar_t key_path[512], module[MAX_PATH * 4];
    HKEY key;
    DWORD types = 7;
    if (app.event_source || !app.service_name) {
        return;
    }

    swprintf(key_path, sizeof(key_path) / sizeof(key_path[0]), L"SYSTEM\\CurrentControlSet\\Services\\EventLog\\Application\\%s", app.service_name);
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, key_path, 0, KEY_READ, &key) == ERROR_SUCCESS) {
        RegCloseKey(key);
    } else if (GetModuleFileNameW(NULL, module, MAX_PATH * 4) &&
               RegCreateKeyExW(HKEY_LOCAL_MACHINE, key_path, 0, NULL, 0, KEY_WRITE, NULL, &key, NULL) == ERROR_SUCCESS) {
        RegSetValueExW(key, L"EventMessageFile", 0, REG_EXPAND_SZ, (const BYTE *)module, (DWORD)((wcslen(module) + 1) * sizeof(wchar_t)));
        RegSetValueExW(key, L"TypesSupported", 0, REG_DWORD, (const BYTE *)&types, sizeof(types));
        RegCloseKey(key);
    }

    app.event_source = RegisterEventSourceW(NULL, app.service_name);
}

static void event_write(const wchar_t *text, unsigned int severity)
{
    WORD type = severity == 2 ? EVENTLOG_ERROR_TYPE : severity == 1 ? EVENTLOG_WARNING_TYPE : EVENTLOG_INFORMATION_TYPE;
    if (app.service_running) {
        event_source_open();
        if (app.event_source) {
            /* the event log is diagnostics only */
            ReportEventW(app.event_source, type, 0, 0, NULL, 1, 0, &text, NULL);
        }
    } else {
        console_line(STD_ERROR_HANDLE, text);
    }
}

/* ------------------------------------------------------------------------- */
/* Timers                                                                     */
/* ------------------------------------------------------------------------- */

static VOID CALLBACK timer_fired(PTP_CALLBACK_INSTANCE instance, PVOID context, PTP_TIMER timer)
{
    payload_writer w;
    (void)instance;
    (void)timer;
    pw_init(&w);
    pw_u8(&w, (unsigned int)(uintptr_t)context);
    pw_u64(&w, (uint64_t)clock_microseconds());
    pw_u64(&w, (uint64_t)clock_tick_milliseconds());
    session_post(app.session, 0x02, 0, w.data, w.length);
}

static void timer_arm(unsigned int id, int repeat, uint32_t interval)
{
    FILETIME due;
    ULARGE_INTEGER relative;
    EnterCriticalSection(&app.timer_lock);
    if (!app.timers[id]) {
        app.timers[id] = CreateThreadpoolTimer(timer_fired, (PVOID)(uintptr_t)id, NULL);
    }

    if (app.timers[id]) {
        /* negative = relative, in 100 ns units */
        relative.QuadPart = (ULONGLONG)(-(LONGLONG)interval * 10000);
        due.dwLowDateTime = relative.LowPart;
        due.dwHighDateTime = relative.HighPart;
        SetThreadpoolTimer(app.timers[id], &due, repeat ? interval : 0, 0);
    }

    LeaveCriticalSection(&app.timer_lock);
}

static void timer_cancel(unsigned int id)
{
    EnterCriticalSection(&app.timer_lock);
    if (app.timers[id]) {
        SetThreadpoolTimer(app.timers[id], NULL, 0, 0);
    }

    LeaveCriticalSection(&app.timer_lock);
}

static void timers_close(void)
{
    int id;
    for (id = 0; id < 256; id++) {
        if (app.timers[id]) {
            SetThreadpoolTimer(app.timers[id], NULL, 0, 0);
            WaitForThreadpoolTimerCallbacks(app.timers[id], TRUE);
            CloseThreadpoolTimer(app.timers[id]);
            app.timers[id] = NULL;
        }
    }
}

/* ------------------------------------------------------------------------- */
/* Commands                                                                   */
/* ------------------------------------------------------------------------- */

static void post(uint8_t type, uint16_t sequence, const payload_writer *w)
{
    session_post(app.session, type, sequence, w->data, w->length);
}

static void each_device(void *context, const hid_interface *item)
{
    uint16_t sequence = *(uint16_t *)context;
    payload_writer w;
    pw_init(&w);
    pw_u32(&w, item->token);
    pw_u16(&w, item->vendor_id);
    pw_u16(&w, item->product_id);
    pw_u16(&w, item->version);
    pw_u16(&w, item->usage_page);
    pw_u16(&w, item->usage);
    pw_u16(&w, (unsigned int)item->input_length);
    pw_u16(&w, (unsigned int)item->output_length);
    pw_text(&w, item->path, TEXT_CLIP);
    pw_text(&w, item->product, TEXT_CLIP);
    post(0x11, sequence, &w);
}

typedef struct {
    uint16_t sequence;
    int count;
} enumeration;

static void each_device_counted(void *context, const hid_interface *item)
{
    enumeration *e = (enumeration *)context;
    each_device(&e->sequence, item);
    e->count++;
}

static void each_process(void *context, uint32_t pid, const wchar_t *name)
{
    payload_writer w;
    pw_init(&w);
    pw_u32(&w, pid);
    pw_text(&w, name, 0);
    post(0x21, *(uint16_t *)context, &w);
}

#define VIOLATION(...) do { snprintf(error, error_length, __VA_ARGS__); return 1; } while (0)

static int on_command(void *context, const g29_frame *frame, char *error, size_t error_length)
{
    payload_reader r;
    payload_writer w;
    (void)context;
    pr_init(&r, frame->payload, frame->length);
    pw_init(&w);
    switch (frame->type) {
    case 0x80:
        InterlockedExchange(&app.exit_code, (LONG)pr_u32(&r));
        if (!pr_end(&r)) VIOLATION("Malformed exit.");
        session_stop(app.session);
        SetEvent(app.finished);
        return 0;
    case 0x81: {
        wchar_t *text;
        pr_u8(&r);
        text = pr_text(&r);
        if (!text || !pr_end(&r)) { free(text); VIOLATION("Malformed log."); }
        console_line(STD_ERROR_HANDLE, text);
        free(text);
        return 0;
    }

    case 0x82: {
        unsigned int stream = pr_u8(&r);
        wchar_t *text = pr_text(&r);
        if (!text || !pr_end(&r)) { free(text); VIOLATION("Malformed console text."); }
        if (stream != 1 && stream != 2) { free(text); VIOLATION("Unknown console stream %u.", stream); }
        console_write(stream == 2 ? STD_ERROR_HANDLE : STD_OUTPUT_HANDLE, text, wcslen(text));
        free(text);
        return 0;
    }

    case 0x83: {
        unsigned int severity = pr_u8(&r);
        wchar_t *text = pr_text(&r), *full;
        size_t length;
        if (!text || !pr_end(&r)) { free(text); VIOLATION("Malformed event-log text."); }
        length = app.pending_length + wcslen(text);
        full = (wchar_t *)calloc(length + 1, sizeof(wchar_t));
        if (full) {
            if (app.pending_text) {
                wcscpy(full, app.pending_text);
            }

            wcscat(full, text);
            event_write(full, severity);
            free(full);
        }

        free(text);
        free(app.pending_text);
        app.pending_text = NULL;
        app.pending_length = 0;
        return 0;
    }

    case 0x85: {
        wchar_t *text = pr_text(&r), *joined;
        size_t length;
        if (!text || !pr_end(&r)) { free(text); VIOLATION("Malformed event-log text."); }
        length = app.pending_length + wcslen(text);
        if (length > PENDING_TEXT_LIMIT) { free(text); VIOLATION("Pending event-log text is too long."); }
        joined = (wchar_t *)realloc(app.pending_text, (length + 1) * sizeof(wchar_t));
        if (!joined) { free(text); VIOLATION("Out of memory."); }
        wcscpy(joined + app.pending_length, text);
        app.pending_text = joined;
        app.pending_length = length;
        free(text);
        return 0;
    }

    case 0x84:
        return 0;
    case 0x86:
        if (!pr_end(&r)) VIOLATION("Malformed host-info request.");
        system_host_info(NULL, &w);
        post(0x06, frame->sequence, &w);
        return 0;
    case 0x90: {
        enumeration e;
        payload_writer end;
        if (!pr_end(&r)) VIOLATION("Malformed enumeration request.");
        e.sequence = frame->sequence;
        e.count = 0;
        post(0x10, frame->sequence, &w);
        /* a failing enumeration reports no interfaces */
        hid_enumerate(app.hid, each_device_counted, &e);
        pw_init(&end);
        pw_u16(&end, (unsigned int)e.count);
        post(0x12, frame->sequence, &end);
        return 0;
    }

    case 0x91: {
        uint32_t token = pr_u32(&r);
        if (!pr_end(&r)) VIOLATION("Malformed open.");
        pw_u32(&w, token);
        pw_u32(&w, (uint32_t)hid_open(app.hid, token));
        post(0x13, frame->sequence, &w);
        return 0;
    }

    case 0x92: {
        uint32_t token = pr_u32(&r);
        if (!pr_end(&r)) VIOLATION("Malformed close.");
        hid_close(app.hid, token);
        return 0;
    }

    case 0x93: {
        uint32_t token = pr_u32(&r);
        unsigned int length = pr_u16(&r);
        const uint8_t *payload = pr_bytes(&r, length);
        const char *refused;
        int result;
        hid_interface target;
        if (!pr_end(&r)) VIOLATION("Malformed write.");
        refused = guard_check(app.guard, payload, length);
        if (refused) VIOLATION("%s", refused);
        result = hid_write(app.hid, token, payload, length);
        if (result == 0 && hid_find(app.hid, token, &target)) {
            guard_written(app.guard, target.path, target.output_length, payload, length);
        }

        pw_u32(&w, token);
        pw_u32(&w, (uint32_t)result);
        post(0x14, frame->sequence, &w);
        return 0;
    }

    case 0xA0: {
        unsigned int id = pr_u8(&r);
        int repeat = pr_u8(&r) != 0;
        uint32_t interval = pr_u32(&r);
        if (!pr_end(&r)) VIOLATION("Malformed timer.");
        timer_arm(id, repeat, interval);
        return 0;
    }

    case 0xA1: {
        unsigned int id = pr_u8(&r);
        if (!pr_end(&r)) VIOLATION("Malformed timer cancel.");
        timer_cancel(id);
        return 0;
    }

    case 0xB0:
    case 0xB1:
    case 0xB2:
    case 0xB3:
    case 0xB4:
    case 0xB5:
        if (system_registry(frame->type, &r, &w, error, error_length) != 0) {
            return 1;
        }

        post(0x30, frame->sequence, &w);
        return 0;
    case 0xB6: {
        wchar_t *path = pr_text(&r);
        if (!path || !pr_end(&r)) { free(path); VIOLATION("Malformed file-info request."); }
        system_file_info(path, &w);
        free(path);
        post(0x32, frame->sequence, &w);
        return 0;
    }

    case 0xB7: {
        wchar_t *name = pr_text(&r);
        if (!name || !pr_end(&r) || !*name) { free(name); VIOLATION("A service needs a name."); }
        if (wcslen(name) > SERVICE_NAME_LIMIT || wcsspn(name, L"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_") != wcslen(name)) {
            free(name);
            VIOLATION("A service name may only use letters, digits and underscores.");
        }

        if (app.service_name) { free(name); VIOLATION("The service was already requested."); }
        /* a service host never pushes the wheel */
        guard_lower_ceiling(app.guard, SERVICE_FORCE_CEILING_PERCENT);
        guard_emergency_stop(app.guard);
        app.service_name = name;
        SetEvent(app.service_requested);
        return 0;
    }

    case 0xBE:
        if (!pr_end(&r)) VIOLATION("Malformed stop request.");
        if (!app.service_running) VIOLATION("Only a running service can stop itself.");
        /* like ServiceBase.Stop(): the program is told to stop */
        SetEvent(app.service_stop);
        session_post(app.session, 0x41, 0, NULL, 0);
        return 0;
    case 0xC0: {
        uint16_t sequence = frame->sequence;
        int count;
        payload_writer end;
        if (!pr_end(&r)) VIOLATION("Malformed process request.");
        post(0x20, sequence, &w);
        count = system_processes(each_process, &sequence);
        pw_init(&end);
        pw_u16(&end, (unsigned int)count);
        post(0x22, sequence, &end);
        return 0;
    }

    case 0xC8: {
        wchar_t *name = pr_text(&r);
        uint32_t size = pr_u32(&r), handle = 0;
        int everyone = pr_u8(&r) != 0, status;
        if (!name || !pr_end(&r)) { free(name); VIOLATION("Malformed shared-memory request."); }
        if (size == 0 || size > 65536) { free(name); VIOLATION("Shared memory size out of range."); }
        status = shm_create(app.shm, name, size, everyone, &handle);
        free(name);
        pw_u32(&w, (uint32_t)status);
        pw_u32(&w, handle);
        post(0x33, frame->sequence, &w);
        return 0;
    }

    case 0xCA: {
        uint32_t handle = pr_u32(&r), offset = pr_u32(&r);
        unsigned int length = pr_u16(&r);
        if (!pr_end(&r) || length > FRAME_MAX_PAYLOAD - 4) VIOLATION("Malformed shared-memory read.");
        pw_u32(&w, 0);
        /* read straight into the answer, after its status */
        if (shm_read(app.shm, handle, offset, w.data + w.length, length) != 0) VIOLATION("Shared memory read outside the mapping.");
        w.length += length;
        post(0x33, frame->sequence, &w);
        return 0;
    }

    case 0xCB: {
        uint32_t handle = pr_u32(&r), offset = pr_u32(&r);
        unsigned int length = pr_u16(&r);
        const uint8_t *data = pr_bytes(&r, length);
        if (!pr_end(&r)) VIOLATION("Malformed shared-memory write.");
        if (shm_write(app.shm, handle, offset, data, length) != 0) VIOLATION("Shared memory write outside the mapping.");
        return 0;
    }

    case 0xCC: {
        uint32_t handle = pr_u32(&r);
        if (!pr_end(&r)) VIOLATION("Malformed shared-memory close.");
        shm_close(app.shm, handle);
        return 0;
    }

    default:
        VIOLATION("Unknown command frame type %02X.", frame->type);
    }
}

static void on_failure(void *context, const char *message)
{
    (void)context;
    guard_trip(app.guard);
    strncpy(app.failure, message, sizeof(app.failure) - 1);
    InterlockedExchange(&app.failed, 1);
    SetEvent(app.finished);
}

static void on_lease_expired(void *context, const char *reason)
{
    (void)context;
    session_abort(app.session, reason);
}

/* ------------------------------------------------------------------------- */
/* Service Control Manager                                                    */
/* ------------------------------------------------------------------------- */

static void report_status(DWORD state)
{
    SERVICE_STATUS status;
    memset(&status, 0, sizeof(status));
    status.dwServiceType = SERVICE_WIN32_OWN_PROCESS;
    status.dwCurrentState = state;
    status.dwControlsAccepted = state == SERVICE_RUNNING ? SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN : 0;
    status.dwWaitHint = state == SERVICE_STOP_PENDING ? SERVICE_STOP_WAIT : 0;
    SetServiceStatus(service_status_handle, &status);
}

static DWORD WINAPI service_control(DWORD control, DWORD type, LPVOID data, LPVOID context)
{
    (void)type;
    (void)data;
    (void)context;
    switch (control) {
    case SERVICE_CONTROL_STOP:
    case SERVICE_CONTROL_SHUTDOWN:
        report_status(SERVICE_STOP_PENDING);
        SetEvent(app.service_stop);
        session_post(app.session, control == SERVICE_CONTROL_STOP ? 0x41 : 0x42, 0, NULL, 0);
        return NO_ERROR;
    case SERVICE_CONTROL_INTERROGATE:
        return NO_ERROR;
    default:
        return ERROR_CALL_NOT_IMPLEMENTED;
    }
}

static VOID WINAPI service_main(DWORD argc, LPWSTR *argv)
{
    HANDLE waits[2];
    (void)argc;
    (void)argv;
    service_status_handle = RegisterServiceCtrlHandlerExW(app.service_name, service_control, NULL);
    if (!service_status_handle) {
        return;
    }

    app.service_running = 1;
    report_status(SERVICE_RUNNING);
    session_post(app.session, 0x40, 0, NULL, 0);
    waits[0] = app.finished;
    waits[1] = app.service_stop;
    if (WaitForMultipleObjects(2, waits, FALSE, INFINITE) == WAIT_OBJECT_0 + 1) {
        /* a stop was requested: the program has up to 10 s to finish */
        WaitForSingleObject(app.finished, SERVICE_STOP_WAIT);
    }

    SetEvent(app.finished);
    report_status(SERVICE_STOPPED);
}

/* ------------------------------------------------------------------------- */
/* Entry                                                                      */
/* ------------------------------------------------------------------------- */

static BOOL WINAPI console_control(DWORD type)
{
    if (type == CTRL_C_EVENT || type == CTRL_BREAK_EVENT) {
        /* the program decides what Ctrl+C means (the force test still stops the wheel) */
        if (app.session) {
            session_post(app.session, 0x44, 0, NULL, 0);
        }

        return TRUE;
    }

    /* the console is closing: nothing may keep pushing the wheel */
    guard_emergency_stop(app.guard);
    return FALSE;
}

int wmain(int argc, wchar_t **argv)
{
    payload_writer boot;
    HANDLE waits[2];
    int index, result;
    /* the service runs as LocalSystem: later DLL loads come from System32 only */
    SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32);
    memset(&app, 0, sizeof(app));
    app.exit_code = 1;
    InitializeCriticalSection(&app.timer_lock);
    app.hid = hid_create();
    app.guard = guard_create(CLI_FORCE_CEILING_PERCENT);
    app.shm = shm_create_table();
    app.finished = CreateEventW(NULL, TRUE, FALSE, NULL);
    app.service_requested = CreateEventW(NULL, TRUE, FALSE, NULL);
    app.service_stop = CreateEventW(NULL, TRUE, FALSE, NULL);
    app.session = session_create(g29_program_run, on_command, on_failure, NULL, BF_DEFAULT_ITERATION_BUDGET);
    if (!app.hid || !app.guard || !app.shm || !app.finished || !app.service_requested || !app.service_stop || !app.session) {
        fprintf(stderr, "Error: Out of memory.\n");
        return 1;
    }

    pw_init(&boot);
    pw_u8(&boot, ROLE_CLI);
    pw_u8(&boot, (unsigned int)sizeof(void *));
    pw_u8(&boot, sizeof(void *) == 8 ? 9 : 0);
    pw_u8(&boot, 0);
    pw_u16(&boot, (unsigned int)(argc - 1));
    for (index = 1; index < argc; index++) {
        pw_text(&boot, argv[index], 0);
    }

    if (boot.overflow) {
        fprintf(stderr, "Error: The command line is too long.\n");
        return 1;
    }

    app.lease = lease_start(app.guard, app.session, CLI_FORCE_HOLD_LIMIT_MS, FORCE_STALL_LIMIT_MS, on_lease_expired, NULL);
    if (!app.lease) {
        fprintf(stderr, "Error: The force lease could not be started.\n");
        return 1;
    }

    SetConsoleCtrlHandler(console_control, TRUE);
    session_post(app.session, 0x01, 0, boot.data, boot.length);
    if (!session_start(app.session)) {
        guard_trip(app.guard);
        fprintf(stderr, "Error: The program thread could not be started.\n");
        return 1;
    }
    waits[0] = app.finished;
    waits[1] = app.service_requested;
    if (WaitForMultipleObjects(2, waits, FALSE, INFINITE) == WAIT_OBJECT_0 + 1 && !app.failed) {
        /* the program asked to run as a Windows service: hand this thread to the SCM */
        SERVICE_TABLE_ENTRYW table[2];
        table[0].lpServiceName = app.service_name;
        table[0].lpServiceProc = service_main;
        table[1].lpServiceName = NULL;
        table[1].lpServiceProc = NULL;
        if (!StartServiceCtrlDispatcherW(table) && WaitForSingleObject(app.finished, 0) != WAIT_OBJECT_0) {
            /* not started by the SCM (for example run from a console) */
            if (!app.failed) {
                strcpy(app.failure, "The service could not connect to the Service Control Manager.");
                InterlockedExchange(&app.failed, 1);
            }

            SetEvent(app.finished);
        }
    }

    WaitForSingleObject(app.finished, INFINITE);
    session_stop(app.session);
    if (!session_join(app.session, 2000)) {
        /* the program is stuck in a host call or a loop: no more force */
        guard_trip(app.guard);
    }

    lease_stop(app.lease);
    SetConsoleCtrlHandler(console_control, FALSE);
    timers_close();
    if (app.failed) {
        wchar_t message[600];
        guard_emergency_stop(app.guard);
        swprintf(message, sizeof(message) / sizeof(message[0]), L"Error: The Brainfuck program failed: %hs", app.failure);
        console_line(STD_ERROR_HANDLE, message);
        result = 1;
    } else {
        result = (int)app.exit_code;
    }

    /* like the .NET host's process-exit handler */
    guard_emergency_stop(app.guard);
    if (app.event_source) {
        DeregisterEventSource(app.event_source);
    }

    return result;
}
