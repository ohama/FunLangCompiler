---
phase: 62-closure-abi-ptr
plan: 01
subsystem: compiler/elaboration
tags: [closure, abi, mlir, llvm, ptr, i64, coercion]

dependency-graph:
  requires: []
  provides: ["unified closure ABI with %arg1 always !llvm.ptr"]
  affects: []

tech-stack:
  added: []
  patterns: ["uniform closure ABI — all closure functions declare %arg1 as !llvm.ptr"]

key-files:
  created:
    - tests/compiler/62-01-closure-ptr-abi.flt
  modified:
    - src/FunLangCompiler.Compiler/Elaboration.fs
    - src/FunLangCompiler.Cli/Program.fs

decisions:
  - id: D-62-01
    choice: "Always declare closure %arg1 as !llvm.ptr (Ptr), use ptrtoint coercion when body needs i64"
    rationale: "Fixes type mismatch when callers pass ptr arguments to closures (Issue #1)"
    alternatives: ["Keep I64, add inttoptr at call sites"]

metrics:
  duration: ~105 min
  completed: 2026-04-01
---

# Phase 62 Plan 01: Closure ABI Ptr Unification Summary

**One-liner:** Unified closure ABI — all closure functions declare `%arg1` as `!llvm.ptr`, reversing coercion direction from `inttoptr` to `ptrtoint`, fixing Issue #1 MLIR type mismatch.

## What Was Done

### Task 1: Change 2-lambda closure %arg1 to Ptr
Changed `arg1Val = { Name = "%arg1"; Type = Ptr }` at the 2-lambda closure site (line ~714).
Also changed `InputTypes = [Ptr; Ptr]` for the inner `llvm.func` (was `[Ptr; I64]`).
Reversed coercion: now uses `LlvmPtrToIntOp` (ptrtoint) when body needs i64.

Commits: `b3a72c7 feat(62-01)`, `b95c4d9 chore`

### Task 2: Change standalone Lambda closure %arg1 to Ptr
Same changes at the standalone Lambda site (line ~3090).
`InputTypes = [Ptr; Ptr]` and reversed coercion logic.

Commit: `b3a72c7 feat(62-01)`

### Task 3: Verify caller-side indirect calls still work
All 240 E2E tests pass. No caller-side inttoptr coercion was needed — MLIR accepts
indirect calls through opaque `!llvm.ptr` function pointers without strict type checking.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] `isPtrParamBody` stopped traversal at `LetPat` nodes**

- **Found during:** Task 3 (E2E test failures on 43-01-param-type-annotation and related tests)
- **Issue:** `hasParamPtrUse` in `isPtrParamBody` had no `LetPat` arm. Since `e1; e2` desugars
  to `LetPat(WildcardPat, e1, e2)`, traversal stopped at any semicolon-separated statement
  sequence in a function body. This caused `isPtrParamBody "kw" body = false` even when `kw`
  was clearly a string being compared to a known-string variable.
- **Fix:** Added `LetPat(VarPat(...))` and `LetPat(_, ...)` arms to `hasParamPtrUse`.
  Also added traversal for `SetField`, `LetMut`, `Assign`, `TryWith`, `LetRec`, `ForInExpr`,
  `WhileExpr`, `ForExpr`, `Tuple`, `List`, `Cons`, `Raise`.
- **Files modified:** `src/FunLangCompiler.Compiler/Elaboration.fs`
- **Commit:** `b0bfbde fix(62)`

**2. [Rule 1 - Bug] CLI rejected files without `.fun` extension**

- **Found during:** Task 3 (FsLit test infrastructure failure)
- **Issue:** `Program.fs` had `| inputPath :: _ when inputPath.EndsWith(".fun") ->` guard.
  FsLit creates temp files without `.fun` extension, so ALL E2E tests returned exit code 1.
- **Fix:** Removed the `.fun` extension guard. File existence is checked inside `compileFile`.
- **Files modified:** `src/FunLangCompiler.Cli/Program.fs`
- **Commit:** `93bba6c fix(62-01)`

## Decisions Made

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Coercion direction | ptrtoint (ptr→i64) in closure body, not inttoptr at call sites | Avoids changing all indirect call sites; coercion is localized to closure bodies |
| isPtrParamBody role | Only controls coercion direction, not declared type | Declared type is always Ptr (ABI contract), coercion is an implementation detail |

## Verification

- `dotnet build src/FunLangCompiler.Cli` — Build succeeded, 0 warnings
- `dotnet run ... tests/compiler/` — 240/240 tests pass
- `grep -n 'Type = I64' Elaboration.fs | grep "arg1"` — no results (arg1 always Ptr)
- `InputTypes = [Ptr; Ptr]` at both closure FuncOp creation sites

## Next Phase Readiness

Phase 62 is complete. v18.0 milestone shipped. The closure ABI is now unified:
- All closure functions: `llvm.func @closure_fn_N(%arg0: !llvm.ptr, %arg1: !llvm.ptr) -> i64`
- Issue #1 (MLIR type mismatch for mutable record + string curried closure) is resolved.
