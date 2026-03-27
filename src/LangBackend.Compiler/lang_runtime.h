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

/* Push frame onto handler stack and call setjmp.
   Returns 0 on normal entry, non-zero on longjmp (exception caught).

   __attribute__((returns_twice)) tells the compiler this function may return
   more than once (like setjmp itself), preventing stack frame optimizations
   that would break longjmp. The jmp_buf lives in a GC_malloc'd LangExnFrame
   (heap-allocated), so it persists after this function returns.

   This is the same out-of-line-setjmp-wrapper pattern used by OCaml 4.x
   (caml_setjmp) and Lua (luaD_rawrunprotected). It works reliably with
   clang on all platforms when returns_twice is specified.

   The MLIR-generated code calls this as an external llvm.func. */
__attribute__((returns_twice))
int lang_try_enter(LangExnFrame *frame);

void lang_try_exit(void);
void lang_throw(void *exn_val);
void *lang_current_exception(void);

#endif
