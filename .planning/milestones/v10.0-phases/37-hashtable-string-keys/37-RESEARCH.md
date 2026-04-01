# Phase 37: Hashtable String Keys - Research

**Researched:** 2026-03-30
**Domain:** C runtime hashtable ABI extension + MLIR elaboration key-type dispatch
**Confidence:** HIGH

## Summary

The current C hashtable uses `int64_t key` throughout `LangHashEntry`, `lang_ht_hash`, `lang_ht_find`, and all six public functions. When a `LangString*` pointer is passed as a key, the compiler coerces it with `LlvmPtrToIntOp` (pointer value → I64), and the C runtime compares keys by integer equality (`e->key == key`). Two strings with identical content but different allocations have different pointer values, so they hash to different buckets — the fundamental ABI mismatch identified in Phase 35-01.

The fix requires two parallel changes. In `lang_runtime.c`: add string-aware hash and equality functions, plus new string-key variants of all six hashtable operations (`lang_hashtable_get_str`, `lang_hashtable_set_str`, `lang_hashtable_containsKey_str`, `lang_hashtable_remove_str`, `lang_hashtable_keys_str`, `lang_hashtable_trygetvalue_str`). The `LangHashEntry` struct must store the key as `void*` (or as `int64_t` with a separate string-pointer field) — the simplest approach that avoids changing existing integer-key code is a new struct `LangHashEntryStr` with `LangString* key` and separate string-specific chain. Alternatively, change `LangHashEntry.key` to `int64_t` with a companion `is_string` flag, but that complicates the existing code. **Recommended**: duplicate the hash entry type — `LangHashEntryStr` stores `LangString* key; int64_t val; LangHashEntryStr* next` — and add a companion `LangHashtableStr` struct or reuse `LangHashtable` with a union bucket array. **Simplest feasible**: keep `LangHashtable` identical, change `LangHashEntry.key` to `int64_t` but add new C functions that accept `LangString*` and do string-content hashing/comparison internally, storing the pointer as `int64_t` (ptrtoint) — but this reintroduces pointer-identity equality on lookup.

The truly correct approach: `LangHashEntry` key must be compared by content. The only correct architecture is to either (a) store keys as `LangString*` in a new struct, or (b) overload the existing struct with a union and a type tag. **Recommended approach for this codebase**: add a new parallel type `LangHashEntryStr { LangString* key; int64_t val; LangHashEntryStr* next }` and a new `LangHashtableStr { int64_t tag; int64_t capacity; int64_t size; LangHashEntryStr** buckets }`. The `tag` field at offset 0 can use `-2` to distinguish from integer hashtable (`-1`). This avoids touching existing integer-key code entirely.

In `Elaboration.fs`: when elaborating `hashtable_get`, `hashtable_set`, etc., detect whether the key expression is `Ptr`-typed and emit the `_str` variant instead of coercing to I64. Similarly, `IndexGet` and `IndexSet` need string-aware dispatch — add new `lang_index_get_str` and `lang_index_set_str` C functions that accept `void* collection, LangString* key`, dispatch on the `tag` field, and call `lang_hashtable_get_str`/`lang_hashtable_set_str`. The `lang_hashtable_keys_str` return type is a `LangCons*` whose `head` values are `LangString*` stored as `int64_t` (ptrtoint) — GC handles this conservatively, and callers receive them as opaque I64 which they can inttoptr back to `LangString*`.

**Primary recommendation:** Add a new `LangHashtableStr` struct + six `_str` functions in `lang_runtime.c/h`; add a `tag = -2` convention; update elaboration to emit `_str` calls when key is `Ptr`; add `lang_index_get_str`/`lang_index_set_str` for `IndexGet`/`IndexSet` with string keys; update both `externalFuncs` lists.

## Standard Stack

No new external libraries. All changes are internal to `lang_runtime.c`, `lang_runtime.h`, and `Elaboration.fs`.

### Core (extended in this phase)

| Component | Location | Phase 37 Change |
|-----------|----------|-----------------|
| `lang_runtime.h` | `src/FunLangCompiler.Compiler/` | Add `LangHashEntryStr`, `LangHashtableStr` structs; declare 6 `_str` functions + 2 index `_str` functions (~25 LOC) |
| `lang_runtime.c` | `src/FunLangCompiler.Compiler/` | Add `lang_ht_str_hash`, `lang_ht_str_find`, `lang_hashtable_create_str` (or reuse `create`), 6 `_str` operation functions, 2 index `_str` functions (~150 LOC) |
| `Elaboration.fs` | `src/FunLangCompiler.Compiler/` | Update key coercion in 6 hashtable builtin arms + `IndexGet`/`IndexSet` to dispatch on key type; update both `externalFuncs` lists (~40 LOC delta) |

### No Changes Required

| Component | Reason |
|-----------|--------|
| `MlirIR.fs` | All required ops exist: `LlvmCallOp`, `LlvmCallVoidOp`, `LlvmPtrToIntOp`, `LlvmIntToPtrOp`, `Ptr` type |
| `Printer.fs` | No new MlirOp cases |
| `Pipeline.fs` | `lang_runtime.c` already compiled and linked |
| `MatchCompiler.fs` | No pattern changes |

**Build:** `dotnet build` — no new packages.

## Architecture Patterns

### Struct Design: LangHashtableStr

```c
// Source: lang_runtime.c/h — new string-key hashtable variant
typedef struct LangHashEntryStr {
    LangString*            key;
    int64_t                val;
    struct LangHashEntryStr* next;
} LangHashEntryStr;

typedef struct {
    int64_t         tag;        // -2 = string hashtable (distinguishes from -1 integer hashtable)
    int64_t         capacity;
    int64_t         size;
    LangHashEntryStr** buckets;
} LangHashtableStr;
```

**Why `tag = -2`:** The `lang_index_get`/`lang_index_set` dispatcher already uses `tag < 0` to detect hashtable vs array. Extending the convention: `tag == -1` → integer hashtable, `tag == -2` → string hashtable. The dispatcher can branch on both. No existing code breaks.

### Hash Function for String Keys

```c
// Source: lang_runtime.c — FNV-1a over string content
static uint64_t lang_ht_str_hash(LangString* key) {
    uint64_t h = UINT64_C(14695981039346656037);  // FNV offset basis
    for (int64_t i = 0; i < key->length; i++) {
        h ^= (uint8_t)key->data[i];
        h *= UINT64_C(1099511628211);              // FNV prime
    }
    return h;
}
```

**Why FNV-1a:** Simple, no external dependencies, good avalanche for string data, consistent with codebase's approach of using established hash finalizers (Phase 23 uses Murmur3 for integers). FNV-1a is simpler to implement correctly for byte-string hashing.

### String Equality for Lookup

```c
// Source: lang_runtime.c
static LangHashEntryStr* lang_ht_str_find(LangHashtableStr* ht, LangString* key) {
    uint64_t bucket = lang_ht_str_hash(key) % (uint64_t)ht->capacity;
    LangHashEntryStr* e = ht->buckets[bucket];
    while (e != NULL) {
        if (e->key->length == key->length &&
            memcmp(e->key->data, key->data, (size_t)key->length) == 0) {
            return e;
        }
        e = e->next;
    }
    return NULL;
}
```

**Critical:** Must compare by content (`memcmp`), not by pointer identity. This is the core fix for RT-01.

### Elaboration.fs Key-Type Dispatch

The existing pattern coerces keys to `I64` unconditionally. The fix adds a branch on `keyVal.Type`:

```fsharp
// Source: Elaboration.fs — updated hashtable_get arm (illustrative pattern)
| App (App (Var ("hashtable_get", _), htExpr, _), keyExpr, _) ->
    let (htVal,  htOps)  = elaborateExpr env htExpr
    let (keyVal, keyOps) = elaborateExpr env keyExpr
    let (htPtr, htCoerce) = coerceToPtrArg env htVal
    let result = { Name = freshName env; Type = I64 }
    match keyVal.Type with
    | Ptr ->
        // String key: pass as Ptr, call _str variant
        (result, htOps @ keyOps @ htCoerce @ [LlvmCallOp(result, "@lang_hashtable_get_str", [htPtr; keyVal])])
    | I64 ->
        // Integer key: existing path
        (result, htOps @ keyOps @ htCoerce @ [LlvmCallOp(result, "@lang_hashtable_get", [htPtr; keyVal])])
    | I1 ->
        let v = { Name = freshName env; Type = I64 }
        let coerce = [ArithExtuIOp(v, keyVal)]
        (result, htOps @ keyOps @ htCoerce @ coerce @ [LlvmCallOp(result, "@lang_hashtable_get", [htPtr; v])])
    | _ ->
        (result, htOps @ keyOps @ htCoerce @ [LlvmCallOp(result, "@lang_hashtable_get", [htPtr; keyVal])])
```

Apply this same pattern to `hashtable_set`, `hashtable_containsKey`, `hashtable_remove`, `hashtable_trygetvalue`.

### IndexGet/IndexSet String Key Dispatch

`IndexGet`/`IndexSet` currently coerce the index/key to `I64`. For string keys, they must call `_str` variants:

```fsharp
// Source: Elaboration.fs — updated IndexGet arm
| IndexGet (collExpr, idxExpr, _) ->
    let (collVal, collOps) = elaborateExpr env collExpr
    let (idxVal, idxOps) = elaborateExpr env idxExpr
    let result = { Name = freshName env; Type = I64 }
    match idxVal.Type with
    | Ptr ->
        // String index: call lang_index_get_str (new)
        (result, collOps @ idxOps @ [LlvmCallOp(result, "@lang_index_get_str", [collVal; idxVal])])
    | I64 ->
        (result, collOps @ idxOps @ [LlvmCallOp(result, "@lang_index_get", [collVal; idxVal])])
    | I1 ->
        let v = { Name = freshName env; Type = I64 } in
        (result, collOps @ idxOps @ [ArithExtuIOp(v, idxVal); LlvmCallOp(result, "@lang_index_get", [collVal; v])])
    | _ ->
        (result, collOps @ idxOps @ [LlvmCallOp(result, "@lang_index_get", [collVal; idxVal])])
```

### lang_hashtable_create_str

String hashtable creation uses `tag = -2`:

```c
LangHashtableStr* lang_hashtable_create_str(void) {
    LangHashtableStr* ht = (LangHashtableStr*)GC_malloc(sizeof(LangHashtableStr));
    ht->tag = -2;
    ht->capacity = 16;
    ht->size = 0;
    ht->buckets = (LangHashEntryStr**)GC_malloc(
        (size_t)(ht->capacity * (int64_t)sizeof(LangHashEntryStr*)));
    for (int64_t i = 0; i < ht->capacity; i++) ht->buckets[i] = NULL;
    return ht;
}
```

**Note:** The user-facing `hashtable_create ()` builtin can still call `lang_hashtable_create` (integer variant). The string variant needs a new `hashtable_create_str ()` builtin, OR `hashtable_create` auto-detects key type on first insert. **Simpler**: `hashtable_create ()` always creates the string-capable version (`LangHashtableStr` with `tag = -2`). But this breaks the integer hashtable. **Best approach**: `hashtable_create ()` creates the integer variant as today; string keys use `Hashtable.create ()` which goes through the same builtin — since the type of keys is known at MLIR elaboration time (Ptr vs I64), the compiler can emit `@lang_hashtable_create_str` vs `@lang_hashtable_create` based on first-use context.

**Actually, the create call has no key argument** — the compiler cannot know at `hashtable_create ()` call site what key type will be used. Resolution: The `lang_index_set_str` and `lang_hashtable_set_str` functions need to work on a `LangHashtableStr*`. The `create` call must produce the right struct type. Since the compiler knows at `ht.["hello"] <- 42` that the key is a string, the dispatcher in `lang_index_set_str` can upgrade the hashtable on first string insert. **Simplest correct solution**: Have `lang_hashtable_create` always produce a unified struct with `tag = -2` (string mode), and change `lang_hashtable_get/set/etc` (the integer variants) to treat the `tag` field as irrelevant — they already work with `LangHashtable` which has `tag = -1`. The two structs are layout-compatible at offset 0 (tag field), so the runtime dispatch in `lang_index_get/set` can use `tag == -2` for string mode.

**Cleaner resolution**: Keep `hashtable_create ()` emitting `@lang_hashtable_create` always. The returned pointer is typed as `Ptr` in MLIR (opaque). When `hashtable_set_str` is called, it initializes the bucket array as `LangHashEntryStr**` by reinterpreting the struct. This only works if the create function allocates with `LangHashtableStr` layout from the start. **Correct resolution**: add `hashtable_create_str ()` as a new builtin that the Hashtable module wrapper can expose as `Hashtable.createStr ()`, AND teach the compiler to select `@lang_hashtable_create_str` when the context is a string-key hashtable.

**However**: the type system has no static type for hashtables — it's all opaque `Ptr`. The compiler cannot statically choose `create` vs `create_str` without type inference. **Practical solution**: Make `lang_hashtable_create` always allocate a `LangHashtableStr` with `tag = -2`, and have the `_str` operations cast to `LangHashtableStr*`. The integer key operations continue to use `tag = -1` (from the existing `lang_hashtable_create`). When `ht.["key"] <- val` is elaborated with a Ptr index, the runtime dispatches to `lang_hashtable_set_str` which expects `LangHashtableStr*` — but the hashtable was created with `lang_hashtable_create` (integer, `tag = -1`). **This is the real problem.**

**Final answer**: Add a new `hashtable_create_str` C function that allocates `LangHashtableStr` with `tag = -2`, and expose it as the `hashtable_create` builtin via type-sensitive dispatch in the elaborator. Since the elaborator knows the key type at call sites (set/get/etc), it should track which create was used — but without type inference, this is not feasible inline.

**Pragmatic solution (used in similar embedded compilers)**: Make `hashtable_create` always produce a *unified* struct that supports both int and string keys. Add a `key_type` field: `0 = int, 1 = str`. Initialize to `0`. On first `set_str`, flip to `1`. Alternatively: Accept that users call `Hashtable.create ()` for int hashtables and add a new `Hashtable.createStr ()` for string hashtables, mapping to two separate C functions.

**Recommended**: Add `hashtable_create_str` to `lang_runtime.c/h`, add a `hashtable_create_str` builtin elaboration arm in `Elaboration.fs` (same pattern as `hashtable_create`), expose it in `Prelude/Hashtable.fun` as `createStr`. The existing `hashtable_create` / `Hashtable.create` remain for integer keys. This is the least-disruptive, clearest approach.

### lang_hashtable_keys_str Return Value

`lang_hashtable_keys_str` returns a `LangCons*` list. Each `head` is a `LangString*` stored as `int64_t` (via `(int64_t)(uintptr_t)ptr`). The GC traces these conservatively. Callers receive the values as I64 and inttoptr back to `LangString*` when needed. This is the existing pattern for all Ptr-as-I64 values in cons lists.

### for-in over String Hashtable

`lang_for_in_hashtable` iterates over `LangHashtable` with integer key entries. Add `lang_for_in_hashtable_str` that iterates over `LangHashtableStr`. The elaborator (`ForInExpr` arm) needs to detect string hashtable vs integer hashtable — but since `collectionKind` is inferred from the binding expression (see `inferCollectionKind` in Elaboration.fs at line ~79), and `hashtable_create_str` can be given its own `CollectionKind` value, this is achievable. **Simplest approach for Phase 37**: defer `for-in` over string hashtables (not in Phase 37 success criteria). The success criteria only require get/set/containsKey/remove/keys with string keys.

### Anti-Patterns to Avoid

- **Using ptrtoint for string key storage**: Defeats content-equality. This is exactly the bug being fixed.
- **Modifying `LangHashEntry` to add a union**: Breaks all existing integer-key code and doubles struct size. Use a separate `LangHashEntryStr` type.
- **Trying to infer hashtable key type from `hashtable_create ()` call site**: No key info available there. Requires a separate `hashtable_create_str ()` builtin.
- **Changing `lang_index_get/set` signatures**: They must continue to accept `int64_t index` for array dispatch. Add new `_str` variants alongside.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| String hash function | Custom polynomial hash | FNV-1a (inline, ~5 lines) | Known distribution, no dependencies, correct for byte-strings |
| String equality | Manual loop | `memcmp` | Handles embedded nulls, optimized by libc |
| GC-safe string storage | Manual root tracking | Store `LangString*` in GC-allocated struct | Boehm GC traces conservatively — any GC_malloc'd struct field is a root |

**Key insight:** The GC is conservative — as long as `LangString*` pointers live inside GC-allocated memory (the `LangHashEntryStr` struct), they are traced automatically. No manual GC roots needed.

## Common Pitfalls

### Pitfall 1: Pointer Identity vs Content Equality
**What goes wrong:** The existing `lang_ht_find` uses `e->key == key` (integer equality). If the key type stays `int64_t` and strings are passed as ptrtoint, lookup fails for different allocations of equal strings.
**Why it happens:** ABI design decision in Phase 23 — integer keys only.
**How to avoid:** Use `memcmp` on `LangString.data` with length check in `lang_ht_str_find`. Never store string pointer value as the equality criterion.
**Warning signs:** `containsKey` returns false after `set` with same string content from different allocation.

### Pitfall 2: create vs create_str Mismatch
**What goes wrong:** User calls `hashtable_create ()` (produces `LangHashtable` with `tag = -1`), then calls `hashtable_set_str` on it. The `_str` function casts to `LangHashtableStr*` — the bucket array was allocated as `LangHashEntry**`, not `LangHashEntryStr**`. First dereference of a bucket crashes.
**Why it happens:** Two incompatible struct layouts with the same opaque `Ptr` type.
**How to avoid:** Require `hashtable_create_str ()` for string-key hashtables. Document in the builtin and in `Prelude/Hashtable.fun`.
**Warning signs:** Crash on first `set_str` call.

### Pitfall 3: ExternalFuncDecl Not Updated in Both Lists
**What goes wrong:** `Elaboration.fs` has two `externalFuncs` lists (around line 3300 and line 3542). Adding a function declaration to only one list causes MLIR validation errors at runtime on programs using the module path.
**Why it happens:** The two-list structure was introduced when FunLangCompiler added a module-aware elaboration path. Both must be kept in sync.
**How to avoid:** Always update both `externalFuncs` lists. Search for the existing `@lang_hashtable_create` entry and duplicate the new entries in the same relative position in both lists.
**Warning signs:** Tests pass in single-file mode but fail in module mode.

### Pitfall 4: IndexGet/IndexSet Only Handles I64 Index
**What goes wrong:** `IndexGet` coerces index to I64 unconditionally. If a `Ptr`-typed string key reaches `ArithExtuIOp(v, idxVal)`, MLIR rejects it (can't zext ptr to i64). Alternatively it reaches the `if idxVal.Type = I64 then` branch as a no-op and is passed as `Ptr` to `@lang_index_get` which expects `i64` — MLIR type error.
**Why it happens:** `IndexGet`/`IndexSet` were designed for arrays (integer index) and extended to hashtables (integer key). String keys are the new case.
**How to avoid:** Add `| Ptr ->` branch before the `if idxVal.Type = I64` check that calls `_str` variants.
**Warning signs:** MLIR validation error "type mismatch" on `lang_index_get` call when string index is used.

### Pitfall 5: lang_hashtable_keys_str Head Values Must Be Ptr-as-I64
**What goes wrong:** If `lang_hashtable_keys_str` stores raw `LangString*` in `LangCons.head` (typed `int64_t`), it works on 64-bit systems (pointer fits in int64_t) but GC might not trace it if it interprets the value as a non-pointer integer during conservative scan. In practice, Boehm GC conservatively scans all words that look like pointers — so the value WILL be traced.
**Why it happens:** `LangCons.head` is `int64_t`, not `void*`. Conservative GC traces all int64-aligned words as potential pointers.
**How to avoid:** Store as `(int64_t)(uintptr_t)ptr` — same pattern as `lang_for_in_hashtable`. This is correct and GC-safe.

## Code Examples

### C: lang_hashtable_set_str

```c
// Source: lang_runtime.c — string-key hashtable set
void lang_hashtable_set_str(LangHashtableStr* ht, LangString* key, int64_t val) {
    if (ht->size * 4 > ht->capacity * 3) {
        lang_ht_str_rehash(ht, ht->capacity * 2);
    }
    LangHashEntryStr* e = lang_ht_str_find(ht, key);
    if (e != NULL) {
        e->val = val;
        return;
    }
    uint64_t bucket = lang_ht_str_hash(key) % (uint64_t)ht->capacity;
    LangHashEntryStr* entry = (LangHashEntryStr*)GC_malloc(sizeof(LangHashEntryStr));
    entry->key = key;
    entry->val = val;
    entry->next = ht->buckets[bucket];
    ht->buckets[bucket] = entry;
    ht->size++;
}
```

### C: lang_index_get_str / lang_index_set_str

```c
// Source: lang_runtime.c — string-index dispatch
int64_t lang_index_get_str(void* collection, LangString* key) {
    // collection is always LangHashtableStr* when key is string (tag = -2)
    return lang_hashtable_get_str((LangHashtableStr*)collection, key);
}

void lang_index_set_str(void* collection, LangString* key, int64_t value) {
    lang_hashtable_set_str((LangHashtableStr*)collection, key, value);
}
```

### Elaboration.fs: hashtable_set with key dispatch

```fsharp
// Source: Elaboration.fs — key-type-aware hashtable_set
| App (App (App (Var ("hashtable_set", _), htExpr, _), keyExpr, _), valExpr, _) ->
    let (htVal,  htOps)  = elaborateExpr env htExpr
    let (keyVal, keyOps) = elaborateExpr env keyExpr
    let (valVal, valOps) = elaborateExpr env valExpr
    let (htPtr, htCoerce) = coerceToPtrArg env htVal
    let (valI64, valCoerce) =
        match valVal.Type with
        | I64 -> (valVal, [])
        | I1  -> let v = { Name = freshName env; Type = I64 } in (v, [ArithExtuIOp(v, valVal)])
        | Ptr -> let v = { Name = freshName env; Type = I64 } in (v, [LlvmPtrToIntOp(v, valVal)])
        | _   -> (valVal, [])
    let unitVal = { Name = freshName env; Type = I64 }
    match keyVal.Type with
    | Ptr ->
        // String key: call _str variant, pass key as Ptr directly
        let ops = htCoerce @ valCoerce @ [LlvmCallVoidOp("@lang_hashtable_set_str", [htPtr; keyVal; valI64]); ArithConstantOp(unitVal, 0L)]
        (unitVal, htOps @ keyOps @ valOps @ ops)
    | _ ->
        // Integer key: original path with I64 coercion
        let (keyI64, keyCoerce) =
            match keyVal.Type with
            | I64 -> (keyVal, [])
            | I1  -> let v = { Name = freshName env; Type = I64 } in (v, [ArithExtuIOp(v, keyVal)])
            | Ptr -> let v = { Name = freshName env; Type = I64 } in (v, [LlvmPtrToIntOp(v, keyVal)])
            | _   -> (keyVal, [])
        let ops = htCoerce @ keyCoerce @ valCoerce @ [LlvmCallVoidOp("@lang_hashtable_set", [htPtr; keyI64; valI64]); ArithConstantOp(unitVal, 0L)]
        (unitVal, htOps @ keyOps @ valOps @ ops)
```

### ExternalFuncDecl entries (new)

```fsharp
// Source: Elaboration.fs — both externalFuncs lists
{ ExtName = "@lang_hashtable_create_str";      ExtParams = [];           ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_hashtable_get_str";         ExtParams = [Ptr; Ptr];   ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_hashtable_set_str";         ExtParams = [Ptr; Ptr; I64]; ExtReturn = None;  IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_hashtable_containsKey_str"; ExtParams = [Ptr; Ptr];   ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_hashtable_remove_str";      ExtParams = [Ptr; Ptr];   ExtReturn = None;     IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_hashtable_keys_str";        ExtParams = [Ptr];        ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_hashtable_trygetvalue_str"; ExtParams = [Ptr; Ptr];   ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_index_get_str";             ExtParams = [Ptr; Ptr];   ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_index_set_str";             ExtParams = [Ptr; Ptr; I64]; ExtReturn = None;  IsVarArg = false; Attrs = [] }
```

### Test pattern (.flt)

```
// --- Command: bash -c 'OUTBIN=$(mktemp /tmp/langback_XXXXXX) && dotnet run --project %S/../../src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj -- %input -o $OUTBIN && $OUTBIN; echo $?; rm -f $OUTBIN'
// --- Input:
let ht = hashtable_create_str ()
let _ = hashtable_set ht "hello" 42
let _ = println (to_string (hashtable_get ht "hello"))
let _ = println (to_string (hashtable_containsKey ht "hello"))
let _ = println (to_string (hashtable_containsKey ht "world"))
let _ = hashtable_remove ht "hello"
let _ = println (to_string (hashtable_containsKey ht "hello"))
// --- Output:
42
1
0
0
0
```

**Key test: string identity vs content equality**

```
let s1 = "hel" ^ "lo"   // different allocation if concat allocates
let ht = hashtable_create_str ()
let _ = hashtable_set ht "hello" 99
let _ = println (to_string (hashtable_get ht s1))
// --- Output:
99
0
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `int64_t key` (pointer identity) | `LangString* key` with content hash/eq | Phase 37 | String keys work correctly across allocations |
| Single `hashtable_create` | `hashtable_create` (int) + `hashtable_create_str` (str) | Phase 37 | Users must choose at creation time |

**Deprecated/outdated:**
- Using `hashtable_create ()` with string keys: Was silently broken (pointer-identity semantics). Phase 37 makes string keys work via explicit `hashtable_create_str ()`.

## Open Questions

1. **hashtable_count_str**
   - What we know: `hashtable_count` uses inline GEP+load at field index 2 (`size` field). `LangHashtableStr` has the same layout (`tag` at 0, `capacity` at 1, `size` at 2).
   - What's unclear: Whether the same `hashtable_count` builtin works for both struct types (it does — it's a raw GEP into the struct, layout-compatible).
   - Recommendation: Do NOT add `hashtable_count_str`. The existing `hashtable_count` arm in the elaborator uses GEP field index 2 which is the same in `LangHashtableStr`. It works unchanged.

2. **for-in over string hashtable**
   - What we know: `lang_for_in_hashtable` iterates `LangHashtable` (int keys). Phase 37 success criteria do NOT require for-in over string hashtables.
   - What's unclear: Whether a future phase will need it.
   - Recommendation: Skip for Phase 37. If needed, add `lang_for_in_hashtable_str` + a new `CollectionKind` variant later.

3. **Prelude/Hashtable.fun update**
   - What we know: `Prelude/Hashtable.fun` exposes `create ()` which calls `hashtable_create ()` (int variant).
   - What's unclear: Should `Prelude/Hashtable.fun` add `createStr ()` or should `create ()` always produce a string-capable hashtable?
   - Recommendation: Add `createStr ()` to `Prelude/Hashtable.fun`. The existing `create ()` remains for integer keys. The success criteria test uses `ht.["hello"] <- 42` which goes through `IndexSet` — for this to work, the hashtable must have been created with `hashtable_create_str ()`.

4. **hashtable_create auto-dispatch at IndexSet**
   - What we know: `ht.["hello"] <- 42` elaborates to `IndexSet (ht, "hello", 42)`. At this point, `ht` is already a `Ptr` value — the compiler doesn't know if it was `create` or `create_str`.
   - What's unclear: How to enforce that `hashtable_create_str` was called before string-key operations.
   - Recommendation: Runtime safety only — `lang_hashtable_set_str` receives a `LangHashtableStr*`. If the user passed a `LangHashtable*` (tag = -1), the bucket array is `LangHashEntry**`, not `LangHashEntryStr**` — the first dereference will corrupt memory. Accept this as a user-error condition (wrong create used). Document in Prelude/Hashtable.fun.

## Sources

### Primary (HIGH confidence)

- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/lang_runtime.c` — Complete hashtable implementation, struct layouts, hashing, for-in
- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/lang_runtime.h` — All struct and function declarations
- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/Elaboration.fs` — All hashtable builtin elaboration arms and ExternalFuncDecl lists
- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/MlirIR.fs` — Available MlirOp cases
- `/Users/ohama/vibe-coding/FunLangCompiler/.planning/milestones/v9.0-phases/35-prelude-modules/35-01-SUMMARY.md` — Documents the original crash and root cause
- `/Users/ohama/vibe-coding/FunLangCompiler/.planning/STATE.md` — Confirms RT-01/RT-02 still open

### Secondary (MEDIUM confidence)

- FNV-1a hash algorithm: well-established, standard byte-string hashing, no external source needed
- Boehm GC conservative scanning behavior: documented in GC manual — all GC_malloc'd words are scanned as potential pointers

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — no new libraries, all changes internal to existing C runtime and F# elaborator
- Architecture: HIGH — direct analysis of existing struct layouts, function signatures, and elaboration patterns
- Pitfalls: HIGH — derived from actual crash described in Phase 35-01-SUMMARY.md and analysis of current code

**Research date:** 2026-03-30
**Valid until:** 2026-04-30 (stable codebase, low churn)
