#include <stdint.h>
#include <string.h>
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>
#include <dirent.h>
#include <gc.h>
#include "lang_runtime.h"

/* String struct layout matches MLIR {i64 length, ptr data} at offsets 0 and 8 */
typedef struct LangString_s {
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

/* Exception handling runtime */
/* Design: generated code calls _setjmp/_longjmp directly in the function frame.
 * lang_try_push just inserts the frame into the handler stack (no setjmp here).
 * lang_throw uses _longjmp to jump back to the try-site in the calling function.
 * This avoids the ARM64 PAC/stack-frame issue with out-of-line setjmp wrappers. */
LangExnFrame *lang_exn_top = NULL;
void *lang_current_exception_val = NULL;

/* Push frame onto handler stack. Generated code calls _setjmp on frame->buf
 * after this returns, in the same function that holds the try-with expression. */
void lang_try_push(LangExnFrame *frame) {
    frame->prev = lang_exn_top;
    lang_exn_top = frame;
}

void lang_try_exit(void) {
    if (lang_exn_top != NULL)
        lang_exn_top = lang_exn_top->prev;
}

void lang_throw(void *exn_val) {
    lang_current_exception_val = exn_val;
    if (lang_exn_top == NULL) {
        fprintf(stderr, "Fatal: unhandled exception\n");
        exit(1);
    }
    LangExnFrame *frame = lang_exn_top;
    _longjmp(frame->buf, 1);
}

void *lang_current_exception(void) {
    return lang_current_exception_val;
}

/* Keep lang_try_enter for backward compatibility / tests that compiled against old ABI */
__attribute__((returns_twice))
int lang_try_enter(LangExnFrame *frame) {
    lang_try_push(frame);
    return _setjmp(frame->buf);
}

/* Array runtime functions.
 * One-block layout: GC_malloc((n+1)*8) where arr[0]=n (length), arr[1..n]=elements. */

int64_t* lang_array_create(int64_t n, int64_t default_val) {
    if (n < 0) {
        lang_failwith("array_create: negative length");
    }
    int64_t* arr = (int64_t*)GC_malloc((size_t)((n + 1) * 8));
    arr[0] = n;
    for (int64_t i = 1; i <= n; i++) {
        arr[i] = default_val;
    }
    return arr;
}

void lang_array_bounds_check(int64_t* arr, int64_t i) {
    int64_t len = arr[0];
    if (i < 0 || i >= len) {
        char tmp[64];
        int msglen = snprintf(tmp, sizeof(tmp), "index out of bounds: %ld, length %ld", (long)i, (long)len);
        char* buf = (char*)GC_malloc((size_t)(msglen + 1));
        memcpy(buf, tmp, (size_t)(msglen + 1));
        LangString* msg = (LangString*)GC_malloc(sizeof(LangString));
        msg->length = (int64_t)msglen;
        msg->data = buf;
        lang_throw((void*)msg);
    }
}

int64_t* lang_array_of_list(LangCons* list) {
    int64_t n = 0;
    LangCons* cur = list;
    while (cur != NULL) { n++; cur = cur->tail; }
    int64_t* arr = (int64_t*)GC_malloc((size_t)((n + 1) * 8));
    arr[0] = n;
    cur = list;
    for (int64_t i = 1; i <= n; i++) {
        arr[i] = cur->head;
        cur = cur->tail;
    }
    return arr;
}

LangCons* lang_array_to_list(int64_t* arr) {
    int64_t n = arr[0];
    LangCons* head = NULL;
    for (int64_t i = n; i >= 1; i--) {
        LangCons* cell = (LangCons*)GC_malloc(sizeof(LangCons));
        cell->head = arr[i];
        cell->tail = head;
        head = cell;
    }
    return head;
}

/* Hashtable runtime functions.
 * Chained-bucket hashtable; all allocations via GC_malloc.
 * Missing-key errors use lang_throw (catchable by try/with). */

/* Murmurhash3 finalizer for i64 keys — fast, good avalanche */
static uint64_t lang_ht_hash(int64_t key) {
    uint64_t h = (uint64_t)key;
    h ^= h >> 33;
    h *= UINT64_C(0xff51afd7ed558ccd);
    h ^= h >> 33;
    h *= UINT64_C(0xc4ceb9fe1a85ec53);
    h ^= h >> 33;
    return h;
}

/* Find entry for key; returns NULL if not present */
static LangHashEntry* lang_ht_find(LangHashtable* ht, int64_t key) {
    uint64_t bucket = lang_ht_hash(key) % (uint64_t)ht->capacity;
    LangHashEntry* e = ht->buckets[bucket];
    while (e != NULL) {
        if (e->key == key) return e;
        e = e->next;
    }
    return NULL;
}

LangHashtable* lang_hashtable_create(void) {
    LangHashtable* ht = (LangHashtable*)GC_malloc(sizeof(LangHashtable));
    ht->tag = -1;
    ht->capacity = 16;
    ht->size = 0;
    ht->buckets = (LangHashEntry**)GC_malloc((size_t)(ht->capacity * (int64_t)sizeof(LangHashEntry*)));
    for (int64_t i = 0; i < ht->capacity; i++) {
        ht->buckets[i] = NULL;
    }
    return ht;
}

int64_t lang_hashtable_get(LangHashtable* ht, int64_t key) {
    LangHashEntry* e = lang_ht_find(ht, key);
    if (e != NULL) return e->val;
    /* Key not found: throw a LangString* error (catchable by try/with) */
    const char* msg_str = "hashtable key not found";
    int64_t msg_len = (int64_t)strlen(msg_str);
    char* buf = (char*)GC_malloc((size_t)(msg_len + 1));
    memcpy(buf, msg_str, (size_t)(msg_len + 1));
    LangString* msg = (LangString*)GC_malloc(sizeof(LangString));
    msg->length = msg_len;
    msg->data = buf;
    lang_throw((void*)msg);
    return 0; /* unreachable */
}

/* Rehash into a table of new_cap buckets */
static void lang_ht_rehash(LangHashtable* ht, int64_t new_cap) {
    LangHashEntry** new_buckets = (LangHashEntry**)GC_malloc((size_t)(new_cap * (int64_t)sizeof(LangHashEntry*)));
    for (int64_t i = 0; i < new_cap; i++) new_buckets[i] = NULL;
    for (int64_t i = 0; i < ht->capacity; i++) {
        LangHashEntry* e = ht->buckets[i];
        while (e != NULL) {
            LangHashEntry* next = e->next;
            uint64_t b = lang_ht_hash(e->key) % (uint64_t)new_cap;
            e->next = new_buckets[b];
            new_buckets[b] = e;
            e = next;
        }
    }
    ht->buckets = new_buckets;
    ht->capacity = new_cap;
}

void lang_hashtable_set(LangHashtable* ht, int64_t key, int64_t val) {
    /* Rehash when load factor exceeds 3/4: size > capacity * 3 / 4 */
    if (ht->size * 4 > ht->capacity * 3) {
        lang_ht_rehash(ht, ht->capacity * 2);
    }
    LangHashEntry* e = lang_ht_find(ht, key);
    if (e != NULL) {
        e->val = val;
        return;
    }
    /* Insert new entry at head of bucket chain */
    uint64_t bucket = lang_ht_hash(key) % (uint64_t)ht->capacity;
    LangHashEntry* entry = (LangHashEntry*)GC_malloc(sizeof(LangHashEntry));
    entry->key = key;
    entry->val = val;
    entry->next = ht->buckets[bucket];
    ht->buckets[bucket] = entry;
    ht->size++;
}

int64_t lang_hashtable_containsKey(LangHashtable* ht, int64_t key) {
    return lang_ht_find(ht, key) != NULL ? 1 : 0;
}

void lang_hashtable_remove(LangHashtable* ht, int64_t key) {
    uint64_t bucket = lang_ht_hash(key) % (uint64_t)ht->capacity;
    LangHashEntry* prev = NULL;
    LangHashEntry* e = ht->buckets[bucket];
    while (e != NULL) {
        if (e->key == key) {
            if (prev == NULL) {
                ht->buckets[bucket] = e->next;
            } else {
                prev->next = e->next;
            }
            ht->size--;
            return;
        }
        prev = e;
        e = e->next;
    }
}

LangCons* lang_hashtable_keys(LangHashtable* ht) {
    LangCons* result = NULL;
    for (int64_t i = 0; i < ht->capacity; i++) {
        LangHashEntry* e = ht->buckets[i];
        while (e != NULL) {
            LangCons* cell = (LangCons*)GC_malloc(sizeof(LangCons));
            cell->head = e->key;
            cell->tail = result;
            result = cell;
            e = e->next;
        }
    }
    return result;
}

/* Phase 28: Runtime dispatch for .[...] indexing syntax.
 * Dispatch on first word: arrays store length (>= 0) at offset 0;
 * hashtables store tag = -1 at offset 0. */

int64_t lang_index_get(void* collection, int64_t index) {
    int64_t first_word = ((int64_t*)collection)[0];
    if (first_word < 0) {
        // Hashtable: tag is -1
        return lang_hashtable_get((LangHashtable*)collection, index);
    } else {
        // Array: first word is non-negative length
        int64_t* arr = (int64_t*)collection;
        lang_array_bounds_check(arr, index);
        return arr[index + 1];
    }
}

void lang_index_set(void* collection, int64_t index, int64_t value) {
    int64_t first_word = ((int64_t*)collection)[0];
    if (first_word < 0) {
        // Hashtable: tag is -1
        lang_hashtable_set((LangHashtable*)collection, index, value);
    } else {
        // Array: first word is non-negative length
        int64_t* arr = (int64_t*)collection;
        lang_array_bounds_check(arr, index);
        arr[index + 1] = value;
    }
}

/* Array higher-order function runtime.
 * Closure ABI: closure ptr points to a struct whose first field is a LangClosureFn.
 * Call: fn = *(LangClosureFn*)closure; fn(closure, arg) */

void lang_array_iter(void* closure, int64_t* arr) {
    LangClosureFn fn = *(LangClosureFn*)closure;
    int64_t n = arr[0];
    for (int64_t i = 1; i <= n; i++) {
        fn(closure, arr[i]);
    }
}

/* Phase 30: for-in loop — iterates list (cons cells) or array, calling closure for each element.
 * Dispatches on collection type at runtime using GC_size:
 *   - NULL          = empty list  -> zero iterations
 *   - block_size < 16 = 0-element array (8 bytes: just count field) -> zero iterations
 *   - block_size > 16 = 2+ element array (count+1 words > 2) -> array iteration
 *   - block_size == 16 = either cons cell (head+tail) or 1-element array: disambiguate via GC_base check
 */
void lang_for_in(void* closure, void* collection) {
    if (collection == NULL) return;  /* empty list */
    LangClosureFn fn = *(LangClosureFn*)closure;
    size_t block_size = GC_size(collection);
    if (block_size > 16) {
        /* Array: cons cells are always exactly 16 bytes, so >16 must be array */
        int64_t* arr = (int64_t*)collection;
        int64_t n = arr[0];
        for (int64_t i = 1; i <= n; i++) {
            fn(closure, arr[i]);
        }
    } else {
        /* 16 bytes or less */
        if (block_size < 16) {
            /* Must be 0-element array (8 bytes: just the count field) */
            return;
        }
        /* block_size == 16: check if this is a 1-element array or a cons cell */
        /* Heuristic: use GC_base to check if slot[1] is a heap pointer */
        int64_t* slots = (int64_t*)collection;
        void* second = (void*)slots[1];
        if (second == NULL || (GC_base(second) != NULL && GC_size(second) == 16)) {
            /* Cons cell: tail is NULL (end of list) or points to another cons cell */
            LangCons* cur = (LangCons*)collection;
            while (cur != NULL) {
                fn(closure, cur->head);
                cur = cur->tail;
            }
        } else if (slots[0] == 1) {
            /* 1-element array: count=1, element=slots[1] */
            fn(closure, slots[1]);
        } else {
            /* Fallback: treat as cons cell */
            LangCons* cur = (LangCons*)collection;
            while (cur != NULL) {
                fn(closure, cur->head);
                cur = cur->tail;
            }
        }
    }
}

int64_t* lang_array_map(void* closure, int64_t* arr) {
    LangClosureFn fn = *(LangClosureFn*)closure;
    int64_t n = arr[0];
    int64_t* out = (int64_t*)GC_malloc((size_t)((n + 1) * 8));
    out[0] = n;
    for (int64_t i = 1; i <= n; i++) {
        out[i] = fn(closure, arr[i]);
    }
    return out;
}

int64_t lang_array_fold(void* closure, int64_t init, int64_t* arr) {
    LangClosureFn fn = *(LangClosureFn*)closure;
    int64_t n = arr[0];
    int64_t acc = init;
    for (int64_t i = 1; i <= n; i++) {
        /* Curried binary closure: two calls per iteration.
         * First call: fn(closure, acc) returns partial application (closure ptr as i64).
         * Second call: fn2(partial_ptr, arr[i]) applies element to partial. */
        int64_t partial = fn(closure, acc);
        void* partial_ptr = (void*)partial;
        LangClosureFn fn2 = *(LangClosureFn*)partial_ptr;
        acc = fn2(partial_ptr, arr[i]);
    }
    return acc;
}

int64_t* lang_array_init(int64_t n, void* closure) {
    if (n < 0) {
        lang_failwith("array_init: negative length");
    }
    LangClosureFn fn = *(LangClosureFn*)closure;
    int64_t* out = (int64_t*)GC_malloc((size_t)((n + 1) * 8));
    out[0] = n;
    for (int64_t i = 0; i < n; i++) {
        out[i + 1] = fn(closure, i);
    }
    return out;
}

/* File I/O runtime functions */

LangString* lang_file_read(LangString* path) {
    FILE* f = fopen(path->data, "rb");
    if (f == NULL) {
        const char* msg_prefix = "read_file: file not found: ";
        int64_t prefix_len = (int64_t)strlen(msg_prefix);
        int64_t total_len = prefix_len + path->length;
        char* buf = (char*)GC_malloc((size_t)(total_len + 1));
        memcpy(buf, msg_prefix, (size_t)prefix_len);
        memcpy(buf + prefix_len, path->data, (size_t)path->length);
        buf[total_len] = '\0';
        LangString* msg = (LangString*)GC_malloc(sizeof(LangString));
        msg->length = total_len;
        msg->data = buf;
        lang_throw((void*)msg);
        return NULL; /* unreachable */
    }
    fseek(f, 0, SEEK_END);
    long size = ftell(f);
    fseek(f, 0, SEEK_SET);
    char* content = (char*)GC_malloc((size_t)(size + 1));
    fread(content, 1, (size_t)size, f);
    fclose(f);
    content[size] = '\0';
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = (int64_t)size;
    s->data = content;
    return s;
}

void lang_file_write(LangString* path, LangString* content) {
    FILE* f = fopen(path->data, "wb");
    if (f == NULL) return;
    fwrite(content->data, 1, (size_t)content->length, f);
    fclose(f);
}

void lang_file_append(LangString* path, LangString* content) {
    FILE* f = fopen(path->data, "ab");
    if (f == NULL) return;
    fwrite(content->data, 1, (size_t)content->length, f);
    fclose(f);
}

int64_t lang_file_exists(LangString* path) {
    FILE* f = fopen(path->data, "r");
    if (f != NULL) { fclose(f); return 1; }
    return 0;
}

void lang_eprint(LangString* s) {
    fwrite(s->data, 1, (size_t)s->length, stderr);
    fflush(stderr);
}

void lang_eprintln(LangString* s) {
    fwrite(s->data, 1, (size_t)s->length, stderr);
    fputc('\n', stderr);
    fflush(stderr);
}

/* Extended File I/O and system runtime functions */

LangCons* lang_read_lines(LangString* path) {
    FILE* f = fopen(path->data, "r");
    if (f == NULL) {
        const char* msg_prefix = "read_lines: file not found: ";
        int64_t prefix_len = (int64_t)strlen(msg_prefix);
        int64_t total_len = prefix_len + path->length;
        char* buf = (char*)GC_malloc((size_t)(total_len + 1));
        memcpy(buf, msg_prefix, (size_t)prefix_len);
        memcpy(buf + prefix_len, path->data, (size_t)path->length);
        buf[total_len] = '\0';
        LangString* msg = (LangString*)GC_malloc(sizeof(LangString));
        msg->length = total_len;
        msg->data = buf;
        lang_throw((void*)msg);
        return NULL; /* unreachable */
    }
    LangCons* head = NULL;
    LangCons** cursor = &head;
    char line_buf[4096];
    while (fgets(line_buf, sizeof(line_buf), f) != NULL) {
        int64_t len = (int64_t)strlen(line_buf);
        if (len > 0 && line_buf[len-1] == '\n') {
            line_buf[--len] = '\0';
            if (len > 0 && line_buf[len-1] == '\r') line_buf[--len] = '\0';
        }
        char* data = (char*)GC_malloc((size_t)(len + 1));
        memcpy(data, line_buf, (size_t)(len + 1));
        LangString* s = (LangString*)GC_malloc(sizeof(LangString));
        s->length = len;
        s->data = data;
        LangCons* cell = (LangCons*)GC_malloc(sizeof(LangCons));
        cell->head = (int64_t)(uintptr_t)s;
        cell->tail = NULL;
        *cursor = cell;
        cursor = &cell->tail;
    }
    fclose(f);
    return head;
}

void lang_write_lines(LangString* path, LangCons* lines) {
    FILE* f = fopen(path->data, "w");
    if (f == NULL) return;
    LangCons* cur = lines;
    while (cur != NULL) {
        LangString* s = (LangString*)(uintptr_t)cur->head;
        fwrite(s->data, 1, (size_t)s->length, f);
        fputc('\n', f);
        cur = cur->tail;
    }
    fclose(f);
}

LangString* lang_stdin_read_line(void) {
    int64_t cap = 256;
    char* buf = (char*)GC_malloc((size_t)cap);
    int64_t len = 0;
    int c;
    while ((c = fgetc(stdin)) != EOF && c != '\n') {
        if (len + 1 >= cap) {
            cap *= 2;
            char* newbuf = (char*)GC_malloc((size_t)cap);
            memcpy(newbuf, buf, (size_t)len);
            buf = newbuf;
        }
        buf[len++] = (char)c;
    }
    buf[len] = '\0';
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = len;
    s->data = buf;
    return s;
}

LangString* lang_stdin_read_all(void) {
    int64_t cap = 1024;
    char* buf = (char*)GC_malloc((size_t)cap);
    int64_t len = 0;
    int c;
    while ((c = fgetc(stdin)) != EOF) {
        if (len + 1 >= cap) {
            cap *= 2;
            char* newbuf = (char*)GC_malloc((size_t)cap);
            memcpy(newbuf, buf, (size_t)len);
            buf = newbuf;
        }
        buf[len++] = (char)c;
    }
    buf[len] = '\0';
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = len;
    s->data = buf;
    return s;
}

LangString* lang_get_env(LangString* varName) {
    const char* val = getenv(varName->data);
    if (val == NULL) {
        const char* msg_prefix = "get_env: variable '";
        const char* msg_suffix = "' not set";
        int64_t prefix_len = (int64_t)strlen(msg_prefix);
        int64_t suffix_len = (int64_t)strlen(msg_suffix);
        int64_t total_len = prefix_len + varName->length + suffix_len;
        char* buf = (char*)GC_malloc((size_t)(total_len + 1));
        memcpy(buf, msg_prefix, (size_t)prefix_len);
        memcpy(buf + prefix_len, varName->data, (size_t)varName->length);
        memcpy(buf + prefix_len + varName->length, msg_suffix, (size_t)(suffix_len + 1));
        LangString* msg = (LangString*)GC_malloc(sizeof(LangString));
        msg->length = total_len;
        msg->data = buf;
        lang_throw((void*)msg);
        return NULL; /* unreachable */
    }
    int64_t len = (int64_t)strlen(val);
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    memcpy(buf, val, (size_t)(len + 1));
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = len;
    s->data = buf;
    return s;
}

LangString* lang_get_cwd(void) {
    char tmp[4096];
    if (getcwd(tmp, sizeof(tmp)) == NULL) {
        const char* msg_str = "get_cwd: failed";
        int64_t msg_len = (int64_t)strlen(msg_str);
        char* buf = (char*)GC_malloc((size_t)(msg_len + 1));
        memcpy(buf, msg_str, (size_t)(msg_len + 1));
        LangString* msg = (LangString*)GC_malloc(sizeof(LangString));
        msg->length = msg_len;
        msg->data = buf;
        lang_throw((void*)msg);
        return NULL; /* unreachable */
    }
    int64_t len = (int64_t)strlen(tmp);
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    memcpy(buf, tmp, (size_t)(len + 1));
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = len;
    s->data = buf;
    return s;
}

LangString* lang_path_combine(LangString* dir, LangString* file) {
    int add_sep = (dir->length > 0 && dir->data[dir->length - 1] != '/') ? 1 : 0;
    int64_t total_len = dir->length + add_sep + file->length;
    char* buf = (char*)GC_malloc((size_t)(total_len + 1));
    memcpy(buf, dir->data, (size_t)dir->length);
    if (add_sep) buf[dir->length] = '/';
    memcpy(buf + dir->length + add_sep, file->data, (size_t)file->length);
    buf[total_len] = '\0';
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = total_len;
    s->data = buf;
    return s;
}

LangCons* lang_dir_files(LangString* path) {
    DIR* d = opendir(path->data);
    if (d == NULL) {
        const char* msg_prefix = "dir_files: directory not found: ";
        int64_t prefix_len = (int64_t)strlen(msg_prefix);
        int64_t total_len = prefix_len + path->length;
        char* buf = (char*)GC_malloc((size_t)(total_len + 1));
        memcpy(buf, msg_prefix, (size_t)prefix_len);
        memcpy(buf + prefix_len, path->data, (size_t)path->length);
        buf[total_len] = '\0';
        LangString* msg = (LangString*)GC_malloc(sizeof(LangString));
        msg->length = total_len;
        msg->data = buf;
        lang_throw((void*)msg);
        return NULL; /* unreachable */
    }
    LangCons* head = NULL;
    LangCons** cursor = &head;
    struct dirent* entry;
    while ((entry = readdir(d)) != NULL) {
        if (entry->d_name[0] == '.' &&
            (entry->d_name[1] == '\0' || (entry->d_name[1] == '.' && entry->d_name[2] == '\0'))) {
            continue;
        }
        if (entry->d_type != DT_REG && entry->d_type != DT_UNKNOWN) continue;
        /* Build full path: path + "/" + d_name */
        int64_t name_len = (int64_t)strlen(entry->d_name);
        int add_sep = (path->length > 0 && path->data[path->length - 1] != '/') ? 1 : 0;
        int64_t full_len = path->length + add_sep + name_len;
        char* full = (char*)GC_malloc((size_t)(full_len + 1));
        memcpy(full, path->data, (size_t)path->length);
        if (add_sep) full[path->length] = '/';
        memcpy(full + path->length + add_sep, entry->d_name, (size_t)(name_len + 1));
        LangString* s = (LangString*)GC_malloc(sizeof(LangString));
        s->length = full_len;
        s->data = full;
        LangCons* cell = (LangCons*)GC_malloc(sizeof(LangCons));
        cell->head = (int64_t)(uintptr_t)s;
        cell->tail = NULL;
        *cursor = cell;
        cursor = &cell->tail;
    }
    closedir(d);
    return head;
}
