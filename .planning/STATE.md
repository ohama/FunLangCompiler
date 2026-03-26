# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-03-26)

**Core value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** Phase 2 — Scalar Codegen via MlirIR

## Current Position

Phase: 2 of 6 (Scalar Codegen via MlirIR)
Plan: 1 of 2 in current phase
Status: In progress
Last activity: 2026-03-26 — Completed 02-01-PLAN.md (Elaboration pass skeleton + integer literal end-to-end)

Progress: [███░░░░░░░] 25%

## Performance Metrics

**Velocity:**
- Total plans completed: 4
- Average duration: ~1.75 min
- Total execution time: ~0.12 hours

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 01-mlirir-foundation | 3 | ~5 min | ~1.7 min |
| 02-scalar-codegen-via-mlirir | 1 | ~2 min | ~2 min |

**Recent Trend:**
- Last 5 plans: 01-01 (2 min), 01-02 (1 min), 01-03 (2 min), 02-01 (2 min)
- Trend: Stable ~2 min/plan

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
- [01-02]: Printer is pure (no I/O) — pipeline writes string to temp file; enables unit testing without file system
- [01-02]: stderr read before WaitForExit to prevent pipe deadlock on large mlir-opt output
- [01-02]: LLVM 20 pass order confirmed: --convert-arith-to-llvm --convert-cf-to-llvm --convert-func-to-llvm --reconcile-unrealized-casts (arith before func per PR #120548)
- [01-03]: Cross-platform LLVM tool paths via resolveTool helper — checks LLVM_BIN_DIR env var, Homebrew path, Linux path
- [02-01]: Elaboration.fs placed after Printer.fs in .fsproj (F# compilation order: depends on MlirIR and Ast)
- [02-01]: freshName generates %t0, %t1, ... SSA names via int ref counter in ElabEnv
- [02-01]: Negate lowered as: arith.constant 0 then arith.subi zero, inner
- [02-01]: parseExpr in CLI replicates LangThree.Program.parse 3-line pattern (avoids Eval/Prelude init)

### Pending Todos

None yet.

### Blockers/Concerns

- [Phase 1, RESOLVED]: mlir-opt pass pipeline flags verified and working
- [Phase 1, RESOLVED]: Process piping handled with stderr-before-WaitForExit pattern
- [Phase 1, RESOLVED in 01-01]: MlirIR DU is extensible — MlirOp is a wide DU; new cases are added without changing MlirModule/FuncOp/MlirRegion/MlirBlock shape.
- [Phase 5]: Closure escape analysis rule for v1: stack-allocate all closures (conservative; correct for programs that do not return closures from functions). Document limitation before Phase 5.

## Session Continuity

Last session: 2026-03-26T02:12:53Z
Stopped at: Completed 02-01-PLAN.md — Elaboration pass skeleton, integer literal end-to-end verified
Resume file: None
