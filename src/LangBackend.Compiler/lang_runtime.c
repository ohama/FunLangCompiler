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
