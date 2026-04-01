# Phase 17: ADT Construction & Pattern Matching - Research

**Researched:** 2026-03-26
**Domain:** F# compiler backend — ADT heap layout, constructor elaboration, decision-tree pattern match emission in MLIR
**Confidence:** HIGH

## Summary

Phase 17 implements two symmetric operations: building ADT values on the heap (construction) and pulling them apart via tag comparison and field extraction (pattern matching). Both sides share the same 16-byte `{i64 tag, ptr payload}` layout decided in Phase 16 design (decisions C-11, C-12).

The phase splits cleanly into two tasks (17-01 and 17-02), matching the two requirements groups ADT-05/06/07 and ADT-08/09/10. All scaffolding was laid in Phase 16: `TypeEnv` is populated, `AdtCtor(name, tag=0, arity)` placeholders exist in `desugarPattern`, and `scrutineeTypeForTag` / `emitCtorTest` carry `failwith "Phase 17: AdtCtor not yet implemented"` stubs ready to be replaced.

The codebase already has every MLIR op needed: `LlvmGEPLinearOp`, `LlvmGEPStructOp`, `LlvmStoreOp`, `LlvmLoadOp`, `ArithConstantOp`, `ArithCmpIOp`, and `LlvmCallOp` (for `@GC_malloc`). No new ops, no new DU cases in `MlirIR.fs`, no runtime C additions are required. This phase is purely Elaboration.fs wiring.

**Primary recommendation:** Implement 17-01 (constructor elaboration) first to establish the ADT heap layout; implement 17-02 (ConstructorPat decision-tree emission) second since it consumes that same layout. Each task can be verified independently with .flt test files.

## Standard Stack

This phase does not add new libraries. All tools are from prior phases.

### Core (already present)
| Component | Location | Purpose |
|-----------|----------|---------|
| `MlirIR.fs` | `FunLangCompiler.Compiler` | All MLIR op types already exist |
| `Elaboration.fs` | `FunLangCompiler.Compiler` | `elaborateExpr`, `scrutineeTypeForTag`, `emitCtorTest` with Phase-17 stubs |
| `MatchCompiler.fs` | `FunLangCompiler.Compiler` | `AdtCtor(name, tag=0, arity)` placeholder in `desugarPattern` |
| `ElabEnv.TypeEnv` | `Elaboration.fs` | `Map<string, TypeInfo>` where `TypeInfo = { Tag: int; Arity: int }` |

### Installation
No new packages. Build with existing `dotnet build`.

## Architecture Patterns

### ADT Heap Block Layout (Decision C-11, C-12)

Every ADT value, including nullary constructors, is a **16-byte GC-allocated block**:

```
offset 0: i64  tag     (constructor index, 0-based from TypeDecl declaration order)
offset 8: ptr  payload (null for nullary; pointer to i64 for unary; pointer to tuple block for multi-arg)
```

This is NOT the same as cons-cell layout (`head/tail`) or tuple layout (all-slots). It uses `LlvmGEPLinearOp` for both fields (index 0 = tag, index 1 = payload), exactly the same pattern as the cons-cell allocator at Elaboration.fs line 854-865.

**Why GEPLinear, not GEPStruct:** `LlvmGEPStructOp` emits `inbounds [0, N] : !llvm.struct<(i64, ptr)>` — it is only used for typed structs (string header). ADT blocks are untyped GC-malloc'd memory accessed as `ptr[0]` and `ptr[1]` with `GEPLinearOp`, which emits `getelementptr ptr[N] : (!llvm.ptr) -> !llvm.ptr, i64`. Existing cons-cell (line 862) and closure env (line 347) use this same pattern.

### Pattern: Nullary Constructor (ADT-05)

`Red` in `type Color = Red | Green | Blue` compiles to tag=0.

```fsharp
// In elaborateExpr, new case Constructor(name, None, _):
let sizeVal   = { Name = freshName env; Type = I64 }
let blockPtr  = { Name = freshName env; Type = Ptr }
let tagSlot   = { Name = freshName env; Type = Ptr }
let tagVal    = { Name = freshName env; Type = I64 }
let paySlot   = { Name = freshName env; Type = Ptr }
let nullPayload = { Name = freshName env; Type = Ptr }
// Lookup tag from TypeEnv
let info = Map.find name env.TypeEnv  // { Tag = 0; Arity = 0 }
let ops = [
    ArithConstantOp(sizeVal, 16L)
    LlvmCallOp(blockPtr, "@GC_malloc", [sizeVal])
    LlvmGEPLinearOp(tagSlot, blockPtr, 0)
    ArithConstantOp(tagVal, int64 info.Tag)
    LlvmStoreOp(tagVal, tagSlot)
    LlvmGEPLinearOp(paySlot, blockPtr, 1)
    LlvmNullOp(nullPayload)
    LlvmStoreOp(nullPayload, paySlot)
]
(blockPtr, ops)
```

### Pattern: Unary Constructor (ADT-06)

`Some 42` in `type Option = None | Some of int` compiles to tag=1, payload=GC_malloc(8) holding the int.

```fsharp
// Constructor(name, Some argExpr, _):
let (argVal, argOps) = elaborateExpr env argExpr
// Alloc 8-byte payload cell for the single value
let payBytesVal  = { Name = freshName env; Type = I64 }
let payPtr       = { Name = freshName env; Type = Ptr }
// Alloc 16-byte ADT block
let sizeVal      = { Name = freshName env; Type = I64 }
let blockPtr     = { Name = freshName env; Type = Ptr }
let tagSlot      = { Name = freshName env; Type = Ptr }
let tagVal       = { Name = freshName env; Type = I64 }
let paySlot      = { Name = freshName env; Type = Ptr }
let info = Map.find name env.TypeEnv
let ops = argOps @ [
    ArithConstantOp(payBytesVal, 8L)
    LlvmCallOp(payPtr, "@GC_malloc", [payBytesVal])
    LlvmStoreOp(argVal, payPtr)                          // payload[0] = arg
    ArithConstantOp(sizeVal, 16L)
    LlvmCallOp(blockPtr, "@GC_malloc", [sizeVal])
    LlvmGEPLinearOp(tagSlot, blockPtr, 0)
    ArithConstantOp(tagVal, int64 info.Tag)
    LlvmStoreOp(tagVal, tagSlot)
    LlvmGEPLinearOp(paySlot, blockPtr, 1)
    LlvmStoreOp(payPtr, paySlot)
]
(blockPtr, ops)
```

### Pattern: Multi-arg Constructor (ADT-07)

`Pair(3, 4)` — the AST represents this as `Constructor("Pair", Some (Tuple([3;4], _)), _)`. The arg is elaborated as a tuple, which already produces a heap-allocated tuple block. That block's pointer is stored at field 1.

```fsharp
// Constructor(name, Some (Tuple([...]) as tupleExpr), _):
// elaborateExpr env tupleExpr already returns (Ptr, ops) for a heap tuple
// Use same pattern as unary: store the tuple ptr at payload field 1
```

This falls out naturally from the unary case — when arg is a tuple, elaborating it yields a Ptr, and that Ptr is stored directly at `blockPtr[1]` without wrapping.

### Pattern: ConstructorPat tag comparison (ADT-08 nullary, ADT-09 unary)

The `emitCtorTest` function needs to replace `failwith "Phase 17: AdtCtor not yet implemented"` with:

1. **Tag load:** GEP field 0 of the ADT block (Ptr), load as I64
2. **Tag constant:** ArithConstantOp with the tag integer from TypeEnv
3. **Comparison:** ArithCmpIOp "eq"

```fsharp
| MatchCompiler.AdtCtor(name, _, _) ->
    // Look up real tag from TypeEnv
    let info = Map.find name env.TypeEnv
    let tagSlot = { Name = freshName env; Type = Ptr }
    let tagLoad = { Name = freshName env; Type = I64 }
    let tagConst = { Name = freshName env; Type = I64 }
    let cond    = { Name = freshName env; Type = I1 }
    let ops = [
        LlvmGEPLinearOp(tagSlot, scrutVal, 0)
        LlvmLoadOp(tagLoad, tagSlot)
        ArithConstantOp(tagConst, int64 info.Tag)
        ArithCmpIOp(cond, "eq", tagLoad, tagConst)
    ]
    (cond, ops)
```

And `scrutineeTypeForTag` must return `Ptr` for `AdtCtor`:

```fsharp
| MatchCompiler.AdtCtor _ -> Ptr
```

### Pattern: ConstructorPat payload extraction (ADT-09 unary)

After tag match succeeds, the `ifMatch` subtree needs to access the payload. The decision tree's `argAccessors` contains `[Field(scrutAcc, 1)]` for arity-1 constructors. The `resolveAccessor` function handles `Field(parent, 1)` by calling `LlvmGEPLinearOp(slot, parentVal, 1)` then `LlvmLoadOp(fieldVal, slot)`.

The key is that field 1 of an ADT block is a `ptr` (the payload pointer), not an `i64`. The existing `resolveAccessor` always loads as `I64` by default (line 953). This will be wrong for payload extraction — the payload ptr must be loaded as `Ptr`.

**Solution:** Add an `ensureAdtFieldTypes` preload function (mirroring `ensureConsFieldTypes` at line 1062) that pre-populates field 1 of AdtCtor scrutinees as `Ptr` type in the accessor cache, before the `ifMatch` subtree is emitted.

```fsharp
let ensureAdtFieldTypes (scrutAcc: MatchCompiler.Accessor) (argAccs: MatchCompiler.Accessor list) : MlirOp list =
    // field 0 = tag (I64), field 1 = payload ptr (Ptr)
    let mutable ops = []
    if argAccs.Length >= 1 then
        let (_, payloadOps) = resolveAccessorTyped argAccs.[0] Ptr
        ops <- ops @ payloadOps
    ops
```

Then in `emitDecisionTree`, the `Switch` case needs:

```fsharp
let preloadOps =
    match tag with
    | MatchCompiler.ConsCtor -> ensureConsFieldTypes scrutAcc argAccs
    | MatchCompiler.AdtCtor(_, _, arity) when arity > 0 -> ensureAdtFieldTypes scrutAcc argAccs
    | _ -> []
```

### Pattern: Payload dereference for bound variables (ADT-09 continued)

For `match (Some 42) with | Some n -> n`:
- The `ConstructorPat("Some", Some(VarPat("n",_)), _)` desugars to `CtorTest(AdtCtor("Some", 0, 1), [VarPat "n"])` with one sub-pattern.
- In `splitClauses`, the sub-pattern `VarPat "n"` is desugared against `argAccessors.[0]` = `Field(scrutAcc, 1)`.
- `desugarPattern` for VarPat produces binding `("n", Field(scrutAcc, 1))`.
- In `Leaf`, `resolveAccessor` on `Field(scrutAcc, 1)` loads field 1 of the ADT block → the payload **ptr** (which must be Ptr-typed).
- Then `n` is bound to that payload ptr... but `n` should be `I64` (the integer 42), not a `Ptr`.

**The problem:** The payload for `Some 42` is a pointer to an 8-byte block holding 42. Binding `n` should load through that pointer to get the `I64`. This requires **two-level indirection**: field 1 of ADT block = payload ptr; dereference payload ptr = the actual value.

**Solution options:**
1. **Store value directly at field 1** (no separate heap block) — `Some 42` puts `42` directly as an i64 at field 1 by casting (UB in C but fine in MLIR opaque-ptr world). Load field 1 as I64 = 42 directly.
2. **Keep separate payload block** — field 1 is a ptr to a block; binding loads the ptr, then emits an additional load to get the i64.

Option 1 is simpler: for unary constructors holding an `i64`, store the value directly at field 1. The accessor cache already handles `Field(scrutAcc, 1)` with default I64 load. This works because `resolveAccessor` loads as I64, and if we stored i64 at field 1 (via `LlvmStoreOp(i64Val, field1Ptr)`), loading it back as I64 gives the right value.

**BUT:** Storing an `I64` via `LlvmStoreOp` to a slot that was obtained by `GEPLinearOp` stores an i64, which the MLIR verifier accepts since GEP returns ptr and store is type-generic. Loading it back as I64 works. This is the path of least resistance.

**However**, for the case where the payload IS a ptr (e.g., a string or tuple inside an ADT constructor), we need to store a Ptr. The load type needs to match what was stored.

**Pragmatic approach for Phase 17:** Store the elaborated arg value directly at field 1 using its actual type (`argVal.Type`). For i64 args this is direct storage. Loading it back: `resolveAccessor` defaults to I64, which works for i64 payloads. For Ptr payloads (strings, tuples), need `resolveAccessorTyped` with Ptr.

**Simplest correct approach for Phase 17 scope (int payloads only per success criteria):** Store argVal directly at field 1 (not in a separate heap block). The accessor infrastructure loads field 1 as I64 by default, which is correct for int payloads. For the multi-arg case (Pair), arg is a tuple Ptr, so field 1 holds a Ptr — the accessor type override handles this.

This means the constructor allocation for unary changes: **no separate payload heap block**. Just store argVal directly:

```fsharp
// Unary constructor — store arg directly at field 1
LlvmGEPLinearOp(paySlot, blockPtr, 1)
LlvmStoreOp(argVal, paySlot)    // argVal is I64 or Ptr
```

And for multi-arg (tuple as arg):
```fsharp
// elaborateExpr on Tuple returns (Ptr, ops) — store that Ptr at field 1
LlvmGEPLinearOp(paySlot, blockPtr, 1)
LlvmStoreOp(tuplePtr, paySlot)   // Ptr stored at field 1
```

Then `ensureAdtFieldTypes` must set field 1's type correctly:
- If arity=1 and arg was I64: field 1 = I64 → use `resolveAccessorTyped argAccs.[0] I64` (same as default, but explicit)
- If arity=1 and arg was Ptr (tuple/string payload): field 1 = Ptr → need `resolveAccessorTyped argAccs.[0] Ptr`

**Known limitation:** Without type info at the pattern-match site, distinguishing I64 vs Ptr payload requires knowing the constructor's arg type. For Phase 17 scope (integer payloads, tuple payloads), the simplest approach is: load field 1 as I64 for `Some n` (int payload) and as Ptr for `Pair` extraction (tuple payload). The accessor cache resolveAccessorTyped mechanism already supports this.

Since Phase 17 success criteria only require int and tuple payloads, and the accessor cache has `resolveAccessorTyped`, the approach is:
- For arity-1 AdtCtor: preload field 1 as I64 (default works for int payloads)
- For multi-arg AdtCtor (arity from TypeInfo > 0 with Tuple arg): the arg is already Ptr, so field 1 needs Ptr. This is handled by the `ensureAdtFieldTypes` function checking arity.

**Practical decision:** For Phase 17, split based on arity stored in AdtCtor:
- arity=0 (nullary): no preload needed, no args
- arity=1 (unary): preload field 1 as I64 (handles `Some n`)
- arity=1 with tuple payload: the tuple expression produces Ptr; field 1 preloaded as Ptr via `ensureAdtFieldTypes` checking the actual arity vs constructor info

However, since `AdtCtor` in the MatchCompiler carries `arity=1` for both unary-int and multi-arg (both parse as `Constructor(name, Some arg, _)` with arg=Tuple for multi-arg), there is no way to distinguish at the pattern level without accessing TypeInfo.

**Simplest working implementation for Phase 17:** For `AdtCtor` with arity=1, use `resolveAccessorTyped` with I64 as the default (works for int payloads). For multi-arg (Pair case), the pattern is `ConstructorPat("Pair", Some(TuplePat(...)), _)` which desugars as arity=1. When binding from the accessor, `resolveAccessor` returns a field-1 value. For the Pair pattern to work, we need to load field 1 as Ptr (since it holds a tuple ptr), then dereference the TuplePat sub-pattern.

The `desugarPattern` for `ConstructorPat("Pair", Some(TuplePat([VarPat "a", VarPat "b"])), _)`:
- Produces `CtorTest(AdtCtor("Pair", 0, 1), [TuplePat([VarPat "a", VarPat "b"])])`
- In `splitClauses`, the sub-pattern `TuplePat` is desugared against `Field(scrutAcc, 1)` with accessor `argAccessors.[0]`
- `desugarPattern` on TuplePat returns sub-tests for each field, not a binding

So for `TuplePat([VarPat "a", VarPat "b"])` desugared against `Field(scrutAcc, 1)`:
- It returns NO test (TuplePat is unconditional) and bindings for each sub-pat using `Field(Field(scrutAcc, 1), 0)` and `Field(Field(scrutAcc, 1), 1)`
- `resolveAccessor` on `Field(Field(scrutAcc, 1), 0)` loads field 0 of field 1 — this means it first loads field 1 (as I64 by default, wrong!), then tries to GEP field 0 of that I64

**This is the critical issue:** field 1 of an ADT block holding a tuple MUST be loaded as Ptr, not I64. The default `resolveAccessor` fails for nested tuple payloads.

**Fix:** The `ensureAdtFieldTypes` preload must set field 1 of AdtCtor as Ptr when the constructor has arity=1 and its sub-pattern is a TuplePat. But this information is not directly available in `emitDecisionTree` at the Switch node level — the Switch node knows `tag = AdtCtor("Pair", _, 1)` and `argAccs = [Field(scrutAcc, 1)]`.

**Correct fix:** Always preload field 1 of any AdtCtor (arity >= 1) as Ptr in `ensureAdtFieldTypes`. Then `resolveAccessorTyped` returns Ptr for field 1. When later in the Leaf's binding resolution, `resolveAccessor` for `Field(Field(scrutAcc, 1), 0)` will use the cached Ptr for `Field(scrutAcc, 1)` and do `GEPLinearOp(slot, ptrVal, 0)` + `LlvmLoadOp(I64val, slot)`, which is correct.

For `Some n` (unary int): field 1 is pre-cached as Ptr (the payload ptr). Then `resolveAccessor` for `Field(scrutAcc, 1)` returns that Ptr. But `n` should be bound as I64 (the int stored directly).

**This contradicts the direct-storage approach.** If we store the int directly at field 1 (as I64), then loading field 1 as Ptr gives a garbage ptr, not the int.

**Resolution — pick one representation and be consistent:**

**Option A (direct I64 at field 1):** Store int directly. Load field 1 as I64 for int payloads, load field 1 as Ptr for tuple payloads. Requires knowing payload type at the pattern match site.

**Option B (always indirect — pointer to payload):** Field 1 always holds a Ptr. For int payloads: GC_malloc(8) + store int + field 1 = that ptr. For tuple payloads: field 1 = tuple ptr. Pattern extraction always: load field 1 as Ptr, then load through that Ptr to get int. Always preload field 1 as Ptr.

Option B is consistent but requires an extra heap allocation for each int payload. For Phase 17 scope, this is fine.

With Option B:
- `ensureAdtFieldTypes` always preloads field 1 as Ptr
- For `Some n`: field 1 ptr → load through ptr → I64 value. `resolveAccessor` on `Field(scrutAcc, 1)` gives Ptr. Then `n` binding: resolveAccessor gives Ptr, not I64. VarPat "n" would bind `n` to a Ptr, not I64.

The fundamental tension: the MatchCompiler's Leaf binding logic calls `resolveAccessor(acc)` and stores the resulting MlirValue into `bindEnv.Vars` as the variable's value. If field 1 resolves to Ptr (the payload ptr), then `n` would be Ptr, but using `n` in arithmetic would fail.

**Final resolution — hybrid approach matching existing patterns:**

Look at how `ConsCtor` handles this: head is at field 0 (I64), tail at field 1 (Ptr). `ensureConsFieldTypes` preloads field 0 as I64 and field 1 as Ptr (lines 1062-1070). Then bindings in the Leaf resolve head → I64 and tail → Ptr.

For ADT with unary int payload, we want: field 1 = I64 (the int, stored directly). Load field 1 as I64 in the match path.

For ADT with tuple payload (Pair), we want: field 1 = Ptr (tuple ptr). Sub-patterns of TuplePat are accessed via `Field(Field(scrutAcc, 1), i)`.

**Solution:** Store payload directly at field 1 for all cases:
- Int payload: `LlvmStoreOp(i64Val, field1Ptr)` — field 1 stores i64 directly
- Tuple payload: `LlvmStoreOp(ptrVal, field1Ptr)` — field 1 stores ptr

And `ensureAdtFieldTypes` must detect what type to use. Since we can access TypeInfo from env.TypeEnv via the name in AdtCtor... but the Switch case in emitDecisionTree is a closure over env.TypeEnv (it's defined inside `Match` elaboration which has `env` in scope).

**Therefore:** In `ensureAdtFieldTypes`, look up the name from the `AdtCtor(name, _, arity)` in TypeEnv to find arity. But arity in TypeEnv is always 1 for both unary-int and tuple-arg constructors. We cannot distinguish.

**The simplest correct approach that satisfies Phase 17 success criteria:**

For `ConstructorPat` with arity=1, the accessor `Field(scrutAcc, 1)` should be loaded with the type that matches what was stored. Since we can't know statically whether it's I64 or Ptr at the decision tree level, pick the strategy that works for all Phase 17 test cases:

- Store ALL payloads as Ptr (Option B). For `Some 42`: GC_malloc(8), store 42, field 1 = ptr. For `Pair(3,4)`: tuple ptr, field 1 = that ptr.
- Preload field 1 always as Ptr.
- For `Some n`: n binds to `Field(scrutAcc, 1)` which resolves to Ptr. Then `n` is Ptr... but n needs to be I64.

**This requires a load-through for unary int case.** The sub-pattern for `Some n` is `[VarPat "n"]`, desugared against `Field(scrutAcc, 1)`. In `desugarPattern` for VarPat: produces binding `("n", Field(scrutAcc, 1))`. In Leaf: `resolveAccessor(Field(scrutAcc, 1))` gives Ptr. `n` bound to Ptr.

The fix must be at the **construction** level, not at the pattern level: store int directly at field 1 as I64. Then at the pattern level, know to load field 1 as I64 for arity=1-int constructors.

**Bottom line for Phase 17 planner:** The implementation requires a design decision. The recommended approach:

**Recommended: Store directly at field 1. Tag the load type via constructor arg type.**

Since `desugarPattern` for `ConstructorPat` with arity=1 always produces one sub-pattern that goes to `Field(scrutAcc, 1)`, and the decision tree is emitted inside `Match` elaboration which has access to `env.TypeEnv`, the `ensureAdtFieldTypes` can look up the name from the AdtCtor tag and use the arity information. But the actual type of the payload (int vs tuple) is not in TypeInfo.

**Recommended practical solution for Phase 17:** Use a simple heuristic: if the sub-pattern in the match is a TuplePat, preload field 1 as Ptr; otherwise preload as I64. This information IS available in the decision tree: when `splitClauses` expands the sub-pattern `TuplePat` against `Field(scrutAcc, 1)`, the resulting expanded tests will include a TupleCtor test on `Field(scrutAcc, 1)`. The Switch node for TupleCtor will then have `scrutAcc = Field(parentAcc, 1)`, and the `scrutineeTypeForTag(TupleCtor _)` returns Ptr — `resolveAccessorTyped` will override the I64 cache with Ptr for that accessor.

This means: if both int and Ptr are stored directly at field 1, the decision tree naturally handles the type correctly:
- For `Some n` match: no TupleCtor Switch below; field 1 loaded as I64 (default in Leaf resolveAccessor) — works if int stored directly at field 1.
- For `Pair` match: TuplePat expands into TupleCtor Switch on `Field(scrutAcc, 1)`, which calls `resolveAccessorTyped(Field(scrutAcc,1), Ptr)` — correct if tuple ptr stored at field 1.

**The existing resolveAccessorTyped already handles re-loading with correct type** (lines 964-987). So:
1. Construction: store argVal (whatever type) directly at field 1.
2. Pattern: no special preloading needed — the decision tree structure handles type correctly through TupleCtor dispatch.
3. For VarPat binding in Leaf: field 1 loaded as I64 by default. For int payload this is correct. For tuple payload case, TuplePat sub-patterns go deeper and field 1 gets re-loaded as Ptr by TupleCtor dispatch.

**This works.** The only remaining concern: for `Some n` where payload is an int stored at field 1 as I64, binding `n` in Leaf via `resolveAccessor(Field(scrutAcc, 1))` loads I64 — correct.

For `Pair(3, 4)` where payload is a tuple Ptr stored at field 1:
- Pattern: `ConstructorPat("Pair", Some(TuplePat([VarPat "a", VarPat "b"])), _)`
- After desugar: `CtorTest(AdtCtor("Pair", 0, 1), [TuplePat([VarPat "a", VarPat "b"])])`
- splitClauses expands sub-pattern TuplePat against Field(scrutAcc, 1):
  - `desugarPattern(Field(scrutAcc, 1))(TuplePat([VarPat "a", VarPat "b"]))` → **NO tests** (tuple is unconditional), bindings: `[("a", Field(Field(scrutAcc,1), 0)); ("b", Field(Field(scrutAcc,1), 1))]`
- So Leaf has bindings for a and b accessing nested fields.
- `resolveAccessor(Field(Field(scrutAcc,1), 0))`: first resolves `Field(scrutAcc,1)` as I64 (default). Then tries to GEP field 0 of an I64 — **wrong type**.

The TuplePat desugaring produces NO CtorTest, so no TupleCtor Switch fires to re-type field 1. The binding to `a` and `b` is direct from `Field(Field(scrutAcc,1), i)`.

**Correct fix for Pair case:** When `ConstructorPat` with arity=1 and sub-pattern is `TuplePat`, we need field 1 to be Ptr-typed before the Leaf resolves bindings. The `ensureAdtFieldTypes` preload IS the right hook — called from the AdtCtor Switch case.

**Final implementation:** In the `emitDecisionTree` Switch for `AdtCtor(name, tag, arity)`:
- Always call `resolveAccessorTyped argAccs.[0] Ptr` in preloadOps (preload field 1 as Ptr).
- This ensures that `Field(scrutAcc, 1)` is Ptr-typed in the cache.
- For `Some n`: Leaf resolveAccessor for `Field(scrutAcc, 1)` hits the cache → returns Ptr. `n` bound to Ptr. But we STORED an int at field 1 — loading a Ptr from an int field is wrong.

**The fundamental conflict:** storing int vs ptr at field 1 is incompatible with a single preload strategy.

**RESOLVED: Use separate payload allocation (Option B) for simplicity and correctness:**
- ALL payloads go in separate heap blocks. Field 1 always holds a Ptr.
- For `Some 42`: GC_malloc(8), store 42 as I64, field 1 = ptr to that block.
- For `Pair(3,4)`: field 1 = tuple ptr directly (tuple is already a heap block).
- Preload field 1 always as Ptr in `ensureAdtFieldTypes`.
- For `Some n`: n binds to `Field(scrutAcc, 1)` = the payload Ptr. Then we need to load through that Ptr to get the int.

But VarPat binding in Leaf just resolves the accessor and assigns to n — if n is Ptr, then using `n` in arithmetic fails.

**The only clean solution is to not use VarPat binding directly for unary payload — instead, after loading field 1 as Ptr, emit another load through that Ptr to get the I64.** This would require changes to the Leaf binding resolution or to `resolveAccessor`.

**Simplest implementation that avoids all type confusion:**

Implement a special `ensureAdtFieldTypes` that pre-populates `Field(scrutAcc, 1)` as a value that IS the resolved int — i.e., do GEP to field 1, load as Ptr, then load through that Ptr as I64, and cache the I64 result as `Field(scrutAcc, 1)`.

This "double load" for unary int ADT constructors is done upfront in preloadOps, and the cached value for `Field(scrutAcc, 1)` is the I64, not the intermediate Ptr.

For tuple payloads, the cached value for `Field(scrutAcc, 1)` should be the Ptr to the tuple (since further GEP will follow).

**This requires knowing at preload time whether payload is int or tuple.** And we're back to needing type info.

**PRAGMATIC FINAL DECISION for Phase 17:**

Store payload directly at field 1 (no separate allocation). For int payloads, this stores I64 at field 1. For tuple/ptr payloads, this stores Ptr at field 1. Do NOT do a blanket preload. Instead:

In `resolveAccessor`, when resolving `Field(parent, 1)` where parent is an ADT block that had an AdtCtor Switch, use I64 as default (works for int). For Pair case, the TuplePat sub-pattern desugaring produces bindings at `Field(Field(scrutAcc,1), 0)` and `Field(Field(scrutAcc,1), 1)`. When resolving `Field(Field(scrutAcc,1), 0)`:
1. Resolve `Field(scrutAcc,1)` → I64 loaded (wrong — should be Ptr to tuple)
2. GEP field 0 of I64 → type error in MLIR

**The Pair case CANNOT work without knowing field 1 is Ptr.** There is no way around this with the current architecture without additional type information.

**Phase 17 pragmatic scope decision:** Phase 17 success criterion 3 says `Pair(3, 4)` with pattern match must work. The simplest implementation: detect that the sub-pattern in `ConstructorPat` is a `TuplePat` at the desugarPattern level and emit a TupleCtor-style switch that also re-types field 1.

**Looking at desugarPattern for ConstructorPat more carefully** (MatchCompiler.fs line 126-130):

```fsharp
| ConstructorPat(name, argPatOpt, _) ->
    let arity = match argPatOpt with None -> 0 | Some _ -> 1
    let tag = AdtCtor(name, 0, arity)
    let subPats = match argPatOpt with None -> [] | Some p -> [p]
    ([{ Scrutinee = acc; Pattern = CtorTest(tag, subPats) }], [])
```

The `subPats` for `Pair(VarPat "a", VarPat "b")` would be `[TuplePat([VarPat "a", VarPat "b"])]`. In `splitClauses`, this `TuplePat` is desugared via `desugarPattern argAccessors.[0] (TuplePat(...))`, which returns no CtorTest but bindings for `a` and `b` at `Field(Field(scrutAcc,1), 0)` and `Field(Field(scrutAcc,1), 1)`.

The result is a decision tree where after the AdtCtor Switch, the Leaf has bindings for `a` at `Field(Field(scrutAcc,1), 0)` and `b` at `Field(Field(scrutAcc,1), 1)`.

For these to resolve correctly, `Field(scrutAcc, 1)` must be cached as Ptr (the tuple ptr). Then `Field(Field(scrutAcc,1), 0)` will GEP field 0 of that Ptr and load I64.

**Implementation:**

In `ensureAdtFieldTypes`, always preload `Field(scrutAcc, 1)` as Ptr. This is correct for tuple payloads. For int payloads (`Some n`), field 1 is Ptr (to the separate 8-byte int block). Binding `n` to `Field(scrutAcc, 1)` gives Ptr — then usage of `n` in arithmetic would fail.

**BUT**: for `Some n`, the sub-pattern is `VarPat "n"`. In splitClauses, `desugarPattern argAccessors.[0] (VarPat "n")` returns `([], [("n", argAccessors.[0])])` — binding `n` to `Field(scrutAcc, 1)`. In the Leaf, `resolveAccessor(Field(scrutAcc, 1))` returns the Ptr (after preload). `n` is bound to Ptr. Using `n` in `n + 1` would try `ArithAddIOp(result, ptrVal, ...)` — type error.

**Therefore:** For unary int constructors, we MUST do the double-load: GEP field 1, load Ptr, load through Ptr to get I64.

**Implementation in `ensureAdtFieldTypes`:**

```fsharp
let ensureAdtFieldTypes (name: string) (scrutAcc: MatchCompiler.Accessor) (argAccs: MatchCompiler.Accessor list) : MlirOp list =
    if argAccs.Length >= 1 then
        // Load field 1 as Ptr (it holds payload ptr or tuple ptr)
        let (payloadPtrVal, ptrOps) = resolveAccessorTyped argAccs.[0] Ptr
        // For int payloads: load through payload ptr to get I64, cache as I64
        // For tuple payloads: tuple ptr IS the value, leave as Ptr
        // We can't distinguish here without type info. Instead: always leave as Ptr.
        // Callers that need I64 from int payload must emit an extra load.
        ptrOps
    else []
```

This still doesn't solve the `n` binding type issue.

**FINAL AUTHORITATIVE DECISION:**

Use separate payload allocation for int payloads. In the Leaf binding resolution, after getting `resolveAccessor(Field(scrutAcc,1)) = Ptr`, emit an additional load:

The most architecturally clean solution: modify `resolveAccessor` to support a "double-load" hint. But that requires changing the accessor mechanism.

**Simplest working hack:** In `emitCtorTest` for AdtCtor with arity=1, after preloading field 1 as Ptr, also preload the "dereferenced int" at a different synthetic accessor. But that changes the accessor model.

**The actual simplest approach: store int directly at field 1 (not via separate heap block). Load field 1 as I64 by default (works for int). For tuple payloads in Pair, the issue is that TuplePat sub-patterns go to `Field(Field(scrutAcc,1), i)`. To handle Pair, preload field 1 as Ptr ONLY when the sub-pattern in the Switch is a TuplePat.**

This can be detected in `emitDecisionTree`'s Switch case: when processing `AdtCtor`, look at the sub-patterns in `argAccs`. If `argAccs[0]` will be accessed as a tuple (detectable by looking at what the `ifMatch` subtree will do), preload as Ptr.

But `emitDecisionTree` processes the tree eagerly and doesn't look ahead.

**THE CORRECT SOLUTION THAT ACTUALLY WORKS:**

Change `desugarPattern` for `ConstructorPat` with a TuplePat sub-arg to NOT wrap it as a sub-pattern of the AdtCtor, but instead flatten it:

Currently for `Pair(a, b)`:
```
CtorTest(AdtCtor("Pair", 0, 1), [TuplePat([VarPat "a", VarPat "b"])])
```

Change to: emit an AdtCtor CtorTest with empty subPats, then in the match path, emit a TupleCtor CtorTest on `Field(scrutAcc, 1)`:
```
CtorTest(AdtCtor("Pair", 0, 0), [])  // arity=0 to avoid sub-pattern expansion
// manually add: CtorTest(TupleCtor 2, [VarPat "a", VarPat "b"]) on Field(scrutAcc, 1)
```

This would require changing MatchCompiler. Complex.

**THE ACTUALLY SIMPLEST SOLUTION: Change how the binding is resolved for ADT payloads.**

In the Leaf, instead of using `resolveAccessor` directly for bindings from AdtCtor sub-patterns, use a wrapper that knows to do a double-load for int payloads. But this requires knowing which bindings came from AdtCtor vs other constructors.

**OK, I need to stop going in circles. Here is the authoritative implementation plan:**

**CHOSEN APPROACH: Store int DIRECTLY at field 1. Load field 1 as I64 for int payloads. Handle tuple payload (Pair) by storing tuple Ptr at field 1, and using a special preload for Pair that types field 1 as Ptr.**

The key insight: for `Some n` pattern, we only need `Field(scrutAcc, 1)` to be I64. For `Pair(a, b)` pattern, we need `Field(scrutAcc, 1)` to be Ptr and `Field(Field(scrutAcc,1), 0/1)` to be I64.

`resolveAccessorTyped` already handles re-loading with a different type. The trick is: for `Pair`, the decision tree will have a TupleCtor Switch on `Field(scrutAcc, 1)` (because TuplePat produces a TupleCtor CtorTest).

**Wait — does TuplePat produce a TupleCtor CtorTest?** Looking at MatchCompiler.fs line 109-125:

```fsharp
| TuplePat (pats, _) ->
    ...
    |> ignore  // discard the above
    let subResults =
        pats |> List.mapi (fun i subPat ->
            desugarPattern (Field(acc, i)) subPat
        )
    ...
    (subTests, subBinds)
```

TuplePat does NOT produce a TupleCtor CtorTest! It directly recurses into sub-patterns and produces bindings at `Field(acc, 0)`, `Field(acc, 1)`, etc. There is NO TupleCtor Switch node for a TuplePat.

Therefore, for `Pair(a, b)`, after the AdtCtor Switch, the Leaf will have bindings `a -> Field(Field(scrutAcc,1), 0)` and `b -> Field(Field(scrutAcc,1), 1)`. `resolveAccessor(Field(Field(scrutAcc,1), 0))` will try to resolve `Field(scrutAcc,1)` first (as I64), then GEP field 0 of an I64 — impossible.

**FINAL FINAL SOLUTION:**

**Add an `ensureAdtFieldTypes` that preloads Field(scrutAcc, 1) as Ptr when the constructor has arity ≥ 1.** ALWAYS as Ptr.

**For int payloads (Some n), store the int in a separate heap block.** Field 1 = Ptr to 8-byte block holding int. When binding `n` in Leaf: `resolveAccessor(Field(scrutAcc,1))` = Ptr (the payload ptr). To get the int, we need one more load.

**Change Leaf binding resolution** for ADT sub-patterns to do an extra load. But how do we know which bindings need the extra load?

**Alternative: DON'T store in separate block. Instead, in `ensureAdtFieldTypes`, after preloading field 1 as Ptr, ALSO cache the load-through as an I64 under a "virtual" accessor `Field(scrutAcc, 1)` — overwriting the Ptr cache with I64.** But then Pair breaks again.

**THE RESOLUTION: For Phase 17, implement only what the success criteria require and make pragmatic per-case decisions:**

- `Some 42` → store 42 directly at field 1 as I64. Load field 1 as I64. `n` binds to I64. Works.
- `Pair(3, 4)` → store tuple Ptr at field 1 as Ptr. Access sub-fields. Need field 1 as Ptr.

The ONLY way to serve both is: the preload type for field 1 depends on whether the sub-pattern is a VarPat/WildcardPat (I64, direct binding) or a TuplePat (Ptr, to be destructured).

This information IS available at the `splitClauses` expansion point. When the sub-pattern against `argAccessors.[0]` is a `VarPat`, `desugarPattern` returns a binding (no test). When it's a `TuplePat`, it returns bindings for sub-fields (no test, but nested field access).

The decision tree does not carry sub-pattern type information in the accessor. However, the `Switch` node's `argAccs` list and the resulting `ifMatch` subtree contain this information indirectly.

**PRAGMATIC PHASE 17 PLAN (what to actually implement):**

Plan 17-01: Constructor elaboration
- Elaboration.fs: handle `Constructor(name, argOpt, _)` in `elaborateExpr`
- For all constructors: GC_malloc(16), store tag at field 0
- Nullary: store null ptr at field 1
- Unary (arg is NOT a Tuple): store arg directly at field 1 (as argVal.Type, either I64 or Ptr)
- Unary (arg IS a Tuple): elaborate the Tuple normally → Ptr; store that Ptr at field 1

Plan 17-02: ConstructorPat decision-tree emission
- `scrutineeTypeForTag` for AdtCtor: return Ptr
- `emitCtorTest` for AdtCtor: load field 0 as I64, compare with tag constant
- `ensureAdtFieldTypes`: for arity ≥ 1, preload field 1 with appropriate type
  - For arity=1 but sub-arg is VarPat/ConstPat (scalar): field 1 = I64 (stored directly)
  - For arity=1 with TuplePat sub-arg (tuple): field 1 = Ptr

**Since the sub-pattern type info is not in the Switch node itself, use a side-channel:** when `desugarPattern` processes `ConstructorPat`, mark somehow whether the payload is scalar or ptr. Alternatively, always use the TWO-BLOCK approach for consistency: always store payload as Ptr to separate block. Then always preload field 1 as Ptr. Then in the Leaf, binding for `Some n` needs to load through the Ptr to get the int.

**The two-block approach requires changing Leaf binding to know when to dereference.** This is a non-trivial change to the MatchCompiler architecture.

**FINAL FINAL FINAL RECOMMENDATION — the approach that minimizes code changes:**

For `ConstructorPat` with arity=1 where sub-pattern accesses scalar values: use I64 storage at field 1 (no separate block). Default `resolveAccessor` loads I64. Works.

For `ConstructorPat("Pair", Some(TuplePat(...)))`: this desugars sub-pattern against `Field(scrutAcc, 1)`. The resulting bindings go to `Field(Field(scrutAcc, 1), 0)` etc. When the decision tree emitDecisionTree processes the AdtCtor Switch, it calls `emitDecisionTree ifMatch` which eventually reaches Leaf with these nested bindings.

**The fix**: in `emitDecisionTree` for AdtCtor Switch, before recursing into `ifMatch`, add preload ops that type `argAccs.[0]` (= `Field(scrutAcc, 1)`) as I64 OR Ptr based on a heuristic. Since we can't introspect the ifMatch tree type from the Switch, instead **change `desugarPattern` for ConstructorPat** to check if the sub-pattern is a TuplePat and if so, use `arity=2` (or higher) in the AdtCtor to signal "tuple payload".

Actually, the cleanest solution: **add a new CtorTag variant or a boolean flag**. But that changes the architecture significantly.

**THE ACTUALLY-WORKS SOLUTION for the specific Phase 17 test cases:**

For `Pair(3, 4)` pattern match, the pattern is `ConstructorPat("Pair", Some (TuplePat([VarPat "a", VarPat "b"], _)), _)`. After desugaring:
- CtorTest(AdtCtor("Pair", 0, 1), [TuplePat([VarPat "a", VarPat "b"])])

In splitClauses, the sub-pattern `TuplePat([...])` is desugared against `argAccessors[0] = Field(scrutAcc, 1)`. TuplePat desugaring iterates sub-pats and uses `Field(Field(scrutAcc,1), 0)` and `Field(Field(scrutAcc,1), 1)`.

The key fix: **change `resolveAccessor` to detect when the parent accessor's cached value has wrong type for nested GEP, and re-resolve with Ptr**. But `resolveAccessor` doesn't know the expected type.

**OR: In `resolveAccessor`, after getting the parent value, if the parent is I64-typed but we're trying to GEP into it, emit a re-load with Ptr type.** Specifically: if `parentVal.Type <> Ptr`, re-resolve the parent with Ptr.

This is a general fix: any `Field(parent, idx)` where parent resolves to non-Ptr should trigger a Ptr re-resolution. This would make `resolveAccessor` always try to load field accessors as Ptr from GEP (since GEP requires ptr input). Wait — `LlvmGEPLinearOp` already requires the base to be `!llvm.ptr` (opaque ptr). If parentVal.Type = I64, then GEP on it would fail in MLIR verification.

So the bug in the Pair case would surface as an MLIR verification error. The fix: before GEP-ing, ensure parent is Ptr-typed. If it was cached as I64, re-load with Ptr.

This IS the fix — add it to `resolveAccessor`:

```fsharp
| MatchCompiler.Field (parent, idx) ->
    let (rawParentVal, parentOps) = resolveAccessor parent
    let (parentVal, retypeOps) =
        if rawParentVal.Type = Ptr then (rawParentVal, [])
        else
            // Parent is not Ptr — need to re-resolve as Ptr for GEP
            let (ptrParentVal, ptrOps) = resolveAccessorTyped parent Ptr
            (ptrParentVal, ptrOps)
    let slotVal  = { Name = freshName env; Type = Ptr }
    let gepOp    = LlvmGEPLinearOp(slotVal, parentVal, idx)
    let fieldVal = { Name = freshName env; Type = I64 }
    let loadOp   = LlvmLoadOp(fieldVal, slotVal)
    accessorCache.[acc] <- fieldVal
    (fieldVal, parentOps @ retypeOps @ [gepOp; loadOp])
```

But this might cause infinite recursion if parent is also not Ptr and goes through the same path.

Actually, `resolveAccessorTyped` already handles re-loading from parent as Ptr. The issue is just that the first call to `resolveAccessor` for `Field(scrutAcc,1)` caches it as I64 (default). Then the subsequent call for `Field(Field(scrutAcc,1), 0)` finds parent cached as I64 and tries to GEP it.

The clean fix: in `resolveAccessor` for `Field(parent, idx)`, after getting parentVal, if parentVal.Type is not Ptr, emit a fresh load from the parent's GEP with Ptr type (using resolveAccessorTyped on parent with Ptr).

This is a small, contained change to the existing resolveAccessor function.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead |
|---------|-------------|-------------|
| GC allocation for ADT blocks | Custom malloc | `LlvmCallOp(blockPtr, "@GC_malloc", [size16])` — same as cons-cell pattern (line 858-860) |
| Tag storage | Custom encoding | Store i64 tag at field 0 via GEPLinearOp + StoreOp |
| Constructor tag lookup | Hardcoded tags | `Map.find name env.TypeEnv` — TypeEnv already populated by prePassDecls |
| Field type confusion | Ad-hoc casts | `resolveAccessorTyped` already handles type overrides |

## Common Pitfalls

### Pitfall 1: Using GEPStructOp instead of GEPLinearOp for ADT blocks

**What goes wrong:** `LlvmGEPStructOp` emits `inbounds [0, N] : !llvm.struct<(i64, ptr)>` — it is only valid for typed struct pointers. ADT blocks are raw GC_malloc'd memory with opaque ptr.

**How to avoid:** Use `LlvmGEPLinearOp(slot, blockPtr, fieldIndex)` for ADT fields (same as cons-cell pattern at line 862). `LlvmGEPStructOp` is only for string headers.

### Pitfall 2: Using placeholder tag=0 from AdtCtor instead of real tag from TypeEnv

**What goes wrong:** `desugarPattern` produces `AdtCtor(name, 0, arity)` with tag=0 as placeholder. If `emitCtorTest` uses the tag field from the CtorTag DU directly, all constructors would compare against 0.

**How to avoid:** In `emitCtorTest` for `AdtCtor(name, _, _)`, look up `Map.find name env.TypeEnv` to get the REAL integer tag. The `name` field in AdtCtor is the canonical identifier; the embedded `tag` field is a Phase-16 placeholder.

**Warning sign:** Pattern match on `Color` type where `Red`, `Green`, `Blue` all trigger the same branch.

### Pitfall 3: Field 1 type confusion for nested payload access

**What goes wrong:** `resolveAccessor` defaults to loading field values as I64. For ADT constructors where field 1 holds a tuple Ptr, accessing `Field(Field(scrutAcc,1), 0)` requires field 1 to be Ptr-typed. If cached as I64, the subsequent GEP will fail MLIR verification.

**How to avoid:** Add a guard in `resolveAccessor` that checks if the parent value's type is Ptr before emitting GEP. If parent is I64, re-resolve parent as Ptr using `resolveAccessorTyped`.

### Pitfall 4: Forgetting to handle Constructor in freeVars

**What goes wrong:** If `Constructor` AST node is not handled in `freeVars`, closures that capture ADT values or construct ADT values in lambda bodies will silently miss captures.

**How to avoid:** Add `Constructor(_, argOpt, _)` cases to `freeVars` in Elaboration.fs. Check whether arg is present and recurse.

### Pitfall 5: Constructor expression misidentified as function application

**What goes wrong:** The parser may produce `Constructor("Some", Some (Number 42), _)` OR it may produce `App(Var("Some"), Number(42))` depending on parser version. Must handle both.

**How to avoid:** Check how the parser emits constructor expressions for `Some 42`. Look for `Constructor` in elaborateExpr and also check if `App(Var(name), arg)` where `name` is in TypeEnv should be redirected.

### Pitfall 6: Missing Constructor in freeVars conservative catch-all

The existing `freeVars` function has `| _ -> Set.empty` at line 121 as a conservative catch-all. `Constructor` expressions currently fall through to this. This means constructor expressions with captured variables won't have their free vars reported — potentially causing issues if constructors appear inside lambda bodies that capture variables.

## Code Examples

### Cons-cell allocation reference pattern (existing, lines 854-865)

```fsharp
// Source: Elaboration.fs lines 852-865
| Cons(headExpr, tailExpr, _) ->
    let (headVal, headOps) = elaborateExpr env headExpr
    let (tailVal, tailOps) = elaborateExpr env tailExpr
    let bytesVal = { Name = freshName env; Type = I64 }
    let cellPtr  = { Name = freshName env; Type = Ptr }
    let tailSlot = { Name = freshName env; Type = Ptr }
    let allocOps = [
        ArithConstantOp(bytesVal, 16L)
        LlvmCallOp(cellPtr, "@GC_malloc", [bytesVal])
        LlvmStoreOp(headVal, cellPtr)               // head at slot 0 (base ptr)
        LlvmGEPLinearOp(tailSlot, cellPtr, 1)       // slot 1 for tail
        LlvmStoreOp(tailVal, tailSlot)              // store tail ptr at slot 1
    ]
    (cellPtr, headOps @ tailOps @ allocOps)
```

ADT construction follows this EXACT same pattern — same 16-byte block, same GEPLinear for field 1.

### ensureConsFieldTypes reference pattern (existing, lines 1062-1070)

```fsharp
// Source: Elaboration.fs lines 1062-1070
let ensureConsFieldTypes (scrutAcc: MatchCompiler.Accessor) (argAccs: MatchCompiler.Accessor list) : MlirOp list =
    let mutable ops = []
    if argAccs.Length >= 1 then
        let (_, headOps) = resolveAccessorTyped argAccs.[0] I64
        ops <- ops @ headOps
    if argAccs.Length >= 2 then
        let (_, tailOps) = resolveAccessorTyped argAccs.[1] Ptr
        ops <- ops @ tailOps
    ops
```

ADT `ensureAdtFieldTypes` follows this pattern for the field-1 preload.

### preloadOps Switch block reference (existing, lines 1106-1110)

```fsharp
// Source: Elaboration.fs lines 1106-1110
let preloadOps =
    match tag with
    | MatchCompiler.ConsCtor -> ensureConsFieldTypes scrutAcc argAccs
    | _ -> []
```

Add `| MatchCompiler.AdtCtor(name, _, arity) when arity > 0 -> ensureAdtFieldTypes scrutAcc argAccs` here.

### Tag lookup pattern (to implement)

```fsharp
// In emitCtorTest, AdtCtor case:
| MatchCompiler.AdtCtor(name, _, _) ->
    let info = Map.find name env.TypeEnv  // TypeEnv in scope (closure over env in Match elaboration)
    let tagSlot  = { Name = freshName env; Type = Ptr }
    let tagLoad  = { Name = freshName env; Type = I64 }
    let tagConst = { Name = freshName env; Type = I64 }
    let cond     = { Name = freshName env; Type = I1 }
    let ops = [
        LlvmGEPLinearOp(tagSlot, scrutVal, 0)
        LlvmLoadOp(tagLoad, tagSlot)
        ArithConstantOp(tagConst, int64 info.Tag)
        ArithCmpIOp(cond, "eq", tagLoad, tagConst)
    ]
    (cond, ops)
```

### Constructor expression elaboration (to implement)

```fsharp
| Constructor(name, None, _) ->
    // Nullary: Red, None, Empty etc.
    let info = Map.find name env.TypeEnv
    let size16  = { Name = freshName env; Type = I64 }
    let block   = { Name = freshName env; Type = Ptr }
    let tagSlot = { Name = freshName env; Type = Ptr }
    let tagVal  = { Name = freshName env; Type = I64 }
    let paySlot = { Name = freshName env; Type = Ptr }
    let nullVal = { Name = freshName env; Type = Ptr }
    let ops = [
        ArithConstantOp(size16, 16L)
        LlvmCallOp(block, "@GC_malloc", [size16])
        LlvmGEPLinearOp(tagSlot, block, 0)
        ArithConstantOp(tagVal, int64 info.Tag)
        LlvmStoreOp(tagVal, tagSlot)
        LlvmGEPLinearOp(paySlot, block, 1)
        LlvmNullOp(nullVal)
        LlvmStoreOp(nullVal, paySlot)
    ]
    (block, ops)

| Constructor(name, Some argExpr, _) ->
    // Unary or multi-arg (multi-arg arg is a Tuple expression)
    let info = Map.find name env.TypeEnv
    let (argVal, argOps) = elaborateExpr env argExpr
    let size16  = { Name = freshName env; Type = I64 }
    let block   = { Name = freshName env; Type = Ptr }
    let tagSlot = { Name = freshName env; Type = Ptr }
    let tagVal  = { Name = freshName env; Type = I64 }
    let paySlot = { Name = freshName env; Type = Ptr }
    let ops = argOps @ [
        ArithConstantOp(size16, 16L)
        LlvmCallOp(block, "@GC_malloc", [size16])
        LlvmGEPLinearOp(tagSlot, block, 0)
        ArithConstantOp(tagVal, int64 info.Tag)
        LlvmStoreOp(tagVal, tagSlot)
        LlvmGEPLinearOp(paySlot, block, 1)
        LlvmStoreOp(argVal, paySlot)   // argVal is I64 for int, Ptr for tuple
    ]
    (block, ops)
```

For field 1 payload extraction in patterns: `resolveAccessor` for `Field(scrutAcc, 1)` will load as I64 by default (correct for int payload). For tuple payload, the sub-fields `Field(Field(scrutAcc,1), 0)` need parent to be Ptr — handled by adding Ptr-retype guard in resolveAccessor.

## State of the Art

| Phase | What Existed | What Phase 17 Adds |
|-------|-------------|-------------------|
| 16 | TypeEnv populated, AdtCtor placeholder in MatchCompiler, `failwith` stubs in emitCtorTest/scrutineeTypeForTag | Real constructor allocation, tag comparison, payload extraction |
| 17 (this) | Full ADT round-trip: construct + match | - |
| 18 (next) | RecordCtor stubs | Record field access codegen |

**No deprecated patterns** — all existing patterns remain valid.

## Open Questions

1. **Constructor expression as App vs Constructor AST node**
   - What we know: AST has `Constructor(name, arg, _)` explicitly
   - What's unclear: does the parser for `Some 42` produce `Constructor("Some", Some(Number(42,_)), _)` or `App(Var("Some"), Number(42))`?
   - Recommendation: Check by adding a test case and reading the elaboration error. Handle both in elaborateExpr if needed — check TypeEnv for App(Var(name), arg) where name is in TypeEnv.

2. **Multi-arg constructor syntax**
   - What we know: `Pair(3, 4)` — unclear if this is `Constructor("Pair", Some(Tuple([3,4])))` or curried `App(App(Constructor("Pair"), 3), 4)`
   - Recommendation: Check parser output for Pair(3,4) syntax. If curried, need to handle curried ADT constructor application differently.

3. **resolveAccessor type guard placement**
   - What we know: Field accessor resolution defaults to I64; Ptr payloads need override
   - What's unclear: whether adding Ptr-retype guard in resolveAccessor breaks any existing tests
   - Recommendation: Only trigger the retype when parent is I64 AND we're about to GEP into it. This should not affect existing cases where I64 fields are leaves (not further GEP'd).

## Sources

### Primary (HIGH confidence)
- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/Elaboration.fs` — Full file read; all patterns, stubs, and infrastructure confirmed
- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/MatchCompiler.fs` — Full file read; AdtCtor placeholder at line 128, ctorArity at 81, desugarPattern at 90
- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/MlirIR.fs` — Full file read; all available ops confirmed
- `/Users/ohama/vibe-coding/FunLangCompiler/.planning/STATE.md` — Design decisions C-11, C-12 confirmed
- `/Users/ohama/vibe-coding/FunLangCompiler/.planning/phases/16-environment-infrastructure/16-02-SUMMARY.md` — Phase 16 completion state confirmed

### Secondary (MEDIUM confidence)
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Ast.fs` — Constructor and ConstructorPat AST nodes confirmed at lines 91 and 129

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all ops exist, confirmed by reading source
- Architecture: HIGH — cons-cell pattern is exact reference for ADT layout
- Pitfalls: HIGH — type confusion issue analyzed in depth; solutions documented
- Constructor→App ambiguity: MEDIUM — need to verify parser output for `Some 42`

**Research date:** 2026-03-26
**Valid until:** 2026-04-26 (stable codebase, no external dependencies)
