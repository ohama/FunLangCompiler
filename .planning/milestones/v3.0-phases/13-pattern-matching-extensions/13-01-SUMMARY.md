---
phase: 13-pattern-matching-extensions
plan: 01
subsystem: compiler
tags: [pattern-matching, fsharp, mlir, decision-tree, when-guard, orpat, charconst]

# Dependency graph
requires:
  - phase: 11-match-compiler
    provides: Jules Jacobs decision tree, MatchCompiler.compile, emitDecisionTree, Leaf/Switch nodes
  - phase: 12-missing-operators
    provides: App(Lambda) inlining, bare Lambda closure, PipeRight/ComposeRight/ComposeLeft desugaring
provides:
  - PAT-08: ConstPat(CharConst) maps to IntLit(int c) in MatchCompiler — char patterns now work
  - PAT-07: OrPat expansion in Elaboration.fs before MatchCompiler.compile — or-patterns now work
  - PAT-06: Guard node in DecisionTree + HasGuard in Clause + Guard case in emitDecisionTree — when-guards now work
  - 3 new E2E tests: 13-01-when-guard.flt, 13-02-orpat.flt, 13-03-char-pattern.flt
affects: [future pattern matching phases, type system phases]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "PAT-08 trivial reuse: CharConst → IntLit(int c) reuses existing integer equality test path"
    - "PAT-07 preprocessing: OrPat expansion happens in Elaboration.fs before MatchCompiler, no changes to decision tree"
    - "PAT-06 Guard node: DecisionTree.Guard carries bindings+bodyIdx+ifFalse; genMatch emits Guard when HasGuard; emitDecisionTree handles Guard like Leaf with conditional branch"

key-files:
  created:
    - tests/compiler/13-01-when-guard.flt
    - tests/compiler/13-02-orpat.flt
    - tests/compiler/13-03-char-pattern.flt
  modified:
    - src/LangBackend.Compiler/MatchCompiler.fs
    - src/LangBackend.Compiler/Elaboration.fs

key-decisions:
  - "OrPat expansion in Elaboration.fs (not MatchCompiler) — cleaner, MatchCompiler.desugarPattern failwith becomes unreachable safety net"
  - "Guard node carries bindings to avoid re-resolving in emitDecisionTree — mirrors Leaf structure with added conditional branch"
  - "compile signature changes from (Pattern * int) list to (Pattern * bool * int) list — minimal API change to thread hasGuard"
  - "splitClauses propagates HasGuard to expanded clauses — necessary for guards inside constructor sub-patterns"

patterns-established:
  - "DecisionTree extensions: add new DU case, handle in emitDecisionTree, update genMatch leaf case"
  - "Clause metadata pattern: add bool fields to Clause to control genMatch behavior at leaf"

# Metrics
duration: 12min
completed: 2026-03-26
---

# Phase 13 Plan 01: Pattern Matching Extensions Summary

**when-guard (PAT-06), OrPat (PAT-07), and CharConst (PAT-08) patterns implemented via Guard DecisionTree node, Elaboration.fs preprocessing, and IntLit remapping**

## Performance

- **Duration:** 12 min
- **Started:** 2026-03-26T08:24:48Z
- **Completed:** 2026-03-26T08:37:03Z
- **Tasks:** 7
- **Files modified:** 5

## Accomplishments
- PAT-08 (trivial): `ConstPat(CharConst c)` → `CtorTest(IntLit(int c), [])` — one line, reuses integer equality test path
- PAT-07: `expandOrPats` inline in Elaboration.fs Match handler — `OrPat([p1;p2;p3], guard, body)` expands to 3 separate clauses before MatchCompiler.compile
- PAT-06: `DecisionTree.Guard` node + `Clause.HasGuard` + `compile` signature change to `(Pattern * bool * int) list` + `emitDecisionTree Guard` case with `cf.cond_br` to body or guard_fail block
- 37/37 E2E tests pass (34 pre-existing + 3 new)

## Task Commits

Each task was committed atomically:

1. **Task 1-2: PAT-08 CharConst + Guard infrastructure in MatchCompiler** - `7443806` (feat)
2. **Task 3-4: PAT-06 Guard + PAT-07 OrPat expansion in Elaboration.fs** - `69a3c16` (feat)
3. **Task 5-7: E2E test files** - `e68fb1a` (test)
4. **Fix: .flt format headers** - `3b3883e` (test)

## Files Created/Modified
- `src/LangBackend.Compiler/MatchCompiler.fs` - Clause.HasGuard, DecisionTree.Guard, genMatch Guard leaf, splitClauses HasGuard propagation, compile (Pattern * bool * int) list, ConstPat CharConst → IntLit
- `src/LangBackend.Compiler/Elaboration.fs` - expandOrPats inline, arms 3-tuple, emitDecisionTree Guard case
- `tests/compiler/13-01-when-guard.flt` - E2E: `match 5 with | n when n > 0 -> 1 | _ -> 0` exits 1
- `tests/compiler/13-02-orpat.flt` - E2E: `match 3 with | 1 | 2 | 3 -> 10 | _ -> 0` exits 10
- `tests/compiler/13-03-char-pattern.flt` - E2E: `match 'A' with | 'A' -> 1 | _ -> 0` exits 1

## Decisions Made
- OrPat expansion in Elaboration.fs before MatchCompiler — keeps MatchCompiler clean, the `failwith "OrPat not yet supported"` stub becomes unreachable
- Guard node carries `bindings` (same as Leaf) so emitDecisionTree doesn't need a separate binding pass
- `compile` API takes `(Pattern * bool * int) list` — cleaner than a separate `compileWithGuards` function
- `splitClauses` propagates `HasGuard` to expanded clauses in case (a) — necessary for guards on constructor patterns

## Deviations from Plan

None — plan executed exactly as written.

## Issues Encountered
- Initial test files lacked fslit `// --- Command:` / `// --- Input:` / `// --- Output:` headers. Fixed before verification — tests ran correctly with proper format.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- PAT-06, PAT-07, PAT-08 complete — pattern matching is now comprehensive for basic use cases
- 37 E2E tests all passing
- Ready for next v3.0 phase (list operations, string operations, or further pattern extensions)

---
*Phase: 13-pattern-matching-extensions*
*Completed: 2026-03-26*
