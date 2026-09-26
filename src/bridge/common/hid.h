/*
 * Generic HID mechanism: enumerate every
 * HID interface without filtering, open one by token, write one output report,
 * read a usage's value from input reports, request an input report, read a
 * usage's logical range. Which device is a G29 and what to write is decided by
 * the Brainfuck program.
 */
#ifndef G29_HID_H
#define G29_HID_H

#include <stdint.h>
#include <wchar.h>

#define HID_ERROR_INVALID_HANDLE 6
#define HID_ERROR_INVALID_PARAMETER 87

typedef struct {
    uint32_t token;
    wchar_t *path;
    uint16_t vendor_id;
    uint16_t product_id;
    uint16_t version;
    uint16_t usage_page;
    uint16_t usage;
    int input_length;
    int output_length;
    wchar_t product[256];
} hid_interface;

typedef void (*hid_each_fn)(void *context, const hid_interface *item);
/* Called with each extracted value, or with ended = 1 once when a reader fails. */
typedef void (*hid_value_fn)(void *context, uint32_t token, uint32_t value, int ended);

typedef struct hid_bridge hid_bridge;

hid_bridge *hid_create(void);
void hid_free(hid_bridge *hid);

/* Enumerates every inspectable interface (tokens are never reused). Returns 0
   or a Win32 error (then nothing was reported). */
int hid_enumerate(hid_bridge *hid, hid_each_fn each, void *context);
/* Inspects one path and gives it a token; 0 when it cannot be inspected. */
uint32_t hid_inspect_path(hid_bridge *hid, const wchar_t *path, hid_interface *out);
/* Copies the interface of a token; 0 when unknown. */
int hid_find(hid_bridge *hid, uint32_t token, hid_interface *out);

int hid_open(hid_bridge *hid, uint32_t token);
void hid_close(hid_bridge *hid, uint32_t token);
int hid_write(hid_bridge *hid, uint32_t token, const uint8_t *payload, size_t length);
/* Opens, writes one report and closes (for the emergency guard). */
int hid_write_once(const wchar_t *path, int output_length, const uint8_t *payload, size_t length);

int hid_value_caps(hid_bridge *hid, uint32_t token, uint16_t usage_page, uint16_t usage, int32_t *minimum, int32_t *maximum, int *bit_size);
int hid_start_reading(hid_bridge *hid, uint32_t token, uint16_t usage_page, uint16_t usage, hid_value_fn each, void *context);
void hid_stop_reading(hid_bridge *hid, uint32_t token);
/* HidD_GetInputReport on a reading token: 0 and the value, or a Win32 error. */
int hid_poll(hid_bridge *hid, uint32_t token, uint32_t *value);

#endif
