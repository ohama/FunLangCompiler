---
phase: 06-cli
plan: "01"
subsystem: cli
tags: [fsharp, cli, fslit, compiler, driver]

# Dependency graph
requires:
  - phase: 05-closures-via-elaboration
    provides: complete Elaboration pipeline (closures, let-rec, scalar, booleans)
  - phase: 01-mlirir-foundation
    provides: Pipeline.compile and CLI scaffolding
provides:
  - CLI auto-naming: langbackend file.lt produces file binary (strips .lt extension)
  - File existence check with readable error message
  - Parse error catch/print via try/with wrapper
  - FsLit E2E tests 06-01 and 06-02 for CLI file input and error handling
  - Full regression suite 15/15 green
affects: []

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Path.GetFileNameWithoutExtension for auto-deriving binary name from .lt input path"
    - "try/with around full pipeline to catch parser exceptions"
    - "FsLit bash -c command copies %input to .lt file to test auto-naming"

key-files:
  created:
    - tests/compiler/06-01-cli-file-input.flt
    - tests/compiler/06-02-cli-error.flt
  modified:
    - src/LangBackend.Cli/Program.fs

key-decisions:
  - "-o flag becomes optional: when absent, output name derived via Path.GetFileNameWithoutExtension(inputPath)"
  - "File.Exists check before File.ReadAllText gives readable error instead of FileNotFoundException"
  - "try/with wraps parse/elaborate/compile to surface parse errors as Error: <message> instead of stack trace"
  - "FsLit auto-naming test uses 'cd /tmp && cp %input ${OUTNAME}.lt' pattern to test .lt extension stripping"

patterns-established:
  - "FsLit bash -c 'cd /tmp && ...' pattern for testing CWD-relative binary creation"
  - "Fixed nonexistent path /tmp/langback_nonexistent_file.lt for deterministic error test output"

# Metrics
duration: 2min
completed: 2026-03-26
---

# Phase 6 Plan 1: CLI File Input and Error Handling Summary

**`langbackend file.lt` auto-derives output binary name by stripping .lt extension, with file-not-found and parse-error handling; 15/15 FsLit tests green**

## Performance

- **Duration:** 2 min
- **Started:** 2026-03-26T03:57:27Z
- **Completed:** 2026-03-26T04:00:21Z
- **Tasks:** 2
- **Files modified:** 3

## Accomplishments

- CLI now accepts `langbackend file.lt` without requiring `-o`; output binary named after file (minus `.lt` extension)
- File existence check: prints `Error: file not found: <path>` to stderr and exits 1 before attempting to read
- Parse/elaborate/compile wrapped in `try/with`; exceptions print `Error: <message>` instead of stack traces
- Two new FsLit E2E tests validate auto-naming and error handling; full suite 15/15 pass

## Task Commits

1. **Task 1: Update CLI argument parsing and error handling** - `2208528` (feat)
2. **Task 2: FsLit E2E tests for CLI file input and error handling** - `ead2ed5` (feat)

## Files Created/Modified

- `src/LangBackend.Cli/Program.fs` - Rewritten main: optional -o, auto-naming, file existence check, try/with
- `tests/compiler/06-01-cli-file-input.flt` - E2E test: let expression via auto-named binary outputs 15
- `tests/compiler/06-02-cli-error.flt` - E2E test: nonexistent file prints readable error and exits 1

## Decisions Made

- `-o` flag is now optional. When absent, `Path.GetFileNameWithoutExtension(inputPath)` gives the binary name. This strips both the `.lt` extension and any directory prefix, placing the binary in CWD.
- `File.Exists` check added before `File.ReadAllText` to give a readable error rather than letting FileNotFoundException propagate with an opaque .NET message.
- `try/with ex -> eprintfn "Error: %s" ex.Message` wraps the entire parse/elaborate/compile pipeline so parser exceptions surface cleanly.
- FsLit test for auto-naming uses `cd /tmp && cp %input ${OUTNAME}.lt` to exercise the extension-stripping code path; the binary is created in `/tmp` and run there.
- Error test uses fixed path `/tmp/langback_nonexistent_file.lt` (not PID-based) for deterministic output matching.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

Phase 6 is complete. All 6 phases of the LangBackend compiler are done:
- Phase 1: MlirIR Foundation + CLI scaffolding
- Phase 2: Scalar codegen via Elaboration
- Phase 3: Booleans, comparisons, control flow
- Phase 4: Known functions via Elaboration (let-rec)
- Phase 5: Closures via Elaboration
- Phase 6: CLI driver with auto-naming and error handling

The compiler is a complete usable tool: `langbackend file.lt` compiles a LangThree source file to a native binary.

---
*Phase: 06-cli*
*Completed: 2026-03-26*
