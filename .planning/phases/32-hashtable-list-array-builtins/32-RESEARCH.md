# Phase 32: Hashtable & List/Array Builtins - Research

**Researched:** 2026-03-29
**Domain:** LangBackend builtin implementation (lang_runtime.c + Elaboration.fs)
**Confidence:** HIGH

## Summary

Phase 32 adds 6 new builtins: HT-01 (`hashtable_trygetvalue`), HT-02 (`hashtable_count`), LA-01 (`list_sort_by`), LA-02 (`list_of_seq`), LA-03 (`array_sort`), LA-04 (`array_of_seq`). Each follows the established three-layer pattern: C runtime function in `lang_runtime.c`, elaboration pattern match in `Elaboration.fs`, and external function declarations in both `externalFuncs` lists (lines 2796 and 2996).

The LangThree interpreter in `../LangThree/src/LangThree/Eval.fs` confirms the exact semantics. The most complex builtin is `hashtable_trygetvalue` which must return a heap-allocated tuple `(bool, value)`. The second-most complex is `list_sort_by` which requires merge sort in C (key extractor closure, no `qsort` because `qsort`'s callback lacks a user-data parameter). The hashtable struct uses field `size` (not `count`) at index 2, so `hashtable_count` can be implemented as a GEP field-load without a C function. For `list_of_seq` and `array_of_seq`, Phase 32 tests should only exercise list-input cases to avoid the runtime ambiguity between list and array pointers.

**Primary recommendation:** Implement `hashtable_trygetvalue` as a C function returning a Ptr to a 16-byte GC_malloc tuple; implement `hashtable_count` as inline GEP+load at offset 2; implement `list_sort_by` as a C merge-sort with LangClosureFn key extractor; implement `array_sort` as qsort on `&arr[1]`; implement `list_of_seq` and `array_of_seq` as simple C pass-throughs that only handle the list-input case in Phase 32 tests.

## Standard Stack

The established tools for this codebase:

### Core
| Component | Location | Purpose | Why Standard |
|-----------|----------|---------|--------------|
| lang_runtime.c | src/LangBackend.Compiler/lang_runtime.c | C runtime library | All builtins that require C stdlib or struct access live here |
| lang_runtime.h | src/LangBackend.Compiler/lang_runtime.h | Header declarations | Declares all runtime function signatures and structs |
| Elaboration.fs | src/LangBackend.Compiler/Elaboration.fs | AST-to-MLIR translation | Pattern-matches on `App(Var("builtin_name"))`, emits MLIR ops |

### Supporting
| Component | Purpose | When to Use |
|-----------|---------|-------------|
| GC_malloc | Heap allocation | All struct allocations in runtime.c (tuples, cons cells, arrays) |
| LlvmCallOp | Emit C call returning a value | All value-returning builtins |
| LlvmCallVoidOp | Emit C call with void/unit return | `array_sort` (returns unit) |
| LlvmGEPLinearOp + LlvmLoadOp | Inline field access | `hashtable_count` field load at index 2 |
| LangClosureFn | `int64_t (*)(void* env, int64_t arg)` | Closure ABI for `list_sort_by` key extractor |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| C function for `hashtable_count` | Inline GEP+load | GEP+load at field index 2 is already the `array_length` pattern and avoids a C call |
| Merge sort for `list_sort_by` | `qsort_r` (Linux/macOS `qsort` with user data) | `qsort_r` has different signatures on macOS vs Linux; merge sort is portable and clean |
| C function for `list_of_seq` identity | Inline identity in elaboration | A C function call for identity is wasteful; pure inline is better, but we can't distinguish list from array at elaboration time without type info |

**Installation:** No new packages. Uses existing `<stdlib.h>` for `qsort`.

## Architecture Patterns

### LangHashtable struct layout (existing)
```
typedef struct {
    int64_t tag;        // offset 0 — always -1 for hashtables
    int64_t capacity;   // offset 1
    int64_t size;       // offset 2  ← this is "count" (not a field named count)
    LangHashEntry** buckets;  // offset 3 (pointer)
} LangHashtable;
```
`hashtable_count` reads `size` at **GEP linear index 2**.

### LangCons struct layout (existing)
```
typedef struct LangCons {
    int64_t         head;   // element value (or pointer-as-int64)
    struct LangCons* tail;  // next cell or NULL
} LangCons;
```

### Array layout (existing)
```
int64_t arr[n+1]:
  arr[0] = n (length)
  arr[1..n] = elements
```

### Pattern 1: hashtable_trygetvalue — C function returning Ptr (tuple)
**What:** Two-arg curried builtin: `hashtable_trygetvalue ht key` returns a heap-allocated 16-byte tuple `(bool_as_i64, value_as_i64)`.
**Why Ptr not I1:** Tuple slots are always 8 bytes. The bool is stored as int64_t (0 or 1) at slot 0, value at slot 1. The elaboration returns Ptr (tuple pointer).
**Example:**
```c
// Source: lang_runtime.c (new)
int64_t* lang_hashtable_trygetvalue(LangHashtable* ht, int64_t key) {
    int64_t* tup = (int64_t*)GC_malloc(16);  /* 2 slots × 8 bytes */
    LangHashEntry* e = lang_ht_find(ht, key);
    if (e != NULL) {
        tup[0] = 1;       /* true */
        tup[1] = e->val;
    } else {
        tup[0] = 0;       /* false */
        tup[1] = 0;       /* unit/0 placeholder */
    }
    return tup;
}
```
Note: `lang_ht_find` is declared `static` in lang_runtime.c. The new function must be placed after `lang_ht_find` or the static declaration made available (use `lang_hashtable_containsKey` + `lang_hashtable_get` pair as fallback, or declare `lang_ht_find` before use).

```fsharp
// Source: Elaboration.fs (new) — matches: App(App(Var("hashtable_trygetvalue"), ht), key)
| App (App (Var ("hashtable_trygetvalue", _), htExpr, _), keyExpr, _) ->
    let (htVal,  htOps)  = elaborateExpr env htExpr
    let (keyVal, keyOps) = elaborateExpr env keyExpr
    let keyI64 =
        if keyVal.Type = I64 then (keyVal, [])
        else let v = { Name = freshName env; Type = I64 } in (v, [ArithExtuIOp(v, keyVal)])
    let (kv, kCoerce) = keyI64
    let result = { Name = freshName env; Type = Ptr }
    (result, htOps @ keyOps @ kCoerce @ [LlvmCallOp(result, "@lang_hashtable_trygetvalue", [htVal; kv])])
```

```fsharp
// externalFuncs entry (both lists):
{ ExtName = "@lang_hashtable_trygetvalue"; ExtParams = [Ptr; I64]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
```

**Returned value usage:** The tuple (Ptr) can be destructured with `let (found, v) = hashtable_trygetvalue ht k`. This uses the existing `LetPat(TuplePat)` arm in elaboration which GEPs slot 0 and slot 1. Slot 0 is loaded as I64 (found = 0 or 1). For use in `if`, the loaded I64 needs `ArithCmpIOp` wrapping — but that happens automatically when the value is used in a boolean context via the existing `if` elaboration arm.

### Pattern 2: hashtable_count — inline GEP+load at field index 2
**What:** One-arg builtin, reads `size` field (index 2) from LangHashtable struct without a C call.
**When to use:** When accessing a known integer field of a heap-allocated struct at a fixed offset.
**Example:**
```fsharp
// Source: Elaboration.fs (new) — mirrors array_length exactly, but GEP index 2
| App (Var ("hashtable_count", _), htExpr, _) ->
    let (htVal, htOps) = elaborateExpr env htExpr
    let sizePtr = { Name = freshName env; Type = Ptr }
    let result  = { Name = freshName env; Type = I64 }
    let ops = [
        LlvmGEPLinearOp(sizePtr, htVal, 2)   // field index 2 = size
        LlvmLoadOp(result, sizePtr)
    ]
    (result, htOps @ ops)
```
No C function needed. No externalFuncs entry needed.

### Pattern 3: list_sort_by — two-arg with closure, C merge sort
**What:** `list_sort_by keyFn list` — key extractor closure applied to each element, sorts ascending.
**Calling convention:** Same closure ABI as `array_map`: `fn(closure_ptr, element)` returns key.
**Sort algorithm:** Merge sort in C operating on LangCons* directly (no intermediate array needed, but an array-conversion approach is also viable and simpler).
**Recommended implementation (array-based approach for simplicity):**
```c
// Source: lang_runtime.c (new)
LangCons* lang_list_sort_by(void* closure, LangCons* list) {
    /* Count elements */
    int64_t n = 0;
    LangCons* cur = list;
    while (cur != NULL) { n++; cur = cur->tail; }
    if (n <= 1) return list;

    /* Build parallel arrays: elements and keys */
    int64_t* elems = (int64_t*)GC_malloc((size_t)(n * 8));
    int64_t* keys  = (int64_t*)GC_malloc((size_t)(n * 8));
    LangClosureFn fn = *(LangClosureFn*)closure;
    cur = list;
    for (int64_t i = 0; i < n; i++) {
        elems[i] = cur->head;
        keys[i]  = fn(closure, cur->head);
        cur = cur->tail;
    }

    /* Insertion sort (simple; merge sort if n is large) */
    for (int64_t i = 1; i < n; i++) {
        int64_t ke = keys[i], ve = elems[i];
        int64_t j = i - 1;
        while (j >= 0 && keys[j] > ke) {
            keys[j+1]  = keys[j];
            elems[j+1] = elems[j];
            j--;
        }
        keys[j+1]  = ke;
        elems[j+1] = ve;
    }

    /* Reconstruct list */
    LangCons* head = NULL;
    for (int64_t i = n - 1; i >= 0; i--) {
        LangCons* cell = (LangCons*)GC_malloc(sizeof(LangCons));
        cell->head = elems[i];
        cell->tail = head;
        head = cell;
    }
    return head;
}
```
Note: Insertion sort is O(n²) but correct and simple. For Phase 32 test cases, n is small. Use merge sort if performance matters (the feature request mentions n < 1000 in practice).

```fsharp
// Source: Elaboration.fs (new) — mirrors array_map closure coercion pattern
| App (App (Var ("list_sort_by", _), closureExpr, _), listExpr, _) ->
    let (fVal,    fOps)    = elaborateExpr env closureExpr
    let (listVal, listOps) = elaborateExpr env listExpr
    let closurePtrVal =
        if fVal.Type = I64 then { Name = freshName env; Type = Ptr }
        else fVal
    let closureOps =
        if fVal.Type = I64 then [LlvmIntToPtrOp(closurePtrVal, fVal)]
        else []
    let result = { Name = freshName env; Type = Ptr }
    (result, fOps @ closureOps @ listOps @ [LlvmCallOp(result, "@lang_list_sort_by", [closurePtrVal; listVal])])
```

```fsharp
// externalFuncs entry (both lists):
{ ExtName = "@lang_list_sort_by"; ExtParams = [Ptr; Ptr]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
```

### Pattern 4: array_sort — void C call, unit return
**What:** In-place sort of int64_t array using qsort. Returns unit (I64 = 0).
**Example:**
```c
// Source: lang_runtime.c (new)
static int lang_compare_i64(const void* a, const void* b) {
    int64_t x = *(const int64_t*)a;
    int64_t y = *(const int64_t*)b;
    if (x < y) return -1;
    if (x > y) return  1;
    return 0;
}

void lang_array_sort(int64_t* arr) {
    int64_t n = arr[0];
    if (n <= 1) return;
    qsort(&arr[1], (size_t)n, sizeof(int64_t), lang_compare_i64);
}
```

```fsharp
// Source: Elaboration.fs (new) — mirrors array_iter (void return → unit)
| App (Var ("array_sort", _), arrExpr, _) ->
    let (arrVal, arrOps) = elaborateExpr env arrExpr
    let unitVal = { Name = freshName env; Type = I64 }
    (unitVal, arrOps @ [LlvmCallVoidOp("@lang_array_sort", [arrVal]); ArithConstantOp(unitVal, 0L)])
```

```fsharp
// externalFuncs entry (both lists):
{ ExtName = "@lang_array_sort"; ExtParams = [Ptr]; ExtReturn = None; IsVarArg = false; Attrs = [] }
```

### Pattern 5: list_of_seq — C function, list-input-only in Phase 32
**What:** `list_of_seq xs` — converts a collection to a list. Phase 32 tests only need list→list (identity).
**Limitation:** Cannot distinguish list pointer from array pointer at runtime reliably (both are untagged pointers; first word of a list cell is an arbitrary int64). Phase 32 tests must NOT call `list_of_seq` on an array argument.
**Implementation:**
```c
// Source: lang_runtime.c (new)
// In Phase 32: only list-input is tested. Identity for lists (return as-is).
// A later phase can add array/HashSet dispatch when those have type tags.
LangCons* lang_list_of_seq(void* collection) {
    /* Phase 32: identity for LangCons* inputs.
     * The pointer is returned unchanged. Array inputs are not supported
     * in Phase 32 (no runtime tag to distinguish list from array). */
    return (LangCons*)collection;
}
```

```fsharp
// Source: Elaboration.fs (new)
| App (Var ("list_of_seq", _), seqExpr, _) ->
    let (seqVal, seqOps) = elaborateExpr env seqExpr
    let result = { Name = freshName env; Type = Ptr }
    (result, seqOps @ [LlvmCallOp(result, "@lang_list_of_seq", [seqVal])])
```

```fsharp
// externalFuncs entry (both lists):
{ ExtName = "@lang_list_of_seq"; ExtParams = [Ptr]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
```

### Pattern 6: array_of_seq — C function, list-input-only in Phase 32
**What:** `array_of_seq xs` — converts a collection to an array. Phase 32 tests only need list→array.
**Implementation:** Delegate to the existing `lang_array_of_list`.
```c
// Source: lang_runtime.c (new)
int64_t* lang_array_of_seq(void* collection) {
    /* Phase 32: treat input as LangCons* (list).
     * Array and HashSet inputs not supported until those have runtime tags. */
    return lang_array_of_list((LangCons*)collection);
}
```

```fsharp
// Source: Elaboration.fs (new)
| App (Var ("array_of_seq", _), seqExpr, _) ->
    let (seqVal, seqOps) = elaborateExpr env seqExpr
    let result = { Name = freshName env; Type = Ptr }
    (result, seqOps @ [LlvmCallOp(result, "@lang_array_of_seq", [seqVal])])
```

```fsharp
// externalFuncs entry (both lists):
{ ExtName = "@lang_array_of_seq"; ExtParams = [Ptr]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
```

### Anti-Patterns to Avoid
- **Forgetting the duplicate externalFuncs:** The externalFuncs list appears TWICE (line 2796 and line 2996). Both must be updated with new entries.
- **Using `lang_ht_find` from a new C function:** `lang_ht_find` is declared `static` in lang_runtime.c. Either place `lang_hashtable_trygetvalue` after `lang_ht_find` in the file, or use the combination of `lang_hashtable_containsKey` + `lang_hashtable_get` (two calls, slightly less efficient but no static visibility issue). Direct placement after `lang_ht_find` is cleaner.
- **Storing I1 bool directly in a tuple slot:** Tuple slots are 8-byte word-sized. The `lang_hashtable_trygetvalue` C function stores `int64_t` 0 or 1 in slot 0 (not I1). The tuple pointer returned as Ptr is loaded as I64 when destructured. Do NOT apply ArithCmpIOp wrapping to the returned tuple — only apply it if the user does `if found then ...` where the loaded I64 slot is used as a bool.
- **Passing list_of_seq an array argument in Phase 32 tests:** The runtime cannot distinguish list from array pointers. Phase 32 E2E tests must only call `list_of_seq` with list arguments.
- **Using qsort for list_sort_by:** Standard `qsort` takes no user-data parameter. Use `qsort_r` (macOS/Linux but different signatures) or implement merge/insertion sort directly.
- **Confusing LangHashtable field names:** The header declares `size` (not `count`) at index 2. The context description mentions `ht->count` which is wrong — the field is `ht->size`.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Hashtable key lookup | Duplicate hash table traversal logic | `lang_ht_find` (already exists, static) | Already correct and handles all hash collision cases |
| Array element sort | Custom sort in Elaboration.fs | `qsort` + `lang_compare_i64` wrapper in C | `qsort` is libc, already linked; simpler and correct |
| List cons cell allocation | Custom allocator | `GC_malloc(sizeof(LangCons))` | Same pattern as `lang_array_to_list`, GC-tracked |
| Array-to-list conversion | Re-implement in new C functions | `lang_array_of_list` / `lang_array_to_list` (already exist) | Phase 22 already has both; `array_of_seq` should delegate |

**Key insight:** `lang_ht_find` is the internal lookup workhorse; `lang_hashtable_trygetvalue` can be placed directly after it in lang_runtime.c to use it. The only new algorithm in Phase 32 is the list sort.

## Common Pitfalls

### Pitfall 1: externalFuncs list duplicated — both must be updated
**What goes wrong:** Adding a new C function to only one of the two `externalFuncs` lists causes "undefined external function" errors.
**Why it happens:** Two elaboration overloads exist (expression-only programs vs. full declaration programs), each building their own MLIR module with its own externalFuncs list.
**How to avoid:** Update the list at line 2796 AND the identical list at line 2996 for every new function.
**Warning signs:** Test passes for expression-only `.flt` tests but fails for tests with `let` declarations at top level.

### Pitfall 2: lang_ht_find visibility — static function
**What goes wrong:** `lang_ht_find` is declared `static` in lang_runtime.c. A new function `lang_hashtable_trygetvalue` placed before `lang_ht_find` cannot call it.
**Why it happens:** C static functions are only visible after their definition in the translation unit.
**How to avoid:** Place `lang_hashtable_trygetvalue` definition AFTER `lang_ht_find` in lang_runtime.c (around line 312). Or: use the two-call approach `containsKey` then `get`, which avoids static visibility.
**Warning signs:** C compilation error: `lang_ht_find` undeclared.

### Pitfall 3: hashtable_count field index — use 2, not 1
**What goes wrong:** LangHashtable has fields `tag` (index 0), `capacity` (index 1), `size` (index 2). Using GEP index 1 returns `capacity`, not the element count.
**Why it happens:** The field named `size` in the runtime is described as `count` in the feature context. Off-by-one in GEP index.
**How to avoid:** Confirm: `tag=0, capacity=1, size=2`. Use `LlvmGEPLinearOp(sizePtr, htVal, 2)`.
**Warning signs:** `hashtable_count ht` returns a large number (capacity 16+) instead of the number of entries.

### Pitfall 4: list_sort_by closure — key extractor, not comparator
**What goes wrong:** `list_sort_by` takes a KEY EXTRACTOR `'a -> 'b`, not a comparator `'a -> 'a -> int`. The sort is ascending by extracted key (int64_t comparison).
**Why it happens:** The LangThree reference shows `List.sortWith` uses `valueCompare k1 k2` after extracting keys. The key extractor is called once per element, not once per comparison pair.
**How to avoid:** In `lang_list_sort_by`, call `fn(closure, elem)` for each element to get its key. Then sort elements by their keys. Do NOT call the closure with two arguments (it's a single-arg closure).
**Warning signs:** Wrong sort order; or crash if the closure is called with two elements directly.

### Pitfall 5: Tuple slot type for hashtable_trygetvalue result
**What goes wrong:** The returned tuple pointer holds `int64_t` values (0/1 for bool, int64 for value). When the user writes `let (found, v) = hashtable_trygetvalue ht k`, the LetPat(TuplePat) elaboration loads slots as I64. If the `found` slot is used in an `if`, the I64 value must be wrapped by `ArithCmpIOp` to become I1. This wrapping is applied automatically by the `if`-expression arm — but only if the value is used directly in a condition. If the user writes `if found then ...`, the loaded I64 `found` will be compared with `ne 0` in the `if` arm.
**Why it happens:** The LetPat(TuplePat) arm loads fields as I64 (for VarPat). The `if` condition elaboration wraps I64 with ArithCmpIOp when needed.
**How to avoid:** Check the existing LetPat TuplePat arm (lines 601-631) — it loads VarPat fields as I64. The `if` elaboration handles I64→I1 conversion. No special handling needed in the `hashtable_trygetvalue` arm itself.
**Warning signs:** Type error in MLIR lowering if the bool field is used where I1 is expected without the intermediate wrapping.

### Pitfall 6: array_sort is void — use LlvmCallVoidOp
**What goes wrong:** `lang_array_sort` returns void. Using `LlvmCallOp` (which expects a return value) instead of `LlvmCallVoidOp` causes MLIR printing errors.
**Why it happens:** `array_sort` modifies the array in-place; there is no meaningful return value. It should return unit (I64 = 0) in the language.
**How to avoid:** Follow the `array_iter` pattern: `LlvmCallVoidOp("@lang_array_sort", [arrVal])` + `ArithConstantOp(unitVal, 0L)` and return `unitVal`.
**Warning signs:** MLIR error about void value being assigned to a register.

## Code Examples

### hashtable_trygetvalue usage pattern
```
// --- Input:
let ht = hashtable_create () in
let _ = hashtable_set ht 42 100 in
let (found, v) = hashtable_trygetvalue ht 42 in
if found then v else -1
// --- Output:
100
0
```

### hashtable_count usage pattern
```
// --- Input:
let ht = hashtable_create () in
let _ = hashtable_set ht 1 10 in
let _ = hashtable_set ht 2 20 in
hashtable_count ht
// --- Output:
2
0
```

### list_sort_by usage pattern
```
// --- Input:
let xs = 3 :: 1 :: 2 :: [] in
let sorted = list_sort_by (fun x -> x) xs in
match sorted with | a :: b :: c :: [] -> a + b * 10 + c * 100 | _ -> 0
// --- Output:
123
0
```

### array_sort usage pattern
```
// --- Input:
let arr = array_of_list (3 :: 1 :: 2 :: []) in
let _ = array_sort arr in
array_get arr 0
// --- Output:
1
0
```

### array_of_seq usage pattern (list input only in Phase 32)
```
// --- Input:
let xs = 1 :: 2 :: 3 :: [] in
let arr = array_of_seq xs in
array_length arr
// --- Output:
3
0
```

### list_of_seq usage pattern (list input only in Phase 32)
```
// --- Input:
let xs = 10 :: 20 :: [] in
let ys = list_of_seq xs in
match ys with | a :: b :: [] -> a + b | _ -> 0
// --- Output:
30
0
```

### Existing reference: array_length (model for hashtable_count)
```fsharp
// Source: Elaboration.fs lines 954-962
| App (Var ("array_length", _), arrExpr, _) ->
    let (arrVal, arrOps) = elaborateExpr env arrExpr
    let lenPtr = { Name = freshName env; Type = Ptr }
    let result = { Name = freshName env; Type = I64 }
    let ops = [
        LlvmGEPLinearOp(lenPtr, arrVal, 0)
        LlvmLoadOp(result, lenPtr)
    ]
    (result, arrOps @ ops)
```

### Existing reference: array_map (model for list_sort_by closure coercion)
```fsharp
// Source: Elaboration.fs lines 1133-1145
| App (App (Var ("array_map", _), closureExpr, _), arrExpr, _) ->
    let (fVal,   fOps)   = elaborateExpr env closureExpr
    let (arrVal, arrOps) = elaborateExpr env arrExpr
    let closurePtrVal =
        if fVal.Type = I64
        then { Name = freshName env; Type = Ptr }
        else fVal
    let closureOps =
        if fVal.Type = I64
        then [LlvmIntToPtrOp(closurePtrVal, fVal)]
        else []
    let result = { Name = freshName env; Type = Ptr }
    (result, fOps @ closureOps @ arrOps @ [LlvmCallOp(result, "@lang_array_map", [closurePtrVal; arrVal])])
```

### Existing reference: array_iter (model for array_sort void return)
```fsharp
// Source: Elaboration.fs lines 1117-1129
| App (App (Var ("array_iter", _), closureExpr, _), arrExpr, _) ->
    ...
    let unitVal = { Name = freshName env; Type = I64 }
    (unitVal, fOps @ closureOps @ arrOps @ [LlvmCallVoidOp("@lang_array_iter", [closurePtrVal; arrVal]); ArithConstantOp(unitVal, 0L)])
```

### Existing reference: Tuple construction (model for understanding trygetvalue result)
```fsharp
// Source: Elaboration.fs lines 1513-1538
// GC_malloc(n * 8), then GEP+Store each slot. The C runtime for trygetvalue
// does the equivalent: GC_malloc(16), arr[0] = found_flag, arr[1] = value.
// The F# user destructures with: let (found, v) = hashtable_trygetvalue ht k
// which invokes LetPat(TuplePat) → GEP slot 0 loaded as I64, GEP slot 1 loaded as I64.
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| No hashtable_trygetvalue | C function returns tuple Ptr | Phase 32 | Enables safe optional lookup without exceptions |
| `hashtable_get` throws on missing key | `hashtable_trygetvalue` returns (false, 0) | Phase 32 | Cleaner error handling |
| No list sorting | `list_sort_by` with key extractor | Phase 32 | Needed for DfaMin.fun |
| No array in-place sort | `array_sort` using qsort | Phase 32 | Needed for Dfa.fun |
| No list_of_seq / array_of_seq | Phase 32 identity/conversion functions | Phase 32 | Partial seq API; full version requires type-tagged collections |

**Deprecated/outdated:**
- Nothing deprecated in Phase 32. All new additions are additive.

## Open Questions

1. **lang_ht_find visibility for hashtable_trygetvalue**
   - What we know: `lang_ht_find` is declared `static` at line 313 in lang_runtime.c. Placing `lang_hashtable_trygetvalue` after line 313 resolves visibility.
   - What's unclear: Whether the plan should specify "insert after lang_ht_find" or use the two-call fallback (containsKey + get) which avoids the static issue.
   - Recommendation: Insert `lang_hashtable_trygetvalue` immediately after `lang_ht_find` (around line 321, before `lang_hashtable_create`). This is clean and uses the existing static lookup.

2. **list_of_seq and array_of_seq — future-proofing for MutableList/HashSet**
   - What we know: Phase 32 only tests list inputs. Future phases add HashSet, Queue, MutableList — those will need runtime dispatch.
   - What's unclear: Should the Phase 32 implementation use a `NULL` check and `first_word` check, or just be a pure pass-through?
   - Recommendation: Phase 32 implements as pure pass-through (list_of_seq = identity, array_of_seq = array_of_list). Comment clearly that future phases need runtime dispatch. Tests must document the list-input-only constraint.

3. **list_sort_by sort stability**
   - What we know: LangThree uses `List.sortWith` which is stable in .NET.
   - What's unclear: Whether the insertion-sort-based C implementation needs to be stable (equal-key elements maintain original order).
   - Recommendation: Insertion sort is inherently stable (equal elements are never swapped). So stability is maintained naturally. No special handling needed.

## Sources

### Primary (HIGH confidence)
- `src/LangBackend.Compiler/lang_runtime.c` — all existing runtime patterns (hashtable CRUD, array layout, LangCons, array_of_list, array_to_list, array_map closure ABI). Lines 134-535 examined in detail.
- `src/LangBackend.Compiler/lang_runtime.h` — LangHashtable struct layout confirmed (tag=0, capacity=1, size=2), LangClosureFn typedef
- `src/LangBackend.Compiler/Elaboration.fs` — array_length (line 954), array_map closure pattern (line 1133), array_iter void pattern (line 1117), Tuple construction (line 1513), LetPat TuplePat (line 601), externalFuncs both lists (lines 2796 and 2996)
- `../LangThree/src/LangThree/Eval.fs` — reference implementations confirmed: hashtable_trygetvalue returns TupleValue [BoolValue; val], list_sort_by uses key extractor (not comparator), array_sort is in-place
- `../LangThree/src/LangThree/Ast.fs` — valueCompare is integer comparison for int values

### Secondary (MEDIUM confidence)
- `langbackend-feature-requests.md` — C function signatures for lang_array_sort (`qsort(&arr[1], n, 8, compare_i64)`) and lang_list_sort_by
- `tests/compiler/` — existing test file format and naming conventions (23-xx for hashtable, 22-xx for array)

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all files examined directly
- Architecture: HIGH — patterns derived from working existing builtins in the same codebase; all struct layouts confirmed
- Pitfalls: HIGH — static visibility issue confirmed by reading lang_runtime.c; duplicate externalFuncs confirmed by grep; field index verified from struct definition

**Research date:** 2026-03-29
**Valid until:** 2026-04-29 (stable codebase)
