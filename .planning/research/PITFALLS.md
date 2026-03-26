# Domain Pitfalls: Functional Language Compiler via MLIR → LLVM

**Domain:** Functional language compiler (F# host, MLIR C API via P/Invoke, LLVM backend)
**Researched:** 2026-03-26
**Confidence:** HIGH for MLIR C API ownership and lowering pipeline; MEDIUM for closure
representation and AST reuse; LOW for .NET/P/Invoke-specific edge cases

---

## Critical Pitfalls

Mistakes in this category cause silent corruption, crashes at runtime, or complete rewrites
of the codegen layer.

---

### Pitfall C-1: Region Ownership Stolen Silently After `mlirOperationCreate`

**What goes wrong:**
When you call `mlirOperationStateAddOwnedRegions` and then `mlirOperationCreate`, the C API
transfers ownership of the regions to the newly created operation. Any F# handle you still
hold to those `MlirRegion` values becomes a dangling reference. Continuing to call
`mlirRegionDestroy` on them (or passing them to another operation) causes a double-free or
use-after-free that crashes the runtime — often silently, or with a segfault that points
nowhere near the actual mistake.

**Why it happens:**
The MLIR C API communicates ownership through naming conventions (`OwnedX` suffix = caller
transfers ownership to callee), but F# P/Invoke wrappers do not enforce this at the type
level. The handle still looks valid. There is no null-out or invalidation of the pointer.
The documentation warning ("caller owned child objects are transferred in this call and must
not be further used") is easy to miss.

**Consequences:**
- Segfault in `libMLIR.so` with no useful stack frame on the F# side
- Double-free detected by ASAN but not by the default runtime
- Silent memory corruption that produces wrong codegen later

**Prevention:**
- Wrap `MlirRegion` in a discriminated union: `type RegionHandle = Owned of nativeint | Transferred`
- After any `OwnedRegions` call, immediately mark the handle as `Transferred` and assert on
  any subsequent access
- Never call `mlirRegionDestroy` on a region that was passed to an operation state
- Pattern: create region → populate blocks → add to state → DO NOT touch region handle again

**Detection (warning signs):**
- Crash inside `libMLIR.so` with no F# frames
- Valgrind/ASAN reports `invalid read` at an address that was recently freed
- Tests pass in isolation but crash when run sequentially (region handle reuse)

**Phase mapping:** Affects every phase involving operation construction; establish the
ownership wrapper in the first codegen phase and never deviate from it.

---

### Pitfall C-2: `mlirModuleDestroy` Destroys All Child Operations — Context Must Outlive Module

**What goes wrong:**
Calling `mlirContextDestroy` before `mlirModuleDestroy` invalidates all types and
attributes, which are owned by the context, not by the module. The module's operations hold
references to those types. Destroying the context first leaves the module pointing at freed
memory. Conversely, calling `mlirModuleDestroy` after translation to LLVM IR is correct
only if you no longer need the MLIR module — but if you attempt to re-use it (e.g. for
diagnostics or a second pass), the behavior is undefined.

**Why it happens:**
Ownership hierarchy is: `MlirContext` owns types/attributes → `MlirModule` owns operations
→ operations reference types/attributes. The C API does not enforce destruction order.
F# `use` bindings and `IDisposable` patterns tempt you into tearing down objects in
reverse declaration order, which is wrong here.

**Consequences:**
- Crash in `mlirTranslateModuleToLLVMIR` or `mlirPassManagerRun` if context is already gone
- Silent wrong-type in operations if attribute memory is reused

**Prevention:**
- Enforce explicit destruction order: destroy module first, then context, always
- Use a `CompilerSession` record that packages `(context, module)` and has a single
  `dispose` function that runs in the correct order
- Never store `MlirContext` and `MlirModule` in separate F# `use` scopes

**Detection:** Crash inside `mlirPassManager*` functions; "invalid operation" assertion in
debug builds of MLIR.

**Phase mapping:** Establish `CompilerSession` abstraction in Phase 1 (infrastructure).
Never change the destruction order pattern.

---

### Pitfall C-3: `unrealized_conversion_cast` Ops Left in the IR Block Translation to LLVM

**What goes wrong:**
After running dialect conversion passes, some operations are lowered but their result types
do not match what downstream consumers expect. The conversion framework inserts
`builtin.unrealized_conversion_cast` ops as temporary bridges. If your pass pipeline does
not finish converting every dialect, these casts remain in the IR. The final
`mlirTranslateModuleToLLVMIR` call fails because `unrealized_conversion_cast` has no LLVM
IR counterpart.

**Why it happens:**
Every dialect in your IR must be fully lowered to the `llvm` dialect before translation.
A typical mistake: you add a new operation (e.g. `arith.cmpi` introduced automatically by
`convert-scf-to-cf`) but your pass pipeline does not include `convert-arith-to-llvm` because
you did not realize the new op was being generated. The conversion partially succeeds and
leaves orphaned casts.

**Consequences:**
- `mlirTranslateModuleToLLVMIR` returns null/error
- Error message: "failed to legalize operation 'builtin.unrealized_conversion_cast'"
- Can occur after adding one new language feature if that feature generates a dialect op
  you had not accounted for

**Prevention:**
Required pass order for a func/arith/scf/cf → LLVM pipeline:
```
convert-scf-to-cf
convert-cf-to-llvm
convert-arith-to-llvm
convert-func-to-llvm
reconcile-unrealized-casts   ← must be last, cleans up any survivors
```
Rules:
- Always run `reconcile-unrealized-casts` as the last pass before translation
- After adding any new source language feature, dump the MLIR IR (before translation) and
  grep for `unrealized_conversion_cast` — if any remain, a pass is missing
- Add a post-pipeline IR validation step that explicitly checks for remaining casts

**Detection:** Translation failure; running `mlir-opt --mlir-print-ir-after-all` shows
exactly which pass leaves the casts behind.

**Phase mapping:** Critical for Phase covering MLIR→LLVM lowering. Revisit whenever a new
dialect op is introduced.

---

### Pitfall C-4: Wrong Lowering Pass Order Causes Type Mismatch Between Passes

**What goes wrong:**
MLIR runs its verifier between every pass. If you lower `func.func` to `llvm.func` before
lowering `arith` ops, the function body still contains `arith` types (e.g. `i1` from
`arith.cmpi`), which are not legal inside an already-LLVM function. The verifier fires with
a cryptic "op requires one of these types" error, not a "wrong pass order" error.

**Why it happens:**
The temptation is to lower structural ops (func, scf) first because they feel "higher level."
But once `func.func` becomes `llvm.func`, the verifier enforces LLVM-dialect type legality
on all operands. Any `arith` or `scf` ops inside are now illegal mid-pass.

**Concrete bad order:**
```
convert-func-to-llvm        ← structural first — WRONG
convert-scf-to-cf
convert-arith-to-llvm
```

**Correct order:** Lower innermost/arithmetic ops first, structural ops last:
```
convert-scf-to-cf
convert-cf-to-llvm
convert-arith-to-llvm
convert-func-to-llvm        ← structural last — CORRECT
reconcile-unrealized-casts
```

**Consequences:** Verifier error after the first pass; confusing error messages pointing at
the wrong operation.

**Phase mapping:** Establish the canonical pass order in Phase 1 and document it explicitly.
Never reorder passes without re-verifying the full pipeline.

---

### Pitfall C-5: Closure Representation Without a Uniform Calling Convention

**What goes wrong:**
When compiling higher-order functions (lambdas, partial application), you need a runtime
representation that is uniform: every closure must be callable through the same interface
regardless of how many free variables it captured. Without this, call sites must know the
concrete closure type, which is impossible in a polymorphic functional language.

**Why it happens:**
The natural first attempt generates a separate LLVM function for each lambda and passes
captured values as extra arguments. This works for direct calls but breaks when the lambda
is stored in a variable, passed to another function, or returned. At that point you need
function pointers — and LLVM function pointers are typed, so all closures must share one
function pointer type.

**Consequences:**
- Direct-call-only codegen works for trivial cases but fails on first higher-order example
- Retrofitting uniform calling convention after the fact requires rewriting all codegen

**Prevention:**
Choose and commit to one closure representation from day 1:

**Recommended: flat closure struct**
```
struct Closure {
  void* fn_ptr;     // always: (Closure*, arg) -> result
  void* env[N];     // captured free variables, type-erased to i8*
}
```
Every lambda compiles to:
1. A top-level LLVM function with signature `(i8* env, i64 arg) -> i64`
2. A heap-allocated struct containing `fn_ptr` and the captured vars

Call sites always go through the struct's `fn_ptr` with the struct as the first arg.
Partial application builds a new struct that bundles the partially-applied closure + new arg.

**MLIR representation:**
- Use `llvm.struct` to represent the closure type
- Use `llvm.func` with a fixed signature for the function pointer slot
- Use `llvm.getelementptr` + `llvm.load` to extract `fn_ptr` at call sites

**Phase mapping:** Design and test the closure representation before implementing any
lambda feature. Do not add lambda codegen until the calling convention is verified with
a hand-written MLIR test case.

---

### Pitfall C-6: Reusing Interpreter AST That Lacks Codegen Annotations

**What goes wrong:**
An interpreter AST is designed for evaluation: it carries enough information to compute
values. A codegen AST needs additional information: source types (for type-directed
emission), explicit closure boundaries (for lambda lifting), and monomorphic type
annotations at every node. Reusing the interpreter AST directly forces the codegen to
re-derive information the type checker already computed but threw away.

**Specific problems with reusing LangThree's AST directly:**
- `Expr.Lambda` does not record which variables are free — you must recompute this at
  codegen time with a separate pass
- `Expr.App` does not distinguish direct call from closure call — both look the same
- Type information from Hindley-Milner inference is not attached to nodes — you must
  re-infer or thread a type environment through codegen
- `let rec` is syntactically present but whether the recursive call is self-recursive or
  mutually recursive is not explicit

**Consequences:**
- Codegen that re-infers types is slower and can diverge from the type checker
- Missing free-variable annotation causes incorrect environment construction for closures
- Recursive functions compiled without knowing they are recursive miss the forward-declaration
  step required in LLVM

**Prevention:**
- Before codegen, run a separate lowering pass: `Expr -> TypedExpr` or `Expr -> LoweredExpr`
- `TypedExpr` must have: resolved type at every node, free variables set for every lambda,
  call-kind annotation (direct vs closure), let-rec scope boundaries explicit
- Do this as an explicit IR lowering step, not as a side-effect inside codegen
- The type checker output (substitution map) must be threaded into this pass and consumed,
  not recomputed

**Phase mapping:** Build the `TypedExpr` / annotation pass before any MLIR emission. Gate
all codegen phases on this pass being complete.

---

## Moderate Pitfalls

Mistakes that cause delays or significant technical debt, but do not require full rewrites.

---

### Pitfall M-1: Boolean Type Mismatch: `i1` vs `i64` at ABI Boundaries

**What goes wrong:**
MLIR's `arith` dialect uses `i1` for booleans (result of `arith.cmpi`). LLVM function
calling conventions typically pass booleans zero-extended to `i8` or `i64` depending on
the ABI. If your if-else codegen uses `i1` as a branch condition but then tries to return
it from a function that has `i64` return type, you get a type error at lowering time. This
is especially common when an `if` expression is used in tail position.

**Prevention:**
- In your language's type system, represent booleans as `i64` at the LLVM ABI boundary
  (0 = false, 1 = true). Use `i1` only as the operand to `cf.cond_br` or `llvm.cond_br`.
- Add an explicit `zext i1 to i64` op when a boolean flows into a position where an integer
  is expected (return value, struct field, closure environment slot).
- Never expose `i1` in function signatures; always convert at the boundary.

**Phase mapping:** Address in the if-else codegen phase; apply consistently to all comparison
results that flow into general value positions.

---

### Pitfall M-2: MLIR C API Has No Stability Guarantee — libMLIR.so Version Must Be Pinned

**What goes wrong:**
The MLIR C API explicitly states it offers no stability guarantees. Function signatures,
struct layouts, and symbol names change between LLVM major versions. If the `libMLIR.so`
on the build machine and the one loaded at runtime differ in version, P/Invoke calls either
crash immediately (symbol not found) or subtly call wrong functions (if a symbol was renamed
but an old symbol happened to remain due to library resolution order).

**Why it happens:**
System-installed LLVM (e.g. `apt install llvm-18`) and source-built LLVM used for
development may have different minor versions. WSL2 makes this worse because the system
LLVM and the developer's custom LLVM can both appear on `LD_LIBRARY_PATH`.

**Prevention:**
- Pin to a specific LLVM major version at project start and document it (e.g. LLVM 18.x)
- Store the version in `Makefile` or `.env` and check it in CI
- Use `LD_LIBRARY_PATH` explicitly in the run command to point to the pinned `libMLIR.so`
- Add a startup check: call `mlirGetVersion()` (if available in your LLVM build) and assert
  the returned version matches the compile-time expectation
- Never use system-default `libMLIR.so` in CI — always use the pinned build

**Detection:** `DllNotFoundException` or `EntryPointNotFoundException` in F# on startup;
correct symbol but segfault inside the library (signature mismatch).

**Phase mapping:** Resolve in Phase 1 (infrastructure). Lock the version before any
P/Invoke wrapper is written.

---

### Pitfall M-3: Block Arguments Not Passed When Branching — CFG Is Invalid

**What goes wrong:**
MLIR uses block arguments instead of phi nodes. Every branch instruction (`cf.br`,
`cf.cond_br`) must supply arguments for all parameters of the target block. If you
emit a branch to a block without providing the arguments (e.g. when compiling an `if`
expression's merge block), MLIR's verifier will reject the IR immediately.

**Why it happens:**
Coming from LLVM IR where phi nodes are inline in the target block, it is natural to
forget that in MLIR the "phi args" are supplied at the branch site, not at the target.
Also, when generating if-else merges, the merge block needs one argument for the result
value, and both branch arms must supply it — this is easy to forget for the else arm.

**Prevention:**
- For every `cf.cond_br`, always write out both `(true_dest, [true_args])` and
  `(false_dest, [false_args])` explicitly
- Merge blocks for if-else results should have exactly one block argument; emit the
  result value as the argument from both arms
- After emitting any basic block branch, assert that the argument count matches the
  target block's parameter count before calling `mlirOperationCreate`

**Phase mapping:** if-else codegen phase; also affects any control flow ops.

---

### Pitfall M-4: `let rec` Compiled as Ordinary `let` — Missing Forward Declaration

**What goes wrong:**
In LLVM/MLIR, a function must be declared before it can be called. For `let rec f x = ... f ...`,
the recursive call to `f` inside the body must reference a function that already exists
in the module. If you emit the function body first and try to reference `f` while emitting
it, the MLIR operation for the call does not yet have a valid callee symbol.

**Prevention:**
- For `let rec`, emit a forward declaration (`llvm.func` with no body / external linkage)
  before emitting the body, then fill in the body
- Alternatively, use a two-pass approach: first collect all `let rec` bindings at module
  level and declare their types; then emit bodies in a second pass
- In MLIR, `func.func` operations can be emitted in any order in a module — use a
  symbol table and emit all `let rec` declarations before any bodies

**Phase mapping:** `let rec` codegen phase.

---

### Pitfall M-5: SSA Dominance Violation When Threading Values Across Blocks

**What goes wrong:**
MLIR enforces SSA dominance: a value can only be used by operations in blocks that are
dominated by the block where the value was defined. When compiling nested expressions
that involve branching (if-else inside a let body), it is easy to accidentally reference
an SSA value from an outer block inside an inner block that is not dominated by it.

**Why it happens:**
In a tree-walking codegen, the recursive return value from `emitExpr` is an `MlirValue`.
Passing that value into a branch of a different block that the builder is now positioned in
is fine — but passing it into a block that might not be dominated (e.g. across a loop back
edge) is not. The MLIR verifier will catch this, but only at verification time, not at
construction time.

**Prevention:**
- Keep a clear mental model of the current block at all times during codegen
- When branching, pass values across block boundaries only as block arguments (phi-style),
  never by using an SSA value from an earlier block directly in a later non-dominated block
- Run `mlirOperationVerify` after emitting each top-level function to catch violations early

**Phase mapping:** Affects any codegen phase with control flow; most acute for if-else and
eventually loops.

---

### Pitfall M-6: P/Invoke Marshaling of `nativeint` vs `IntPtr` in Struct Wrappers

**What goes wrong:**
MLIR C API functions return and accept opaque handle structs (e.g. `MlirValue`,
`MlirOperation`) that are single-field structs containing a `void*`. In F# P/Invoke,
if you declare the struct with `int` instead of `nativeint`/`IntPtr`, the handles will be
truncated on 64-bit platforms, producing garbage pointers that crash inside `libMLIR.so`.

**Why it happens:**
F# struct layout rules and P/Invoke marshaling do not warn when a `int` field is used for
a pointer-sized value. The crash happens inside the C library with no diagnostic.

**Prevention:**
- All MLIR handle types must use `nativeint` (or `IntPtr`) for the pointer field, not `int`
- Add a startup test that creates a context, calls `mlirContextGetNumLoadedDialects`, and
  verifies a nonzero result — this exercises the basic marshaling path before any codegen
- Review every `[<Struct>]` P/Invoke binding: any field representing a C pointer must be
  `nativeint`

**Phase mapping:** Phase 1 (P/Invoke infrastructure). Non-negotiable before any other work.

---

## Minor Pitfalls

Annoying but fixable without structural changes.

---

### Pitfall m-1: MLIR Diagnostics Not Registered — Silent Failures

**What goes wrong:**
By default, MLIR operations that fail emit diagnostics to a handler. If no handler is
registered on the context, errors go to stderr only in debug builds. In release builds of
`libMLIR.so`, some failures are silent and only manifest as null returns. `mlirPassManagerRun`
returning failure without any error message is the most common manifestation.

**Prevention:**
- Register a diagnostic handler on the context immediately after creation:
  `mlirContextAttachDiagnosticHandler`
- The handler should write to an F# buffer and surface all diagnostics as exceptions or
  error results
- Always check return values of `mlirPassManagerRun` and `mlirTranslateModuleToLLVMIR`
  explicitly

**Phase mapping:** Phase 1 infrastructure. Add diagnostic handler before testing any pass.

---

### Pitfall m-2: Integer Signedness Mismatch in `arith` Operations

**What goes wrong:**
`arith.divsi` (signed division) and `arith.divui` (unsigned division) produce different
results for negative numbers. Similarly, `arith.extsi` vs `arith.extui` for extending
smaller integers. Functional language integers are typically signed, but if the codegen
accidentally emits the unsigned variant, negative number arithmetic will be wrong.

**Prevention:**
- Establish a convention at project start: all integer ops use the signed (`si`) variant
- Write a constant in the codegen module: `let intDivOp = "arith.divsi"` etc., never
  inline the op name string

**Phase mapping:** Arithmetic codegen phase.

---

### Pitfall m-3: MLIR Pass Manager Created Before Dialects Registered

**What goes wrong:**
If `convert-arith-to-llvm` or `convert-func-to-llvm` is added to the pass manager before
the required dialects are registered on the context, the pass manager will reject the pass
with "pass not found" or silently skip it, depending on MLIR version. The IR will then not
be fully lowered.

**Prevention:**
- Register all required dialects at context creation time: `mlirRegisterAllDialects` or
  individually: `mlirRegisterAllLLVMTranslations`, `mlirRegisterAllPasses`
- Register dialects before creating the pass manager, not after

**Phase mapping:** Phase 1 infrastructure.

---

### Pitfall m-4: Forgetting `mlirContextSetAllowUnregisteredDialects` for Custom Dialects

**What goes wrong:**
If you define a custom dialect (or use a non-standard op temporarily during development),
MLIR will reject operations from unregistered dialects during verification. This is correct
behavior, but easy to trigger accidentally when prototyping.

**Prevention:**
- During development/prototyping only, call `mlirContextSetAllowUnregisteredDialects(ctx, true)`
- Remove this before any pass runs that needs the IR to be fully verified
- For production: register every dialect you use explicitly

**Phase mapping:** Initial prototyping only. Remove before Phase 2.

---

## Phase-Specific Warnings

| Phase Topic | Likely Pitfall | Mitigation |
|---|---|---|
| P/Invoke infrastructure | M-6: pointer size truncation in struct wrappers | Use `nativeint` everywhere; add startup smoke test |
| Context/Module lifecycle | C-2: context destroyed before module | `CompilerSession` wrapper with enforced destruction order |
| First MLIR operation emission | C-1: region handle used after ownership transfer | Ownership-tracking wrapper type; never touch handle after `AddOwnedRegions` |
| Dialect registration | m-3: passes silently missing due to late registration | Register all dialects + passes before creating pass manager |
| Arithmetic codegen | m-2: signed vs unsigned ops | Constant map for all op names |
| if-else codegen | M-1: i1 boolean not zero-extended to i64 at return | Explicit `zext` at boundary; M-3: block args missing on branch |
| SSA control flow | M-5: dominance violation | Run `mlirOperationVerify` after each function |
| MLIR → LLVM lowering | C-3: unrealized_conversion_cast remains | Add `reconcile-unrealized-casts` last; grep IR after pipeline |
| MLIR → LLVM lowering | C-4: wrong pass order | Canonical order: scf→cf→cf-llvm→arith-llvm→func-llvm→reconcile |
| let rec codegen | M-4: missing forward declaration | Two-pass: declare all let-rec symbols before emitting bodies |
| Lambda / closure codegen | C-5: non-uniform calling convention | Flat closure struct decided before first lambda; verify with hand-written MLIR test |
| AST annotation | C-6: missing free-variable and type annotation | `TypedExpr` lowering pass before any MLIR emission |
| libMLIR.so version | M-2: version mismatch between build and runtime | Pin LLVM version; explicit `LD_LIBRARY_PATH`; CI uses pinned build |
| Diagnostics | m-1: silent failure from unregistered handler | Register diagnostic handler in Phase 1 |

---

## Sources

- [MLIR C API — Official Documentation](https://mlir.llvm.org/docs/CAPI/)
- [Ownership semantics in MLIR C++ API — LLVM Discourse](https://discourse.llvm.org/t/ownership-semantics-in-mlir-c-api/90090)
- [Dialect Conversion — MLIR Official](https://mlir.llvm.org/docs/DialectConversion/)
- [How to avoid persistent unrealized_conversion_casts — LLVM Discourse](https://discourse.llvm.org/t/how-to-avoid-persistent-unrealized-conversion-cast-s-when-converting-dialects/71721)
- [Ordering of lowering passes may lead to conversion failure — GitHub Issue #55028](https://github.com/llvm/llvm-project/issues/55028)
- [Invalid LLVM dialect after lowering — GitHub Issue #168837](https://github.com/llvm/llvm-project/issues/168837)
- [MLIR Lowering through LLVM — Jeremy Kun](https://www.jeremykun.com/2023/11/01/mlir-lowering-through-llvm/)
- [MLIR Dialect Conversion — Jeremy Kun](https://www.jeremykun.com/2023/10/23/mlir-dialect-conversion/)
- [Chapter 5: Partial Lowering — MLIR Tutorial](https://mlir.llvm.org/docs/Tutorials/Toy/Ch-5/)
- [Chapter 6: Lowering to LLVM — MLIR Tutorial](https://mlir.llvm.org/docs/Tutorials/Toy/Ch-6/)
- [MLIR Language Reference](https://mlir.llvm.org/docs/LangRef/)
- [LLVM IR Target — MLIR](https://mlir.llvm.org/docs/TargetLLVMIR/)
- [Using MLIR from C and Python — LLVM Dev Meeting 2024](https://llvm.org/devmtg/2024-10/slides/tutorial/Zinenko-UsingMLIR-from-C-and-Python.pdf)
- [Notes on Using the MLIR C API in Swift — duan.ca 2024](https://duan.ca/2024/08/swift-mlir-cmake/)
- [Enabling external dialects as shared libs for C API — GitHub Issue #108253](https://github.com/llvm/llvm-project/issues/108253)
- [MLIR func Dialect](https://mlir.llvm.org/docs/Dialects/Func/)
- [MLIR scf Dialect](https://mlir.llvm.org/docs/Dialects/SCFDialect/)
- [MLIR Understanding the IR Structure](https://mlir.llvm.org/docs/Tutorials/UnderstandingTheIRStructure/)
- [Lambda the Ultimate SSA — arxiv 2022](https://arxiv.org/pdf/2201.07272) (SSA + functional IR patterns)
