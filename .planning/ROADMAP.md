# Roadmap: LangBackend

## Overview

LangBackend compiles LangThree source to native x86-64 binaries via MLIR → LLVM.
Pipeline: LangThree AST → Elaboration → MlirIR → Printer → `.mlir` → `mlir-opt` → `mlir-translate` → `clang` → binary.

## Milestones

- ✅ **v1.0 Core Compiler** - Phases 1-6 (shipped 2026-03-26)
- ✅ **v2.0 Data Types & Pattern Matching** - Phases 7-11 (shipped 2026-03-26)
- 🚧 **v3.0 Language Completeness** - Phases 12-15 (in progress)

## v3.0 Phases

**Milestone Goal:** 누락 연산자, 빌트인, 패턴 매칭 확장으로 대부분의 LangThree 프로그램이 컴파일 가능하도록 한다.

- [ ] **Phase 12: Missing Operators** - Modulo, Char literal, PipeRight, ComposeRight/Left
- [ ] **Phase 13: Pattern Matching Extensions** - when guards, OrPat, ConstPat(CharConst)
- [ ] **Phase 14: Builtin Extensions** - failwith, string_sub, string_contains, string_to_int, char_to_int, int_to_char, variable print/println
- [ ] **Phase 15: Range** - [start..stop] and [start..step..stop] list generation

### Phase 12: Missing Operators
**Goal**: LangThree의 누락 연산자들이 컴파일러에서 지원되어 `%`, `'A'`, `|>`, `>>`, `<<` 연산자를 사용하는 프로그램이 정상 컴파일된다
**Depends on**: Phase 11
**Requirements**: OP-01, OP-02, OP-03, OP-04, OP-05
**Success Criteria** (what must be TRUE):
  1. `10 % 3` compiles and exits with 1 (modulo via `arith.remsi`)
  2. `'A'` compiles to `int64 65` and `char_to_int 'A'` exits with 65
  3. `5 |> fun x -> x + 1` compiles and exits with 6 (PipeRight desugars to App)
  4. `let inc = fun x -> x + 1 in let dbl = fun x -> x * 2 in (inc >> dbl) 3` exits with 8 (ComposeRight)
  5. `(dbl << inc) 3` exits with 8 (ComposeLeft)
**Plans**: TBD

### Phase 13: Pattern Matching Extensions
**Goal**: 패턴 매칭이 when 가드, OrPat, CharConst 패턴을 지원하여 더 복잡한 match 식이 컴파일된다
**Depends on**: Phase 12
**Requirements**: PAT-06, PAT-07, PAT-08
**Success Criteria** (what must be TRUE):
  1. `match 5 with | n when n > 0 -> 1 | _ -> 0` exits with 1 (when guard)
  2. `match 3 with | 1 | 2 | 3 -> 10 | _ -> 0` exits with 10 (OrPat)
  3. `match 'A' with | 'A' -> 1 | _ -> 0` exits with 1 (CharConst pattern)
**Plans**: TBD

### Phase 14: Builtin Extensions
**Goal**: failwith, 문자열 조작 빌트인, 문자 변환 빌트인이 컴파일되어 실용적인 프로그램 작성이 가능하다
**Depends on**: Phase 12
**Requirements**: BLT-01, BLT-02, BLT-03, BLT-04, BLT-05, BLT-06, BLT-07
**Success Criteria** (what must be TRUE):
  1. `failwith "error"` prints "error" to stderr and exits 1
  2. `string_sub "hello world" 6 5` returns "world" (substring extraction)
  3. `string_contains "hello world" "world"` returns true
  4. `string_to_int "42"` exits with 42
  5. `char_to_int 'Z'` exits with 90, `int_to_char 65` exits with 65
  6. `let s = string_concat "hello" " world" in println s` prints "hello world" (variable string print)
**Plans**: TBD

### Phase 15: Range
**Goal**: Range 문법으로 정수 리스트를 생성할 수 있어 반복 패턴이 간결해진다
**Depends on**: Phase 14
**Requirements**: RNG-01, RNG-02
**Success Criteria** (what must be TRUE):
  1. `let rec sum lst = match lst with | [] -> 0 | h :: t -> h + sum t in sum [1..5]` exits with 15
  2. `[1..2..10]` generates `[1; 3; 5; 7; 9]` and length is 5
**Plans**: TBD

## Progress

**Execution Order:**
Phases 12 → 13 → 14 → 15 (13 and 14 are independent, can run in parallel after 12)

| Phase | Milestone | Plans Complete | Status | Completed |
|-------|-----------|----------------|--------|-----------|
| 12. Missing Operators | v3.0 | 0/TBD | Not started | - |
| 13. Pattern Matching Extensions | v3.0 | 0/TBD | Not started | - |
| 14. Builtin Extensions | v3.0 | 0/TBD | Not started | - |
| 15. Range | v3.0 | 0/TBD | Not started | - |
