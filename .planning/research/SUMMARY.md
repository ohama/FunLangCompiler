# Project Research Summary

**Project:** LangBackend v4.0 — ADT/GADT, Records (mutable fields), Exception Handling
**Domain:** Functional language compiler backend (F# host, MLIR text emission, LLVM pipeline)
**Researched:** 2026-03-26
**Confidence:** HIGH

---

## Executive Summary

LangBackend v4.0 adds three interdependent feature clusters to an existing compiler that already
handles int/bool/string/tuple/list/closure/pattern-matching via MLIR text → LLVM → native binary.
All three features (ADTs, Records, Exceptions) follow a single, consistent runtime strategy: uniform
heap representation using `{i64 tag, ptr payload}` for ADTs and flat pointer arrays for records,
with setjmp/longjmp exceptions routed through a C runtime wrapper. This approach is deliberate and
conservative — it matches the OCaml runtime model, works correctly with Boehm GC, and reuses every
existing MlirOp, GEP pattern, and pattern-matching infrastructure already in the codebase.

The recommended implementation order is strict: ADT infrastructure (TypeEnv, ElabEnv extension,
constructor elaboration, ConstructorPat in MatchCompiler) must come first because exceptions are
ADT values and the exception handler dispatch reuses ADT pattern matching. Records are independent
of both and can proceed in parallel with ADTs. Exception handling (`TryWith`/`Raise`) comes last
because it depends on both ADT values (exception payloads are DataValues) and the complete
MatchCompiler extension (handler dispatch uses the same decision tree). The 10-step build order
in ARCHITECTURE.md is the definitive sequencing reference.

The critical risks are all in the exception mechanism: (1) `setjmp` must not be wrapped in a
regular out-of-line C function at optimization levels above `-O0` — it requires `returns_twice`
semantics, best achieved via a `static inline` or macro wrapper in `runtime.c`; (2) the handler
stack (`lang_exn_top`) must be popped before the handler body executes, not after, or a second
exception inside the handler causes infinite re-entry; (3) ADT constant constructors (nullary,
e.g. `None`, `Red`) must always allocate a 16-byte block with a real tag value — representing
them as null pointers is incorrect for multi-constructor types.

---

## Key Findings

### Recommended Stack

The v4.0 stack is entirely additive to the existing v3 stack (F# / .NET 10, LLVM 20 / MLIR 20,
func/arith/cf/llvm dialects, Boehm GC 8.2.12). No new external dependencies are introduced.
All new runtime code goes into the existing `lang_runtime.c`. No new `MlirType` variants are
needed; ADT tags are `i64` and payloads are `!llvm.ptr`, both already in the type system.

**Core technologies (existing, unchanged):**
- **F# / .NET 10**: host language for the compiler — unchanged
- **MLIR 20 / LLVM 20**: lowering target — unchanged; no new dialects required
- **Boehm GC 8.2.12**: conservative GC — unchanged; `GC_malloc` covers all new allocations
- **lang_runtime.c**: C runtime helpers — extended with 4 new functions for exception handling

**New additions for v4.0:**
- **`lang_try_enter` / `lang_try_exit` / `lang_throw` / `lang_current_exception`**: C runtime exception protocol using `setjmp`/`longjmp` with a linked-list handler stack (`LangExnFrame`)
- **`ElabEnv` additions**: `TypeEnv` (constructor name → tag + arity), `RecordEnv` (field name → index + type), `JmpBufPtr` (current in-scope handler frame pointer)
- **`MatchCompiler.CtorTag` additions**: `AdtCtor of name * tag` and `RecordCtor of fields` variants
- **`elaborateProgram`**: new entry point replacing `elaborateModule`; processes declarations in a pre-pass before emitting any IR

### Expected Features

**Must have (table stakes) — v4.0 MVP:**
- `TypeDecl` / `ExceptionDecl` processing populates `TypeEnv` with constructor → tag index mapping
- `Constructor(name, None)` — nullary: allocate 16 bytes, store tag, store null payload
- `Constructor(name, Some arg)` — unary: allocate 16 bytes, store tag, store payload ptr
- `ConstructorPat(name, None)` — tag test only, no payload extraction
- `ConstructorPat(name, Some pat)` — tag test + GEP field 1 + load + sub-pattern dispatch
- `RecordDecl` processing populates `RecordEnv` with field → index mapping
- `RecordExpr` — GC_malloc(n*8) + sequential field stores in declaration order
- `FieldAccess` — GEP(fieldIndex) + load using `RecordEnv`
- `RecordUpdate` — allocate new block, copy non-overridden fields, write overridden fields
- `SetField` — GEP(fieldIndex) + store; returns unit (i64 = 0)
- `RecordPat` — unconditional structural match (identical to TuplePat but indexed by field name)
- `Raise` — elaborate exception value + call `@lang_throw` + `llvm.unreachable`
- `TryWith` — GC_malloc `LangExnFrame` + call `lang_try_enter` + setjmp branch + handler decision tree + merge block

**Should have (add after core is working) — v4.x:**
- ADT constructor as first-class value (wrap unary ctor in a lambda)
- Exception re-raise on handler miss (call `lang_throw` from `Fail` branch of handler decision tree)
- Nested ADT pattern matching (multi-level constructor patterns, requires recursive GEP chains)

**Defer — v5+:**
- Stack traces on exceptions (requires DWARF/frame pointer walking)
- Unboxed/specialized ADT representation (requires monomorphization)
- Printf/sprintf for exception messages (`sprintf` already deferred)
- C++ exception ABI (`_Unwind_RaiseException`) — incompatible with Boehm GC

### Architecture Approach

The architecture is strictly additive. All five existing modules (`MlirIR.fs`, `Elaboration.fs`,
`MatchCompiler.fs`, `Printer.fs`, `Pipeline.fs`) require only new match arms or new record fields —
no structural rewrites. `Pipeline.fs` is unchanged. `Printer.fs` gains 2 new `printOp` arms (for
`LlvmSetjmpOp` / `LlvmLongjmpOp` if those are added as separate DU cases, otherwise unchanged).
`lang_runtime.c` gains 4 C functions and 1 struct. The key architectural decision is the new
`elaborateProgram` entry point which performs a declaration pre-pass before IR emission.

**Major components and their v4.0 responsibilities:**
1. **`elaborateProgram`** (new in `Elaboration.fs`) — pre-pass: scan all `TypeDecl`, `RecordTypeDecl`, `ExceptionDecl`; assign constructor tags and field indices; populate `TypeEnv` and `RecordEnv` in `ElabEnv`; no IR emitted at this stage
2. **`elaborateExpr` extensions** (in `Elaboration.fs`) — new arms for `Constructor`, `RecordExpr`, `FieldAccess`, `RecordUpdate`, `SetField`, `Raise`, `TryWith`; all reuse existing GEP/store/load/call ops
3. **`MatchCompiler.fs` extension** — add `AdtCtor` and `RecordCtor` to `CtorTag`; implement `desugarPattern` for `ConstructorPat` and `RecordPat` (both currently guarded by `failwith`); thread `ctorTags`/`fieldIndices` maps into `compile`
4. **`lang_runtime.c` extension** — add `LangExnFrame` struct, `lang_exn_top` global, `lang_try_enter` (calls `setjmp` via `static inline` to preserve `returns_twice`), `lang_try_exit`, `lang_throw` (calls `longjmp`), `lang_current_exception`

### Critical Pitfalls

The full pitfall catalog covers 16 critical + 14 moderate + 6 minor items across all milestones.
The pitfalls most likely to block v4.0 specifically:

1. **`setjmp` without `returns_twice` causes silent miscompilation at -O2** (C-15) — `lang_try_enter` must call `setjmp` via `static inline` or macro in the caller's frame, never as an out-of-line function. Symptom: exception tests pass at `-O0`, fail silently at `-O1`/`-O2`.

2. **Handler stack popped in wrong order — infinite re-entry on nested exceptions** (C-16) — pop `lang_exn_top` immediately when `setjmp` returns non-zero (before handler body executes), not after. Normal-path body must also call `lang_try_exit()` before branching to merge block.

3. **ADT tag at field 0 conflicts with existing GEP-0 loads for tuples/cons cells** (C-11) — ADT layout is `{i64 tag @field0, ptr payload @field1}`. Never reuse tuple/cons-cell codegen paths for ADT blocks; keep them as separate code paths even though the struct shape looks similar.

4. **Nullary constructors must always allocate a 16-byte block with a real tag** (C-12) — representing `None`, `Red`, etc. as null pointers breaks multi-constructor tag discrimination. The null encoding is reserved for `NilCtor` (list empty) only.

5. **`longjmp` over live GC roots creates floating garbage** (C-14) — Boehm GC conservatively scans abandoned stack frames after `longjmp`. Mitigate by calling `GC_collect_a_little()` after `longjmp` returns in the C wrapper, and keeping handler code allocation-free before the first handler block.

---

## Implications for Roadmap

Based on the dependency graph from FEATURES.md and the 10-step build order from ARCHITECTURE.md,
the natural phase structure is 4 core phases plus a polish phase.

### Phase 1: Environment Infrastructure
**Rationale:** All subsequent elaboration depends on `TypeEnv`, `RecordEnv`, and the new `ElabEnv`
fields being populated. This is pure setup with no IR emission — lowest risk, immediately unblocks
everything else. Also covers extending `MatchCompiler.CtorTag` since the decision tree must know
ADT/record cases before any pattern match elaboration is attempted.
**Delivers:** `elaborateProgram` entry point; `ElabEnv` extended with `TypeEnv`, `RecordEnv`,
`ExnTags`, `JmpBufPtr`; `MatchCompiler.CtorTag` gains `AdtCtor` and `RecordCtor`; `desugarPattern`
implements `ConstructorPat` and `RecordPat` dispatch; all 45 existing E2E tests still pass.
**Addresses:** TypeDecl/RecordDecl/ExceptionDecl tag/index registration
**Avoids:** Pitfall C-11 (establish tag-at-field-0 invariant before any IR is emitted)
**Research flag:** Well-documented F# Map patterns — skip `/gsd:research-phase`

### Phase 2: ADT Construction and Pattern Matching
**Rationale:** ADT values are prerequisite for exception values (exceptions are ADT DataValues).
This phase closes the round-trip: constructors build values, ConstructorPat matches them. It also
validates the MatchCompiler extension from Phase 1 under real IR emission.
**Delivers:** `Constructor(name, optArg)` elaboration to 16-byte `{tag, payload}` GC_malloc blocks;
`ConstructorPat` elaboration (tag load + cmpi eq + branch + payload GEP); full ADT round-trip
with E2E tests on multi-constructor types (`Option`, `Result`, recursive `Tree`, `Color`).
**Uses:** `LlvmGEPLinearOp`, `LlvmLoadOp`, `LlvmStoreOp`, `ArithCmpIOp`, `ArithConstantOp` — all existing
**Avoids:** Pitfall C-12 (allocate real 16-byte blocks for nullary ctors); C-11 (tag at field 0)
**Research flag:** Standard two-field struct + GEP patterns — skip `/gsd:research-phase`

### Phase 3: Records (Mutable Fields)
**Rationale:** Records are architecturally independent of both ADTs and exceptions. They share
the GEP/store/load infrastructure with tuples. This phase can run in parallel with Phase 2 if
bandwidth allows, but both must complete before Phase 4 (exception handlers may pattern match on
record-typed exception payloads).
**Delivers:** `RecordExpr`, `FieldAccess`, `RecordUpdate`, `SetField`, `RecordPat` elaboration;
mutable field mutation verified with aliasing semantics test; E2E tests for record construction,
field access, copy-update, and mutable mutation.
**Uses:** `LlvmGEPLinearOp` + `LlvmLoadOp` + `LlvmStoreOp` — identical to tuple codegen
**Avoids:** Pitfall M-12 (document that `let r2 = r1` is an alias for mutable records, not a copy); M-13 (RecordUpdate is a shallow copy — document and test explicitly)
**Research flag:** Identical to tuple codegen with named index lookup — skip `/gsd:research-phase`

### Phase 4: Exception Handling
**Rationale:** Last because it depends on ADT values, ConstructorPat in the decision tree, and
the complete `TryWith` block structure. It has the most runtime complexity (setjmp/longjmp,
handler stack management) and the most dangerous pitfalls (C-14, C-15, C-16). Deferring it means
Phases 1-3 contribute a robust regression baseline before the exception machinery is wired in.
**Delivers:** `lang_runtime.c` additions (`LangExnFrame` struct, `lang_try_enter` with `static inline`
setjmp, `lang_try_exit`, `lang_throw`, `lang_current_exception`); `Raise` elaboration (elaborate
exception DataValue + call `@lang_throw` + `llvm.unreachable`); `TryWith` elaboration (GC_malloc
frame + `lang_try_enter` call + setjmp branch + handler decision tree reusing ConstructorPat +
merge block); unhandled exception propagation via `lang_throw` with null `lang_exn_top` check.
**Addresses:** All P1 Exception features from FEATURES.md
**Avoids:** Pitfall C-15 (`returns_twice` — `static inline` / macro for `lang_try_enter`); C-16 (pop handler stack before handler body); C-14 (call `GC_collect_a_little()` post-longjmp)
**Research flag:** The `returns_twice` / `static inline setjmp` interaction with MLIR-emitted LLVM IR and clang linking warrants a small proof-of-concept before full `TryWith` codegen. Recommend writing an isolated `runtime.c` test (not a full E2E compiler test) that raises and catches an exception at `-O2` before integrating into the codegen phase.

### Phase 5: P2 Completeness (Post-Validation)
**Rationale:** These features are not required for the v4.0 milestone declaration but are needed
for real programs. Add after all 4 core phases are validated.
**Delivers:** ADT constructor as first-class value (lambda wrapping); exception re-raise on handler
miss; nested ADT pattern matching (multi-level GEP chains).
**Addresses:** P2 features from FEATURES.md prioritization matrix
**Research flag:** Standard patterns, reuses existing lambda/closure machinery — skip `/gsd:research-phase`

### Phase Ordering Rationale

- Phase 1 before everything: the `ElabEnv` maps are the shared foundation; all subsequent phases read from them
- Phase 2 before Phase 4: exception values are `DataValue` ADT structs; without ADT elaboration there is no exception value to raise or catch
- Phase 3 parallel-possible with Phase 2: records share no code paths with ADT constructors; the only constraint is both must complete before Phase 4
- Phase 4 last: highest-risk phase (C-14, C-15, C-16); deferring it means the 45+ passing tests from Phases 1-3 protect against regressions when the exception machinery is wired in
- All existing 45 E2E tests must continue passing after each phase — use them as the regression gate at every step

### Research Flags

Needs deeper research or isolated proof-of-concept before implementation:
- **Phase 4 (Exception Handling):** Validate that `static inline lang_try_enter` calling `setjmp` is correctly treated as `returns_twice` by clang at `-O2` when linked with MLIR-emitted LLVM IR. Write a standalone C + LLVM IR test before writing `TryWith` codegen.

Phases with well-established patterns (skip `/gsd:research-phase`):
- **Phase 1:** Pure F# Map operations extending existing record types
- **Phase 2:** Two-field struct layout identical to existing tuple/cons-cell patterns; decision tree extension is additive
- **Phase 3:** Tuple codegen with named field lookup substituted for positional index
- **Phase 5:** Lambda wrapping reuses existing closure machinery; exception re-raise reuses `lang_throw`

---

## Confidence Assessment

| Area | Confidence | Notes |
|------|------------|-------|
| Stack | HIGH | No new external dependencies; confirmed against direct code inspection of all 5 compiler modules and `lang_runtime.c`; all new ops reuse existing `MlirType` variants |
| Features | HIGH | Based on direct inspection of `Ast.fs`, `MatchCompile.fs`, `Elaboration.fs`, `MlirIR.fs`; the LangThree AST defines all target nodes explicitly; scope is tightly bounded |
| Architecture | HIGH | Derived from direct code analysis; all new patterns use existing ops; `MatchCompiler.fs` line 121-125 explicitly confirms `ConstructorPat` and `RecordPat` are `failwith` stubs |
| Pitfalls | HIGH | All v4-specific critical pitfalls (C-11 through C-16) are grounded in known LLVM/GC/setjmp behaviors with documented symptoms and detection methods |

**Overall confidence:** HIGH

### Gaps to Address

- **`returns_twice` verification (Phase 4):** ARCHITECTURE.md notes two implementation strategies for the setjmp wrapper (direct `LlvmSetjmpOp` variant vs `LlvmCallOp` reuse). STACK.md recommends the C runtime wrapper approach. The exact mechanism for ensuring `-O2` correctness needs an isolated empirical test before full `TryWith` integration. Handle by writing a standalone `runtime.c` proof-of-concept that raises and catches an exception before wiring into the elaborator.

- **`RecordExpr` type name resolution (Phase 3):** When `typeName = None` in `RecordExpr`, the elaborator must use field names to disambiguate the record type. STACK.md recommends requiring `typeName = Some _` for v4.0. Validate this assumption against actual LangThree AST output before starting Phase 3.

- **GADT multi-arg payloads (Phase 2):** STACK.md documents wrapping multi-arg GADT constructors as tuple payloads. Confirm that either the LangThree AST already performs this wrapping or that `elaborateProgram` must handle it explicitly.

---

## Sources

### Primary (HIGH confidence)
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Ast.fs` — direct inspection; all target AST nodes confirmed (`TypeDecl`, `ConstructorDecl`, `GadtConstructorDecl`, `RecordDecl`, `RecordFieldDecl`, `Constructor`, `RecordExpr`, `FieldAccess`, `SetField`, `RecordUpdate`, `RecordPat`, `Raise`, `TryWith`)
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/MatchCompile.fs` — direct inspection; `ConstructorPat` and `RecordPat` confirmed as `failwith`-guarded stubs in current codebase
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/Elaboration.fs` — direct inspection; existing `elaborateExpr` patterns, `ElabEnv` shape, `testPattern` dispatch
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/MlirIR.fs` — direct inspection; current `MlirOp` cases (~17), `MlirType`, existing GEP/load/store op set
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/MatchCompiler.fs` — direct inspection; `CtorTag` DU, Jacobs decision tree, `ctorArity`
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/lang_runtime.c` — direct inspection; existing `GC_malloc` wrapper, `lang_match_failure`, `lang_range`

### Secondary (MEDIUM confidence)
- OCaml runtime (`ocaml/runtime/fail.c`, `ocaml/runtime/sys.c`) — `setjmp`/`longjmp` exception model; `caml_exn_bucket` thread-local pattern used as reference for `LangExnFrame` linked-list design
- Boehm GC documentation — conservative stack scanning, `GC_malloc_atomic`, `GC_INIT()` stack base detection; `jmp_buf` on stack is correctly scanned as part of the stack frame
- LLVM Language Reference — `returns_twice` attribute semantics; wrappers around `setjmp` require explicit attribute propagation

### Tertiary (LOW confidence — needs empirical validation)
- `static inline setjmp` wrapper interaction with clang `-O2` when called from MLIR-emitted LLVM IR — documented in pitfall C-15 but requires a proof-of-concept before relying on it in production codegen

---
*Research completed: 2026-03-26*
*Ready for roadmap: yes*
