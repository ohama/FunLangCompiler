# Phase 49: Error Tests CHECK-RE - Research

**Researched:** 2026-04-01
**Domain:** fslit test framework CHECK-RE, .NET regex, error test migration
**Confidence:** HIGH

## Summary

Phase 49 migrates 8 error test files (44-*, 46-*) from exact-match output to CHECK-RE
regex matching. This makes error tests independent of Prelude line count changes: since
Phase 47 made user code start at line 1 regardless of Prelude size, line numbers in error
output are already stable. However, exact-match tests will break if Prelude ever changes
the `In scope:` list (46-03), adds/removes fields in ADT error messages (44-02), or if
filename paths change between cd-based and absolute-path commands.

The two tests already using CHECK-RE (45-01, 46-05) serve as the pattern to follow. The
45-02 test uses exact `[Parse] parse error` with no position — this is intentionally
correct behavior (parseExpr fallback succeeds on first decl), leave it unchanged.

**Primary recommendation:** Convert 8 exact-match tests to CHECK-RE, preserving
verification of `[Elaboration]` category, filename, line:col, and the essential error
message keyword. Use `.+` or `.*` for variable-length content like the `In scope:` list
and verbose AST dumps.

## Standard Stack

### Core

| Tool | Version | Purpose | Why Standard |
|------|---------|---------|--------------|
| fslit | local binary | Test runner for .flt files | Project's established test tool |
| .NET Regex | .NET runtime | CHECK-RE pattern engine | fslit uses .NET regex internally |

### Supporting

| Tool | Version | Purpose | When to Use |
|------|---------|---------|-------------|
| fslit `--verbose` flag | - | Shows actual vs expected on failure | Debugging CHECK-RE patterns during authoring |
| fslit `--filter` flag | - | Run single test by glob | Rapid iteration on one test at a time |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| CHECK-RE | CONTAINS | CONTAINS scans forward for substring but doesn't anchor to a line; CHECK-RE is cleaner for structured output |
| CHECK-RE | exact match | Exact match breaks on any message wording change; CHECK-RE survives |

**Installation:** Already installed at `/Users/ohama/.local/bin/fslit`

## Architecture Patterns

### fslit CHECK-RE Semantics

CHECK-RE matches the **next actual output line** against a .NET regex pattern. It is
sequential — it consumes exactly one line per directive. Plain lines and CHECK-RE can be
mixed. The regex is a **full-line match** (anchored to the whole line content).

```
// --- Output:
CHECK-RE: \[Elaboration\] filename\.fun:\d+:\d+: message keyword.*
1
```

Key .NET regex metacharacters that need escaping in CHECK-RE patterns:
- `[` `]` → `\[` `\]` (brackets are char classes)
- `.` → `\.` (literal dot in filenames)
- `{` `}` → `\{` `\}` (quantifiers)
- `(` `)` → `\(` `\)` (groups)
- `|` → `\|` (alternation)

### Test File Structure (no change needed)

```
// Test: <description>
// --- Command: bash -c 'cd %S && dotnet run --project .../LangBackend.Cli.fsproj -- filename.fun 2>&1; echo $?'
// --- Input:
// --- Output:
CHECK-RE: <pattern>
1
```

The `1` (exit code) is a plain exact-match line, not changed.

### Pattern Template for Elaboration Errors

```
CHECK-RE: \[Elaboration\] <escaped-filename>:\d+:\d+: <error-category>: <core-message-pattern>
```

Where:
- `\[Elaboration\]` — escaped brackets, category stays exact
- `<escaped-filename>` — the `.fun` extension needs `\.` escaped
- `:\d+:\d+:` — matches any line:col
- `<error-category>:` — exact subcategory (e.g., `FieldAccess:`, `RecordExpr:`, `Elaboration:`)
- `<core-message-pattern>` — anchor the distinctive keyword, use `.*` for variable parts

### Anti-Patterns to Avoid

- **Over-matching with `.*`:** Don't reduce to `CHECK-RE: \[Elaboration\].*` — this verifies nothing useful. Keep category and core message.
- **Under-escaping:** Forgetting `\.` for literal dots in filenames causes `.fun` to match `Xfun`.
- **Escaping too aggressively:** `'` and `-` do not need escaping in .NET regex.
- **Matching `In scope:` list verbatim:** The list grows as Prelude adds functions. Use `.*` after the core message.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Regex testing | manual regex tester script | `fslit --verbose` on single test | Runner shows actual vs expected directly |
| Exit code checking | separate assertion | plain `1` line after CHECK-RE | fslit matches output lines in order |

**Key insight:** fslit's CHECK-RE is .NET regex, not POSIX. Use `\d+` not `[0-9]+`, use
`.*` not `.*?` for greedy match of rest-of-line.

## Common Pitfalls

### Pitfall 1: Missing Exit Code Line

**What goes wrong:** Test file has only `CHECK-RE: ...` but no `1` line, causing a mismatch if output has two lines (error + exit code).
**Why it happens:** The `echo $?` in the command appends `1` as a second output line.
**How to avoid:** Always include `1` as the final plain-match line after CHECK-RE for error tests.
**Warning signs:** Test passes when it shouldn't (extra output lines silently ignored) — verify with `--verbose`.

### Pitfall 2: 44-02 Multi-line Output

**What goes wrong:** 44-02 outputs a multi-line F# record dump. Naive single CHECK-RE only matches first line.
**Why it happens:** `ConstPat` AST node is printed with multi-line F# pretty-printer format spanning 6 lines.
**How to avoid:** Use multiple directives — one CHECK-RE for the first line, then CONTAINS or plain matches for subsequent lines, OR rewrite to CHECK-RE on first line only and let remaining lines be unmatched.

**Current actual output for 44-02:**
```
[Elaboration] 44-02-error-location-pattern.fun:1:4: Elaboration: unsupported sub-pattern in TuplePat: ConstPat (IntConst 1, { FileName = "44-02-error-location-pattern.fun"
                        StartLine = 1
                        StartColumn = 4
                        EndLine = 1
                        EndColumn = 6 })
1
```

fslit matches output lines sequentially — if the .flt has 3 expected lines and actual has 6, it depends on whether fslit treats extra lines as failure. Safest approach: match the first line with CHECK-RE and use CONTAINS for the critical fields, or match line-by-line with exact matches for the stable middle lines.

**Recommendation for 44-02:** Use CHECK-RE for the first line (containing filename:line:col), then exact-match the stable indented lines (StartLine/EndLine values are stable since Phase 47), then `1` for exit code.

### Pitfall 3: 46-03 In-Scope List Changes

**What goes wrong:** `In scope: ++, <|>, Array_create, ...` list will grow as Prelude adds functions.
**Why it happens:** The list is dynamically built from all known functions at elaboration time.
**How to avoid:** Use `CHECK-RE: \[Elaboration\] 46-03-function-hint\.fun:\d+:\d+: Elaboration: unsupported App.*`
**Warning signs:** 46-03 test fails after adding any Prelude function.

### Pitfall 4: 45-02 Should NOT Get CHECK-RE

**What goes wrong:** Someone adds CHECK-RE to 45-02 expecting file:line:col.
**Why it happens:** Misunderstanding Phase 48's scope. 45-02 input has `def foo(x) = x\ndef 123bar() = 1`. The parseExpr fallback succeeds on the first declaration, so no positioned error is produced. The actual output is `[Parse] parse error` (no position).
**How to avoid:** Leave 45-02.flt unchanged — `[Parse] parse error` is correct behavior.
**Warning signs:** Would require changing the test input to produce a positioned error.

## Code Examples

### Pattern for Simple Single-Line Elaboration Error (44-01, 44-03, 46-01, 46-02, 46-04)

```
// --- Output:
CHECK-RE: \[Elaboration\] 44-01-error-location-unbound\.fun:\d+:\d+: Elaboration: unbound variable 'y'
1
```

Filename is exact (test files don't move), line:col flexible, core message stays exact.

### Pattern for Variable-Content Error (46-03 — In scope list)

```
// --- Output:
CHECK-RE: \[Elaboration\] 46-03-function-hint\.fun:\d+:\d+: Elaboration: unsupported App.*
1
```

The `.*` swallows the entire `In scope:` suffix.

### Pattern for Already-Working Tests (45-01, 46-05) — reference

```
// --- Output:
CHECK-RE: \[Parse\] .*45-01-parse-error-preserved\.fun:\d+:\d+: parse error
1
```

Note `.*` before filename to handle absolute path (45-01 command uses `%S/` prefix).

### Pattern for 44-02 Multi-line Output

```
// --- Output:
CHECK-RE: \[Elaboration\] 44-02-error-location-pattern\.fun:\d+:\d+: Elaboration: unsupported sub-pattern in TuplePat: ConstPat \(IntConst 1, \{ FileName = "44-02-error-location-pattern\.fun"
                        StartLine = 1
                        StartColumn = 4
                        EndLine = 1
                        EndColumn = 6 \}\)
1
```

Or simpler — match only the first line with CHECK-RE and drop the rest:
```
// --- Output:
CHECK-RE: \[Elaboration\] 44-02-error-location-pattern\.fun:\d+:\d+: Elaboration: unsupported sub-pattern in TuplePat.*
1
```

**Recommendation:** Use the simpler `.*` form for 44-02 since the AST dump is an implementation detail, not what the test is validating (the test validates that location is included, not the exact AST dump format).

### Running a Single Test (for verification)

```bash
/Users/ohama/.local/bin/fslit --verbose tests/compiler/44-01-error-location-unbound.flt
```

### Running All Error Tests

```bash
/Users/ohama/.local/bin/fslit tests/compiler/44-01-error-location-unbound.flt \
  tests/compiler/44-02-error-location-pattern.flt \
  tests/compiler/44-03-error-location-field.flt \
  tests/compiler/45-01-parse-error-preserved.flt \
  tests/compiler/45-02-parse-error-position.flt \
  tests/compiler/46-01-record-type-hint.flt \
  tests/compiler/46-02-field-hint.flt \
  tests/compiler/46-03-function-hint.flt \
  tests/compiler/46-04-error-category-elab.flt \
  tests/compiler/46-05-error-category-parse.flt
```

## Complete Test-by-Test Migration Plan

### Tests Already Using CHECK-RE (no change needed)

| File | Current State | Action |
|------|--------------|--------|
| `45-01-parse-error-preserved.flt` | CHECK-RE with `.*` before filename | No change |
| `46-05-error-category-parse.flt` | CHECK-RE with `\d+:\d+` | No change |

### Tests Using Exact Match — to be converted

| File | Current Exact Output | Why Fragile | Recommended CHECK-RE Pattern |
|------|---------------------|-------------|------------------------------|
| `44-01-error-location-unbound.flt` | `[Elaboration] 44-01-error-location-unbound.fun:2:17: Elaboration: unbound variable 'y'` | `2:17` is fragile if file changes | `CHECK-RE: \[Elaboration\] 44-01-error-location-unbound\.fun:\d+:\d+: Elaboration: unbound variable 'y'` |
| `44-02-error-location-pattern.flt` | Multi-line with `fun:1:4` and AST dump | Multi-line, AST format fragile | `CHECK-RE: \[Elaboration\] 44-02-error-location-pattern\.fun:\d+:\d+: Elaboration: unsupported sub-pattern in TuplePat.*` then `1` |
| `44-03-error-location-field.flt` | `[Elaboration] 44-03...:3:17: FieldAccess: unknown field 'z'. Known records: Point: {x; y}` | `3:17` fragile | `CHECK-RE: \[Elaboration\] 44-03-error-location-field\.fun:\d+:\d+: FieldAccess: unknown field 'z'.*` |
| `46-01-record-type-hint.flt` | `[Elaboration] 46-01...:2:6: RecordExpr: cannot resolve record type for fields ["a"; "b"]. Available record types: Point` | `2:6` fragile | `CHECK-RE: \[Elaboration\] 46-01-record-type-hint\.fun:\d+:\d+: RecordExpr: cannot resolve record type for fields.*` |
| `46-02-field-hint.flt` | `[Elaboration] 46-02...:3:17: FieldAccess: unknown field 'z'. Known records: Point: {x; y}` | `3:17` fragile | `CHECK-RE: \[Elaboration\] 46-02-field-hint\.fun:\d+:\d+: FieldAccess: unknown field 'z'.*` |
| `46-03-function-hint.flt` | `[Elaboration] 46-03...:2:27: Elaboration: unsupported App — ... In scope: ++, <\|>, Array_create, ...` | `2:27` fragile, In scope list grows | `CHECK-RE: \[Elaboration\] 46-03-function-hint\.fun:\d+:\d+: Elaboration: unsupported App.*` |
| `46-04-error-category-elab.flt` | `[Elaboration] 46-04...:1:17: Elaboration: unbound variable 'z'` | `1:17` fragile | `CHECK-RE: \[Elaboration\] 46-04-error-category-elab\.fun:\d+:\d+: Elaboration: unbound variable 'z'` |

### Test That Must NOT Change

| File | Reason |
|------|--------|
| `45-02-parse-error-position.flt` | Output is `[Parse] parse error` with no position — correct behavior per Phase 48 design; converting to CHECK-RE with `\d+:\d+` would fail |

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Tests with `file:174:col` (Prelude-relative) | Tests with `file:1:col` (user-relative) | Phase 47 | Line numbers stable now, but still exact |
| No position in parse errors | `file:line:col` in parse errors | Phase 48 | 45-01 and 46-05 already use CHECK-RE |
| All tests exact-match | 2/10 tests use CHECK-RE | Phase 48 | Phase 49 completes the migration for 7 more |

**Deprecated/outdated:**
- Hardcoded Prelude-offset line numbers (e.g., `175:17`): replaced by Phase 47 with user-relative `2:17`
- Exact-match for `In scope:` list in 46-03: will break as Prelude grows

## Open Questions

1. **44-02 multi-line output handling**
   - What we know: fslit matches lines sequentially; a CHECK-RE for line 1 followed by `1` will leave 5 middle lines unmatched
   - What's unclear: Whether fslit fails if actual output has MORE lines than expected, or only fails on mismatches
   - Recommendation: Use `CHECK-RE: ...*` on first line then `1` — if fslit ignores extra lines, this is simplest. If not, must match all 6 lines. Verify empirically with `--verbose`.

2. **Whether `\|>` needs escaping in 46-03 In scope list**
   - What we know: `<|>` is in the current In scope output; since we use `.*` to skip the list, this is moot
   - What's unclear: Only matters if we try to match the list exactly (we should not)
   - Recommendation: Use `.*` suffix, never match the In scope list exactly

## Sources

### Primary (HIGH confidence)

- `/Users/ohama/.local/bin/fslit --help` — authoritative CHECK-RE semantics, directive list
- Direct test execution `fslit --verbose` on all 10 test files — confirmed current pass state
- `src/LangBackend.Cli/Program.fs` — actual error formatting code, confirms output format
- `.planning/phases/47-prelude-separate-parsing/47-VERIFICATION.md` — confirms Phase 47 outcome (user line numbers correct)
- `.planning/phases/48-parse-error-position/48-VERIFICATION.md` — confirms Phase 48 outcome (45-02 intentionally has no position)

### Secondary (MEDIUM confidence)

- Test file content read directly — all 10 .flt files examined, all 10 .fun source files examined

## Metadata

**Confidence breakdown:**
- fslit CHECK-RE syntax: HIGH — from `--help` output, confirmed by two existing working tests
- Which tests need conversion: HIGH — all 10 tests examined with actual content
- Regex patterns: HIGH — derived from actual compiler output, verified current pass state
- 44-02 multi-line handling: MEDIUM — behavior when actual > expected lines needs empirical test

**Research date:** 2026-04-01
**Valid until:** 2026-05-01 (stable domain — fslit syntax won't change)
