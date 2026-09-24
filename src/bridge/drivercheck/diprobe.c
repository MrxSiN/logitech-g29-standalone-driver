/*
 * Test/diagnostic only: opens the G29 through real DirectInput the way a game
 * does (exclusive, background), reports what DirectInput and the effect driver
 * answer, and prints the driver's debug messages. It creates NO effects: the
 * only report the driver can send is the zero-force stop report.
 *   diprobe                 (as above)
 *   diprobe --force P       additionally plays a constant force of P percent
 *                           (-25..25, DirectInput units) for one second, then
 *                           stops. The wheel turns: hands clear or holding it.
 * Not shipped.
 */
#define CINTERFACE
#define COBJMACROS
#define DIRECTINPUT_VERSION 0x0800
#define INITGUID
#include <windows.h>
#include <dinput.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static IDirectInputDevice8W *found;

/* The service's heartbeat (tick ms at 0, active flag at 8), or -1. */
static long long heartbeat(int *active)
{
    HANDLE mapping = OpenFileMappingW(FILE_MAP_READ, FALSE, L"Global\\G29Standalone.ForceHeartbeat");
    const unsigned char *view;
    long long beat;
    if (!mapping) {
        return -1;
    }

    view = (const unsigned char *)MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, 16);
    if (!view) {
        CloseHandle(mapping);
        return -1;
    }

    beat = *(const long long *)view;
    *active = *(const int *)(view + 8);
    UnmapViewOfFile(view);
    CloseHandle(mapping);
    return beat;
}
static volatile LONG stop_listening;

/* Captures OutputDebugString text of this process (no debugger attached). */
static DWORD WINAPI debug_listener(LPVOID unused)
{
    HANDLE ready = CreateEventW(NULL, FALSE, FALSE, L"DBWIN_BUFFER_READY");
    HANDLE data = CreateEventW(NULL, FALSE, FALSE, L"DBWIN_DATA_READY");
    HANDLE mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, 4096, L"DBWIN_BUFFER");
    char *view;
    (void)unused;
    if (!ready || !data || !mapping) {
        return 0;
    }

    view = (char *)MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, 4096);
    if (!view) {
        return 0;
    }

    for (;;) {
        SetEvent(ready);
        if (WaitForSingleObject(data, 200) == WAIT_OBJECT_0) {
            if (*(DWORD *)view == GetCurrentProcessId()) {
                printf("  [driver debug] %s", view + sizeof(DWORD));
            }
        } else if (stop_listening) {
            return 0;
        }
    }
}

static BOOL CALLBACK each_device(LPCDIDEVICEINSTANCEW instance, LPVOID context)
{
    IDirectInput8W *di = (IDirectInput8W *)context;
    printf("device: %ls / instance name %ls / product %08lX / instance %08lX-%04X-%04X-%02X%02X / type 0x%08lX\n", instance->tszProductName, instance->tszInstanceName, instance->guidProduct.Data1, instance->guidInstance.Data1, instance->guidInstance.Data2, instance->guidInstance.Data3, instance->guidInstance.Data4[0], instance->guidInstance.Data4[1], instance->dwDevType);
    if (instance->guidProduct.Data1 == 0xC24F046D && !found) {
        HRESULT hr = IDirectInput8_CreateDevice(di, &instance->guidInstance, &found, NULL);
        printf("  CreateDevice 0x%08lX\n", (unsigned long)hr);
    }

    return DIENUM_CONTINUE;
}

static BOOL CALLBACK each_effect(LPCDIEFFECTINFOW info, LPVOID context)
{
    (*(int *)context)++;
    printf("  effect: %ls\n", info->tszName);
    return DIENUM_CONTINUE;
}

int main(int argc, char **argv)
{
    int force_percent = 0;
    IDirectInput8W *di;
    DIDEVCAPS caps;
    DWORD state = 0;
    HWND window;
    HRESULT hr;
    int effects = 0;
    HANDLE listener;
    if (argc == 3 && strcmp(argv[1], "--force") == 0) {
        force_percent = atoi(argv[2]);
        if (force_percent < -25 || force_percent > 25 || force_percent == 0) {
            printf("--force takes -25..25 (not 0)\n");
            return 2;
        }
    }

    listener = CreateThread(NULL, 0, debug_listener, NULL, 0, NULL);
    Sleep(100);
    hr = DirectInput8Create(GetModuleHandleW(NULL), DIRECTINPUT_VERSION, &IID_IDirectInput8W, (void **)&di, NULL);
    printf("DirectInput8Create 0x%08lX\n", (unsigned long)hr);
    if (FAILED(hr)) {
        return 1;
    }

    printf("-- all game controllers --\n");
    IDirectInput8_EnumDevices(di, DI8DEVCLASS_GAMECTRL, each_device, di, DIEDFL_ATTACHEDONLY);
    if (!found) {
        printf("no G29 device (VID 046D PID C24F)\n");
        return 1;
    }

    caps.dwSize = sizeof(caps);
    hr = IDirectInputDevice8_GetCapabilities(found, &caps);
    printf("GetCapabilities 0x%08lX flags 0x%08lX forcefeedback=%d ffDriverVersion 0x%08lX\n", (unsigned long)hr, caps.dwFlags, (caps.dwFlags & DIDC_FORCEFEEDBACK) != 0, caps.dwFFDriverVersion);
    IDirectInputDevice8_EnumEffects(found, each_effect, &effects, DIEFT_ALL);
    printf("effects offered: %d\n", effects);
    window = CreateWindowExW(0, L"STATIC", L"diprobe", WS_OVERLAPPEDWINDOW, 0, 0, 100, 100, NULL, NULL, GetModuleHandleW(NULL), NULL);
    printf("driver loaded after CreateDevice: %d\n", GetModuleHandleW(L"g29ffb64.dll") != NULL);
    hr = IDirectInputDevice8_SetDataFormat(found, &c_dfDIJoystick2);
    printf("SetDataFormat 0x%08lX\n", (unsigned long)hr);
    hr = IDirectInputDevice8_SetCooperativeLevel(found, window, DISCL_EXCLUSIVE | DISCL_BACKGROUND);
    printf("SetCooperativeLevel 0x%08lX, driver loaded: %d\n", (unsigned long)hr, GetModuleHandleW(L"g29ffb64.dll") != NULL);
    {
        /* what a game does before acquiring (autocenter off: no torque) */
        DIPROPDWORD autocenter;
        memset(&autocenter, 0, sizeof(autocenter));
        autocenter.diph.dwSize = sizeof(autocenter);
        autocenter.diph.dwHeaderSize = sizeof(DIPROPHEADER);
        autocenter.diph.dwHow = DIPH_DEVICE;
        autocenter.dwData = DIPROPAUTOCENTER_OFF;
        hr = IDirectInputDevice8_SetProperty(found, DIPROP_AUTOCENTER, &autocenter.diph);
        printf("before Acquire: SetProperty(AUTOCENTER off) 0x%08lX, driver loaded: %d\n", (unsigned long)hr, GetModuleHandleW(L"g29ffb64.dll") != NULL);
    }

    hr = IDirectInputDevice8_Acquire(found);
    printf("Acquire 0x%08lX, driver loaded: %d\n", (unsigned long)hr, GetModuleHandleW(L"g29ffb64.dll") != NULL);
    {
        DIPROPDWORD autocenter;
        memset(&autocenter, 0, sizeof(autocenter));
        autocenter.diph.dwSize = sizeof(autocenter);
        autocenter.diph.dwHeaderSize = sizeof(DIPROPHEADER);
        autocenter.diph.dwHow = DIPH_DEVICE;
        autocenter.dwData = DIPROPAUTOCENTER_OFF;
        hr = IDirectInputDevice8_SetProperty(found, DIPROP_AUTOCENTER, &autocenter.diph);
        printf("after Acquire: SetProperty(AUTOCENTER off) 0x%08lX\n", (unsigned long)hr);
    }
    Sleep(300);
    hr = IDirectInputDevice8_GetForceFeedbackState(found, &state);
    printf("GetForceFeedbackState 0x%08lX state 0x%08lX\n", (unsigned long)hr, state);
    hr = IDirectInputDevice8_SendForceFeedbackCommand(found, DISFFC_RESET);
    printf("SendForceFeedbackCommand(RESET) 0x%08lX\n", (unsigned long)hr);
    hr = IDirectInputDevice8_GetForceFeedbackState(found, &state);
    printf("GetForceFeedbackState 0x%08lX state 0x%08lX\n", (unsigned long)hr, state);
    {
        /* a constant force of magnitude 0: exercises download, start and the
           force loop without any torque */
        DICONSTANTFORCE constant = { 0 };
        DWORD axes[1] = { DIJOFS_X };
        LONG directions[1] = { 0 };
        DIEFFECT effect;
        IDirectInputEffect *zero = NULL;
        long long before, after;
        int active = 0;
        memset(&effect, 0, sizeof(effect));
        effect.dwSize = sizeof(effect);
        effect.dwFlags = DIEFF_CARTESIAN | DIEFF_OBJECTOFFSETS;
        effect.dwDuration = INFINITE;
        effect.dwGain = DI_FFNOMINALMAX;
        effect.dwTriggerButton = DIEB_NOTRIGGER;
        effect.cAxes = 1;
        effect.rgdwAxes = axes;
        effect.rglDirection = directions;
        effect.cbTypeSpecificParams = sizeof(constant);
        effect.lpvTypeSpecificParams = &constant;
        hr = IDirectInputDevice8_CreateEffect(found, &GUID_ConstantForce, &effect, &zero, NULL);
        printf("CreateEffect(constant, magnitude 0) 0x%08lX\n", (unsigned long)hr);
        if (zero) {
            DWORD status = 0;
            hr = IDirectInputEffect_Start(zero, 1, 0);
            printf("Start 0x%08lX\n", (unsigned long)hr);
            before = heartbeat(&active);
            Sleep(500);
            after = heartbeat(&active);
            printf("heartbeat before %lld after %lld (advanced %lld ms) active %d\n", before, after, after - before, active);
            hr = IDirectInputEffect_GetEffectStatus(zero, &status);
            printf("GetEffectStatus 0x%08lX playing %lu\n", (unsigned long)hr, status);
            hr = IDirectInputDevice8_GetForceFeedbackState(found, &state);
            printf("GetForceFeedbackState 0x%08lX state 0x%08lX\n", (unsigned long)hr, state);
            if (force_percent != 0) {
                constant.lMagnitude = force_percent * 100;
                hr = IDirectInputEffect_SetParameters(zero, &effect, DIEP_TYPESPECIFICPARAMS);
                printf("SetParameters(magnitude %ld) 0x%08lX -- force for 1 s\n", constant.lMagnitude, (unsigned long)hr);
                Sleep(250);
                heartbeat(&active);
                printf("heartbeat active while forcing: %d\n", active);
                Sleep(750);
            }

            hr = IDirectInputEffect_Stop(zero);
            printf("Stop 0x%08lX\n", (unsigned long)hr);
            IDirectInputEffect_Release(zero);
        }
    }

    IDirectInputDevice8_Unacquire(found);
    IDirectInputDevice8_Release(found);
    IDirectInput8_Release(di);
    Sleep(300);
    InterlockedExchange(&stop_listening, 1);
    WaitForSingleObject(listener, 1000);
    return 0;
}
