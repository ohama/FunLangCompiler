# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-03-28)

**Core value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** v6.0 — Phase 27: File I/O Extended

## Current Position

Phase: 27 of 27 (File I/O Extended)
Plan: 1 of 2 completed
Status: In progress
Last activity: 2026-03-27 — Completed 27-01-PLAN.md (8 C runtime functions for extended file I/O)

Progress: [██████████████░░░░░░] v5.0 complete, v6.0 phase 26 complete, phase 27 plan 1 done (4/4 v6 plans done)

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
- 25-02: Two-arm qualified name desugar required: FieldAccess arm for M.x/M.Ctor (standalone), App arm for M.f arg (functions in KnownFuncs not Vars)
- 25-02: App desugar guard `not (Map.containsKey memberName env.TypeEnv)` prevents constructor arity bypass
- 25-02: Match compilation has pre-existing domination bug when two named-ctor arms both extract values; workaround: use one nullary arm
- 26-01: lang_file_read uses lang_throw (catchable) not lang_failwith (exits) for missing file error
- 26-01: LangString struct renamed to LangString_s (named struct) to allow forward declaration in .h without clang typedef redefinition error
- 26-01: eprint/eprintln E2E tests use single-line `let _ = eprint "..." in println "ok"` + `2>/dev/null`; multiline semicolon format causes IndentFilter NEWLINE to break SeqExpr parsing
- 26-01: file_exists uses fopen("r") not access() — no extra unistd.h needed
- 26-01: fwrite+fflush for stderr output avoids IsVarArg complexity of fprintf
- 27-01: lang_dir_files allows DT_UNKNOWN in addition to DT_REG to handle filesystems that don't populate d_type
- 27-01: Dynamic buffer growth for stdin functions uses GC_malloc+memcpy (no realloc) — consistent with no-malloc/free rule
- 27-01: String list return ABI: LangCons* with cell->head = (int64_t)(uintptr_t)LangString* — same pattern as hashtable_keys

### Pending Todos

None.

### Blockers/Concerns

- Pre-existing MLIR domination bug: match with two named-ctor arms each extracting values fails in mlir-opt (unrelated to module system; observed with `type Shape = Circle of int | Square of int`)

## Session Continuity

Last session: 2026-03-27
Stopped at: Completed 27-01-PLAN.md — 8 extended file I/O C functions + header declarations, 108 tests still passing
Resume file: None
