# Phase 10: Lists - Research

**Researched:** 2026-03-26
**Domain:** MLIR llvm dialect null pointers, GC_malloc cons cell allocation, list pattern matching via null check + GEP
**Confidence:** HIGH

---

## Summary

Phase 10 implements list types in the FunLangCompiler compiler. There are four concerns: (1) `[]` as a null pointer via `llvm.mlir.zero`, (2) cons cell allocation as a 16-byte `GC_malloc` with head/tail pointer stores, (3) list literal desugaring from `List [e1; e2; e3]` to nested `Cons(e1, Cons(e2, Cons(e3, EmptyList)))` in Elaboration, and (4) list pattern matching via a null-check then GEP load for `ConsPat(h, t)`.

All four concerns are handled in a single plan (`10-01-PLAN.md`) because they share the same MlirIR primitive additions (two new ops: `LlvmNullOp` and `LlvmIcmpOp`) and the Elaboration changes are tightly coupled. The pattern matching for `[]` / `h :: t` in a `match` expression is handled entirely in the Elaboration pass using existing `CfCondBrOp` infrastructure.

The key MLIR primitive for the null pointer case is `llvm.mlir.zero : !llvm.ptr` — this is semantically "the null pointer constant" and is distinct from `arith.constant 0 : i64` cast to a pointer. The success criterion explicitly requires this form. For list pattern matching, null-checking a pointer uses `llvm.icmp "eq"` with `llvm.mlir.zero` as the comparand.

**Primary recommendation:** Add `LlvmNullOp` and `LlvmIcmpOp` to MlirIR + Printer; elaborate `EmptyList` to `LlvmNullOp`; elaborate `Cons` to `GC_malloc(16)` + two stores; elaborate `List [...]` by folding right into nested cons; elaborate `LetPat(ConsPat)` + `Match` arms for `EmptyListPat`/`ConsPat` using `LlvmIcmpOp` + `CfCondBrOp`.

---

## Standard Stack

### Core

| Component | Version | Purpose | Why Standard |
|-----------|---------|---------|--------------|
| `llvm.mlir.zero : !llvm.ptr` | MLIR 20 | Null pointer constant | Semantically correct null ptr; distinct from integer 0; required by success criterion |
| `GC_malloc(16)` | bdw-gc 8.2.12 | Cons cell allocation | 16 bytes = 8 (head ptr) + 8 (tail ptr); uniform ptr-sized slot layout from Phase 7 |
| `llvm.icmp "eq" %ptr, %null : !llvm.ptr` | MLIR 20 | Null check for `[]` pattern | Standard LLVM icmp on pointers; result is i1 for CfCondBrOp |
| `llvm.getelementptr %ptr[0]` / `[1]` | MLIR 20 | Head/tail field access | Linear byte GEP by slot index (existing `LlvmGEPLinearOp`); slot 0 = head, slot 1 = tail |

### Supporting

| Tool | Version | Purpose | When to Use |
|------|---------|---------|-------------|
| `LlvmLoadOp` | existing | Load head/tail ptr values | After GEP to extract list head (i64) or tail (ptr) |
| `LlvmStoreOp` | existing | Store head/tail at cons cell fields | During `Cons` construction; slot 0 = head value, slot 1 = tail ptr |
| `CfCondBrOp` | existing | Branch on null/non-null | For `EmptyListPat` null check in match; reuses Phase 3 if-else mechanism |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `llvm.mlir.zero` | `arith.constant 0 : i64` + inttoptr | Wrong semantically; success criterion explicitly forbids it; `llvm.mlir.zero` is the correct MLIR 20 null pointer constant |
| `GC_malloc(16)` flat | `GC_malloc(n*8)` with struct type | Flat 16-byte alloc is simpler; no struct type definition needed; pointer casting handles head/tail access uniformly |
| `llvm.icmp` | `arith.cmpi` | `arith.cmpi` does not operate on `!llvm.ptr`; must use `llvm.icmp` for pointer comparison |

---

## Architecture Patterns

### Recommended Project Structure

```
src/FunLangCompiler.Compiler/
├── MlirIR.fs        # Add LlvmNullOp + LlvmIcmpOp to MlirOp
├── Printer.fs       # Emit llvm.mlir.zero and llvm.icmp ops
└── Elaboration.fs   # EmptyList → LlvmNullOp; Cons → GC_malloc+store; List → fold;
                     # LetPat(ConsPat) → null check + GEP loads;
                     # Match EmptyListPat/ConsPat arms → CfCondBrOp chain
tests/compiler/
├── 10-01-list-literal.flt         # [1; 2; 3] compiles, desugars to nested cons
└── 10-02-list-length.flt          # let rec length via [] / h::t pattern
```

### Pattern 1: EmptyList as Null Pointer

**What:** `EmptyList` elaborates to `LlvmNullOp` which prints as `llvm.mlir.zero : !llvm.ptr`.
**When to use:** Whenever `EmptyList` AST node appears.

```fsharp
// MlirIR.fs — new op
| LlvmNullOp of result: MlirValue
// result.Type must be Ptr

// Printer.fs
| LlvmNullOp(result) ->
    sprintf "%s%s = llvm.mlir.zero : !llvm.ptr" indent result.Name

// Elaboration.fs
| EmptyList _ ->
    let v = { Name = freshName env; Type = Ptr }
    (v, [LlvmNullOp(v)])
```

### Pattern 2: Cons Cell Allocation

**What:** `Cons(h, t)` elaborates to `GC_malloc(16)` + GEP to slot 0 (head store) + GEP to slot 1 (tail store).
**When to use:** Every `Cons` expression.

```fsharp
// Elaboration.fs
| Cons(headExpr, tailExpr, _) ->
    let (headVal, headOps) = elaborateExpr env headExpr
    let (tailVal, tailOps) = elaborateExpr env tailExpr
    let bytesVal  = { Name = freshName env; Type = I64 }
    let cellPtr   = { Name = freshName env; Type = Ptr }
    let headSlot  = { Name = freshName env; Type = Ptr }
    let tailSlot  = { Name = freshName env; Type = Ptr }
    let allocOps = [
        ArithConstantOp(bytesVal, 16L)
        LlvmCallOp(cellPtr, "@GC_malloc", [bytesVal])
        LlvmStoreOp(headVal, cellPtr)          // store head at slot 0 (raw ptr = base)
        LlvmGEPLinearOp(tailSlot, cellPtr, 1)  // slot 1 for tail
        LlvmStoreOp(tailVal, tailSlot)
    ]
    (cellPtr, headOps @ tailOps @ allocOps)
```

Note: Storing head directly at `cellPtr` (base address = slot 0) avoids a redundant GEP. Tail at slot 1 via `LlvmGEPLinearOp`.

### Pattern 3: List Literal Desugaring

**What:** `List [e1; e2; e3]` desugars to `Cons(e1, Cons(e2, Cons(e3, EmptyList)))` — a right fold.
**When to use:** `List` AST node with any element count (including empty list = EmptyList).

```fsharp
// Elaboration.fs — inside elaborateExpr
| List(elems, span) ->
    // Desugar in Elaboration: fold right into Cons chain
    let desugar =
        List.foldBack (fun elem acc ->
            Cons(elem, acc, span)
        ) elems (EmptyList span)
    elaborateExpr env desugar
```

### Pattern 4: Null Check for Pattern Matching

**What:** `LlvmIcmpOp` compares two `!llvm.ptr` values for equality. Used to check if a list pointer is null.
**When to use:** `EmptyListPat` in a match clause; also `ConsPat` (non-null check = `ne`).

```fsharp
// MlirIR.fs — new op
| LlvmIcmpOp of result: MlirValue * predicate: string * lhs: MlirValue * rhs: MlirValue
// predicate: "eq" or "ne"; lhs/rhs both Ptr; result is I1

// Printer.fs
| LlvmIcmpOp(result, pred, lhs, rhs) ->
    sprintf "%s%s = llvm.icmp \"%s\" %s, %s : !llvm.ptr"
        indent result.Name pred lhs.Name rhs.Name

// Usage in elaboration for EmptyListPat:
//   nullVal <- LlvmNullOp
//   cmpVal  <- LlvmIcmpOp("eq", listPtr, nullVal)
//   CfCondBrOp(cmpVal, matchLabel, [], nextLabel, [])
```

### Pattern 5: LetPat(ConsPat) — Head/Tail Extraction

**What:** `let (h :: t) = expr in body` extracts head and tail via null-check + GEP + load.
**Note:** This is for `LetPat` with `ConsPat`; the `Match` case in Phase 11 will handle arbitrary match.
**For Phase 10:** Only need to support the `match lst with | [] -> e1 | h :: t -> e2` pattern sufficient for a recursive length function.

```fsharp
// Head extraction: load from base ptr (slot 0)
// %head = llvm.load %cellPtr : !llvm.ptr -> i64

// Tail extraction: GEP slot 1, then load
// %tailSlot = llvm.getelementptr %cellPtr[1] : (!llvm.ptr) -> !llvm.ptr, i64
// %tail = llvm.load %tailSlot : !llvm.ptr -> !llvm.ptr
```

### Pattern 6: Match with EmptyListPat + ConsPat

**What:** Compile `match lst with | [] -> e1 | h :: t -> e2` using null check + CfCondBrOp.
**Strategy:** Inline into Elaboration as a two-branch conditional. This is simpler than full match compilation (Phase 11) and sufficient for Phase 10 success criteria.

```fsharp
// For match with exactly: EmptyListPat arm + ConsPat arm (common list recursion pattern)
// Step 1: emit LlvmNullOp to get null constant
// Step 2: emit LlvmIcmpOp("eq", scrutinee, nullPtr) -> isNull: I1
// Step 3: emit CfCondBrOp(isNull, emptyLabel, [], consLabel, [])
// Step 4: in emptyLabel block — elaborate e1, branch to merge
// Step 5: in consLabel block — bind h = load cellPtr, t = load (GEP cellPtr 1), elaborate e2, branch to merge
// Step 6: merge block takes result arg
```

### Anti-Patterns to Avoid

- **Using `arith.constant 0 : i64` cast to ptr for null:** Must use `llvm.mlir.zero : !llvm.ptr`. The success criterion explicitly checks for this in emitted `.mlir`.
- **Using `arith.cmpi` to compare pointers:** `arith.cmpi` only works on integer types. Use `llvm.icmp` for `!llvm.ptr` comparisons.
- **Storing head as i64 without type awareness:** Head of a list can be any type. In Phase 10 scope (integer lists), head is i64. The load type in `LlvmLoadOp` must match the stored type.
- **Not null-checking before GEP on a cons cell:** If a `ConsPat` branch is taken, the non-null check ensures the pointer is valid. The GEP is safe only after confirming `isNull = false`.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Null pointer constant | `arith.constant 0` + bitcast | `llvm.mlir.zero : !llvm.ptr` | Correct MLIR 20 idiom; verifier rejects integer-to-pointer cast in typed context |
| Pointer null check | Integer comparison | `llvm.icmp "eq" %ptr, %null : !llvm.ptr` | `arith.cmpi` does not accept `!llvm.ptr` operands |
| List desugaring | Recursive elaboration | `List.foldBack` in Elaboration | Three lines, correct, no new AST passes needed |
| Cons cell field layout | Named struct type | Flat 16-byte layout with GEP by index | Matches existing closure env layout; no new struct type needed in MlirType |

**Key insight:** The cons cell layout (two pointer-sized slots: head at index 0, tail at index 1) mirrors the existing closure env layout (fn_ptr at index 0, captures at 1..N). The `LlvmGEPLinearOp` primitive already handles this. No new GEP variant is needed.

---

## Common Pitfalls

### Pitfall 1: Wrong null pointer representation

**What goes wrong:** Success criterion check fails — emitted `.mlir` for `[]` contains integer zero instead of `llvm.mlir.zero`.
**Why it happens:** Developer uses `ArithConstantOp(v, 0L)` and tries to use I64 0 as a pointer.
**How to avoid:** Add `LlvmNullOp` to MlirIR and use it exclusively for `EmptyList` elaboration.
**Warning signs:** MLIR verifier error about type mismatch; success criterion explicitly fails.

### Pitfall 2: Using arith.cmpi on pointer types

**What goes wrong:** `mlir-opt` rejects the program with "type mismatch in operands".
**Why it happens:** `arith.cmpi` requires integer operands; `!llvm.ptr` is not an integer in MLIR 20.
**How to avoid:** Use `llvm.icmp "eq"` for all pointer-to-pointer comparisons.
**Warning signs:** `error: 'arith.cmpi' op operand #0 must be signless integer or index`

### Pitfall 3: Load type mismatch for head

**What goes wrong:** `mlir-opt` rejects load with wrong inferred type, or runtime produces wrong value.
**Why it happens:** `LlvmLoadOp` in existing code always loads as I64 (from `result.Type`). Tail pointer is `Ptr`, not `I64`.
**How to avoid:** When loading the tail (slot 1 of cons cell), use `result.Type = Ptr`. When loading the head (slot 0), use `result.Type = I64` for integer lists.
**Warning signs:** Type mismatch in MLIR or wrong value at runtime.

### Pitfall 4: List.foldBack direction for List desugaring

**What goes wrong:** List elements appear in reverse order in the cons chain.
**Why it happens:** `List.fold` (left fold) builds cons from right to left but in wrong order. Must use `List.foldBack` for right fold.
**How to avoid:** Use `List.foldBack (fun elem acc -> Cons(elem, acc, span)) elems (EmptyList span)`.
**Warning signs:** `[1; 2; 3]` has head=3 instead of head=1.

### Pitfall 5: Match scope — only support two-arm null+cons pattern for Phase 10

**What goes wrong:** Trying to implement full general `match` in Phase 10 scope bloat.
**Why it happens:** Phase 11 covers full pattern matching. Phase 10 only needs the null-check + cons-extract pattern for the list length success criterion.
**How to avoid:** In Elaboration, special-case `Match(scrutinee, [EmptyListPat, None, e1; ConsPat(hPat, tPat), None, e2], _)` with a null-check path. All other match patterns remain `failwithf "unsupported"` until Phase 11.
**Warning signs:** Scope creep; trying to implement tuple patterns, const patterns, etc. in Phase 10.

---

## Code Examples

### llvm.mlir.zero (null pointer constant)

```mlir
// Source: MLIR 20 LLVM dialect docs; verified on LLVM 20.1.4
%null = llvm.mlir.zero : !llvm.ptr
```

### llvm.icmp for pointer equality

```mlir
// Source: MLIR 20 LLVM dialect; verified
%is_null = llvm.icmp "eq" %list_ptr, %null : !llvm.ptr
```

### Cons cell construction

```mlir
// Source: verified pattern matching existing closure env layout
%bytes = arith.constant 16 : i64
%cell = llvm.call @GC_malloc(%bytes) : (i64) -> !llvm.ptr
llvm.store %head, %cell : i64, !llvm.ptr           // head at slot 0 (base)
%tail_slot = llvm.getelementptr %cell[1] : (!llvm.ptr) -> !llvm.ptr, i64
llvm.store %tail, %tail_slot : !llvm.ptr, !llvm.ptr  // tail ptr at slot 1
```

### Head/tail extraction

```mlir
// Source: verified GEP pattern
%head = llvm.load %cell : !llvm.ptr -> i64
%tail_slot = llvm.getelementptr %cell[1] : (!llvm.ptr) -> !llvm.ptr, i64
%tail = llvm.load %tail_slot : !llvm.ptr -> !llvm.ptr
```

### List length function emitted MLIR shape

```mlir
// let rec length lst = match lst with | [] -> 0 | h :: t -> 1 + length t
func.func @length(%arg0: !llvm.ptr) -> i64 {
    %null = llvm.mlir.zero : !llvm.ptr
    %is_null = llvm.icmp "eq" %arg0, %null : !llvm.ptr
    cf.cond_br %is_null, ^empty0, ^cons0
  ^empty0:
    %zero = arith.constant 0 : i64
    return %zero : i64
  ^cons0:
    %head = llvm.load %arg0 : !llvm.ptr -> i64
    %tail_slot = llvm.getelementptr %arg0[1] : (!llvm.ptr) -> !llvm.ptr, i64
    %tail = llvm.load %tail_slot : !llvm.ptr -> !llvm.ptr
    %one = arith.constant 1 : i64
    %sub_len = func.call @length(%tail) : (!llvm.ptr) -> i64
    %result = arith.addi %one, %sub_len : i64
    return %result : i64
}
```

---

## AST Analysis

### List-related AST nodes (from FunLang/src/FunLang/Ast.fs)

```fsharp
// Expression nodes
| EmptyList of span: Span                   // [] — empty list literal
| List of Expr list * span: Span            // [e1; e2; e3] — list literal
| Cons of Expr * Expr * span: Span          // h :: t — cons operator

// Pattern nodes
| ConsPat of Pattern * Pattern * span: Span  // h :: t pattern
| EmptyListPat of span: Span                 // [] pattern
```

### Key observations

1. `EmptyList` and `List []` are both represented — `List []` with empty element list should desugar to `EmptyList`. Check: `List.foldBack` over empty list returns `EmptyList` directly. Correct.
2. `ConsPat(hPat, tPat, _)` — `hPat` binds the head (I64 for integer lists), `tPat` binds the tail (Ptr).
3. `EmptyListPat` is a zero-argument pattern that always matches null pointer.
4. `LetRec` with a `Ptr`-typed parameter: the `LetRec` elaboration hardcodes `%arg0: I64`. For list functions, the parameter is a list pointer (`Ptr`). This requires a change to how `LetRec` infers parameter type. See Open Questions.

---

## Critical Issue: LetRec Parameter Type

The current `LetRec` elaboration hardcodes `%arg0: I64`:

```fsharp
// Elaboration.fs line ~333
let sig_ : FuncSignature =
    { MlirName = "@" + name; ParamTypes = [I64]; ReturnType = I64; ClosureInfo = None }
let bodyEnv : ElabEnv =
    { Vars = Map.ofList [(param, { Name = "%arg0"; Type = I64 })]
```

For `let rec length lst = match lst with | [] -> 0 | h :: t -> ...`, `lst` must be typed as `Ptr` (list pointer). The return type can be inferred from the body after elaboration (Phase 4 already infers return from `bodyVal.Type`), but the parameter type must be known before elaborating the body.

**Resolution:** Add an optional `paramType` hint to the `LetRec` elaboration. Since FunLangCompiler has no full type system, use a heuristic: if the `LetRec` body is a `Match` expression and the scrutinee is the parameter variable, and any arm contains `EmptyListPat` or `ConsPat`, then the parameter is `Ptr`. Otherwise default to `I64`.

Alternatively: Always elaborate `LetRec` parameters as `Ptr` when the function body uses `Match`. Or: add a `Ptr` type annotation in the FsLit test.

**Simpler approach:** Look at what the function does in the body before committing the param type. Since elaboration is a single pass, we need the param type upfront. The cleanest solution for Phase 10 is: if the LetRec body `Match`es the parameter with `EmptyListPat` or `ConsPat` arms, set paramType = Ptr. This is a structural check on the AST, not type inference.

---

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| No list support | Null ptr + GC_malloc cons cells | Phase 10 | First heap-allocated recursive data type |
| LetRec always I64 param | LetRec with Ptr param for list funcs | Phase 10 | Enables recursive list traversal |

---

## Open Questions

1. **LetRec parameter type for list functions**
   - What we know: Current elaboration hardcodes I64 param for LetRec. List functions need Ptr param.
   - What's unclear: Best mechanism — AST pre-scan, heuristic, or explicit annotation.
   - Recommendation: Pre-scan the LetRec body for EmptyListPat/ConsPat to detect list parameter type. If match clauses contain list patterns → paramType = Ptr, else I64.

2. **Mixed match patterns (int arms + list arms)**
   - What we know: Phase 10 needs to support exactly `[] -> e1 | h :: t -> e2` arms.
   - What's unclear: What if a Match has other arm types mixed in?
   - Recommendation: Phase 10 only handles the two-arm list pattern. Other match shapes → failwith until Phase 11.

3. **Head type for non-integer lists**
   - What we know: All current tests use integer lists.
   - What's unclear: Strings, closures, nested lists — all would need Ptr-typed heads.
   - Recommendation: Phase 10 scope = integer lists only. Head type = I64. Future: uniform Ptr boxing in v3.

4. **FuncOp parameter type for `@length`-style functions**
   - What we know: `FuncOp.InputTypes` is `MlirType list`. Currently only `I64` is used for LetRec funcs.
   - Recommendation: When LetRec paramType is determined to be Ptr, set `InputTypes = [Ptr]` in the emitted FuncOp and `FuncSignature.ParamTypes = [Ptr]`.

---

## Sources

### Primary (HIGH confidence)
- Direct codebase analysis: MlirIR.fs, Elaboration.fs, Printer.fs, Ast.fs (FunLang) — all read
- MLIR 20 LLVM dialect: `llvm.mlir.zero`, `llvm.icmp`, `llvm.getelementptr` — same toolchain used in Phase 7
- Phase 7 patterns: cons cell layout identical to closure env layout (GEP by slot index)
- .planning/ROADMAP.md Phase 10 goal and success criteria — verified all four requirements

### Secondary (MEDIUM confidence)
- MLIR LLVM dialect documentation for pointer operations
- Existing Phase 7 FsLit tests (07-01, 07-02, 07-03) as format reference

---

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all components exist and work (Phase 7 established GC_malloc + GEP + load/store)
- Architecture: HIGH — cons cell mirrors closure env layout exactly; patterns are proven
- Pitfalls: HIGH — null pointer representation and pointer comparison issues identified with clear fixes
- Open question (LetRec param type): MEDIUM — solvable with AST pre-scan, specific implementation TBD in plan

**Research date:** 2026-03-26
**Valid until:** 2026-09-26
