# Technology Stack: LangBackend v5.0 — Mutable Variables, Array, Hashtable

**Project:** LangBackend — LangThree MLIR → LLVM → native binary compiler
**Milestone:** v5.0 — LetMut/Assign (mutable variables), Array, Hashtable
**Researched:** 2026-03-27
**Confidence:** HIGH for all three features — patterns are direct extensions of existing SetField
(mutable record fields) and the Range/lang_range (C runtime builtins) already established.

---

## Scope of This Document

This document covers ONLY stack additions and changes required for v5.0. The existing v4.0 stack
(F# / .NET 10, LLVM 20 / MLIR 20, func/arith/cf/llvm dialects, Boehm GC 8.2.12, lang_runtime.c,
67 passing E2E tests) remains unchanged. Each section states precisely what changes and why.

---

## What v5.0 Adds to the Stack

### Summary Table

| Category | What Changes | Why |
|----------|-------------|-----|
| Runtime library (`lang_runtime.c`) | Add `lang_array_create`, `lang_array_get`, `lang_array_set`, `lang_array_length`; add `lang_hashtable_create`, `lang_hashtable_get`, `lang_hashtable_set`, `lang_hashtable_contains_key`, `lang_hashtable_remove`, `lang_hashtable_keys` | Array and Hashtable are heap objects managed by C runtime; cannot be emitted purely from MLIR ops |
| Runtime library header (`lang_runtime.h`) | Add typedefs for `LangArray` and forward declarations for all new runtime functions | Needed by C compiler when compiling lang_runtime.c itself |
| MlirIR.fs — `MlirOp` | No new variants needed | LetMut uses `LlvmCallOp` + `LlvmGEPLinearOp` + `LlvmStoreOp` + `LlvmLoadOp` (all existing); Array and Hashtable operations route through `LlvmCallOp` and `LlvmCallVoidOp` to C runtime functions |
| MlirIR.fs — `MlirType` | No new variants needed | Ref cells, arrays, and hashtables are all `Ptr` (opaque heap pointers) in the uniform boxed representation |
| Elaboration.fs | Add arms for `LetMut`, `Assign`, `LetMutDecl`; add builtin dispatch arms for `array_create`, `array_get`, `array_set`, `array_length`, `array_of_list`, `array_to_list`, `array_iter`, `array_map`, `array_fold`, `array_init`, `hashtable_create`, `hashtable_get`, `hashtable_set`, `hashtable_contains_key`, `hashtable_remove`, `hashtable_keys` | New AST nodes and new builtin function names need codegen dispatch |
| Elaboration.fs — `freeVars` | No changes needed | `LetMut` and `Assign` already present in the `_ -> Set.empty` fallthrough; `LetMutDecl` is a module-level declaration handled in `elaborateProgram` not `freeVars` |
| Pipeline.fs | No changes | lang_runtime.c already compiled and linked; new C functions go into the same file |
| Test infrastructure | No new framework; extend E2E tests | 67 existing tests continue to pass; new `.flt` files added |

---

## 1. LetMut / Assign: Mutable Variable Ref Cells

### 1.1 Semantic Model and Runtime Representation

The LangThree evaluator represents `LetMut` by allocating an F# `ref` cell and storing a `RefValue`
in the environment. When a `Var` lookup finds a `RefValue`, it transparently dereferences it.
`Assign` writes through the ref cell and returns unit.

**Compiled representation:** Every mutable variable is a GC-managed ref cell — a 1-word heap block
containing the current value:

```
GC_malloc(8) → ptr to one 8-byte word
```

Why 8 bytes (one word), not a typed struct: the uniform boxed representation stores all values as
either `i64` (scalars) or `ptr` (heap objects). A ref cell is a 1-slot array of words. Reading the
variable is a `LlvmLoadOp` from the cell pointer; writing is a `LlvmStoreOp` to the cell pointer.

### 1.2 LetMut Codegen

`LetMut("x", initExpr, bodyExpr, span)` compiles to:

```mlir
; elaborate initExpr → %init : i64 (or !llvm.ptr for heap-typed inits)
%cellSz = arith.constant 8 : i64
%cell   = llvm.call @GC_malloc(%cellSz) : (i64) -> !llvm.ptr
llvm.store %init, %cell : i64, !llvm.ptr   ; (or !llvm.ptr, !llvm.ptr)
; bind "x" → %cell in env (Ptr type — the cell pointer, NOT the contained value)
; elaborate bodyExpr in env' where Vars["x"] = { Name = %cell; Type = Ptr }
```

Variable reads (`Var("x")`) in `bodyExpr` must dereference the cell:

```mlir
%x_val = llvm.load %cell : !llvm.ptr -> i64   ; (or !llvm.ptr for heap-typed vars)
```

**Key distinction from immutable Let:** In an immutable `Let`, `Vars["x"]` holds the value itself
(e.g., `{ Name = "%t3"; Type = I64 }`). In `LetMut`, `Vars["x"]` holds the cell pointer
(`{ Name = "%cell"; Type = Ptr }`), and every `Var("x")` lookup must emit a `LlvmLoadOp`.

**How to distinguish in elaborateExpr `Var` arm:** The existing `Var(name, _)` arm looks up
`env.Vars` and returns the stored value directly. With ref cells, a Ptr-typed entry whose name
came from a LetMut cell would be emitted with an extra load. **The cleanest approach is to track
mutable variable names in a new `MutVars: Set<string>` field in `ElabEnv`**, and emit the load
only for names in `MutVars`. This is analogous to how `ExnTags` tracks exception constructor names.

```fsharp
type ElabEnv = {
    // ... existing fields ...
    MutVars: Set<string>   // NEW: names bound by LetMut/LetMutDecl (need deref on Var lookup)
}
```

When elaborating `Var(name, _)`:

```fsharp
| Var(name, _) ->
    match Map.tryFind name env.Vars with
    | Some cellPtr when Set.contains name env.MutVars ->
        // Mutable variable: deref the cell
        let valType = I64  // default; use Ptr if context requires (see §1.5)
        let result = { Name = freshName env; Type = valType }
        (result, [LlvmLoadOp(result, cellPtr)])
    | Some v -> (v, [])
    | None -> failwithf "Undefined variable: %s" name
```

### 1.3 Assign Codegen

`Assign("x", newValExpr, span)` compiles to:

```mlir
; elaborate newValExpr → %newVal : i64 (or !llvm.ptr)
; look up Vars["x"] → %cell : !llvm.ptr  (must be in MutVars)
llvm.store %newVal, %cell : i64, !llvm.ptr
; result = unit value → arith.constant 0 : i64
```

```fsharp
| Assign(name, valueExpr, _) ->
    let (newVal, valOps) = elaborateExpr env valueExpr
    let cellPtr =
        match Map.tryFind name env.Vars with
        | Some v when Set.contains name env.MutVars -> v
        | _ -> failwithf "Assign: '%s' is not a mutable variable" name
    let unitVal = { Name = freshName env; Type = I64 }
    let ops = valOps @ [
        LlvmStoreOp(newVal, cellPtr)
        ArithConstantOp(unitVal, 0L)
    ]
    (unitVal, ops)
```

### 1.4 LetMutDecl Codegen (Module Level)

`LetMutDecl(name, initExpr, span)` is the module-level form. The evaluator handles it in
`elaborateProgram`'s declaration fold. Codegen is identical to `LetMut` for the init allocation,
but the cell pointer is added to the module-level environment rather than a local block environment.

The existing `elaborateProgram` accumulates a `Map<string, MlirValue>` of top-level bindings for
use in `main`. Add `MutVars` tracking in that same fold:

```fsharp
| LetMutDecl(name, body, _) ->
    let (initVal, initOps) = elaborateExpr env body
    let cellSz   = { Name = freshName env; Type = I64 }
    let cellPtr  = { Name = freshName env; Type = Ptr }
    let storeOp  = LlvmStoreOp(initVal, cellPtr)
    let allOps   = initOps @ [
        ArithConstantOp(cellSz, 8L)
        LlvmCallOp(cellPtr, "@GC_malloc", [cellSz])
        storeOp
    ]
    // Emit allOps into current block
    let env' = { env with
                   Vars    = Map.add name cellPtr env.Vars
                   MutVars = Set.add name env.MutVars }
    (env', allOps)
```

### 1.5 Type of Mutable Variables

All mutable variables in LangThree are dynamically typed (the interpreter stores `Value ref`).
In the compiled backend, the type of the stored word in the cell is `i64` for integer/bool/unit
values and `!llvm.ptr` for heap-allocated values (strings, tuples, lists, arrays, hashtables, ADTs,
records). At codegen time the type is determined from the initial value expression's type.

**Recommendation:** Use the elaborated type of the init expression to decide whether to store/load
as `I64` or `Ptr`. This mirrors how record fields work in `SetField` and `FieldAccess`.

### 1.6 GC Implications

A ref cell is a 1-word `GC_malloc` block. Boehm GC's conservative scan will find the cell
pointer in the local frame (it is a `!llvm.ptr` on the stack or in a register). If the cell
contains a pointer to another heap object, the GC will trace through the cell correctly since
Boehm is conservative — it treats any pointer-shaped word as a live reference. No special
GC registration or write barriers are needed.

**One risk:** if a mutable variable holding a `Ptr` is stored as `i64` (mistyped), the GC will
miss the pointer and may collect the object. Ensure mutable variables holding heap-typed values
always use `Ptr` as the stored type.

---

## 2. Array: Fixed-Size Mutable Heap Array

### 2.1 Runtime Layout

Every array is a GC-managed heap block with this layout:

```
offset  0 : i64          — element count (length)
offset  8 : ptr          — pointer to element data block (GC_malloc'd array of words)
total     : 16 bytes (array header)

Element data block:
offset  0 : word[0]      — first element (i64 or ptr, 8 bytes)
offset  8 : word[1]
...
offset n*8: word[n-1]
total     : length * 8 bytes
```

This matches the `LangString` header layout (`{i64 length, ptr data}`) which already exists in
`lang_runtime.c`. The element data is a separate GC_malloc'd block so that the header can be
updated if arrays were ever resized (they are not in this design, but consistency with strings is
valuable for code reuse).

**Alternative considered — flat layout `{length, elem0, elem1, ...}`:** This avoids one extra
allocation and one indirection per element access. However, it requires knowing the element count
at the malloc site, complicates GEP arithmetic (field 0 = length, field i+1 = element i), and
cannot be expressed cleanly with `LlvmGEPLinearOp` which uses a single linear index. The two-word
header layout is recommended because it keeps element-access arithmetic uniform: the element data
pointer is at a fixed offset (field 1 of the header), and element `i` is at `dataPtr[i]`.

### 2.2 C Runtime Functions for Array

Add to `lang_runtime.c`:

```c
typedef struct {
    int64_t  length;
    int64_t* data;    /* treated as word array; element may be i64 or ptr */
} LangArray;

/* lang_array_create(n, defVal) : allocate length-n array, fill with defVal.
   n must be >= 0. Returns !llvm.ptr to LangArray header. */
LangArray* lang_array_create(int64_t n, int64_t def_val);

/* lang_array_get(arr, i) : bounds-check, return arr->data[i].
   Raises (via lang_throw) on out-of-bounds. Returns i64 (element value). */
int64_t lang_array_get(LangArray* arr, int64_t i);

/* lang_array_set(arr, i, val) : bounds-check, arr->data[i] = val.
   Raises on out-of-bounds. Returns void (unit). */
void lang_array_set(LangArray* arr, int64_t i, int64_t val);

/* lang_array_length(arr) : return arr->length */
int64_t lang_array_length(LangArray* arr);
```

**Why C runtime for array operations, not inline MLIR ops:**
- Bounds checking requires conditional branches, error strings, and a call to `lang_throw`. This
  is complex to emit inline and would bloat Elaboration.fs significantly.
- Reuses the existing `lang_throw` exception propagation mechanism for out-of-bounds errors —
  consistent with how LangThree raises on OOB access.
- `lang_array_create` needs to zero-initialize or fill with a default value, which is a loop
  better expressed in C than as unrolled MLIR.

**C runtime function signatures (MLIR-side view):**

| Function | MLIR Param Types | MLIR Return Type |
|----------|-----------------|-----------------|
| `@lang_array_create` | `(i64, i64)` | `!llvm.ptr` |
| `@lang_array_get` | `(!llvm.ptr, i64)` | `i64` |
| `@lang_array_set` | `(!llvm.ptr, i64, i64)` | void |
| `@lang_array_length` | `(!llvm.ptr)` | `i64` |

Note: element values are passed as `i64` at the ABI boundary (the uniform boxed representation).
Pointer-typed elements (strings, tuples, etc.) are passed via `ptrtoint` at the call site, and
`inttoptr` after `lang_array_get`. This is the same coercion already used in closure/ADT codegen.

### 2.3 Builtin Dispatch in Elaboration.fs

The LangThree evaluator exposes these builtins: `array_create`, `array_get`, `array_set`,
`array_length`, `array_of_list`, `array_to_list`, `array_iter`, `array_map`, `array_fold`,
`array_init`. The compiler maps each curried F# builtin application to a C runtime call.

**Pattern:** Each builtin is matched as a fully-applied `App` expression before the general `App`
arm, exactly as `string_concat`, `string_sub`, `lang_range` are matched today.

```fsharp
// array_create : int -> 'a -> 'a array
| App(App(Var("array_create", _), nExpr, _), defExpr, _) ->
    let (nVal, nOps)    = elaborateExpr env nExpr
    let (defVal, dOps)  = elaborateExpr env defExpr
    // Coerce defVal to i64 if it is Ptr (uniform ABI)
    let (i64Def, coerceOps) = coerceToI64 env defVal
    let result = { Name = freshName env; Type = Ptr }
    (result, nOps @ dOps @ coerceOps @ [LlvmCallOp(result, "@lang_array_create", [nVal; i64Def])])

// array_get : 'a array -> int -> 'a
| App(App(Var("array_get", _), arrExpr, _), idxExpr, _) ->
    let (arrVal, aOps) = elaborateExpr env arrExpr
    let (idxVal, iOps) = elaborateExpr env idxExpr
    let result = { Name = freshName env; Type = I64 }
    (result, aOps @ iOps @ [LlvmCallOp(result, "@lang_array_get", [arrVal; idxVal])])

// array_set : 'a array -> int -> 'a -> unit
| App(App(App(Var("array_set", _), arrExpr, _), idxExpr, _), valExpr, _) ->
    let (arrVal, aOps) = elaborateExpr env arrExpr
    let (idxVal, iOps) = elaborateExpr env idxExpr
    let (newVal, vOps) = elaborateExpr env valExpr
    let (i64New, coerceOps) = coerceToI64 env newVal
    let unitVal = { Name = freshName env; Type = I64 }
    (unitVal, aOps @ iOps @ vOps @ coerceOps @
              [LlvmCallVoidOp("@lang_array_set", [arrVal; idxVal; i64New])
               ArithConstantOp(unitVal, 0L)])

// array_length : 'a array -> int
| App(Var("array_length", _), arrExpr, _) ->
    let (arrVal, aOps) = elaborateExpr env arrExpr
    let result = { Name = freshName env; Type = I64 }
    (result, aOps @ [LlvmCallOp(result, "@lang_array_length", [arrVal])])
```

**Higher-order array builtins (`array_iter`, `array_map`, `array_fold`, `array_init`):**
These require looping over array elements and calling a user closure for each element. This cannot
be expressed as a single C runtime call (because the callback is a compiled closure, not a C
function pointer). These are deferred to a later phase or expressed as loop constructs in MLIR.

See §2.5 for the recommended strategy.

### 2.4 array_of_list and array_to_list

These two builtins bridge between List (cons-cell linked list) and Array.

```c
/* lang_array_of_list: traverse cons list, allocate array */
LangArray* lang_array_of_list(void* cons_list);   /* cons_list is ptr or null */

/* lang_array_to_list: traverse array, build cons list */
void* lang_array_to_list(LangArray* arr);          /* returns ptr (head cons cell) or null */
```

Both are C runtime functions. `lang_array_of_list` must traverse the cons list to count elements
first (or use a growable buffer), then allocate. Both traverse structures the C runtime already
understands (cons cells use the existing 2-word layout).

### 2.5 Higher-Order Array Builtins (array_iter, array_map, array_fold, array_init)

These require invoking user-defined closures per element. Two options:

**Option A (recommended for v5.0):** Implement as C runtime functions that accept a closure
pointer and call it via the existing indirect-call ABI. This requires the C runtime to understand
the closure calling convention (`fn_ptr(arg, env_ptr)` → `i64`). This is a significant coupling
between the C runtime and the MLIR closure ABI.

**Option B (deferred):** Implement as loop constructs in MLIR directly — emit a loop with a
counter, load each element, emit an indirect call, store the result. This keeps all calling
convention knowledge in Elaboration.fs, not the C runtime. However it requires either a loop
construct in the IR (which `cf.br`/`cf.cond_br` can express) or inline unrolling.

**Recommendation: Defer `array_iter`, `array_map`, `array_fold`, `array_init` to a follow-on
phase.** The core array primitives (`array_create`, `array_get`, `array_set`, `array_length`,
`array_of_list`, `array_to_list`) are sufficient for the most common array usage patterns. Higher-
order functions can be expressed by callers using explicit recursion in the language. Add them to
MLIR loop emission later.

### 2.6 GC Implications for Arrays

- The `LangArray` header (16 bytes) is `GC_malloc`'d. Boehm scans it and finds `data` as a pointer.
- The element data block is also `GC_malloc`'d. Boehm finds it via the `data` pointer in the header.
- Elements that are pointers (strings, tuples, etc.) are stored as `int64_t` words at the C level
  but contain pointer bit patterns. **Boehm's conservative scan will treat any word that looks like
  a heap address as a live reference**, so these are traced correctly without special annotation.
- No explicit GC registration needed. The existing `GC_malloc` calls in `lang_array_create` are
  sufficient.

---

## 3. Hashtable: Mutable Key-Value Store

### 3.1 Runtime Representation

The LangThree evaluator uses `.NET Dictionary<Value, Value>`. In the compiled backend, implement
the hashtable as a C-level open-addressing or chained hash table, fully managed by `lang_runtime.c`
and allocated via `GC_malloc`.

**Recommended layout — chained bucket array:**

```c
typedef struct LangHashEntry {
    int64_t              key;    /* key value (i64 or ptr-as-i64) */
    int64_t              val;    /* value (i64 or ptr-as-i64) */
    struct LangHashEntry* next;  /* next entry in chain (NULL = end) */
} LangHashEntry;

typedef struct {
    int64_t       capacity;    /* number of buckets */
    int64_t       size;        /* number of key-value pairs */
    LangHashEntry** buckets;   /* GC_malloc'd array of bucket pointers */
} LangHashtable;
```

All allocations via `GC_malloc`. No `free` calls — Boehm GC handles reclamation.

**Why chained buckets over open addressing:**
- Open addressing requires tombstones for deletion (`hashtable_remove`) which complicates
  the implementation. Chaining handles deletion cleanly by relinking.
- Boehm GC conservatively traces through all `LangHashEntry*` chains because they are heap pointers.
  With open addressing, deleted slots holding stale pointer-shaped values could cause false retention;
  chaining avoids this by nulling the pointer on removal.

**Why not reuse a C standard library hash table (e.g., `hsearch`, POSIX):**
- These do not use `GC_malloc` and cannot be reclaimed by Boehm GC automatically.
- No standard portable chaining hash table exists in libc.

### 3.2 C Runtime Functions for Hashtable

Add to `lang_runtime.c`:

```c
/* lang_hashtable_create() : allocate empty hashtable. Returns !llvm.ptr. */
LangHashtable* lang_hashtable_create(void);

/* lang_hashtable_get(ht, key) : look up key; lang_throw on miss. Returns i64 value. */
int64_t lang_hashtable_get(LangHashtable* ht, int64_t key);

/* lang_hashtable_set(ht, key, val) : insert or update. Returns void. */
void lang_hashtable_set(LangHashtable* ht, int64_t key, int64_t val);

/* lang_hashtable_contains_key(ht, key) : returns 1 if found, 0 if not. */
int64_t lang_hashtable_contains_key(LangHashtable* ht, int64_t key);

/* lang_hashtable_remove(ht, key) : remove key if present (no error if absent). Returns void. */
void lang_hashtable_remove(LangHashtable* ht, int64_t key);

/* lang_hashtable_keys(ht) : return cons list of all keys. Returns !llvm.ptr (cons list head). */
void* lang_hashtable_keys(LangHashtable* ht);
```

**C runtime function signatures (MLIR-side view):**

| Function | MLIR Param Types | MLIR Return Type |
|----------|-----------------|-----------------|
| `@lang_hashtable_create` | `()` | `!llvm.ptr` |
| `@lang_hashtable_get` | `(!llvm.ptr, i64)` | `i64` |
| `@lang_hashtable_set` | `(!llvm.ptr, i64, i64)` | void |
| `@lang_hashtable_contains_key` | `(!llvm.ptr, i64)` | `i64` (0 or 1) |
| `@lang_hashtable_remove` | `(!llvm.ptr, i64)` | void |
| `@lang_hashtable_keys` | `(!llvm.ptr)` | `!llvm.ptr` (cons list) |

Keys and values are `i64` at the ABI boundary (same coercion pattern as array elements).
`lang_hashtable_contains_key` returns `i64` (0/1) rather than `i1` to avoid I1/I64 extend
complications; the caller compares to 0 with `ArithCmpIOp("ne", ...)` to get `I1` for branches.

### 3.3 Builtin Dispatch in Elaboration.fs

```fsharp
// hashtable_create : unit -> hashtable
| App(Var("hashtable_create", _), _, _) ->   // argument is unit (ignored)
    let result = { Name = freshName env; Type = Ptr }
    (result, [LlvmCallOp(result, "@lang_hashtable_create", [])])

// hashtable_get : hashtable -> key -> value
| App(App(Var("hashtable_get", _), htExpr, _), keyExpr, _) ->
    let (htVal,  hOps) = elaborateExpr env htExpr
    let (keyVal, kOps) = elaborateExpr env keyExpr
    let (i64Key, coerceOps) = coerceToI64 env keyVal
    let result = { Name = freshName env; Type = I64 }
    (result, hOps @ kOps @ coerceOps @ [LlvmCallOp(result, "@lang_hashtable_get", [htVal; i64Key])])

// hashtable_set : hashtable -> key -> value -> unit
| App(App(App(Var("hashtable_set", _), htExpr, _), keyExpr, _), valExpr, _) ->
    let (htVal,  hOps) = elaborateExpr env htExpr
    let (keyVal, kOps) = elaborateExpr env keyExpr
    let (newVal, vOps) = elaborateExpr env valExpr
    let (i64Key, kcOps) = coerceToI64 env keyVal
    let (i64Val, vcOps) = coerceToI64 env newVal
    let unitVal = { Name = freshName env; Type = I64 }
    (unitVal, hOps @ kOps @ vOps @ kcOps @ vcOps @
              [LlvmCallVoidOp("@lang_hashtable_set", [htVal; i64Key; i64Val])
               ArithConstantOp(unitVal, 0L)])

// hashtable_contains_key : hashtable -> key -> bool
| App(App(Var("hashtable_contains_key", _), htExpr, _), keyExpr, _) ->
    let (htVal,  hOps) = elaborateExpr env htExpr
    let (keyVal, kOps) = elaborateExpr env keyExpr
    let (i64Key, coerceOps) = coerceToI64 env keyVal
    let rawResult  = { Name = freshName env; Type = I64 }
    let zeroVal    = { Name = freshName env; Type = I64 }
    let boolResult = { Name = freshName env; Type = I1 }
    let ops = [
        LlvmCallOp(rawResult, "@lang_hashtable_contains_key", [htVal; i64Key])
        ArithConstantOp(zeroVal, 0L)
        ArithCmpIOp(boolResult, "ne", rawResult, zeroVal)
    ]
    (boolResult, hOps @ kOps @ coerceOps @ ops)

// hashtable_remove : hashtable -> key -> unit
| App(App(Var("hashtable_remove", _), htExpr, _), keyExpr, _) ->
    let (htVal,  hOps) = elaborateExpr env htExpr
    let (keyVal, kOps) = elaborateExpr env keyExpr
    let (i64Key, coerceOps) = coerceToI64 env keyVal
    let unitVal = { Name = freshName env; Type = I64 }
    (unitVal, hOps @ kOps @ coerceOps @
              [LlvmCallVoidOp("@lang_hashtable_remove", [htVal; i64Key])
               ArithConstantOp(unitVal, 0L)])

// hashtable_keys : hashtable -> key list
| App(Var("hashtable_keys", _), htExpr, _) ->
    let (htVal, hOps) = elaborateExpr env htExpr
    let result = { Name = freshName env; Type = Ptr }
    (result, hOps @ [LlvmCallOp(result, "@lang_hashtable_keys", [htVal])])
```

### 3.4 Hashing Strategy in C Runtime

LangThree hashtable keys can be any `Value`: `IntValue`, `BoolValue`, `StringValue`, `TupleValue`,
etc. In the compiled backend, keys are passed as `i64` (the uniform representation). The C runtime
must implement a hash function over `int64_t` key words.

For the common cases (integer keys, boolean keys, pointer-as-int keys for string/tuple keys):
use a 64-bit integer hash (e.g., the FNV-1a mix or a simple multiply-shift hash). The hash
table's equality check must be `int64_t` equality — two keys are equal iff their `i64`
representations are equal. This is correct for integer and boolean keys, and for pointer keys
when the same object is used as the key (identity equality). **String equality by content is
NOT supported with this approach** — two distinct string objects with the same content will hash
to different buckets if they are different pointers. This is acceptable for v5.0 and matches
common usage patterns. Document clearly in source.

### 3.5 GC Implications for Hashtable

- All `LangHashtable` and `LangHashEntry` allocations use `GC_malloc`. Boehm GC traces them
  conservatively through bucket array and entry chain pointers.
- Keys and values stored as `int64_t` that are actually heap pointers: Boehm's conservative
  scan treats pointer-sized integers as potential live references, so GC-managed objects used
  as keys/values will not be collected prematurely.
- **Resize / rehash:** when the load factor exceeds a threshold, `lang_hashtable_set` should
  rehash. During rehash, the old `buckets` array is still alive (referenced by `ht->buckets`).
  The new `buckets` array is allocated with `GC_malloc` before the old one is replaced. Since
  no `free` is called, both coexist during the rehash — the old array is collected on the next
  GC cycle after the pointer in `ht->buckets` is updated. No write barriers needed.

---

## 4. Shared Infrastructure: coerceToI64 Helper

Array and Hashtable operations both need to pass values as `i64` to C runtime functions, even when
the value is a heap-typed `Ptr`. This is the same coercion already done in the closure-making call
path (`LlvmPtrToIntOp`). Extract it as a helper function in `Elaboration.fs`:

```fsharp
/// Coerce an MlirValue to I64 for C ABI boundary.
/// If the value is already I64: no-op.
/// If the value is Ptr: emit LlvmPtrToIntOp.
/// If the value is I1: emit ArithExtuIOp (zero-extend to I64).
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
        (r, [ArithExtuIOp(r, v)])   // treat as unsigned extend for return values
```

This helper replaces the ad-hoc coercion code already in the closure-making path and `to_string`
dispatch. Consolidating it reduces duplication.

---

## 5. MlirIR.fs — No Changes Required

All new operations use existing `MlirOp` cases:

| Operation | MlirOp Used |
|-----------|------------|
| Allocate ref cell | `LlvmCallOp` (`@GC_malloc`) |
| Read mutable variable | `LlvmLoadOp` |
| Write mutable variable | `LlvmStoreOp` |
| Array create | `LlvmCallOp` (`@lang_array_create`) |
| Array get | `LlvmCallOp` (`@lang_array_get`) |
| Array set | `LlvmCallVoidOp` (`@lang_array_set`) |
| Array length | `LlvmCallOp` (`@lang_array_length`) |
| Hashtable create | `LlvmCallOp` (`@lang_hashtable_create`) |
| Hashtable get | `LlvmCallOp` (`@lang_hashtable_get`) |
| Hashtable set | `LlvmCallVoidOp` (`@lang_hashtable_set`) |
| Hashtable contains key | `LlvmCallOp` + `ArithCmpIOp` |
| Hashtable remove | `LlvmCallVoidOp` (`@lang_hashtable_remove`) |
| Hashtable keys | `LlvmCallOp` (`@lang_hashtable_keys`) |
| Unit result | `ArithConstantOp(v, 0L)` |
| Ptr-to-I64 coercion | `LlvmPtrToIntOp` |

**Confidence: HIGH.** The existing op set is expressive enough. No new dialect ops are needed.

---

## 6. ExternalFuncDecl Registrations

All new C runtime functions must be declared as `ExternalFuncDecl` in the emitted MLIR module.
The existing `elaborateProgram` function builds the `ExternalFuncs` list. Add these entries:

```fsharp
// Array runtime
{ ExtName = "@lang_array_create";          ExtParams = [I64; I64]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_array_get";             ExtParams = [Ptr; I64]; ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_array_set";             ExtParams = [Ptr; I64; I64]; ExtReturn = None; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_array_length";          ExtParams = [Ptr]; ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_array_of_list";         ExtParams = [Ptr]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_array_to_list";         ExtParams = [Ptr]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
// Hashtable runtime
{ ExtName = "@lang_hashtable_create";      ExtParams = []; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_hashtable_get";         ExtParams = [Ptr; I64]; ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_hashtable_set";         ExtParams = [Ptr; I64; I64]; ExtReturn = None; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_hashtable_contains_key";ExtParams = [Ptr; I64]; ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_hashtable_remove";      ExtParams = [Ptr; I64]; ExtReturn = None; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_hashtable_keys";        ExtParams = [Ptr]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
```

**Note:** Only emit these `ExternalFuncDecl` entries when the corresponding builtins are actually
referenced in the program. The existing pattern (add all externals unconditionally in
`elaborateProgram`) is simpler and already works — unnecessary declarations do not affect correctness.

---

## 7. freeVars and Closure Analysis

`LetMut` and `Assign` need to be handled in `freeVars` for correct closure capture analysis.

Current state: the `freeVars` function has a `| _ -> Set.empty` catch-all fallthrough. This means
`LetMut` and `Assign` are currently treated as having no free variables — **incorrect** if lambdas
capture mutable variables.

**Required change to `freeVars`:**

```fsharp
| LetMut(name, initExpr, bodyExpr, _) ->
    Set.union (freeVars boundVars initExpr)
              (freeVars (Set.add name boundVars) bodyExpr)
| Assign(name, valueExpr, _) ->
    let valFree = freeVars boundVars valueExpr
    // "name" being assigned: if it's a free variable (not in boundVars), it IS free
    // (the assignment reads the cell pointer, which lives in the enclosing scope)
    let nameFree = if Set.contains name boundVars then Set.empty else Set.singleton name
    Set.union valFree nameFree
```

This ensures closures that capture a mutable variable (read OR write) correctly capture the cell
pointer, not just the initial value. Without this fix, a lambda that assigns to a captured mutable
variable would not capture the cell, leading to a runtime failure or silent data corruption.

---

## 8. What NOT to Add

| Idea | Why Not |
|------|---------|
| New `MlirType` variants for `ArrayType` or `HashtableType` | Uniform boxed representation — all heap types are `Ptr`. Adding typed heap pointers would require propagating type information through all existing match arms. Not worth the complexity for a conservative GC. |
| New `MlirOp` for array element GEP + load inline | Bounds checking requires a branch + error path, which requires `cf.cond_br`. Doing this inline would require emitting 5-8 ops per array access in Elaboration.fs. Routing through C runtime is cleaner and already validated by the Range pattern. |
| Open-addressing hash table | Deletion (hashtable_remove) requires tombstones and complicates GC scanning. Chaining is simpler and correct. |
| Resizable arrays | LangThree does not have array resize. Do not add `array_push`, `array_pop`, or `array_resize`. Array is fixed-size from creation. |
| Stack-allocated ref cells (alloca) | `LlvmAllocaOp` exists and could allocate ref cells on the stack. However, stack cells cannot escape into closures. Since mutable variables can be captured by closures, all ref cells must be GC-heap-allocated. Using alloca would be incorrect for the capturing case. |
| Write barriers | Boehm GC is a stop-the-world conservative collector with no generational structure. Write barriers are not needed and would add overhead with no benefit. |
| `MutVars` in MatchCompiler | Pattern matching never needs to distinguish mutable vs immutable bindings. `MutVars` tracking only belongs in `ElabEnv` and `freeVars`. |

---

## 9. Pipeline.fs — No Changes Required

The build pipeline (`mlir-opt` → `mlir-translate` → `clang` → link with `lang_runtime.c` + Boehm
GC) remains unchanged. The new C runtime functions are added to the existing `lang_runtime.c` file
compiled in Step 4. No new compilation steps, no new link flags.

---

## 10. Confidence Assessment

| Area | Confidence | Basis |
|------|------------|-------|
| LetMut/Assign ref cell approach | HIGH | Direct analogy to SetField mutable record fields (already working in 18-04-setfield test). Same GEP+store pattern, different object size. |
| MutVars in ElabEnv | HIGH | Mirrors ExnTags pattern already in ElabEnv. Clean, non-intrusive. freeVars fix is straightforward. |
| Array C runtime layout | HIGH | Mirrors existing LangString layout exactly. lang_array_get/set raise via lang_throw (already working). |
| Array builtin dispatch | HIGH | Same App-matching pattern as string_sub (3-arg) and string_concat (2-arg). |
| Hashtable C runtime | MEDIUM-HIGH | Chained hash table is standard. Key hashing by i64 value is a simplification but correct for the primary use cases. String-key equality by pointer (not content) is a known limitation. |
| Higher-order array builtins (array_iter, etc.) | MEDIUM | Requires closure indirect call from C runtime OR loop emission in MLIR. Defer to follow-on phase. |
| coerceToI64 helper | HIGH | The Ptr→I64 coercion already exists in the closure-making path (LlvmPtrToIntOp). Extracting it is purely mechanical refactoring. |
| freeVars correctness for LetMut | HIGH | The fix is straightforward. Failure to fix it is a correctness bug for closures capturing mutable variables. |

---

## Sources

- Existing `lang_runtime.c` and `lang_runtime.h` — SetField (mutable record), TryWith (lang_throw)
  patterns directly applicable
- Existing `Elaboration.fs` — App matching for string_concat, string_sub, Range (lang_range) as
  direct patterns for Array/Hashtable builtin dispatch
- LangThree `Ast.fs` — confirms LetMut/Assign/LetMutDecl AST shapes and RefValue semantics
- LangThree `Eval.fs` — confirms array_create/get/set/length signatures and hashtable_* signatures
- Existing `MlirIR.fs` — confirms LlvmPtrToIntOp, LlvmCallOp, LlvmCallVoidOp, LlvmLoadOp,
  LlvmStoreOp cover all needed operations without new variants
- `Pipeline.fs` — confirms lang_runtime.c compilation and linking unchanged
