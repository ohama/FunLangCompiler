---
phase: 05-closures-via-elaboration
plan: "01"
subsystem: compiler-ir
tags: [mlir, llvm-dialect, closures, free-variables, elaboration, fsharp]

# Dependency graph
requires:
  - phase: 04-known-functions-via-elaboration
    provides: FuncOp, DirectCallOp, FuncSignature, KnownFuncs map in ElabEnv, LetRec/App elaboration

provides:
  - MlirType.Ptr representing !llvm.ptr (opaque pointer, LLVM 20)
  - 7 new MlirOp cases: LlvmAllocaOp, LlvmStoreOp, LlvmLoadOp, LlvmAddressOfOp, LlvmGEPLinearOp, LlvmReturnOp, IndirectCallOp
  - FuncOp.IsLlvmFunc field (true -> llvm.func, false -> func.func)
  - Printer serialization for all 7 new ops and IsLlvmFunc keyword switch
  - freeVars pure helper for computing free variables of any Expr
  - ClosureInfo type and FuncSignature.ClosureInfo option field
  - ClosureCounter in ElabEnv for unique closure function name generation
  - Lambda compilation in Let handler: llvm.func body + func.func closure-maker + KnownFuncs entry
  - App dispatch extended for closure-making calls and indirect closure calls

affects:
  - 05-02-closures-via-elaboration (future plans using ClosureInfo dispatch in App)
  - Any phase adding new MlirOp cases (follows the extended DU pattern)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Flat closure struct { fn_ptr: ptr, cap_0: i64, ... } stack-allocated in caller's frame (caller-allocates avoids stack-use-after-return UB)"
    - "Lambda body functions are llvm.func (not func.func) so llvm.mlir.addressof can take their address"
    - "All-linear-GEP for field access: LlvmGEPLinearOp with integer index (no struct type annotation needed in fill/load code)"
    - "Closure-maker func.func takes (outerParam: I64, env_ptr: Ptr) and returns Ptr; caller allocates env before calling"
    - "ClosureInfo option on FuncSignature distinguishes closure-making funcs (Some) from direct-call funcs (None)"
    - "freeVars {outerParam, innerParam} innerBody computes captures for Let(name, Lambda(outerParam, Lambda(innerParam, body)))"

key-files:
  created: []
  modified:
    - src/LangBackend.Compiler/MlirIR.fs
    - src/LangBackend.Compiler/Printer.fs
    - src/LangBackend.Compiler/Elaboration.fs

key-decisions:
  - "Ptr added to MlirType as | Ptr case (represents !llvm.ptr, LLVM 20 opaque pointer convention)"
  - "IsLlvmFunc: bool added to FuncOp to switch keyword between llvm.func and func.func; all existing construction sites updated with IsLlvmFunc = false"
  - "freeVars {outerParam, innerParam} innerBody equivalent to freeVars {outerParam} (Lambda(innerParam, body)) — computed directly to avoid needing Span.empty"
  - "Captures list is sorted for determinism (Set.toList |> List.sort)"
  - "ClosureCounter is shared across the whole elaboration (ref passed in ElabEnv, not reset per scope)"

patterns-established:
  - "LlvmReturnOp used in llvm.func bodies; ReturnOp used in func.func bodies — never mixed"
  - "Closure-maker func.func emits ReturnOp [%arg1] (returns the filled env pointer)"
  - "Inner llvm.func emits LlvmReturnOp [bodyVal]"
  - "App dispatch: ClosureInfo.IsNone -> DirectCallOp; ClosureInfo.IsSome -> alloca+DirectCallOp; Vars with Ptr type -> LlvmLoadOp+IndirectCallOp"

# Metrics
duration: 6min
completed: 2026-03-26
---

# Phase 5 Plan 01: Closure IR Types, Printer, and Elaboration Summary

**MlirType.Ptr + 7 LLVM-level MlirOp cases + FuncOp.IsLlvmFunc + Printer serialization + freeVars + ClosureInfo + Lambda compilation in Let handler using caller-allocates flat closure struct**

## Performance

- **Duration:** 6 min
- **Started:** 2026-03-26T03:35:42Z
- **Completed:** 2026-03-26T03:41:43Z
- **Tasks:** 2
- **Files modified:** 3

## Accomplishments
- Extended MlirIR with closure representation: Ptr type, 7 LLVM-level ops, IsLlvmFunc on FuncOp
- Added Printer serialization for all new constructs with correct MLIR 20 syntax
- Implemented full Lambda compilation pipeline: freeVars analysis + ClosureInfo tracking + Let handler that emits paired llvm.func/func.func closure functions
- 11/11 existing FsLit tests pass with zero regressions

## Task Commits

Each task was committed atomically:

1. **Task 1: MlirIR closure types and Printer serialization** - `f18fd24` (feat)
2. **Task 2: Elaboration freeVars, ClosureInfo, and Lambda compilation** - `3b9f23a` (feat)

**Plan metadata:** (docs commit below)

## Files Created/Modified
- `src/LangBackend.Compiler/MlirIR.fs` - Added Ptr to MlirType, 7 new MlirOp cases, IsLlvmFunc to FuncOp, updated return42Module
- `src/LangBackend.Compiler/Printer.fs` - Added printType Ptr, 7 new printOp cases, IsLlvmFunc keyword switch in printFuncOp
- `src/LangBackend.Compiler/Elaboration.fs` - Added ClosureInfo, FuncSignature.ClosureInfo, ClosureCounter, freeVars, Lambda in Let handler, extended App dispatch, updated LetRec FuncSignature

## Decisions Made
- Used `freeVars (Set.ofList [outerParam; innerParam]) innerBody` rather than reconstructing a Lambda AST node to avoid needing Span.empty (which doesn't exist on the Span type — only unknownSpan exists)
- Captures sorted with `List.sort` for deterministic GEP index assignment across compilations
- ClosureCounter shared across the module-level elaboration (same ref as outer env) so each Lambda gets a globally unique name
- Inner llvm.func body SSA names use a pre-allocated scheme: `%t0..%t(N-1)` for GEP results, `%tN..%t(2N-1)` for loaded capture values, then fresh names from counter starting at 2N for the body

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed IsLlvmFunc = false in Elaboration.fs at build time**
- **Found during:** Task 1 (after MlirIR changes)
- **Issue:** Two existing FuncOp record literals in Elaboration.fs (LetRec handler and elaborateModule) did not include the new IsLlvmFunc field — F# compiler error FS0764
- **Fix:** Added `IsLlvmFunc = false` to both sites
- **Files modified:** src/LangBackend.Compiler/Elaboration.fs
- **Verification:** Build succeeded with 0 errors after fix
- **Committed in:** f18fd24 (Task 1 commit)

**2. [Rule 1 - Bug] Fixed Span.empty reference (type does not have this member)**
- **Found during:** Task 2 (build attempt)
- **Issue:** Used `Lambda(innerParam, innerBody, Span.empty)` to reconstruct an AST node for freeVars — Span type has no `.empty` member (only `unknownSpan` exists as a let binding)
- **Fix:** Computed freeVars directly as `freeVars (Set.ofList [outerParam; innerParam]) innerBody` which is mathematically equivalent without needing to reconstruct a Lambda node
- **Files modified:** src/LangBackend.Compiler/Elaboration.fs
- **Verification:** Build succeeded with 0 errors
- **Committed in:** 3b9f23a (Task 2 commit)

---

**Total deviations:** 2 auto-fixed (both Rule 1 - build errors from new field/type member)
**Impact on plan:** Both were expected build errors from adding new fields; no scope creep.

## Issues Encountered
- None beyond the two auto-fixed build errors above

## Next Phase Readiness
- MlirIR, Printer, and Elaboration all have full closure support
- Lambda compilation path is implemented and builds correctly
- App dispatch handles all three cases: direct-call, closure-making, and indirect closure call
- Ready for Phase 5 Plan 02: E2E closure tests verifying the generated MLIR compiles and produces correct exit codes

---
*Phase: 05-closures-via-elaboration*
*Completed: 2026-03-26*
