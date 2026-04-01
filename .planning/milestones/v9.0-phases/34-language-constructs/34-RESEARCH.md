# Phase 34: Language Constructs - Research

**Researched:** 2026-03-29
**Domain:** FunLangCompiler Elaboration.fs + C runtime — string slicing, list comprehension, ForInExpr tuple destructuring, and collection for-in
**Confidence:** HIGH

## Summary

Phase 34 adds four language constructs that are already parsed by LangThree and fully evaluated by `Eval.fs`, but have no elaboration arm in `Elaboration.fs`. The AST node types `StringSliceExpr`, `ListCompExpr`, and the extended `ForInExpr` (with `TuplePat`) all exist in `LangThree/src/LangThree/Ast.fs`. The compiler's `Elaboration.fs` currently falls through to `failwithf "Elaboration: unsupported expression %A"` for all four constructs.

The three sub-plans map cleanly to existing compiler patterns. Plan 34-01 (string slicing) uses the existing `lang_string_sub(s, start, len)` C function — a new thin wrapper `lang_string_slice(s, start, stop)` that converts inclusive `stop` to `len` is the right approach. Plan 34-02 (list comprehension) desugars `ListCompExpr` to a lambda + runtime `lang_list_comp_*` calls; the range form `[for i in 0..n -> expr]` is already parsed as `ListCompExpr(var, Range(start, stop, None, ...), body, ...)` — the `Range` node evaluates to a list via `@lang_range`, so elaboration can just use `lang_for_in_list` internally. Plan 34-03 (ForInExpr tuple destructuring + new collection for-in) is the most complex: `TuplePat` in the loop variable requires the closure to receive a heap-allocated tuple pointer, and HashSet/Queue/MutableList/Hashtable each need a dedicated `lang_for_in_*` C function.

**Primary recommendation:** Add elaboration arms to `Elaboration.fs` for all four constructs, add C helper functions to `lang_runtime.c`/`h`, and add externalFuncs entries to BOTH lists in `Elaboration.fs`. Follow the exact patterns from Phase 30 (ForInExpr) and Phase 33 (collection types).

## Standard Stack

### Core
| Component | Location | Purpose | Why Standard |
|-----------|----------|---------|--------------|
| lang_runtime.c | src/FunLangCompiler.Compiler/lang_runtime.c | New C helpers for slice and for-in variants | All runtime implementations live here (1024 lines) |
| lang_runtime.h | src/FunLangCompiler.Compiler/lang_runtime.h | Header declarations | All prototypes must be declared here |
| Elaboration.fs | src/FunLangCompiler.Compiler/Elaboration.fs | AST-to-MLIR translation | Pattern-match on new AST nodes, emit ops |

### Supporting
| Component | Purpose | When to Use |
|-----------|---------|-------------|
| `lang_string_sub(s, start, len)` | Existing substring helper | Basis for `lang_string_slice` — wraps with `len = stop - start + 1` |
| `lang_for_in_list(closure, coll)` | Existing list iteration | Pattern to copy for HashSet/Queue/MutableList/Hashtable variants |
| `lang_range(start, stop, step)` | Existing range → LangCons list | ListCompExpr range form uses Range → list, then `lang_for_in_list` |
| `LlvmCallVoidOp` + `ArithConstantOp` | Void C call → MLIR unit | Pattern for for-in (returns void → unit I64 0) |
| `LlvmCallOp` | C call returning value | For `lang_string_slice` (returns Ptr) and `lang_list_comp` (returns Ptr) |
| `externalFuncs` (×2) | MLIR external declarations | Both lists at ~line 2953 and ~line 3178 in Elaboration.fs must be kept in sync |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| New `lang_string_slice(s, start, stop)` | Inline MLIR ops computing `stop - start + 1` then calling `lang_string_sub` | Wrapper is simpler and consistent with runtime-function pattern used throughout |
| New `lang_list_comp` runtime function | Elaborate ListCompExpr as Lambda + for-in + cons accumulation in MLIR | Runtime function is far simpler — complex MLIR list-building without a helper would be hard |
| Separate `lang_for_in_hashset/queue/mlist/ht` | Single generic dispatcher checking struct tag bits | Separate functions are safer and match the compile-time dispatch pattern from Phase 30 |
| TuplePat in ForInExpr via desugaring to LetPat inside body | Dedicated tuple-closure C function | Desugaring is cleaner — no new C ABI needed for tuple destructuring |

**Installation:** No new packages. All within the existing C + F# + MLIR pipeline.

## Architecture Patterns

### Recommended File Structure
```
src/FunLangCompiler.Compiler/
├── lang_runtime.c    # Add: lang_string_slice, lang_list_comp, lang_for_in_hashset,
│                     #      lang_for_in_queue, lang_for_in_mlist, lang_for_in_hashtable
├── lang_runtime.h    # Add: corresponding prototypes
└── Elaboration.fs    # Add: StringSliceExpr, ListCompExpr, ForInExpr TuplePat,
                      #      ForInExpr new-collection arms; update both externalFuncs lists
tests/compiler/
├── 34-01-string-slice-bounded.flt
├── 34-02-string-slice-open.flt
├── 34-03-list-comp-coll.flt
├── 34-04-list-comp-range.flt
├── 34-05-forin-tuple-ht.flt
├── 34-06-forin-hashset.flt
├── 34-07-forin-queue.flt
├── 34-08-forin-mutablelist.flt
└── 34-09-forin-hashtable.flt
```

### Pattern 1: StringSliceExpr — new C wrapper + Elaboration arm

**What:** `s.[start..stop]` compiles to `lang_string_slice(s_ptr, start_i64, stop_i64)`.
`s.[start..]` (open-ended) uses `stop = s->length - 1`.

**C implementation:**
```c
// lang_runtime.c
LangString* lang_string_slice(LangString* s, int64_t start, int64_t stop) {
    // stop is inclusive — convert to len
    int64_t len = stop - start + 1;
    return lang_string_sub(s, start, len);
}

// Open-ended variant — stop = length - 1
LangString* lang_string_slice_open(LangString* s, int64_t start) {
    return lang_string_sub(s, start, s->length - start);
}
```

**Alternative (simpler):** Use a single function with sentinel value for open-ended:
```c
LangString* lang_string_slice(LangString* s, int64_t start, int64_t stop) {
    // stop == -1 means "to end"
    if (stop < 0) stop = s->length - 1;
    int64_t len = stop - start + 1;
    return lang_string_sub(s, start, len);
}
```
Use `stop = -1L` constant from Elaboration when `stopOpt = None`.

**Elaboration arm:**
```fsharp
| StringSliceExpr (strExpr, startExpr, stopOpt, _) ->
    let (strVal, strOps)     = elaborateExpr env strExpr
    let (startVal, startOps) = elaborateExpr env startExpr
    let (stopVal, stopOps) =
        match stopOpt with
        | Some stopExpr -> elaborateExpr env stopExpr
        | None ->
            let v = { Name = freshName env; Type = I64 }
            (v, [ArithConstantOp(v, -1L)])   // sentinel: -1 = open-ended
    let result = { Name = freshName env; Type = Ptr }
    (result, strOps @ startOps @ stopOps @ [LlvmCallOp(result, "@lang_string_slice", [strVal; startVal; stopVal])])
```

**externalFuncs entry (both lists):**
```fsharp
{ ExtName = "@lang_string_slice"; ExtParams = [Ptr; I64; I64]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
```

### Pattern 2: ListCompExpr — new C runtime + Elaboration arm

**What:** `[for x in coll -> expr]` builds a new list by applying body to each element.
The range form `[for i in 0..n -> expr]` is parsed as `ListCompExpr(i, Range(0, n, None), body)` — `Range` already elaborates to a `LangCons*` list via `@lang_range`.

**C implementation strategy:** Create `lang_list_comp(closure, collection)` that is identical to `lang_for_in_list` but accumulates results into a new `LangCons*` list returned as value.

```c
// lang_runtime.c
LangCons* lang_list_comp(void* closure, void* collection) {
    LangClosureFn fn = *(LangClosureFn*)closure;
    LangCons* cur = (LangCons*)collection;
    // Build result list (reverse accumulate, then reverse)
    LangCons* result = NULL;
    while (cur != NULL) {
        int64_t val = fn(closure, cur->head);
        LangCons* cell = (LangCons*)GC_malloc(sizeof(LangCons));
        cell->head = val;
        cell->tail = result;
        result = cell;
        cur = cur->tail;
    }
    // Reverse the accumulated list to preserve order
    LangCons* reversed = NULL;
    while (result != NULL) {
        LangCons* next = result->tail;
        result->tail = reversed;
        reversed = result;
        result = next;
    }
    return reversed;
}
```

**Elaboration arm:** Mirrors the ForInExpr arm (wrap body as Lambda, call `lang_list_comp`).
```fsharp
| ListCompExpr (var, collExpr, bodyExpr, span) ->
    let closureLambda = Lambda(var, bodyExpr, span)
    let (closureVal, closureOps) = elaborateExpr env closureLambda
    let (collVal, collOps) = elaborateExpr env collExpr
    // Coerce closureVal/collVal to Ptr if I64 (same as ForInExpr)
    ...
    let result = { Name = freshName env; Type = Ptr }
    (result, ... @ [LlvmCallOp(result, "@lang_list_comp", [closurePtrVal; collPtrVal])])
```

**externalFuncs entry (both lists):**
```fsharp
{ ExtName = "@lang_list_comp"; ExtParams = [Ptr; Ptr]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
```

### Pattern 3: ForInExpr TuplePat — desugar to LetPat inside the loop body

**What:** `for (k, v) in ht do body` — the loop var is a `TuplePat`. The current `ForInExpr` elaboration ignores non-`VarPat` patterns (using `freshName env` as the lambda parameter). The fix: use a fresh variable name for the lambda parameter and prepend a `LetPat(TuplePat(...), Var(freshVarName), body)` as the real body.

**Insight from existing code:** `LetPat(TuplePat, ...)` is already fully implemented in Elaboration.fs (lines 601-630). The `TuplePat` destructuring extracts GEP-loaded fields from a heap pointer.

**Desugar strategy:**
```fsharp
| ForInExpr (TuplePat(pats, patSpan) as tuplePat, collExpr, bodyExpr, span) ->
    let paramName = freshName env   // fresh param for the lambda
    // Wrap body: let (k, v) = param in body
    let innerBody = LetPat(tuplePat, Var(paramName, span), bodyExpr, span)
    let closureLambda = Lambda(paramName, innerBody, span)
    let (closureVal, closureOps) = elaborateExpr env closureLambda
    // ... rest is same as VarPat ForInExpr arm
```

**Key insight:** For Hashtable for-in, each element passed to the closure is a heap-allocated 2-slot tuple `(key, value)` created by `lang_for_in_hashtable`. So the closure receives a `Ptr` (tuple pointer), and the `LetPat(TuplePat(...), ...)` GEP-extracts the key/value fields correctly.

### Pattern 4: ForInExpr new-collection for-in

**What:** `for x in hs do ...`, `for x in q do ...`, `for x in ml do ...`, `for (k,v) in ht do ...`
Need new `lang_for_in_*` C functions for HashSet, Queue, MutableList, and Hashtable.

**Key design decisions:**
- HashSet iteration: walk all buckets and entries, call `fn(closure, entry->key)`
- Queue iteration: walk linked list from head to tail via `node->next`
- MutableList iteration: index loop over `ml->data[0..ml->len-1]`
- Hashtable iteration: walk all entries, allocate 2-word tuple `[key, val]`, pass as `int64_t` pointer cast

```c
// HashSet — iterate all bucket chains
void lang_for_in_hashset(void* closure, LangHashSet* hs) {
    LangClosureFn fn = *(LangClosureFn*)closure;
    for (int64_t i = 0; i < hs->capacity; i++) {
        LangHashSetEntry* e = hs->buckets[i];
        while (e != NULL) {
            fn(closure, e->key);
            e = e->next;
        }
    }
}

// Queue — walk head->tail
void lang_for_in_queue(void* closure, LangQueue* q) {
    LangClosureFn fn = *(LangClosureFn*)closure;
    LangQueueNode* node = q->head;
    while (node != NULL) {
        fn(closure, node->value);
        node = node->next;
    }
}

// MutableList — index loop
void lang_for_in_mlist(void* closure, LangMutableList* ml) {
    LangClosureFn fn = *(LangClosureFn*)closure;
    for (int64_t i = 0; i < ml->len; i++) {
        fn(closure, ml->data[i]);
    }
}

// Hashtable — yields (key, value) tuples as heap-allocated int64_t[2] pointers
// The closure receives an int64_t cast of a 2-word heap ptr: [slot0=key, slot1=val]
void lang_for_in_hashtable(void* closure, LangHashtable* ht) {
    LangClosureFn fn = *(LangClosureFn*)closure;
    for (int64_t i = 0; i < ht->capacity; i++) {
        LangHashEntry* e = ht->buckets[i];
        while (e != NULL) {
            int64_t* tup = (int64_t*)GC_malloc(2 * sizeof(int64_t));
            tup[0] = e->key;
            tup[1] = e->val;
            fn(closure, (int64_t)(uintptr_t)tup);
            e = e->next;
        }
    }
}
```

**Elaboration dispatch:** Extend `isArrayExpr`-style logic with a new `isCollectionType` helper OR just extend `ForInExpr` arm to detect collection type from the expression:

```fsharp
// In ForInExpr elaboration — select runtime function based on collection source
let forInFn =
    match collExpr with
    | App (Var ("hashset_create", _), _, _)
    | Var (name, _) when isHashSetVar env.HashSetVars name -> "@lang_for_in_hashset"
    | App (Var ("queue_create", _), _, _)
    | Var (name, _) when isQueueVar env.QueueVars name -> "@lang_for_in_queue"
    | App (Var ("mutablelist_create", _), _, _)
    | Var (name, _) when isMListVar env.MListVars name -> "@lang_for_in_mlist"
    | App (Var ("hashtable_create", _), _, _)
    | Var (name, _) when isHtVar env.HtVars name -> "@lang_for_in_hashtable"
    | _ ->
        if isArrayExpr env.ArrayVars collExpr
        then "@lang_for_in_array"
        else "@lang_for_in_list"
```

**Alternative approach (simpler, avoids new tracking sets):** Use type-discriminated approach with a single enum tracking in `ElabEnv`:

```
CollectionKind = List | Array | HashSet | Queue | MutableList | Hashtable
```

Add a `CollectionVars: Map<string, CollectionKind>` field to `ElabEnv` and track it in `Let`/`LetPat` bindings alongside `ArrayVars`.

**Recommendation:** Add `CollectionVars: Map<string, CollectionKind>` to `ElabEnv`. Update all `Let` binding sites that currently update `ArrayVars` to also update `CollectionVars`.

### Anti-Patterns to Avoid
- **Using a single generic `lang_for_in_collection` dispatcher at runtime:** Would require encoding collection type in the struct layout (tag bits), which is not the established pattern (Phase 30 uses compile-time dispatch).
- **Not updating both externalFuncs lists:** Phase 33 RESEARCH explicitly notes both lists at ~line 2953 and ~line 3178. Missing one causes link errors.
- **Using `lang_string_sub` directly from Elaboration:** `lang_string_sub` takes `(s, start, len)` but `StringSliceExpr` has `(s, start, stop)` — off-by-one if passed directly.
- **Forgetting to track `CollectionVars` in closure elaboration:** Lambda bodies evaluated in a new environment may not inherit `ArrayVars`/`CollectionVars` from the outer scope. Check how the existing closure env is set up.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| List building in comprehension | MLIR cons-cell allocation inline | `lang_list_comp(closure, collection)` C function | Allocating a list in MLIR without a helper is dozens of ops; reuse `LangCons` allocation pattern |
| Substring with inclusive stop | Custom MLIR arithmetic | `lang_string_slice(s, start, stop)` C wrapper over `lang_string_sub` | Consistent with all other string operations going through C runtime |
| Tuple allocation for hashtable for-in | MLIR GC_malloc + GEP + store inline | Allocate 2-word heap struct inside `lang_for_in_hashtable` | Keeps MLIR clean; C side is simpler |

**Key insight:** All non-trivial heap operations are done in C. The MLIR emitted by Elaboration.fs is purely dispatch (call ops with coerced pointer args).

## Common Pitfalls

### Pitfall 1: ElabEnv CollectionVars not propagated into closures
**What goes wrong:** `for x in hs do ...` — inside the lambda body, `env.CollectionVars` is empty because closure elaboration creates a fresh inner env.
**Why it happens:** Line 447 and 756 in Elaboration.fs reset `ArrayVars = Set.empty` for closures. A similar reset on `CollectionVars` would break nested for-in.
**How to avoid:** When creating the closure env for a Lambda, propagate `CollectionVars` from the outer `env` (same as `MutableVars` is propagated at line 447).
**Warning signs:** Nested `for x in ml do (for y in ml2 do ...)` uses `lang_for_in_list` for the inner `ml2` (wrong function).

### Pitfall 2: Missing externalFuncs entries in second list
**What goes wrong:** MLIR module has undeclared function references → `mlir-opt` emits "unknown function" error.
**Why it happens:** `Elaboration.fs` has two identical `externalFuncs` lists — one in `elaborateModule` (the main path) and one in `elaborateProgram` (the declaration path). Both must be updated.
**How to avoid:** Always search for `@lang_range` (a stable entry) and add new entries immediately after it in both occurrences.
**Warning signs:** E2E test fails with `error: operation refers to an unknown function` in the mlir-opt stderr.

### Pitfall 3: StringSliceExpr stop=-1 sentinel vs actual negative index
**What goes wrong:** `s.[-1..]` would send -1 as the `stop` argument to `lang_string_slice`, which would be misinterpreted as the open-ended sentinel.
**Why it happens:** The sentinel value -1L is used for open-ended slices in the recommended design.
**How to avoid:** Use a dedicated `lang_string_slice_open(s, start)` for the `None` stop case instead of a sentinel. This avoids ambiguity completely.
**Warning signs:** Test `s.[0..]` returns correct result but `s.[0..-1]` crashes or returns wrong result.

### Pitfall 4: ListCompExpr range form — Range elaborates to list, not array
**What goes wrong:** `[for i in 0..5 -> i*2]` — the `Range` node calls `@lang_range` which returns a `LangCons*` (list), not an array. If `lang_list_comp` is written expecting an array, it will segfault.
**Why it happens:** `lang_range` always returns `LangCons*` (see Elaboration.fs line 1741, and `lang_runtime.c`).
**How to avoid:** `lang_list_comp` must take a `LangCons*` collection — same signature as `lang_for_in_list`. For array comprehensions, a separate `lang_list_comp_array` would be needed but is out of scope.
**Warning signs:** `[for i in 0..5 -> i]` produces wrong output or crashes.

### Pitfall 5: Hashtable for-in element type — tuple vs int64
**What goes wrong:** `for (k, v) in ht do ...` — the closure receives an `int64_t` from `lang_for_in_hashtable`, which is the cast of a `int64_t*` tuple pointer. The `LetPat(TuplePat, ...)` destructuring assumes the value is a `Ptr` type and does GEP+load. If the value arrives as `I64`, a `LlvmIntToPtrOp` coercion is needed before GEP.
**Why it happens:** `LangClosureFn` signature is `int64_t (*fn)(void*, int64_t)` — the second arg is always `int64_t`, even for pointers.
**How to avoid:** In the `ForInExpr (TuplePat(...))` arm, emit `LlvmIntToPtrOp` to convert the `I64` closure argument to `Ptr` before doing GEP. Look at how `LetPat(TuplePat(...), ...)` receives its `tupPtrVal` — it needs to be `Ptr` type. The lambda body synthesis must cast the loop variable from `I64` to `Ptr`.
**Warning signs:** MLIR validation error: "expected ptr type operand" on GEP.

### Pitfall 6: ForInExpr TuplePat — fresh param name collides with body vars
**What goes wrong:** `freshName env` generates `%t42` which is used as both the lambda parameter name and potentially appears in the body's string GEP ops.
**Why it happens:** `freshName env` uses a global counter; `Lambda(freshName, body, ...)` creates a local binding.
**How to avoid:** The fresh name is bound by the lambda as a local parameter, so collisions with outer `%t42`-style names are impossible — MLIR SSA scoping prevents shadowing. No extra precaution needed.

## Code Examples

### StringSliceExpr — Elaboration arm (complete)
```fsharp
// Source: Elaboration.fs — follows StringExpr pattern
| StringSliceExpr (strExpr, startExpr, stopOpt, _) ->
    let (strVal, strOps)     = elaborateExpr env strExpr
    let (startVal, startOps) = elaborateExpr env startExpr
    let (stopVal, stopOps) =
        match stopOpt with
        | Some stopExpr -> elaborateExpr env stopExpr
        | None ->
            let sv = { Name = freshName env; Type = I64 }
            (sv, [ArithConstantOp(sv, -1L)])
    // Coerce strVal to Ptr if needed (strings always arrive as Ptr, but defensive)
    let strPtrVal =
        if strVal.Type = I64 then { Name = freshName env; Type = Ptr } else strVal
    let strCoerceOps =
        if strVal.Type = I64 then [LlvmIntToPtrOp(strPtrVal, strVal)] else []
    let result = { Name = freshName env; Type = Ptr }
    (result, strOps @ startOps @ stopOps @ strCoerceOps
             @ [LlvmCallOp(result, "@lang_string_slice", [strPtrVal; startVal; stopVal])])
```

### lang_string_slice C function
```c
// Source: lang_runtime.c — wraps lang_string_sub
LangString* lang_string_slice(LangString* s, int64_t start, int64_t stop) {
    // stop == -1 means open-ended (to end of string)
    if (stop < 0) stop = s->length - 1;
    int64_t len = stop - start + 1;
    return lang_string_sub(s, start, len);
}
```

### ListCompExpr — Elaboration arm (complete)
```fsharp
// Source: Elaboration.fs — mirrors ForInExpr arm but calls lang_list_comp
| ListCompExpr (var, collExpr, bodyExpr, span) ->
    let closureLambda = Lambda(var, bodyExpr, span)
    let (closureVal, closureOps) = elaborateExpr env closureLambda
    let (collVal, collOps) = elaborateExpr env collExpr
    let closurePtrVal =
        if closureVal.Type = I64 then { Name = freshName env; Type = Ptr } else closureVal
    let closureCoerceOps =
        if closureVal.Type = I64 then [LlvmIntToPtrOp(closurePtrVal, closureVal)] else []
    let collPtrVal =
        if collVal.Type = I64 then { Name = freshName env; Type = Ptr } else collVal
    let collCoerceOps =
        if collVal.Type = I64 then [LlvmIntToPtrOp(collPtrVal, collVal)] else []
    let result = { Name = freshName env; Type = Ptr }
    (result, closureOps @ collOps @ closureCoerceOps @ collCoerceOps
             @ [LlvmCallOp(result, "@lang_list_comp", [closurePtrVal; collPtrVal])])
```

### ForInExpr TuplePat — desugar to LetPat inside lambda
```fsharp
// Source: Elaboration.fs — extended ForInExpr arm
| ForInExpr (TuplePat (pats, patSpan) as tuplePat, collExpr, bodyExpr, span) ->
    let paramName = freshName env
    // Inner body: let (k, v) = paramVar in bodyExpr
    let innerBody = LetPat(tuplePat, Var(paramName, span), bodyExpr, span)
    // Coerce param from I64 to Ptr at lambda entry (Hashtable passes int64_t(tuple_ptr))
    let castPtrName = freshName env
    let castBody = LetPat(VarPat(castPtrName, span),
                          // Need an explicit inttoptr here — use Annot trick or raw op
                          Var(paramName, span), innerBody, span)
    // Actually: emit the inttoptr in the lambda body by wrapping...
    // Simplest: use the same TuplePat LetPat machinery but pass paramName as Ptr
    // (The Lambda param always arrives as I64; GEP needs Ptr)
    // See Pitfall 5: must emit LlvmIntToPtrOp before LetPat(TuplePat)
    ...
```

Note: The full TuplePat arm requires emitting `LlvmIntToPtrOp` inside the lambda body before the `LetPat(TuplePat)` can GEP. The cleanest approach is to elaborate the lambda with a `VarPat(paramName)`, then inside the body emit:
1. `LlvmIntToPtrOp(ptrVal, paramI64Val)` — convert i64 lambda arg to Ptr
2. Then bind the tuple fields via `bindTuplePat env ptrVal pats`

This means the TuplePat ForInExpr arm cannot be handled purely by desugaring — it needs explicit MLIR op emission for the I64→Ptr coercion before the tuple destructure. Look at how `LetPat(TuplePat, ...)` is implemented at lines 601-630 to see how `bindTuplePat` works.

### E2E test pattern (established convention)
```flt
// --- Command: bash -c 'OUTBIN=$(mktemp /tmp/langback_XXXXXX) && dotnet run --project %S/../../src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj -- %input -o $OUTBIN && $OUTBIN; echo $?; rm -f $OUTBIN'
// --- Input:
let s = "hello world" in
println (s.[0..4]);
println (s.[6..])
// --- Output:
hello
world
0
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Generic `lang_for_in` (single function) | Compile-time dispatch: `lang_for_in_list` vs `lang_for_in_array` | Phase 30 (v8.0) | Must extend: add `lang_for_in_hashset`, `lang_for_in_queue`, `lang_for_in_mlist`, `lang_for_in_hashtable` |
| `StringSliceExpr` → unsupported | `StringSliceExpr` → `@lang_string_slice` | Phase 34 (this phase) | New C function + elaboration arm |
| `ListCompExpr` → unsupported | `ListCompExpr` → `@lang_list_comp` | Phase 34 (this phase) | New C function + elaboration arm |
| `ForInExpr (VarPat)` only | `ForInExpr (VarPat | TuplePat)` | Phase 34 (this phase) | Extended dispatch in ForInExpr arm |

**Currently unsupported (falls to `failwithf`):** `StringSliceExpr`, `ListCompExpr`, `ForInExpr` with `TuplePat`, `ForInExpr` over HashSet/Queue/MutableList/Hashtable.

## Open Questions

1. **ElabEnv CollectionVars tracking scope**
   - What we know: `ArrayVars` is tracked via `Set<string>` in `ElabEnv`. New collection types need similar tracking for `isHashSetExpr`, `isQueueExpr`, etc.
   - What's unclear: Whether to add separate `Set<string>` fields for each type, or a `Map<string, CollectionKind>` combined field.
   - Recommendation: Use `CollectionVars: Map<string, CollectionKind>` where `CollectionKind = Array | HashSet | Queue | MutableList | Hashtable`. Add to `ElabEnv` and update all `Let`/`LetPat` binding sites. This is a ~10-line addition.

2. **ForInExpr TuplePat — I64→Ptr coercion placement**
   - What we know: Lambda param arrives as `int64_t` (the closure ABI). Tuple GEP requires `Ptr`. The existing `LetPat(TuplePat, ...)` elaboration takes a `Ptr`-typed value (`tupPtrVal`).
   - What's unclear: Exactly where to inject the `LlvmIntToPtrOp` — inside the lambda body or at the call site.
   - Recommendation: Inject it inside the lambda body as the first op: convert the `I64` parameter to `Ptr` via `LlvmIntToPtrOp`, then feed that `Ptr` to `bindTuplePat`. The lambda always receives tuples as `int64_t` casts of heap pointers.

3. **ListCompExpr for arrays — `lang_list_comp_array` needed?**
   - What we know: `ListCompExpr` takes a collection. If the collection is an array (result of `array_of_list`, etc.), `lang_list_comp` won't work (it casts to `LangCons*`).
   - What's unclear: Whether the requirements include `[for x in arr -> expr]` for array inputs.
   - Recommendation: LANG-02 says "list, array, or native collection" in LangThree Eval.fs. Add `lang_list_comp_array` if required, or add a runtime check in `lang_list_comp` similar to `lang_index_get`. For minimum viable, handle the collection type using the same `isArrayExpr` dispatch from ForInExpr.

## Sources

### Primary (HIGH confidence)
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Ast.fs` — `StringSliceExpr`, `ListCompExpr`, `ForInExpr` AST node definitions
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Eval.fs` — Reference semantics for all four constructs
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Parser.fsy` — Parse rules confirming `ListCompExpr` range form desugars to `Range` node
- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/Elaboration.fs` — All existing elaboration patterns (ForInExpr, LetPat TuplePat, Range, Tuple)
- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/lang_runtime.c` — All existing C runtime functions
- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/lang_runtime.h` — All struct definitions and prototypes
- `.planning/phases/33-collection-types/33-RESEARCH.md` — Patterns and pitfalls from immediately preceding phase

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all files directly inspected, patterns confirmed in code
- Architecture: HIGH — ForInExpr, LetPat TuplePat, Tuple construction all verified in Elaboration.fs
- Pitfalls: HIGH — externalFuncs×2 confirmed by reading both lists; I64/Ptr coercion confirmed by reading ForInExpr arm; sentinel=-1 concern confirmed by reading `lang_string_sub`

**Research date:** 2026-03-29
**Valid until:** 2026-04-29 (stable codebase, no external dependencies)
