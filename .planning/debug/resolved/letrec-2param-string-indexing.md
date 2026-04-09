---
status: resolved
trigger: "letrec-2param-string-indexing"
created: 2026-04-09T00:00:00Z
updated: 2026-04-09T00:02:00Z
---

## Current Focus

hypothesis: RESOLVED
test: All 263/263 E2E tests pass
expecting: n/a
next_action: archived

## Symptoms

expected: `findSlash "ab/cdefgh"` returns 2 (index of '/')
actual: Compile error "redefinition of symbol '@go'". Wrong dispatch (lang_index_get instead of lang_string_char_at).
errors: "mlir-opt failed: redefinition of symbol named 'go'"
reproduction: `let findSlash (s:string):int = let rec go (i:int):int = if i >= String.length s then -1 else if s.[i] = 47 then i else go (i+1) in go 0`
started: After Phase 94 fix / commit 2a2c6a6

Note: `let rec findRec (s:string)(i:int):int = ...` was already working correctly.

## Eliminated

- hypothesis: if-else creates new env scopes that drop StringVars
  evidence: StringVars IS propagated through if-else; the real issue is lambda lifting
  timestamp: 2026-04-09T00:01:00Z

## Evidence

- timestamp: 2026-04-09T00:00:00Z
  checked: prior investigation summary
  found: simple `s.[0]` works, complex `s.[i]` in if-else fails (wrong dispatch)
  implication: StringVars IS populated for simple case, but something in the complex control flow path loses it

- timestamp: 2026-04-09T00:01:00Z
  checked: LambdaLift.fs
  found: Local LetRec with free string var `s` gets lambda-lifted: go(i:int) becomes go(s)(i) where s is prepended capture
  implication: paramIsString check in LetRec handler sees param="s", paramTypeAnnot=None, annotation for original span has TArrow(TInt,TInt) not TArrow(TString,...) -> paramIsString=false -> StringVars=empty

- timestamp: 2026-04-09T00:01:00Z
  checked: Elaboration.fs Lambda handler (line 2577)
  found: innerEnv.StringVars = env.StringVars (goBodyEnv.StringVars) = Set.empty
  implication: In closure fn, isStringExpr returns false for Var("s") -> lang_index_get used instead of lang_string_char_at

- timestamp: 2026-04-09T00:01:00Z
  checked: Elaboration.fs LetRec handler (line 864, now line ~877)
  found: env.Funcs.Value <- env.Funcs.Value @ [funcOp] -- no dedup, plus baseMlirName was always mlirFuncName(name)
  implication: Prelude's @go and user's lambda-lifted @go both added -> MLIR "redefinition of symbol 'go'"

- timestamp: 2026-04-09T00:01:00Z
  checked: env.StringVars at LetRec elaboration site
  found: When LetRec is inside findSlash's body, env.StringVars = {"s"} (s is findSlash's string param)
  implication: Fix for Bug 1: also check Set.contains param env.StringVars in paramIsString determination

## Resolution

root_cause: |
  Lambda lifting (LambdaLift.fs) transforms `let rec go (i:int) = (body using s)` into `go(s)(i) = ...` where s becomes the first LetRec param (prepended capture). Two bugs result:
  Bug 1: LetRec handler paramIsString check misses the lambda-lifted case: annotation shows original TArrow(TInt,TInt) and paramTypeAnnot=None, so paramIsString=false. StringVars stays empty. Inside the closure body, isStringExpr(Var("s")) returns false -> lang_index_get used instead of lang_string_char_at.
  Bug 2: LetRec handler generated @go with mlirFuncName(name) which collided with Prelude's List.go. No uniqueness mechanism existed.

fix: |
  Bug 1 (Elaboration.fs ~line 802-812): Added `Set.contains param env.StringVars` as fallback in paramIsString check. When the lambda lifter promotes a string capture to a LetRec param, the outer env.StringVars still has the var name, so paramIsString=true -> StringVars includes the param -> isStringExpr returns true -> lang_string_char_at used.
  Bug 2 (Elaboration.fs ~line 819-836): When mlirFuncName(name) already exists in env.Funcs, generate a unique name `@_letrec_N_name` using ClosureCounter. The sig_.MlirName is then used for FuncOp to keep calls consistent.

verification: |
  findSlash "ab/cdefgh" = 2 (correct)
  findSlash "hello/world" = 5 (correct)  
  findSlash "noslash" = -1 (correct)
  findSlash "/start" = 0 (correct)
  Full E2E suite: 263/263 passed (including new test 96-01)

files_changed:
  - src/FunLangCompiler.Compiler/Elaboration.fs
  - tests/compiler/96-01-letrec-string-capture-indexing.flt
  - tests/compiler/96-01-letrec-string-capture-indexing.sh
