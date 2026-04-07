# Phase 88 Plans 02-03: Core Tagging + C Boundary Summary

**One-liner:** Tagged literal representation (2n+1) with arithmetic correction and C-boundary untag/retag

## What Was Done

### Plan 88-02: Core Tagging
- Tagged Number/Char literals with `tagConst(n)` = 2n+1 encoding
- Tagged all unit constants (0L -> 1L) including emitVoidCall helper
- Fixed Add with -1 correction: (2a+1)+(2b+1)-1 = 2(a+b)+1
- Fixed Subtract with +1 correction: (2a+1)-(2b+1)+1 = 2(a-b)+1
- Fixed Multiply/Divide/Modulo with untag-compute-retag pattern
- Fixed Negate: 0-a -> 2-a for tagged representation
- Fixed 7 truthiness checks (If/And/Or/While): compare against 1L (tagged false) not 0L
- Fixed for-loop increment: raw 2L step (tagged(n)+2 = tagged(n+1))
- Tagged range default step (1L -> tagConst 1L = 3L)
- Tagged ConstPat(IntConst) in match compiler (ElabHelpers + both emitCtorTest)
- Untagged @main return value for correct process exit code

### Plan 88-03: C Boundary Untag/Retag
- Untag before 20+ C function call sites (integer args)
- Retag after 10+ C function call sites (integer results)
- Special range handling: pass tagged start/stop, raw step = tagged_step - 1
- Handle unreachable blocks in @main untag (appendUntagIfSafe)
- Handle I1/Ptr result types in @main exit code
- Coerce closure args to I64 for uniform ABI (IndirectCallOp)
- Fixed coerceToI64Arg to untag I64 for sprintf integer args

## Decisions Made

1. **Range values stay tagged in lists** - lang_range receives tagged start/stop and raw step (tagged_step - 1), producing tagged list elements that work with FunLang arithmetic
2. **C-callback limitation accepted** - C runtime (array_init, for-in-*, etc.) passes raw values to closures; 4 tests regress until Phase 89 updates C runtime
3. **@main return handles all types** - I64: untag; I1: zext; Ptr: return 0; with unreachable-safe guard

## Test Results

- Before: 253/257 (4 pre-existing failures from coerceToI64 retag)
- After: 249/257 (same 4 pre-existing + 4 new C-callback regressions)
- New regressions: 24-04-array-init, 34-05-forin-tuple-ht, 34-06-forin-hashset, 35-02-hashtable-module

## Commits

| Hash | Description |
|------|-------------|
| 848708f | feat(88-02): core tagging |
| 14bdd64 | feat(88-03): C boundary untag/retag |

## Key Files Modified

- `src/FunLangCompiler.Compiler/Elaboration.fs` - All literal/arithmetic/C-boundary changes
- `src/FunLangCompiler.Compiler/ElabHelpers.fs` - emitVoidCall, coerceToI64Arg, ConstPat tagging
- `src/FunLangCompiler.Compiler/ElabProgram.fs` - @main untag with unreachable-safe guard

## Duration

~54 minutes
