---
phase: 07-gc-runtime-integration
plan: 01
subsystem: compiler-ir
tags: [mlir, boehm-gc, bdw-gc, llvm-dialect, fsharp, pipeline]

# Dependency graph
requires:
  - phase: 05-closures-via-elaboration
    provides: MlirOp DU with LlvmAllocaOp and closure IR infrastructure
  - phase: 06-cli
    provides: Pipeline.fs compile function with clang invocation
provides:
  - MlirGlobal type (StringConstant) for module-level string constants
  - ExternalFuncDecl record for llvm.func forward declarations
  - LlvmCallOp and LlvmCallVoidOp MlirOp cases for external C calls
  - MlirModule.Globals and MlirModule.ExternalFuncs fields
  - Printer serialization of all new constructs in MLIR 20 syntax
  - Platform-aware -lgc linking in Pipeline (macOS Homebrew path detection)
affects:
  - 07-02-gc-closures (uses LlvmCallOp @GC_malloc in Elaboration)
  - 07-03-print-println (uses LlvmCallOp @printf and MlirGlobal StringConstant)

# Tech tracking
tech-stack:
  added: [bdw-gc (Boehm GC), System.Runtime.InteropServices]
  patterns:
    - MlirModule extended with Globals + ExternalFuncs for module-level declarations
    - printf vararg calls use vararg(!llvm.func<i32 (ptr, ...)>) MLIR 20 syntax
    - Platform detection via RuntimeInformation.IsOSPlatform for linker paths

key-files:
  created: []
  modified:
    - src/LangBackend.Compiler/MlirIR.fs
    - src/LangBackend.Compiler/Printer.fs
    - src/LangBackend.Compiler/Pipeline.fs
    - src/LangBackend.Compiler/Elaboration.fs

key-decisions:
  - "GC symbol is @GC_init (lowercase i) — GC_INIT is a C macro, not a linkable symbol"
  - "LlvmCallVoidOp has no result field — void calls do not consume an SSA name slot"
  - "printModule emits globals -> extern decls -> funcs (MLIR 20 requires this order)"
  - "macOS Homebrew bdw-gc path: -L/opt/homebrew/opt/bdw-gc/lib; Linux: -lgc only"

patterns-established:
  - "Pattern: LlvmCallOp for value-returning C calls; LlvmCallVoidOp for void calls"
  - "Pattern: printGlobal escapes literal newlines as \\0A and appends \\00 null terminator"
  - "Pattern: printExternalDecl emits llvm.func with optional vararg suffix and return type"

# Metrics
duration: 3min
completed: 2026-03-26
---

# Phase 7 Plan 01: GC IR Infrastructure Summary

**MlirModule extended with Globals/ExternalFuncs fields, LlvmCallOp/LlvmCallVoidOp ops added, Printer handles all new constructs, Pipeline links -lgc with macOS Homebrew path detection**

## Performance

- **Duration:** 3 min
- **Started:** 2026-03-26T04:51:49Z
- **Completed:** 2026-03-26T04:54:56Z
- **Tasks:** 3
- **Files modified:** 4

## Accomplishments

- Extended MlirIR with MlirGlobal, ExternalFuncDecl, LlvmCallOp, and LlvmCallVoidOp — the IR foundation for all GC and printf calls in plans 07-02 and 07-03
- Updated Printer to serialize all new constructs with correct MLIR 20 syntax including printf vararg annotation and string global null termination
- Added platform-aware -lgc linking in Pipeline.fs; all 15 existing FsLit tests pass with the new link flag

## Task Commits

Each task was committed atomically:

1. **Task 1: Extend MlirIR with GC infrastructure types and ops** - `72278dd` (feat)
2. **Task 2: Update Printer to serialize globals, external decls, and new call ops** - `b89ca46` (feat)
3. **Task 3: Add platform-aware -lgc linking to Pipeline** - `836f155` (feat)

## Files Created/Modified

- `src/LangBackend.Compiler/MlirIR.fs` - Added MlirGlobal, ExternalFuncDecl types; LlvmCallOp + LlvmCallVoidOp op cases; extended MlirModule with Globals + ExternalFuncs fields
- `src/LangBackend.Compiler/Printer.fs` - Added printGlobal, printExternalDecl; new op cases; updated printModule to emit globals -> extern decls -> funcs
- `src/LangBackend.Compiler/Pipeline.fs` - Added gcLinkFlags with RuntimeInformation.IsOSPlatform detection; threaded into clang invocation
- `src/LangBackend.Compiler/Elaboration.fs` - Updated MlirModule construction to supply empty Globals and ExternalFuncs fields (deviation fix)

## Decisions Made

- Used `@GC_init` (lowercase `i`) as the callee symbol per research confirming `GC_INIT` is a C preprocessor macro, not a linkable symbol
- `LlvmCallVoidOp` has no `result: MlirValue` field — void calls produce no SSA value and must not consume a freshName counter slot (per research Pitfall 5)
- Always emit `{addr_space = 0 : i32}` on `llvm.mlir.global` — required by MLIR 20

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing Critical] Updated Elaboration.fs MlirModule construction**

- **Found during:** Task 1 build verification
- **Issue:** Elaboration.fs line 395 constructed `{ Funcs = ... }` without the new `Globals` and `ExternalFuncs` fields, causing FS0764 compile error
- **Fix:** Added `Globals = []; ExternalFuncs = []` to the MlirModule record expression in `elaborateModule`
- **Files modified:** src/LangBackend.Compiler/Elaboration.fs
- **Verification:** Build succeeded with zero errors after fix; Task 1 build re-run confirmed clean
- **Committed in:** `72278dd` (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (1 missing critical — Elaboration.fs not listed in plan's files_modified)
**Impact on plan:** Necessary for correctness. The plan listed only MlirIR.fs, Printer.fs, Pipeline.fs but extending MlirModule required Elaboration.fs to be updated too. No scope creep.

## Issues Encountered

None beyond the Elaboration.fs deviation documented above.

## User Setup Required

Boehm GC must be installed before binaries that call GC_malloc will link:
- macOS: `brew install bdw-gc`
- Linux: `apt install libgc-dev`

This plan only adds the IR infrastructure and link flags. Actual GC calls are emitted in plan 07-02.

## Next Phase Readiness

- Plan 07-02 (closure env migration to GC_malloc): All IR ops needed (LlvmCallOp) are available; Elaboration.fs ready for App dispatch changes
- Plan 07-03 (print/println builtins): MlirGlobal, ExternalFuncDecl, printGlobal, printExternalDecl all in place; LlvmCallOp @printf ready
- No blockers; zero test regressions confirmed

---
*Phase: 07-gc-runtime-integration*
*Completed: 2026-03-26*
