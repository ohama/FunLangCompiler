#include <stdint.h>
#include <string.h>
#include <stdio.h>
#include <stdlib.h>
#include <gc.h>

/* String struct layout matches MLIR {i64 length, ptr data} at offsets 0 and 8 */
typedef struct {
    int64_t length;
    char*   data;
} LangString;

LangString* lang_string_concat(LangString* a, LangString* b) {
    int64_t total = a->length + b->length;
    char* buf = (char*)GC_malloc(total + 1);
    memcpy(buf, a->data, (size_t)a->length);
    memcpy(buf + a->length, b->data, (size_t)b->length);
    buf[total] = '\0';
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = total;
    s->data = buf;
    return s;
}

LangString* lang_to_string_int(int64_t n) {
    char tmp[32];
    int len = snprintf(tmp, sizeof(tmp), "%ld", (long)n);
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    memcpy(buf, tmp, (size_t)(len + 1));
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = (int64_t)len;
    s->data = buf;
    return s;
}

LangString* lang_to_string_bool(int64_t b) {
    const char* str = b ? "true" : "false";
    int64_t len = (int64_t)strlen(str);
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    memcpy(buf, str, (size_t)(len + 1));
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = len;
    s->data = buf;
    return s;
}

void lang_match_failure(void) {
    fprintf(stderr, "Fatal: non-exhaustive match\n");
    exit(1);
}

void lang_failwith(const char* msg) {
    fprintf(stderr, "%s\n", msg);
    exit(1);
}

LangString* lang_string_sub(LangString* s, int64_t start, int64_t len) {
    if (start < 0) start = 0;
    if (start > s->length) start = s->length;
    if (len < 0) len = 0;
    if (start + len > s->length) len = s->length - start;
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    memcpy(buf, s->data + start, (size_t)len);
    buf[len] = '\0';
    LangString* r = (LangString*)GC_malloc(sizeof(LangString));
    r->length = len;
    r->data = buf;
    return r;
}

int64_t lang_string_contains(LangString* s, LangString* sub) {
    if (sub->length == 0) return 1;
    return strstr(s->data, sub->data) != NULL ? 1 : 0;
}

int64_t lang_string_to_int(LangString* s) {
    return (int64_t)strtol(s->data, NULL, 10);
}

/* Cons cell layout: {int64_t head @ offset 0, ConsCell* tail @ offset 8} — 16 bytes total */
/* Matches Phase 10 GC_malloc(16) cons cell layout exactly. */
typedef struct LangCons {
    int64_t         head;
    struct LangCons* tail;
} LangCons;

/* lang_range: build inclusive cons list [start..step..stop].
   step must be non-zero. Returns NULL (empty list) when range is immediately empty. */
LangCons* lang_range(int64_t start, int64_t stop, int64_t step) {
    if (step == 0) {
        fprintf(stderr, "Fatal: range step cannot be zero\n");
        exit(1);
    }
    LangCons* head = NULL;
    LangCons** cursor = &head;
    if (step > 0) {
        for (int64_t i = start; i <= stop; i += step) {
            LangCons* cell = (LangCons*)GC_malloc(sizeof(LangCons));
            cell->head = i;
            cell->tail = NULL;
            *cursor = cell;
            cursor = &cell->tail;
        }
    } else {
        for (int64_t i = start; i >= stop; i += step) {
            LangCons* cell = (LangCons*)GC_malloc(sizeof(LangCons));
            cell->head = i;
            cell->tail = NULL;
            *cursor = cell;
            cursor = &cell->tail;
        }
    }
    return head;
}
