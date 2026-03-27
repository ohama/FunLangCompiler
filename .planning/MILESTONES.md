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

### v3.0 — Language Completeness (2026-03-26)

**Goal:** 누락 연산자, 빌트인, 패턴 매칭 확장으로 대부분의 LangThree 프로그램 컴파일 가능

**Phases:** 12–15 (5 plans, all verified)
**Requirements:** 17/17 complete
**Tests:** 45 FsLit E2E tests

**What shipped:**
- Modulo (%), Char literal ('A'), PipeRight (|>), ComposeRight/Left (>>, <<) operators
- when guards, OrPat (| P1 | P2), ConstPat(CharConst) pattern matching extensions
- failwith runtime error, string_sub/string_contains/string_to_int string builtins
- char_to_int/int_to_char character builtins, variable print/println
- Range syntax ([start..stop], [start..step..stop]) list generation

**Key decisions validated:**
- PipeRight/Compose are elaboration-time desugar only, no new MLIR ops ✓
- OrPat expanded before MatchCompiler; char is already i64 (identity elaboration) ✓
- Range via C runtime (lang_range) returning Phase-10-compatible cons list ✓

---

### v4.0 — Type System & Error Handling (2026-03-27)

**Goal:** ADT/GADT, Records(mutable fields), Exception handling(setjmp/longjmp)으로 LangThree 타입 시스템 기능 대부분 네이티브 코드 지원

**Phases:** 16–20 (12 plans, all verified)
**Requirements:** 27/27 complete
**Tests:** 67 FsLit E2E tests (22 new)
**LOC:** 2,861 F# + 184 C

**What shipped:**
- ADT discriminated unions: nullary/unary/multi-arg constructors + full pattern matching round-trip
- Record types: field access, functional update, mutable field mutation, structural pattern matching
- Exception handling: setjmp/longjmp C runtime, nested try-with, payload extraction, handler-miss re-raise
- First-class constructors: unary+ ctors as higher-order function arguments via Lambda closure wrapping
- Nested ADT pattern matching: multi-level GEP chains with Ptr-retype resolution

**Key decisions validated:**
- ADT 16-byte block layout {tag, payload} with uniform boxed representation ✓
- Record flat layout GC_malloc(n*8), no tag prefix ✓
- setjmp/longjmp with inline _setjmp for ARM64 PAC compatibility ✓
- Uniform closure ABI (ptr, i64) -> i64 with ptrtoint/inttoptr bridging ✓

---

## Current

Planning next milestone
