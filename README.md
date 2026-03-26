# LangBackend

LangThree 소스 코드를 네이티브 x86-64 바이너리로 컴파일하는 컴파일러 백엔드.

F#으로 구현되며, [LangThree](../LangThree) 프론트엔드(파서/타입체커)를 재사용한다. 내부 IR(MlirIR)을 MLIR 텍스트로 직렬화한 뒤 `mlir-opt` → `mlir-translate` → `clang` 파이프라인으로 네이티브 바이너리를 생성한다.

## Quick Start

```bash
# Build
dotnet build

# Compile a LangThree source file
dotnet run --project src/LangBackend.Cli -- hello.lt

# With explicit output name
dotnet run --project src/LangBackend.Cli -- hello.lt -o hello

# Run the compiled binary
./hello
```

## Supported Language Features

| Category | Features |
|----------|----------|
| Scalars | Integer literals (`i64`), boolean literals (`i1`), arithmetic (`+`, `-`, `*`, `/`), comparisons (`=`, `<>`, `<`, `>`, `<=`, `>=`) |
| Logic | `&&`, `||` (short-circuit), `if-else` |
| Bindings | `let`, `let rec` (recursive functions) |
| Functions | Lambda with free-variable capture, closures (`{fn_ptr, env}`), direct and indirect call dispatch |
| Strings | Heap-allocated `{length, data}` structs, `string_length`, `string_concat`, `to_string`, `=`/`<>` via `strcmp` |
| Tuples | N-ary tuple construction, `let (a, b) = ...` destructuring |
| Lists | `[]`, `h :: t` cons, `[e1; e2; ...]` literals, recursive processing |
| Pattern Matching | `match` with constant, wildcard, variable, tuple, list, string patterns; non-exhaustive match runtime error |
| I/O | `print`, `println` builtins |
| GC | Boehm GC (`libgc`) for all heap allocation |

## Architecture

```
LangThree AST
    |
    v
Elaboration (F#)     -- AST -> MlirIR translation
    |
    v
MlirIR (F# DU)       -- Typed internal IR
    |
    v
Printer (F#)          -- MlirIR -> .mlir text
    |
    v
mlir-opt              -- Lowering passes (arith/cf/func -> llvm)
    |
    v
mlir-translate        -- LLVM IR generation
    |
    v
clang + libgc         -- Native binary linking
```

### Source Files

| File | Lines | Purpose |
|------|-------|---------|
| `MlirIR.fs` | ~130 | Typed IR: `MlirModule`, `FuncOp`, `MlirOp` DU (~25 cases), `MlirType`, `MlirValue` |
| `Printer.fs` | ~180 | Pure serializer: MlirIR -> `.mlir` text |
| `Elaboration.fs` | ~870 | AST -> MlirIR recursive pass with `ElabEnv` |
| `Pipeline.fs` | ~110 | Shell pipeline: `mlir-opt` -> `mlir-translate` -> `clang` |
| `lang_runtime.c` | ~50 | C runtime: `lang_string_concat`, `lang_to_string_int/bool`, `lang_match_failure` |
| `Program.fs` | ~65 | CLI entry point: parse -> elaborate -> compile |

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/)
- [LLVM 20](https://llvm.org/) (`mlir-opt`, `mlir-translate`, `clang`)
- [Boehm GC](https://www.hboehm.info/gc/) (`libgc`)
- [LangThree](../LangThree) (sibling directory)

### macOS (Homebrew)

```bash
brew install llvm bdw-gc
```

### Linux (apt)

```bash
# LLVM 20
wget https://apt.llvm.org/llvm.sh && chmod +x llvm.sh && sudo ./llvm.sh 20

# Boehm GC
sudo apt install libgc-dev
```

## Testing

34 FsLit E2E tests covering all feature categories:

```bash
# Run all tests
dotnet fslit tests/compiler/

# Run a specific test
dotnet fslit tests/compiler/04-01-fact.flt
```

Each `.flt` file compiles a LangThree expression, runs the binary, and verifies the output.

## Examples

```ocaml
(* Factorial *)
let rec fact n = if n <= 1 then 1 else n * fact (n - 1) in fact 5
(* -> exits 120 *)

(* Fibonacci *)
let rec fib n = if n <= 1 then n else fib (n - 1) + fib (n - 2) in fib 10
(* -> exits 55 *)

(* Closures *)
let add_n n = fun x -> x + n in let add5 = add_n 5 in add5 3
(* -> exits 8 *)

(* List processing with pattern matching *)
let rec sum lst = match lst with | [] -> 0 | h :: t -> h + sum t in sum [1; 2; 3]
(* -> exits 6 *)

(* Tuples *)
let (a, b) = (3, 4) in a + b
(* -> exits 7 *)

(* Strings *)
let _ = println (string_concat "hello " "world") in 0
(* -> prints "hello world" *)
```

## MLIR Pipeline

The lowering pass order (LLVM 20):

```
--convert-arith-to-llvm
--convert-cf-to-llvm
--convert-func-to-llvm
--reconcile-unrealized-casts
```

## License

MIT
