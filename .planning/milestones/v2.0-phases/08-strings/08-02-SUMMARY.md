---
phase: 08-strings
plan: 02
subsystem: compiler-ir
tags: [mlir, strings, strcmp, string-equality, string-concat, to-string, arith-extui, fsharp, builtins]

# Dependency graph
requires:
  - phase: 08-strings/08-01
    provides: LlvmGEPStructOp, lang_runtime.c (lang_string_concat, lang_to_string_bool, lang_to_string_int), string_length builtin, ExternalFuncs for @strcmp/@lang_string_concat/@lang_to_string_int/@lang_to_string_bool

provides:
  - strcmp-based string equality: Equal/NotEqual on Ptr operands routes to @strcmp via GEP field 1 + load + arith.cmpi I32
  - string_concat builtin: App(App(Var("string_concat"), a), b) → LlvmCallOp(@lang_string_concat)
  - to_string builtin: App(Var("to_string"), arg) → @lang_to_string_int (I64) or @lang_to_string_bool (I64 after zext I1)
  - ArithExtuIOp MlirOp case for arith.extui zero-extension (I1 → I64 for C ABI)
  - STR-02, STR-04, STR-05 FsLit tests passing
  - Phase 8 complete: all five string requirements satisfied

affects:
  - 09+ (ArithExtuIOp pattern available for other type conversions)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - strcmp equality pattern: GEP field 1 from both structs → load data ptrs → llvm.call @strcmp → arith.constant 0 : i32 → arith.cmpi eq/ne : i32
    - I1→I64 zero-extension before C ABI calls: ArithExtuIOp(extVal, boolVal) then pass extVal
    - Nested App special-case before general App: App(App(Var("string_concat"),...),...)  must precede general App dispatch

key-files:
  created:
    - tests/compiler/08-02-string-equality.flt
    - tests/compiler/08-03-string-concat.flt
    - tests/compiler/08-04-to-string.flt
  modified:
    - src/LangBackend.Compiler/MlirIR.fs
    - src/LangBackend.Compiler/Printer.fs
    - src/LangBackend.Compiler/Elaboration.fs

key-decisions:
  - "ArithExtuIOp (arith.extui) added to MlirIR/Printer to zero-extend I1→I64 for C ABI: @lang_to_string_bool takes int64_t, MLIR won't let i1 pass directly to i64 param"
  - "to_string bool path: elaborate arg first (I1), emit ArithExtuIOp to extend to I64, then call @lang_to_string_bool with I64 value"
  - "string_concat nested App matched before general App: App(App(Var('string_concat'),...),...)  ordered before general App(funcExpr, argExpr) in elaborateExpr"
  - "FsLit tests for multi-expression programs must use single-line format (parser does not accept newlines between sub-expressions)"

patterns-established:
  - "strcmp equality pattern: GEP data ptr from both string structs, call @strcmp, compare I32 result to ArithConstantOp 0 : I32"
  - "C ABI I1 promotion: always ArithExtuIOp I1 to I64 before passing to external C functions taking int64_t"

# Metrics
duration: 11min
completed: 2026-03-26
---

# Phase 8 Plan 02: String Builtins Summary

**strcmp-based string equality, string_concat via @lang_string_concat, and to_string with ArithExtuIOp I1→I64 promotion — all 5 STR requirements met, 22 tests pass**

## Performance

- **Duration:** 11 min
- **Started:** 2026-03-26T05:47:57Z
- **Completed:** 2026-03-26T05:58:53Z
- **Tasks:** 3
- **Files modified:** 6

## Accomplishments
- Wired strcmp-based string equality: Equal/NotEqual on Ptr operands GEPs data ptrs from both structs, calls @strcmp → I32, compares to 0 via arith.cmpi eq/ne : i32
- Added string_concat builtin via App(App(Var("string_concat"), a), b) pattern matched before general App dispatch → LlvmCallOp(@lang_string_concat)
- Added to_string builtin with ArithExtuIOp fix: I1 booleans zero-extended to I64 before @lang_to_string_bool call; I64 integers call @lang_to_string_int directly
- Added ArithExtuIOp to MlirIR and Printer (arith.extui: value : T1 to T2) for type promotion
- All 22 FsLit tests pass (19 existing + STR-02/STR-04/STR-05)

## Task Commits

1. **Task 1: Wire strcmp-based equality for string operands** - `1cdd955` (feat)
2. **Task 2: Add string_concat and to_string builtins** - `c5e7205` (feat)
3. **Task 3: Write FsLit tests + ArithExtuIOp fix** - `71394e4` (test)

## Files Created/Modified
- `src/LangBackend.Compiler/MlirIR.fs` - Added ArithExtuIOp case to MlirOp DU
- `src/LangBackend.Compiler/Printer.fs` - Added printOp case for ArithExtuIOp (arith.extui)
- `src/LangBackend.Compiler/Elaboration.fs` - strcmp equality, string_concat, to_string builtins
- `tests/compiler/08-02-string-equality.flt` - STR-02: "abc"="abc" true, "abc"="def" false → exit 1
- `tests/compiler/08-03-string-concat.flt` - STR-04: string_concat "foo" "bar" → length 6
- `tests/compiler/08-04-to-string.flt` - STR-05: to_string 42 → "42", to_string true → "true"

## Decisions Made
- Added ArithExtuIOp rather than changing C runtime to take `_Bool`/`i1` — the C runtime uses `int64_t` for bool parameter, which is the conventional C ABI; zero-extending I1 to I64 in the compiler is the correct approach
- FsLit tests written in single-line format — the LangThree parser does not accept newlines between sub-expressions; all multi-statement programs must be written on one line

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Added ArithExtuIOp for I1→I64 zero-extension before @lang_to_string_bool**
- **Found during:** Task 3 (writing STR-05 to_string test)
- **Issue:** `@lang_to_string_bool` declared as `ExtParams = [I64]` in ExternalFuncs (matches C `int64_t`), but `to_string true` elaborates `true` to an I1 value. MLIR validation rejected `llvm.call @lang_to_string_bool(%t2) : (i1) -> !llvm.ptr` with error "operand type mismatch for operand 0: 'i1' != 'i64'"
- **Fix:** Added `ArithExtuIOp` (arith.extui) to MlirIR and Printer; updated `to_string` I1 path to emit `ArithExtuIOp(extVal, argVal)` then pass `extVal : I64` to the call
- **Files modified:** src/LangBackend.Compiler/MlirIR.fs, src/LangBackend.Compiler/Printer.fs, src/LangBackend.Compiler/Elaboration.fs
- **Verification:** 08-04-to-string.flt passes; all 22 tests pass
- **Committed in:** `71394e4` (Task 3 commit)

---

**Total deviations:** 1 auto-fixed (Rule 1 - bug: I1/I64 type mismatch for C ABI call)
**Impact on plan:** ArithExtuIOp is a minimal additive fix that doesn't change any existing behavior. No scope creep.

## Issues Encountered
- FsLit test 08-04 initially written with newlines between let-bindings — discovered parser does not accept newlines, rewrote test as single-line expression (same as 07-03-println-basic.flt pattern)

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Phase 8 complete: all five string requirements satisfied (STR-01 through STR-05)
- ArithExtuIOp available for future type promotion needs
- 22 tests passing, zero regressions
- Phase 9 (tuples) can reuse LlvmGEPStructOp and string struct patterns

---
*Phase: 08-strings*
*Completed: 2026-03-26*
