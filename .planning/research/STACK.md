# Technology Stack: LangBackend (LangThree Native Compiler)

**Project:** LangBackend — LangThree MLIR → LLVM → native binary compiler
**Researched:** 2026-03-26
**Confidence:** HIGH for core stack; MEDIUM for advanced MLIR C API details (no existing .NET bindings to reference)

---

## Recommended Stack

### Core Runtime Environment

| Technology | Version | Purpose | Why |
|------------|---------|---------|-----|
| .NET / F# | .NET 10 / F# 10 (LTS, released Nov 2025) | Implementation language and runtime host | Same language as LangThree frontend; maximal code reuse; F# 10 on .NET 10 is the current LTS with 3-year support |
| LLVM / MLIR | 20.x (stable, released March 2025) | IR backend and lowering infrastructure | Current stable branch; apt.llvm.org packages available; MLIR 20 has stable C API |

### MLIR C API Access (P/Invoke Layer)

| Technology | Version | Purpose | Why |
|------------|---------|---------|-----|
| libMLIR-C.so | LLVM 20 | Core MLIR C API shared library | The C API is the only ABI-stable interface to MLIR for non-C++ languages; all types are opaque `{ void *ptr }` structs suitable for P/Invoke |
| libMLIR.so | LLVM 20 | Full MLIR runtime (required by libMLIR-C) | Pulled in automatically when linking against libMLIR-C |
| F# DllImport / LibraryImport | .NET 10 built-in | P/Invoke binding mechanism | .NET 10 prefers `[<LibraryImport>]` (source-generated, no reflection) over `[<DllImport>]`; both work; DllImport is fine for our use case |

**P/Invoke ABI contract:** Every MLIR C API handle (`MlirContext`, `MlirOperation`, `MlirModule`, `MlirBlock`, `MlirRegion`, `MlirType`, `MlirAttribute`, `MlirValue`) is defined via a macro that expands to `typedef struct { void *ptr; } MlirX;`. In F# these map to:

```fsharp
// Each MLIR handle is a struct wrapping a single native pointer.
// Use [<Struct>] to guarantee value-type layout matching the C ABI.
[<Struct>]
type MlirContext = { ptr: nativeint }

[<Struct>]
type MlirOperation = { ptr: nativeint }

[<Struct>]
type MlirModule = { ptr: nativeint }

[<Struct>]
type MlirType = { ptr: nativeint }

[<Struct>]
type MlirAttribute = { ptr: nativeint }

[<Struct>]
type MlirValue = { ptr: nativeint }

[<Struct>]
type MlirBlock = { ptr: nativeint }

[<Struct>]
type MlirRegion = { ptr: nativeint }

[<Struct>]
type MlirLocation = { ptr: nativeint }

[<Struct>]
type MlirStringRef = { data: nativeint; length: unativeint }

// Null checks follow the MLIR convention: a null handle has ptr = 0n
let isNull (handle: MlirContext) = handle.ptr = 0n
```

**Key P/Invoke import pattern:**
```fsharp
[<DllImport("libMLIR-C.so", CallingConvention = CallingConvention.Cdecl)>]
extern MlirContext mlirContextCreate()

[<DllImport("libMLIR-C.so", CallingConvention = CallingConvention.Cdecl)>]
extern void mlirContextDestroy(MlirContext ctx)
```

**Why not a higher-level .NET wrapper library?** No maintained .NET/F# MLIR binding library exists as of 2026. The closest analogues are `mlir-hs` (Haskell) and `melior` (Rust), both wrapping the C API directly. F# P/Invoke is the correct approach.

### MLIR Dialects (Codegen Target)

For LangThree v1 (int/bool, arithmetic, comparisons, let/let rec, lambda/application, if-else):

| Dialect | Purpose | When Used |
|---------|---------|-----------|
| `func` | Function definitions and calls (`func.func`, `func.call`, `func.return`) | All function-bearing constructs; main entry point; let-bound functions |
| `arith` | Integer arithmetic and comparison (`arith.constant`, `arith.addi`, `arith.subi`, `arith.muli`, `arith.divsi`, `arith.cmpi`) | All arithmetic expressions, boolean constants (i1), integer constants |
| `cf` (control flow) | Conditional branches (`cf.cond_br`, `cf.br`) | Compiling if-else expressions to basic block branches |
| `llvm` | LLVM IR in MLIR form (`llvm.func`, `llvm.call`, `llvm.mlir.constant`) | Output of the lowering pipeline; input to `mlirTranslateModuleToLLVMIR` |

**Why this dialect set?**
- `func` + `arith` + `cf` is the minimal well-supported set for a first-order functional language targeting LLVM
- All three have complete, maintained `*-to-llvm` lowering passes in LLVM 20
- Closures/lambdas in v1 are compiled as named `func.func` with explicit argument passing (closure conversion) rather than using a closure dialect — defers GC and function pointer complexity
- `scf` (structured control flow) is intentionally skipped: if-else naturally maps to `cf.cond_br` and is simpler at this abstraction level

**Dialect NOT used (and why):**

| Dialect | Why Skipped |
|---------|-------------|
| `scf` | Structured loops not needed for v1; adds pass dependency |
| `memref` | Memory references for arrays/tuples — v1 out of scope |
| `index` | Loop indexing — not needed without loops |
| Custom dialect | Unnecessary for v1; adds build complexity; standard dialects sufficient |

### Lowering Pipeline

The mandatory pass sequence to go from `func`+`arith`+`cf` to pure `llvm` dialect:

```
convert-arith-to-llvm
convert-cf-to-llvm
convert-func-to-llvm
reconcile-unrealized-casts
```

**Order matters.** A 2024 LLVM upstream change (PR #120548) removed the implicit `arith-to-llvm` inclusion inside `func-to-llvm`. Both must now be explicit, with `arith-to-llvm` run **before** `func-to-llvm`. The `reconcile-unrealized-casts` pass must run **last** to clean up any intermediate type cast operations injected during progressive lowering.

**Via C API — two approaches:**

**Option A: Textual pipeline string (recommended for simplicity)**
```fsharp
// mlirParsePassPipeline / mlirOpPassManagerAddPipeline accept a string
let pipeline =
    "convert-arith-to-llvm,convert-cf-to-llvm,convert-func-to-llvm,reconcile-unrealized-casts"
```
This approach is more robust to API changes and easier to debug by replaying with `mlir-opt`.

**Option B: Programmatic pass construction**
Uses `mlirCreateConvertArithToLLVM()`, `mlirCreateConvertCFToLLVM()`, etc. from the conversion headers. More verbose but avoids string parsing.

### Native Binary Linking

| Tool | Version | Purpose | Why |
|------|---------|---------|-----|
| `mlir-translate` | LLVM 20 (from mlir-20-tools) | Translate lowered MLIR → LLVM IR text (`.ll`) | Standard tool; used in development/debugging |
| `llc` | LLVM 20 | Compile LLVM IR → object file (`.o`) | Converts `.ll` to platform-specific object code |
| `clang-20` | LLVM 20 | Link object file → executable | Handles startup code (crt0), stdlib linking; simpler than raw ld/lld |
| `lld-20` | LLVM 20 (optional) | Alternative linker | Can replace system ld; useful for hermetic builds |

**Two-tier linking strategy:**

**Tier 1 (programmatic — for production):** Use MLIR's `mlirTranslateModuleToLLVMIR` C API function to get an LLVM IR module in-memory, then use LLVM's `LLVMTargetMachineEmitToMemoryBuffer` or similar to write an object file without spawning external processes.

**Tier 2 (shell pipeline — for development and testing):**
```bash
# Full pipeline via tools (good for debugging each stage)
mlir-opt --convert-arith-to-llvm --convert-cf-to-llvm \
         --convert-func-to-llvm --reconcile-unrealized-casts \
         input.mlir | \
mlir-translate --mlir-to-llvmir | \
llc -filetype=obj -o output.o
clang-20 output.o -o program
```

**Recommended for v1:** Start with Tier 2 (shell pipeline via `System.Diagnostics.Process`) to unblock development quickly. Migrate to Tier 1 (programmatic emission) in a later phase once the pipeline is validated end-to-end.

### Frontend Integration

| Technology | Version | Purpose | Why |
|------------|---------|---------|-----|
| LangThree.fsproj | project ref (../LangThree) | AST types, type checker | Zero duplication; parser/type checker already battle-tested |
| FsLexYacc | 11.3.0 (April 2024) | Lexer/parser (used by LangThree, not directly) | Already embedded in frontend; no new dependency |
| FSharp.Text.Lexing | bundled with FsLexYacc | Lex buffer types | Transitive dep via LangThree |

### Testing

| Technology | Version | Purpose | Why |
|------------|---------|---------|-----|
| xUnit | 2.9.x | Unit and integration tests | Standard .NET test framework; F# friendly |
| FsUnit | 5.x | F# assertion DSL for xUnit | Makes F# test assertions readable |
| Shell scripts / Makefile | — | E2E test runner | Compile `.lt` file → run binary → check stdout; simplest possible E2E |

### Build and Tooling

| Technology | Version | Purpose | Why |
|------------|---------|---------|-----|
| .NET SDK 10 | 10.0.x | Build system | Matches runtime; `dotnet build`, `dotnet test` |
| Makefile | — | Build orchestration | Simple wrapper for common tasks: build, test, e2e |
| libmlir-20-dev | LLVM 20 (apt) | Development headers + libMLIR-C.so | Required at compile time for header verification; at runtime for P/Invoke |
| mlir-20-tools | LLVM 20 (apt) | `mlir-opt`, `mlir-translate` | Required for Tier 2 pipeline and debugging |
| clang-20 | LLVM 20 (apt) | Compiler + linker driver | Final step of native binary production |

---

## Installation

```bash
# Add LLVM 20 apt repository (Ubuntu/Debian, including WSL2)
wget -qO- https://apt.llvm.org/llvm-snapshot.gpg.key \
  | sudo tee /etc/apt/trusted.gpg.d/apt.llvm.org.asc

# For Ubuntu 24.04 (noble):
echo "deb http://apt.llvm.org/noble/ llvm-toolchain-noble-20 main" \
  | sudo tee /etc/apt/sources.list.d/llvm-20.list

sudo apt-get update
sudo apt-get install -y \
  libmlir-20-dev \
  mlir-20-tools \
  clang-20 \
  lld-20 \
  llvm-20-dev

# Verify MLIR shared library is present
ls /usr/lib/x86_64-linux-gnu/libMLIR-C.so* 2>/dev/null || \
ls /usr/lib/llvm-20/lib/libMLIR-C.so* 2>/dev/null

# Install .NET 10 SDK (if not already present)
# https://learn.microsoft.com/en-us/dotnet/core/install/linux
dotnet --version  # should show 10.x

# Create solution and projects
dotnet new sln -n LangBackend
dotnet new classlib -lang F# -o src/LangBackend.Compiler -n LangBackend.Compiler
dotnet new console  -lang F# -o src/LangBackend.Cli    -n LangBackend.Cli
dotnet new xunit    -lang F# -o tests/LangBackend.Tests -n LangBackend.Tests

# Add project reference to LangThree frontend
dotnet add src/LangBackend.Compiler/LangBackend.Compiler.fsproj \
  reference ../LangThree/src/LangThree/LangThree.fsproj

# NuGet packages
dotnet add tests/LangBackend.Tests/LangBackend.Tests.fsproj package FsUnit
```

**Runtime library path** (if libMLIR-C.so is not in the default linker path):
```bash
# Add to /etc/ld.so.conf.d/ or set at runtime:
export LD_LIBRARY_PATH=/usr/lib/llvm-20/lib:$LD_LIBRARY_PATH
```

Or pin it in the .fsproj with a native asset hint for development:
```xml
<PropertyGroup>
  <!-- Tells the runtime loader where to find libMLIR-C.so -->
  <RuntimeLibraryPath>/usr/lib/llvm-20/lib</RuntimeLibraryPath>
</PropertyGroup>
```

---

## Alternatives Considered

| Category | Recommended | Alternative | Why Not |
|----------|-------------|-------------|---------|
| MLIR access from F# | P/Invoke → MLIR C API | C++/CLI wrapper DLL | C++/CLI only works on Windows; project targets Linux |
| MLIR access from F# | P/Invoke → MLIR C API | MLIR Python bindings from F# via IPC | Absurd overhead; not a real alternative |
| MLIR access from F# | P/Invoke → MLIR C API | Build MLIR from source for custom wrapper | 2–4h build time; not needed; apt packages have the C API |
| LLVM version | LLVM 20 | LLVM 18 (previous LTS) | LLVM 20 is current stable (March 2025); LLVM 18 is still supported but 20 has better `arith-to-llvm` pass separation |
| LLVM version | LLVM 20 | LLVM 21/22 (development) | Development branches have unstable API; not in apt stable |
| Dialect for codegen | `func` + `arith` + `cf` | Direct LLVM dialect codegen | Skipping intermediate dialects works but loses all MLIR optimization passes; harder to debug; not the standard approach |
| Dialect for codegen | `func` + `arith` + `cf` | Custom dialect | Adds significant build complexity (TableGen, CMake); provides no benefit for v1 |
| Lowering approach | `mlir-opt` text pipeline | Programmatic pass construction | Text pipeline is easier to debug and matches `mlir-opt` CLI exactly; can switch later |
| Closures/lambdas | Closure conversion → `func.func` | MLIR closure dialect | No standard closure dialect; closure-conversion to named functions is the universal approach for ML-style lambdas |
| Native codegen (v1) | Shell pipeline (mlir-translate + llc + clang) | In-process LLVM emission | In-process requires linking against heavy LLVM C++ libraries; shell pipeline is simpler and sufficient for v1 |
| Test framework | xUnit + FsUnit | NUnit, Expecto | xUnit is the most common in .NET ecosystem; Expecto is excellent but adds a dependency; xUnit suffices |
| Frontend reuse | Project reference | Git submodule or package | Project reference is simplest for sibling directories; no packaging overhead |

---

## Key Structural Decisions

### Decision 1: Opaque struct P/Invoke (not nativeint directly)

Use `[<Struct>] type MlirContext = { ptr: nativeint }` for each handle type, NOT bare `nativeint`.

**Rationale:** Type-safe handles prevent accidentally passing an `MlirType` where an `MlirContext` is expected. The F# compiler enforces the distinction. The struct layout (single pointer field) is guaranteed to match the C ABI `{ void *ptr }` definition.

### Decision 2: MlirStringRef requires special handling

`MlirStringRef` is a struct containing `(const char* data, size_t length)` — it is **not** null-terminated. When passing F# strings to MLIR:

```fsharp
// Pattern: pin the string bytes and create MlirStringRef
let withMlirStringRef (s: string) (f: MlirStringRef -> 'a) =
    let bytes = System.Text.Encoding.UTF8.GetBytes(s)
    let gch = System.Runtime.InteropServices.GCHandle.Alloc(bytes, GCHandleType.Pinned)
    try
        let ref = { data = gch.AddrOfPinnedObject(); length = unativeint bytes.Length }
        f ref
    finally
        gch.Free()
```

This is the single most error-prone P/Invoke pattern in the MLIR C API. Get it right once in a utility module and use it everywhere.

### Decision 3: Closures as closure-converted `func.func`

LangThree lambdas are compiled via **closure conversion** before MLIR codegen:
- Each lambda becomes a named `func.func` that takes all free variables as extra leading parameters
- Call sites that capture a lambda pass both the function pointer and the captured values
- For v1 (integers and booleans only), this is sufficient without heap allocation

This defers the need for a closure representation in MLIR (which requires `memref` or GC) until v2.

### Decision 4: `let rec` → MLIR `func.func` with recursive `func.call`

`let rec f x = ... f ...` compiles directly to a MLIR function with a recursive call. MLIR and LLVM handle this natively — no special recursion representation needed.

### Decision 5: Booleans as `i1`, integers as `i64`

- `bool` → `i1` in arith dialect
- `int` → `i64` in arith dialect (64-bit integers; matches `long` in C on x86-64)
- `arith.cmpi` returns `i1`, which feeds directly into `cf.cond_br`
- No boxing required for v1 (no polymorphism in the value representation)

---

## C API Header Coverage (MLIR 20)

The relevant C API headers for this project, all in `mlir-c/`:

| Header | Key Functions Used |
|--------|--------------------|
| `mlir-c/IR.h` | `mlirContextCreate`, `mlirContextDestroy`, `mlirModuleCreateEmpty`, `mlirModuleGetBody`, `mlirModuleGetOperation`, `mlirModuleDestroy`, `mlirOperationCreate`, `mlirOperationDestroy`, `mlirBlockCreate`, `mlirRegionCreate`, `mlirLocationUnknownGet` |
| `mlir-c/BuiltinTypes.h` | `mlirIntegerTypeGet` (for i1, i64), `mlirFunctionTypeGet` |
| `mlir-c/BuiltinAttributes.h` | `mlirIntegerAttrGet`, `mlirBoolAttrGet`, `mlirStringAttrGet` |
| `mlir-c/Dialect/Func.h` | `mlirDialectHandleGetNamespace` + dialect registration for func |
| `mlir-c/Dialect/Arith.h` | Dialect registration for arith |
| `mlir-c/Dialect/ControlFlow.h` | Dialect registration for cf |
| `mlir-c/Dialect/LLVMIR.h` | Dialect registration for llvm |
| `mlir-c/Pass.h` | `mlirPassManagerCreate`, `mlirPassManagerDestroy`, `mlirPassManagerRunOnOp`, `mlirParsePassPipeline` |
| `mlir-c/Target/LLVMIR.h` | `mlirTranslateModuleToLLVMIR` (translate fully-lowered MLIR → LLVM IR module) |

**Note:** The `mlir-c/Target/LLVMIR.h` function `mlirTranslateModuleToLLVMIR` requires `mlirRegisterAllLLVMTranslations()` to be called first to register the dialect translation interfaces.

---

## Confidence Assessment

| Area | Confidence | Notes |
|------|------------|-------|
| F# / .NET 10 | HIGH | Official release; LTS; well-documented |
| LLVM 20 / MLIR 20 apt packages | HIGH | Released March 2025; apt packages confirmed available |
| MLIR C API struct layout for P/Invoke | HIGH | `{ void *ptr }` macro-generated layout is stable; confirmed in MLIR source |
| `MlirStringRef` P/Invoke pattern | HIGH | Documented; known pain point |
| Dialect selection (func+arith+cf) | HIGH | Toy tutorial + multiple blog posts confirm this is the standard path |
| Lowering pass order | HIGH | Confirmed by upstream PR #120548 and discourse; arith-before-func is required in LLVM 20 |
| Shell pipeline (Tier 2) linking | HIGH | Well-documented; `mlir-translate | llc | clang` is the canonical approach |
| In-process `mlirTranslateModuleToLLVMIR` | MEDIUM | Function exists in C API; requires registering translations; integration details need phase-level research |
| Closure conversion approach | MEDIUM | Standard technique; F# implementation details need phase-level research |
| `mlirParsePassPipeline` string API | MEDIUM | Function exists; exact string format should be verified against MLIR 20 pass names at implementation time |

---

## Sources

- [MLIR C API Documentation](https://mlir.llvm.org/docs/CAPI/) — official, authoritative
- [MLIR Toy Tutorial Chapter 6: Lowering to LLVM](https://mlir.llvm.org/docs/Tutorials/Toy/Ch-6/) — official, lowering pipeline reference
- [MLIR LLVM IR Target](https://mlir.llvm.org/docs/TargetLLVMIR/) — official, mlirTranslateModuleToLLVMIR
- [MLIR Dialects — func](https://mlir.llvm.org/docs/Dialects/Func/) — official
- [MLIR Dialects — arith](https://mlir.llvm.org/docs/Dialects/ArithOps/) — official
- [MLIR Dialects — llvm](https://mlir.llvm.org/docs/Dialects/LLVM/) — official
- [MLIR Pass Infrastructure](https://mlir.llvm.org/docs/PassManagement/) — official, mlirParsePassPipeline
- [LLVM/Clang Debian/Ubuntu apt packages](https://apt.llvm.org/) — official, LLVM 20 installation
- [MLIR: Lowering through LLVM — Jeremy Kun](https://www.jeremykun.com/2023/11/01/mlir-lowering-through-llvm/) — verified blog post
- [LLVM upstream PR #120548: Remove arith-to-llvm from func-to-llvm](https://github.com/llvm/llvm-project/pull/120548) — confirms pass ordering requirement
- [F# P/Invoke / External Functions — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/functions/external-functions) — official F# P/Invoke reference
- [.NET Native Interop Best Practices](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/best-practices) — official, LibraryImport vs DllImport
- [speakeztech/fsharp-mlir-hello](https://github.com/speakeztech/fsharp-mlir-hello) — only known F# + MLIR PoC project; confirms approach is viable
- [F# 10 / .NET 10 Release — InfoQ](https://www.infoq.com/news/2025/11/dotnet-10-release/) — version confirmation
- [FsLexYacc 11.3.0 — NuGet](https://www.nuget.org/packages/FsLexYacc/) — version confirmation
- [mlir-hs Haskell bindings — MLIR.Native source](https://google.github.io/mlir-hs/mlir-hs-0.1.0.0/src/MLIR.Native.html) — reference for C API wrapping patterns in a functional language
