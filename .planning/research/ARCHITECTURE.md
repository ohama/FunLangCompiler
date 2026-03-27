# Architecture Patterns: LangBackend v5.0
**Domain:** Functional language compiler backend — MLIR text generation + LLVM pipeline
**Researched:** 2026-03-27
**Scope:** Mutable variables (LetMut/Assign/LetMutDecl) + Array + Hashtable — integration with existing architecture
**Confidence:** HIGH (based on direct code analysis of all backend modules + LangThree AST/Eval)

---

## Existing Architecture Snapshot (v4.0 baseline)

The compiler is a text-generating pipeline: F# DU (`MlirIR`) → `.mlir` string (`Printer`) → native binary (shell via `Pipeline`). Every new feature integrates by adding elaboration cases and optional C runtime helpers. Nothing in the pipeline stages changes.

```
Source (.lt)
  |
  v
LangThree Frontend (AST, TypeChecker)
  |
  v
Elaboration.fs
  elaborateProgram: Ast.Module → MlirModule
  elaborateExpr:    Expr → ElabEnv → (MlirValue × MlirOp list)
  ElabEnv { Vars, Counter, LabelCounter, Blocks,
            KnownFuncs, Funcs, ClosureCounter,
            Globals, GlobalCounter,
            TypeEnv, RecordEnv, ExnTags }
  |
  v
MlirModule (F# DU: MlirOp ~22 cases, no new cases needed for v5)
  |
  v
Printer.fs  (pure text serializer, no new cases needed)
  |
  v
Pipeline.fs (shell: mlir-opt → mlir-translate → clang)
  |
  v
lang_runtime.c (linked object: GC, strings, lists, range, exceptions)
  + NEW: array_create, array_get, array_set, array_length,
         array_of_list, array_to_list,
         hashtable_create, hashtable_get, hashtable_set,
         hashtable_containsKey, hashtable_keys, hashtable_remove
```

### Current Component Inventory

| Component | Purpose | v5 Status |
|-----------|---------|-----------|
| `MlirIR.fs` | F# DU: MlirType, MlirOp (~22 cases), MlirBlock, FuncOp, MlirModule | No change needed |
| `Elaboration.fs` | AST → MlirIR: elaborateExpr, ElabEnv | Extend with LetMut/Assign/LetMutDecl/Array/Hashtable cases |
| `MatchCompiler.fs` | Jacobs decision tree | No change needed |
| `Printer.fs` | MlirIR → MLIR text, pure serializer | No change needed |
| `Pipeline.fs` | Shell pipeline: mlir-opt / mlir-translate / clang | No change needed |
| `lang_runtime.c` | C runtime: GC_malloc, string ops, range, exceptions | Add array_* and hashtable_* functions |
| `lang_runtime.h` | C runtime header | Add array_* and hashtable_* declarations |

**Key insight:** All three new features (LetMut, Array, Hashtable) use the existing `LlvmCallOp` pattern to call C runtime functions. No new `MlirOp` DU cases are needed. No `Printer.fs` changes are needed. Only `Elaboration.fs` and `lang_runtime.c` change.

---

## Feature 1: Mutable Variables (LetMut / Assign / LetMutDecl)

### Semantics (from LangThree Eval.fs)

- `LetMut(name, valueExpr, body, _)`: allocates a ref cell, binds `name` to it in the environment, evaluates `body`.
- `Assign(name, valueExpr, _)`: looks up `name`, expects a `RefValue`, mutates it in place, returns unit (0).
- `Var(name, _)` when the variable is mutable: transparently dereferences the ref cell to return the stored value.
- `LetMutDecl`: module-level mutable variable — same semantics as `LetMut`, scoped to the module body.

### Memory Layout: GC_malloc ref cell

A mutable variable is stored as a GC-managed 8-byte cell: a single `i64` word (or `ptr` for pointer-typed values). The SSA value in `env.Vars` is the `Ptr` to this cell — not the value itself.

```
GC_malloc(8) → ptr to one i64 word
  offset 0: current value (i64 or ptr coerced to i64)
```

This matches the existing uniform representation: all heap values are `Ptr`; scalars and pointers are stored as `i64` words at fixed GEP offsets.

**Decision: GC_malloc (not alloca)**

Use `GC_malloc(8)` for the ref cell, not `llvm.alloca`. Rationale: closures may capture mutable variables. An `alloca` would become a dangling stack pointer when the allocating function returns but the closure lives longer. `GC_malloc` gives a heap-stable address the closure can safely store. This is the same reason closures already use `GC_malloc` for their env struct.

### LetMut Elaboration

```
; LetMut(name, valueExpr, body, _)
; 1. Evaluate initial value
%init = ... elaborate valueExpr ...    ; type: I64

; 2. Allocate 8-byte ref cell on GC heap
%size    = arith.constant 8 : i64
%refptr  = llvm.call @GC_malloc(%size) : (i64) -> !llvm.ptr

; 3. Store initial value into cell
llvm.store %init, %refptr : i64, !llvm.ptr

; 4. Bind name -> refptr (Ptr) in env.Vars
; Elaborate body with env' = { env with Vars = Map.add name refptr env.Vars }
```

**Important:** `name` maps to the `Ptr`-typed `%refptr`, not to the `I64` value. All uses of `name` as a rvalue must emit a fresh `LlvmLoadOp` to dereference.

### Var Dereference for Mutable Variables

The existing `Var` elaboration case returns the SSA value from `env.Vars` directly. For mutable variables, that value is a `Ptr`; consuming code expects an `I64`. Therefore:

```fsharp
| Var (name, _) ->
    match Map.tryFind name env.Vars with
    | Some v when v.Type = Ptr ->
        // Mutable variable — dereference the ref cell
        let loaded = { Name = freshName env; Type = I64 }
        (loaded, [LlvmLoadOp(loaded, v)])
    | Some v -> (v, [])
    | None -> failwithf "Elaboration: unbound variable '%s'" name
```

**Caveat:** This change to `Var` will fire for ALL `Ptr`-typed variables, not just mutable ones. Existing `Ptr`-typed variables — closure pointers, record pointers, list pointers — must NOT be silently dereferenced here.

**Recommended approach:** Add a separate `MutableVars` field to `ElabEnv` (a `Set<string>`) that explicitly tracks which variables are mutable ref cells. Only dereference when `name` is in `MutableVars`. This avoids breaking existing `Ptr`-typed variable bindings.

```fsharp
type ElabEnv = {
    // ... existing fields ...
    MutableVars: Set<string>   // NEW: names bound to GC_malloc'd ref cells
}
```

Alternatively, keep `env.Vars` holding the `Ptr` for mutable vars and introduce a separate `MutableVarPtrs: Map<string, MlirValue>` for the cell pointers, so `Vars` continues to hold the plain value (after load). But the `MutableVars` set approach is simpler and has lower risk.

### Assign Elaboration

```
; Assign(name, valueExpr, _)
; 1. Evaluate new value
%newval = ... elaborate valueExpr ...  ; type: I64

; 2. Look up cell ptr — must be in MutableVars
%refptr = Map.find name env.Vars       ; Ptr

; 3. Store in place
llvm.store %newval, %refptr : i64, !llvm.ptr

; 4. Return unit
%unit = arith.constant 0 : i64
```

### LetMutDecl in prePassDecls / extractMainExpr

`LetMutDecl` appears in `Decl` (module-level). The existing `prePassDecls` ignores it (only handles TypeDecl, RecordTypeDecl, ExceptionDecl). The existing `extractMainExpr` must be extended:

```fsharp
| Ast.Decl.LetMutDecl(name, body, _) :: rest ->
    // Treat as LetMut(name, body, build rest, s)
    LetMut(name, body, build rest, s)
```

This desugars module-level `let mut x = e` into a nested `LetMut` expression in `@main`, which elaborates as above.

### freeVars Extension

The `freeVars` function must account for `LetMut` and `Assign`:

```fsharp
| LetMut (name, e1, e2, _) ->
    Set.union (freeVars boundVars e1) (freeVars (Set.add name boundVars) e2)
| Assign (name, e, _) ->
    // name is a reference, not a binding — it must be in scope
    let nameFree = if Set.contains name boundVars then Set.empty else Set.singleton name
    Set.union nameFree (freeVars boundVars e)
```

---

## Feature 2: Array

### Semantics (from LangThree Eval.fs)

Array operations are all builtins matched as `App(Var("array_*"), ...)` chains. The evaluator uses F# `Value array` internally. The compiler needs:

| Builtin | Signature | Notes |
|---------|-----------|-------|
| `array_create` | `int -> 'a -> 'a array` | Two-arg curried |
| `array_get` | `'a array -> int -> 'a` | Two-arg curried; bounds check |
| `array_set` | `'a array -> int -> 'a -> unit` | Three-arg curried; bounds check |
| `array_length` | `'a array -> int` | One-arg |
| `array_of_list` | `'a list -> 'a array` | One-arg |
| `array_to_list` | `'a array -> 'a list` | One-arg |
| `array_iter` | `('a -> unit) -> 'a array -> unit` | HOF — defer to later |
| `array_map` | `('a -> 'b) -> 'a array -> 'b array` | HOF — defer to later |
| `array_fold` | `('acc -> 'a -> 'acc) -> 'acc -> 'a array -> 'acc` | HOF — defer to later |
| `array_init` | `int -> (int -> 'a) -> 'a array` | HOF — defer to later |

**Decision: C runtime functions for all array operations**

Do NOT emit inline GEP/load/store for array operations. Use C runtime wrappers for every operation. Rationale:

1. Arrays need bounds checking (the evaluator raises exceptions on OOB). Implementing bounds checking inline would require additional MLIR blocks (if/else). The C runtime can do the check and call `@lang_failwith` or `@lang_throw`.
2. `array_of_list`, `array_to_list` require traversing a linked list — this cannot be expressed as a single GEP op and would require looping constructs not yet in the elaborator.
3. HOF variants (`array_iter`, `array_map`, etc.) require calling function values — these can be added in a later phase when the need is demonstrated.
4. Consistency: the C runtime already handles all `lang_string_*` and `lang_range` operations.

### Array Memory Layout (in C runtime)

An array is a GC-managed struct:

```c
typedef struct {
    int64_t length;   // offset 0: number of elements
    int64_t* data;    // offset 8: pointer to GC_malloc'd element array
} LangArray;
```

- Total size: 16 bytes (matches string struct layout — two-field struct)
- `data` points to a separate `GC_malloc(length * 8)` allocation — one i64 per element
- Elements are stored as `i64` (same uniform representation as all other values)
- Pointer-typed values are stored as `ptrtoint` i64 (consistent with the uniform ABI)

This struct layout is intentionally identical to `LangString` so the same GEP patterns apply if needed. `LlvmGEPStructOp(ptr, 0)` = &length, `LlvmGEPStructOp(ptr, 1)` = &data.

### New C Runtime Functions

```c
// lang_runtime.c additions:

// array_create : int -> i64 -> LangArray*
LangArray* lang_array_create(int64_t n, int64_t default_val);
  // GC_malloc(sizeof(LangArray)) for header
  // GC_malloc(n * 8) for element data
  // fill all elements with default_val

// array_get : LangArray* -> int -> i64
int64_t lang_array_get(LangArray* arr, int64_t i);
  // bounds check: 0 <= i < arr->length
  // return arr->data[i]

// array_set : LangArray* -> int -> i64 -> void
void lang_array_set(LangArray* arr, int64_t i, int64_t val);
  // bounds check: 0 <= i < arr->length
  // arr->data[i] = val

// array_length : LangArray* -> int
int64_t lang_array_length(LangArray* arr);
  // return arr->length

// array_of_list : LangCons* -> LangArray*
LangArray* lang_array_of_list(LangCons* list);
  // count list length, allocate array, fill from list

// array_to_list : LangArray* -> LangCons*
LangCons* lang_array_to_list(LangArray* arr);
  // build cons list from arr->data in order
```

### New ExternalFuncDecl Registrations (Elaboration.fs)

```fsharp
{ ExtName = "@lang_array_create";   ExtParams = [I64; I64]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_array_get";      ExtParams = [Ptr; I64]; ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_array_set";      ExtParams = [Ptr; I64; I64]; ExtReturn = None; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_array_length";   ExtParams = [Ptr];      ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_array_of_list";  ExtParams = [Ptr];      ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_array_to_list";  ExtParams = [Ptr];      ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
```

### Array Builtin Elaboration Pattern

Array builtins are matched before the general `App` case, following the same pattern as `string_concat`, `lang_range`, etc.:

```fsharp
// array_create: App(App(Var("array_create"), nExpr), defExpr)
| App (App (Var ("array_create", _), nExpr, _), defExpr, _) ->
    let (nVal, nOps)    = elaborateExpr env nExpr
    let (defVal, defOps) = elaborateExpr env defExpr
    let result = { Name = freshName env; Type = Ptr }
    (result, nOps @ defOps @ [LlvmCallOp(result, "@lang_array_create", [nVal; defVal])])

// array_get: App(App(Var("array_get"), arrExpr), idxExpr)
| App (App (Var ("array_get", _), arrExpr, _), idxExpr, _) ->
    let (arrVal, arrOps) = elaborateExpr env arrExpr
    let (idxVal, idxOps) = elaborateExpr env idxExpr
    let result = { Name = freshName env; Type = I64 }
    (result, arrOps @ idxOps @ [LlvmCallOp(result, "@lang_array_get", [arrVal; idxVal])])

// array_set: App(App(App(Var("array_set"), arrExpr), idxExpr), valExpr)
| App (App (App (Var ("array_set", _), arrExpr, _), idxExpr, _), valExpr, _) ->
    let (arrVal, arrOps) = elaborateExpr env arrExpr
    let (idxVal, idxOps) = elaborateExpr env idxExpr
    let (valArg, valOps) = elaborateExpr env valExpr
    let unitVal = { Name = freshName env; Type = I64 }
    (unitVal,
     arrOps @ idxOps @ valOps @
     [LlvmCallVoidOp("@lang_array_set", [arrVal; idxVal; valArg])
      ArithConstantOp(unitVal, 0L)])

// array_length: App(Var("array_length"), arrExpr)
| App (Var ("array_length", _), arrExpr, _) ->
    let (arrVal, arrOps) = elaborateExpr env arrExpr
    let result = { Name = freshName env; Type = I64 }
    (result, arrOps @ [LlvmCallOp(result, "@lang_array_length", [arrVal])])

// array_of_list: App(Var("array_of_list"), listExpr)
| App (Var ("array_of_list", _), listExpr, _) ->
    let (listVal, listOps) = elaborateExpr env listExpr
    let result = { Name = freshName env; Type = Ptr }
    (result, listOps @ [LlvmCallOp(result, "@lang_array_of_list", [listVal])])

// array_to_list: App(Var("array_to_list"), arrExpr)
| App (Var ("array_to_list", _), arrExpr, _) ->
    let (arrVal, arrOps) = elaborateExpr env arrExpr
    let result = { Name = freshName env; Type = Ptr }
    (result, arrOps @ [LlvmCallOp(result, "@lang_array_to_list", [arrVal])])
```

### freeVars for Array Builtins

Array builtins are `Var`-named references. They are not in `env.Vars` (not user-defined), so `freeVars` will report them as free variables if encountered inside a lambda body. This is the same as `string_concat`, `print`, etc. — no special handling needed. The existing `| _ -> Set.empty` fallback in `freeVars` handles the unrecognized cases.

The existing `freeVars` line 151: `| _ -> Set.empty` must remain as-is. `LetMut` and `Assign` need explicit cases as noted above; array operations are just `App` chains over `Var` which are already handled.

---

## Feature 3: Hashtable

### Semantics (from LangThree Eval.fs)

Hashtable operations are all builtins matched as `App(Var("hashtable_*"), ...)` chains using `System.Collections.Generic.Dictionary<Value, Value>` internally.

| Builtin | Signature | Notes |
|---------|-----------|-------|
| `hashtable_create` | `unit -> hashtable<'k,'v>` | Zero-arg (takes `()` as unit) |
| `hashtable_get` | `hashtable -> 'k -> 'v` | Two-arg curried; raises on missing key |
| `hashtable_set` | `hashtable -> 'k -> 'v -> unit` | Three-arg curried; mutates in place |
| `hashtable_containsKey` | `hashtable -> 'k -> bool` | Two-arg curried |
| `hashtable_keys` | `hashtable -> 'k list` | One-arg; returns cons list |
| `hashtable_remove` | `hashtable -> 'k -> unit` | Two-arg curried |

**Decision: Must use C runtime (too complex for inline MLIR)**

Hashtable operations cannot be expressed as simple GEP/store sequences. A hash table requires dynamic dispatch for key comparison, hash computation, bucket management, and resizing. The only viable approach is a C runtime wrapper around an opaque C data structure.

### Hashtable Memory Layout (in C runtime)

The hashtable is represented as an opaque pointer to a C struct wrapping a hash table implementation. The MLIR side sees only `!llvm.ptr`.

**Recommended C implementation:** Use a simple chaining hash table with `GC_malloc`-managed nodes. The Boehm GC will trace pointers through the linked chains conservatively. Alternatively, use `uthash` (public domain single-header hash table). The simplest portable approach is a custom linked-list hash table with GC_malloc nodes.

**Key type handling:** Keys are stored as `i64` (uniform representation). For pointer-typed keys (strings, tuples, ADTs), the comparison must dereference and do structural equality. The `hashtable_get`/`containsKey` operations must implement value-level equality consistent with LangThree semantics:
- Integer keys: i64 comparison
- String keys: strcmp
- Boolean keys: i64 comparison (bools are i64 0/1)

**For v5.0 scope:** Support integer and string keys only (the most common use cases). Complex key types (tuples, ADTs) can be added later.

### C Runtime Hashtable Struct

```c
// In lang_runtime.c / lang_runtime.h

typedef struct LangHtEntry {
    int64_t key;              // key (i64 for int/bool, or ptr coerced to i64 for string)
    int64_t key_is_ptr;       // 1 if key is a pointer (string), 0 if plain i64
    int64_t value;            // value (i64 for scalars, or ptr coerced to i64)
    struct LangHtEntry* next; // chaining
} LangHtEntry;

typedef struct {
    int64_t bucket_count;
    LangHtEntry** buckets;   // GC_malloc'd array of bucket head pointers
    int64_t size;             // number of entries
} LangHashtable;
```

### New C Runtime Functions

```c
// hashtable_create : unit -> LangHashtable*
LangHashtable* lang_hashtable_create(void);

// hashtable_get : LangHashtable* -> i64 -> i64   (raises if key missing)
int64_t lang_hashtable_get(LangHashtable* ht, int64_t key);

// hashtable_set : LangHashtable* -> i64 -> i64 -> void
void lang_hashtable_set(LangHashtable* ht, int64_t key, int64_t val);

// hashtable_containsKey : LangHashtable* -> i64 -> i64 (returns 0 or 1)
int64_t lang_hashtable_containsKey(LangHashtable* ht, int64_t key);

// hashtable_keys : LangHashtable* -> LangCons*
LangCons* lang_hashtable_keys(LangHashtable* ht);

// hashtable_remove : LangHashtable* -> i64 -> void
void lang_hashtable_remove(LangHashtable* ht, int64_t key);
```

### New ExternalFuncDecl Registrations (Elaboration.fs)

```fsharp
{ ExtName = "@lang_hashtable_create";      ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_hashtable_get";         ExtParams = [Ptr; I64];  ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_hashtable_set";         ExtParams = [Ptr; I64; I64]; ExtReturn = None; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_hashtable_containsKey"; ExtParams = [Ptr; I64];  ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_hashtable_keys";        ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_hashtable_remove";      ExtParams = [Ptr; I64];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
```

### Hashtable Builtin Elaboration Pattern

Same App-chain matching as array and string builtins:

```fsharp
// hashtable_create: App(Var("hashtable_create"), unitExpr)
// unitExpr is typically Number(0,_) or () — elaborate it but discard result
| App (Var ("hashtable_create", _), _unitExpr, _) ->
    let result = { Name = freshName env; Type = Ptr }
    (result, [LlvmCallOp(result, "@lang_hashtable_create", [])])

// hashtable_get: App(App(Var("hashtable_get"), htExpr), keyExpr)
| App (App (Var ("hashtable_get", _), htExpr, _), keyExpr, _) ->
    let (htVal, htOps)   = elaborateExpr env htExpr
    let (keyVal, keyOps) = elaborateExpr env keyExpr
    let result = { Name = freshName env; Type = I64 }
    (result, htOps @ keyOps @ [LlvmCallOp(result, "@lang_hashtable_get", [htVal; keyVal])])

// hashtable_set: App(App(App(Var("hashtable_set"), htExpr), keyExpr), valExpr)
| App (App (App (Var ("hashtable_set", _), htExpr, _), keyExpr, _), valExpr, _) ->
    let (htVal,  htOps)  = elaborateExpr env htExpr
    let (keyVal, keyOps) = elaborateExpr env keyExpr
    let (valArg, valOps) = elaborateExpr env valExpr
    let unitVal = { Name = freshName env; Type = I64 }
    (unitVal,
     htOps @ keyOps @ valOps @
     [LlvmCallVoidOp("@lang_hashtable_set", [htVal; keyVal; valArg])
      ArithConstantOp(unitVal, 0L)])

// hashtable_containsKey: App(App(Var("hashtable_containsKey"), htExpr), keyExpr)
| App (App (Var ("hashtable_containsKey", _), htExpr, _), keyExpr, _) ->
    let (htVal,  htOps)  = elaborateExpr env htExpr
    let (keyVal, keyOps) = elaborateExpr env keyExpr
    let rawResult  = { Name = freshName env; Type = I64 }
    let zeroVal    = { Name = freshName env; Type = I64 }
    let boolResult = { Name = freshName env; Type = I1 }
    let ops = [
        LlvmCallOp(rawResult, "@lang_hashtable_containsKey", [htVal; keyVal])
        ArithConstantOp(zeroVal, 0L)
        ArithCmpIOp(boolResult, "ne", rawResult, zeroVal)
    ]
    (boolResult, htOps @ keyOps @ ops)

// hashtable_keys: App(Var("hashtable_keys"), htExpr)
| App (Var ("hashtable_keys", _), htExpr, _) ->
    let (htVal, htOps) = elaborateExpr env htExpr
    let result = { Name = freshName env; Type = Ptr }
    (result, htOps @ [LlvmCallOp(result, "@lang_hashtable_keys", [htVal])])

// hashtable_remove: App(App(Var("hashtable_remove"), htExpr), keyExpr)
| App (App (Var ("hashtable_remove", _), htExpr, _), keyExpr, _) ->
    let (htVal,  htOps)  = elaborateExpr env htExpr
    let (keyVal, keyOps) = elaborateExpr env keyExpr
    let unitVal = { Name = freshName env; Type = I64 }
    (unitVal,
     htOps @ keyOps @
     [LlvmCallVoidOp("@lang_hashtable_remove", [htVal; keyVal])
      ArithConstantOp(unitVal, 0L)])
```

**Note on `hashtable_containsKey` return type:** The C function returns `i64` (0 or 1). The MLIR result must be `I1` for boolean uses. The pattern above extends the C result to `I1` via `arith.cmpi ne`, identical to the `lang_string_contains` pattern in the existing codebase.

---

## Component Interaction Map (v5.0)

```
Ast.Decl list
   |
   | elaborateProgram (existing entry point, minimal extension)
   |
   |-- LetMutDecl   → desugar to LetMut in extractMainExpr
   |
   | elaborateExpr (extended with new cases)
   |
   |-- LetMut       → GC_malloc(8) ref cell + store init + bind Ptr in MutableVars
   |-- Assign       → load refptr from MutableVars + store new val + return 0
   |-- Var (mut)    → load from refptr (only when name in MutableVars)
   |
   |-- App("array_create", n, def)     → @lang_array_create(n, def) → Ptr
   |-- App("array_get", arr, idx)      → @lang_array_get(arr, idx) → I64
   |-- App("array_set", arr, idx, val) → @lang_array_set(arr, idx, val) + unit
   |-- App("array_length", arr)        → @lang_array_length(arr) → I64
   |-- App("array_of_list", list)      → @lang_array_of_list(list) → Ptr
   |-- App("array_to_list", arr)       → @lang_array_to_list(arr) → Ptr
   |
   |-- App("hashtable_create", _)           → @lang_hashtable_create() → Ptr
   |-- App("hashtable_get", ht, key)        → @lang_hashtable_get(ht, key) → I64
   |-- App("hashtable_set", ht, key, val)   → @lang_hashtable_set(ht, key, val) + unit
   |-- App("hashtable_containsKey", ht, k)  → @lang_hashtable_containsKey → I1
   |-- App("hashtable_keys", ht)            → @lang_hashtable_keys(ht) → Ptr
   |-- App("hashtable_remove", ht, key)     → @lang_hashtable_remove(ht, key) + unit
   |
   v
MlirModule (unchanged structure)
   |
   v
Printer.fs (no changes)
   |
   v
.mlir text → Pipeline.fs → native binary (linked with updated lang_runtime.c)
```

---

## Build Order

| Step | Files Changed | What It Delivers | Prerequisite | Rationale |
|------|--------------|-----------------|-------------|-----------|
| 1 | `lang_runtime.c`, `lang_runtime.h` | `lang_array_*` C functions compile and link | None | Independent; validates C data structure before wiring into MLIR |
| 2 | `lang_runtime.c`, `lang_runtime.h` | `lang_hashtable_*` C functions compile and link | None (parallel with step 1) | Independent from array; validates hashtable implementation |
| 3 | `Elaboration.fs` | `MutableVars` field added to `ElabEnv`; `emptyEnv` updated | None (parallel with steps 1-2) | Prerequisite for LetMut/Assign elaboration |
| 4 | `Elaboration.fs` | `freeVars` extended for `LetMut` and `Assign` cases | Step 3 | Free variable analysis must be correct before closures can capture mutable vars |
| 5 | `Elaboration.fs` | `extractMainExpr` handles `LetMutDecl` | None | Desugaring must be in place before elaboration tests can run |
| 6 | `Elaboration.fs` | `LetMut` elaboration: `GC_malloc(8)` ref cell + store + bind in `MutableVars` | Step 3 | Core feature; no external deps |
| 7 | `Elaboration.fs` | `Assign` elaboration: store to ref cell + return unit | Steps 3, 6 | Requires mutable var ptr to be in env |
| 8 | `Elaboration.fs` | `Var` dereference for mutable variables | Steps 3, 6 | Reading a mut var requires the ref cell to exist |
| 9 | `Elaboration.fs` | Array builtin elaboration + `ExternalFuncDecl` registrations | Step 1 | Requires C functions to be available for linking |
| 10 | `Elaboration.fs` | Hashtable builtin elaboration + `ExternalFuncDecl` registrations | Step 2 | Requires C functions to be available for linking |

**Build order summary:**
- Steps 1 and 2 are independent (parallel): implement and test C runtime functions in isolation.
- Step 3 is independent: add `MutableVars` field without changing any behavior.
- Steps 4-8 are sequential (LetMut phase): freeVars, desugar, then LetMut, then Assign, then Var dereference.
- Steps 9 and 10 are independent of each other and of steps 3-8 (Array and Hashtable builtins have no interaction with mutable variable machinery).

**Recommended phase structure:**
- Phase 1: LetMut/Assign (steps 3-8 + partial step 1 for C tests)
- Phase 2: Array (step 1 + step 9)
- Phase 3: Hashtable (step 2 + step 10)

This ordering delivers usable features incrementally and isolates failures to a single feature at a time.

---

## Data Layout Summary

| Type | Memory | MLIR SSA type | Allocation |
|------|--------|--------------|------------|
| Mutable variable ref cell | `{i64 value}` — 8 bytes | `Ptr` (to ref cell) | `GC_malloc(8)` |
| Array header | `{i64 length, i64* data}` — 16 bytes | `Ptr` (to header) | `GC_malloc(16)` via C runtime |
| Array data | `i64[n]` — `n*8` bytes | (internal, via C runtime) | `GC_malloc(n*8)` via C runtime |
| Hashtable | opaque struct (bucket array + entries) | `Ptr` (to LangHashtable) | `GC_malloc` via C runtime |

All three are GC-managed `Ptr`-typed values at the MLIR level, consistent with the existing uniform representation.

---

## ElabEnv Changes

Only one new field is needed:

```fsharp
type ElabEnv = {
    // --- existing fields (unchanged) ---
    Vars:           Map<string, MlirValue>
    Counter:        int ref
    LabelCounter:   int ref
    Blocks:         MlirBlock list ref
    KnownFuncs:     Map<string, FuncSignature>
    Funcs:          FuncOp list ref
    ClosureCounter: int ref
    Globals:        (string * string) list ref
    GlobalCounter:  int ref
    TypeEnv:        Map<string, TypeInfo>
    RecordEnv:      Map<string, Map<string, int>>
    ExnTags:        Map<string, int>
    // --- new field ---
    MutableVars:    Set<string>   // names bound to GC_malloc'd ref cells (for Var dereference)
}
```

`emptyEnv ()` sets `MutableVars = Set.empty`.

Array and Hashtable builtins do not need env extensions — they are matched by name in `elaborateExpr` and dispatched to C runtime calls directly, just as `lang_string_concat` is matched by name today.

---

## Anti-Patterns

### Anti-Pattern 1: Storing Mutable Variable as alloca

**What people do:** Use `LlvmAllocaOp` for the ref cell instead of `GC_malloc(8)`.

**Why it is wrong:** A closure capturing a mutable variable stores the cell's address. If the cell is stack-allocated in function F and the closure outlives F, the stored pointer is dangling. GC_malloc ensures the cell lives on the GC heap for as long as any reference to it exists (conservative scan will find the pointer in the closure's capture slots).

**Do this instead:** `LlvmCallOp(refptr, "@GC_malloc", [sizeVal8])` where `sizeVal8` is `arith.constant 8 : i64`.

### Anti-Pattern 2: Returning the Ptr Directly from Var Lookup for Mutable Variables

**What people do:** When `Var "x"` is looked up and `x` is a mutable variable, return the `Ptr` SSA value (the ref cell pointer) directly as the expression result.

**Why it is wrong:** Consumer code expects an `I64` value. Arithmetic operations, comparisons, and function calls receive an `!llvm.ptr` where they expect `i64`, causing MLIR type errors.

**Do this instead:** Emit `LlvmLoadOp(loaded, refptr)` for every `Var` reference to a mutable variable. Use `MutableVars` set to know which variables need this dereference.

### Anti-Pattern 3: Changing Existing `Var` Logic Without MutableVars Guard

**What people do:** Modify the `Var` case to dereference ALL `Ptr`-typed env values.

**Why it is wrong:** Closures, records, lists, strings, and ADT values are all `Ptr`-typed in `env.Vars`. Dereferencing them as ref cells would read the first 8 bytes of those structs as integer values — silently corrupting all non-mutable pointer-typed variables.

**Do this instead:** Check `Set.contains name env.MutableVars` before emitting the load. Only emit the dereference load for confirmed mutable ref cells.

### Anti-Pattern 4: Implementing Array Operations Inline with GEP

**What people do:** Instead of calling `@lang_array_get`, emit inline GEP/load sequences to access array elements directly.

**Why it is wrong:** Inline GEP cannot perform bounds checking without emitting an if/else control flow structure. Skipping bounds checking silently reads/writes memory outside the array, producing undefined behavior rather than the expected LangThree exception. Additionally, `array_of_list` and `array_to_list` require iteration — inline GEP cannot express loops without `LetRec` or a new loop construct.

**Do this instead:** Call C runtime functions for all array operations. The bounds check is inside the C runtime and can call `lang_failwith` to terminate cleanly.

### Anti-Pattern 5: Using stdlib malloc/free for Hashtable Instead of GC_malloc

**What people do:** Implement the hashtable using `malloc` / `free` directly to avoid GC interaction.

**Why it is wrong:** The Boehm GC is conservative: it scans all heap memory for pointer-shaped values. If the hashtable's internal nodes are allocated with `malloc`, the GC will not see them, but it will still try to scan them if any GC-managed pointer points to a `malloc`-managed block. The GC may also attempt to move or track allocations in ways that conflict with `malloc`. More practically: if a hashtable key or value is a GC-managed pointer and the GC collects it (because only the hashtable's `malloc`-managed storage references it), the hashtable entry becomes a dangling pointer.

**Do this instead:** All hashtable allocations (the header struct, the bucket array, and all entry nodes) use `GC_malloc`. The conservative GC scanner will trace pointers through them correctly.

---

## Scalability Considerations

| Concern | After v5.0 | Notes |
|---------|-----------|-------|
| `MlirOp` DU cases | 0 new cases | All features use existing `LlvmCallOp` / `LlvmCallVoidOp` |
| `Printer.fs` match arms | 0 new arms | No new MlirOp cases |
| `ElabEnv` fields | +1 (`MutableVars`) | 14 total fields |
| `lang_runtime.c` functions | +12 (6 array + 6 hashtable) | All GC-safe |
| `elaborateExpr` pattern arms | +12 builtin App patterns | Sequential matching; no perf concern at current scale |
| Mutable variable ref cells | 1 GC alloc per `LetMut` | Same as any other heap value |
| Array allocation | 2 GC allocs per `array_create` | Header + data; fixed after creation |
| Hashtable initial allocation | ~2 GC allocs | Header + initial bucket array; grows on demand |

---

## Sources

- Direct code analysis: `Elaboration.fs`, `MlirIR.fs`, `Printer.fs`, `lang_runtime.c`, `lang_runtime.h` in `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/`
- AST definition: `Ast.fs` in `/Users/ohama/vibe-coding/LangThree/src/LangThree/` — confirmed `LetMut`, `Assign`, `LetMutDecl` nodes at lines 113-114, 330
- Evaluator semantics: `Eval.fs` in `/Users/ohama/vibe-coding/LangThree/src/LangThree/` — lines 437-571 for array/hashtable builtins, lines 749-763 for LetMut/Assign
- Existing `string_contains` pattern (Elaboration.fs lines 689-701): reference for the `I64 → I1` conversion pattern used in `hashtable_containsKey`
- Existing `lang_range` / `lang_string_sub` pattern (Elaboration.fs lines 682-688, 960-970): reference for multi-arg builtin App matching
- Existing GC_malloc pattern (Elaboration.fs lines 911-924): reference for `GC_malloc(n*8)` allocation sequences
- Previous ARCHITECTURE.md (v4.0): unchanged constraints on `Ptr` uniform representation, `GC_malloc` allocation, and decision tree pattern matching

---
*Architecture research for: LangBackend v5.0 Mutable & Collections milestone*
*Researched: 2026-03-27*
