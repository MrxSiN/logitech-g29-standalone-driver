/* Generic HID mechanism: see hid.h. */
#include "hid.h"

#include <windows.h>
#include <process.h>
#include <setupapi.h>
#include <hidsdi.h>
#include <hidpi.h>
#include <stdlib.h>
#include <string.h>

#define ERROR_GEN_FAILURE_CODE 31
#define READ_WAIT_MILLISECONDS 100

typedef struct hid_reader {
    HANDLE handle;
    HANDLE poll_handle;
    HANDLE event;
    HANDLE thread;
    PHIDP_PREPARSED_DATA preparsed;
    int report_length;
    uint16_t usage_page;
    uint16_t usage;
    uint32_t token;
    volatile LONG stopping;
    hid_value_fn each;
    void *context;
} hid_reader;

typedef struct {
    hid_interface info;
    HANDLE open;
    hid_reader *reader;
} hid_entry;

struct hid_bridge {
    CRITICAL_SECTION lock;
    hid_entry *entries;
    size_t count;
    size_t capacity;
    uint32_t next_token;
};

hid_bridge *hid_create(void)
{
    hid_bridge *hid = (hid_bridge *)calloc(1, sizeof(hid_bridge));
    if (hid) {
        InitializeCriticalSection(&hid->lock);
        hid->next_token = 1;
    }

    return hid;
}

static void reader_free(hid_reader *reader);

void hid_free(hid_bridge *hid)
{
    size_t index;
    if (!hid) {
        return;
    }

    for (index = 0; index < hid->count; index++) {
        if (hid->entries[index].reader) {
            reader_free(hid->entries[index].reader);
        }

        if (hid->entries[index].open) {
            CloseHandle(hid->entries[index].open);
        }

        free(hid->entries[index].info.path);
    }

    free(hid->entries);
    DeleteCriticalSection(&hid->lock);
    free(hid);
}

static hid_entry *find_entry(hid_bridge *hid, uint32_t token)
{
    size_t index;
    for (index = 0; index < hid->count; index++) {
        if (hid->entries[index].info.token == token) {
            return &hid->entries[index];
        }
    }

    return NULL;
}

static HANDLE open_path(const wchar_t *path, DWORD access, DWORD flags)
{
    return CreateFileW(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, flags, NULL);
}

static uint32_t inspect(hid_bridge *hid, const wchar_t *path, hid_interface *out)
{
    HANDLE device = open_path(path, 0, 0);
    HIDD_ATTRIBUTES attributes;
    PHIDP_PREPARSED_DATA preparsed;
    HIDP_CAPS caps;
    hid_entry entry;
    uint32_t token;
    if (device == INVALID_HANDLE_VALUE) {
        return 0;
    }

    memset(&attributes, 0, sizeof(attributes));
    attributes.Size = sizeof(attributes);
    if (!HidD_GetAttributes(device, &attributes) || !HidD_GetPreparsedData(device, &preparsed)) {
        CloseHandle(device);
        return 0;
    }

    if (HidP_GetCaps(preparsed, &caps) != HIDP_STATUS_SUCCESS) {
        HidD_FreePreparsedData(preparsed);
        CloseHandle(device);
        return 0;
    }

    HidD_FreePreparsedData(preparsed);
    memset(&entry, 0, sizeof(entry));
    if (!HidD_GetProductString(device, entry.info.product, sizeof(entry.info.product) - sizeof(wchar_t))) {
        entry.info.product[0] = 0;
    }

    CloseHandle(device);
    entry.info.path = _wcsdup(path);
    if (!entry.info.path) {
        return 0;
    }

    entry.info.vendor_id = attributes.VendorID;
    entry.info.product_id = attributes.ProductID;
    entry.info.version = attributes.VersionNumber;
    entry.info.usage_page = caps.UsagePage;
    entry.info.usage = caps.Usage;
    entry.info.input_length = caps.InputReportByteLength;
    entry.info.output_length = caps.OutputReportByteLength;
    EnterCriticalSection(&hid->lock);
    if (hid->count == hid->capacity) {
        size_t capacity = hid->capacity ? hid->capacity * 2 : 32;
        hid_entry *entries = (hid_entry *)realloc(hid->entries, capacity * sizeof(hid_entry));
        if (!entries) {
            LeaveCriticalSection(&hid->lock);
            free(entry.info.path);
            return 0;
        }

        hid->entries = entries;
        hid->capacity = capacity;
    }

    token = hid->next_token++;
    entry.info.token = token;
    hid->entries[hid->count++] = entry;
    if (out) {
        *out = entry.info;
    }

    LeaveCriticalSection(&hid->lock);
    return token;
}

int hid_enumerate(hid_bridge *hid, hid_each_fn each, void *context)
{
    GUID guid;
    HDEVINFO set;
    DWORD index;
    HidD_GetHidGuid(&guid);
    set = SetupDiGetClassDevsW(&guid, NULL, NULL, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
    if (set == INVALID_HANDLE_VALUE) {
        return (int)GetLastError();
    }

    for (index = 0;; index++) {
        SP_DEVICE_INTERFACE_DATA data;
        DWORD required = 0;
        PSP_DEVICE_INTERFACE_DETAIL_DATA_W detail;
        hid_interface item;
        memset(&data, 0, sizeof(data));
        data.cbSize = sizeof(data);
        if (!SetupDiEnumDeviceInterfaces(set, NULL, &guid, index, &data)) {
            DWORD error = GetLastError();
            SetupDiDestroyDeviceInfoList(set);
            return error == ERROR_NO_MORE_ITEMS ? 0 : (int)error;
        }

        SetupDiGetDeviceInterfaceDetailW(set, &data, NULL, 0, &required, NULL);
        detail = (PSP_DEVICE_INTERFACE_DETAIL_DATA_W)calloc(1, required ? required : sizeof(*detail));
        if (!detail) {
            continue;
        }

        detail->cbSize = sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W);
        if (SetupDiGetDeviceInterfaceDetailW(set, &data, detail, required, NULL, NULL) &&
            inspect(hid, detail->DevicePath, &item)) {
            each(context, &item);
        }

        free(detail);
    }
}

uint32_t hid_inspect_path(hid_bridge *hid, const wchar_t *path, hid_interface *out)
{
    if (!path || !*path) {
        return 0;
    }

    return inspect(hid, path, out);
}

int hid_find(hid_bridge *hid, uint32_t token, hid_interface *out)
{
    hid_entry *entry;
    EnterCriticalSection(&hid->lock);
    entry = find_entry(hid, token);
    if (entry && out) {
        *out = entry->info;
    }

    LeaveCriticalSection(&hid->lock);
    return entry != NULL;
}

int hid_open(hid_bridge *hid, uint32_t token)
{
    hid_interface info;
    HANDLE handle;
    hid_entry *entry;
    if (!hid_find(hid, token, &info)) {
        return HID_ERROR_INVALID_HANDLE;
    }

    handle = open_path(info.path, GENERIC_READ | GENERIC_WRITE, 0);
    if (handle == INVALID_HANDLE_VALUE) {
        DWORD error = GetLastError();
        return error ? (int)error : HID_ERROR_INVALID_HANDLE;
    }

    EnterCriticalSection(&hid->lock);
    entry = find_entry(hid, token);
    if (entry->open) {
        CloseHandle(entry->open);
    }

    entry->open = handle;
    LeaveCriticalSection(&hid->lock);
    return 0;
}

void hid_close(hid_bridge *hid, uint32_t token)
{
    hid_entry *entry;
    EnterCriticalSection(&hid->lock);
    entry = find_entry(hid, token);
    if (entry && entry->open) {
        CloseHandle(entry->open);
        entry->open = NULL;
    }

    LeaveCriticalSection(&hid->lock);
}

/* One output report: at offset 0 when the collection's report is exactly the
   payload length, otherwise after a zero report ID with zero padding.
   HidD_SetOutputReport first, WriteFile as the fallback. */
static int write_report(HANDLE handle, int report_length, const uint8_t *payload, size_t length)
{
    uint8_t *report;
    DWORD set_error, written = 0;
    int offset, result = 0;
    if (report_length < 0 || (size_t)report_length < length) {
        return HID_ERROR_INVALID_PARAMETER;
    }

    report = (uint8_t *)calloc(1, report_length ? (size_t)report_length : 1);
    if (!report) {
        return ERROR_NOT_ENOUGH_MEMORY;
    }

    offset = (size_t)report_length == length ? 0 : 1;
    memcpy(report + offset, payload, length);
    if (HidD_SetOutputReport(handle, report, (ULONG)report_length)) {
        free(report);
        return 0;
    }

    set_error = GetLastError();
    if (!WriteFile(handle, report, (DWORD)report_length, &written, NULL) || written != (DWORD)report_length) {
        DWORD write_error = GetLastError();
        DWORD error = write_error ? write_error : set_error;
        result = error ? (int)error : HID_ERROR_INVALID_PARAMETER;
    }

    free(report);
    return result;
}

int hid_write(hid_bridge *hid, uint32_t token, const uint8_t *payload, size_t length)
{
    hid_entry *entry;
    HANDLE handle = NULL;
    int output_length = 0;
    EnterCriticalSection(&hid->lock);
    entry = find_entry(hid, token);
    if (entry && entry->open) {
        handle = entry->open;
        output_length = entry->info.output_length;
    }

    LeaveCriticalSection(&hid->lock);
    if (!handle) {
        return HID_ERROR_INVALID_HANDLE;
    }

    return write_report(handle, output_length, payload, length);
}

int hid_write_once(const wchar_t *path, int output_length, const uint8_t *payload, size_t length)
{
    HANDLE handle = open_path(path, GENERIC_READ | GENERIC_WRITE, 0);
    int result;
    if (handle == INVALID_HANDLE_VALUE) {
        return (int)GetLastError();
    }

    result = write_report(handle, output_length, payload, length);
    CloseHandle(handle);
    return result;
}

int hid_value_caps(hid_bridge *hid, uint32_t token, uint16_t usage_page, uint16_t usage, int32_t *minimum, int32_t *maximum, int *bit_size)
{
    hid_interface info;
    HANDLE device;
    PHIDP_PREPARSED_DATA preparsed;
    HIDP_VALUE_CAPS caps;
    USHORT count = 1;
    NTSTATUS status;
    *minimum = 0;
    *maximum = 0;
    *bit_size = 0;
    if (!hid_find(hid, token, &info)) {
        return HID_ERROR_INVALID_HANDLE;
    }

    device = open_path(info.path, 0, 0);
    if (device == INVALID_HANDLE_VALUE) {
        DWORD error = GetLastError();
        return error ? (int)error : ERROR_GEN_FAILURE_CODE;
    }

    if (!HidD_GetPreparsedData(device, &preparsed)) {
        DWORD error = GetLastError();
        CloseHandle(device);
        return error ? (int)error : ERROR_GEN_FAILURE_CODE;
    }

    status = HidP_GetSpecificValueCaps(HidP_Input, usage_page, 0, usage, &caps, &count, preparsed);
    HidD_FreePreparsedData(preparsed);
    CloseHandle(device);
    if (status != HIDP_STATUS_SUCCESS || count == 0) {
        return ERROR_NOT_FOUND;
    }

    *minimum = caps.LogicalMin;
    *maximum = caps.LogicalMax;
    *bit_size = caps.BitSize;
    return 0;
}

static int extract(hid_reader *reader, uint8_t *report, uint32_t *value)
{
    ULONG raw = 0;
    if (HidP_GetUsageValue(HidP_Input, reader->usage_page, 0, reader->usage, &raw, reader->preparsed, (PCHAR)report, (ULONG)reader->report_length) != HIDP_STATUS_SUCCESS) {
        return 0;
    }

    *value = raw;
    return 1;
}

static unsigned __stdcall reader_thread(void *argument)
{
    hid_reader *reader = (hid_reader *)argument;
    uint8_t *report = (uint8_t *)calloc(1, (size_t)reader->report_length);
    int ended = 0;
    if (!report) {
        ended = 1;
    }

    while (!ended && !reader->stopping) {
        OVERLAPPED overlapped;
        DWORD bytes = 0;
        memset(&overlapped, 0, sizeof(overlapped));
        overlapped.hEvent = reader->event;
        ResetEvent(reader->event);
        if (!ReadFile(reader->handle, report, (DWORD)reader->report_length, NULL, &overlapped) && GetLastError() != ERROR_IO_PENDING) {
            ended = 1;
            break;
        }

        while (WaitForSingleObject(reader->event, READ_WAIT_MILLISECONDS) == WAIT_TIMEOUT) {
            if (reader->stopping) {
                CancelIoEx(reader->handle, &overlapped);
                GetOverlappedResult(reader->handle, &overlapped, &bytes, TRUE);
                free(report);
                return 0;
            }
        }

        if (!GetOverlappedResult(reader->handle, &overlapped, &bytes, FALSE)) {
            ended = 1;
            break;
        }

        if (bytes == (DWORD)reader->report_length) {
            uint32_t value;
            if (extract(reader, report, &value)) {
                reader->each(reader->context, reader->token, value, 0);
            }
        }
    }

    free(report);
    if (ended && !reader->stopping) {
        /* the device went away; this thread may live inside a game process */
        reader->each(reader->context, reader->token, 0, 1);
    }

    return 0;
}

static void reader_free(hid_reader *reader)
{
    InterlockedExchange(&reader->stopping, 1);
    if (reader->thread) {
        CancelIoEx(reader->handle, NULL);
        if (GetCurrentThreadId() != GetThreadId(reader->thread)) {
            WaitForSingleObject(reader->thread, 1000);
        }

        CloseHandle(reader->thread);
    }

    if (reader->poll_handle && reader->poll_handle != INVALID_HANDLE_VALUE) {
        CloseHandle(reader->poll_handle);
    }

    if (reader->handle && reader->handle != INVALID_HANDLE_VALUE) {
        CloseHandle(reader->handle);
    }

    if (reader->event) {
        CloseHandle(reader->event);
    }

    if (reader->preparsed) {
        HidD_FreePreparsedData(reader->preparsed);
    }

    free(reader);
}

int hid_start_reading(hid_bridge *hid, uint32_t token, uint16_t usage_page, uint16_t usage, hid_value_fn each, void *context)
{
    hid_interface info;
    hid_reader *reader;
    HIDP_CAPS caps;
    hid_entry *entry;
    int error = 0;
    if (!hid_find(hid, token, &info)) {
        return HID_ERROR_INVALID_HANDLE;
    }

    hid_stop_reading(hid, token);
    reader = (hid_reader *)calloc(1, sizeof(hid_reader));
    if (!reader) {
        return ERROR_NOT_ENOUGH_MEMORY;
    }

    reader->usage_page = usage_page;
    reader->usage = usage;
    reader->token = token;
    reader->each = each;
    reader->context = context;
    reader->handle = open_path(info.path, GENERIC_READ, FILE_FLAG_OVERLAPPED);
    if (reader->handle == INVALID_HANDLE_VALUE) {
        error = (int)GetLastError();
    } else if (!HidD_GetPreparsedData(reader->handle, &reader->preparsed)) {
        error = (int)GetLastError();
    } else if (HidP_GetCaps(reader->preparsed, &caps) != HIDP_STATUS_SUCCESS) {
        error = ERROR_GEN_FAILURE_CODE;
    } else {
        reader->report_length = caps.InputReportByteLength ? caps.InputReportByteLength : 1;
        /* synchronous handle: HidD_GetInputReport does not take an OVERLAPPED */
        reader->poll_handle = open_path(info.path, GENERIC_READ | GENERIC_WRITE, 0);
        if (reader->poll_handle == INVALID_HANDLE_VALUE) {
            error = (int)GetLastError();
        } else {
            reader->event = CreateEventW(NULL, TRUE, FALSE, NULL);
            if (!reader->event) {
                error = (int)GetLastError();
            }
        }
    }

    if (!error) {
        reader->thread = (HANDLE)_beginthreadex(NULL, 0, reader_thread, reader, 0, NULL);
        if (!reader->thread) {
            error = ERROR_NOT_ENOUGH_MEMORY;
        }
    }

    if (error) {
        reader_free(reader);
        return error ? error : ERROR_GEN_FAILURE_CODE;
    }

    EnterCriticalSection(&hid->lock);
    entry = find_entry(hid, token);
    entry->reader = reader;
    LeaveCriticalSection(&hid->lock);
    return 0;
}

void hid_stop_reading(hid_bridge *hid, uint32_t token)
{
    hid_entry *entry;
    hid_reader *reader = NULL;
    EnterCriticalSection(&hid->lock);
    entry = find_entry(hid, token);
    if (entry) {
        reader = entry->reader;
        entry->reader = NULL;
    }

    LeaveCriticalSection(&hid->lock);
    if (reader) {
        reader_free(reader);
    }
}

int hid_poll(hid_bridge *hid, uint32_t token, uint32_t *value)
{
    hid_entry *entry;
    hid_reader *reader = NULL;
    uint8_t *report;
    int result;
    *value = 0;
    EnterCriticalSection(&hid->lock);
    entry = find_entry(hid, token);
    if (entry) {
        reader = entry->reader;
    }

    LeaveCriticalSection(&hid->lock);
    if (!reader) {
        return HID_ERROR_INVALID_HANDLE;
    }

    report = (uint8_t *)calloc(1, (size_t)reader->report_length);
    if (!report) {
        return ERROR_NOT_ENOUGH_MEMORY;
    }

    if (!HidD_GetInputReport(reader->poll_handle, report, (ULONG)reader->report_length)) {
        DWORD error = GetLastError();
        free(report);
        return error ? (int)error : ERROR_GEN_FAILURE_CODE;
    }

    result = extract(reader, report, value) ? 0 : ERROR_GEN_FAILURE_CODE;
    free(report);
    return result;
}
