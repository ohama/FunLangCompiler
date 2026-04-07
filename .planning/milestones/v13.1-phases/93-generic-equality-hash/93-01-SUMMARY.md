# Phase 93 Plan 01: Heap Tag Word Summary

**One-liner:** Add slot-0 heap_tag to string/tuple/record/cons/ADT blocks; shift all data offsets in C runtime and compiler IR

## What Was Done

### Task 1: Update C runtime structs and all creation sites
- Added `LANG_HEAP_TAG_*` constants to `lang_runtime.h` (STRING=1, TUPLE=2, RECORD=3, LIST=4, ADT=5)
- Added `int64_t heap_tag` as first field of `LangString_s` struct (16 -> 24 bytes)
- Added `int64_t heap_tag` as first field of `LangCons` struct (16 -> 24 bytes)
- Set `heap_tag` at all 34 LangString and 11 LangCons creation sites
- Updated `lang_hashtable_trygetvalue`: tuple now 4 slots (tag=TUPLE, count=2, bool, value)
- Updated `lang_for_in_hashtable`: tuple now 4 slots (tag=TUPLE, count=2, key, value)
- Commit: `7aeec11`

### Task 2: Update compiler GEP offsets and MatchCompiler indices
- **String literal (ElabHelpers.fs):** alloc 24 bytes, store tag@0 len@1 data@2
- **String data access (Elaboration.fs):** all 20 GEPStructOp sites shifted slot 1 -> 2
- **Tuple allocation:** `(n+2)*8` bytes, TUPLE tag@0, field count@1, fields@2+
- **Cons allocation:** 24 bytes, LIST tag@0, head@1, tail@2
- **ADT allocation (nullary+unary):** 24 bytes, ADT tag@0, ctor_tag@1, payload@2
- **Record allocation (RecordExpr+RecordUpdate):** `(n+2)*8` bytes, RECORD tag@0, count@1, fields@2+
- **FieldAccess/SetField:** record GEP indices +2
- **LetPat TuplePat:** field GEP i -> i+2
- **LetPat ConsPat (ElabHelpers):** head@1, tail@2 (was head@0, tail@1)
- **MatchCompiler:** ADT field i+1->i+2, Cons i->i+1, Tuple/Record i->i+2
- **Printer.fs:** string struct type `(i64, ptr)` -> `(i64, i64, ptr)` for GEPStructOp validation
- **ADT tag read in match:** slot 0 -> slot 1 (both emitCtorTest instances)
- **ensureRecordFieldTypes:** GEP +2 (both instances)
- Commit: `c8d843f`

## Decisions Made

| Decision | Rationale |
|----------|-----------|
| Tag constants 1-5, no overlap with hashtable tag=-1 | Arrays/hashtables use non-negative length or -1 at slot 0; small positive tags are safe |
| Tuple/record get both tag AND field count | Generic hash/equality (Plan 02) needs iteration count at runtime |
| Closures, arrays, mutable cells, hashtables unchanged | Never used as hash keys; adding tags would break function call ABI |
| Printer struct type updated | MLIR GEPStructOp validates field index against struct type definition |

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing] Printer.fs struct type update**
- **Found during:** Task 2 verification
- **Issue:** GEPStructOp emits `!llvm.struct<(i64, ptr)>` which only has 2 fields; index 2 is out of bounds
- **Fix:** Updated Printer.fs to emit `!llvm.struct<(i64, i64, ptr)>` matching new 3-field string layout
- **Files modified:** Printer.fs
- **Commit:** c8d843f

## Metrics

- **Duration:** ~18 minutes
- **Tests:** 257/257 passing
- **Files modified:** 5 (lang_runtime.h, lang_runtime.c, Elaboration.fs, ElabHelpers.fs, MatchCompiler.fs, Printer.fs)

## Next Phase Readiness

Plan 02 (generic hash/equality) can now dispatch on `((int64_t*)ptr)[0]` to determine the heap type of any pointer value. The field count at slot 1 of tuples/records enables iteration without compile-time type information.
