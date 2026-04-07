# Phase 90: Hashtable Unification and Compatibility - Research

**Researched:** 2026-04-07
**Domain:** C runtime hashtable unification, LLVM IR codegen, interpreter compatibility
**Confidence:** HIGH

## Summary

Phase 90 unifies two separate C hashtable implementations (LangHashtable for int keys, LangHashtableStr for string keys) into a single implementation that uses LSB dispatch: tagged ints have LSB=1, heap pointers (strings) have LSB=0. This eliminates 7 `*_str` function variants and their corresponding compiler dispatch logic.

The current architecture has compile-time dispatch in Elaboration.fs: `keyVal.Type = Ptr` routes to `_str` functions, otherwise to int functions. After unification, a single set of C functions accepts `int64_t key` and checks `key & 1` at runtime to select the appropriate hash/equality function. The compiler no longer needs compile-time dispatch or untagging of int keys before hashtable calls.

The interpreter (deps/FunLang) needs no functional changes since its `hashtable_create` etc. are already polymorphic (.NET Dictionary handles any key type). It just needs to also recognize the `*_str` builtin names as aliases for COMPAT-01.

**Primary recommendation:** Unify to ONE C struct with `int64_t key` storing tagged ints and raw pointers. ONE set of 7 C functions with `key & 1` dispatch. Compiler passes keys as-is (no untag for int keys). Remove all `_str` patterns from Elaboration.fs.

## Current State Analysis

### C Runtime: Two Separate Implementations

**LangHashtable (int-key)** — `lang_runtime.h:34-45`
```c
typedef struct LangHashEntry {
    int64_t key;             // stored UNTAGGED (compiler untags before set)
    int64_t val;
    struct LangHashEntry* next;
} LangHashEntry;

typedef struct {
    int64_t tag;        // -1
    int64_t capacity;
    int64_t size;
    LangHashEntry** buckets;
} LangHashtable;
```
- Hash: Murmurhash3 finalizer on raw int64
- Equality: `e->key == key`
- Tag: `-1`

**LangHashtableStr (string-key)** — `lang_runtime.h:58-69`
```c
typedef struct LangHashEntryStr {
    struct LangString_s* key;   // pointer to LangString
    int64_t val;
    struct LangHashEntryStr* next;
} LangHashEntryStr;

typedef struct {
    int64_t tag;        // -2
    int64_t capacity;
    int64_t size;
    LangHashEntryStr** buckets;
} LangHashtableStr;
```
- Hash: FNV-1a over string bytes
- Equality: length + memcmp
- Tag: `-2`

### C Functions (14 total, 7 pairs)

| Function | Int variant | Str variant |
|----------|------------|-------------|
| create | `lang_hashtable_create()` | `lang_hashtable_create_str()` |
| get | `lang_hashtable_get(ht, key)` | `lang_hashtable_get_str(ht, key)` |
| set | `lang_hashtable_set(ht, key, val)` | `lang_hashtable_set_str(ht, key, val)` |
| containsKey | `lang_hashtable_containsKey(ht, key)` | `lang_hashtable_containsKey_str(ht, key)` |
| keys | `lang_hashtable_keys(ht)` | `lang_hashtable_keys_str(ht)` |
| remove | `lang_hashtable_remove(ht, key)` | `lang_hashtable_remove_str(ht, key)` |
| trygetvalue | `lang_hashtable_trygetvalue(ht, key)` | `lang_hashtable_trygetvalue_str(ht, key)` |

### Compiler Dispatch (Elaboration.fs)

Current approach: compile-time dispatch on `keyVal.Type`:
- `keyVal.Type = Ptr` -> call `_str` variant
- `keyVal.Type = I64` -> untag key via `emitUntag`, call int variant
- Explicit `*_str` builtin names (`hashtable_create_str`, etc.) also recognized separately
- `hashtable_create` and `hashtable_keys` use type inference (`THashtable(TString, _)`) for dispatch

Total hashtable-related patterns in Elaboration.fs: ~20 match arms (lines 1230-1510)

### LLVM External Declarations (ElabProgram.fs:87-100, 518-531)

14 external function declarations, duplicated in two places. After unification: 7 declarations.

### Prelude/Hashtable.fun (Compiler)

16 lines, 8 `*Str` functions + 8 base functions. After unification: 8 base functions only.

### Prelude/Hashtable.fun (Interpreter, deps/FunLang)

9 lines, NO `*Str` variants. Already uses polymorphic builtins. No change needed here.

### Key Storage Convention

**Current:** Int keys stored UNTAGGED. Compiler calls `emitUntag` before `hashtable_set/get/etc.` `for_in_hashtable` re-tags keys with `LANG_TAG_INT(e->key)`.

**After unification:** Keys stored AS-IS (tagged int = LSB 1, pointer = LSB 0). No untag/retag needed. The C function checks `key & 1` to decide hash/equality strategy.

## Architecture: Unified Hashtable

### Unified C Struct

```c
// One entry type — key is int64_t storing either tagged int or raw pointer
typedef struct LangHashEntry {
    int64_t key;           // tagged int (LSB=1) or pointer (LSB=0)
    int64_t val;
    struct LangHashEntry* next;
} LangHashEntry;

typedef struct {
    int64_t tag;           // -1 (single tag for all hashtables)
    int64_t capacity;
    int64_t size;
    LangHashEntry** buckets;
} LangHashtable;
```

### Unified Hash Function

```c
static uint64_t lang_ht_hash(int64_t key) {
    if (key & 1) {
        // Tagged int — murmurhash3 finalizer
        uint64_t h = (uint64_t)key;
        h ^= h >> 33;
        h *= UINT64_C(0xff51afd7ed558ccd);
        h ^= h >> 33;
        h *= UINT64_C(0xc4ceb9fe1a85ec53);
        h ^= h >> 33;
        return h;
    } else {
        // Pointer (string) — FNV-1a over string content
        LangString* s = (LangString*)(uintptr_t)key;
        uint64_t h = UINT64_C(14695981039346656037);
        for (int64_t i = 0; i < s->length; i++) {
            h ^= (uint8_t)s->data[i];
            h *= UINT64_C(1099511628211);
        }
        return h;
    }
}
```

### Unified Equality

```c
static int lang_ht_eq(int64_t a, int64_t b) {
    if (a & 1) {
        // Both tagged ints — direct comparison
        return a == b;
    } else {
        // Both pointers (strings) — content equality
        LangString* sa = (LangString*)(uintptr_t)a;
        LangString* sb = (LangString*)(uintptr_t)b;
        return sa->length == sb->length &&
               memcmp(sa->data, sb->data, (size_t)sa->length) == 0;
    }
}
```

### Unified Function Signatures

All 7 functions take `int64_t key` (not `LangString*`):

```c
LangHashtable* lang_hashtable_create(void);
int64_t lang_hashtable_get(LangHashtable* ht, int64_t key);
void lang_hashtable_set(LangHashtable* ht, int64_t key, int64_t val);
int64_t lang_hashtable_containsKey(LangHashtable* ht, int64_t key);
void lang_hashtable_remove(LangHashtable* ht, int64_t key);
LangCons* lang_hashtable_keys(LangHashtable* ht);
int64_t* lang_hashtable_trygetvalue(LangHashtable* ht, int64_t key);
```

### LLVM Declarations After Unification

All functions use `I64` for key parameter (not `Ptr`):

```fsharp
{ ExtName = "@lang_hashtable_create";      ExtParams = [];              ExtReturn = Some Ptr }
{ ExtName = "@lang_hashtable_get";         ExtParams = [Ptr; I64];     ExtReturn = Some I64 }
{ ExtName = "@lang_hashtable_set";         ExtParams = [Ptr; I64; I64]; ExtReturn = None }
{ ExtName = "@lang_hashtable_containsKey"; ExtParams = [Ptr; I64];     ExtReturn = Some I64 }
{ ExtName = "@lang_hashtable_remove";      ExtParams = [Ptr; I64];     ExtReturn = None }
{ ExtName = "@lang_hashtable_keys";        ExtParams = [Ptr];          ExtReturn = Some Ptr }
{ ExtName = "@lang_hashtable_trygetvalue"; ExtParams = [Ptr; I64];     ExtReturn = Some Ptr }
```

Key insight: string keys (Ptr type in LLVM) need `PtrToInt` coercion to pass as `I64` to the unified C function.

## Detailed Change Plan

### 1. C Runtime Changes (lang_runtime.c + lang_runtime.h)

**Remove:**
- `LangHashEntryStr` struct
- `LangHashtableStr` struct
- All `_str` functions (7 functions)
- `lang_ht_str_hash`, `lang_ht_str_find`, `lang_ht_str_rehash` statics

**Modify:**
- `lang_ht_hash` — add LSB dispatch (int vs string)
- `lang_ht_find` — use `lang_ht_eq` instead of `==`
- `lang_hashtable_set` — use `lang_ht_eq` in find, no change to key storage
- `lang_hashtable_remove` — use `lang_ht_eq` in chain walk
- `lang_hashtable_keys` — key already stored as int64_t, no change needed
- `lang_for_in_hashtable` — remove `LANG_TAG_INT(e->key)`, key is already tagged
- `lang_hashtable_create` — tag stays `-1`

**Also modify:**
- `lang_index_get_str` — currently calls `lang_hashtable_get_str`, redirect to `lang_hashtable_get` with `(int64_t)(uintptr_t)key`
- `lang_index_set_str` — same, redirect to unified `lang_hashtable_set`

### 2. Compiler Elaboration.fs Changes

**For each of the 7 operations**, replace dual match arms with single:
- No more `keyVal.Type` dispatch
- No more `emitUntag` on int keys (pass tagged as-is)
- String keys: `PtrToInt` coercion to I64 before call
- Remove all explicit `*_str` builtin pattern matches

**Example — unified `hashtable_set`:**
```fsharp
| App (App (App (Var ("hashtable_set", _), htExpr, _), keyExpr, _), valExpr, _) ->
    let (htVal, htOps) = elaborateExpr env htExpr
    let (keyVal, keyOps) = elaborateExpr env keyExpr
    let (valVal, valOps) = elaborateExpr env valExpr
    let (htPtr, htCoerce) = coerceToPtrArg env htVal
    // Coerce key to I64 (tagged int already I64, string Ptr→PtrToInt)
    let (keyI64, keyCoerce) = coerceToI64 env keyVal
    let (valI64, valCoerce) = coerceToI64 env valVal
    let (unitVal, callOps) = emitVoidCall env "@lang_hashtable_set" [htPtr; keyI64; valI64]
    (unitVal, htOps @ keyOps @ valOps @ htCoerce @ keyCoerce @ valCoerce @ callOps)
```

**Operations affected:**
1. `hashtable_set` — remove str dispatch, remove untag
2. `hashtable_get` — remove str dispatch, remove untag
3. `hashtable_containsKey` — remove str dispatch, remove untag
4. `hashtable_remove` — remove str dispatch, remove untag
5. `hashtable_keys` — remove type-inference dispatch, no key handling needed
6. `hashtable_create` — remove type-inference dispatch, single function
7. `hashtable_trygetvalue` — remove str dispatch, remove untag

**Also remove explicit `*_str` patterns (lines 1391-1475):**
- `hashtable_keys_str` pattern
- `hashtable_create_str` pattern
- `hashtable_set_str` pattern
- `hashtable_get_str` pattern
- `hashtable_containsKey_str` pattern
- `hashtable_remove_str` pattern
- `hashtable_trygetvalue_str` pattern

**IndexGet/IndexSet:** These use `lang_index_get`/`lang_index_set` and `lang_index_get_str`/`lang_index_set_str`. These can ALSO be unified but may be kept separate since they serve array+hashtable dispatch. Analysis: `lang_index_get_str` directly calls `lang_hashtable_get_str`. After unification, it should call `lang_hashtable_get` with `(int64_t)key`. The compiler dispatch in IndexGet/IndexSet can stay as-is (dispatches on Ptr vs I64 type) since array indexing still needs raw int index.

### 3. ElabProgram.fs Changes

Remove 7 `_str` external declarations (in BOTH declaration blocks, lines 94-100 and 525-531).

### 4. Builtin Name List

Remove from builtin names list (line 2330-2335):
- `hashtable_create_str`
- `hashtable_get_str`
- `hashtable_set_str`
- `hashtable_containsKey_str`
- `hashtable_keys_str`
- `hashtable_remove_str`
- `hashtable_trygetvalue_str`

### 5. Prelude/Hashtable.fun (Compiler)

Remove all `*Str` functions. Final version:
```
module Hashtable =
    let create ()           = hashtable_create ()
    let get ht key          = hashtable_get ht key
    let set ht key value    = hashtable_set ht key value
    let containsKey ht key  = hashtable_containsKey ht key
    let keys ht             = hashtable_keys ht
    let remove ht key       = hashtable_remove ht key
    let tryGetValue ht key  = hashtable_trygetvalue ht key
    let count ht            = hashtable_count ht
```

### 6. Interpreter Compatibility (COMPAT-01)

The FunLang interpreter (deps/FunLang) already has polymorphic builtins. But Prelude/Hashtable.fun (compiler version) currently calls `hashtable_create_str` etc. After unification, those names go away.

For COMPAT-01, the interpreter should recognize the base names (`hashtable_create`, etc.) for BOTH int and string keys. It already does — its builtins are polymorphic. The `_str` variants are also registered (Eval.fs:723-778) but are functionally identical to base variants.

**No interpreter changes needed** if Prelude/Hashtable.fun only uses base names. The `_str` builtins can stay in the interpreter for backward compatibility with any code using the old names directly.

### 7. hashtable_count — No Change

`hashtable_count` does inline GEP at field index 2 (size). The unified struct has the same layout: `[tag, capacity, size, buckets]`. No change needed.

### 8. for_in_hashtable — Simplifies

Current: `tup[0] = LANG_TAG_INT(e->key)` (retags untagged key)
After: `tup[0] = e->key` (key already tagged/raw pointer, passed as-is)

### 9. detectCollectionKind — Minor Update

Remove `hashtable_create_str` pattern (ElabHelpers.fs:138). Only `hashtable_create` needed.

## Common Pitfalls

### Pitfall 1: Key Coercion Direction
**What goes wrong:** Forgetting to PtrToInt string keys before passing to unified C function
**Why it happens:** Old code passed Ptr directly to `_str` functions. New unified functions take I64.
**How to avoid:** Every key must be coerced to I64 before call. Int keys are already I64 (tagged). String keys need PtrToInt.

### Pitfall 2: Removing Untag for Int Keys
**What goes wrong:** Keeping `emitUntag` on int keys, causing double-shift corruption
**Why it happens:** Old code untagged int keys before passing to C (C stored raw ints). New code must NOT untag.
**How to avoid:** Search for all `emitUntag` calls in hashtable paths and remove them.

### Pitfall 3: for_in_hashtable Retag
**What goes wrong:** Keeping `LANG_TAG_INT(e->key)` which double-tags already-tagged keys
**Why it happens:** Old code stored untagged keys and retagged on iteration. New code stores tagged keys.
**How to avoid:** Change to `tup[0] = e->key` (pass through as-is).

### Pitfall 4: lang_index_get/set Still Need Str Variants
**What goes wrong:** Removing `lang_index_get_str`/`lang_index_set_str` entirely
**Why it happens:** These serve `ht.["key"]` syntax where the compiler dispatches on Ptr type
**How to avoid:** Keep `lang_index_get_str`/`lang_index_set_str` but redirect internally to unified functions. OR unify these too by having the compiler PtrToInt the string key and call `lang_index_get`.

### Pitfall 5: containsKey Return Value
**What goes wrong:** Forgetting that `lang_hashtable_containsKey` returns 0/1 (raw), and Elaboration.fs compares with zero to produce I1
**Why it happens:** Return value convention doesn't change but might be overlooked during refactor
**How to avoid:** Keep the `ArithCmpIOp(boolVal, "ne", rawVal, zeroVal)` post-processing.

### Pitfall 6: NULL Pointer Keys
**What goes wrong:** A NULL pointer (0) has LSB=0, same as string pointers
**Why it happens:** Unit value `()` is represented as 0 in the compiler
**How to avoid:** This is not a real concern — hashtable keys are always int or string in practice. Document the assumption.

## Test Impact

### Existing Tests to Verify (COMPAT-02)
- `32-01-hashtable-trygetvalue.flt` — int-key trygetvalue
- `32-02-hashtable-count.flt` — count
- `35-02-hashtable-module.flt` — Prelude module
- `37-01-hashtable-string-keys.flt` — string keys
- `37-02-hashtable-string-content-equality.flt` — string equality
- `66-09-int-hashtablestr-prelude.flt` — Prelude Str variants (will need update to use non-Str names)

### Test 66-09 Update Required
This test uses `Hashtable.createStr`, `Hashtable.setStr`, etc. After removing `*Str` from Prelude, update to use `Hashtable.create`, `Hashtable.set`, etc.

### Full Test Suite
All 257+ compiler tests must pass. Run: `dotnet run --project deps/fslit/FsLit/FsLit.fsproj -- tests/compiler/`

## Execution Order

Recommended implementation sequence:

1. **C runtime unification** — Merge structs, implement LSB dispatch hash/eq, unify 7 functions, update `for_in_hashtable`, update `lang_index_get_str`/`lang_index_set_str`
2. **Header update** — Remove `LangHashtableStr`, `LangHashEntryStr`, `_str` declarations
3. **ElabProgram.fs** — Remove `_str` external declarations (both blocks)
4. **Elaboration.fs** — Simplify all 7 hashtable patterns, remove `_str` patterns, remove untag calls
5. **ElabHelpers.fs** — Remove `hashtable_create_str` from `detectCollectionKind`
6. **Builtin names list** — Remove `_str` names
7. **Prelude/Hashtable.fun** — Remove `*Str` functions
8. **Test 66-09** — Update to use non-Str names
9. **Full test run** — Verify all 257+ tests pass

## Open Questions

1. **IndexGet/IndexSet unification** — Should `lang_index_get_str`/`lang_index_set_str` be kept as thin wrappers, or should the compiler be changed to PtrToInt and call `lang_index_get`/`lang_index_set`? Recommendation: Keep as wrappers for now (simpler, less risk to IndexGet/IndexSet which handle arrays too).

2. **Mixed-key hashtables** — After unification, nothing prevents storing both int and string keys in the same hashtable. This is correct behavior (the runtime handles it) but was previously prevented by having separate types. This is actually a feature, not a bug.

## Sources

### Primary (HIGH confidence)
- `src/FunLangCompiler.Compiler/lang_runtime.h` — struct definitions, function signatures
- `src/FunLangCompiler.Compiler/lang_runtime.c` — full C implementation (lines 439-840)
- `src/FunLangCompiler.Compiler/Elaboration.fs` — compiler dispatch (lines 1230-1510)
- `src/FunLangCompiler.Compiler/ElabProgram.fs` — LLVM declarations (lines 87-100, 518-531)
- `src/FunLangCompiler.Compiler/ElabHelpers.fs` — detectCollectionKind (lines 125-141)
- `Prelude/Hashtable.fun` — compiler Prelude
- `deps/FunLang/Prelude/Hashtable.fun` — interpreter Prelude
- `deps/FunLang/src/FunLang/Eval.fs` — interpreter builtins (lines 656-778)

## Metadata

**Confidence breakdown:**
- C runtime unification: HIGH — full source read, struct layouts verified
- Compiler codegen changes: HIGH — all patterns identified and analyzed
- Interpreter compatibility: HIGH — verified polymorphic builtins, no changes needed
- Test impact: HIGH — all hashtable tests identified

**Research date:** 2026-04-07
**Valid until:** 2026-05-07 (stable codebase, no external dependencies)
