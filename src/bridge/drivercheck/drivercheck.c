/*
 * Test-only: loads g29ffb64.dll or g29ffb32.dll (matching this process) without
 * registration, creates the effect driver through its class factory and checks
 * the calls that need no device. Prints PASS or the first failure. Not shipped.
 */
#define CINTERFACE
#define COBJMACROS
#define DIRECTINPUT_VERSION 0x0800
#define INITGUID
#include <windows.h>
#include <objbase.h>
#include <dinput.h>
#include <dinputd.h>
#include <stdio.h>
#include <string.h>

static const CLSID class_id = { 0xD252A2D4, 0xA917, 0x47D3, { 0xBD, 0x1B, 0xF5, 0xA0, 0x13, 0x8C, 0xFE, 0x12 } };

typedef HRESULT (__stdcall *get_class_object_fn)(REFCLSID, REFIID, void **);

#define CHECK(condition, name) do { if (!(condition)) { printf("FAIL %s\n", name); return 1; } } while (0)

int wmain(int argc, wchar_t **argv)
{
    HMODULE module;
    get_class_object_fn get_class_object;
    IClassFactory *factory = NULL;
    IDirectInputEffectDriver *driver = NULL;
    DIDRIVERVERSIONS versions;
    DIDEVICESTATE state;
    DIHIDFFINITINFO info;
    DWORD handle = 0, status = 0;
    unsigned char buffer[64];
    DIEFFECT effect;
    DICONSTANTFORCE constant;
    if (argc < 2) {
        printf("usage: drivercheck <dll>\n");
        return 2;
    }

    module = LoadLibraryW(argv[1]);
    CHECK(module != NULL, "load the driver");
    get_class_object = (get_class_object_fn)(void *)GetProcAddress(module, "DllGetClassObject");
    CHECK(get_class_object != NULL, "DllGetClassObject is exported");
    CHECK(GetProcAddress(module, "DllCanUnloadNow") != NULL, "DllCanUnloadNow is exported");
    CHECK(get_class_object(&IID_IUnknown, &IID_IClassFactory, (void **)&factory) == CLASS_E_CLASSNOTAVAILABLE, "only our class");
    CHECK(get_class_object(&class_id, &IID_IClassFactory, (void **)&factory) == S_OK, "class factory");
    CHECK(IClassFactory_CreateInstance(factory, NULL, &IID_IDirectInputEffectDriver, (void **)&driver) == S_OK, "create the driver");

    memset(buffer, 0xEE, sizeof(buffer));
    memcpy(&versions, buffer, sizeof(versions));
    versions.dwSize = sizeof(versions);
    CHECK(IDirectInputEffectDriver_GetVersions(driver, &versions) == S_OK, "versions");
    CHECK(versions.dwFirmwareRevision == 0 && versions.dwHardwareRevision == 0 && versions.dwFFDriverVersion == 0x00010000, "versions written");
    CHECK(IDirectInputEffectDriver_GetVersions(driver, NULL) == DIERR_INVALIDPARAM, "no versions buffer");
    CHECK(IDirectInputEffectDriver_Escape(driver, 0, 0, NULL) == E_NOTIMPL, "escape");
    CHECK(IDirectInputEffectDriver_SetGain(driver, 0, 1) == DIERR_NOTINITIALIZED, "no session");
    state.dwSize = sizeof(state);
    CHECK(IDirectInputEffectDriver_GetForceFeedbackState(driver, 0, &state) == DIERR_NOTINITIALIZED, "no session for the state");
    memset(&effect, 0, sizeof(effect));
    effect.dwSize = sizeof(effect);
    constant.lMagnitude = 5000;
    effect.cbTypeSpecificParams = sizeof(constant);
    effect.lpvTypeSpecificParams = &constant;
    CHECK(IDirectInputEffectDriver_DownloadEffect(driver, 0, 1, &handle, &effect, DIEP_TYPESPECIFICPARAMS) == DIERR_NOTINITIALIZED, "no session for effects");
    CHECK(IDirectInputEffectDriver_GetEffectStatus(driver, 0, 1, &status) == DIERR_NOTINITIALIZED, "no session for a status");
    CHECK(IDirectInputEffectDriver_DeviceID(driver, 0x0800, 0, 1, 0, NULL) == DIERR_INVALIDPARAM, "no DIHIDFFINITINFO");
    memset(&info, 0, sizeof(info));
    info.dwSize = sizeof(info);
    info.pwszDeviceInterface = L"\\\\?\\hid#vid_0000&pid_0000#g29-check-missing#{4d1e55b2-f16f-11cf-88cb-001111000030}";
    CHECK(IDirectInputEffectDriver_DeviceID(driver, 0x0800, 0, 1, 0, &info) == E_FAIL, "a missing device is refused");
    CHECK(IDirectInputEffectDriver_DeviceID(driver, 0x0800, 0, 0, 0, NULL) == S_OK, "DeviceID end");
    IDirectInputEffectDriver_Release(driver);
    IClassFactory_Release(factory);
    printf("PASS %d-bit\n", (int)(sizeof(void *) * 8));
    return 0;
}
