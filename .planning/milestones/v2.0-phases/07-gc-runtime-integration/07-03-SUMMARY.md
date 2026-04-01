---
phase: 07-gc-runtime-integration
plan: 03
subsystem: compiler-ir
tags: [mlir, printf, print, println, string-globals, elaboration, fsharp, builtins]

# Dependency graph
requires:
  - phase: 07-gc-runtime-integration/07-02
    provides: ExternalFuncs pattern, LlvmCallOp, LlvmCallVoidOp, GC_init in @main
  - phase: 07-gc-runtime-integration/07-01
    provides: MlirGlobal type, StringConstant DU case, Printer.printGlobal, Printer.printExternalDecl
provides:
  - print "s" elaborates to llvm.mlir.addressof + llvm.call @printf (no newline)
  - println "s" elaborates to same but appends \n to the string global
  - String globals accumulated in ElabEnv.Globals with content-based deduplication
  - @printf declaration added to MlirModule.ExternalFuncs unconditionally
  - LetPat(WildcardPat) and LetPat(VarPat) elaboration (required to use "let _ = print..." syntax)
  - Requirement GC-03 satisfied
affects:
  - 08+ (print/println are the first stdout-producing builtins; all future user output uses this pattern)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - print/println matched as App(Var("print"|"println"), String(s)) BEFORE general App branch
    - String globals accumulate in env.Globals as (name, rawValue) pairs; dedup by rawValue
    - println appends "\n" to rawValue before storing global (so printed string has newline baked in)
    - @printf always declared in ExternalFuncs alongside GC_init and GC_malloc (dead decls harmless)
    - LetPat(WildcardPat) treated as sequencing: eval bind for side effects, discard result

key-files:
  created:
    - tests/compiler/07-02-print-basic.flt
    - tests/compiler/07-03-println-basic.flt
  modified:
    - src/FunLangCompiler.Compiler/Elaboration.fs

key-decisions:
  - "print/println cases placed BEFORE general App arm — F# pattern matching is top-to-bottom, must match first"
  - "LetPat(WildcardPat) added as deviation — let _ = expr in body is the natural sequencing idiom in LangThree"
  - "@printf always declared unconditionally (same pattern as GC_init/GC_malloc from 07-02)"
  - "Content-based dedup: same string literal reuses same global (List.tryFind on rawValue)"

patterns-established:
  - "Pattern: special-case builtins as App(Var(name), arg) BEFORE general App dispatch"
  - "Pattern: module-level string globals accumulated in ElabEnv and transferred to MlirModule in elaborateModule"

# Metrics
duration: 10min
completed: 2026-03-26
---

# Phase 7 Plan 03: print/println Builtins Summary

**print/println builtins elaborate to @printf calls with string globals; LetPat sequencing added; all 18 FsLit tests pass**

## Performance

- **Duration:** ~10 min
- **Started:** 2026-03-26T05:02:39Z
- **Completed:** 2026-03-26T05:12:39Z
- **Tasks:** 2
- **Files modified:** 3 (Elaboration.fs + 2 new test files)

## Accomplishments

- Added `Globals: (string * string) list ref` and `GlobalCounter: int ref` to `ElabEnv` with content-based deduplication via `addStringGlobal`
- Added `App(Var("print"), String(s))` and `App(Var("println"), String(s))` special cases in `elaborateExpr` before the general App branch — each emits `LlvmAddressOfOp + LlvmCallOp(@printf) + ArithConstantOp(0)` (unit-as-zero return)
- `println` appends `"\n"` to the raw string before storing the global, so the newline is baked into the MLIR string constant
- Updated `elaborateModule` to transfer `env.Globals` to `MlirModule.Globals` and add `@printf` to `ExternalFuncs`
- Added `LetPat(WildcardPat)` and `LetPat(VarPat)` elaboration cases to support the `let _ = print "hello" in body` sequencing idiom
- Propagated `Globals`/`GlobalCounter` refs to `innerEnv` (closure bodies) and `bodyEnv` (LetRec) so string globals from nested scopes accumulate correctly
- Created `07-02-print-basic.flt`: `print "hello" + println " world"` produces `"hello world\n"`
- Created `07-03-println-basic.flt`: two `println` calls produce two separate lines

## Task Commits

Each task was committed atomically:

1. **Task 1: Add string global accumulator and elaborate print/println builtins** - `b4159f5` (feat)
2. **Task 2: Add FsLit E2E tests for print and println** - `09a1b70` (test)

## Files Created/Modified

- `src/FunLangCompiler.Compiler/Elaboration.fs` - Changes A-D: ElabEnv Globals fields, addStringGlobal helper, print/println special cases, LetPat cases, elaborateModule globals transfer + @printf ExternalFunc
- `tests/compiler/07-02-print-basic.flt` - print + println combined output test
- `tests/compiler/07-03-println-basic.flt` - two println calls producing two lines

## Decisions Made

- `LetPat(WildcardPat)` added as auto-fix (Rule 2 deviation) because `let _ = expr in body` is the primary sequencing idiom in LangThree — without it, no print/println test would compile
- `@printf` declared unconditionally in ExternalFuncs (same pattern as GC_init/GC_malloc from 07-02) — dead forward declarations are harmless in MLIR/LLVM
- Content-based deduplication in `addStringGlobal` prevents duplicate string globals when the same string literal appears multiple times
- `println` bakes `\n` into the global string value (not into a format string) — simple and matches the verified MLIR pattern from research

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing critical functionality] Added LetPat elaboration**

- **Found during:** Task 1 verification
- **Issue:** `let _ = println "hello" in 0` parses as `LetPat(WildcardPat, App(Var("println")...), ...)` — Elaboration had no LetPat case and would throw "unsupported expression LetPat"
- **Fix:** Added `LetPat(WildcardPat)` (eval bind for side effects, discard, eval body) and `LetPat(VarPat)` (like Let) cases in elaborateExpr
- **Files modified:** `src/FunLangCompiler.Compiler/Elaboration.fs`
- **Commit:** `b4159f5`

## Issues Encountered

- Initial manual test used shell `echo '...'` which adds trailing newline — LangThree lexer emits NEWLINE token before EOF causing parse failure. Fixed by using `printf '...'` (no trailing newline). FsLit strips trailing newlines from Input sections automatically, so tests work correctly.

## Next Phase Readiness

- Plan 07-03 complete — all 18 tests passing (16 previous + 2 new print/println tests)
- Requirement GC-03 satisfied
- Programs can now produce stdout output — foundation for all subsequent user-facing output
- No blockers for Phase 8 (string type, list type, pattern matching)

---
*Phase: 07-gc-runtime-integration*
*Completed: 2026-03-26*
