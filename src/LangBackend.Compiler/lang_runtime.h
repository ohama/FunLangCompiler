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

typedef int64_t (*LangClosureFn)(void* env, int64_t arg);
void lang_array_iter(void* closure, int64_t* arr);
int64_t* lang_array_map(void* closure, int64_t* arr);
int64_t lang_array_fold(void* closure, int64_t init, int64_t* arr);
int64_t* lang_array_init(int64_t n, void* closure);

/* Forward declaration for LangString (defined in lang_runtime.c) */
struct LangString_s;
typedef struct LangString_s LangString;
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

#endif
