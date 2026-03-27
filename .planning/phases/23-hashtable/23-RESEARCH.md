# Phase 23: Hashtable - Research

**Researched:** 2026-03-27
**Domain:** C runtime hash table implementation + MLIR elaboration for hashtable builtins
**Confidence:** HIGH

## Summary

Phase 23 implements eight hashtable builtins (HT-01 through HT-08) entirely through C runtime delegation. Every hashtable operation (create, get, set, containsKey, keys, remove, plus the value-hashing infrastructure) is routed through `lang_runtime.c` functions. The MLIR side sees only opaque `!llvm.ptr` — no new `MlirOp` DU cases are needed. This is the same pattern used for `lang_range`, `lang_string_concat`, and the array builtins that delegate to C (e.g., `lang_array_create`).

The C runtime implements a chained-bucket hash table using `GC_malloc` throughout. Boehm GC traces the chain of `LangHashEntry*` pointers conservatively, ensuring live keys and values are never prematurely collected. Keys are passed as `i64` across the C ABI — both plain integer keys and pointer keys (strings) coerced via `ptrtoint`. String key equality requires `strcmp` through the `LangString` struct; integer key equality is a plain `int64_t` compare. The `key_is_ptr` flag in each entry determines which comparison to use at lookup time.

Six elaboration arms are added to `Elaboration.fs` matching the same App-chain pattern as the array builtins. One helper function `coerceToI64` (extract from existing `LlvmPtrToIntOp` usage) handles the Ptr→I64 coercion at the call site. Six `ExternalFuncDecl` entries are added to both `externalFuncs` lists. No changes to `MlirIR.fs`, `Printer.fs`, `Pipeline.fs`, or `MatchCompiler.fs`.

**Primary recommendation:** Implement C runtime structs + functions first (validates the data structure independently), then add elaboration arms and ExternalFuncDecl entries, then run E2E tests.

## Standard Stack

Phase 23 introduces no new external libraries. All components already exist in the project.

### Core (already present, extended in this phase)

| Component | Location | Phase 23 Change |
|-----------|----------|-----------------|
| `lang_runtime.c` | `src/LangBackend.Compiler/` | Add `LangHashEntry`, `LangHashtable` structs + 6 functions (~120 LOC) |
| `lang_runtime.h` | `src/LangBackend.Compiler/` | Add struct typedefs + 6 function declarations (~20 LOC) |
| `Elaboration.fs` | `src/LangBackend.Compiler/` | Add 6 builtin match arms + `coerceToI64` helper + 6 ExternalFuncDecl entries |

### No Changes Required

| Component | Reason |
|-----------|--------|
| `MlirIR.fs` | All ops already exist: `LlvmCallOp`, `LlvmCallVoidOp`, `ArithConstantOp`, `ArithCmpIOp`, `LlvmPtrToIntOp` |
| `Printer.fs` | No new MlirOp cases |
| `Pipeline.fs` | `lang_runtime.c` is compiled and linked in Step 4 without changes |
| `MatchCompiler.fs` | Hashtable builtins are `App` chains, not match patterns |

**Installation:** No new packages. Build with existing `dotnet build`.

## Architecture Patterns

### Recommended Implementation Order

```
1. lang_runtime.h: Add LangHashEntry + LangHashtable typedefs
2. lang_runtime.c: lang_hashtable_create (validates struct layout)
3. lang_runtime.c: lang_hashtable_set + key equality + hashing (dependency for all lookups)
4. lang_runtime.c: lang_hashtable_get (depends on set for tests)
5. lang_runtime.c: lang_hashtable_containsKey (same lookup path as get)
6. lang_runtime.c: lang_hashtable_remove (chain relinking)
7. lang_runtime.c: lang_hashtable_keys (returns cons list of keys)
8. Elaboration.fs: coerceToI64 helper (extract from existing PtrToInt usage)
9. Elaboration.fs: ExternalFuncDecl registrations (both lists)
10. Elaboration.fs: hashtable_create elaboration arm
11. Elaboration.fs: hashtable_set (3-arg — must come before 2-arg and 1-arg arms)
12. Elaboration.fs: hashtable_get (2-arg)
13. Elaboration.fs: hashtable_containsKey (2-arg)
14. Elaboration.fs: hashtable_remove (2-arg)
15. Elaboration.fs: hashtable_keys (1-arg)
16. E2E tests: 23-01 through 23-08
```

### Pattern 1: C Runtime Struct Layout

**What:** The hashtable is a chained-bucket table with GC_malloc'd nodes.

```c
// lang_runtime.h additions

typedef struct LangHashEntry {
    int64_t              key;        /* i64 value of key (raw i64 or ptr-as-i64) */
    int64_t              key_is_ptr; /* 1 if key is a LangString* pointer, 0 if plain i64 */
    int64_t              val;        /* value (i64 scalar or ptr-as-i64) */
    struct LangHashEntry* next;      /* next entry in chain (NULL = end of chain) */
} LangHashEntry;

typedef struct {
    int64_t        capacity;  /* number of buckets */
    int64_t        size;      /* number of key-value pairs */
    LangHashEntry** buckets;  /* GC_malloc'd array of bucket head pointers */
} LangHashtable;

LangHashtable* lang_hashtable_create(void);
int64_t        lang_hashtable_get(LangHashtable* ht, int64_t key, int64_t key_is_ptr);
void           lang_hashtable_set(LangHashtable* ht, int64_t key, int64_t key_is_ptr, int64_t val);
int64_t        lang_hashtable_containsKey(LangHashtable* ht, int64_t key, int64_t key_is_ptr);
void           lang_hashtable_remove(LangHashtable* ht, int64_t key, int64_t key_is_ptr);
LangCons*      lang_hashtable_keys(LangHashtable* ht);
```

**IMPORTANT — ABI decision:** The MLIR-side elaboration arms must decide at compile time whether a key is a string pointer or a plain i64. Two options:

**Option A (simpler): i64-only keys (no string key equality)**
All keys are treated as raw `i64` values. String keys compare by pointer identity (same object = same key). The ABI is `(Ptr ht, I64 key) -> ...`. No `key_is_ptr` flag needed.

**Option B (richer): key type tag passed as extra i64**
The elaboration arm inspects the key expression's elaborated type. If `Ptr`, it passes `key_is_ptr=1`; if `I64`, passes `key_is_ptr=0`. The C runtime then does `strcmp` vs int64 compare accordingly.

**Recommendation: Use Option A (i64-only equality) for Phase 23.** The phase requirements (HT-01 through HT-08) do not specify string key equality by content. The LangThree evaluator uses `.NET Dictionary<Value, Value>` which has value-level equality, but Phase 23 E2E tests will likely use integer keys for simplicity. Document the string-key limitation. This keeps the ABI clean: `(Ptr, I64) -> ...` matches the existing pattern in STACK.md and avoids extra ABI args.

### Pattern 2: C Runtime Implementation

```c
// lang_runtime.c additions (with Option A: i64 key equality)

#define LANG_HT_INITIAL_CAPACITY 16
#define LANG_HT_LOAD_FACTOR_NUM  3
#define LANG_HT_LOAD_FACTOR_DEN  4

static uint64_t lang_ht_hash(int64_t key) {
    /* FNV-1a-inspired 64-bit integer mix */
    uint64_t h = (uint64_t)key;
    h ^= h >> 33;
    h *= 0xff51afd7ed558ccdULL;
    h ^= h >> 33;
    h *= 0xc4ceb9fe1a85ec53ULL;
    h ^= h >> 33;
    return h;
}

LangHashtable* lang_hashtable_create(void) {
    LangHashtable* ht = (LangHashtable*)GC_malloc(sizeof(LangHashtable));
    ht->capacity = LANG_HT_INITIAL_CAPACITY;
    ht->size     = 0;
    ht->buckets  = (LangHashEntry**)GC_malloc(
                       (size_t)(LANG_HT_INITIAL_CAPACITY * (int64_t)sizeof(LangHashEntry*)));
    /* GC_malloc zero-initialises — all bucket heads start NULL */
    return ht;
}

/* Lookup helper: returns entry pointer if found, NULL if not. */
static LangHashEntry* lang_ht_find(LangHashtable* ht, int64_t key) {
    uint64_t h   = lang_ht_hash(key) % (uint64_t)ht->capacity;
    LangHashEntry* e = ht->buckets[h];
    while (e != NULL) {
        if (e->key == key) return e;
        e = e->next;
    }
    return NULL;
}

int64_t lang_hashtable_get(LangHashtable* ht, int64_t key) {
    LangHashEntry* e = lang_ht_find(ht, key);
    if (e == NULL) {
        /* Raise exception: key not found */
        int msglen = snprintf(NULL, 0, "Hashtable.get: key not found");
        char* buf  = (char*)GC_malloc((size_t)(msglen + 1));
        snprintf(buf, (size_t)(msglen + 1), "Hashtable.get: key not found");
        LangString* msg = (LangString*)GC_malloc(sizeof(LangString));
        msg->length = (int64_t)msglen;
        msg->data   = buf;
        lang_throw((void*)msg);
        return 0; /* unreachable */
    }
    return e->val;
}

void lang_hashtable_set(LangHashtable* ht, int64_t key, int64_t val) {
    /* Rehash if load factor exceeded */
    if (ht->size * LANG_HT_LOAD_FACTOR_DEN >= ht->capacity * LANG_HT_LOAD_FACTOR_NUM) {
        int64_t new_cap = ht->capacity * 2;
        LangHashEntry** new_buckets =
            (LangHashEntry**)GC_malloc((size_t)(new_cap * (int64_t)sizeof(LangHashEntry*)));
        for (int64_t i = 0; i < ht->capacity; i++) {
            LangHashEntry* e = ht->buckets[i];
            while (e != NULL) {
                LangHashEntry* next = e->next;
                uint64_t slot = lang_ht_hash(e->key) % (uint64_t)new_cap;
                e->next = new_buckets[slot];
                new_buckets[slot] = e;
                e = next;
            }
        }
        ht->buckets  = new_buckets;
        ht->capacity = new_cap;
    }
    /* Insert or update */
    LangHashEntry* e = lang_ht_find(ht, key);
    if (e != NULL) {
        e->val = val;
        return;
    }
    uint64_t slot = lang_ht_hash(key) % (uint64_t)ht->capacity;
    LangHashEntry* new_e = (LangHashEntry*)GC_malloc(sizeof(LangHashEntry));
    new_e->key  = key;
    new_e->val  = val;
    new_e->next = ht->buckets[slot];
    ht->buckets[slot] = new_e;
    ht->size++;
}

int64_t lang_hashtable_containsKey(LangHashtable* ht, int64_t key) {
    return lang_ht_find(ht, key) != NULL ? 1 : 0;
}

void lang_hashtable_remove(LangHashtable* ht, int64_t key) {
    uint64_t h = lang_ht_hash(key) % (uint64_t)ht->capacity;
    LangHashEntry** prev = &ht->buckets[h];
    LangHashEntry*  e    = ht->buckets[h];
    while (e != NULL) {
        if (e->key == key) {
            *prev = e->next;  /* unlink */
            ht->size--;
            return;
        }
        prev = &e->next;
        e    = e->next;
    }
    /* Key not present: no-op (consistent with LangThree remove semantics) */
}

LangCons* lang_hashtable_keys(LangHashtable* ht) {
    LangCons* head = NULL;
    for (int64_t i = 0; i < ht->capacity; i++) {
        LangHashEntry* e = ht->buckets[i];
        while (e != NULL) {
            LangCons* cell = (LangCons*)GC_malloc(sizeof(LangCons));
            cell->head = e->key;
            cell->tail = head;
            head = cell;
            e = e->next;
        }
    }
    return head;
}
```

**Note on `GC_malloc` zero-init:** Boehm `GC_malloc` zeroes memory (unlike `malloc`). So the `buckets` array initialises to all-NULL without explicit memset.

### Pattern 3: coerceToI64 Helper in Elaboration.fs

The key and value arguments must be passed as `i64` to the C functions. Extract this as a reusable helper:

```fsharp
/// Coerce an MlirValue to I64 for C ABI boundary.
/// I64 → no-op; Ptr → LlvmPtrToIntOp; I1 → ArithExtuIOp; I32 → ArithExtuIOp.
let private coerceToI64 (env: ElabEnv) (v: MlirValue) : MlirValue * MlirOp list =
    match v.Type with
    | I64 -> (v, [])
    | Ptr ->
        let r = { Name = freshName env; Type = I64 }
        (r, [LlvmPtrToIntOp(r, v)])
    | I1 ->
        let r = { Name = freshName env; Type = I64 }
        (r, [ArithExtuIOp(r, v)])
    | I32 ->
        let r = { Name = freshName env; Type = I64 }
        (r, [ArithExtuIOp(r, v)])
```

Place this near the top of `elaborateExpr` or as a module-level private let. It is used by all hashtable elaboration arms.

### Pattern 4: Elaboration Arms

All hashtable arms go BEFORE the general `App` case. Three-arg arm (`hashtable_set`) must appear before two-arg arms (`hashtable_get`, `hashtable_containsKey`, `hashtable_remove`), which must appear before one-arg arm (`hashtable_keys`). `hashtable_create` is a one-arg form (the argument is `()` / unit, elaborated but result discarded).

```fsharp
// Phase 23: hashtable_create — one-arg (unit arg elaborated and discarded)
| App (Var ("hashtable_create", _), _unitExpr, _) ->
    let result = { Name = freshName env; Type = Ptr }
    (result, [LlvmCallOp(result, "@lang_hashtable_create", [])])

// Phase 23: hashtable_set — three-arg (must appear before two-arg patterns)
| App (App (App (Var ("hashtable_set", _), htExpr, _), keyExpr, _), valExpr, _) ->
    let (htVal,  htOps)  = elaborateExpr env htExpr
    let (keyVal, keyOps) = elaborateExpr env keyExpr
    let (valRaw, valOps) = elaborateExpr env valExpr
    let (i64Key, kCoerce) = coerceToI64 env keyVal
    let (i64Val, vCoerce) = coerceToI64 env valRaw
    let unitVal = { Name = freshName env; Type = I64 }
    (unitVal,
     htOps @ keyOps @ valOps @ kCoerce @ vCoerce @
     [LlvmCallVoidOp("@lang_hashtable_set", [htVal; i64Key; i64Val])
      ArithConstantOp(unitVal, 0L)])

// Phase 23: hashtable_get — two-arg
| App (App (Var ("hashtable_get", _), htExpr, _), keyExpr, _) ->
    let (htVal,  htOps)  = elaborateExpr env htExpr
    let (keyVal, keyOps) = elaborateExpr env keyExpr
    let (i64Key, kCoerce) = coerceToI64 env keyVal
    let result = { Name = freshName env; Type = I64 }
    (result, htOps @ keyOps @ kCoerce @ [LlvmCallOp(result, "@lang_hashtable_get", [htVal; i64Key])])

// Phase 23: hashtable_containsKey — two-arg
| App (App (Var ("hashtable_containsKey", _), htExpr, _), keyExpr, _) ->
    let (htVal,  htOps)  = elaborateExpr env htExpr
    let (keyVal, keyOps) = elaborateExpr env keyExpr
    let (i64Key, kCoerce) = coerceToI64 env keyVal
    let rawResult  = { Name = freshName env; Type = I64 }
    let zeroVal    = { Name = freshName env; Type = I64 }
    let boolResult = { Name = freshName env; Type = I1 }
    let ops = [
        LlvmCallOp(rawResult, "@lang_hashtable_containsKey", [htVal; i64Key])
        ArithConstantOp(zeroVal, 0L)
        ArithCmpIOp(boolResult, "ne", rawResult, zeroVal)
    ]
    (boolResult, htOps @ keyOps @ kCoerce @ ops)

// Phase 23: hashtable_remove — two-arg
| App (App (Var ("hashtable_remove", _), htExpr, _), keyExpr, _) ->
    let (htVal,  htOps)  = elaborateExpr env htExpr
    let (keyVal, keyOps) = elaborateExpr env keyExpr
    let (i64Key, kCoerce) = coerceToI64 env keyVal
    let unitVal = { Name = freshName env; Type = I64 }
    (unitVal,
     htOps @ keyOps @ kCoerce @
     [LlvmCallVoidOp("@lang_hashtable_remove", [htVal; i64Key])
      ArithConstantOp(unitVal, 0L)])

// Phase 23: hashtable_keys — one-arg
| App (Var ("hashtable_keys", _), htExpr, _) ->
    let (htVal, htOps) = elaborateExpr env htExpr
    let result = { Name = freshName env; Type = Ptr }
    (result, htOps @ [LlvmCallOp(result, "@lang_hashtable_keys", [htVal])])
```

### Pattern 5: ExternalFuncDecl Registrations

Add to BOTH `externalFuncs` lists in `Elaboration.fs` (there are two identical lists, around lines 2115 and 2252). Match the `@lang_array_*` entries immediately preceding them:

```fsharp
{ ExtName = "@lang_hashtable_create";      ExtParams = [];              ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_hashtable_get";         ExtParams = [Ptr; I64];      ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_hashtable_set";         ExtParams = [Ptr; I64; I64]; ExtReturn = None;     IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_hashtable_containsKey"; ExtParams = [Ptr; I64];      ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_hashtable_remove";      ExtParams = [Ptr; I64];      ExtReturn = None;     IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_hashtable_keys";        ExtParams = [Ptr];           ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
```

### Pattern 6: E2E Test File Format

Follow the existing test file format (e.g., `22-01-array-create.flt`):

```
// --- Command: bash -c 'OUTBIN=$(mktemp /tmp/langback_XXXXXX) && dotnet run --project %S/../../src/LangBackend.Cli/LangBackend.Cli.fsproj -- %input -o $OUTBIN && $OUTBIN; echo $?; rm -f $OUTBIN'
// --- Input:
[lang source]
// --- Output:
[expected exit code or output]
```

Tests for this phase go in `tests/compiler/` as `23-01-*.flt` through `23-08-*.flt`.

### Anti-Patterns to Avoid

- **Using `malloc` instead of `GC_malloc`:** All C allocations (`LangHashtable`, `LangHashEntry`, `buckets` array, exception string) must use `GC_malloc`. Raw `malloc` allocations are not traced by Boehm GC and may not be reclaimed.
- **Open addressing instead of chaining:** Deletion with open addressing requires tombstones, which complicate the GC scanning (stale pointer-sized tombstone values cause false retention). Chaining handles deletion cleanly.
- **Two-arg match before three-arg match:** `hashtable_set` is three-arg. If the two-arg `hashtable_get`/`hashtable_containsKey` patterns appear first, `hashtable_set ht key` partially applies and the three-arg case never fires. Three-arg must come first.
- **Forgetting to update both ExternalFuncDecl lists:** There are two identical lists in `Elaboration.fs`. Updating only one will cause intermittent failures. Search for `@lang_array_to_list` and add the new entries at BOTH occurrences.
- **`hashtable_containsKey` returning I1 directly from C:** The C function returns `i64` (0 or 1). Convert to `I1` using the `lang_string_contains` pattern: `ArithCmpIOp(result, "ne", raw, zero)`.
- **Assuming GC_malloc zeroes struct fields without relying on it:** `GC_malloc` does zero-initialise memory (unlike `malloc`). The bucket array starts all-NULL. This is valid because Boehm guarantees zeroed memory on `GC_malloc`.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Hash function | Custom hash from scratch | FNV-1a-style 64-bit integer mix (in C) | Existing well-tested distribution; avalanche properties prevent clustering |
| Chaining linked list | Open addressing with tombstones | Chained `LangHashEntry*` linked list | Deletion is O(1) chain relink; no tombstone GC false-retention issue |
| Resizing logic | Fixed-size table | Rehash on set when `size * 4 >= capacity * 3` | Fixed table degrades to O(n) on collision; must rehash for correctness |
| Key coercion in F# | Type-dispatched elaboration arms | `coerceToI64` helper | Ptr/I1/I64 types all need different coercions; centralize once |
| Bool return from C | I1 from `LlvmCallOp` | `LlvmCallOp(I64) + ArithCmpIOp("ne", ..., 0)` | C ABI cannot return `i1` safely; return `i64`, convert in MLIR |

**Key insight:** All hashtable operations are delegated entirely to C. There is no inline MLIR for hashtable logic — the MLIR side emits one `LlvmCallOp` or `LlvmCallVoidOp` per operation. This is the right tradeoff: hash bucket traversal, rehashing, and key comparison all require loops or conditional branches that the elaborator does not have a clean way to express.

## Common Pitfalls

### Pitfall 1: Two `externalFuncs` Lists Not Both Updated

**What goes wrong:** `mlir-opt` fails: "use of undefined value '@lang_hashtable_get'". But only in certain programs.

**Why it happens:** `Elaboration.fs` has two separate `let externalFuncs = [...]` blocks (confirmed at lines ~2115 and ~2252). Each corresponds to a different elaboration entry point. Only one gets updated.

**How to avoid:** Search `Elaboration.fs` for `@lang_array_to_list` (the last array entry) and add hashtable entries at BOTH locations.

**Warning signs:** Some programs compile and link but others don't; the failure pattern correlates with whether `elaborateProgram` or the alternate entry point is used.

### Pitfall 2: hashtable_set Three-Arg Pattern Masked by Two-Arg Pattern

**What goes wrong:** `hashtable_set ht "key" 42` partially applies — the compiler tries to elaborate `hashtable_set ht "key"` as a two-arg builtin (which matches `hashtable_get` or `hashtable_remove` if mislabeled), or falls through to the general `App` case and emits a closure indirect call instead of a direct C call.

**Why it happens:** F# match arms are tried top to bottom. If a two-arg pattern `App(App(Var("hashtable_set", _), ht, _), key, _)` appears above the three-arg pattern, it fires on `hashtable_set ht key` and the outer value application falls through to general `App`.

**How to avoid:** Place `hashtable_set` (three-arg) BEFORE `hashtable_get` / `hashtable_containsKey` / `hashtable_remove` (two-arg) in the match. Follow the exact ordering in the implementation order above.

**Warning signs:** `hashtable_set ht "key" 42` compiles but crashes at runtime; or MLIR verification error about wrong number of arguments to the C function.

### Pitfall 3: Key Not Coerced from Ptr to I64

**What goes wrong:** A string key (elaborated as `Ptr`) is passed directly to `@lang_hashtable_get` which expects `i64`. MLIR verifier rejects: "type mismatch in call argument: expected 'i64', got '!llvm.ptr'".

**Why it happens:** String expressions elaborate to `Ptr`-typed MlirValues. The C ABI takes `int64_t key`.

**How to avoid:** Always apply `coerceToI64` to key and value arguments before calling hashtable C functions. This emits `LlvmPtrToIntOp` for `Ptr` keys and a no-op for `I64` keys.

**Warning signs:** MLIR opt error: "type mismatch in llvm.call".

### Pitfall 4: GC Pointer Liveness of Keys/Values

**What goes wrong:** A string key stored in the hashtable (as ptr-coerced-to-i64 in `LangHashEntry.key`) gets collected by Boehm GC, leaving a dangling integer that points to freed memory. Subsequent `hashtable_get` calls with the same string dereference freed memory.

**Why it happens:** Boehm GC is conservative: it scans `GC_malloc`'d memory for pointer-shaped values. `LangHashEntry.key` is typed `int64_t` — but Boehm's conservative scanner treats any pointer-sized value that looks like a valid heap address as a live reference. As long as `LangHashEntry` is itself `GC_malloc`'d and reachable from the `LangHashtable`, the key pointer is retained conservatively.

**Why this is actually safe:** Since `LangHashEntry` is `GC_malloc`'d and reachable from `ht->buckets`, Boehm conservatively scans the entire `LangHashEntry` struct including the `key` field. If `key` holds a valid heap address (string pointer), Boehm retains that allocation. This works correctly without any extra write barriers.

**Warning signs:** Non-deterministic crash when using string keys and GC pressure is high. (Should not occur if all allocations use `GC_malloc`.)

### Pitfall 5: hashtable_get Not Raising via lang_throw

**What goes wrong:** Missing key returns garbage (e.g., 0) instead of raising an exception. The success criterion HT-03 requires `hashtable_get ht "missing"` to raise.

**Why it happens:** Naive implementation returns a sentinel (0 or -1) on miss rather than calling `lang_throw`.

**How to avoid:** In `lang_hashtable_get`, when `lang_ht_find` returns NULL: construct a `LangString*` error message and call `lang_throw((void*)msg)`. Mirror the `lang_array_bounds_check` pattern exactly.

**Warning signs:** `try hashtable_get ht "missing" with | _ -> 99` returns 0 instead of 99.

### Pitfall 6: hashtable_containsKey Returning I64 Not Converted to I1

**What goes wrong:** `hashtable_containsKey ht k` is used in an `if` branch which expects `I1`. MLIR verifier rejects: "expected 'i1', got 'i64'".

**Why it happens:** The C function returns `int64_t` (0 or 1). This elaborates to an `I64` MlirValue. `if` and `cf.cond_br` require `I1`.

**How to avoid:** Apply the `lang_string_contains` pattern: after the `LlvmCallOp` returning `I64`, emit `ArithConstantOp(zero, 0L)` + `ArithCmpIOp(boolResult, "ne", rawResult, zero)`. The result is `I1`.

**Warning signs:** MLIR opt error: "condition must be of i1 type".

### Pitfall 7: uint64_t Narrowing in Modulo for Bucket Index

**What goes wrong:** `lang_ht_hash(key) % ht->capacity` — if `ht->capacity` is `int64_t` and the hash result is `uint64_t`, the modulo may invoke implementation-defined behavior for negative values of the signed type.

**Why it happens:** `int64_t % int64_t` is signed division; a negative hash value gives a negative remainder.

**How to avoid:** Cast capacity to `uint64_t` before the modulo: `lang_ht_hash(key) % (uint64_t)ht->capacity`. `lang_ht_hash` returns `uint64_t`, so the result is always in `[0, capacity)`.

**Warning signs:** Random crashes or wrong bucket selection for some keys; negative array index access.

## Code Examples

### Full C Runtime: lang_hashtable_get with Exception

```c
/* Source: Pattern 2 above */
int64_t lang_hashtable_get(LangHashtable* ht, int64_t key) {
    LangHashEntry* e = lang_ht_find(ht, key);
    if (e == NULL) {
        /* Build LangString* error message and throw */
        const char* msg_str = "Hashtable.get: key not found";
        int64_t     msg_len = (int64_t)strlen(msg_str);
        char*       buf     = (char*)GC_malloc((size_t)(msg_len + 1));
        memcpy(buf, msg_str, (size_t)(msg_len + 1));
        LangString* msg = (LangString*)GC_malloc(sizeof(LangString));
        msg->length = msg_len;
        msg->data   = buf;
        lang_throw((void*)msg);
        return 0; /* unreachable — satisfies compiler */
    }
    return e->val;
}
```

### hashtable_containsKey Elaboration (I64 to I1 Conversion)

```fsharp
(* Source: Pattern 4 above — mirrors lang_string_contains pattern *)
| App (App (Var ("hashtable_containsKey", _), htExpr, _), keyExpr, _) ->
    let (htVal,  htOps)   = elaborateExpr env htExpr
    let (keyVal, keyOps)  = elaborateExpr env keyExpr
    let (i64Key, kCoerce) = coerceToI64 env keyVal
    let rawResult  = { Name = freshName env; Type = I64 }
    let zeroVal    = { Name = freshName env; Type = I64 }
    let boolResult = { Name = freshName env; Type = I1 }
    let ops = [
        LlvmCallOp(rawResult, "@lang_hashtable_containsKey", [htVal; i64Key])
        ArithConstantOp(zeroVal, 0L)
        ArithCmpIOp(boolResult, "ne", rawResult, zeroVal)
    ]
    (boolResult, htOps @ keyOps @ kCoerce @ ops)
```

### MLIR Output for hashtable_set ht k v

```mlir
; Source: Pattern 4 above — hashtable_set ht "key" 42

; Elaborate ht → %t0 : !llvm.ptr
; Elaborate "key" → %t1 : !llvm.ptr (string header)
; Elaborate 42 → %t2 : i64

; coerceToI64 key: LlvmPtrToIntOp
%t3 = llvm.ptrtoint %t1 : !llvm.ptr to i64

; coerceToI64 val: no-op (already i64)
; LlvmCallVoidOp
llvm.call @lang_hashtable_set(%t0, %t3, %t2) : (!llvm.ptr, i64, i64) -> ()

; unit return
%t4 = arith.constant 0 : i64
```

### lang_hashtable_keys Returning Cons List

```c
/* Source: Pattern 2 above */
/* Returns cons list of all keys in arbitrary order (bucket traversal order). */
/* Key values are raw int64_t — pointer keys are returned as ptr-coerced-to-int64. */
LangCons* lang_hashtable_keys(LangHashtable* ht) {
    LangCons* head = NULL;
    for (int64_t i = 0; i < ht->capacity; i++) {
        LangHashEntry* e = ht->buckets[i];
        while (e != NULL) {
            LangCons* cell = (LangCons*)GC_malloc(sizeof(LangCons));
            cell->head = e->key;
            cell->tail = head;
            head = cell;
            e = e->next;
        }
    }
    return head;
}
```

## State of the Art

| Old Approach | Current Approach | Impact |
|--------------|-----------------|--------|
| No hashtable support | Chained bucket C runtime + MLIR builtin dispatch | Hashtables fully operational |
| All complex ops inline in MLIR | C runtime delegation (same as array_create, lang_range) | Clean separation; no new MlirOp cases needed |

**No deprecated patterns.** This phase extends the architecture without replacing anything.

## Open Questions

1. **String key content-equality (HT-08)**
   - What we know: The requirements state "C runtime hash function for boxed int, string, tuple, ADT values." The current design uses `i64` equality for all keys, meaning string keys compare by pointer identity, not by string content.
   - What's unclear: Do Phase 23 E2E tests use `hashtable_get` with two different string objects that have the same content? If so, the `strcmp`-based approach (Option B) is required.
   - Recommendation: Start with Option A (i64 identity equality). If a test fails because string content equality is needed, switch to Option B by adding a `key_is_ptr` flag to LangHashEntry and passing it from the elaboration arm. The ABI change is: `ExtParams = [Ptr; I64; I64]` where the extra I64 is the `key_is_ptr` flag. See ARCHITECTURE.md lines 360-374 for the full struct layout.

2. **`hashtable_keys` return order**
   - What we know: LangThree evaluator iterates Dictionary keys in insertion-undefined order. The compiled runtime returns keys in bucket-traversal order (not insertion order).
   - What's unclear: Do any E2E tests check the exact order of `hashtable_keys` results?
   - Recommendation: Tests should sort the key list before comparing, or use `hashtable_containsKey` to verify membership rather than checking exact key order. If a test requires insertion order, the data structure needs an additional `LangCons*` insertion-order list — defer to a follow-on phase.

3. **`hashtable_create ()` unit argument elaboration**
   - What we know: `hashtable_create ()` parses as `App(Var("hashtable_create"), Tuple([], span), span)`. The `_unitExpr` in the elaboration arm is the empty tuple. It must be elaborated (to advance freshName counters etc.) or can be ignored.
   - What's unclear: Whether elaborating the unit expr ever emits meaningful ops (it shouldn't — `Tuple([])` should be a zero-size allocation or constant).
   - Recommendation: Use `| App (Var ("hashtable_create", _), _unitExpr, _) ->` which ignores the argument without elaborating it. This is safe because the unit argument has no side effects. If `hashtable_create` is called with a non-unit expression, it still works correctly.

## Sources

### Primary (HIGH confidence)
- Direct code analysis of `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/lang_runtime.c` — confirmed `LangString`, `LangCons` layouts; `lang_throw` pattern; `GC_malloc` usage; `lang_array_create` pattern
- Direct code analysis of `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/Elaboration.fs` — confirmed `App`-chain matching order; ExternalFuncDecl two-list structure at lines ~2115 and ~2252; `lang_string_contains` I64→I1 pattern; `LlvmPtrToIntOp` usage; `elaborateExpr` dispatch shape
- Direct code analysis of `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/MlirIR.fs` — confirmed all needed ops exist: `LlvmCallOp`, `LlvmCallVoidOp`, `ArithCmpIOp`, `LlvmPtrToIntOp`, `ArithConstantOp`
- Direct code analysis of `/Users/ohama/vibe-coding/LangThree/src/LangThree/Eval.fs` lines 526-571 — authoritative source for hashtable semantics: `hashtable_create`, `get` (raises on miss), `set`, `containsKey`, `keys`, `remove`
- `/Users/ohama/vibe-coding/LangBackend/.planning/research/STACK.md` sections 3.1-3.5 — chaining rationale; C function signatures; ExternalFuncDecl templates; coerceToI64 helper design
- `/Users/ohama/vibe-coding/LangBackend/.planning/research/ARCHITECTURE.md` lines 325-469 — hashtable memory layout options; key type handling; builtin dispatch patterns

### Secondary (MEDIUM confidence)
- `/Users/ohama/vibe-coding/LangBackend/.planning/phases/22-array-core/22-RESEARCH.md` — direct precedent for the array implementation; hashtable follows same pattern

### Tertiary (LOW confidence)
- None

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — no new external deps; all MlirOp cases confirmed by direct code analysis; no new DU cases needed
- Architecture: HIGH — chained bucket table design confirmed in STACK.md and ARCHITECTURE.md; elaboration patterns confirmed from `lang_string_contains` and array builtins as direct precedent; two-list ExternalFuncDecl confirmed in Elaboration.fs
- Pitfalls: HIGH — two-list bug confirmed in code; three-arg-before-two-arg ordering confirmed by array pattern; I64→I1 conversion confirmed from `lang_string_contains` precedent; GC malloc zeroing confirmed; `lang_throw` on miss confirmed from `lang_array_bounds_check`

**Research date:** 2026-03-27
**Valid until:** 2026-04-27 (stable domain — only changes if MLIR dialect syntax changes or project architecture decisions are revised)
