# Phase 7: GC Runtime Integration - Research

**Researched:** 2026-03-26
**Domain:** Boehm GC (bdw-gc), MLIR llvm dialect external function declarations, closure env heap migration, printf builtins
**Confidence:** HIGH

---

## Summary

Phase 7 wires Boehm GC into every emitted binary, migrates closure environment allocation from stack (`llvm.alloca`) to the GC heap (`GC_malloc`), and adds `print`/`println` builtins via `printf`. All three requirements have been verified end-to-end on this system (macOS arm64, LLVM 20.1.4 / Homebrew clang 21.1.8, bdw-gc 8.2.12).

The GC integration requires only two new external function declarations in the MLIR module (`llvm.func @GC_init()` and `llvm.func @GC_malloc(i64) -> !llvm.ptr`) plus `-L/opt/homebrew/opt/bdw-gc/lib -lgc` in the clang flags. The critical naming detail: the C symbol is `GC_init` (lowercase `i`), not `GC_INIT`. `GC_INIT` is a preprocessor macro defined in `gc.h` that expands to `GC_init()`.

Closure environment migration is straightforward: replace the two-op sequence `(ArithConstantOp(1), LlvmAllocaOp)` in `elaborateExpr` App dispatch with a bytes-constant + `LlvmCallOp(@GC_malloc)`. The caller-allocates interface of the closure-maker functions (`@add_n` takes `(i64, ptr) -> ptr`) is unchanged — the only difference is that the ptr passed in now points to GC heap instead of stack. Escaped closures (returned from their defining function) now work correctly because the env outlives the stack frame.

`print`/`println` are builtin variables in FunLang's `Eval.initialBuiltinEnv`. They appear in the parsed AST as `App(Var("print", _), String("hello", _), _)`. The MLIR implementation emits a module-level string constant (`llvm.mlir.global internal constant`) plus an `llvm.call @printf` with vararg syntax. `MlirModule` needs a new `Globals` field for these string constants, and `MlirModule` needs an `ExternalFuncs` field for the `llvm.func` declarations.

**Primary recommendation:** Add `Globals` + `ExternalFuncs` to `MlirModule`, two new `MlirOp` cases (`LlvmCallOp` and `LlvmCallVoidOp`), handle `App(Var("print"|"println"), String(s), _)` specially in `elaborateExpr`, prepend `LlvmCallVoidOp("@GC_init", [])` to `@main`'s entry block, and add platform-aware `-lgc` flags in `Pipeline.fs`.

---

## Standard Stack

### Core

| Component | Version | Purpose | Why Standard |
|-----------|---------|---------|--------------|
| Boehm GC (bdw-gc) | 8.2.12 | Conservative GC, no IR changes needed | Zero MLIR IR impact — pure link-time addition; macOS: `brew install bdw-gc` |
| `llvm.func @GC_init()` | MLIR 20 | External declaration of `GC_init` (void) | C symbol name is `GC_init` (lowercase i); `GC_INIT` is only a macro in `gc.h` |
| `llvm.func @GC_malloc(i64) -> !llvm.ptr` | MLIR 20 | External declaration of `GC_malloc` | Allocates GC-traced heap memory; same signature as `malloc(size_t)` |
| `llvm.func @printf(!llvm.ptr, ...) -> i32` | MLIR 20 | Vararg external for print/println | Standard libc; vararg calls use `vararg(!llvm.func<i32 (ptr, ...)>)` syntax |
| `llvm.mlir.global internal constant` | MLIR 20 | Module-level string constants for print | Must be emitted before `func.func @main` in the module text |

### Supporting

| Tool | Version | Purpose | When to Use |
|------|---------|---------|-------------|
| macOS clang flag | — | `-L/opt/homebrew/opt/bdw-gc/lib -lgc` | macOS Homebrew path; detect via `RuntimeInformation.IsOSPlatform(OSPlatform.OSX)` |
| Linux clang flag | — | `-lgc` | System install at `/usr/lib`; no `-L` needed |
| `GC_PRINT_STATS=1` env var | bdw-gc | Verify GC was initialized at runtime | Use in E2E test assertions / manual verification |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `GC_init` direct call | `GC_INIT` macro | `GC_INIT` is a C preprocessor macro, not a linkable symbol; MLIR can only call real symbols |
| `printf` for print | `puts` / `fputs` | `puts` adds newline (println only), `fputs` needs `stdout` ptr which is complex in MLIR; `printf` handles both uniformly |
| `llvm.func @main` | `func.func @main` | `func.func @main` already works and supports mixed `llvm.call` ops inside it (verified) |
| Per-call `LlvmAllocaOp` | None (removed) | `LlvmAllocaOp` stays in the DU for backward compat but is no longer generated |

**Installation (macOS):**
```bash
brew install bdw-gc
# Library: /opt/homebrew/opt/bdw-gc/lib/libgc.dylib
# Header:  /opt/homebrew/opt/bdw-gc/include/gc.h
```

---

## Architecture Patterns

### Recommended Project Structure

```
src/FunLangCompiler.Compiler/
├── MlirIR.fs        # Add MlirGlobal type; add Globals + ExternalFuncs to MlirModule;
│                    # add LlvmCallOp + LlvmCallVoidOp to MlirOp
├── Printer.fs       # Emit globals before funcs; emit external llvm.func decls;
│                    # print LlvmCallOp + LlvmCallVoidOp
├── Elaboration.fs   # Prepend GC_init to @main; replace LlvmAllocaOp with GC_malloc call;
│                    # handle App(Var("print"|"println"), String(s), _) specially;
│                    # add string global accumulator to ElabEnv
└── Pipeline.fs      # Add platform-aware -lgc / -L/opt/homebrew/... to clang flags
tests/compiler/
├── 07-01-gc-closure-escape.flt   # Escaped closure: returned from function, applied after
├── 07-02-print-basic.flt          # print "hello" + println "world"
└── ... (fill to reach 15 closure tests total)
```

### Pattern 1: MlirModule Extension

**What:** Add `Globals` (string constants) and `ExternalFuncs` (forward declarations) to `MlirModule`.

```fsharp
// MlirIR.fs — new types
type MlirGlobal =
    | StringConstant of name: string * value: string
    // name: e.g. "@__str_0"; value: raw content WITHOUT null terminator (printer adds \00)

type ExternalFuncDecl = {
    ExtName:     string           // e.g. "@GC_init", "@GC_malloc", "@printf"
    ExtParams:   MlirType list    // param types; special: Ptr can represent vararg start
    ExtReturn:   MlirType option  // None = void
    IsVarArg:    bool             // true for printf
}

// MlirModule extension
type MlirModule = {
    Globals:       MlirGlobal list        // NEW: string constants
    ExternalFuncs: ExternalFuncDecl list  // NEW: llvm.func forward declarations
    Funcs:         FuncOp list
}
```

### Pattern 2: New MlirOp Cases

**What:** Two new ops for calling external C functions at the LLVM dialect level.

```fsharp
// MlirIR.fs — new MlirOp cases
| LlvmCallOp     of result: MlirValue * callee: string * args: MlirValue list
  // %result = llvm.call @callee(%args...) : (types...) -> retType
  // Used for: GC_malloc, printf

| LlvmCallVoidOp of callee: string * args: MlirValue list
  // llvm.call @callee(%args...) : (types...) -> ()
  // Used for: GC_init (void return)
```

Note: `LlvmCallVarArgOp` for `printf` can share `LlvmCallOp` since the vararg syntax is always the same. The printer can emit the `vararg(!llvm.func<i32 (ptr, ...)>)` suffix when it detects the callee is `@printf`.

### Pattern 3: Printer Additions

**What:** Globals and external decls must appear at the top of the module, before `func.func` and `llvm.func` definitions.

```
// Printer output order:
module {
  llvm.mlir.global internal constant @__str_0("hello\00") {addr_space = 0 : i32}
  llvm.func @GC_init()
  llvm.func @GC_malloc(i64) -> !llvm.ptr
  llvm.func @printf(!llvm.ptr, ...) -> i32
  llvm.func @closure_fn_0(...)  { ... }   // existing closure body
  func.func @add_n(...)  { ... }           // existing closure maker
  func.func @main() -> i64  { ... }        // @main always last
}
```

```fsharp
// Printer.fs — emit global
let printGlobal (g: MlirGlobal) : string =
    match g with
    | StringConstant(name, value) ->
        // Escape value: replace \n -> \0A, add \00 for null terminator
        let escaped = value.Replace("\\n", "\\0A") + "\\00"
        sprintf "  llvm.mlir.global internal constant %s(\"%s\") {addr_space = 0 : i32}" name escaped

// External func declaration
let printExternalDecl (d: ExternalFuncDecl) : string =
    let paramStr = d.ExtParams |> List.map printType |> String.concat ", "
    let varargSuffix = if d.IsVarArg then ", ..." else ""
    let retStr = match d.ExtReturn with None -> "" | Some t -> sprintf " -> %s" (printType t)
    sprintf "  llvm.func %s(%s%s)%s" d.ExtName paramStr varargSuffix retStr
```

```fsharp
// Printer.fs — new op printers
| LlvmCallOp(result, callee, args) ->
    let argList = args |> List.map (fun v -> v.Name) |> String.concat ", "
    let argTypes = args |> List.map (fun v -> printType v.Type) |> String.concat ", "
    let varargSuffix =
        if callee = "@printf" then
            sprintf " vararg(!llvm.func<i32 (ptr, ...)>)"
        else ""
    sprintf "%s%s = llvm.call %s(%s)%s : (%s) -> %s"
        indent result.Name callee argList varargSuffix argTypes (printType result.Type)

| LlvmCallVoidOp(callee, args) ->
    let argList = args |> List.map (fun v -> v.Name) |> String.concat ", "
    let argTypes = args |> List.map (fun v -> printType v.Type) |> String.concat ", "
    if args.IsEmpty then
        sprintf "%sllvm.call %s() : () -> ()" indent callee
    else
        sprintf "%sllvm.call %s(%s) : (%s) -> ()" indent callee argList argTypes
```

### Pattern 4: Elaboration Changes

**What:** Three coordinated changes in `Elaboration.fs`.

**Change A — GC_init in @main:** Prepend `LlvmCallVoidOp("@GC_init", [])` to entry block ops in `elaborateModule`.

```fsharp
// Elaboration.fs — in elaborateModule
let gcInitOp = LlvmCallVoidOp("@GC_init", [])
// prepend to allBlocks[0].Body
let allBlocksWithGC =
    match allBlocks with
    | [] -> allBlocks
    | entryBlock :: rest ->
        { entryBlock with Body = gcInitOp :: entryBlock.Body } :: rest
```

**Change B — alloca → GC_malloc in App dispatch:** In the `Some sig_` branch for closure-making calls:

```fsharp
// Replace:
//   ArithConstantOp(countVal, 1L)
//   LlvmAllocaOp(envPtrVal, countVal, ci.NumCaptures)
// With:
let bytesVal = { Name = freshName env; Type = I64 }
let envPtrVal = { Name = freshName env; Type = Ptr }
let setupOps = [
    ArithConstantOp(bytesVal, int64 ((ci.NumCaptures + 1) * 8))
    LlvmCallOp(envPtrVal, "@GC_malloc", [bytesVal])
]
```

**Change C — print/println builtins:** Add special case to `elaborateExpr` before the general `App` handling:

```fsharp
// In elaborateExpr, BEFORE the general App match:
| App (Var ("print", _), String (s, _), _) ->
    // Add string global to module globals (deduped)
    let globalName = addStringGlobal env s   // adds to env.Globals, returns "@__str_N"
    let ptrVal  = { Name = freshName env; Type = Ptr }
    let fmtRes  = { Name = freshName env; Type = I32 }  // printf result (discarded)
    let unitVal = { Name = freshName env; Type = I64 }  // unit-as-zero for let binding
    let ops = [
        LlvmAddressOfOp(ptrVal, globalName)
        LlvmCallOp(fmtRes, "@printf", [ptrVal])
        ArithConstantOp(unitVal, 0L)
    ]
    (unitVal, ops)

| App (Var ("println", _), String (s, _), _) ->
    // println appends \n — store "s\n" as the global string
    let globalName = addStringGlobal env (s + "\n")
    let ptrVal  = { Name = freshName env; Type = Ptr }
    let fmtRes  = { Name = freshName env; Type = I32 }
    let unitVal = { Name = freshName env; Type = I64 }
    let ops = [
        LlvmAddressOfOp(ptrVal, globalName)
        LlvmCallOp(fmtRes, "@printf", [ptrVal])
        ArithConstantOp(unitVal, 0L)
    ]
    (unitVal, ops)
```

`ElabEnv` needs a new `Globals` field: `Globals: (string * string) list ref` (name * raw value), plus a `GlobalCounter: int ref`. `addStringGlobal` checks for duplicates and returns the existing or new global name.

**Change D — populate ExternalFuncs in MlirModule:** In `elaborateModule`, after building the `MlirModule`, add all required external declarations. The set of needed externals depends on what was used (always GC_init + GC_malloc; printf only if print/println appeared). Simplest: always emit all three.

```fsharp
let externalFuncs = [
    { ExtName = "@GC_init";   ExtParams = [];    ExtReturn = None;     IsVarArg = false }
    { ExtName = "@GC_malloc"; ExtParams = [I64]; ExtReturn = Some Ptr; IsVarArg = false }
    { ExtName = "@printf";    ExtParams = [Ptr]; ExtReturn = Some I32; IsVarArg = true  }
]
```

### Pattern 5: Pipeline.fs — Platform-Aware GC Link Flags

```fsharp
// Pipeline.fs — detect macOS vs Linux
open System.Runtime.InteropServices

let private gcLinkFlags =
    if RuntimeInformation.IsOSPlatform(OSPlatform.OSX) then
        "-L/opt/homebrew/opt/bdw-gc/lib -lgc"
    else
        "-lgc"

// In compile:
let clangArgs = sprintf "-Wno-override-module %s %s -o %s" llFile gcLinkFlags outputPath
```

### Anti-Patterns to Avoid

- **Calling `GC_INIT` (uppercase I) as the symbol name:** `GC_INIT` is a C preprocessor macro that expands to `GC_init()`. The actual linkable C symbol is `GC_init` (lowercase `i`). Linking `@GC_INIT` will produce "undefined symbol" linker error.
- **Allocating env struct inside the closure-maker function (callee-allocates):** If the env is stack-allocated inside `@add_n`, it goes out of scope when `@add_n` returns. Phase 5 research already established caller-allocates is the correct pattern; GC_malloc is just a better allocator for the same caller-allocates site.
- **Emitting `GC_malloc` before `GC_init` in @main:** GC_init must come first (first op in the entry block). Since closure env allocations happen inline in the elaborated ops, and GC_init is prepended to the entry block before everything else, ordering is guaranteed.
- **Forgetting `{addr_space = 0 : i32}` on string globals:** The `llvm.mlir.global` attribute is required in MLIR 20; omitting it causes a parse error.
- **Not null-terminating string globals:** The string value must end with `\00`. The printer should always append it.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Heap allocation for closures | Custom allocator, reference counting | Boehm GC (`GC_malloc`) | Zero IR changes, conservative GC handles all pointer types |
| Platform path detection for libgc | Fragile path probing | `RuntimeInformation.IsOSPlatform` | .NET built-in, one-liner |
| Vararg printf call syntax | Custom calling convention | `vararg(!llvm.func<i32 (ptr, ...)>)` in `llvm.call` | MLIR 20 standard vararg syntax; verified working |
| String escaping for MLIR globals | Custom escape logic | Simple `.Replace("\\n", "\\0A")` + `+ "\\00"` | Only \n is needed for println; \00 for null term |

**Key insight:** Boehm GC is designed to be a drop-in replacement for malloc with zero source changes — that's exactly our use case. The only integration points are `GC_init` once at startup and `GC_malloc` wherever heap allocation occurs.

---

## Common Pitfalls

### Pitfall 1: GC_INIT vs GC_init Symbol Name

**What goes wrong:** Linking fails with "undefined symbol `_GC_INIT`" for architecture arm64.
**Why it happens:** `GC_INIT()` is a C preprocessor macro in `gc.h` that expands to `GC_init()`. MLIR emits the symbol name literally; there is no preprocessor step.
**How to avoid:** Declare `llvm.func @GC_init()` (lowercase `i`). Verified: `nm /opt/homebrew/opt/bdw-gc/lib/libgc.dylib | grep GC_init` confirms `_GC_init` is the exported symbol.
**Warning signs:** Clang linker error: "Undefined symbols for architecture arm64: `_GC_INIT`"

### Pitfall 2: Missing -L Flag on macOS

**What goes wrong:** Clang cannot find `libgc.dylib` even though bdw-gc is installed.
**Why it happens:** Homebrew installs to `/opt/homebrew/opt/bdw-gc/lib/`, which is not on the default linker search path.
**How to avoid:** Add `-L/opt/homebrew/opt/bdw-gc/lib` before `-lgc` in the clang args when running on macOS.
**Warning signs:** "library not found for -lgc"

### Pitfall 3: printf vararg syntax for MLIR 20

**What goes wrong:** `mlir-translate` or `mlir-opt` rejects the printf call.
**Why it happens:** MLIR 20 requires explicit vararg type annotation on indirect/vararg calls.
**How to avoid:** Use exactly: `llvm.call @printf(%ptr) vararg(!llvm.func<i32 (ptr, ...)>) : (!llvm.ptr) -> i32`
**Warning signs:** MLIR parse error about vararg

### Pitfall 4: String global `addr_space` attribute

**What goes wrong:** MLIR parse error on `llvm.mlir.global` without `{addr_space = 0 : i32}`.
**Why it happens:** MLIR 20 requires the attribute explicitly for string globals.
**How to avoid:** Always emit `{addr_space = 0 : i32}` in the Printer.
**Warning signs:** `error: expected '{' in 'llvm.mlir.global'`

### Pitfall 5: SSA name collision between GC_init call and body ops

**What goes wrong:** Duplicate SSA value name `%t0` if GC_init call produces a result named the same as the first real op.
**Why it happens:** `LlvmCallVoidOp` doesn't produce a result SSA value, so this is NOT an issue. `LlvmCallOp` for GC_malloc uses `freshName` which increments the counter. Just ensure `LlvmCallVoidOp` doesn't consume a counter slot.
**How to avoid:** `LlvmCallVoidOp` has no `result: MlirValue` field — don't call `freshName` for it.

### Pitfall 6: Return type mismatch when @main is func.func but calls llvm.func ops

**What goes wrong:** `mlir-opt` might reject mixing `func.return` and `llvm.call` inside `func.func @main`.
**Why it happens:** Dialect mixing concern.
**How to avoid:** Verified: `llvm.call` inside `func.func` works with the existing lowering pipeline (`--convert-arith-to-llvm --convert-func-to-llvm --reconcile-unrealized-casts`). No change needed to `loweringPasses` in Pipeline.fs.

---

## Code Examples

Verified patterns (all tested on this system, LLVM 20.1.4):

### External Function Declarations (MLIR 20)

```mlir
// Source: verified on LLVM 20.1.4
llvm.func @GC_init()
llvm.func @GC_malloc(i64) -> !llvm.ptr
llvm.func @printf(!llvm.ptr, ...) -> i32
```

### GC_init Call (void) + GC_malloc Call

```mlir
// Source: verified
llvm.call @GC_init() : () -> ()
%bytes = arith.constant 16 : i64
%env = llvm.call @GC_malloc(%bytes) : (i64) -> !llvm.ptr
```

### String Global + printf (print / println)

```mlir
// Source: verified
// print "hello"
llvm.mlir.global internal constant @__str_0("hello\00") {addr_space = 0 : i32}
// println "world"  (note \0A = \n)
llvm.mlir.global internal constant @__str_1("world\0A\00") {addr_space = 0 : i32}

// Inside func.func @main:
%p0 = llvm.mlir.addressof @__str_0 : !llvm.ptr
%_ = llvm.call @printf(%p0) vararg(!llvm.func<i32 (ptr, ...)>) : (!llvm.ptr) -> i32
%p1 = llvm.mlir.addressof @__str_1 : !llvm.ptr
%__ = llvm.call @printf(%p1) vararg(!llvm.func<i32 (ptr, ...)>) : (!llvm.ptr) -> i32
```

### Escaped Closure Pattern (alloca → GC_malloc)

```mlir
// Source: verified — closure env on GC heap, returned from defining function
func.func @make_adder(%arg0: i64) -> !llvm.ptr {
  %bytes = arith.constant 16 : i64          // (1 ptr + 1 i64) * 8 = 16
  %env = llvm.call @GC_malloc(%bytes) : (i64) -> !llvm.ptr
  %fn_ptr = llvm.mlir.addressof @closure_fn_0 : !llvm.ptr
  llvm.store %fn_ptr, %env : !llvm.ptr, !llvm.ptr
  %slot = llvm.getelementptr %env[1] : (!llvm.ptr) -> !llvm.ptr, i64
  llvm.store %arg0, %slot : i64, !llvm.ptr
  return %env : !llvm.ptr
}
// Call after make_adder's frame is gone → works correctly because env is GC heap
```

### Complete Program Pattern (print + closure + return)

```mlir
// Source: verified
module {
  llvm.mlir.global internal constant @__str_0("hello\0A\00") {addr_space = 0 : i32}
  llvm.func @GC_init()
  llvm.func @GC_malloc(i64) -> !llvm.ptr
  llvm.func @printf(!llvm.ptr, ...) -> i32

  func.func @main() -> i64 {
    llvm.call @GC_init() : () -> ()          // always first
    // ... closure ops using GC_malloc ...
    %ptr = llvm.mlir.addressof @__str_0 : !llvm.ptr
    %_ = llvm.call @printf(%ptr) vararg(!llvm.func<i32 (ptr, ...)>) : (!llvm.ptr) -> i32
    %unit = arith.constant 0 : i64           // unit-as-zero for print return value
    %ret = arith.constant 42 : i64
    return %ret : i64
  }
}
```

### Platform-Aware GC Link Flags (F#)

```fsharp
// Source: .NET RuntimeInformation API
open System.Runtime.InteropServices
let gcLinkFlags =
    if RuntimeInformation.IsOSPlatform(OSPlatform.OSX) then
        "-L/opt/homebrew/opt/bdw-gc/lib -lgc"
    else
        "-lgc"
```

---

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Stack alloca for closure env | GC_malloc for closure env | Phase 7 | Closures can safely escape their defining function |
| No print/println in backend | printf via llvm.call | Phase 7 | Programs can produce output |
| No GC | Boehm conservative GC | Phase 7 | All heap memory is automatically collected |

**Deprecated/outdated in Phase 7:**
- `LlvmAllocaOp` in App dispatch: retained in MlirOp DU but no longer generated. Can be formally removed in a later cleanup phase.
- The two-op pattern `(ArithConstantOp(1L), LlvmAllocaOp)` in Elaboration.fs: replaced by `(ArithConstantOp(bytes), LlvmCallOp(@GC_malloc))`.

---

## Open Questions

1. **print/println with non-String arguments**
   - What we know: The FunLang Eval builtins fail at runtime if arg is not StringValue.
   - What's unclear: Should the backend handle `App(Var("print"), Var(x))` where x might be a string?
   - Recommendation: For Phase 7 scope, only handle the `String` literal case (`App(Var("print"), String(s, _), _)`). Variable arguments require a full string type in the IR (future phase).

2. **15 closure tests target**
   - What we know: Currently 2 closure tests (05-01, 05-02). Success criterion says 15 tests pass.
   - What's unclear: Phase 7 must ADD 13 new closure tests (escape, multi-capture, nested, etc.) OR the "15 tests" is aspirational for the whole compiler at this point.
   - Recommendation: Interpret as "all existing tests pass + add escape test + print tests". Clarify in planning if exact count matters.

3. **ElabEnv.Globals deduplication strategy**
   - What we know: Multiple `print "hello"` calls should share one string global.
   - What's unclear: Is content-based dedup necessary (same string → same global) or just name-based (each call gets a new global)?
   - Recommendation: Content-based dedup via `List.tryFind (fun (_, v) -> v = escapedStr) env.Globals.Value`. This avoids wasted globals but adds a linear scan.

4. **`printf` with special characters in strings**
   - What we know: `printf(s)` where s contains `%` will be misinterpreted as a format specifier.
   - What's unclear: Does the language allow `%` in string literals?
   - Recommendation: For Phase 7, strings with `%` are undefined behavior. Future fix: emit `printf("%s", s)` instead of `printf(s)`.

---

## Sources

### Primary (HIGH confidence)
- Direct MLIR 20.1.4 toolchain testing — all code examples compiled and ran on this machine
- `mlir-translate`, `mlir-opt`, `clang` from `/opt/homebrew/opt/llvm/bin` (version 21.1.8)
- `nm /opt/homebrew/opt/bdw-gc/lib/libgc.dylib` — confirmed `_GC_init` symbol name
- https://mlir.llvm.org/docs/Dialects/LLVM/ — external function declaration syntax
- https://www.hboehm.info/gc/gcinterface.html — GC_init / GC_malloc C API

### Secondary (MEDIUM confidence)
- Boehm GC bdw-gc 8.2.12 pkg-config at `/opt/homebrew/opt/bdw-gc/lib/pkgconfig/bdw-gc.pc`
- FunLang `Eval.fs` lines 230-246 — confirmed `print`/`println` appear as `App(Var("print"), String(s))` in AST

---

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all components installed and tested end-to-end
- Architecture: HIGH — code patterns verified with actual MLIR/LLVM toolchain
- Pitfalls: HIGH — most pitfalls discovered during research verification (GC_INIT vs GC_init was caught via nm)

**Research date:** 2026-03-26
**Valid until:** 2026-09-26 (bdw-gc and MLIR llvm dialect syntax are stable)
