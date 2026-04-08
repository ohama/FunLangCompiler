---
status: resolved
trigger: "empty block: expect at least a terminator errors in multi-file import"
created: 2026-04-07T00:00:00Z
updated: 2026-04-07T00:00:00Z
---

## Current Focus

hypothesis: confirmed - three root causes
test: all 261 E2E tests pass, 8 original errors reduced to 0
expecting: done
next_action: done

## Symptoms

expected: All functions compile with proper terminators in merge blocks
actual: 8 functions have empty/unterminated blocks in multi-file import
errors: "empty block: expect at least a terminator" for parseTokenDecl, closure_fn_143/148, parseStartDecl, parseTypeDecl, parsePrecOverride; "block with no terminator" for closure_fn_219/233
reproduction: cd /Users/ohama/vibe-coding/FunLexYacc && fnc src/funyacc/FunyaccMain.fun -O0 -o /dev/null
started: multi-file import compilation

## Eliminated

- hypothesis: block indices from imported modules pollute per-function block lists
  evidence: each func.func gets fresh Blocks = ref [], independent of imports
  timestamp: 2026-04-07

## Evidence

- timestamp: 2026-04-07
  checked: MLIR output for parseTokenDecl ^merge40
  found: merge block empty; continuation ops stuck after CfCondBrOp in ^then35
  implication: continuation ops placed inline instead of in merge block

- timestamp: 2026-04-07
  checked: FunLang source pattern causing issue
  found: `(let c = peek ps in if c = -1 then "EOF" else to_string(int_to_char c))` inside Tuple/Constructor args
  implication: Let wrapping If not detected as complex by LetNormalize

- timestamp: 2026-04-07
  checked: LetNormalize isComplexExpr
  found: only checks top-level If/Match/And/Or/TryWith, not Let/LetPat wrapping them
  implication: Let(name, simple_bind, If(...)) stays as Tuple element, not extracted

- timestamp: 2026-04-07
  checked: Let handler block patching order
  found: `Body = rops @ targetBlock.Body` (prepend) causes wrong op order when multiple nested handlers patch same block
  implication: Err ops prepended before ParseError ops, but Err uses ParseError result

- timestamp: 2026-04-07
  checked: Match Guard handler terminatedOps
  found: no appendToBlock logic for body ending with terminator + blocks created
  implication: nested if/while in guarded match arms left merge blocks empty

## Resolution

root_cause: Three interrelated issues:
1. LetNormalize.isComplexExpr did not detect Let/LetPat/LetMut wrapping complex (If/Match/And/Or/TryWith) sub-expressions. When such expressions appeared as Tuple/Constructor arguments, they were not extracted to let-bindings, causing terminator ops to appear mid-block.
2. Let/LetPat handlers used PREPEND (`rops @ targetBlock.Body`) when patching merge blocks. When multiple nested handlers patched the same block, prepend reversed the correct execution order (inner results used before computed).
3. Match Guard handler lacked appendToBlock logic for body expressions ending with terminators from nested control flow (if/match/while).

fix: 
1. Made isComplexExpr recursive: `Let(_, bind, body, _) -> isComplexExpr bind || isComplexExpr body` (and same for LetPat/LetMut).
2. Changed Let/LetPat handlers from prepend to append: `targetBlock.Body @ rops` instead of `rops @ targetBlock.Body`.
3. Added appendToBlock logic to Match Guard handler, matching the existing Leaf handler pattern.

verification: All 261 E2E tests pass. All 8 original MLIR errors eliminated. Remaining error (redefinition of @main) is a pre-existing naming conflict unrelated to this fix.

files_changed:
  - src/FunLangCompiler.Compiler/LetNormalize.fs
  - src/FunLangCompiler.Compiler/Elaboration.fs
