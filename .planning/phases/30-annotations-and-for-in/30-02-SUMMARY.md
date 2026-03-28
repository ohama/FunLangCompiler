---
phase: 30-annotations-and-for-in
plan: 02
subsystem: codegen
tags: [for-in, closure, lambda, c-runtime, list, array, freeVars, GC, elaboration]

# Dependency graph
requires:
  - phase: 30-01
    provides: Annot/LambdaAnnot pass-throughs needed before ForInExpr
  - phase: 29
    provides: ForExpr/WhileExpr CFG patterns; Lambda closure infrastructure
  - phase: 12
    provides: bare-Lambda closure allocation (reused by ForInExpr)
  - phase: 10
    provides: cons cell list representation

provides:
  - lang_for_in_list C runtime function (walks cons-cell chains)
  - lang_for_in_array C runtime function (walks count-prefixed arrays)
  - ForInExpr elaboration: desugars to Lambda closure + C runtime call
  - freeVars ForInExpr case
  - ArrayVars tracking in ElabEnv for compile-time list/array dispatch
  - 4 E2E tests: 30-03 list, 30-04 array, 30-05 closure-capture, 30-06 empty

affects:
  - future phases using ForInExpr
  - any phase adding new array-creating builtins (needs isArrayExpr update)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "ForInExpr desugar: wrap body as Lambda(var, body), elaborate to closure, call C runtime"
    - "ArrayVars: Set<string> in ElabEnv tracks array-typed variables for compile-time dispatch"
    - "isArrayExpr: recursive AST analysis to detect array-producing expressions"
    - "Two separate C functions (lang_for_in_list, lang_for_in_array) instead of runtime GC_size heuristic"

key-files:
  created:
    - tests/compiler/30-03-forin-list.flt
    - tests/compiler/30-04-forin-array.flt
    - tests/compiler/30-05-forin-closure-capture.flt
    - tests/compiler/30-06-forin-empty.flt
  modified:
    - src/LangBackend.Compiler/lang_runtime.c
    - src/LangBackend.Compiler/lang_runtime.h
    - src/LangBackend.Compiler/Elaboration.fs

key-decisions:
  - "Use two C functions (lang_for_in_list + lang_for_in_array) instead of GC_size runtime dispatch — GC_size rounds up to block boundaries (GC_malloc(16) returns 32), making cons cells and 1-2 element arrays indistinguishable at runtime"
  - "Add ArrayVars: Set<string> to ElabEnv — minimal compile-time type tracking sufficient for practical for-in usage patterns"
  - "isArrayExpr checks AST shape: array_of_list, array_create, array_init builtins, and Var names in ArrayVars"

patterns-established:
  - "ForInExpr pattern: Lambda wraps body, Lambda elaboration builds closure, LlvmCallVoidOp to C runtime, ArithConstantOp(unit, 0)"
  - "Compile-time collection type dispatch: check isArrayExpr at elaboration site to select C function"

# Metrics
duration: 45min
completed: 2026-03-27
---

# Phase 30 Plan 02: For-In Loop Implementation Summary

**ForInExpr desugars to Lambda closure + compile-time-dispatched C runtime call (lang_for_in_list or lang_for_in_array), with ArrayVars tracking in ElabEnv enabling correct list vs array selection at compile time.**

## Performance

- **Duration:** ~45 min
- **Started:** 2026-03-27T00:00:00Z
- **Completed:** 2026-03-27T00:45:00Z
- **Tasks:** 2
- **Files modified:** 7

## Accomplishments

- `lang_for_in_list` and `lang_for_in_array` C runtime functions covering both collection types
- `ForInExpr` elaboration: desugars body to `Lambda(var, body)`, elaborates closure, calls appropriate C runtime
- `freeVars` case for `ForInExpr` (FIN-04) — correctly computes free variables with var bound inside body
- `ArrayVars: Set<string>` in `ElabEnv` + `isArrayExpr` helper for compile-time list/array dispatch
- 4 E2E tests passing: list, array, closure capture, empty collection
- 144/144 tests passing (138 prior + 2 annotation from plan-01 + 4 for-in)

## Task Commits

Each task was committed atomically:

1. **Task 1: Add lang_for_in C runtime function** - `5987a6e` (feat)
2. **Task 2: Add ForInExpr elaboration, freeVars, extern, and E2E tests** - `2af63f7` (feat)

**Plan metadata:** (this commit)

## Files Created/Modified

- `src/LangBackend.Compiler/lang_runtime.c` - Added lang_for_in_list, lang_for_in_array, lang_for_in (compat alias)
- `src/LangBackend.Compiler/lang_runtime.h` - Declared all three lang_for_in functions
- `src/LangBackend.Compiler/Elaboration.fs` - ForInExpr elaboration, freeVars case, ArrayVars tracking, extern decls
- `tests/compiler/30-03-forin-list.flt` - E2E: for-in over 3-element list prints "123"
- `tests/compiler/30-04-forin-array.flt` - E2E: for-in over 3-element array prints "102030"
- `tests/compiler/30-05-forin-closure-capture.flt` - E2E: loop body captures outer variable
- `tests/compiler/30-06-forin-empty.flt` - E2E: for-in over empty list = zero iterations

## Decisions Made

1. **GC_size heuristic abandoned** — The plan proposed using `GC_size` to distinguish cons cells from arrays at runtime. In practice, `GC_size(GC_malloc(16))` returns 32 (not 16), because Boehm GC rounds allocations to 16-byte bucket boundaries. A cons cell (16 bytes) and a 1-2 element array (16-24 bytes) both map to GC_size=32, making them indistinguishable. The original plan's heuristic `if (block_size > 16)` was wrong for all multi-element arrays.

2. **Two separate C functions chosen** — `lang_for_in_list` for cons-cell chains, `lang_for_in_array` for count-prefixed arrays. Dispatch happens at compile time in Elaboration.fs via `isArrayExpr` + `ArrayVars`.

3. **`ArrayVars: Set<string>` added to ElabEnv** — Minimal tracking: records variable names bound to array-producing expressions. Updated in `Let` and `LetPat(VarPat)` bindings. `isArrayExpr` checks direct array builtins (`array_of_list`, `array_create`, `array_init`) and variables in `ArrayVars`.

4. **Empty list test uses variable** — `for x in [] do` causes a parse error (parser grammar conflicts). The empty test uses `let xs = [] in for x in xs do` instead.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] GC_size heuristic replaced with compile-time dispatch**
- **Found during:** Task 2 (running 30-03 and 30-05 tests)
- **Issue:** Plan's `lang_for_in` used `GC_size > 16` to detect arrays, but GC_size rounds up — cons cells and small arrays both return 32, causing cons cell iteration to be treated as array iteration (reading `head` as count and `tail` as element)
- **Fix:** Added `ArrayVars: Set<string>` to `ElabEnv`, added `isArrayExpr` helper, used `lang_for_in_list` vs `lang_for_in_array` at elaboration time
- **Files modified:** Elaboration.fs, lang_runtime.c, lang_runtime.h
- **Verification:** 144/144 tests pass including all 4 for-in tests
- **Committed in:** 2af63f7 (Task 2 commit)

**2. [Rule 1 - Bug] 30-06 test changed to use variable for empty list**
- **Found during:** Task 2 (testing 30-06)
- **Issue:** `for x in [] do` causes parse error — direct `[]` in for-in collection position conflicts with parser grammar
- **Fix:** Changed test to bind empty list to variable: `let xs = [] in for x in xs do`
- **Files modified:** tests/compiler/30-06-forin-empty.flt
- **Verification:** Test passes with expected output `done0`
- **Committed in:** 2af63f7 (Task 2 commit)

---

**Total deviations:** 2 auto-fixed (both Rule 1 — bugs)
**Impact on plan:** Both fixes necessary for correctness. No scope creep. The architectural decision (two C functions vs runtime dispatch) is a better design anyway — type-safe at compile time.

## Issues Encountered

- GC_size behavior on macOS/Homebrew Boehm GC: allocations rounded to 16-byte boundaries (16→16, 17-32→32, 33-48→48). Confirmed by writing a test program. Required redesigning the runtime dispatch approach.
- Parser limitation: `for x in [] do` is a parse error. The `[]` literal in the collection position conflicts with something in the grammar. Workaround: bind to variable first.

## Next Phase Readiness

- Phase 30 complete: annotations (plan-01) and for-in loops (plan-02) both implemented
- ForInExpr works for list and array collections with closure capture
- 144 tests passing — full regression coverage maintained
- If future phases add new array-creating builtins, they need to be added to `isArrayExpr` in Elaboration.fs

---
*Phase: 30-annotations-and-for-in*
*Completed: 2026-03-27*
