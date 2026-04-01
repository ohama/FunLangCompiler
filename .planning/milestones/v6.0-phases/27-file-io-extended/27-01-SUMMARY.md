---
phase: 27-file-io-extended
plan: 01
subsystem: runtime
tags: [c-runtime, file-io, posix, gc_malloc, lang_throw, LangCons, LangString]

# Dependency graph
requires:
  - phase: 26-file-io-core
    provides: lang_runtime.c/h with LangString_s typedef, LangCons, lang_throw, and Phase 26 file I/O functions as templates
provides:
  - 8 new C runtime functions in lang_runtime.c for extended file I/O and system builtins
  - 8 new declarations in lang_runtime.h
  - POSIX includes unistd.h and dirent.h in lang_runtime.c
affects: [27-02-elaboration, any future phase using read_lines/write_lines/stdin/get_env/get_cwd/path_combine/dir_files]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "String list return: LangCons* linked list with cell->head = (int64_t)(uintptr_t)LangString* — same pointer-cast-to-i64 pattern as hashtable_keys"
    - "Forward list cursor technique: LangCons** cursor = &head; *cursor = cell; cursor = &cell->tail"
    - "Dynamic GC_malloc buffer: start at 256/1024 bytes, double on overflow using GC_malloc+memcpy (no realloc/free)"
    - "Unit-arg C functions called with empty argument list from elaboration side"

key-files:
  created: []
  modified:
    - src/FunLangCompiler.Compiler/lang_runtime.c
    - src/FunLangCompiler.Compiler/lang_runtime.h

key-decisions:
  - "lang_dir_files skips . and .. by name prefix check, skips non-regular files by d_type != DT_REG && d_type != DT_UNKNOWN (allows DT_UNKNOWN for filesystems that don't provide d_type)"
  - "lang_path_combine adds '/' separator only if dir doesn't already end with '/'"
  - "Dynamic buffer growth uses GC_malloc + memcpy instead of realloc — consistent with no-malloc/free rule"

patterns-established:
  - "Pattern 1 (String list return): LangCons* head using cursor technique, cell->head = (int64_t)(uintptr_t)LangString*"
  - "Pattern 2 (String list input): iterate LangCons*, cast head back via (LangString*)(uintptr_t)cur->head"
  - "Pattern 3 (unit-arg system call): C function takes void, elaboration discards unit value"
  - "Pattern 4 (one-arg string return with error): mirrors lang_file_read shape exactly"

# Metrics
duration: 2min
completed: 2026-03-27
---

# Phase 27 Plan 01: Extended File I/O C Runtime Summary

**8 new POSIX C runtime functions in lang_runtime.c for read_lines, write_lines, stdin_read_line/all, get_env, get_cwd, path_combine, and dir_files — all GC_malloc, all errors via lang_throw**

## Performance

- **Duration:** ~2 min
- **Started:** 2026-03-27T23:23:58Z
- **Completed:** 2026-03-27T23:25:37Z
- **Tasks:** 2
- **Files modified:** 2

## Accomplishments
- Added `#include <unistd.h>` and `#include <dirent.h>` to lang_runtime.c
- Implemented all 8 C functions following Phase 26 patterns exactly
- All functions use GC_malloc exclusively (no malloc/realloc/free)
- Error cases use lang_throw (catchable by try/with), not lang_failwith (exits)
- All pointer-to-int casts go through (uintptr_t) intermediate
- Project builds with 0 warnings, 0 errors

## Task Commits

Each task was committed atomically:

1. **Task 1: Add includes and 8 C functions to lang_runtime.c** - `ed877bd` (feat)
2. **Task 2: Add declarations to lang_runtime.h** - `80f4c10` (feat)

## Files Created/Modified
- `src/FunLangCompiler.Compiler/lang_runtime.c` - Added 2 POSIX includes and 8 new C functions (~208 lines)
- `src/FunLangCompiler.Compiler/lang_runtime.h` - Added 8 function declarations before #endif guard

## Decisions Made
- `lang_dir_files` allows `DT_UNKNOWN` in addition to `DT_REG` to handle filesystems (e.g., some Linux ext4 mounts) that don't populate `d_type` — prevents silently empty results on those systems
- Dynamic buffer growth for stdin functions uses `GC_malloc` + `memcpy` loop instead of `realloc`, consistent with the project-wide no-malloc/free rule
- `lang_path_combine` checks `dir->data[dir->length - 1] != '/'` before adding separator, matching .NET `Path.Combine` semantics

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
None.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- All 8 C functions are compiled and available for linking
- lang_runtime.h declares all 8 for use by any consumer
- Ready for Phase 27 Plan 02: Elaboration.fs registration (externalFuncs lists + elaborateExpr arms)
- No blockers

---
*Phase: 27-file-io-extended*
*Completed: 2026-03-27*
