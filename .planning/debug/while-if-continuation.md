---
status: resolved
trigger: "issue #18: while body + if-then continuation becomes dead code"
created: 2026-04-07T00:00:00Z
updated: 2026-04-07T00:00:00Z
---

## Current Focus

hypothesis: confirmed - parser issue, not compiler issue
test: grammar fix with precedence declarations
expecting: correct AST where continuation is after if-then, not inside
next_action: done

## Symptoms

expected: while loop body executes if-then, then continues with i <- i + 1, loop terminates after 3 iterations
actual: infinite loop printing 0 - the i <- i + 1 never executes
errors: none (compiles successfully, runtime infinite loop)
reproduction: compile and run the minimal test case
started: unknown

## Eliminated

- hypothesis: Elaboration.fs appendToBlock places ops in wrong block
  evidence: AST dump showed the continuation was already inside the if-then body in the AST from parser
  timestamp: 2026-04-07

- hypothesis: WhileExpr back-edge patching puts ops in wrong block
  evidence: The MLIR structure was correct for the (wrong) AST it received; the issue was upstream
  timestamp: 2026-04-07

## Evidence

- timestamp: 2026-04-07
  checked: MLIR output for test case
  found: i <- i + 1 ops appeared in ^then3 block instead of ^merge5 block
  implication: continuation code was placed in then-branch, not after if

- timestamp: 2026-04-07
  checked: AST structure via debug logging in WhileExpr handler
  found: AST was LetPat(WC, println, If(LetPat(WC, println"mid", Assign(i,i+1)), Unit))
  implication: parser placed Assign inside if-then's then-branch, not after it

- timestamp: 2026-04-07
  checked: Parser.fsy grammar rule for if-then without else
  found: IF Expr THEN SeqExpr - SeqExpr greedily consumed all remaining statements
  implication: root cause is grammar, not elaboration

## Resolution

root_cause: FunLang parser grammar rule `IF Expr THEN SeqExpr` (without else) uses `SeqExpr` for the then-branch, which greedily consumes all continuation statements in a semicolon-sequence. The `i <- i + 1` was parsed as part of the then-branch, not as a statement after the if-then.
fix: Changed grammar to `IF Expr THEN Expr %prec IFTHEN` with precedence declarations `%nonassoc SEMICOLON` < `%nonassoc IFTHEN` < `%nonassoc ELSE`. This makes the parser reduce (close the if-then) when seeing SEMICOLON, while still shifting ELSE for if-then-else.
verification: All 260 compiler E2E tests pass, all 244 FunLang unit tests pass, all 723 FunLang flt tests pass. Manual test produces correct output: 0, 1, mid, 2, done.
files_changed:
  - deps/FunLang/src/FunLang/Parser.fsy
