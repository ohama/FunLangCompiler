---
phase: 61-fnc-build-test
plan: 02
subsystem: testing
tags: [fsharp, cli, flt, e2e, fnc-build, fnc-test, bash, funproj-toml]

# Dependency graph
requires:
  - phase: 61-fnc-build-test/61-01
    provides: fnc build/test CLI subcommands, handleBuild/handleTest, compileFile helper
  - phase: 60-funproj-toml-parser
    provides: ProjectFile.findFunProj / parseFunProj / resolveTarget
provides:
  - E2E .flt tests for fnc build (full, specific target, no-funproj, bad-target)
  - E2E .flt test for fnc test (compile + run + PASS report)
  - 5 test files exercising TEST-02, TEST-03, ERR-01, ERR-03 scenarios
affects: [future fnc CLI changes, regression testing for project file support]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - ".flt bash test pattern: mktemp temp dir + symlink Prelude + write funproj.toml + write .fun + cd + dotnet run"
    - "Timing strip: pipe dotnet run output through sed 's/ ([0-9.]*s)//' for deterministic matching"
    - "CONTAINS: directive for error message matching (avoids exact match on variable suffixes)"
    - "ExitCode: directive for explicit exit code assertion in error tests"

key-files:
  created:
    - tests/compiler/61-01-fnc-build.flt
    - tests/compiler/61-02-fnc-test.flt
    - tests/compiler/61-03-build-specific.flt
    - tests/compiler/61-04-build-no-funproj.flt
    - tests/compiler/61-05-build-bad-target.flt
  modified: []

key-decisions:
  - "Use CONTAINS: directive for error messages instead of exact match — avoids brittleness on error message suffix changes"
  - "Symlink Prelude into temp dir to give test projects access to standard library"
  - "fnc test binary stdout ('all good') included in expected output — test runner captures all stdout including binary output"
  - "Strip timing with sed in command rather than using CHECK-RE: — keeps expected output section clean"

patterns-established:
  - "fnc build/test .flt pattern: temp dir + funproj.toml + .fun sources + cd + dotnet run -- build/test | sed timing-strip"

# Metrics
duration: 8min
completed: 2026-04-01
---

# Phase 61 Plan 02: FnC Build/Test E2E Tests Summary

**5 bash-based .flt E2E tests covering fnc build (full, specific-target, no-funproj, bad-target) and fnc test (compile+run+pass) — completing v17.0 test coverage**

## Performance

- **Duration:** ~8 min
- **Started:** 2026-04-01T12:05:42Z
- **Completed:** 2026-04-01T12:13:00Z
- **Tasks:** 2
- **Files created:** 5

## Accomplishments
- Created 5 .flt E2E tests that set up real temp project directories with funproj.toml and compile .fun sources via `dotnet run`
- TEST-02 satisfied: 61-01-fnc-build.flt verifies full build produces working binary ("OK: hello -> build/hello" + binary runs)
- TEST-03 satisfied: 61-02-fnc-test.flt verifies fnc test reports "PASS: unit" and "1/1 tests passed"
- ERR-01 satisfied: 61-04-build-no-funproj.flt verifies "funproj.toml not found" error and exit 1
- ERR-03 satisfied: 61-05-build-bad-target.flt verifies unknown target name error and exit 1
- CLI-02 satisfied: 61-03-build-specific.flt verifies `fnc build hello` compiles only hello, not goodbye

## Task Commits

Each task was committed atomically:

1. **Task 1: Create fnc build E2E tests** - `9e9d24b` (test)
2. **Task 2: Create fnc test E2E test** - `940f49f` (test)

**Plan metadata:** (pending docs commit)

## Files Created/Modified
- `tests/compiler/61-01-fnc-build.flt` - Full build E2E: builds hello target, runs binary, verifies OK line + binary output
- `tests/compiler/61-02-fnc-test.flt` - Test subcommand E2E: compiles test target, runs binary, verifies PASS + summary line
- `tests/compiler/61-03-build-specific.flt` - Specific target: builds hello only, verifies goodbye binary absent
- `tests/compiler/61-04-build-no-funproj.flt` - Error case: no funproj.toml → exit 1 with CONTAINS error message
- `tests/compiler/61-05-build-bad-target.flt` - Error case: unknown target name → exit 1 with CONTAINS error message

## Decisions Made
- Used `CONTAINS:` directive for error message tests — avoids brittleness if error message wording includes variable suffixes like "(searched from current directory upward)"
- Symlinked Prelude into each temp dir so test projects get full standard library access (same as real user projects)
- Discovered fnc test captures binary stdout in combined output — test_unit.fun prints "all good" which appears before "PASS: unit" line; expected output includes it
- Stripped timing with `sed 's/ ([0-9.]*s)//'` in command pipeline for deterministic matching rather than using CHECK-RE: (keeps output section clean)

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] fnc test expected output missing binary stdout**
- **Found during:** Task 2 (manual verification of 61-02-fnc-test.flt)
- **Issue:** Initial expected output had only "PASS: unit" and "1/1 tests passed" — but the test binary itself prints "all good" to stdout before the PASS line
- **Fix:** Added "all good" as first line in expected output section
- **Files modified:** tests/compiler/61-02-fnc-test.flt
- **Verification:** Manual run of command produced matching output
- **Committed in:** 940f49f (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (1 bug)
**Impact on plan:** Essential for correct test output matching. No scope creep.

## Issues Encountered
None beyond the binary stdout deviation documented above.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- v17.0 Project File support is fully tested: ProjectFile.fs (Phase 60) + CLI routing (Phase 61-01) + E2E tests (Phase 61-02)
- All 5 planned test scenarios (TEST-02, TEST-03, ERR-01, ERR-03, CLI-02) covered
- No blockers for future phases

---
*Phase: 61-fnc-build-test*
*Completed: 2026-04-01*
