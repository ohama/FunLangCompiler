# Phase 19: Exception Handling - Research

**Researched:** 2026-03-27
**Domain:** setjmp/longjmp C runtime + MLIR LLVM dialect exception elaboration in F# compiler backend
**Confidence:** HIGH

## Summary

Phase 19 implements exception handling via a C runtime extension (`lang_runtime.c`) and two new elaboration cases in `Elaboration.fs`. The work splits into three tasks matching requirements EXN-01 through EXN-06: C runtime extension, `Raise` elaboration, and `TryWith` elaboration.

The critical design constraint is decision C-15: `lang_try_enter` MUST call `setjmp` via a `static inline` function or macro defined in a C header — NOT via an out-of-line C function. This is because `setjmp` must be called in the **same stack frame** where the `jmp_buf` lives; passing a `jmp_buf*` to a separate C function and calling `setjmp` there creates a dangling-frame bug when `longjmp` unwinds past that frame. The `static inline`/macro approach keeps `setjmp` in the caller's frame while still wrapping the call for the MLIR-emitted code to invoke. The `jmp_buf` field inside `LangExnFrame` must be stack-allocated (alloca in MLIR) inside the try-body function, NOT heap-allocated.

Decision C-16 specifies that the handler stack is popped (`lang_try_exit`) **before** the handler body executes. This prevents double-free and ensures `longjmp` inside a handler propagates to the outer frame.

The MLIR-emitted code calls `@lang_try_enter`, `@lang_try_exit`, and `@lang_throw` as ordinary external `llvm.func` declarations — same pattern as existing `@lang_match_failure`, `@lang_string_concat`, etc. No new MlirIR.fs DU cases are needed. The only additions are: new external function declarations in `elaborateProgram`, stack-alloca ops for `LangExnFrame`, and new `Raise`/`TryWith` cases in `elaborateExpr`.

**Primary recommendation:** Implement C runtime first (19-01), then `Raise` (19-02), then `TryWith` (19-03). Each task is independently verifiable with `.flt` tests before moving on.

## Standard Stack

This phase adds no new NuGet packages. All tooling is already present.

### Core (already present)
| Component | Location | Purpose |
|-----------|----------|---------|
| `lang_runtime.c` | `src/LangBackend.Compiler/` | Add `LangExnFrame`, `lang_try_enter`, `lang_try_exit`, `lang_throw`, `lang_current_exception` |
| `Elaboration.fs` | `src/LangBackend.Compiler/` | Add `Raise` and `TryWith` cases to `elaborateExpr` |
| `MlirIR.fs` | `src/LangBackend.Compiler/` | No new DU cases required; existing `LlvmAllocaOp`, `LlvmCallVoidOp`, `LlvmCallOp`, `CfCondBrOp`, `CfBrOp`, `LlvmUnreachableOp` cover everything |
| `Pipeline.fs` | `src/LangBackend.Compiler/` | No changes — `lang_runtime.c` already compiled and linked via `runtimeSrc`/`runtimeObj` |
| `Boehm GC (libgc)` | system | Already linked; `GC_malloc` not needed for frame (stack alloca) |

### Installation
No new packages. Build with existing `dotnet build`.

## Architecture Patterns

### C Runtime Extension Layout

```c
/* lang_runtime.h (new file) — must be #include-d by lang_runtime.c */
#ifndef LANG_RUNTIME_H
#define LANG_RUNTIME_H
#include <setjmp.h>
#include <stdint.h>

/* Exception frame — stack-allocated by MLIR-emitted code via llvm.alloca */
typedef struct LangExnFrame {
    jmp_buf          buf;      /* setjmp state — must NOT be moved after setjmp call */
    struct LangExnFrame *prev; /* previous handler on stack (NULL = top) */
} LangExnFrame;

/* Thread-local handler stack head */
extern LangExnFrame *lang_exn_top;

/* Current exception value (ADT DataValue ptr) — set by lang_throw */
extern void *lang_current_exception_val;

/* Push frame and call setjmp.
   MUST be static inline / macro so setjmp runs in the same frame as buf. */
static inline int lang_try_enter(LangExnFrame *frame) {
    frame->prev = lang_exn_top;
    lang_exn_top = frame;
    return setjmp(frame->buf);
}

/* Pop handler frame — called before executing handler body (C-16) */
void lang_try_exit(void);

/* Set current exception and longjmp to nearest handler, or abort if none */
void lang_throw(void *exn_val);

/* Return current exception value */
void *lang_current_exception(void);

#endif /* LANG_RUNTIME_H */
```

```c
/* Additions to lang_runtime.c */
#include "lang_runtime.h"
#include <stdio.h>
#include <stdlib.h>

LangExnFrame *lang_exn_top = NULL;
void         *lang_current_exception_val = NULL;

void lang_try_exit(void) {
    if (lang_exn_top != NULL)
        lang_exn_top = lang_exn_top->prev;
}

void lang_throw(void *exn_val) {
    lang_current_exception_val = exn_val;
    if (lang_exn_top == NULL) {
        /* Unhandled — EXN-06: print tag and abort */
        /* ADT block: slot 0 = i64 tag; for Failure slot 1 = LangString* */
        fprintf(stderr, "Fatal: unhandled exception\n");
        exit(1);
    }
    LangExnFrame *frame = lang_exn_top;
    /* Do NOT pop here; MLIR-emitted code calls lang_try_exit before handler body */
    longjmp(frame->buf, 1);
}

void *lang_current_exception(void) {
    return lang_current_exception_val;
}
```

**Critical constraint:** `lang_try_enter` is `static inline` so `setjmp` runs in the **same C stack frame** that allocates `LangExnFrame`. If `lang_try_enter` were an out-of-line function, `longjmp` would restore a destroyed stack frame — undefined behavior. This is decision C-15.

### MLIR Alloca for LangExnFrame

The `LangExnFrame` struct on a typical x86-64 Linux system is `sizeof(jmp_buf) + sizeof(ptr)` bytes. `jmp_buf` is 200 bytes on Linux x86-64, 148 bytes on macOS arm64. The safest approach is to use a **fixed worst-case alloca size** computed at C compile time, exposed via a constant or to over-allocate generously (e.g., 256 bytes for jmp_buf + 8 bytes for `prev` ptr = 264 bytes, rounded to 272 for alignment).

However, the cleanest approach for this project is: `lang_try_enter` is static inline — MLIR code allocates a raw `!llvm.ptr`-sized byte array via `llvm.alloca`, then passes the pointer to `lang_try_enter`. The size constant must be either platform-determined or set conservatively large. A practical choice: alloca 272 bytes (enough for any platform's jmp_buf + one ptr).

Alternatively, declare a C helper that returns `sizeof(LangExnFrame)` and call it at startup — but this adds runtime complexity. The **recommended approach** is a compile-time constant exposed via a C `#define` in the header used when MLIR code is compiled, OR simply alloca a fixed 272-byte block (conservative upper bound that works on both macOS arm64 and Linux x86-64).

The MLIR `llvm.alloca` for the frame:
```
%frame_size = arith.constant 272 : i64
%frame_ptr = llvm.alloca %frame_size x i8 : (i64) -> !llvm.ptr
```

Note: The existing `LlvmAllocaOp` in `MlirIR.fs` emits `llvm.alloca %count x !llvm.struct<(...)>`. For the exception frame we want `llvm.alloca %count x i8` to get a raw byte buffer. This requires either:
1. Adding a new `LlvmAllocaBytesOp` case to `MlirIR.fs`, OR
2. Using the existing `LlvmAllocaOp` with `numCaptures = 0` and checking if it emits `ptr` only (it emits `!llvm.struct<(ptr)>` which may not be what we want), OR
3. Emitting the alloca via `LlvmCallOp("@__builtin_alloca", ...)` — not portable, OR
4. Using `GC_malloc` for the frame (simplest, avoids alloca complexity, acceptable since the GC won't collect it during the try block).

**Recommended: Use `GC_malloc` for `LangExnFrame`** instead of alloca. Rationale: This sidesteps the alloca-size portability issue entirely. The frame pointer is stable (heap-allocated), and Boehm GC is conservative so it will not free the frame while there is a live C pointer to it on the stack. The `lang_try_enter` static inline still works correctly because `setjmp` is called in the frame where the frame **pointer** (not the buf) lives. The buf is in GC-heap memory which is persistent. This matches OCaml's behavior — OCaml also heap-allocates its exception frames.

Updated runtime signature if using GC_malloc for frame:
```c
/* Called from MLIR: allocate frame, push onto stack, call setjmp */
/* Returns setjmp result: 0 = normal entry, non-zero = longjmp return (exception caught) */
static inline int lang_try_enter(LangExnFrame *frame) {
    frame->prev = lang_exn_top;
    lang_exn_top = frame;
    return setjmp(frame->buf);
}
/* MLIR side: call GC_malloc(272) → ptr; then call lang_try_enter(ptr) */
```

If GC_malloc is used, the existing `LlvmCallOp` for `@GC_malloc` handles frame allocation. `lang_try_enter` takes the `Ptr` result and returns `I64` (the setjmp return value: 0 or 1).

### Pattern: Raise Elaboration (EXN-02)

```fsharp
// In elaborateExpr, new case Raise(exprExpr, _):
| Raise(exnExpr, _) ->
    let (exnVal, exnOps) = elaborateExpr env exnExpr
    // exnVal : Ptr — the ADT DataValue block allocated by Constructor elaboration
    // Call @lang_throw(exnVal) — void return, noreturn
    let throwOp  = LlvmCallVoidOp("@lang_throw", [exnVal])
    let deadVal  = { Name = freshName env; Type = I64 }
    // llvm.unreachable terminates the block after noreturn call
    // Return a dead value to satisfy elaborateExpr's (MlirValue * MlirOp list) contract
    let deadOps  = exnOps @ [throwOp; LlvmUnreachableOp]
    (deadVal, deadOps)
```

The `deadVal` is never used — `LlvmUnreachableOp` terminates the block. But `elaborateExpr` must return a value; using a fresh `I64` name satisfies the type system without emitting any extra ops.

### Pattern: TryWith Elaboration (EXN-03, EXN-04, EXN-05, EXN-06)

TryWith is the most complex elaboration in Phase 19. It uses the same multi-block pattern as `Match` (see Elaboration.fs lines 903-1216) but with setjmp branching instead of constructor tag tests.

```
[entry block of TryWith]
  %frame_size = arith.constant 272 : i64
  %frame = llvm.call @GC_malloc(%frame_size) : (i64) -> !llvm.ptr
  %setjmp_result = llvm.call @lang_try_enter(%frame) : (!llvm.ptr) -> i64
  %zero = arith.constant 0 : i64
  %is_exn = arith.cmpi eq, %setjmp_result, %zero : i64
  cf.cond_br %is_exn, ^try_body, ^exn_caught

^try_body:
  [elaborate body expr]
  llvm.call @lang_try_exit() : () -> ()
  cf.br ^merge(%body_result : i64)

^exn_caught:
  [lang_try_exit() call — pop handler BEFORE dispatching (C-16)]
  llvm.call @lang_try_exit() : () -> ()
  %exn_ptr = llvm.call @lang_current_exception() : () -> !llvm.ptr
  [dispatch decision tree over handlers — same as Match elaboration]
  [each handler arm: extract fields from exn_ptr, bind variables, elaborate body]
  [on Fail (no matching handler): call @lang_throw(%exn_ptr) + llvm.unreachable]

^merge(%result: i64):
  [result used by outer expression]
```

**Key insight:** The setjmp branch is inverted — `setjmp` returns 0 on first call (normal try path) and non-zero on longjmp (exception path). The `%is_exn = arith.cmpi eq, %setjmp_result, %zero` check is `true` for the normal path, so `^try_body` is the true branch.

**Handler dispatch:** Re-use the existing `MatchCompiler` infrastructure and `emitDecisionTree`. The exception value returned by `@lang_current_exception()` is a `Ptr` to an ADT block (`{i64 tag, ptr payload}`). The handlers in TryWith are exactly `MatchClause list` — same as `Match`. The only difference is the `Fail` case: instead of branching to `@lang_match_failure`, the unhandled case must call `@lang_throw(%exn_ptr)` followed by `llvm.unreachable` (re-raises to outer handler).

**Nested TryWith (EXN-04):** The linked-list structure of `LangExnFrame` handles nesting automatically. Each `TryWith` allocates a new frame and pushes it onto `lang_exn_top`. When `lang_try_enter` is called for the inner `TryWith`, it stores `lang_exn_top` (the outer frame) in `frame->prev`. On `lang_try_exit`, the outer frame becomes the new top. No special F# handling is needed for nesting — the C runtime handles it.

**Exception with payload (EXN-05):** The payload extraction from `%exn_ptr` uses existing ADT accessor code. `%exn_ptr` is a `Ptr` to `{i64 tag, ptr payload}`. Tag = exception constructor's tag index from `ExnTags`. Payload at slot 1 = the argument (e.g., a `LangString*` for `Failure "msg"`). The existing `AdtCtor` matching path in `emitCtorTest` handles this — pass the exception pointer as the `scrutinee` for the decision tree.

**Exception tag lookup for handlers:** Exception constructors are in `env.ExnTags` (populated by `prePassDecls`). When elaborating a `TryWith` handler with pattern `ConstructorPat("Failure", ...)`, MatchCompiler uses `AdtCtor("Failure", 0, arity)` — but the tag must be looked up from `env.ExnTags`, not `env.TypeEnv`. The existing `emitCtorTest` for `AdtCtor` already looks up `env.TypeEnv`. For exception constructors, the lookup must also check `env.ExnTags`. Solution: in `prePassDecls`, also add exception constructors to `TypeEnv` (same `Map<string, TypeInfo>`) so the existing lookup works uniformly. Exception tags are assigned by `exnCounter` independently from ADT tags — they share the same namespace in `TypeEnv`.

**IMPORTANT:** `prePassDecls` currently adds exception constructors to `ExnTags` (a separate map) but NOT to `TypeEnv`. The `emitCtorTest` for `AdtCtor` only looks in `TypeEnv`. To make exception pattern matching work with the existing decision-tree infrastructure, **also add exception constructors to `TypeEnv` in `prePassDecls`**. The tag values from `ExnTags` and the entries in `TypeEnv` must be consistent.

### Existing Infrastructure Reuse

| What | Where | How Reused |
|------|-------|-----------|
| `LlvmCallVoidOp` | `MlirIR.fs` | Call `@lang_throw`, `@lang_try_exit` |
| `LlvmCallOp` | `MlirIR.fs` | Call `@GC_malloc`, `@lang_try_enter`, `@lang_current_exception` |
| `LlvmUnreachableOp` | `MlirIR.fs` | After `@lang_throw` in Raise and unhandled paths |
| `ArithCmpIOp` | `MlirIR.fs` | Compare setjmp result to 0 |
| `CfCondBrOp` | `MlirIR.fs` | Branch on setjmp result |
| `CfBrOp` | `MlirIR.fs` | Jump to merge block |
| `emitDecisionTree` (local function in Match case) | `Elaboration.fs` | Copy/adapt for TryWith handler dispatch |
| `AdtCtor` match in `emitCtorTest` | `Elaboration.fs` | Tag comparison for exception constructor patterns |
| `freshLabel`, `freshName` | `Elaboration.fs` | Label/value generation |
| External func decls in `elaborateProgram` | `Elaboration.fs` | Add `@lang_try_enter`, `@lang_try_exit`, `@lang_throw`, `@lang_current_exception` |

### Recommended Project Structure Changes

```
src/LangBackend.Compiler/
├── lang_runtime.h           # NEW — LangExnFrame typedef, static inline lang_try_enter
├── lang_runtime.c           # EXTENDED — lang_try_exit, lang_throw, lang_current_exception
├── Elaboration.fs           # EXTENDED — Raise and TryWith cases
└── MlirIR.fs                # NO CHANGES needed
```

The `Pipeline.fs` build step already compiles `lang_runtime.c` with `-c`. Since `lang_runtime.h` is in the same directory and `lang_runtime.c` does `#include "lang_runtime.h"`, the header is found automatically by Clang.

### Anti-Patterns to Avoid

- **Out-of-line `lang_try_enter` function:** If `setjmp` is called inside a non-inline C function, `longjmp` will restore a destroyed stack frame (undefined behavior). MUST be `static inline` or macro. Decision C-15.
- **Heap-allocating jmp_buf separately from LangExnFrame:** If `jmp_buf` is in a separately-allocated block, the GEP/ptr arithmetic in `lang_try_enter` still works, but the single GC_malloc approach (full `LangExnFrame` struct) is cleaner.
- **Popping handler after body execution:** The handler pop must happen BEFORE the handler body, not after. If the handler body re-raises or calls a function that raises, the handler must already be removed from the stack. Decision C-16.
- **Using LLVM `llvm.eh.sjlj.setjmp` intrinsic:** The LLVM SJLJ EH intrinsic requires manual population of frameaddress and stacksave — much more complex than calling C's `setjmp` via a static inline wrapper. Use the C stdlib `setjmp` through a static inline.
- **Adding exception constructors only to `ExnTags` but not `TypeEnv`:** The existing `emitCtorTest` for `AdtCtor` looks up `TypeEnv` only. Exception handler patterns will fail silently at elaboration time if exceptions are not also in `TypeEnv`.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| setjmp/longjmp mechanics | Custom stack-save assembly | C stdlib `setjmp`/`longjmp` via `static inline` wrapper | Correct platform ABI handling, well-tested |
| Handler dispatch decision tree | New matching algorithm | Existing `MatchCompiler.compile` + `emitDecisionTree` | All the ADT tag matching logic already exists |
| GC frame allocation | `llvm.alloca` with platform-specific size | `@GC_malloc(272)` for `LangExnFrame` | Avoids portability issues with `jmp_buf` size differences across platforms |
| External function declarations | New IR types | Existing `ExternalFuncDecl` pattern in `elaborateProgram` | Four new entries follow the exact same pattern as existing ones |

**Key insight:** The biggest implementation risk is the `jmp_buf` size problem. Using `GC_malloc` for `LangExnFrame` instead of `llvm.alloca` eliminates this entirely and is the recommended approach.

## Common Pitfalls

### Pitfall 1: setjmp Called Outside Its Stack Frame
**What goes wrong:** `longjmp` triggers undefined behavior (crash or silent corruption) when called after the function containing the `setjmp` has returned.
**Why it happens:** Passing `&frame->buf` to an out-of-line `lang_try_enter` function and calling `setjmp` there — the function returns before `longjmp` is ever called, destroying the saved stack frame.
**How to avoid:** `lang_try_enter` must be `static inline` (or a macro) so `setjmp` runs in the same frame as the `LangExnFrame` allocation.
**Warning signs:** Segfault on any `raise` expression; works only when `try-with` body returns without raising.

### Pitfall 2: Handler Not Popped Before Body Execution
**What goes wrong:** If a handler body calls `raise` again (re-raise or a nested raise), `longjmp` jumps back to the **same** handler, causing infinite loop or double execution.
**Why it happens:** `lang_try_exit` called after the handler body instead of before (violates C-16).
**How to avoid:** Emit `LlvmCallVoidOp("@lang_try_exit", [])` at the START of `^exn_caught` before the handler decision tree.
**Warning signs:** Re-raise inside a handler does not propagate to outer handler; stack grows unboundedly on repeated exceptions.

### Pitfall 3: Exception Constructors Not in TypeEnv
**What goes wrong:** `emitCtorTest` for `AdtCtor("Failure", _, _)` raises `KeyNotFoundException` because `TypeEnv` has no entry for `"Failure"`.
**Why it happens:** `prePassDecls` currently puts exception constructors only in `ExnTags`, not in `TypeEnv`.
**How to avoid:** In `prePassDecls`, for each `ExceptionDecl(name, dataType, _)`, also add `Map.add name { Tag = tag; Arity = arity } typeEnv` where `arity = match dataType with None -> 0 | Some _ -> 1`.
**Warning signs:** Runtime exception / F# exception thrown during elaboration when any TryWith handler pattern references an exception constructor.

### Pitfall 4: Merge Block Result Type Mismatch
**What goes wrong:** MLIR type error — merge block argument type doesn't match values from try-body and handler arms.
**Why it happens:** The try body and all handler arms must produce the same `MlirType`. If a handler arm produces `Ptr` (e.g., returns a string) but the try body produces `I64`, the merge block argument type is wrong.
**How to avoid:** Use the same `resultType ref` pattern as in `Match` elaboration (Elaboration.fs line 1126). Set it from the first leaf value and verify subsequent arms match.
**Warning signs:** `mlir-opt` error about mismatched block argument types.

### Pitfall 5: jmp_buf Size Platform Mismatch
**What goes wrong:** `LangExnFrame` alloca is too small for the platform's `jmp_buf`, corrupting adjacent stack data.
**Why it happens:** `jmp_buf` is 200 bytes on Linux x86-64, 148 bytes on macOS arm64, other sizes on other platforms.
**How to avoid:** Use `GC_malloc(272)` for the full `LangExnFrame` (conservative upper bound). 272 = 256 (jmp_buf) + 8 (prev ptr) + 8 (padding). OR include `lang_runtime.h` in a C file that computes `sizeof(LangExnFrame)` and export a constant.
**Warning signs:** Crash only on specific platforms; works on macOS but fails on Linux or vice versa.

### Pitfall 6: lang_try_exit Called Twice (Normal Path)
**What goes wrong:** Handler stack is popped twice — once in `^try_body` (normal exit) and once if some code path re-enters the exception path.
**Why it happens:** Misplaced `lang_try_exit` call.
**How to avoid:** `lang_try_exit` is called exactly once per TryWith: in `^try_body` after the body succeeds (before merging) AND in `^exn_caught` before handler dispatch. These are in separate basic blocks — only one path executes per TryWith invocation.
**Warning signs:** Nested TryWith causes outer handler to be prematurely popped; exceptions escape to wrong handler.

## Code Examples

### External Function Declarations (add to `elaborateProgram`)

```fsharp
// Source: Elaboration.fs lines 1600-1614 (existing pattern)
{ ExtName = "@lang_try_enter";          ExtParams = [Ptr];  ExtReturn = Some I64; IsVarArg = false }
{ ExtName = "@lang_try_exit";           ExtParams = [];     ExtReturn = None;     IsVarArg = false }
{ ExtName = "@lang_throw";              ExtParams = [Ptr];  ExtReturn = None;     IsVarArg = false }
{ ExtName = "@lang_current_exception";  ExtParams = [];     ExtReturn = Some Ptr; IsVarArg = false }
```

### Raise Case in elaborateExpr

```fsharp
// Source: codebase analysis — follows existing LlvmUnreachableOp usage (Elaboration.fs ~1206-1209)
| Raise(exnExpr, _) ->
    let (exnVal, exnOps) = elaborateExpr env exnExpr
    // exnVal.Type = Ptr (ADT DataValue block)
    let deadVal = { Name = freshName env; Type = I64 }
    (deadVal, exnOps @ [ LlvmCallVoidOp("@lang_throw", [exnVal]); LlvmUnreachableOp ])
```

### TryWith Frame Allocation

```fsharp
// Source: codebase analysis — follows GC_malloc pattern (Elaboration.fs ~1330-1331)
let frameSizeVal = { Name = freshName env; Type = I64 }
let framePtr     = { Name = freshName env; Type = Ptr }
let frameOps = [
    ArithConstantOp(frameSizeVal, 272L)    // sizeof(LangExnFrame) conservatively
    LlvmCallOp(framePtr, "@GC_malloc", [frameSizeVal])
]
```

### setjmp Branch in TryWith

```fsharp
// Source: codebase analysis — ArithCmpIOp + CfCondBrOp pattern
let setjmpResult = { Name = freshName env; Type = I64 }
let zero64       = { Name = freshName env; Type = I64 }
let isNormal     = { Name = freshName env; Type = I1 }
let tryBodyLabel = freshLabel env "try_body"
let exnCaughtLabel = freshLabel env "exn_caught"
let entryBranchOps = [
    LlvmCallOp(setjmpResult, "@lang_try_enter", [framePtr])
    ArithConstantOp(zero64, 0L)
    ArithCmpIOp(isNormal, "eq", setjmpResult, zero64)
    CfCondBrOp(isNormal, tryBodyLabel, [], exnCaughtLabel, [])
]
```

### ExnCaught Block Structure

```fsharp
// Source: codebase analysis — follows Match block emission pattern (Elaboration.fs ~1204-1216)
let exnPtrVal = { Name = freshName env; Type = Ptr }
let exnExitOp = LlvmCallVoidOp("@lang_try_exit", [])  // C-16: pop before dispatch
let exnLoadOps = [
    LlvmCallOp(exnPtrVal, "@lang_current_exception", [])
]
// Then run MatchCompiler.compile on the TryWith handlers with exnPtrVal as scrutinee
// Fail case → call @lang_throw(exnPtrVal) + LlvmUnreachableOp (re-raise to outer)
```

### prePassDecls Exception Tag Fix

```fsharp
// Source: codebase analysis — Elaboration.fs lines 1519-1522
| Ast.Decl.ExceptionDecl(name, dataTypeOpt, _) ->
    let tag   = exnCounter.Value
    exnCounter.Value <- tag + 1
    exnTags <- Map.add name tag exnTags
    // FIX: also add to typeEnv so AdtCtor pattern matching finds the tag
    let arity = match dataTypeOpt with None -> 0 | Some _ -> 1
    typeEnv <- Map.add name { Tag = tag; Arity = arity } typeEnv
```

### Test File Format (for .flt E2E tests)

```
// --- Command: bash -c 'OUTBIN=$(mktemp /tmp/langback_XXXXXX) && dotnet run --project %S/../../src/LangBackend.Cli/LangBackend.Cli.fsproj -- %input -o $OUTBIN && $OUTBIN; echo $?; rm -f $OUTBIN'
// --- Input:
exception Failure of string
let _ = try raise (Failure "x") with
        | Failure msg -> string_length msg
// --- Output:
1
```

## State of the Art

| Old Approach | Current Approach | Notes |
|--------------|------------------|-------|
| LLVM EH Landingpad (C++ style) | setjmp/longjmp via C runtime | Landingpad requires LLVM personality functions; much more complex for simple interpreters/VMs |
| LLVM SJLJ intrinsics (`llvm.eh.sjlj.setjmp`) | C stdlib `setjmp` via static inline | LLVM SJLJ intrinsics require manual frameaddress + stacksave; the C stdlib version is simpler and more portable |
| Stack alloca for jmp_buf | GC_malloc for LangExnFrame | Avoids platform jmp_buf size portability issues |

**Current standard for simple language runtimes (MicroML, toy compilers):** C `setjmp`/`longjmp` with a thread-local linked-list handler stack, exception value stored as a global/thread-local. This is exactly what OCaml used before OCaml 5 (effects), and what this design implements.

## Open Questions

1. **`lang_try_enter` signature seen from MLIR**
   - What we know: It's declared as `static inline` in the header, but from MLIR's perspective it will be called as an external `llvm.func` declaration returning `i64`. The linker must find the symbol.
   - What's unclear: Does `static inline` in a C header generate a symbol visible to the linker when `lang_runtime.c` includes it? Answer: Yes — when `lang_runtime.c` includes `lang_runtime.h`, Clang generates a non-inline version of `lang_try_enter` as part of the translation unit (the inline linkage ensures the definition is available). The MLIR-generated `.ll` file declares it as an external, and the linker resolves it from `lang_runtime.o`.
   - Recommendation: Verify by checking that `nm lang_runtime.o | grep lang_try_enter` shows a symbol after compiling. If not, use `__attribute__((used))` or make it non-static `inline` (C99/C11 inline semantics differ — use `static inline` for simplicity).

2. **ExnTags vs TypeEnv separation**
   - What we know: `ExnTags` exists as a separate map. `TypeEnv` is used by `emitCtorTest` for ADT matching.
   - What's unclear: Whether the existing code ever uses `ExnTags` for anything in the backend (it appears to be populated but never consumed currently).
   - Recommendation: In Phase 19, unify by also adding exceptions to `TypeEnv` in `prePassDecls`. The `ExnTags` field can remain for any future use but is not relied upon by Phase 19 elaboration.

3. **Return type of TryWith when body is `unit`**
   - What we know: The try body returns some `MlirValue`. If the body contains a `raise` expression (which is noreturn), the body block terminates with `llvm.unreachable`.
   - What's unclear: How to handle the merge block when all paths are noreturn.
   - Recommendation: Use the same `resultType ref` approach as in `Match`. If the body is noreturn, the merge block is unreachable but must still exist in the MLIR output for structural validity. The merge block arg type defaults to `I64` if no leaf ever sets `resultType`.

## Sources

### Primary (HIGH confidence)
- Codebase: `src/LangBackend.Compiler/Elaboration.fs` — `Match` elaboration pattern (lines 900-1216) is the direct template for `TryWith`
- Codebase: `src/LangBackend.Compiler/lang_runtime.c` — existing runtime structure; `lang_match_failure` is the template for `lang_throw`
- Codebase: `src/LangBackend.Compiler/MlirIR.fs` — all existing op types; no new types needed
- Codebase: `src/LangBackend.Compiler/Pipeline.fs` — how `lang_runtime.c` is compiled and linked

### Secondary (MEDIUM confidence)
- [setjmp/longjmp in LLVM IR — folkertdev gist](https://gist.github.com/folkertdev/b5d330c0126c16b957b57a3c0cd89879) — confirmed LLVM intrinsics vs C stdlib difference; why C stdlib `setjmp` via wrapper is correct
- [setjmp(), longjmp(), and Exception Handling in C — DEV Community](https://dev.to/pauljlucas/setjmp-longjmp-and-exception-handling-in-c-1h7h) — linked-list handler stack pattern confirmed; `cx_impl_try_block_t` is structurally identical to `LangExnFrame`
- [Make setjmp-calling functions uninlinable — LLVM Issue #54447](https://github.com/llvm/llvm-project/issues/54447) — confirmed that `returns_twice` must be in the same frame; confirms static inline requirement

### Tertiary (LOW confidence)
- [Setjmp/Longjmp Exception Handling — Mapping High Level Constructs to LLVM IR](https://mapping-high-level-constructs-to-llvm-ir.readthedocs.io/en/latest/exception-handling/setjmp+longjmp-exception-handling.html) — general patterns; site returned 403 so content not directly verified

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all existing tools, no new packages
- Architecture (C runtime): HIGH — derived from codebase analysis + well-established setjmp/longjmp pattern
- Architecture (MLIR elaboration): HIGH — direct analogy to existing `Match` case
- Pitfalls: HIGH — setjmp/longjmp semantic requirements are well-documented; codebase-specific pitfalls derived from code analysis
- `lang_try_enter` symbol visibility: MEDIUM — standard C behavior but should be verified

**Research date:** 2026-03-27
**Valid until:** 2026-04-27 (stable domain; MLIR 20 LLVM dialect is established)
