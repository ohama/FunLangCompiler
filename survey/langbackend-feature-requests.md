# LangBackend Feature Requests for FunLexYacc Compilation

**Date:** 2026-03-28
**Author:** Generated from source analysis of LangThree, LangBackend, and FunLexYacc
**Purpose:** Enumerate every feature gap in LangBackend (the compiler) that prevents it from compiling FunLexYacc source files

## Context

- **LangThree** (`../LangThree/`) is the interpreter. It already supports all features FunLexYacc uses.
- **LangBackend** (`../LangBackend/`) is the AOT compiler (AST -> MLIR -> LLVM -> native binary). It currently has 144 E2E tests and reaches LangThree v5.0 feature parity for simple programs, but is missing several features required by FunLexYacc.
- **FunLexYacc** is a lexer/parser generator written entirely in LangThree `.fun` files. It uses .NET interop, sprintf, file import, list comprehensions, advanced List/Array functions, StringBuilder, Dictionary, HashSet, Queue, and System.IO.

LangBackend's `PROJECT.md` explicitly lists three items as "Out of Scope":
1. `printf/sprintf` format strings
2. `FileImportDecl` (multi-file import)
3. `get_args` (CLI argument access)

All three are **required** by FunLexYacc.

---

## Summary Table

| # | Feature | Category | LangThree | LangBackend | FunLexYacc Files | Priority | Effort |
|---|---------|----------|-----------|-------------|------------------|----------|--------|
| 1 | `sprintf` format strings | Language construct | Supported (Eval.fs:266) | Missing (Out of Scope) | DfaMin.fun:373, ParserTables.fun:496+ | P0 | HIGH |
| 2 | `printfn` format strings | Language construct | Supported (Eval.fs:258) | Missing (Out of Scope) | ParserTables.fun:490+ | P0 | HIGH |
| 3 | File import (`open "file.fun"`) | Language construct | Supported (Eval.fs:1342) | Missing (Out of Scope) | All 19 .fun files use `open ModuleName` cross-file | P0 | HIGH |
| 4 | `get_args` / CLI argv | Builtin | Supported (Eval.fs:378) | Missing (Out of Scope) | FunlexMain.fun:117, FunyaccMain.fun:111 | P0 | MEDIUM |
| 5 | `System.Text.StringBuilder` | .NET interop | Supported (interpreter) | Missing | LexParser.fun:156+, DfaMin.fun:349+, GrammarParser.fun:144+ | P0 | HIGH |
| 6 | `System.Collections.Generic.Dictionary` | .NET interop | Supported (interpreter) | Missing | Symtab.fun:16+, Dfa.fun:157+, DfaMin.fun:93+, Lr0.fun:72+ | P0 | HIGH |
| 7 | `System.Collections.Generic.HashSet` | .NET interop | Supported (interpreter) | Missing | Dfa.fun:69, GrammarParser.fun:630+, Lalr.fun:89+, Lr0.fun:44+ | P0 | HIGH |
| 8 | `System.Collections.Generic.Queue` | .NET interop | Supported (interpreter) | Missing | Dfa.fun:70, Lalr.fun:91, Lr0.fun:199+ | P0 | HIGH |
| 9 | `System.Collections.Generic.List` (mutable) | .NET interop | Supported (interpreter) | Missing | Lalr.fun:83+, Lr0.fun:71+, ParserTables.fun:115+ | P0 | HIGH |
| 10 | String slicing `s.[start .. end]` | Language construct | Supported (v5.0 indexing) | Missing | FunlexMain.fun:39+65+70+77, LexParser.fun:916, GrammarParser.fun:828 | P0 | MEDIUM |
| 11 | `.Length` property on strings | .NET interop | Supported (interpreter) | Missing | LexParser.fun:137, LexEmit.fun:158, DfaMin.fun:163+, all parsers | P0 | MEDIUM |
| 12 | `.Length` property on arrays | .NET interop | Supported (interpreter) | Missing | Lalr.fun:104+, YaccEmit.fun:275+, ParserTables.fun:141+ | P0 | MEDIUM |
| 13 | `for x in collection do` (for-in on .NET collections) | Language construct | Supported (v5.0) | Partial (v8.0: list/array only) | Lr0.fun:87+, Lalr.fun:94+, all modules with `for s in states` | P0 | MEDIUM |
| 14 | `.Append()` method on StringBuilder | .NET interop | Supported (interpreter) | Missing | LexParser.fun:156+, DfaMin.fun:349+, GrammarParser.fun:144+ | P0 | HIGH |
| 15 | `.ToString()` method | .NET interop | Supported (interpreter) | Missing | LexParser.fun:167, DfaMin.fun:185+377, GrammarParser.fun:155+ | P0 | LOW |
| 16 | `.TryGetValue()` method on Dictionary | .NET interop | Supported (interpreter) | Missing | Symtab.fun:31+48, DfaMin.fun:106+, Dfa.fun:190+ | P0 | HIGH |
| 17 | `.Add()` method on HashSet/List | .NET interop | Supported (interpreter) | Missing | Dfa.fun:71, Lr0.fun:87+, Lalr.fun:200+ | P0 | MEDIUM |
| 18 | `.Enqueue()`/`.Dequeue()` on Queue | .NET interop | Supported (interpreter) | Missing | Dfa.fun:71+73, Lalr.fun:91, Lr0.fun:199+ | P0 | MEDIUM |
| 19 | `.Count` property on collections | .NET interop | Supported (interpreter) | Missing | Dfa.fun:73+218, DfaMin.fun:199, Lr0.fun:257+ | P0 | LOW |
| 20 | `.Keys` property on Dictionary | .NET interop | Supported (interpreter) | Missing | DfaMin.fun:250 | P1 | LOW |
| 21 | `List.sort` | Prelude/stdlib | Supported (Prelude) | Missing | DfaMin.fun:250 | P1 | LOW |
| 22 | `List.sortBy` | Prelude/stdlib | Supported (Prelude) | Missing | DfaMin.fun:282 | P1 | LOW |
| 23 | `List.distinctBy` | Prelude/stdlib | Supported (Prelude) | Missing | DfaMin.fun:273 | P1 | LOW |
| 24 | `List.mapi` | Prelude/stdlib | Supported (Prelude) | Missing | LexEmit.fun:226+284, Nfa.fun:279 | P1 | LOW |
| 25 | `List.item` | Prelude/stdlib | Supported (Prelude/nth) | Missing | DfaMin.fun:366+369, LexEmit.fun:208, YaccEmit.fun:359+ | P1 | LOW |
| 26 | `List.exists` | Prelude/stdlib | Supported (Prelude/any) | Missing | YaccEmit.fun:73+140 | P1 | LOW |
| 27 | `List.tryFind` | Prelude/stdlib | Supported (Prelude) | Missing | YaccEmit.fun:330+335+516 | P1 | LOW |
| 28 | `List.choose` | Prelude/stdlib | Supported (Prelude) | Missing | YaccEmit.fun:111 | P1 | LOW |
| 29 | `List.ofSeq` | Prelude/stdlib | Supported (interpreter) | Missing | DfaMin.fun:160+200, Lr0.fun:148+ | P1 | MEDIUM |
| 30 | `List.isEmpty` / `List.head` / `List.tail` | Prelude/stdlib | Supported (Prelude) | Missing | LexEmit.fun:203, DfaMin.fun:132, Dfa.fun:226 | P1 | LOW |
| 31 | `Array.sort` (in-place) | Builtin | Supported (interpreter) | Missing | Dfa.fun:79 | P1 | MEDIUM |
| 32 | `Array.ofSeq` | Builtin | Supported (interpreter) | Missing | Dfa.fun:78 | P1 | MEDIUM |
| 33 | `Array.ofList` | Builtin | Supported (Eval.fs:476) | Present in Elaboration.fs | Lr0.fun:148+149 | Done | - |
| 34 | `Array.toList` | Builtin | Supported (Eval.fs:482) | Present in Elaboration.fs | DfaMin.fun:185, FunlexMain.fun:117 | Done | - |
| 35 | `Array.map` | Builtin | Supported (Eval.fs:499) | Present in Elaboration.fs | YaccEmit.fun:189+, Lr0.fun:178 | Done | - |
| 36 | `Array.init` | Builtin | Supported (Eval.fs:516) | Present in Elaboration.fs | ParserTables.fun:257+260 | Done | - |
| 37 | `Array.create` | Builtin | Supported (Eval.fs:476) | Present in Elaboration.fs | DfaMin.fun:100+120+132+175 | Done | - |
| 38 | `System.Char.IsDigit()` | .NET interop | Supported (interpreter) | Missing | YaccEmit.fun:307+310 | P1 | LOW |
| 39 | `System.Char.ToUpper()` | .NET interop | Supported (interpreter) | Missing | FunlexMain.fun:76, YaccEmit.fun:512 | P1 | LOW |
| 40 | `System.IO.File.ReadAllText()` | .NET interop | Supported (interpreter) | Missing (has `read_file` builtin) | FunlexMain.fun:126, FunyaccMain.fun | P1 | LOW |
| 41 | `System.IO.File.WriteAllText()` | .NET interop | Supported (interpreter) | Missing (has `write_file` builtin) | FunlexMain.fun:139, FunyaccMain.fun | P1 | LOW |
| 42 | `System.IO.File.Exists()` | .NET interop | Supported (interpreter) | Missing (has `file_exists` builtin) | FunlexMain.fun:128 | P1 | LOW |
| 43 | `.EndsWith()` string method | .NET interop | Supported (interpreter) | Missing | FunlexMain.fun:38+69, FunyaccMain.fun | P1 | LOW |
| 44 | `.Trim()` string method | .NET interop | Supported (interpreter) | Missing | GrammarParser.fun:386, YaccEmit.fun:367 | P2 | LOW |
| 45 | List comprehension `[ for x in coll -> expr ]` | Language construct | Supported (v5.0) | Missing | Symtab.fun:58, DfaMin.fun:200+250+303+309+318+327 | P0 | HIGH |
| 46 | `Unchecked.defaultof` | .NET interop | Supported (interpreter) | Missing | Lalr.fun:125, ParserTables.fun:341 | P1 | MEDIUM |
| 47 | Pipe with `.` method `\|> ignore` | Prelude/stdlib | Supported (Prelude) | Missing | Many files with `.Add() \|> ignore`, `.Append() \|> ignore` | P1 | LOW |
| 48 | `String.concat` (module-qualified) | Prelude/stdlib | Supported (Prelude) | Missing | DfaMin.fun:59+185, LexEmit.fun:67+, FunlexMain.fun:44 | P1 | LOW |
| 49 | `eprintfn` | Builtin | Supported (interpreter) | Missing | Diagnostics.fun:56 | P1 | MEDIUM |
| 50 | `Array.sort` returns sorted copy | Builtin | Supported (interpreter) | Missing | Dfa.fun:79 | P1 | MEDIUM |
| 51 | Record field access via `.field` on .NET types (`.Key`, `.Value`) | .NET interop | Supported (interpreter) | Missing | Symtab.fun:58, DfaMin.fun:160+200, many `kvp.Key`/`kvp.Value` | P0 | HIGH |

---

## Detailed Feature Descriptions

---

### Category 1: Language Constructs

---

#### Feature 1: `sprintf` Format Strings

**Description:** `sprintf` takes a format string with `%d`, `%s`, `%02x`, etc. and returns a formatted string. This is distinct from `printf` (which prints to stdout).

**LangThree interpreter status:** Fully supported. Defined in `Eval.fs` line 266 as a `BuiltinValue`. Handles `%d`, `%s`, `%b`, `%%`, and width-specifier variants like `%02x`, `%8s`, `%3d`.

**LangBackend compiler status:** Missing. Explicitly listed in `PROJECT.md` line 96 as "Out of Scope" with note "complexity high." The compiler uses libc `printf` for print/println but has no format-string parser for the LangThree `%d`/`%s` format mini-language.

**FunLexYacc usage:**
- `src/funlex/DfaMin.fun:373` — `sprintf "%02x" c` for hex formatting in table printing
- `src/funyacc/ParserTables.fun:496` — `sprintf "%8s" tables.termNames.[ti]` for table alignment
- `src/funyacc/ParserTables.fun:503` — `sprintf "  s%3d:" si` for state labels
- `src/funyacc/ParserTables.fun:512` — `sprintf "%8d" encoded` for action table values
- `src/funyacc/ParserTables.fun:530` — `sprintf "%8s"` for goto table header
- `src/funyacc/ParserTables.fun:537` — `sprintf "  s%3d:"` for goto table rows
- `src/funyacc/ParserTables.fun:540` — `sprintf "%8d"` for goto table values

**Implementation hint:** Implement a format-string parser in Elaboration.fs that at compile time inspects the format string literal and emits a sequence of `snprintf` / string-concat calls. For each `%d` → emit `snprintf(buf, n, "%d", arg)` via libc; for `%s` → emit string copy; for `%02x` → emit `snprintf(buf, n, "%02x", arg)`. The result is a heap-allocated LangThree string. Alternative: implement a C runtime function `lang_sprintf(fmt, arg)` that handles each format specifier.

**Test suggestion:** Write `.flt` tests: `sprintf "%d" 42` should produce `"42"`, `sprintf "%02x" 255` should produce `"ff"`, `sprintf "%8s" "hi"` should produce `"      hi"`.

**Priority:** P0 (blocker) — used in both funlex and funyacc table printing
**Effort:** HIGH — requires format string parsing + variable-arity argument handling

---

#### Feature 2: `printfn` Format Strings

**Description:** `printfn` prints a formatted string to stdout with a trailing newline. Same format mini-language as `sprintf`.

**LangThree interpreter status:** Fully supported. Defined in `Eval.fs` line 258.

**LangBackend compiler status:** Missing. The compiler supports `print`/`println` with plain strings (Elaboration.fs lines 1234-1300) but not format-string variants.

**FunLexYacc usage:**
- `src/funyacc/ParserTables.fun:490` — `printfn "ACTION table (%d states x %d terminals):" tables.nStates tables.nTerminals`
- `src/funyacc/ParserTables.fun:498` — `printfn "%s" (header.ToString())`
- `src/funyacc/ParserTables.fun:514` — `printfn "%s" (row.ToString())`
- `src/funyacc/ParserTables.fun:524` — `printfn "GOTO table (%d states x %d nonterminals):" ...`
- `src/funyacc/ParserTables.fun:532,542` — more formatted table output

**Implementation hint:** Once `sprintf` is implemented, `printfn fmt args...` can desugar to `println (sprintf fmt args...)`. Alternatively, emit direct libc `printf` calls with the format string re-written from LangThree format to C format (they are largely compatible for `%d`, `%s`).

**Test suggestion:** `.flt` test: `printfn "%d + %d = %d" 1 2 3` should output `"1 + 2 = 3\n"`.

**Priority:** P0 (blocker)
**Effort:** HIGH (shared with sprintf implementation)

---

#### Feature 3: File Import (`open "file.fun"`)

**Description:** `open "path/to/file.fun"` imports all declarations from another `.fun` file into the current scope. The LangThree interpreter resolves paths relative to the importing file, detects circular imports, and recursively evaluates the imported file.

**LangThree interpreter status:** Fully supported since v2.0. Implemented in `Eval.fs` line 1342 (`FileImportDecl` arm). Uses a file evaluator callback registered at line 587-597.

**LangBackend compiler status:** Missing. Explicitly listed in `PROJECT.md` line 97 as "Out of Scope" with note "requires recursive parser calls." The `FileImportDecl` AST node is not handled in `Elaboration.fs`.

**FunLexYacc usage:** Every single `.fun` file in FunLexYacc uses `open ModuleName` to reference other modules:
- `src/funlex/LexParser.fun:21-22` — `open ErrorInfo`, `open LexSyntax`
- `src/funlex/LexEmit.fun:31-32` — `open LexSyntax`, `open DfaMin`
- `src/funlex/Dfa.fun` — implicit dependency on `Nfa`, `Cset`
- `src/funyacc/Lalr.fun:19-22` — `open GrammarSyntax`, `open Symtab`, `open Lr0`, `open FirstFollow`
- `src/funyacc/ParserTables.fun:35-39` — opens 5 modules
- All 19 `.fun` files have cross-file dependencies

Note: FunLexYacc uses `open ModuleName` (module open, not file import), but these modules are defined in separate files. The compiler needs either (a) multi-file compilation where all `.fun` files are parsed together, or (b) `open "file.fun"` file import support, or (c) a build system that concatenates files in dependency order.

**Implementation hint:** The simplest approach: add a `--multi-file` mode to LangBackend CLI that accepts multiple `.fun` files, parses all of them, merges their AST declarations (respecting module namespaces), and elaborates the combined AST. Alternatively, implement `FileImportDecl` handling in Elaboration.fs that recursively calls the parser on the imported file path. The LangThree frontend already handles `FileImportDecl` in the parser; LangBackend just needs to handle it during elaboration.

**Test suggestion:** Create a two-file test: `lib.fun` defines `module Lib` with `let add x y = x + y`, `main.fun` does `open Lib` then `println (to_string (add 1 2))`. Compile `main.fun` and verify output is `"3"`.

**Priority:** P0 (blocker) — without this, no FunLexYacc file can compile
**Effort:** HIGH — requires changes to CLI, parser invocation, and elaboration

---

#### Feature 4: `get_args` (CLI Argument Access)

**Description:** `get_args ()` returns `string list` of command-line arguments passed to the compiled binary.

**LangThree interpreter status:** Fully supported. Defined in `Eval.fs` line 378.

**LangBackend compiler status:** Missing. Explicitly listed in `PROJECT.md` line 98 as "Out of Scope" with note "requires @main signature change (argc/argv)."

**FunLexYacc usage:**
- `src/funlex/FunlexMain.fun:117` — `match parseArgs (Array.toList argv) with` (where `argv` comes from `main` parameter)
- `src/funyacc/FunyaccMain.fun:111` — same pattern

Note: FunLexYacc's `main` function takes `argv : string array` as a parameter, which comes from `get_args()` or direct CLI integration.

**Implementation hint:** Change the `@main` function signature from `() -> i64` to `(i32, ptr) -> i64` (argc, argv). In Elaboration.fs, when encountering `get_args`, emit code that iterates `argv[0..argc-1]`, wraps each C string into a LangThree string struct, and builds a LangThree list. Add a C runtime helper `lang_get_args(argc, argv)` that returns a boxed list.

**Test suggestion:** `.flt` test: compile a program that calls `get_args()` and prints the length. Run with `./a.out foo bar` and verify it prints `3` (program name + 2 args) or `2` (just foo + bar, depending on convention).

**Priority:** P0 (blocker) — both funlex and funyacc entry points use it
**Effort:** MEDIUM — main signature change + list construction

---

#### Feature 5: String Slicing `s.[start .. end]`

**Description:** String slice syntax `s.[start .. end]` extracts a substring from index `start` to index `end` (inclusive). Also supports open-ended: `s.[start ..]` (to end).

**LangThree interpreter status:** Fully supported since v5.0 (indexing syntax).

**LangBackend compiler status:** Missing. LangBackend v7.0 added `arr.[i]` indexing for arrays and hashtables, but not string slicing with `..` ranges.

**FunLexYacc usage:**
- `src/funlex/FunlexMain.fun:39` — `input.[0 .. input.Length - 6]`
- `src/funlex/FunlexMain.fun:65` — `outputPath.[lastSlash + 1 ..]`
- `src/funlex/FunlexMain.fun:70` — `baseName.[0 .. baseName.Length - 5]`
- `src/funlex/FunlexMain.fun:77` — `stripped.[1 ..]`
- `src/funlex/LexParser.fun:916` — `ps.src.[start .. ps.pos - 1]`
- `src/funyacc/GrammarParser.fun:828` — `ps.src.[start .. ps.pos - 1]`
- `src/funyacc/YaccEmit.fun:512` — `startSym.[1 .. startSym.Length - 1]`
- `src/funyacc/FunyaccMain.fun:38+64+69+76` — multiple slice operations

**Implementation hint:** Implement as a call to `string_sub` (which already exists in Elaboration.fs). `s.[a .. b]` desugars to `string_sub s a (b - a + 1)`. `s.[a ..]` desugars to `string_sub s a (string_length s - a)`. Add string indexing `s.[i]` as `char_at(s, i)` returning a char.

**Test suggestion:** `.flt` test: `let s = "hello" in s.[1 .. 3]` should produce `"ell"`. `s.[2 ..]` should produce `"llo"`.

**Priority:** P0 (blocker) — used extensively in both main entry points and all parsers
**Effort:** MEDIUM — desugar to existing `string_sub` builtin

---

#### Feature 6: List Comprehension `[ for x in coll -> expr ]`

**Description:** F#-style list comprehension that iterates over a collection and produces a list from each element.

**LangThree interpreter status:** Fully supported since v5.0.

**LangBackend compiler status:** Missing. LangBackend handles `for i = start to end do body` loops (v7.0) and `for x in list/array do body` (v8.0), but not the `[ for ... -> ... ]` list-building comprehension syntax.

**FunLexYacc usage:**
- `src/common/Symtab.fun:58` — `[ for kv in st.nameToId -> (kv.Key, kv.Value) ]`
- `src/funlex/DfaMin.fun:200` — `[ for kvp2 in sigMap -> List.ofSeq kvp2.Value ]`
- `src/funlex/DfaMin.fun:250` — `List.sort [ for g in groupRep.Keys -> g ]`
- `src/funlex/DfaMin.fun:303` — `[ for i in 0 .. numStates - 1 -> ... ]`
- `src/funlex/DfaMin.fun:309` — `[ for c in 0 .. 255 -> ... ]`
- `src/funlex/DfaMin.fun:318` — `[ for i in 0 .. numStates - 1 -> ... ]`
- `src/funlex/DfaMin.fun:327` — `[ for i in 0 .. numStates - 1 -> ... ]`

**Implementation hint:** Desugar `[ for x in coll -> expr ]` into `List.map (fun x -> expr) coll` (or equivalent loop that builds a reversed list then reverses). For `[ for i in 0 .. n -> expr ]`, desugar to a loop over the range. The iteration over .NET collections (Dictionary.Keys, etc.) needs the for-in-collection support from Feature 13.

**Test suggestion:** `.flt` test: `[ for i in 0 .. 4 -> i * i ]` should produce `[0; 1; 4; 9; 16]`.

**Priority:** P0 (blocker) — heavily used in DfaMin.fun table extraction
**Effort:** HIGH — requires new AST handling + list construction in a loop

---

### Category 2: .NET Interop (Collections and Methods)

This is the largest gap. FunLexYacc uses .NET BCL types directly (not through LangThree's builtin hashtable/array). LangBackend has no .NET interop — it compiles to native code via LLVM, so .NET types are unavailable.

**Strategy decision required:** The LangBackend team must choose one of:
1. **Rewrite FunLexYacc** to use only LangThree builtins (Hashtable, Array) instead of .NET types
2. **Implement .NET interop stubs** — for each .NET type used, provide a C runtime equivalent
3. **Add a LangThree-native standard library** with StringBuilder, Dictionary, HashSet, Queue implemented in C runtime

Option 2/3 is recommended since FunLexYacc uses these types pervasively (384 call sites across 14 files).

---

#### Feature 7: `System.Text.StringBuilder`

**Description:** Mutable string builder with `.Append()` and `.ToString()` methods. Used for efficient string construction.

**LangThree interpreter status:** Supported via .NET interop (the interpreter runs on .NET).

**LangBackend compiler status:** Missing entirely. No StringBuilder concept in the native compiler.

**FunLexYacc usage (51 call sites across 6 files):**
- `src/funlex/LexParser.fun:156-167` — `StringBuilder()` + `.Append(char)` + `.ToString()` for identifier reading
- `src/funlex/DfaMin.fun:349-377` — `StringBuilder()` for table printing
- `src/funyacc/GrammarParser.fun:144,169,243,365` — string building in parser
- `src/funyacc/YaccEmit.fun:304,388` — code generation output
- `src/funyacc/ParserTables.fun:492,502,526,536` — table printing

**Implementation hint:** Add a C runtime `LangStringBuilder` type:
```c
typedef struct { char* buf; int64_t len; int64_t cap; } LangStringBuilder;
LangStringBuilder* lang_sb_create();
void lang_sb_append_char(LangStringBuilder* sb, int64_t ch);
void lang_sb_append_str(LangStringBuilder* sb, LangString* s);
LangString* lang_sb_to_string(LangStringBuilder* sb);
```
In Elaboration.fs, recognize `System.Text.StringBuilder()` constructor and `.Append()`/`.ToString()` method calls, emitting the corresponding C runtime calls.

**Test suggestion:** `.flt` test: create a StringBuilder, append "hello" and " world", call ToString(), verify result is "hello world".

**Priority:** P0 (blocker)
**Effort:** HIGH — new C runtime type + method dispatch in Elaboration.fs

---

#### Feature 8: `System.Collections.Generic.Dictionary<K,V>`

**Description:** Hash map with `.TryGetValue()`, `.[key]` indexing, `.[key] <- value` mutation, `.Keys`, `.Count` properties.

**LangThree interpreter status:** Supported via .NET interop.

**LangBackend compiler status:** LangBackend has `hashtable_*` builtins, but these are a different type with different API. The .NET `Dictionary<K,V>` has methods like `.TryGetValue()` that return `(bool, V)` tuple-via-out-parameter — not present in LangThree hashtable builtins.

**FunLexYacc usage (extensive — 60+ call sites):**
- `src/common/Symtab.fun:16-18` — `Dictionary<string, int>` and `Dictionary<int, string>`
- `src/funlex/Dfa.fun:157` — `Dictionary<string, int>` for state deduplication
- `src/funlex/DfaMin.fun:93,142,188,231,243,297,306` — multiple Dictionary uses
- `src/funyacc/Lalr.fun:54,187,193` — lookahead sets
- `src/funyacc/Lr0.fun:72,253,255` — goto table, state map
- `src/funyacc/ParserTables.fun:51,84,85,220-225` — term/NT maps
- `src/funyacc/FirstFollow.fun:23-25` — FIRST/FOLLOW/NULLABLE dictionaries

Key methods used:
- Constructor: `Dictionary<K,V>()` — Symtab.fun:16, Dfa.fun:157
- `.TryGetValue(key)` returning `(bool, value)` — Symtab.fun:31, DfaMin.fun:106
- `.[key]` get — Lalr.fun:101+, ParserTables.fun:103+
- `.[key] <- value` set — Symtab.fun:33-34, DfaMin.fun:181+
- `.Keys` — DfaMin.fun:250

**Implementation hint:** Map `System.Collections.Generic.Dictionary` to the existing LangThree hashtable runtime. The `.[key]` and `.[key] <- value` syntax already works for hashtables in LangBackend v7.0. The missing piece is `.TryGetValue()` which returns a `(bool * V)` tuple. Add a C runtime function `lang_hashtable_try_get(ht, key, out_value) -> bool` and emit a two-value return.

**Test suggestion:** `.flt` test: create a Dictionary, set key "a" to 1, call TryGetValue("a") and verify `(true, 1)`, call TryGetValue("b") and verify `(false, _)`.

**Priority:** P0 (blocker)
**Effort:** HIGH — many methods + .NET API shape

---

#### Feature 9: `System.Collections.Generic.HashSet<T>`

**Description:** Mutable set with `.Add()`, `.Contains()`, `.Count` properties.

**LangThree interpreter status:** Supported via .NET interop.

**LangBackend compiler status:** Missing entirely. No set type in the native compiler.

**FunLexYacc usage (30+ call sites):**
- `src/funlex/Dfa.fun:69` — `HashSet<int>()` for epsilon closure visited set
- `src/funyacc/GrammarParser.fun:630-631` — `HashSet<string>()` for token set
- `src/funyacc/Lalr.fun:89,111,125` — `HashSet<int>()` for FIRST sets, lookaheads
- `src/funyacc/Lr0.fun:44,86,107,197,272` — nonterminal ID sets, closure visited
- `src/funyacc/FirstFollow.fun:24-25,33-34,45` — FIRST/FOLLOW sets
- `src/funyacc/ParserTables.fun:316,341` — conflict detection
- `src/funyacc/Ielr.fun:545` — split lookahead sets

Key methods used:
- Constructor: `HashSet<T>()`
- `.Add(value)` returning `bool` (whether newly added) — Dfa.fun:71, Lalr.fun:200
- `.Contains(value)` — not directly seen but `.Add()` return value serves same purpose
- Iteration: `for x in hashSet do` — Lalr.fun:127,155,266,315,338
- `.Count` — DfaMin.fun:199

**Implementation hint:** Implement as a C runtime type using the same hash table infrastructure (murmurhash3) but storing only keys (value = unit). Add runtime functions:
```c
LangHashSet* lang_hashset_create();
int64_t lang_hashset_add(LangHashSet* hs, int64_t key); // returns 1 if new
int64_t lang_hashset_contains(LangHashSet* hs, int64_t key);
int64_t lang_hashset_count(LangHashSet* hs);
```

**Test suggestion:** `.flt` test: create a HashSet, add 1, add 2, add 1 again (should return false), check count = 2.

**Priority:** P0 (blocker)
**Effort:** HIGH — new C runtime type

---

#### Feature 10: `System.Collections.Generic.Queue<T>`

**Description:** FIFO queue with `.Enqueue()`, `.Dequeue()`, `.Count`.

**LangThree interpreter status:** Supported via .NET interop.

**LangBackend compiler status:** Missing entirely.

**FunLexYacc usage:**
- `src/funlex/Dfa.fun:70-73+218` — BFS worklist for epsilon closure and subset construction
- `src/funyacc/Lalr.fun:91` — closure worklist
- `src/funyacc/Lr0.fun:199+256` — LR(0) item closure and state construction

**Implementation hint:** Implement as a C runtime circular buffer or linked list:
```c
LangQueue* lang_queue_create();
void lang_queue_enqueue(LangQueue* q, int64_t value);
int64_t lang_queue_dequeue(LangQueue* q);
int64_t lang_queue_count(LangQueue* q);
```

**Test suggestion:** `.flt` test: create a Queue, enqueue 1, 2, 3, dequeue should return 1, then 2.

**Priority:** P0 (blocker)
**Effort:** HIGH — new C runtime type

---

#### Feature 11: `System.Collections.Generic.List<T>` (Mutable List)

**Description:** .NET mutable list (resizable array) with `.Add()`, indexing `.[i]`, `.Count`.

**LangThree interpreter status:** Supported via .NET interop.

**LangBackend compiler status:** Missing. LangBackend has immutable functional lists and mutable arrays, but not the .NET `List<T>` resizable list.

**FunLexYacc usage:**
- `src/funyacc/Lalr.fun:83,90,188,212` — mutable lists for closure computation
- `src/funyacc/Lr0.fun:71,128,198,238,254,256` — state lists, flat production lists
- `src/funyacc/ParserTables.fun:115,226,227,313` — overrides and name lists
- `src/funyacc/Ielr.fun` — multiple uses
- `src/funyacc/YaccEmit.fun:196,238` — code generation assignments

**Implementation hint:** Can reuse the existing LangThree array with dynamic resizing, or implement a C runtime growable array:
```c
LangMutableList* lang_mlist_create();
void lang_mlist_add(LangMutableList* ml, int64_t value);
int64_t lang_mlist_get(LangMutableList* ml, int64_t index);
int64_t lang_mlist_count(LangMutableList* ml);
```

**Test suggestion:** `.flt` test: create a List, add 10, add 20, verify count = 2 and .[0] = 10.

**Priority:** P0 (blocker)
**Effort:** HIGH — new C runtime type

---

#### Feature 12: `.Length` Property on Strings

**Description:** `s.Length` returns the length of string `s` as an int.

**LangThree interpreter status:** Supported (strings are .NET strings with `.Length` property).

**LangBackend compiler status:** Missing. LangBackend has `string_length s` as a function call, but not the `.Length` property syntax on strings.

**FunLexYacc usage (20+ sites):**
- `src/funlex/LexParser.fun:137` — `while i < s.Length`
- `src/funlex/FunlexMain.fun:59+74` — `outputPath.Length`, `stripped.Length`
- `src/funyacc/GrammarParser.fun:51+56+60` — `ps.src.Length`
- `src/funyacc/YaccEmit.fun:306+307+310,511-554` — `action.Length`, `startSym.Length`
- `src/funyacc/Lalr.fun:104+114+138+139+152+226` — `prod.rhs.Length` (array Length)

**Implementation hint:** In Elaboration.fs, when encountering `FieldAccess(expr, "Length")` where `expr` has type `string`, emit `string_length(expr)`. For arrays, emit `array_length(expr)`. This is a compile-time desugar based on the type-checked AST.

**Test suggestion:** `.flt` test: `let s = "hello" in s.Length` should return 5.

**Priority:** P0 (blocker)
**Effort:** MEDIUM — type-directed field access desugar

---

#### Feature 13: `.TryGetValue()` Method on Dictionary

**Description:** `dict.TryGetValue(key)` returns `(bool, value)` tuple — `(true, v)` if key exists, `(false, default)` otherwise. FunLexYacc uses pattern matching on the result: `match dict.TryGetValue(key) with | true, v -> ... | false, _ -> ...`.

**LangThree interpreter status:** Supported via .NET interop.

**LangBackend compiler status:** Missing.

**FunLexYacc usage:**
- `src/common/Symtab.fun:31+48` — `st.nameToId.TryGetValue(name, &id)`
- `src/funlex/Dfa.fun:190` — `stateMap.TryGetValue(key)`
- `src/funlex/DfaMin.fun:106+146+192+235+311` — group/state lookups
- `src/funyacc/Lalr.fun:125` — FIRST set lookup
- `src/funyacc/ParserTables.fun:103` — precedence table lookup

Note: Some uses follow the F# out-parameter pattern `TryGetValue(key, &outVar)`, others use the tuple-return pattern `match dict.TryGetValue(key) with | true, v -> ...`.

**Implementation hint:** Add `lang_hashtable_try_get_value(ht, key) -> (i64, i64)` to the C runtime that returns a 2-tuple `(found_bool, value)`. In Elaboration.fs, recognize `.TryGetValue()` method calls on Dictionary types and emit the runtime call.

**Test suggestion:** `.flt` test: dictionary with key "a"=1, `TryGetValue("a")` returns `(true, 1)`, `TryGetValue("b")` returns `(false, 0)`.

**Priority:** P0 (blocker)
**Effort:** HIGH — new calling convention for tuple-returning methods

---

#### Feature 14: `for x in collection do` on .NET Collections

**Description:** Iterate over .NET collection types (Dictionary, HashSet, Queue, List) using `for x in coll do body`.

**LangThree interpreter status:** Supported (interprets .NET IEnumerable).

**LangBackend compiler status:** Partial. v8.0 supports `for x in list do` and `for x in array do` via C runtime closure callbacks, but not for .NET collection types (Dictionary, HashSet).

**FunLexYacc usage (40+ sites):**
- `src/funyacc/Lr0.fun:87` — `for rule in spec.rules do` (iterating a list — already supported)
- `src/funyacc/Lalr.fun:94` — `for (item, la) in items do` (iterating .NET List<T> — NOT supported)
- `src/funyacc/Lalr.fun:127` — `for tokName in ntFirst do` (iterating HashSet — NOT supported)
- `src/funyacc/Lalr.fun:208` — `for kernelItem in state.kernel do` (iterating functional list)
- `src/funyacc/ParserTables.fun:276+384` — `for kvp in ielr.gotoTable do` (iterating Dictionary — NOT supported)
- `src/funyacc/Ielr.fun:94` — `for kvp in ... do` (iterating Dictionary)
- `src/funlex/DfaMin.fun:102+121+134` — `for s in states do` (iterating functional list)

**Implementation hint:** For each .NET collection type that gets a C runtime equivalent, add an iteration helper:
```c
void lang_hashset_iter(LangHashSet* hs, LangClosureFn callback);
void lang_dict_iter(LangHashtable* ht, LangClosureFn callback);  // yields (key, value) pairs
void lang_mlist_iter(LangMutableList* ml, LangClosureFn callback);
```

**Test suggestion:** `.flt` test: create a HashSet with {1,2,3}, `for x in hs do print (to_string x)`, verify all elements printed.

**Priority:** P0 (blocker)
**Effort:** MEDIUM — extension of existing for-in pattern to new types

---

#### Feature 15: `.Append()`, `.Add()`, `.Enqueue()`, `.Dequeue()` Method Calls

**Description:** Method-call syntax on .NET collection types.

**LangThree interpreter status:** Supported via .NET interop method dispatch.

**LangBackend compiler status:** Missing. LangBackend has no concept of method calls — all operations are function calls or field access.

**FunLexYacc usage:** 100+ method call sites across all files (see features 7-11 for details).

**Implementation hint:** In Elaboration.fs, recognize method calls on known types. When the type checker resolves `expr.Method(args)`, emit the corresponding C runtime function call. This requires either:
1. Type-directed dispatch in Elaboration.fs (check the type of `expr` to determine which runtime function to call), or
2. A vtable-like mechanism where each collection type carries a function table.

Option 1 is simpler and sufficient for the finite set of collection types used.

**Test suggestion:** Covered by individual collection type tests.

**Priority:** P0 (blocker)
**Effort:** HIGH — foundational method dispatch mechanism

---

#### Feature 16: `.Key` and `.Value` on Dictionary KeyValuePair

**Description:** When iterating a Dictionary with `for kvp in dict do`, `kvp.Key` and `kvp.Value` access the key and value.

**LangThree interpreter status:** Supported via .NET interop.

**LangBackend compiler status:** Missing.

**FunLexYacc usage:**
- `src/common/Symtab.fun:58` — `kv.Key`, `kv.Value`
- `src/funlex/DfaMin.fun:159-160` — `kvp.Key`, `kvp.Value`
- `src/funyacc/ParserTables.fun:280+388` — `kvp.Value`
- `src/funyacc/Ielr.fun:94+457+545` — `kvp.Key`, `kvp.Value`

**Implementation hint:** When iterating a Dictionary, yield 2-tuples `(key, value)` as the loop variable. Then `.Key` maps to `fst` and `.Value` maps to `snd`. Alternatively, yield a record-like struct with named fields.

**Test suggestion:** `.flt` test: `for kvp in dict do print kvp.Key`.

**Priority:** P0 (blocker)
**Effort:** HIGH — tied to Dictionary iteration implementation

---

#### Feature 17: `System.Char.IsDigit()` and `System.Char.ToUpper()`

**Description:** Static methods on .NET's `System.Char` class.

**LangThree interpreter status:** Supported via .NET interop.

**LangBackend compiler status:** Missing.

**FunLexYacc usage:**
- `src/funyacc/YaccEmit.fun:307+310` — `System.Char.IsDigit(action.[i])`
- `src/funyacc/YaccEmit.fun:512` — `System.Char.ToUpper(startSym.[0])`
- `src/funlex/FunlexMain.fun:76` — `System.Char.ToUpper(stripped.[0])`

**Implementation hint:** Add simple C runtime helpers or inline the logic:
- `IsDigit(c)` → `c >= '0' && c <= '9'`
- `ToUpper(c)` → `c >= 'a' && c <= 'z' ? c - 32 : c`

In Elaboration.fs, recognize `System.Char.IsDigit` and `System.Char.ToUpper` as known static method calls.

**Test suggestion:** `.flt` test: `System.Char.IsDigit('5')` returns true, `System.Char.ToUpper('a')` returns 'A'.

**Priority:** P1 (needed)
**Effort:** LOW — trivial inline logic

---

#### Feature 18: `System.IO.File` Methods

**Description:** `File.ReadAllText()`, `File.WriteAllText()`, `File.Exists()`.

**LangThree interpreter status:** Supported via .NET interop.

**LangBackend compiler status:** Has equivalent builtins: `read_file`, `write_file`, `file_exists`. But does NOT recognize the `System.IO.File.X()` syntax.

**FunLexYacc usage:**
- `src/funlex/FunlexMain.fun:126` — `System.IO.File.ReadAllText(inputPath)`
- `src/funlex/FunlexMain.fun:128` — `System.IO.File.Exists(inputPath)`
- `src/funlex/FunlexMain.fun:139` — `System.IO.File.WriteAllText(outputPath, generated)`

**Implementation hint:** In Elaboration.fs, map these to the existing builtins:
- `System.IO.File.ReadAllText(path)` → `read_file(path)`
- `System.IO.File.WriteAllText(path, content)` → `write_file(path, content)`
- `System.IO.File.Exists(path)` → `file_exists(path)`

**Test suggestion:** Already covered by existing read_file/write_file tests.

**Priority:** P1 (needed)
**Effort:** LOW — simple alias in Elaboration.fs

---

#### Feature 19: `.EndsWith()` and `.Trim()` String Methods

**Description:** `s.EndsWith(".funl")` checks suffix; `s.Trim()` removes whitespace.

**LangThree interpreter status:** Supported via .NET string interop.

**LangBackend compiler status:** Missing.

**FunLexYacc usage:**
- `src/funlex/FunlexMain.fun:38+69` — `.EndsWith(".funl")`, `.EndsWith(".fun")`
- `src/funyacc/FunyaccMain.fun` — same pattern
- `src/funyacc/GrammarParser.fun:386` — `.Trim()`
- `src/funyacc/YaccEmit.fun:367` — `.Trim()`

**Implementation hint:** Add C runtime helpers:
```c
int64_t lang_string_ends_with(LangString* s, LangString* suffix);
LangString* lang_string_trim(LangString* s);
```

**Test suggestion:** `.flt` test: `"hello.fun".EndsWith(".fun")` returns true, `"  hi  ".Trim()` returns `"hi"`.

**Priority:** P1 (needed)
**Effort:** LOW — simple C string operations

---

#### Feature 20: `Unchecked.defaultof<T>`

**Description:** Returns the default/zero value for a type (0 for int, null for reference types).

**LangThree interpreter status:** Supported via .NET interop.

**LangBackend compiler status:** Missing.

**FunLexYacc usage:**
- `src/funyacc/Lalr.fun:125` — `Unchecked.defaultof<System.Collections.Generic.HashSet<string>>`
- `src/funyacc/ParserTables.fun:341` — `Unchecked.defaultof<System.Collections.Generic.HashSet<int>>`

**Implementation hint:** In the boxed uniform representation, `Unchecked.defaultof<T>` can always be compiled as a null pointer (0). Add a simple case in Elaboration.fs that emits `inttoptr 0`.

**Test suggestion:** `.flt` test: `let x = Unchecked.defaultof<int> in x` should return 0.

**Priority:** P1 (needed)
**Effort:** MEDIUM — need to handle the generic type syntax

---

### Category 3: Prelude/Stdlib Functions

These are functions defined in LangThree's Prelude (`.fun` files) or available as builtins. They work in the interpreter but LangBackend needs to either compile the Prelude files or provide native implementations.

---

#### Feature 21: `List.sort`, `List.sortBy`, `List.distinctBy`

**Description:** Sorting and deduplication functions for lists.

**LangThree interpreter status:** Supported (these are likely Prelude functions or .NET interop).

**LangBackend compiler status:** Missing. The Prelude files are not compiled by LangBackend. These functions are not builtins in Elaboration.fs.

**FunLexYacc usage:**
- `src/funlex/DfaMin.fun:250` — `List.sort [ for g in ... ]`
- `src/funlex/DfaMin.fun:282` — `List.sortBy (fun s -> s.id)`
- `src/funlex/DfaMin.fun:273` — `List.distinctBy (fun (c, _) -> c)`

**Implementation hint:** Implement as C runtime functions that convert list to array, sort (using qsort), and convert back. Or implement merge sort in C working directly on the cons-cell list structure:
```c
LangList* lang_list_sort(LangList* lst, LangClosureFn compare);
LangList* lang_list_sort_by(LangList* lst, LangClosureFn key_fn);
LangList* lang_list_distinct_by(LangList* lst, LangClosureFn key_fn);
```

**Test suggestion:** `.flt` test: `List.sort [3; 1; 2]` should return `[1; 2; 3]`.

**Priority:** P1 (needed)
**Effort:** MEDIUM — sort algorithm in C runtime

---

#### Feature 22: `List.mapi`

**Description:** `List.mapi f xs` applies `f index element` to each element, returning a new list.

**LangThree interpreter status:** Supported (Prelude or interpreter).

**LangBackend compiler status:** Missing.

**FunLexYacc usage:**
- `src/funlex/LexEmit.fun:226` — `List.mapi emitCase`
- `src/funlex/LexEmit.fun:284` — `List.mapi (fun i r -> emitEntryPoint r i)`
- `src/funlex/Nfa.fun:279` — `List.mapi (fun idx ruleCase -> ...)`

**Implementation hint:** Can be compiled from Prelude source (if file import works) or added as a C runtime function. A Prelude definition would be:
```
let rec mapi_aux f i xs = match xs with | [] -> [] | h :: t -> f i h :: mapi_aux f (i + 1) t
let mapi f xs = mapi_aux f 0 xs
```

**Test suggestion:** `.flt` test: `List.mapi (fun i x -> i + x) [10; 20; 30]` should return `[10; 21; 32]`.

**Priority:** P1 (needed)
**Effort:** LOW — simple recursive function

---

#### Feature 23: `List.item`

**Description:** `List.item n xs` returns the n-th element (0-indexed).

**LangThree interpreter status:** Supported as `nth` in Prelude (`Prelude/List.fun:16`).

**LangBackend compiler status:** Missing. Not in Elaboration.fs.

**FunLexYacc usage:**
- `src/funlex/DfaMin.fun:366+369` — `List.item i trans`, `List.item c row`
- `src/funlex/LexEmit.fun:208` — `List.item 0 rule.args`
- `src/funyacc/YaccEmit.fun:347+349+359+362` — `List.item N valueStack`

**Implementation hint:** If Prelude compilation works, `List.item` is already defined as `nth`. Otherwise, add as a C runtime function that walks the cons-cell chain.

**Test suggestion:** `.flt` test: `List.item 2 [10; 20; 30; 40]` should return `30`.

**Priority:** P1 (needed)
**Effort:** LOW

---

#### Feature 24: `List.exists`, `List.tryFind`, `List.choose`

**Description:**
- `List.exists pred xs` — returns true if any element satisfies pred
- `List.tryFind pred xs` — returns `Some x` for first match, `None` if none
- `List.choose f xs` — applies f to each, keeps `Some` results

**LangThree interpreter status:** Supported. `exists` = `any` in Prelude (`Prelude/List.fun:13`).

**LangBackend compiler status:** Missing.

**FunLexYacc usage:**
- `src/funyacc/YaccEmit.fun:73+140` — `List.exists (fun t -> t.name = "EOF")`
- `src/funyacc/YaccEmit.fun:330+335+516` — `List.tryFind ...`
- `src/funyacc/YaccEmit.fun:111` — `List.choose ...`

**Implementation hint:** Can be Prelude functions or C runtime. Prelude definitions are straightforward:
```
let rec tryFind pred xs = match xs with | [] -> None | h :: t -> if pred h then Some h else tryFind pred t
let rec choose f xs = match xs with | [] -> [] | h :: t -> match f h with | Some v -> v :: choose f t | None -> choose f t
```

**Test suggestion:** `.flt` test: `List.tryFind (fun x -> x > 3) [1; 2; 5; 3]` should return `Some 5`.

**Priority:** P1 (needed)
**Effort:** LOW

---

#### Feature 25: `List.isEmpty`, `List.head`, `List.tail`

**Description:** Basic list operations.

**LangThree interpreter status:** Supported in Prelude (`hd`, `tl` at `Prelude/List.fun:8-9`).

**LangBackend compiler status:** Missing as qualified `List.isEmpty` etc., though the underlying operations (pattern match on `[]` vs `h :: t`) work.

**FunLexYacc usage:**
- `src/funlex/LexEmit.fun:203` — `List.isEmpty rule.args`
- `src/funlex/DfaMin.fun:132` — `List.head states`
- `src/funlex/Dfa.fun:226` — `List.isEmpty targets`
- `src/funyacc/YaccEmit.fun:370+440+476+477+495` — `List.isEmpty`, `List.head`

**Implementation hint:** Map `List.isEmpty xs` to `xs = []` pattern match. `List.head` to `hd`. `List.tail` to `tl`. Alternatively, compile Prelude.

**Test suggestion:** `.flt` test: `List.isEmpty []` returns true, `List.head [1; 2]` returns 1.

**Priority:** P1 (needed)
**Effort:** LOW

---

#### Feature 26: `List.ofSeq` and `Array.ofSeq`

**Description:** Convert an `IEnumerable`/`seq` to a list or array. Used to convert .NET collection types to functional lists.

**LangThree interpreter status:** Supported via .NET interop.

**LangBackend compiler status:** Missing.

**FunLexYacc usage:**
- `src/funlex/DfaMin.fun:160` — `List.ofSeq kvp.Value` (converting .NET List<int> to functional list)
- `src/funlex/DfaMin.fun:200` — `List.ofSeq kvp2.Value`
- `src/funlex/Dfa.fun:78` — `Array.ofSeq visited` (converting HashSet to array)

**Implementation hint:** For each collection type, provide a `toList` conversion. `List.ofSeq(MutableList)` walks the list and builds cons cells. `Array.ofSeq(HashSet)` allocates an array and fills it.

**Test suggestion:** `.flt` test: create a HashSet with {1,2,3}, `Array.ofSeq hs` should produce an array of length 3.

**Priority:** P1 (needed)
**Effort:** MEDIUM — per-type conversion functions

---

#### Feature 27: `Array.sort` (In-Place)

**Description:** `Array.sort arr` sorts an array in-place.

**LangThree interpreter status:** Supported (interpreter, .NET Array.Sort).

**LangBackend compiler status:** Missing.

**FunLexYacc usage:**
- `src/funlex/Dfa.fun:79` — `Array.sort arr` (sort NFA state ID array for canonical form)

**Implementation hint:** Call libc `qsort` on the array data. Since LangBackend arrays are `{length, slot0, slot1, ...}` in a flat GC block, `qsort(&array[1], length, 8, compare_i64)`. Add a C runtime wrapper:
```c
void lang_array_sort(int64_t* arr); // sorts in-place using the length in slot 0
```

**Test suggestion:** `.flt` test: `let a = Array.ofList [3; 1; 2] in Array.sort a; Array.toList a` should return `[1; 2; 3]`.

**Priority:** P1 (needed)
**Effort:** MEDIUM

---

#### Feature 28: `String.concat`

**Description:** `String.concat sep strings` joins a list of strings with a separator.

**LangThree interpreter status:** Supported (Prelude operator `^^` for binary concat, plus `String.concat` as module function).

**LangBackend compiler status:** `string_concat` (binary, two strings) is supported. Module-qualified `String.concat sep list` is missing.

**FunLexYacc usage (15+ sites):**
- `src/funlex/DfaMin.fun:59` — `String.concat "," (List.map string sortedIds)`
- `src/funlex/DfaMin.fun:185` — `String.concat "," (Array.toList parts)`
- `src/funlex/LexEmit.fun:67+68+79+80+227+285+289` — `String.concat "; "`, `String.concat "\n"`
- `src/funlex/FunlexMain.fun:44` — `String.concat " " rest`
- All emitter files use it extensively

**Implementation hint:** Add a C runtime function:
```c
LangString* lang_string_concat_list(LangString* sep, LangList* strings);
```
Walk the list, calculate total length, allocate result string, copy segments with separator between them.

**Test suggestion:** `.flt` test: `String.concat ", " ["a"; "b"; "c"]` should return `"a, b, c"`.

**Priority:** P1 (needed)
**Effort:** LOW — straightforward string operation

---

#### Feature 29: `eprintfn`

**Description:** Like `printfn` but outputs to stderr.

**LangThree interpreter status:** Supported (interpreter).

**LangBackend compiler status:** `eprint` exists (Elaboration.fs line 1161) but not `eprintfn` (formatted stderr output).

**FunLexYacc usage:**
- `src/common/Diagnostics.fun:56` — `eprintfn "%s" (formatError err)`

**Implementation hint:** Once `sprintf` is implemented, `eprintfn fmt args` can desugar to `eprint (sprintf fmt args); eprint "\n"`. Or emit `fprintf(stderr, ...)` libc call.

**Test suggestion:** `.flt` test: `eprintfn "%s: %d" "error" 42` should output "error: 42\n" to stderr.

**Priority:** P1 (needed)
**Effort:** MEDIUM (depends on sprintf)

---

### Category 4: Prelude Compilation

---

#### Feature 30: Prelude Auto-Loading

**Description:** LangThree automatically loads `Prelude/*.fun` files at startup, making Option, Result, List, Core, Array, Hashtable modules available. LangBackend needs to either compile these Prelude files alongside user code or provide native implementations of all Prelude functions.

**LangThree interpreter status:** Fully automatic. Files in `Prelude/` are loaded in order.

**LangBackend compiler status:** Missing. No Prelude loading mechanism.

**FunLexYacc dependency:** FunLexYacc defines its own `ErrorInfo.Result` type (not using Prelude's), but uses:
- `List.map`, `List.filter`, `List.fold`, `List.length`, `List.head`, `List.tail`, etc.
- `Option` type (`Some`/`None`)
- `string_concat`, `to_string`, `failwith`
- `|>` pipe operator (already handled in LangBackend v3.0)

**Implementation hint:** Two approaches:
1. **Compile Prelude:** Requires Feature 3 (file import). Concatenate Prelude files + user code, compile together.
2. **Native implementations:** Implement all Prelude functions as C runtime or hardcoded Elaboration.fs cases.

Approach 1 is more maintainable. Approach 2 is needed for functions like `List.sort` that don't exist in Prelude.

**Priority:** P0 (blocker) — many List functions used everywhere
**Effort:** HIGH — requires either file import or 20+ C runtime functions

---

## Recommended Implementation Order

The features have complex interdependencies. Here is a recommended order that unlocks the most FunLexYacc code at each step:

### Wave 1: Foundation (unlocks basic compilation)

| Order | Feature # | Name | Rationale |
|-------|-----------|------|-----------|
| 1.1 | 3 | File import / multi-file compilation | Unlocks ALL cross-file dependencies. Without this, nothing compiles. |
| 1.2 | 30 | Prelude compilation | Once file import works, Prelude can be loaded. Unlocks List.*, Option, etc. |
| 1.3 | 4 | `get_args` | Unlocks main entry points (FunlexMain, FunyaccMain). |
| 1.4 | 12 | `.Length` on strings/arrays | Used in virtually every file for loop bounds. |
| 1.5 | 10 | String slicing `s.[a..b]` | Used in all main entry points and parsers. |

### Wave 2: Format Strings (unlocks output/diagnostics)

| Order | Feature # | Name | Rationale |
|-------|-----------|------|-----------|
| 2.1 | 1 | `sprintf` | Foundation for all formatted output. |
| 2.2 | 2 | `printfn` | Builds on sprintf. |
| 2.3 | 49 | `eprintfn` | Builds on sprintf. Unlocks Diagnostics.fun. |

### Wave 3: .NET Collections (unlocks core algorithms)

| Order | Feature # | Name | Rationale |
|-------|-----------|------|-----------|
| 3.1 | 7 | StringBuilder | Most-used .NET type (51 call sites). |
| 3.2 | 8 | Dictionary | Second most-used (60+ sites). Also unlocks .TryGetValue. |
| 3.3 | 13 | .TryGetValue | Paired with Dictionary. |
| 3.4 | 9 | HashSet | Required by Dfa, Lalr, Lr0, FirstFollow. |
| 3.5 | 10 | Queue | Required by Dfa (epsilon closure), Lr0 (state construction). |
| 3.6 | 11 | Mutable List | Required by Lalr, Lr0, ParserTables. |
| 3.7 | 15 | Method dispatch (.Append, .Add, etc.) | Cross-cutting: all collections need method calls. |
| 3.8 | 16 | .Key/.Value on KVP | Paired with Dictionary iteration. |
| 3.9 | 14 | for-in on .NET collections | Paired with all collection types. |
| 3.10 | 19 | .Count property | Paired with all collection types. |
| 3.11 | 20 | .Keys property | Paired with Dictionary. |

### Wave 4: List/Array Extras (unlocks DfaMin, LexEmit, emitters)

| Order | Feature # | Name | Rationale |
|-------|-----------|------|-----------|
| 4.1 | 45 | List comprehension | 7 sites in DfaMin alone. |
| 4.2 | 21 | List.sort/sortBy/distinctBy | DfaMin minimization. |
| 4.3 | 22 | List.mapi | LexEmit, Nfa. |
| 4.4 | 23 | List.item | DfaMin table access. |
| 4.5 | 24 | List.exists/tryFind/choose | YaccEmit. |
| 4.6 | 25 | List.isEmpty/head/tail | Various. |
| 4.7 | 26 | List.ofSeq / Array.ofSeq | Collection conversions. |
| 4.8 | 27 | Array.sort | Dfa epsilon closure. |
| 4.9 | 28 | String.concat | All emitter files. |

### Wave 5: Minor .NET Interop (polishing)

| Order | Feature # | Name | Rationale |
|-------|-----------|------|-----------|
| 5.1 | 38+39 | System.Char methods | FunlexMain, YaccEmit. |
| 5.2 | 40+41+42 | System.IO.File methods | Map to existing builtins. |
| 5.3 | 43 | .EndsWith | Main entry points. |
| 5.4 | 44 | .Trim | GrammarParser, YaccEmit. |
| 5.5 | 46 | Unchecked.defaultof | Lalr, ParserTables. |
| 5.6 | 51 | KVP field access | Dictionary iteration. |
| 5.7 | 47 | pipe + ignore | Style convenience. |

### Alternative Strategy: Rewrite FunLexYacc

If implementing full .NET interop is too costly, an alternative is to rewrite FunLexYacc to use only LangThree-native types:

| .NET Type | LangThree Replacement |
|-----------|----------------------|
| `System.Text.StringBuilder` | `string_concat` chains or a new `StringBuilder` Prelude module |
| `System.Collections.Generic.Dictionary<K,V>` | `Hashtable.create/get/set/containsKey` |
| `System.Collections.Generic.HashSet<T>` | `Hashtable.create/set/containsKey` (key-only) |
| `System.Collections.Generic.Queue<T>` | Functional list used as queue (or new Prelude module) |
| `System.Collections.Generic.List<T>` | LangThree mutable array with dynamic resizing |
| `.Length` | `string_length` / `array_length` |
| `System.IO.File.*` | `read_file` / `write_file` / `file_exists` |
| `System.Char.*` | `char_to_int` + manual comparison |
| `s.[a..b]` | `string_sub s a (b - a + 1)` |

This rewrite affects approximately 384 call sites across 14 files but keeps LangBackend's scope manageable.

---

## Effort Summary

| Category | Features | Total Effort |
|----------|----------|-------------|
| Language constructs (sprintf, file import, slicing, comprehension) | 6 features | ~4 HIGH |
| .NET Collections (StringBuilder, Dictionary, HashSet, Queue, List) | 11 features | ~8 HIGH |
| Method dispatch + properties | 6 features | ~3 HIGH, 3 MEDIUM |
| Prelude/stdlib functions | 9 features | ~2 MEDIUM, 7 LOW |
| Minor .NET interop | 7 features | ~7 LOW |
| **Total** | **39 new features** | **~15 HIGH, 5 MEDIUM, 14 LOW** |

Estimated total development effort: **3-5 weeks** for a single developer, assuming the "implement C runtime" approach for .NET collections.

If the "rewrite FunLexYacc" approach is taken instead, the LangBackend work reduces to: file import + sprintf/printfn + get_args + string slicing + list comprehension + Prelude compilation + a few List/Array extras = **approximately 1-2 weeks**.
