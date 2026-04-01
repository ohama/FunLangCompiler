---
phase: 21-mutable-variables
verified: 2026-03-27T10:04:46Z
status: passed
score: 5/5 must-haves verified
gaps: []
---

# Phase 21: Mutable Variables Verification Report

**Phase Goal:** Programs using mutable bindings compile and execute with correct mutation semantics
**Verified:** 2026-03-27T10:04:46Z
**Status:** PASSED
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| #  | Truth                                                                                   | Status     | Evidence                                                                 |
|----|-----------------------------------------------------------------------------------------|------------|--------------------------------------------------------------------------|
| 1  | `let mut x = 5 in x <- 10; x` compiles and evaluates to 10                             | VERIFIED   | Test 21-02-assign-read.flt passes: `let mut x = 5 in let _ = x <- 10 in x` → 10 |
| 2  | A closure capturing a mutable variable sees mutations made after closure creation       | VERIFIED   | Test 21-05-closure-capture-mut.flt passes: `let mut x = 5 in let f = fun z -> x in let _ = x <- 99 in f 0` → 99 |
| 3  | Module-level `let mut x = e` declarations compile via extractMainExpr desugaring        | VERIFIED   | Test 21-04-let-mut-decl.flt passes; LetMutDecl case in extractMainExpr at line 2130 |
| 4  | `Var(name)` for a mutable name emits a load through the ref cell pointer               | VERIFIED   | Lines 350–352: `Set.contains name env.MutableVars` → `LlvmLoadOp(loaded, v)` |
| 5  | freeVars correctly identifies mutable variables so closures capture the ref cell        | VERIFIED   | Lines 158–165: explicit LetMut and Assign cases; lines 114–117: LetPat(WildcardPat/VarPat) cases |

**Score:** 5/5 truths verified

### Required Artifacts

| Artifact                                            | Expected                                                                    | Status     | Details                                                                         |
|-----------------------------------------------------|-----------------------------------------------------------------------------|------------|---------------------------------------------------------------------------------|
| `src/FunLangCompiler.Compiler/Elaboration.fs`           | ElabEnv.MutableVars field, LetMut/Assign/Var-deref/LetMutDecl elaboration   | VERIFIED   | 2189 lines; MutableVars at lines 38, 46, 350, 368, 411, 417, 702, 1387, 1391   |
| `tests/compiler/21-01-let-mut-basic.flt`            | Basic let mut read test                                                      | VERIFIED   | Exists, substantive, passes: `let mut x = 5 in x` → 5                          |
| `tests/compiler/21-02-assign-read.flt`              | Assign + read test                                                           | VERIFIED   | Exists, substantive, passes: `let mut x = 5 in let _ = x <- 10 in x` → 10     |
| `tests/compiler/21-03-multiple-assign.flt`          | Multiple assignment test                                                     | VERIFIED   | Exists, substantive, passes: multiple assigns, last value 3                     |
| `tests/compiler/21-04-let-mut-decl.flt`             | Module-level let mut test                                                    | VERIFIED   | Exists, substantive, passes: module-level `let mut x = 42` then assign → 99    |
| `tests/compiler/21-05-closure-capture-mut.flt`      | Closure sees post-capture mutation test                                      | VERIFIED   | Exists, substantive, passes: closure reads post-capture mutated value → 99     |
| `tests/compiler/21-06-closure-mut-counter.flt`      | Mutable counter via closure test                                             | VERIFIED   | Exists, substantive, passes: counter closure 3 increments → 3                  |

### Key Link Verification

| From                                          | To                          | Via                                                           | Status     | Details                                                                 |
|-----------------------------------------------|-----------------------------|---------------------------------------------------------------|------------|-------------------------------------------------------------------------|
| Elaboration.fs LetMut case                    | GC_malloc(8)                | LlvmCallOp (line 363)                                         | WIRED      | `ArithConstantOp(sizeVal, 8L)` + `LlvmCallOp(cellPtr, "@GC_malloc", [sizeVal])` + `LlvmStoreOp(initVal, cellPtr)` |
| Elaboration.fs Var case                       | env.MutableVars             | Set.contains check before LlvmLoadOp (lines 350–352)         | WIRED      | `if Set.contains name env.MutableVars then let loaded = ... (loaded, [LlvmLoadOp(loaded, v)])` |
| Elaboration.fs closure capture load          | env.MutableVars             | capType conditional on Set.contains (lines 417, 1391)        | WIRED      | `let capType = if Set.contains capName env.MutableVars then Ptr else I64` — present at both closure compilation sites |
| Elaboration.fs innerEnv construction         | env.MutableVars             | MutableVars field propagation (lines 411, 1387)               | WIRED      | `MutableVars = env.MutableVars` in innerEnv at both Let-Lambda-Lambda and bare-closure cases |
| freeVars LetMut/Assign cases                  | LetMut/Assign AST nodes     | Explicit match cases before catch-all (lines 158–165)        | WIRED      | Before `| _ -> Set.empty` catch-all at line 166                        |
| freeVars LetPat WildcardPat/VarPat cases      | LetPat nodes                | Explicit match cases (lines 114–117)                          | WIRED      | Handles `let _ = x <- ...` patterns inside closure bodies              |
| extractMainExpr                               | Ast.Decl.LetMutDecl         | filter + build fold cases (lines 2103, 2130–2131)             | WIRED      | Filters LetMutDecl in exprDecls, desugars to `LetMut(name, body, build rest, s)` |

### Requirements Coverage

| Requirement | Status    | Supporting Truth                                                              |
|-------------|-----------|-------------------------------------------------------------------------------|
| MUT-01      | SATISFIED | Truth 1 (LetMut allocates GC ref cell) — verified at lines 357–370           |
| MUT-02      | SATISFIED | Truth 1 (Assign stores to ref cell, returns unit) — verified at lines 372–379 |
| MUT-03      | SATISFIED | Truth 4 (Var emits LlvmLoadOp for mutable names) — verified at lines 347–355 |
| MUT-04      | SATISFIED | Truth 3 (LetMutDecl desugared via extractMainExpr) — verified at lines 2100–2131 |
| MUT-05      | SATISFIED | Truth 5 (freeVars handles LetMut/Assign/LetPat) — verified at lines 114–165  |
| MUT-06      | SATISFIED | Truth 2 (closure captures ref cell Ptr, not I64 value) — verified at lines 411–427, 1387–1400 |

Note: MUT-01 through MUT-06 are still marked Pending in REQUIREMENTS.md. The requirements state tracking was not updated by the implementation plans; the code itself is fully implemented and all tests pass.

### Anti-Patterns Found

No blockers, warnings, or stubs found in the implementation.

| File                                               | Line  | Pattern                    | Severity | Impact |
|----------------------------------------------------|-------|----------------------------|----------|--------|
| None                                               | —     | —                          | —        | —      |

Code quality notes:
- `MutableVars = Set.empty` at line 702 is for the bare non-capturing Lambda case (a top-level function, not a closure). This is intentional and correct — top-level functions cannot close over anything. Closures use `MutableVars = env.MutableVars` (lines 411, 1387).

### Human Verification Required

None. All success criteria are verifiable programmatically, and all tests pass.

### Test Count Verification

**Expected:** 73 (67 existing + 6 new)
**Actual:** 73/73 passed (confirmed by `fslit tests/compiler/`)

---

## Gaps Summary

No gaps. All 5 observable truths are verified. All 7 required artifacts exist, are substantive, and are wired. All 6 key links are confirmed in the actual code. All 6 new E2E tests pass. No regressions in the 67 existing tests.

The full test run output: `Results: 73/73 passed`

---

_Verified: 2026-03-27T10:04:46Z_
_Verifier: Claude (gsd-verifier)_
