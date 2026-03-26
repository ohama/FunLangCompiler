# Milestones: LangBackend

## Completed

### v1.0 — Core Compiler (2026-03-26)

**Goal:** LangThree 소스 코드를 네이티브 실행 바이너리로 컴파일

**Phases:** 1–6 (11 plans, all verified)
**Requirements:** 21/21 complete
**Tests:** 15 FsLit E2E tests

**What shipped:**
- MlirIR typed internal IR (F# DU)
- Elaboration pass (AST → MlirIR translation)
- Scalar codegen (int, arith, let/var SSA)
- Booleans, comparisons, control flow (if-else, &&, ||)
- Known functions (let rec → FuncOp + DirectCallOp)
- Closures (lambda capture → flat struct + indirect call)
- CLI (`langbackend file.lt` → native binary)

**Key decisions validated:**
- MLIR text format direct generation (no P/Invoke) ✓
- MlirIR as typed internal IR (not thin wrapper) ✓
- Flat closure struct {fn_ptr, env_fields} ✓
- Caller-allocates closure pattern ✓

---

## Current

### v2.0 — Data Types & Pattern Matching

**Goal:** String, 튜플, 리스트 타입 지원 + 패턴 매칭 + GC 런타임
**Started:** 2026-03-26
**Status:** Defining requirements
