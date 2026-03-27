---
phase: 19-exception-handling
plan: 01
subsystem: compiler
tags: [fsharp, c-runtime, mlir, elaboration, exception-handling, setjmp, longjmp]

# Dependency graph
requires:
  - phase: 18-records
    provides: Phase 18 complete; prePassDecls/elaborateProgram/ElabEnv patterns established
  - phase: 17-adt-construction-pattern-matching
    provides: TypeEnv/ExnTags in ElabEnv; emitCtorTest tag-lookup pattern
provides:
  - lang_runtime.h: LangExnFrame struct, returns_twice lang_try_enter declaration
  - lang_runtime.c: setjmp-based handler stack (lang_try_enter/exit/throw/current_exception)
  - Elaboration.fs: 4 ExternalFuncDecl entries for exception runtime functions in both elaborate and elaborateProgram
  - Elaboration.fs: prePassDecls adds exception constructors to TypeEnv (not just ExnTags) for emitCtorTest
  - Elaboration.fs: freeVars handles Raise and TryWith via inline patBoundVars
affects:
  - 19-exception-handling-02 (TryWith/Raise elaboration codegen depends on runtime + scaffolding)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Out-of-line setjmp wrapper with returns_twice (same pattern as OCaml caml_setjmp, Lua luaD_rawrunprotected)"
    - "Handler stack: heap-allocated LangExnFrame nodes linked via prev pointer; lang_exn_top global"
    - "prePassDecls dual-write: ExceptionDecl writes to both ExnTags and TypeEnv (tag + arity)"
    - "freeVars Raise/TryWith: inline patBoundVars recursive function extracts bound vars from patterns"

key-files:
  created:
    - src/LangBackend.Compiler/lang_runtime.h
  modified:
    - src/LangBackend.Compiler/lang_runtime.c
    - src/LangBackend.Compiler/Elaboration.fs

key-decisions:
  - "returns_twice attribute on BOTH lang_try_enter declaration (header) and definition (c file) — required for correct callee-saved register handling across longjmp"
  - "LangExnFrame heap-allocated (GC_malloc in caller) so jmp_buf persists after lang_try_enter returns"
  - "lang_throw longjmps into nearest handler frame (frame = lang_exn_top at call time, not after pop)"
  - "prePassDecls ExceptionDecl: dual-write to ExnTags AND TypeEnv; arity 0 for nullary, 1 for unary"
  - "freeVars uses inline patBoundVars, not MatchCompiler.boundVarsOfPattern (that function does not exist)"

patterns-established:
  - "Exception runtime: C functions callable from MLIR-generated code via external linkage"
  - "TypeEnv lookup for exception ctors: same emitCtorTest path as ADT constructors"

# Metrics
duration: 6min
completed: 2026-03-27
---

# Phase 19 Plan 01: Exception Handling Infrastructure Summary

**setjmp/longjmp C runtime with handler stack (lang_try_enter/exit/throw/current_exception) and Elaboration.fs scaffolding (external decls, prePassDecls TypeEnv fix, freeVars Raise/TryWith)**

## Performance

- **Duration:** 6 min
- **Started:** 2026-03-27T05:08:53Z
- **Completed:** 2026-03-27T05:15:02Z
- **Tasks:** 2
- **Files modified:** 3 (1 created, 2 extended)

## Accomplishments

- Created `lang_runtime.h` with LangExnFrame struct and `__attribute__((returns_twice))` on `lang_try_enter` declaration
- Extended `lang_runtime.c` with fully working setjmp/longjmp handler stack; all 4 symbols verified via `nm`
- Added 4 ExternalFuncDecl entries (`@lang_try_enter`, `@lang_try_exit`, `@lang_throw`, `@lang_current_exception`) to both `elaborate` and `elaborateProgram` externalFuncs lists in Elaboration.fs
- Fixed `prePassDecls` ExceptionDecl case to write exception constructors into TypeEnv (not just ExnTags), enabling `emitCtorTest` to resolve exception tags for TryWith handler pattern matching
- Added `Raise` and `TryWith` to `freeVars` using inline `patBoundVars` local function
- All 57 existing E2E tests pass — zero regressions

## Task Commits

1. **Task 1: C runtime exception infrastructure** - `ed40d9d` (feat)
2. **Task 2: Elaboration.fs scaffolding** - `469fc65` (feat)

## Files Created/Modified

- `src/LangBackend.Compiler/lang_runtime.h` - LangExnFrame struct, extern globals, 4 function declarations with returns_twice on lang_try_enter
- `src/LangBackend.Compiler/lang_runtime.c` - Exception runtime: global handler stack, lang_try_enter (setjmp), lang_try_exit (pop), lang_throw (longjmp + unhandled exit), lang_current_exception
- `src/LangBackend.Compiler/Elaboration.fs` - 4 new ExternalFuncDecl entries (both lists), prePassDecls TypeEnv fix, freeVars Raise/TryWith cases

## Decisions Made

- `returns_twice` attribute placed on BOTH the header declaration and the .c definition — only having it on one is insufficient for the compiler to correctly preserve callee-saved registers at call sites
- `lang_throw` captures `lang_exn_top` into a local `frame` variable before longjmping — handles case where lang_exn_top could theoretically change (defensive coding)
- Inline `patBoundVars` in freeVars instead of any MatchCompiler helper — `MatchCompiler.boundVarsOfPattern` does not exist in the codebase
- `prePassDecls` ExceptionDecl changed from wildcard `(name, _, _)` to `(name, dataTypeOpt, _)` to extract arity for TypeEnv entry

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None - both tasks compiled and passed regression tests on first attempt.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- C runtime infrastructure complete and verified via `nm` symbol check
- Elaboration.fs scaffolding in place; `@lang_try_enter/exit/throw/current_exception` registered as external functions
- Exception constructors now appear in TypeEnv alongside ADT constructors — `emitCtorTest` can find them without changes
- Ready for plan 19-02: Raise and TryWith elaboration codegen

---
*Phase: 19-exception-handling*
*Completed: 2026-03-27*
