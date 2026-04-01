# Phase 51: AST Structure Sync - Research

**Researched:** 2026-04-01
**Domain:** F# discriminated union pattern matching — syncing LangBackend.Compiler patterns to updated LangThree AST
**Confidence:** HIGH

## Summary

Phase 51 is a mechanical pattern-match update. The LangThree AST gained three new fields across v12.0 development:
`TypeDecl` gained a `deriving: string list` field (5th field before Span), `TypeClassDecl` gained a `superclasses: string list` field (4th field before Span), `InstanceDecl` gained a `constraints: (string * TypeExpr) list` field (4th field before Span), and `DerivingDecl` was added as a new `Decl` case.

The build is currently broken with exactly **1 compile error**: `Elaboration.fs(4073,30): FS0727: This union case expects 5 arguments in tupled form, but was given 4`. This is the only blocking error. There are no `TypeClassDecl`, `InstanceDecl`, or `DerivingDecl` references in Elaboration.fs currently — those are entirely absent and thus cause no compile errors (they just need to be handled in the `_ -> ()` catch-all in `prePassDecls`).

The fix scope is minimal: one pattern in `prePassDecls` (line 4073) needs updating. The new `TypeClassDecl`, `InstanceDecl`, and `DerivingDecl` cases are already handled by the `| _ -> ()` wildcard in `prePassDecls` and the `| _ -> [d]` wildcard in `flattenDecls`. No other files reference these types.

**Primary recommendation:** Update the single `TypeDecl` pattern in `prePassDecls` from 4-field to 5-field, then verify the build passes. Add `| Ast.Decl.TypeClassDecl _ | Ast.Decl.InstanceDecl _ | Ast.Decl.DerivingDecl _ -> ()` explicit cases to `prePassDecls` to eliminate any future incomplete-match warnings.

## Standard Stack

This is an internal codebase change — no external libraries are involved.

### Core
| File | Purpose | What Changes |
|------|---------|--------------|
| `src/LangBackend.Compiler/Elaboration.fs` | Main compiler elaboration pass | 1 pattern match site to update |
| `/Users/ohama/vibe-coding/LangThree/src/LangThree/Ast.fs` | Source of truth for AST types | Read-only reference |

### Supporting
| Tool | Purpose |
|------|---------|
| `dotnet build src/LangBackend.Compiler/` | Verify fix compiles |
| `tests/compiler/*.flt` | E2E regression tests |

### Alternatives Considered
N/A — this is a targeted fix with no design choices.

## Architecture Patterns

### Current LangThree AST (as of 2026-04-01, after v12.0)

The authoritative definitions in `/Users/ohama/vibe-coding/LangThree/src/LangThree/Ast.fs`:

```fsharp
// TypeDecl — 5 fields (name, typeParams, constructors, deriving, Span)
and TypeDecl =
    | TypeDecl of name: string * typeParams: string list * constructors: ConstructorDecl list * deriving: string list * Span

// Decl cases for typeclasses (in Ast.Decl DU):
| TypeClassDecl of className: string * typeVar: string * methods: (string * TypeExpr) list * superclasses: string list * Span
| InstanceDecl of className: string * instanceType: TypeExpr * methods: (string * Expr) list * constraints: (string * TypeExpr) list * Span
| DerivingDecl of typeName: string * classNames: string list * Span
```

### Current Elaboration.fs Pattern (broken — 4-field)

```fsharp
// Elaboration.fs line 4073 — CURRENT (broken):
| Ast.Decl.TypeDecl (Ast.TypeDecl(_, _, ctors, _)) ->
```

### Correct Pattern After Fix (5-field)

```fsharp
// Elaboration.fs line 4073 — FIXED:
| Ast.Decl.TypeDecl (Ast.TypeDecl(_, _, ctors, _, _)) ->
```

The 4th wildcard captures `deriving: string list`, the 5th captures `Span`.

### Recommended Project Structure

No structural changes needed. Only Elaboration.fs requires modification.

### Pattern 1: Wildcard-Skip for New Decl Cases

**What:** Add explicit wildcard arms for new Decl types so the match is exhaustive and no warnings arise.
**When to use:** Any `match decl with` block in `prePassDecls` that uses `| _ -> ()`.
**Example:**

```fsharp
// Source: Elaboration.fs prePassDecls
| Ast.Decl.TypeClassDecl _ -> ()   // Phase 71: typeclasses skipped in pre-pass
| Ast.Decl.InstanceDecl   _ -> ()  // Phase 71: instances skipped in pre-pass
| Ast.Decl.DerivingDecl   _ -> ()  // v12.0: deriving skipped in pre-pass
| _ -> ()
```

Adding explicit arms is optional (the existing `| _ -> ()` already handles them) but is good practice to document intent.

### Anti-Patterns to Avoid

- **Matching on TypeClassDecl/InstanceDecl fields in prePassDecls:** Phase 51 only skips these nodes. Full elaboration is Phase 52.
- **Modifying LangThree:** The constraint is absolute — LangThree is under parallel development and must not be touched.
- **Using `| _ ->` without explicit new-type arms in extractMainExpr/flattenDecls:** These already correctly fall through to `| _ -> false` and `| _ -> [d]` respectively. TypeClassDecl/InstanceDecl/DerivingDecl nodes will be ignored, which is correct for Phase 51.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Pattern exhaustiveness | Manual tracking of cases | F# compiler warnings (FS0025) | Compiler tells you what's missing |
| AST field counts | Manual counting | Read Ast.fs directly | Source of truth is in LangThree/src/LangThree/Ast.fs |

**Key insight:** The F# compiler error FS0727 ("expects N arguments") is the complete specification for what needs changing. There is no guesswork.

## Common Pitfalls

### Pitfall 1: Fixing TypeDecl but missing the field order
**What goes wrong:** `TypeDecl(_, _, ctors, _, _)` captures fields correctly but a wrong position for `ctors`.
**Why it happens:** Confusion about which wildcard position corresponds to which field.
**How to avoid:** The new signature is `TypeDecl of name * typeParams * constructors * deriving * Span`. Position 3 (index 2) is `constructors`. Pattern `TypeDecl(_, _, ctors, _, _)` is correct.
**Warning signs:** Build succeeds but `ctors` is empty list — would indicate wrong field captured.

### Pitfall 2: Incomplete match warning on Decl after adding new cases
**What goes wrong:** F# emits FS0025 (incomplete pattern match) for `prePassDecls` if the new Decl cases aren't covered.
**Why it happens:** `TypeClassDecl`, `InstanceDecl`, `DerivingDecl` are new cases not covered by existing arms.
**How to avoid:** The existing `| _ -> ()` wildcard already covers them. No change needed unless you want explicit documentation arms.
**Warning signs:** Compiler warning FS0025 — "incomplete pattern matches".

### Pitfall 3: Expecting flattenDecls or extractMainExpr to need changes
**What goes wrong:** Developer modifies `flattenDecls` to explicitly drop TypeClassDecl nodes.
**Why it happens:** Wanting to be explicit about intent.
**How to avoid:** `flattenDecls` already uses `| _ -> [d]` — it passes TypeClassDecl/InstanceDecl/DerivingDecl through unchanged. `extractMainExpr` filters to only LetDecl/LetRecDecl/LetMutDecl/LetPatDecl, so typeclass decls are naturally filtered out. No changes needed in either function for Phase 51.

### Pitfall 4: Verifying against wrong AST
**What goes wrong:** Looking at `LangBackend.bak/src/FunLang.Compiler/Ast.fs` instead of LangThree.
**Why it happens:** Glob finds the backup copy.
**How to avoid:** Always use `/Users/ohama/vibe-coding/LangThree/src/LangThree/Ast.fs` as the authoritative source.

## Code Examples

### Complete Fix for prePassDecls

```fsharp
// Source: Elaboration.fs, prePassDecls function (around line 4073)
// BEFORE (broken — TypeDecl has 5 fields now, not 4):
| Ast.Decl.TypeDecl (Ast.TypeDecl(_, _, ctors, _)) ->

// AFTER (correct — 5 fields: name, typeParams, ctors, deriving, span):
| Ast.Decl.TypeDecl (Ast.TypeDecl(_, _, ctors, _, _)) ->
```

No other changes are needed in Elaboration.fs for the build to succeed.

### Optional: Explicit new Decl arms (for documentation clarity)

```fsharp
// In prePassDecls match block, these can be added before | _ -> ()
// to make the skip intent explicit (Phase 52 will handle them):
| Ast.Decl.TypeClassDecl _ -> ()
| Ast.Decl.InstanceDecl   _ -> ()
| Ast.Decl.DerivingDecl   _ -> ()
```

## State of the Art

| Old Shape | New Shape | When Changed | Impact |
|-----------|-----------|--------------|--------|
| `TypeDecl(name, typeParams, ctors, span)` | `TypeDecl(name, typeParams, ctors, deriving, span)` | LangThree v12.0 (2026-04-01) | 4-field patterns now fail FS0727 |
| `TypeClassDecl(className, typeVar, methods, span)` | `TypeClassDecl(className, typeVar, methods, superclasses, span)` | LangThree v12.0 (2026-04-01) | Any pattern would fail if used |
| `InstanceDecl(className, instanceType, methods, span)` | `InstanceDecl(className, instanceType, methods, constraints, span)` | LangThree v12.0 (2026-04-01) | Any pattern would fail if used |
| N/A | `DerivingDecl(typeName, classNames, span)` | LangThree v12.0 (2026-04-01) | New case; needs explicit arm or covered by wildcard |

**Deprecated/outdated:**
- `TypeDecl(_, _, ctors, _)`: 4-field pattern is invalid after LangThree v12.0

## Open Questions

1. **Should TypeClassDecl/InstanceDecl be explicitly listed in prePassDecls?**
   - What we know: The existing `| _ -> ()` wildcard covers them.
   - What's unclear: Whether the planner wants explicit arms for Phase 51 or defers that to Phase 52.
   - Recommendation: Add explicit arms in Phase 51 to document intent and prevent future FS0025 surprises.

2. **Are there any other match sites in the codebase that pattern-match on TypeDecl?**
   - What we know: `grep` across all LangBackend `.fs` files found only one site: `Elaboration.fs:4073`. Program.fs has no AST pattern matches on Decl subtypes.
   - What's unclear: Nothing — this was verified comprehensively.
   - Recommendation: Confident that line 4073 is the only change needed.

## Sources

### Primary (HIGH confidence)
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Ast.fs` — authoritative AST type definitions, read directly
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/Elaboration.fs` — all pattern match sites, read directly
- `dotnet build` output — confirms exactly 1 error (FS0727 at line 4073) and its cause

### Secondary (MEDIUM confidence)
- `git log` on LangThree — confirmed TypeDecl got `deriving` field in v12.0 commit `7c3fc0f`, InstanceDecl got `constraints` field in commit `163929a`

## Metadata

**Confidence breakdown:**
- Exact change needed: HIGH — compiler error message specifies file, line, and field count mismatch
- No other files affected: HIGH — grep across entire codebase found 0 other sites
- TypeClassDecl/InstanceDecl/DerivingDecl currently absent from Elaboration.fs: HIGH — verified by grep

**Research date:** 2026-04-01
**Valid until:** Valid as long as LangThree AST doesn't change again (stable for Phase 51 scope)
