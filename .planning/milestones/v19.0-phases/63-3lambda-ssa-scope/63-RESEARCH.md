# Phase 63: 3-Lambda SSA Scope Fix - Research

**Researched:** 2026-04-01
**Domain:** FunLang compiler — Elaboration.fs closure compilation, SSA scoping
**Confidence:** HIGH

## Summary

This phase fixes an SSA scope violation in the 2-lambda pattern compiler
(`Elaboration.fs` lines ~713-881). The pattern handles
`let f (a) (b) = body` by compiling two functions: a closure body
(`closure_fn_N`) and a closure-maker (`@f`). When `f` is 3-argument
(`let f (a) (b) (c) = body`), the pattern still fires because F# pattern
matching sees `Lambda(a, Lambda(b, Lambda(c, body)))`. The "inner body" is
`Lambda(c, body)` — a third lambda.

During step 3 of the 2-lambda compiler (lines ~775-796), `elaborateExpr` is
called on `innerBody` which is `Lambda(c, body)`. This hits the standalone
Lambda path (line ~3131). The standalone Lambda path uses `env.Vars` from
`innerEnvWithCaptures` to populate the inline closure struct. It generates new
SSA temporaries (`%t121`, etc.) in `innerEnvWithCaptures` (whose `Counter` ref
is shared). These SSA values belong to the `closure_fn_N` function body.

The problem is in step 4 (maker construction, lines ~818-838). The maker
function (`@f`) captures variables by looking them up in `env.Vars` (the
**outer** env, before `innerEnvWithCaptures` was created). However, the
`captures` list is computed at line 719 using `freeVars` on `innerBody`. For a
3-lambda, `innerBody = Lambda(c, actualBody)`. The free variables of that
lambda include `a`, `b` (and any other outer vars), which ARE in `env.Vars`
correctly. So `env.Vars` lookup in the maker is correct for these.

**The actual bug is not where the issue report says.** The issue report claims
`%t121` ends up in the maker's body. Let's trace exactly how that could happen.

### Exact Execution Trace

**Line 718-722: Capture computation**

```fsharp
let captures =
    freeVars (Set.singleton innerParam) innerBody   // innerParam = "b"
    |> Set.filter (fun name -> Map.containsKey name env.Vars || name = outerParam)
    |> Set.toList
    |> List.sort
```

For `let buildCharClass (nfa) (ranges) (negated) = body`:
- `outerParam` = `"nfa"`
- `innerParam` = `"ranges"`
- `innerBody` = `Lambda("negated", actualBody)`

`freeVars ({"ranges"}) (Lambda("negated", actualBody))` computes free vars of
`actualBody` minus `{"ranges", "negated"}`. This gives the actual free vars
of the body, e.g. `{"nfa"}`. The filter keeps names in `env.Vars` or equal to
`outerParam`. `"nfa"` equals `outerParam`, so `captures = ["nfa"]`.

This is correct. `env.Vars` at this point is the outer module-level env, which
has no SSA temporaries.

**Lines 756-773: Closure-fn body build**

`innerEnvWithCaptures` is constructed with:
- `Vars = {"ranges" -> %arg1, "nfa" -> %t1}` (capture loaded at %t1)
- `Counter = ref 2` (past the GEP/load for 1 capture)
- `Blocks = ref []` (fresh, shared mutable ref)

**Line 776: Elaborate inner body**

```fsharp
let (bodyVal, bodyEntryOps) = elaborateExpr innerEnvWithCaptures innerBody
```

`innerBody = Lambda("negated", actualBody)` hits the standalone Lambda path
(line 3131). In that path:

1. `captures` for this Lambda = free vars of `actualBody` minus `{"negated"}`
   filtered by `env.Vars` — here `env` IS `innerEnvWithCaptures`, so it
   includes `{"ranges" -> %arg1, "nfa" -> %t1}`. Both may appear free.

2. A new `closure_fn_M` is built with its own isolated env and counter.

3. The standalone Lambda path returns `(envPtrVal, allocOps @ captureStoreOps)`
   where `envPtrVal` is a new SSA name from `freshName innerEnvWithCaptures`
   (i.e., `%t2`, `%t3`, etc. advancing `innerEnvWithCaptures.Counter`).

4. `captureStoreOps` stores `capVal = Map.find capName innerEnvWithCaptures.Vars`
   into the inline closure struct. These `capVal`s are `%arg1` (for "ranges")
   and `%t1` (for "nfa"). Both are valid SSA names within `closure_fn_N`.

So the Lambda inline path generates new SSA temporaries (e.g., `%t2`, `%t3`,
`%t4` for the malloc call, fnptr, etc.) and the `bodyEntryOps` contain ops that
USE `%arg1` and `%t1`. This is all inside `closure_fn_N`'s body — correct.

**Lines 818-838: Maker capture stores**

```fsharp
let captureStoreOps =
    captures |> List.mapi (fun i capName ->
        ...
        let captureVal =
            if capName = outerParam then makerArg0          // "nfa" -> %arg0 (I64)
            else
                match Map.tryFind capName env.Vars with     // env = OUTER env
                | Some v -> v
                | None -> failWithSpan ...
```

The maker looks up capture values in the **outer** `env.Vars`. For a simple
3-argument function where `captures = ["nfa"]` and `"nfa" = outerParam`, the
value is `makerArg0` (`%arg0` of the maker). This is correct.

### Where the SSA Leak Actually Occurs

The issue report says `%t121` appears in the maker's body. This would happen
in a more complex case where the captures list includes variables that are in
`env.Vars` but whose SSA values in `env.Vars` were themselves generated by
prior elaboration in the OUTER scope — and somehow those values are wrong for
the maker's scope.

**The true trigger:** The 2-lambda pattern uses `env.Vars` at line 829 to look
up captures. If a capture's value in `env.Vars` is a high-numbered SSA temp
(like `%t121`), it means that variable was bound in the outer function scope
BEFORE this let-binding. The maker function only has `%arg0` and `%arg1`. It
cannot reference `%t121` from the calling context.

This would happen if:
- A previous `let` binding in the outer scope bound a variable to `%t121`
- That variable appears free in `innerBody`
- The captures computation includes it
- The maker tries to store `%t121` into the closure slot

**Concrete example:**

```funlang
let x = someComplexExpr  // x bound to %t121 in outer scope
let buildCharClass (nfa) (ranges) (negated) =
    body using x  // x is free in innerBody
```

The `captures` computation at line 719 calls `freeVars ({"ranges"}) innerBody`.
For `innerBody = Lambda("negated", body using x)`, `freeVars` returns `{x, nfa}`.
The filter `Map.containsKey name env.Vars || name = outerParam` keeps `x` (it's
in `env.Vars`) and `nfa` (equals `outerParam`).

Now the maker (line 829) tries to store `x`'s value. `Map.tryFind "x" env.Vars`
returns `%t121` (an SSA value from the outer function). The maker emits
`llvm.store %t121, %slot` — but `%t121` is defined in the OUTER function body,
not in the maker function body. **SSA scope violation.**

### Root Cause Summary

The 2-lambda maker pattern works correctly when all captures are either:
1. The `outerParam` itself (`%arg0` of the maker)
2. Module-level globals or KnownFuncs (not in `env.Vars`)

It **breaks** when a capture is a locally-bound variable from an enclosing
`let` expression, whose SSA value was generated by the OUTER function's
elaboration. The maker function is a separate `func.func` — it can only
reference its own arguments `%arg0` (the outer param) and `%arg1` (the env
ptr). It cannot reference SSA temporaries from the surrounding function.

For a 3-argument function like `buildCharClass`, if the function body uses
variables bound in the outer module-level let chain that aren't module globals,
those variables appear as SSA temporaries in `env.Vars`, and the maker
incorrectly tries to use them.

### Additional dimension: the 3rd lambda triggers a nested problem

When `innerBody = Lambda("negated", actualBody)`, the `elaborateExpr
innerEnvWithCaptures innerBody` call (line 776) hits the STANDALONE Lambda
path (line 3131). That path does:

```fsharp
let captures =
    allFree
    |> Set.filter (fun name -> Map.containsKey name env.Vars)
    |> Set.toList
    |> List.sort
```

Where `env` here is `innerEnvWithCaptures` (NOT the outer env). The captures
for the innermost lambda include `{nfa, ranges}` which are in
`innerEnvWithCaptures.Vars` as `{%t1, %arg1}`. The inline closure struct is
built, and the ops ARE placed in `closure_fn_N`'s body ops. This is correct.

BUT: the `closure_fn_M` (for the 3rd lambda) is built by the standalone Lambda
path using `env.Vars = innerEnvWithCaptures.Vars`, which has the capture-load
values (`%t1` for nfa, `%arg1` for ranges). These ARE valid within
`closure_fn_N`.

So the 3-lambda case has two separate structural issues:
1. Maker body referencing outer-scope SSA values (the fundamental issue above)
2. The "closure_fn_N" for the middle lambda isn't actually needed as a closure
   at all — it just wraps the 3rd lambda inline closure, adding an unnecessary
   level of indirection.

## Standard Stack

This is a pure compiler bug fix — no new libraries needed.

| Component | Location | Purpose |
|-----------|----------|---------|
| Elaboration.fs | `src/FunLangCompiler.Compiler/Elaboration.fs` | Core elaboration pass |
| 2-lambda pattern | Lines ~713-881 | Compiles 2-arg curried functions |
| standalone Lambda | Lines ~3131-3224 | Compiles bare lambda as closure value |
| freeVars | Lines ~141-200 | Computes free variable sets |

## Architecture Patterns

### The 2-Lambda Pattern (current)

```
let f (a) (b) = body
  =>
closure_fn_N: llvm.func(%arg0: ptr, %arg1: ptr) -> i64
  // loads captures from env, elaborates body
@f: func.func(%arg0: i64, %arg1: ptr) -> ptr
  // stores fn ptr + captures into caller-allocated env
```

For 3-arg functions: `innerBody = Lambda(c, actualBody)` and when
`elaborateExpr innerEnvWithCaptures innerBody` runs, the standalone Lambda path
fires, generating an inline closure struct in `closure_fn_N`'s body. This is
the right-shaped output but the maker can't see outer SSA values.

### What the Maker Can Reference

The maker function `@f` body can ONLY reference:
- `%arg0` — the outer parameter (I64)
- `%arg1` — the caller-allocated env pointer (Ptr)
- Compile-time constants (via `addressof`)
- SSA values it generates itself

It CANNOT reference any SSA value from the calling context.

### Fix Pattern: Thread captures as extra maker arguments OR store via the outer closure

**Option A: Treat non-outerParam env captures as "grandparent captures"**

If a capture is not `outerParam` and is an SSA value in `env.Vars`, the maker
cannot store it directly. Instead:
- Those values must be stored into a GRANDPARENT closure env before calling
  the maker
- The maker receives them through a different channel

This requires restructuring the calling convention.

**Option B: Encode the 3-lambda as nested 2-lambda patterns**

Recognize `Lambda(a, Lambda(b, Lambda(c, body)))` as a 3-lambda and compile it
differently: instead of the 2-lambda pattern firing on the outer two, handle it
as `Lambda(a, closure-of-b-c-body)`.

**Option C: Restrict the 2-lambda pattern to only fire when all captures are outerParam or KnownFuncs**

Add a guard to the pattern match:
```fsharp
| Let (name, ...) when
    freeVars (Set.singleton innerParam) innerBody
    |> Set.filter (fun n -> Map.containsKey n env.Vars && n <> outerParam)
    |> Set.isEmpty ->
```

When the guard fails, fall through to a different path (e.g., compile as a
nested lambda chain).

**Option D: Compile 3-lambda as three nested closures**

The 3-lambda `let f (a) (b) (c) = body` compiles to:
- `@f` is a KnownFunc that returns a closure for `(b) (c) = body` given `a`
- Each application peels one layer

This is the most general but requires the outer call sites to be updated.

### Recommended Fix: Option C + Option B (Detect and redirect 3+ lambda)

The cleanest fix adds a new pattern BEFORE the 2-lambda pattern that explicitly
handles `Lambda(a, Lambda(b, Lambda(c, body)))`:

```fsharp
| Let (name, StripAnnot (Lambda (p1, StripAnnot (Lambda (p2, StripAnnot (Lambda (p3, body3, _)), _)), _)), inExpr, letSpan) ->
    // Compile as @name: func.func(p1: i64, ...) that builds a 2-lambda closure for (p2)(p3)->body3
```

But this only handles exactly 3 arguments. A general solution needs to handle
N arguments.

**Simplest correct fix for the immediate bug:**

The bug in the maker (lines 818-838) is that it looks up capture values in
`env.Vars`. For captures that are NOT `outerParam`, their values in `env.Vars`
may be SSA temporaries from the calling scope.

The fix depends on what those captures ARE:
- If they are module-level KnownFuncs: they should not appear in `env.Vars` at
  all (they're in `env.KnownFuncs`)
- If they are let-bound local variables: the maker needs to receive them

For a 3-argument curried function at module top level, the `env.Vars` at the
point of compilation is typically EMPTY (module-level bindings go into
KnownFuncs). The bug therefore manifests specifically when the 3-arg function:
1. Is defined inside another function (nested), OR
2. References variables defined in an outer let chain that ended up in env.Vars

### Concrete fix for the reported case (`@buildCharClass`)

If `buildCharClass` is a top-level function and the only captures are
`outerParam = "nfa"`, then `captures = ["nfa"]` and the maker correctly uses
`makerArg0`. There should be no SSA leak for this case alone.

The bug manifests when `captures` contains names whose `env.Vars` entries are
non-trivial SSA values. To investigate the SPECIFIC `%t121` report:

1. Find what variable `%t121` corresponds to in the outer scope
2. Check if that variable appears free in `innerBody`
3. Verify it ends up in `captures` via the filter

The most likely scenario: `innerBody = Lambda("negated", body)` and the
`freeVars` call traverses INTO the Lambda, past the `Lambda` case which adds
`"negated"` to bound vars. Any variable free in `body` that's also in
`env.Vars` becomes a capture. If `env.Vars` contains SSA temporaries from
previous elaboration steps, they get pulled into the maker.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead |
|---------|-------------|-------------|
| SSA value scoping | A new SSA renaming pass | Fix capture lookup at construction time |
| Multi-lambda patterns | A general N-ary lambda flattener | Handle each arity explicitly or add guard |

## Common Pitfalls

### Pitfall 1: Fixing only the 3-lambda case

The guard/redirect approach must handle N-lambda (4-arg, 5-arg, etc.) or the
same bug will recur. `freeVars` recurses into nested `Lambda` nodes, so the
`captures` computation for a 3-lambda middle tier can pull in outer SSA values
just as it does for 3-arg functions.

**How to avoid:** Either add a guard that prevents the 2-lambda pattern from
firing when captures have outer-scope SSA values, OR add a recursive
"desugar to KnownFunc chain" pass before elaboration.

### Pitfall 2: Breaking the existing 2-lambda path

The 2-lambda path is battle-tested for 2-argument functions. Any fix must not
regress those. The fix should be additive: either a new pattern match arm added
BEFORE the 2-lambda arm, or a guard on the existing arm with fallback.

### Pitfall 3: The `innerBody` Lambda path also needs fixing

When `innerBody = Lambda(c, body)`, `elaborateExpr innerEnvWithCaptures innerBody`
runs and the standalone Lambda path (line 3131) captures variables from
`innerEnvWithCaptures.Vars`. These captures are `%arg1` (ranges) and `%t1`
(nfa). These are valid WITHIN `closure_fn_N`'s body, so this part is actually
correct. Do not "fix" it.

### Pitfall 4: The 2-lambda pattern guard condition

```fsharp
| Let (name, StripAnnot (Lambda (outerParam, StripAnnot (Lambda (innerParam, innerBody, _)), _)), inExpr, letSpan) ->
```

This pattern matches both 2-arg AND 3+-arg functions. Adding an explicit
3-lambda pattern arm BEFORE this will correctly shadow the 3-arg case.

## Code Examples

### Current maker capture lookup (the bug site)

```fsharp
// Lines 824-831 of Elaboration.fs
let captureVal =
    if capName = outerParam then makerArg0          // %arg0 of maker — correct
    else
        match Map.tryFind capName env.Vars with     // BUG: may return outer SSA temp
        | Some v -> v
        | None -> failWithSpan letSpan "..."
```

### Guard approach to prevent the 2-lambda pattern from matching 3+ lambdas

Add a `when` guard at line 713:

```fsharp
| Let (name, StripAnnot (Lambda (outerParam, StripAnnot (Lambda (innerParam, innerBody, _)), _)), inExpr, letSpan)
    when (match stripAnnot innerBody with Lambda _ -> false | _ -> true) ->
```

This makes the 2-lambda pattern only fire when `innerBody` is NOT itself a
lambda. 3+ arg functions fall through to the single-lambda Let pattern or the
general Let pattern.

But note: the single-arg Let-Lambda pattern (line 893) also has a guard that
prevents it from matching when `body` is a Lambda. So a 3-arg function
`let f (a) (b) (c) = body` would fall through ALL named-lambda patterns and
hit the GENERAL `Let` path (line 953), which would try to elaborate the entire
`Lambda(a, Lambda(b, Lambda(c, body)))` as a Var binding — which would hit the
standalone Lambda path and produce an inline closure, treating `@f` as a
module-level closure value rather than a KnownFunc.

This means the fix requires ALSO adding a proper 3-lambda compilation path.

### Proper 3-lambda fix: Add explicit 3-lambda pattern BEFORE the 2-lambda pattern

```fsharp
// New pattern: let f (a) (b) (c) = body
// Compiles as: @f is a func.func(a: i64, envOut: ptr) -> ptr
//   that stores `a` + any outer captures into envOut
//   and sets fn ptr to @f_mid
// @f_mid is a closure_fn that takes (env, b) and returns a closure for (c -> body)
| Let (name,
       StripAnnot (Lambda (p1,
         StripAnnot (Lambda (p2,
           StripAnnot (Lambda (p3, innerBody3, _)), _)), _)),
       inExpr, letSpan) ->
    // Compile the 3-lambda chain correctly
    ...
```

## State of the Art

| Approach | When Used | Status |
|----------|-----------|--------|
| 2-lambda special case | 2-arg curried top-level functions | Working for 2-arg |
| standalone Lambda inline closure | Bare lambda values, 3rd lambda in 3-arg functions | Works within closures, leaks in maker |
| General N-ary compilation | Not yet implemented | Needed |

## Open Questions

1. **Exact variables triggering `%t121` in `@buildCharClass`**
   - What we know: `buildCharClass` is 3-arg, the maker stores `%t121`
   - What's unclear: what AST variable corresponds to `%t121` in the outer scope
   - Recommendation: Add debug logging to print `env.Vars` at the maker
     construction point to identify which capture is misbehaving

2. **Are there 4-arg or 5-arg curried functions in the codebase?**
   - What we know: At least one 3-arg function exists
   - What's unclear: Whether a general fix is needed vs. just 3-arg
   - Recommendation: `grep -n "let .* (.*).*(.*).*(.*).*(.*).*=" *.fl` to find them

3. **Does the 2-lambda pattern fire on 3-arg functions at module top level?**
   - What we know: `freeVars` recurses into the 3rd Lambda and finds free vars
   - What's unclear: Whether `env.Vars` is empty at module top level (in which
     case `captures = ["nfa"]` only, and the lookup is always `outerParam`)
   - Recommendation: Trace the ACTUAL captures list for `buildCharClass`

## Sources

### Primary (HIGH confidence)
- Direct code reading of `Elaboration.fs` lines 141-200, 713-881, 3131-3224
- Exact trace of execution path for 3-argument curried functions

### Secondary (MEDIUM confidence)
- Issue report analysis in the objective description

## Metadata

**Confidence breakdown:**
- Bug location: HIGH — code reading confirms the maker uses `env.Vars` for captures
- Execution order: HIGH — F# is eager/sequential, order is unambiguous in the code
- Root cause for `%t121`: MEDIUM — depends on what's in `env.Vars` at that point
- Fix approach: HIGH for guard+3-lambda pattern, MEDIUM for exact implementation

**Research date:** 2026-04-01
**Valid until:** Until Elaboration.fs is modified
