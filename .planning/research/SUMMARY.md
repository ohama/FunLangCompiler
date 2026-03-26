# Project Research Summary

**Project:** LangBackend v2.0 — Data Types & Pattern Matching
**Domain:** Functional language compiler backend (F# / MLIR text emission / LLVM pipeline)
**Researched:** 2026-03-26
**Confidence:** HIGH

## Executive Summary

LangBackend v2.0 extends the existing F# compiler backend that emits MLIR as text strings and pipes through `mlir-opt → mlir-translate → clang` to produce native binaries. The v2 milestone adds the first heap-allocated types to the language: strings, tuples, and lists, plus full pattern matching compilation. The entire implementation surface is confined to four files — `MlirIR.fs`, `Printer.fs`, `Elaboration.fs`, and `Pipeline.fs` — with no new NuGet packages, no new MLIR passes, and no structural change to the existing pipeline. The only external dependency added is libgc (Boehm GC 8.2.12), a single `brew install bdw-gc` or `apt-get install libgc-dev` away.

The recommended approach is strict sequential dependency order: GC integration first (prerequisite for all heap allocation), then strings, tuples, and lists in parallel (all depend on GC but not on each other), then pattern matching last (depends on all three heap types being present for test programs). All heap allocation goes through `GC_malloc` with no exceptions — this prevents the most serious correctness risk (stack-allocated closures being stored in heap containers). Pattern matching compiles to the existing `cf.cond_br`/`cf.br` control flow machinery with no new ops required beyond five additions to `MlirOp` for null pointers, pointer comparisons, and GEP struct access.

The key risk is the interaction between v1's stack-allocated closure environments (`llvm.alloca`) and the new heap types: the moment a closure escapes into a list or tuple, its stack frame is freed and the pointer becomes dangling. The mitigation is clear and must happen in Phase 1: migrate all closure environment allocation from `llvm.alloca` to `GC_malloc` as part of GC integration, before any list or tuple codegen is written.

---

## Key Findings

### Recommended Stack

The v1 stack (F# / .NET 10, LLVM 20 / MLIR 20, `func/arith/cf/llvm` dialects) is unchanged. The only addition is **Boehm GC (libgc 8.2.12)** linked via `-lgc` in the clang step. The integration requires zero MLIR pass changes: new ops (`LlvmCallOp`, `LlvmGEPStructOp`, `LlvmNullOp`, `LlvmIcmpOp`, `LlvmInsertValueOp`, `LlvmExtractValueOp`) are all already in the llvm dialect and pass through the existing lowering pipeline unchanged. No statepoints, no GC strategy plugin, no root enumeration — conservative GC eliminates all of these.

**Core technologies:**
- **Boehm GC 8.2.12 (libgc):** Sole new runtime dependency — conservative collector, zero IR changes, proven in Crystal / Nim / Racket; integrates as two external function declarations plus `-lgc` at link time
- **F# / .NET 10 (unchanged):** Host language for the compiler; no new NuGet packages
- **LLVM 20 / MLIR 20 (unchanged):** Target; new ops are all llvm dialect, no new passes needed
- **xUnit 2.9.3 / FsUnit.xUnit 7.1.1 / FsLit E2E (unchanged):** Extend with new test cases for each heap type

**Critical version note:** Ubuntu 24.04 noble ships libgc 7.x via apt; version 7.x is fine for this use case (GC_malloc + GC_INIT API stable since v7). macOS Homebrew ships 8.2.12. No version pinning is critical.

### Expected Features

**Must have (table stakes) — v2.0 scope:**
- GC integration (Boehm GC_malloc, GC_INIT, -lgc link flag) — prerequisite for all heap types
- Closure environment migration from `llvm.alloca` to `GC_malloc` — prevents use-after-free when closures escape into lists/tuples
- String literals with `{i64 length, ptr data}` two-field heap layout — compatible with printf/strcmp/strlen without marshalling
- `print`, `println`, `to_string`, `string_length`, `string_concat` builtins — libc call wrappers
- Tuple construction (`GC_malloc` + field stores) and `TuplePat` destructuring (GEP + load)
- `EmptyList` as null pointer (`llvm.mlir.zero`), `Cons` as 16-byte two-pointer cons cell
- List literal desugaring to iterated cons in Elaboration (no new IR nodes)
- `Match` expression: full pattern compilation for `VarPat`, `WildcardPat`, `ConstPat`, `EmptyListPat`, `ConsPat`, `TuplePat`
- Non-exhaustive match fallback: `llvm.unreachable` or `@lang_match_failure` abort call

**Should have (add if test programs require it):**
- `when` guard compilation — already in AST; medium complexity; add to Phase 5 if guards appear in test suite
- Or-pattern (`OrPat`) compilation — low priority; rare in v2 test programs
- `GC_malloc_atomic` for string byte arrays — prevents false pointer retention from conservative scanning of byte data

**Defer to v3+:**
- ADTs / discriminated unions (`Constructor`, `ConstructorPat`, `DataValue`)
- Records and field access (`RecordExpr`, `FieldAccess`, `RecordUpdate`)
- Exceptions (`Raise`, `TryWith`)
- `Char` type as compiled type
- `sprintf` format-string builtins beyond basic `print`
- Tagged value representation / unboxed tuple specialization
- Decision tree optimization (Maranget's algorithm) for pattern matching

### Architecture Approach

The existing text-generation architecture (F# DU-based IR serialized to `.mlir` files, processed by shell subprocesses) requires only additive changes. `MlirModule` gains a `GlobalDecls: string list` field for pre-function declarations (GC extern decl, string byte globals). `MlirOp` gains 6 new cases. `ElabEnv` gains 2 new fields (`StringGlobals`, `NeedsGcDecl`). Every other type and function stays structurally unchanged. Printer gains new `printOp` match arms. Elaboration gains new `elaborateExpr` match arms.

**Major components:**
1. **`MlirIR.fs`** — Add `StructType` to `MlirType`; add 6 new `MlirOp` cases; add `GlobalDecls` field to `MlirModule`
2. **`Elaboration.fs`** — Add match arms for `String`, `Tuple`, `EmptyList`, `Cons`, `List`, `LetPat`, `Match`; migrate closure alloca to GC_malloc
3. **`Printer.fs`** — Add `printOp` arms for each new op; emit `GlobalDecls` before FuncOps
4. **`Pipeline.fs`** — Add `-lgc` (and `-L/opt/homebrew/opt/bdw-gc/lib` on macOS) to clang link flags

**Key patterns to follow:**
- Extend DU, do not restructure (every v1 feature used this pattern successfully)
- GEP + LlvmLoadOp for field extraction (reuses existing pattern from closure capture loading)
- `GlobalDecls` as raw strings to avoid over-engineering (promote to typed DU in v3 if needed)
- Match compiles like nested if-else: existing `CfCondBrOp`/`CfBrOp` mechanism, no new control-flow ops

### Critical Pitfalls

1. **Stack-allocated closures escaping into heap containers (C-7)** — When a closure is stored in a list or tuple, its `llvm.alloca`-allocated environment becomes a dangling pointer after the allocating frame returns. Prevention: migrate all closure environment allocation to `GC_malloc` in the GC integration phase, before any list/tuple codegen.

2. **Boehm GC not initialized before first GC_malloc (C-8)** — Omitting `GC_INIT()` produces non-deterministic premature collection that only manifests under allocation pressure. Prevention: emit `llvm.call @GC_INIT()` as the first op in `@main`; verify with `GC_PRINT_STATS=1`.

3. **Mixing malloc and GC_malloc (C-9)** — Plain `malloc`'d memory holding pointers to GC-managed objects is invisible to the collector, causing live objects to be freed. Prevention: use `GC_malloc` for every generated heap allocation without exception; use `GC_malloc_atomic` for byte arrays that contain no pointers (string data).

4. **Pattern match with no default arm (C-10)** — A match expression without an explicit fallback produces undefined behavior when no arm matches. Prevention: always emit a `llvm.unreachable` or `@lang_match_failure` terminal block as the final match arm before writing any match codegen.

5. **GC_malloc size off-by-one (M-11)** — Passing too-small a byte count to `GC_malloc` causes writes past the allocation boundary, corrupting GC bookkeeping. Prevention: define named size constants in codegen (`consSize = 16`, `tupleSize n = n * 8`, `stringHeaderSize = 16`) rather than inline arithmetic.

---

## Implications for Roadmap

All four research files agree on a five-phase structure with strict left-to-right dependency ordering. The phase structure below directly maps the FEATURES.md MVP recommendation, the ARCHITECTURE.md build order, and the PITFALLS.md phase mappings.

### Phase 1: GC Runtime Integration

**Rationale:** Every subsequent phase requires heap allocation. Stack-allocated closures must be migrated to `GC_malloc` before any heap type is added, or closures stored in lists/tuples will produce use-after-free (Pitfall C-7). This phase has no feature dependencies on the others.

**Delivers:** A working Boehm GC runtime integrated into the emitted binary; all future allocations go through `GC_malloc`; closures are safe to store in heap containers.

**Addresses:**
- Add `-lgc` and platform-specific `-L` flag to `Pipeline.fs`
- Add `GC_malloc` / `GC_INIT` extern declarations to `MlirModule.GlobalDecls`
- Add `LlvmCallOp` to `MlirIR` + `Printer`
- Migrate closure environment allocation from `LlvmAllocaOp` to `LlvmCallOp(@GC_malloc)`
- Emit `GC_INIT()` call at start of `@main`
- Use `GC_malloc_atomic` for byte arrays (string data) — no pointer scanning needed

**Avoids:** C-7 (dangling closure pointers), C-8 (uninitialized GC), C-9 (malloc/GC_malloc mixing), M-11 (establish size constants here)

**Research flag:** Well-documented; Boehm GC API is stable and proven. Skip phase research.

---

### Phase 2: Strings

**Rationale:** Strings are needed for `print`/`println` builtins, which appear in virtually every test program. String builtins are simpler than tuples/lists (no recursive structure) and provide a forcing function for validating the GC integration end-to-end.

**Depends on:** Phase 1 (GC_malloc)

**Delivers:** Compiled string literals; `print`, `println`, `to_string`, `string_length`, `string_concat` builtins working end-to-end.

**Addresses:**
- Add `LlvmGlobalConstantOp` (or raw string in GlobalDecls) for static byte arrays
- Add `StringGlobals: (string * string) list ref` and `StringCounter: int ref` to `ElabEnv`
- Elaborate `String` node: global byte array + GC_malloc 16-byte header struct + field stores
- Wire `print`/`println` as `llvm.call @printf`; `string_length` as `llvm.call @strlen`
- String layout: `{i64 length, ptr data}` two-field struct

**Avoids:** M-8 (missing length header breaking string_length and embedded-null strings)

**Research flag:** String literal codegen pattern (global array + pointer-to-it) needs one integration verification. Low risk; standard MLIR pattern.

---

### Phase 3: Tuples

**Rationale:** Tuples are structurally simpler than lists (no recursive pointer chasing, no null check needed) and provide the foundation for `TuplePat` destructuring used in pattern matching. Independent of strings.

**Depends on:** Phase 1 (GC_malloc)

**Delivers:** Tuple construction and `TuplePat` / `LetPat(TuplePat)` destructuring working end-to-end.

**Addresses:**
- Elaborate `Tuple` node: `GC_malloc(n * 8)` + sequential field stores via `LlvmGEPLinearOp` + `LlvmStoreOp`
- Elaborate `LetPat(TuplePat)`: GEP + load per field (reuses existing ops — no new `MlirOp` cases)
- Test: `let (a, b) = (1, 2) in a + b` compiles and exits with 3

**Avoids:** M-10 (tuple pattern decomposition — implement component-by-component from the start), M-11 (size constants)

**Research flag:** Standard pattern; no phase research needed.

---

### Phase 4: Lists

**Rationale:** Lists require null pointer representation (`llvm.mlir.zero`) and introduce recursive pointer structure (cons cells). Must come before pattern matching since `ConsPat`/`EmptyListPat` test programs require actual list values. Independent of strings and tuples.

**Depends on:** Phase 1 (GC_malloc)

**Delivers:** `EmptyList`, `Cons`, `List [...]` literal (desugared to cons) compiled and runnable.

**Addresses:**
- Add `LlvmNullOp` (`llvm.mlir.zero : !llvm.ptr`) to `MlirIR` + `Printer`
- Add `LlvmIcmpOp` for pointer equality comparison (null check, reused in Phase 5)
- Elaborate `EmptyList` as `llvm.mlir.zero : !llvm.ptr`
- Elaborate `Cons(h, t)`: `GC_malloc(16)` + store head at field 0 + store tail at field 1
- Elaborate `List [e1; e2; ...]`: desugar to nested `Cons` with `EmptyList` tail in Elaboration
- Test: list construction and head access

**Avoids:** M-7 (nil/integer-zero collision — type-directed codegen keeps `Ptr` and `I64` in separate SSA slots; null pointer only ever appears in `Ptr`-typed positions), M-11 (size constants)

**Research flag:** No phase research needed.

---

### Phase 5: Pattern Matching

**Rationale:** Pattern matching requires all three heap types to be present for comprehensive test coverage (`ConsPat` needs lists, `TuplePat` needs tuples, `ConstPat(StringConst)` needs strings). Compiles to existing `CfCondBrOp`/`CfBrOp` machinery — no new control-flow ops.

**Depends on:** Phases 2, 3, 4

**Delivers:** `Match` expression fully compiled for all v2 pattern types; `LetPat` as degenerate irrefutable match; non-exhaustive match runtime fallback.

**Addresses:**
- Add `LlvmUnreachableOp` (`llvm.unreachable`) or emit `@lang_match_failure` abort call as match exhaustion terminal
- Elaborate `Match`: sequential clause chain of `cf.cond_br` blocks (same structure as if-else)
- `VarPat`/`WildcardPat`: always succeed, bind/discard; emit `arith.constant 1 : i1`
- `ConstPat(IntConst/BoolConst)`: `arith.cmpi eq` (already available from v1)
- `EmptyListPat`: `LlvmIcmpOp "eq" ptr null` (uses op from Phase 4)
- `ConsPat(hp, tp)`: null-check + GEP-load head/tail + recurse on sub-patterns
- `TuplePat([p1; p2; ...])`: GEP-load each field + recurse on sub-patterns
- `ConstPat(StringConst s)`: `strcmp` call + `arith.cmpi eq` on result
- `LetPat(TuplePat)`: treated as irrefutable single-clause match (no conditional branching)
- Test: `let rec sum lst = match lst with | [] -> 0 | h :: t -> h + sum t in sum [1; 2; 3]` exits with 6

**Avoids:** C-10 (missing default arm — always emit unreachable/abort terminal), M-10 (tuple decomposition — component-by-component from Phase 3 carries forward)

**Research flag:** `when` guard and `OrPat` compilation — add to this phase if any test programs use them; otherwise defer to v3.

---

### Phase Ordering Rationale

- Phase 1 must be first: it is the prerequisite for all heap allocation and contains the closure migration that prevents use-after-free (Pitfall C-7). All other phases are blocked on it.
- Phases 2, 3, 4 are independent of each other and can be planned in any order once Phase 1 is validated with a passing E2E test. The recommended order (strings before tuples/lists) puts I/O-enabling work earliest so test programs can use `print` throughout.
- Phase 5 is last by hard dependency: its test programs need all three heap types.
- The `LlvmIcmpOp` op added in Phase 4 (null pointer check) is reused directly in Phase 5 (`EmptyListPat`). Adding it in Phase 4 rather than Phase 5 keeps Phase 4 self-contained and avoids a forward dependency on Phase 5.

### Research Flags

Phases needing a `/gsd:research-phase` call during planning:
- **None.** All five phases have well-documented patterns at HIGH confidence. The most uncertain areas (string literal global array syntax, `llvm.mlir.zero` vs `llvm.inttoptr` choice, `insertvalue` text format) are low-risk integration questions resolvable with a 15-minute MLIR doc check during implementation, not a full research phase.

Phases with standard well-documented patterns (skip research-phase):
- **Phase 1 (GC Integration):** Boehm GC API is stable since v7; Crystal/Nim/Racket precedent; MLIR llvm.call pattern already used in v1.
- **Phase 2 (Strings):** `llvm.mlir.global` + two-field struct is standard MLIR pattern.
- **Phase 3 (Tuples):** GEP + load already used in v1 for closure capture extraction.
- **Phase 4 (Lists):** Null pointer for nil is standard; cons cell is a two-pointer struct.
- **Phase 5 (Pattern Matching):** Sequential `cf.cond_br` chain is the established simple compilation strategy; reuses existing control flow ops.

---

## Confidence Assessment

| Area | Confidence | Notes |
|------|------------|-------|
| Stack | HIGH | Boehm GC API stable since v7; MLIR llvm dialect ops verified in MLIR 20 docs; no new NuGet packages required |
| Features | HIGH | Feature list derived from direct inspection of LangThree `Ast.fs` and `Eval.fs`; authoritative source, not inferred |
| Architecture | HIGH | v1 architecture verified from actual source files; all new ops are additive DU extensions; change surface is small and well-bounded |
| Pitfalls | HIGH (GC/heap/patterns); MEDIUM (GC-closure interaction) | C-7 (stack closures escaping into heap containers) is the primary correctness risk; mitigation is clear but the exact closure alloca scope in v1 should be confirmed at Phase 1 implementation time |

**Overall confidence:** HIGH

### Gaps to Address

- **GC_INIT requirement:** STACK.md says `GC_INIT()` is needed; ARCHITECTURE.md Anti-Pattern 5 says it is not required (lazy init works). PITFALLS.md C-8 says omitting it causes non-deterministic corruption under allocation pressure. **Resolution: emit GC_INIT() unconditionally in `@main`. Cost is one extra call; risk of omitting is non-deterministic memory corruption.** Flag this for the Phase 1 task.

- **String literal byte array allocation strategy:** Two approaches are documented — `llvm.mlir.global` with pointer-to-global vs `GC_malloc` for the byte array. The recommended path (global byte array + GC_malloc'd header struct pointing into static data) avoids extra allocation for literals but the exact `llvm.mlir.global` MLIR text syntax should be confirmed against MLIR 20 docs during Phase 2 implementation.

- **`insertvalue`/`extractvalue` vs GEP+load/store:** STACK.md documents both. ARCHITECTURE.md concludes GEP+load/store is primary for heap objects. This is consistent and resolved: use GEP+load/store for all heap object field access; `insertvalue`/`extractvalue` only if building SSA register struct values without a memory round-trip (unlikely to be needed in v2).

- **`when` guard and `OrPat` compilation scope:** Both are in the LangThree AST and FEATURES.md lists them as "should have." Neither appears in the v2 critical path test programs. Defer both unless a test program requires them; if needed, add to Phase 5.

---

## Sources

### Primary (HIGH confidence)
- LangThree `Ast.fs` (direct inspection) — authoritative pattern and expression node list
- LangThree `Eval.fs` (direct inspection) — runtime value representations matching compiled layout
- LangThree `MatchCompile.fs` (direct inspection) — decision tree structure for pattern matching
- [MLIR llvm Dialect documentation](https://mlir.llvm.org/docs/Dialects/LLVM/) — `llvm.call`, `llvm.mlir.zero`, `llvm.icmp`, `llvm.getelementptr`, `llvm.mlir.global`, `llvm.unreachable`
- [bdwgc releases (GitHub)](https://github.com/bdwgc/bdwgc/releases) — v8.2.12 confirmed 2025-02-05
- [bdwgc overview (GitHub)](https://github.com/bdwgc/bdwgc/blob/master/docs/overview.md) — GC_malloc / GC_INIT API
- LangBackend v1 source files: `MlirIR.fs`, `Printer.fs`, `Elaboration.fs`, `Pipeline.fs` — actual architecture baseline (HIGH confidence)

### Secondary (MEDIUM confidence)
- [Homebrew bdw-gc formula](https://formulae.brew.sh/formula/bdw-gc) — v8.2.12, arm64+x86_64
- [Debian sid libgc-dev](https://packages.debian.org/sid/libgc-dev) — v1:8.2.12-1
- [llvm-boehmgc-sample (GitHub)](https://github.com/tattn/llvm-boehmgc-sample) — LLVM IR + Boehm GC integration via `-lgc`
- [MLIR Discourse: replacing malloc with custom functions](https://discourse.llvm.org/t/llvm-dialect-replacing-malloc-and-free-with-custom-functions/63481) — custom allocator patterns
- [NuGet FsUnit.xUnit 7.1.1](https://www.nuget.org/packages/FsUnit.Xunit/) and [xunit 2.9.3](https://www.nuget.org/packages/xunit) — version confirmation

### Tertiary (informational)
- OCaml value representation — boxed vs unboxed scalars (context for uniform boxing decision)
- MinCaml heap allocation paper — precedent for GC_malloc struct sizing for tuples and closures

---
*Research completed: 2026-03-26*
*Ready for roadmap: yes*
