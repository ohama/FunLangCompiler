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

### v2.0 — Data Types & Pattern Matching (2026-03-26)

**Goal:** String, 튜플, 리스트 타입 지원 + 패턴 매칭 + Boehm GC 런타임

**Phases:** 7–11 (9 plans, all verified)
**Requirements:** 20/20 complete
**Tests:** 34 FsLit E2E tests
**LOC:** 1,861 (F# + C)

**What shipped:**
- Boehm GC runtime integration (GC_INIT + GC_malloc for all heap allocation)
- String compilation ({i64 length, ptr data} heap struct, strcmp equality, builtins)
- Tuple compilation (GC_malloc'd N-field structs, destructuring via GEP + load)
- List compilation (null-pointer nil, cons cells, literal desugaring)
- Pattern matching (Jacobs decision tree, cf.cond_br chain, all v2 pattern types)
- print/println builtins, string_length, string_concat, to_string

**Key decisions validated:**
- Boehm GC (conservative collector, `-lgc` link only) ✓
- Uniform boxed representation (all heap types as ptr) ✓
- Sequential cf.cond_br match compilation ✓
- @lang_match_failure fallback for non-exhaustive match ✓
- Jacobs decision tree for pattern matching ✓

---

## Current

### v3.0 — Language Completeness

**Goal:** 누락 연산자, 빌트인, 패턴 매칭 확장으로 대부분의 LangThree 프로그램 컴파일 가능
**Started:** 2026-03-26
**Status:** Defining requirements
