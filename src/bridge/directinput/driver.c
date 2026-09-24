/*
 * g29ffb64.dll / g29ffb32.dll: the DirectInput force-feedback effect driver as a
 * native in-process COM server. A mechanical shell (port of G29EffectDriver.cs
 * and DirectInputBridge.cs): every IDirectInputEffectDriver call is serialized
 * to the Brainfuck program (role DIRECTINPUT), which decides the result; the
 * values it returns are copied into the caller's buffers within their declared
 * sizes. The bridge performs the program's generic HID, timer and shared-memory
 * commands. The emergency output guard is the only independent logic.
 */
#define CINTERFACE
#define COBJMACROS
#define DIRECTINPUT_VERSION 0x0800
#define INITGUID
#include <windows.h>
#include <objbase.h>
#include <process.h>
#include <dinput.h>
#include <dinputd.h>
#include <timeapi.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "../common/bfvm.h"
#include "../common/frames.h"
#include "../common/guard.h"
#include "../common/hid.h"
#include "../common/program.h"
#include "../common/session.h"
#include "../common/system.h"

#define ROLE_DIRECTINPUT 3
#define GAME_FORCE_CEILING_PERCENT 100
#define HIGH_RESOLUTION_BELOW_MS 15
#define CALL_TIMEOUT_MS 10000
#define EXIT_TIMEOUT_MS 1000
#define FAILED_HRESULT ((HRESULT)0x80004005L)
#define MAX_OUTPUTS 8
#define MAX_BRIDGES 16

/* DIEP bits that say which DIEFFECT pointers DirectInput filled in. */
#define VALID_DIRECTION 0x040
#define VALID_ENVELOPE 0x080
#define VALID_TYPE_SPECIFIC 0x100
#define TYPE_SPECIFIC_BYTES 24

/* {D252A2D4-A917-47D3-BD1B-F5A0138CFE12}: the class the program registers. */
static const CLSID class_id = { 0xD252A2D4, 0xA917, 0x47D3, { 0xBD, 0x1B, 0xF5, 0xA0, 0x13, 0x8C, 0xFE, 0x12 } };

static HMODULE module_handle;
static volatile LONG object_count;
static volatile LONG traced_calls;

/* Diagnostics: short lines for a debug-output listener (DebugView or similar);
   they cost nothing when nobody listens. */
static void trace(const char *format, ...)
{
    char line[256];
    int length;
    va_list arguments;
    length = snprintf(line, sizeof(line), "g29ffb[%lu]: ", GetCurrentProcessId());
    va_start(arguments, format);
    vsnprintf(line + length, sizeof(line) - (size_t)length, format, arguments);
    va_end(arguments);
    strncat(line, "\n", sizeof(line) - strlen(line) - 1);
    OutputDebugStringA(line);
}

/* ------------------------------------------------------------------------- */
/* Bridge                                                                     */
/* ------------------------------------------------------------------------- */

typedef struct {
    g29_session *session;
    hid_bridge *hid;
    output_guard *guard;
    shared_memory *shm;
    CRITICAL_SECTION call_lock;
    CRITICAL_SECTION lock;
    CONDITION_VARIABLE changed;
    uint16_t next_sequence;
    uint16_t waiting_sequence;
    int has_answer;
    g29_frame answer;
    int failed;
    int disposed;
    HANDLE exited;
    HANDLE timer_wake;
    HANDLE timer_thread;
    int timer_id;
    uint32_t timer_interval;
    int timer_repeat;
    int timer_armed;
    int high_resolution;
} di_bridge;

typedef struct {
    int offset;
    uint32_t value;
} di_output;

static CRITICAL_SECTION bridges_lock;
static di_bridge *bridges[MAX_BRIDGES];

static void post(di_bridge *bridge, uint8_t type, uint16_t sequence, const payload_writer *w)
{
    session_post(bridge->session, type, sequence, w->data, w->length);
}

static void set_high_resolution(di_bridge *bridge, int enabled)
{
    int change;
    EnterCriticalSection(&bridge->lock);
    change = enabled != bridge->high_resolution;
    bridge->high_resolution = enabled;
    LeaveCriticalSection(&bridge->lock);
    if (change) {
        if (enabled) {
            timeBeginPeriod(1);
        } else {
            timeEndPeriod(1);
        }
    }
}

static void on_value(void *context, uint32_t token, uint32_t value, int ended)
{
    di_bridge *bridge = (di_bridge *)context;
    payload_writer w;
    pw_init(&w);
    pw_u32(&w, token);
    if (ended) {
        post(bridge, 0x17, 0, &w);
        return;
    }

    pw_u32(&w, value);
    pw_u64(&w, (uint64_t)clock_microseconds());
    post(bridge, 0x15, 0, &w);
}

#define VIOLATION(...) do { snprintf(error, error_length, __VA_ARGS__); return 1; } while (0)

/* Runs on the program's thread, in the order the program wrote the frames. */
static int on_command(void *context, const g29_frame *frame, char *error, size_t error_length)
{
    di_bridge *bridge = (di_bridge *)context;
    payload_reader r;
    payload_writer w;
    pr_init(&r, frame->payload, frame->length);
    pw_init(&w);
    switch (frame->type) {
    case 0xD0:
        EnterCriticalSection(&bridge->lock);
        if (frame->sequence == 0 || frame->sequence != bridge->waiting_sequence || bridge->has_answer) {
            LeaveCriticalSection(&bridge->lock);
            VIOLATION("CMD_DI_RETURN for a call that is not pending.");
        }

        bridge->answer = *frame;
        bridge->has_answer = 1;
        WakeAllConditionVariable(&bridge->changed);
        LeaveCriticalSection(&bridge->lock);
        return 0;
    case 0x80:
        session_stop(bridge->session);
        SetEvent(bridge->exited);
        return 0;
    case 0x81:
    case 0x84:
        return 0;
    case 0x91: {
        uint32_t token = pr_u32(&r);
        if (!pr_end(&r)) VIOLATION("Malformed open.");
        pw_u32(&w, token);
        pw_u32(&w, (uint32_t)hid_open(bridge->hid, token));
        post(bridge, 0x13, frame->sequence, &w);
        return 0;
    }

    case 0x92: {
        uint32_t token = pr_u32(&r);
        if (!pr_end(&r)) VIOLATION("Malformed close.");
        hid_close(bridge->hid, token);
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
        refused = guard_check(bridge->guard, payload, length);
        if (refused) VIOLATION("%s", refused);
        result = hid_write(bridge->hid, token, payload, length);
        if (result == 0 && hid_find(bridge->hid, token, &target)) {
            guard_written(bridge->guard, target.path, target.output_length, payload, length);
        }

        pw_u32(&w, token);
        pw_u32(&w, (uint32_t)result);
        post(bridge, 0x14, frame->sequence, &w);
        return 0;
    }

    case 0x94: {
        uint32_t token = pr_u32(&r);
        unsigned int page = pr_u16(&r), usage = pr_u16(&r);
        if (!pr_end(&r)) VIOLATION("Malformed read start.");
        pw_u32(&w, token);
        pw_u32(&w, (uint32_t)hid_start_reading(bridge->hid, token, (uint16_t)page, (uint16_t)usage, on_value, bridge));
        post(bridge, 0x13, frame->sequence, &w);
        return 0;
    }

    case 0x95: {
        uint32_t token = pr_u32(&r);
        if (!pr_end(&r)) VIOLATION("Malformed read stop.");
        hid_stop_reading(bridge->hid, token);
        return 0;
    }

    case 0x96: {
        uint32_t token = pr_u32(&r), value = 0;
        int status;
        if (!pr_end(&r)) VIOLATION("Malformed input request.");
        status = hid_poll(bridge->hid, token, &value);
        if (status == 0) {
            payload_writer input;
            pw_init(&input);
            pw_u32(&input, token);
            pw_u32(&input, value);
            pw_u64(&input, (uint64_t)clock_microseconds());
            post(bridge, 0x15, frame->sequence, &input);
        }

        pw_u32(&w, token);
        pw_u32(&w, (uint32_t)status);
        post(bridge, 0x18, frame->sequence, &w);
        return 0;
    }

    case 0x97: {
        uint32_t token = pr_u32(&r);
        unsigned int page = pr_u16(&r), usage = pr_u16(&r);
        int32_t minimum, maximum;
        int bits, status;
        if (!pr_end(&r)) VIOLATION("Malformed usage request.");
        status = hid_value_caps(bridge->hid, token, (uint16_t)page, (uint16_t)usage, &minimum, &maximum, &bits);
        pw_u32(&w, token);
        pw_u32(&w, (uint32_t)status);
        pw_u32(&w, (uint32_t)minimum);
        pw_u32(&w, (uint32_t)maximum);
        pw_u16(&w, (unsigned int)bits);
        post(bridge, 0x16, frame->sequence, &w);
        return 0;
    }

    case 0xA0: {
        unsigned int id = pr_u8(&r);
        int repeat = pr_u8(&r) != 0;
        uint32_t interval = pr_u32(&r);
        if (!pr_end(&r)) VIOLATION("Malformed timer.");
        EnterCriticalSection(&bridge->lock);
        bridge->timer_id = (int)id;
        bridge->timer_repeat = repeat;
        bridge->timer_interval = interval;
        bridge->timer_armed = 1;
        LeaveCriticalSection(&bridge->lock);
        set_high_resolution(bridge, interval < HIGH_RESOLUTION_BELOW_MS);
        SetEvent(bridge->timer_wake);
        return 0;
    }

    case 0xA1: {
        unsigned int id = pr_u8(&r);
        if (!pr_end(&r)) VIOLATION("Malformed timer cancel.");
        EnterCriticalSection(&bridge->lock);
        if (bridge->timer_id == (int)id) {
            bridge->timer_armed = 0;
        }

        LeaveCriticalSection(&bridge->lock);
        set_high_resolution(bridge, 0);
        SetEvent(bridge->timer_wake);
        return 0;
    }

    case 0xC9: {
        wchar_t *name = pr_text(&r);
        uint32_t size = pr_u32(&r), handle = 0;
        int status;
        if (!name || !pr_end(&r)) { free(name); VIOLATION("Malformed shared-memory request."); }
        if (size == 0 || size > 65536) { free(name); VIOLATION("Shared memory size out of range."); }
        status = shm_open(bridge->shm, name, size, &handle);
        free(name);
        pw_u32(&w, (uint32_t)status);
        pw_u32(&w, handle);
        post(bridge, 0x33, frame->sequence, &w);
        return 0;
    }

    case 0xCB: {
        uint32_t handle = pr_u32(&r), offset = pr_u32(&r);
        unsigned int length = pr_u16(&r);
        const uint8_t *data = pr_bytes(&r, length);
        if (!pr_end(&r)) VIOLATION("Malformed shared-memory write.");
        if (shm_write(bridge->shm, handle, offset, data, length) != 0) VIOLATION("Shared memory write outside the mapping.");
        return 0;
    }

    case 0xCC: {
        uint32_t handle = pr_u32(&r);
        if (!pr_end(&r)) VIOLATION("Malformed shared-memory close.");
        shm_close(bridge->shm, handle);
        return 0;
    }

    default:
        VIOLATION("Unknown command frame type %02X.", frame->type);
    }
}

static void on_failure(void *context, const char *message)
{
    di_bridge *bridge = (di_bridge *)context;
    trace("program failure: %s", message);
    OutputDebugStringA("g29ffb: the Brainfuck program failed: ");
    OutputDebugStringA(message);
    OutputDebugStringA("\n");
    EnterCriticalSection(&bridge->lock);
    bridge->failed = 1;
    bridge->timer_armed = 0;
    WakeAllConditionVariable(&bridge->changed);
    LeaveCriticalSection(&bridge->lock);
    SetEvent(bridge->timer_wake);
    guard_emergency_stop(bridge->guard);
}

/* One timer, as the program uses it: a sleep loop so short intervals keep their
   pace. A tick is skipped while the program still has unread input, so a slow
   program is never buried under stale ticks. */
static unsigned __stdcall timer_loop(void *argument)
{
    di_bridge *bridge = (di_bridge *)argument;
    for (;;) {
        int id, repeat, armed, fire = 0;
        uint32_t interval;
        EnterCriticalSection(&bridge->lock);
        if (bridge->disposed || bridge->failed) {
            LeaveCriticalSection(&bridge->lock);
            return 0;
        }

        id = bridge->timer_id;
        interval = bridge->timer_interval;
        repeat = bridge->timer_repeat;
        armed = bridge->timer_armed;
        LeaveCriticalSection(&bridge->lock);
        if (!armed) {
            WaitForSingleObject(bridge->timer_wake, INFINITE);
            continue;
        }

        if (WaitForSingleObject(bridge->timer_wake, interval) == WAIT_OBJECT_0) {
            /* re-armed or cancelled: start over with the new settings */
            continue;
        }

        EnterCriticalSection(&bridge->lock);
        if (bridge->timer_armed && bridge->timer_id == id && !bridge->disposed && !bridge->failed) {
            fire = 1;
            if (!repeat) {
                bridge->timer_armed = 0;
            }
        }

        LeaveCriticalSection(&bridge->lock);
        if (fire && session_pending_bytes(bridge->session) == 0) {
            payload_writer w;
            pw_init(&w);
            pw_u8(&w, (unsigned int)id);
            pw_u64(&w, (uint64_t)clock_microseconds());
            pw_u64(&w, (uint64_t)clock_tick_milliseconds());
            post(bridge, 0x02, 0, &w);
        }
    }
}

static di_bridge *bridge_create(void)
{
    char error[256];
    const bf_program *program = program_shared(module_handle, error, sizeof(error));
    di_bridge *bridge;
    payload_writer boot;
    int index;
    if (!program) {
        trace("the program could not be loaded: %s", error);
        return NULL;
    }

    bridge = (di_bridge *)calloc(1, sizeof(di_bridge));
    if (!bridge) {
        return NULL;
    }

    InitializeCriticalSection(&bridge->call_lock);
    InitializeCriticalSection(&bridge->lock);
    InitializeConditionVariable(&bridge->changed);
    bridge->next_sequence = 1;
    bridge->hid = hid_create();
    bridge->guard = guard_create(GAME_FORCE_CEILING_PERCENT);
    bridge->shm = shm_create_table();
    bridge->exited = CreateEventW(NULL, TRUE, FALSE, NULL);
    bridge->timer_wake = CreateEventW(NULL, FALSE, FALSE, NULL);
    bridge->session = session_create(program, on_command, on_failure, bridge, BF_DEFAULT_STEP_BUDGET);
    if (!bridge->hid || !bridge->guard || !bridge->shm || !bridge->exited || !bridge->timer_wake || !bridge->session) {
        /* a partly built bridge is left for the process to reclaim */
        trace("the bridge could not be created");
        return NULL;
    }

    pw_init(&boot);
    pw_u8(&boot, ROLE_DIRECTINPUT);
    pw_u8(&boot, (unsigned int)sizeof(void *));
    pw_u8(&boot, sizeof(void *) == 8 ? 9 : 0);
    pw_u8(&boot, 0);
    pw_u16(&boot, 0);
    session_post(bridge->session, 0x01, 0, boot.data, boot.length);
    if (!session_start(bridge->session)) {
        trace("the program thread could not be started");
        return NULL;
    }

    trace("program started");

    bridge->timer_thread = (HANDLE)_beginthreadex(NULL, 0, timer_loop, bridge, 0, NULL);
    EnterCriticalSection(&bridges_lock);
    for (index = 0; index < MAX_BRIDGES; index++) {
        if (!bridges[index]) {
            bridges[index] = bridge;
            break;
        }
    }

    LeaveCriticalSection(&bridges_lock);
    return bridge;
}

/* One COM call: event type and arguments (after the timestamp). Returns the
   program's HRESULT and its outputs, or E_FAIL when the program failed or did
   not answer. */
static HRESULT bridge_call(di_bridge *bridge, uint8_t type, const payload_writer *arguments, di_output *outputs, int *output_count)
{
    payload_writer w;
    payload_reader r;
    uint16_t sequence;
    ULONGLONG deadline;
    HRESULT result;
    unsigned int count, index;
    int answered;
    *output_count = 0;
    EnterCriticalSection(&bridge->call_lock);
    EnterCriticalSection(&bridge->lock);
    if (bridge->failed || bridge->disposed) {
        LeaveCriticalSection(&bridge->lock);
        LeaveCriticalSection(&bridge->call_lock);
        return FAILED_HRESULT;
    }

    sequence = bridge->next_sequence++;
    if (bridge->next_sequence == 0) {
        bridge->next_sequence = 1;
    }

    bridge->waiting_sequence = sequence;
    bridge->has_answer = 0;
    LeaveCriticalSection(&bridge->lock);
    pw_init(&w);
    pw_u64(&w, (uint64_t)clock_microseconds());
    pw_bytes(&w, arguments->data, arguments->length);
    if (w.overflow || arguments->overflow) {
        LeaveCriticalSection(&bridge->call_lock);
        return FAILED_HRESULT;
    }

    post(bridge, type, sequence, &w);
    deadline = GetTickCount64() + CALL_TIMEOUT_MS;
    EnterCriticalSection(&bridge->lock);
    while (!bridge->has_answer && !bridge->failed) {
        ULONGLONG now = GetTickCount64();
        if (now >= deadline) {
            break;
        }

        SleepConditionVariableCS(&bridge->changed, &bridge->lock, (DWORD)(deadline - now));
    }

    answered = bridge->has_answer;
    bridge->waiting_sequence = 0;
    LeaveCriticalSection(&bridge->lock);
    if (!answered) {
        if (!bridge->failed) {
            on_failure(bridge, "The Brainfuck program did not answer a DirectInput call.");
        }

        LeaveCriticalSection(&bridge->call_lock);
        return FAILED_HRESULT;
    }

    pr_init(&r, bridge->answer.payload, bridge->answer.length);
    result = (HRESULT)pr_u32(&r);
    count = pr_u8(&r);
    for (index = 0; index < count; index++) {
        int offset = (int)pr_u16(&r);
        uint32_t value = pr_u32(&r);
        if (index < MAX_OUTPUTS) {
            outputs[index].offset = offset;
            outputs[index].value = value;
        }
    }

    LeaveCriticalSection(&bridge->call_lock);
    if (!pr_end(&r) || count > MAX_OUTPUTS) {
        return FAILED_HRESULT;
    }

    *output_count = (int)count;
    return result;
}

/* ------------------------------------------------------------------------- */
/* COM object                                                                 */
/* ------------------------------------------------------------------------- */

typedef struct {
    IDirectInputEffectDriver iface;
    volatile LONG references;
    CRITICAL_SECTION lock;
    di_bridge *bridge;
} effect_driver;

static di_bridge *driver_bridge(effect_driver *driver)
{
    di_bridge *bridge;
    EnterCriticalSection(&driver->lock);
    if (!driver->bridge) {
        driver->bridge = bridge_create();
    }

    bridge = driver->bridge;
    LeaveCriticalSection(&driver->lock);
    return bridge;
}

/* Sends the call and copies the returned values into output (bounded by
   output_size bytes). A value outside the buffer is a program fault: nothing is
   written and the call fails. */
static HRESULT driver_call(effect_driver *driver, uint8_t type, const payload_writer *arguments, void *output, uint32_t output_size)
{
    di_output outputs[MAX_OUTPUTS];
    int count = 0, index;
    HRESULT result;
    di_bridge *bridge = driver_bridge(driver);
    if (!bridge) {
        return FAILED_HRESULT;
    }

    result = bridge_call(bridge, type, arguments, outputs, &count);
    if (InterlockedIncrement(&traced_calls) <= 200 || FAILED(result)) {
        trace("call %02X -> 0x%08lX", type, (unsigned long)result);
    }

    for (index = 0; index < count; index++) {
        if (!output || (uint64_t)outputs[index].offset + 4 > output_size) {
            return FAILED_HRESULT;
        }
    }

    for (index = 0; index < count; index++) {
        memcpy((uint8_t *)output + outputs[index].offset, &outputs[index].value, 4);
    }

    return result;
}

static HRESULT STDMETHODCALLTYPE driver_query_interface(IDirectInputEffectDriver *self, REFIID riid, void **object)
{
    if (!object) {
        return E_POINTER;
    }

    if (IsEqualIID(riid, &IID_IUnknown) || IsEqualIID(riid, &IID_IDirectInputEffectDriver)) {
        *object = self;
        self->lpVtbl->AddRef(self);
        return S_OK;
    }

    *object = NULL;
    return E_NOINTERFACE;
}

static ULONG STDMETHODCALLTYPE driver_add_ref(IDirectInputEffectDriver *self)
{
    return (ULONG)InterlockedIncrement(&((effect_driver *)self)->references);
}

static ULONG STDMETHODCALLTYPE driver_release(IDirectInputEffectDriver *self)
{
    effect_driver *driver = (effect_driver *)self;
    LONG references = InterlockedDecrement(&driver->references);
    if (references == 0) {
        /* The bridge stays alive with the process (its program may still hold
           a session and threads); the process-exit path stops the wheel. */
        DeleteCriticalSection(&driver->lock);
        free(driver);
        InterlockedDecrement(&object_count);
    }

    return (ULONG)references;
}

static HRESULT STDMETHODCALLTYPE driver_device_id(IDirectInputEffectDriver *self, DWORD version, DWORD external_id, DWORD begin, DWORD internal_id, LPVOID init_info)
{
    effect_driver *driver = (effect_driver *)self;
    uint32_t needed = (uint32_t)sizeof(DIHIDFFINITINFO);
    uint32_t size = 0;
    hid_interface device;
    memset(&device, 0, sizeof(device));
    int inspected = 0;
    payload_writer w;
    __try {
        if (init_info) {
            size = ((DIHIDFFINITINFO *)init_info)->dwSize;
            if (size >= needed && ((DIHIDFFINITINFO *)init_info)->pwszDeviceInterface) {
                di_bridge *bridge = driver_bridge(driver);
                if (bridge && hid_inspect_path(bridge->hid, ((DIHIDFFINITINFO *)init_info)->pwszDeviceInterface, &device)) {
                    inspected = 1;
                }
            }
        }

        pw_init(&w);
        pw_u32(&w, version);
        pw_u32(&w, external_id);
        pw_u32(&w, begin);
        pw_u32(&w, internal_id);
        pw_u8(&w, init_info ? 1 : 0);
        pw_u32(&w, size);
        pw_u32(&w, needed);
        pw_u8(&w, (unsigned int)inspected);
        if (inspected) {
            pw_u32(&w, device.token);
            pw_u16(&w, device.vendor_id);
            pw_u16(&w, device.product_id);
            pw_u16(&w, device.version);
            pw_u16(&w, device.usage_page);
            pw_u16(&w, device.usage);
            pw_u16(&w, (unsigned int)device.input_length);
            pw_u16(&w, (unsigned int)device.output_length);
        } else {
            int index;
            pw_u32(&w, 0);
            for (index = 0; index < 7; index++) {
                pw_u16(&w, 0);
            }
        }

        return driver_call(driver, 0x50, &w, NULL, 0);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return FAILED_HRESULT;
    }
}

static HRESULT STDMETHODCALLTYPE driver_get_versions(IDirectInputEffectDriver *self, LPDIDRIVERVERSIONS versions)
{
    payload_writer w;
    __try {
        uint32_t size = versions ? versions->dwSize : 0;
        pw_init(&w);
        pw_u8(&w, versions ? 1 : 0);
        pw_u32(&w, size);
        return driver_call((effect_driver *)self, 0x51, &w, versions, size);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return FAILED_HRESULT;
    }
}

static HRESULT STDMETHODCALLTYPE driver_escape(IDirectInputEffectDriver *self, DWORD id, DWORD effect, LPDIEFFESCAPE escape)
{
    payload_writer w;
    (void)escape;
    pw_init(&w);
    pw_u32(&w, id);
    pw_u32(&w, effect);
    return driver_call((effect_driver *)self, 0x52, &w, NULL, 0);
}

static HRESULT STDMETHODCALLTYPE driver_set_gain(IDirectInputEffectDriver *self, DWORD id, DWORD gain)
{
    payload_writer w;
    pw_init(&w);
    pw_u32(&w, id);
    pw_u32(&w, gain);
    return driver_call((effect_driver *)self, 0x53, &w, NULL, 0);
}

static HRESULT STDMETHODCALLTYPE driver_command(IDirectInputEffectDriver *self, DWORD id, DWORD command)
{
    payload_writer w;
    pw_init(&w);
    pw_u32(&w, id);
    pw_u32(&w, command);
    return driver_call((effect_driver *)self, 0x54, &w, NULL, 0);
}

static HRESULT STDMETHODCALLTYPE driver_get_state(IDirectInputEffectDriver *self, DWORD id, LPDIDEVICESTATE state)
{
    payload_writer w;
    __try {
        uint32_t size = state ? state->dwSize : 0;
        pw_init(&w);
        pw_u32(&w, id);
        pw_u8(&w, state ? 1 : 0);
        pw_u32(&w, size);
        return driver_call((effect_driver *)self, 0x55, &w, state, size);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return FAILED_HRESULT;
    }
}

/* The DIEFFECT part of EV_DI_DOWNLOAD_EFFECT (src/brainfuck/dieffect.bfa).
   Fields beyond dwSize are never read; pointer members are followed only when
   DirectInput marked them valid. */
static void serialize_effect(const DIEFFECT *effect, DWORD changed, payload_writer *w)
{
    uint32_t dx5 = (uint32_t)sizeof(DIEFFECT_DX5), full = (uint32_t)sizeof(DIEFFECT);
    uint32_t size = effect ? effect->dwSize : 0;
    DIEFFECT fields;
    uint32_t start_delay = 0;
    int direction;
    memset(&fields, 0, sizeof(fields));
    if (effect && size >= full) {
        fields = *effect;
        start_delay = effect->dwStartDelay;
    } else if (effect && size >= dx5) {
        memcpy(&fields, effect, dx5);
    }

    pw_u32(w, changed);
    pw_u8(w, effect ? 1 : 0);
    pw_u32(w, size);
    pw_u32(w, dx5);
    pw_u32(w, full);
    pw_u32(w, fields.dwFlags);
    pw_u32(w, fields.dwDuration);
    pw_u32(w, fields.dwSamplePeriod);
    pw_u32(w, fields.dwGain);
    pw_u32(w, fields.dwTriggerButton);
    pw_u32(w, fields.dwTriggerRepeatInterval);
    pw_u32(w, fields.cAxes);
    direction = (changed & VALID_DIRECTION) && fields.cAxes != 0 && fields.rglDirection;
    pw_u8(w, (unsigned int)direction);
    pw_u32(w, direction ? (uint32_t)fields.rglDirection[0] : 0);
    if ((changed & VALID_ENVELOPE) && fields.lpEnvelope) {
        pw_u8(w, 1);
        pw_u32(w, fields.lpEnvelope->dwAttackLevel);
        pw_u32(w, fields.lpEnvelope->dwAttackTime);
        pw_u32(w, fields.lpEnvelope->dwFadeLevel);
        pw_u32(w, fields.lpEnvelope->dwFadeTime);
    } else {
        pw_u8(w, 0);
        pw_u32(w, 0);
        pw_u32(w, 0);
        pw_u32(w, 0);
        pw_u32(w, 0);
    }

    pw_u32(w, start_delay);
    if ((changed & VALID_TYPE_SPECIFIC) && fields.lpvTypeSpecificParams) {
        uint32_t count = fields.cbTypeSpecificParams < TYPE_SPECIFIC_BYTES ? fields.cbTypeSpecificParams : TYPE_SPECIFIC_BYTES;
        pw_u8(w, 1);
        pw_u32(w, fields.cbTypeSpecificParams);
        pw_u16(w, count);
        pw_bytes(w, fields.lpvTypeSpecificParams, count);
    } else {
        pw_u8(w, 0);
        pw_u32(w, fields.cbTypeSpecificParams);
        pw_u16(w, 0);
    }
}

static HRESULT STDMETHODCALLTYPE driver_download(IDirectInputEffectDriver *self, DWORD id, DWORD effect_id, LPDWORD handle, LPCDIEFFECT effect, DWORD flags)
{
    payload_writer w;
    __try {
        pw_init(&w);
        pw_u32(&w, id);
        pw_u32(&w, effect_id);
        pw_u8(&w, handle ? 1 : 0);
        pw_u32(&w, handle ? *handle : 0);
        serialize_effect(effect, flags, &w);
        return driver_call((effect_driver *)self, 0x56, &w, handle, handle ? 4 : 0);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return FAILED_HRESULT;
    }
}

static HRESULT STDMETHODCALLTYPE driver_destroy(IDirectInputEffectDriver *self, DWORD id, DWORD effect)
{
    payload_writer w;
    pw_init(&w);
    pw_u32(&w, id);
    pw_u32(&w, effect);
    return driver_call((effect_driver *)self, 0x57, &w, NULL, 0);
}

static HRESULT STDMETHODCALLTYPE driver_start(IDirectInputEffectDriver *self, DWORD id, DWORD effect, DWORD mode, DWORD count)
{
    payload_writer w;
    pw_init(&w);
    pw_u32(&w, id);
    pw_u32(&w, effect);
    pw_u32(&w, mode);
    pw_u32(&w, count);
    return driver_call((effect_driver *)self, 0x58, &w, NULL, 0);
}

static HRESULT STDMETHODCALLTYPE driver_stop(IDirectInputEffectDriver *self, DWORD id, DWORD effect)
{
    payload_writer w;
    pw_init(&w);
    pw_u32(&w, id);
    pw_u32(&w, effect);
    return driver_call((effect_driver *)self, 0x59, &w, NULL, 0);
}

static HRESULT STDMETHODCALLTYPE driver_status(IDirectInputEffectDriver *self, DWORD id, DWORD effect, LPDWORD status)
{
    payload_writer w;
    __try {
        pw_init(&w);
        pw_u32(&w, id);
        pw_u32(&w, effect);
        pw_u8(&w, status ? 1 : 0);
        return driver_call((effect_driver *)self, 0x5A, &w, status, status ? 4 : 0);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return FAILED_HRESULT;
    }
}

static IDirectInputEffectDriverVtbl driver_vtable = {
    driver_query_interface,
    driver_add_ref,
    driver_release,
    driver_device_id,
    driver_get_versions,
    driver_escape,
    driver_set_gain,
    driver_command,
    driver_get_state,
    driver_download,
    driver_destroy,
    driver_start,
    driver_stop,
    driver_status
};

/* ------------------------------------------------------------------------- */
/* Class factory and DLL entry points                                         */
/* ------------------------------------------------------------------------- */

static HRESULT STDMETHODCALLTYPE factory_query_interface(IClassFactory *self, REFIID riid, void **object)
{
    if (!object) {
        return E_POINTER;
    }

    if (IsEqualIID(riid, &IID_IUnknown) || IsEqualIID(riid, &IID_IClassFactory)) {
        *object = self;
        return S_OK;
    }

    *object = NULL;
    return E_NOINTERFACE;
}

static ULONG STDMETHODCALLTYPE factory_add_ref(IClassFactory *self)
{
    (void)self;
    return 2;
}

static ULONG STDMETHODCALLTYPE factory_release(IClassFactory *self)
{
    (void)self;
    return 1;
}

static HRESULT STDMETHODCALLTYPE factory_create(IClassFactory *self, IUnknown *outer, REFIID riid, void **object)
{
    effect_driver *driver;
    HRESULT result;
    (void)self;
    if (!object) {
        return E_POINTER;
    }

    *object = NULL;
    if (outer) {
        return CLASS_E_NOAGGREGATION;
    }

    driver = (effect_driver *)calloc(1, sizeof(effect_driver));
    if (!driver) {
        return E_OUTOFMEMORY;
    }

    driver->iface.lpVtbl = &driver_vtable;
    driver->references = 1;
    InitializeCriticalSection(&driver->lock);
    InterlockedIncrement(&object_count);
    result = driver_query_interface(&driver->iface, riid, object);
    trace("CreateInstance -> 0x%08lX", (unsigned long)result);
    driver_release(&driver->iface);
    return result;
}

static HRESULT STDMETHODCALLTYPE factory_lock(IClassFactory *self, BOOL lock)
{
    (void)self;
    if (lock) {
        InterlockedIncrement(&object_count);
    } else {
        InterlockedDecrement(&object_count);
    }

    return S_OK;
}

static IClassFactoryVtbl factory_vtable = {
    factory_query_interface,
    factory_add_ref,
    factory_release,
    factory_create,
    factory_lock
};

static IClassFactory factory = { &factory_vtable };

HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID riid, void **object)
{
    if (!object) {
        return E_POINTER;
    }

    *object = NULL;
    if (!IsEqualCLSID(clsid, &class_id)) {
        trace("DllGetClassObject for another class");
        return CLASS_E_CLASSNOTAVAILABLE;
    }

    trace("DllGetClassObject");

    return factory_query_interface(&factory, riid, object);
}

/* The program's session and threads live until the process ends. */
HRESULT __stdcall DllCanUnloadNow(void)
{
    return S_FALSE;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    int index;
    switch (reason) {
    case DLL_PROCESS_ATTACH:
        module_handle = instance;
        trace("loaded (%d-bit)", (int)(sizeof(void *) * 8));
        InitializeCriticalSection(&bridges_lock);
        DisableThreadLibraryCalls(instance);
        break;
    case DLL_PROCESS_DETACH:
        trace("unloaded (%s)", reserved ? "process exit" : "FreeLibrary");
        if (reserved) {
            /* Process exit: the program's thread is already gone, so the guard
               alone makes sure no wheel keeps a force. */
            for (index = 0; index < MAX_BRIDGES; index++) {
                if (bridges[index]) {
                    guard_emergency_stop(bridges[index]->guard);
                }
            }
        }

        break;
    default:
        break;
    }

    return TRUE;
}

#ifdef G29_DRIVER_TEST
/* Test build only (g29drivertest.dll): the DIEFFECT serialization, so the test
   suite can check it against the frozen reader vectors. */
__declspec(dllexport) int __stdcall G29TestSerializeEffect(const DIEFFECT *effect, DWORD changed, uint8_t *out, int capacity)
{
    payload_writer w;
    pw_init(&w);
    serialize_effect(effect, changed, &w);
    if (w.overflow || (int)w.length > capacity) {
        return -1;
    }

    memcpy(out, w.data, w.length);
    return (int)w.length;
}
#endif
