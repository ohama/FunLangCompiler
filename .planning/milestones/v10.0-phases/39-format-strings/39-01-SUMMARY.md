---
phase: 39-format-strings
plan: 01
subsystem: compiler
tags: [sprintf, printfn, snprintf, format-strings, elaboration, fsharp, c-runtime]

# Dependency graph
requires:
  - phase: 38-cli-args
    provides: lang_init_args/lang_get_args C runtime pattern, ExternalFuncDecl in both lists
  - phase: 35-prelude
    provides: coerceToPtrArg, coerceToI64 helpers in Elaboration.fs
provides:
  - 6 typed snprintf wrapper C functions (lang_sprintf_1i, 1s, 2ii, 2si, 2is, 2ss)
  - FmtSpec DU type and fmtSpecTypes compile-time format string parser in Elaboration.fs
  - coerceToI64Arg helper parallel to coerceToPtrArg
  - sprintf elaboration arms: 2-arg (ii/si/is/ss) before 1-arg (int/str)
  - printfn desugaring arms: desugar to println(sprintf ...) at elaboration time
  - ExternalFuncDecl entries in both externalFuncs lists
  - 3 E2E tests: 39-01 (int specifiers), 39-02 (multi-arg), 39-03 (printfn)
affects:
  - 40-funlexyacc (uses sprintf/printfn for compiler output formatting)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - Typed C wrapper dispatch: 6 wrapper functions for (arity x arg-type) combos avoid vararg MLIR issues
    - Compile-time format string dispatch: fmtSpecTypes parses format literal at elaboration time to select wrapper
    - printfn as desugar: printfn elaborates to println(sprintf ...) - zero new C code
    - Two-pass snprintf idiom: snprintf(NULL, 0, ...) for length, then GC_malloc + fill

key-files:
  created:
    - src/FunLangCompiler.Compiler/lang_runtime.c (6 sprintf wrappers added)
    - src/FunLangCompiler.Compiler/lang_runtime.h (6 declarations added)
    - tests/compiler/39-01-sprintf-int.flt
    - tests/compiler/39-02-sprintf-multi.flt
    - tests/compiler/39-03-printfn.flt
  modified:
    - src/FunLangCompiler.Compiler/Elaboration.fs (FmtSpec, fmtSpecTypes, coerceToI64Arg, sprintf/printfn arms, ExternalFuncDecl in both lists)

key-decisions:
  - "2-arg sprintf arms must come BEFORE 1-arg arms — outer App matches first (Pitfall 1)"
  - "Format string literal uses addStringGlobal + LlvmAddressOfOp (NOT elaborateExpr) to avoid extra GEP+Load"
  - "printfn desugars to println(sprintf ...) at elaboration time — zero new C functions needed"
  - "ExternalFuncDecl entries added to BOTH externalFuncs lists (module-path and program-path)"
  - "Inline let via semicolons rejected by F# in match arms — must use separate let bindings"

patterns-established:
  - "compile-time format dispatch: fmtSpecTypes → wrapper selection at elaboration time"
  - "two-pass snprintf: first call NULL/0 for length, second call fills GC_malloc'd buffer"

# Metrics
duration: 15min
completed: 2026-03-30
---

# Phase 39 Plan 01: Format Strings Summary

**sprintf/printfn builtins via 6 typed snprintf C wrappers and compile-time format string dispatch in Elaboration.fs — covers %d, %x, %02x, %c, %s with 1-arg and 2-arg formats**

## Performance

- **Duration:** ~15 min
- **Started:** 2026-03-29T22:16:00Z
- **Completed:** 2026-03-29T22:31:47Z
- **Tasks:** 2
- **Files modified:** 5

## Accomplishments
- 6 typed snprintf wrappers in lang_runtime.c/h handle all required specifier/arity combos
- FmtSpec DU + fmtSpecTypes helper enables compile-time dispatch without runtime format string inspection
- sprintf 2-arg (ordered before 1-arg), 1-arg int, and 1-arg string arms in elaborateExpr
- printfn desugar arms: printfn "%d" n → println (sprintf "%d" n) — zero new C code
- 192/192 tests pass (3 new + 189 existing, no regressions)

## Task Commits

Each task was committed atomically:

1. **Task 1: C runtime wrappers + FmtSpec helpers + ExternalFuncDecl** - `5d9722f` (feat)
2. **Task 2: sprintf/printfn arms + E2E tests** - `70ca23e` (feat)

## Files Created/Modified
- `src/FunLangCompiler.Compiler/lang_runtime.c` - 6 typed snprintf wrappers (lang_sprintf_1i/1s/2ii/2si/2is/2ss)
- `src/FunLangCompiler.Compiler/lang_runtime.h` - Declarations for all 6 wrappers
- `src/FunLangCompiler.Compiler/Elaboration.fs` - FmtSpec type, fmtSpecTypes, coerceToI64Arg, sprintf/printfn arms, ExternalFuncDecl in both lists
- `tests/compiler/39-01-sprintf-int.flt` - E2E: %d, %x, %02x, %c specifiers
- `tests/compiler/39-02-sprintf-multi.flt` - E2E: 2-arg %s=%d and %d+%d formats
- `tests/compiler/39-03-printfn.flt` - E2E: printfn with format arg and plain string

## Decisions Made
- 2-arg sprintf arms placed BEFORE 1-arg arms to prevent partial match on 3-deep App nesting
- Format string literal accessed via `addStringGlobal env fmt` + `LlvmAddressOfOp` (avoids GEP+Load overhead)
- printfn desugars to `println(sprintf ...)` at elaboration time — no new C wrappers needed
- ExternalFuncDecl entries added to BOTH externalFuncs lists (module-path ~line 3515, program-path ~line 3780)
- Inline semicolon-separated `let` bindings rejected by F# in match arms — fixed by using separate `let` lines

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed semicolon-separated let bindings in match arm**
- **Found during:** Task 2 (sprintf arms implementation)
- **Issue:** Research example used `let dp1 = ...; let da1 = ...` on one line, which F# rejects inside match arms
- **Fix:** Split into separate `let` bindings on separate lines
- **Files modified:** src/FunLangCompiler.Compiler/Elaboration.fs
- **Verification:** Build succeeded, all 192 tests pass
- **Committed in:** 70ca23e (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (Rule 1 - syntax bug in research example)
**Impact on plan:** Minor fix, no scope change.

## Issues Encountered
- F# compile error at first attempt due to semicolon-separated `let` bindings inside match arm body (not valid F# syntax). Fixed by splitting to separate let bindings.

## Next Phase Readiness
- sprintf/printfn fully functional: %d, %s, %x, %02x, %c specifiers, 1-arg and 2-arg formats
- Phase 40 (FunLexYacc) can use sprintf for compiler output formatting (RT-05/RT-06/RT-07/RT-08)
- No blockers

---
*Phase: 39-format-strings*
*Completed: 2026-03-30*
