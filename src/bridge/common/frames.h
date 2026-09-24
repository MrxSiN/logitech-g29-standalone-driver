/*
 * The framed byte ABI between the bridge and the Brainfuck program
 * (src/brainfuck/ABI.md):
 *   A5 | version | type | flags | sequence u16 LE | length u16 LE | payload
 * Integers are little-endian; text is a u16 byte length and UTF-8 bytes.
 */
#ifndef G29_FRAMES_H
#define G29_FRAMES_H

#include <stddef.h>
#include <stdint.h>
#include <wchar.h>

#define FRAME_MAGIC 0xA5
#define FRAME_VERSION 1
#define FRAME_HEADER 8
#define FRAME_MAX_PAYLOAD 4096

typedef struct {
    uint8_t type;
    uint8_t flags;
    uint16_t sequence;
    uint16_t length;
    uint8_t payload[FRAME_MAX_PAYLOAD];
} g29_frame;

/* Splits the program's output into frames; anything malformed is a violation. */
typedef struct {
    uint8_t header[FRAME_HEADER];
    int header_count;
    int payload_count;
    g29_frame frame;
} frame_parser;

/* Returns 1 when b completes a frame (in parser->frame), 0 when more bytes are
   needed, -1 on a violation (message filled in). */
int frame_parser_push(frame_parser *parser, uint8_t b, char *error, size_t error_length);

/* Encodes a frame into out (FRAME_HEADER + length bytes). */
size_t frame_encode(uint8_t type, uint8_t flags, uint16_t sequence, const uint8_t *payload, size_t length, uint8_t *out);

typedef struct {
    uint8_t data[FRAME_MAX_PAYLOAD];
    size_t length;
    int overflow;
} payload_writer;

void pw_init(payload_writer *w);
void pw_u8(payload_writer *w, unsigned int value);
void pw_u16(payload_writer *w, unsigned int value);
void pw_u32(payload_writer *w, uint32_t value);
void pw_u64(payload_writer *w, uint64_t value);
void pw_bytes(payload_writer *w, const void *bytes, size_t length);
/* UTF-16 text, converted to UTF-8, clipped to max_chars characters (0 = no limit). */
void pw_text(payload_writer *w, const wchar_t *text, size_t max_chars);

typedef struct {
    const uint8_t *data;
    size_t length;
    size_t position;
    int error;
} payload_reader;

void pr_init(payload_reader *r, const uint8_t *data, size_t length);
unsigned int pr_u8(payload_reader *r);
unsigned int pr_u16(payload_reader *r);
uint32_t pr_u32(payload_reader *r);
uint64_t pr_u64(payload_reader *r);
const uint8_t *pr_bytes(payload_reader *r, size_t length);
/* Text as a newly allocated UTF-16 string (free with free()); NULL on error. */
wchar_t *pr_text(payload_reader *r);
/* 1 when the whole payload was read without error. */
int pr_end(payload_reader *r);

#endif
