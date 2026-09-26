/* Framed ABI codec: see frames.h. */
#include "frames.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <windows.h>

int frame_parser_push(frame_parser *parser, uint8_t b, char *error, size_t error_length)
{
    if (parser->header_count < FRAME_HEADER) {
        if (parser->header_count == 0 && b != FRAME_MAGIC) {
            snprintf(error, error_length, "Output byte %02X is not the start of a frame.", b);
            return -1;
        }

        if (parser->header_count == 1 && b != FRAME_VERSION) {
            snprintf(error, error_length, "Output frame uses ABI version %u; version %u is required.", b, FRAME_VERSION);
            return -1;
        }

        parser->header[parser->header_count++] = b;
        if (parser->header_count < FRAME_HEADER) {
            return 0;
        }

        parser->frame.type = parser->header[2];
        parser->frame.flags = parser->header[3];
        if (parser->frame.flags != 0) {
            /* reserved: no command defines a flag, so a set bit is not understood */
            snprintf(error, error_length, "Output frame sets reserved flags %02X.", parser->frame.flags);
            return -1;
        }

        parser->frame.sequence = (uint16_t)(parser->header[4] | (parser->header[5] << 8));
        parser->frame.length = (uint16_t)(parser->header[6] | (parser->header[7] << 8));
        if (parser->frame.length > FRAME_MAX_PAYLOAD) {
            snprintf(error, error_length, "Output frame payload of %u bytes exceeds the limit.", (unsigned int)parser->frame.length);
            return -1;
        }

        parser->payload_count = 0;
        if (parser->frame.length == 0) {
            parser->header_count = 0;
            return 1;
        }

        return 0;
    }

    parser->frame.payload[parser->payload_count++] = b;
    if (parser->payload_count == parser->frame.length) {
        parser->header_count = 0;
        return 1;
    }

    return 0;
}

size_t frame_encode(uint8_t type, uint8_t flags, uint16_t sequence, const uint8_t *payload, size_t length, uint8_t *out)
{
    out[0] = FRAME_MAGIC;
    out[1] = FRAME_VERSION;
    out[2] = type;
    out[3] = flags;
    out[4] = (uint8_t)sequence;
    out[5] = (uint8_t)(sequence >> 8);
    out[6] = (uint8_t)length;
    out[7] = (uint8_t)(length >> 8);
    if (length) {
        memcpy(out + FRAME_HEADER, payload, length);
    }

    return FRAME_HEADER + length;
}

void pw_init(payload_writer *w)
{
    w->length = 0;
    w->overflow = 0;
}

void pw_bytes(payload_writer *w, const void *bytes, size_t length)
{
    if (w->overflow || w->length + length > FRAME_MAX_PAYLOAD) {
        w->overflow = 1;
        return;
    }

    if (length) {
        memcpy(w->data + w->length, bytes, length);
    }

    w->length += length;
}

void pw_u8(payload_writer *w, unsigned int value)
{
    uint8_t b = (uint8_t)value;
    pw_bytes(w, &b, 1);
}

void pw_u16(payload_writer *w, unsigned int value)
{
    uint8_t b[2];
    b[0] = (uint8_t)value;
    b[1] = (uint8_t)(value >> 8);
    pw_bytes(w, b, 2);
}

void pw_u32(payload_writer *w, uint32_t value)
{
    uint8_t b[4];
    int i;
    for (i = 0; i < 4; i++) {
        b[i] = (uint8_t)(value >> (8 * i));
    }

    pw_bytes(w, b, 4);
}

void pw_u64(payload_writer *w, uint64_t value)
{
    uint8_t b[8];
    int i;
    for (i = 0; i < 8; i++) {
        b[i] = (uint8_t)(value >> (8 * i));
    }

    pw_bytes(w, b, 8);
}

void pw_text(payload_writer *w, const wchar_t *text, size_t max_chars)
{
    int chars = text ? (int)wcslen(text) : 0;
    int bytes;
    char *utf8;
    if (max_chars && (size_t)chars > max_chars) {
        chars = (int)max_chars;
    }

    if (chars == 0) {
        pw_u16(w, 0);
        return;
    }

    bytes = WideCharToMultiByte(CP_UTF8, 0, text, chars, NULL, 0, NULL, NULL);
    utf8 = (char *)malloc((size_t)bytes);
    if (!utf8 || bytes > 0xFFFF) {
        free(utf8);
        w->overflow = 1;
        return;
    }

    WideCharToMultiByte(CP_UTF8, 0, text, chars, utf8, bytes, NULL, NULL);
    pw_u16(w, (unsigned int)bytes);
    pw_bytes(w, utf8, (size_t)bytes);
    free(utf8);
}

void pr_init(payload_reader *r, const uint8_t *data, size_t length)
{
    r->data = data;
    r->length = length;
    r->position = 0;
    r->error = 0;
}

const uint8_t *pr_bytes(payload_reader *r, size_t length)
{
    const uint8_t *start;
    if (r->error || r->position + length > r->length) {
        r->error = 1;
        return NULL;
    }

    start = r->data + r->position;
    r->position += length;
    return start;
}

unsigned int pr_u8(payload_reader *r)
{
    const uint8_t *b = pr_bytes(r, 1);
    return b ? b[0] : 0;
}

unsigned int pr_u16(payload_reader *r)
{
    const uint8_t *b = pr_bytes(r, 2);
    return b ? (unsigned int)(b[0] | (b[1] << 8)) : 0;
}

uint32_t pr_u32(payload_reader *r)
{
    const uint8_t *b = pr_bytes(r, 4);
    uint32_t value = 0;
    int i;
    if (!b) {
        return 0;
    }

    for (i = 3; i >= 0; i--) {
        value = (value << 8) | b[i];
    }

    return value;
}

uint64_t pr_u64(payload_reader *r)
{
    const uint8_t *b = pr_bytes(r, 8);
    uint64_t value = 0;
    int i;
    if (!b) {
        return 0;
    }

    for (i = 7; i >= 0; i--) {
        value = (value << 8) | b[i];
    }

    return value;
}

wchar_t *pr_text(payload_reader *r)
{
    unsigned int length = pr_u16(r);
    const uint8_t *bytes = pr_bytes(r, length);
    int chars;
    wchar_t *text;
    if (r->error) {
        return NULL;
    }

    chars = length ? MultiByteToWideChar(CP_UTF8, 0, (const char *)bytes, (int)length, NULL, 0) : 0;
    text = (wchar_t *)calloc((size_t)chars + 1, sizeof(wchar_t));
    if (!text) {
        r->error = 1;
        return NULL;
    }

    if (chars) {
        MultiByteToWideChar(CP_UTF8, 0, (const char *)bytes, (int)length, text, chars);
    }

    return text;
}

int pr_end(payload_reader *r)
{
    return !r->error && r->position == r->length;
}
