# Project Research Summary

**Project:** LangBackend v6.0 — Modules & I/O
**Domain:** MLIR/LLVM compiler backend — module system + file I/O builtins
**Researched:** 2026-03-27
**Confidence:** HIGH

## Executive Summary

v6.0 adds two features to the LangBackend MLIR/LLVM compiler: a module system and 14 file I/O builtins. Both are pure in-project source changes — no new external dependencies, no new MLIR ops, no new `MlirType` variants. The module system is implemented as compile-time AST flattening in `Elaboration.fs` (`prePassDecls` + `extractMainExpr`), and file I/O is implemented as C runtime additions to `lang_runtime.c` following the exact same pattern used by all existing builtins (string, array, hashtable). The entire change surface is: `lang_runtime.c` (+14 C functions, +2 includes), `lang_runtime.h` (+14 declarations), and `Elaboration.fs` (module flattening + 14 builtin dispatch arms + 14 `ExternalFuncDecl` entries).

The single non-obvious complexity is qualified name resolution. The backend CLI (`Program.fs`) calls `parseProgram` then `elaborateProgram` directly — it does NOT call the LangThree TypeCheck. This means the raw AST still contains `FieldAccess(Constructor("M", None), "field")` nodes for qualified module access. The backend must desugar these itself. The correct approach is a one-line pattern in `elaborateExpr`: `FieldAccess(Constructor(_, None, _), fieldName, span)` where the constructor name is a known module → treat as `Var(fieldName, span)`. This works because module declarations are flattened first, so all inner bindings are in scope at their unqualified names.

`FileImportDecl` (the `open "lib.fun"` syntax) should be treated as a no-op or explicit error for v6.0. The LangThree test suite has 3 file-import tests vs. 11 module tests and the feature requires multi-file compilation. All other module features (`ModuleDecl`, `OpenDecl`, qualified access, ADTs/records/exceptions inside modules) are required and achievable with the flattening strategy.

## Key Findings

### Recommended Stack

No new external dependencies. The existing stack handles everything:

**Core technologies:**
- F# / .NET 10: compiler implementation — unchanged
- MLIR LLVM 20 (`mlir-opt`, `mlir-translate`): lowering pipeline — unchanged
- Boehm GC (`libgc`): heap allocation — unchanged; note that GC does NOT close file handles, so all file I/O C functions must call `fclose` before returning
- `lang_runtime.c` (in-project): C builtins — add 14 new functions + `<unistd.h>` + `<dirent.h>` includes; all new functions use the established `GC_malloc` + `LangString*` pattern

The full builtin inventory (STD-02 through STD-15) is defined in `TypeCheck.fs` and implemented in `Eval.fs`. The backend must wire them up in `Elaboration.fs` and `lang_runtime.c` only.

### Expected Features

**Must have (module system — 11 compiler tests):**
- `ModuleDecl` flattening in `prePassDecls` and `extractMainExpr` — inner let/type/record/exception decls must be visible
- `OpenDecl` handling — no-op at elaboration time once names are flat
- Qualified access desugar — `M.func` → `Var("func")` in `elaborateExpr`
- `NamespaceDecl` — treat identically to `ModuleDecl` (transparent container)
- Module-scoped ADTs/records/exceptions — requires `prePassDecls` recursion

**Must have (file I/O — 8 compiler tests):**
- All 14 builtins (STD-02 through STD-15): `read_file`, `stdin_read_all`, `stdin_read_line`, `write_file`, `append_file`, `file_exists`, `read_lines`, `write_lines`, `get_args`, `get_env`, `get_cwd`, `path_combine`, `dir_files`, `eprint`, `eprintln`
- Error-throwing builtins (`read_file`, `read_lines`, `get_env`, `dir_files`) must use `lang_throw`, not `exit()`, so errors are catchable in user `try-with`
- `read_lines` / `dir_files` / `get_args` must return `LangCons*` linked lists, not arrays
- Void-return builtins (`write_file`, `append_file`, `write_lines`, `eprint`, `eprintln`) must follow the `print`/`println` pattern: `LlvmCallVoidOp` + `ArithConstantOp(unitVal, 0L)`

**Defer (stretch / v7+):**
- `FileImportDecl` — requires multi-file compilation; only 3 tests; treat as no-op or explicit error in v6.0
- `get_args` returning actual argv — requires changing `@main` MLIR signature from `() -> i64` to `(i32, ptr) -> i64`; return empty list for v6.0
- Partial application of multi-arg file I/O builtins (e.g., `let f = write_file "path"`) — not supported by the saturated-call pattern; consistent with all other multi-arg builtins

### Architecture Approach

The architecture is strictly additive. `MlirIR.fs`, `Printer.fs`, and `Pipeline.fs` require zero changes. All module and I/O additions land in three files: `Elaboration.fs`, `lang_runtime.c`, and `lang_runtime.h`.

**Major components and their changes:**

1. `prePassDecls` (Elaboration.fs line ~2317) — add `ModuleDecl`/`NamespaceDecl` arms that recurse into inner decls and merge `TypeEnv`/`RecordEnv`/`ExnTags`; this is required for module-scoped ADTs/records/exceptions to be visible during elaboration

2. `extractMainExpr` (Elaboration.fs line ~2355) — add a `flattenDecls` pre-pass that recursively inlines `ModuleDecl`/`NamespaceDecl` inner decls and skips `OpenDecl`/`FileImportDecl`; the resulting flat `Decl list` flows through the existing `LetDecl`/`LetRecDecl`/`LetMutDecl` filter unchanged

3. `elaborateExpr` builtin dispatch (Elaboration.fs before the general `App` case at line ~1112) — add 14 file I/O builtin arms; two-arg builtins (`write_file`, `append_file`, `path_combine`, `write_lines`) must appear before one-arg builtins to prevent the general `App` case from claiming them first; also add the `FieldAccess(Constructor(_, None, _), fieldName, _)` arm for qualified module access desugar

4. `elaborateProgram` external functions list (Elaboration.fs line ~2279 / ~2426) — add 14 `ExternalFuncDecl` entries; note this list exists in TWO places (`elaborateModule` and `elaborateProgram`); extract into a single shared `let private standardExternalFuncs` to avoid divergence

5. `lang_runtime.c` + `lang_runtime.h` — add 14 new C functions; all use `GC_malloc`; all string args/returns use `LangString*`; all use `->data` when passing to OS functions; all call `fclose` before returning; error paths use `lang_throw` not `exit`

### Critical Pitfalls

1. **`prePassDecls` not recursing into `ModuleDecl`** — inner `TypeDecl`/`RecordTypeDecl`/`ExceptionDecl` are invisible to `TypeEnv`; pattern matching on module-scoped ADTs fails at runtime with "constructor not found". Fix: add recursive arm to `prePassDecls`.

2. **`extractMainExpr` silently dropping `ModuleDecl` bodies** — `let` bindings inside modules are discarded; programs compile but module functions are missing, causing "variable not in scope" errors. Fix: flatten module inner decls before the existing filter.

3. **Builtin arm ordering in `elaborateExpr`** — two-arg builtins (`write_file path content`) must be matched before one-arg builtins and before the general `App` case; wrong order causes "unsupported App — write_file is not a known function" elaboration failure.

4. **File handles not closed by Boehm GC** — `fopen()` returns OS file descriptors; GC cannot call `fclose`. Programs opening many files will exhaust `EMFILE` (default ~1024). Fix: all `lang_read_file`/`lang_write_file` etc. must call `fclose` internally before returning; do not expose raw `FILE*` to user code.

5. **Duplicate `ExternalFuncs` list** — `elaborateModule` and `elaborateProgram` each have their own hardcoded list; adding a new C function to only one causes link failures. Fix: extract into one shared constant.

6. **`fopen` null return not checked** — returning a null `Ptr` from a failed open causes a segfault on the next GEP, not a catchable exception. Fix: every file C function must check `fopen` return and call `lang_throw` on NULL.

7. **`FieldAccess` qualified access not desugared** — because the backend CLI skips TypeCheck, `M.func` remains as `FieldAccess(Constructor("M", None), "func")` in the AST; it must be desugared in `elaborateExpr` to `Var("func")` after confirming the constructor name is a known module.

## Implications for Roadmap

Based on the hard dependencies between components, the natural phase split is:

### Phase 1: Module System — AST Flattening

**Rationale:** Module flattening is a prerequisite for any tests that use `module M = let f = ...` syntax. It is also the most structurally significant change (touches `prePassDecls`, `extractMainExpr`, and `elaborateExpr`). It must be done before file I/O tests that happen to be inside modules.

**Delivers:** Working `module M = ...` declarations, `open M`, qualified access `M.func`, module-scoped ADTs/records/exceptions. Passes 11 module compiler tests.

**Addresses features:** `ModuleDecl` flattening, `OpenDecl` no-op, `NamespaceDecl` transparency, qualified `FieldAccess` desugar, `prePassDecls` recursion.

**Avoids pitfalls:** prePassDecls recursion gap (Pitfall 2), extractMainExpr drop (Pitfall 3), FieldAccess desugar (Pitfall 7), constructor name collision (Pitfall 14).

**Standard patterns:** Well-documented; no deeper research needed. The interpreter's `evalModuleDecls` in `Eval.fs` is the reference implementation.

### Phase 2: File I/O Builtins

**Rationale:** File I/O is purely additive — new C functions + new dispatch arms — with no structural impact on the module or elaboration architecture. It can proceed in parallel with or after Phase 1. The C patterns are identical to existing builtins.

**Delivers:** All 14 file I/O builtins wired up end-to-end. Passes 8 file I/O compiler tests.

**Addresses features:** STD-02 through STD-15, `LangCons*` list returns for `read_lines`/`dir_files`/`get_args`, `lang_throw` error paths for all fallible operations, `fclose` discipline in C runtime.

**Avoids pitfalls:** GC/fclose lifetime (Pitfall 5), null fopen check (Pitfall 6), builtin arm ordering (Pitfall 3), void return convention (units as `ArithConstantOp 0L`), `ExternalFuncs` deduplication (Pitfall 7/PITFALL 7), `LangString*` not `char*` in C signatures (Pitfall 11), `read_lines` returns cons list not array (Pitfall 13), correct fopen modes `"r"`/`"w"`/`"a"` (Pitfall 12).

**Standard patterns:** Well-documented; follows the same four-part pattern as the 15+ existing builtins. No deeper research needed.

### Phase 3: Integration and Edge Cases

**Rationale:** After both features are individually working, integration tests that combine modules and file I/O should be verified. Edge cases like `stdin_read_line` EOF semantics, `get_args` returning empty list, and `FileImportDecl` error message should be finalized.

**Delivers:** Passing combined module+I/O tests; documented `FileImportDecl` behavior (explicit error); `get_args` empty-list stub; `stdin_read_line` EOF matches interpreter (`""` not exception).

**Avoids pitfalls:** stdin EOF semantic mismatch (Pitfall 9), `write_lines` newline convention (open question 3).

### Phase Ordering Rationale

- Phase 1 before Phase 2: module tests and file I/O tests are independent in the test suite, but modules are structurally more invasive (requires modifying core `prePassDecls`/`extractMainExpr` logic). Establishing the flattened module architecture first avoids having to retrofit it after file I/O is in place.
- Phase 2 is almost entirely additive (new code, not modified code) and can be largely implemented in parallel with Phase 1 if desired.
- Phase 3 is a clean-up and integration phase, not a blocker for either.

### Research Flags

Phases with standard patterns (skip research-phase):
- **Phase 1:** Module flattening is fully documented by the interpreter's `Eval.fs`; `prePassDecls`/`extractMainExpr` structure is clear from source
- **Phase 2:** File I/O pattern is identical to existing builtins; 14 functions follow the same 4-step template

Phases that may benefit from targeted investigation during planning:
- **Phase 1, qualified name handling:** Confirm the exact `FieldAccess(Constructor(_, None, _), fieldName)` desugar is sufficient for all module access patterns in the 11 test cases before finalizing the plan
- **Phase 2, `ExternalFuncs` duplication:** Verify line numbers of both list locations in `Elaboration.fs` and decide whether to consolidate before or during implementation
- **Phase 3, `get_args`:** Decide at plan time whether to extend `@main` to `(i32, ptr) -> i64` or return empty list stub; the tradeoff affects test coverage

## Confidence Assessment

| Area | Confidence | Notes |
|------|------------|-------|
| Stack | HIGH | No new dependencies; confirmed via Pipeline.fs and lang_runtime.c source |
| Features | HIGH | All 14 builtins read directly from TypeCheck.fs + Eval.fs; module AST read from Ast.fs |
| Architecture | HIGH | prePassDecls/extractMainExpr structure clear from source; builtin pattern well-established across 15+ existing builtins |
| Pitfalls | HIGH | All pitfalls grounded in actual code paths; MLIR symbol naming confirmed via official LangRef and OCaml toolchain history |

**Overall confidence:** HIGH

### Gaps to Address

- **Qualified name resolution implementation choice:** The backend CLI skips TypeCheck, so the backend must desugar `FieldAccess(Constructor(modName, None), field)` itself. The recommended approach (treat as `Var(field)` after confirming `modName` is in a `ModuleNames` set) is correct after flattening, but should be verified against all 11 module test cases before committing to the implementation during planning.

- **`ExternalFuncs` two-list duplication:** Research confirmed there are two separate `externalFuncs` list literals in `Elaboration.fs` (one in `elaborateModule`, one in `elaborateProgram`). The plan should explicitly address consolidation — either before adding the 14 new entries or by carefully updating both.

- **`get_args` implementation scope:** The current `@main` MLIR signature is `() -> i64` with no argv. Returning an empty list for v6.0 is safe, but if any compiler test exercises `get_args` with actual arguments, a `@main` signature change is required. This should be confirmed during phase planning by reading the file I/O test cases.

- **`stdin_read_line` buffer size:** The C implementation using `fgets(buf, 4096, stdin)` truncates lines longer than 4095 chars. This is acceptable for v6.0 but should be documented.

## Sources

### Primary (HIGH confidence — direct source reading)
- `src/LangBackend.Compiler/Elaboration.fs` — prePassDecls, extractMainExpr, elaborateProgram, all builtin dispatch patterns, ExternalFuncDecl list
- `src/LangBackend.Compiler/lang_runtime.c` — all existing C runtime patterns (LangString, LangCons, GC_malloc, lang_throw, lang_string_concat)
- `src/LangBackend.Compiler/lang_runtime.h` — struct definitions, function declarations
- `src/LangBackend.Compiler/Pipeline.fs` — confirmed no changes needed
- `src/LangBackend.Compiler/MlirIR.fs` — confirmed no new MlirOp/MlirType variants needed
- `../LangThree/src/LangThree/Ast.fs` — all Decl variants (ModuleDecl, OpenDecl, NamespaceDecl, FileImportDecl, LetPatDecl)
- `../LangThree/src/LangThree/Eval.fs` — evalModuleDecls, initialBuiltinEnv, ModuleValueEnv
- `../LangThree/src/LangThree/TypeCheck.fs` — all 14 file I/O builtin type signatures, rewriteModuleAccess, resolveImportPath
- `../LangThree/src/LangThree/Parser.fsy` — module grammar, QualifiedIdent rules
- `src/LangBackend.Cli/Program.fs` — confirmed backend CLI does NOT call TypeCheck

### Secondary (HIGH confidence — official documentation)
- [MLIR Language Reference](https://mlir.llvm.org/docs/LangRef/) — suffix-id character rules; `__` separator recommendation
- [MLIR Symbols and Symbol Tables](https://mlir.llvm.org/docs/SymbolsAndSymbolTables/) — symbol reference rules
- [Boehm GC documentation](https://www.hboehm.info/gc/) — GC manages heap memory, does not close file descriptors

### Tertiary (MEDIUM confidence)
- OCaml PR #11430 / #12933 / #14143 — dots in LLVM symbol names broke LLDB on macOS arm64; validates `__` separator recommendation
- LangThree test files in `tests/flt/file/module/` (11 tests) and `tests/flt/file/fileio/` (8 tests) and `tests/flt/file/import/` (3 tests)

---
*Research completed: 2026-03-27*
*Ready for roadmap: yes*
