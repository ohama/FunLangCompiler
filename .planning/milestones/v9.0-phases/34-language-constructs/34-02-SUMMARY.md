---
phase: 34-language-constructs
plan: 02
subsystem: compiler
tags: [list-comprehension, elaboration, c-runtime, mlir, llvm, LangCons, ForInExpr]

# Dependency graph
requires:
  - phase: 34-language-constructs-01
    provides: StringSliceExpr elaboration arm; pattern for adding new elaboration arms in Phase 34
  - phase: 33-collection-types-02
    provides: Queue + MutableList C runtime; externalFuncs×2 pattern confirmed
  - phase: 30-for-in
    provides: ForInExpr elaboration pattern, lang_for_in_list, LangClosureFn closure ABI
provides:
  - lang_list_comp C function in lang_runtime.c — applies closure to LangCons* list, returns new LangCons* list
  - lang_list_comp prototype in lang_runtime.h
  - ListCompExpr elaboration arm in Elaboration.fs
  - externalFuncs @lang_list_comp entry in BOTH lists (elaborateModule + elaborateProgram)
  - E2E tests 34-03 (collection) and 34-04 (range) — 169 total tests passing
affects:
  - phase: 34-language-constructs-03 (ForInExpr tuple destructuring + new collection for-in)
  - any future list-building constructs

# Tech tracking
tech-stack:
  added: []
  patterns:
    - ListCompExpr desugars to Lambda + lang_list_comp — same closure ABI as ForInExpr
    - Range form works naturally: lang_range returns LangCons* list; lang_list_comp iterates it
    - Reverse-accumulate then reverse pattern for order-preserving list building in C

key-files:
  created:
    - tests/compiler/34-03-list-comp-coll.flt
    - tests/compiler/34-04-list-comp-range.flt
  modified:
    - src/FunLangCompiler.Compiler/lang_runtime.c
    - src/FunLangCompiler.Compiler/lang_runtime.h
    - src/FunLangCompiler.Compiler/Elaboration.fs

key-decisions:
  - "ListCompExpr var is a string (not a Pattern) — Lambda(var, bodyExpr, span) works directly"
  - "lang_list_comp takes void* collection cast to LangCons* — same as lang_for_in_list; Range elaborates to LangCons* via @lang_range so no separate array path needed for Phase 34"
  - "E2E tests use for-in iteration to print elements (not to_string on list) — to_string only handles int64/bool"

patterns-established:
  - "Phase 34: ListCompExpr uses LlvmCallOp returning Ptr (not LlvmCallVoidOp) — result is the new list"
  - "Closure + collection both coerced I64→Ptr before call, matching ForInExpr pattern"

# Metrics
duration: 12min
completed: 2026-03-29
---

# Phase 34 Plan 02: List Comprehension Summary

**`lang_list_comp` C function + ListCompExpr elaboration arm enable `[for x in coll -> expr]` and `[for i in 0..n -> expr]` list comprehensions; 169 E2E tests pass**

## Performance

- **Duration:** 12 min
- **Started:** 2026-03-29T15:47:00Z
- **Completed:** 2026-03-29T15:59:09Z
- **Tasks:** 2
- **Files modified:** 4 (lang_runtime.c, lang_runtime.h, Elaboration.fs, 2 test files)

## Accomplishments
- `lang_list_comp(closure, collection)` C function that iterates a `LangCons*` list, applies a closure to each head value, and returns a new `LangCons*` list preserving element order (reverse-accumulate + reverse pattern)
- `ListCompExpr` elaboration arm in Elaboration.fs: wraps body as Lambda, calls `@lang_list_comp`, returns `Ptr` result
- `@lang_list_comp` externalFuncs entry added to BOTH lists (elaborateModule ~line 3092 and elaborateProgram ~line 3321)
- Both E2E tests pass: collection form `[for x in [1;2;3] -> x*10]` produces `[10;20;30]`; range form `[for i in 0..4 -> i*i]` produces `[0;1;4;9;16]`

## Task Commits

1. **Task 1: Add lang_list_comp C function + header declaration** - `4b3af4e` (feat)
2. **Task 2: Add ListCompExpr elaboration arm + externalFuncs + E2E tests** - `de45191` (feat)

## Files Created/Modified
- `src/FunLangCompiler.Compiler/lang_runtime.c` - Added `lang_list_comp` function after `lang_for_in`
- `src/FunLangCompiler.Compiler/lang_runtime.h` - Added `LangCons* lang_list_comp(void*, void*)` prototype near `lang_for_in_*`
- `src/FunLangCompiler.Compiler/Elaboration.fs` - Added `ListCompExpr` arm before catch-all; added `@lang_list_comp` to both externalFuncs lists
- `tests/compiler/34-03-list-comp-coll.flt` - E2E test: list comprehension over collection `[1;2;3]`
- `tests/compiler/34-04-list-comp-range.flt` - E2E test: list comprehension over range `0..4`

## Decisions Made
- `ListCompExpr var` is a `string` (confirmed from Ast.fs line 126: `ListCompExpr of var: string * ...`), so `Lambda(var, bodyExpr, span)` works directly without pattern extraction
- `lang_list_comp` uses `LangCons*` for both collection input and result — Range elaborates to `LangCons*` via `@lang_range`, making the range form work uniformly without a separate array path
- E2E tests use `for y in ys do println (to_string y)` (per-element iteration) instead of `println (to_string ys)` — `to_string` only handles `int64` and `bool`, not `LangCons*` lists

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing Critical] E2E test format changed from `println (to_string list)` to per-element for-in**
- **Found during:** Task 2 (writing E2E tests)
- **Issue:** The plan specified `println (to_string ys)` but `to_string` elaboration only dispatches on `I64` and `I1` types — passing a `LangCons*` (Ptr) would use the int path and print a garbage pointer value
- **Fix:** Used `for y in ys do println (to_string y)` pattern to iterate and print each element — verifies both content and order
- **Files modified:** tests/compiler/34-03-list-comp-coll.flt, tests/compiler/34-04-list-comp-range.flt
- **Verification:** Both tests pass, element values and order confirmed correct
- **Committed in:** de45191 (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (missing critical — plan test format would have printed garbage)
**Impact on plan:** Auto-fix necessary for test correctness; scope unchanged.

## Issues Encountered
None beyond the E2E test format deviation.

## Next Phase Readiness
- List comprehension over `LangCons*` collections and ranges fully working
- Phase 34-03 (ForInExpr tuple destructuring + HashSet/Queue/MutableList/Hashtable for-in) can proceed
- All 169 E2E tests passing as baseline

---
*Phase: 34-language-constructs*
*Completed: 2026-03-29*
