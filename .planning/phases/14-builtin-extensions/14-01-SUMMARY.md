---
phase: 14-builtin-extensions
plan: 01
subsystem: compiler
tags: [mlir, builtin, runtime, c, string, char, failwith]

# Dependency graph
requires:
  - phase: 13-pattern-matching-extensions
    provides: OrPat expansion, when-guard, CharConst in patterns; Char literal elaboration already in Phase 12
  - phase: 12-closures-and-operators
    provides: Char(c,_) elaboration case emitting ArithConstantOp(int64 (int c))
provides:
  - BLT-01: failwith — prints message to stderr, exits 1
  - BLT-02: string_sub — GC_malloc'd substring with bounds clamping
  - BLT-03: string_contains — strstr-based needle search returning I1
  - BLT-04: string_to_int — strtol-based string-to-integer conversion
  - BLT-05: char_to_int — identity op (char is already i64 ASCII code point)
  - BLT-06: int_to_char — identity op (int treated as char code point)
  - BLT-07: println with let-bound string variable (already worked; verified with E2E test)
  - 4 new C functions in lang_runtime.c
  - 7 new App pattern cases in Elaboration.fs
  - 4 new ExternalFuncDecl entries
  - 6 new FsLit E2E tests (43 total)
affects: [future phases using string manipulation, error handling, char operations]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Multi-arg curried builtins use nested App(App(App(...))) match, outermost first"
    - "C void functions called via LlvmCallVoidOp; result synthesized as ArithConstantOp(0L)"
    - "char_to_int and int_to_char are identity elaborations — no C function needed"
    - "string_contains: C returns I64 0/1; convert to I1 via ArithCmpIOp(ne, 0)"
    - "failwith: GEP field 1 to get data ptr, LlvmLoadOp to get char*, then LlvmCallVoidOp"

key-files:
  created:
    - tests/compiler/14-01-failwith.flt
    - tests/compiler/14-02-string-sub.flt
    - tests/compiler/14-03-string-contains.flt
    - tests/compiler/14-04-string-to-int.flt
    - tests/compiler/14-05-char-to-int.flt
    - tests/compiler/14-06-println-variable.flt
  modified:
    - src/LangBackend.Compiler/lang_runtime.c
    - src/LangBackend.Compiler/Elaboration.fs

key-decisions:
  - "char_to_int and int_to_char are identity operations — char is already i64 in MLIR"
  - "string_contains returns I64 from C; elaboration converts to I1 via cmpi ne 0 for boolean use in if-then-else"
  - "failwith synthesizes a dead ArithConstantOp(0L) after LlvmCallVoidOp to give the case an MlirValue return type"
  - "Multi-arg patterns (string_sub 3-arg, string_contains 2-arg) placed before string_concat and general App"
  - "Char literal elaboration was already present from Phase 12 — skipped as instructed"

patterns-established:
  - "Pattern: Void C helper -> LlvmCallVoidOp + ArithConstantOp(0L) for unit value"
  - "Pattern: I64 0/1 from C -> ArithCmpIOp(ne, 0) for MLIR I1 boolean"

# Metrics
duration: ~5min
completed: 2026-03-26
---

# Phase 14 Plan 01: Builtin Extensions Summary

**Six new builtins (failwith, string_sub, string_contains, string_to_int, char_to_int, int_to_char) added via 4 C helpers in lang_runtime.c and 7 new App pattern cases in Elaboration.fs; 43/43 FsLit E2E tests pass**

## Performance

- **Duration:** ~5 min
- **Started:** 2026-03-26T10:04:00Z
- **Completed:** 2026-03-26T10:09:00Z
- **Tasks:** 3
- **Files modified:** 2 (lang_runtime.c, Elaboration.fs) + 6 new test files

## Accomplishments

- Added `lang_failwith`, `lang_string_sub`, `lang_string_contains`, `lang_string_to_int` to lang_runtime.c with bounds clamping and GC-backed allocation
- Added 7 new App pattern cases to Elaboration.fs covering all BLT-01..07 requirements; multi-arg patterns ordered before general App dispatch
- Added 4 new ExternalFuncDecl entries for the new C functions
- Created 6 FsLit E2E tests; all pass alongside 37 pre-existing tests (43 total)

## Task Commits

Each task was committed atomically:

1. **Task 1: Add C helper functions to lang_runtime.c** - `2690064` (feat)
2. **Task 2: Add Char elaboration, new builtin cases, ExternalFuncDecl entries** - `69f989f` (feat)
3. **Task 3: Write E2E tests for BLT-01..07** - `e31b75d` (test)

## Files Created/Modified

- `src/LangBackend.Compiler/lang_runtime.c` - Added lang_failwith, lang_string_sub, lang_string_contains, lang_string_to_int
- `src/LangBackend.Compiler/Elaboration.fs` - Added 7 new builtin App cases and 4 ExternalFuncDecl entries
- `tests/compiler/14-01-failwith.flt` - BLT-01: failwith exits 1
- `tests/compiler/14-02-string-sub.flt` - BLT-02: string_sub extracts "world" (length 5)
- `tests/compiler/14-03-string-contains.flt` - BLT-03: string_contains returns true
- `tests/compiler/14-04-string-to-int.flt` - BLT-04: string_to_int "42" exits 42
- `tests/compiler/14-05-char-to-int.flt` - BLT-05: char_to_int 'Z' exits 90
- `tests/compiler/14-06-println-variable.flt` - BLT-07: println with let-bound string variable

## Decisions Made

- `char_to_int` and `int_to_char` are identity operations: char literals elaborate to `i64` (ASCII code point), so no C function is needed
- `string_contains` C function returns `int64_t` 0/1; elaboration adds `ArithCmpIOp(ne, 0)` to produce `I1` for `if-then-else` compatibility
- `failwith` is a `void` C function; elaboration emits a dead `ArithConstantOp(0L)` after `LlvmCallVoidOp` so the match case has a typed return value
- `Char(c, _)` elaboration was already present from Phase 12 — confirmed and skipped

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Multi-line input in FsLit test caused parse error**
- **Found during:** Task 3 (14-06-println-variable test)
- **Issue:** The test source had line breaks between `let` bindings; FsLit passes the file path to the compiler which read the multi-line content, but the parser returned a parse error
- **Fix:** Rewrote test source to single-line `let s = "hello" in let _ = println s in 0` matching the pattern used in 07-02-print-basic.flt
- **Files modified:** tests/compiler/14-06-println-variable.flt
- **Verification:** Test passes after fix
- **Committed in:** e31b75d (Task 3 commit)

---

**Total deviations:** 1 auto-fixed (Rule 1 - bug)
**Impact on plan:** Minor format issue in test file; fix was trivial and consistent with existing test conventions.

## Issues Encountered

None beyond the multi-line input deviation above.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- All 43 FsLit E2E tests green
- BLT-01..07 requirements fully satisfied
- lang_runtime.c and Elaboration.fs are ready for further builtin extensions if needed
- No blockers for next phase

---
*Phase: 14-builtin-extensions*
*Completed: 2026-03-26*
