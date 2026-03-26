# Roadmap: LangBackend

## Overview

LangBackend compiles LangThree source to native x86-64 binaries via MLIR → LLVM.
The pipeline runs: LangThree AST → Elaboration (F# pass) → MlirIR (compiler-internal F# DU) → Printer → `.mlir` text → `mlir-opt` → `mlir-translate` → `clang` → binary.
MlirIR is the project's own IR that grows phase by phase: scalar ops first, then let/var SSA, then bool/if/cond_br, then FuncOp for known functions, then closure structs for lambdas — with the Printer and shell pipeline introduced in Phase 1 and held stable across all subsequent phases.

## Phases

**Phase Numbering:**
- Integer phases (1, 2, 3): Planned milestone work
- Decimal phases (2.1, 2.2): Urgent insertions (marked with INSERTED)

Decimal phases appear between their surrounding integers in numeric order.

- [x] **Phase 1: MlirIR Foundation** - MlirIR DU (Region/Block/Op/Value/Type), `.mlir` text printer, mlir-opt/translate/clang shell pipeline, E2E smoke test with hardcoded `return 42`
- [x] **Phase 2: Scalar Codegen via MlirIR** - Elaboration pass introduced for scalar expressions; MlirIR gains scalar arith ops and SSA let/var bindings; integer arithmetic programs compile end-to-end
- [x] **Phase 3: Booleans, Comparisons, Control Flow** - MlirIR extended with bool/comparison ops and cond_br; Elaboration handles bool literals, comparison, short-circuit logic, and if-else
- [x] **Phase 4: Known Functions via Elaboration** - MlirIR extended with FuncOp and direct call; Elaboration handles let rec (no free variables) as forward-declared func.func calls
- [ ] **Phase 5: Closures via Elaboration** - MlirIR extended with closure struct representation and indirect call; Elaboration handles lambda capture, closure allocation, and call dispatch
- [ ] **Phase 6: CLI** - `.lt` file to native binary CLI driver, module-level declarations

## Phase Details

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
- [ ] 01-01-PLAN.md — MlirIR DU (MlirModule/FuncOp/MlirRegion/MlirBlock/MlirOp/MlirValue/MlirType) and LangBackend.Compiler .fsproj with LangThree project reference
- [ ] 01-02-PLAN.md — MlirIR Printer (pure string serializer) and shell Pipeline (mlir-opt → mlir-translate → clang via System.Diagnostics.Process)
- [ ] 01-03-PLAN.md — LangBackend.Cli entry point and FsLit E2E smoke test (`01-return42.flt`)

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
- [ ] 05-01-PLAN.md — MlirIR closure types (Ptr, 7 LLVM ops, IsLlvmFunc), Printer serialization, Elaboration freeVars + ClosureInfo + Lambda compilation
- [ ] 05-02-PLAN.md — App dispatch (DirectCall vs ClosureCall vs IndirectCall) and FsLit closure E2E tests

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
- [ ] 06-01: CLI driver (file input, Elaboration + MlirIR pipeline orchestration, output binary naming)

## Progress

**Execution Order:**
Phases execute in numeric order: 1 → 2 → 3 → 4 → 5 → 6

| Phase | Plans Complete | Status | Completed |
|-------|----------------|--------|-----------|
| 1. MlirIR Foundation | 3/3 | Complete | 2026-03-26 |
| 2. Scalar Codegen via MlirIR | 2/2 | Complete | 2026-03-26 |
| 3. Booleans, Comparisons, Control Flow | 2/2 | Complete | 2026-03-26 |
| 4. Known Functions via Elaboration | 1/1 | Complete | 2026-03-26 |
| 5. Closures via Elaboration | 0/2 | Not started | - |
| 6. CLI | 0/1 | Not started | - |
