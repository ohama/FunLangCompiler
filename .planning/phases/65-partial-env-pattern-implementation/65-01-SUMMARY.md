---
phase: 65-partial-env-pattern-implementation
plan: 01
subsystem: compiler
tags: [elaboration, closure, env-allocation, gc-malloc, letrec, partial-env, mlir]

# Dependency graph
requires:
  - phase: 64-caller-side-env
    provides: KnownFuncs with ClosureInfo.CaptureNames/OuterParamName, caller-side capture stores
provides:
  - Definition-site partial env allocation in Elaboration.fs (2-lambda Let handler)
  - Template-copy call path in Elaboration.fs (KnownFuncs fallback)
  - Fix for Issue #5 crash in LetRec bodies calling 3+ arg curried funcs with outer captures
affects: [all future phases using LetRec + 3+ arg curried functions with outer captures]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Partial env pattern: definition-site GC_malloc + fn_ptr + non-outerParam captures pre-stored"
    - "Template-copy call path: clone template env, fill outerParam slot, indirect call"

key-files:
  created: []
  modified:
    - src/FunLangCompiler.Compiler/Elaboration.fs

key-decisions:
  - "Template env stored in env.Vars[name] (Ptr type) alongside KnownFuncs entry — lets LetRec bodies find it"
  - "Template-copy replaces broken indirect fallback — allocates fresh env per call to avoid mutation of shared template"
  - "outerParam slot copied from template then overwritten — harmless since GC_malloc zeroes memory"
  - "Fast path (all captures in scope) unchanged — template only used in fallback when captureStoreResult has None"

patterns-established:
  - "hasNonOuterCaptures guard: only emit template env when captures beyond outerParam exist"
  - "freshName env uses outer env counter for all new SSA values in template allocation"
  - "Template ops (templateEnvOps) prepended to inOps, not wrapped inside conditional"

# Metrics
duration: 6min
completed: 2026-04-02
---

# Phase 65 Plan 01: Partial Env Pattern Implementation Summary

**Definition-site template env (GC_malloc + fn_ptr + non-outerParam captures) pre-allocated for 2-lambda closures, with template-copy call path fixing Issue #5 crash in LetRec bodies**

## Performance

- **Duration:** 6 min
- **Started:** 2026-04-01T23:00:18Z
- **Completed:** 2026-04-01T23:06:xx Z
- **Tasks:** 2
- **Files modified:** 1

## Accomplishments

- Implemented definition-site partial env: when a 2-lambda function has non-outerParam captures, Elaboration.fs now GC_mallocs a template env and stores fn_ptr + all non-outerParam captures at definition time, before elaborating `inExpr`
- Stored the template env ptr in `env'.Vars[name]` so that LetRec bodies (which reset Vars) can find it via `Map.tryFind name env.Vars` at call time
- Replaced the broken Phase 64 indirect fallback with a correct template-copy path: allocate fresh env, copy all slots from template, overwrite outerParam slot, load fn_ptr from slot 0, indirect call
- 246/246 E2E tests pass with zero regressions

## Task Commits

1. **Task 1: Definition-site template env + template-copy call path** - `4602136` (feat)
2. **Task 2: Full E2E test suite verification** - `d915fb9` (test)

## Files Created/Modified

- `src/FunLangCompiler.Compiler/Elaboration.fs` — 86 insertions, 15 deletions: Step 7 of 2-lambda Let handler rewritten to emit template env ops; KnownFuncs fallback branch replaced with template-copy path

## Decisions Made

- **Template stored in Vars not KnownFuncs:** KnownFuncs carries `FuncSignature` (type-level info). The template env is a runtime SSA value (Ptr). Storing in `env'.Vars[name]` is the correct slot — same map LetRec bodies consult.
- **No mutation of template:** Each call clones the template into a fresh GC_malloc env. The template itself is never written after initialization, making it safe across multiple LetRec calls.
- **outerParam slot index computed at call site:** `List.tryFindIndex ((=) ci.OuterParamName) ci.CaptureNames |> Option.get + 1` — no ClosureInfo change needed.
- **Fast path completely unchanged:** `captureStoreResult |> List.forall Option.isSome` guard is identical to Phase 64. Template-copy path is only the `else` branch.

## Deviations from Plan

None — plan executed exactly as written. The research (65-RESEARCH.md) provided precise code snippets that were applied directly.

## Issues Encountered

- First E2E run showed 3 failures: two (`07-01`, `11-04`) were build-lock races from parallel dotnet processes competing for the binary — not compiler bugs. One (`53-02`) passed when run directly. Re-run confirmed 246/246 pass.

## Next Phase Readiness

- Issue #5 is fixed. LetRec bodies calling 3+ arg curried functions with outer captures now work correctly.
- Phase 65 Plan 02 (new E2E tests for the specific scenarios) can proceed.
- No blockers.

---
*Phase: 65-partial-env-pattern-implementation*
*Completed: 2026-04-02*
