/*
 * Diagnostic only: like diprobe, through the ANSI DirectInput interface and
 * with COM initialized (the way some games open devices). Creates no effects.
 * Not shipped.
 */
#define CINTERFACE
#define COBJMACROS
#define DIRECTINPUT_VERSION 0x0800
#define INITGUID
#include <windows.h>
#include <objbase.h>
#include <dinput.h>
#include <stdio.h>
#include <string.h>

static IDirectInputDevice8A *found;

static int driver_loaded(void)
{
    return GetModuleHandleW(L"g29ffb64.dll") != NULL;
}

static BOOL CALLBACK each_device(LPCDIDEVICEINSTANCEA instance, LPVOID context)
{
    IDirectInput8A *di = (IDirectInput8A *)context;
    if (instance->guidProduct.Data1 == 0xC24F046D && !found) {
        HRESULT hr = IDirectInput8_CreateDevice(di, &instance->guidInstance, &found, NULL);
        printf("device %s: CreateDevice 0x%08lX, driver loaded %d\n", instance->tszProductName, (unsigned long)hr, driver_loaded());
    }

    return DIENUM_CONTINUE;
}

int main(int argc, char **argv)
{
    IDirectInput8A *di;
    DIDEVCAPS caps;
    DIPROPDWORD autocenter;
    HWND window;
    HRESULT hr;
    int com = argc > 1 && strcmp(argv[1], "--com") == 0;
    /* --nocaps: go straight to the properties, as some games do */
    int nocaps = argc > 1 && strcmp(argv[argc - 1], "--nocaps") == 0;
    if (com) {
        hr = CoInitializeEx(NULL, COINIT_APARTMENTTHREADED);
        printf("CoInitializeEx(STA) 0x%08lX\n", (unsigned long)hr);
    }

    hr = DirectInput8Create(GetModuleHandleW(NULL), DIRECTINPUT_VERSION, &IID_IDirectInput8A, (void **)&di, NULL);
    printf("DirectInput8Create(A) 0x%08lX\n", (unsigned long)hr);
    if (FAILED(hr)) {
        return 1;
    }

    IDirectInput8_EnumDevices(di, DI8DEVCLASS_GAMECTRL, each_device, di, DIEDFL_ATTACHEDONLY);
    if (!found) {
        printf("no G29\n");
        return 1;
    }

    memset(&caps, 0, sizeof(caps));
    caps.dwSize = sizeof(caps);
    hr = nocaps ? S_FALSE : IDirectInputDevice8_GetCapabilities(found, &caps);
    if (nocaps) {
        caps.dwFlags = 0;
        caps.dwFFDriverVersion = 0;
    }

    printf("GetCapabilities 0x%08lX forcefeedback %d ffDriverVersion 0x%08lX, driver loaded %d\n", (unsigned long)hr, (caps.dwFlags & DIDC_FORCEFEEDBACK) != 0, caps.dwFFDriverVersion, driver_loaded());
    window = CreateWindowExA(0, "STATIC", "diprobea", WS_OVERLAPPEDWINDOW, 0, 0, 100, 100, NULL, NULL, GetModuleHandleW(NULL), NULL);
    hr = IDirectInputDevice8_SetDataFormat(found, &c_dfDIJoystick2);
    printf("SetDataFormat 0x%08lX\n", (unsigned long)hr);
    hr = IDirectInputDevice8_SetCooperativeLevel(found, window, DISCL_EXCLUSIVE | DISCL_BACKGROUND);
    printf("SetCooperativeLevel 0x%08lX\n", (unsigned long)hr);
    memset(&autocenter, 0, sizeof(autocenter));
    autocenter.diph.dwSize = sizeof(autocenter);
    autocenter.diph.dwHeaderSize = sizeof(DIPROPHEADER);
    autocenter.diph.dwHow = DIPH_DEVICE;
    autocenter.dwData = DIPROPAUTOCENTER_OFF;
    hr = IDirectInputDevice8_SetProperty(found, DIPROP_AUTOCENTER, &autocenter.diph);
    printf("SetProperty(AUTOCENTER off) 0x%08lX, driver loaded %d\n", (unsigned long)hr, driver_loaded());
    {
        DIPROPDWORD buffer;
        memset(&buffer, 0, sizeof(buffer));
        buffer.diph.dwSize = sizeof(buffer);
        buffer.diph.dwHeaderSize = sizeof(DIPROPHEADER);
        buffer.diph.dwHow = DIPH_DEVICE;
        buffer.dwData = 64;
        hr = IDirectInputDevice8_SetProperty(found, DIPROP_BUFFERSIZE, &buffer.diph);
        printf("SetProperty(BUFFERSIZE 64) 0x%08lX\n", (unsigned long)hr);
    }
    IDirectInputDevice8_Release(found);
    IDirectInput8_Release(di);
    return 0;
}
