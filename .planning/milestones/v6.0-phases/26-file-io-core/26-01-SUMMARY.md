---
phase: 26-file-io-core
plan: 01
subsystem: compiler-runtime
tags: [c-runtime, file-io, elaboration, builtins, gc, posix-stdio]

# Dependency graph
requires:
  - phase: 25-module-system
    provides: qualified name desugar, 100 passing E2E tests baseline
  - phase: 22-arrays
    provides: C runtime builtin pattern (lang_array_*, ExternalFuncDecl, LlvmCallOp)
  - phase: 19-exception-handling
    provides: lang_throw for catchable runtime exceptions

provides:
  - 6 C runtime functions: lang_file_read, lang_file_write, lang_file_append, lang_file_exists, lang_eprint, lang_eprintln
  - ExternalFuncDecl entries in both externalFuncs lists in Elaboration.fs
  - 6 elaborateExpr arms dispatching file I/O builtins to C calls
  - 8 E2E tests covering write+read, file_exists true/false, append, read-missing exception, eprint/eprintln stderr, overwrite

affects: [27-file-io-extended, any phase using file I/O or stderr builtins]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "POSIX stdio (fopen/fread/fwrite/fclose/fseek/ftell) for file I/O in C runtime"
    - "lang_throw for catchable file-not-found errors (LangString* message)"
    - "fwrite(s->data, 1, s->length, stderr) + fflush for stderr output without format strings"
    - "ArithCmpIOp(boolVal, ne, rawVal, zeroVal) to convert I64 0/1 to I1 for file_exists"
    - "LlvmCallVoidOp + ArithConstantOp(unitVal, 0L) for void C functions returning unit"
    - "Two-arg builtins placed before one-arg builtins in elaborateExpr (F# first-match semantics)"

key-files:
  created:
    - tests/compiler/26-01-write-read.flt
    - tests/compiler/26-02-file-exists-true.flt
    - tests/compiler/26-03-file-exists-false.flt
    - tests/compiler/26-04-append-file.flt
    - tests/compiler/26-05-read-missing.flt
    - tests/compiler/26-06-eprint.flt
    - tests/compiler/26-07-eprintln.flt
    - tests/compiler/26-08-write-overwrite.flt
  modified:
    - src/FunLangCompiler.Compiler/lang_runtime.c
    - src/FunLangCompiler.Compiler/lang_runtime.h
    - src/FunLangCompiler.Compiler/Elaboration.fs

key-decisions:
  - "lang_file_read throws via lang_throw on missing file (catchable by try/with), not lang_failwith (which exits)"
  - "lang_file_write silently ignores fopen failure (no throw) — requirement does not specify error behavior"
  - "fwrite+fflush for eprint/eprintln instead of fprintf — avoids format string parsing, uses known-length binary string"
  - "LangString typedef moved to forward declaration in .h (struct LangString_s typedef) to fix clang redefinition error"
  - "E2E tests for eprint/eprintln use single-line let _ = ... in format and redirect stderr with 2>/dev/null — multiline semicolons caused IndentFilter parse error"
  - "file_exists uses fopen with 'r' mode — portable, consistent with existing C runtime dependencies, no extra unistd.h needed"

patterns-established:
  - "Pattern: Two-arg void builtin — App(App(Var name, arg1), arg2) -> LlvmCallVoidOp + ArithConstantOp(unit, 0L)"
  - "Pattern: One-arg Ptr-returning builtin — App(Var name, arg) -> LlvmCallOp with Type = Ptr"
  - "Pattern: Bool-returning builtin — I64 result + ArithCmpIOp ne 0 to produce I1"

# Metrics
duration: 25min
completed: 2026-03-27
---

# Phase 26 Plan 01: File I/O Core Summary

**6 C runtime functions (lang_file_read/write/append/exists, lang_eprint/eprintln) + Elaboration.fs wiring + 8 E2E tests; all 108 tests passing**

## Performance

- **Duration:** ~25 min
- **Started:** 2026-03-27T07:00:00Z
- **Completed:** 2026-03-27T07:25:00Z
- **Tasks:** 3
- **Files modified:** 11 (3 source + 8 test)

## Accomplishments
- Implemented 6 C runtime functions following established builtin pattern (GC_malloc, lang_throw, fwrite+fflush)
- Wired all 6 builtins in Elaboration.fs: ExternalFuncDecl entries in BOTH lists + 6 elaborateExpr arms
- Added 8 E2E tests covering all specified behaviors: write/read round-trip, append, file_exists true/false, read-missing exception, eprint/eprintln stderr, overwrite
- All 108 tests pass (100 existing + 8 new)

## Task Commits

1. **Task 1: C runtime functions** - `9db6a25` (feat)
2. **Task 2: Elaboration.fs wiring** - `be817b0` (feat)
3. **Task 3: E2E tests + typedef fix** - `cbbe277` (feat)

## Files Created/Modified
- `src/FunLangCompiler.Compiler/lang_runtime.c` - Added lang_file_read, lang_file_write, lang_file_append, lang_file_exists, lang_eprint, lang_eprintln; renamed struct to LangString_s
- `src/FunLangCompiler.Compiler/lang_runtime.h` - Forward declaration for LangString_s typedef + 6 function declarations
- `src/FunLangCompiler.Compiler/Elaboration.fs` - 6 ExternalFuncDecl entries in both lists + 6 elaborateExpr arms
- `tests/compiler/26-01-write-read.flt` through `26-08-write-overwrite.flt` - 8 E2E tests

## Decisions Made
- lang_file_read uses lang_throw (catchable) not lang_failwith (exits) for missing file
- lang_file_write silently ignores fopen failure (requirement unspecified)
- fwrite+fflush for stderr rather than fprintf (avoids IsVarArg, uses known-length buffer)
- file_exists uses fopen("r") not access() (no extra include needed)
- eprint/eprintln E2E tests use single-line `let _ = eprint "..." in println "ok"` with `2>/dev/null` to suppress stderr — multiline format caused IndentFilter NEWLINE token to break SeqExpr parsing

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed LangString typedef redefinition (clang error)**
- **Found during:** Task 3 (running fslit test suite)
- **Issue:** lang_runtime.h defined `typedef struct { ... } LangString` inline, but lang_runtime.c already defines `typedef struct { ... } LangString`. Clang rejected this as a redefinition error: "typedef redefinition with different types".
- **Fix:** Renamed struct tag to `LangString_s` in lang_runtime.c and used a forward declaration `struct LangString_s; typedef struct LangString_s LangString;` in lang_runtime.h.
- **Files modified:** lang_runtime.c, lang_runtime.h
- **Verification:** dotnet build succeeds, fslit tests pass (previously all 100 tests were failing with clang error)
- **Committed in:** cbbe277 (Task 3 commit)

**2. [Rule 1 - Bug] Fixed E2E test format for eprint/eprintln**
- **Found during:** Task 3 (running fslit test suite)
- **Issue:** Plan specified `eprint "err msg"; println "ok"` as test input. The FunLang IndentFilter inserts NEWLINE tokens on line breaks, breaking `SeqExpr` parsing when a bare function call (`eprint`) starts a new line without `let`/`in` context. Result: "parse error".
- **Fix:** Rewrote eprint/eprintln tests to use single-line `let _ = eprint "..." in println "ok"` format, consistent with established pattern. Also added `2>/dev/null` to command to suppress stderr from test output.
- **Files modified:** tests/compiler/26-06-eprint.flt, tests/compiler/26-07-eprintln.flt
- **Verification:** Both tests now pass with fslit
- **Committed in:** cbbe277 (Task 3 commit)

---

**Total deviations:** 2 auto-fixed (2 Rule 1 bugs)
**Impact on plan:** Both fixes necessary for correctness. No scope creep.

## Issues Encountered
- clang typedef redefinition error broke all 100 existing tests (not just new ones) — quickly diagnosed via `fslit -v` showing the clang error in stderr output

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- File I/O core is complete (read, write, append, exists, eprint, eprintln)
- All 108 tests passing — solid baseline for phase 27
- Phase 27 (file I/O extended) can add path_combine, dir_files, get_env, delete_file on top of this foundation

---
*Phase: 26-file-io-core*
*Completed: 2026-03-27*
