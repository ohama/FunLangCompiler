# Domain Pitfalls: Functional Language Compiler via MLIR → LLVM

**Domain:** Functional language compiler (F# host, MLIR text format, LLVM backend)
**Researched:** 2026-03-26
**Scope:** v1 pitfalls (MLIR C API, closures, lowering pipeline) + v2 additions (Boehm GC,
heap-allocated types, pattern matching) + v3 additions (ADT/GADT, mutable records, setjmp/longjmp exceptions)
**Confidence:** HIGH for Boehm GC integration and heap representation; HIGH for pattern
matching compilation; HIGH for ADT/GADT tag representation; HIGH for setjmp/longjmp pitfalls;
MEDIUM for interaction between existing stack closures and new GC heap

---

## Critical Pitfalls

Mistakes in this category cause silent memory corruption, crashes at runtime, or complete
rewrites of the codegen layer.

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

### Pitfall C-7: Existing Stack-Allocated Closures Are Invalidated When Closures Escape

**What goes wrong:**
v1 closures use `llvm.alloca` — the caller allocates the environment struct on its own stack
frame and passes the pointer to the closure-maker. This is correct when the closure is
consumed in the same call frame (e.g. immediately passed to a known function). But if the
closure value escapes — stored in a list, returned from a function, or captured by another
closure — the stack memory is freed when the allocating frame returns. The receiving code
then holds a dangling pointer.

**Why it happens specifically for v2:**
v2 introduces lists and tuples. The moment a closure can be stored inside a list element or
tuple field, it escapes its allocating frame. The existing v1 pattern of
`llvm.alloca → fill → call closure-maker → use ptr in same frame` stops being safe.

**Concrete trigger:**
```
let f = fun x -> x + 1
let cells = [f; f; f]   (* f escapes into a list — stack alloca is now dangling *)
```

**Consequences:**
- Correct output for trivial programs where closures never escape
- Segfault or wrong computation when closures are stored in heap-allocated containers
- Extremely hard to diagnose: the stack memory may appear valid until the frame is reused

**Prevention:**
- In v2, allocate ALL closure environments via `GC_malloc` instead of `llvm.alloca`
- Replace `LlvmAllocaOp` in closure-maker codegen with a `GC_malloc` call (extern declaration
  + `llvm.call @GC_malloc(size_in_bytes)`)
- The size to allocate is `(1 + numCaptures) * sizeof(pointer)` = `(1 + N) * 8` bytes on
  64-bit platforms
- Remove the caller-allocates pattern entirely; closure-maker is responsible for allocation

**Detection (warning signs):**
- Programs that store closures in lists or return closures from functions fail
- ASan reports `use-after-return` or `stack-use-after-scope`
- Program works in debug builds (stack frames preserved longer) but fails in release builds

**Phase mapping:** Must be addressed as part of the GC integration phase, before any list or
tuple codegen that could store closure values. Do not mix alloca-closures and heap types.

---

### Pitfall C-8: Boehm GC Not Initialized Before First `GC_malloc` Call — Silent Corruption

**What goes wrong:**
Boehm GC requires `GC_INIT()` to be called before any `GC_malloc` or `GC_free` call. If
`GC_INIT()` is omitted, the collector may appear to work for small programs (the slow-path
fallback initializes lazily), but stack scanning for roots will use an incorrect stack base,
causing live objects to be collected prematurely. The result is a dangling pointer that
contains either garbage data or a different valid object.

**Why it happens:**
The lazy initialization path does work for simple single-threaded programs, making the bug
hard to trigger in tests. It only manifests in programs with significant allocation pressure
or recursion depth that pushes the stack base detection off.

**Consequences:**
- Premature collection of live objects → reading freed memory → wrong values or crashes
- Non-deterministic: fails on larger inputs but passes on small test cases
- Extremely hard to debug because the corrupted object may be far from the allocation site

**Prevention:**
- Emit a call to `GC_INIT()` at the very start of `@main` before any other code
- Declare `@GC_INIT` as an external function in the MLIR module:
  `func.func private @GC_INIT() -> ()`
- This must be the first call in main, before any string/tuple/list allocation
- Verify by running with `GC_PRINT_STATS=1` to confirm the GC is active

**Detection (warning signs):**
- Programs pass with small inputs but produce wrong output or crash with large inputs
- Adding more let bindings (more allocation) makes the bug more likely
- Running with `GC_FIND_LEAK=1` reports leaks that should not exist

**Phase mapping:** GC integration phase — the very first task. All other v2 phases depend
on GC being correctly initialized.

---

### Pitfall C-9: Mixing `malloc` and `GC_malloc` — GC Cannot See malloc'd Roots

**What goes wrong:**
Boehm GC works by scanning memory for pointer-shaped values and treating them as roots.
It only scans memory regions it knows about: the stack, registers, and heap blocks allocated
via `GC_malloc`. If you allocate a struct via plain `malloc` and that struct contains a
pointer to a `GC_malloc`'d object, the GC cannot see the reference. It may collect the
object while the `malloc`'d struct is still live, producing a dangling pointer.

**Why it happens for LangBackend specifically:**
The existing v1 closure structs are stack-allocated (`llvm.alloca`). If v2 naively adds
heap allocation but some paths still use `malloc` (e.g. string interning, a C helper
function), those paths will be invisible to the GC.

**Consequences:**
- Object collected while still reachable via malloc'd memory
- Dangling pointer read → wrong data, type confusion, or crash
- Bug is intermittent: depends on GC timing and allocation pressure

**Prevention:**
- Use `GC_malloc` for every heap allocation in generated code without exception
- For any C helper functions linked into the binary, either (a) use `GC_malloc` inside them
  or (b) register the memory range with `GC_add_roots`
- Do not call `free` on `GC_malloc`'d memory — the GC owns it
- Do not call `GC_free` on `malloc`'d memory — this is undefined behavior

**Detection (warning signs):**
- Enable `GC_FIND_LEAK=1` — leaks may indicate malloc/GC_malloc mixing
- ASan with `detect_leaks=1` will report GC_malloc allocations as leaks (expected)
- Valgrind may report double-frees if GC_free is called on malloc memory

**Phase mapping:** GC integration phase. Establish the rule "all generated heap allocations
use GC_malloc" before any type codegen. Apply consistently across strings, tuples, lists,
and closures.

---

### Pitfall C-10: Pattern Match With No Default Arm — Runtime Crash on Unmatched Constructor

**What goes wrong:**
When compiling pattern matching for lists (`[] | x :: xs -> ...`), the compiler must emit
code that handles all constructors the type can have. If the generated code tests the tag
of a cons cell but has no fallback for an unexpected tag value, a match failure at runtime
crashes with no useful error message — typically a segfault from jumping to address 0, or
undefined behavior from reading uninitialized memory.

**Why it happens:**
The decision-tree algorithm for compiling pattern matches (Maranget 2008) produces
correct code only if the compiler correctly identifies all constructors for a type and
generates the default branch for any missing cases. A simplified implementation that only
handles the cases written by the programmer (and assumes they are exhaustive) will silently
produce code with missing arms.

**Consequences:**
- Runtime crash (segfault or wrong result) when a match is incomplete
- Silent wrong computation if the "fallthrough" path happens to produce an integer that
  looks valid
- No compile-time warning, because the compiler does not check exhaustiveness

**Prevention:**
- Every compiled `match` expression must have an explicit fallback arm that calls a runtime
  error function (e.g., `@lang_match_failure`)
- Declare `@lang_match_failure` as an external function that prints a message and calls
  `abort()`; emit it in the module preamble
- Even if the source-level type checker guarantees exhaustiveness, the codegen should still
  emit the runtime error fallback as a safety net
- For list patterns specifically: always emit a tag check for both `0` (nil) and `non-zero`
  (cons); never assume the cons path is "everything else"

**Detection (warning signs):**
- Programs crash on inputs not covered by tests
- Valgrind reports "invalid read" inside a match-compiled basic block
- The generated MLIR has a branch block with no successors other than the match arms

**Phase mapping:** Pattern matching phase. Establish the `@lang_match_failure` convention
before writing any match codegen.

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

### Pitfall M-7: Tag Value Collision Between Nil and Integer Zero

**What goes wrong:**
When representing list cells, the most natural representation of the empty list (nil) is a
null pointer (`0` / `nullptr`). But if integers in the language are also represented as
unboxed `i64` values, a variable holding the integer `0` and a variable holding `nil` have
the same bit pattern. A tag check that compares the pointer to zero to detect nil will fire
on integer zero, causing pattern matching to incorrectly route integer values to the nil
branch.

**Why it happens:**
The simplest encoding maps `nil = null pointer` and `cons = heap pointer`. An integer is not
a pointer, but if both are passed through the same `i64`-typed SSA value position (e.g., a
polymorphic function), the tag test is ambiguous.

**Consequences:**
- `match x with [] -> ... | h :: t -> ...` produces wrong results when called with `0`
- Extremely hard to debug: the incorrect branch may produce a plausible-looking result

**Prevention:**
- Choose a representation where nil is type-distinguished from integers. Options:
  1. **Header word approach**: Every heap object (cons cell, tuple, string) starts with a
     tag word. Nil is a statically allocated singleton object with tag `0`. Tag checks read
     the header word, not the pointer value itself.
  2. **Separate type tag in calling convention**: Every language value carries a `(tag, payload)`
     pair, where tag distinguishes int/bool/string/cons/nil.
- For LangBackend's typed language: LangThree's type system prevents mixing integer and
  list values in the same position, so the null pointer encoding for nil is safe as long as
  the type-directed codegen never emits integer and list values through the same SSA slot.
  Enforce this through `MlirType` — list pointers are always `Ptr`, integers are always
  `I64`; never coerce between them.

**Detection (warning signs):**
- Pattern match on lists produces wrong output when the list contains zero values
- Test case `match [0; 1] with h :: t -> h` returns the wrong branch

**Phase mapping:** List representation design phase. Choose the encoding before writing
any cons cell codegen.

---

### Pitfall M-8: String Length Header Missing — C Interop and Length Queries Break

**What goes wrong:**
The simplest string representation is a null-terminated `i8*`, identical to a C string.
This breaks in two ways: (1) strings with embedded null bytes are silently truncated; (2)
computing string length requires O(N) scanning, which is fine for static strings but breaks
when a language built-in `length` function is needed. The bigger practical problem is that
some C library functions (printf with `%s`) expect null termination, while others (memcpy,
fwrite) use explicit lengths — so you need both.

**Why it happens:**
The easy path is `llvm.mlir.addressof @my_string_literal` which gives you a pointer to a
null-terminated byte sequence. This works for `printf` but breaks for any length query or
embedded-null string.

**Prevention:**
- Represent strings as a two-word heap struct: `{ i64 length, i8* data }` allocated via
  `GC_malloc`. The `data` field points to the byte array (null-terminated for C compat).
- String literals: allocate the header struct in `@main` setup, point `data` at the
  MLIR global string constant
- This layout also lets the GC scan the header struct (it sees the `data` pointer as a
  potential root even though it points into global memory — harmless false retention)
- Never use raw `i8*` as the language-level string type; always go through the header struct

**Detection (warning signs):**
- `string_length` built-in function returns wrong result
- Printf of a string with embedded null prints a truncated string

**Phase mapping:** String codegen phase. Decide the layout before emitting any string
literal.

---

### Pitfall M-9: Boehm GC False Pointer Retention From Integer Values

**What goes wrong:**
Boehm GC is conservative: it treats any word-sized value anywhere on the stack or in GC
heap blocks that looks like a valid heap address as a pointer. If your language's integer
values happen to be numerically equal to a heap address (e.g. a large integer like
`0x0000555500001234`), the GC will retain the object at that address indefinitely, even
if it is actually garbage. This is "floating garbage" — memory that is logically unreachable
but never collected.

**Why it happens:**
LangBackend uses unboxed `i64` for integers. These 64-bit integers live on the stack (as
SSA values materialized in registers/stack slots) and in closure environment structs
(as `i64` fields). The GC scans both locations and may mistake large integers for pointers.

**Consequences:**
- Memory growth: programs that allocate a lot will use more memory than expected
- Not a correctness bug (objects are retained, not collected too early)
- Can mask real memory leaks

**Prevention:**
- This is inherent to conservative GC and cannot be fully eliminated
- Mitigation: avoid storing large integer values in GC-scanned heap structs; use
  `GC_malloc_atomic` for memory that contains no pointers (pure integer arrays, string data)
  — the GC does not scan atomic allocations for pointers
- For string `data` arrays: allocate with `GC_malloc_atomic` since the byte array contains
  no pointers
- For closure environments and cons cells: use regular `GC_malloc` (they do contain pointers)

**Detection (warning signs):**
- `GC_PRINT_STATS=1` shows heap growing steadily without collection cycles recovering space
- Running with `BDWGC_PRINT_BLACK_LIST=1` shows many values blacklisted as false pointers

**Phase mapping:** GC integration phase + any allocation-heavy phase. Establish the
`GC_malloc` vs `GC_malloc_atomic` split as part of GC integration.

---

### Pitfall M-10: Tuple Components Not Separately Decomposed in Pattern Matrix

**What goes wrong:**
When compiling `match (a, b) with (1, 2) -> ... | (x, y) -> ...`, the pattern compiler
must decompose the tuple pattern into its component patterns and match them independently.
A naive implementation that treats `(1, 2)` as an atomic pattern (rather than two separate
sub-patterns) cannot correctly handle nested tuples, wildcards in one component, or
tuple patterns combined with list patterns.

**Why it happens:**
Maranget's decision tree algorithm for compiling pattern matching requires that tuple
patterns are never stored as atomic cells in the pattern matrix — their components are
pushed into separate columns. Implementing this decomposition step incorrectly (or skipping
it) produces a pattern compiler that only handles the simplest flat cases.

**Consequences:**
- Nested tuple patterns fail at compile time or produce wrong codegen
- Wildcard patterns (`_`) inside tuples are ignored, causing spurious match failures
- Mixing tuple and list patterns in the same match expression produces wrong branching

**Prevention:**
- Implement the full Maranget specialization operation: for a tuple scrutinee with N fields,
  expand each tuple pattern row into N separate sub-columns before building the decision tree
- Test with: `match (a, b) with (_, 0) -> "zero-second" | (x, y) -> "other"` — the `_`
  must not test `a` at all
- Implement the default matrix operation: rows with a variable binding (wildcard or name)
  in the tuple position pass through unchanged to the default branch

**Phase mapping:** Pattern matching phase. Implement tuple decomposition before list
patterns — tuple matching is strictly simpler (no recursive structure).

---

### Pitfall M-11: `GC_malloc` Size Calculation Off-By-One for Cons Cells and Tuples

**What goes wrong:**
When allocating a cons cell `{ tag: i64, head: i64|ptr, tail: ptr }` or a tuple
`{ tag: i64, field_0, field_1, ... }`, the size argument to `GC_malloc` must be the exact
number of bytes required. A common mistake is to count the number of fields and forget
the tag word, or to use the count of pointer-sized slots rather than byte count.

**Concrete example:**
A cons cell with one `i64` head and one `ptr` tail needs `3 * 8 = 24 bytes` (tag + head +
tail). Passing `2 * 8 = 16 bytes` allocates a struct that is too small; writing the tag
word at offset 0 is fine, but writing the tail pointer at offset 16 writes past the
allocation into adjacent heap memory, corrupting the GC's own bookkeeping.

**Prevention:**
- Define size constants as named values in the codegen layer, not as inline arithmetic:
  ```fsharp
  let consSize = 3 * 8   // tag(8) + head(8) + tail(8)
  let tupleSize n = (1 + n) * 8  // tag(8) + n fields * 8
  let stringHeaderSize = 2 * 8   // length(8) + data_ptr(8)
  ```
- Emit an `arith.constant` for the size and pass it to `GC_malloc` rather than computing
  inline
- Add a unit test that allocates a cons cell, writes all fields, and reads them back — a
  misaligned allocation will corrupt the adjacent cell during write

**Detection (warning signs):**
- Heap corruption detected by Boehm GC's internal checks (`GC_DEBUG=1`)
- ASan reports heap buffer overflow immediately on the store after a small allocation
- Programs fail non-deterministically depending on allocation pattern

**Phase mapping:** GC integration phase + cons cell / tuple codegen.

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

### Pitfall m-5: `GC_INIT` Called Too Late Causes Thread-Specific Stack Base Errors

**What goes wrong:**
If `GC_INIT()` is called from a function other than `main` — for example, from the first
allocation function — Boehm GC may compute the wrong stack base. The stack base must be the
bottom of the main thread's stack, which is only reliably accessible from the `main` function
itself. A late call to `GC_INIT` from inside a helper function records a stack base that is
several frames above the true bottom, causing the GC to miss roots in the outer frames.

**Prevention:**
- Always emit `call @GC_INIT()` as the first instruction in the generated `@main` function
- This is a property of the generated code, not the host F# code: the `@main` MLIR function
  must begin with `func.call @GC_INIT()`

**Phase mapping:** GC integration phase — enforced in the `elaborateModule` function that
constructs the `@main` FuncOp.

---

### Pitfall m-6: String Literals Referenced via `llvm.mlir.addressof` Are Not GC Roots

**What goes wrong:**
MLIR string constants defined via `llvm.mlir.global` or emitted as global byte arrays in
the LLVM IR reside in the `.rodata` section, not on the GC heap. A pointer to such a string
is a pointer into static memory, not a GC object. If the language-level string type is a
GC-managed header struct that points to this static data, the GC will scan the header struct
and see the `data` pointer pointing into `.rodata`. Boehm GC will not collect `.rodata`
(it's not a GC heap block), which is correct. But if you accidentally mix raw `i8*` pointers
to `.rodata` with GC-allocated string header pointers, you will have two incompatible string
representations that look identical at the type level (`Ptr`).

**Prevention:**
- Define a language-level invariant: every string value at the language type level is a
  pointer to a GC-allocated header struct `{ i64 length, i8* data }`. The `data` field may
  point to `.rodata` or to a GC-allocated byte array — this is an internal detail.
- Never expose raw `i8*` pointers to string literals as first-class language values
- Add an `isStringHeader` wrapper in the codegen that always allocates the header struct
  for literals, even if the byte data lives in `.rodata`

**Phase mapping:** String codegen phase.

---

---

## Critical Pitfalls — v3: ADT/GADT, Records, Exceptions

Mistakes specific to adding algebraic data types with mutable records and setjmp/longjmp
exceptions to the existing system.

---

### Pitfall C-11: ADT Tag Stored at Field 0 Conflicts With Existing `LlvmGEPLinearOp(_, ptr, 0)` Loads

**What goes wrong:**
The existing codebase uses `LlvmGEPLinearOp(slot, ptr, 0)` / `LlvmLoadOp` to load field 0
of cons cells (the `head`) and tuples (first element). ADTs need a tag word at field 0 and
payload fields at indices 1..N. If ADT constructors are compiled with the same
`GEP field 0 = first payload` convention used for tuples and cons cells, the tag check is
missing and pattern matching reads payload data as a tag, producing garbage.

**Why it happens:**
The temptation is to reuse the existing tuple representation `{field0, field1, ...}` for
ADT constructors and simply put the payload at field 0. This works for single-constructor
types but breaks immediately for multi-constructor types that need tag discrimination.
The existing `CtorTag` union in `MatchCompiler.fs` already has cases for `ConsCtor` and
`NilCtor` but no `UserDefinedCtor` case — adding one without establishing the tag-at-0
invariant is the pitfall.

**Consequences:**
- Pattern matching on multi-constructor ADTs reads payload pointer as a tag value
- Null-payload constructors (constant constructors like `None`, `True`) are confused with
  heap-allocated constructors
- Silent wrong computation — the match succeeds but dispatches to the wrong arm

**How to avoid:**
- Establish the invariant: every ADT heap block has the layout
  `{ i64 tag @offset0, ptr|i64 field1, ptr|i64 field2, ... }`.
  Constant constructors (arity 0) can be represented as a small integer (the tag itself
  stored as an unboxed `i64`) rather than a heap pointer, to avoid allocation — but this
  requires type-directed dispatch at call sites.
- Add a new `CtorTag` case: `UserCtor of typeName: string * ctorName: string * tag: int`
- GEP for tag check: `LlvmGEPLinearOp(tagSlot, ptr, 0)` + load as `I64`; GEP for payload:
  `LlvmGEPLinearOp(fieldSlot, ptr, 1 + fieldIndex)`
- Never share layout code between ADT blocks and tuples/cons cells — keep them separate
  codegen paths even if the struct layout looks similar

**Warning signs:**
- Match on `Some x | None` with `None` case always triggering
- Tag value printed/logged shows a large number (a heap pointer) rather than 0 or 1
- ASan reports out-of-bounds read at offset 0 of an ADT block that was allocated too small

**Phase to address:** ADT codegen phase, before any constructor pattern compilation.

---

### Pitfall C-12: Constant Constructor Allocation on Every Evaluation

**What goes wrong:**
Constant constructors (zero-arity constructors like `None`, `False`, `Nil`) have no payload.
If the codegen naively allocates a fresh `GC_malloc` block for every evaluation of `None`,
programs that evaluate `None` in a loop allocate O(N) heap blocks that immediately become
garbage, causing unnecessary GC pressure and bloating the heap. More critically, if
`None` is represented as a heap pointer, the null-pointer test used for `NilCtor` in the
existing cons-cell code will not fire for `None`, breaking existing `LlvmNullOp` / null
checks if they are reused naively.

**Why it happens:**
The uniform-boxing instinct: "every value is a pointer to a GC block" is correct for
payload-bearing constructors but wasteful for constant constructors. The mistake is applying
the rule universally without distinguishing the two cases.

**Consequences:**
- O(N) GC allocation for a constant constructor evaluated N times
- Possible confusion with existing null-pointer encoding for `NilCtor` (list empty)
- Pattern matching cost becomes O(N) pointer comparisons if constants are not interned

**How to avoid:**
- Represent zero-arity constructors as small unboxed integers (the tag value as `I64`), not
  heap pointers. This is the same representation used for `BoolLit` and `IntLit` in the
  existing `CtorTag`.
- Use type-directed codegen: when the result type is known to be a zero-arity constructor,
  emit `ArithConstantOp(result, tagValue)`. When arity > 0, emit `GC_malloc` + fill.
- Update `MlirType` handling: constant constructors produce `I64` SSA values, not `Ptr`.
  Pattern matching on them uses `ArithCmpIOp("eq", scrutVal, tagConst)`, not a GEP load.
- Keep `NilCtor` as the null-pointer encoding for the existing list type; ADT
  constant constructors use integer encoding on a separate type-directed path.

**Warning signs:**
- Heap grows linearly with evaluation count even for programs that only construct `None`
- `GC_PRINT_STATS=1` shows many single-word allocations being collected immediately

**Phase to address:** ADT codegen phase, alongside constructor representation design.

---

### Pitfall C-13: GADT Type Refinement Discarded — Payload Loaded With Wrong MLIR Type

**What goes wrong:**
In GADTs, the type of a constructor's payload depends on the type index. For example,
`Expr of 'a`:
```
type _ expr =
  | IntE : int expr
  | BoolE : bool expr
  | IfE : bool expr * 'a expr * 'a expr -> 'a expr
```
At the match arm `IntE -> ...`, the type refines `'a = int`, so the payload (if any) should
be loaded as `I64`. At `IfE -> ...`, some payloads are `Ptr` (sub-expressions). If the
codegen ignores the GADT refinement and always loads payloads as `Ptr` (the safe uniform
representation), integer-typed fields are misinterpreted as pointers. Conversely, loading
all fields as `I64` misinterprets pointer fields as integers.

**Why it happens:**
The existing `testPattern` in `Elaboration.fs` uses a simple heuristic
(`loadTypeOfPat = function TuplePat -> Ptr | _ -> I64`) that was designed for tuples.
Reusing this for GADT payloads without threading the GADT type refinement produces wrong
load types. The F# host compiler erases GADT refinements at compile time; the LangBackend
codegen must also erase them, but must do so correctly by consulting the type annotation
at each constructor arm.

**Consequences:**
- Integer payload loaded as `Ptr`, then used in arithmetic → type error in MLIR verifier or
  wrong result (pointer value interpreted as integer)
- `Ptr` payload loaded as `I64` → pointer stored as integer, GC cannot follow it → premature
  collection of live sub-expression trees

**How to avoid:**
- Thread explicit type annotations through the match compiler for GADT patterns. Each
  `ConstructorPat` must carry the resolved payload types (post-refinement) as annotations,
  not derive them from the pattern shape.
- For the MLIR text emission phase: each payload field has a declared `MlirType` (`I64` or
  `Ptr`) that comes from the type annotation, not from a syntactic heuristic.
- Treat GADTs as requiring a type-annotation pass before codegen. Emit `ConstructorPat`
  nodes that include the field type list explicitly.
- If type annotations are not yet available, conservatively box everything as `Ptr` and
  accept the integer-as-pointer false retention by Boehm GC, but plan to fix it.

**Warning signs:**
- MLIR verifier fires with "type mismatch: expected i64, got !llvm.ptr" inside a GADT
  match arm
- Programs with GADT integer payloads return garbage values or crash on arithmetic

**Phase to address:** GADT codegen phase. Requires type annotation infrastructure to be in
place before codegen starts.

---

### Pitfall C-14: `longjmp` Over Stack Frames Containing Live GC Roots — Roots Not Cleared

**What goes wrong:**
When `longjmp` is called from a `raise` site, it unwinds the C stack past one or more stack
frames that contain pointer-typed SSA values live at the time of the jump. Boehm GC scans
the stack conservatively — it treats every word on the stack that looks like a heap address
as a live root. After `longjmp`, those stack frames are logically dead, but the stack memory
is not zeroed. If the GC runs between the `longjmp` and the next write to those stack
locations, it will see the old pointer values and treat those objects as live, preventing
collection.

This is the "floating garbage" form of the bug: objects that should be collected after an
exception are retained indefinitely. More dangerously, if the stack memory is reused for a
new frame with a different layout, a previously live GC pointer now aliases an integer slot
in the new frame — the GC will follow a stale pointer to an object whose identity has
changed.

**Why it happens:**
Boehm GC does not know the boundaries of stack frames or which slots contain live pointers.
It scans every word. `longjmp` does not zero the abandoned stack memory. The GC has no
way to know that frames below the `setjmp` frame were abandoned.

**Consequences:**
- Floating garbage: objects kept alive longer than needed (correctness-safe but memory leak)
- Stale pointer aliasing after stack reuse: reading a GC-managed object through a stale
  pointer in a reused frame can produce type confusion (accessing field N of object X when
  the pointer now aliases object Y)
- Non-deterministic: depends on GC timing and allocation pressure

**How to avoid:**
- After each `longjmp` return in the handler (`setjmp` returns non-zero), immediately
  re-initialize all pointer-typed locals that could alias the abandoned frames to null.
  In practice this means the handler itself should allocate no pointers before the cleanup.
- Use a C runtime wrapper `lang_setjmp` / `lang_longjmp` that calls `GC_collect_a_little()`
  after the longjmp returns, to clear the stale roots via a GC cycle.
- Keep exception handler code as simple as possible: no allocation between `setjmp` and the
  first handler block.
- For correctness: document that the implementation has conservative-GC floating-garbage
  semantics for exceptions. This is acceptable for most programs but must be acknowledged.

**Warning signs:**
- `GC_PRINT_STATS=1` shows heap growing after every `raise` + handler cycle
- Programs that raise many exceptions run out of memory despite small working sets
- Valgrind reports "invalid read" in GC scan of abandoned stack frames (rare)

**Phase to address:** Exception handling phase. Address in the runtime wrapper design before
any generated `try-with` code is produced.

---

### Pitfall C-15: `setjmp` Without `returns_twice` Attribute Causes LLVM Miscompilation

**What goes wrong:**
LLVM's optimizer assumes that a function call returns at most once, unless the callee is
marked with the `returns_twice` attribute. The C standard library `setjmp` is declared with
this attribute in system headers, but if you call a wrapper function `lang_setjmp` (defined
in `runtime.c`) that calls `setjmp` internally, the wrapper itself is not marked
`returns_twice`. LLVM sees `lang_setjmp` as a normal function and optimizes the caller
assuming it returns once. This means:
- Variables modified between `lang_setjmp` and the corresponding `longjmp` may be
  optimized away (dead-store elimination)
- The result value of `lang_setjmp` may be cached in a register and never re-read after
  `longjmp` returns

This produces miscompilations that are silent at `-O0` but wrong at `-O2` or higher.

**Why it happens:**
The `returns_twice` attribute is an LLVM IR calling-convention annotation, not a C
attribute. A C wrapper around `setjmp` does not inherit the attribute. The MLIR text
emission for a `call @lang_setjmp(...)` instruction does not automatically annotate the
callee. Since LangBackend emits MLIR text, there is no mechanism to attach
`returns_twice` through the normal codegen path without explicit support.

**Consequences:**
- At `-O2`: `try-with` blocks silently always take the no-exception branch even when an
  exception is raised, because the `setjmp` return value is cached as `0`
- Dead-store elimination removes writes to the exception value variable between try body
  and handler
- Only visible with optimizations; tests at `-O0` pass, tests at `-O2` fail

**How to avoid:**
- The `lang_setjmp` wrapper in `runtime.c` must either (a) call `setjmp` directly as a
  macro (so the C compiler sees the true `setjmp` with its `returns_twice` treatment), or
  (b) be declared `__attribute__((returns_twice))` explicitly in the C source.
  Option (a) is strongly preferred: use `#define lang_setjmp(buf) setjmp(buf)` or a static
  inline wrapper, not an out-of-line function.
- Never put `setjmp` behind a regular function call boundary where the optimizer cannot see
  the attribute.
- Emit MLIR at `-O0` during development; only promote to `-O2` after the
  `returns_twice` issue is verified resolved.
- Add a regression test that raises an exception at `-O2` and checks the handler fires.

**Warning signs:**
- Exception tests pass at `-O0` but fail at `-O1` or `-O2`
- Handler code is never executed even though `raise` was called
- `lang_setjmp` return value is always `0` in debugger even after `longjmp`

**Phase to address:** Exception handling phase, runtime.c design. Non-negotiable before
testing with any optimization level.

---

### Pitfall C-16: Exception Raised Inside Exception Handler — `jmp_buf` Stack Corruption

**What goes wrong:**
Each `try-with` block requires a `jmp_buf` allocated at the corresponding stack frame and
pushed onto a runtime exception handler stack. When an exception is raised, `longjmp` jumps
to the innermost handler. If the handler body itself raises an exception (directly or
indirectly via a function it calls), the second `longjmp` must find the next enclosing
handler. If the handler stack is not correctly popped before the handler body executes, the
second `longjmp` jumps back to the same handler — an infinite loop or stack smash.

Additionally, if the `jmp_buf` was allocated on the stack of the try-frame (which is the
correct and efficient allocation), and the handler is in the same frame, the `longjmp` does
not unwind to a different frame — it restores registers within the same frame. This is
correct. But if the `jmp_buf` is allocated on the GC heap (a common shortcut to avoid
thinking about lifetimes), the handler stack management must account for the extra
indirection.

**Why it happens:**
The natural implementation uses a global or thread-local linked list of `jmp_buf` pointers.
Each `try-with` pushes at entry; the handler pops at entry before executing the handler body.
The mistake is popping after the handler body executes, or not popping at all when the
protected body completes normally (no exception). Both leave the handler stack in a wrong
state for the next `try-with`.

**Consequences:**
- Second exception in handler jumps to the same handler → infinite loop
- Normal completion of protected body leaves a dangling entry on the handler stack →
  next unrelated `raise` jumps to an already-completed handler frame → crash

**How to avoid:**
- Use the standard pattern:
  1. Push `jmp_buf` pointer onto handler stack
  2. Call `setjmp`
  3. If `setjmp` returns 0 (normal path): execute protected body, then **pop** handler stack
  4. If `setjmp` returns non-zero (exception path): do NOT push again; execute handler body
     (handler stack was already popped in step 3's exception path? No — it wasn't executed)
  The correct invariant: pop the handler stack immediately when `setjmp` returns non-zero,
  before executing any handler code.
- Allocate `jmp_buf` on the C stack (as a local variable in the generated function), not on
  the GC heap. The GC heap allocation adds false-root noise and complicates lifetime reasoning.
- In `runtime.c`, expose: `lang_push_handler(jmp_buf*)`, `lang_pop_handler()`,
  `lang_current_handler() -> jmp_buf*`, `lang_raise(ptr exnValue)`.

**Warning signs:**
- Program hangs after the second exception raised inside a handler
- Stack overflow after many exception/handler cycles
- Handler fires for an unrelated `raise` that should have been caught by a different handler

**Phase to address:** Exception handling phase. Implement and test the handler stack
management in `runtime.c` before generating any `try-with` codegen.

---

## Moderate Pitfalls — v3: ADT/GADT, Records, Exceptions

---

### Pitfall M-12: Mutable Record Field Aliasing — Two Language Values Share the Same Heap Cell

**What goes wrong:**
When a mutable record is passed to a function, the generated code passes a `Ptr` to the
GC-allocated record block. If the function stores that pointer and the caller also retains
the pointer, both have an alias to the same heap cell. A mutation via one alias is visible
through the other. This is correct semantics for a by-reference mutable record, but it
becomes a bug when the programmer (or the compiler) assumes copy semantics.

Specifically: `let r2 = r1` in the generated code emits no copy — both `r2` and `r1` are
the same `Ptr` SSA value. If the source language intends value semantics for records (as in
F# default records), every assignment must emit a copy. If the source language intends
reference semantics (as in OCaml `ref` or F# `[<Struct>]`-free records with mutable fields),
no copy is needed. Conflating the two produces silent aliasing bugs.

**How to avoid:**
- Define the semantics of mutable record binding in the source language explicitly before
  writing any codegen: is `let r2 = r1` a copy or an alias?
- If copy semantics: add a `lang_record_copy` path that `GC_malloc`s a new block and
  `memcpy`s all fields. Emit this at every `let r2 = r1` assignment where `r1` is a mutable
  record type.
- If reference semantics: document the aliasing and ensure the pattern match compiler
  never emits a copy (it currently does not for `Ptr` values).
- Add a test: mutate via `r1`, read via `r2`; check whether the read sees the mutation.

**Warning signs:**
- Mutation via one record binding is invisible through another binding that should alias it
  (reference semantics expected but copy emitted)
- Mutation via one record binding unexpectedly changes a "different" binding (copy semantics
  expected but alias emitted)

**Phase to address:** Mutable record codegen phase, semantics design step.

---

### Pitfall M-13: Copy-and-Update Expression Shallow-Copies Only — Nested Mutable Records Not Duplicated

**What goes wrong:**
Copy-and-update syntax `{ r with field = newVal }` allocates a new record block, copies all
fields from `r`, then overwrites the specified field. If `r` has a field of record type
(pointer to another GC block), the copy copies the pointer, not the inner record. After the
copy-and-update, the new record and the old record share the inner mutable record. A mutation
to the inner record via the old record is visible via the new record.

**Why it happens:**
The natural implementation is a shallow copy (`GC_malloc` + field-by-field copy of pointer
values). This is correct for immutable inner records but wrong for mutable inner records if
the source language intends deep copy semantics on copy-and-update.

**How to avoid:**
- Decide the copy depth policy: OCaml and F# use shallow copy for record update. Deep copy
  is expensive and not the convention.
- Document: copy-and-update is a shallow copy; nested mutable records are aliased.
- Add a test that demonstrates the aliasing to prevent accidental "fixing" it in a way that
  breaks the expected semantics.
- If deep copy is desired, it must be explicit in the language (a `deepcopy` function), not
  implicit in the copy-and-update syntax.

**Warning signs:**
- Test case `let r2 = { r1 with x = 5 }; mutate r2.inner; check r1.inner` shows r1.inner
  was changed unexpectedly

**Phase to address:** Mutable record codegen phase.

---

### Pitfall M-14: `ConstructorPat` Not Added to `CtorTag` — Pattern Compiler Silently Falls Through

**What goes wrong:**
The existing `MatchCompiler.fs` `CtorTag` DU has cases for `IntLit`, `BoolLit`,
`StringLit`, `ConsCtor`, `NilCtor`, `TupleCtor`. The `compilePatterns` function and the
`CtorTag` equality checks implicitly assume all tags are from this set. Adding ADT
constructors requires adding `UserCtor of ...` to `CtorTag` and updating every pattern
match on `CtorTag` in `MatchCompiler.fs` and `Elaboration.fs`. Missing even one match case
causes the F# compiler to warn, but if the codebase uses catch-all `| _` arms (see the
`failwithf "testPattern: pattern %A not supported in v2"` fallthrough), ADT patterns will
be silently ignored or raise a confusing internal error.

**How to avoid:**
- Remove all `| _ -> failwithf` catch-alls in `CtorTag` match expressions after adding
  `UserCtor`. Replace with explicit cases so the F# compiler reports exhaustiveness warnings.
- Add `UserCtor` to `ctorArity`, `ctorTag equality`, and `Elaboration.testPattern` in the
  same commit to avoid intermediate states where one function handles `UserCtor` but another
  does not.
- Write a test that uses every constructor of a two-variant ADT in a match expression before
  marking the phase complete.

**Warning signs:**
- F# compiler warns about incomplete pattern matches after adding `UserCtor`
- Pattern match on an ADT raises `System.Exception: testPattern: pattern UserCtor not
  supported` at compile time

**Phase to address:** ADT pattern matching phase. Update `MatchCompiler.fs` and
`Elaboration.fs` together in one step.

---

### Pitfall M-15: `volatile` Not Used for Variables Modified Between `setjmp` and `longjmp`

**What goes wrong:**
The C standard (and LLVM's optimizer) only guarantee that variables modified between the
`setjmp` call and a `longjmp` are visible in the handler if they are declared `volatile`.
Any non-volatile automatic variable that is written after `setjmp` and read in the handler
may have its write optimized away (it may be held in a register that `longjmp` restores to
the value from `setjmp` time).

For LangBackend: the variable that holds the raised exception value (typically a `void*` or
`ptr` pointing to the exception object) must be `volatile` if it is written after `setjmp`
and read after `longjmp` returns.

**How to avoid:**
- In `runtime.c`, declare the current exception value pointer as
  `volatile void* lang_current_exception;`
- Any local variable in a C wrapper that is written between `setjmp` and `longjmp` and then
  read in the handler path must be `volatile`.
- Add a comment at each `volatile` declaration explaining which rule requires it.
- Test at `-O2` explicitly: compile a test that sets `lang_current_exception` then calls
  `longjmp`; verify the handler reads the correct pointer.

**Warning signs:**
- Exception handler receives a null or garbage exception value at `-O2`
- Exception value is correct at `-O0` but wrong at `-O1` or `-O2`

**Phase to address:** Exception handling phase, runtime.c implementation. Apply at the same
time as the `returns_twice` fix (Pitfall C-15).

---

### Pitfall M-16: Nested `try-with` Allocates `jmp_buf` on GC Heap — False Pointer Retention

**What goes wrong:**
If `jmp_buf` structs are allocated via `GC_malloc` (to avoid thinking about their lifetime),
each try-with block allocates a GC-managed buffer that contains register values including
stack pointers and instruction pointers. Boehm GC will scan this buffer conservatively and
may find values that look like heap addresses, retaining live objects that should be
collectible. More importantly, after `longjmp` restores from the buffer, the `jmp_buf` block
is no longer needed but still holds register-shaped values — more false roots.

**How to avoid:**
- Allocate `jmp_buf` on the C stack (local variable of the function that calls `setjmp`).
  This is the correct, standard approach. The `jmp_buf` is valid as long as the function
  has not returned.
- If a `try-with` must be compiled in a context where the `jmp_buf` lifetime is ambiguous
  (e.g., inside a closure that may outlive its defining frame), use a global or thread-local
  handler stack with stack-allocated nodes — do not put the `jmp_buf` itself on the GC heap.
- Use `GC_malloc_atomic` if a `jmp_buf` must be heap-allocated for any reason (e.g., in a
  test harness): atomic allocation prevents GC scanning of the buffer contents.

**Warning signs:**
- Heap growth proportional to try-with nesting depth
- `GC_PRINT_STATS=1` shows many 200-byte blocks (typical `jmp_buf` size) accumulating

**Phase to address:** Exception handling phase. Decided at the time of choosing
`jmp_buf` allocation strategy.

---


## Phase-Specific Warnings

| Phase Topic | Likely Pitfall | Mitigation |
|---|---|---|
| GC integration — first task | C-8: GC_INIT omitted or too late | Emit `@GC_INIT()` as first instruction in @main; also m-5 |
| GC integration — allocation | C-9: malloc/GC_malloc mixing | Only GC_malloc in generated code; use GC_malloc_atomic for byte arrays |
| Migrating v1 closures to heap | C-7: stack-allocated closure escapes | Replace LlvmAllocaOp with GC_malloc call in closure-maker; do this before list codegen |
| String codegen | M-8: missing length header | Struct `{length, data*}` before any string literal emission; m-6: raw i8* vs header |
| String codegen | M-9: integer false pointer retention | Use GC_malloc_atomic for string byte data |
| Tuple codegen | M-10: pattern matrix decomposition | Decompose tuple pattern into sub-columns before building decision tree |
| Tuple codegen | M-11: size calculation wrong | Named size constants; unit test writes all fields |
| List (cons cell) codegen | M-7: nil == integer 0 tag collision | Use typed SSA values (Ptr vs I64 never mixed in same slot) |
| List codegen | M-11: cons cell size | Tag word + head + tail = 3 * 8 bytes |
| Pattern matching — any type | C-10: missing default arm | Emit @lang_match_failure fallback before any match codegen |
| Pattern matching — tuples | M-10: tuple not decomposed | Full Maranget specialization; test wildcards in one component |
| Pattern matching — lists | M-7: nil check via pointer comparison | Header-word tag check, not pointer-to-zero check |
| Any new heap allocation | M-9: false pointer retention | Use GC_malloc_atomic for pure-byte regions |
| MLIR → LLVM lowering (v2) | C-3: new dialects introduce unrealized casts | Re-check IR after pipeline whenever a new MlirOp case is added |
| P/Invoke infrastructure | M-6: pointer size truncation in struct wrappers | Use `nativeint` everywhere; add startup smoke test |
| Context/Module lifecycle | C-2: context destroyed before module | `CompilerSession` wrapper with enforced destruction order |
| First MLIR operation emission | C-1: region handle used after ownership transfer | Ownership-tracking wrapper type |
| AST annotation | C-6: missing free-variable and type annotation | TypedExpr lowering pass before any MLIR emission |
| ADT constructor representation | C-11: tag at field 0 vs existing GEP conventions | Separate ADT codegen path; tag @offset0, payload @offset1+ |
| ADT constant constructors | C-12: GC_malloc on every evaluation | Integer encoding for zero-arity ctors; Ptr only for payload-bearing ctors |
| GADT codegen | C-13: payload loaded with wrong MlirType | Thread type annotations through ConstructorPat before codegen |
| Exception handling — setjmp wrapper | C-15: returns_twice missing | Use `#define lang_setjmp setjmp` or `__attribute__((returns_twice))`; test at -O2 |
| Exception handling — handler stack | C-16: double-exception jmp_buf corruption | Pop handler before executing handler body; test nested raises |
| Exception handling — GC interaction | C-14: stale roots after longjmp | Call GC_collect_a_little after longjmp returns; null out ptrs in handler |
| Mutable record codegen | M-12: aliasing vs copy semantics | Decide semantics first; add explicit copy if needed |
| Mutable record copy-and-update | M-13: shallow copy shares inner records | Document and test shallow-copy behavior |
| Pattern matching — ADT ctors | M-14: CtorTag not extended | Add UserCtor to CtorTag and update all match sites in same commit |
| Exception volatile variables | M-15: exception value clobbered by optimizer | `volatile` on lang_current_exception; regression test at -O2 |
| Nested try-with jmp_buf allocation | M-16: GC_malloc'd jmp_buf scanned as roots | Stack-allocate jmp_buf; GC_malloc_atomic if heap allocation required |

---

## Sources

- [Boehm GC — Conservative GC Algorithmic Overview](https://www.hboehm.info/gc/gcdescr.html)
- [Boehm GC — Why Conservative GC?](https://www.hboehm.info/gc/conservative.html)
- [Boehm GC — Debugging](https://hboehm.info/gc/debugging.html)
- [Boehm GC — Porting Directions](https://hboehm.info/gc/porting.html)
- [bdwgc — GitHub repository](https://github.com/bdwgc/bdwgc)
- [Nim issue: Boehm GC does not scan thread-local storage](https://github.com/nim-lang/Nim/issues/14364)
- [Nim issue: Boehm disables interior pointer checking](https://github.com/nim-lang/Nim/issues/12286)
- [OCaml Runtime Memory Layout — Real World OCaml](https://dev.realworldocaml.org/runtime-memory-layout.html)
- [OCaml Memory Representation of Values — Official Docs](https://ocaml.org/docs/memory-representation)
- [Colin James — Compiling Pattern Matching](https://compiler.club/compiling-pattern-matching/)
- [Maranget — Compiling Pattern Matching to Good Decision Trees (ML 2008)](http://moscova.inria.fr/~maranget/papers/ml05e-maranget.pdf)
- [Jules Jacobs — How to compile pattern matching](https://julesjacobs.com/notes/patternmatching/patternmatching.pdf)
- [MLIR C API — Official Documentation](https://mlir.llvm.org/docs/CAPI/)
- [Dialect Conversion — MLIR Official](https://mlir.llvm.org/docs/DialectConversion/)
- [LLVM IR Target — MLIR](https://mlir.llvm.org/docs/TargetLLVMIR/)
- [MLIR Tutorial Chapter 5 — Partial Lowering](https://mlir.llvm.org/docs/Tutorials/Toy/Ch-5/)
- [MLIR Tutorial Chapter 6 — Lowering to LLVM](https://mlir.llvm.org/docs/Tutorials/Toy/Ch-6/)
- [How to avoid persistent unrealized_conversion_casts — LLVM Discourse](https://discourse.llvm.org/t/how-to-avoid-persistent-unrealized-conversion-cast-s-when-converting-dialects/71721)
- [LLVM Garbage Collection — Official Docs](https://llvm.org/docs/GarbageCollection.html)
- [Go closure escape analysis — DEV Community](https://dev.to/imzihad21/go-008-closures-escape-analysis-in-action-3gjb)
- [OCaml Memory Representation of Values — Real World OCaml](https://dev.realworldocaml.org/runtime-memory-layout.html)
- [OCaml GADT Tutorial — Official Manual](https://ocaml.org/manual/5.2/gadts-tutorial.html)
- [setjmp/longjmp and Exception Handling in C — DEV Community](https://dev.to/pauljlucas/setjmp-longjmp-and-exception-handling-in-c-1h7h)
- [SEI CERT C — MSC22-C: Use setjmp/longjmp facility securely](https://wiki.sei.cmu.edu/confluence/display/c/MSC22-C.+Use+the+setjmp%28%29%2C+longjmp%28%29+facility+securely)
- [Setjmp/Longjmp Exception Handling — Mapping High Level Constructs to LLVM IR](https://mapping-high-level-constructs-to-llvm-ir.readthedocs.io/en/latest/exception-handling/setjmp+longjmp-exception-handling.html)
- [LLVM Exception Handling — Official Documentation](https://llvm.org/docs/ExceptionHandling.html)
- [Avoid LLVM setjmp bug — Julia Discourse](https://discourse.julialang.org/t/avoid-llvm-setjmp-bug/1140)
- [longjmp — cppreference.com](https://en.cppreference.com/w/c/program/longjmp)
- [Conservative GC Algorithmic Overview — hboehm.info](https://www.hboehm.info/gc/gcdescr.html)
- [Boehm GC Porting Directions — stack scanning](https://hboehm.info/gc/porting.html)
- [Mutable Record Fields — OCaml Programming: Correct + Efficient + Beautiful](https://cs3110.github.io/textbook/chapters/mut/mutable_fields.html)
