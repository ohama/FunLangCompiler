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

#endif
