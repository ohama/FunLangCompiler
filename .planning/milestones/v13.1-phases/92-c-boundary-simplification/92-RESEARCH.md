# Phase 92: C Boundary Simplification - Research

**Researched:** 2026-04-07
**Domain:** Compiler/Runtime C boundary for tagged integer representation
**Confidence:** HIGH

## Summary

Phase 92 eliminates emitUntag/emitRetag calls in Elaboration.fs by moving the untag/retag responsibility into the C runtime functions themselves. Currently, the compiler emits inline MLIR ops to untag integers before C calls and retag integers after C calls. After this phase, C functions will receive tagged integers directly and use LANG_UNTAG_INT/LANG_TAG_INT macros internally.

There are **45 emitUntag/emitRetag call sites** across Elaboration.fs, ElabHelpers.fs, and ElabProgram.fs. These fall into three categories: (1) untag-before-C-call (~25 sites), (2) retag-after-C-call (~12 sites), and (3) arithmetic/exit-code untag (~8 sites that must NOT change). The C runtime has ~20 functions that need internal untag/retag additions.

**Primary recommendation:** Change C functions one group at a time (char functions, string functions, array functions, collection count functions, index dispatch functions), removing corresponding emitUntag/emitRetag in Elaboration.fs in lockstep, running tests after each group.

## Complete Inventory of emitUntag/emitRetag Sites

### Category A: C-Boundary Sites TO REMOVE (target of Phase 92)

#### A1: Functions that receive int args (emitUntag before C call)

| Line(s) | Builtin | C Function | Args to Untag | Notes |
|----------|---------|------------|----------------|-------|
| Elab:857-858 | string_sub | lang_string_sub | start, len (2 args) | |
| Elab:968,974 | show (int/bool) | lang_to_string_bool, lang_to_string_int | argVal (1 arg) | Two paths: bool and int |
| Elab:1040,1046 | to_string (int/bool) | lang_to_string_bool, lang_to_string_int | argVal (1 arg) | Same pattern as show |
| Elab:1095 | array_set | lang_array_bounds_check + GEP | idx (1 arg) | Also used in GEP arithmetic |
| Elab:1117 | array_get | lang_array_bounds_check + GEP | idx (1 arg) | Same pattern |
| Elab:1141 | array_create | lang_array_create | n (1 arg) | defVal stays tagged |
| Elab:1212 | IndexGet (string char_at) | lang_string_char_at | idx (1 arg) | |
| Elab:1223 | IndexGet (array) | lang_index_get | idx (1 arg) | Runtime dispatch |
| Elab:1245 | IndexSet (array) | lang_index_set | idx (1 arg) | Runtime dispatch |
| Elab:1352 | array_init | lang_array_init | n (1 arg) | |
| Elab:1479 | mutablelist_set | lang_mlist_set | idx (1 arg) | |
| Elab:1490 | mutablelist_get | lang_mlist_get | idx (1 arg) | |
| Elab:1567 | dbg | lang_to_string_int | argVal (1 arg) | |
| Elab:1781 | char_to_upper | lang_char_to_upper | char (1 arg) | |
| Elab:1790 | char_to_lower | lang_char_to_lower | char (1 arg) | |
| Elab:3503-3506 | string_slice | lang_string_slice | start, stop (2 args) | stop may be raw -1 sentinel |
| ElabH:218 | emitCharPredicate | lang_char_is_* (4 funcs) | char (1 arg) | Used by is_digit/letter/upper/lower |
| ElabH:465 | coerceToI64Arg | sprintf wrappers | int arg (1 arg) | Used by sprintf_1i, _2ii, _2si, _2is |

#### A2: Functions that return int results (emitRetag after C call)

| Line(s) | Builtin | C Function | Notes |
|----------|---------|------------|-------|
| Elab:898 | string_indexof | lang_string_indexof | Returns raw int |
| Elab:1060 | string_length | inline GEP+load | Loads raw length from struct |
| Elab:1084 | string_to_int | lang_string_to_int | Returns raw int |
| Elab:1156 | array_length | inline GEP+load | Loads raw length from array[0] |
| Elab:1214 | IndexGet (string char_at) | lang_string_char_at | Returns raw char code |
| Elab:1309 | hashtable_count | inline GEP+load | Loads raw size from struct field 2 |
| Elab:1440 | hashset_count | lang_hashset_count | Returns raw int |
| Elab:1467 | queue_count | lang_queue_count | Returns raw int |
| Elab:1511 | mutablelist_count | lang_mlist_count | Returns raw int |
| Elab:1783 | char_to_upper | lang_char_to_upper | Returns raw char code |
| Elab:1792 | char_to_lower | lang_char_to_lower | Returns raw char code |

### Category B: Arithmetic Sites - DO NOT CHANGE

| Line(s) | Operation | Why Keep |
|----------|-----------|----------|
| Elab:42-45 | Multiply | Untag operands for MulI, retag result - pure arithmetic |
| Elab:50-53 | Divide | Same pattern |
| Elab:58-61 | Modulo | Same pattern |

These are NOT C boundary calls. They use MLIR arithmetic ops (ArithMulIOp, ArithDivSIOp, ArithRemSIOp) which require raw integers. These must stay.

### Category C: Exit Code Sites - DO NOT CHANGE

| Line(s) | File | Why Keep |
|----------|------|----------|
| ElabP:28 | ElabProgram.fs | Untag @main return for process exit code |
| ElabP:452 | ElabProgram.fs | Same, project-file module path |

These untag the final program result for the OS exit code. Not a C boundary issue.

### Category D: coerceToI64 retag (I1 -> tagged I64) - KEEP

| Line(s) | File | Notes |
|----------|------|-------|
| ElabH:378 | ElabHelpers.fs | I1 zext then retag - converts bool to tagged int representation |

This is a type coercion, not a C boundary.

## C Functions Requiring Changes

### Group 1: Char Functions (simple, low risk)
These currently receive/return raw int64_t. Add LANG_UNTAG_INT on input, LANG_TAG_INT on output.

| Function | Receives | Returns | Change |
|----------|----------|---------|--------|
| lang_char_is_digit(c) | raw char | raw 0/1 | Add UNTAG on c; return stays raw (compared != 0 in compiler) |
| lang_char_is_letter(c) | raw char | raw 0/1 | Same |
| lang_char_is_upper(c) | raw char | raw 0/1 | Same |
| lang_char_is_lower(c) | raw char | raw 0/1 | Same |
| lang_char_to_upper(c) | raw char | raw char | Add UNTAG on c, TAG on return |
| lang_char_to_lower(c) | raw char | raw char | Add UNTAG on c, TAG on return |

**Note on char predicates:** The compiler compares their return against 0 (not tagged 0). If we make them return tagged, we'd need to change the comparison too. **Recommendation:** Only untag the input; keep return as raw 0/1 since the compiler compares with raw 0. For char_to_upper/lower, both untag input AND tag output.

### Group 2: String Functions with int args/returns

| Function | Int Args | Int Return | Change |
|----------|----------|------------|--------|
| lang_to_string_int(n) | n: raw int | Ptr (no change) | Add UNTAG on n |
| lang_to_string_bool(b) | b: raw bool | Ptr (no change) | Add UNTAG on b |
| lang_string_sub(s, start, len) | start, len: raw | Ptr (no change) | Add UNTAG on start, len |
| lang_string_slice(s, start, stop) | start, stop: raw | Ptr (no change) | Add UNTAG on start, stop; BUT stop=-1 sentinel is raw! |
| lang_string_char_at(s, index) | index: raw | raw char code | Add UNTAG on index, TAG on return |
| lang_string_indexof(s, sub) | none | raw int | TAG on return |
| lang_string_to_int(s) | none | raw int | TAG on return |

### Group 3: Array Functions

| Function | Int Args | Int Return | Change |
|----------|----------|------------|--------|
| lang_array_create(n, default_val) | n: raw count | Ptr | UNTAG n; default_val is already tagged (values stored tagged) |
| lang_array_bounds_check(arr, i) | i: raw index | void | UNTAG i; BUT arr[0] stores raw length |
| lang_array_init(n, closure) | n: raw count | Ptr | UNTAG n; closure already tags index via LANG_TAG_INT |
| lang_index_get(coll, index) | index: raw | I64 (tagged value) | UNTAG index; return stays as-is (loaded from tagged storage) |
| lang_index_set(coll, index, value) | index: raw | void | UNTAG index |

**Critical detail for lang_index_get/set:** These dispatch to hashtable when first_word < 0. Currently they do `LANG_TAG_INT(index)` when calling hashtable_get/set (line 625, 638). After Phase 92, `index` arrives tagged, so the LANG_TAG_INT wrapper must be removed -- the index is already tagged.

### Group 4: MutableList Functions

| Function | Int Args | Int Return | Change |
|----------|----------|------------|--------|
| lang_mlist_get(ml, index) | index: raw | I64 (tagged value) | UNTAG index |
| lang_mlist_set(ml, index, value) | index: raw | void | UNTAG index |
| lang_mlist_count(ml) | none | raw len | TAG return |

### Group 5: Collection Count Functions

| Function | Change |
|----------|--------|
| lang_hashset_count(hs) | TAG return (currently returns raw hs->size) |
| lang_queue_count(q) | TAG return (currently returns raw q->count) |
| lang_mlist_count(ml) | TAG return (currently returns raw ml->len) |

### Group 6: sprintf Wrappers

| Function | Int Args | Change |
|----------|----------|--------|
| lang_sprintf_1i(fmt, a) | a: raw | UNTAG a |
| lang_sprintf_2ii(fmt, a, b) | a, b: raw | UNTAG both |
| lang_sprintf_2si(fmt, a, b) | b: raw | UNTAG b |
| lang_sprintf_2is(fmt, a, b) | a: raw | UNTAG a |

## Functions That Already Handle Tagged Values (NO CHANGE)

These were updated in Phase 90-91 to work with tagged values via LSB dispatch:

| Function | Why No Change |
|----------|---------------|
| lang_hashtable_set/get/containsKey/remove | Keys already passed tagged (Phase 90) |
| lang_hashtable_keys/create/trygetvalue | No int args |
| lang_hashset_add/contains | Values already passed tagged (Phase 91) |
| lang_hashset_create | No int args |
| lang_queue_create/enqueue/dequeue | Values already tagged |
| lang_mlist_create/add | Values already tagged |
| lang_array_fold/iter/map | Values in arrays already tagged |
| lang_array_sort | Sorts tagged values (comparison still works since tag preserves order) |
| lang_array_of_list/to_list | Values already tagged |
| lang_for_in_* | Values already tagged |
| lang_list_comp/sort_by | Values already tagged |
| lang_range | Special: receives tagged start/stop, raw step (step is 2*k not 2k+1) |
| lang_string_* (no int args) | concat, trim, split, replace, toupper, tolower, contains, endswith, startswith |
| lang_sb_*/file_*/eprint* | No int args |

## Special Cases and Gotchas

### 1. lang_range Step Conversion
Currently the compiler converts tagged step to "raw double": `rawStep = taggedStep - 1` which gives `2k` (the step in tagged space). This is NOT a standard untag. This pattern must be preserved -- lang_range works in tagged space for start/stop but needs the doubled step. **Do not change lang_range.**

### 2. string_slice Stop Sentinel
`lang_string_slice` receives stop=-1 as a raw sentinel (meaning "to end of string"). After Phase 92, if stop is tagged, the C function would receive `tagged(-1) = -1` (since (-1 << 1) | 1 = -1). Wait -- `(-1 << 1) | 1 = -1` is true! So tagged(-1) = -1. This means the sentinel actually works correctly even with tagged values. But the compiler currently passes raw -1 for the "no stop" case (line 3497: `ArithConstantOp(sv, -1L)`). This raw -1 would NOT be untagged since it doesn't go through emitUntag (line 3507: `None -> (stopVal, [])`). So after Phase 92, the C function would see: user-provided stop as tagged, sentinel as raw -1. **The C function needs to untag user-provided stop but not the sentinel.** Since tagged(-1) = -1 = raw(-1), the LANG_UNTAG_INT(-1) = -1 >> 1 = -1 (arithmetic shift). So UNTAG(-1) = -1. This means we CAN safely UNTAG the stop unconditionally, since UNTAG(-1) = -1 preserves the sentinel.

### 3. Inline GEP+Load Patterns (string_length, array_length, hashtable_count)
These load raw values from struct fields and then emitRetag. After Phase 92, two options:
- **Option A:** Keep inline GEP+load but still emitRetag (since struct fields store raw values)
- **Option B:** Replace with C function calls that return tagged values

**Recommendation:** Option A is simpler -- just keep the retag for these 3 inline patterns. The struct fields store raw counts (set by C code like `arr[0] = n`). Changing the stored format would be far more invasive. But these inline patterns DO need to keep emitRetag. Alternatively, to fully eliminate emitRetag from the compiler, create tiny C wrapper functions: `lang_string_length(s)` returning `LANG_TAG_INT(s->length)`, `lang_array_length(arr)` returning `LANG_TAG_INT(arr[0])`, `lang_hashtable_count_tagged(ht)` returning `LANG_TAG_INT(ht->size)`.

**Recommendation:** Create C wrapper functions for all three. This fully eliminates emitRetag from Elaboration.fs for C-boundary cases, making the simplification complete.

### 4. array_set/array_get GEP Arithmetic
Currently these untag the index, then do `slot = rawIdx + 1` and `GEP arr slot`. After Phase 92, the C bounds_check would untag internally, but the GEP arithmetic still needs a raw index. **Two options:**
- **Option A:** Move the entire array_get/set to C functions (bounds check + element access)
- **Option B:** Keep inline GEP but still untag index in the compiler for GEP arithmetic

**Recommendation:** Option A -- create `lang_array_get(arr, tagged_idx)` and `lang_array_set(arr, tagged_idx, val)` C functions. This eliminates both the emitUntag and the inline GEP+arithmetic, dramatically simplifying the compiler code.

### 5. dbg with Ptr Argument
The dbg handler (line 1563-1565) has a special path for Ptr args that does PtrToInt then calls lang_to_string_int with the raw pointer value (not untagged). This path should NOT be affected since it doesn't use emitUntag. Only the I64 path (line 1567) uses emitUntag.

### 6. char Predicate Return Values
The char_is_* functions return raw 0/1 which the compiler compares against raw 0 (ArithCmpIOp "ne"). These returns should NOT be tagged -- only the input char argument should be untagged internally.

### 7. coerceToI64Arg in sprintf
ElabHelpers.fs:465 uses `emitUntag` for I64 case in `coerceToI64Arg`. After Phase 92, if sprintf wrappers untag internally, this should just pass through: `| I64 -> (v, [])`.

## Risk Assessment

### Low Risk (change C + remove untag/retag, straightforward)
1. **char functions** (6 functions, 6 compiler sites) - Simple, isolated
2. **to_string_int/bool** (2 functions, 4 compiler sites) - Straightforward
3. **collection count functions** (3 functions, 3 compiler sites) - Just TAG return
4. **string_to_int** (1 function, 1 site) - Just TAG return
5. **string_indexof** (1 function, 1 site) - Just TAG return

### Medium Risk (slightly more complex)
6. **string_sub, string_slice** (2 functions, 2 sites) - Multiple int args, sentinel issue
7. **sprintf wrappers** (4 functions, coerceToI64Arg) - Multiple call paths
8. **array_create, array_init** (2 functions, 2 sites) - Mixed tagged/raw args

### Higher Risk (structural change)
9. **array_get/set** (new C functions replacing inline GEP) - Behavioral change
10. **lang_index_get/set** (2 functions, 2 sites) - Dispatch + LANG_TAG_INT removal
11. **Inline GEP patterns** (string_length, array_length, hashtable_count) - New C wrapper functions
12. **mutablelist_get/set** - Index bounds check with tagged vs raw

## Recommended Implementation Order

1. **Group 1: Char functions** -- 6 C functions, ~6 compiler sites, low risk
2. **Group 2: to_string_int/bool + dbg** -- 2 C functions, ~5 compiler sites
3. **Group 3: sprintf wrappers** -- 4 C functions, 1 helper change
4. **Group 4: String int functions** -- string_sub, string_slice, string_char_at, string_indexof, string_to_int
5. **Group 5: Collection counts** -- hashset_count, queue_count, mutablelist_count + new wrappers for string_length, array_length, hashtable_count
6. **Group 6: Array functions** -- New lang_array_get/set C functions, array_create, array_init, array_bounds_check
7. **Group 7: Index dispatch** -- lang_index_get/set with LANG_TAG_INT removal
8. **Group 8: MutableList** -- mutablelist_get/set

## Metrics

| Metric | Before | After (estimated) |
|--------|--------|-------------------|
| emitUntag calls in Elaboration.fs | ~25 C-boundary sites | 0 |
| emitRetag calls in Elaboration.fs | ~12 C-boundary sites | 0 |
| Arithmetic emitUntag/retag (kept) | 6 | 6 |
| Exit-code emitUntag (kept) | 2 | 2 |
| coerceToI64 retag (kept) | 1 | 1 |
| C functions modified | 0 | ~20 |
| New C wrapper functions | 0 | ~3 (string_length, array_length, array_get/set) |
| Net MLIR ops eliminated per C call | 0 | 2-3 per site |

## Open Questions

1. **Should array_get/set become C functions?** Moving to C is cleaner but adds function call overhead for every array access. The inline GEP pattern is faster. Compromise: keep inline for -O3, use C function for -O0/-O2. **Recommendation for Phase 92:** Move to C for simplicity; optimize later if needed.

2. **Should string_length/array_length stay inline?** Same tradeoff. A GEP+load is 2 instructions; a C function call is ~5. **Recommendation:** Create C wrappers for Phase 92 simplicity. The performance impact is negligible for these (called once, not in tight loops typically).

3. **lang_range special handling:** The current tagged-start/stop + raw-doubled-step pattern is unique. Should it change? **Recommendation:** Leave lang_range as-is. It already works correctly with tagged values.

## Sources

### Primary (HIGH confidence)
- Elaboration.fs: Full source code analysis, line-by-line inventory
- ElabHelpers.fs: emitUntag/emitRetag implementation, helper functions
- ElabProgram.fs: Exit code untag patterns
- lang_runtime.c: All C function signatures and implementations

### Confidence breakdown
- Complete inventory: HIGH - every emitUntag/emitRetag call verified with source lines
- C function changes: HIGH - each function's behavior verified in source
- Risk assessment: HIGH - based on structural analysis of each change
- Inline GEP decision: MEDIUM - performance tradeoff not measured

**Research date:** 2026-04-07
**Valid until:** 2026-05-07 (stable domain, no external dependencies)
