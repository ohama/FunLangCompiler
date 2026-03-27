---
phase: 17-adt-construction-pattern-matching
plan: "02"
subsystem: compiler
tags: [mlir, adt, pattern-matching, matchcompiler, elaboration, indentfilter, fsharp]

requires:
  - phase: 17-01
    provides: ADT constructor elaboration, heap layout (slot 0=tag, slot 1=payload), TypeEnv with real tags

provides:
  - AdtCtor ConstructorPat emission in Elaboration.fs (tag comparison + payload extraction)
  - resolveAccessor Ptr-retype guard for nested GEP when parent cached as I64 but holds Ptr
  - MatchCompiler slot offset fix (AdtCtor argAccessors use i+1 to skip tag slot)
  - IndentFilter wired into parseProgram — multi-line source files now parse correctly
  - 3 E2E test files: 17-04 (nullary), 17-05 (unary), 17-06 (multi-arg) pattern match round-trips

affects:
  - phase 18 (Records): RecordPat matching will reuse the same resolveAccessor mechanism
  - phase 19 (Exception Handling): ExceptionPat may need similar tag-comparison logic

tech-stack:
  added: []
  patterns:
    - "ADT match: GEP[0] load tag, compare with real tag from TypeEnv; GEP[1+i] for payload fields"
    - "Ptr-retype guard: if parent cached as I64 but needs GEP, re-resolve parent as Ptr"
    - "IndentFilter.filter wraps raw Lexer token stream before Parser.parseModule call"

key-files:
  created:
    - tests/compiler/17-04-nullary-match.flt
    - tests/compiler/17-05-unary-match.flt
    - tests/compiler/17-06-multi-arg-match.flt
  modified:
    - src/LangBackend.Compiler/MatchCompiler.fs
    - src/LangBackend.Cli/Program.fs

key-decisions:
  - "MatchCompiler AdtCtor argAccessors offset by +1: slot 0 is reserved for tag, payload at slot 1..N"
  - "IndentFilter must be applied in parseProgram — raw NEWLINE tokens from Lexer are rejected by parser grammar"
  - "Ptr-retype guard in resolveAccessor Field case: re-resolve parent as Ptr when I64 but GEP needed (multi-arg ADT payloads stored as tuple Ptr)"

patterns-established:
  - "parseModule requires filtered token stream: lexAndFilter collects raw tokens, applies IndentFilter, then feeds custom tokenizer to Parser.parseModule"
  - "ADT payload accessor: Field(scrutAcc, i+1) for i-th payload field (not Field(scrutAcc, i))"

duration: 45min
completed: 2026-03-27
---

# Phase 17 Plan 02: ADT Pattern Matching Summary

**ConstructorPat fully implemented: tag comparison + payload extraction; MatchCompiler slot offset bug fixed; IndentFilter wired into CLI; 51/51 E2E tests pass**

## Performance

- **Duration:** ~45 min
- **Started:** 2026-03-26T23:55:00Z (continued from previous session)
- **Completed:** 2026-03-27T00:44:54Z
- **Tasks:** 2
- **Files modified:** 5

## Accomplishments

- Fixed critical MatchCompiler bug: `argAccessors` for `AdtCtor` were using `Field(selAcc, i)` (tag slot) instead of `Field(selAcc, i+1)` (payload slot). This caused `Some 42 -> 1` instead of `42`.
- Fixed parseProgram to route through `LangThree.IndentFilter` so multi-line `.lt` source files parse correctly. Previously, raw NEWLINE tokens caused parse errors and tests were "passing by accident" (exit 1 = expected "1").
- Added Ptr-retype guard in `resolveAccessor Field` case: when a parent accessor was cached as `I64` but the new use requires a GEP (pointer deref), re-resolve the parent as `Ptr` using `resolveAccessorTyped`.
- Added 3 real E2E tests that verify actual semantics: nullary dispatch (Green->2), unary payload extraction (Some 42->42), multi-arg field extraction (Pair(7,5)->b=5).
- All 51/51 tests pass.

## Task Commits

1. **Task 1: Ptr-retype guard in resolveAccessor** — `8c2c23c` (feat)
2. **Task 2: MatchCompiler slot offset fix + IndentFilter + 3 E2E tests** — `29fffdb` (feat)

## Files Created/Modified

- `src/LangBackend.Compiler/MatchCompiler.fs` — AdtCtor argAccessors now `Field(selAcc, i+1)` to skip tag slot
- `src/LangBackend.Cli/Program.fs` — `lexAndFilter` helper + IndentFilter integrated into `parseProgram`; debug eprintfn removed
- `tests/compiler/17-04-nullary-match.flt` — `Green -> 2` (non-trivially "1" output, genuinely passing)
- `tests/compiler/17-05-unary-match.flt` — `Some 42 -> 42` (payload extraction verified)
- `tests/compiler/17-06-multi-arg-match.flt` — `Pair(7,5) -> b=5` (second field extraction verified)

## Decisions Made

- **AdtCtor argAccessors offset**: Slot 0 is the i64 tag, so arity-k constructor payload lives at slots 1..k. Fix is `List.init arity (fun i -> Field(selAcc, i + 1))` for `AdtCtor`, unchanged for other ctor types.
- **IndentFilter in parseProgram**: The raw `Lexer.tokenize` produces `NEWLINE col` tokens. The parser grammar doesn't handle these — only `INDENT`/`DEDENT` (produced by IndentFilter). Without this fix, all multi-line test inputs silently fell through to `parseExpr` fallback or returned parse error exit 1.
- **Test values chosen to avoid accidental pass**: 17-04 uses `Green -> 2` (not "1"), 17-05 expects exit 42, 17-06 expects exit 5. Prior tests expected "1" which matched the parse error exit code.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] MatchCompiler AdtCtor argAccessors using wrong slot index**
- **Found during:** Task 2 debugging (MLIR dump showed `GEP[0]` for payload, should be `GEP[1]`)
- **Issue:** `splitClauses` was generating `Field(selAcc, i)` for all ctor types; for `AdtCtor`, slot 0 is the tag, so `n` in `Some n -> n` bound to the tag value (1) not the payload (42)
- **Fix:** Added match on `selTag`: `AdtCtor _ -> List.init arity (fun i -> Field(selAcc, i + 1))`
- **Files modified:** `src/LangBackend.Compiler/MatchCompiler.fs`
- **Commit:** `29fffdb`

**2. [Rule 1 - Bug] parseProgram bypassing IndentFilter — multi-line inputs failing**
- **Found during:** Task 2 test creation (17-04 and 17-05 tests failed to compile)
- **Issue:** `Program.fs` was calling `Parser.parseModule Lexer.tokenize lexbuf` directly; raw `NEWLINE col` tokens are unknown to the parser grammar. All type declarations spanning multiple lines caused parse errors. Tests 17-01, 17-02, 17-03 were "passing by accident" — exit code 1 from parse error matched expected output "1".
- **Fix:** Added `lexAndFilter` helper (mirrors LangThree's `lexAndFilter`) and updated `parseProgram` to use filtered tokens via custom tokenizer function
- **Files modified:** `src/LangBackend.Cli/Program.fs`
- **Commit:** `29fffdb`

---

**Total deviations:** 2 auto-fixed bugs
**Impact on plan:** Both bugs were on the critical path; without fixing them no pattern match test could pass. No scope creep.

## Issues Encountered

- Previous session had temporary debug `eprintfn "=== MLIR ===" ...` in Program.fs — removed in this session as planned.
- `resolveAccessorTyped` had to become mutually recursive (`and`) with `resolveAccessor` to enable the Ptr-retype guard call (Task 1, committed `8c2c23c`).

## Next Phase Readiness

- Phase 17 complete: ADT construction (17-01) + pattern matching (17-02) both working, 51/51 tests pass
- Phase 18 (Records): `RecordPat` matching will use the same `resolveAccessor` mechanism; the Ptr-retype guard should handle record fields correctly already
- IndentFilter fix means all future multi-line `.lt` tests will parse correctly

---
*Phase: 17-adt-construction-pattern-matching*
*Completed: 2026-03-27*
