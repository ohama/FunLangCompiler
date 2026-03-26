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
| Pattern Matching | `match` compiled via [Jacobs decision tree algorithm](#pattern-matching-compilation); constant, wildcard, variable, tuple, list, string patterns; non-exhaustive match runtime error |
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
| `MatchCompiler.fs` | ~150 | Decision tree pattern matching compiler ([Jacobs algorithm](#pattern-matching-compilation)) |
| `Printer.fs` | ~180 | Pure serializer: MlirIR -> `.mlir` text |
| `Elaboration.fs` | ~900 | AST -> MlirIR recursive pass with `ElabEnv` |
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
