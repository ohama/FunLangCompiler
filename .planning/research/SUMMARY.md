# Project Research Summary

**Project:** LangBackend — LangThree MLIR/LLVM Native Compiler
**Domain:** Functional language compiler backend (ML-style, F# host, MLIR → LLVM → native binary)
**Researched:** 2026-03-26
**Confidence:** HIGH

## Executive Summary

LangBackend is a native-code compiler backend for LangThree, an ML-style functional language with integers, booleans, arithmetic, comparisons, let/let-rec bindings, lambdas, and function application. The established expert approach for this class of compiler is a straight-line pipeline: a typed AST feeds a MLIR code generator that emits `func` + `arith` + `cf` dialect operations, which are then lowered through a fixed pass sequence to the `llvm` dialect, translated to LLVM IR, and linked into a native binary via clang. This is the same path used by the MLIR Toy tutorial and confirmed by the only known F# + MLIR proof-of-concept project. No novel patterns are required.

The recommended implementation language is F# on .NET 10, accessing MLIR exclusively through its C API via P/Invoke. No maintained .NET/F# MLIR binding library exists; the C API is the only ABI-stable interface to MLIR for non-C++ hosts. The core design decisions are: opaque struct wrappers (not bare `nativeint`) for all MLIR handles; a flat closure struct with uniform `(env_ptr, arg) -> result` calling convention for lambdas; integer type `i64` and boolean type `i1` throughout; and a Tier 2 shell pipeline (`mlir-translate | llc | clang`) for the initial end-to-end path, deferring in-process LLVM emission to a later phase.

The dominant risks are all in the MLIR C API integration layer, not in compiler algorithm complexity. Region ownership transfer, context/module destruction order, incomplete dialect lowering leaving `unrealized_conversion_cast` residue, and wrong lowering pass order are each capable of producing silent corruption or opaque crashes. These must be addressed structurally in Phase 1 before any codegen work begins. Closure representation is the hardest algorithmic decision and must be finalized before the Lambda phase — retrofitting a uniform calling convention is a full codegen rewrite.

## Key Findings

### Recommended Stack

The stack is well-established with no meaningful alternatives. F# / .NET 10 (LTS, November 2025) is the host language, chosen for direct compatibility with the existing LangThree frontend. LLVM 20 / MLIR 20 (stable, March 2025) is the target infrastructure; apt packages from `apt.llvm.org` provide `libmlir-20-dev`, `mlir-20-tools`, and `clang-20`. The MLIR C API (`libMLIR-C.so`) is accessed via F# `[<DllImport>]` P/Invoke declarations with `[<Struct>]` typed wrappers for all opaque handles.

The dialect set for v1 is `func` + `arith` + `cf` for code generation, lowered to `llvm` via a four-pass pipeline: `convert-arith-to-llvm`, `convert-cf-to-llvm`, `convert-func-to-llvm`, `reconcile-unrealized-casts` (in that exact order). Closures use the `llvm` dialect directly for struct allocation and GEP, bypassing `memref`. Testing uses xUnit + FsUnit for unit/integration tests and shell scripts/Makefile for E2E tests.

**Core technologies:**
- F# / .NET 10: implementation host — same language as LangThree frontend; maximal code reuse; current LTS
- LLVM 20 / MLIR 20: backend infrastructure — current stable release; apt packages confirmed; stable C API
- MLIR C API via P/Invoke: MLIR access mechanism — only ABI-stable interface for non-C++ languages; no .NET MLIR library exists
- `func` + `arith` + `cf` dialects: codegen target — minimal well-supported set for a first-order functional language; all have maintained LLVM lowering passes
- clang-20: linker driver — handles libc startup automatically; simpler than raw ld
- xUnit + FsUnit + Makefile: test infrastructure — standard .NET ecosystem; E2E via shell pipeline

### Expected Features

The v1 compiler has a clear, bounded scope. The success criterion is correct compilation and execution of `let rec factorial` and `let rec fib`. Features decompose into a strict dependency order that dictates implementation sequence.

**Must have (table stakes) — v1:**
- Integer literals (`i64`) and boolean literals (`i1`)
- Arithmetic: `+`, `-`, `*`, `/` (signed), unary negation
- Comparisons: `=`, `<>`, `<`, `>`, `<=`, `>=` via `arith.cmpi`
- Logical operators `&&` and `||` with short-circuit semantics (branch-based, not bitwise)
- If-else expressions (both branches must produce the same type)
- Let binding (pure SSA, no heap)
- Variable reference (SSA symbol table lookup)
- Let rec / recursive functions (known-function path: no free variables beyond self-reference)
- Lambda / function application (flat closure representation with heap-allocated environment)
- Module-level declarations (top-level `func.func` for each declaration)
- Executable entry point (`func.func @main` returning i64 exit code)
- E2E test harness (compile `.lt` file, run binary, verify exit code / stdout)

**Should have (differentiators) — v1 if feasible:**
- Known-function optimization: `let rec f x = ...` with no captured free vars compiles as a direct `func.call` with no closure allocation (MinCaml approach; most recursive functions qualify)
- Type-directed emission: consume HM-inferred `TInt`/`TBool`/`TArrow` from LangThree type checker directly; eliminates type re-inference in codegen
- Source location attributes: propagate AST `Span` info into MLIR locations for meaningful error messages
- CLI interface: `langbackend <file.lt>` (already in project scope via `FunLang.Compiler.Cli`)

**Defer to v2+:**
- Strings: require heap allocation, null termination, GC interaction
- Tuples and lists: require struct layout, boxing, recursive types
- Pattern matching: depends on tuples/lists
- ADTs: not in LangThree v1
- Garbage collector: full GC requires runtime, object headers, root sets
- Tail call elimination (TCO): valuable but addable after basic recursion works; defer unless quick win
- Incremental / separate compilation: requires module system and caching infrastructure
- REPL integration: interpreter REPL already exists; JIT adds significant complexity

### Architecture Approach

The architecture is a straight-line pipeline with no shared mutable state between stages. The LangThree frontend (already complete) produces a typed `Ast.Module`. An annotation pass converts this to a `TypedExpr` representation with resolved types, explicit free-variable sets on every lambda, and call-kind annotations (direct vs. closure). The `MLIRGen` module traverses `TypedExpr` with a single recursive function `emitExpr : TypedExpr -> CodegenEnv -> MlirValue`, managing an immutable SSA symbol table (`Map<string, MlirValue>`) threaded through recursion. MLIR operations are built via the C API (never textual IR construction), lowered through the fixed pass pipeline, translated to LLVM IR, and linked via clang.

**Major components:**
1. LangThree Frontend — lex, parse, Hindley-Milner type check; produces typed `Ast.Module`; already complete
2. TypedExpr Annotation Pass — lower `Ast.Expr` to type-annotated, free-variable-annotated IR; prerequisite for all codegen
3. MLIR C API Bindings (`MlirBindings.fs`) — raw P/Invoke `extern` declarations with `[<Struct>]` typed handle wrappers; must be the only layer touching `nativeint`
4. CodegenEnv (`CodegenEnv.fs`) — MLIR context, module, current insertion block, SSA symbol table, closure tracking; owns `CompilerSession` with enforced destruction order
5. MLIRGen (`MLIRGen.fs`) — recursive AST traversal; emits `func`, `arith`, `cf`, `llvm` dialect ops; pure codegen logic, no handle ownership
6. Lowering Pipeline — four-pass `convert-arith-to-llvm, convert-cf-to-llvm, convert-func-to-llvm, reconcile-unrealized-casts` via `mlirParsePassPipeline`
7. LLVM Backend / Linker — `mlirTranslateModuleToLLVMIR` then `clang-20` for object file and link (Tier 2 shell pipeline in v1)
8. CLI Driver (`FunLang.Compiler.Cli`) — orchestrates pipeline from `.lt` file path to native binary

**Key patterns:**
- Emit-before-insert: populate `MlirOperationState` fully, then `mlirOperationCreate`, then insert into block; never mutate after insertion
- Block-centric emission: `CodegenEnv` tracks current insertion block; if-else creates then/else/merge blocks and switches insertion point
- If-else via `cf.cond_br` + block arguments (phi-style): both branches supply value to merge block argument
- Let binding is zero-cost: extends `SymbolTable` map, emits no MLIR op
- LetRec via forward-declared `func.func` symbol: recursive call uses `func.call @f` by symbol name, not SSA closure call

### Critical Pitfalls

Six critical pitfalls (silent crashes or forced rewrites) were identified, all in the MLIR C API integration and closure design layers:

1. **Region ownership silently transferred after `mlirOperationCreate`** — wrap `MlirRegion` in a DU (`Owned | Transferred`); mark as `Transferred` immediately after `mlirOperationStateAddOwnedRegions`; never call `mlirRegionDestroy` on a transferred region. Establish this in Phase 1.

2. **Context destroyed before module** — use a `CompilerSession` record that packages `(context, module)` with a single `dispose` that always destroys module first, context second. Never put them in separate `use` scopes.

3. **`unrealized_conversion_cast` ops remaining after lowering** — always run `reconcile-unrealized-casts` as the last pass; after adding any new language feature, dump IR and grep for `unrealized_conversion_cast` before proceeding.

4. **Wrong lowering pass order causes type mismatch** — canonical order is innermost ops first: `convert-arith-to-llvm` before `convert-func-to-llvm`. Document and never reorder.

5. **Non-uniform closure calling convention** — decide on flat closure struct (`{ fn_ptr, env_ptr }` where implementation function signature is always `(i8* env, arg) -> result`) before writing any lambda codegen. Verify with a hand-written MLIR test. Retrofitting is a full rewrite.

6. **Reusing interpreter AST without codegen annotations** — run a `Ast.Expr -> TypedExpr` lowering pass before any MLIR emission; `TypedExpr` must carry: resolved HM type at every node, explicit free-variable sets on every lambda, call-kind annotation (direct vs. closure).

## Implications for Roadmap

The research establishes a hard build order enforced by technical dependencies. The pipeline must produce an executable binary before any codegen feature is useful to test. The closure representation must be finalized before any lambda feature is started. The `TypedExpr` annotation pass must exist before any MLIR emission. These constraints yield a natural phase structure.

### Phase 1: P/Invoke Infrastructure and Pipeline Skeleton

**Rationale:** All subsequent work depends on a working MLIR C API connection and a proven end-to-end pipeline (MLIR → object file → linked binary). The worst pitfalls (ownership, destruction order, pointer size marshaling) must be eliminated structurally before any codegen begins. Emit the simplest possible program (`return 42`) and verify the full pipeline works. No AST involvement yet.

**Delivers:** Working `CompilerSession` with enforced context/module lifecycle; typed P/Invoke handle wrappers (`MlirContext`, `MlirOperation`, etc.) with `nativeint` fields; dialect and pass registration; diagnostic handler; lowering pipeline (`convert-arith-to-llvm, convert-cf-to-llvm, convert-func-to-llvm, reconcile-unrealized-casts`); Tier 2 shell pipeline (mlir-translate → llc → clang) producing runnable ELF binary.

**Implements:** MLIR C API Bindings + CodegenEnv components; establishes `CompilerSession` pattern.

**Avoids:** C-1 (region ownership), C-2 (context/module destruction order), M-6 (pointer size in P/Invoke structs), m-1 (unregistered diagnostics), m-3 (pass manager before dialect registration).

### Phase 2: Scalar Arithmetic Codegen

**Rationale:** Integers and arithmetic are pure SSA — no memory model, no control flow, no closures. They validate the emit-before-insert pattern and prove the lowering pipeline handles `arith` dialect ops. If this phase's E2E test (compile and run `1 + 2 * 3`) passes, the core infrastructure is sound.

**Delivers:** Codegen for `Number`, `Add`, `Sub`, `Mul`, `Div`, unary negation. `arith.constant i64`, `arith.addi`, `arith.subi`, `arith.muli`, `arith.divsi`. E2E: compile expression → binary → verify exit code.

**Implements:** Core of `MLIRGen.fs`; entry-point `func.func @main() -> i64` wrapper.

**Avoids:** m-2 (signed vs. unsigned ops — use `arith.divsi` exclusively); C-4 (pass order locked from Phase 1).

### Phase 3: TypedExpr Annotation Pass

**Rationale:** Before adding let bindings and especially before lambdas, the AST must carry resolved types and free-variable sets. Building this pass while the feature set is still small (integers only) means it can be validated cheaply. Deferring it to the lambda phase means debugging annotation bugs and codegen bugs simultaneously.

**Delivers:** `TypedExpr` IR that carries: HM-resolved type at every node; explicit free-variable set on every `Lambda` node; call-kind annotation distinguishing direct calls from closure calls; explicit `LetRec` scope marker distinguishing self-recursive from non-recursive.

**Implements:** TypedExpr Annotation Pass component; consumed by all subsequent MLIRGen phases.

**Avoids:** C-6 (AST reuse without codegen annotations — the core structural fix for this pitfall).

### Phase 4: Let Binding, Variables, and Booleans / If-Else

**Rationale:** Let binding is zero-cost SSA extension; validating it before closures means the symbol table logic is proven independently. Boolean and if-else together because `cf.cond_br` requires `i1` from `arith.cmpi` and the merge block argument pattern must be established before closures need control flow.

**Delivers:** `Var`, `Let` (SSA symbol table); `Bool` literals (`arith.constant i1`); comparison ops (`arith.cmpi` with all predicates); logical operators `&&` / `||` (branch-based for short-circuit); `If`-`Else` via `cf.cond_br` + basic blocks + merge block argument.

**Implements:** Block-centric emission pattern in CodegenEnv; `zext i1 to i64` at boolean/integer boundaries.

**Avoids:** M-1 (i1/i64 boundary mismatch — explicit `zext`); M-3 (block arguments on both branches of `cf.cond_br`); M-5 (SSA dominance — run `mlirOperationVerify` after each function).

### Phase 5: LetRec (Known Functions, No Captures)

**Rationale:** Most practical recursive programs (factorial, fibonacci) qualify as known functions with no free variables beyond the recursion variable itself. Implementing this path first gives a working recursive compiler without the full closure machinery. Provides the v1 success criterion (`let rec fact n = ...`) before tackling closures.

**Delivers:** `LetRec` codegen for known functions: forward-declared `func.func @f`; recursive body with `func.call @f` by symbol name; wraps `f` in a closure struct for external callers while using direct call internally. E2E: `let rec fact n = if n <= 1 then 1 else n * fact (n - 1) in fact 10` → `3628800`.

**Implements:** Two-pass LetRec strategy (declare symbols before emitting bodies).

**Avoids:** M-4 (missing forward declaration for let rec).

### Phase 6: Lambda and Full Closures

**Rationale:** This is the hardest phase. The closure representation (`{ fn_ptr, env_ptr }` flat struct with uniform `(i8* env, arg) -> result` calling convention) must be finalized before writing a single line of lambda codegen. Verify the closure layout and calling convention with a hand-written MLIR test case before connecting to the AST traversal. Then implement free-variable analysis → closure struct allocation → closure invocation.

**Delivers:** Full closure representation in `llvm` dialect: `llvm.alloca`/`llvm.getelementptr`/`llvm.store` for environment allocation; uniform `fn_ptr` calling convention; `App` node → load `fn_ptr` from struct → indirect call with `env_ptr`. E2E: `let add_n n = fun x -> x + n in let add5 = add_n 5 in add5 3` → `8`.

**Implements:** Complete MLIRGen for Lambda and App; uses `llvm` dialect struct operations.

**Avoids:** C-5 (non-uniform calling convention — committed and tested before first lambda).

### Phase 7: CLI and Module-Level Declarations

**Rationale:** Once all expression forms compile correctly, the CLI and module-level wiring are straightforward plumbing. Separate phase to keep earlier phases focused on codegen correctness.

**Delivers:** `FunLang.Compiler.Cli` binary: reads `.lt` file, invokes LangThree frontend, runs TypedExpr annotation, runs MLIRGen, runs lowering pipeline, invokes clang, produces named executable. Module-level `let` declarations as top-level `func.func` or `@__init` globals.

**Implements:** CLI Driver component; module declaration codegen.

### Phase Ordering Rationale

- Phases 1-2 establish the end-to-end pipeline before building features. Discovering linking failures late (after 6 phases of codegen) is the most common mistake in MLIR compiler projects.
- Phase 3 (TypedExpr) precedes all non-trivial codegen because the annotation it produces is required by Phases 4-6. Building it while the feature set is small makes validation trivial.
- Phases 4 and 5 deliver the v1 success criterion (recursive integer programs) before tackling closures, limiting the scope of the hardest phase.
- Phase 6 (closures) is deliberately isolated. The flat closure representation requires `llvm` dialect struct ops that are separate from `arith`/`cf`; keeping it in its own phase prevents entanglement with control flow debugging.
- Phase 7 is last because CLI wiring is mechanical once codegen is proven.

### Research Flags

Phases likely needing per-phase deeper research during planning:

- **Phase 1 (P/Invoke Infrastructure):** Exact `libMLIR-C.so` symbol names and header locations vary with apt package layout. Verify `libMLIR-C.so` vs `libMLIR.so` symbol split in LLVM 20 before writing P/Invoke declarations. The `mlirParsePassPipeline` string format should be validated against MLIR 20 pass registry names using `mlir-opt --help`.
- **Phase 6 (Closures):** Free-variable analysis implementation in F# and exact `llvm` dialect struct type construction via C API (field GEP indices, alloca sizing). The closure escape analysis heuristic (stack vs. heap allocation) needs a concrete decision: recommend conservative heap-for-all-escaping rule for v1.

Phases with well-documented patterns (skip research-phase):

- **Phase 2 (Arithmetic):** `arith` dialect ops are extensively documented; straightforward P/Invoke emit pattern.
- **Phase 4 (If-Else/Let):** `cf.cond_br` + block argument pattern is covered verbatim in MLIR tutorials.
- **Phase 5 (LetRec known functions):** `func.func` forward declaration strategy is well-understood; minimal novel territory.
- **Phase 7 (CLI):** Pure F# plumbing; no MLIR novelty.

## Confidence Assessment

| Area | Confidence | Notes |
|------|------------|-------|
| Stack | HIGH | All technologies are released and confirmed available; LLVM 20 apt packages verified; only gap is `mlirParsePassPipeline` string format requiring runtime verification |
| Features | HIGH | LangThree AST inspected directly; feature dependencies derived from AST structure, not inference; feature scope matches existing interpreter coverage |
| Architecture | HIGH | Pipeline structure confirmed by MLIR Toy tutorial (official), Jeremy Kun walkthrough, and fsharp-mlir-hello PoC; closure representation confirmed by SpeakEZ Gaining Closure and canonical LLVM IR mapping reference |
| Pitfalls | HIGH for C API ownership and lowering pipeline; MEDIUM for closure implementation details | C API ownership documented in upstream discourse; lowering pass order confirmed by LLVM PR #120548; closure calling convention edge cases (partial application, escaping closures) need phase-level validation |

**Overall confidence:** HIGH

### Gaps to Address

- **`mlirParsePassPipeline` exact string format (MLIR 20):** Run `mlir-opt --help | grep convert-arith` to verify the pass name string before using it in production code. Fallback: use programmatic pass construction API.
- **In-process `mlirTranslateModuleToLLVMIR` integration:** This C API function requires `mlirRegisterAllLLVMTranslations()` to be called first and returns an opaque `LLVMModuleRef`. The exact sequence of calls to go from `LLVMModuleRef` to an object file without spawning external processes is MEDIUM confidence. Tier 2 shell pipeline is HIGH confidence and sufficient for v1 — do not block on this.
- **Stack vs. heap allocation boundary for closures:** The escape analysis heuristic for Phase 6 needs a concrete rule. Recommendation: stack-allocate all closures in v1 (known to be correct for programs that don't return closures from functions); document the limitation; revisit in v2 with proper escape analysis.
- **F# struct layout with `[<Struct>]` and P/Invoke on .NET 10:** The `[<Struct>]` + single `nativeint` field pattern is the standard approach, but .NET 10 may have `[<LibraryImport>]` / source-generated P/Invoke improvements. Verify that `[<DllImport>]` struct-return semantics work correctly for all handle types in Phase 1 smoke tests.

## Sources

### Primary (HIGH confidence)
- [MLIR C API Documentation](https://mlir.llvm.org/docs/CAPI/) — handle ownership, naming conventions, `MlirStringRef`
- [MLIR Toy Tutorial Ch. 2](https://mlir.llvm.org/docs/Tutorials/Toy/Ch-2/) — AST visitor pattern, direct dialect emission
- [MLIR Toy Tutorial Ch. 6](https://mlir.llvm.org/docs/Tutorials/Toy/Ch-6/) — lowering pass pipeline to LLVM
- [MLIR Dialects: func, arith, cf, llvm](https://mlir.llvm.org/docs/Dialects/) — op names, operand types, lowering availability
- [MLIR LLVM IR Target](https://mlir.llvm.org/docs/TargetLLVMIR/) — `mlirTranslateModuleToLLVMIR`, translation registration
- [MLIR Pass Infrastructure](https://mlir.llvm.org/docs/PassManagement/) — `mlirParsePassPipeline`
- [LLVM upstream PR #120548](https://github.com/llvm/llvm-project/pull/120548) — confirms `arith-to-llvm` must be explicit and ordered before `func-to-llvm` in LLVM 20
- [apt.llvm.org](https://apt.llvm.org/) — LLVM 20 package availability confirmed
- [fsharp-mlir-hello PoC](https://github.com/speakeztech/fsharp-mlir-hello) — only known F# + MLIR integration; confirms P/Invoke approach is viable
- LangThree AST/Eval/Type source files (direct inspection) — feature scope, type system, AST node inventory
- MinCaml paper — known-function optimization for let rec
- Matt Might — closure conversion strategies
- Mapping High-Level Constructs to LLVM IR (lambda section) — flat closure struct + fn pointer pattern
- F# P/Invoke / External Functions (Microsoft Learn) — `[<DllImport>]` struct-return semantics

### Secondary (MEDIUM confidence)
- [SpeakEZ — Gaining Closure](https://speakez.tech/blog/gaining-closure/) — flat closure representation in F# + MLIR (no source visible, but pattern matches canonical approach)
- [Jeremy Kun — MLIR Lowering through LLVM](https://www.jeremykun.com/2023/11/01/mlir-lowering-through-llvm/) — practical pass pipeline walkthrough
- [MLIR discourse — Ownership semantics in C API](https://discourse.llvm.org/t/ownership-semantics-in-mlir-c-api/90090) — C-1 pitfall source
- [MLIR discourse — unrealized_conversion_cast](https://discourse.llvm.org/t/how-to-avoid-persistent-unrealized-conversion-cast-s-when-converting-dialects/71721) — C-3 pitfall source
- LLVM Tail Recursion Elimination — TCO feasibility for let rec

### Tertiary (LOW confidence)
- .NET 10 `[<LibraryImport>]` source-generated P/Invoke — benefits over `[<DllImport>]` for struct handles; needs hands-on validation
- In-process LLVM object file emission via `LLVMTargetMachineEmitToMemoryBuffer` — exact API sequence after `mlirTranslateModuleToLLVMIR`; not tested in known F# projects

---
*Research completed: 2026-03-26*
*Ready for roadmap: yes*
