---
phase: 21-mutable-variables
plan: 01
subsystem: compiler
tags: [elaboration, mutable-variables, gc-malloc, ref-cells, fsharp]

# Dependency graph
requires:
  - phase: 20-misc-fixes
    provides: 67 passing E2E tests, stable elaboration pipeline
provides:
  - MutableVars field in ElabEnv tracking which names are GC ref cells
  - freeVars handles LetMut and Assign nodes explicitly
  - LetMut elaboration: GC_malloc(8) ref cell allocation + transparent deref on Var
  - Assign elaboration: LlvmStoreOp to ref cell, returns unit (0L)
  - LetMutDecl desugaring in extractMainExpr (module-level let mut)
  - 4 E2E tests covering basic mut, assign-read, multiple assign, module-level decl
affects: [22-array-core, 23-hashtable, 24-array-hofs]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Mutable variable as GC ref cell: Var maps to Ptr; reads load from Ptr; assigns store to Ptr"
    - "MutableVars: Set<string> in ElabEnv distinguishes ref cells from plain SSA values"
    - "LetMutDecl desugars to LetMut expression via extractMainExpr build fold"

key-files:
  created:
    - tests/compiler/21-01-let-mut-basic.flt
    - tests/compiler/21-02-assign-read.flt
    - tests/compiler/21-03-multiple-assign.flt
    - tests/compiler/21-04-let-mut-decl.flt
  modified:
    - src/FunLangCompiler.Compiler/Elaboration.fs

key-decisions:
  - "GC ref cell approach: 8-byte GC_malloc'd cell; Var with name in MutableVars emits LlvmLoadOp transparently"
  - "MutableVars tracked as Set<string> in ElabEnv, not propagated into closure inner envs (closure capture is Plan 02)"
  - "Assign returns unit (ArithConstantOp 0L) after LlvmStoreOp — consistent with existing side-effect pattern"

patterns-established:
  - "Transparent deref pattern: check Set membership before deciding to emit load vs pass through"
  - "LetMutDecl single-element base case handled by :: rest fold (rest=[] gives Number(0,s) continuation)"

# Metrics
duration: 6min
completed: 2026-03-27
---

# Phase 21 Plan 01: Mutable Variables Core Summary

**GC ref cell mutable variables: LetMut allocates 8-byte cell, Var transparently dereferences, Assign stores — 71 E2E tests pass (67 + 4 new)**

## Performance

- **Duration:** 6 min
- **Started:** 2026-03-27T09:33:44Z
- **Completed:** 2026-03-27T09:39:44Z
- **Tasks:** 3
- **Files modified:** 1 + 4 created

## Accomplishments
- Added `MutableVars: Set<string>` to ElabEnv; explicit `freeVars` cases for LetMut/Assign
- LetMut elaboration allocates 8-byte GC ref cell, stores initial value, elaborates body in extended env
- Var case checks MutableVars membership and emits LlvmLoadOp transparently when mutable
- Assign case stores new value to ref cell via LlvmStoreOp, returns unit (0L)
- LetMutDecl desugars to LetMut via extractMainExpr build fold
- 4 new E2E tests all pass; all 67 existing tests still pass (71 total)

## Task Commits

Each task was committed atomically:

1. **Task 1: Add freeVars cases and ElabEnv.MutableVars field** - `689f578` (feat)
2. **Task 2: Implement LetMut, Var deref, Assign, and LetMutDecl elaboration** - `6235f21` (feat)
3. **Task 3: Add E2E tests for core mutable variable features** - `0cef03c` (test)

**Plan metadata:** (docs commit follows)

## Files Created/Modified
- `src/FunLangCompiler.Compiler/Elaboration.fs` - Added MutableVars field, freeVars cases, LetMut/Assign/Var cases, LetMutDecl in extractMainExpr
- `tests/compiler/21-01-let-mut-basic.flt` - `let mut x = 5 in x` → exit 5
- `tests/compiler/21-02-assign-read.flt` - assign then read → exit 10
- `tests/compiler/21-03-multiple-assign.flt` - multiple assigns → exit 3 (last value)
- `tests/compiler/21-04-let-mut-decl.flt` - module-level LetMutDecl → exit 99

## Decisions Made
- GC ref cell approach: 8-byte `GC_malloc` cell; `Var` with name in `MutableVars` emits `LlvmLoadOp` transparently — no caller changes needed
- `MutableVars` not propagated into closure inner envs (fresh `Set.empty`) — closure capture of mutable vars deferred to Plan 02
- `Assign` returns unit 0L via `ArithConstantOp` after store — consistent with existing side-effect idiom

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
None.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Plan 02 (closure capture of mutable variables) can now proceed
- MutableVars infrastructure is in place; closure inner envs need to propagate parent MutableVars for captured mutable names
- All 71 E2E tests passing, stable baseline for Plan 02

---
*Phase: 21-mutable-variables*
*Completed: 2026-03-27*
