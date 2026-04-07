# Phase 93: Generic Equality and Hash - Research

**Researched:** 2026-04-07
**Domain:** Runtime type tagging, structural hash/equality, LLVM IR codegen
**Confidence:** HIGH

## Summary

This phase adds runtime type discrimination for heap-allocated values so that `lang_ht_hash` and `lang_ht_eq` can structurally hash and compare any value -- not just tagged integers and strings. Currently, all pointer values (LSB=0) are assumed to be strings in the hash/equality functions, which crashes or produces incorrect results for tuples, records, lists, and ADTs used as hashtable keys.

The standard approach is to add a **header tag word** to every heap-allocated block, following a pattern similar to OCaml's header word. The most practical variant for FunLang is to allocate one extra i64 slot at offset 0 for a type tag, shifting all data fields by +1 slot. This affects 5 allocation categories in the compiler and approximately 13 GC_malloc call sites in Elaboration.fs/ElabHelpers.fs.

The generic hash and equality functions in `lang_runtime.c` then dispatch on `(value & 1)` for int vs pointer, and on `((int64_t*)ptr)[0]` for the specific pointer type (string/tuple/record/list/ADT). Both functions must be recursive for compound types.

**Primary recommendation:** Add a tag word at slot 0 of every heap block (string, tuple, record, list cons, ADT). Shift all data by +1 slot. Implement `lang_generic_hash` and `lang_generic_eq` in C with recursive dispatch.

## Current Heap Layouts (Pre-Phase 93)

Understanding what must change:

### String: `LangString` struct (C-side)
```
Offset 0: int64_t length
Offset 8: char*   data
```
- Size: 16 bytes (2 x i64)
- Allocated in: `elaborateStringLiteral` (ElabHelpers.fs), all `lang_string_*` C functions
- Accessed via: `LlvmGEPStructOp(ptr, 0)` for length, `LlvmGEPStructOp(ptr, 1)` for data

### Tuple: flat i64 array
```
Offset 0: field_0 (i64)
Offset 8: field_1 (i64)
...
Offset (n-1)*8: field_(n-1) (i64)
```
- Size: `n * 8` bytes
- Allocated in: Elaboration.fs `Tuple` case
- Accessed via: `LlvmGEPLinearOp(ptr, i)` for field i

### Record: flat i64 array (same as tuple)
```
Offset 0: field_0 (i64)  -- ordered by RecordEnv declaration order
...
```
- Size: `n * 8` bytes
- Allocated in: Elaboration.fs `RecordExpr` case, `RecordUpdate` case
- Accessed via: `LlvmGEPLinearOp(ptr, slotIdx)`

### List Cons Cell
```
Offset 0: int64_t head
Offset 8: LangCons* tail  (NULL for end)
```
- Size: 16 bytes
- Allocated in: Elaboration.fs `Cons` case, many C runtime functions
- Accessed via: `LlvmGEPLinearOp(ptr, 0)` for head, `LlvmGEPLinearOp(ptr, 1)` for tail
- Nil = NULL pointer (not a heap block)

### ADT Data Value
```
Offset 0: int64_t tag      (constructor discriminant, 0-based)
Offset 8: int64_t payload  (single field or pointer to tuple)
```
- Size: 16 bytes (always 2 slots)
- Allocated in: Elaboration.fs `Constructor` case
- Accessed via: `LlvmGEPLinearOp(ptr, 0)` for tag, `LlvmGEPLinearOp(ptr, 1)` for payload

### Other heap blocks (NOT tagged -- keep as-is)
- Closure environments: `{fn_ptr, capture_0, capture_1, ...}` -- never used as hash keys
- Mutable variables: 8-byte cell -- never used as hash keys
- Arrays: `{length, elem_0, elem_1, ...}` -- length at slot 0 already serves as discriminant
- Hashtable: `{tag=-1, capacity, size, buckets}` -- tag=-1 already serves as discriminant
- HashSet, Queue, MutableList, StringBuilder: specialized structs -- never hash keys
- Exception frames: 272-byte block -- never hash keys

## Architecture Patterns

### Recommended: Tag Word at Slot 0

**What:** Every heap block that might be used as a hash key gets a type-tag i64 at slot 0. All data shifts by +1 slot.

**Tag constants:**
```c
#define LANG_HEAP_TAG_STRING  1
#define LANG_HEAP_TAG_TUPLE   2
#define LANG_HEAP_TAG_RECORD  3
#define LANG_HEAP_TAG_LIST    4
#define LANG_HEAP_TAG_ADT     5
```

**New layouts after tagging:**

String:
```
Offset 0: int64_t heap_tag = LANG_HEAP_TAG_STRING
Offset 8: int64_t length
Offset 16: char*  data
```
Size: 24 bytes (was 16)

Tuple (n fields):
```
Offset 0: int64_t heap_tag = LANG_HEAP_TAG_TUPLE
Offset 8: int64_t num_fields  (needed for generic hash to know iteration count)
Offset 16: field_0
Offset 24: field_1
...
```
Size: `(n + 2) * 8` bytes (was `n * 8`)

Record (n fields):
```
Offset 0: int64_t heap_tag = LANG_HEAP_TAG_RECORD
Offset 8: int64_t num_fields
Offset 16: field_0
...
```
Size: `(n + 2) * 8` bytes (was `n * 8`)

List Cons:
```
Offset 0: int64_t heap_tag = LANG_HEAP_TAG_LIST
Offset 8: int64_t head
Offset 16: LangCons* tail
```
Size: 24 bytes (was 16)

ADT:
```
Offset 0: int64_t heap_tag = LANG_HEAP_TAG_ADT
Offset 8: int64_t constructor_tag
Offset 16: int64_t payload
```
Size: 24 bytes (was 16)

### Alternative Considered: Reuse ADT tag / structural detection

**Why rejected:** Tuples and records have no existing tag. List cons cells are indistinguishable from 2-field tuples at runtime. Without a type tag there is NO way to tell a `(int, int)` tuple from a cons cell from an ADT with tag=0. A uniform header tag is the only reliable solution.

### Alternative Considered: Header word BEFORE pointer (OCaml-style)

**Why rejected:** OCaml allocates `header + data` and returns a pointer to data (header at ptr-8). This would work but requires changing the GC_malloc wrapper to allocate N+8 bytes and return ptr+8. Every existing C runtime function that receives a LangString*, LangCons*, etc. would still work (they'd receive the offset pointer). However, `GC_free` and GC internals expect the original allocation pointer, not an offset. With Boehm GC this is actually fine (interior pointers are supported), but it adds cognitive complexity. The slot-0 approach is simpler and more explicit.

### Alternative Considered: Tag at slot 0 WITHOUT storing num_fields

**Why rejected for tuples/records:** Generic hash needs to know how many fields to iterate. Options: (a) store field count, (b) use type information from compiler. Since hash/eq runs in C at runtime without type info, storing the field count is necessary. The 8-byte overhead per tuple/record is acceptable.

## Compiler Changes Required

### Change Category 1: Elaboration.fs -- Tuple allocation (1 site)
- Line ~2054: Change `n * 8` to `(n + 2) * 8`
- Store `LANG_HEAP_TAG_TUPLE` at GEP slot 0
- Store `n` at GEP slot 1
- Shift all field stores from `GEP(ptr, i)` to `GEP(ptr, i + 2)`

### Change Category 2: Elaboration.fs -- Tuple field access (multiple sites)
- Everywhere `LlvmGEPLinearOp(_, tupPtr, i)` accesses tuple field i, change to `i + 2`
- This includes match destructuring, field projection, etc.
- **Key search pattern:** Find all GEPLinearOp on tuple-typed values

### Change Category 3: Elaboration.fs -- Record allocation (2 sites: RecordExpr, RecordUpdate)
- Same as tuple: add 2 header slots, shift field indices by +2
- Record field access via `LlvmGEPLinearOp(_, recPtr, slotIdx)` must become `slotIdx + 2`

### Change Category 4: Elaboration.fs -- Cons cell (1 allocation site)
- Line ~2084: Change `16L` to `24L`
- Store `LANG_HEAP_TAG_LIST` at slot 0
- Head store at slot 1 (was slot 0), tail at slot 2 (was slot 1)

### Change Category 5: Elaboration.fs -- ADT constructor (2 allocation sites: nullary, unary)
- Lines ~2602, ~2626: Change `16L` to `24L`
- Store `LANG_HEAP_TAG_ADT` at slot 0
- Constructor tag at slot 1 (was slot 0), payload at slot 2 (was slot 1)

### Change Category 6: ElabHelpers.fs -- String literal (1 site)
- Change `16L` to `24L`
- Store `LANG_HEAP_TAG_STRING` at slot 0
- Length at GEPStruct slot 1 (was 0), data at GEPStruct slot 2 (was 1)

### Change Category 7: Elaboration.fs -- Pattern matching
- ADT tag load: was `GEPLinearOp(_, ptr, 0)`, becomes `GEPLinearOp(_, ptr, 1)`
- ADT payload: was `GEPLinearOp(_, ptr, 1)`, becomes `GEPLinearOp(_, ptr, 2)`
- Cons head access: was `GEPLinearOp(_, ptr, 0)`, becomes `GEPLinearOp(_, ptr, 1)`
- Cons tail access: was `GEPLinearOp(_, ptr, 1)`, becomes `GEPLinearOp(_, ptr, 2)`
- String length: was `GEPStructOp(_, ptr, 0)`, becomes `GEPStructOp(_, ptr, 1)`
- String data: was `GEPStructOp(_, ptr, 1)`, becomes `GEPStructOp(_, ptr, 2)`

### Change Category 8: lang_runtime.c -- C struct definitions
- `LangString_s`: add `int64_t heap_tag` as first field
- `LangCons`: add `int64_t heap_tag` as first field
- All C functions that create these structs must set the tag
- `lang_hashtable_trygetvalue` creates raw 2-slot tuples -- must add tag + field count

### Change Category 9: lang_runtime.c -- Hash and equality functions
- Replace `lang_ht_hash` / `lang_ht_eq` with generic versions
- New dispatch: `if (val & 1)` -> int hash; else `switch(((int64_t*)ptr)[0])` -> type-specific

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Hash combining | Custom XOR-shift | `h = h * 31 + child_hash` or FNV-style combine | Well-studied, low collision rate |
| String hashing | New algorithm | Keep existing FNV-1a | Already proven in Phase 90 |
| Integer hashing | New algorithm | Keep existing murmurhash3 finalizer | Already proven in Phase 90 |
| Cycle detection in recursive hash | Full graph marking | Depth limit (e.g., 100) | Lists/ADTs can be recursive but deep recursion = stack overflow anyway |

## Common Pitfalls

### Pitfall 1: Forgetting to shift ALL GEP indices
**What goes wrong:** Adding a tag at slot 0 means every existing `GEPLinearOp(ptr, i)` must become `GEPLinearOp(ptr, i + offset)` for the tagged types. Missing even ONE causes reading wrong data.
**How to avoid:** Systematic search: `grep -n "GEPLinearOp\|GEPStructOp"` in Elaboration.fs and ElabHelpers.fs. Verify each one against its allocation site.
**Warning signs:** Segfaults, wrong values, tests that print garbage.

### Pitfall 2: C runtime struct mismatch
**What goes wrong:** C code reads `s->length` but the struct now has `heap_tag` at offset 0 and `length` at offset 8. If the C struct definition isn't updated, every string operation breaks.
**How to avoid:** Update `LangString_s` and `LangCons` struct definitions FIRST, then fix all creation sites.
**Warning signs:** All string tests fail.

### Pitfall 3: Hashtable/Array tag confusion
**What goes wrong:** `lang_index_get` currently checks `((int64_t*)collection)[0] < 0` for hashtable vs array. If tuples/records also have small positive tags at slot 0, they could be confused with arrays.
**How to avoid:** Tag values (1-5) are all small positive numbers. Array length at slot 0 is also non-negative. BUT arrays and hashtables are never used interchangeably with tuples in `lang_index_get`, so this dispatch is safe. Just ensure tag constants don't overlap with -1 (hashtable tag).
**Actually:** This is not a problem because `lang_index_get` is only called on arrays and hashtables (type system ensures this).

### Pitfall 4: Closures and mutable variables should NOT get tags
**What goes wrong:** If you add tags to closure environments or mutable variable cells, you break function calls and variable access.
**How to avoid:** Only tag the 5 types listed (string, tuple, record, list, ADT). Closures and mutable cells are never hash keys.

### Pitfall 5: C runtime creates cons cells and tuples too
**What goes wrong:** `lang_runtime.c` creates LangCons cells (e.g., `lang_range`, `lang_hashtable_keys`, `lang_list_comp`) and tuples (e.g., `lang_hashtable_trygetvalue`, `lang_for_in_hashtable`). These must also get the tag.
**How to avoid:** Search for `GC_malloc(sizeof(LangCons))` and `GC_malloc(16)` (2-slot tuples) in lang_runtime.c. Each must add the heap_tag field.

### Pitfall 6: Recursive hash on infinite/cyclic structures
**What goes wrong:** A list created with `let rec xs = 1 :: xs` would infinite-loop in generic hash.
**How to avoid:** FunLang doesn't support cyclic data via let rec on non-functions, so this shouldn't occur. But add a depth limit (e.g., 256) as safety net.

## Code Examples

### Generic hash function (C)
```c
static uint64_t lang_generic_hash(int64_t val) {
    if (val & 1) {
        // Tagged int -- murmurhash3 finalizer (existing code)
        uint64_t h = (uint64_t)val;
        h ^= h >> 33; h *= 0xff51afd7ed558ccdULL;
        h ^= h >> 33; h *= 0xc4ceb9fe1a85ec53ULL;
        h ^= h >> 33;
        return h;
    }
    // Pointer -- dispatch on heap tag at slot 0
    int64_t* block = (int64_t*)val;
    int64_t tag = block[0];
    switch (tag) {
    case LANG_HEAP_TAG_STRING: {
        LangString* s = (LangString*)block;
        uint64_t h = 14695981039346656037ULL;
        for (int64_t i = 0; i < s->length; i++) {
            h ^= (uint8_t)s->data[i];
            h *= 1099511628211ULL;
        }
        return h;
    }
    case LANG_HEAP_TAG_TUPLE:
    case LANG_HEAP_TAG_RECORD: {
        int64_t n = block[1]; // num_fields
        uint64_t h = (uint64_t)tag;
        for (int64_t i = 0; i < n; i++) {
            h = h * 31 + lang_generic_hash(block[2 + i]);
        }
        return h;
    }
    case LANG_HEAP_TAG_LIST: {
        uint64_t h = 0x9e3779b97f4a7c15ULL;
        int64_t* cur = block;
        while (cur != NULL) {
            h = h * 31 + lang_generic_hash(cur[1]); // head
            int64_t tail = cur[2];
            cur = (tail == 0) ? NULL : (int64_t*)tail;
        }
        return h;
    }
    case LANG_HEAP_TAG_ADT: {
        uint64_t h = lang_generic_hash(block[1]); // constructor tag (tagged int)
        int64_t payload = block[2];
        if (payload != 0) {
            h = h * 31 + lang_generic_hash(payload);
        }
        return h;
    }
    default:
        // Unknown pointer type -- hash the pointer value itself
        return (uint64_t)val * 0x9e3779b97f4a7c15ULL;
    }
}
```

### Generic equality function (C)
```c
static int lang_generic_eq(int64_t a, int64_t b) {
    if (a == b) return 1;  // Fast path: same value/pointer
    if ((a & 1) != (b & 1)) return 0;  // int vs ptr mismatch
    if (a & 1) return 0;  // Both ints but different (caught by a==b above)
    
    int64_t* ba = (int64_t*)a;
    int64_t* bb = (int64_t*)b;
    if (ba[0] != bb[0]) return 0;  // Different heap tags
    
    switch (ba[0]) {
    case LANG_HEAP_TAG_STRING: {
        LangString* sa = (LangString*)ba;
        LangString* sb = (LangString*)bb;
        return sa->length == sb->length &&
               memcmp(sa->data, sb->data, (size_t)sa->length) == 0;
    }
    case LANG_HEAP_TAG_TUPLE:
    case LANG_HEAP_TAG_RECORD: {
        int64_t na = ba[1], nb = bb[1];
        if (na != nb) return 0;
        for (int64_t i = 0; i < na; i++) {
            if (!lang_generic_eq(ba[2+i], bb[2+i])) return 0;
        }
        return 1;
    }
    case LANG_HEAP_TAG_LIST: {
        int64_t* ca = ba;
        int64_t* cb = bb;
        while (ca != NULL && cb != NULL) {
            if (!lang_generic_eq(ca[1], cb[1])) return 0;
            ca = (ca[2] == 0) ? NULL : (int64_t*)ca[2];
            cb = (cb[2] == 0) ? NULL : (int64_t*)cb[2];
        }
        return (ca == NULL && cb == NULL) ? 1 : 0;
    }
    case LANG_HEAP_TAG_ADT: {
        if (ba[1] != bb[1]) return 0;  // different constructor
        return lang_generic_eq(ba[2], bb[2]);  // compare payloads
    }
    default:
        return 0;
    }
}
```

### Compiler-side tuple allocation (F#, after change)
```fsharp
// Phase 93: Tuple construction — GC_malloc((n+2)*8): slot 0 = heap tag, slot 1 = field count, slots 2..n+1 = fields
| Tuple (exprs, _) ->
    let n = List.length exprs
    let bytesVal  = { Name = freshName env; Type = I64 }
    let tupPtrVal = { Name = freshName env; Type = Ptr }
    let tagSlot   = { Name = freshName env; Type = Ptr }
    let tagVal    = { Name = freshName env; Type = I64 }
    let countSlot = { Name = freshName env; Type = Ptr }
    let countVal  = { Name = freshName env; Type = I64 }
    let allocOps  = [
        ArithConstantOp(bytesVal, int64 ((n + 2) * 8))
        LlvmCallOp(tupPtrVal, "@GC_malloc", [bytesVal])
        LlvmGEPLinearOp(tagSlot, tupPtrVal, 0)
        ArithConstantOp(tagVal, 2L)  // LANG_HEAP_TAG_TUPLE
        LlvmStoreOp(tagVal, tagSlot)
        LlvmGEPLinearOp(countSlot, tupPtrVal, 1)
        ArithConstantOp(countVal, int64 n)
        LlvmStoreOp(countVal, countSlot)
    ]
    // Fields at slots 2..n+1
    let storeOps = fieldVals |> List.mapi (fun i fv -> ...)  // GEP(ptr, i+2)
```

## Impact Assessment

### Scope of compiler changes
- **Elaboration.fs:** ~55 GEPLinearOp/GEPStructOp references, approximately 30-40 need updating (those on tuple/record/list/ADT/string pointers). Others are on closures, arrays, mutable cells -- unchanged.
- **ElabHelpers.fs:** ~6 GEP references (string literal elaboration) -- all need updating.
- **lang_runtime.c:** ~25 GC_malloc sites creating LangCons cells or LangString structs -- all need tag field added.
- **lang_runtime.h:** Struct definitions for LangString, LangCons -- add heap_tag field.

### Risk level: MEDIUM-HIGH
- The change is mechanical but pervasive
- Missing a single GEP offset shift breaks tests
- Approach: change C structs first, then compiler, run tests after each category

### Performance impact
- 8 extra bytes per string, cons cell, ADT (was 16, now 24) = 50% overhead on small blocks
- 16 extra bytes per tuple/record (tag + count) = varies
- Hash/equality slightly slower due to tag check overhead
- Acceptable tradeoff for generic hashtable key support

## Execution Order (Critical)

The recommended implementation order to keep tests passing incrementally:

1. **Add heap_tag to C structs + all C creation sites** (LangString, LangCons). Update ALL C functions that access struct fields. Run tests -- they will ALL fail.
2. **Update compiler string allocation** (ElabHelpers.fs) + all string field access in Elaboration.fs. String tests should start passing.
3. **Update compiler cons cell allocation + list pattern matching + list access.** List tests pass.
4. **Update compiler ADT allocation + ADT pattern matching.** ADT tests pass.
5. **Update compiler tuple allocation + tuple field access.** Tuple tests pass.
6. **Update compiler record allocation + record field access + record update.** Record tests pass.
7. **Implement lang_generic_hash / lang_generic_eq.** Replace lang_ht_hash / lang_ht_eq.
8. **Add E2E tests for tuple/record/list as hashtable keys.**

**Alternative order (less risky):** Do all changes in a single batch (compiler + C together), since partial changes will break everything anyway.

## Open Questions

1. **Should tuples and records hash differently?**
   - Currently `LANG_HEAP_TAG_TUPLE=2` and `LANG_HEAP_TAG_RECORD=3` are separate tags
   - For hash/equality: a 2-field tuple `(1, 2)` and a 2-field record `{x=1, y=2}` SHOULD hash differently (they are different types)
   - Recommendation: Include the heap tag in the hash computation to distinguish them

2. **Should `lang_hashtable_trygetvalue` tuples get tagged?**
   - Currently creates `GC_malloc(16)` with 2 slots for `(bool, value)` result
   - These are consumed immediately and never used as hash keys
   - Recommendation: YES, tag them anyway for consistency; the Option type pattern match may read slot 0 expecting a tag

3. **Should closures stay untagged?**
   - Closures are never used as hash keys
   - No code reads a closure's slot 0 and interprets it as a type tag
   - Recommendation: Leave closures untagged. They have fn_ptr at slot 0 which is always a valid function pointer, never confused with small tag constants.

## Sources

### Primary (HIGH confidence)
- `src/FunLangCompiler.Compiler/lang_runtime.c` -- LangString, LangCons struct definitions, hash/eq functions, all GC_malloc sites
- `src/FunLangCompiler.Compiler/lang_runtime.h` -- All struct definitions, function signatures
- `src/FunLangCompiler.Compiler/Elaboration.fs` -- All heap allocation sites, GEP patterns, pattern matching
- `src/FunLangCompiler.Compiler/ElabHelpers.fs` -- String literal elaboration

### Secondary (MEDIUM confidence)
- OCaml header word design (well-known approach, used as reference for tag-at-slot-0 pattern)

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH -- all code is in-repo, fully auditable
- Architecture: HIGH -- slot-0 tag is the simplest approach that works with Boehm GC
- Pitfalls: HIGH -- identified from direct code reading of all affected sites
- Code examples: MEDIUM -- hash/equality functions need testing for edge cases

**Research date:** 2026-04-07
**Valid until:** 2026-05-07 (stable -- compiler internals)
