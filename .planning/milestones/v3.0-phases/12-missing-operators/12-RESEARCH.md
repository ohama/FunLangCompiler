# Phase 12: Missing Operators - Research

**Researched:** 2026-03-26
**Domain:** MLIR codegen, AST desugaring, operator lowering
**Confidence:** HIGH

## Summary

Phase 12 adds five missing operators to the LangBackend compiler: Modulo (%), Char literals, PipeRight (|>), ComposeRight (>>), and ComposeLeft (<<). All five AST nodes already exist in LangThree's Ast.fs. The compiler pipeline (Elaboration.fs) has no cases for them, causing match exceptions at runtime.

The work splits cleanly into three categories:
1. **New MlirOp + one Elaboration case**: Modulo requires `ArithRemSIOp` added to MlirIR.fs, a print case in Printer.fs, and one elaboration case matching `Divide`'s pattern exactly.
2. **Trivial constant lowering**: Char is already an `int64` value (Unicode code point); elaboration emits `ArithConstantOp` just like `Number`.
3. **AST desugaring at elaboration time**: PipeRight, ComposeRight, ComposeLeft are never lowered to new MlirOps — they desugar to existing `App` and `Lambda` nodes that the existing elaborator handles recursively.

**Primary recommendation:** Implement all five operators in one cohesive plan; group Modulo+Char (pure lowering) and Pipe+Compose (desugaring) as two tasks within the same plan.

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| MLIR `arith` dialect | LLVM 20 | Integer arithmetic ops | All existing arith ops (`addi`, `subi`, `muli`, `divsi`) live here |
| MLIR `arith.remsi` | LLVM 20 | Signed integer remainder | Canonical op for `%` on `i64`; same format as `arith.divsi` |

### No New Dependencies

All five operators reuse existing infrastructure. No new NuGet packages, no new MLIR dialects.

## Architecture Patterns

### AST Representation (LangThree/src/LangThree/Ast.fs)

```fsharp
// Lines 65, 105-111
| Char of char * span: Span            // Char literal: 'A'
| PipeRight of left: Expr * right: Expr * span: Span       // x |> f
| ComposeRight of left: Expr * right: Expr * span: Span    // f >> g
| ComposeLeft of left: Expr * right: Expr * span: Span     // f << g
| Modulo of Expr * Expr * Span
```

### Pattern 1: New MlirOp Case (Modulo)

Add `ArithRemSIOp` to MlirIR.fs alongside `ArithDivSIOp`:

```fsharp
// MlirIR.fs — add after ArithDivSIOp line 44
| ArithRemSIOp    of result: MlirValue * lhs: MlirValue * rhs: MlirValue
```

Add print case in Printer.fs after the `ArithDivSIOp` case:

```fsharp
| ArithRemSIOp(result, lhs, rhs) ->
    sprintf "%s%s = arith.remsi %s, %s : %s"
        indent result.Name lhs.Name rhs.Name (printType result.Type)
```

Add elaboration case in Elaboration.fs matching the `Divide` pattern exactly:

```fsharp
| Modulo (lhs, rhs, _) ->
    let (lv, lops) = elaborateExpr env lhs
    let (rv, rops) = elaborateExpr env rhs
    let result = { Name = freshName env; Type = I64 }
    (result, lops @ rops @ [ArithRemSIOp(result, lv, rv)])
```

### Pattern 2: Char as int64 (trivial)

`char` in .NET is a UTF-16 code point. F# `int c` converts it to `int`. Cast to `int64` for `ArithConstantOp`.

```fsharp
| Char (c, _) ->
    let v = { Name = freshName env; Type = I64 }
    (v, [ArithConstantOp(v, int64 (int c))])
```

'A' = 65, so this emits `arith.constant 65 : i64`. Exit code 65 confirms correctness.

### Pattern 3: PipeRight Desugar

`x |> f` is syntactic sugar for `f x` (i.e., `App(f, x)`). No new MlirOp needed.

```fsharp
| PipeRight (left, right, span) ->
    // x |> f  ≡  f x  ≡  App(right, left)
    elaborateExpr env (App(right, left, span))
```

This is a one-liner that recursively dispatches to the existing `App` elaboration path.

### Pattern 4: ComposeRight Desugar (f >> g)

`f >> g` creates a new function `fun x -> g (f x)`. Since `elaborateExpr` handles `Lambda` and `App`, we desugar at elaboration time using a synthetic fresh parameter name:

```fsharp
| ComposeRight (f, g, span) ->
    // f >> g  ≡  fun x -> g (f x)
    let param = freshName env   // generates a unique SSA-safe name like "%t5"
    // But Lambda expects a plain string name, not %t5.
    // Use a gensym'd F# identifier: __comp_N
    let n = env.Counter.Value
    env.Counter.Value <- n + 1
    let paramName = sprintf "__comp_%d" n
    let innerApp = App(f, Var(paramName, span), span)
    let outerApp = App(g, innerApp, span)
    let lambda   = Lambda(paramName, outerApp, span)
    elaborateExpr env lambda
```

**Important:** Use a fresh name that does not collide with user variables. Prefix `__comp_` is safe since the LangThree parser never produces identifiers starting with `__`.

### Pattern 5: ComposeLeft Desugar (f << g)

`f << g` creates `fun x -> f (g x)` — same as ComposeRight but with f and g swapped:

```fsharp
| ComposeLeft (f, g, span) ->
    // f << g  ≡  fun x -> f (g x)
    let n = env.Counter.Value
    env.Counter.Value <- n + 1
    let paramName = sprintf "__comp_%d" n
    let innerApp = App(g, Var(paramName, span), span)
    let outerApp = App(f, innerApp, span)
    let lambda   = Lambda(paramName, outerApp, span)
    elaborateExpr env lambda
```

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Modulo op | Custom shift-subtract loop | `arith.remsi` | Already in the arith dialect; one line |
| Char encoding | UTF-8 codec | `int64 (int c)` | F# `int` gives the Unicode code point directly |
| Compose closures | New MLIR closure machinery | Desugar to Lambda+App | Existing Lambda elaboration handles captures already |

**Key insight:** Pipe and compose never reach the MLIR level as their own ops. They are purely syntactic sugar eliminated in the elaboration pass.

## Common Pitfalls

### Pitfall 1: Counter Reuse in Desugar
**What goes wrong:** Using `freshName env` for the Lambda parameter name produces `%t5` which is an MLIR SSA name, not a valid F# identifier for `Lambda(param, ...)`. Lambda's `param` field is a plain string used as a `Map` key in `env.Vars`.
**How to avoid:** Gensym using `env.Counter` but produce a plain-string name (e.g., `__comp_0`) that is a valid identifier and guaranteed not to clash with user code.
**Warning signs:** `Var(__comp_0)` lookup fails because `env.Vars` was populated with the wrong key name.

### Pitfall 2: freeVars Missing Modulo/Char/Pipe Cases
**What goes wrong:** `freeVars` in Elaboration.fs has a catch-all `| _ -> Set.empty` at line 109. Modulo and Char fall through correctly (Modulo has two subexpressions so it *should* recurse, but currently falls to the catch-all). This means closures that capture variables used inside a `Modulo` expression may not capture them correctly.
**How to avoid:** Add explicit cases for `Modulo` (recurse both operands) and `PipeRight`/`ComposeRight`/`ComposeLeft` (recurse both operands) to `freeVars` before the catch-all.
**Warning signs:** Runtime failure "closure capture 'x' not found in outer scope" when modulo appears inside a lambda.

### Pitfall 3: Char Pattern Matching
**What goes wrong:** Phase 12 only adds `Char` *expressions*. `ConstPat(CharConst c, _)` patterns in match expressions are separate (they live in `testPattern`). Do not attempt to add char pattern support in this phase unless it's in scope.
**How to avoid:** Only add `Char(c, _)` to `elaborateExpr`. Leave `testPattern` alone.

## Code Examples

### Complete Modulo E2E (expected MLIR output for `10 % 3`)

```mlir
%t0 = arith.constant 10 : i64
%t1 = arith.constant 3 : i64
%t2 = arith.remsi %t0, %t1 : i64
```
Exit code: 1

### Complete Char E2E (expected MLIR for `'A'`)

```mlir
%t0 = arith.constant 65 : i64
```
Exit code: 65 (program returns the char value)

### PipeRight trace: `5 |> fun x -> x + 1`

Desugars to `App(Lambda("x", Add(Var "x", Number 1)), Number 5)`.
The elaborator sees an App of an anonymous Lambda applied to 5.
This is the **inline lambda call** path — not a named closure.
Current `App` dispatch in Elaboration.fs line 683 only handles `Var(name, _)` as the function, not a raw `Lambda`.

**Critical finding:** The existing `App` elaborator at line 718 has:
```fsharp
| _ ->
    failwithf "Elaboration: unsupported App (only named function application supported in Phase 5)"
```
So `App(Lambda(...), arg)` will fail. To fix PipeRight (and inline lambdas generally), we need to either:
- Add a case `| Lambda(param, body, _)` inside the `App` handler that inlines the lambda as a `Let` binding, OR
- Desugar `PipeRight(x, Lambda(p, body, s1), s2)` at the PipeRight case to `Let(p, x, body, s2)` directly.

The simplest approach: desugar PipeRight into `Let(param, left, body, span)` when the right side is a Lambda, and `App(right, left, span)` when right is a named function (Var). For general expressions as right, we need the App-of-Lambda fix.

**Recommendation:** Add `App(Lambda(...), arg, _)` case to the App handler that desugars inline:
```fsharp
| Lambda(param, body, _) ->
    // Inline lambda application: (fun x -> body) arg  ≡  let x = arg in body
    let (argVal, argOps) = elaborateExpr env argExpr
    let env' = { env with Vars = Map.add param argVal env.Vars }
    let (bodyVal, bodyOps) = elaborateExpr env' body
    (bodyVal, argOps @ bodyOps)
```

This fixes both PipeRight-with-lambda and any other inline lambda application.

## State of the Art

| Old Approach | Current Approach | Impact |
|--------------|------------------|--------|
| N/A — operators just missing | Add ArithRemSIOp + desugar | All five operators become first-class |

## Open Questions

1. **Compose with closures vs direct functions**
   - What we know: ComposeRight/Left desugar to `Lambda(param, App(g, App(f, Var param)))`. If `f` and `g` are closures (Ptr-typed), the inner `App` will use the indirect call path correctly.
   - What's unclear: If `f` or `g` is a multi-argument curried function (returns another closure), does the desugar produce the right arity?
   - Recommendation: Test `(inc >> dbl) 3` where `inc` and `dbl` are single-param LetRec functions. Success criteria confirm this is sufficient.

## Sources

### Primary (HIGH confidence)
- MLIR arith dialect docs: `arith.remsi` is the canonical signed remainder op (same format as `arith.divsi`)
- LangThree Ast.fs (read directly): confirmed all 5 AST node shapes
- Elaboration.fs (read directly): confirmed App dispatch limitation at line 718

### Secondary (MEDIUM confidence)
- F# language spec: `int c` on a `char` yields the Unicode BMP code point as `int`

## Metadata

**Confidence breakdown:**
- Modulo (MlirOp): HIGH — identical pattern to ArithDivSIOp
- Char (constant): HIGH — trivial int64 cast
- PipeRight (desugar): HIGH — one recursive call after fixing App(Lambda) path
- ComposeRight/Left (desugar): HIGH — lambda gensym approach is clean
- freeVars fix: HIGH — catch-all is documented in code, fix is mechanical
- App(Lambda) inline fix: HIGH — simple let-binding inlining

**Research date:** 2026-03-26
**Valid until:** 2026-06-26 (stable compiler infrastructure)
