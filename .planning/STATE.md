# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-03-28)

**Core value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** v6.0 — Phase 25: Module System

## Current Position

Phase: 25 of 27 (Module System)
Plan: 1 of 2 completed
Status: In progress
Last activity: 2026-03-27 — Completed 25-01-PLAN.md (module flattening foundation)

Progress: [██████████░░░░░░░░░░] v5.0 complete, v6.0 in progress (1/3 plans started)

## Performance Metrics

**Velocity:**
- v1.0: 11 plans, 6 phases
- v2.0: 9 plans, 5 phases — 34 FsLit tests, 1,861 LOC
- v3.0: 5 plans, 4 phases — 45 FsLit tests
- v4.0: 12 plans, 5 phases — 67 FsLit tests, 2,861 F# LOC + 184 C LOC
- v5.0: 8 plans, 4 phases — 92 FsLit tests, 3,187 F# LOC + 450 C LOC

**Recent Trend:** Stable

*Updated after each plan completion*

## Accumulated Context

### Key Decisions

See: .planning/PROJECT.md Key Decisions table (full history)

Recent decisions relevant to v6.0:
- Module system: compile-time AST flattening in Elaboration.fs only; OpenDecl/NamespaceDecl are no-ops at backend
- Qualified names: FieldAccess(Constructor(M), x) pattern, desugar in Elaboration.fs
- File I/O: 14 C runtime functions in lang_runtime.c/h; zero changes to MlirIR/Printer/Pipeline
- Both externalFuncs lists (Elaboration.fs + Codegen) must be updated for each new builtin
- 25-01: prePassDecls threads exnCounter as int ref parameter through recursion (not merge via return); flattenDecls placed private before extractMainExpr
- 25-01: LetPatDecl maps to existing LetPat expression node; OpenDecl is wildcard no-op in build

### Pending Todos

None.

### Blockers/Concerns

None.

## Session Continuity

Last session: 2026-03-27
Stopped at: Completed 25-01-PLAN.md — module flattening foundation, 97 tests passing
Resume file: None
