# Roadmap: LangBackend — LangThree Compiler (F#, MLIR, LLVM)

## Milestones

- v1.0-v10.0: Shipped (Phases 1-42, archived)
- v11.0 Compiler Error Messages: Phases 44-46 (complete 2026-03-31)
- v13.0 LangThree Typeclass Sync: Phases 51-53 (current)

## Phases

<details>
<summary>v1.0-v12.0 (Phases 1-50) - SHIPPED 2026-03-31</summary>

Phases 1-42: Core compiler pipeline (v1.0-v10.0)
Phase 43: Uncommitted work (stripAnnot, BoolVars, mutual recursion, sanitizeMlirName)
Phases 44-46: Compiler error messages (v11.0)
Phases 47-50: v12.0 (archived)

</details>

### v13.0 LangThree Typeclass Sync (In Progress)

**Milestone Goal:** LangBackend compiles LangThree source that uses typeclasses by syncing with LangThree v10.0-v12.0 AST changes and implementing typeclass elaboration.

#### Phase 51: AST Structure Sync
**Goal**: LangBackend Elaboration.fs compiles without errors against the updated LangThree AST (TypeDecl 5-field, TypeClassDecl with superclasses, InstanceDecl with constraints, DerivingDecl)
**Depends on**: Nothing (first phase of v13.0)
**Requirements**: AST-01, AST-02, AST-03, AST-04, AST-05
**Success Criteria** (what must be TRUE):
  1. Elaboration.fs pattern matches on TypeDecl with 5 fields (name, typeParams, ctors, deriving, span) without compiler warning or error
  2. Elaboration.fs pattern matches on TypeClassDecl with superclasses field without compiler warning or error
  3. Elaboration.fs pattern matches on InstanceDecl with constraints field without compiler warning or error
  4. DerivingDecl is handled (skipped/ignored) rather than causing a match-incomplete warning
  5. All existing E2E tests (tests/compiler/) that passed before continue to pass
**Plans**: 1 plan

Plans:
- [ ] 51-01-PLAN.md — Update TypeDecl to 5-field pattern, add explicit skip arms for TypeClassDecl/InstanceDecl/DerivingDecl

#### Phase 52: Typeclass Elaboration
**Goal**: LangBackend can elaborate programs that contain typeclass declarations and instances by running elaborateTypeclasses before elaborateProgram
**Depends on**: Phase 51
**Requirements**: TC-01, TC-02, TC-03, TC-04, TC-05
**Success Criteria** (what must be TRUE):
  1. elaborateTypeclasses removes TypeClassDecl nodes from the declaration list so elaborateProgram never sees them
  2. elaborateTypeclasses converts each InstanceDecl method into a LetDecl binding with a mangled name (e.g., `show_Int`)
  3. elaborateTypeclasses removes DerivingDecl nodes from the declaration list
  4. elaborateTypeclasses recurses into ModuleDecl and NamespacedModule so nested typeclasses are handled
  5. The compiler pipeline calls elaborateTypeclasses after parseProgram and before elaborateProgram
**Plans**: 1 plan

Plans:
- [ ] 52-01-PLAN.md — Implement elaborateTypeclasses and wire into compiler pipeline

#### Phase 53: Prelude Sync & E2E Tests
**Goal**: LangThree programs using typeclass show/eq/deriving compile and produce correct output
**Depends on**: Phase 52
**Requirements**: PRE-01, PRE-02, TEST-01, TEST-02, TEST-03
**Success Criteria** (what must be TRUE):
  1. Prelude/Typeclass.fun exists in LangBackend and matches the LangThree copy
  2. The CLI loads Typeclass.fun as part of the standard Prelude sequence
  3. A program calling `show` on an Int/String compiles and prints the correct string representation
  4. A program calling `eq` on two values compiles and returns the correct boolean
  5. A program using `deriving Show` compiles and the auto-generated show function produces correct output
**Plans**: 1 plan

Plans:
- [ ] 53-01-PLAN.md — Copy Typeclass.fun, enhance DerivingDecl expansion, update CLI loader, add E2E tests

## Progress

| Phase | Milestone | Plans Complete | Status | Completed |
|-------|-----------|----------------|--------|-----------|
| 44. Error Location Foundation | v11.0 | 2/2 | Complete | 2026-03-31 |
| 45. Error Preservation | v11.0 | 1/1 | Complete | 2026-03-31 |
| 46. Context Hints & Unified Format | v11.0 | 1/1 | Complete | 2026-03-31 |
| 51. AST Structure Sync | v13.0 | 1/1 | ✓ Complete | 2026-04-01 |
| 52. Typeclass Elaboration | v13.0 | 1/1 | ✓ Complete | 2026-04-01 |
| 53. Prelude Sync & E2E Tests | v13.0 | 1/1 | ✓ Complete | 2026-04-01 |
