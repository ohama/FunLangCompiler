# Roadmap: LangBackend

## Overview

LangBackend compiles LangThree source to native x86-64 binaries via MLIR → LLVM.
The pipeline runs: LangThree AST → Elaboration (F# pass) → MlirIR (compiler-internal F# DU) → Printer → `.mlir` text → `mlir-opt` → `mlir-translate` → `clang` → binary.
MlirIR is the project's own IR that grows phase by phase: scalar ops first, then let/var SSA, then bool/if/cond_br, then FuncOp for known functions, then closure structs for lambdas — with the Printer and shell pipeline introduced in Phase 1 and held stable across all subsequent phases.

## Milestones

- ✅ **v1.0 Core Compiler** - Phases 1-6 (shipped 2026-03-26)
- 🚧 **v2.0 Data Types & Pattern Matching** - Phases 7-11 (in progress)

## Phases

**Phase Numbering:**
- Integer phases (1, 2, 3): Planned milestone work
- Decimal phases (2.1, 2.2): Urgent insertions (marked with INSERTED)

Decimal phases appear between their surrounding integers in numeric order.

<details>
<summary>✅ v1.0 Core Compiler (Phases 1-6) - SHIPPED 2026-03-26</summary>

- [x] **Phase 1: MlirIR Foundation** - MlirIR DU (Region/Block/Op/Value/Type), `.mlir` text printer, mlir-opt/translate/clang shell pipeline, E2E smoke test with hardcoded `return 42`
- [x] **Phase 2: Scalar Codegen via MlirIR** - Elaboration pass introduced for scalar expressions; MlirIR gains scalar arith ops and SSA let/var bindings; integer arithmetic programs compile end-to-end
- [x] **Phase 3: Booleans, Comparisons, Control Flow** - MlirIR extended with bool/comparison ops and cond_br; Elaboration handles bool literals, comparison, short-circuit logic, and if-else
- [x] **Phase 4: Known Functions via Elaboration** - MlirIR extended with FuncOp and direct call; Elaboration handles let rec (no free variables) as forward-declared func.func calls
- [x] **Phase 5: Closures via Elaboration** - MlirIR extended with closure struct representation and indirect call; Elaboration handles lambda capture, closure allocation, and call dispatch
- [x] **Phase 6: CLI** - `.lt` file to native binary CLI driver, module-level declarations

### Phase 1: MlirIR Foundation
**Goal**: The compiler has a typed internal IR (MlirIR) and a working end-to-end pipeline — MlirIR encodes a hardcoded `return 42` program, the Printer serializes it to `.mlir` text, and the shell pipeline produces a runnable ELF binary
**Depends on**: Nothing (first phase)
**Requirements**: INFRA-01, INFRA-02, INFRA-03, INFRA-04, INFRA-05, CLI-02, TEST-01
**Success Criteria** (what must be TRUE):
  1. MlirIR F# DU is defined with types for Region, Block, Op, Value, and Type — the hardcoded `return 42` program can be expressed as an MlirIR value in F# source without any string manipulation
  2. The MlirIR Printer produces a valid `.mlir` text file containing a `func.func @main() -> i64` module that can be opened and read as plain text
  3. `mlir-opt` runs via `System.Diagnostics.Process` with the lowering pipeline and exits 0; `mlir-translate --mlir-to-llvmir` followed by `clang` each exit 0 and produce a runnable ELF binary that exits with code 42
  4. A FsLit `.flt` smoke test file for `return 42` compiles through the full pipeline and verifies the exit code automatically
**Plans**: 3 plans

Plans:
- [x] 01-01-PLAN.md — MlirIR DU (MlirModule/FuncOp/MlirRegion/MlirBlock/MlirOp/MlirValue/MlirType) and LangBackend.Compiler .fsproj with LangThree project reference
- [x] 01-02-PLAN.md — MlirIR Printer (pure string serializer) and shell Pipeline (mlir-opt → mlir-translate → clang via System.Diagnostics.Process)
- [x] 01-03-PLAN.md — LangBackend.Cli entry point and FsLit E2E smoke test (`01-return42.flt`)

### Phase 2: Scalar Codegen via MlirIR
**Goal**: LangThree integer expressions (literals, arithmetic, let bindings, variable references) are elaborated into MlirIR and compile to native binaries that produce correct results — this phase also introduces the Elaboration pass as the canonical LangThree AST → MlirIR translation layer
**Depends on**: Phase 1
**Requirements**: SCALAR-01, SCALAR-02, CTRL-02, CTRL-03, ELAB-01
**MlirIR evolution**: Adds `ArithConstantOp`, `ArithAddIOp`, `ArithSubIOp`, `ArithMulIOp`, `ArithDivSIOp`, and SSA value name binding in Block
**Success Criteria** (what must be TRUE):
  1. The Elaboration pass accepts a LangThree AST node and emits MlirIR ops — it carries type information and produces MlirIR values typed as `i64`
  2. An integer literal `.lt` file compiles end-to-end and the binary exits with the literal value
  3. An arithmetic expression such as `1 + 2 * 3 - 4 / 2` compiles and the binary exits with the correct value (5)
  4. A `let x = 5 in let y = x + 3 in y` program compiles and exits with 8, confirming SSA let binding and variable lookup through the Elaboration pass
**Plans**: 2 plans

Plans:
- [x] 02-01-PLAN.md — Elaboration pass skeleton, MlirIR scalar arith op types + Printer cases, CLI wired to parse .lt files, integer literal FsLit test
- [x] 02-02-PLAN.md — FsLit tests for arithmetic expression (1+2*3-4/2=5) and let/variable SSA binding (let x=5 in let y=x+3 in y=8)

### Phase 3: Booleans, Comparisons, Control Flow
**Goal**: LangThree programs using boolean literals, comparison operators, logical short-circuit operators, and if-else expressions are elaborated into MlirIR and execute correctly
**Depends on**: Phase 2
**Requirements**: SCALAR-03, SCALAR-04, SCALAR-05, CTRL-01
**MlirIR evolution**: Adds `ArithCmpIOp`, `ArithConstantOp i1`, `CfCondBrOp`, and Block argument threading for merge points
**Success Criteria** (what must be TRUE):
  1. `true` and `false` literals compile to `arith.constant i1` and `if true then 1 else 0` exits with 1
  2. All six comparison operators (`=`, `<>`, `<`, `>`, `<=`, `>=`) produce correct results, verified by FsLit tests
  3. `&&` and `||` short-circuit correctly: Elaboration emits `cf.cond_br` control flow rather than eager evaluation
  4. `if n <= 0 then 0 else 1` compiles via `cf.cond_br` + merge block argument and executes correctly for both branches
**Plans**: TBD

Plans:
- [x] 03-01: MlirIR bool/comparison op types and Elaboration for bool literals and comparison ops
- [x] 03-02: Short-circuit logical ops and if-else elaboration via cf.cond_br

### Phase 4: Known Functions via Elaboration
**Goal**: Recursive functions with no free variables beyond the recursion variable itself are elaborated into MlirIR FuncOp nodes and emitted as direct `func.func` calls, enabling factorial and fibonacci programs to compile and produce correct results
**Depends on**: Phase 3
**Requirements**: ELAB-02
**MlirIR evolution**: Adds `FuncOp` (with name, parameters, return type, body Region) and `DirectCallOp`
**Success Criteria** (what must be TRUE):
  1. `let rec fact n = if n <= 1 then 1 else n * fact (n - 1) in fact 10` compiles and the binary exits with 3628800
  2. `let rec fib n = if n <= 1 then n else fib (n - 1) + fib (n - 2) in fib 10` compiles and exits with 55
  3. The emitted `.mlir` text contains a forward-declared `func.func @fact` with a `func.call @fact` recursive call body — Elaboration has annotated the call as `DirectCall` and no closure struct is allocated
  4. FsLit test files for let-rec recursive functions pass
**Plans**: 1 plan

Plans:
- [x] 04-01-PLAN.md — DirectCallOp in MlirIR, LetRec/App elaboration with KnownFuncs, factorial and fibonacci E2E tests

### Phase 5: Closures via Elaboration
**Goal**: Lambda expressions that capture free variables are elaborated into MlirIR closure representations (flat struct with `{fn_ptr, env_fields}`) and emitted with indirect call dispatch, enabling higher-order functions to work correctly
**Depends on**: Phase 4
**Requirements**: ELAB-03, ELAB-04, TEST-02
**MlirIR evolution**: Adds `ClosureAllocOp` (struct layout with fn_ptr and env fields), `IndirectCallOp`, and the Elaboration logic to classify applications as `DirectCall` vs `ClosureCall`
**Success Criteria** (what must be TRUE):
  1. `let add_n n = fun x -> x + n in let add5 = add_n 5 in add5 3` compiles and the binary exits with 8
  2. Every lambda with captured variables produces a `ClosureAllocOp` in MlirIR that the Printer serializes to a flat closure struct `{ fn_ptr, env_fields... }` in the `llvm` dialect — inspection of emitted `.mlir` text confirms no bare function pointer calls for closure-applied functions
  3. Function application dispatch is driven by Elaboration annotation: known `let rec` functions emit `DirectCallOp`, closure values emit `IndirectCallOp` with fn_ptr load from the closure struct
  4. FsLit test files for all feature categories (arithmetic, comparison, if-else, let, let-rec, lambda) pass together
**Plans**: 2 plans

Plans:
- [x] 05-01-PLAN.md — MlirIR closure types (Ptr, 7 LLVM ops, IsLlvmFunc), Printer serialization, Elaboration freeVars + ClosureInfo + Lambda compilation
- [x] 05-02-PLAN.md — App dispatch (DirectCall vs ClosureCall vs IndirectCall) and FsLit closure E2E tests

### Phase 6: CLI
**Goal**: A usable `langbackend <file.lt>` command reads a LangThree source file, runs the full Elaboration → MlirIR → Printer → shell pipeline, and produces a named native executable that the user can run directly
**Depends on**: Phase 5
**Requirements**: CLI-01
**Success Criteria** (what must be TRUE):
  1. Running `langbackend hello.lt` produces a `hello` executable in the current directory
  2. The produced executable runs without dynamic library issues on the target Linux x86-64 system
  3. A compile error in the source file prints a human-readable error message and exits non-zero without producing an incomplete binary
**Plans**: TBD

Plans:
- [x] 06-01: CLI driver (file input, Elaboration + MlirIR pipeline orchestration, output binary naming)

</details>

## v2.0 Phases

**Milestone Goal:** Extend the compiler with heap-allocated types (string, tuple, list) and pattern matching, backed by Boehm GC runtime. Every heap allocation goes through `GC_malloc`. After this milestone, programs using strings, tuples, lists, and `match` expressions compile and produce correct results.

- [ ] **Phase 7: GC Runtime Integration** - Boehm GC linked into emitted binaries; `GC_INIT` emitted in `@main`; v1 closure `llvm.alloca` environments migrated to `GC_malloc`; `print`/`println` builtins wired
- [ ] **Phase 8: Strings** - String literals compiled to heap-allocated `{i64 length, ptr data}` structs; string equality, `string_length`, `string_concat`, `to_string` builtins working end-to-end
- [ ] **Phase 9: Tuples** - Tuple construction compiled to `GC_malloc`'d structs; `let (a, b) = ...` tuple destructuring via GEP + load; `TuplePat` in match working
- [ ] **Phase 10: Lists** - `[]` compiled as null pointer; `h :: t` as GC_malloc'd cons cell; list literal desugaring; list pattern matching via null check + GEP
- [ ] **Phase 11: Pattern Matching** - `match` expression compiled to `cf.cond_br` decision chain for all pattern types; non-exhaustive match runtime fallback emitted

### Phase 7: GC Runtime Integration
**Goal**: Every emitted binary links Boehm GC, calls `GC_INIT()` on startup, and allocates all heap memory through `GC_malloc` — v1 closure environments are migrated off the stack so closures can safely escape into heap containers; `print` and `println` builtins are available for all subsequent test programs
**Depends on**: Phase 6
**Requirements**: GC-01, GC-02, GC-03
**Success Criteria** (what must be TRUE):
  1. A compiled binary that allocates closures and then calls `print` runs without crash; `GC_PRINT_STATS=1` output shows GC was initialized (confirms `GC_INIT` executed before first `GC_malloc`)
  2. The v1 closure E2E FsLit tests (15 tests) all continue to pass after migrating closure environment allocation from `llvm.alloca` to `GC_malloc` — no regressions
  3. A program that returns a closure from a function and applies it after the defining stack frame has returned executes correctly (confirms heap closure environments do not become dangling)
  4. `print "hello"` and `println "world"` compile and write the expected strings to stdout in a FsLit E2E test
**Plans**: TBD

Plans:
- [ ] 07-01: Add `-lgc` / platform `-L` flag to Pipeline.fs; add `GC_INIT` + `GC_malloc` extern declarations to MlirModule.GlobalDecls; emit `GC_INIT()` call at start of `@main`; add `LlvmCallOp` to MlirIR + Printer
- [ ] 07-02: Migrate closure environment allocation from `LlvmAllocaOp` to `LlvmCallOp(@GC_malloc)` in Elaboration; verify v1 FsLit tests pass
- [ ] 07-03: Elaborate `print` / `println` builtins as `llvm.call @printf`; FsLit E2E tests for print output

### Phase 8: Strings
**Goal**: String literals compile to heap-allocated `{i64 length, ptr data}` two-field structs managed by Boehm GC; the string builtins `print`, `println`, `string_length`, `string_concat`, and `to_string` all work in compiled programs
**Depends on**: Phase 7
**Requirements**: STR-01, STR-02, STR-03, STR-04, STR-05
**Success Criteria** (what must be TRUE):
  1. `let s = "hello" in string_length s` compiles and the binary exits with 5, confirming the length field is set correctly during string literal elaboration
  2. `"abc" = "abc"` evaluates to true and `"abc" = "def"` evaluates to false in a compiled program, confirming `strcmp`-based equality is wired correctly
  3. `string_concat "foo" "bar"` returns a new string whose length is 6 and whose content equals `"foobar"` — verified by a FsLit test that prints the result
  4. `to_string 42` returns `"42"` and `to_string true` returns `"true"` — FsLit tests compile and print the expected values
**Plans**: TBD

Plans:
- [ ] 08-01: Add `StructType` to `MlirType`; add `LlvmGlobalConstantOp` / `GlobalDecls` byte array emission; elaborate `String` node to GC_malloc'd header struct with length + data fields; STR-01 FsLit test
- [ ] 08-02: Elaborate `=` / `<>` on strings as `strcmp` calls; elaborate `string_length`, `string_concat`, `to_string` builtins; STR-02..05 FsLit tests

### Phase 9: Tuples
**Goal**: Tuple expressions compile to `GC_malloc`'d structs with N pointer-sized fields; `let (a, b) = expr` and `TuplePat` destructuring in `match` both produce correct results via GEP + load
**Depends on**: Phase 7
**Requirements**: TUP-01, TUP-02, TUP-03
**Success Criteria** (what must be TRUE):
  1. `let t = (3, 4) in let (a, b) = t in a + b` compiles and the binary exits with 7, confirming tuple construction and `LetPat` destructuring are both correct
  2. A nested tuple `let t = (1, (2, 3)) in let (x, inner) = t in let (y, z) = inner in x + y + z` compiles and exits with 6, confirming recursive GEP field access
  3. The emitted `.mlir` for a tuple construction contains a `GC_malloc` call with the correct byte count (N * 8 for N fields) — no `llvm.alloca` for tuple storage
**Plans**: TBD

Plans:
- [ ] 09-01: Elaborate `Tuple` node as `GC_malloc(n*8)` + sequential `LlvmGEPStructOp` + `LlvmStoreOp` field stores; elaborate `LetPat(TuplePat)` as GEP + load per field; FsLit E2E tests for TUP-01, TUP-02, TUP-03

### Phase 10: Lists
**Goal**: The empty list compiles to a null pointer (`llvm.mlir.zero`), cons cells compile to `GC_malloc`'d 16-byte two-pointer structs, and list literal syntax desugars to nested cons in Elaboration — list construction and head/tail access work in compiled programs
**Depends on**: Phase 7
**Requirements**: LIST-01, LIST-02, LIST-03, LIST-04
**Success Criteria** (what must be TRUE):
  1. `let lst = [1; 2; 3] in lst` compiles without error — the list literal desugars to `1 :: (2 :: (3 :: []))` and each cons cell is heap-allocated
  2. A recursive `let rec length lst = match lst with | [] -> 0 | _ :: t -> 1 + length t in length [1; 2; 3]` compiles and the binary exits with 3, confirming null-pointer nil check and cons cell GEP are correct
  3. The emitted `.mlir` for `[]` contains `llvm.mlir.zero : !llvm.ptr` — no integer zero cast to pointer
  4. A FsLit E2E test for list construction and recursive traversal passes end-to-end
**Plans**: TBD

Plans:
- [ ] 10-01: Add `LlvmNullOp` + `LlvmIcmpOp` to MlirIR + Printer; elaborate `EmptyList` as `llvm.mlir.zero`; elaborate `Cons(h, t)` as `GC_malloc(16)` + head/tail field stores; elaborate `List [...]` as nested cons desugaring; FsLit E2E tests for LIST-01..04

### Phase 11: Pattern Matching
**Goal**: The `match` expression compiles to a sequential `cf.cond_br` decision chain that handles all v2 pattern types (constant, wildcard, variable, string, empty list, cons, tuple); a non-exhaustive match always has a `@lang_match_failure` fallback block so undefined behavior is impossible
**Depends on**: Phases 8, 9, 10
**Requirements**: PAT-01, PAT-02, PAT-03, PAT-04, PAT-05
**Success Criteria** (what must be TRUE):
  1. `match 42 with | 0 -> "zero" | 42 -> "answer" | _ -> "other"` compiles and the binary prints `"answer"`, confirming constant int pattern and wildcard pattern both work
  2. `let rec sum lst = match lst with | [] -> 0 | h :: t -> h + sum t in sum [1; 2; 3]` compiles and exits with 6, confirming `EmptyListPat` null check and `ConsPat` destructuring work together with recursive calls
  3. `match (1, 2) with | (a, b) -> a + b` compiles and exits with 3, confirming `TuplePat` GEP-load field extraction in match works
  4. `match "hello" with | "hello" -> 1 | _ -> 0` compiles and exits with 1, confirming `strcmp`-based string constant pattern comparison works
  5. A program with a non-exhaustive match on integers (no default arm) links and calls `@lang_match_failure` at runtime when no arm matches — the process exits non-zero rather than producing undefined behavior
**Plans**: TBD

Plans:
- [ ] 11-01: Add `LlvmUnreachableOp` to MlirIR + Printer; elaborate `Match` as sequential `CfCondBrOp` chain; `VarPat`/`WildcardPat` always-true arm; `ConstPat(IntConst/BoolConst)` via `arith.cmpi eq`; emit `@lang_match_failure` terminal block; PAT-01, PAT-02, PAT-04, PAT-05 FsLit tests
- [ ] 11-02: `ConstPat(StringConst)` via `strcmp` + `arith.cmpi eq`; `EmptyListPat` via `LlvmIcmpOp null`; `ConsPat` via null-check + GEP head/tail + recursive sub-pattern; `TuplePat` in match via GEP-load + recursive sub-pattern; PAT-03 + comprehensive multi-pattern FsLit tests

## Phase Details (v1.0)

*See collapsed section above for v1.0 phase details.*

## Progress

**Execution Order:**
Phases execute in numeric order: 1 → 2 → 3 → 4 → 5 → 6 → 7 → 8 → 9 → 10 → 11

| Phase | Milestone | Plans Complete | Status | Completed |
|-------|-----------|----------------|--------|-----------|
| 1. MlirIR Foundation | v1.0 | 3/3 | Complete | 2026-03-26 |
| 2. Scalar Codegen via MlirIR | v1.0 | 2/2 | Complete | 2026-03-26 |
| 3. Booleans, Comparisons, Control Flow | v1.0 | 2/2 | Complete | 2026-03-26 |
| 4. Known Functions via Elaboration | v1.0 | 1/1 | Complete | 2026-03-26 |
| 5. Closures via Elaboration | v1.0 | 2/2 | Complete | 2026-03-26 |
| 6. CLI | v1.0 | 1/1 | Complete | 2026-03-26 |
| 7. GC Runtime Integration | v2.0 | 0/TBD | Not started | - |
| 8. Strings | v2.0 | 0/TBD | Not started | - |
| 9. Tuples | v2.0 | 0/TBD | Not started | - |
| 10. Lists | v2.0 | 0/TBD | Not started | - |
| 11. Pattern Matching | v2.0 | 0/TBD | Not started | - |
