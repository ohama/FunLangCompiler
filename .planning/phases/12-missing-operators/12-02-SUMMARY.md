---
phase: 12-missing-operators
plan: 02
subsystem: compiler
tags: [mlir, pipe, compose, closure, lambda, elaboration, desugar]

# Dependency graph
requires:
  - phase: 12-missing-operators
    plan: 01
    provides: App(Lambda) inline fix, freeVars fix for pipe/compose, existing closure infrastructure
provides:
  - PipeRight (|>) operator elaboration via App desugar
  - ComposeRight (>>) operator elaboration via Lambda desugar
  - ComposeLeft (<<) operator elaboration via Lambda desugar
  - Bare Lambda as expression: inline closure alloc (llvm.func + GC_malloc)
affects: []

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "PipeRight desugar: x |> f → elaborateExpr (App(f, x)) — one recursive call"
    - "ComposeRight/Left desugar: f >> g → elaborateExpr (Lambda(__comp_N, App(g, App(f, Var __comp_N))))"
    - "Bare Lambda closure: llvm.func body + inline GC_malloc + fn_ptr store + capture stores"
    - "Gensym prefix __comp_ for compose lambda params (never collides with user identifiers)"

key-files:
  created: []
  modified:
    - src/LangBackend.Compiler/Elaboration.fs

key-decisions:
  - "ComposeRight/Left produce a Lambda node that hits a new bare-Lambda closure case (not the two-arg closure-maker path)"
  - "Bare Lambda captures only env.Vars entries (not KnownFuncs), since KnownFuncs functions are direct-call, not closure values"
  - "Bare Lambda inline-allocates the closure struct in the current function body (no func.func wrapper needed)"

patterns-established:
  - "Pattern: Operator desugar at elaboration time — PipeRight/Compose never reach MLIR as their own ops"
  - "Pattern: Bare Lambda closure = closureFnIdx llvm.func + GC_malloc((numCaptures+1)*8) + store fn_ptr + store captures"

# Metrics
duration: 5min
completed: 2026-03-26
---

# Phase 12 Plan 02: PipeRight + ComposeRight + ComposeLeft Summary

**PipeRight (|>), ComposeRight (>>), ComposeLeft (<<) implemented via elaboration-time desugar; bare Lambda as expression creates inline GC-allocated closure**

## Performance

- **Duration:** ~5 min
- **Started:** 2026-03-26T00:05:00Z
- **Completed:** 2026-03-26T00:10:00Z
- **Tasks:** 1
- **Files modified:** 1

## Accomplishments
- PipeRight desugars to `App(right, left)` — one recursive elaborateExpr call
- ComposeRight desugars to `Lambda(__comp_N, App(g, App(f, Var __comp_N)))` with gensym'd param
- ComposeLeft desugars to `Lambda(__comp_N, App(f, App(g, Var __comp_N)))` (f/g swapped)
- New bare `Lambda` elaboration case: creates inline closure struct via GC_malloc + llvm.func
- All 5 operator tests pass: OP-01=1, OP-02=65, OP-03=6, OP-04=8, OP-05=8

## Task Commits

1. **Task 1: PipeRight, ComposeRight, ComposeLeft + bare Lambda closure** - `8822630` (feat)

## Files Created/Modified
- `src/LangBackend.Compiler/Elaboration.fs` - PipeRight/ComposeRight/ComposeLeft cases + bare Lambda closure case

## Decisions Made
- The plan's note about adding a bare `Lambda` case was correct: ComposeRight/Left produce a Lambda that binds to `let composed = ...`, which doesn't match the two-arg `Let(name, Lambda(outer, Lambda(inner, body)))` pattern. A new bare Lambda case was needed.
- The bare Lambda closure only captures variables from `env.Vars` — functions in `KnownFuncs` are compiled as direct calls and don't need to be stored in the closure env.
- Inline closure allocation (no `func.func` wrapper): the closure struct is built directly in the current function's instruction stream, returning a Ptr. This matches the existing indirect call pattern.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing Critical] Added bare Lambda closure elaboration case**
- **Found during:** Task 1 verification (ComposeRight test)
- **Issue:** `inc >> dbl` desugars to `Lambda("__comp_0", App(dbl, App(inc, ...)))` which the plan said to call `elaborateExpr env (Lambda(...))`. But the existing elaborator had no `Lambda` case as a standalone expression — it would hit the catch-all with "unsupported expression Lambda"
- **Fix:** Added a `| Lambda(param, body, _)` case to `elaborateExpr` that creates an inline closure: computes free vars (filtered to `env.Vars`), emits an `llvm.func` for the body, inline-allocates a closure struct, stores fn_ptr and captures
- **Files modified:** `src/LangBackend.Compiler/Elaboration.fs`
- **Verification:** `(inc >> dbl) 3` exits 8, `(dbl << inc) 3` exits 8
- **Committed in:** `8822630` (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (1 missing critical — bare Lambda closure case)
**Impact on plan:** Essential for ComposeRight/ComposeLeft to work. No scope creep — the plan's note explicitly anticipated this case might be needed.

## Issues Encountered
- The plan noted: "If Lambda as a bare expression is not handled, add [...] Let(__lam_N, Lambda(...), Var __lam_N) desugaring." The Let-based desugar would have looped since `Let(name, Lambda(single_param, body))` also lacks a handler. The direct bare Lambda closure case was the correct fix.

## Next Phase Readiness
- All 5 Phase 12 operators implemented and verified (OP-01 through OP-05)
- Phase 12-missing-operators complete
- Closure infrastructure handles: two-arg curried closures (Plan 5), bare single-arg lambdas (Plan 12)
- Ready for next v3.0 language completeness phase

---
*Phase: 12-missing-operators*
*Completed: 2026-03-26*
