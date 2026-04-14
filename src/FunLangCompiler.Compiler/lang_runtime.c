#include <stdint.h>
#include <string.h>
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>
#include <dirent.h>
#include <ctype.h>
#include <signal.h>
#include <gc.h>
#include "lang_runtime.h"

/* Phase 89: Tagged integer helpers for C↔FunLang boundary.
 * All integers passed to FunLang closures or stored in compiler-read
 * data structures must use tagged representation: int n → (n<<1)|1.
 * Pointers (strings, lists, etc.) are always even (LSB=0). */
#define LANG_TAG_INT(n)    (((int64_t)(n) << 1) | 1)
#define LANG_UNTAG_INT(v)  ((int64_t)(v) >> 1)

/* Phase 93: String struct layout — {heap_tag, length, data} at offsets 0, 8, 16 */
typedef struct LangString_s {
    int64_t heap_tag;
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
    s->heap_tag = LANG_HEAP_TAG_STRING;
    s->length = total;
    s->data = buf;
    return s;
}

LangString* lang_to_string_int(int64_t n) {
    n = LANG_UNTAG_INT(n);
    char tmp[32];
    int len = snprintf(tmp, sizeof(tmp), "%ld", (long)n);
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    memcpy(buf, tmp, (size_t)(len + 1));
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->heap_tag = LANG_HEAP_TAG_STRING;
    s->length = (int64_t)len;
    s->data = buf;
    return s;
}

LangString* lang_to_string_bool(int64_t b) {
    b = LANG_UNTAG_INT(b);
    const char* str = b ? "true" : "false";
    int64_t len = (int64_t)strlen(str);
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    memcpy(buf, str, (size_t)(len + 1));
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->heap_tag = LANG_HEAP_TAG_STRING;
    s->length = len;
    s->data = buf;
    return s;
}

// Phase 99: Runtime call stack for backtrace on match failure / failwith.
// Simple fixed-size stack of function name C strings.
#define LANG_CALLSTACK_MAX 256
static const char* lang_callstack[LANG_CALLSTACK_MAX];
static int lang_callstack_depth = 0;

void lang_trace_push(const char* funcName) {
    if (lang_callstack_depth < LANG_CALLSTACK_MAX)
        lang_callstack[lang_callstack_depth] = funcName;
    lang_callstack_depth++;
}

void lang_trace_pop(void) {
    if (lang_callstack_depth > 0)
        lang_callstack_depth--;
}

static void lang_print_backtrace(void) {
    int depth = lang_callstack_depth;
    if (depth > LANG_CALLSTACK_MAX) depth = LANG_CALLSTACK_MAX;
    if (depth == 0) return;
    fprintf(stderr, "Backtrace (most recent call last):\n");
    for (int i = 0; i < depth; i++) {
        fprintf(stderr, "  %d: %s\n", i, lang_callstack[i]);
    }
}

void lang_match_failure(const char* location, int64_t value) {
    fprintf(stderr, "Fatal: non-exhaustive match at %s (value=%lld)\n", location, (long long)value);
    lang_print_backtrace();
    exit(1);
}

void lang_failwith(const char* msg, const char* location) {
    fprintf(stderr, "Fatal: %s at %s\n", msg, location);
    lang_print_backtrace();
    exit(1);
}

/* Phase 110 (Issue #29): Signal handler for SIGSEGV/SIGBUS/SIGFPE/SIGILL.
 * Prints a fatal message + FunLang-level backtrace, then re-raises the signal
 * so the OS still records the abnormal termination. This gives users the same
 * diagnostic UX as match-failure/failwith even for hardware faults (stack
 * overflow, null deref, division by zero, etc.). */
static const char* lang_signal_name(int sig) {
    switch (sig) {
        case SIGSEGV: return "SIGSEGV (segmentation fault — likely stack overflow, null deref, or invalid memory access)";
        case SIGBUS:  return "SIGBUS (bus error — misaligned memory access)";
        case SIGFPE:  return "SIGFPE (arithmetic fault — division by zero, integer overflow)";
        case SIGILL:  return "SIGILL (illegal instruction — corrupted code pointer)";
        case SIGABRT: return "SIGABRT (abort)";
        default: return "unknown";
    }
}

static void lang_signal_handler(int sig) {
    /* Only async-signal-safe operations would be strictly correct, but since
     * we're about to terminate and prefer diagnostic clarity, use fprintf. */
    fprintf(stderr, "Fatal: runtime signal %d: %s\n", sig, lang_signal_name(sig));
    lang_print_backtrace();
    /* Restore default handler and re-raise so the OS exit code reflects the
     * crash (e.g., 139 for SIGSEGV). This preserves core-dump behaviour. */
    signal(sig, SIG_DFL);
    raise(sig);
}

/* Alt-stack backing buffer: allocated once; signal handlers run here when
 * the primary stack is exhausted (stack overflow -> SIGSEGV). Without this
 * the handler would itself overflow and the process dies silently. */
#define LANG_ALTSTACK_SIZE (128 * 1024)
static char lang_altstack_buf[LANG_ALTSTACK_SIZE];

void lang_install_signal_handlers(void) {
    /* Install alternate signal stack so handlers can run even on stack overflow. */
    stack_t ss;
    ss.ss_sp = lang_altstack_buf;
    ss.ss_size = LANG_ALTSTACK_SIZE;
    ss.ss_flags = 0;
    sigaltstack(&ss, NULL);

    struct sigaction sa;
    sa.sa_handler = lang_signal_handler;
    sigemptyset(&sa.sa_mask);
    sa.sa_flags = SA_ONSTACK | SA_RESETHAND;
    sigaction(SIGSEGV, &sa, NULL);
    sigaction(SIGBUS,  &sa, NULL);
    sigaction(SIGFPE,  &sa, NULL);
    sigaction(SIGILL,  &sa, NULL);
    /* SIGABRT intentionally NOT caught — user abort() and library assertions
     * should pass through without our diagnostic. */
}

/* Internal helper: raw (untagged) start/len arguments */
static LangString* string_sub_raw(LangString* s, int64_t start, int64_t len) {
    if (start < 0) start = 0;
    if (start > s->length) start = s->length;
    if (len < 0) len = 0;
    if (start + len > s->length) len = s->length - start;
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    memcpy(buf, s->data + start, (size_t)len);
    buf[len] = '\0';
    LangString* r = (LangString*)GC_malloc(sizeof(LangString));
    r->heap_tag = LANG_HEAP_TAG_STRING;
    r->length = len;
    r->data = buf;
    return r;
}

LangString* lang_string_sub(LangString* s, int64_t start, int64_t len) {
    start = LANG_UNTAG_INT(start);
    len = LANG_UNTAG_INT(len);
    return string_sub_raw(s, start, len);
}

/* Phase 34-01: LANG-01 String slicing — s.[start..stop] and s.[start..] */
LangString* lang_string_slice(LangString* s, int64_t start, int64_t stop) {
    start = LANG_UNTAG_INT(start);
    stop = LANG_UNTAG_INT(stop);
    // stop == -1 means open-ended (to end of string)
    if (stop < 0) stop = s->length - 1;
    int64_t len = stop - start + 1;
    return string_sub_raw(s, start, len);
}

/* Phase 66: String character access — returns byte at index as i64 (char code) */
int64_t lang_string_char_at(LangString* s, int64_t index) {
    index = LANG_UNTAG_INT(index);
    return LANG_TAG_INT((int64_t)(unsigned char)s->data[index]);
}

/* Phase 92: Return tagged string length for compiler — replaces inline GEP+load+retag */
int64_t lang_string_length(LangString* s) {
    return LANG_TAG_INT(s->length);
}

int64_t lang_string_contains(LangString* s, LangString* sub) {
    if (sub->length == 0) return 1;
    return strstr(s->data, sub->data) != NULL ? 1 : 0;
}

int64_t lang_string_endswith(LangString* s, LangString* suffix) {
    if (suffix->length > s->length) return 0;
    int64_t offset = s->length - suffix->length;
    return memcmp(s->data + offset, suffix->data, (size_t)suffix->length) == 0 ? 1 : 0;
}

int64_t lang_string_startswith(LangString* s, LangString* prefix) {
    if (prefix->length > s->length) return 0;
    return memcmp(s->data, prefix->data, (size_t)prefix->length) == 0 ? 1 : 0;
}

LangString* lang_string_trim(LangString* s) {
    int64_t start = 0;
    int64_t end = s->length - 1;
    while (start <= end && (s->data[start] == ' ' || s->data[start] == '\t' ||
           s->data[start] == '\n' || s->data[start] == '\r')) start++;
    while (end >= start && (s->data[end] == ' ' || s->data[end] == '\t' ||
           s->data[end] == '\n' || s->data[end] == '\r')) end--;
    int64_t len = end - start + 1;
    if (len < 0) len = 0;
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    memcpy(buf, s->data + start, (size_t)len);
    buf[len] = '\0';
    LangString* r = (LangString*)GC_malloc(sizeof(LangString));
    r->heap_tag = LANG_HEAP_TAG_STRING;
    r->length = len;
    r->data = buf;
    return r;
}

/* Phase 54: string_indexof — find substring position, return -1 if not found */
int64_t lang_string_indexof(LangString* s, LangString* sub) {
    if (sub->length == 0) return LANG_TAG_INT(0);
    if (sub->length > s->length) return LANG_TAG_INT(-1);
    char* found = strstr(s->data, sub->data);
    if (!found) return LANG_TAG_INT(-1);
    return LANG_TAG_INT((int64_t)(found - s->data));
}

/* Phase 54: string_replace — replace all occurrences */
LangString* lang_string_replace(LangString* s, LangString* old, LangString* rep) {
    if (old->length == 0) {
        LangString* r = (LangString*)GC_malloc(sizeof(LangString));
        r->heap_tag = LANG_HEAP_TAG_STRING;
        char* buf = (char*)GC_malloc((size_t)(s->length + 1));
        memcpy(buf, s->data, (size_t)s->length);
        buf[s->length] = '\0';
        r->length = s->length;
        r->data = buf;
        return r;
    }
    int64_t count = 0;
    const char* p = s->data;
    while ((p = strstr(p, old->data)) != NULL) {
        count++;
        p += old->length;
    }
    int64_t new_len = s->length + count * (rep->length - old->length);
    char* buf = (char*)GC_malloc((size_t)(new_len + 1));
    char* dst = buf;
    const char* src = s->data;
    while (*src) {
        if (strncmp(src, old->data, (size_t)old->length) == 0) {
            memcpy(dst, rep->data, (size_t)rep->length);
            dst += rep->length;
            src += old->length;
        } else {
            *dst++ = *src++;
        }
    }
    *dst = '\0';
    LangString* r = (LangString*)GC_malloc(sizeof(LangString));
    r->heap_tag = LANG_HEAP_TAG_STRING;
    r->length = new_len;
    r->data = buf;
    return r;
}

/* Phase 54: string_toupper — convert to uppercase */
LangString* lang_string_toupper(LangString* s) {
    char* buf = (char*)GC_malloc((size_t)(s->length + 1));
    for (int64_t i = 0; i < s->length; i++) {
        buf[i] = (char)toupper((unsigned char)s->data[i]);
    }
    buf[s->length] = '\0';
    LangString* r = (LangString*)GC_malloc(sizeof(LangString));
    r->heap_tag = LANG_HEAP_TAG_STRING;
    r->length = s->length;
    r->data = buf;
    return r;
}

/* Phase 54: string_tolower — convert to lowercase */
LangString* lang_string_tolower(LangString* s) {
    char* buf = (char*)GC_malloc((size_t)(s->length + 1));
    for (int64_t i = 0; i < s->length; i++) {
        buf[i] = (char)tolower((unsigned char)s->data[i]);
    }
    buf[s->length] = '\0';
    LangString* r = (LangString*)GC_malloc(sizeof(LangString));
    r->heap_tag = LANG_HEAP_TAG_STRING;
    r->length = s->length;
    r->data = buf;
    return r;
}

int64_t lang_string_to_int(LangString* s) {
    return LANG_TAG_INT((int64_t)strtol(s->data, NULL, 10));
}

int64_t lang_char_is_digit(int64_t c) {
    c = LANG_UNTAG_INT(c);
    return isdigit((int)c) ? 1 : 0;
}
int64_t lang_char_is_letter(int64_t c) {
    c = LANG_UNTAG_INT(c);
    return isalpha((int)c) ? 1 : 0;
}
int64_t lang_char_is_upper(int64_t c) {
    c = LANG_UNTAG_INT(c);
    return isupper((int)c) ? 1 : 0;
}
int64_t lang_char_is_lower(int64_t c) {
    c = LANG_UNTAG_INT(c);
    return islower((int)c) ? 1 : 0;
}
int64_t lang_char_to_upper(int64_t c) {
    c = LANG_UNTAG_INT(c);
    return LANG_TAG_INT((int64_t)toupper((int)c));
}
int64_t lang_char_to_lower(int64_t c) {
    c = LANG_UNTAG_INT(c);
    return LANG_TAG_INT((int64_t)tolower((int)c));
}

/* Phase 93: Cons cell layout: {heap_tag, head, tail} — 24 bytes total */
typedef struct LangCons {
    int64_t         heap_tag;
    int64_t         head;
    struct LangCons* tail;
} LangCons;

LangString* lang_string_concat_list(LangString* sep, LangCons* list) {
    int64_t total = 0;
    int64_t count = 0;
    LangCons* cur = list;
    while (cur != NULL) {
        LangString* item = (LangString*)(uintptr_t)cur->head;
        total += item->length;
        count++;
        cur = cur->tail;
    }
    if (count > 1) total += sep->length * (count - 1);
    char* buf = (char*)GC_malloc((size_t)(total + 1));
    int64_t pos = 0;
    cur = list;
    int64_t i = 0;
    while (cur != NULL) {
        if (i > 0 && sep->length > 0) {
            memcpy(buf + pos, sep->data, (size_t)sep->length);
            pos += sep->length;
        }
        LangString* item = (LangString*)(uintptr_t)cur->head;
        memcpy(buf + pos, item->data, (size_t)item->length);
        pos += item->length;
        cur = cur->tail;
        i++;
    }
    buf[total] = '\0';
    LangString* r = (LangString*)GC_malloc(sizeof(LangString));
    r->heap_tag = LANG_HEAP_TAG_STRING;
    r->length = total;
    r->data = buf;
    return r;
}

/* Phase 54: string_split — split string by separator, return cons list of LangString* */
LangCons* lang_string_split(LangString* s, LangString* sep) {
    LangCons* head = NULL;
    LangCons** cursor = &head;
    const char* src = s->data;
    int64_t src_len = s->length;
    int64_t sep_len = sep->length;
    if (sep_len == 0) {
        LangCons* cell = (LangCons*)GC_malloc(sizeof(LangCons));
        cell->heap_tag = LANG_HEAP_TAG_LIST;
        cell->head = (int64_t)s;
        cell->tail = NULL;
        return cell;
    }
    int64_t pos = 0;
    while (pos <= src_len) {
        const char* found = NULL;
        if (pos < src_len) {
            for (int64_t i = pos; i <= src_len - sep_len; i++) {
                if (memcmp(src + i, sep->data, (size_t)sep_len) == 0) {
                    found = src + i;
                    break;
                }
            }
        }
        int64_t chunk_len;
        if (found) {
            chunk_len = (int64_t)(found - (src + pos));
        } else {
            chunk_len = src_len - pos;
        }
        LangString* part = (LangString*)GC_malloc(sizeof(LangString));
        part->heap_tag = LANG_HEAP_TAG_STRING;
        char* buf = (char*)GC_malloc((size_t)(chunk_len + 1));
        memcpy(buf, src + pos, (size_t)chunk_len);
        buf[chunk_len] = '\0';
        part->length = chunk_len;
        part->data = buf;
        LangCons* cell = (LangCons*)GC_malloc(sizeof(LangCons));
        cell->heap_tag = LANG_HEAP_TAG_LIST;
        cell->head = (int64_t)part;
        cell->tail = NULL;
        *cursor = cell;
        cursor = &cell->tail;
        if (found) {
            pos = (int64_t)(found - src) + sep_len;
        } else {
            break;
        }
    }
    return head ? head : NULL;
}

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
            cell->heap_tag = LANG_HEAP_TAG_LIST;
            cell->head = i;
            cell->tail = NULL;
            *cursor = cell;
            cursor = &cell->tail;
        }
    } else {
        for (int64_t i = start; i >= stop; i += step) {
            LangCons* cell = (LangCons*)GC_malloc(sizeof(LangCons));
            cell->heap_tag = LANG_HEAP_TAG_LIST;
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
        lang_print_backtrace();
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
    n = LANG_UNTAG_INT(n);
    if (n < 0) {
        lang_failwith("array_create: negative length", "<runtime>");
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
        msg->heap_tag = LANG_HEAP_TAG_STRING;
        msg->length = (int64_t)msglen;
        msg->data = buf;
        lang_throw((void*)msg);
    }
}

/* Phase 92: Return tagged array length — replaces inline GEP+load+retag */
int64_t lang_array_length(int64_t* arr) {
    return LANG_TAG_INT(arr[0]);
}

/* Phase 92: Array element access — untag index, bounds check, access */
int64_t lang_array_get(int64_t* arr, int64_t tagged_idx) {
    int64_t i = LANG_UNTAG_INT(tagged_idx);
    lang_array_bounds_check(arr, i);
    return arr[i + 1];
}

void lang_array_set(int64_t* arr, int64_t tagged_idx, int64_t value) {
    int64_t i = LANG_UNTAG_INT(tagged_idx);
    lang_array_bounds_check(arr, i);
    arr[i + 1] = value;
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
        cell->heap_tag = LANG_HEAP_TAG_LIST;
        cell->head = arr[i];
        cell->tail = head;
        head = cell;
    }
    return head;
}

/* Hashtable runtime functions.
 * Chained-bucket hashtable; all allocations via GC_malloc.
 * Missing-key errors use lang_throw (catchable by try/with). */

/* Phase 93: Generic hash — dispatches on LSB (tagged int) or heap tag at slot 0.
 * Handles: tagged ints, strings, tuples, records, lists, ADTs.
 * Depth limit 256 for list traversal to prevent stack overflow. */
static uint64_t lang_ht_hash(int64_t val) {
    if (val & 1) {
        /* Tagged int — murmurhash3 finalizer */
        uint64_t h = (uint64_t)val;
        h ^= h >> 33;
        h *= UINT64_C(0xff51afd7ed558ccd);
        h ^= h >> 33;
        h *= UINT64_C(0xc4ceb9fe1a85ec53);
        h ^= h >> 33;
        return h;
    }
    if (val == 0) return 0;  /* NULL pointer (nil list, unit, etc.) */

    int64_t* block = (int64_t*)val;
    int64_t tag = block[0];
    switch (tag) {
    case LANG_HEAP_TAG_STRING: {
        LangString* s = (LangString*)block;
        uint64_t h = UINT64_C(14695981039346656037);
        for (int64_t i = 0; i < s->length; i++) {
            h ^= (uint8_t)s->data[i];
            h *= UINT64_C(1099511628211);
        }
        return h;
    }
    case LANG_HEAP_TAG_TUPLE:
    case LANG_HEAP_TAG_RECORD: {
        int64_t n = block[1]; /* num_fields */
        uint64_t h = (uint64_t)tag;
        for (int64_t i = 0; i < n; i++) {
            h = h * 31 + lang_ht_hash(block[2 + i]);
        }
        return h;
    }
    case LANG_HEAP_TAG_LIST: {
        uint64_t h = UINT64_C(0x9e3779b97f4a7c15);
        int64_t* cur = block;
        int depth = 0;
        while (cur != NULL && depth < 256) {
            h = h * 31 + lang_ht_hash(cur[1]); /* head at slot 1 */
            int64_t tail = cur[2];              /* tail at slot 2 */
            cur = (tail == 0) ? NULL : (int64_t*)tail;
            depth++;
        }
        return h;
    }
    case LANG_HEAP_TAG_ADT: {
        uint64_t h = lang_ht_hash(block[1]); /* constructor tag (tagged int) at slot 1 */
        int64_t payload = block[2];           /* payload at slot 2 */
        if (payload != 0) {
            h = h * 31 + lang_ht_hash(payload);
        }
        return h;
    }
    default:
        /* Unknown pointer (closure, etc.) — hash pointer value */
        return (uint64_t)val * UINT64_C(0x9e3779b97f4a7c15);
    }
}

/* Phase 93: Generic equality — structural comparison dispatching on heap tag.
 * Handles: tagged ints, strings, tuples, records, lists, ADTs.
 * Depth limit 256 for list traversal. */
static int lang_ht_eq(int64_t a, int64_t b) {
    if (a == b) return 1;                      /* Same value/pointer */
    if ((a & 1) != (b & 1)) return 0;          /* int vs ptr mismatch */
    if (a & 1) return 0;                        /* Both ints but different */
    if (a == 0 || b == 0) return 0;             /* One is NULL */

    int64_t* ba = (int64_t*)a;
    int64_t* bb = (int64_t*)b;
    if (ba[0] != bb[0]) return 0;              /* Different heap tags */

    switch (ba[0]) {
    case LANG_HEAP_TAG_STRING: {
        LangString* sa = (LangString*)ba;
        LangString* sb = (LangString*)bb;
        return sa->length == sb->length &&
               memcmp(sa->data, sb->data, (size_t)sa->length) == 0;
    }
    case LANG_HEAP_TAG_TUPLE:
    case LANG_HEAP_TAG_RECORD: {
        int64_t na = ba[1], nb = bb[1];
        if (na != nb) return 0;
        for (int64_t i = 0; i < na; i++) {
            if (!lang_ht_eq(ba[2+i], bb[2+i])) return 0;
        }
        return 1;
    }
    case LANG_HEAP_TAG_LIST: {
        int64_t* ca = ba;
        int64_t* cb = bb;
        int depth = 0;
        while (ca != NULL && cb != NULL && depth < 256) {
            if (!lang_ht_eq(ca[1], cb[1])) return 0;
            ca = (ca[2] == 0) ? NULL : (int64_t*)ca[2];
            cb = (cb[2] == 0) ? NULL : (int64_t*)cb[2];
            depth++;
        }
        return (ca == NULL && cb == NULL) ? 1 : 0;
    }
    case LANG_HEAP_TAG_ADT: {
        if (ba[1] != bb[1]) return 0;          /* different constructor tag */
        return lang_ht_eq(ba[2], bb[2]);        /* compare payloads */
    }
    default:
        return 0;
    }
}

/* Phase 93: Non-static wrapper for lang_ht_eq — called by compiled = operator
 * for structural equality on all heap types (string, tuple, record, list, ADT).
 * Returns 1 (equal) or 0 (not equal) as int64_t. */
int64_t lang_generic_eq(int64_t a, int64_t b) {
    return lang_ht_eq(a, b) ? 1 : 0;
}

/* Find entry for key; returns NULL if not present.
 * Phase 90: Uses lang_ht_eq for unified int/string equality. */
static LangHashEntry* lang_ht_find(LangHashtable* ht, int64_t key) {
    uint64_t bucket = lang_ht_hash(key) % (uint64_t)ht->capacity;
    LangHashEntry* e = ht->buckets[bucket];
    while (e != NULL) {
        if (lang_ht_eq(e->key, key)) return e;
        e = e->next;
    }
    return NULL;
}

int64_t* lang_hashtable_trygetvalue(LangHashtable* ht, int64_t key) {
    /* Phase 100: Return ADT option format matching FunLang's type Option 'a = None | Some of 'a
     * Layout: { heap_tag=ADT(5), constructor_tag, payload }
     * None = tag 0, Some = tag 1 (declaration order in Prelude/Option.fun) */
    int64_t* block = (int64_t*)GC_malloc(24);  /* 3 slots x 8 bytes */
    block[0] = LANG_HEAP_TAG_ADT;
    LangHashEntry* e = lang_ht_find(ht, key);
    if (e != NULL) {
        block[1] = 1;       /* Some constructor tag */
        block[2] = e->val;  /* payload (already tagged) */
    } else {
        block[1] = 0;       /* None constructor tag */
        block[2] = 0;
    }
    return block;
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
    msg->heap_tag = LANG_HEAP_TAG_STRING;
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
        if (lang_ht_eq(e->key, key)) {
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

/* Phase 92: Return tagged hashtable size — replaces inline GEP+load+retag */
int64_t lang_hashtable_count(LangHashtable* ht) {
    return LANG_TAG_INT(ht->size);
}

LangCons* lang_hashtable_keys(LangHashtable* ht) {
    LangCons* result = NULL;
    for (int64_t i = 0; i < ht->capacity; i++) {
        LangHashEntry* e = ht->buckets[i];
        while (e != NULL) {
            LangCons* cell = (LangCons*)GC_malloc(sizeof(LangCons));
            cell->heap_tag = LANG_HEAP_TAG_LIST;
            cell->head = e->key;
            cell->tail = result;
            result = cell;
            e = e->next;
        }
    }
    return result;
}

/* Phase 90: _str functions removed — unified hashtable handles both int and string keys.
 * lang_index_get_str / lang_index_set_str kept as thin wrappers for ht.["key"] syntax. */

int64_t lang_index_get_str(void* collection, LangString* key) {
    return lang_hashtable_get((LangHashtable*)collection, (int64_t)(uintptr_t)key);
}

void lang_index_set_str(void* collection, LangString* key, int64_t value) {
    lang_hashtable_set((LangHashtable*)collection, (int64_t)(uintptr_t)key, value);
}

/* Phase 28: Runtime dispatch for .[...] indexing syntax.
 * Dispatch on first word: arrays store length (>= 0) at offset 0;
 * hashtables store tag = -1 at offset 0. */

int64_t lang_index_get(void* collection, int64_t index) {
    int64_t first_word = ((int64_t*)collection)[0];
    if (first_word < 0) {
        // Hashtable: index arrives tagged, pass directly
        return lang_hashtable_get((LangHashtable*)collection, index);
    } else {
        // Array: untag index, bounds check, access
        int64_t* arr = (int64_t*)collection;
        int64_t i = LANG_UNTAG_INT(index);
        lang_array_bounds_check(arr, i);
        return arr[i + 1];
    }
}

void lang_index_set(void* collection, int64_t index, int64_t value) {
    int64_t first_word = ((int64_t*)collection)[0];
    if (first_word < 0) {
        // Hashtable: index arrives tagged, pass directly
        lang_hashtable_set((LangHashtable*)collection, index, value);
    } else {
        // Array: untag index, bounds check, store
        int64_t* arr = (int64_t*)collection;
        int64_t i = LANG_UNTAG_INT(index);
        lang_array_bounds_check(arr, i);
        arr[i + 1] = value;
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

/* Phase 30: for-in loop over a cons-cell list.
 * Iterates head values of LangCons chain until NULL. */
void lang_for_in_list(void* closure, void* collection) {
    LangClosureFn fn = *(LangClosureFn*)closure;
    LangCons* cur = (LangCons*)collection;
    while (cur != NULL) {
        fn(closure, cur->head);
        cur = cur->tail;
    }
}

/* Phase 30: for-in loop over an array (first word = count, elements at [1..n]).
 * Handles NULL and zero-count arrays as zero iterations. */
void lang_for_in_array(void* closure, void* collection) {
    if (collection == NULL) return;
    LangClosureFn fn = *(LangClosureFn*)closure;
    int64_t* arr = (int64_t*)collection;
    int64_t n = arr[0];
    for (int64_t i = 1; i <= n; i++) {
        fn(closure, arr[i]);
    }
}

/* Phase 30: for-in loop — generic version kept for backward compatibility.
 * NOTE: Use lang_for_in_list or lang_for_in_array when the collection type is known. */
void lang_for_in(void* closure, void* collection) {
    lang_for_in_list(closure, collection);
}

/* Phase 34-03: LANG-03/04 — for-in loop over Phase 33 collection types */

void lang_for_in_hashset(void* closure, LangHashSet* hs) {
    LangClosureFn fn = *(LangClosureFn*)closure;
    for (int64_t i = 0; i < hs->capacity; i++) {
        LangHashSetEntry* e = hs->buckets[i];
        while (e != NULL) {
            fn(closure, e->key);  /* key stored as-is (tagged) */
            e = e->next;
        }
    }
}

void lang_for_in_queue(void* closure, LangQueue* q) {
    LangClosureFn fn = *(LangClosureFn*)closure;
    LangQueueNode* node = q->head;
    while (node != NULL) {
        fn(closure, node->value);
        node = node->next;
    }
}

void lang_for_in_mlist(void* closure, LangMutableList* ml) {
    LangClosureFn fn = *(LangClosureFn*)closure;
    for (int64_t i = 0; i < ml->len; i++) {
        fn(closure, ml->data[i]);
    }
}

void lang_for_in_hashtable(void* closure, LangHashtable* ht) {
    LangClosureFn fn = *(LangClosureFn*)closure;
    for (int64_t i = 0; i < ht->capacity; i++) {
        LangHashEntry* e = ht->buckets[i];
        while (e != NULL) {
            int64_t* tup = (int64_t*)GC_malloc(4 * sizeof(int64_t));  /* Phase 93: tag + count + 2 fields */
            tup[0] = LANG_HEAP_TAG_TUPLE;
            tup[1] = 2;  /* field count */
            tup[2] = e->key;  /* Phase 90: key already tagged (int) or pointer — pass as-is */
            tup[3] = e->val;               /* val already tagged */
            fn(closure, (int64_t)(uintptr_t)tup);
            e = e->next;
        }
    }
}

/* Phase 34: list comprehension — applies closure to each element of a LangCons* list,
 * accumulates results into a new LangCons* list preserving element order. */
LangCons* lang_list_comp(void* closure, void* collection) {
    LangClosureFn fn = *(LangClosureFn*)closure;
    LangCons* cur = (LangCons*)collection;
    /* Reverse-accumulate, then reverse to preserve order */
    LangCons* result = NULL;
    while (cur != NULL) {
        int64_t val = fn(closure, cur->head);
        LangCons* cell = (LangCons*)GC_malloc(sizeof(LangCons));
        cell->heap_tag = LANG_HEAP_TAG_LIST;
        cell->head = val;
        cell->tail = result;
        result = cell;
        cur = cur->tail;
    }
    LangCons* reversed = NULL;
    while (result != NULL) {
        LangCons* next = result->tail;
        result->tail = reversed;
        reversed = result;
        result = next;
    }
    return reversed;
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
    n = LANG_UNTAG_INT(n);
    if (n < 0) {
        lang_failwith("array_init: negative length", "<runtime>");
    }
    LangClosureFn fn = *(LangClosureFn*)closure;
    int64_t* out = (int64_t*)GC_malloc((size_t)((n + 1) * 8));
    out[0] = n;
    for (int64_t i = 0; i < n; i++) {
        out[i + 1] = fn(closure, LANG_TAG_INT(i));
    }
    return out;
}

/* Phase 32-02: list_sort_by — sort list ascending by key extractor closure */
LangCons* lang_list_sort_by(void* closure, LangCons* list) {
    int64_t n = 0;
    LangCons* cur = list;
    while (cur != NULL) { n++; cur = cur->tail; }
    if (n <= 1) return list;

    int64_t* elems = (int64_t*)GC_malloc((size_t)(n * 8));
    int64_t* keys  = (int64_t*)GC_malloc((size_t)(n * 8));
    LangClosureFn fn = *(LangClosureFn*)closure;
    cur = list;
    for (int64_t i = 0; i < n; i++) {
        elems[i] = cur->head;
        keys[i]  = fn(closure, cur->head);
        cur = cur->tail;
    }

    for (int64_t i = 1; i < n; i++) {
        int64_t ke = keys[i], ve = elems[i];
        int64_t j = i - 1;
        while (j >= 0 && keys[j] > ke) {
            keys[j+1]  = keys[j];
            elems[j+1] = elems[j];
            j--;
        }
        keys[j+1]  = ke;
        elems[j+1] = ve;
    }

    LangCons* head = NULL;
    for (int64_t i = n - 1; i >= 0; i--) {
        LangCons* cell = (LangCons*)GC_malloc(sizeof(LangCons));
        cell->heap_tag = LANG_HEAP_TAG_LIST;
        cell->head = elems[i];
        cell->tail = head;
        head = cell;
    }
    return head;
}

/* Phase 32-02: list_of_seq — identity pass-through (list already is a seq) */
LangCons* lang_list_of_seq(void* collection) {
    return (LangCons*)collection;
}

/* Phase 32-03: array_sort — in-place ascending sort of int64_t array */
static int lang_compare_i64(const void* a, const void* b) {
    int64_t x = *(const int64_t*)a;
    int64_t y = *(const int64_t*)b;
    if (x < y) return -1;
    if (x > y) return  1;
    return 0;
}

void lang_array_sort(int64_t* arr) {
    int64_t n = arr[0];
    if (n <= 1) return;
    qsort(&arr[1], (size_t)n, sizeof(int64_t), lang_compare_i64);
}

/* Phase 32-03: array_of_seq — delegates to lang_array_of_list */
int64_t* lang_array_of_seq(void* collection) {
    return lang_array_of_list((LangCons*)collection);
}

/* Phase 33-01: COL-01 StringBuilder (struct defined in lang_runtime.h) */
LangStringBuilder* lang_sb_create(void) {
    LangStringBuilder* sb = (LangStringBuilder*)GC_malloc(sizeof(LangStringBuilder));
    sb->cap = 64;
    sb->buf = (char*)GC_malloc((size_t)sb->cap);
    sb->len = 0;
    return sb;
}

LangStringBuilder* lang_sb_append(LangStringBuilder* sb, LangString* s) {
    int64_t new_len = sb->len + s->length;
    if (new_len >= sb->cap) {
        int64_t new_cap = sb->cap * 2;
        while (new_cap <= new_len) new_cap *= 2;
        char* new_buf = (char*)GC_malloc((size_t)new_cap);
        memcpy(new_buf, sb->buf, (size_t)sb->len);
        sb->buf = new_buf;
        sb->cap = new_cap;
    }
    memcpy(sb->buf + sb->len, s->data, (size_t)s->length);
    sb->len = new_len;
    return sb;
}

LangString* lang_sb_tostring(LangStringBuilder* sb) {
    char* data = (char*)GC_malloc((size_t)(sb->len + 1));
    memcpy(data, sb->buf, (size_t)sb->len);
    data[sb->len] = '\0';
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->heap_tag = LANG_HEAP_TAG_STRING;
    s->length = sb->len;
    s->data = data;
    return s;
}

/* Phase 33-01: COL-02 HashSet (struct defined in lang_runtime.h)
 * HashSet stores raw (untagged) integers, so uses murmurhash only (no LSB dispatch). */
/* Phase 91: HashSet uses unified lang_ht_hash/lang_ht_eq from Phase 90.
 * Values stored as-is (tagged ints, raw pointers). */
LangHashSet* lang_hashset_create(void) {
    LangHashSet* hs = (LangHashSet*)GC_malloc(sizeof(LangHashSet));
    hs->capacity = 16;
    hs->size = 0;
    hs->buckets = (LangHashSetEntry**)GC_malloc((size_t)(16 * (int64_t)sizeof(LangHashSetEntry*)));
    for (int64_t i = 0; i < 16; i++) hs->buckets[i] = NULL;
    return hs;
}

int64_t lang_hashset_add(LangHashSet* hs, int64_t key) {
    uint64_t bucket = lang_ht_hash(key) % (uint64_t)hs->capacity;
    LangHashSetEntry* e = hs->buckets[bucket];
    while (e != NULL) {
        if (lang_ht_eq(e->key, key)) return 0; /* already present */
        e = e->next;
    }
    LangHashSetEntry* ne = (LangHashSetEntry*)GC_malloc(sizeof(LangHashSetEntry));
    ne->key = key;
    ne->next = hs->buckets[bucket];
    hs->buckets[bucket] = ne;
    hs->size++;
    return 1; /* newly added */
}

int64_t lang_hashset_contains(LangHashSet* hs, int64_t key) {
    uint64_t bucket = lang_ht_hash(key) % (uint64_t)hs->capacity;
    LangHashSetEntry* e = hs->buckets[bucket];
    while (e != NULL) {
        if (lang_ht_eq(e->key, key)) return 1;
        e = e->next;
    }
    return 0;
}

int64_t lang_hashset_count(LangHashSet* hs) { return LANG_TAG_INT(hs->size); }

LangCons* lang_hashset_keys(LangHashSet* hs) {
    LangCons* result = NULL;
    for (int64_t i = 0; i < hs->capacity; i++) {
        LangHashSetEntry* e = hs->buckets[i];
        while (e != NULL) {
            LangCons* cell = (LangCons*)GC_malloc(sizeof(LangCons));
            cell->heap_tag = LANG_HEAP_TAG_LIST;
            cell->head = e->key;
            cell->tail = result;
            result = cell;
            e = e->next;
        }
    }
    return result;
}

/* Phase 33-02: COL-03 Queue (struct defined in lang_runtime.h) */
LangQueue* lang_queue_create(void) {
    LangQueue* q = (LangQueue*)GC_malloc(sizeof(LangQueue));
    q->head  = NULL;
    q->tail  = NULL;
    q->count = 0;
    return q;
}

void lang_queue_enqueue(LangQueue* q, int64_t value) {
    LangQueueNode* node = (LangQueueNode*)GC_malloc(sizeof(LangQueueNode));
    node->value = value;
    node->next  = NULL;
    if (q->tail == NULL) {
        q->head = node;
        q->tail = node;
    } else {
        q->tail->next = node;
        q->tail       = node;
    }
    q->count++;
}

int64_t lang_queue_dequeue(LangQueue* q) {
    if (q->head == NULL) {
        lang_failwith("Queue.Dequeue: queue is empty", "<runtime>");
    }
    LangQueueNode* node = q->head;
    int64_t value       = node->value;
    q->head = node->next;
    if (q->head == NULL) q->tail = NULL;
    q->count--;
    return value;
}

int64_t lang_queue_count(LangQueue* q) { return LANG_TAG_INT(q->count); }

/* Phase 33-02: COL-04 MutableList (struct defined in lang_runtime.h) */
LangMutableList* lang_mlist_create(void) {
    LangMutableList* ml = (LangMutableList*)GC_malloc(sizeof(LangMutableList));
    ml->cap  = 8;
    ml->data = (int64_t*)GC_malloc((size_t)(8 * (int64_t)sizeof(int64_t)));
    ml->len  = 0;
    return ml;
}

void lang_mlist_add(LangMutableList* ml, int64_t value) {
    if (ml->len >= ml->cap) {
        int64_t  new_cap  = ml->cap * 2;
        int64_t* new_data = (int64_t*)GC_malloc((size_t)(new_cap * (int64_t)sizeof(int64_t)));
        memcpy(new_data, ml->data, (size_t)(ml->len * (int64_t)sizeof(int64_t)));
        ml->data = new_data;
        ml->cap  = new_cap;
    }
    ml->data[ml->len++] = value;
}

int64_t lang_mlist_get(LangMutableList* ml, int64_t index) {
    index = LANG_UNTAG_INT(index);
    if (index < 0 || index >= ml->len) {
        lang_failwith("MutableList.get: index out of bounds", "<runtime>");
    }
    return ml->data[index];
}

void lang_mlist_set(LangMutableList* ml, int64_t index, int64_t value) {
    index = LANG_UNTAG_INT(index);
    if (index < 0 || index >= ml->len) {
        lang_failwith("MutableList.set: index out of bounds", "<runtime>");
    }
    ml->data[index] = value;
}

int64_t lang_mlist_count(LangMutableList* ml) { return LANG_TAG_INT(ml->len); }

LangCons* lang_mlist_to_list(LangMutableList* ml) {
    LangCons* result = NULL;
    for (int64_t i = ml->len - 1; i >= 0; i--) {
        LangCons* cell = (LangCons*)GC_malloc(sizeof(LangCons));
        cell->heap_tag = LANG_HEAP_TAG_LIST;
        cell->head = ml->data[i];
        cell->tail = result;
        result = cell;
    }
    return result;
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
        msg->heap_tag = LANG_HEAP_TAG_STRING;
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
    s->heap_tag = LANG_HEAP_TAG_STRING;
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

// Phase 98: Raw C string trace to stderr (for --trace flag, no GC/LangString needed)
void lang_trace(const char* s) {
    fputs(s, stderr);
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
        msg->heap_tag = LANG_HEAP_TAG_STRING;
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
        s->heap_tag = LANG_HEAP_TAG_STRING;
        s->length = len;
        s->data = data;
        LangCons* cell = (LangCons*)GC_malloc(sizeof(LangCons));
        cell->heap_tag = LANG_HEAP_TAG_LIST;
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
    s->heap_tag = LANG_HEAP_TAG_STRING;
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
    s->heap_tag = LANG_HEAP_TAG_STRING;
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
        msg->heap_tag = LANG_HEAP_TAG_STRING;
        msg->length = total_len;
        msg->data = buf;
        lang_throw((void*)msg);
        return NULL; /* unreachable */
    }
    int64_t len = (int64_t)strlen(val);
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    memcpy(buf, val, (size_t)(len + 1));
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->heap_tag = LANG_HEAP_TAG_STRING;
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
        msg->heap_tag = LANG_HEAP_TAG_STRING;
        msg->length = msg_len;
        msg->data = buf;
        lang_throw((void*)msg);
        return NULL; /* unreachable */
    }
    int64_t len = (int64_t)strlen(tmp);
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    memcpy(buf, tmp, (size_t)(len + 1));
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->heap_tag = LANG_HEAP_TAG_STRING;
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
    s->heap_tag = LANG_HEAP_TAG_STRING;
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
        msg->heap_tag = LANG_HEAP_TAG_STRING;
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
        s->heap_tag = LANG_HEAP_TAG_STRING;
        s->length = full_len;
        s->data = full;
        LangCons* cell = (LangCons*)GC_malloc(sizeof(LangCons));
        cell->heap_tag = LANG_HEAP_TAG_LIST;
        cell->head = (int64_t)(uintptr_t)s;
        cell->tail = NULL;
        *cursor = cell;
        cursor = &cell->tail;
    }
    closedir(d);
    return head;
}

/* Phase 38: CLI argument support */
/* Phase 39: Format string wrappers (snprintf delegation) */
LangString* lang_sprintf_1i(char* fmt, int64_t a) {
    a = LANG_UNTAG_INT(a);
    int len = snprintf(NULL, 0, fmt, (long)a);
    if (len < 0) len = 0;
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    snprintf(buf, (size_t)(len + 1), fmt, (long)a);
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->heap_tag = LANG_HEAP_TAG_STRING;
    s->length = (int64_t)len;
    s->data = buf;
    return s;
}

LangString* lang_sprintf_1s(char* fmt, char* a) {
    int len = snprintf(NULL, 0, fmt, a);
    if (len < 0) len = 0;
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    snprintf(buf, (size_t)(len + 1), fmt, a);
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->heap_tag = LANG_HEAP_TAG_STRING;
    s->length = (int64_t)len;
    s->data = buf;
    return s;
}

LangString* lang_sprintf_2ii(char* fmt, int64_t a, int64_t b) {
    a = LANG_UNTAG_INT(a);
    b = LANG_UNTAG_INT(b);
    int len = snprintf(NULL, 0, fmt, (long)a, (long)b);
    if (len < 0) len = 0;
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    snprintf(buf, (size_t)(len + 1), fmt, (long)a, (long)b);
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->heap_tag = LANG_HEAP_TAG_STRING;
    s->length = (int64_t)len;
    s->data = buf;
    return s;
}

LangString* lang_sprintf_2si(char* fmt, char* a, int64_t b) {
    b = LANG_UNTAG_INT(b);
    int len = snprintf(NULL, 0, fmt, a, (long)b);
    if (len < 0) len = 0;
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    snprintf(buf, (size_t)(len + 1), fmt, a, (long)b);
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->heap_tag = LANG_HEAP_TAG_STRING;
    s->length = (int64_t)len;
    s->data = buf;
    return s;
}

LangString* lang_sprintf_2is(char* fmt, int64_t a, char* b) {
    a = LANG_UNTAG_INT(a);
    int len = snprintf(NULL, 0, fmt, (long)a, b);
    if (len < 0) len = 0;
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    snprintf(buf, (size_t)(len + 1), fmt, (long)a, b);
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->heap_tag = LANG_HEAP_TAG_STRING;
    s->length = (int64_t)len;
    s->data = buf;
    return s;
}

LangString* lang_sprintf_2ss(char* fmt, char* a, char* b) {
    int len = snprintf(NULL, 0, fmt, a, b);
    if (len < 0) len = 0;
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    snprintf(buf, (size_t)(len + 1), fmt, a, b);
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->heap_tag = LANG_HEAP_TAG_STRING;
    s->length = (int64_t)len;
    s->data = buf;
    return s;
}

static int64_t  s_argc = 0;
static char**   s_argv = NULL;

void lang_init_args(int64_t argc, char** argv) {
    s_argc = argc;
    s_argv = argv;
}

LangCons* lang_get_args(void) {
    LangCons* head = NULL;
    LangCons** cursor = &head;
    /* Start from i=1 to skip argv[0] (program name) */
    for (int64_t i = 1; i < s_argc; i++) {
        int64_t len = (int64_t)strlen(s_argv[i]);
        char* buf = (char*)GC_malloc((size_t)(len + 1));
        memcpy(buf, s_argv[i], (size_t)(len + 1));
        LangString* s = (LangString*)GC_malloc(sizeof(LangString));
        s->heap_tag = LANG_HEAP_TAG_STRING;
        s->length = len;
        s->data = buf;
        LangCons* cell = (LangCons*)GC_malloc(sizeof(LangCons));
        cell->heap_tag = LANG_HEAP_TAG_LIST;
        cell->head = (int64_t)(uintptr_t)s;
        cell->tail = NULL;
        *cursor = cell;
        cursor = &cell->tail;
    }
    return head;
}

/* C entry point — calls compiler-generated @_fnc_entry.
 * This allows user code to define a function named "main" without conflict. */
extern int64_t _fnc_entry(int64_t argc, char** argv);

int main(int argc, char** argv) {
    lang_install_signal_handlers();
    return (int)_fnc_entry((int64_t)argc, argv);
}
