# Phase 46 Research: Context Hints & Unified Format

## Error Sites Requiring Context Hints (CTX-01/02/03)

### Record Type Resolution Failures (CTX-01)

All use `env.RecordEnv : Map<string, Map<string, int>>` (type name -> field name -> slot index).

| Line | Function | Current Message | Hint to Add |
|------|----------|-----------------|-------------|
| 2699 | `ensureRecordFieldTypes` | "cannot resolve record type for fields %A" | Available record types from `env.RecordEnv |> Map.keys` |
| 2994 | `RecordExpr` handler | "cannot resolve record type for fields %A" | Same |
| 3057 | `RecordUpdate` handler | "cannot resolve record type for fields %A" | Same |
| 3390 | `ensureRecordFieldTypes2` | "cannot resolve record type for fields %A" | Same |

### Field Access Failures (CTX-02)

Need to find WHICH record type the field was expected on, then list valid fields.

| Line | Function | Current Message | Hint to Add |
|------|----------|-----------------|-------------|
| 3034 | `FieldAccess` handler | "unknown field '%s'" | Valid fields from all record types: `env.RecordEnv |> Map.values |> Seq.collect Map.keys` |
| 3094 | `SetField` handler | "unknown field '%s'" | Same |

### Function Not Found (CTX-03)

| Line | Function | Current Message | Hint to Add |
|------|----------|-----------------|-------------|
| 2374 | `App(Var)` handler | "'%s' is not a known function or closure value" | Sample from `env.KnownFuncs |> Map.keys` and `env.Vars |> Map.keys` |

## Error Categorization (CAT-01/02)

### Current State

Program.fs catch-all at line 197: `eprintfn "Error: %s" ex.Message`

Elaboration errors already have format `file:line:col: message` from `failWithSpan`.
Parse errors have various formats from FsLexYacc.
Pipeline compile errors are already handled by pattern match (lines 188-195).

### Implementation Approach

1. Elaboration.fs errors: already contain `file:line:col:` prefix from failWithSpan. Add `[Elaboration]` prefix inside failWithSpan.
2. Parse errors: Wrap parseProgram call in try/catch, prefix with `[Parse]`.
3. Compile errors (Pipeline): Already handled separately. Add `[Compile]` prefix to the eprintfn messages.

### Format Target

`[Phase] file:line:col: message` for all errors.

- `[Elaboration] test.lt:5:3: unknown field 'z'. Available fields: x, y`
- `[Parse] Error near token 5 of 12: ...`
- `[Compile] mlir-opt failed (exit 1): ...`
