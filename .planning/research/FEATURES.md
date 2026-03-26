# Feature Research

**Domain:** ML-family functional language compiler backend — v4.0 ADT/GADT, Records, Exception Handling
**Researched:** 2026-03-26
**Confidence:** HIGH

---

## Scope Anchor

This file addresses the v4.0 milestone: adding ADT/GADT (discriminated unions), Records (with
mutable fields), and Exception handling to LangBackend, which already has int/bool/string/tuple/
list/pattern-matching/closures/char/range compiled to native code via MLIR→LLVM. The LangThree
AST already defines all target nodes. The work is entirely in the compiler backend.

**Existing foundation:**
- GC: Boehm GC (`GC_malloc`), uniform `ptr` representation for all heap values
- Pattern matching: Jacobs decision tree via `MatchCompiler.fs`, `cf.cond_br` chains
- Closure: `{fn_ptr, env}` struct; indirect calls via env+arg ABI
- String: `{i64 length, ptr data}` header struct; `strcmp` for equality
- Tuple: N-field GC_malloc'd ptr array, GEP by index
- List: null nil, `{head: ptr, tail: ptr}` cons cell

---

## Feature Landscape

### Table Stakes (Users Expect These)

Features that must work for v4.0 to be complete. Missing any of these means the milestone is
not done.

| Feature | Why Expected | Complexity | Notes |
|---------|--------------|------------|-------|
| ADT construction (`Constructor` node) | Discriminated unions are the core abstraction of ML-family languages; every non-trivial LangThree program uses them | MEDIUM | `DataValue(ctor, optArg)` → heap struct `{i64 tag, ptr payload}`; tag is the constructor index in declaration order; nullary constructors store `null` in payload |
| ADT pattern matching (`ConstructorPat`) | Pattern matching on ADTs is the primary way to inspect their values; without it ADTs are write-only | HIGH | Decision tree already handles `Switch(testVar, ctorName, argVars, ...)` — need to emit: load tag, compare tag against known index, GEP payload; MatchCompile.fs already encodes constructor names as the discriminator |
| `TypeDecl` elaboration | The compiler must register ADT constructor names and their tag indices at declaration time so that `Constructor(name, optArg)` and `ConstructorPat(name, optArg)` can look up the tag | MEDIUM | Walk `ConstructorDecl list` in declaration order, assign tag 0, 1, 2, … into an `AdtEnv` (Map from constructor name to tag index + arity); thread through `ElabEnv` |
| Nullary constructors (no payload) | Common in every ADT: `type Color = Red \| Green \| Blue`, `type Bool = True \| False` | LOW | `Constructor(name, None)` → allocate `{tag: i64, payload: ptr}`, store tag, store null payload; `ConstructorPat(name, None)` → test tag only, no payload load |
| Unary constructors (one payload arg) | Common: `type Option = None \| Some of 'a`, `type List = Nil \| Cons of 'a * List` | MEDIUM | `Constructor(name, Some arg)` → elaborate arg, store into payload field; `ConstructorPat(name, Some pat)` → load payload ptr, apply sub-pattern |
| Record construction (`RecordExpr`) | Records are the standard named-field product type; fundamental to modeling structured data | MEDIUM | `RecordExpr(_, fields, _)` → allocate `{ptr field_0, ptr field_1, ...}` struct indexed by sorted field order (fields are globally unique per RecordDecl); each field value is an elaborated ptr or i64 |
| Record field access (`FieldAccess`) | The primary way to read record fields | LOW | Requires field-name-to-index map from `RecordDecl`; emit GEP to field slot + load |
| Record copy-update (`RecordUpdate`) | Standard ML idiom: `{ r with field = newVal }`; avoids manual field extraction | MEDIUM | Allocate a new record struct, copy all fields from the source ptr via GEP+load+store, then overwrite the named fields with new values |
| Record patterns (`RecordPat`) | Pattern matching on records is the clean way to destructure them | MEDIUM | `RecordPat(fields)` → for each named field: GEP to field slot by index, load value, bind to pattern variable; unconditional match (no tag test needed) |
| Mutable field assignment (`SetField`) | `RecordFieldDecl` has `isMutable: bool`; `SetField(expr, fieldName, value)` mutates a field in-place | MEDIUM | GEP to field slot, elaborate new value, store; returns unit (0L as i64); this is the only mutation mechanism in LangThree records |
| Exception declaration (`ExceptionDecl`) | Exceptions are sugar over constructors with `ResultType = TExn`; declaration must create a named constructor | LOW | `ExceptionDecl(name, dataType, _)` → register as a special constructor in `AdtEnv` with a synthetic tag; the runtime exception object is `DataValue(name, optPayload)` wrapped in a `TExn`-typed pointer |
| `Raise` expression | The only way to signal an error; without it exceptions are useless | MEDIUM | `Raise(expr)` → elaborate the exception value (a `DataValue` ptr), call `@lang_raise` with that ptr; `@lang_raise` does `longjmp` to the nearest handler frame; elaborated result is an unreachable i64 zero (UB after noreturn call) |
| `TryWith` expression | The only way to catch an exception; handlers use the same `MatchClause` structure as `Match` | HIGH | `TryWith(body, handlers)` → emit `setjmp` to push a handler frame, evaluate body, on normal exit pop the frame and branch to merge; on exception signal (`longjmp`), receive the exception value from a thread-local slot, run pattern-matching dispatch on handlers using the same `MatchCompiler` decision tree; if no handler matches, re-raise |
| Exception equality in pattern matching | Handlers like `\| Failure msg -> ...` test the constructor name of the exception DataValue | MEDIUM | `ConstructorPat` on exception values uses the same tag-based matching as ADTs; the pattern dispatches via tag index in the handler switch |

### Differentiators (Competitive Advantage)

Features beyond table stakes. These improve correctness, ergonomics, or performance — and some
are effectively required for non-trivial programs even if not labeled "table stakes."

| Feature | Value Proposition | Complexity | Notes |
|---------|-------------------|------------|-------|
| `GadtConstructorDecl` support | LangThree's type system already encodes GADTs; compiling them enables programs that use typed index types | MEDIUM | GADT constructors have explicit `argTypes` and `returnType`; at the compiled value level they are identical to normal ADT constructors (`{tag, payload}`); the type system has already checked soundness; no new runtime representation needed |
| Recursive ADT types (e.g. `type Tree = Leaf \| Node of Tree * int * Tree`) | Recursive structures are the primary use case for ADTs | MEDIUM | Already handled: the `payload` field is a `ptr` to another GC-managed heap object; no layout change needed; the `AdtEnv` must accept forward references within a type block |
| Polymorphic ADTs (`type 'a Option = None \| Some of 'a`) | The most common use case — `Option`, `Result`, `List` | LOW | At the compiled level, `'a` is a `ptr` (uniform boxing); no monomorphization needed; the constructor tag+payload layout works for any `'a` |
| Multiple constructors per ADT (> 2) | Realistic ADTs have many constructors: `type Shape = Circle of float \| Rect of float * float \| Triangle of ...` | LOW | The tag is an `i64` index; `ConstructorPat` tests emit `arith.cmpi eq` against the constructor's tag index; `cf.cond_br` chain proceeds through all handler arms (same as existing int/bool patterns) |
| Nested ADT pattern matching | `match t with \| Node(Node(_, v, _), _, _) -> v \| ...` — nested constructor patterns | HIGH | The decision tree already supports nested sub-patterns via `Switch` recursion and `argVars` expansion; but the implementation of loading the payload from a nested ADT constructor requires recursive GEP+load chains for each level |
| Exception re-raise on handler miss | If no handler clause matches, the exception propagates upward | MEDIUM | After the handler decision tree reaches `Fail`, call `@lang_raise` again with the original exception value from the thread-local slot; this enables proper exception propagation through nested `TryWith` frames |
| `setjmp`/`longjmp` stack discipline | The handler frame stack must be a linked list (or array) on the C stack, not a global; each `TryWith` pushes/pops its own frame | MEDIUM | Standard implementation: `struct ExnFrame { jmp_buf buf; ExnFrame* prev; };` allocated via `alloca` (or GC_malloc) in each `TryWith` scope; a global/TLS pointer `__lang_exn_top` tracks the current handler; `@lang_raise` walks the frame linked list via `longjmp` to the nearest frame |
| Record field ordering stability | Field access by name must produce the same GEP index across all compilation units | LOW | Sort fields alphabetically (or by declaration order) and store the mapping in `RecordEnv`; declaration order is simpler and matches the AST |
| ADT constructor as first-class value | `let f = Some` — constructor used as a function | MEDIUM | Wrap each unary constructor in a single-argument lambda at elaboration time; nullary constructors elaborate to the DataValue directly; this allows higher-order use of constructors without special AST support |

### Anti-Features (Commonly Requested, Often Problematic)

Features to deliberately NOT build in v4.0.

| Feature | Why Requested | Why Problematic | Alternative |
|---------|---------------|-----------------|-------------|
| Unboxed/specialized ADT representation | Avoids the `{tag, payload}` indirection for known-small types like `Option<int>` | Requires monomorphization infrastructure (a multi-phase project); breaks the uniform `ptr` representation that GC and closures rely on | Stay with uniform `{tag: i64, payload: ptr}`; 16 bytes per ADT value is acceptable; defer unboxed optimization to v5+ |
| C++ exception ABI (`_Unwind_RaiseException`) | Interop with C++ exception-safe libraries | Requires linking `libstdc++` or `libc++`; the unwind ABI is platform-specific and fragile with Boehm GC | Use `setjmp`/`longjmp`; this is what OCaml does and it interoperates with Boehm GC correctly |
| Exception stack traces / backtraces | Developers want to know where an exception was raised | Requires walking the call stack at `Raise` time (DWARF unwinding or frame pointer chains); significant runtime infrastructure | For v4, store only the exception value (constructor + payload) — no stack trace. Add stack traces in v5+ if needed |
| Multi-payload constructors (e.g. `Cons of int * int * int`) | Syntactically natural in some languages | LangThree's `ConstructorDecl` only carries one `TypeExpr option` payload; multi-field constructors require tuple wrapping: `Cons of (int * int * int)` | Use a tuple as the payload: `Constructor("Cons", Some (Tuple [a; b; c]))` — this is already expressible and requires no new compiler support |
| Open/extensible exception types | F#-style extensible discriminated unions via `exception` extending a base type | Requires a global exception registry or a pointer-equality scheme (OCaml's extensible variants); significantly complicates the tag system | All exceptions in LangThree are declared up-front with `ExceptionDecl`; tag assignment at declaration time is sound and closed |
| Mutable record field through closures (captured `ref`-style) | Simulating ML-style `ref` cells via single-field mutable records | Works already via `SetField` — no special support needed; the anti-feature is implementing a separate `ref` type | Just use `type Ref = { mutable value: int }` and `SetField`; the ADT+Record system covers this |
| Module-level global mutable state via records | A record at the top level acts like a singleton mutable object | LangThree programs are expressions, not statement sequences; module-level records can be declared but mutation requires threading the record pointer | Use `let r = { mutable x = 0 }` in the top-level expression body — mutation works correctly inside an expression |
| Printf-style format strings for exceptions | `raise (Failure (sprintf "expected %d got %d" ...))` | `sprintf` is already out of scope for v4 (deferred to v5) | Use `string_concat` or `to_string` for exception message construction |

---

## Feature Dependencies

```
AdtEnv (constructor-name → tag index + arity mapping)
    └──required by──> Constructor(name, optArg) elaboration
    └──required by──> ConstructorPat(name, optPat) elaboration
    └──required by──> TypeDecl declaration processing

Constructor(name, optArg) elaboration
    └──requires──> GC_malloc (already done)
    └──requires──> LlvmStoreOp, LlvmGEPStructOp (already done)
    └──produces──> DataValue heap struct {i64 tag, ptr payload}

ConstructorPat(name, optPat) elaboration
    └──requires──> Constructor elaboration (to know what tag values mean)
    └──requires──> existing cf.cond_br pattern dispatch (already done)
    └──requires──> existing decision tree (MatchCompile.fs — already handles Switch on ctor names)

RecordEnv (field-name → field index mapping)
    └──required by──> RecordExpr elaboration
    └──required by──> FieldAccess elaboration
    └──required by──> RecordUpdate elaboration
    └──required by──> SetField elaboration
    └──required by──> RecordPat elaboration

RecordExpr elaboration
    └──requires──> GC_malloc (already done)
    └──requires──> RecordEnv

FieldAccess elaboration
    └──requires──> RecordEnv
    └──requires──> LlvmGEPLinearOp + LlvmLoadOp (already done)

RecordUpdate elaboration
    └──requires──> FieldAccess (copy all fields from source)
    └──requires──> RecordExpr (allocate new struct)

SetField elaboration
    └──requires──> RecordEnv (GEP to mutable field slot)
    └──requires──> LlvmStoreOp (already done)

RecordPat elaboration
    └──requires──> RecordEnv
    └──requires──> existing testPattern dispatch (already done for TuplePat)
    └──enhances──> ADT pattern matching (records appear as constructor payloads)

ExceptionDecl elaboration
    └──requires──> AdtEnv (exceptions are constructors under TExn type)

Raise elaboration
    └──requires──> ExceptionDecl (to produce a valid exception DataValue)
    └──requires──> setjmp/longjmp C runtime support (@lang_raise)
    └──requires──> Constructor elaboration (exception value is a DataValue)

TryWith elaboration
    └──requires──> Raise elaboration (@lang_raise mechanism)
    └──requires──> setjmp/longjmp frame protocol (@lang_push_handler, @lang_pop_handler)
    └──requires──> ConstructorPat on exception values (handler dispatch)
    └──requires──> existing decision tree for handler clause matching

Exception handler dispatch (pattern matching on handlers)
    └──requires──> ConstructorPat elaboration (ADT pattern matching)
    └──requires──> existing MatchCompiler decision tree
    └──enhances──> TryWith (handlers are MatchClause list, same as Match)
```

### Dependency Notes

- **ADT before Records:** Records can be implemented independently, but ADT tagging infrastructure
  (`AdtEnv`) is needed before Exceptions, since exception objects are DataValues.
- **Raise before TryWith:** `@lang_raise` (longjmp) must be implemented before `TryWith` (setjmp)
  can be tested, since TryWith without Raise is untestable.
- **ConstructorPat requires AdtEnv:** The tag index for each constructor must be known at
  pattern-matching elaboration time. The `AdtEnv` must be populated during `TypeDecl` processing
  before any expression that uses the constructors.
- **RecordPat is structurally identical to TuplePat:** Both are unconditional destructures by
  index. The only difference is that RecordPat looks up index by field name instead of position.
  This means RecordPat can be implemented by reusing `testPattern`'s TuplePat path with a
  field-name→index indirection.
- **SetField does not require new MlirOp:** It uses the existing `LlvmGEPLinearOp` + `LlvmStoreOp`
  combination. The only new work is resolving the field name to an index.

---

## MVP Definition

### Launch With — v4.0

Minimum set for the milestone to be declared complete.

- [ ] `TypeDecl` declaration processing populates `AdtEnv` with constructor→tag index mapping
- [ ] `Constructor(name, None)` — nullary constructor elaboration to `{tag, null_ptr}` struct
- [ ] `Constructor(name, Some arg)` — unary constructor elaboration to `{tag, payload_ptr}` struct
- [ ] `ConstructorPat(name, None)` — nullary pattern: test tag, no payload extraction
- [ ] `ConstructorPat(name, Some pat)` — unary pattern: test tag, load payload, apply sub-pattern
- [ ] `RecordDecl` processing populates `RecordEnv` with field→index mapping
- [ ] `RecordExpr` — record construction to GC_malloc'd field array
- [ ] `FieldAccess` — field read via GEP + load using `RecordEnv`
- [ ] `RecordUpdate` — copy-update allocation (new struct + field overwrite)
- [ ] `SetField` — mutable field in-place store via GEP + store
- [ ] `RecordPat` — record destructure in pattern matching
- [ ] `ExceptionDecl` — registers exception constructor in `AdtEnv` under TExn namespace
- [ ] `Raise` — elaborates exception value + calls `@lang_raise` (longjmp-based)
- [ ] `TryWith` — emits `setjmp` frame + handler decision tree + merge block

### Add After Validation — v4.x

Features to add once the core is working.

- [ ] ADT constructor as first-class value (wrap in lambda) — needed once curried constructors appear in test programs
- [ ] Exception re-raise on handler miss — needed for `TryWith` in non-exhaustive handler programs
- [ ] Nested ADT pattern matching (multi-level constructor patterns) — add when test programs require it

### Future Consideration — v5+

Features to defer.

- [ ] GADT refined type checking at compile time — type system already checked by LangThree frontend; compiler just emits the same `{tag, payload}` struct
- [ ] Stack traces on exceptions — requires DWARF or frame pointer walking
- [ ] Printf/sprintf for exception messages — `sprintf` is separately deferred
- [ ] Unboxed ADT specialization — requires monomorphization

---

## Feature Prioritization Matrix

| Feature | User Value | Implementation Cost | Priority |
|---------|------------|---------------------|----------|
| ADT construction + ConstructorPat | HIGH | MEDIUM | P1 |
| TypeDecl/AdtEnv registration | HIGH | LOW | P1 |
| RecordExpr + FieldAccess | HIGH | MEDIUM | P1 |
| RecordUpdate | HIGH | LOW | P1 (reuses RecordExpr + FieldAccess) |
| SetField (mutable fields) | MEDIUM | LOW | P1 (GEP+store, no new IR) |
| RecordPat | MEDIUM | LOW | P1 (reuses TuplePat path) |
| ExceptionDecl | MEDIUM | LOW | P1 (trivial AdtEnv registration) |
| Raise | HIGH | MEDIUM | P1 |
| TryWith | HIGH | HIGH | P1 |
| Nested ADT patterns | MEDIUM | HIGH | P2 |
| ADT constructor as first-class value | MEDIUM | MEDIUM | P2 |
| Exception re-raise propagation | MEDIUM | MEDIUM | P2 |
| GADT compiled support | LOW | LOW | P3 (already works at value level) |
| Unboxed ADT | LOW | HIGH | P3 |

**Priority key:**
- P1: Must have for v4.0 launch
- P2: Should have, add when core is working
- P3: Nice to have, future milestone

---

## Reference Implementation Notes

### ADT Heap Layout

Every ADT value (including exceptions) uses a uniform two-field struct:

```
+--------+---------+
| tag    | payload |
+--------+---------+
  i64      ptr
```

- `tag`: constructor index in declaration order (0-based); stored as `i64` for uniform alignment
- `payload`: pointer to the argument value (another GC-managed heap object), or `null` for nullary
- Total size: 16 bytes (2 × 8-byte words)
- Allocated with `GC_malloc(16)`

This is consistent with the existing uniform `ptr` representation: an ADT value is a `ptr` to
this 16-byte struct.

Example for `type Option = None | Some of int`:
- `None` → `{tag=0, payload=null}`
- `Some 42` → `{tag=1, payload=ptr_to_boxed_42}` where the boxed int is a separate GC_malloc'd i64

### Record Heap Layout

Records are N-field pointer arrays, structurally identical to tuples but with named access:

```
+----------+----------+----------+
| field_0  | field_1  | field_2  |
+----------+----------+----------+
   ptr        ptr        ptr
```

- Fields are indexed in declaration order (0-based), stored in `RecordEnv`
- Each field slot holds a `ptr` (uniform boxing: integers are boxed if stored in record fields)
- Mutable fields: `SetField` GEPs to the field slot and stores a new value in-place
- Total size: `N * 8` bytes

The existing `LlvmGEPLinearOp` + `LlvmStoreOp`/`LlvmLoadOp` already handles this layout.

### Exception Runtime Protocol

The exception mechanism uses `setjmp`/`longjmp` with a thread-local linked list of handler frames.
This is the OCaml model and works with Boehm GC (conservative, no stack walk needed).

Relevant C runtime helpers (to be implemented in `runtime.c` alongside existing string/GC helpers):

```c
// Opaque exception frame — one per TryWith on the call stack
typedef struct LangExnFrame {
    jmp_buf buf;
    struct LangExnFrame *prev;
} LangExnFrame;

// Thread-local pointer to current innermost handler
extern __thread LangExnFrame *__lang_exn_top;

// Exception value slot (set before longjmp, read after setjmp returns nonzero)
extern __thread void *__lang_exn_value;

// Called by Raise: stores exn value, unwinds to nearest handler
void lang_raise(void *exn_value);  // noreturn

// Called at TryWith entry: push frame
void lang_push_handler(LangExnFrame *frame);

// Called at TryWith exit (normal): pop frame
void lang_pop_handler(void);
```

MLIR elaboration for `TryWith(body, handlers)`:

1. `alloca` a `LangExnFrame` (or `GC_malloc` — alloca is simpler and correct here)
2. Call `@lang_push_handler` with frame ptr
3. Emit `setjmp` call: `i32 @setjmp(ptr %frame_buf_ptr)`
4. `cf.cond_br` on setjmp result: `0 → body_block`, `nonzero → handler_block`
5. `body_block`: elaborate body; on normal fall-through call `@lang_pop_handler`, branch to merge
6. `handler_block`: load `__lang_exn_value` (the exception DataValue ptr), run handler decision tree
7. `merge_block`: phi node for result value from body or handler

This requires a new `LlvmCallOp` variant for `@setjmp` (which returns `i32`, not `ptr`) — the
existing `LlvmCallOp of result: MlirValue * callee * args` already supports any return type based
on `result.Type`.

---

## Comparison: ML-Family Exception Implementations

| Approach | Used By | GC Compatible | Stack Cost | Complexity |
|----------|---------|--------------|------------|------------|
| `setjmp`/`longjmp` | OCaml (legacy), LangThree v4 | Yes (conservative GC needs no unwind info) | ~200 bytes/frame | LOW |
| Zero-cost unwind (`_Unwind_RaiseException`) | GHC, modern OCaml 5 | Requires precise GC or root enumeration | ~0 on happy path | VERY HIGH |
| Result type (`Ok`/`Err`) | Rust, Haskell | N/A (no runtime exceptions) | None | N/A (language design choice) |

**Recommendation for v4.0:** `setjmp`/`longjmp`. It is correct, simple, and matches how OCaml
handled exceptions before OCaml 5's effects system. The Boehm GC is conservative so there is no
stack-walking requirement. The implementation is entirely in `runtime.c` — the compiler only needs
to emit `setjmp` + branch + `lang_push/pop_handler` calls.

---

## Sources

- LangThree AST: `../LangThree/src/LangThree/Ast.fs` — direct inspection, HIGH confidence
  (TypeDecl, ConstructorDecl, GadtConstructorDecl, RecordDecl, RecordFieldDecl, all Expr nodes)
- LangThree MatchCompile: `../LangThree/src/LangThree/MatchCompile.fs` — direct inspection, HIGH
  confidence (Switch uses ctorName strings; RecordPat already handled in patternToConstructor)
- LangBackend Elaboration.fs — direct inspection of existing testPattern/elaborateExpr patterns
- LangBackend MlirIR.fs — direct inspection of existing op set (LlvmGEPStructOp, LlvmStoreOp, etc.)
- LangBackend PROJECT.md — v4.0 milestone goals and pending decisions
- OCaml exception implementation: `setjmp`/`longjmp` + `caml_exn_bucket` thread-local model
  (OCaml runtime source: `ocaml/runtime/sys.c`, `ocaml/runtime/fail.c`)
- "Tag-based discriminated union layout" — standard across GHC (info tables), OCaml (block tag),
  MLton (tag word); v4 uses the simplest form: explicit `{i64 tag, ptr payload}`
- Boehm GC + setjmp interaction: conservative GC scans `jmp_buf` as potential roots automatically;
  no explicit root registration needed (this is a documented property of Boehm GC)

---
*Feature research for: LangBackend v4.0 — ADT/GADT, Records, Exception Handling*
*Researched: 2026-03-26*
