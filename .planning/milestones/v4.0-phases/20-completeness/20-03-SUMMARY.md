---
phase: 20-completeness
plan: 03
subsystem: compiler
tags: [mlir, llvm-dialect, elaboration, closures, higher-order-functions, adt, uniform-abi, inttoptr, ptrtoint]

# Dependency graph
requires:
  - phase: 20-01
    provides: LlvmIntToPtrOp/LlvmPtrToIntOp ops; constructor-as-value Lambda wrapping; Ptr-typed closure ABI
  - phase: 20-02
    provides: nested ADT pattern matching; resolveAccessorTyped field branch fixes
provides:
  - Higher-order constructor passing: apply Some 42 compiles and runs correctly
  - General App case in Elaboration.fs that handles non-Var function expressions
  - Ptr-to-I64 coercion in closure-making calls for uniform ABI compliance
  - E2E test 20-04-ho-ctor.flt proving ADT-11 fully satisfied
affects: []

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Closure-making calls coerce Ptr arg to I64 via LlvmPtrToIntOp (uniform ABI compliance)"
    - "General App case evaluates funcExpr, then dispatches via type: Ptr -> direct indirect, I64 -> inttoptr + indirect"

key-files:
  created:
    - tests/compiler/20-04-ho-ctor.flt
  modified:
    - src/LangBackend.Compiler/Elaboration.fs

key-decisions:
  - "General App case added for non-Var, non-Lambda function expressions (e.g., App(App(...), arg))"
  - "Closure-making calls coerce Ptr-typed args to I64 via LlvmPtrToIntOp before DirectCallOp"
  - "ADT-11 satisfied: apply Some 42 compiles, runs, exits 42"

patterns-established:
  - "All higher-order calls through the general App case reduce to IndirectCallOp via inttoptr dispatch"

# Metrics
duration: 9min
completed: 2026-03-27
---

# Phase 20 Plan 03: Higher-Order Constructor Passing Summary

**I64-typed closure dispatch via inttoptr and general App case enable `apply Some 42` style higher-order constructor passing, closing ADT-11 and completing Phase 20**

## Performance

- **Duration:** ~9 min
- **Started:** 2026-03-27T08:04:59Z
- **Completed:** 2026-03-27T08:13:59Z
- **Tasks:** 2
- **Files modified:** 2

## Accomplishments
- Fixed higher-order constructor passing: `apply Some 42` compiles and exits 42
- Added I64 closure dispatch path in App Var(name) branch (inttoptr before GEP/load/call)
- Added general App case that handles arbitrary function expressions (not just Var/Lambda)
- Added Ptr-to-I64 coercion in closure-making calls for uniform ABI compliance
- All 67 E2E tests pass (66 existing + 1 new)

## Task Commits

Each task was committed atomically:

1. **Task 1a: I64 closure dispatch in App Var branch** - `5e93de9` (feat)
2. **Task 1b: General App case + Ptr-to-I64 coercion for closure-making calls** - `575254e` (feat)
3. **Task 2: E2E test 20-04-ho-ctor.flt** - `ca55a62` (test)

**Plan metadata:** (docs commit follows)

## Files Created/Modified
- `src/LangBackend.Compiler/Elaboration.fs` - I64 dispatch arm, general App case, Ptr-to-I64 coercion
- `tests/compiler/20-04-ho-ctor.flt` - E2E test: `apply Some 42` exits 42

## Decisions Made
- General App case needed: `apply Some 42` parses as `App(App(Var "apply", Var "Some"), 42)`. The outer App has a non-Var function expression; adding a general case that elaborates funcExpr and dispatches by type handles this correctly.
- Ptr-to-I64 coercion needed in closure-making calls: `@apply` maker expects `(I64, Ptr)` but `Some` is a Ptr-typed closure; emit `LlvmPtrToIntOp` before `DirectCallOp`.
- Module-level `let apply f x = f x` (no `in`) + `let _ = match apply Some 42 with ...` used in test because `let f param1 param2 = body in expr` is not a grammar production (only `let x = e1 in e2` is).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing Critical] Added general App case for non-Var function expressions**
- **Found during:** Task 2 (E2E test creation)
- **Issue:** Plan specified only the I64 arm for the Var(name) branch, but `apply Some 42` produces `App(App(...), 42)` where the outer App has funcExpr = `App(Var "apply", Var "Some")` — hits `| _ -> failwithf` fallthrough
- **Fix:** Added general `| _ ->` arm that elaborates funcExpr recursively, then dispatches by type (Ptr → indirect, I64 → inttoptr + indirect)
- **Files modified:** src/LangBackend.Compiler/Elaboration.fs
- **Verification:** `apply Some 42` compiles and exits 42; all 66 existing tests pass
- **Committed in:** `575254e`

**2. [Rule 2 - Missing Critical] Added Ptr-to-I64 coercion in closure-making call**
- **Found during:** Task 2 (E2E test debugging)
- **Issue:** `@apply` closure maker expects `(I64, Ptr)` but `Some` closure is Ptr-typed; MLIR type mismatch error
- **Fix:** Added `LlvmPtrToIntOp` coercion before `DirectCallOp` when argVal.Type = Ptr
- **Files modified:** src/LangBackend.Compiler/Elaboration.fs
- **Verification:** MLIR compiles cleanly; exit 42
- **Committed in:** `575254e`

---

**Total deviations:** 2 auto-fixed (both missing critical — needed to enable correct HO dispatch)
**Impact on plan:** Both fixes were necessary for correctness. No scope creep.

## Issues Encountered
- The plan's `apply Some 42` syntax (using `let apply f x = f x in <body>`) caused parse errors because the grammar only supports `let x = e in body` at module level (not `let f p1 p2 = body in ...`). Reformulated test as module-level declarations.
- Initial I64 dispatch arm was correct but insufficient alone — discovered general App case also needed.

## Next Phase Readiness
- Phase 20 fully complete: all 5 must-haves verified, 67/67 tests pass, ADT-11 satisfied
- v4.0 compiler is complete

---
*Phase: 20-completeness*
*Completed: 2026-03-27*
