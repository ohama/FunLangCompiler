# Feature Landscape

**Domain:** Functional language compiler backend (ML-style, MLIR → LLVM → native)
**Researched:** 2026-03-26
**Confidence:** HIGH

---

## Scope Anchor

This file addresses the v1 compiler for LangThree: integers, booleans, arithmetic, comparisons, logical operators, let bindings, let rec (recursion), lambda, function application, if-else. Strings, tuples, lists, pattern matching, and ADTs are explicitly deferred to v2+.

---

## Table Stakes

Features a v1 functional language compiler must have to be minimally useful. Missing any of these = compiler is incomplete for its stated scope.

| Feature | Why Expected | Complexity | Runtime Requirement |
|---------|--------------|------------|---------------------|
| Integer literals | Foundation of all computation | Low | Stack (i64 SSA value) |
| Boolean literals | Needed for conditionals | Low | Stack (i1 SSA value) |
| Arithmetic (+, -, *, /) | Core operations on integers | Low | `arith` dialect ops |
| Unary negation | Natural integer counterpart | Low | `arith.subi 0, x` |
| Comparison ops (=, <>, <, >, <=, >=) | Needed for non-trivial control flow | Low | `arith.cmpi` variants |
| Logical ops (&& and \|\|) with short-circuit | Conditional evaluation semantics | Medium | Branch-based `cf` or `scf` |
| If-else expression | Basic control flow | Low | `scf.if` → `cf` blocks |
| Let binding (non-recursive) | Scoped variable introduction | Low | SSA value naming, no heap |
| Variable reference | Core of any expression language | Low | SSA value lookup |
| Function definition (lambda) | First-class functions | High | Closure struct on heap |
| Function application | Calling functions | High | Indirect call through closure |
| Let rec (recursive functions) | Enables recursion | High | Self-reference in closure env |
| Module-level let declarations | Top-level program structure | Medium | Top-level MLIR func ops |
| Main entry point / program output | Compiler output is executable | Medium | `printf` via LLVM libc call or exit code |
| E2E test harness (compile → run → verify) | Proves the compiler works | Medium | Shell or F# test runner |

**Threshold note:** A compiler that can handle let rec factorial and let rec fib, producing correct output, is the v1 success criterion. All table stakes above are prerequisites for this.

---

## Differentiators

Features that go beyond minimum viability. Not expected by users at v1, but meaningfully improve the compiler.

| Feature | Value Proposition | Complexity | Notes |
|---------|-------------------|------------|-------|
| Known-function optimization | `let rec f x = ...` with no captured free vars can be compiled as a direct MLIR `func.func` call, eliminating closure allocation entirely | Medium | MinCaml approach: attempt direct-call first, fall back to closure if free vars detected. Most recursive functions qualify. |
| Tail call recognition for let rec | Self-tail calls in `let rec` bodies become loops instead of stack frames, enabling unbounded recursion | High | LLVM `musttail` attribute or `scf.while` transformation. Critical for language credibility. |
| Reuse LangThree type info | Type-directed codegen: use inferred `TInt`/`TBool`/`TArrow` to select MLIR types statically, no runtime type tags needed | Medium | The typechecker already provides `Type` for each node; codegen can consume this directly. Eliminates boxing for int/bool. |
| Meaningful error messages for codegen failures | Propagate `Span` from AST into MLIR source location attributes | Low | AST already carries span info on every node. Free improvement with small implementation cost. |
| CLI interface (`langbackend <file.lt>`) | Makes the compiler usable as a standalone tool | Low | Already in project scope (`FunLang.Compiler.Cli`). |

---

## Anti-Features

Features to explicitly NOT build in v1. These are common mistakes or tempting scope creep.

| Anti-Feature | Why Avoid | What to Do Instead |
|--------------|-----------|-------------------|
| Garbage collector (GC) | Full GC requires runtime, object headers, root sets. Massively increases scope. | Use heap allocation only for closure environments; rely on program exit for deallocation. Closures are short-lived in v1 test programs. |
| String support | Strings require heap allocation, null termination semantics, and GC interaction. The interpreter already handles strings. | Defer to v2. Stub out `String` AST node as a codegen error. |
| Tuple / list support | Both require heap boxing, layout decisions, and (for lists) recursive struct types in LLVM. | Defer to v2. Stub out `Tuple`/`List`/`Cons` as codegen errors. |
| Pattern matching compilation | Depends on tuples and lists; without them, only trivial const patterns possible. | Defer to v2. The interpreter handles this already. |
| REPL integration | The interpreter REPL already exists in LangThree. Incremental compilation adds JIT complexity. | Not needed; the compiler handles file → binary. |
| Polymorphic generics at codegen | Type variables in HM inference are resolved before codegen; monomorphization or boxing required. | v1 only handles monomorphic programs (int/bool). Type variables that remain unresolved at codegen = compile error. |
| Register allocation tuning | LLVM already performs register allocation. Manual allocation is wasted effort. | Emit valid MLIR → LLVM IR and let LLVM optimize. |
| Custom calling convention | Non-standard calling conventions break interop and debugging. | Use standard C calling convention (cdecl / System V AMD64 ABI). |
| ADT / variant types | No ADT in LangThree v1. | Not applicable for v1. |
| Exception handling / error propagation at runtime | Division by zero, etc. produce undefined behavior at LLVM level; runtime error handling needs a runtime. | In v1, these produce undefined behavior or abort. Document this limitation clearly. |
| Incremental compilation | Requires module system, separate compilation, caching infrastructure. | Whole-program compilation only in v1. |

---

## Feature Details: Implementation Concerns

### Integer Arithmetic
- MLIR type: `i64` (matches typical 64-bit platform int)
- Ops: `arith.addi`, `arith.subi`, `arith.muli`, `arith.divsi` (signed division)
- Unary negate: `arith.subi(constant 0, x)`
- Division by zero: undefined behavior in v1 (LLVM sdiv semantics)
- No overflow checking needed in v1

### Boolean Literals and Comparisons
- MLIR type: `i1` (boolean as 1-bit integer, standard in MLIR/LLVM)
- Integer comparisons: `arith.cmpi` with predicates: `eq`, `ne`, `slt`, `sgt`, `sle`, `sge`
- Boolean equality (`=` on bools): `arith.cmpi eq, i1, i1`
- Boolean inequality: `arith.cmpi ne`

### Logical Operators (&&, ||)
- Short-circuit evaluation requires conditional branches, not just `arith.andi`/`arith.ori`
- Implementation: `scf.if` with result type or explicit `cf` basic blocks
- Recommended: lower to `cf` blocks directly; easier to control short-circuit semantics

### If-Else Expression
- LangThree if-else is an expression (both branches must produce the same type)
- MLIR: `scf.if` produces a result value when both regions yield a value
- Must handle nested if-else correctly (each becomes its own `scf.if`)
- `scf.yield` terminates each branch and provides the result

### Let Binding
- Non-recursive: pure SSA. Bind expression result to a named SSA value; substitute all occurrences of the variable in the body with this SSA value
- No heap allocation needed for simple let bindings
- Environment model: recursive descent through the AST with an `env: Map<string, MlirValue>` passed along

### Let Rec (Recursive Functions)
- The key challenge: the function needs to reference itself before it is fully defined
- Recommended approach (MinCaml-style known-function optimization):
  1. Attempt to compile `let rec f x = body` as a top-level `func.func` with direct calls
  2. If `body` contains no free variables beyond `x` and `f` itself, it qualifies as a "known function" with no closure
  3. If free variables are present, fall back to closure representation
- For v1 test programs (factorial, fibonacci), the known-function path should handle all cases
- Stack-overflow risk: deep recursion without TCO will exhaust the stack. LLVM will perform TCE for self-tail-calls if the `musttail` attribute is set; this is a v1 differentiator worth implementing for `let rec`

### Lambda and Function Application
- The hardest feature in v1 due to closure representation requirements
- Closure representation (flat closure, recommended):
  - `{ fn_ptr: ptr, env_ptr: ptr }` — two-word struct
  - `fn_ptr` points to a regular LLVM function `(env_ptr, arg) -> result`
  - `env_ptr` points to a heap-allocated struct containing captured free variables
  - Free variable analysis: compute `freeVars(body) - {param}` at each lambda
  - Heap allocation: `llvm.call @malloc(size)` or `llvm.alloca` for short-lived closures
- Calling a closure (App node):
  1. Load `fn_ptr` from closure struct
  2. Load `env_ptr` from closure struct
  3. Call `fn_ptr(env_ptr, evaluated_arg)`
- Runtime memory requirement: heap for env structs (malloc/free or arena)
- Stack vs heap: closure environment is heap-allocated (variable size at compile time for general lambdas). Known functions (no free vars) need no heap.

### Curried Functions (Multi-argument via chained lambda)
- LangThree functions are single-argument (Lambda has one param)
- Multi-argument functions are curried: `fun x -> fun y -> x + y`
- Each application peels one argument: `f a b` = `(f a) b`
- No special handling needed beyond single-argument lambda; currying falls out naturally
- Performance concern: each partial application allocates a new closure. In v1, this is acceptable.

### Module-Level Declarations
- `Decl` = `LetDecl of name * body * Span`
- Each top-level let generates a `func.func` or a global in MLIR
- For function-valued top-level lets: generate a named function
- For non-function top-level lets: can be compiled into `func.func @__init()` or evaluated at global init time

---

## Feature Dependencies

```
Integer literals
    └─> Arithmetic ops (+, -, *, /)
            └─> Comparison ops (<, >, =, etc.)
                    └─> If-else expression (condition must be boolean)
                            └─> Let binding (name the result of any expression)
                                    └─> Variable reference

Boolean literals ─────────────────> If-else expression
                                    Logical ops (&& ||) ─> If-else (for short-circuit)

Let binding
    └─> Let rec (recursive variant; needs closure or known-function mechanism)
            └─> Lambda (general closures; let rec is a constrained lambda)
                    └─> Function application (App node; calls via closure or direct)
                            └─> Currying (falls out from single-arg lambda + App)

Lambda ──────────────────────────> Closure representation (flat closure struct)
    └─> Free variable analysis (prerequisite for closure env layout)
            └─> Heap allocation (malloc for env struct)
```

**Critical path for v1:** Integer literals → Arithmetic → Comparisons → If-else → Let binding → Variable reference → Let rec (known function) → Lambda (closure) → App

**Unblocking order:**
1. Integers + Arithmetic + Comparisons: pure SSA, no memory model
2. Boolean + If-else: adds branching
3. Let binding + Var: scoping in SSA
4. Let rec (known function only): adds recursion without closures
5. Lambda + App (full closure): adds first-class functions
6. Module-level declarations: top-level plumbing

---

## MVP Recommendation

### Phase 1 — Scalar Core (No Functions)
Build and test: integers, booleans, arithmetic, comparisons, logical ops, if-else, let, var.

Produces: programs that compute numeric results from pure expressions. No heap allocation. All values are SSA `i64` or `i1`.

Success criterion: compile and run `let x = (3 + 4) * 2 in if x > 10 then 1 else 0` → correct exit code.

### Phase 2 — Non-Recursive Functions (Known Functions)
Build and test: lambda, application, let binding of functions. Assume no free variables (known functions only). Compile as top-level `func.func` with direct calls.

Success criterion: compile and run `let add = fun x -> fun y -> x + y in add 3 4` → `7`.

### Phase 3 — Recursive Functions (Let Rec, Known)
Build and test: `let rec` where the function is a known function (no captured free vars beyond the recursion variable).

Success criterion: compile and run `let rec fact n = if n <= 1 then 1 else n * (fact (n - 1)) in fact 10` → `3628800`.

### Phase 4 — Full Closures (Lambdas with Free Variables)
Build and test: lambdas that capture variables from enclosing scope. Implement flat closure representation with heap-allocated environment.

Success criterion: compile and run a higher-order function like `let add_n n = fun x -> x + n in let add5 = add_n 5 in add5 3` → `8`.

### Defer to v2+
- Strings: heap, null termination, GC
- Tuples: struct layout, boxing
- Lists: recursive types, cons cells
- Pattern matching: depends on tuples/lists
- ADTs: not in LangThree v1
- GC: requires runtime
- Tail call elimination: valuable but can be added incrementally after basic recursion works

---

## Sources

- LangThree AST: `/home/shoh/vibe-coding/LangThree/src/LangThree/Ast.fs` (direct inspection — HIGH confidence)
- LangThree Eval: `/home/shoh/vibe-coding/LangThree/src/LangThree/Eval.fs` (direct inspection — HIGH confidence)
- LangThree Type: `/home/shoh/vibe-coding/LangThree/src/LangThree/Type.fs` (direct inspection — HIGH confidence)
- MinCaml compiler paper: known-function optimization, let rec handling (https://esumii.github.io/min-caml/paper.pdf — HIGH confidence)
- Matt Might: closure conversion strategies (https://matt.might.net/articles/closure-conversion/ — HIGH confidence)
- MLIR arith dialect: `arith.cmpi`, integer ops (https://mlir.llvm.org/docs/Dialects/ArithOps/ — HIGH confidence)
- MLIR func dialect: `IsolatedFromAbove`, function definitions (https://mlir.llvm.org/docs/Dialects/Func/ — HIGH confidence)
- MLIR LLVM dialect: struct types, opaque pointers (https://mlir.llvm.org/docs/Dialects/LLVM/ — HIGH confidence)
- Xavier Leroy — Compiling Functional Languages (https://xavierleroy.org/talks/compilation-agay.pdf — HIGH confidence)
- LLVM Tail Recursion Elimination (https://llvm.org/doxygen/TailRecursionElimination_8cpp_source.html — MEDIUM confidence)
