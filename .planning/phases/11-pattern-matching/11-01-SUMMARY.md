---
phase: 11-pattern-matching
plan: 01
subsystem: compiler
tags: [mlir, llvm-dialect, pattern-matching, match-expression, cf-cond-br, elaboration, fsharp, fslit, e2e-tests, lang-match-failure]

# Dependency graph
requires:
  - phase: 10-lists
    provides: LlvmNullOp, LlvmIcmpOp, EmptyListPat/ConsPat handling — reused in general testPattern
  - phase: 09-tuples
    provides: Phase 9 single-TuplePat desugar case preserved above general Match
  - phase: 07-gc-and-closures
    provides: LlvmCallVoidOp pattern — reused for @lang_match_failure void call
provides:
  - LlvmUnreachableOp: emits llvm.unreachable (noreturn terminator after @lang_match_failure)
  - lang_match_failure() C function in lang_runtime.c: fprintf(stderr) + exit(1)
  - testPattern helper: dispatches WildcardPat, VarPat, ConstPat(Int/Bool), EmptyListPat, ConsPat
  - General compileMatchArms recursive compiler: sequential cf.cond_br decision chain
  - @lang_match_failure declared unconditionally in ExternalFuncs
  - E2E tests: PAT-01 (decision chain), PAT-02 (const int/bool), PAT-04 (wildcard/var), PAT-05 (non-exhaustive)
affects:
  - 11-02-pattern-matching-extended (adds TuplePat, ConstPat(String) to testPattern)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "compileMatchArms recursive function: each arm produces (testOps, bodySetupOps, bodyOps, merge branch)"
    - "testPattern 4-tuple: (condOpt, testOps, bodySetupOps, bindEnv) — testOps run in entry/test block, bodySetupOps run at start of body block"
    - "Failure block emitted BEFORE merge block in env.Blocks.Value — ensures merge block is last and gets ReturnOp from elaborateModule"
    - "bodySetupOps pattern: ConsPat loads head/tail in body block, not test block — clean separation of test vs bind"

key-files:
  created:
    - tests/compiler/11-01-const-int-wildcard.flt
    - tests/compiler/11-02-bool-pattern.flt
    - tests/compiler/11-03-nonexhaustive.flt
  modified:
    - src/LangBackend.Compiler/MlirIR.fs
    - src/LangBackend.Compiler/Printer.fs
    - src/LangBackend.Compiler/Elaboration.fs
    - src/LangBackend.Compiler/lang_runtime.c

key-decisions:
  - "LlvmUnreachableOp is zero-operand — emits llvm.unreachable with no arguments"
  - "Failure block appended BEFORE merge block so elaborateModule's last-block ReturnOp logic applies to merge block"
  - "testPattern returns 4-tuple (condOpt, testOps, bodySetupOps, bindEnv) — bodySetupOps for ConsPat head/tail loads"
  - "EmptyListPat/ConsPat added to testPattern to avoid regression in Phase 10 list tests"
  - "@lang_match_failure declared unconditionally (same pattern as @GC_init)"

patterns-established:
  - "General match compiler: compileArms recursion, each conditional arm has test block + body block + next-test block"
  - "Unconditional arm (wildcard/var): CfBrOp directly to body, terminates the chain (no further arms)"
  - "Failure block: LlvmCallVoidOp(@lang_match_failure) + LlvmUnreachableOp — does NOT pass value to merge"

# Metrics
duration: 7min
completed: 2026-03-26
---

# Phase 11 Plan 01: General Match Compiler Summary

**General match expression compiler with sequential cf.cond_br decision chain, @lang_match_failure noreturn fallback, and support for VarPat, WildcardPat, ConstPat(Int/Bool), EmptyListPat, ConsPat**

## Performance

- **Duration:** ~7 min
- **Started:** 2026-03-26T06:37:42Z
- **Completed:** 2026-03-26T06:44:39Z
- **Tasks:** 4
- **Files modified:** 5

## Accomplishments
- Replaced Phase 10's two-arm EmptyListPat+ConsPat Match handler with a general recursive `compileMatchArms` function
- Added `testPattern` helper dispatching 6 pattern types with 4-tuple return (condOpt, testOps, bodySetupOps, bindEnv)
- Added `LlvmUnreachableOp` to MlirIR DU and Printer for noreturn terminator after match failure calls
- Added `lang_match_failure()` C function to lang_runtime.c (stderr + exit 1)
- All 30/30 FsLit tests pass (27 prior + 3 new)

## Task Commits

Each task was committed atomically:

1. **Task 1: Add LlvmUnreachableOp to MlirIR and Printer** - `219c1cb` (feat)
2. **Task 2: Add lang_match_failure to lang_runtime.c** - `bf82339` (feat)
3. **Task 3: Implement general Match compiler in Elaboration.fs** - `7e1f744` (feat)
4. **Task 4: Write FsLit E2E tests + fix regression** - `d0df3df` (feat)

## Files Created/Modified
- `src/LangBackend.Compiler/MlirIR.fs` - Added LlvmUnreachableOp to MlirOp DU
- `src/LangBackend.Compiler/Printer.fs` - Emit llvm.unreachable for LlvmUnreachableOp
- `src/LangBackend.Compiler/Elaboration.fs` - testPattern helper + general compileMatchArms + @lang_match_failure in ExternalFuncs
- `src/LangBackend.Compiler/lang_runtime.c` - lang_match_failure() + #include <stdlib.h>
- `tests/compiler/11-01-const-int-wildcard.flt` - 3-arm int match exits 1
- `tests/compiler/11-02-bool-pattern.flt` - bool false/true match exits 1
- `tests/compiler/11-03-nonexhaustive.flt` - non-exhaustive match exits 1

## Decisions Made
- Failure block appended BEFORE merge block so `elaborateModule`'s "append ReturnOp to last side block" logic correctly targets the merge block (same invariant as if/else)
- `testPattern` returns 4-tuple with `bodySetupOps` — ops that run at start of body block (e.g. ConsPat head/tail loads)
- Extended `testPattern` to handle EmptyListPat and ConsPat (Phase 10 patterns) to avoid regression

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Phase 10 list pattern regression**
- **Found during:** Task 4 (FsLit E2E test run)
- **Issue:** Replacing Phase 10 Match case with general compiler caused `10-02-list-length.flt` to fail — `testPattern` called `failwithf` for EmptyListPat/ConsPat
- **Fix:** Extended `testPattern` to handle `EmptyListPat` (null-check eq) and `ConsPat` (null-check ne + bodySetupOps for head/tail loads); 4-tuple return added to carry `bodySetupOps`
- **Files modified:** `src/LangBackend.Compiler/Elaboration.fs`
- **Verification:** 30/30 FsLit tests pass
- **Committed in:** `d0df3df` (Task 4 commit)

---

**Total deviations:** 1 auto-fixed (Rule 1 - bug)
**Impact on plan:** Essential for correctness — Phase 10 list tests must pass. No scope creep; extending testPattern is exactly the right abstraction for Plan 11-02 anyway.

## Issues Encountered
- Block ordering: initially appended fail block after merge block, causing merge block to be empty (no terminator). Fixed by appending fail block first, merge block last — so `elaborateModule` correctly appends `ReturnOp` to the merge block.

## Next Phase Readiness
- General match infrastructure is in place; testPattern is the extension point for Plan 11-02
- Plan 11-02 adds: TuplePat, ConstPat(String), and potentially multi-arm list patterns
- All 30 prior tests pass — no regressions

---
*Phase: 11-pattern-matching*
*Completed: 2026-03-26*
