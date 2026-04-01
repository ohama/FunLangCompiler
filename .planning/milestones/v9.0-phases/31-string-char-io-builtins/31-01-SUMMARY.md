---
phase: 31-string-char-io-builtins
plan: 01
subsystem: compiler
tags: [lang_runtime, elaboration, string-builtins, mlir, c-runtime]

# Dependency graph
requires:
  - phase: 30-annotations-and-for-in
    provides: ForInExpr elaboration and annotation pass-through
provides:
  - lang_string_endswith: C runtime + elaboration arm (bool-wrapping pattern)
  - lang_string_startswith: C runtime + elaboration arm (bool-wrapping pattern)
  - lang_string_trim: C runtime + elaboration arm (one-arg Ptr return)
  - lang_string_concat_list: C runtime + elaboration arm (two-arg Ptr return)
  - externalFuncs entries in both elaborateModule and elaborateProgram lists
affects:
  - 31-02-startswith (char builtins phase)
  - FunLexYacc compilation (string prefix/suffix/trim/join needed)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Bool-wrapping: LlvmCallOp(raw I64) -> ArithConstantOp(0) -> ArithCmpIOp(ne) -> I1 boolResult"
    - "One-arg string->string: LlvmCallOp(result Ptr) pattern"
    - "Two-arg (Ptr,Ptr)->Ptr: LlvmCallOp with two args"
    - "LangCons* functions must be placed after LangCons typedef in lang_runtime.c"
    - "externalFuncs must be added to BOTH lists in Elaboration.fs"

key-files:
  created:
    - tests/compiler/31-01-string-endswith.flt
    - tests/compiler/31-02-string-startswith.flt
    - tests/compiler/31-03-string-trim.flt
    - tests/compiler/31-04-string-concat-list.flt
  modified:
    - src/FunLangCompiler.Compiler/lang_runtime.c
    - src/FunLangCompiler.Compiler/lang_runtime.h
    - src/FunLangCompiler.Compiler/Elaboration.fs

key-decisions:
  - "Test format uses to_string(bool) + println rather than if/then/else to avoid two-sequential-if MLIR limitation"
  - "lang_string_concat_list placed after LangCons typedef (uses LangCons*)"
  - "ForInExpr var: Pattern bug fixed by extracting VarPat name; was blocking all builds"

patterns-established:
  - "String bool predicates: endswith/startswith follow string_contains pattern exactly"
  - "E2E test avoid sequential if: use to_string(bool) for predicate tests"

# Metrics
duration: 20min
completed: 2026-03-29
---

# Phase 31 Plan 01: String Builtins Summary

**Four string builtins (endswith, startswith, trim, concat_list) added to lang_runtime.c + Elaboration.fs; all 148 tests pass**

## Performance

- **Duration:** 20 min
- **Started:** 2026-03-29T12:40:09Z
- **Completed:** 2026-03-29T13:00:25Z
- **Tasks:** 2
- **Files modified:** 6 (+ 4 test files created)

## Accomplishments
- Added 4 C runtime functions: `lang_string_endswith`, `lang_string_startswith`, `lang_string_trim`, `lang_string_concat_list`
- Added elaboration arms in Elaboration.fs for all 4 builtins with correct type signatures
- Added externalFuncs entries in BOTH module lists (expression-only and full-declaration paths)
- Created 4 E2E test files; all pass; 148/148 total tests pass
- Fixed pre-existing ForInExpr `var: Pattern` type mismatch that was breaking all builds

## Task Commits

Each task was committed atomically:

1. **Task 1: C runtime functions for 4 string builtins** - `a40dc0b` (feat)
2. **Task 2: Elaboration arms + externalFuncs + E2E tests** - `13002ba` (feat)

## Files Created/Modified
- `src/FunLangCompiler.Compiler/lang_runtime.c` - Added 4 string functions; moved lang_string_concat_list after LangCons typedef
- `src/FunLangCompiler.Compiler/lang_runtime.h` - Added declarations for 4 new functions
- `src/FunLangCompiler.Compiler/Elaboration.fs` - Added 4 elaboration arms + externalFuncs entries in both lists; fixed ForInExpr Pattern bug
- `tests/compiler/31-01-string-endswith.flt` - E2E test: true/false cases via to_string
- `tests/compiler/31-02-string-startswith.flt` - E2E test: true/false cases via to_string
- `tests/compiler/31-03-string-trim.flt` - E2E test: whitespace stripping
- `tests/compiler/31-04-string-concat-list.flt` - E2E test: list join with separator

## Decisions Made
- **Test format**: Used `to_string(bool)` + `println` pattern instead of `if/then/else` to avoid a known limitation where two sequential `if` expressions produce invalid MLIR (empty entry block after first CfCondBrOp). This is a backend limitation - two sequential `if` expressions share the same entry block and both CfCondBrOps cannot coexist.
- **lang_string_concat_list placement**: Must be placed after `LangCons` typedef in `lang_runtime.c` since it uses `LangCons*`. The other three functions (`endswith`, `startswith`, `trim`) only use `LangString*` which is defined earlier.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed pre-existing ForInExpr Pattern type mismatch**
- **Found during:** Task 1 (build verification)
- **Issue:** `ForInExpr (var, ...)` was changed in LangThree AST to use `var: Pattern` (not `string`). Elaboration.fs still used `var` (a `Pattern`) in `Set.add var` and `Lambda(var, ...)` which expect `string`. This broke all builds.
- **Fix:** Added `let varName = match var with Ast.VarPat(n, _) -> n | _ -> "_"` in `freeVars` and `let varName = match var with Ast.VarPat(n, _) -> n | _ -> freshName env` in ForInExpr elaboration
- **Files modified:** `src/FunLangCompiler.Compiler/Elaboration.fs`
- **Verification:** Build succeeded; all 30 existing for-in tests pass
- **Committed in:** `a40dc0b` (Task 1 commit)

**2. [Rule 1 - Bug] Fixed lang_string_concat_list ordering in lang_runtime.c**
- **Found during:** Task 2 (E2E test execution)
- **Issue:** `lang_string_concat_list` used `LangCons*` but was placed before the `LangCons` typedef, causing clang compilation errors
- **Fix:** Moved `lang_string_concat_list` to after the `LangCons` typedef declaration
- **Files modified:** `src/FunLangCompiler.Compiler/lang_runtime.c`
- **Verification:** Clang compilation succeeds; E2E test 31-04 passes
- **Committed in:** `13002ba` (Task 2 commit)

---

**Total deviations:** 2 auto-fixed (2 Rule 1 bugs)
**Impact on plan:** Both auto-fixes necessary for correctness. No scope creep.

## Issues Encountered
- The plan's suggested test format used `printfn "%d" a` which is not supported (printfn is not implemented). Switched to `println (to_string a)`.
- The plan's suggested test structure with two sequential `if` expressions fails due to a backend limitation: two `CfCondBrOp` terminators cannot coexist in the same entry block. Restructured tests to use `to_string(bool)` pattern instead of `if/then/else`.

## Next Phase Readiness
- All 4 string builtins are implemented and tested
- Phase 31 plan 02 (char builtins) can proceed using the same patterns
- The two-sequential-if MLIR limitation is documented - future plans should be aware

---
*Phase: 31-string-char-io-builtins*
*Completed: 2026-03-29*
