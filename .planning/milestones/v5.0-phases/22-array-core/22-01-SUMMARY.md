---
phase: 22-array-core
plan: 01
subsystem: compiler
tags: [mlir, llvm, gep, arrays, c-runtime, bdw-gc]

# Dependency graph
requires:
  - phase: 21-mutable-vars
    provides: LlvmGEPStructOp/LlvmGEPLinearOp patterns, lang_throw for catchable errors
  - phase: 19-exception-handling
    provides: lang_throw/lang_try_push setjmp/longjmp exception infra
provides:
  - LlvmGEPDynamicOp DU case in MlirIR.fs for SSA-value array element addressing
  - Printer support for LlvmGEPDynamicOp emitting correct MLIR syntax
  - lang_array_create: one-block GC array with slot 0 = length
  - lang_array_bounds_check: catchable OOB via lang_throw (not lang_failwith)
  - lang_array_of_list: cons list to array
  - lang_array_to_list: array to cons list
affects:
  - 22-02-PLAN.md (array builtin elaboration uses all of these)
  - any future phase using dynamic index GEP

# Tech tracking
tech-stack:
  added: []
  patterns:
    - One-block array layout: GC_malloc((n+1)*8), slot 0 = length, slots 1..n = elements
    - Dynamic GEP via LlvmGEPDynamicOp with i64 element type annotation
    - Catchable runtime errors use lang_throw (not lang_failwith or exit)

key-files:
  created: []
  modified:
    - src/FunLangCompiler.Compiler/MlirIR.fs
    - src/FunLangCompiler.Compiler/Printer.fs
    - src/FunLangCompiler.Compiler/lang_runtime.c
    - src/FunLangCompiler.Compiler/lang_runtime.h

key-decisions:
  - "lang_array_bounds_check uses lang_throw (not lang_failwith) so OOB is catchable by try/with"
  - "LlvmGEPDynamicOp trailing element type is i64 (not !llvm.ptr) per MLIR verifier requirement"
  - "lang_array_to_list iterates backwards (arr[n] down to arr[1]) to build cons list in correct order"

patterns-established:
  - "LangCons forward-declared in lang_runtime.h so array functions can reference it without circular deps"
  - "New MlirOp DU case placed adjacent to related ops (LlvmGEPDynamicOp after LlvmGEPStructOp)"

# Metrics
duration: 6min
completed: 2026-03-27
---

# Phase 22 Plan 01: Array Core IR and C Runtime Summary

**LlvmGEPDynamicOp IR op + four C runtime array functions (create/bounds_check/of_list/to_list) using one-block GC layout with catchable OOB via lang_throw**

## Performance

- **Duration:** 6 min
- **Started:** 2026-03-27T10:56:47Z
- **Completed:** 2026-03-27T11:02:54Z
- **Tasks:** 2
- **Files modified:** 4

## Accomplishments
- Added LlvmGEPDynamicOp DU case and printer arm for dynamic-index element addressing (element type i64)
- Implemented lang_array_create with one-block GC_malloc layout (slot 0 = length)
- Implemented lang_array_bounds_check raising catchable exception via lang_throw
- Implemented lang_array_of_list and lang_array_to_list for list-array conversion
- All 4 C symbols verified via nm; all 73 E2E tests passing

## Task Commits

Each task was committed atomically:

1. **Task 1: Add LlvmGEPDynamicOp to MlirIR and Printer** - `e29b581` (feat)
2. **Task 2: Add C runtime array functions** - `96a2377` (feat)

**Plan metadata:** (see docs commit below)

## Files Created/Modified
- `src/FunLangCompiler.Compiler/MlirIR.fs` - Added LlvmGEPDynamicOp DU case after LlvmGEPStructOp
- `src/FunLangCompiler.Compiler/Printer.fs` - Added printer arm for LlvmGEPDynamicOp
- `src/FunLangCompiler.Compiler/lang_runtime.c` - Added 4 array runtime functions
- `src/FunLangCompiler.Compiler/lang_runtime.h` - Added LangCons forward decl + 4 function prototypes

## Decisions Made
- lang_array_bounds_check uses lang_throw (not lang_failwith) so OOB is catchable by try/with in user code
- LlvmGEPDynamicOp trailing element type is i64 (not !llvm.ptr) — MLIR verifier requires the element type, and i64 gives correct 8-byte stride
- lang_array_to_list iterates backwards from arr[n] to arr[1] to build cons list in forward order

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
None.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- LlvmGEPDynamicOp is available for Elaboration.fs to emit dynamic array element GEPs
- All 4 C runtime functions ready for ExternalFuncDecl registration in Elaboration.fs
- Ready to proceed with 22-02-PLAN.md (array builtin elaboration: array_create, array_length, array_get, array_set, array_of_list, array_to_list)
- No blockers

---
*Phase: 22-array-core*
*Completed: 2026-03-27*
