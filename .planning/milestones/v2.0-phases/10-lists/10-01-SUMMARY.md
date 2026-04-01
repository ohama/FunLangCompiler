---
phase: 10-lists
plan: 01
subsystem: compiler
tags: [mlir, llvm-dialect, lists, gc-malloc, gep, null-pointer, pattern-matching, elaboration, fsharp, fslit, e2e-tests]

# Dependency graph
requires:
  - phase: 09-tuples
    provides: GC_malloc heap allocation, LlvmGEPLinearOp, LlvmStoreOp/LlvmLoadOp, CfCondBrOp — all reused for cons cell layout and null-check branch
  - phase: 07-gc-and-closures
    provides: GC_malloc declaration, LlvmCallOp for @GC_malloc — cons cells use same allocation pattern as closure envs
provides:
  - LlvmNullOp: emits llvm.mlir.zero : !llvm.ptr (null pointer constant for EmptyList)
  - LlvmIcmpOp: emits llvm.icmp "eq"/"ne" %ptr, %null : !llvm.ptr (pointer null check)
  - EmptyList elaboration to LlvmNullOp (Ptr typed)
  - Cons elaboration to GC_malloc(16) + store head at slot 0 + GEP slot 1 + store tail
  - List literal [e1;e2;e3] desugaring via List.foldBack to nested Cons ending with EmptyList
  - LetRec with isListParamBody heuristic: Ptr param when body Match has EmptyListPat/ConsPat arms
  - Match with two-arm EmptyListPat+ConsPat: null-check CfCondBrOp chain with head/tail GEP loads
  - Two FsLit E2E tests: LIST-01 through LIST-04 passing (27/27 total)
affects:
  - 11-phase-match (full pattern matching extends Match case in Elaboration)
  - future list operations (map, filter, fold) build on cons cell layout

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Cons cell layout: flat 16-byte GC_malloc with head at slot 0 (base ptr), tail at slot 1 (GEP[1]) — mirrors closure env layout"
    - "Null pointer: llvm.mlir.zero : !llvm.ptr — NOT arith.constant 0; required by MLIR 20 LLVM dialect for pointer types"
    - "Pointer comparison: llvm.icmp (not arith.cmpi) — arith.cmpi rejects !llvm.ptr operands"
    - "LetRec param type heuristic: pre-scan body for EmptyListPat/ConsPat before committing param type"
    - "List desugaring: List.foldBack in elaborateExpr — right fold creates correct head-first cons chain"

key-files:
  created:
    - tests/compiler/10-01-list-literal.flt
    - tests/compiler/10-02-list-length.flt
  modified:
    - src/FunLangCompiler.Compiler/MlirIR.fs
    - src/FunLangCompiler.Compiler/Printer.fs
    - src/FunLangCompiler.Compiler/Elaboration.fs

key-decisions:
  - "LlvmNullOp result.Type = Ptr, prints as llvm.mlir.zero : !llvm.ptr (not arith.constant 0 + cast)"
  - "LlvmIcmpOp result.Type = I1, prints as llvm.icmp \"pred\" %lhs, %rhs : !llvm.ptr"
  - "Head stored at cellPtr base (slot 0) directly — avoids redundant GEP; tail at GEP[1]"
  - "isListParamBody pre-scans LetRec body before elaboration — sets paramType = Ptr when Match scrutinee=param has EmptyListPat/ConsPat arms"
  - "Phase 10 Match only handles two-arm EmptyListPat+ConsPat; all other patterns failwithf until Phase 11"
  - "Head type hardcoded I64 for Phase 10 integer-list scope; tail type always Ptr"

patterns-established:
  - "isListParamBody: AST pre-scan for list function detection — structural check without full type inference"
  - "List patterns compiled as null-check branch (same CfCondBrOp mechanism as if-else from Phase 3)"

# Metrics
duration: 4min
completed: 2026-03-26
---

# Phase 10 Plan 01: Lists Summary

**Integer list compilation via null-ptr EmptyList, GC_malloc cons cells, List.foldBack desugaring, and null-check pattern matching — all 27 FsLit tests passing**

## Performance

- **Duration:** ~4 min
- **Started:** 2026-03-26T06:15:00Z
- **Completed:** 2026-03-26T06:19:13Z
- **Tasks:** 4
- **Files modified:** 5

## Accomplishments
- Added `LlvmNullOp` and `LlvmIcmpOp` to MlirIR DU with correct MLIR 20 LLVM dialect serialization
- Implemented EmptyList, Cons, List literal elaboration (LIST-01, LIST-02, LIST-03)
- Added `isListParamBody` heuristic enabling LetRec functions with Ptr-typed parameters for list traversal
- Implemented two-arm Match (EmptyListPat + ConsPat) via null-check CfCondBrOp chain (LIST-04)
- All 27 FsLit tests pass: 25 prior (zero regressions) + 2 new list tests

## Task Commits

Each task was committed atomically:

1. **Task 1: Add LlvmNullOp and LlvmIcmpOp to MlirIR** - `217e7d0` (feat)
2. **Task 2: Update Printer to serialize LlvmNullOp and LlvmIcmpOp** - `3d9f565` (feat)
3. **Task 3: Extend Elaboration for EmptyList, Cons, List, LetRec (Ptr), Match (list)** - `767cf31` (feat)
4. **Task 4: Write FsLit E2E tests and verify all pass** - `0b455c1` (test)

## Files Created/Modified
- `src/FunLangCompiler.Compiler/MlirIR.fs` - Added LlvmNullOp and LlvmIcmpOp to MlirOp DU
- `src/FunLangCompiler.Compiler/Printer.fs` - Serialization of LlvmNullOp (llvm.mlir.zero) and LlvmIcmpOp (llvm.icmp)
- `src/FunLangCompiler.Compiler/Elaboration.fs` - isListParamBody helper; EmptyList/Cons/List/Match/LetRec list elaboration
- `tests/compiler/10-01-list-literal.flt` - E2E test: [1; 2; 3] compiles, exits 42
- `tests/compiler/10-02-list-length.flt` - E2E test: recursive length via match, exits 3

## Decisions Made
- `llvm.mlir.zero : !llvm.ptr` is the correct MLIR 20 null pointer constant — NOT `arith.constant 0 : i64` cast to pointer. The MLIR verifier rejects integer-to-pointer casts in typed contexts.
- `llvm.icmp` (LLVM dialect) must be used for pointer comparison — `arith.cmpi` rejects `!llvm.ptr` operands with "must be signless integer or index" error.
- Head stored directly at `cellPtr` base address (slot 0) without GEP — saves one SSA value. Tail at `LlvmGEPLinearOp(cellPtr, 1)`.
- `isListParamBody` is a structural AST pre-scan (not type inference) — sufficient for Phase 10 scope where all list functions have Match scrutinee = param with EmptyListPat/ConsPat arms.
- Phase 10 Match is limited to exactly two arms (EmptyListPat + ConsPat, any order). Other patterns remain `failwithf` until Phase 11 full pattern matching.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None. All compilation succeeded on first attempt.

## Next Phase Readiness
- List types fully working: construction, desugaring, and pattern matching via null-check
- Phase 11 (full pattern matching) can extend the existing Match case in Elaboration.fs — the two-arm list pattern is a special case of the general match compiler needed in Phase 11
- Cons cell layout (head@slot0, tail@slot1) is established and tested — future list operations (map, filter, fold) can use GEP[0] for head, GEP[1] for tail
- No blockers

---
*Phase: 10-lists*
*Completed: 2026-03-26*
