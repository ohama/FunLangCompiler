# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-04-02)

**Core value:** FunLang 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** Phase 92 — C Boundary Simplification (complete)

## Current Position

Phase: 92 (C Boundary Simplification)
Plan: 92-02 of 92-02
Status: Phase complete
Last activity: 2026-04-07 — Completed 92-02-PLAN.md (structural GEP + array access)

Progress: v1.0-v21.0 + Phase 88-92 [█████████████████████░] 69 phases / 114 plans

## Performance Metrics

**Velocity:**
- Total plans completed: 114
- Average duration: ~10 min/plan

## Accumulated Context

### Decisions

| Decision | Context | Date |
|----------|---------|------|
| Range values stay tagged in C lists | lang_range receives tagged start/stop, raw step = tagged_step - 1 | 2026-04-07 |
| C-callback limitation accepted for Phase 88 | 4 tests regress (array_init, for-in-hashset, for-in-hashtable, hashtable-trygetvalue) until Phase 89 | 2026-04-07 |
| @main return handles all types | I64: untag; I1: zext; Ptr: return 0 | 2026-04-07 |
| LSB dispatch for hashtable keys | key & 1 selects hash/equality: tagged int (LSB=1) vs string pointer (LSB=0) | 2026-04-07 |
| HashSet uses dedicated hash function | HashSet stores raw untagged ints, needs murmurhash-only (no LSB dispatch) | 2026-04-07 |
| lang_index_get/set pass tagged index | C functions untag internally for arrays, pass tagged directly for hashtables | 2026-04-07 |
| string_sub_raw helper for internal calls | lang_string_slice calls string_sub_raw (not lang_string_sub) to avoid double-untag | 2026-04-07 |
| array_set coerces value to I64 | coerceToI64 ensures I1/Ptr values are compatible with C int64_t parameter | 2026-04-07 |

### Pending Todos

- None

### Blockers/Concerns

- None — 257/257 tests pass

## Session Continuity

Last session: 2026-04-07
Stopped at: Completed 92-02-PLAN.md
Resume file: None
