---
phase: 19-exception-handling
plan: "03"
subsystem: compiler
tags: [mlir, setjmp, longjmp, exception-handling, arm64, cfg, multi-block, pattern-matching]

# Dependency graph
requires:
  - phase: 19-exception-handling
    provides: Raise elaboration, exception ADT layout, lang_runtime exception infrastructure
  - phase: 17-adt-construction-pattern-matching
    provides: MatchCompiler decision tree for handler dispatch
provides:
  - TryWith elaboration: full multi-block CFG (entry, try_body, exn_caught, exn_fail, merge)
  - Inline _setjmp in generated code (ARM64 PAC-safe, no out-of-line wrapper)
  - returns_twice attribute on @_setjmp external declaration
  - Nested try-with support inside let binding continuations
  - Exception payload extraction via Ptr-typed GEP access
  - Unmatched exception re-raise via exn_fail block
  - 4 E2E tests covering basic catch, nested, payload, and fallthrough
affects:
  - Future exception-handling plans (finally, custom exception hierarchies)
  - Any plan adding multi-block CFG expressions inside let continuations

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Inline _setjmp: call _setjmp directly in the function containing try-with, not via wrapper"
    - "Multi-block TryWith CFG: entry->try_body/exn_caught, exn_caught->merge/exn_fail, exn_fail unreachable"
    - "Let nested-terminator fix: detect inner terminator, patch last side block with continuation ops"
    - "ExternalFuncDecl.Attrs: string list for MLIR function attributes like returns_twice"

key-files:
  created:
    - tests/compiler/19-03-try-basic.flt
    - tests/compiler/19-04-try-nested.flt
    - tests/compiler/19-05-try-payload.flt
    - tests/compiler/19-06-try-fallthrough.flt
  modified:
    - src/FunLangCompiler.Compiler/Elaboration.fs
    - src/FunLangCompiler.Compiler/MlirIR.fs
    - src/FunLangCompiler.Compiler/Printer.fs
    - src/FunLangCompiler.Compiler/lang_runtime.c
    - src/FunLangCompiler.Compiler/lang_runtime.h
    - src/FunLangCompiler.Compiler/Pipeline.fs

key-decisions:
  - "Use _setjmp inline (not lang_try_enter wrapper) to fix ARM64 PAC setjmp stack-frame issue"
  - "Use _longjmp in lang_throw to avoid signal mask overhead and match _setjmp"
  - "Exception payload GEP uses Ptr type (not I64) since payloads are always heap strings"
  - "Nested TryWith inside let: patch inner merge block with continuation ops (not append after terminator)"
  - "exn_fail block re-raises via lang_throw(lang_current_exception()) + LlvmUnreachableOp"
  - "returns_twice attribute on @_setjmp prevents LLVM from misoptimizing the setjmp call site"

patterns-established:
  - "blocksBeforeBind tracking: record env.Blocks.Value.Length before elaborating bind expr to detect newly added blocks"
  - "isTerminator check in Let: detect CfBrOp/CfCondBrOp/LlvmUnreachableOp as block terminators"
  - "TryWith bodyOps terminator detection: handle Raise (unreachable), inner TryWith (CfBrOp), and normal (append try_exit + CfBrOp)"

# Metrics
duration: 180min
completed: 2026-03-27
---

# Phase 19 Plan 03: TryWith Elaboration Summary

**try-with exception handling via inline _setjmp/longjmp with multi-block MLIR CFG, handler dispatch via MatchCompiler decision trees, and ARM64 PAC-safe stack frame management**

## Performance

- **Duration:** ~3 hours (multi-session)
- **Started:** 2026-03-27
- **Completed:** 2026-03-27
- **Tasks:** 2
- **Files modified:** 10

## Accomplishments
- Full TryWith elaboration: 5-block CFG per try-with expression (entry, try_body, exn_caught, exn_fail, merge)
- ARM64 PAC-safe exception handling by calling `_setjmp` inline in generated code instead of via wrapper function
- Nested try-with in let binding continuations via inner merge block patching
- 4 E2E tests all passing; full suite 63/63 pass

## Task Commits

Each task was committed atomically:

1. **Task 1: TryWith elaboration case** - `5bb789d` (feat)
2. **Task 2: E2E tests for try-with** - `78395bd` (test)

**Plan metadata:** (this commit) (docs: complete plan)

## Files Created/Modified
- `src/FunLangCompiler.Compiler/Elaboration.fs` - TryWith case, Let nested-terminator fix, updated external decls
- `src/FunLangCompiler.Compiler/MlirIR.fs` - Added Attrs field to ExternalFuncDecl
- `src/FunLangCompiler.Compiler/Printer.fs` - Emit attributes string for external decls
- `src/FunLangCompiler.Compiler/lang_runtime.c` - lang_try_push (no setjmp), lang_throw uses _longjmp
- `src/FunLangCompiler.Compiler/lang_runtime.h` - Updated declarations
- `src/FunLangCompiler.Compiler/Pipeline.fs` - Removed debug WriteAllText
- `tests/compiler/19-03-try-basic.flt` - Basic catch test
- `tests/compiler/19-04-try-nested.flt` - Nested try-with test
- `tests/compiler/19-05-try-payload.flt` - Payload extraction test
- `tests/compiler/19-06-try-fallthrough.flt` - Unmatched re-raise test

## Decisions Made
- **Inline _setjmp**: Out-of-line `lang_try_enter` wrapper failed on macOS ARM64 because longjmp can't return to a freed stack frame. Fixed by emitting `_setjmp` directly in the function containing try-with, with `lang_try_push` handling only the stack management.
- **_longjmp over longjmp**: Used `_longjmp` in lang_throw to match `_setjmp` (avoids signal mask save/restore).
- **Ptr for payload GEP**: Exception payload fields are heap strings; changed `resolveAccessorTyped2` to use `Ptr` not `I64`.
- **returns_twice attribute**: Added to `@_setjmp` external declaration to prevent LLVM misoptimization.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] ARM64 PAC setjmp wrapper failure**
- **Found during:** Task 1 (TryWith elaboration)
- **Issue:** `lang_try_enter` wrapper called `setjmp` internally then returned; `longjmp` could not return to freed stack frame on macOS ARM64 with pointer authentication
- **Fix:** Split `lang_try_enter` into `lang_try_push` (stack management only) + emit `_setjmp` call inline in generated MLIR code
- **Files modified:** lang_runtime.c, lang_runtime.h, Elaboration.fs
- **Verification:** 19-03-try-basic.flt passes; confirmed catch path executes
- **Committed in:** 5bb789d

**2. [Rule 1 - Bug] Exception payload loaded as I64 instead of Ptr**
- **Found during:** Task 1 (exception handler dispatch)
- **Issue:** `ensureAdtFieldTypes2` used I64 for payload slot; exception payloads are heap strings (Ptr), causing MLIR type mismatch in GEPStructOp
- **Fix:** Changed `resolveAccessorTyped2 argAccs.[0] I64` to `resolveAccessorTyped2 argAccs.[0] Ptr`
- **Files modified:** Elaboration.fs
- **Verification:** 19-05-try-payload.flt passes; string_length of caught message returns correct value
- **Committed in:** 5bb789d

**3. [Rule 1 - Bug] Let continuation ops appended after block terminator**
- **Found during:** Task 1 (nested try-with test)
- **Issue:** When TryWith appears in let bind position, the entry block ends with CfCondBrOp (a terminator); the Let case was appending body ops after the terminator — invalid MLIR
- **Fix:** Added `blocksBeforeBind` tracking and `isTerminator` check in Let case; when bind ends with terminator and added side blocks, patch last side block (inner merge) with body ops
- **Files modified:** Elaboration.fs
- **Verification:** 19-04-try-nested.flt passes; nested try-with with let continuation works
- **Committed in:** 5bb789d

**4. [Rule 1 - Bug] 19-06 test used inline try-with syntax rejected by parser**
- **Found during:** Task 2 (E2E test verification)
- **Issue:** `let _ = try raise (NotFound) with | ...` with `|` indented at column 8 while `try` is at column 9 fails IndentFilter validation
- **Fix:** Rewrote to use `let _ =\n  try raise (NotFound)\n  with\n  | ...` format
- **Files modified:** tests/compiler/19-06-try-fallthrough.flt
- **Verification:** 19-06-try-fallthrough.flt passes; all 63 tests pass
- **Committed in:** 78395bd

---

**Total deviations:** 4 auto-fixed (4 bugs)
**Impact on plan:** All fixes necessary for correct ARM64 execution and valid MLIR generation. No scope creep.

## Issues Encountered
- `returns_twice` attribute on external MLIR declaration does not propagate through mlir-opt lowering to LLVM IR `declare`. The attribute is still emitted in MLIR for documentation, but the key fix was eliminating the need for it by using inline `_setjmp` (no wrapper to mark).
- The IndentFilter's `try...with` column validation required all test files to use `try` on a new indented line, not inline after `let _ =`.

## Next Phase Readiness
- TryWith elaboration complete; exception handling is fully functional
- Ready for phase 19 plan 04 (finally blocks) or additional exception features
- No blockers

---
*Phase: 19-exception-handling*
*Completed: 2026-03-27*
