# Phase 18: Records - Research

**Researched:** 2026-03-27
**Domain:** F# compiler backend — record heap layout, field access, record update, mutable SetField, RecordPat decision-tree emission in MLIR
**Confidence:** HIGH

## Summary

Phase 18 implements five symmetric operations for record values: construction (`RecordExpr`), field access (`FieldAccess`), functional update (`RecordUpdate`), in-place mutable mutation (`SetField`), and structural pattern matching (`RecordPat`). All five are compile-only concerns in `Elaboration.fs`; no new MlirIR ops, no runtime C functions, and no parser changes are needed.

The phase splits cleanly into two tasks matching the requirements: 18-01 covers REC-02/03/04/05 (construction, access, update, mutation); 18-02 covers REC-06 (RecordPat). The entire scaffolding was laid in Phase 16: `RecordEnv` is populated by `prePassDecls` as `Map<string, Map<string, int>>` (type name → field name → slot index), and `MatchCompiler.fs` already has `RecordCtor fields` in the `CtorTag` DU with correct arity, `desugarPattern` wired for `RecordPat`, and `splitClauses` offset correctly for records (no slot-0 tag, unlike ADTs). Two stubs `failwith "Phase 18: RecordCtor not yet implemented"` live in `scrutineeTypeForTag` and `emitCtorTest` in `Elaboration.fs`.

Records use a **flat n-slot layout**: `GC_malloc(n*8)` with field at slot `i` (0-based, declaration order from `RecordEnv`). This differs from ADTs (16-byte `{tag, payload}`) and strings (struct-typed `{i64 len, ptr data}`). The correct GEP op is `LlvmGEPLinearOp` (emits `getelementptr ptr[i]`), not `LlvmGEPStructOp` (for typed structs only). Every field is an `i64` in the uniform representation — all values are either integers or pointers, both stored as 8 bytes.

**Primary recommendation:** Implement 18-01 first (RecordExpr, FieldAccess, RecordUpdate, SetField in `elaborateExpr` plus `freeVars`) then 18-02 (RecordCtor pattern emission). Both tasks are pure `Elaboration.fs` wiring using only already-present MlirIR ops.

## Standard Stack

This phase adds no new libraries or tools. All components are from prior phases.

### Core (already present)
| Component | Location | Purpose |
|-----------|----------|---------|
| `MlirIR.fs` | `FunLangCompiler.Compiler` | All MLIR ops needed already exist: `LlvmGEPLinearOp`, `LlvmStoreOp`, `LlvmLoadOp`, `LlvmCallOp`, `ArithConstantOp` |
| `Elaboration.fs` | `FunLangCompiler.Compiler` | `elaborateExpr` (add new cases), `freeVars` (add new cases), `scrutineeTypeForTag`/`emitCtorTest` (fill stubs) |
| `MatchCompiler.fs` | `FunLangCompiler.Compiler` | `RecordCtor fields` DU case already present; `desugarPattern` for `RecordPat` already wired; `splitClauses` uses `Field(selAcc, i)` for non-ADT records (no +1 offset) |
| `ElabEnv.RecordEnv` | `Elaboration.fs` | `Map<string, Map<string, int>>` — type name → (field name → slot index), populated by `prePassDecls` |

### Installation
No new packages. Build with existing `dotnet build`.

## Architecture Patterns

### Record Heap Layout (REC-02)

Records use a **flat n-slot layout** — n consecutive 8-byte slots, no tag prefix:

```
offset 0:    i64 or ptr   field[0]   (declaration order from RecordDecl)
offset 8:    i64 or ptr   field[1]
...
offset (n-1)*8:  i64 or ptr  field[n-1]
```

- `GC_malloc(n * 8)` allocates the block
- Slot index comes from `env.RecordEnv[typeName][fieldName]`
- Field type in the IR is always `I64` (uniform representation; pointers fit in i64 slots via `LlvmStoreOp`)
- `LlvmGEPLinearOp(slotPtr, blockPtr, i)` for GEP; `LlvmStoreOp(fieldVal, slotPtr)` for write; `LlvmLoadOp(fieldVal, slotPtr)` for read

**Key distinction from ADTs:** No slot-0 tag. Field 0 is the first declared field. `splitClauses` in `MatchCompiler.fs` already uses `Field(selAcc, i)` (not `Field(selAcc, i+1)`) for non-AdtCtor tags, so RecordCtor arg accessors are already correct.

### Pattern 1: RecordExpr Construction (REC-02)

**What:** `{ x = 3; y = 4 }` → allocate n-slot block, store each field in declaration order.
**When to use:** Any `RecordExpr` node in `elaborateExpr`.

**Type name resolution:** The parser always produces `RecordExpr(typeName = None, ...)`. Must infer type name by looking up the field names in `RecordEnv`. Strategy: iterate `env.RecordEnv` and find the entry whose field set is a superset of the expression's field names. For v4.0, require exact match or raise a clear error.

```fsharp
// Source: codebase analysis of Elaboration.fs tuple elaboration (line 821-841)
| RecordExpr(typeNameOpt, fields, _) ->
    // Resolve type name from RecordEnv by matching field names
    let fieldNames = fields |> List.map fst |> Set.ofList
    let typeName =
        match typeNameOpt with
        | Some n -> n
        | None ->
            env.RecordEnv
            |> Map.tryFindKey (fun _ fmap ->
                Set.ofSeq (fmap |> Map.toSeq |> Seq.map fst) = fieldNames)
            |> Option.defaultWith (fun () ->
                failwithf "RecordExpr: cannot resolve record type for fields %A" (Set.toList fieldNames))
    let fieldMap = Map.find typeName env.RecordEnv  // field name -> slot index
    let n = Map.count fieldMap
    // Elaborate all field expressions
    let fieldResults = fields |> List.map (fun (_, e) -> elaborateExpr env e)
    let allFieldOps  = fieldResults |> List.collect snd
    let fieldVals    = fieldResults |> List.map fst
    // Allocate n-slot block
    let bytesVal  = { Name = freshName env; Type = I64 }
    let recPtrVal = { Name = freshName env; Type = Ptr }
    let allocOps  = [
        ArithConstantOp(bytesVal, int64 (n * 8))
        LlvmCallOp(recPtrVal, "@GC_malloc", [bytesVal])
    ]
    // Store each field at its declared slot index
    let storeOps =
        fields |> List.collect (fun (fieldName, _) ->
            let slotIdx = Map.find fieldName fieldMap
            let fieldVal = List.nth fieldVals (fields |> List.findIndex (fun (fn, _) -> fn = fieldName))
            let slotPtr = { Name = freshName env; Type = Ptr }
            [ LlvmGEPLinearOp(slotPtr, recPtrVal, slotIdx)
              LlvmStoreOp(fieldVal, slotPtr) ]
        )
    (recPtrVal, allFieldOps @ allocOps @ storeOps)
```

### Pattern 2: FieldAccess (REC-03)

**What:** `p.x` → GEP into record block at field's slot index, load value.

```fsharp
// Source: codebase analysis of Elaboration.fs tuple elaboration (line 821-841)
| FieldAccess(recExpr, fieldName, _) ->
    let (recVal, recOps) = elaborateExpr env recExpr
    // Must determine record type to look up field index.
    // Since recVal is Ptr, need to search RecordEnv for a type containing fieldName.
    let slotIdx =
        env.RecordEnv
        |> Map.toSeq
        |> Seq.tryPick (fun (_, fmap) -> Map.tryFind fieldName fmap)
        |> Option.defaultWith (fun () ->
            failwithf "FieldAccess: unknown field '%s'" fieldName)
    let slotPtr  = { Name = freshName env; Type = Ptr }
    let fieldVal = { Name = freshName env; Type = I64 }
    let ops = [
        LlvmGEPLinearOp(slotPtr, recVal, slotIdx)
        LlvmLoadOp(fieldVal, slotPtr)
    ]
    (fieldVal, recOps @ ops)
```

**Ambiguity concern:** If two record types share a field name, slot lookup by field name alone could pick the wrong type. For v4.0 this is acceptable — field names should be unique across types in the test suite. Planner should note this as a known limitation.

### Pattern 3: RecordUpdate (REC-04)

**What:** `{ p with y = 10 }` → allocate new n-slot block, copy all fields from source, overwrite updated fields.

```fsharp
// Source: codebase analysis — mirrors tuple construction but reads from source first
| RecordUpdate(sourceExpr, overrides, _) ->
    let (srcVal, srcOps) = elaborateExpr env sourceExpr
    // Resolve type from overrides field names (or by another mechanism)
    let overrideNames = overrides |> List.map fst |> Set.ofList
    let (typeName, fieldMap) =
        env.RecordEnv
        |> Map.tryFindKey (fun _ fmap ->
            overrideNames |> Set.forall (fun fn -> Map.containsKey fn fmap))
        |> Option.map (fun tn -> (tn, Map.find tn env.RecordEnv))
        |> Option.defaultWith (fun () ->
            failwithf "RecordUpdate: cannot resolve record type for fields %A" (Set.toList overrideNames))
    let n = Map.count fieldMap
    // Allocate new block
    let bytesVal  = { Name = freshName env; Type = I64 }
    let newPtrVal = { Name = freshName env; Type = Ptr }
    let allocOps  = [
        ArithConstantOp(bytesVal, int64 (n * 8))
        LlvmCallOp(newPtrVal, "@GC_malloc", [bytesVal])
    ]
    // Build override map: fieldName -> elaborated value
    let overrideResults = overrides |> List.map (fun (fn, e) -> (fn, elaborateExpr env e))
    let overrideOps     = overrideResults |> List.collect (fun (_, (_, ops)) -> ops)
    let overrideVals    = overrideResults |> Map.ofList |> Map.map (fun _ (v, _) -> v)
    // Copy or override each field
    let copyOps =
        fieldMap |> Map.toList |> List.collect (fun (fieldName, slotIdx) ->
            let dstSlotPtr = { Name = freshName env; Type = Ptr }
            match Map.tryFind fieldName overrideVals with
            | Some newVal ->
                // Overridden: store new value directly
                [ LlvmGEPLinearOp(dstSlotPtr, newPtrVal, slotIdx)
                  LlvmStoreOp(newVal, dstSlotPtr) ]
            | None ->
                // Non-overridden: copy from source
                let srcSlotPtr = { Name = freshName env; Type = Ptr }
                let srcFieldVal = { Name = freshName env; Type = I64 }
                [ LlvmGEPLinearOp(srcSlotPtr, srcVal, slotIdx)
                  LlvmLoadOp(srcFieldVal, srcSlotPtr)
                  LlvmGEPLinearOp(dstSlotPtr, newPtrVal, slotIdx)
                  LlvmStoreOp(srcFieldVal, dstSlotPtr) ]
        )
    (newPtrVal, srcOps @ overrideOps @ allocOps @ copyOps)
```

### Pattern 4: SetField — Mutable Field Mutation (REC-05)

**What:** `r.v <- 42` → GEP to field slot in existing block, store new value, return unit (i64 = 0).

```fsharp
// Source: codebase analysis — mirrors LlvmStoreOp pattern from cons-cell elaboration
| SetField(recExpr, fieldName, valueExpr, _) ->
    let (recVal, recOps)     = elaborateExpr env recExpr
    let (newVal, newValOps)  = elaborateExpr env valueExpr
    let slotIdx =
        env.RecordEnv
        |> Map.toSeq
        |> Seq.tryPick (fun (_, fmap) -> Map.tryFind fieldName fmap)
        |> Option.defaultWith (fun () ->
            failwithf "SetField: unknown field '%s'" fieldName)
    let slotPtr = { Name = freshName env; Type = Ptr }
    let unitVal = { Name = freshName env; Type = I64 }
    let ops = [
        LlvmGEPLinearOp(slotPtr, recVal, slotIdx)
        LlvmStoreOp(newVal, slotPtr)
        ArithConstantOp(unitVal, 0L)   // unit = i64 0
    ]
    (unitVal, recOps @ newValOps @ ops)
```

**isMutable field:** `RecordFieldDecl` in `Ast.fs` carries `isMutable: bool` but `RecordEnv` (populated in Phase 16) stores only `field name → slot index`, not mutability info. For v4.0, no mutability check is performed at compile time — `SetField` is allowed on any field. This matches the decision in the context ("Phase 18 concern: verify that RecordExpr with typeName = None is handled").

### Pattern 5: RecordPat in Decision Tree (REC-06)

**What:** `match p with | { x = 3; y } -> y | _ -> 0` — structural match extracting fields by name.

**MatchCompiler.fs already handles:** `desugarPattern` for `RecordPat` sorts field names, produces `RecordCtor fieldNames` tag + sub-patterns. `splitClauses` generates `argAccessors = List.init arity (fun i -> Field(selAcc, i))` — correct 0-based offsets (no +1 because records have no tag slot).

**Elaboration.fs stubs to fill (two locations):**

1. `scrutineeTypeForTag` — `RecordCtor _` returns `Ptr` (records are always heap-allocated pointers).
2. `emitCtorTest` — `RecordCtor _` always matches structurally (no tag check), emit unconditional `true` like `TupleCtor`.

```fsharp
// Source: codebase analysis of emitCtorTest TupleCtor case (line 1035-1039)

// In scrutineeTypeForTag:
| MatchCompiler.RecordCtor _ -> Ptr

// In emitCtorTest:
| MatchCompiler.RecordCtor _ ->
    // Records always match structurally — emit unconditional true
    let cond = { Name = freshName env; Type = I1 }
    let ops  = [ ArithConstantOp(cond, 1L) ]
    (cond, ops)
```

**Field accessor resolution:** The `resolveAccessor` / `resolveAccessorTyped` machinery in the decision tree emitter already handles `Field(parent, idx)` generically — it emits `LlvmGEPLinearOp(slotPtr, parentVal, idx)` + `LlvmLoadOp(fieldVal, slotPtr)`. Since `argAccessors` for `RecordCtor` are `Field(scrutAcc, 0)`, `Field(scrutAcc, 1)`, ... (from `splitClauses`), the correct slot indices flow automatically from the MatchCompiler.

**Critical ordering constraint:** The `argAccessors` slot indices come from `splitClauses`, which enumerates sub-patterns in the **sorted field name order** (from `desugarPattern`). This matches `RecordEnv` declaration order only if the parser's sub-patterns are also sorted by field name — which they are (see `desugarPattern` line 132: `let sorted = fields |> List.sortBy fst`). So `argAccessors.[0]` corresponds to the alphabetically first field name, and the field's value at that index must be looked up from `RecordEnv` — but this is already handled by sub-pattern index alignment.

**Wait — there is a mismatch to fix:** `argAccessors.[i]` = `Field(scrutAcc, i)` uses index `i` which is the sub-pattern position in sorted order. But the actual slot in the heap block is `RecordEnv[typeName][fieldName]` in declaration order, NOT alphabetical order. The two orderings will diverge when the type's declaration order differs from alphabetical order.

**Resolution:** The `resolveAccessor` machinery emits `LlvmGEPLinearOp(slotPtr, parentVal, idx)` using the `idx` from the `Field` accessor — which is the alphabetically-sorted position, NOT the declaration-order slot. This is WRONG if `RecordEnv` uses declaration order.

The fix is: during `emitCtorTest` or a new pre-load function for `RecordCtor`, after the sub-patterns are expanded, each `argAccessors.[i]` must GEP using the *declaration-order slot index* for the i-th alphabetically-sorted field name, not `i` itself.

There are two viable approaches:

**Approach A (recommended):** Emit a dedicated `ensureRecordFieldTypes` function (mirroring `ensureAdtFieldTypes`) that resolves `argAccessors.[i]` using the correct slot from `RecordEnv`, by:
1. Knowing the field names (from `RecordCtor fields` tag)
2. Looking up each field's declaration-order index in `RecordEnv`
3. Emitting `LlvmGEPLinearOp` at the *declaration-order slot* (not `i`)
4. Caching the result in `accessorCache` at the `argAccessors.[i]` key

This requires passing `fields` from the `RecordCtor` tag into the preload helper and querying `RecordEnv`.

**Approach B:** Change `splitClauses` in `MatchCompiler.fs` to generate `argAccessors` using declaration-order indices instead of sequential 0..n-1. This requires passing `RecordEnv` into `splitClauses` — a larger change.

**Approach A is preferred** — it is localized to Elaboration.fs's `emitDecisionTree` Switch case, consistent with how `ensureAdtFieldTypes` handles the ADT slot-1 offset.

```fsharp
// ensureRecordFieldTypes: pre-load each field with correct slot from RecordEnv
let ensureRecordFieldTypes (fields: string list) (argAccs: MatchCompiler.Accessor list) : MlirOp list =
    // fields is sorted alphabetically (from RecordCtor fields list, from desugarPattern)
    // Find typeName by matching field set
    let fieldSet = Set.ofList fields
    let (typeName, fieldMap) =
        env.RecordEnv
        |> Map.tryFindKey (fun _ fm -> Set.ofSeq (fm |> Map.toSeq |> Seq.map fst) = fieldSet)
        |> Option.map (fun tn -> (tn, Map.find tn env.RecordEnv))
        |> Option.defaultWith (fun () ->
            failwithf "ensureRecordFieldTypes: cannot resolve record type for fields %A" fields)
    let mutable ops = []
    fields |> List.iteri (fun i fieldName ->
        let declSlotIdx = Map.find fieldName fieldMap  // declaration-order slot
        // argAccs.[i] = Field(scrutAcc, i) from MatchCompiler — but we need to load from declSlotIdx
        // Override the GEP index by resolving with the correct type at the right slot
        if i < argAccs.Length then
            // Re-resolve using the correct slot (declaration order), not i
            // This means we need to cache argAccs.[i] mapped to load from declSlotIdx
            // Approach: re-emit load from parent at correct slot, override cache
            let parentAcc = match argAccs.[i] with MatchCompiler.Field(p, _) -> p | r -> r
            let (parentVal, parentOps) = resolveAccessor parentAcc
            let slotPtr  = { Name = freshName env; Type = Ptr }
            let fieldVal = { Name = freshName env; Type = I64 }
            let gepOp  = LlvmGEPLinearOp(slotPtr, parentVal, declSlotIdx)
            let loadOp = LlvmLoadOp(fieldVal, slotPtr)
            accessorCache.[argAccs.[i]] <- fieldVal
            ops <- ops @ parentOps @ [gepOp; loadOp]
    )
    ops
```

Then in `emitDecisionTree` Switch:
```fsharp
let preloadOps =
    match tag with
    | MatchCompiler.ConsCtor -> ensureConsFieldTypes scrutAcc argAccs
    | MatchCompiler.AdtCtor(_, _, arity) when arity > 0 -> ensureAdtFieldTypes scrutAcc argAccs
    | MatchCompiler.RecordCtor fields -> ensureRecordFieldTypes fields argAccs
    | _ -> []
```

### Anti-Patterns to Avoid

- **Using `LlvmGEPStructOp` for record fields:** `LlvmGEPStructOp` emits typed `inbounds [0, N] : !llvm.struct<(i64, ptr)>` — only correct for string headers. Record slots use `LlvmGEPLinearOp` (untyped `ptr[N]`).
- **Assuming argAccessors.[i] slot == RecordEnv slot:** The MatchCompiler generates `Field(scrutAcc, i)` using sequential i from sorted field order; `RecordEnv` uses declaration order. These differ when declared and alphabetical orders differ.
- **Using `Map.find` on `RecordEnv` with a field name directly:** `RecordEnv` is `Map<typeName, Map<fieldName, slotIdx>>` — must find the inner map first, then the slot index.
- **Omitting `freeVars` cases:** `RecordExpr`, `FieldAccess`, `RecordUpdate`, `SetField` must be added to `freeVars` or they will be caught by the `| _ -> Set.empty` fallback, which returns `Set.empty` (conservatively correct for closure capture but semantically wrong). For correctness in closure capture, add explicit cases.
- **Forgetting the unit return for `SetField`:** `r.v <- 42` must produce an `i64 = 0` unit value, not `()` (void). Returning the stored value's SSA name would break the type system.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| GEP + store pattern | Custom store logic | `LlvmGEPLinearOp` + `LlvmStoreOp` (already in MlirIR) | Already covers all untyped pointer GEPs |
| Field index lookup | Scan record AST | `env.RecordEnv[typeName][fieldName]` | Phase 16 already built the index map |
| Pattern match record | New MatchCompiler logic | `RecordCtor` already in DU; fill two stubs in Elaboration.fs | MatchCompiler already desugar and splits correctly |
| Type inference for RecordExpr | Full type inference | Field-set lookup in `RecordEnv` | Sufficient for monomorphic records in v4.0 |

**Key insight:** Phase 16 did the hard infrastructure work. Phase 18 is entirely wiring — every op, every data structure, and the pattern match algorithm are already in place.

## Common Pitfalls

### Pitfall 1: Slot Index vs Sub-Pattern Position Mismatch
**What goes wrong:** RecordPat field extraction reads from wrong slot — e.g., for `type Point = { y: int; x: int }` (y declared first), matching `{ x = 3 }` reads slot 0 (y) instead of slot 1 (x).
**Why it happens:** `MatchCompiler.splitClauses` generates `argAccessors = [Field(acc, 0); Field(acc, 1); ...]` in sorted field name order (alphabetical), but heap slots are in declaration order from `RecordEnv`.
**How to avoid:** Implement `ensureRecordFieldTypes` that overrides the `accessorCache` to load from the declaration-order slot (see Architecture Pattern 5 above).
**Warning signs:** Pattern match test passes for records where alphabetical and declaration order coincide (e.g., `{ a: int; b: int }`), but fails for `{ y: int; x: int }`.

### Pitfall 2: RecordExpr typeName Resolution Fragility
**What goes wrong:** `RecordExpr(typeName = None, ...)` fails to resolve the type if two record types share field names.
**Why it happens:** Parser always produces `typeName = None`; type resolution must search `RecordEnv`.
**How to avoid:** For v4.0, the test suite has no conflicting field names. Use field-set equality matching. Log a clear error if ambiguous.
**Warning signs:** Error messages like "cannot resolve record type for fields" during compilation of valid code.

### Pitfall 3: RecordUpdate Ordering of Ops
**What goes wrong:** Override values are elaborated before the source record, but store ops for copy and override are interleaved, causing MLIR SSA value use-before-def.
**Why it happens:** Copy ops for non-overridden fields read from source SSA value; override stores write from elaborated value SSA. All must be emitted in correct topological order.
**How to avoid:** Emit all source elaboration ops first, then all override value elaboration ops, then the alloc ops, then all store/copy ops. Follow the same pattern as Tuple elaboration (line 821-841).
**Warning signs:** `mlir-opt` error "use of undefined value".

### Pitfall 4: SetField Returning Wrong Value
**What goes wrong:** `r.v <- 42` returns `42` (the stored value) instead of unit (i64 = 0), causing type mismatch in calling expressions.
**Why it happens:** Easy to return `newVal` instead of a fresh `ArithConstantOp(unitVal, 0L)`.
**How to avoid:** Always allocate a fresh `unitVal` and return it, as with print/println builtins.
**Warning signs:** `r.v <- 42` in `let _ = r.v <- 42 in r.v` computes 42 instead of the actual field value.

### Pitfall 5: freeVars fallback masking capture errors
**What goes wrong:** A closure that captures a record value fails at runtime because the closure's env struct doesn't include the record pointer.
**Why it happens:** `freeVars` has `| _ -> Set.empty` at line 123 — `RecordExpr`, `FieldAccess`, `RecordUpdate`, `SetField` are not listed, so their free variables are silently dropped.
**How to avoid:** Add explicit `freeVars` cases for all four new Expr variants before 18-01 is tested with closure-capturing code.
**Warning signs:** Tests that pass records into closures produce wrong values or segfault.

## Code Examples

### RecordExpr: allocate 2-field `{ x = 3; y = 4 }`

```fsharp
// Source: codebase analysis of Elaboration.fs Tuple case (line 821-841)
// type Point = { x: int; y: int }  →  RecordEnv["Point"] = { "x"→0, "y"→1 }
// RecordExpr(None, [("x", Number 3); ("y", Number 4)], _)
// Emits:
//   %t0 = arith.constant 3 : i64
//   %t1 = arith.constant 4 : i64
//   %t2 = arith.constant 16 : i64
//   %t3 = llvm.call @GC_malloc(%t2) : (i64) -> !llvm.ptr
//   %t4 = llvm.getelementptr %t3[0] : (!llvm.ptr) -> !llvm.ptr, i64
//   llvm.store %t0, %t4 : i64, !llvm.ptr
//   %t5 = llvm.getelementptr %t3[1] : (!llvm.ptr) -> !llvm.ptr, i64
//   llvm.store %t1, %t5 : i64, !llvm.ptr
//   (result = %t3)
```

### FieldAccess: load `p.x`

```fsharp
// Source: codebase analysis of Elaboration.fs LlvmGEPLinearOp usage
// RecordEnv["Point"]["x"] = 0
// FieldAccess(Var "p", "x", _)
// Emits:
//   %t0 = llvm.getelementptr %p[0] : (!llvm.ptr) -> !llvm.ptr, i64
//   %t1 = llvm.load %t0 : !llvm.ptr -> i64
//   (result = %t1)
```

### SetField: `r.v <- 42`

```fsharp
// Source: codebase analysis
// SetField(Var "r", "v", Number 42, _)
// Emits:
//   %t0 = arith.constant 42 : i64
//   %t1 = llvm.getelementptr %r[0] : (!llvm.ptr) -> !llvm.ptr, i64
//   llvm.store %t0, %t1 : i64, !llvm.ptr
//   %t2 = arith.constant 0 : i64
//   (result = %t2, unit)
```

### RecordCtor in emitCtorTest (unconditional match)

```fsharp
// Source: codebase analysis of TupleCtor case (line 1035-1039)
| MatchCompiler.RecordCtor _ ->
    let cond = { Name = freshName env; Type = I1 }
    let ops  = [ ArithConstantOp(cond, 1L) ]
    (cond, ops)
```

### RecordCtor in scrutineeTypeForTag

```fsharp
// Source: codebase analysis of TupleCtor case (line 987)
| MatchCompiler.RecordCtor _ -> Ptr
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| RecordExpr/FieldAccess/etc. → `failwith` | Fill in elaborateExpr cases | Phase 18 | Records compile end-to-end |
| RecordCtor in MatchCompiler | `failwith "Phase 18: RecordCtor not yet implemented"` stubs in Elaboration.fs | Phase 18 | RecordPat pattern match compiles |
| `freeVars _ -> Set.empty` fallback for Record exprs | Explicit freeVars cases | Phase 18 | Closure capture correct for record values |

**Deprecated/outdated:**
- None — no existing approach is being superseded.

## Open Questions

1. **Field name ambiguity across record types**
   - What we know: `RecordEnv` is keyed by type name; field-set lookup finds the type by matching all field names.
   - What's unclear: If two types share all the same field names (structural equivalence), resolution is non-deterministic.
   - Recommendation: For v4.0, document that field names must be unique across all record types in a program. No disambiguation mechanism needed for the test suite.

2. **isMutable enforcement at compile time**
   - What we know: `RecordFieldDecl` carries `isMutable: bool`; `prePassDecls` does not store mutability in `RecordEnv`.
   - What's unclear: Should `SetField` on non-mutable fields be a compile error?
   - Recommendation: For v4.0, skip the check. Any field can be mutated at the MLIR level. Add a compile-time check in a future phase.

3. **RecordPat field subset matching**
   - What we know: `desugarPattern` generates `RecordCtor fieldNames` with only the fields mentioned in the pattern (not all fields of the type).
   - What's unclear: The `ensureRecordFieldTypes` function must resolve the type from a *partial* field set.
   - Recommendation: Use `Map.forall (fun fn _ -> Map.containsKey fn fullFieldMap)` to find a type that *contains* all the pattern fields, not an exact match. Types with more fields should also match.

4. **Nested record field access in patterns (e.g., `{ p = { x = 3 } }`)**
   - What we know: The decision tree handles nested `Field(Field(...))` accessors generically.
   - What's unclear: Whether the nested GEP correctly loads Ptr (nested record ptr) vs I64.
   - Recommendation: For v4.0, test only flat record patterns. Nested record patterns use the `resolveAccessorTyped` machinery but need type information to know when to load as Ptr vs I64. Out of scope for Phase 18.

## Sources

### Primary (HIGH confidence)
- Codebase: `src/FunLangCompiler.Compiler/Elaboration.fs` — direct inspection of all existing patterns, stubs, and helper functions
- Codebase: `src/FunLangCompiler.Compiler/MatchCompiler.fs` — direct inspection of `RecordCtor`, `desugarPattern`, `splitClauses`
- Codebase: `LangThree/src/LangThree/Ast.fs` — direct inspection of `RecordExpr`, `RecordPat`, `RecordFieldDecl`, `RecordDecl` definitions
- Codebase: `LangThree/src/LangThree/Parser.fsy` — confirmed parser always produces `RecordExpr(typeName = None, ...)`
- Codebase: `src/FunLangCompiler.Compiler/MlirIR.fs` — confirmed all needed ops are present

### Secondary (MEDIUM confidence)
- `.planning/phases/17-adt-construction-pattern-matching/17-RESEARCH.md` — prior phase's patterns (ADT layout, GEP ops, decision tree emission) directly applicable
- `.planning/phases/16-environment-infrastructure/` — confirmed RecordEnv structure and prePassDecls implementation

### Tertiary (LOW confidence)
- None required — all findings come from direct code inspection.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — confirmed by direct file inspection; no new deps needed
- Architecture (layout, ops): HIGH — confirmed by reading existing Tuple/Cons/ADT patterns in Elaboration.fs
- Pitfalls (slot ordering mismatch): HIGH — confirmed by reading MatchCompiler.splitClauses logic and RecordEnv declaration-order storage
- RecordPat emit (two stubs): HIGH — stubs are clearly marked, pattern matches TupleCtor exactly
- Open questions (ambiguity, mutability): MEDIUM — not blockers for Phase 18 test suite

**Research date:** 2026-03-27
**Valid until:** 2026-04-27 (codebase is stable; findings derived from direct inspection)
