---
phase: 94-string-parameter-indexing-bug-fix
plan: 01
subsystem: compiler
tags: [elaboration, mlir, string, indexing, heuristics, ElabHelpers, Elaboration]

# Dependency graph
requires:
  - phase: 66-string-char-indexing
    provides: lang_string_char_at dispatch and StringVars tracking infrastructure
provides:
  - Fixed s.[i] on string function parameters — dispatches to lang_string_char_at with correct !llvm.ptr parameter type
  - E2E test 94-01 for Issue #22 regression prevention
affects:
  - 97-option-fun-multi-param (multi-param string functions in Prelude/Option.fun)
  - FunLexYacc integration (string parsing functions that index into string parameters)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "TEString (not TEName \"string\") is the FunLang AST TypeExpr for the string primitive type"
    - "Build warnings from dotnet run go to stdout — use >/dev/null 2>/dev/null in test scripts"
    - "LambdaAnnot preservation: bindExprOrig must be kept (not stripped) to detect TEString annotation"

key-files:
  created:
    - tests/compiler/94-01-string-param-indexing.flt
    - tests/compiler/94-01-string-param-indexing.sh
  modified:
    - src/FunLangCompiler.Compiler/ElabHelpers.fs
    - src/FunLangCompiler.Compiler/Elaboration.fs

key-decisions:
  - "Bug A: IndexGet(Var(v,_),_,_) case added to hasParamPtrUse — ensures string params typed as !llvm.ptr"
  - "Bug B (Let-Lambda): preserve bindExprOrig across pattern restructuring to detect LambdaAnnot(p,TEString,...)"
  - "Bug B (LetRec): use paramTypeAnnot (not ignored _paramTypeAnnot) to detect TEString annotation"
  - "TEString not TEName 'string' — FunLang parser uses a dedicated TypeExpr union case for the string primitive"

patterns-established:
  - "StringVars population for function parameters: set at bodyEnv construction, not inferred post-hoc"
  - "Test script build output suppression: >/dev/null 2>/dev/null on dotnet run to prevent stdout build warnings"

# Metrics
duration: 28min
completed: 2026-04-09
---

# Phase 94 Plan 01: String Parameter Indexing Bug Fix Summary

**Two-bug fix: string params now typed !llvm.ptr (not i64) and s.[i] dispatches to lang_string_char_at — returning 104 for "hello".[0] instead of wrong value 2**

## Performance

- **Duration:** 28 min
- **Started:** 2026-04-09T02:19:08Z
- **Completed:** 2026-04-09T02:47:22Z
- **Tasks:** 2
- **Files modified:** 4

## Accomplishments

- Fixed Bug A: `hasParamPtrUse` now recognizes `IndexGet(Var(param,...))` as a Ptr-requiring pattern, so string parameters get `!llvm.ptr` type in MLIR instead of `i64`
- Fixed Bug B: Let-Lambda and LetRec elaboration now detect `LambdaAnnot(p, TEString, ...)` and populate `StringVars` with the parameter name, enabling `isStringExpr` to return `true` and dispatch to `@lang_string_char_at`
- Added E2E test 94-01 covering all three scenarios: let-bound string, string literal param, and string variable param — all produce 104 (ASCII for 'h' in "hello")

## Task Commits

Each task was committed atomically:

1. **Task 1: Fix two heuristic bugs in ElabHelpers.fs and Elaboration.fs** - `f6982f7` (fix)
2. **Task 2: Add E2E test for Issue #22 scenario** - `8818bfe` (test)

## Files Created/Modified

- `src/FunLangCompiler.Compiler/ElabHelpers.fs` - Added `IndexGet(Var(v,_),_,_)` case to `hasParamPtrUse`
- `src/FunLangCompiler.Compiler/Elaboration.fs` - Restructured Let-Lambda match to preserve `bindExprOrig`; added `paramIsString` detection via `TEString`; updated LetRec to use `paramTypeAnnot` and populate `StringVars`
- `tests/compiler/94-01-string-param-indexing.flt` - FsLit test expecting `104\n104\n104`
- `tests/compiler/94-01-string-param-indexing.sh` - Compile/run script with `>/dev/null 2>/dev/null` suppression

## Decisions Made

- Used `TEString` (not `TEName "string"`) — FunLang's parser has a dedicated union case for the string primitive type. Discovered during debugging when `paramIsString` remained `false` despite correct guard matching.
- Restructured Let-Lambda `StripAnnot` active pattern to a named `bindExprOrig` binding so the original `LambdaAnnot` is accessible for type annotation inspection. The `StripAnnot` pattern erased the annotation irreversibly.
- Suppressed both stdout and stderr from `dotnet run` in test script — dotnet sends build warnings to stdout (not stderr), which pollutes test output when running in parallel with fresh builds.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] `TEString` vs `Ast.TEName "string"` in paramIsString detection**

- **Found during:** Task 1 (smoke test showing `2` after Bug B fix attempt)
- **Issue:** Plan specified `Ast.TEName "string"` but FunLang uses `TEString` as a dedicated variant for the string primitive type; the match never fired
- **Fix:** Changed `Ast.TEName "string"` to `TEString` (with `open Ast` in scope, no qualification needed) in both Let-Lambda and LetRec cases
- **Files modified:** `src/FunLangCompiler.Compiler/Elaboration.fs`
- **Verification:** Smoke test printed `104\n104\n104` after fix
- **Committed in:** `f6982f7` (Task 1 commit)

**2. [Rule 1 - Bug] Build warning goes to stdout in `dotnet run`**

- **Found during:** Task 2 (E2E test showing build warning as first line of output)
- **Issue:** Plan's script template used `2>/dev/null`, but dotnet build warnings go to stdout; parallel test execution triggered rebuilds that polluted output
- **Fix:** Changed to `>/dev/null 2>/dev/null` in the test script
- **Files modified:** `tests/compiler/94-01-string-param-indexing.sh`
- **Verification:** FsLit reports PASS after redirect fix
- **Committed in:** `8818bfe` (Task 2 commit)

**3. [Rule 1 - Bug] Incomplete pattern match warning for destructuring `(Lambda (param, body, lamSpan))`**

- **Found during:** Task 1 (compiler warning FS0025 on the refactored let binding)
- **Issue:** `let (Lambda (p,b,s)) = stripAnnot bindExprOrig` is an incomplete pattern match — F# warns even though the guard guarantees it
- **Fix:** Replaced with `match stripAnnot bindExprOrig with | Lambda (p,b,s) -> ... | _ -> failwith "impossible"`
- **Files modified:** `src/FunLangCompiler.Compiler/Elaboration.fs`
- **Verification:** Build produces no new warnings (pre-existing THashSet warning unchanged)
- **Committed in:** `f6982f7` (Task 1 commit)

---

**Total deviations:** 3 auto-fixed (3 bugs discovered during implementation)
**Impact on plan:** All auto-fixes were necessary for correctness. No scope creep.

## Issues Encountered

- F# record expression parsing: `StringVars = if cond then A else B; NextField = ...` — the `;` terminates the `if` expression, causing `NextField` to be parsed as a statement, not a field. Fixed by wrapping the conditional in parentheses: `StringVars = (if cond then A else B)`.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Issue #22 is resolved; `s.[i]` on string function parameters now returns correct char codes
- FunLexYacc string parsing functions can now be ported to FunLang
- Phase 95 (Type System Sync) can proceed — FunLang submodule at v14.0 sync
- All 259/262 E2E tests pass (3 pre-existing forin failures unrelated to this fix)

---
*Phase: 94-string-parameter-indexing-bug-fix*
*Completed: 2026-04-09*
