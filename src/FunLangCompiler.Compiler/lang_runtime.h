#ifndef LANG_RUNTIME_H
#define LANG_RUNTIME_H
#include <setjmp.h>
#include <stdint.h>

typedef struct LangExnFrame {
    jmp_buf buf;
    struct LangExnFrame *prev;
} LangExnFrame;

extern LangExnFrame *lang_exn_top;
extern void *lang_current_exception_val;

/* Push frame onto handler stack.
   After calling this, the CALLER must call _setjmp(frame->buf) directly
   to save the setjmp state in the caller's stack frame.
   This avoids the ARM64 PAC/out-of-line-setjmp problem. */
void lang_try_push(LangExnFrame *frame);

void lang_try_exit(void);
void lang_throw(void *exn_val);  // calls _longjmp to top handler
void *lang_current_exception(void);

/* Backward-compat: push + _setjmp combined. */
__attribute__((returns_twice))
int lang_try_enter(LangExnFrame *frame);

typedef struct LangCons LangCons;
int64_t* lang_array_create(int64_t n, int64_t default_val);
void lang_array_bounds_check(int64_t* arr, int64_t i);
int64_t* lang_array_of_list(LangCons* list);
LangCons* lang_array_to_list(int64_t* arr);

typedef struct LangHashEntry {
    int64_t key;
    int64_t val;
    struct LangHashEntry* next;
} LangHashEntry;

typedef struct {
    int64_t tag;        // -1 = hashtable (arrays have non-negative length at offset 0)
    int64_t capacity;
    int64_t size;
    LangHashEntry** buckets;
} LangHashtable;

LangHashtable* lang_hashtable_create(void);
int64_t lang_hashtable_get(LangHashtable* ht, int64_t key);
void lang_hashtable_set(LangHashtable* ht, int64_t key, int64_t val);
int64_t lang_hashtable_containsKey(LangHashtable* ht, int64_t key);
void lang_hashtable_remove(LangHashtable* ht, int64_t key);
LangCons* lang_hashtable_keys(LangHashtable* ht);
int64_t* lang_hashtable_trygetvalue(LangHashtable* ht, int64_t key);
int64_t lang_index_get(void* collection, int64_t index);
void lang_index_set(void* collection, int64_t index, int64_t value);

/* Forward declaration for LangString (defined in lang_runtime.c) */
struct LangString_s;
typedef struct LangString_s LangString;

/* Phase 90: lang_index_get_str / lang_index_set_str — thin wrappers for ht.["key"] syntax.
 * These coerce LangString* key to int64_t and call unified hashtable functions. */
int64_t lang_index_get_str(void* collection, LangString* key);
void lang_index_set_str(void* collection, LangString* key, int64_t value);

typedef int64_t (*LangClosureFn)(void* env, int64_t arg);
void lang_array_iter(void* closure, int64_t* arr);
void lang_for_in(void* closure, void* collection);
void lang_for_in_list(void* closure, void* collection);
void lang_for_in_array(void* closure, void* collection);
LangCons* lang_list_comp(void* closure, void* collection);
int64_t* lang_array_map(void* closure, int64_t* arr);
int64_t lang_array_fold(void* closure, int64_t init, int64_t* arr);
int64_t* lang_array_init(int64_t n, void* closure);
LangString* lang_file_read(LangString* path);
void        lang_file_write(LangString* path, LangString* content);
void        lang_file_append(LangString* path, LangString* content);
int64_t     lang_file_exists(LangString* path);
void        lang_eprint(LangString* s);
void        lang_eprintln(LangString* s);

LangCons*   lang_read_lines(LangString* path);
void        lang_write_lines(LangString* path, LangCons* lines);
LangString* lang_stdin_read_line(void);
LangString* lang_stdin_read_all(void);
LangString* lang_get_env(LangString* varName);
LangString* lang_get_cwd(void);
LangString* lang_path_combine(LangString* dir, LangString* file);
LangCons*   lang_dir_files(LangString* path);

/* Phase 34-01: LANG-01 String slicing */
LangString* lang_string_slice(LangString* s, int64_t start, int64_t stop);

int64_t     lang_string_endswith(LangString* s, LangString* suffix);
int64_t     lang_string_startswith(LangString* s, LangString* prefix);
LangString* lang_string_trim(LangString* s);
LangCons*   lang_string_split(LangString* s, LangString* sep);
int64_t     lang_string_indexof(LangString* s, LangString* sub);
LangString* lang_string_replace(LangString* s, LangString* old_str, LangString* rep);
LangString* lang_string_toupper(LangString* s);
LangString* lang_string_tolower(LangString* s);
LangString* lang_string_concat_list(LangString* sep, LangCons* list);

int64_t lang_char_is_digit(int64_t c);
int64_t lang_char_is_letter(int64_t c);
int64_t lang_char_is_upper(int64_t c);
int64_t lang_char_is_lower(int64_t c);
int64_t lang_char_to_upper(int64_t c);
int64_t lang_char_to_lower(int64_t c);

LangCons* lang_list_sort_by(void* closure, LangCons* list);
LangCons* lang_list_of_seq(void* collection);

void lang_array_sort(int64_t* arr);
int64_t* lang_array_of_seq(void* collection);

/* Phase 33-01: COL-01 StringBuilder */
typedef struct {
    char*   buf;
    int64_t len;
    int64_t cap;
} LangStringBuilder;

LangStringBuilder* lang_sb_create(void);
LangStringBuilder* lang_sb_append(LangStringBuilder* sb, LangString* s);
LangString* lang_sb_tostring(LangStringBuilder* sb);

/* Phase 33-01: COL-02 HashSet */
typedef struct LangHashSetEntry {
    int64_t key;
    struct LangHashSetEntry* next;
} LangHashSetEntry;

typedef struct {
    int64_t capacity;
    int64_t size;
    LangHashSetEntry** buckets;
} LangHashSet;

LangHashSet* lang_hashset_create(void);
int64_t lang_hashset_add(LangHashSet* hs, int64_t key);
int64_t lang_hashset_contains(LangHashSet* hs, int64_t key);
int64_t lang_hashset_count(LangHashSet* hs);

/* Phase 33-02: COL-03 Queue */
typedef struct LangQueueNode {
    int64_t value;
    struct LangQueueNode* next;
} LangQueueNode;

typedef struct {
    LangQueueNode* head;
    LangQueueNode* tail;
    int64_t        count;
} LangQueue;

LangQueue* lang_queue_create(void);
void       lang_queue_enqueue(LangQueue* q, int64_t value);
int64_t    lang_queue_dequeue(LangQueue* q);
int64_t    lang_queue_count(LangQueue* q);

/* Phase 33-02: COL-04 MutableList */
typedef struct {
    int64_t* data;
    int64_t  len;
    int64_t  cap;
} LangMutableList;

LangMutableList* lang_mlist_create(void);
void             lang_mlist_add(LangMutableList* ml, int64_t value);
int64_t          lang_mlist_get(LangMutableList* ml, int64_t index);
void             lang_mlist_set(LangMutableList* ml, int64_t index, int64_t value);
int64_t          lang_mlist_count(LangMutableList* ml);

/* Phase 34-03: LANG-03/04 — for-in loop over Phase 33 collection types */
void lang_for_in_hashset(void* closure, LangHashSet* hs);
void lang_for_in_queue(void* closure, LangQueue* q);
void lang_for_in_mlist(void* closure, LangMutableList* ml);
void lang_for_in_hashtable(void* closure, LangHashtable* ht);

/* Phase 38: CLI argument support */
void      lang_init_args(int64_t argc, char** argv);
LangCons* lang_get_args(void);

/* Phase 39: Format string wrappers (snprintf delegation) */
LangString* lang_sprintf_1i(char* fmt, int64_t a);
LangString* lang_sprintf_1s(char* fmt, char* a);
LangString* lang_sprintf_2ii(char* fmt, int64_t a, int64_t b);
LangString* lang_sprintf_2si(char* fmt, char* a, int64_t b);
LangString* lang_sprintf_2is(char* fmt, int64_t a, char* b);
LangString* lang_sprintf_2ss(char* fmt, char* a, char* b);

#endif
