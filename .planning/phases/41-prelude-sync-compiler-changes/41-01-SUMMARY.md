---
phase: 41-prelude-sync-compiler-changes
plan: 01
subsystem: compiler
tags: [fsharp, mlir, elaboration, module-system, open-decl]

# Dependency graph
requires:
  - phase: 35-module-system
    provides: flattenDecls with module-qualified naming (Core_id pattern)
  - phase: 40-multi-file-import
    provides: expandImports, multi-file program assembly
provides:
  - collectModuleMembers first-pass scan building Map<modName, qualifiedNames list>
  - flattenDecls with OpenDecl handling (two-pass: collect then flatten)
  - KnownFuncs aliasing for two-lambda OpenDecl aliases in elaborateExpr
  - 3 E2E tests proving OpenDecl works for values, operators, and multi-open shadowing
affects:
  - 41-02-PLAN (operator sanitization, Prelude sync)
  - Prelude files using open Core, open List, open Option etc.

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Two-pass flattenDecls: first collectModuleMembers, then flattenDecls with OpenDecl expansion"
    - "KnownFuncs alias for two-lambda functions: Let(shortName, Var(qualName)) special case in elaborateExpr"

key-files:
  created:
    - tests/compiler/41-01-open-module.flt
    - tests/compiler/41-02-open-operator.flt
    - tests/compiler/41-03-open-multi-module.flt
  modified:
    - src/LangBackend.Compiler/Elaboration.fs

key-decisions:
  - "collectModuleMembers is a separate first-pass function (not inline in flattenDecls) for clarity and testability"
  - "OpenDecl emits LetDecl(shortName, Var(qualifiedName)) aliases in flattenDecls output"
  - "For two-lambda functions in KnownFuncs (not Vars), a special Let case in elaborateExpr handles aliasing via KnownFuncs copy"
  - "Multi-segment OpenDecl paths (e.g., open A.B.C) are no-op (not supported in this phase)"

patterns-established:
  - "Two-pass flattenDecls pattern: collect first, then expand — reusable for future scope features"
  - "KnownFuncs alias: Let(name, Var(qualName), cont) where qualName in KnownFuncs → add name to KnownFuncs, continue"

# Metrics
duration: 19min
completed: 2026-03-30
---

# Phase 41 Plan 01: OpenDecl Compiler Implementation Summary

**Two-pass flattenDecls with collectModuleMembers + KnownFuncs aliasing — `open ModuleName` now correctly brings all module members into scope as unqualified names**

## Performance

- **Duration:** 19 min
- **Started:** 2026-03-30T04:02:28Z
- **Completed:** 2026-03-30T04:21:28Z
- **Tasks:** 2
- **Files modified:** 4 (1 modified + 3 created)

## Accomplishments
- Added `collectModuleMembers` first-pass scan that builds `Map<modName, qualifiedNames list>` from module declarations
- Modified `flattenDecls` to take `moduleMembers` as first arg and handle `OpenDecl` by emitting `LetDecl` short-name aliases
- Added special `Let(name, Var(qualName)) when qualName in KnownFuncs` case in `elaborateExpr` to handle two-lambda function aliasing
- `open A` now correctly shadows `B`'s same-name functions when only `A` is opened (fixes Phase 35's "last defined wins" implicit aliasing)
- Created 3 E2E tests: basic open, operator open, multi-open shadowing — all pass. 200/200 total tests pass.

## Task Commits

Each task was committed atomically:

1. **Task 1: Two-pass flattenDecls with collectModuleMembers** - `e663167` (feat)
2. **Task 1 (fix): KnownFuncs aliasing for two-lambda functions** - `8706499` (feat)
3. **Task 2: E2E tests for OpenDecl** - `3be1983` (test)

## Files Created/Modified
- `src/LangBackend.Compiler/Elaboration.fs` - Added collectModuleMembers, updated flattenDecls signature with OpenDecl case, added KnownFuncs aliasing in elaborateExpr
- `tests/compiler/41-01-open-module.flt` - E2E test: open Core makes id and not callable without prefix
- `tests/compiler/41-02-open-operator.flt` - E2E test: operator (^^) inside module compiles after open
- `tests/compiler/41-03-open-multi-module.flt` - E2E test: multiple opens shadow correctly (last open wins)

## Decisions Made
- Used `Ast.Var(qualifiedName, s)` as alias body in flattenDecls (as per plan spec)
- For two-lambda functions (in KnownFuncs, not Vars): added special case in `elaborateExpr` `Let` handler to alias via `KnownFuncs` copy rather than value binding — this avoids creating unnecessary closure wrappers
- Multi-segment OpenDecl paths emit `[]` (no-op) — single-segment only supported in this phase

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] KnownFuncs aliasing required for two-lambda functions**
- **Found during:** Task 1 (after implementing flattenDecls OpenDecl case)
- **Issue:** `Ast.Var(qualifiedName, s)` as alias body works for single-lambda functions (stored in Vars as closure ptr), but fails for two-lambda functions (stored only in KnownFuncs, not Vars). `elaborateExpr (Var("Str_concat"))` throws "unbound variable" when `Str_concat` is a two-lambda direct-call function.
- **Fix:** Added special case before general `Let` in `elaborateExpr`: `Let(name, Var(qualName), cont)` where `qualName` is in `KnownFuncs` but not `Vars` → add `name` as alias in `KnownFuncs`, continue with `cont`.
- **Files modified:** `src/LangBackend.Compiler/Elaboration.fs`
- **Verification:** `open Str` with `let (^^) a b = string_concat a b` compiles and runs correctly. 200/200 tests pass.
- **Committed in:** `8706499` (separate commit for clarity)

---

**Total deviations:** 1 auto-fixed (Rule 1 - Bug)
**Impact on plan:** Necessary fix for correctness. Plan spec said "Use Var(qualifiedName) as alias body" which works for single-lambda but not two-lambda. No scope creep.

## Issues Encountered
- Phase 35's implicit short-name aliasing (adds `id` when processing `Core_id`) made initial testing misleading — `open Core` appeared to work without changes. Closer analysis revealed the Phase 35 approach has a "last wins" bug when multiple modules define the same short name.
- Two-lambda functions (multi-param) are in `KnownFuncs` only (direct call, no closure), not in `Vars`. The `Var(qualifiedName)` alias approach needed a companion fix in `elaborateExpr`.

## Next Phase Readiness
- OpenDecl works correctly: single-open, multi-open with shadowing, operator names, both single and two-lambda functions
- Ready for Phase 41-02: operator sanitization in MLIR names, Prelude LangThree sync
- No blockers

---
*Phase: 41-prelude-sync-compiler-changes*
*Completed: 2026-03-30*
