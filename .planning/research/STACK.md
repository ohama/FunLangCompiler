# Technology Stack: LangBackend v2.0 — Data Types & Pattern Matching

**Project:** LangBackend — LangThree MLIR → LLVM → native binary compiler
**Milestone:** v2.0 Data Types & Pattern Matching
**Researched:** 2026-03-26
**Confidence:** HIGH for Boehm GC integration and MLIR llvm dialect ops; HIGH for struct/list representation; HIGH for pattern matching compilation strategy

---

## Scope of This Document

This document covers ONLY stack additions and changes required for v2.0. The existing v1 stack (F# / .NET 10, LLVM 20 / MLIR 20, func/arith/cf/llvm dialects, shell pipeline via mlir-opt → mlir-translate → clang) remains unchanged. Each section states what changes and why.

---

## What v2 Adds to the Stack

### Summary Table

| Category | What Changes | Why |
|----------|-------------|-----|
| Runtime library | Add libgc (Boehm GC) as link dependency | First heap allocation in the project; GC_malloc replaces stack alloca for heap objects |
| MLIR MlirType | Add `Struct` variant to `MlirType` DU | Tuples and cons cells are anonymous llvm structs |
| MLIR MlirOp | Add 6 new ops: `LlvmCallOp`, `LlvmGEPStructOp`, `LlvmNullOp`, `LlvmIcmpOp`, `LlvmInsertValueOp`, `LlvmExtractValueOp` | Heap allocation call, struct field access/construction, null pointer, pointer comparison |
| Elaboration | Add string/tuple/list elaboration + pattern match compilation | New AST constructors need codegen |
| Pipeline (clang step) | Add `-lgc` to clang link flags | Link the Boehm GC runtime |
| Test infrastructure | No new test framework; extend FsLit E2E tests | Existing xUnit + FsUnit 7.1.1 is sufficient |

---

## 1. Boehm GC (libgc / bdwgc)

### Recommended: libgc 8.2.12 via system package manager

**Version:** 8.2.12 (released 2025-02-05, latest stable as of 2026-03-26)

**Why Boehm GC:**
- Conservative collector: scans the C stack for pointers without compiler cooperation. Works with any LLVM-generated code because LLVM-compiled functions are normal C-ABI functions on the stack — the GC can find roots without statepoints or safepoints.
- Zero IR changes: GC integration is purely a linker concern and a change to the allocation call emitted. No MLIR pass, no safepoint insertion, no GC strategy annotation needed.
- Industry precedent: Used by Crystal, Racket, Chicken Scheme, Nim, and Mercury as the GC for compiled languages. Well-understood for this exact use case.
- Simple API: `GC_INIT()` once at startup, then `GC_malloc(size)` everywhere `malloc` would be used.

**Why NOT LLVM's built-in GC framework (`llvm.gcroot`):**
LLVM's GC framework requires statepoint insertion, a custom GC strategy plugin, and stack-map emission. This is a significant compiler engineering undertaking. Boehm GC needs none of this — the conservative scan handles root identification automatically.

### Installation

**Linux (Ubuntu/Debian including WSL2):**
```bash
sudo apt-get install libgc-dev libgc1
# Provides: /usr/lib/x86_64-linux-gnu/libgc.so, /usr/include/gc/gc.h
```

Debian sid ships version 1:8.2.12-1. Ubuntu noble (24.04) ships a slightly older version; for 8.2.x use the upstream PPA or build from source if needed. The apt version (7.x on older Ubuntu LTS) works fine — the core API (`GC_malloc`, `GC_INIT`) has been stable since version 7.

**macOS (Homebrew, arm64 + x86_64):**
```bash
brew install bdw-gc
# Installs to /opt/homebrew/opt/bdw-gc/ (arm64) or /usr/local/opt/bdw-gc/ (x86_64)
# Provides: libgc.dylib, include/gc/gc.h
```

### Integration: Zero changes to the MLIR pipeline

The integration requires exactly two changes:

**Change 1 — Pipeline.fs: Add `-lgc` to the clang link step**
```fsharp
// Before:
let clangArgs = sprintf "-Wno-override-module %s -o %s" llFile outputPath
// After:
let clangArgs = sprintf "-Wno-override-module %s -lgc -o %s" llFile outputPath
```

If libgc is not on the default library search path (macOS Homebrew), also pass `-L/opt/homebrew/opt/bdw-gc/lib`.

**Change 2 — Emitted MLIR: Declare and call GC_malloc / GC_INIT**

The Elaboration pass emits declarations for external C functions as `llvm.func` bodies with no region (external declarations), then calls them with `llvm.call`. This is identical to how the existing code calls other external C functions — no new mechanism is required.

In MLIR text format, the necessary external declarations look like:
```mlir
// External GC API declarations — emitted once per module
llvm.func @GC_init() -> ()
llvm.func @GC_malloc(i64) -> !llvm.ptr
```

And a typical GC_malloc call:
```mlir
// Allocate a string struct: { i64 length, ptr bytes }
%size = arith.constant 16 : i64
%ptr  = llvm.call @GC_malloc(%size) : (i64) -> !llvm.ptr
```

`GC_INIT()` is called once from the MLIR-generated `@main` function before any allocation. The conservative GC finds all GC roots automatically — no `gcroot` annotations or statepoints needed.

### What NOT to do

- Do NOT use LLVM's GC statepoint / gcroot / safepoint machinery. It requires a custom GC strategy plugin and stack-map emission — a multi-week undertaking that Boehm GC makes entirely unnecessary.
- Do NOT use `memref.alloc` + memref-to-llvm lowering with custom allocator names. That path goes through the memref dialect, which adds `--convert-memref-to-llvm` to the pass pipeline and couples the lowering to memref semantics. Emitting `llvm.call @GC_malloc` directly in the llvm dialect is simpler and matches the existing closure allocation pattern exactly.

---

## 2. MLIR Type Extensions (MlirType DU)

### New variant: `Struct`

The existing `MlirType` DU currently has `I64 | I32 | I1 | Ptr`. All heap-allocated data in v2 is represented as opaque `!llvm.ptr` at the use site (Boehm GC is pointer-untyped). However, the Printer needs to emit correct struct type annotations in `llvm.getelementptr` and `llvm.alloca` for known-layout allocations.

Add one variant:
```fsharp
type MlirType =
    | I64
    | I32
    | I1
    | Ptr        // !llvm.ptr — opaque pointer (existing, used everywhere)
    | StructType of MlirType list  // !llvm.struct<(T1, T2, ...)> — for GEP type annotations
```

`StructType` is used exclusively in GEP type arguments and alloca element type positions. Values passed between ops always use `Ptr` (consistent with the LLVM 20 opaque pointer convention already used in v1).

**Printer mapping:**
```fsharp
| StructType fields ->
    let inner = fields |> List.map printType |> String.concat ", "
    sprintf "!llvm.struct<(%s)>" inner
```

---

## 3. New MLIR Ops (MlirOp DU additions)

Six new operations cover the entire v2 codegen surface. All are in the llvm dialect — no new dialect is needed.

### 3.1 LlvmCallOp — General external function call

```fsharp
| LlvmCallOp of result: MlirValue option * callee: string * args: MlirValue list * retType: MlirType option
```

Covers: `GC_malloc`, `GC_init`, and any future runtime calls (string operations, etc.).

Emitted text:
```mlir
// With result:
%ptr = llvm.call @GC_malloc(%size) : (i64) -> !llvm.ptr
// Void (no result):
llvm.call @GC_init() : () -> ()
```

**Why this is distinct from the existing `DirectCallOp`:** `DirectCallOp` uses `func.call` syntax (for func dialect functions). External C runtime functions must be called via `llvm.call` inside `llvm.func` bodies, consistent with v1's `IndirectCallOp`.

### 3.2 LlvmGEPStructOp — Struct field pointer

```fsharp
| LlvmGEPStructOp of result: MlirValue * basePtr: MlirValue * fieldIndex: int * structType: MlirType
```

Returns a pointer to the field at `fieldIndex` within the struct at `basePtr`.

Emitted text:
```mlir
// Get pointer to field 1 of a 2-field struct {i64, ptr}:
%fptr = llvm.getelementptr %base[0, 1] : (!llvm.ptr) -> !llvm.ptr, !llvm.struct<(i64, !llvm.ptr)>
```

The leading `0` dereferences the outer pointer (standard GEP convention); the field index follows. MLIR's `llvm.getelementptr` requires struct indices to be compile-time constants — this constraint is always satisfied in v2 because field indices are always known statically.

**Why not reuse LlvmGEPLinearOp:** The existing `LlvmGEPLinearOp` emits linear (array-element) GEP: `%ptr[N] : (!llvm.ptr) -> !llvm.ptr, i64`. Struct GEP needs a struct type annotation and two indices (`[0, fieldIndex]`). They are structurally different enough to warrant separate DU cases.

### 3.3 LlvmNullOp — Null pointer constant

```fsharp
| LlvmNullOp of result: MlirValue
```

Emitted text:
```mlir
%null = llvm.mlir.zero : !llvm.ptr
```

Used to represent the empty list (`[]`) and as the tail of the last cons cell.

### 3.4 LlvmIcmpOp — Integer/pointer comparison

```fsharp
| LlvmIcmpOp of result: MlirValue * predicate: string * lhs: MlirValue * rhs: MlirValue
```

Emitted text:
```mlir
%is_null = llvm.icmp "eq" %ptr, %null : !llvm.ptr
```

Used for empty-list check in pattern matching. Predicates: `"eq"`, `"ne"`, `"slt"`, etc. Returns `i1`.

Note: The existing `ArithCmpIOp` (`arith.cmpi`) works for integer comparisons but cannot compare `!llvm.ptr` operands. `llvm.icmp` handles both integer and pointer comparisons.

### 3.5 LlvmInsertValueOp — Insert field into struct value

```fsharp
| LlvmInsertValueOp of result: MlirValue * aggregate: MlirValue * value: MlirValue * index: int
```

Emitted text:
```mlir
%s1 = llvm.insertvalue %field, %undef[0] : !llvm.struct<(i64, !llvm.ptr)>
```

Used to build struct values in registers. Combined with `llvm.mlir.undef` as the initial aggregate.

### 3.6 LlvmExtractValueOp — Extract field from struct value

```fsharp
| LlvmExtractValueOp of result: MlirValue * aggregate: MlirValue * index: int
```

Emitted text:
```mlir
%len = llvm.extractvalue %str[0] : !llvm.struct<(i64, !llvm.ptr)>
```

Used when destructuring a loaded struct (e.g., reading the length or data pointer from a string).

**Alternative considered — always use GEP + load:** Instead of `extractvalue`/`insertvalue`, one can always GEP to get a field pointer and then load/store. Both approaches work. `insertvalue`/`extractvalue` are more idiomatic for value-typed struct operations; GEP+load/store are more idiomatic for pointer-typed access on heap-allocated objects. In v2, heap objects are always accessed via pointer, so **GEP + LlvmLoadOp/LlvmStoreOp is the primary pattern**. `insertvalue`/`extractvalue` are needed only for building struct values in SSA registers without a memory round-trip.

---

## 4. Heap-Allocated Data Type Representations

These are not new stack items — they are decisions about how to use the existing + new llvm dialect ops. Documented here so roadmap phases can make specific codegen choices.

### 4.1 Strings

**Layout:** `!llvm.struct<(i64, !llvm.ptr)>` — length (bytes) followed by pointer to UTF-8 bytes.

```
struct LangString { int64_t length; char* data; }
```

- The `data` bytes are a separate GC_malloc'd allocation. The struct itself is also GC_malloc'd.
- Strings are immutable in LangThree — no copy-on-write needed.
- String literals are emitted as MLIR global bytes + a runtime allocation copy, OR directly inlined as `llvm.mlir.constant` byte sequences. The simpler path for v2: emit a global null-terminated byte array and a string struct pointing to it. Since the global lives in static memory, no separate GC allocation for the bytes is needed for literals.

### 4.2 Tuples

**Layout:** `!llvm.struct<(T1, T2, ...)>` where each field matches the element type.

For the common case where all fields are int (`i64`) or pointer (`!llvm.ptr`), this is a flat inline struct. For v2, all non-scalar values are represented as `!llvm.ptr` (boxed). So a 2-tuple of `(int, list)` is `!llvm.struct<(i64, !llvm.ptr)>`.

- Tuples are GC_malloc'd on the heap.
- Size = sum of field sizes (all i64 or ptr, both 8 bytes on x86-64/arm64).
- Field access via LlvmGEPStructOp + LlvmLoadOp.

### 4.3 Lists (cons cells)

**Layout:** Two representations:

```
Empty list:  !llvm.ptr where the pointer is null (llvm.mlir.zero)
Cons cell:   !llvm.struct<(!llvm.ptr, !llvm.ptr)>  — head (boxed value ptr), tail (next cons or null)
```

Head values that are scalars (int, bool) must be boxed as `!llvm.struct<(i64)>` or stored in a tagged representation. Simplest v2 approach: represent all list element values as `!llvm.ptr` to a GC_malloc'd box containing the actual value (uniform representation). This avoids a type-dispatch problem at the cost of one extra indirection.

- `h :: t` allocates a new cons cell via `GC_malloc(16)` (two pointers = 16 bytes on 64-bit), stores head ptr in field 0, tail ptr in field 1.
- `[]` is represented as `llvm.mlir.zero : !llvm.ptr`.
- Empty-list check: `llvm.icmp "eq" %list, %null : !llvm.ptr`.

### 4.4 Pattern Matching Compilation Strategy

Pattern matching compiles to a sequence of `cf.cond_br` / `cf.br` blocks — the same block-based control flow already used for `if-else`. No new ops or passes are needed.

**Compilation recipe:**

For `match scrutinee with | pat1 -> e1 | pat2 -> e2 | ... | _ -> eN`:

1. Elaborate `scrutinee` to get a value `%scrut`.
2. For each clause, generate a "test block" that checks whether `%scrut` matches the pattern:
   - `VarPat x` — always succeeds; binds `%scrut` to `x` in the subsequent env.
   - `WildcardPat` — always succeeds.
   - `EmptyListPat` — emit `llvm.icmp "eq" %scrut, %null : !llvm.ptr`; branch on result.
   - `ConsPat(hPat, tPat)` — check non-null, then GEP-load head/tail pointers; recurse for sub-patterns.
   - `TuplePat(pats)` — GEP-load each field; recurse for sub-patterns.
   - `ConstPat(IntConst n)` — emit `arith.cmpi "eq" %scrut, %c_n : i64`.
   - `ConstPat(BoolConst b)` — same with i1.
3. Each test block on success branches to a "body block" that evaluates the arm expression.
4. Each test block on failure branches to the next clause's test block.
5. The last clause is the fallthrough (exhaustive by type checker; no runtime error needed for v2).

This is the classic "sequential search" pattern match compilation. It is O(clauses * depth) in the worst case, which is acceptable for v2. Decision-tree optimization (Maranget's algorithm) is a v3+ concern.

**Key insight:** Pattern matching requires no new MLIR ops beyond those already listed (LlvmIcmpOp, LlvmNullOp, LlvmGEPStructOp, existing ArithCmpIOp, CfCondBrOp, CfBrOp). The block structure already supports multiple-arm control flow.

---

## 5. Updated Pass Pipeline

The lowering pass pipeline does NOT change for v2. The existing:

```
--convert-arith-to-llvm --convert-cf-to-llvm --convert-func-to-llvm --reconcile-unrealized-casts
```

continues to work. New ops (LlvmCallOp, LlvmGEPStructOp, etc.) are all already in the llvm dialect — they pass through the pipeline unchanged and are translated directly by `mlir-translate --mlir-to-llvmir`.

The only pipeline change is in the final clang invocation:

```fsharp
// Pipeline.fs — update clang step
let clangArgs =
    let gcLib =
        if File.Exists "/opt/homebrew/opt/bdw-gc/lib/libgc.dylib" then
            "-L/opt/homebrew/opt/bdw-gc/lib -lgc"
        elif File.Exists "/usr/lib/x86_64-linux-gnu/libgc.so" then
            "-lgc"
        else
            "-lgc"  // hope it's on LD_LIBRARY_PATH
    sprintf "-Wno-override-module %s %s -o %s" llFile gcLib outputPath
```

---

## 6. No New F# / .NET Dependencies

All v2 features are implemented in the F# code generator (Elaboration.fs, MlirIR.fs, Printer.fs, Pipeline.fs). No new NuGet packages are needed.

The existing testing infrastructure suffices:
- **xUnit 2.9.3** — unit tests for new elaboration logic
- **FsUnit.xUnit 7.1.1** — F# assertion DSL
- **FsLit E2E tests** — extend with new `.lt` test files for string/tuple/list/pattern-match scenarios

---

## 7. MlirIR.fs Changes Summary

```fsharp
// MlirType additions
type MlirType =
    | I64 | I32 | I1 | Ptr        // unchanged
    | StructType of MlirType list  // NEW: !llvm.struct<(...)>

// MlirOp additions (v2)
type MlirOp =
    // ... existing ops unchanged ...
    | LlvmCallOp         of result: MlirValue option * callee: string * args: MlirValue list * retType: MlirType option
    | LlvmGEPStructOp    of result: MlirValue * basePtr: MlirValue * fieldIndex: int * structType: MlirType
    | LlvmNullOp         of result: MlirValue
    | LlvmIcmpOp         of result: MlirValue * predicate: string * lhs: MlirValue * rhs: MlirValue
    | LlvmInsertValueOp  of result: MlirValue * aggregate: MlirValue * value: MlirValue * index: int
    | LlvmExtractValueOp of result: MlirValue * aggregate: MlirValue * index: int
    | LlvmUndefOp        of result: MlirValue  // llvm.mlir.undef — initial value for insertvalue chains
```

`LlvmUndefOp` emits `%v = llvm.mlir.undef : <type>`, used as the seed for `insertvalue` when building struct values in registers.

---

## 8. Alternatives Considered

| Category | Recommended | Alternative | Why Not |
|----------|-------------|-------------|---------|
| GC | Boehm GC (conservative, zero IR changes) | LLVM statepoint / precise GC | Requires safepoint insertion pass, custom GC strategy plugin, stack map emission — 3–4 weeks of work vs. 1 day for Boehm |
| GC | Boehm GC | Reference counting | Requires explicit rc inc/dec at every assignment; breaks with cycles; substantial codegen complexity |
| GC | Boehm GC | No GC (just leak) | Works for short-lived programs; unacceptable for list recursion |
| Heap allocation in MLIR | `llvm.call @GC_malloc` directly | `memref.alloc` with custom allocator | memref path adds `--convert-memref-to-llvm` pass + memref dialect semantics; unnecessary complexity; llvm.call is direct and already proven (v1 uses llvm.call for IndirectCallOp) |
| String representation | `{length, data_ptr}` struct | Null-terminated C string | No length = O(n) strlen; breaks for strings with embedded NUL; not safe |
| String representation | `{length, data_ptr}` struct | Fat pointer inline string (length + bytes in one alloc) | Requires variable-size alloc `GC_malloc(8 + length)`; fine but slightly more complex GEP |
| List representation | Null pointer for empty | Tagged integer (0x0 = empty) | Null pointer is the standard and is directly comparable with `llvm.icmp "eq" %p, %zero` |
| List head boxing | Uniform `!llvm.ptr` to boxed value | Tagged value (pointer/integer in same word) | Tag-checking requires bit operations; uniform boxing is simpler and sufficient for v2 correctness |
| Pattern matching | Sequential search (cf.cond_br chain) | Decision tree (Maranget's algorithm) | Decision tree is more efficient but requires a separate compilation pass; sequential is correct and simple; v3 optimization |
| Struct field access | GEP + load/store | llvm.extractvalue / llvm.insertvalue on non-pointer struct | On heap objects, GEP+load/store is the right approach; extractvalue/insertvalue are for value-typed SSA aggregates |
| scf dialect | Not used (stay with cf) | scf.while for list traversal | scf adds a pass dependency (--convert-scf-to-cf); cf.br loops are equivalent and already supported |

---

## 9. Installation Summary for v2

### Linux (Ubuntu 24.04 noble / WSL2)

```bash
# libgc — only new dependency
sudo apt-get install libgc-dev libgc1

# Verify
ls /usr/lib/x86_64-linux-gnu/libgc.so*
# or
ls /usr/lib/aarch64-linux-gnu/libgc.so*   # arm64
```

### macOS (arm64 / Homebrew)

```bash
brew install bdw-gc
# Verify
ls /opt/homebrew/opt/bdw-gc/lib/libgc.dylib
```

### Compiler build — no changes

No new NuGet packages. No changes to the .fsproj files. The only code changes are in the four compiler source files.

---

## Confidence Assessment

| Area | Confidence | Notes |
|------|------------|-------|
| Boehm GC API + integration | HIGH | API has been stable since version 7; GC_malloc + GC_init is the entire integration surface; confirmed by Crystal, Nim, Racket using the same approach |
| llvm dialect external func call syntax | HIGH | `llvm.func @name(args) -> ret` (no body) + `llvm.call @name(args) : (types) -> ret` confirmed in MLIR 20 docs |
| GEP struct syntax `[0, fieldIndex]` | HIGH | Confirmed in MLIR docs and examples: `llvm.getelementptr %p[0,1] : (!llvm.ptr) -> !llvm.ptr, !llvm.struct<(i32, f32)>` |
| `llvm.mlir.zero` for null pointer | HIGH | Explicitly documented in MLIR 20 llvm dialect |
| `llvm.icmp` for pointer comparison | HIGH | Documented as accepting LLVM pointer types |
| Sequential pattern match → cond_br | HIGH | Standard technique for ML-family compilers; no exotic ops needed |
| libgc apt version on ubuntu 24.04 | MEDIUM | Ubuntu noble ships 7.x; Debian sid has 8.2.12. 7.x works fine for this use case; version pinning not critical |
| String intern/literal strategy | MEDIUM | Need to verify mlir global array + pointer-to-it works cleanly at v2 phase research time |
| List head boxing overhead | MEDIUM | Uniform !llvm.ptr boxing is correct but slow; acceptable for v2; may need to revisit in v3 with a tagged-value representation |
| insertvalue / extractvalue text format | MEDIUM | Syntax confirmed from docs; verify exact struct type annotation format at implementation time |

---

## Sources

- [bdwgc releases (GitHub)](https://github.com/bdwgc/bdwgc/releases) — version 8.2.12 confirmed, 2025-02-05
- [bdwgc overview (GitHub)](https://github.com/bdwgc/bdwgc/blob/master/docs/overview.md) — GC_malloc / GC_init API
- [Homebrew bdw-gc formula](https://formulae.brew.sh/formula/bdw-gc) — version 8.2.12, arm64 + x86_64
- [Debian sid libgc-dev](https://packages.debian.org/sid/libgc-dev) — version 1:8.2.12-1
- [llvm-boehmgc-sample (GitHub)](https://github.com/tattn/llvm-boehmgc-sample) — confirms LLVM IR + Boehm GC integration via -lgc link flag
- [MLIR llvm Dialect documentation](https://mlir.llvm.org/docs/Dialects/LLVM/) — llvm.func external decl, llvm.call, llvm.mlir.zero, llvm.icmp, llvm.getelementptr struct syntax
- [MLIR Discourse: replacing malloc with custom functions](https://discourse.llvm.org/t/llvm-dialect-replacing-malloc-and-free-with-custom-functions/63481) — custom allocator patterns in MLIR llvm dialect
- [MLIR Passes documentation](https://mlir.llvm.org/docs/Passes/) — pass pipeline verification
- [NuGet FsUnit.xUnit 7.1.1](https://www.nuget.org/packages/FsUnit.Xunit/) — version confirmation
- [NuGet xunit 2.9.3](https://www.nuget.org/packages/xunit) — version confirmation
- LangThree Ast.fs — authoritative source for pattern types (VarPat, WildcardPat, TuplePat, ConsPat, EmptyListPat, ConstPat) and expression types (String, Tuple, List, Cons, Match) to be compiled
