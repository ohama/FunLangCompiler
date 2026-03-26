# Feature Landscape

**Domain:** Compiled ML-style functional language — v2.0 Data Types & Pattern Matching
**Researched:** 2026-03-26
**Confidence:** HIGH

---

## Scope Anchor

This file addresses the v2.0 milestone: adding GC runtime, strings, tuples, lists, and
pattern matching to the existing LangBackend compiler. v1 already handles int, bool,
arithmetic, comparisons, logical ops, if-else, let, let rec, lambda, closures, and the
CLI. The LangThree AST already defines all target nodes — the work is entirely in the
compiler backend (Elaboration, MlirIR, Printer).

---

## What the LangThree AST Defines (Authoritative Source)

Read from `../LangThree/src/LangThree/Ast.fs` and `Eval.fs` (direct inspection).

### Values already in the runtime model

| Value | AST Node | Eval representation |
|-------|----------|---------------------|
| String | `String of string * Span` | `StringValue of string` |
| Char | `Char of char * Span` | `CharValue of char` |
| Tuple | `Tuple of Expr list * Span` | `TupleValue of Value list` |
| Empty list | `EmptyList of Span` | `ListValue []` |
| List literal | `List of Expr list * Span` | `ListValue of Value list` |
| Cons | `Cons of Expr * Expr * Span` | `ListValue (h :: t)` |

### Patterns already in the runtime model

| Pattern | AST Node |
|---------|----------|
| Variable | `VarPat of string * Span` |
| Wildcard | `WildcardPat of Span` |
| Tuple destructure | `TuplePat of Pattern list * Span` |
| List empty | `EmptyListPat of Span` |
| List cons | `ConsPat of Pattern * Pattern * Span` |
| Constant | `ConstPat of Constant * Span` (int, bool, string, char) |
| Or-pattern | `OrPat of Pattern list * Span` |

### Match expression

`Match of scrutinee: Expr * clauses: MatchClause list * Span`
where `MatchClause = Pattern * Expr option * Expr` (pattern, optional when-guard, body).

LangThree's evaluator compiles pattern matching to a decision tree via `MatchCompile.fs`
(Jacobs-style algorithm). The compiler can reuse this tree structure directly.

### String builtins already in Eval.initialBuiltinEnv

`string_length`, `string_concat`, `string_sub`, `string_contains`, `to_string`,
`string_to_int`, `print`, `println`, `printf`, `printfn`, `sprintf`, `failwith`.

These need corresponding compiled implementations — either inlined as LLVM/libc calls or
as a compiled stdlib. The compiler must emit correct ABI-compatible code that matches what
the interpreter treats as built-in.

---

## Table Stakes

Features users expect. Missing any of these means v2.0 is incomplete for its stated scope.

| Feature | Why Expected | Complexity | Runtime Requirement |
|---------|--------------|------------|---------------------|
| GC integration (Boehm GC) | All heap-allocated types (string, tuple, list) require automatic memory management; without GC, every allocation leaks | High | `GC_malloc` replaces `malloc` everywhere; link `-lgc` or `-l:libgc.a`; requires `GC_INIT()` call at program start |
| String literals | `String` AST node is a fundamental LangThree value; programs cannot do I/O without strings | Medium | Heap-allocated null-terminated C string (GC_malloc'd), represented as `!llvm.ptr` |
| String equality (= and <>) | Pattern matching on string constants requires structural equality | Medium | `strcmp` from libc; `arith.cmpi eq, i32, i32` on result |
| String concatenation (+ operator) | Already works in the interpreter; `Add` on `StringValue` dispatches to concat | Medium | Allocate new string, `memcpy` both halves; depends on GC |
| Tuple construction | `Tuple` AST node; needed for let-pattern and match | Medium | GC_malloc'd struct of N pointers (boxed values); tag first word with arity |
| Tuple destructure (let pat and match) | `TuplePat` is the primary way to extract tuple components | Medium | GEP into tuple struct by index |
| List empty `[]` | Required for any list program | Low | Null pointer or singleton sentinel; `!llvm.ptr` null |
| List cons `h :: t` | `Cons` AST node; fundamental list construction | Medium | GC_malloc'd two-word struct: `{head: ptr, tail: ptr}` (cons cell) |
| List literal `[e1; e2; ...]` | Sugar for nested cons; already in `List` AST node | Low | Desugar to repeated cons; no new IR nodes needed |
| Pattern matching on lists ([] and h::t) | The primary way to process lists in ML-style code | High | Decision tree compilation: test head pointer for null (empty) vs non-null (cons) |
| Pattern matching on tuples | Tuple destructuring via `match` | Medium | GEP by component index after arity check |
| Pattern matching on constants (int, bool) | `ConstPat` for integers and booleans; already partially handled via comparison ops | Low | `arith.cmpi eq` against constant value |
| Pattern matching on string constants | `ConstPat (StringConst s, _)` — common in dispatch-style code | Medium | `strcmp` call to test equality |
| Wildcard and variable patterns | Universal in any real match expression | Low | No test needed; variable patterns just bind the scrutinee value |
| Non-exhaustive match failure at runtime | The interpreter raises `"Match failure: no pattern matched"`; the compiled program must also fail usefully | Low | Call `abort()` or `exit(1)` with a printed error |
| `print` / `println` builtins | Required for any program to produce output | Low | `printf("%s", str)` / `printf("%s\n", str)` via libc; already linked via `clang` |
| `string_length` builtin | Fundamental string operation | Low | `strlen` from libc |
| `string_concat` builtin | Core string operation; also implied by `+` on strings | Low | Allocate + `memcpy` |
| `to_string` builtin | Converts int/bool to string; used in nearly every output program | Low | `sprintf` for int; conditional string for bool |

---

## Differentiators

Features that go beyond minimum viability for v2.0. Not required for correctness, but
meaningfully improve the compiler.

| Feature | Value Proposition | Complexity | Notes |
|---------|-------------------|------------|-------|
| Tagged value representation (uniform boxing) | Using a uniform `ptr` representation for all heap values enables polymorphic list elements, higher-order functions over mixed types, and future ADT support without changing the calling convention | High | Tag the low bits of a pointer with a type tag (standard in OCaml, GHC). Alternatively, use a `{tag: i64, payload: ptr}` header on every GC-allocated object. The tag approach is more efficient but requires alignment guarantees. |
| or-pattern compilation (`| P1 \| P2`) | `OrPat` is already in the AST. Compiling it avoids duplicating match arms in the emitted LLVM IR | Medium | Expand `OrPat` into multiple decision-tree branches that share the same leaf body (emit one basic block, branch from multiple test paths). |
| `when` guard compilation | Guards are already in `MatchClause = Pattern * Expr option * Expr`. Compiling them is needed for programs with conditional matches | Medium | After pattern bindings are set up, evaluate guard expression as `i1`; if false, fall through to next clause. |
| String pattern matching via trie | For large numbers of string constant patterns, a character-by-character trie is more efficient than sequential `strcmp` calls | High | Not needed for v2; linear `strcmp` chain is correct and sufficient. Defer if string dispatch appears in hot paths. |
| `string_sub` / `string_contains` builtins | Used in string-processing programs | Low | `strncpy`-based substring; `strstr`-based contains. Mostly libc calls wrapped as builtins. |
| `sprintf` builtin | Needed for formatted string construction | Medium | Requires `asprintf` or a fixed-size buffer + `snprintf`; GC-managed result buffer. |
| `char` type and `char_to_int` / `int_to_char` | `CharValue` already exists in the interpreter | Low | A `char` is just `i8` in LLVM; `char_to_int` is a zero-extend, `int_to_char` is a trunc + range check. |
| GC_malloc with size-of-type calculation | The compiler statically knows the layout of every heap object; using precise sizing prevents wasted GC scan time | Medium | Compute size as `num_fields * sizeof(ptr)` at codegen time; emit `GC_malloc(constant_size)`. This is straightforward since all fields are uniformly boxed pointers or scalars. |

---

## Anti-Features

Features to explicitly NOT build in v2.0.

| Anti-Feature | Why Avoid | What to Do Instead |
|--------------|-----------|-------------------|
| Precise/moving GC | Writing a precise GC (root-set enumeration, write barriers, compaction) is a multi-month project orthogonal to type support | Use Boehm GC (conservative, no root declarations needed, single-header integration via `-lgc`) |
| Reference counting | Introduces cycle leaks for recursive data structures (lists, trees) and requires runtime overhead on every pointer copy | Boehm GC handles cycles transparently |
| Unboxed tuple specialization | Emitting different LLVM struct types per tuple arity avoids indirection but requires monomorphization infrastructure | Use uniform boxed representation: all fields are `ptr` for simplicity. Defer unboxed optimization to v3. |
| String interning / deduplication | Reduces memory for repeated string constants but adds a hash table at runtime | In v2, every string allocation is independent. String equality uses `strcmp`, not pointer comparison. |
| Lazy list (streams / sequences) | OCaml-style `Lazy.t` requires thunk allocation and forcing machinery | LangThree lists are strict (all elements evaluated at construction). No lazy needed. |
| ADT / discriminated union compilation | LangThree AST already has `Constructor`, `ConstructorPat`, `DataValue` — but these are not in the v2 milestone scope | Keep as `failwith "Constructor not supported"` stub. ADTs are a v3 feature. |
| Record type compilation | `RecordExpr`, `FieldAccess`, `RecordUpdate` are not in the v2 milestone scope | Stub with `failwith`. Records are a v3+ feature. |
| Exception runtime (`Raise` / `TryWith`) | Requires `setjmp`/`longjmp` or C++ exception ABI; complex interaction with GC roots | Stub with `failwith`. Exceptions are a v3+ feature. |
| Printf format string parsing at compile time | The interpreter parses format strings at runtime. Implementing compile-time format parsing is significant work | Use runtime string-based `printf` dispatch via libc for v2. Compile-time formats are a v4+ optimization. |
| Tail call optimization (TCO) | The v1 design does not guarantee TCO; LLVM may or may not perform it. Explicit `musttail` annotation requires careful ABI matching with the closure calling convention | Accept LLVM's best-effort TCO for known functions. Lists and tuples don't introduce new tail-call sites that didn't exist in v1. |

---

## Feature Dependencies

```
GC runtime (Boehm GC_malloc)
    └─> String heap allocation
    └─> Tuple heap allocation
    └─> List cons-cell heap allocation
            └─> List literal [e1; e2; ...] (desugar to cons)
            └─> Pattern match on lists ([] / h :: t)

Tuple heap allocation
    └─> Tuple construction expression (Tuple node)
    └─> Pattern match on tuples (TuplePat)
    └─> LetPat with tuple pattern

String heap allocation
    └─> String literal compilation (String node)
    └─> String equality (= and <>) for ConstPat(StringConst)
    └─> String + operator (Add on string operands)
    └─> print / println builtins
    └─> to_string builtin
    └─> string_length, string_concat builtins

Pattern matching (Match node)
    └─> Decision tree evaluation (reuse LangThree MatchCompile.fs structure)
    └─> ConsPat (requires list cons cells)
    └─> TuplePat (requires tuple heap layout)
    └─> ConstPat(StringConst) (requires strcmp)
    └─> ConstPat(IntConst / BoolConst) (already available from v1)
    └─> when guards (requires bool elaboration, already available)
    └─> Wildcard / VarPat (no test needed; bind value)
    └─> Non-exhaustive match: abort() call
```

**Critical path for v2.0:**
1. GC integration (prerequisite for all heap allocation)
2. String literals + basic string builtins (print/println, string_length, to_string)
3. Tuple construction + TuplePat destructuring
4. List cons-cell + EmptyList + ConsPat destructuring
5. Match expression: full pattern compilation using decision tree

**Already available from v1 (no new work needed):**
- ConstPat(IntConst) and ConstPat(BoolConst): use existing `arith.cmpi eq`
- VarPat / WildcardPat: bind or discard, no new IR
- Boolean guards on when clauses: use existing bool elaboration

---

## MVP Recommendation

### Phase 1 — GC Runtime Integration

Integrate Boehm GC so that all future heap allocations are GC-managed.

Work: replace `llvm.call @malloc` with `llvm.call @GC_malloc`; add `GC_INIT()` call at
program start in `@main`; link with `-lgc`; add `GC_malloc` as an external func declaration
in the emitted MLIR.

Success criterion: a program that allocates a closure (already done in v1) and calls
`GC_malloc` instead of `malloc` compiles, links, and runs without crash.

### Phase 2 — Strings

Compile string literals to null-terminated GC_malloc'd char arrays. Wire up `print`,
`println`, and `to_string` as direct libc / stdlib calls.

Success criterion: `print "hello"` compiles and prints `hello`. `to_string 42` compiles
and produces the string `"42"`.

### Phase 3 — Tuples

Compile tuple construction to a GC_malloc'd array of `ptr`-sized words. Compile `TuplePat`
by GEP-indexing into the array.

Success criterion: `let (a, b) = (1, 2) in a + b` compiles and exits with 3. A nested
tuple pattern works.

### Phase 4 — Lists

Compile `EmptyList` as a null pointer, `Cons` as a two-word GC_malloc'd cons cell, and
`List [...]` as iterated cons construction.

Success criterion: `let rec sum lst = match lst with | [] -> 0 | h :: t -> h + sum t in
sum [1; 2; 3]` compiles and exits with 6.

### Phase 5 — Pattern Matching

Compile the `Match` AST node to LLVM control flow using the decision-tree structure from
LangThree's `MatchCompile.fs` as a guide.

Success criterion: a `match` on a list with `[]` and `h :: t` arms, and a `match` on a
tuple with a `TuplePat` arm, both compile and execute correctly.

### Defer to v3+

- ADTs / discriminated unions
- Records and field access
- Exceptions (Raise / TryWith)
- Char type as a separate compiled type
- `sprintf` and format-string builtins beyond basic `print`
- Or-pattern (`OrPat`) compilation (low priority; rare in v2 test programs)
- `when` guard compilation (medium priority; add if test programs need it)

---

## Feature Detail: GC-Managed Heap Layout

### Uniform value representation

All heap-allocated LangThree values use a uniform pointer-to-struct representation:
- Every value is either a raw scalar (`i64` / `i1`) passed by value in SSA, or a pointer
  to a GC-managed struct.
- This means lists and tuples contain `ptr` elements (all fields boxed).
- For v2, scalars (int, bool) that appear inside tuples or lists must be boxed: allocate a
  one-word GC_malloc'd struct holding the `i64` or `i1`.

Alternative: tag the low bit of a pointer to distinguish boxed pointers from immediate
integers (like OCaml's value representation). This is more efficient but more complex to
implement. Defer to v3 unless boxing overhead is a measurable problem in v2 tests.

### Tuple layout

```
+-------+-------+-------+
| arity |  [0]  |  [1]  |  ...
+-------+-------+-------+
  i64     ptr     ptr
```

- First word: arity (number of elements) as `i64` — used for safety checks and future
  pattern matching on tuple size.
- Remaining words: one `ptr` per element.
- Total size: `(1 + arity) * sizeof(ptr)` = `(1 + arity) * 8` bytes.

Alternative: omit arity word and rely on static compile-time knowledge of tuple size.
This saves 8 bytes per tuple. Since LangThree's type system always knows the tuple arity at
compile time, this is safe and preferred for v2.

### Cons-cell layout

```
+-------+-------+
|  head |  tail |
+-------+-------+
   ptr     ptr
```

- `head`: pointer to the element value.
- `tail`: pointer to the next cons cell, or null for empty list.
- Size: 16 bytes (two pointers).

### String layout

- Null-terminated C string stored as `i8*` / `!llvm.ptr`.
- Allocated with `GC_malloc(strlen + 1)`.
- Compatible with `printf`, `strcmp`, `strlen` without any marshalling.

---

## Sources

- LangThree AST: `../LangThree/src/LangThree/Ast.fs` — direct inspection, HIGH confidence
- LangThree Eval: `../LangThree/src/LangThree/Eval.fs` — direct inspection, HIGH confidence
- LangThree MatchCompile: `../LangThree/src/LangThree/MatchCompile.fs` — direct inspection, HIGH confidence
- LangBackend v1 FEATURES.md: `.planning/research/FEATURES.md` (v1 scope anchor)
- LangBackend PROJECT.md: `.planning/PROJECT.md` — v2 milestone definition
- Boehm GC documentation: https://www.hboehm.info/gc/ — conservative GC API (`GC_INIT`, `GC_malloc`)
- OCaml value representation: https://v2.ocaml.org/api/compiledfiles/runtime/gc.html — boxed vs unboxed scalars
- MinCaml — Heap allocation for tuples and closures: https://esumii.github.io/min-caml/paper.pdf
- LLVM opaque pointers (`!llvm.ptr`): https://mlir.llvm.org/docs/Dialects/LLVM/ — used for all GC-managed values
