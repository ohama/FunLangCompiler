# Phase 1: MlirIR Foundation - Research

**Researched:** 2026-03-26
**Domain:** F# discriminated union IR design, MLIR text format, shell pipeline (mlir-opt / mlir-translate / clang), FsLit test format
**Confidence:** HIGH

---

## Summary

Phase 1 builds the foundational IR type (`MlirIR`) in F#, a pure-string Printer, and the three-step shell pipeline that takes `.mlir` text all the way to a runnable ELF binary. The pipeline is verified end-to-end by hand before any real compiler logic is written.

**The architecture is text-based, not P/Invoke.** MlirIR is a plain F# discriminated union (DU). The Printer serialises it to a `.mlir` text file. `mlir-opt` lowers that file to the LLVM dialect, `mlir-translate` converts it to `.ll`, and `clang` compiles to a binary. All three tool invocations use `System.Diagnostics.Process`. This approach avoids every P/Invoke pitfall listed in the project research and lets each stage be debugged independently.

The tools are confirmed present at `/usr/local/bin/mlir-opt`, `/usr/local/bin/mlir-translate`, and `/usr/local/bin/clang` (all LLVM 20.1.4). The full pipeline from `.mlir` to an ELF that exits with code 42 has been manually verified in this research session. FsLit (`/home/shoh/vibe-coding/FsLit/dist/fslit`) checks stdout; E2E tests echo the exit code as the final line of output, making them straightforward.

**Primary recommendation:** Define MlirIR as a minimal DU covering exactly `Module / FuncOp / Block / ReturnOp / ArithConstantOp / MlirType / MlirValue`, write a Printer that emits valid MLIR 20 text for `return 42`, wire up the three-process pipeline, and lock the test with a FsLit `.flt` file before any other code is written.

---

## Standard Stack

### Core

| Tool / Library | Version | Purpose | Why Standard |
|----------------|---------|---------|--------------|
| `mlir-opt` | LLVM 20.1.4 | Lower `.mlir` text through pass pipeline | Canonical MLIR tool; handles all dialect conversions |
| `mlir-translate` | LLVM 20.1.4 | Translate lowered MLIR to LLVM IR (`.ll`) | Only standard tool for `--mlir-to-llvmir` |
| `clang` | 20.1.4 | Compile `.ll` to native ELF binary | Handles crt0/libc startup automatically; simpler than raw ld |
| F# / .NET | 10.0.105 | Host language for MlirIR DU and Printer | Same language as FunLang frontend |
| `System.Diagnostics.Process` | .NET 10 built-in | Spawn mlir-opt/mlir-translate/clang | Standard .NET subprocess API; no extra dependencies |

### Supporting

| Tool | Version | Purpose | When to Use |
|------|---------|---------|-------------|
| FsLit (`fslit`) | — | `.flt` file-based E2E test runner | TEST-01: compile → run → verify exit code |
| FunLang.fsproj | project ref | Frontend AST, type checker reuse | CLI-02: reference from FunLangCompiler.Compiler |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Text-based MLIR generation | P/Invoke MLIR C API | P/Invoke adds ownership/lifetime complexity; text is sufficient for Phase 1 and produces debuggable intermediate files |
| `clang` for linking | `llc` + `ld` | `clang` handles startup symbols automatically; `llc` + `ld` requires manual libc linking |
| Single-process pass invocation | Pipe mlir-opt → mlir-translate | Separate process calls are easier to debug and produce inspectable intermediate files |

### Tool Paths (Verified)

```bash
/usr/local/bin/mlir-opt        # LLVM 20.1.4
/usr/local/bin/mlir-translate  # LLVM 20.1.4
/usr/local/bin/clang           # clang version 20.1.4
/home/shoh/vibe-coding/FsLit/dist/fslit  # FsLit test runner
```

---

## Architecture Patterns

### Recommended Project Structure

```
src/
├── FunLangCompiler.Compiler/
│   ├── MlirIR.fs          # F# DU: Module, FuncOp, Block, Op, Value, Type
│   ├── Printer.fs         # MlirIR -> .mlir text string (pure, no I/O)
│   ├── Pipeline.fs        # System.Diagnostics.Process calls: mlir-opt, mlir-translate, clang
│   └── FunLangCompiler.Compiler.fsproj
└── FunLangCompiler.Cli/
    └── Program.fs         # CLI driver (Phase 6 concern; in Phase 1 only used for smoke test)
tests/
└── compiler/
    └── 01-return42.flt    # FsLit E2E smoke test
```

### Pattern 1: MlirIR as a Minimal F# DU

**What:** Represent MLIR concepts as a plain F# discriminated union — no P/Invoke handles, no pointer types, no ownership tracking. Just an algebraic data type.

**When to use:** Phase 1 through Phase 5. MlirIR grows per-phase by adding new Op cases.

**Phase 1 MlirIR needs only:**

```fsharp
// MlirIR.fs
module MlirIR

// MLIR type system — only i64 needed in Phase 1
type MlirType =
    | I64
    | I32
    | I1

// SSA value — a named result from an operation
type MlirValue = {
    Name: string   // e.g. "%c42", "%0"
    Type: MlirType
}

// Operations — one DU case per MLIR op
// Phase 1: only arith.constant and func.return needed
type MlirOp =
    | ArithConstantOp of result: MlirValue * value: int64
    | ReturnOp        of operands: MlirValue list

// A basic block: a named sequence of ops
type MlirBlock = {
    Args:  MlirValue list    // block arguments (empty for entry block)
    Body:  MlirOp list
}

// A region: a list of basic blocks
type MlirRegion = {
    Blocks: MlirBlock list
}

// A func.func operation
type FuncOp = {
    Name:       string           // "@main"
    InputTypes: MlirType list    // [] for @main
    ReturnType: MlirType option  // Some I64 for @main
    Body:       MlirRegion
}

// Top-level module
type MlirModule = {
    Funcs: FuncOp list
}
```

**Extensibility note:** In later phases, add new `MlirOp` cases (`ArithAddIOp`, `CfCondBrOp`, etc.) without changing the shape of `MlirModule`, `FuncOp`, `MlirBlock`, or `MlirRegion`.

### Pattern 2: Printer as Pure String Generation

**What:** `Printer.fs` contains `printModule : MlirModule -> string`. Pure function, no I/O, no side effects. Takes an MlirIR value, returns the `.mlir` text.

**When to use:** Always. Keeping Printer pure makes it trivially testable.

**Expected output for `return 42`:**

```
module {
  func.func @main() -> i64 {
    %c42 = arith.constant 42 : i64
    return %c42 : i64
  }
}
```

**Verified MLIR 20 syntax notes:**
- Module wrapper: `module { ... }` (no attributes needed)
- Function: `func.func @name() -> return_type { ... }`
- Constant: `%name = arith.constant value : type`
- Return: `return %val : type`
- i64 type: `i64` (not `!i64` — no `!` prefix for builtin integer types)
- Indentation: 2 spaces per level (cosmetic only; MLIR parser is whitespace-insensitive)

### Pattern 3: Shell Pipeline via System.Diagnostics.Process

**What:** `Pipeline.fs` wraps each tool call in a function that returns `Result<string, string>` (Ok = stdout, Error = stderr + exit code).

**When to use:** Phase 1. This is the permanent pipeline pattern — later phases only extend MlirIR, not the pipeline.

```fsharp
// Pipeline.fs — illustrative structure
module Pipeline

open System.Diagnostics

type PipelineError =
    | MlirOptFailed   of exitCode: int * stderr: string
    | TranslateFailed of exitCode: int * stderr: string
    | ClangFailed     of exitCode: int * stderr: string

let runProcess (program: string) (args: string) : Result<unit, int * string> =
    let psi = ProcessStartInfo(program, args)
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    use proc = new Process(StartInfo = psi)
    proc.Start() |> ignore
    let stderr = proc.StandardError.ReadToEnd()
    proc.WaitForExit()
    if proc.ExitCode = 0 then Ok ()
    else Error (proc.ExitCode, stderr)

// Pass pipeline: arith-to-llvm BEFORE func-to-llvm (LLVM 20 requirement)
let loweringPipeline =
    "--convert-arith-to-llvm --convert-cf-to-llvm --convert-func-to-llvm --reconcile-unrealized-casts"

let compileMlir (mlirText: string) (outputBinary: string) : Result<unit, PipelineError> =
    // 1. Write .mlir to temp file
    // 2. mlir-opt <pipeline> input.mlir -o lowered.mlir
    // 3. mlir-translate --mlir-to-llvmir lowered.mlir -o output.ll
    // 4. clang -Wno-override-module output.ll -o outputBinary
    ...
```

### Pattern 4: FsLit Test for Compiler E2E

**What:** A `.flt` file that invokes the compiler CLI on source text (via `%input`), runs the resulting binary, echoes the exit code, and checks the output.

**FsLit variables:**
- `%input` — path to a temp file containing the `// --- Input:` section content
- `%output` — path to a temp file containing the `// --- Output:` section content (used as a reference file, NOT a binary path)
- `%s` — path to the test file itself
- `%S` — directory containing the test file

**Pattern for "compile FunLang source, run binary, verify exit code":**

```
// --- Command: bash -c 'OUTBIN=$(mktemp /tmp/langback_XXXXXX) && langbackend %input -o $OUTBIN && $OUTBIN; echo $?; rm -f $OUTBIN'
// --- Input:
42
// --- Output:
42
```

For Phase 1, before there is a real CLI, the test can hardcode the `.mlir` generation:

```
// --- Command: bash -c 'OUTBIN=$(mktemp /tmp/r42_XXXXXX) && dotnet run --project /path/to/FunLangCompiler.Cli -- %input -o $OUTBIN && $OUTBIN; echo $?; rm -f $OUTBIN'
// --- Input:
return 42
// --- Output:
42
```

**Key insight about FsLit:** FsLit compares stdout of the command against `// --- Output:`. It does NOT have a native "exit code" assertion. The convention is: `$OUTBIN; echo $?` appends the exit code as the last line of stdout, then `// --- Output:` contains `42`. This is the standard FunLangCompiler testing pattern.

### Anti-Patterns to Avoid

- **P/Invoke in Phase 1:** The architecture explicitly excludes MLIR C API bindings. MlirIR is a pure F# DU — no `nativeint` handles, no `DllImport`, no `libMLIR.so`.
- **String-building with concatenation:** The Printer should pattern-match on the MlirIR DU, not concatenate ad-hoc strings in the pipeline. Keep print logic in `Printer.fs`, not in `Pipeline.fs`.
- **Deferring the linking step:** Verify that `clang` produces a runnable ELF before writing more IR logic. The linking step has independent failure modes.
- **Using `mlir-opt` flag order `--convert-func-to-llvm` before `--convert-arith-to-llvm`:** LLVM 20 requires arith lowering BEFORE func lowering (see Pitfall 3 below).

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| MLIR text serialisation | Custom MLIR text printer from scratch | Pattern-match MlirIR DU in `Printer.fs` | The DU cases directly correspond to MLIR ops; a simple recursive printer is 50–100 lines |
| Subprocess management | Custom process pool or async wrappers | `System.Diagnostics.Process` directly | Sequential process calls are sufficient; async adds no value for a batch compiler |
| Exit code checking in tests | Custom test runner | FsLit with `echo $?` convention | FsLit already handles temp file creation, substitution, and output comparison |
| MLIR pass pipeline discovery | Dynamic pass enumeration | Hardcode the four-pass string | The pipeline is fixed for this dialect set; discovery would be overengineering |

**Key insight:** This phase is entirely about wiring up known-good tools in the correct order. The value is in the integration, not any individual piece. Resist abstracting until the pipeline works end-to-end.

---

## Common Pitfalls

### Pitfall 1: Wrong pass order — `func-to-llvm` before `arith-to-llvm`

**What goes wrong:** mlir-opt verifier fires with "type mismatch" errors inside the lowered function body.

**Why it happens:** LLVM 20 (PR #120548) removed the implicit arith→llvm step from inside func→llvm. Once `func.func` is converted to `llvm.func`, the verifier enforces LLVM-dialect legality on all operands. Any remaining `arith` ops are illegal inside an already-lowered function.

**How to avoid:** Always use this exact pass order:
```
--convert-arith-to-llvm --convert-cf-to-llvm --convert-func-to-llvm --reconcile-unrealized-casts
```

**Warning signs:** `mlir-opt` exits non-zero with "failed to legalize" errors on the first run.

### Pitfall 2: `unrealized_conversion_cast` remaining after lowering

**What goes wrong:** `mlir-translate --mlir-to-llvmir` fails with "failed to legalize operation 'builtin.unrealized_conversion_cast'".

**Why it happens:** Dialect conversion inserts temporary cast ops when types don't match perfectly. If `--reconcile-unrealized-casts` is omitted or run before the other passes, these casts remain.

**How to avoid:** `--reconcile-unrealized-casts` must be the LAST pass in the pipeline. Never move it earlier.

**Warning signs:** `mlir-translate` fails immediately after a successful `mlir-opt` run.

### Pitfall 3: `clang` warning about module target triple

**What goes wrong:** `clang` emits `warning: overriding the module target triple with x86_64-unknown-linux-gnu [-Woverride-module]` because the MLIR-generated LLVM IR has no target triple set.

**Why it happens:** `mlir-translate` produces `.ll` without a target triple declaration. `clang` adds one automatically.

**How to avoid:** Pass `-Wno-override-module` to clang, or treat the warning as benign (clang still exits 0 and produces a correct binary).

**Warning signs:** Not a real problem — clang exits 0. Suppress with `-Wno-override-module` to keep CI output clean.

### Pitfall 4: MlirIR DU not extensible for future phases

**What goes wrong:** Phase 1 MlirIR uses a flat list of op strings instead of typed DU cases. Adding new ops in Phase 2 requires touching Printer logic scattered across the codebase.

**Why it happens:** "Phase 1 only needs `return 42`, so let's just use strings" thinking.

**How to avoid:** Define the full DU shape upfront — `MlirModule / FuncOp / MlirRegion / MlirBlock / MlirOp / MlirValue / MlirType` — even if only two `MlirOp` cases exist in Phase 1 (`ArithConstantOp` and `ReturnOp`). The shape is stable; only the cases grow.

### Pitfall 5: FsLit `%output` confusion

**What goes wrong:** Using `%output` as the path for the compiled binary. FsLit writes the expected output content into the `%output` file — if you also write a binary there, the file is clobbered.

**Why it happens:** The name `%output` suggests "output file" but it means "file containing expected output text."

**How to avoid:** Use `mktemp` for the binary path. Reserve `%output` for FsLit's own use (or don't reference it in the command at all if you don't need it).

### Pitfall 6: `main() -> i64` exit code truncation

**What goes wrong:** The binary exits with the lower 8 bits of the i64 return value. For values > 255, the exit code wraps around. For smoke testing `return 42`, this is fine (42 < 256). For general correctness verification in later phases, use stdout (`printf "%d\n"`) instead of exit codes.

**Why it happens:** POSIX exit codes are 8-bit unsigned integers.

**How to avoid:** For Phase 1 smoke test, `return 42` is safe. Document the limitation. For Phase 2+, consider printing the result to stdout and using `// --- Output:` comparison instead of exit code checking.

---

## Code Examples

### Verified: Minimal MLIR 20 text for `return 42`

```mlir
module {
  func.func @main() -> i64 {
    %c42 = arith.constant 42 : i64
    return %c42 : i64
  }
}
```

This file was fed to the full pipeline and the resulting binary exited with code 42. Verified 2026-03-26.

### Verified: mlir-opt pass pipeline for func+arith→llvm

```bash
mlir-opt \
  --convert-arith-to-llvm \
  --convert-cf-to-llvm \
  --convert-func-to-llvm \
  --reconcile-unrealized-casts \
  input.mlir \
  -o lowered.mlir
```

Exit 0 on the `return 42` module. Verified 2026-03-26.

### Verified: mlir-translate flag

```bash
mlir-translate --mlir-to-llvmir lowered.mlir -o output.ll
```

Produces valid LLVM IR with `define i64 @main() { ret i64 42 }`. Verified 2026-03-26.

### Verified: clang compile to ELF

```bash
clang -Wno-override-module output.ll -o program
```

Exit 0; `./program; echo $?` prints `42`. Verified 2026-03-26.

### Verified: FsLit E2E test pattern for compiler

```
// --- Command: bash -c 'OUTBIN=$(mktemp /tmp/langback_XXXXXX) && <compiler_invocation> %input -o $OUTBIN && $OUTBIN; echo $?; rm -f $OUTBIN'
// --- Input:
<source code>
// --- Output:
<expected exit code as decimal>
```

FsLit compares stdout of the command against `// --- Output:`. The `echo $?` pattern prints the exit code as the last stdout line.

### F# MlirIR DU — Phase 1 minimal shape

```fsharp
module MlirIR

type MlirType =
    | I64
    | I32
    | I1

type MlirValue = {
    Name: string
    Type: MlirType
}

type MlirOp =
    | ArithConstantOp of result: MlirValue * value: int64
    | ReturnOp        of operands: MlirValue list

type MlirBlock = {
    Args: MlirValue list
    Body: MlirOp list
}

type MlirRegion = {
    Blocks: MlirBlock list
}

type FuncOp = {
    Name:       string
    InputTypes: MlirType list
    ReturnType: MlirType option
    Body:       MlirRegion
}

type MlirModule = {
    Funcs: FuncOp list
}

// Hardcoded return 42 — the Phase 1 target value
let return42Module : MlirModule =
    let c42 = { Name = "%c42"; Type = I64 }
    {
        Funcs = [
            {
                Name       = "@main"
                InputTypes = []
                ReturnType = Some I64
                Body = {
                    Blocks = [
                        {
                            Args = []
                            Body = [
                                ArithConstantOp(c42, 42L)
                                ReturnOp [c42]
                            ]
                        }
                    ]
                }
            }
        ]
    }
```

### F# Printer — Phase 1 implementation sketch

```fsharp
module Printer

open MlirIR

let private printType = function
    | I64 -> "i64"
    | I32 -> "i32"
    | I1  -> "i1"

let private printValue (v: MlirValue) = v.Name

let private printOp (indent: string) = function
    | ArithConstantOp(result, value) ->
        sprintf "%s%s = arith.constant %d : %s"
            indent result.Name value (printType result.Type)
    | ReturnOp [] ->
        sprintf "%sreturn" indent
    | ReturnOp operands ->
        let ops = operands |> List.map (fun v -> sprintf "%s : %s" v.Name (printType v.Type))
        sprintf "%sreturn %s" indent (String.concat ", " ops)

let private printBlock (indent: string) (block: MlirBlock) =
    block.Body |> List.map (printOp (indent + "  ")) |> String.concat "\n"

let private printFuncOp (func: FuncOp) =
    let retType =
        match func.ReturnType with
        | None   -> ""
        | Some t -> sprintf " -> %s" (printType t)
    let body = func.Body.Blocks |> List.map (printBlock "  ") |> String.concat "\n"
    sprintf "  func.func %s() -> %s {\n%s\n  }" func.Name (printType (func.ReturnType |> Option.get)) body

let printModule (m: MlirModule) : string =
    let funcs = m.Funcs |> List.map printFuncOp |> String.concat "\n"
    sprintf "module {\n%s\n}" funcs
```

### F# Pipeline.fs — Process invocation pattern

```fsharp
module Pipeline

open System.Diagnostics
open System.IO

type CompileError =
    | MlirOptFailed   of exitCode: int * stderr: string
    | TranslateFailed of exitCode: int * stderr: string
    | ClangFailed     of exitCode: int * stderr: string

let private runTool (program: string) (args: string) : Result<unit, int * string> =
    let psi = ProcessStartInfo(program, args)
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    use proc = new Process(StartInfo = psi)
    proc.Start() |> ignore
    let stderr = proc.StandardError.ReadToEnd()
    proc.WaitForExit()
    if proc.ExitCode = 0 then Ok ()
    else Error (proc.ExitCode, stderr)

/// Full pipeline: MlirModule -> ELF binary
let compile (m: MlirIR.MlirModule) (outputPath: string) : Result<unit, CompileError> =
    let mlirText  = Printer.printModule m
    let mlirFile  = Path.GetTempFileName() + ".mlir"
    let lowered   = Path.GetTempFileName() + ".mlir"
    let llFile    = Path.GetTempFileName() + ".ll"
    try
        File.WriteAllText(mlirFile, mlirText)

        let passArgs =
            sprintf "--convert-arith-to-llvm --convert-cf-to-llvm --convert-func-to-llvm --reconcile-unrealized-casts %s -o %s"
                mlirFile lowered
        match runTool "mlir-opt" passArgs with
        | Error (code, err) -> Error (MlirOptFailed (code, err))
        | Ok () ->

        match runTool "mlir-translate" (sprintf "--mlir-to-llvmir %s -o %s" lowered llFile) with
        | Error (code, err) -> Error (TranslateFailed (code, err))
        | Ok () ->

        match runTool "clang" (sprintf "-Wno-override-module %s -o %s" llFile outputPath) with
        | Error (code, err) -> Error (ClangFailed (code, err))
        | Ok () -> Ok ()
    finally
        for f in [mlirFile; lowered; llFile] do
            if File.Exists f then File.Delete f
```

---

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `convert-func-to-llvm` implicitly ran arith lowering | Must explicitly run `--convert-arith-to-llvm` before `--convert-func-to-llvm` | LLVM 20 (PR #120548) | Pass order must be explicit; affects every func+arith pipeline |
| P/Invoke MLIR C API | Text-based MLIR generation | Architecture decision for this project | No ownership/lifetime issues; debuggable intermediate files |
| mlir-opt-20 (version-suffixed binary) | mlir-opt (version-less, at /usr/local/bin) | Installed as default | Use `mlir-opt` not `mlir-opt-20` on this system |

**Confirmed working binary names on this system:**
- `mlir-opt` (not `mlir-opt-20`)
- `mlir-translate` (not `mlir-translate-20`)
- `clang` or `clang-20` (both present; use `clang`)

---

## Open Questions

1. **dotnet solution structure**
   - What we know: No `.sln` or `.fsproj` files exist yet in FunLangCompiler
   - What's unclear: Should CLI be in `FunLangCompiler.Cli` or `FunLang.Compiler.Cli` (there was an untracked `src/FunLang.Compiler.Cli/` in git status)?
   - Recommendation: The planner should create `src/FunLangCompiler.Compiler/` (library) + `src/FunLangCompiler.Cli/` (console). The git status shows an orphaned `src/FunLang.Compiler.Cli/` directory — either clean it up or use that naming convention; pick one and be consistent.

2. **FsLit test for Phase 1 before CLI exists**
   - What we know: FsLit requires a command that can run. In Phase 1, there's no CLI binary yet.
   - What's unclear: Should the FsLit test use `dotnet run --project ...` or wait for a proper CLI?
   - Recommendation: Use `dotnet run --project src/FunLangCompiler.Cli -- %input -o $OUTBIN` in the `.flt` command. This is slower but avoids publishing a binary in Phase 1.

3. **`main() -> i64` vs `main() -> i32`**
   - What we know: Both work on this system; `i64` return from `@main` is truncated to 8 bits by the OS exit code mechanism. `i32` is the C standard for `main`.
   - Recommendation: Use `i64` for Phase 1 (matching FunLang's `int` type = i64). For Phase 6 CLI, consider wrapping in a proper `main(argc, argv) -> i32` that calls an inner `eval() -> i64`.

---

## Sources

### Primary (HIGH confidence — verified on this system 2026-03-26)

- MLIR 20.1.4 installed at `/usr/local/bin/mlir-opt` — confirmed `--version` and pipeline execution
- `mlir-translate --mlir-to-llvmir` — confirmed flag name and output format
- `clang 20.1.4` — confirmed `-Wno-override-module` flag and ELF binary output
- FsLit source at `/home/shoh/vibe-coding/FsLit/FsLit/src/` — confirmed `%input`, `%output`, `%s`, `%S` substitution variables; confirmed stdout-only output comparison (no native exit code checking)
- End-to-end pipeline test: `module { func.func @main() -> i64 { ... return 42 } }` → binary that exits 42

### Secondary (HIGH confidence — project research docs)

- `.planning/research/ARCHITECTURE.md` — pipeline design, dialect selection, if-else/let-rec patterns
- `.planning/research/STACK.md` — LLVM 20 pass ordering rationale, tool paths, P/Invoke patterns
- `.planning/research/PITFALLS.md` — ownership rules, conversion cast pitfalls, pass order pitfalls

### Tertiary (MEDIUM confidence — not re-verified in this session)

- LLVM upstream PR #120548 — confirms `arith-to-llvm` must precede `func-to-llvm` in LLVM 20
- FunLang `Ast.fs` — Expr DU shape; relevant for Phase 2+ Elaboration design

---

## Metadata

**Confidence breakdown:**
- Standard Stack: HIGH — tools verified present and functional on this exact machine
- Architecture (MlirIR DU shape): HIGH — directly derived from MLIR's own IR concepts, matches project ARCHITECTURE.md
- Pipeline pass order: HIGH — verified by running the pipeline; backed by upstream PR documentation
- FsLit test format: HIGH — read FsLit source code (Parser.fs, Substitution.fs, Runner.fs, Checker.fs)
- Pitfalls: HIGH for pass-order and unrealized-cast issues (verified); MEDIUM for extensibility concerns (design judgment)

**Research date:** 2026-03-26
**Valid until:** 2026-04-25 (stable tools; MLIR 20.x ABI will not change within patch versions)
