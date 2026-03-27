#include <stdint.h>
#include <string.h>
#include <stdio.h>
#include <stdlib.h>
#include <gc.h>
#include "lang_runtime.h"

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
