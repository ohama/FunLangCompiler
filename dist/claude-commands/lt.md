# FunLang Compiler Expert

Compile, run, and debug FunLang (.fun/.lt) files using the native compiler.

**Usage:** `/lt [compile|run|check] <file>` or `/lt` for language help.

## Arguments

- `compile <file>` — Compile to native binary in /tmp
- `run <file>` — Compile and run
- `check` — Compile all .fun files in current directory
- (no args) — Show language quick reference

## Compiler

**Binary:** `dist/FunLangCompiler.Cli`

```bash
# Compile
FunLangCompiler.Cli source.fun -o output_binary

# Compile and run
OUTBIN=$(mktemp /tmp/langback_XXXXXX) && FunLangCompiler.Cli source.fun -o $OUTBIN && $OUTBIN; rm -f $OUTBIN
```

Prelude (12 modules) auto-loads from `Prelude/` relative to source file.

## Process

When the user provides arguments:

### `/lt compile <file>`
```bash
OUTBIN=$(mktemp /tmp/langback_XXXXXX)
dist/FunLangCompiler.Cli "$FILE" -o "$OUTBIN" 2>&1
echo "Compiled: $OUTBIN"
```

### `/lt run <file>`
```bash
OUTBIN=$(mktemp /tmp/langback_XXXXXX)
dist/FunLangCompiler.Cli "$FILE" -o "$OUTBIN" 2>&1 && "$OUTBIN"
RC=$?; rm -f "$OUTBIN"; exit $RC
```

### `/lt check`
```bash
for f in *.fun; do
  echo -n "$f: "
  OUTBIN=$(mktemp /tmp/langback_XXXXXX)
  if dist/FunLangCompiler.Cli "$f" -o "$OUTBIN" 2>&1; then
    echo "OK"
  else
    echo "FAIL"
  fi
  rm -f "$OUTBIN"
done
```

### `/lt` (no args) — Quick Reference

Show the language reference below.

## Language Reference

### Declarations

```
let name = expr                          -- value binding
let name p1 p2 = expr                   -- function (curried)
let name (p : type) : retType = expr    -- type annotations
let rec name param = expr               -- recursive
let rec f x = ... and g y = ...         -- mutual recursion
let mut name = expr                     -- mutable
type T = A | B of type                  -- ADT
type T<'a> = ...                        -- generic ADT
type T = { f1: type; f2: type }         -- record
exception E of type                     -- exception
module M = decl+                        -- module
open M                                  -- open module
open "file.fun"                         -- file import
```

### Expressions

```
fun x -> expr                           -- lambda
f x, x |> f                            -- application, pipe
if c then e1 else e2                    -- conditional
match e with | P -> body                -- pattern match
try e with | P -> handler               -- exception
e1; e2                                  -- sequencing
while c do body                         -- while
for i = a to b do body                  -- for
for x in coll do body                   -- for-in
[for x in xs -> f x]                    -- list comprehension
```

### Built-in Functions

**I/O:** `print`, `println`, `printfn`, `eprint`, `eprintln`, `sprintf`, `stdin_read_line`, `stdin_read_all`, `get_args`

**String:** `string_length`, `string_concat`, `string_sub`, `string_contains`, `string_startswith`, `string_endswith`, `string_trim`, `string_concat_list`, `string_to_int`, `to_string`

**Char:** `char_to_int`, `int_to_char`, `char_is_digit`, `char_is_letter`, `char_is_upper`, `char_is_lower`, `char_to_upper`, `char_to_lower`

**Collections:** `array_*` (create/init/get/set/length/of_list/to_list/iter/map/fold/sort), `hashtable_*` (create/create_str/get/set/containsKey/remove/keys/keys_str/count), `hashset_*`, `queue_*`, `mutablelist_*`, `stringbuilder_*`, `list_sort_by`

**File:** `read_file`, `write_file`, `append_file`, `file_exists`, `read_lines`, `write_lines`, `get_env`, `get_cwd`, `path_combine`, `dir_files`

**Other:** `failwith`, `raise`

### Prelude Modules (auto-loaded)

`Core` (id, const, compose, flip, not, min, max, abs, fst, snd, ignore, (^^))
`List` (map, filter, fold, length, reverse, append, hd, tl, zip, take, drop, any, all, flatten, nth, (++), isEmpty, sort, sortBy, mapi, tryFind, choose, distinctBy)
`Option` (optionMap, optionBind, optionDefault, isSome, isNone, (<|>))
`Result` (resultMap, resultBind, resultMapError, isOk, isError, resultToOption)
`String` (concat, endsWith, startsWith, trim, length, contains)
`Char` (IsDigit, ToUpper, IsLetter, IsUpper, IsLower, ToLower)
`Array`, `Hashtable`, `HashSet`, `MutableList`, `Queue`, `StringBuilder`

### Key Differences from F#

- Native binary via MLIR/LLVM (not .NET)
- `int` is 64-bit, Boehm GC
- `let mut` (not `let mutable`)
- `hashtable_create_str` for string keys (separate from int keys)
- `open "file.fun"` for file imports
- Type annotations parsed but ignored at codegen

### Common Error Patterns

| Error | Fix |
|-------|-----|
| `parse error` | Check indentation (spaces only) |
| `unbound variable` | Ensure defined before use |
| `not a known function` | Function not in scope |
| `mlir-opt failed` | Compiler bug — check MLIR type mismatch in error |
| `Circular import` | Break import cycle |
