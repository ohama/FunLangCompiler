# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-04-02)

**Core value:** FunLang 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** Phase 93 — Generic Equality and Hash (complete)

## Current Position

Phase: 93 (Generic Equality and Hash)
Plan: 93-03 of 93-03
Status: Phase complete
Last activity: 2026-04-07 — Completed 93-03-PLAN.md (E2E tests + generic = operator)

Progress: v1.0-v21.0 + Phase 88-93 [█████████████████████░] 69 phases / 117 plans

## Performance Metrics

**Velocity:**
- Total plans completed: 117
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
| Heap tag constants 1-5, closures/arrays untagged | Only string/tuple/record/cons/ADT get tags; closures never used as hash keys | 2026-04-07 |
| Tuple/record store field count at slot 1 | Generic hash/equality needs iteration count at runtime without type info | 2026-04-07 |
| Generic hash/eq replaces LSB-only dispatch | lang_ht_hash/lang_ht_eq now dispatch on heap tag for all 5 types + tagged ints | 2026-04-07 |
| = operator uses lang_generic_eq for Ptr types | Replaces strcmp-only comparison; enables structural equality for tuples, records, lists, ADTs | 2026-04-07 |

### Pending Todos

- None

### Blockers/Concerns

- None — 260/260 tests pass

## Session Continuity

Last session: 2026-04-07
Stopped at: Completed 93-03-PLAN.md (Phase 93 complete)
Resume file: None
