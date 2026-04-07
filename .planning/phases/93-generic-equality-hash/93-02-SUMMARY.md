# Phase 93 Plan 02: Generic Hash and Equality Summary

**One-liner:** Replace string-only LSB dispatch with recursive generic hash/eq that handles tagged ints, strings, tuples, records, lists, and ADTs via heap tag at slot 0

## What Was Done

### Task 1: Implement lang_generic_hash and lang_generic_eq
- Replaced bodies of `lang_ht_hash` and `lang_ht_eq` (kept function names to avoid touching call sites)
- **lang_ht_hash dispatch:**
  - `val & 1` -> tagged int: murmurhash3 finalizer (unchanged)
  - `val == 0` -> NULL: returns 0
  - `block[0] == STRING` -> FNV-1a over string bytes (unchanged algorithm)
  - `block[0] == TUPLE/RECORD` -> `h = tag; for each field: h = h*31 + hash(field)`
  - `block[0] == LIST` -> iterate cons cells (head at slot 1, tail at slot 2), depth limit 256
  - `block[0] == ADT` -> hash constructor tag + payload
  - default -> hash pointer value as fallback
- **lang_ht_eq dispatch:**
  - Same pointer -> true (fast path)
  - Different LSB parity or different heap tags -> false
  - STRING -> length check + memcmp
  - TUPLE/RECORD -> field count match + recursive field comparison
  - LIST -> parallel cons cell traversal with depth limit 256
  - ADT -> constructor tag match + payload comparison
- Commit: `379f128`

## Decisions Made

| Decision | Rationale |
|----------|-----------|
| Keep `lang_ht_hash`/`lang_ht_eq` names | Avoids touching 12+ call sites in hashtable/hashset code |
| Depth limit 256 for lists | Prevents stack overflow on pathological inputs; FunLang has no cyclic data |
| ADT hashes ctor tag + single payload slot | ADT layout is [tag, ctor, payload]; no field count stored |
| Unknown pointers hash by address | Safe fallback for closures etc. that should never be hash keys |

## Deviations from Plan

None -- plan executed exactly as written.

## Metrics

- **Duration:** ~4 minutes
- **Tests:** 257/257 passing
- **Files modified:** 1 (lang_runtime.c)

## Next Phase Readiness

Plan 03 (E2E tests for tuple/list/ADT as hashtable keys) can proceed. The generic hash/equality functions are in place and all existing tests pass.
