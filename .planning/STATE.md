# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-03-26)

**Core value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** Phase 1 — MlirIR Foundation

## Current Position

Phase: 1 of 6 (MlirIR Foundation)
Plan: 0 of 3 in current phase
Status: Ready to plan
Last activity: 2026-03-26 — Roadmap revised (MlirIR design: explicit compiler IR + Elaboration pass)

Progress: [░░░░░░░░░░] 0%

## Performance Metrics

**Velocity:**
- Total plans completed: 0
- Average duration: — min
- Total execution time: 0 hours

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| - | - | - | - |

**Recent Trend:**
- Last 5 plans: none yet
- Trend: —

*Updated after each plan completion*

## Accumulated Context

### Decisions

Decisions are logged in PROJECT.md Key Decisions table.
Recent decisions affecting current work:

- [Init]: Use MLIR text format directly — F# emits `.mlir` text files, then shells out to `mlir-opt` → `mlir-translate` → `clang` via `System.Diagnostics.Process`. No P/Invoke, no ownership issues.
- [Revised 2026-03-26]: P/Invoke MLIR C API bindings dropped entirely. All 21 v1 requirements map cleanly without any C interop concerns.
- [Revised 2026-03-26]: MlirIR is the compiler's own IR (F# DU: Region, Block, Op, Value, Type). It is NOT a thin wrapper over MLIR text — it is a typed intermediate representation that the Printer serializes to `.mlir`. This separation enables future MlirIR optimization passes.
- [Revised 2026-03-26]: Pipeline is LangThree AST → Elaboration pass → MlirIR → Printer → `.mlir` text → mlir-opt → mlir-translate → clang → binary. The old "TypedExpr annotation pass" (Phase 3) is replaced by the Elaboration pass, which is introduced in Phase 2 and extended per phase.
- [Revised 2026-03-26]: MlirIR evolves incrementally: Phase 1 (skeleton + smoke test), Phase 2 (scalar arith + SSA), Phase 3 (bool/cmp/cond_br), Phase 4 (FuncOp + DirectCallOp), Phase 5 (ClosureAllocOp + IndirectCallOp).
- [Init]: Flat closure struct `{ fn_ptr, env_fields }` with uniform `(i8* env, arg) -> result` calling convention — must be committed before any lambda codegen (retrofitting is a full rewrite)
- [Init]: Shell pipeline for Phase 1: `mlir-opt` lowering → `mlir-translate --mlir-to-llvmir` → `clang` linking

### Pending Todos

None yet.

### Blockers/Concerns

- [Phase 1]: Verify exact `mlir-opt` pass pipeline flag names for the installed MLIR version — run `mlir-opt --help | grep convert-arith` before writing the Process invocation
- [Phase 1]: Confirm `System.Diagnostics.Process` stdin/stdout piping behavior for large `.mlir` files (pipe buffering deadlock risk if both stdout and stderr are read synchronously)
- [Phase 1]: Design MlirIR DU to be extensible — each subsequent phase adds new Op union cases without breaking existing ones. Consider `Op` as a wide DU from the start rather than adding cases later.
- [Phase 5]: Closure escape analysis rule for v1: stack-allocate all closures (conservative; correct for programs that do not return closures from functions). Document limitation before Phase 5.

## Session Continuity

Last session: 2026-03-26
Stopped at: Roadmap revised — MlirIR design adopted (explicit compiler IR + Elaboration pass), 6 phases, ready to plan Phase 1
Resume file: None
