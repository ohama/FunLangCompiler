# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-03-30)

**Core value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** Phase 37 — Hashtable String Keys (v10.0)

## Current Position

Phase: 37 of 40 (Hashtable String Keys)
Plan: 01 of TBD
Status: In progress
Last activity: 2026-03-30 — Completed 37-01-PLAN.md (C runtime string-key hashtable functions)

Progress: [██░░░░░░░░] 20% (v10.0, Phase 37-01 complete)

## Performance Metrics

**Velocity:**
- Total plans completed: 72 (v1.0–v9.0)
- Average duration: ~10 min/plan
- Total execution time: ~12 hours

**By Phase (v10.0):**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 36 | TBD | - | - |
| 37 | TBD | - | - |
| 38 | TBD | - | - |
| 39 | TBD | - | - |
| 40 | TBD | - | - |

**Recent Trend:** Stable

## Accumulated Context

### Decisions

Recent decisions affecting current work:

- v9.0 Phase 34-03: for-in mutable capture segfault is pre-existing bug (now FIX-01)
- v9.0 Phase 35-01: Hashtable string keys crash — C hashtable uses int64_t key ABI (now RT-01/RT-02)
- v9.0 Phase 31-01: Two-sequential-if MLIR empty-block limitation (now FIX-02)
- v9.0 Phase 35-01: Bool module function returns I64, needs `<> 0` workaround (now FIX-03)
- Phase 36-01: FIX-02 uses blocksAfterBind - 1 index (not List.last) to target outer merge block
- Phase 36-01: If case also needs terminator detection when condExpr is And/Or (patches CfCondBrOp into merge block)
- Phase 36-01: FIX-01, FIX-02, FIX-03 all resolved — downstream workarounds can be removed
- Phase 37-01: LangHashtableStr uses tag=-2; lang_index_get_str/lang_index_set_str need no tag dispatch (string index always means string hashtable)
- Phase 37-01: LangString* stored as int64_t in LangCons.head via (int64_t)(uintptr_t)ptr — GC-safe, same as int-key keys()

### Pending Todos

None.

### Blockers/Concerns

- RT-01/RT-02 (Hashtable string key ABI): RESOLVED in Phase 37-01. C runtime now has LangHashtableStr with all 9 string-key functions.
- FIX-01 RESOLVED: for-in mutable capture works correctly (verified by 36-01-forin-mutable-capture.flt)
- FIX-02 RESOLVED: Sequential if expressions produce valid MLIR (verified by 36-02-sequential-if.flt)
- FIX-03 RESOLVED: And/Or/While accept I1-typed conditions (verified by 36-03-bool-and-or-while.flt)

## Session Continuity

Last session: 2026-03-30
Stopped at: Completed 37-01-PLAN.md — C runtime string-key hashtable (LangHashtableStr + 9 functions)
Resume file: None
