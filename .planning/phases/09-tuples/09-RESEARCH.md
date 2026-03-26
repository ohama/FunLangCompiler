# Phase 9: Tuples - Research

**Researched:** 2026-03-26
**Domain:** MLIR llvm dialect GEP/store/load for heap structs, LangThree Tuple/LetPat AST, Boehm GC_malloc struct allocation
**Confidence:** HIGH

---

## Summary

Phase 9 compiles LangThree tuple expressions to GC-managed heap structs and destructures them via GEP + load. The work is entirely internal to the existing MlirIR/Elaboration/Printer pipeline — no new external dependencies are required beyond the Boehm GC already wired in Phase 7.

A tuple `(e1, e2, ..., eN)` becomes a `GC_malloc(N * 8)`-allocated block of N pointer-sized (8-byte) slots. Each field is stored via `llvm.getelementptr ptr[i]` + `llvm.store`. Destructuring `let (a, b) = t in body` becomes two GEP + load pairs that bind `a` and `b` to SSA values. The uniform `Ptr` representation already used for closures is reused for tuple pointers — each field is stored/loaded as `i64` (or `Ptr` for nested tuples), matching the project's fully-boxed-value convention.

The LangThree AST encodes tuples as `Tuple(exprs, span)` and destructuring as `LetPat(TuplePat(pats, span), bindExpr, bodyExpr, span)`. The Elaboration pass currently handles `LetPat` for `WildcardPat` and `VarPat` only (Phase 7). Phase 9 adds the `TuplePat` case. For `TuplePat in match`, the scrutinee is already a `Ptr`; each sub-pattern gets a GEP + load and is then matched recursively.

**Primary recommendation:** Add `LlvmGEPStructOp` (struct-indexed GEP producing `!llvm.ptr`) to MlirIR + Printer, then implement `Tuple` and `LetPat(TuplePat)` in Elaboration using `GC_malloc + sequential GEP/store` and `GEP/load per field` respectively. All three requirements fit in one plan.

---

## Standard Stack

### Core

| Component | Version | Purpose | Why Standard |
|-----------|---------|---------|--------------|
| `GC_malloc` (Boehm GC) | 8.2.12 | Heap-allocate N×8-byte tuple struct | Already integrated in Phase 7; same call pattern as closure env alloc |
| `llvm.getelementptr` | MLIR 20 | Index into tuple struct by field offset | Standard LLVM GEP — same op already used for closure envs (`LlvmGEPLinearOp`) |
| `llvm.store` / `llvm.load` | MLIR 20 | Write/read tuple fields | Already in MlirIR as `LlvmStoreOp` / `LlvmLoadOp` |

### Supporting

| Component | Version | Purpose | When to Use |
|-----------|---------|---------|-------------|
| `LlvmGEPLinearOp` (existing) | Phase 5 | GEP with single linear index | Already used for closure env field access — tuple fields use SAME op |
| `ArithConstantOp` | Phase 2 | Emit N*8 byte count for GC_malloc | Same pattern as closure env byte count: `(numFields) * 8` |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Uniform `i64` field storage | Typed struct fields | Uniform boxing is simpler and consistent with closures; typed fields require a type system in the IR |
| `llvm.alloca` for tuple | `GC_malloc` | Alloca would fail for tuples returned/escaped from their defining scope; GC_malloc required by TUP-01 success criterion |
| New `LlvmGEPStructOp` DU case | Reuse `LlvmGEPLinearOp` | `LlvmGEPLinearOp` already does exactly the right thing — GEP `ptr[i]` with integer index. No new op needed. |

**No new installation required** — GC is already linked via `-lgc`.

---

## Architecture Patterns

### Recommended Project Structure

```
src/LangBackend.Compiler/
├── MlirIR.fs       — NO CHANGES NEEDED (LlvmGEPLinearOp, LlvmStoreOp, LlvmLoadOp, LlvmCallOp already exist)
├── Printer.fs      — NO CHANGES NEEDED (all needed ops already serialized)
├── Elaboration.fs  — ADD: Tuple case + LetPat(TuplePat) case
tests/compiler/
├── 09-01-tuple-basic.flt         — TUP-01 + TUP-02: let (a,b) = (3,4) in a+b → exits 7
├── 09-02-tuple-nested.flt        — TUP-02 nested: let (x,inner) = (1,(2,3)) in let (y,z) = inner in x+y+z → exits 6
└── 09-03-tuple-match.flt         — TUP-03: match (1,2) with (a,b) -> a+b → exits 3
```

### Pattern 1: Tuple Construction (`Tuple(exprs, span)`)

**What:** Allocate N×8 bytes via GC_malloc, then store each elaborated field value at index i via GEP.

**Elaboration code pattern:**
```fsharp
| Tuple (exprs, _) ->
    let n = List.length exprs
    // 1. Elaborate all field expressions
    let fieldResults = exprs |> List.map (elaborateExpr env)
    let allFieldOps = fieldResults |> List.collect snd
    let fieldVals   = fieldResults |> List.map fst
    // 2. GC_malloc(n * 8)
    let bytesVal  = { Name = freshName env; Type = I64 }
    let tupPtrVal = { Name = freshName env; Type = Ptr }
    let allocOps  = [
        ArithConstantOp(bytesVal, int64 (n * 8))
        LlvmCallOp(tupPtrVal, "@GC_malloc", [bytesVal])
    ]
    // 3. Store each field: GEP ptr[i] + store
    let storeOps =
        fieldVals |> List.mapi (fun i fv ->
            let slotVal = { Name = freshName env; Type = Ptr }
            [ LlvmGEPLinearOp(slotVal, tupPtrVal, i)
              LlvmStoreOp(fv, slotVal) ]
        ) |> List.concat
    (tupPtrVal, allFieldOps @ allocOps @ storeOps)
```

**MLIR output for `(3, 4)`:**
```mlir
%bytes = arith.constant 16 : i64
%tup = llvm.call @GC_malloc(%bytes) : (i64) -> !llvm.ptr
%slot0 = llvm.getelementptr %tup[0] : (!llvm.ptr) -> !llvm.ptr, i64
llvm.store %c3, %slot0 : i64, !llvm.ptr
%slot1 = llvm.getelementptr %tup[1] : (!llvm.ptr) -> !llvm.ptr, i64
llvm.store %c4, %slot1 : i64, !llvm.ptr
```

### Pattern 2: Tuple Destructuring (`LetPat(TuplePat(pats), bindExpr, bodyExpr)`)

**What:** For each sub-pattern at index i, emit GEP + load to extract the field value. Then bind each extracted value per sub-pattern (VarPat → add to env, WildcardPat → discard).

**Elaboration code pattern:**
```fsharp
| LetPat (TuplePat (pats, _), bindExpr, bodyExpr, _) ->
    // 1. Elaborate the tuple-valued expression → tupPtrVal : Ptr
    let (tupPtrVal, bindOps) = elaborateExpr env bindExpr
    // 2. For each sub-pattern, emit GEP + load
    let (extractOps, env') =
        pats |> List.mapi (fun i pat -> (i, pat))
             |> List.fold (fun (opsAcc, envAcc) (i, pat) ->
                let slotVal = { Name = freshName env; Type = Ptr }
                let fieldVal = { Name = freshName env; Type = I64 }
                let gepOp   = LlvmGEPLinearOp(slotVal, tupPtrVal, i)
                let loadOp  = LlvmLoadOp(fieldVal, slotVal)
                let envAcc' =
                    match pat with
                    | VarPat (name, _) -> { envAcc with Vars = Map.add name fieldVal envAcc.Vars }
                    | WildcardPat _ -> envAcc
                    | _ -> failwithf "Elaboration: unsupported sub-pattern in TuplePat: %A" pat
                (opsAcc @ [gepOp; loadOp], envAcc')
             ) ([], env)
    // 3. Elaborate body with extended env
    let (bodyVal, bodyOps) = elaborateExpr env' bodyExpr
    (bodyVal, bindOps @ extractOps @ bodyOps)
```

**MLIR output for `let (a, b) = t in a + b`:**
```mlir
// bindOps: t was already elaborated to %tupPtr
%slot0 = llvm.getelementptr %tupPtr[0] : (!llvm.ptr) -> !llvm.ptr, i64
%a = llvm.load %slot0 : !llvm.ptr -> i64
%slot1 = llvm.getelementptr %tupPtr[1] : (!llvm.ptr) -> !llvm.ptr, i64
%b = llvm.load %slot1 : !llvm.ptr -> i64
%result = arith.addi %a, %b : i64
```

### Pattern 3: TuplePat in match (TUP-03)

**What:** The scrutinee is already a `Ptr` to a tuple struct. For `match t with | (a, b) -> body`, emit the same GEP + load sequence as LetPat destructuring, bind the sub-patterns, then elaborate the body.

**Note:** Full `match` compilation is Phase 11's scope. TUP-03 only requires that `TuplePat` appears in match expressions and works correctly. The minimal implementation: handle `LetPat(TuplePat(...))` fully in Phase 9, and ensure Phase 11 can extend it. A single-arm `match (a,b) with (x,y) -> x+y` can be tested with `LetPat(TuplePat(...))` desugared form if the parser expresses it that way.

**AST for `match (1, 2) with | (a, b) -> a + b`:**
```
Match(
  scrutinee = Tuple([Number(1), Number(2)]),
  clauses   = [(TuplePat([VarPat("a"), VarPat("b")]), None, Add(Var("a"), Var("b")))]
)
```

For Phase 9, add a minimal `Match` case in Elaboration that handles the single-clause tuple case:
```fsharp
| Match (scrutinee, [(TuplePat (pats, _), None, body)], _) ->
    // Desugar to LetPat(TuplePat(...), scrutinee, body)
    elaborateExpr env (LetPat(TuplePat(pats, unknownSpan), scrutinee, body, unknownSpan))
```

This satisfies TUP-03 without requiring the full match decision chain (Phase 11).

### Pattern 4: Nested Tuples

**What:** When a tuple field is itself a tuple, it is stored as a `Ptr` (its GC_malloc'd address). The store/load must use the right type.

**Type inference at elaboration time:** All expressions elaborate to a `MlirValue` with a `Type` field. A nested tuple `(2, 3)` elaborates to `{ Type = Ptr }`. The outer GEP slot stores a `Ptr` value.

**LlvmStoreOp emits:** `llvm.store %innerTup, %slot1 : !llvm.ptr, !llvm.ptr` — this requires the Printer's `LlvmStoreOp` to use the actual type of the value, not hardcode `i64`.

**Current Printer.fs for LlvmStoreOp:**
```fsharp
| LlvmStoreOp(value, ptr) ->
    sprintf "%sllvm.store %s, %s : %s, !llvm.ptr"
        indent value.Name ptr.Name (printType value.Type)
```
This is already correct — it uses `value.Type` which will be `Ptr` for nested tuples. No change needed.

**Load type for nested tuple field:** When destructuring, a field that holds a nested tuple must be loaded as `Ptr`, not `i64`. The load type follows the sub-pattern: if sub-pattern is `TuplePat(...)`, the loaded value type is `Ptr`; if `VarPat`, it's `I64` by default.

**Simplification for Phase 9:** Since the compiler has no static type system, use `I64` for all leaf `VarPat` bindings and `Ptr` for `TuplePat` sub-pattern bindings. This is consistent with the uniform boxing convention.

**Revised LetPat(TuplePat) with type inference:**
```fsharp
let typeOfPat = function
    | TuplePat _ -> Ptr
    | _          -> I64  // VarPat, WildcardPat — treat as i64 (leaf value)
```

### Anti-Patterns to Avoid

- **Using `llvm.alloca` for tuples:** Tuples must use `GC_malloc` per TUP-01 success criterion ("no llvm.alloca for tuple storage"). The alloca would also break for tuples returned from functions.
- **Hardcoding `i64` as load type for nested tuples:** A nested tuple field contains a `Ptr`, not an `i64`. Load with the correct type based on sub-pattern shape.
- **Emitting `N * 8` as the byte count without checking field count:** Tuples with 0 or 1 fields are theoretically possible but should at minimum not crash; use `max 1 n` if needed (though LangThree tuples are always ≥ 2 elements in practice).
- **Forgetting to bind the scrutinee before GEP:** In `LetPat(TuplePat, bindExpr, bodyExpr)`, the bindExpr must be fully elaborated to obtain the `tupPtrVal` BEFORE the GEP ops.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Struct field indexing | Custom struct layout | `LlvmGEPLinearOp` with index 0..N-1 | Same op already used for closure envs; tested and verified |
| Heap allocation | Stack alloca | `GC_malloc(N * 8)` | TUP-01 explicitly forbids alloca; GC handles lifetime |
| New IR ops for tuple | New `LlvmGEPStructOp` DU case | Reuse `LlvmGEPLinearOp` | Already in MlirIR; already printed correctly |

**Key insight:** Tuples reuse the exact same MLIR op pattern as closure env allocation (`GC_malloc + GEP + store/load`). No new IR infrastructure required.

---

## Common Pitfalls

### Pitfall 1: Nested tuple field stored as i64 instead of Ptr

**What goes wrong:** `let (x, inner) = (1, (2, 3)) in let (y, z) = inner in x+y+z` crashes or produces wrong results.
**Why it happens:** The inner tuple `(2, 3)` elaborates to a `Ptr` (its GC_malloc address). When stored at index 1 of the outer tuple, it's stored as a `Ptr`. When loaded back for the inner destructure, it must be loaded as `Ptr`. If the code assumes all fields are `I64`, the inner tuple pointer is misinterpreted.
**How to avoid:** Use `typeOfPat` to choose the load type: `TuplePat(...)` → `Ptr`, `VarPat`/`WildcardPat` → `I64`.
**Warning signs:** Nested tuple test exits with wrong value or segfaults.

### Pitfall 2: SSA name counter not advanced for GEP/load pairs

**What goes wrong:** Duplicate SSA names like `%t5` used for both a GEP result and a later op.
**Why it happens:** `freshName` increments the counter; if the pattern emits multiple ops, each must call `freshName` exactly once for each SSA-result-producing op.
**How to avoid:** Call `freshName` once for `slotVal` (GEP result) and once for `fieldVal` (load result). The counter increments naturally.
**Warning signs:** MLIR parse error: "redefinition of value '%tN'"

### Pitfall 3: TuplePat sub-pattern depth handling

**What goes wrong:** A `TuplePat` containing another `TuplePat` as a sub-pattern crashes the elaborator.
**Why it happens:** Recursive sub-patterns require recursive elaboration. The naive approach only handles `VarPat` and `WildcardPat` leaves.
**How to avoid:** For Phase 9, the `LetPat(TuplePat)` implementation should recursively handle `TuplePat` sub-patterns by generating a fresh temp `Ptr` binding and recursively calling the tuple destructure logic. The nested test `let (x, inner) = ... in let (y, z) = inner` uses TWO `LetPat` calls (not one deeply nested `TuplePat`), so this pitfall only applies to patterns like `let (a, (b, c)) = expr`.
**Recommendation:** For Phase 9, support `VarPat`, `WildcardPat`, and nested `TuplePat` sub-patterns. Nested `TuplePat` at depth > 1 can be handled by recursion.

### Pitfall 4: match desugaring requires unknownSpan

**What goes wrong:** Constructing a synthetic `LetPat(TuplePat(...), ...)` node requires a `Span`. Using `Ast.unknownSpan` is the correct choice.
**How to avoid:** Import or reference `Ast.unknownSpan` in the Match desugar branch.
**Warning signs:** Compilation error about missing Span argument.

---

## Code Examples

Verified patterns consistent with existing codebase:

### Tuple Construction — complete op sequence for `(3, 4)`

```mlir
// Source: consistent with Phase 7 GC_malloc pattern
%c3 = arith.constant 3 : i64
%c4 = arith.constant 4 : i64
%bytes = arith.constant 16 : i64
%tup = llvm.call @GC_malloc(%bytes) : (i64) -> !llvm.ptr
%slot0 = llvm.getelementptr %tup[0] : (!llvm.ptr) -> !llvm.ptr, i64
llvm.store %c3, %slot0 : i64, !llvm.ptr
%slot1 = llvm.getelementptr %tup[1] : (!llvm.ptr) -> !llvm.ptr, i64
llvm.store %c4, %slot1 : i64, !llvm.ptr
```

### Tuple Destructuring — `let (a, b) = tup in a + b`

```mlir
// Source: consistent with Phase 5 closure env GEP/load pattern
%slot0 = llvm.getelementptr %tup[0] : (!llvm.ptr) -> !llvm.ptr, i64
%a = llvm.load %slot0 : !llvm.ptr -> i64
%slot1 = llvm.getelementptr %tup[1] : (!llvm.ptr) -> !llvm.ptr, i64
%b = llvm.load %slot1 : !llvm.ptr -> i64
%result = arith.addi %a, %b : i64
```

### Nested Tuple Destructuring — `let (x, inner) = (1, (2, 3))`

```mlir
// outer tuple: (1, innerTup)
%c1 = arith.constant 1 : i64
// inner tuple: (2, 3) → elaborates to %innerTup : Ptr
%c2 = arith.constant 2 : i64
%c3 = arith.constant 3 : i64
%innerBytes = arith.constant 16 : i64
%innerTup = llvm.call @GC_malloc(%innerBytes) : (i64) -> !llvm.ptr
%is0 = llvm.getelementptr %innerTup[0] : (!llvm.ptr) -> !llvm.ptr, i64
llvm.store %c2, %is0 : i64, !llvm.ptr
%is1 = llvm.getelementptr %innerTup[1] : (!llvm.ptr) -> !llvm.ptr, i64
llvm.store %c3, %is1 : i64, !llvm.ptr
// outer tuple: (1, innerTup)
%outerBytes = arith.constant 16 : i64
%outerTup = llvm.call @GC_malloc(%outerBytes) : (i64) -> !llvm.ptr
%os0 = llvm.getelementptr %outerTup[0] : (!llvm.ptr) -> !llvm.ptr, i64
llvm.store %c1, %os0 : i64, !llvm.ptr
%os1 = llvm.getelementptr %outerTup[1] : (!llvm.ptr) -> !llvm.ptr, i64
llvm.store %innerTup, %os1 : !llvm.ptr, !llvm.ptr   // store Ptr, not i64!
// destructure: let (x, inner) = outerTup
%xs0 = llvm.getelementptr %outerTup[0] : (!llvm.ptr) -> !llvm.ptr, i64
%x = llvm.load %xs0 : !llvm.ptr -> i64
%xs1 = llvm.getelementptr %outerTup[1] : (!llvm.ptr) -> !llvm.ptr, i64
%inner = llvm.load %xs1 : !llvm.ptr -> !llvm.ptr    // load as Ptr!
// destructure: let (y, z) = inner
%ys0 = llvm.getelementptr %inner[0] : (!llvm.ptr) -> !llvm.ptr, i64
%y = llvm.load %ys0 : !llvm.ptr -> i64
%ys1 = llvm.getelementptr %inner[1] : (!llvm.ptr) -> !llvm.ptr, i64
%z = llvm.load %ys1 : !llvm.ptr -> i64
%xy = arith.addi %x, %y : i64
%xyz = arith.addi %xy, %z : i64
```

### FsLit Test Format

```
// --- Command: bash -c 'OUTBIN=$(mktemp /tmp/langback_XXXXXX) && dotnet run --project %S/../../src/LangBackend.Cli/LangBackend.Cli.fsproj -- %input -o $OUTBIN && $OUTBIN; echo $?; rm -f $OUTBIN'
// --- Input:
let t = (3, 4) in let (a, b) = t in a + b
// --- Output:
7
```

---

## Key AST Facts

### `Tuple` node
```fsharp
| Tuple of Expr list * span: Span
```
Example: `(3, 4)` → `Tuple([Number(3, _), Number(4, _)], _)`

### `LetPat(TuplePat(...))` node
```fsharp
| LetPat of Pattern * Expr * Expr * span: Span
// Pattern case:
| TuplePat of Pattern list * span: Span
```
Example: `let (a, b) = t in a + b` → `LetPat(TuplePat([VarPat("a",_); VarPat("b",_)], _), Var("t",_), Add(Var("a",_), Var("b",_), _), _)`

### `Match` with `TuplePat` (TUP-03)
```fsharp
| Match of scrutinee: Expr * clauses: MatchClause list * span: Span
// MatchClause = Pattern * Expr option * Expr  (pattern, when-guard, body)
```
Example: `match (1, 2) with | (a, b) -> a + b` →
`Match(Tuple([Number(1,_); Number(2,_)], _), [(TuplePat([VarPat("a",_); VarPat("b",_)], _), None, Add(Var("a",_),Var("b",_),_))], _)`

### Existing `LetPat` cases (Phase 7)
```fsharp
| LetPat (WildcardPat _, bindExpr, bodyExpr, _) -> ...
| LetPat (VarPat (name, _), bindExpr, bodyExpr, _) -> ...
```
Phase 9 adds `LetPat (TuplePat (...))` immediately after these.

---

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| No heap types | GC_malloc structs | Phase 7 (closures), Phase 9 (tuples) | All heap objects tracked by GC |
| LetPat only for Wildcard/Var | LetPat for TuplePat too | Phase 9 | Destructuring let bindings work |

**Unchanged from Phase 7:**
- `LlvmGEPLinearOp` syntax in Printer: `llvm.getelementptr %ptr[i] : (!llvm.ptr) -> !llvm.ptr, i64` — verified correct for tuple field indexing
- `LlvmStoreOp` syntax: `llvm.store %val, %ptr : <valType>, !llvm.ptr` — correct for both `i64` and `Ptr` values
- `LlvmLoadOp` syntax: `%res = llvm.load %ptr : !llvm.ptr -> <resType>` — correct for both types

---

## Open Questions

1. **Tuple field type when loading for VarPat**
   - What we know: The compiler has no static type system. All scalars are `i64`; all heap objects are `Ptr`.
   - What's unclear: How does the elaborator know whether a tuple field was stored as `i64` or `Ptr`?
   - Recommendation: Use `typeOfPat` heuristic — `TuplePat` sub-pattern → load as `Ptr`; `VarPat`/`WildcardPat` → load as `I64`. This is correct for the test cases in scope and consistent with the untyped compiler convention.

2. **Match with TuplePat: desugar vs full match compiler**
   - What we know: Full match compilation is Phase 11. TUP-03 only needs TuplePat in match to work.
   - What's unclear: Does the Phase 9 test use `match` or `let (a,b) = ...`?
   - Recommendation: Add a minimal `Match([TuplePat(...)])` case that desugars to `LetPat(TuplePat(...))`. This satisfies TUP-03 and keeps Phase 11 clean.

3. **Phase 8 (Strings) dependency**
   - What we know: ROADMAP says Phase 9 depends on Phase 7, not Phase 8. String fields in tuples are out of scope for Phase 9.
   - What's unclear: Will Phase 9 tests use string-valued tuple fields?
   - Recommendation: All Phase 9 tests use integer-valued tuples only. String tuples are Phase 8 + Phase 9 combined, which is out of scope.

---

## Sources

### Primary (HIGH confidence)
- Direct codebase reading: `MlirIR.fs`, `Printer.fs`, `Elaboration.fs`, `Ast.fs` — confirmed all needed ops exist
- Phase 7 research + implementation: closure env uses identical GEP/store/load pattern
- MLIR 20 `llvm.getelementptr` syntax: confirmed from existing `LlvmGEPLinearOp` in `Printer.fs` line 83

### Secondary (MEDIUM confidence)
- LangThree `Ast.fs` tuple/pattern nodes: directly read from `../LangThree/src/LangThree/Ast.fs`
- Phase 7 RESEARCH.md patterns: `LlvmGEPLinearOp` as verified field accessor

---

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — reuses existing ops; no new dependencies
- Architecture: HIGH — tuple pattern is structurally identical to closure env (already working)
- Pitfalls: HIGH — nested type issue is the main risk; identified and solution documented

**Research date:** 2026-03-26
**Valid until:** 2026-09-26 (MLIR llvm dialect and Boehm GC API are stable)
