# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-03-26)

**Core value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** v2.0 — Phase 7: GC Runtime Integration

## Current Position

Phase: 7 of 11 (GC Runtime Integration)
Plan: 0 of TBD in current phase
Status: Ready to plan
Last activity: 2026-03-26 — v2.0 roadmap created; Phases 7-11 defined

Progress: [██████░░░░░░░░░░░░░░] 6/11 phases complete (v1.0 done, v2.0 starting)

## Performance Metrics

**Velocity:**
- Total plans completed: 11 (v1.0 ALL COMPLETE)
- Average duration: ~2.3 min
- Total execution time: ~0.38 hours

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 01-mlirir-foundation | 3 | ~5 min | ~1.7 min |
| 02-scalar-codegen-via-mlirir | 2 | ~4 min | ~2 min |
| 03-booleans-comparisons-control-flow | 2 | ~4 min | ~2 min |
| 04-known-functions-via-elaboration | 1 | ~3 min | ~3 min |
| 05-closures-via-elaboration | 2 | ~9 min | ~4.5 min |
| 06-cli | 1 | ~2 min | ~2 min |

**Recent Trend:**
- Last 11 plans: 01-01 (2 min), 01-02 (1 min), 01-03 (2 min), 02-01 (2 min), 02-02 (2 min), 03-01 (2 min), 03-02 (2 min), 04-01 (3 min), 05-01 (6 min), 05-02 (3 min), 06-01 (2 min)
- Trend: Stable ~2-3 min/plan; v2 plans expected slightly longer (GC integration, pattern matching)

*Updated after each plan completion*

## Accumulated Context

### Decisions

Decisions are logged in PROJECT.md Key Decisions table.
Recent decisions affecting current work:

- [v2.0 GC]: Use Boehm GC (libgc) — conservative collector, no IR changes, `-lgc` link only
- [v2.0 GC]: GC_INIT() emitted unconditionally as first op in @main (C-8 prevention)
- [v2.0 GC]: All heap allocation through GC_malloc without exception (C-9 prevention)
- [v2.0 GC]: Migrate closure environments from llvm.alloca to GC_malloc in Phase 7 BEFORE any heap type codegen (C-7 prevention)
- [v2.0 Strings]: String layout is {i64 length, ptr data} two-field heap struct; byte array in llvm.mlir.global (static) + GC_malloc'd header
- [v2.0 Lists]: EmptyList = llvm.mlir.zero (null ptr); Cons = GC_malloc(16) two-pointer cons cell
- [v2.0 PatMatch]: Match compiles to sequential cf.cond_br chain (same mechanism as if-else); always emit @lang_match_failure terminal (C-10 prevention)
- [06-01]: -o flag is optional; Path.GetFileNameWithoutExtension(inputPath) auto-derives output binary name

### Pending Todos

None.

### Blockers/Concerns

- [Phase 7, ACTIVE]: Confirm exact scope of v1 closure alloca in Elaboration.fs before migrating — all LlvmAllocaOp uses must be found and moved to GC_malloc
- [Phase 7, ACTIVE]: macOS requires `-L/opt/homebrew/opt/bdw-gc/lib` in clang flags; Linux uses system path — platform detection in Pipeline.fs needed

## Session Continuity

Last session: 2026-03-26
Stopped at: v2.0 roadmap created (Phases 7-11); ready to plan Phase 7
Resume file: None
