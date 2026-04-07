---
phase: 92-c-boundary-simplification
plan: 01
subsystem: compiler-runtime-boundary
tags: [tagged-int, c-runtime, elaboration, untag, retag]
dependency-graph:
  requires: [88-tagged-int, 90-hashtable-unification]
  provides: [simplified-c-boundary-calls, c-internal-untag-retag]
  affects: [92-02-structural-sites]
tech-stack:
  added: []
  patterns: [c-internal-untag, string_sub_raw-helper]
file-tracking:
  key-files:
    created: []
    modified:
      - src/FunLangCompiler.Compiler/lang_runtime.c
      - src/FunLangCompiler.Compiler/Elaboration.fs
      - src/FunLangCompiler.Compiler/ElabHelpers.fs
decisions:
  - id: string-sub-raw-helper
    choice: "Extract string_sub_raw static helper to avoid double-untag when lang_string_slice calls lang_string_sub"
    reason: "Both functions now untag inputs; internal call needs raw args"
metrics:
  duration: "~13 min"
  completed: "2026-04-07"
---

# Phase 92 Plan 01: C Boundary Simplification (Simple Function Groups) Summary

**One-liner:** Move LANG_UNTAG_INT/LANG_TAG_INT into 24 C runtime functions, removing ~27 emitUntag/emitRetag compiler sites

## What Was Done

### Task 1: Char + to_string + sprintf functions
- 6 char functions: added internal LANG_UNTAG_INT on input (predicates keep raw 0/1 return)
- char_to_upper/lower: untag input + tag output internally
- lang_to_string_int/bool: untag input internally
- 4 sprintf wrappers (1i, 2ii, 2si, 2is): untag int args internally
- Removed emitUntag from emitCharPredicate and coerceToI64Arg I64 case
- Removed emitUntag/emitRetag from show, to_string, dbg, char_to_upper, char_to_lower
- Added emitRetag for I1 zext path in show/to_string (C now expects tagged bool)

### Task 2: String/collection/mutablelist functions
- string_sub: untag start/len internally
- string_slice: untag start/stop internally (UNTAG(-1) = -1 preserves sentinel)
- string_char_at: untag index + tag result internally
- string_indexof: return tagged result
- string_to_int: return tagged result
- hashset_count, queue_count, mlist_count: return tagged result
- mlist_get/set: untag index internally
- Removed emitUntag/emitRetag from 10 Elaboration.fs call sites

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing functionality] Extracted string_sub_raw static helper**
- **Found during:** Task 2
- **Issue:** lang_string_slice calls lang_string_sub; after both got internal UNTAG, the arguments were double-untagged
- **Fix:** Extracted static `string_sub_raw()` helper that takes raw args; both `lang_string_sub` and `lang_string_slice` untag then call `string_sub_raw`
- **Files modified:** lang_runtime.c
- **Commit:** e12c915

## Remaining emitUntag/emitRetag Sites

After Phase 92-01, these sites remain (20 in Elaboration.fs, 3 in ElabHelpers.fs):

| Category | Count | Sites |
|----------|-------|-------|
| Arithmetic (mul/div/mod) | 9 | Lines 42-61 - must keep |
| I1 bool retag (show/to_string) | 2 | Lines 963, 1034 - newly added, correct |
| Inline GEP retag (string_length, array_length, hashtable_count) | 3 | Lines 1056, 1151, 1300 - structural |
| Array set/get GEP untag | 2 | Lines 1090, 1112 - structural |
| Array create/init untag | 2 | Lines 1136, 1343 - structural |
| IndexGet/Set dispatch untag | 2 | Lines 1214, 1236 - structural |
| ElabHelpers definitions | 2 | Lines 166, 172 - definitions |
| coerceToI64 I1 retag | 1 | Line 378 - type coercion |

## Commits

| Hash | Description |
|------|-------------|
| 0403173 | feat(92-01): move untag into C char/to_string/sprintf functions |
| e12c915 | feat(92-01): move untag into C string/collection/mutablelist functions |

## Test Results

257/257 E2E tests passing.
