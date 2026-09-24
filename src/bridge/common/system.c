/* Generic system mechanisms: see system.h. */
#include "system.h"

#include <windows.h>
#include <sddl.h>
#include <tlhelp32.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

#define ERROR_KEY_HAS_CHILDREN_CODE 1020

int64_t clock_microseconds(void)
{
    static LARGE_INTEGER frequency;
    LARGE_INTEGER now;
    if (!frequency.QuadPart) {
        QueryPerformanceFrequency(&frequency);
    }

    QueryPerformanceCounter(&now);
    return (int64_t)((double)now.QuadPart / (double)frequency.QuadPart * 1000000.0);
}

int64_t clock_tick_milliseconds(void)
{
    return (int64_t)GetTickCount64();
}

void system_host_info(void *module, payload_writer *out)
{
    wchar_t path[MAX_PATH * 4];
    DWORD length = GetModuleFileNameW((HMODULE)module, path, (DWORD)(sizeof(path) / sizeof(path[0])));
    wchar_t *slash;
    if (length == 0 || length >= sizeof(path) / sizeof(path[0])) {
        pw_text(out, L"", 0);
        return;
    }

    slash = wcsrchr(path, L'\\');
    if (slash) {
        slash[1] = 0;
    }

    pw_text(out, path, 0);
}

/* ------------------------------------------------------------------------- */
/* Registry                                                                   */
/* ------------------------------------------------------------------------- */

static void failure(uint8_t type, LONG code, payload_writer *out)
{
    pw_u32(out, (uint32_t)code);
    if (type == 0xB0 || type == 0xB2) {
        pw_u8(out, 0);
    } else if (type == 0xB4) {
        pw_u32(out, 0);
        pw_u32(out, 0);
    }
}

static int registry_read(HKEY hive, REGSAM view, const wchar_t *path, const wchar_t *name, payload_writer *out)
{
    HKEY key;
    DWORD kind = 0, size = 0;
    LONG status = RegOpenKeyExW(hive, path, 0, KEY_READ | view, &key);
    if (status == ERROR_FILE_NOT_FOUND) {
        pw_u32(out, ERROR_FILE_NOT_FOUND);
        pw_u8(out, 0);
        return 0;
    }

    if (status != ERROR_SUCCESS) {
        failure(0xB0, status, out);
        return 0;
    }

    status = RegQueryValueExW(key, name, NULL, &kind, NULL, &size);
    if (status == ERROR_FILE_NOT_FOUND) {
        RegCloseKey(key);
        pw_u32(out, ERROR_FILE_NOT_FOUND);
        pw_u8(out, 0);
        return 0;
    }

    if (status != ERROR_SUCCESS) {
        RegCloseKey(key);
        failure(0xB0, status, out);
        return 0;
    }

    if (kind == REG_SZ || kind == REG_EXPAND_SZ) {
        wchar_t *text = (wchar_t *)calloc((size_t)size / sizeof(wchar_t) + 2, sizeof(wchar_t));
        DWORD read = size;
        if (!text || RegQueryValueExW(key, name, NULL, &kind, (LPBYTE)text, &read) != ERROR_SUCCESS) {
            free(text);
            RegCloseKey(key);
            failure(0xB0, ERROR_NOT_ENOUGH_MEMORY, out);
            return 0;
        }

        /* strings are read like RegistryKey.GetValue: expandable ones expanded */
        if (kind == REG_EXPAND_SZ) {
            DWORD expanded = ExpandEnvironmentStringsW(text, NULL, 0);
            wchar_t *full = (wchar_t *)calloc((size_t)expanded + 1, sizeof(wchar_t));
            if (full && ExpandEnvironmentStringsW(text, full, expanded)) {
                free(text);
                text = full;
            } else {
                free(full);
            }
        }

        pw_u32(out, 0);
        pw_u8(out, 1);
        pw_text(out, text, 1000);
        free(text);
    } else if (kind == REG_DWORD && size == 4) {
        DWORD value = 0, read = 4;
        RegQueryValueExW(key, name, NULL, NULL, (LPBYTE)&value, &read);
        pw_u32(out, 0);
        pw_u8(out, 2);
        pw_u32(out, value);
    } else {
        pw_u32(out, 0);
        pw_u8(out, (kind == REG_BINARY || kind == REG_NONE) ? 3 : 4);
    }

    RegCloseKey(key);
    return 0;
}

int system_registry(uint8_t type, payload_reader *request, payload_writer *out, char *error, size_t error_length)
{
    unsigned int root = pr_u8(request);
    unsigned int view_code = pr_u8(request);
    wchar_t *path = pr_text(request);
    HKEY hive;
    REGSAM view;
    int result = 0;
    if (!path) {
        snprintf(error, error_length, "Malformed registry command.");
        return 1;
    }

    switch (root) {
    case 1: hive = HKEY_LOCAL_MACHINE; break;
    case 2: hive = HKEY_CURRENT_USER; break;
    default:
        free(path);
        snprintf(error, error_length, "Unknown registry root %u.", root);
        return 1;
    }

    switch (view_code) {
    case 0: view = 0; break;
    case 1: view = KEY_WOW64_64KEY; break;
    case 2: view = KEY_WOW64_32KEY; break;
    default:
        free(path);
        snprintf(error, error_length, "Unknown registry view %u.", view_code);
        return 1;
    }

    switch (type) {
    case 0xB0: {
        wchar_t *name = pr_text(request);
        if (!name || !pr_end(request)) {
            free(name);
            snprintf(error, error_length, "Malformed registry read.");
            result = 1;
            break;
        }

        registry_read(hive, view, path, name, out);
        free(name);
        break;
    }

    case 0xB1: {
        wchar_t *name = pr_text(request);
        unsigned int kind = pr_u8(request);
        HKEY key;
        LONG status;
        wchar_t *text = NULL;
        DWORD dword = 0;
        const uint8_t *bytes = NULL;
        unsigned int length = 0;
        if (kind == 1) {
            text = pr_text(request);
        } else if (kind == 2) {
            dword = pr_u32(request);
        } else if (kind == 3) {
            length = pr_u16(request);
            bytes = pr_bytes(request, length);
        } else {
            free(name);
            snprintf(error, error_length, "Unknown registry value kind %u.", kind);
            result = 1;
            break;
        }

        if (!name || !pr_end(request) || (kind == 1 && !text)) {
            free(name);
            free(text);
            snprintf(error, error_length, "Malformed registry write.");
            result = 1;
            break;
        }

        status = RegCreateKeyExW(hive, path, 0, NULL, 0, KEY_WRITE | KEY_READ | view, NULL, &key, NULL);
        if (status == ERROR_SUCCESS) {
            if (kind == 1) {
                status = RegSetValueExW(key, name, 0, REG_SZ, (const BYTE *)text, (DWORD)((wcslen(text) + 1) * sizeof(wchar_t)));
            } else if (kind == 2) {
                status = RegSetValueExW(key, name, 0, REG_DWORD, (const BYTE *)&dword, sizeof(dword));
            } else {
                status = RegSetValueExW(key, name, 0, REG_BINARY, bytes, length);
            }

            RegCloseKey(key);
        }

        pw_u32(out, (uint32_t)status);
        free(name);
        free(text);
        break;
    }

    case 0xB2: {
        HKEY key;
        LONG status;
        int existed;
        if (!pr_end(request)) {
            snprintf(error, error_length, "Malformed registry command.");
            result = 1;
            break;
        }

        status = RegOpenKeyExW(hive, path, 0, KEY_READ | view, &key);
        existed = status == ERROR_SUCCESS;
        if (existed) {
            RegCloseKey(key);
        } else if (status != ERROR_FILE_NOT_FOUND) {
            failure(type, status, out);
            break;
        }

        status = RegCreateKeyExW(hive, path, 0, NULL, 0, KEY_WRITE | KEY_READ | view, NULL, &key, NULL);
        if (status != ERROR_SUCCESS) {
            failure(type, status, out);
            break;
        }

        RegCloseKey(key);
        pw_u32(out, 0);
        pw_u8(out, (unsigned int)existed);
        break;
    }

    case 0xB3: {
        HKEY key;
        LONG status;
        if (!pr_end(request)) {
            snprintf(error, error_length, "Malformed registry command.");
            result = 1;
            break;
        }

        /* a missing key is not an error; only the rights RegDeleteTree needs,
           since keys other software created may deny WRITE_DAC/WRITE_OWNER */
        status = RegOpenKeyExW(hive, path, 0, DELETE | KEY_ENUMERATE_SUB_KEYS | KEY_QUERY_VALUE | KEY_SET_VALUE | view, &key);
        if (status == ERROR_FILE_NOT_FOUND) {
            pw_u32(out, 0);
            break;
        }

        if (status == ERROR_SUCCESS) {
            status = RegDeleteTreeW(key, NULL);
            RegCloseKey(key);
        }

        if (status == ERROR_SUCCESS) {
            status = RegDeleteKeyExW(hive, path, view, 0);
            if (status == ERROR_FILE_NOT_FOUND) {
                status = ERROR_SUCCESS;
            }
        }

        pw_u32(out, (uint32_t)status);
        break;
    }

    case 0xB4: {
        HKEY key;
        DWORD subkeys = 0, values = 0;
        LONG status;
        if (!pr_end(request)) {
            snprintf(error, error_length, "Malformed registry command.");
            result = 1;
            break;
        }

        status = RegOpenKeyExW(hive, path, 0, KEY_READ | view, &key);
        if (status != ERROR_SUCCESS) {
            failure(type, status == ERROR_FILE_NOT_FOUND ? ERROR_FILE_NOT_FOUND : status, out);
            break;
        }

        status = RegQueryInfoKeyW(key, NULL, NULL, NULL, &subkeys, NULL, NULL, &values, NULL, NULL, NULL, NULL);
        RegCloseKey(key);
        if (status != ERROR_SUCCESS) {
            failure(type, status, out);
            break;
        }

        pw_u32(out, 0);
        pw_u32(out, subkeys);
        pw_u32(out, values);
        break;
    }

    case 0xB5: {
        HKEY key;
        DWORD subkeys = 0;
        LONG status;
        if (!pr_end(request)) {
            snprintf(error, error_length, "Malformed registry command.");
            result = 1;
            break;
        }

        /* only an empty key; a missing key is not an error */
        status = RegOpenKeyExW(hive, path, 0, KEY_READ | view, &key);
        if (status == ERROR_FILE_NOT_FOUND) {
            pw_u32(out, 0);
            break;
        }

        if (status != ERROR_SUCCESS) {
            failure(type, status, out);
            break;
        }

        status = RegQueryInfoKeyW(key, NULL, NULL, NULL, &subkeys, NULL, NULL, NULL, NULL, NULL, NULL, NULL);
        RegCloseKey(key);
        if (status == ERROR_SUCCESS && subkeys > 0) {
            status = ERROR_KEY_HAS_CHILDREN_CODE;
        } else if (status == ERROR_SUCCESS) {
            status = RegDeleteKeyExW(hive, path, view, 0);
            if (status == ERROR_FILE_NOT_FOUND) {
                status = ERROR_SUCCESS;
            }
        }

        pw_u32(out, (uint32_t)status);
        break;
    }

    default:
        snprintf(error, error_length, "%02X is not a registry command.", type);
        result = 1;
        break;
    }

    free(path);
    return result;
}

void system_file_info(const wchar_t *path, payload_writer *out)
{
    DWORD attributes = GetFileAttributesW(path);
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY)) {
        pw_u32(out, ERROR_FILE_NOT_FOUND);
    } else {
        pw_u32(out, 0);
    }

    pw_text(out, L"", 0);
    pw_text(out, L"", 0);
}

int system_processes(process_each_fn each, void *context)
{
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    PROCESSENTRY32W entry;
    int count = 0;
    if (snapshot == INVALID_HANDLE_VALUE) {
        return 0;
    }

    memset(&entry, 0, sizeof(entry));
    entry.dwSize = sizeof(entry);
    if (Process32FirstW(snapshot, &entry)) {
        do {
            wchar_t name[MAX_PATH];
            size_t length;
            wcsncpy(name, entry.th32ProcessID == 0 ? L"Idle" : entry.szExeFile, MAX_PATH - 1);
            name[MAX_PATH - 1] = 0;
            length = wcslen(name);
            if (length > 4 && _wcsicmp(name + length - 4, L".exe") == 0) {
                name[length - 4] = 0;
            }

            each(context, entry.th32ProcessID, name);
            count++;
        } while (Process32NextW(snapshot, &entry));
    }

    CloseHandle(snapshot);
    return count;
}

/* ------------------------------------------------------------------------- */
/* Shared memory                                                              */
/* ------------------------------------------------------------------------- */

#define SHM_MAX 8

typedef struct {
    uint32_t handle;
    HANDLE mapping;
    uint8_t *view;
    uint32_t size;
} shm_entry;

struct shared_memory {
    CRITICAL_SECTION lock;
    shm_entry entries[SHM_MAX];
    uint32_t next;
};

shared_memory *shm_create_table(void)
{
    shared_memory *table = (shared_memory *)calloc(1, sizeof(shared_memory));
    if (table) {
        InitializeCriticalSection(&table->lock);
        table->next = 1;
    }

    return table;
}

void shm_free_table(shared_memory *table)
{
    int index;
    if (!table) {
        return;
    }

    for (index = 0; index < SHM_MAX; index++) {
        if (table->entries[index].handle) {
            UnmapViewOfFile(table->entries[index].view);
            CloseHandle(table->entries[index].mapping);
        }
    }

    DeleteCriticalSection(&table->lock);
    free(table);
}

static int shm_add(shared_memory *table, HANDLE mapping, uint32_t size, uint32_t *handle)
{
    int index;
    uint8_t *view = (uint8_t *)MapViewOfFile(mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, size);
    if (!view) {
        DWORD error = GetLastError();
        CloseHandle(mapping);
        return error ? (int)error : ERROR_ACCESS_DENIED;
    }

    EnterCriticalSection(&table->lock);
    for (index = 0; index < SHM_MAX && table->entries[index].handle; index++) {
    }

    if (index == SHM_MAX) {
        LeaveCriticalSection(&table->lock);
        UnmapViewOfFile(view);
        CloseHandle(mapping);
        return ERROR_TOO_MANY_OPEN_FILES;
    }

    table->entries[index].handle = table->next++;
    table->entries[index].mapping = mapping;
    table->entries[index].view = view;
    table->entries[index].size = size;
    *handle = table->entries[index].handle;
    LeaveCriticalSection(&table->lock);
    return 0;
}

int shm_create(shared_memory *table, const wchar_t *name, uint32_t size, int everyone, uint32_t *handle)
{
    SECURITY_ATTRIBUTES attributes;
    PSECURITY_DESCRIPTOR descriptor = NULL;
    HANDLE mapping;
    *handle = 0;
    memset(&attributes, 0, sizeof(attributes));
    attributes.nLength = sizeof(attributes);
    /* read and write for Everyone (FILE_MAP_READ | FILE_MAP_WRITE) */
    if (everyone && !ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:(A;;0x6;;;WD)", SDDL_REVISION_1, &descriptor, NULL)) {
        return (int)GetLastError();
    }

    attributes.lpSecurityDescriptor = descriptor;
    mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, everyone ? &attributes : NULL, PAGE_READWRITE, 0, size, name);
    if (descriptor) {
        LocalFree(descriptor);
    }

    if (!mapping) {
        DWORD error = GetLastError();
        return error ? (int)error : ERROR_ACCESS_DENIED;
    }

    return shm_add(table, mapping, size, handle);
}

int shm_open(shared_memory *table, const wchar_t *name, uint32_t size, uint32_t *handle)
{
    HANDLE mapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, name);
    *handle = 0;
    if (!mapping) {
        DWORD error = GetLastError();
        return error ? (int)error : ERROR_FILE_NOT_FOUND;
    }

    return shm_add(table, mapping, size, handle);
}

static shm_entry *shm_find(shared_memory *table, uint32_t handle)
{
    int index;
    for (index = 0; index < SHM_MAX; index++) {
        if (handle && table->entries[index].handle == handle) {
            return &table->entries[index];
        }
    }

    return NULL;
}

int shm_read(shared_memory *table, uint32_t handle, uint32_t offset, uint8_t *data, size_t length)
{
    shm_entry *entry;
    int result = -1;
    EnterCriticalSection(&table->lock);
    entry = shm_find(table, handle);
    if (entry && (uint64_t)offset + length <= entry->size) {
        memcpy(data, entry->view + offset, length);
        result = 0;
    }

    LeaveCriticalSection(&table->lock);
    return result;
}

int shm_write(shared_memory *table, uint32_t handle, uint32_t offset, const uint8_t *data, size_t length)
{
    shm_entry *entry;
    int result = -1;
    EnterCriticalSection(&table->lock);
    entry = shm_find(table, handle);
    if (entry && (uint64_t)offset + length <= entry->size) {
        memcpy(entry->view + offset, data, length);
        result = 0;
    }

    LeaveCriticalSection(&table->lock);
    return result;
}

void shm_close(shared_memory *table, uint32_t handle)
{
    shm_entry *entry;
    EnterCriticalSection(&table->lock);
    entry = shm_find(table, handle);
    if (entry) {
        UnmapViewOfFile(entry->view);
        CloseHandle(entry->mapping);
        memset(entry, 0, sizeof(*entry));
    }

    LeaveCriticalSection(&table->lock);
}
