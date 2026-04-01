---
description: "FunLang native compiler expert — compile, run, debug .fun/.lt files"
trigger: "TRIGGER when: user asks to compile, run, or debug FunLang/FunLangCompiler code, or when working with .fun/.lt files. Also trigger when user asks about FunLang syntax, builtins, Prelude modules, or compiler errors."
---

# FunLang Compiler Expert

You are an expert in **FunLang**, an ML-family language compiled to native binaries via MLIR/LLVM.

## Compiler

**Binary:** `dist/FunLangCompiler.Cli`

```bash
# Compile
FunLangCompiler.Cli source.fun -o output_binary

# Compile and run
OUTBIN=$(mktemp /tmp/langback_XXXXXX) && FunLangCompiler.Cli source.fun -o $OUTBIN && $OUTBIN; rm -f $OUTBIN
```

- Prelude (12 modules) auto-loads from `Prelude/` directory relative to source file
- Multi-file: `open "other.fun"` imports declarations (relative path from source)
- Pipeline: F# parser → Elaboration → MLIR text → mlir-opt → mlir-translate → clang → native binary
- Requires: `mlir-opt`, `mlir-translate`, `clang`, `libgc` (Boehm GC)

## Language Reference

### Declarations

```
let name = expr                          -- value binding
let name param1 param2 = expr           -- function (curried)
let name (p : type) : retType = expr    -- with type annotations (ignored at codegen)
let rec name param = expr               -- recursive function
let rec f x = ... and g y = ...         -- mutual recursion
let mut name = expr                     -- mutable variable
type Name = Ctor1 | Ctor2 of type      -- algebraic data type
type Name<'a> = ...                     -- generic ADT
type Name = { field1: type; ... }       -- record type
exception Name of type                  -- exception
module Name = decl+                     -- module (compile-time flattening)
open ModuleName                         -- bring module members into scope
open "file.fun"                         -- file import
```

### Expressions

```
let x = e1 in e2                        -- local binding
fun x -> expr                           -- lambda
fun (x : int) -> expr                   -- annotated lambda
f x                                     -- application (curried, left-assoc)
x |> f                                  -- pipe (= f x)
f >> g                                  -- compose right (= fun x -> g(f(x)))
if cond then e1 else e2                 -- conditional
if cond then e1                         -- if-then (unit else)
match expr with | P1 -> e1 | P2 -> e2  -- pattern matching
try expr with | P -> handler            -- exception handling
raise (ExnName value)                   -- raise exception
e1; e2                                  -- sequencing
x <- newValue                           -- mutable assign
while cond do body                      -- while loop
for i = start to stop do body           -- for loop
for x in collection do body             -- for-in loop
[for x in xs -> f x]                    -- list comprehension
[1; 2; 3]                               -- list literal
[1..10]                                 -- range
(a, b, c)                               -- tuple
{field1 = v1; field2 = v2}             -- record
{existing with field = newVal}          -- record update
arr.[i]                                 -- index read
arr.[i] <- v                            -- index write
s.[start..stop]                         -- string slice
```

### Patterns

```
_                                       -- wildcard
x                                       -- variable binding
42 | true | "hello" | 'c'              -- constant
Ctor | Ctor arg                         -- constructor
(a, b)                                  -- tuple
h :: t                                  -- cons (head :: tail)
[]                                      -- empty list
[a; b; c]                               -- list literal
{field = pat}                           -- record
P1 | P2                                 -- or-pattern
pat when guard                          -- when guard
```

### Types (annotations only, ignored at codegen)

```
int, bool, string, char, unit           -- primitives
'a, 'b                                  -- type variables
int list                                -- postfix application
Result<'a>                              -- angle-bracket generic
int -> bool                             -- function type
int * string                            -- tuple type
```

### Operators (by precedence, low → high)

```
|>                                      -- pipe
>> <<                                   -- compose
|| &&                                   -- logical (short-circuit)
= <> < > <= >=                          -- comparison
:: @ ^                                  -- cons, user infix
+ - * / %                               -- arithmetic
**                                      -- exponentiation (right-assoc)
f x                                     -- application
- (unary)                               -- negation
```

### Built-in Functions

**I/O:**
`print`, `println`, `printfn`, `eprint`, `eprintln`, `eprintfn`, `sprintf`, `stdin_read_line`, `stdin_read_all`, `get_args`

**String/Char:**
`string_length`, `string_concat`, `string_sub`, `string_contains`, `string_startswith`, `string_endswith`, `string_trim`, `string_concat_list`, `string_to_int`, `to_string`, `char_to_int`, `int_to_char`, `char_is_digit`, `char_is_letter`, `char_is_upper`, `char_is_lower`, `char_to_upper`, `char_to_lower`

**Collections:**
`array_create`, `array_init`, `array_get`, `array_set`, `array_length`, `array_of_list`, `array_to_list`, `array_iter`, `array_map`, `array_fold`, `array_sort`, `array_of_seq`, `hashtable_create`, `hashtable_create_str`, `hashtable_get`, `hashtable_set`, `hashtable_containsKey`, `hashtable_remove`, `hashtable_keys`, `hashtable_keys_str`, `hashtable_trygetvalue`, `hashtable_count`, `list_sort_by`, `list_of_seq`, `hashset_create`, `hashset_add`, `hashset_contains`, `hashset_count`, `queue_create`, `queue_enqueue`, `queue_dequeue`, `queue_count`, `mutablelist_create`, `mutablelist_add`, `mutablelist_get`, `mutablelist_set`, `mutablelist_count`, `stringbuilder_create`, `stringbuilder_append`, `stringbuilder_tostring`

**File/System:**
`read_file`, `write_file`, `append_file`, `file_exists`, `read_lines`, `write_lines`, `get_env`, `get_cwd`, `path_combine`, `dir_files`, `failwith`

### Prelude Modules (auto-loaded)

| Module | Key functions |
|--------|--------------|
| Core | `id`, `const`, `compose`, `flip`, `(^^)` (string concat), `not`, `min`, `max`, `abs`, `fst`, `snd`, `ignore` |
| List | `map`, `filter`, `fold`, `length`, `reverse`, `append`, `hd`, `tl`, `zip`, `take`, `drop`, `any`, `all`, `flatten`, `nth`, `(++)`, `isEmpty`, `sort`, `sortBy`, `mapi`, `tryFind`, `choose`, `distinctBy` |
| Option | `optionMap`, `optionBind`, `optionDefault`, `isSome`, `isNone`, `(<\|>)` |
| Result | `resultMap`, `resultBind`, `resultMapError`, `isOk`, `isError`, `resultToOption` |
| String | `concat`, `endsWith`, `startsWith`, `trim`, `length`, `contains` |
| Char | `IsDigit`, `ToUpper`, `IsLetter`, `IsUpper`, `IsLower`, `ToLower` |
| Array | `create`, `get`, `set`, `length`, `ofList`, `toList`, `iter`, `map`, `fold`, `init`, `sort` |
| Hashtable | `create`, `createStr`, `get`, `set`, `containsKey`, `keys`, `keysStr`, `remove`, `tryGetValue`, `count` |
| HashSet | `create`, `add`, `contains`, `count` |
| MutableList | `create`, `add`, `get`, `set`, `count` |
| Queue | `create`, `enqueue`, `dequeue`, `count` |
| StringBuilder | `create`, `add`, `toString` |

### Common Patterns

```fsharp
// Hello world
let _ = println "hello"

// Factorial
let rec fact n = if n <= 1 then 1 else n * fact (n - 1)

// List processing
let doubled = List.map (fun x -> x * 2) [1; 2; 3]
let evens = List.filter (fun x -> x % 2 = 0) [1; 2; 3; 4]
let sum = List.fold (fun acc x -> acc + x) 0 [1; 2; 3]

// ADT + pattern matching
type Shape = Circle of int | Rect of int * int
let area s = match s with
    | Circle r -> r * r * 3
    | Rect (w, h) -> w * h

// Module usage
module Math =
    let square x = x * x
    let cube x = x * x * x
open Math
let _ = println (to_string (square 5))

// Mutual recursion
let rec even n = if n = 0 then true else odd (n - 1)
and odd n = if n = 0 then false else even (n - 1)

// Mutable + loop
let mut sum = 0
for i = 1 to 100 do
    sum <- sum + i
let _ = println (to_string sum)

// File I/O
let content = read_file "input.txt"
let _ = write_file "output.txt" content

// Exception handling
exception NotFound of string
let find key =
    try hashtable_get ht key
    with _ -> raise (NotFound key)

// String formatting
let msg = sprintf "result: %d" 42
let _ = printfn "hello %s, you are %d" name age

// Multi-file import
open "utils.fun"
```

### Compiler Error Patterns

| Error | Cause | Fix |
|-------|-------|-----|
| `parse error` | Syntax error or indentation issue | Check indentation (spaces only, no tabs) |
| `unbound variable 'x'` | Variable not in scope | Check spelling, ensure `let` before use |
| `unsupported App — 'f' is not a known function` | Calling undefined function | Check function is defined before use |
| `mlir-opt failed` | MLIR validation error | Usually a compiler bug, report it |
| `Circular import detected` | A imports B imports A | Break the cycle |

### Key Differences from F#

- No `.NET` runtime — compiles to native via MLIR/LLVM
- Boehm GC instead of .NET GC
- `int` is 64-bit (not 32-bit)
- No `printfn "%b"` — use `to_string` for bools
- Hashtable: `create`/`createStr` separate (int vs string keys)
- `open "file.fun"` for file imports (not `#load`)
- No `namespace` with dots, no `private`, no `mutable` keyword on let (use `let mut`)
- Type annotations parsed but ignored at codegen (uniform I64/Ptr representation)
