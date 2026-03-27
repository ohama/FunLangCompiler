---
phase: 25-module-system
plan: 02
subsystem: compiler
tags: [fsharp, elaboration, module-system, qualified-names, FieldAccess, desugar]

# Dependency graph
requires:
  - phase: 25-01
    provides: flattenDecls + prePassDecls foundation with 97 tests passing

provides:
  - FieldAccess(Constructor(M,None,_), name, _) desugar arm in elaborateExpr — M.x → Var(x) or M.Ctor → Constructor(Ctor)
  - App(FieldAccess(Constructor(M,None,_), name, _), arg, _) desugar arm — M.f arg → App(Var("f"), arg) for non-constructor members
  - 3 new E2E tests covering qualified var, constructor, and function access
  - Updated 25-01 basic test to use Math.add Math.pi 4
  - 100 total tests passing

affects: [25-03-file-io, future phases using module-qualified name resolution]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Two-arm qualified name desugar: FieldAccess arm for values/constructors (standalone), App arm for functions (call context)"
    - "TypeEnv guard on App desugar prevents constructor arity being bypassed (M.Ctor arg uses FieldAccess path not App path)"

key-files:
  created:
    - tests/compiler/25-06-qualified-var.flt
    - tests/compiler/25-07-qualified-ctor.flt
    - tests/compiler/25-08-qualified-func.flt
  modified:
    - src/LangBackend.Compiler/Elaboration.fs
    - tests/compiler/25-01-module-basic.flt

key-decisions:
  - "Two desugar arms required: FieldAccess arm handles M.x/M.Ctor standalone; App arm handles M.f arg (KnownFuncs functions not in Vars)"
  - "App desugar guard: `not (Map.containsKey memberName env.TypeEnv)` prevents constructor application path from being bypassed"
  - "App arm placed before FieldAccess arm would be redundant — App arm catches function calls, FieldAccess falls through for standalone M.x"

patterns-established:
  - "Qualified name desugar at elaboration layer: two pattern arms covering distinct syntactic contexts (standalone vs application)"
  - "TypeEnv membership as discriminator: constructors go through Constructor elaboration path, functions/values through Var path"

# Metrics
duration: 28min
completed: 2026-03-27
---

# Phase 25 Plan 02: Qualified Name Desugar Summary

**Two-arm qualified name desugar in Elaboration.fs: FieldAccess(Constructor(M)) → Var/Constructor for values, App(FieldAccess(Constructor(M))) → App(Var(f)) for functions, enabling M.x, M.Ctor, and M.f syntax**

## Performance

- **Duration:** 28 min
- **Started:** 2026-03-27T20:52:41Z
- **Completed:** 2026-03-27T21:20:43Z
- **Tasks:** 2
- **Files modified:** 5

## Accomplishments

- FieldAccess desugar arm: `M.x` → `Var("x")` (value), `M.Ctor` → `Constructor("Ctor")` (constructor)
- App-level desugar arm: `M.f arg` → `App(Var("f"), arg)` for functions in KnownFuncs
- 3 new E2E tests covering all three qualified name forms
- Updated 25-01 basic module test to exercise qualified syntax end-to-end
- 100 tests passing total

## Task Commits

Each task was committed atomically:

1. **Task 1: Qualified name desugar guard in elaborateExpr** - `146cdab` (feat)
2. **Task 1 deviation: App-level qualified function call desugar** - `c15fd30` (feat)
3. **Task 2: E2E tests for qualified names + update basic test** - `29f10b9` (feat)

## Files Created/Modified

- `src/LangBackend.Compiler/Elaboration.fs` - Added FieldAccess desugar arm (Phase 25 comment) + App-level desugar arm with TypeEnv guard
- `tests/compiler/25-06-qualified-var.flt` - Config.x + Config.y → 42 (qualified value)
- `tests/compiler/25-07-qualified-ctor.flt` - Shapes.Circle 5 match → 5 (qualified constructor)
- `tests/compiler/25-08-qualified-func.flt` - Math.add 3 4 → 7, Math.double 10 → 20 (qualified functions)
- `tests/compiler/25-01-module-basic.flt` - Updated: Math.add Math.pi 4 → 7

## Decisions Made

- Two desugar arms needed because functions live in KnownFuncs (not env.Vars): standalone FieldAccess can't resolve function names via Var path
- TypeEnv guard on App arm: `not (Map.containsKey memberName env.TypeEnv)` routes constructors through the FieldAccess arm instead, preserving correct arity-1 constructor semantics
- App arm must precede general App(funcExpr,argExpr) arm so direct-call dispatch (KnownFuncs) is used

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing Critical] Added App-level desugar for qualified function calls**
- **Found during:** Task 1 (testing qualified name desugar)
- **Issue:** Plan's FieldAccess desugar alone handled M.x (values) and M.Ctor (constructors) but not M.f arg (functions). Functions are in KnownFuncs, not env.Vars — `Var("f", span)` evaluated standalone fails with "unbound variable"
- **Fix:** Added `App(FieldAccess(Constructor(_,None,_), memberName, fspan), argExpr, span) when not (Map.containsKey memberName env.TypeEnv)` arm before general App handler, rewrites to `App(Var(memberName, fspan), argExpr, span)` so direct-call dispatch applies
- **Files modified:** src/LangBackend.Compiler/Elaboration.fs
- **Verification:** Math.add 3 4 = 7, Math.double 10 = 20; all 100 tests pass
- **Committed in:** c15fd30 (between Task 1 and Task 2)

---

**Total deviations:** 1 auto-fixed (missing critical functionality)
**Impact on plan:** Required for the must-have "M.f arg calls the correct function". No scope creep.

## Issues Encountered

- Match compilation domination bug (pre-existing): `type Shape = Circle of int | Square of int` with two named-ctor arms each extracting values fails in mlir-opt. Worked around in 25-07 by using `Empty | Circle of int` pattern (one nullary arm) matching the same style as existing Option tests.

## Next Phase Readiness

- Qualified name access (M.x, M.Ctor, M.f arg) fully implemented
- Phase 25 (module-system) complete: 100 tests passing
- Ready for phase 26 or any phase requiring module-qualified name resolution

---
*Phase: 25-module-system*
*Completed: 2026-03-27*
