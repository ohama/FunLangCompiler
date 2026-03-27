# Feature Landscape: v5.0 Mutable Variables, Array, Hashtable

**Domain:** ML-family functional language compiler backend — v5.0 mutable bindings + collections
**Researched:** 2026-03-27
**Confidence:** HIGH (all findings sourced by direct inspection of LangThree AST, Eval, Parser, Bidir)

---

## Scope Anchor

This file addresses the v5.0 milestone: compiling mutable variable bindings (`LetMut`, `Assign`,
`LetMutDecl`), fixed-size mutable arrays (`ArrayValue`), and mutable key-value hashtables
(`HashtableValue`) in LangBackend. The LangThree frontend already defines all AST nodes, evaluator
semantics, parser syntax, and type-system rules. All work is in the compiler backend only.

**Existing foundation (already compiled by LangBackend):**
- GC: Boehm GC (`GC_malloc`), uniform `ptr` representation for all heap values
- Closures: `{fn_ptr, env}` struct with curried call ABI `(ptr, i64) -> i64`
- Pattern matching: Jacobs decision tree via `MatchCompiler.fs`
- String: `{i64 length, ptr data}` header; string builtins in `lang_runtime.c`
- Tuple/List: GC_malloc'd N-field ptr arrays; null nil, `{head, tail}` cons cells
- ADT: `{i64 tag, ptr payload}` structs; `ConstructorPat` tag-dispatch
- Records: N-field ptr arrays with `RecordEnv` name→index mapping; `SetField` in-place store
- Exceptions: `setjmp`/`longjmp` via `lang_try_push`/`lang_throw` in `lang_runtime.c`
- `extractMainExpr` and `prePassDecls` in `Elaboration.fs` handle module-level decls

---

## Complete AST Node Inventory

Every AST node that requires elaboration in v5.0.

### Mutable Variables

| AST Node | Location | Signature | What It Does in LangThree |
|----------|----------|-----------|---------------------------|
| `LetMut` | `Ast.Expr` | `name: string * value: Expr * body: Expr * span` | Expression-level mutable binding: allocates a ref cell, binds `name` to `RefValue(ref value)` in env, evaluates `body` in extended env. Returns body's value. |
| `Assign` | `Ast.Expr` | `name: string * value: Expr * span` | Mutation: looks up `name` in env (must be `RefValue`), evaluates new value, updates the ref cell in-place. Returns `TupleValue []` (unit). |
| `LetMutDecl` | `Ast.Decl` | `name: string * body: Expr * span` | Module-level mutable binding: same as `LetMut` but at top-level scope; stored in env as `RefValue`. No continuation expression — subsequent decls act as the body. |

**Runtime representation in LangThree evaluator:**
- `LetMut(name, valueExpr, body)` → `let refCell = ref (eval valueExpr)` → bind `name` to `RefValue refCell` → `eval body`
- `Assign(name, valueExpr)` → `r.Value <- eval valueExpr` → return `TupleValue []`
- `Var(name)` with `RefValue r` in env → `r.Value` (transparent deref — user code never sees the `RefValue` wrapper)

**Type-system rules in LangThree Bidir.fs:**
- `LetMut`: NO generalization — mutable variables are monomorphic (value restriction). The type is `Scheme([], apply s1 valueTy)` with no quantified variables.
- `Assign`: checks `Set.contains name mutableVars`; raises `ImmutableVariableAssignment` if not mutable.
- `mutableVars` is a module-level mutable set that tracks which names are currently mutable (scoped restore on exit from `LetMut`'s body).

### Array

| AST Node | Location | Signature | What It Does in LangThree |
|----------|----------|-----------|---------------------------|
| `ArrayValue` | `Ast.Value` | `Value array` | Runtime value: wraps a .NET `Value array` (mutable, fixed-size). Equality is reference identity (`ReferenceEquals`). |

**There is no array literal syntax in the parser.** Arrays are created and accessed exclusively via builtin functions. All array operations are `BuiltinValue` in the evaluator, typed in `TypeCheck.fs`.

**Complete array builtin inventory:**

| Builtin Name | Type Signature | Behavior |
|--------------|---------------|----------|
| `array_create` | `int -> 'a -> 'a array` | Allocates `.NET Array.create n defVal`. Raises `LangThreeException` if `n < 0`. |
| `array_get` | `'a array -> int -> 'a` | Returns `arr.[i]`. Raises `LangThreeException` on out-of-bounds. |
| `array_set` | `'a array -> int -> 'a -> unit` | Mutates `arr.[i] <- newVal`. Raises on out-of-bounds. Returns `TupleValue []`. |
| `array_length` | `'a array -> int` | Returns `IntValue arr.Length`. |
| `array_of_list` | `'a list -> 'a array` | Converts `ListValue` → `ArrayValue` via `Array.ofList`. |
| `array_to_list` | `'a array -> 'a list` | Converts `ArrayValue` → `ListValue` via `Array.toList`. |
| `array_iter` | `('a -> unit) -> 'a array -> unit` | Iterates array, calls `fVal` on each element, discards result. Returns unit. |
| `array_map` | `('a -> 'b) -> 'a array -> 'b array` | Maps a function over array, returns new `ArrayValue`. |
| `array_fold` | `('acc -> 'a -> 'acc) -> 'acc -> 'a array -> 'acc` | Left fold over array elements. |
| `array_init` | `int -> (int -> 'a) -> 'a array` | Allocates `Array.init n (fun i -> fVal i)`. Raises if `n < 0`. |

**Display format:** `[|e1; e2; e3|]` (F# array literal notation, used by `formatValue`).

**Type representation:** `TArray of Type` in `Type.fs`. Unifies element-wise (`TArray t1 ~ TArray t2` iff `t1 ~ t2`). Formatted as `"T array"`.

### Hashtable

| AST Node | Location | Signature | What It Does in LangThree |
|----------|----------|-----------|---------------------------|
| `HashtableValue` | `Ast.Value` | `Dictionary<Value, Value>` | Runtime value: wraps a .NET `Dictionary<Value, Value>` (mutable, unbounded). Equality is reference identity. |

**There is no hashtable literal syntax in the parser.** Hashtables are created and accessed exclusively via builtin functions.

**Complete hashtable builtin inventory:**

| Builtin Name | Type Signature | Behavior |
|--------------|---------------|----------|
| `hashtable_create` | `unit -> hashtable<'k, 'v>` | Allocates a new empty `Dictionary<Value, Value>`. Returns `HashtableValue`. |
| `hashtable_get` | `hashtable<'k, 'v> -> 'k -> 'v` | Looks up key. Raises `LangThreeException(StringValue "Hashtable.get: key not found")` if missing. |
| `hashtable_set` | `hashtable<'k, 'v> -> 'k -> 'v -> unit` | `ht.[key] <- value`. Returns unit. |
| `hashtable_containsKey` | `hashtable<'k, 'v> -> 'k -> bool` | Returns `BoolValue (ht.ContainsKey key)`. |
| `hashtable_keys` | `hashtable<'k, 'v> -> 'k list` | Returns `ListValue (ht.Keys |> Seq.toList)`. No ordering guarantee. |
| `hashtable_remove` | `hashtable<'k, 'v> -> 'k -> unit` | `ht.Remove(key) |> ignore`. Returns unit. |

**Display format:** `hashtable{k1 -> v1; k2 -> v2}` (used by `formatValue`).

**Type representation:** `THashtable of Type * Type` in `Type.fs`. Unifies key and value types separately. Formatted as `"hashtable<K, V>"`.

---

## Table Stakes

Features that must compile for v5.0 to be complete. Missing any of these = milestone not done.

| Feature | Why Required | Complexity | AST Node |
|---------|-------------|------------|----------|
| `LetMut` expression elaboration | Core mutable binding; all `let mut x = e in body` programs need it | MEDIUM | `Expr.LetMut` |
| `Assign` expression elaboration | Core mutation; without it mutable variables are read-only | MEDIUM | `Expr.Assign` |
| `LetMutDecl` module-level elaboration | Module-level `let mut x = e` needed for global mutable state programs | MEDIUM | `Decl.LetMutDecl` |
| Transparent `Var` deref for mutable variables | `Var(name)` where `name` was bound by `LetMut` must transparently load the ref cell | MEDIUM | `Expr.Var` (existing, needs extension) |
| `array_create` builtin | The only way to construct an array; without it no array programs run | MEDIUM | Builtin |
| `array_get` builtin | The only way to read an array element | MEDIUM | Builtin |
| `array_set` builtin | The only way to write an array element; without it arrays are immutable | MEDIUM | Builtin |
| `array_length` builtin | Needed for bounds-checking and iteration idioms | LOW | Builtin |
| `array_of_list` builtin | Standard conversion; many programs build arrays from lists | LOW | Builtin |
| `array_to_list` builtin | Standard conversion; enables array results to be used with list operations | LOW | Builtin |
| `hashtable_create` builtin | The only way to construct a hashtable | MEDIUM | Builtin |
| `hashtable_get` builtin | The only way to read a hashtable entry | MEDIUM | Builtin |
| `hashtable_set` builtin | The only way to write a hashtable entry | MEDIUM | Builtin |
| `hashtable_containsKey` builtin | Needed before every `hashtable_get` to avoid exceptions | LOW | Builtin |
| `hashtable_keys` builtin | Required for iteration over hashtable contents | LOW | Builtin |
| `hashtable_remove` builtin | Required for entry deletion | LOW | Builtin |

---

## Differentiators

Features beyond strict table stakes. These are listed here because they appear in LangThree's
builtin set and will be expected by users writing realistic programs.

| Feature | Value Proposition | Complexity | Notes |
|---------|-------------------|------------|-------|
| `array_iter` builtin | Idiomatic imperative-style array iteration without index arithmetic | LOW | `('a -> unit) -> 'a array -> unit`; calls curried function on each element |
| `array_map` builtin | Functional array transformation — needed for array-processing programs | LOW | Returns new `ArrayValue`; must not mutate source |
| `array_fold` builtin | Reduction over arrays — generalizes `array_iter` | LOW | Curried: `('acc -> 'a -> 'acc) -> 'acc -> 'a array -> 'acc` |
| `array_init` builtin | Construct arrays from index-to-value functions; avoids `array_create` + loop | LOW | Same negative-size guard as `array_create` |
| Out-of-bounds error messages | Programs get a meaningful runtime error instead of segfault on bad array index | LOW | `array_get`/`array_set` must raise a `LangThreeException` (not a native crash); implement via `@lang_throw` with a string payload |
| Missing-key error messages | `hashtable_get` on absent key raises `LangThreeException` not native crash | LOW | Same pattern as array OOB; payload is a string `HashtableValue` |

---

## Anti-Features

Features to deliberately NOT build in v5.0.

| Anti-Feature | Why Requested | Why Problematic | What to Do Instead |
|--------------|---------------|-----------------|-------------------|
| Array literal syntax `[| e1; e2 |]` | Ergonomic way to construct small arrays | The LangThree parser does NOT support array literal syntax (no `LBRACKETPIPE` / `PIPEBRACKET` tokens); adding it requires parser changes which are out of scope for the backend | Use `array_create` + `array_set` or `array_of_list [e1; e2]` |
| Hashtable literal syntax `{k: v, ...}` | Ergonomic initialization | The LangThree parser has no hashtable literal; parsers would need new tokens and grammar rules | Use `hashtable_create ()` + `hashtable_set ht k v` calls |
| Mutable variable polymorphism | Attempting `let mut id = fun x -> x` and using it at multiple types | LangThree already rejects this at the type-checker level (no generalization on `LetMut` — value restriction). The backend must NOT work around this | Trust the type checker; the backend only sees monomorphic mutable bindings |
| Separate `RefValue` type in compiled output | Exposing ref cells as first-class values (like OCaml's `ref`) | `RefValue` is an evaluator-internal implementation detail; the LangThree type system does not have a `ref` type visible to the user. `Var` dereferences transparently | Implement the ref cell as a GC_malloc'd single-slot box, dereferenced automatically on every `Var` access of a mutable name |
| `array_fill` / `array_copy` / `Array.blit` | Convenient bulk array operations | Not in LangThree's builtin set; adding them requires LangThree frontend changes | Use `array_iter` + `array_set` for filling; use `array_init` + `array_get` for copying |
| `hashtable_values` builtin | Symmetric with `hashtable_keys` | Not in LangThree's builtin set | Use `hashtable_keys ht |> List.map (fun k -> hashtable_get ht k)` |
| Hashtable iteration order guarantees | Deterministic key enumeration | .NET `Dictionary` has unspecified iteration order; matching LangThree evaluator behavior | Acceptable — callers should not depend on key order |
| Mutable record fields and `LetMut` as unified system | Using `LetMut` + `SetField` interchangeably | `SetField` operates on record fields via GEP (already implemented in v4). `LetMut`/`Assign` operates on stack-like ref cells via a separate mechanism. These are different IR patterns | Keep them separate: `SetField` = GEP+store on a record slot; `Assign` = store through a ref-cell pointer |

---

## Feature Dependencies

```
LetMut elaboration
    └──requires──> GC_malloc (already done)
    └──requires──> LlvmAllocaOp or GC_malloc for ref cell allocation
    └──requires──> LlvmStoreOp (already done) — store initial value into ref cell
    └──produces──> RefCell: a 1-slot GC_malloc'd box holding a ptr/i64

Assign elaboration
    └──requires──> LetMut elaboration (must know which names are ref-cells in env)
    └──requires──> LlvmStoreOp (already done) — overwrite ref cell slot
    └──produces──> unit (ArithConstantOp 0L)

Var deref for mutable variables
    └──requires──> LetMut elaboration (must distinguish ref-cell names from plain names)
    └──requires──> LlvmLoadOp (already done) — load through ref cell pointer
    └──modifies──> existing `Var` elaboration case (add RefCell check)

LetMutDecl elaboration
    └──requires──> LetMut elaboration (same mechanism)
    └──modifies──> extractMainExpr / prePassDecls (must handle LetMutDecl like LetDecl but with ref cell)

array_create builtin
    └──requires──> GC_malloc — allocate n-slot ptr array for element storage
    └──requires──> a new runtime struct or direct GC_malloc layout for arrays
    └──produces──> ArrayHeader: {i64 length, ptr data_block}

array_get builtin
    └──requires──> array_create (to have arrays)
    └──requires──> LlvmGEPLinearOp + LlvmLoadOp (already done) — index into data block
    └──requires──> bounds check → @lang_throw on failure

array_set builtin
    └──requires──> array_create
    └──requires──> LlvmGEPLinearOp + LlvmStoreOp (already done)
    └──requires──> bounds check → @lang_throw on failure

array_length builtin
    └──requires──> array_create (to have the length field in ArrayHeader)
    └──requires──> LlvmGEPStructOp (slot 0) + LlvmLoadOp (already done)

array_of_list / array_to_list builtins
    └──requires──> array_create (for array_of_list)
    └──requires──> list elaboration (already done)

array_iter / array_map / array_fold / array_init builtins
    └──requires──> array_create, array_get, array_set, array_length
    └──requires──> curried function call mechanism (already done via IndirectCallOp)

hashtable_create builtin
    └──requires──> a new C runtime struct or external library allocation
    └──produces──> HashtableHandle: an opaque pointer to a C-level hashtable

hashtable_get / hashtable_set / hashtable_containsKey / hashtable_remove builtin
    └──requires──> hashtable_create (to have the handle)
    └──requires──> new C runtime helper functions in lang_runtime.c
    └──requires──> @lang_throw for missing-key error (already done)

hashtable_keys builtin
    └──requires──> hashtable_create
    └──requires──> list construction mechanism (already done)
    └──requires──> C runtime helper that builds a GC-rooted linked list of keys
```

### Dependency Notes

- **LetMut before Assign:** `Assign` is meaningless without `LetMut` establishing the ref cell.
  Both must be implemented in the same phase.
- **Var deref is a cross-cutting change:** Every `Var` lookup in `elaborateExpr` must check
  whether the name was bound by `LetMut`/`LetMutDecl` and emit a load through the ref-cell
  pointer. The env must distinguish ref-cell values from plain values. The cleanest approach:
  store a special `MlirValue` tag or a separate `MutableVars: Set<string>` in `ElabEnv`.
- **Array layout must match LangThree's `ArrayValue` semantics:** The evaluator uses a .NET
  `Value array` with length embedded in the .NET array object. The compiled representation must
  also store the length explicitly (as a field in the heap struct) since LLVM/C has no equivalent
  of .NET array metadata. Recommended layout: `{i64 length, ptr elements_block}` — same pattern
  as strings.
- **Hashtable must be implemented in C runtime:** Unlike arrays (which have a simple GEP layout),
  a hashtable requires a hash function over `Value` and a dynamic bucket structure. Implementing
  this in MLIR/LLVM IR is not practical. The correct approach is to add C helper functions to
  `lang_runtime.c` that accept opaque `ptr` arguments and dispatch to a C-level hash map.
- **Builtins are resolved at elaboration time:** The backend already resolves builtins like
  `string_concat` via the `TypeCheck.fs` builtin env. Array and hashtable builtins must be added
  to the same builtin resolution path in `Elaboration.fs` so that `BuiltinValue`-typed expressions
  produce the correct `LlvmCallOp` or `IndirectCallOp` sequences.
- **LetMutDecl requires `extractMainExpr` extension:** The current `extractMainExpr` filters only
  `LetDecl` and `LetRecDecl`. It must also handle `LetMutDecl` by wrapping them in `LetMut`
  expressions or elaborating them as ref-cell allocations that persist into subsequent decls.

---

## MVP Definition

### Launch With — v5.0

Minimum set for the milestone to be declared complete:

**Mutable Variables:**
- [ ] `ElabEnv` extended with `MutableVars: Set<string>` (or equivalent ref-cell tracking)
- [ ] `LetMut(name, valueExpr, body)` — allocate 1-slot GC_malloc ref cell, store initial value, elaborate body with ref-cell binding
- [ ] `Assign(name, valueExpr)` — load ref-cell ptr from env, store new value, return unit
- [ ] `Var(name)` when `name` is mutable — emit `LlvmLoadOp` through ref-cell pointer (transparent deref)
- [ ] `LetMutDecl(name, body)` — module-level ref-cell; `extractMainExpr` extended to include `LetMutDecl`

**Array:**
- [ ] `ArrayHeader` heap layout: `{i64 length, ptr elements_block}` — two GC_malloc's per `array_create`
- [ ] `array_create` builtin compiled to: allocate header, allocate `n * 8` data block, store length, fill slots with `defVal`
- [ ] `array_get` builtin compiled to: load length, bounds-check (raise on failure), GEP+load from data block
- [ ] `array_set` builtin compiled to: bounds-check, GEP+store into data block, return unit
- [ ] `array_length` builtin compiled to: load length field from header
- [ ] `array_of_list` compiled to: compute list length, `array_create`, iterate cons cells, fill slots
- [ ] `array_to_list` compiled to: iterate slots 0..length-1, build cons cells

**Hashtable:**
- [ ] C runtime structs in `lang_runtime.c`: `LangHashtable` wrapping a simple open-addressing or chaining hash map with `Value` key/value
- [ ] `hashtable_create` compiled to: call `@lang_ht_create()` returning `Ptr`
- [ ] `hashtable_get` compiled to: call `@lang_ht_get(ht, key)`, branch on missing-key sentinel → `@lang_throw`
- [ ] `hashtable_set` compiled to: call `@lang_ht_set(ht, key, val)`, return unit
- [ ] `hashtable_containsKey` compiled to: call `@lang_ht_contains(ht, key)`, return `I1`-extended to `I64` bool
- [ ] `hashtable_keys` compiled to: call `@lang_ht_keys(ht)` returning a GC-rooted list `Ptr`
- [ ] `hashtable_remove` compiled to: call `@lang_ht_remove(ht, key)`, return unit

### Add After Core Works — v5.x

- [ ] `array_iter`, `array_map`, `array_fold`, `array_init` — add once array_create/get/set/length pass tests
- [ ] Bounds-check error message quality improvements
- [ ] `hashtable_containsKey` before `hashtable_get` pattern in generated code (safety idiom docs)

### Defer to v6+

- [ ] Array literal syntax (`[| e1; e2 |]`) — requires parser changes in LangThree
- [ ] `hashtable_values`, `hashtable_size` builtins — not in LangThree's current builtin set
- [ ] Unboxed integer arrays — requires monomorphization
- [ ] Hashtable resizing policy control — implementation detail hidden in runtime

---

## Feature Prioritization Matrix

| Feature | User Value | Implementation Cost | Priority |
|---------|------------|---------------------|----------|
| `LetMut` + `Assign` + Var deref | HIGH | MEDIUM | P1 |
| `LetMutDecl` (module-level) | HIGH | LOW (extends LetMut) | P1 |
| `array_create` + `array_get` + `array_set` + `array_length` | HIGH | MEDIUM | P1 |
| `array_of_list` + `array_to_list` | HIGH | LOW | P1 |
| `hashtable_create` + `hashtable_set` + `hashtable_get` | HIGH | HIGH (C runtime) | P1 |
| `hashtable_containsKey` + `hashtable_keys` + `hashtable_remove` | MEDIUM | LOW (extend C runtime) | P1 |
| `array_iter` + `array_map` + `array_fold` + `array_init` | MEDIUM | LOW | P2 |
| Array out-of-bounds error quality | MEDIUM | LOW | P2 |
| Hashtable missing-key error quality | MEDIUM | LOW | P2 |

**Priority key:**
- P1: Must have for v5.0 launch
- P2: Add once P1 items pass tests

---

## Reference Implementation Notes

### Mutable Variable (Ref Cell) Heap Layout

```
+----------+
| value    |
+----------+
  ptr (or i64)
```

- A ref cell is a 1-slot GC_malloc'd block, 8 bytes.
- `LetMut(name, init, body)`: allocate `GC_malloc(8)`, store `init` value at slot 0.
- `Assign(name, val)`: GEP slot 0, store new value.
- `Var(name)` where name is mutable: GEP slot 0, load.
- `ElabEnv` must track which variable names are mutable (e.g., `MutableVars: Set<string>`).
- The ref cell pointer itself is stored in the env like any other `MlirValue`.

### Array Heap Layout

```
+----------+----------+----------+----------+----------+
| length   | elem_0   | elem_1   | ...      | elem_n-1 |
+----------+----------+----------+----------+----------+
   i64        ptr        ptr                   ptr
```

Two allocation options:
1. **Two-block**: `ArrayHeader {i64 length, ptr data}` (like strings) + separate `data` block of `n * 8` bytes.
2. **One-block**: Allocate `(1 + n) * 8` bytes; slot 0 = length as i64, slots 1..n = elements.

Recommendation: **One-block** (simpler, fewer GC roots). `GC_malloc((n + 1) * 8)`.
- `array_length arr` → `GEP(arr, 0)` + `LlvmLoadOp` → `i64`
- `array_get arr i` → bounds check, then `GEP(arr, i + 1)` + `LlvmLoadOp`
- `array_set arr i v` → bounds check, then `GEP(arr, i + 1)` + `LlvmStoreOp`

The `LlvmGEPLinearOp` (existing) already handles this pattern; it needs a runtime-computed index
(not a compile-time constant). Check whether `LlvmGEPLinearOp` supports SSA-value indexing or
only constant offsets — if only constants, a new `LlvmGEPDynamicOp` variant may be needed.

### Hashtable C Runtime Design

Since hashtable semantics require a dynamic hash function over arbitrary `Value` types, the
implementation must live in `lang_runtime.c`. The recommended design is to wrap a simple
C-level hash map:

```c
// Opaque hashtable — allocated via GC_malloc so Boehm GC traces it
typedef struct LangHashtable {
    int64_t  capacity;
    int64_t  count;
    void**   keys;    // GC_malloc'd ptr array
    void**   values;  // GC_malloc'd ptr array
    int64_t* hashes;  // cached hash values (i64)
} LangHashtable;

LangHashtable* lang_ht_create(void);
void*          lang_ht_get(LangHashtable* ht, void* key);
void           lang_ht_set(LangHashtable* ht, void* key, void* value);
int64_t        lang_ht_contains(LangHashtable* ht, void* key);
void*          lang_ht_keys(LangHashtable* ht);     // returns LangList* (null-terminated cons list)
void           lang_ht_remove(LangHashtable* ht, void* key);
```

Key implementation notes:
- All `Value` pointers are passed as `void*` (opaque `ptr` in MLIR).
- The hash function must handle boxed integers (dereference and hash the i64), strings (content hash),
  tuples (recursive hash), and other value types.
- `lang_ht_get` must return a sentinel (e.g., a null pointer) on missing key, not a crash;
  the calling compiled code tests for the sentinel and calls `@lang_throw` on miss.
- All internal arrays (`keys`, `values`, `hashes`) must be allocated with `GC_malloc` or
  registered as GC roots — otherwise Boehm GC may collect live values stored inside the table.
- `lang_ht_keys` returns a linked list built with `GC_malloc` cons cells compatible with the
  existing `{head: ptr, tail: ptr}` list layout.

### `extractMainExpr` Extension for `LetMutDecl`

Current `extractMainExpr` filters: `LetDecl`, `LetRecDecl`.
Must add: `LetMutDecl`.

```fsharp
// Proposed extension to build function in extractMainExpr:
| Ast.Decl.LetMutDecl(name, body, _) :: rest ->
    LetMut(name, body, build rest, s)
```

This desugars module-level `let mut x = e` into a nested `LetMut` expression at the start of the
main body, which the existing `LetMut` elaboration case then handles uniformly.

---

## Sources

- `../LangThree/src/LangThree/Ast.fs` — direct inspection (HIGH confidence)
  - `Expr.LetMut`, `Expr.Assign`, `Decl.LetMutDecl`, `Value.ArrayValue`, `Value.HashtableValue`, `Value.RefValue`
- `../LangThree/src/LangThree/Eval.fs` — direct inspection (HIGH confidence)
  - LetMut/Assign semantics (ref cells), Var deref, all 10 array builtins, all 6 hashtable builtins
  - `RefValue r -> formatValue !r` (transparent display)
  - `ArrayValue` equality = reference identity; `HashtableValue` equality = reference identity
- `../LangThree/src/LangThree/Parser.fsy` — direct inspection (HIGH confidence)
  - `LET MUTABLE IDENT EQUALS Expr IN Expr` → `LetMut`
  - `IDENT LARROW Expr` → `Assign`
  - `LET MUTABLE IDENT EQUALS Expr` (module-level) → `LetMutDecl`
  - No array literal syntax (`[|...|]`) tokens exist in the lexer/parser
  - No hashtable literal syntax exists
- `../LangThree/src/LangThree/Bidir.fs` — direct inspection (HIGH confidence)
  - `LetMut`: monomorphic scheme, `mutableVars` set management (save/restore on scope exit)
  - `Assign`: guards on `mutableVars` membership, raises `ImmutableVariableAssignment`
- `../LangThree/src/LangThree/TypeCheck.fs` — direct inspection (HIGH confidence)
  - All 10 array builtin type signatures (`TArray`, `TArrow`, `TInt`, etc.)
  - All 6 hashtable builtin type signatures (`THashtable`)
- `../LangThree/src/LangThree/Type.fs` — direct inspection (HIGH confidence)
  - `TArray of Type`, `THashtable of Type * Type` defined in the `Type` DU
- `src/LangBackend.Compiler/Elaboration.fs` — direct inspection (HIGH confidence)
  - Confirmed: `LetMut`, `Assign`, `ArrayValue`, `HashtableValue` NOT yet elaborated (no match cases)
  - `extractMainExpr` currently handles only `LetDecl` / `LetRecDecl`
  - `prePassDecls` handles `TypeDecl`, `RecordTypeDecl`, `ExceptionDecl` — does NOT handle `LetMutDecl`
- `src/LangBackend.Compiler/MlirIR.fs` — direct inspection (HIGH confidence)
  - `LlvmGEPLinearOp of result * ptr * index: int` — index is a compile-time `int`, not SSA value
  - This confirms a new dynamic-index GEP op may be needed for array access
- `src/LangBackend.Compiler/lang_runtime.c` — direct inspection (HIGH confidence)
  - String, exception (`lang_throw`, `lang_try_push`, `lang_try_exit`) runtime exists
  - No array or hashtable C runtime functions exist yet

---
*Feature research for: LangBackend v5.0 — Mutable Variables, Array, Hashtable*
*Researched: 2026-03-27*
