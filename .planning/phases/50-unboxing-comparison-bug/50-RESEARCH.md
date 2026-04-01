# Phase 50: Unboxing Comparison Bug - Research

**Researched:** 2026-04-01
**Domain:** F# compiler/Elaboration — MLIR type coercion for boxed integer comparisons
**Confidence:** HIGH

## Summary

This phase fixes a type mismatch bug in the MLIR elaboration of comparison operators (`>`, `<`, `>=`, `<=`) when the operand has been coerced to `Ptr` type due to `isPtrParamBody` heuristics. The root cause is that `isPtrParamBody` correctly detects that a lambda parameter needs Ptr type when it is passed to ADT constructors like `Some x`, but then the comparison operators (`LessThan`, `GreaterThan`, etc.) blindly emit `arith.cmpi` with the Ptr-typed operand — which is invalid in MLIR because `arith.cmpi` only operates on integer types.

The fix is surgical: in the elaboration of `LessThan`, `GreaterThan`, `LessEqual`, `GreaterEqual`, check if either operand has type `Ptr` and if so, emit a `ptrtoint` (`LlvmPtrToIntOp`) to convert to `I64` before the comparison. The precedent for this pattern already exists in `coerceToI64` and is used throughout the compiler.

The `Equal` and `NotEqual` operators already have special `if lv.Type = Ptr` handling (for string comparison via `strcmp`) and do NOT need the same fix — they handle Ptr operands intentionally.

**Primary recommendation:** Add Ptr-to-I64 coercion in the four ordinal comparison cases (`LessThan`, `GreaterThan`, `LessEqual`, `GreaterEqual`) in `elaborateExpr`, and add/update the test for `List.choose` and `List.filter` with comparison predicates on integer lists.

## Standard Stack

This phase is a pure compiler bug fix. No new libraries or tools are needed.

### Core Files

| File | Role | What changes |
|------|------|--------------|
| `src/LangBackend.Compiler/Elaboration.fs` | Main compiler — `elaborateExpr` | Add Ptr→I64 coercion in LessThan/GreaterThan/LessEqual/GreaterEqual cases |
| `tests/compiler/35-08-list-tryfind-choose.fun` | Test source | Add `List.choose` and `List.filter` with comparison predicates |
| `tests/compiler/35-08-list-tryfind-choose.flt` | Test oracle | Update expected output |

### Architecture Patterns

The compiler uses a uniform closure ABI: `(%arg0: !llvm.ptr, %arg1: i64) -> i64`. When a lambda parameter `x` is detected (by `isPtrParamBody`) as needing Ptr type (e.g., because it is passed to `Some x`), the inner closure body inserts:

```mlir
%t0 = llvm.inttoptr %arg1 : i64 to !llvm.ptr
```

After this, `x` maps to `%t0: !llvm.ptr`. If the body then compares `x > 2`:

```fsharp
| GreaterThan (lhs, rhs, _) ->
    let (lv, lops) = elaborateExpr env lhs   // lv = %t0 : Ptr
    let (rv, rops) = elaborateExpr env rhs   // rv = %t1 : I64 (constant 2)
    let result = { Name = freshName env; Type = I1 }
    (result, lops @ rops @ [ArithCmpIOp(result, "sgt", lv, rv)])
// EMITS: arith.cmpi sgt, %t0, %t1 : !llvm.ptr   <-- INVALID
```

The fix inserts `ptrtoint` before the comparison:

```fsharp
| GreaterThan (lhs, rhs, _) ->
    let (lv, lops) = elaborateExpr env lhs
    let (rv, rops) = elaborateExpr env rhs
    let (lv64, lCoerce) = if lv.Type = Ptr then
                              let r = { Name = freshName env; Type = I64 }
                              (r, [LlvmPtrToIntOp(r, lv)])
                          else (lv, [])
    let (rv64, rCoerce) = if rv.Type = Ptr then
                              let r = { Name = freshName env; Type = I64 }
                              (r, [LlvmPtrToIntOp(r, rv)])
                          else (rv, [])
    let result = { Name = freshName env; Type = I1 }
    (result, lops @ rops @ lCoerce @ rCoerce @ [ArithCmpIOp(result, "sgt", lv64, rv64)])
// EMITS: arith.cmpi sgt, %tN, %tM : i64   <-- VALID
```

The same pattern applies to `LessThan`, `LessEqual`, `GreaterEqual`.

## Architecture Patterns

### Pattern 1: Ptr-to-I64 coercion before integer operation

**What:** When an operand is typed `Ptr` but an integer MLIR operation requires `I64`, emit `LlvmPtrToIntOp` first.

**When to use:** Any comparison or arithmetic operation where the operand may have been coerced to `Ptr` by `isPtrParamBody` or by closure ABI bridging.

**Existing precedent in Elaboration.fs:**

```fsharp
// Source: Elaboration.fs, coerceToI64 helper (line ~264)
let private coerceToI64 (env: ElabEnv) (v: MlirValue) : (MlirValue * MlirOp list) =
    match v.Type with
    | I64 -> (v, [])
    | I1  ->
        let r = { Name = freshName env; Type = I64 }
        (r, [ArithExtuIOp(r, v)])
    | Ptr ->
        let r = { Name = freshName env; Type = I64 }
        (r, [LlvmPtrToIntOp(r, v)])
    | _ -> (v, [])
```

Use `coerceToI64` for both operands before each ordinal comparison operation.

### Pattern 2: The isPtrParamBody trigger

**What:** `isPtrParamBody` returns `true` when a parameter is passed to an ADT constructor, causing the lambda to coerce `%arg1: i64` to `%t0: !llvm.ptr`. This is correct for record access, fst/snd, etc. The bug is that comparison operators didn't account for this coercion.

**When triggered (relevant case):**
```fsharp
// Elaboration.fs line ~407
| Constructor(_, Some(Var(v, _)), _) when v = paramName -> true
```

So `fun x -> if x > 2 then Some x else None` triggers `isPtrParamBody "x"` to return `true` because `Some x` uses `x` as a constructor argument. After coercion, `x: Ptr`. Then `x > 2` fails.

### Pattern 3: Equal/NotEqual already handle Ptr (string comparison)

`Equal` and `NotEqual` have `if lv.Type = Ptr` branches that invoke `strcmp` for string comparison. These are correct and must NOT be changed. The ordinal operators (`<`, `>`, `<=`, `>=`) do not have this branch and incorrectly pass `Ptr` to `arith.cmpi`.

### Anti-Patterns to Avoid

- **Don't add a new `isIntPtrParamBody` heuristic:** The root issue is not the heuristic but the missing coercion at the comparison site. The heuristic is correct — `Some x` genuinely needs `x` to be Ptr so it can be stored in the ADT struct.
- **Don't change `Equal`/`NotEqual` Ptr handling:** Those branches are for string comparison via `strcmp`. Changing them would break string equality tests.
- **Don't handle arithmetic operators (Add, Subtract, etc.):** Those operators assume the argument IS an integer. If a Ptr is added/subtracted, that would be a language type error, not a boxing issue. Only comparison operators are affected in the comparison-predicate pattern.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Ptr→I64 coercion | Custom logic | `coerceToI64 env lv` | Already defined, handles I1/Ptr/I64 uniformly |
| New test pattern | New .fun file | Extend existing 35-08 | Single test file covers all tryFind/choose scenarios |

**Key insight:** The `coerceToI64` helper already exists and handles all MlirType cases correctly. The fix is simply to apply it to both LHS and RHS of each ordinal comparison operator.

## Common Pitfalls

### Pitfall 1: Only fixing one comparison operator
**What goes wrong:** Fix `GreaterThan` but forget `LessThan`, `LessEqual`, `GreaterEqual`. Future tests with `<`, `<=`, `>=` on boxed-ptr params will fail.
**Why it happens:** The fix looks mechanical; skipping some feels like saving work.
**How to avoid:** Fix all four ordinal comparison operators in one commit.
**Warning signs:** Tests with `x < h` in `List._insert` or `x <= n` style predicates fail at MLIR verification.

### Pitfall 2: Breaking Equal/NotEqual string comparison
**What goes wrong:** Adding Ptr→I64 coercion to `Equal`/`NotEqual` would convert string Ptr operands to integers, then do integer equality instead of `strcmp`.
**Why it happens:** Looks symmetric with the ordinal operators.
**How to avoid:** Leave `Equal`/`NotEqual` alone. They already handle `Ptr` correctly via string comparison path.
**Warning signs:** String equality tests (like `s = "hello"`) produce wrong results.

### Pitfall 3: Wrong test oracle
**What goes wrong:** Writing the test but getting the expected output wrong.
**Why it happens:** `List.choose (fun x -> if x > 2 then Some x else None) [1;2;3;4]` should return `[3;4]`. Printing these with a for-in loop gives `3` then `4` on separate lines.
**How to avoid:** Manually trace the expected output before writing the `.flt` oracle.

### Pitfall 4: Coercion ordering — emit lCoerce/rCoerce AFTER elaborating both sides
**What goes wrong:** Inserting coerce ops at wrong position in the op list.
**Why it happens:** `lops` and `rops` are the ops for computing the values; coerce ops must come after both values are computed.
**How to avoid:** Pattern: `lops @ rops @ lCoerce @ rCoerce @ [ArithCmpIOp(...)]`.

## Code Examples

### The fix pattern for GreaterThan (apply to all four ordinal operators)

```fsharp
// Source: Elaboration.fs — elaborateExpr, comparison cases
| GreaterThan (lhs, rhs, _) ->
    let (lv, lops) = elaborateExpr env lhs
    let (rv, rops) = elaborateExpr env rhs
    // Unbox Ptr→I64 if operand was coerced by isPtrParamBody (e.g., lambda param used in Some x AND x > n)
    let (lv64, lCoerce) = coerceToI64 env lv
    let (rv64, rCoerce) = coerceToI64 env rv
    let result = { Name = freshName env; Type = I1 }
    (result, lops @ rops @ lCoerce @ rCoerce @ [ArithCmpIOp(result, "sgt", lv64, rv64)])
```

### Test source to add to 35-08-list-tryfind-choose.fun

```fsharp
// Add after existing content:
let filtered = List.filter (fun x -> x > 2) [1;2;3;4]
let chosen2 = List.choose (fun x -> if x > 2 then Some x else None) [1;2;3;4]
let _ = (for x in filtered do println (to_string x))
let _ = (for x in chosen2 do println (to_string x))
```

Expected additional output:
```
3
4
3
4
```

### Minimal reproduction

The bug is triggered by ANY lambda where:
1. The body uses the parameter as a constructor argument (`Some x`, `Ok x`, etc.) — this makes `isPtrParamBody` return `true`
2. AND the body also compares the parameter with `>`, `<`, `>=`, `<=`

Example minimal trigger:
```
let _ = List.filter (fun x -> x > 2) [3;4]
```

## State of the Art

| Old Behavior | Fixed Behavior | Phase | Impact |
|--------------|---------------|-------|--------|
| `arith.cmpi sgt, %ptr, %int : !llvm.ptr` — MLIR verification error | `ptrtoint` then `arith.cmpi sgt, %i64, %int : i64` — valid | 50 | `List.filter`/`List.choose` with comparison predicates work on integer lists |

## Open Questions

1. **Does `List._insert x xs` (used by `List.sort`) have this bug?**
   - What we know: `_insert x xs` where `x` is a cons head loaded as `I64`, and the body is `if x < h then ...`. `isPtrParamBody "x"` — does `x :: h :: t` (Cons node) trigger Ptr detection? Looking at `hasParamPtrUse`, `Cons(Var("x"), ...)` is NOT listed as a Ptr indicator. So `isPtrParamBody` returns `false` for `_insert`.
   - What's unclear: Does `sort` currently work or does it also fail?
   - Recommendation: Test `List.sort [3;1;4;1;5]` as part of the verification run. If it passes now, the fix doesn't change anything for `_insert`.

2. **Should `Add`/`Subtract`/`Multiply` also get Ptr→I64 coercion?**
   - What we know: Arithmetic operators don't check for Ptr operands. But if a lambda has `x + 1` where `x` is also used in `Some x`, then `x: Ptr` and `arith.addi` would also fail.
   - What's unclear: Whether any existing test or realistic code triggers this.
   - Recommendation: Fix arithmetic operators (Add, Subtract, Multiply, Divide, Modulo) with the same coercion pattern as a defensive measure. It costs nothing and prevents future bugs.

## Sources

### Primary (HIGH confidence)
- Direct code inspection of `src/LangBackend.Compiler/Elaboration.fs` — `elaborateExpr`, `isPtrParamBody`, `hasParamPtrUse`, `coerceToI64`
- Direct code inspection of `src/LangBackend.Compiler/Printer.fs` — `ArithCmpIOp` printer, `IndirectCallOp` printer
- Direct code inspection of `src/LangBackend.Compiler/MlirIR.fs` — type definitions
- Direct code inspection of `src/LangBackend.Compiler/lang_runtime.h` — `LangClosureFn` typedef
- Direct code inspection of `src/LangBackend.Compiler/lang_runtime.c` — `LangCons` layout, closure ABI
- Direct code inspection of `Prelude/List.fun` — `filter`, `choose`, `_insert` implementations
- Direct code inspection of `tests/compiler/35-08-list-tryfind-choose.fun` and `.flt` — current test state

### Secondary (MEDIUM confidence)
- `.planning/STATE.md` — confirms blocker description: "List.choose 비교 람다에서 arith.cmpi + !llvm.ptr 타입 불일치"

## Metadata

**Confidence breakdown:**
- Bug location: HIGH — exact lines identified in Elaboration.fs (`LessThan`, `GreaterThan`, `LessEqual`, `GreaterEqual` cases at lines 1081-1100)
- Fix approach: HIGH — `coerceToI64` helper already exists and is the correct tool
- Test plan: HIGH — test file location and expected output fully determined
- Side effects: HIGH — Equal/NotEqual unaffected; arithmetic operators not in scope per requirements

**Research date:** 2026-04-01
**Valid until:** Indefinitely (compiler source code, not an external API)
