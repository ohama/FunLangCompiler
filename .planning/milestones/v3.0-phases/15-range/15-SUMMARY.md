---
phase: 15-range
plan: 15
subsystem: compiler
tags: [mlir, llvm, range-syntax, cons-list, c-runtime, gc, elaboration, fsharp]

# Dependency graph
requires:
  - phase: 10-lists
    provides: cons cell layout (16-byte GC_malloc cells, head i64 @ 0, tail ptr @ 8)
  - phase: 14-builtin-extensions
    provides: C runtime helper pattern (lang_runtime.c + externalFuncs + LlvmCallOp)
provides:
  - Range AST node elaborated to @lang_range C runtime call
  - lang_range(start, stop, step) -> ptr builds inclusive cons list
  - RNG-01: [start..stop] with default step=1
  - RNG-02: [start..step..stop] with explicit step
affects: [future phases using range syntax, list processing tests]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - Range elaboration: three-arg LlvmCallOp with ArithConstantOp(1L) default step
    - Inclusive stop semantics: C loop uses <= (step>0) or >= (step<0)

key-files:
  created:
    - tests/compiler/15-01-range-sum.flt
    - tests/compiler/15-02-range-step.flt
  modified:
    - src/FunLangCompiler.Compiler/lang_runtime.c
    - src/FunLangCompiler.Compiler/Elaboration.fs

key-decisions:
  - "lang_range returns ptr to cons list; same Phase 10 layout — no new MLIR constructs needed"
  - "Default step=1 emitted as ArithConstantOp(v, 1L) compile-time constant"
  - "LangCons typedef in lang_runtime.c mirrors Phase 10 16-byte cell exactly"

patterns-established:
  - "Range -> LlvmCallOp pattern: same as lang_string_concat, lang_to_string_int"

# Metrics
duration: 6min
completed: 2026-03-26
---

# Phase 15: Range Summary

**[start..stop] and [start..step..stop] range syntax compiled to C runtime lang_range(start, stop, step) returning Phase-10-compatible cons list ptr; 45/45 FsLit tests pass**

## Performance

- **Duration:** ~6 min
- **Started:** 2026-03-26T00:00:00Z
- **Completed:** 2026-03-26T00:06:00Z
- **Tasks:** 5
- **Files modified:** 2 + 2 new test files

## Accomplishments
- Added `lang_range(i64 start, i64 stop, i64 step) -> LangCons*` to `lang_runtime.c` — builds inclusive cons list via `GC_malloc(16)` cells identical to Phase 10 layout
- Declared `@lang_range` in `externalFuncs` list with signature `(I64, I64, I64) -> Ptr`
- Added `Range` case in `elaborateExpr` — default step=1 via `ArithConstantOp(v, 1L)`, then `LlvmCallOp(result, "@lang_range", [startVal; stopVal; stepVal])`
- All 45/45 FsLit E2E tests pass (43 pre-existing + 2 new range tests)

## Task Commits

1. **Task 1: Add lang_range to C runtime** - `cd264d4` (feat)
2. **Task 2: Declare @lang_range in externalFuncs** - `4b8fd48` (feat)
3. **Task 3: Elaborate Range AST node** - `a740d7f` (feat)
4. **Tasks 4+5: Add RNG-01 and RNG-02 tests** - `716fed4` (test)

## Files Created/Modified
- `src/FunLangCompiler.Compiler/lang_runtime.c` - Added `LangCons` typedef + `lang_range` function (36 lines)
- `src/FunLangCompiler.Compiler/Elaboration.fs` - Added `@lang_range` to `externalFuncs`; added `Range` case in `elaborateExpr` (16 lines)
- `tests/compiler/15-01-range-sum.flt` - RNG-01: `sum [1..5]` exits 15
- `tests/compiler/15-02-range-step.flt` - RNG-02: `length [1..2..10]` exits 5

## Decisions Made
- `lang_range` returns `LangCons*` (ptr) using exact Phase 10 cell layout — existing list operations (`sum`, `length`, pattern matching) work without modification
- Default step=1 is a compile-time `ArithConstantOp(v, 1L)` constant, not a runtime branch
- C function branches on `step > 0` vs `step < 0` to use `<=` vs `>=` for inclusive stop — matches F# semantics

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
- FsLit test inputs must not have trailing newlines — the indent-sensitive FunLang lexer emits NEWLINE tokens from `\n` which disrupts the parser. Tests written without trailing newline (consistent with all existing .flt test files).

## Next Phase Readiness
- Range syntax fully operational; all existing list-processing code works on range-produced lists
- No blockers for subsequent phases

---
*Phase: 15-range*
*Completed: 2026-03-26*
