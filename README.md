# FunLangCompiler

FunLang 소스 코드를 네이티브 x86-64 바이너리로 컴파일하는 컴파일러 백엔드.

F#으로 구현되며, [FunLang](deps/FunLang) 프론트엔드(파서/타입체커)를 재사용한다. 내부 IR(MlirIR)을 MLIR 텍스트로 직렬화한 뒤 `mlir-opt` → `mlir-translate` → `clang` 파이프라인으로 네이티브 바이너리를 생성한다.

## Quick Start

```bash
# Clone with submodules
git clone --recursive https://github.com/ohama/FunLangCompiler.git
cd FunLangCompiler

# If already cloned without --recursive
git submodule update --init

# Build
dotnet build src/FunLangCompiler.Cli

# Compile a source file (default -O2 optimization)
fnc hello.fun

# With explicit output name
fnc hello.fun -o hello

# Optimization levels
fnc hello.fun -O0    # no optimization
fnc hello.fun -O2    # default
fnc hello.fun -O3    # aggressive

# Run the compiled binary
./hello

# Or use dotnet run directly
dotnet run --project src/FunLangCompiler.Cli -- hello.fun -o hello
```

## Binary Names

| Binary | Repository | Description |
|--------|-----------|-------------|
| `fn` | [FunLang](https://github.com/ohama/FunLang) | FunLang 인터프리터 |
| `fnc` | [FunLangCompiler](https://github.com/ohama/FunLangCompiler) | FunLang → 네이티브 컴파일러 |

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
| Pattern Matching | `match` compiled via [Jacobs decision tree algorithm](#pattern-matching-compilation); constant, wildcard, variable, tuple, list, string patterns; non-exhaustive match runtime error |
| I/O | `print`, `println` builtins |
| GC | Boehm GC (`libgc`) for all heap allocation |

## Architecture

```
FunLang AST
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
| `MatchCompiler.fs` | ~150 | Decision tree pattern matching compiler ([Jacobs algorithm](#pattern-matching-compilation)) |
| `Printer.fs` | ~180 | Pure serializer: MlirIR -> `.mlir` text |
| `Elaboration.fs` | ~4600 | AST -> MlirIR recursive pass with `ElabEnv` |
| `Pipeline.fs` | ~110 | Shell pipeline: `mlir-opt` -> `mlir-translate` -> `clang` |
| `lang_runtime.c` | ~1450 | C runtime: string, array, hashtable, collection, I/O builtins |
| `Program.fs` | ~235 | CLI entry point: parse -> elaborate -> compile |

## Dependencies

외부 의존성은 git submodule로 관리된다. `git clone --recursive` 또는 `git submodule update --init`으로 받는다.

| Submodule | Path | Purpose |
|-----------|------|---------|
| [FunLang](https://github.com/ohama/FunLang) | `deps/FunLang` | 프론트엔드 (파서/타입체커/AST) |
| [fslit](https://github.com/ohama/fslit) | `deps/fslit` | E2E 테스트 러너 |

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/)
- [LLVM 20](https://llvm.org/) (`mlir-opt`, `mlir-translate`, `clang`)
- [Boehm GC](https://www.hboehm.info/gc/) (`libgc`)

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

234개의 fslit E2E 테스트. 테스트 러너는 submodule(`deps/fslit`)로 포함되어 있다.

```bash
# Run all tests
dotnet run --project deps/fslit/FsLit -- tests/compiler/

# Run a specific test
dotnet run --project deps/fslit/FsLit -- tests/compiler/04-01-fact.flt
```

Each `.flt` file compiles a FunLang source file, runs the binary, and verifies the output.

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

## Pattern Matching Compilation

Pattern matching is compiled using the decision tree algorithm from [Jules Jacobs, "How to compile pattern matching" (2021)](https://julesjacobs.com/notes/patternmatching/patternmatching.pdf), based on [Maranget 2008](https://dl.acm.org/doi/10.1145/1411304.1411311).

### Algorithm Overview

The compiler transforms `match` expressions into binary decision trees that perform **no unnecessary tests**:

1. **Clause representation**: Each match arm becomes a clause with explicit tests `{a1 is pat1, a2 is pat2, ...} => body`
2. **Variable elimination**: Tests against bare variables (`a is x`) are pushed into the body as bindings
3. **Branching heuristic**: Select the test from the first clause that appears in the **maximum number** of other clauses (minimizes clause duplication in the tree)
4. **Binary splitting**: For the selected test `a is C(P1,...,Pn)`:
   - **(a)** Clause has same constructor C → expand sub-patterns, add to **match** branch
   - **(b)** Clause has different constructor D → add to **no-match** branch
   - **(c)** Clause has no test for `a` → add to **both** branches
5. **Recursion**: Generate sub-trees for match/no-match branches
6. **Base cases**: Empty clauses → `Fail` (match failure); first clause has no tests → `Leaf` (success)

### Decision Tree IR

```fsharp
type DecisionTree =
    | Leaf of bindings * bodyIndex    // Pattern matched — bind variables, run body
    | Fail                            // No pattern matched — runtime error
    | Switch of scrutinee * ctor * args * ifMatch * ifNoMatch  // Binary test
```

### Supported Patterns

| Pattern | Constructor Tag | Sub-fields |
|---------|----------------|------------|
| `42`, `true` | `IntLit(42)`, `BoolLit(true)` | 0 |
| `"hello"` | `StringLit("hello")` | 0 |
| `[]` | `NilCtor` | 0 |
| `h :: t` | `ConsCtor` | 2 (head, tail) |
| `(a, b, c)` | `TupleCtor(3)` | 3 |
| `x`, `_` | (eliminated as bindings) | — |

### Code Generation

The `DecisionTree` is lowered to MLIR `cf.cond_br` chains in `Elaboration.fs`. Each `Switch` node becomes a conditional branch; `Leaf` nodes emit variable bindings and elaborate the body expression; `Fail` nodes call `@lang_match_failure`.

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
