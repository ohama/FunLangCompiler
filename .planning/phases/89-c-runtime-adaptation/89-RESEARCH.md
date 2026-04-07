# Phase 89: C Runtime Adaptation - Research

**Researched:** 2026-04-07
**Domain:** C runtime tagging/untagging for tagged integer ABI (Phase 88)
**Confidence:** HIGH

## Summary

Phase 88 introduced tagged integers (2n+1 encoding) at the compiler level. The compiler untags integers before passing them to C runtime functions and retags results. However, 6 tests fail because certain C runtime functions **call back into FunLang closures** or **construct data structures** that the compiler reads without awareness of the raw/tagged boundary.

The C runtime (`lang_runtime.c`) is exclusively owned by the compiler -- the FunLang interpreter is pure F# and does not use it. Therefore, modifying the C runtime has ZERO impact on the interpreter.

**Primary recommendation:** Fix all 6 issues in the C runtime by adding TAG/UNTAG macros. This is the cleanest approach since: (a) the C runtime is compiler-private, (b) fixes are localized to ~10 lines across 4 functions, and (c) the alternative (compiler-side wrapper closures) would add significant complexity for each call site.

## Tagging Scheme

```
Tagged integer:   2n + 1   (always odd, low bit = 1)
Pointer:          raw ptr  (always even on 64-bit, low bit = 0)
Tagged true:      3        (2*1 + 1)
Tagged false:     1        (2*0 + 1)
```

**C macros needed:**
```c
#define LANG_TAG_INT(n)    (((n) << 1) | 1)
#define LANG_UNTAG_INT(v)  ((v) >> 1)
```

## Root Cause Analysis: 6 Failing Tests

### Issue 1: `lang_array_init` passes raw index to closure (Test 24-04)

**Test:** `array_init 5 (fun i -> i * i)` expects `0+1+4+9+16 = 30`

**C code (line 894):**
```c
for (int64_t i = 0; i < n; i++) {
    out[i + 1] = fn(closure, i);  // BUG: i is raw (0,1,2,3,4)
}
```

**Problem:** Closure expects tagged i (1,3,5,7,9). Inside the closure, `i * i` does `emitUntag` on both sides. Untagging raw 2 gives 1 (2>>1=1), not 2. Result is wrong.

**Fix (C-side):**
```c
out[i + 1] = fn(closure, LANG_TAG_INT(i));
```

### Issue 2: `lang_for_in_hashtable` passes raw key in tuple (Tests 34-05, 35-02)

**Test:** `for (k, v) in ht do println (to_string (k + v))` with ht[7]=100

**C code (line 826-828):**
```c
int64_t* tup = (int64_t*)GC_malloc(2 * sizeof(int64_t));
tup[0] = e->key;   // raw key (was untagged by compiler at hashtable_set time)
tup[1] = e->val;   // already tagged (compiler stores val as-is)
fn(closure, (int64_t)(uintptr_t)tup);
```

**Problem:** Keys are stored raw in the hashtable (compiler untags before `hashtable_set`). When iterating, the raw key goes into a tuple, and FunLang code treats it as tagged.

**Fix (C-side):**
```c
tup[0] = LANG_TAG_INT(e->key);   // retag key for FunLang consumption
tup[1] = e->val;                  // val is already tagged
```

### Issue 3: `lang_for_in_hashset` passes raw value to closure (Test 34-06)

**Test:** `for x in hs do println (to_string x)` with hs containing 42

**C code (line 798-799):**
```c
while (e != NULL) {
    fn(closure, e->key);  // BUG: key is raw (was untagged at hashset_add time)
    e = e->next;
}
```

**Fix (C-side):**
```c
fn(closure, LANG_TAG_INT(e->key));
```

### Issue 4: `lang_hashtable_trygetvalue` stores raw bool in tuple (Tests 32-01, 35-02)

**Test:** `let (found, v) = hashtable_trygetvalue ht 42` then `to_string found` expects "true"

**C code (line 461-466):**
```c
if (e != NULL) {
    tup[0] = 1;       // raw true -- BUG: should be tagged true (3)
    tup[1] = e->val;
} else {
    tup[0] = 0;       // raw false -- BUG: should be tagged false (1)
    tup[1] = 0;       // BUG: should be tagged 0 (= 1)
}
```

**Problem:** Compiler destructures the tuple and reads slot 0 as an I64 tagged value. Raw 1 is not tagged true (3), and raw 0 is not tagged false (1).

**Fix (C-side):**
```c
if (e != NULL) {
    tup[0] = LANG_TAG_INT(1);   // tagged true = 3
    tup[1] = e->val;            // val already tagged
} else {
    tup[0] = LANG_TAG_INT(0);   // tagged false = 1
    tup[1] = LANG_TAG_INT(0);   // tagged 0 = 1
}
```

**Note on `_str` variant:** `lang_hashtable_trygetvalue_str` has the same bool issue at its tup[0], needs the same fix. The tup[1] (val) is stored as-is (already tagged) so no change needed there.

### Issue 5: `lang_file_exists` returns raw 0/1 (Test 66-06)

**Test:** `file_exists inputPath` in a closure context

**C code (line 1153-1156):**
```c
int64_t lang_file_exists(LangString* path) {
    FILE* f = fopen(path->data, "r");
    if (f != NULL) { fclose(f); return 1; }
    return 0;
}
```

**Current compiler handling (line 1784-1791):**
```fsharp
let rawVal  = { Name = freshName env; Type = I64 }
let boolVal = { Name = freshName env; Type = I1  }
let ops = [
    LlvmCallOp(rawVal, "@lang_file_exists", [ptrVal])
    ArithConstantOp(zeroVal, 0L)
    ArithCmpIOp(boolVal, "ne", rawVal, zeroVal)
]
(boolVal, pathOps @ castOps @ ops)
```

**Analysis:** The compiler already converts the raw 0/1 to I1 via comparison, then I1 gets tagged at function return boundaries via `coerceToI64`. This should work correctly. The actual bug in test 66-06 may be a different issue -- likely related to closure capture or the `if-then-else` branching with `not`. Need to verify by running the test.

**Recommendation:** Run test 66-06 after fixing issues 1-4 to see if it passes. If not, debug separately -- it may be a compiler-side issue unrelated to C runtime tagging.

## Architecture: Compiler-side vs C-side Fix

### Why C-side is correct

| Factor | C-side fix | Compiler-side fix |
|--------|-----------|-------------------|
| Code changes | ~10 lines in lang_runtime.c | Wrapper closures at every call site |
| Interpreter impact | NONE (runtime is compiler-private) | N/A |
| Correctness | Fixes at source | Fragile: new C-calling patterns need wrappers |
| Performance | Negligible (TAG is `(n<<1)|1`) | Extra closure allocation per callback |
| Maintainability | Clear convention: C↔FunLang boundary uses tagged ints | Implicit knowledge scattered in compiler |

### Convention going forward

All C runtime functions that pass integer values to FunLang closures MUST tag them. All C runtime functions that store integers in data structures read by FunLang code MUST store them tagged.

Functions that receive tagged integers from the compiler (e.g., `hashtable_set` key) store them raw (the compiler already untags). When returning those values to FunLang, the C code must re-tag.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Wrapper closures in compiler | Custom closure-wrapping codegen per C function | TAG macro in C | Avoids heap allocation and complex codegen |
| Per-function retag logic in compiler | Custom retag after each C call returning tuples | Fix the C functions | C functions know their own semantics best |

## Common Pitfalls

### Pitfall 1: Forgetting to tag placeholder values
**What goes wrong:** `tup[1] = 0` for the "not found" case of trygetvalue stores raw 0, but FunLang reads it as tagged 0 (which is 1).
**How to avoid:** Always use `LANG_TAG_INT(0)` for any integer placeholder in C-constructed data.

### Pitfall 2: Double-tagging values that are already tagged
**What goes wrong:** `tup[1] = e->val` -- the val was stored tagged by the compiler. Tagging it again would corrupt it.
**How to avoid:** Only tag values that originate in C code (loop indices, boolean results, stored-raw keys). Values that pass through from FunLang (like hashtable values) are already tagged.

### Pitfall 3: Forgetting the `_str` variant
**What goes wrong:** Fix `lang_hashtable_trygetvalue` but not `lang_hashtable_trygetvalue_str`.
**How to avoid:** Always check and fix both int-key and string-key variants of hashtable functions.

### Pitfall 4: Other C functions that call closures
**What goes wrong:** Fixing the known 4 functions but missing others.
**Functions that call closures (audit):**
- `lang_array_init` -- NEEDS FIX (passes raw index)
- `lang_array_iter` -- OK (passes stored values, already tagged)
- `lang_for_in_list` -- OK (passes stored values from cons cells)
- `lang_for_in_array` -- OK (passes stored values from array slots)
- `lang_for_in_hashtable` -- NEEDS FIX (constructs tuple with raw key)
- `lang_for_in_hashset` -- NEEDS FIX (passes raw key)
- `lang_for_in_queue` -- NEEDS CHECK (are queue values stored tagged?)
- `lang_for_in_mlist` -- NEEDS CHECK (are mlist values stored tagged?)
- `lang_list_comp` -- same as for_in_list, OK
- `lang_array_map` -- passes stored values, OK
- `lang_array_fold` -- passes stored values, OK
- `lang_list_sort_by` -- passes stored values, OK

### Queue and MutableList audit

Queue `enqueue` and MutableList `add` -- need to check if the compiler untags before storing.

## Code Examples

### Macro definitions (add to top of lang_runtime.c)
```c
/* Phase 89: Tagged integer helpers for C↔FunLang boundary */
#define LANG_TAG_INT(n)    (((int64_t)(n) << 1) | 1)
#define LANG_UNTAG_INT(v)  ((int64_t)(v) >> 1)
```

### Fixed lang_array_init
```c
int64_t* lang_array_init(int64_t n, void* closure) {
    if (n < 0) { lang_failwith("array_init: negative length"); }
    LangClosureFn fn = *(LangClosureFn*)closure;
    int64_t* out = (int64_t*)GC_malloc((size_t)((n + 1) * 8));
    out[0] = n;
    for (int64_t i = 0; i < n; i++) {
        out[i + 1] = fn(closure, LANG_TAG_INT(i));
    }
    return out;
}
```

### Fixed lang_hashtable_trygetvalue
```c
int64_t* lang_hashtable_trygetvalue(LangHashtable* ht, int64_t key) {
    int64_t* tup = (int64_t*)GC_malloc(16);
    LangHashEntry* e = lang_ht_find(ht, key);
    if (e != NULL) {
        tup[0] = LANG_TAG_INT(1);   /* tagged true */
        tup[1] = e->val;            /* already tagged */
    } else {
        tup[0] = LANG_TAG_INT(0);   /* tagged false */
        tup[1] = LANG_TAG_INT(0);   /* tagged zero placeholder */
    }
    return tup;
}
```

### Fixed lang_for_in_hashtable
```c
void lang_for_in_hashtable(void* closure, LangHashtable* ht) {
    LangClosureFn fn = *(LangClosureFn*)closure;
    for (int64_t i = 0; i < ht->capacity; i++) {
        LangHashEntry* e = ht->buckets[i];
        while (e != NULL) {
            int64_t* tup = (int64_t*)GC_malloc(2 * sizeof(int64_t));
            tup[0] = LANG_TAG_INT(e->key);   /* retag stored-raw key */
            tup[1] = e->val;                  /* val already tagged */
            fn(closure, (int64_t)(uintptr_t)tup);
            e = e->next;
        }
    }
}
```

### Fixed lang_for_in_hashset
```c
void lang_for_in_hashset(void* closure, LangHashSet* hs) {
    LangClosureFn fn = *(LangClosureFn*)closure;
    for (int64_t i = 0; i < hs->capacity; i++) {
        LangHashSetEntry* e = hs->buckets[i];
        while (e != NULL) {
            fn(closure, LANG_TAG_INT(e->key));
            e = e->next;
        }
    }
}
```

## Scope of Changes

### Files to modify
1. `src/FunLangCompiler.Compiler/lang_runtime.c` -- add TAG macros, fix 4-5 functions

### Functions confirmed needing fix
| Function | Fix | Lines affected |
|----------|-----|---------------|
| `lang_array_init` | `LANG_TAG_INT(i)` in closure call | 1 line |
| `lang_for_in_hashtable` | `LANG_TAG_INT(e->key)` in tuple construction | 1 line |
| `lang_for_in_hashset` | `LANG_TAG_INT(e->key)` in closure call | 1 line |
| `lang_hashtable_trygetvalue` | Tag bool + placeholder in tuple | 3 lines |
| `lang_hashtable_trygetvalue_str` | Tag bool + placeholder in tuple | 3 lines |

### Functions needing audit
| Function | Question |
|----------|----------|
| `lang_for_in_queue` | Does `queue_enqueue` store tagged values? |
| `lang_for_in_mlist` | Does `mlist_add` store tagged values? |
| `lang_hashtable_containsKey` | Returns raw 0/1 -- compiler already converts to I1? |
| `lang_hashset_contains` | Returns raw 0/1 -- compiler already converts to I1? |
| `lang_hashset_add` | Returns raw 0/1 -- compiler already converts to I1? |
| `lang_file_exists` | Returns raw 0/1 -- compiler already converts to I1 (confirmed) |

### Test 66-06 uncertainty
The `file_exists` test failure may not be a C runtime issue at all. The compiler already converts the raw 0/1 return to I1 and then tags it. The bug might be in closure capture or branching logic. Recommend: fix the 5 confirmed C functions first, re-run all tests, then debug 66-06 separately if it still fails.

## Open Questions

1. **Queue/MutableList tagging:** Do `queue_enqueue` and `mlist_add` store values as-is (tagged) or untag first? If they store tagged values, `lang_for_in_queue` and `lang_for_in_mlist` need no fix. Need to verify in Elaboration.fs.

2. **Test 66-06 root cause:** May not be a C runtime issue. Needs separate investigation after the other 5 fixes land.

3. **Other return-bool C functions:** Functions like `lang_hashtable_containsKey`, `lang_hashset_contains`, `lang_hashset_add` return raw 0/1, but the compiler converts them to I1 inline. Are there any code paths where this raw value escapes without conversion?

## Sources

### Primary (HIGH confidence)
- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/lang_runtime.c` -- C runtime source, all function implementations
- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/lang_runtime.h` -- C runtime headers, closure typedef
- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/Elaboration.fs` -- compiler codegen, Phase 88 tag/untag
- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/ElabHelpers.fs` -- emitUntag, emitRetag, tagConst, coerceToI64

### Test files (HIGH confidence)
- `tests/compiler/24-04-array-init.flt`
- `tests/compiler/32-01-hashtable-trygetvalue.flt`
- `tests/compiler/34-05-forin-tuple-ht.flt`
- `tests/compiler/34-06-forin-hashset.flt`
- `tests/compiler/35-02-hashtable-module.flt`
- `tests/compiler/66-06-file-exists-coerce.flt` + `.sh`

## Metadata

**Confidence breakdown:**
- Root cause analysis: HIGH -- traced through C code and compiler codegen for each test
- Fix approach (C-side): HIGH -- C runtime is compiler-private, no interpreter impact
- Specific fixes for 5 functions: HIGH -- exact lines and values identified
- Test 66-06 root cause: LOW -- may not be C runtime related
- Queue/MutableList audit: MEDIUM -- likely OK but needs verification

**Research date:** 2026-04-07
**Valid until:** until Phase 88 tagging scheme changes
