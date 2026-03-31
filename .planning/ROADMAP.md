# Roadmap: LangBackend v11.0 — Compiler Error Messages

## Overview

Transform compiler error messages from opaque failures into actionable diagnostics with source locations, preserved parser/MLIR errors, contextual hints, and categorized output. Three phases: build the location infrastructure, preserve currently-lost error information, then enrich messages with context and unified formatting.

## Milestones

- v1.0-v10.0: Shipped (Phases 1-42, archived)
- Phase 43: Uncommitted work (stripAnnot, BoolVars, mutual recursion, sanitizeMlirName)
- v11.0 Compiler Error Messages: Phases 44-46 (current)

## Phases

- [ ] **Phase 44: Error Location Foundation** - failWithSpan helper + source locations in all Elaboration/pattern errors
- [ ] **Phase 45: Error Preservation** - Parser fallback error retention + MLIR debug file preservation
- [ ] **Phase 46: Context Hints & Unified Format** - Record/field/function hints + categorized error output

## Phase Details

### Phase 44: Error Location Foundation
**Goal**: Every Elaboration error message includes file:line:col source location
**Depends on**: Nothing (first phase of v11.0)
**Requirements**: LOC-01, LOC-02, LOC-03
**Success Criteria** (what must be TRUE):
  1. `failWithSpan` helper exists and converts Span to "file:line:col: message" format
  2. Unbound variable error shows the file name, line number, and column where it occurred
  3. Pattern matching errors (unsupported pattern, ConsPat) show source location
  4. All ~15 failwithf sites in Elaboration.fs use failWithSpan instead of bare failwithf
**Plans**: TBD

Plans:
- [ ] 44-01: failWithSpan helper + Elaboration error site migration
- [ ] 44-02: Pattern matching error locations

### Phase 45: Error Preservation
**Goal**: Error information that is currently lost (parser fallback, MLIR temp files) is preserved and surfaced
**Depends on**: Phase 44
**Requirements**: PARSE-01, PARSE-02, MLIR-01, MLIR-02
**Success Criteria** (what must be TRUE):
  1. When parser falls back from one grammar to another, the original parse error message is shown (not swallowed)
  2. Parser error messages include line:col position information
  3. When mlir-opt or mlir-translate fails, the .mlir temp file is NOT deleted
  4. The error message includes the path to the preserved .mlir file so the user can inspect it
**Plans**: TBD

Plans:
- [ ] 45-01: Parser error preservation with position
- [ ] 45-02: MLIR debug file preservation

### Phase 46: Context Hints & Unified Format
**Goal**: Error messages include actionable hints (available types/fields/functions) and follow a consistent categorized format
**Depends on**: Phase 44, Phase 45
**Requirements**: CTX-01, CTX-02, CTX-03, CAT-01, CAT-02
**Success Criteria** (what must be TRUE):
  1. Record type resolution failure lists all available record types in the error message
  2. Field access error on a record lists the valid fields for that record type
  3. Unresolved function call error lists in-scope functions as hints
  4. Every error message is prefixed with its phase: [Parse], [Elaboration], or [Compile]
  5. All error messages follow the format `[phase] file:line:col: message`
**Plans**: TBD

Plans:
- [ ] 46-01: Record/field/function context hints
- [ ] 46-02: Error categorization and unified format

## Progress

| Phase | Plans Complete | Status | Completed |
|-------|----------------|--------|-----------|
| 44. Error Location Foundation | 0/2 | Not started | - |
| 45. Error Preservation | 0/2 | Not started | - |
| 46. Context Hints & Unified Format | 0/2 | Not started | - |
