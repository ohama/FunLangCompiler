# FunLexYacc Compilation Gap Status — FunLangCompiler v9.0

**Date:** 2026-03-30
**Purpose:** `langbackend-feature-requests.md` (2026-03-28, v8.0 기준) 을 FunLangCompiler v9.0 + LangThree v7.1 네이티브 API 전환 기준으로 재평가

## Context

- LangThree v7.1에서 dot notation이 제거되고, 네이티브 모듈 API가 도입됨
- FunLangCompiler v9.0에서 StringBuilder, HashSet, Queue, MutableList, String/Char/Array 빌트인, for-in 패턴, 리스트 컴프리헨션, 문자열 슬라이싱 등이 구현됨 (183 E2E 테스트)
- FunLexYacc 소스를 `.NET interop` → `LangThree 네이티브 모듈 API`로 전환하면 대부분의 gap이 해소됨
- 워크어라운드 가이드: `../LangThree/survey/funlexyacc-workaround-guide.md`

---

## 1. 해결 완료 (v9.0에서 구현됨)

원본 feature-requests.md의 51개 항목 중, **FunLexYacc 소스를 네이티브 API로 전환하면 해결되는 항목:**

| # | Feature | v9.0 대응 | 전환 방법 |
|---|---------|-----------|-----------|
| 5 | StringBuilder | `stringbuilder_create/append/tostring` + Prelude/StringBuilder.fun | `StringBuilder()` → `StringBuilder.create ()`, `.Append()` → `StringBuilder.add`, `.ToString()` → `StringBuilder.toString` |
| 6 | Dictionary | `hashtable_*` + Prelude/Hashtable.fun | `Dictionary<K,V>()` → `Hashtable.create ()`, `.[key]` → `Hashtable.get`, `.TryGetValue` → `Hashtable.tryGetValue` |
| 7 | HashSet | `hashset_*` + Prelude/HashSet.fun (Phase 33) | `HashSet<T>()` → `HashSet.create ()`, `.Add` → `HashSet.add`, `.Contains` → `HashSet.contains` |
| 8 | Queue | `queue_*` + Prelude/Queue.fun (Phase 33) | `Queue<T>()` → `Queue.create ()`, `.Enqueue` → `Queue.enqueue`, `.Dequeue` → `Queue.dequeue ()` |
| 9 | List\<T\> (mutable) | `mutablelist_*` + Prelude/MutableList.fun (Phase 33) | `List<T>()` → `MutableList.create ()`, `.Add` → `MutableList.add` |
| 10 | String slicing | `string_sub` + StringSliceExpr (Phase 34) | `s.[a..b]` 그대로 사용 가능 |
| 11 | `.Length` (string) | `string_length` + Prelude/String.fun | `s.Length` → `String.length s` |
| 12 | `.Length` (array) | `array_length` + Prelude/Array.fun | `arr.Length` → `Array.length arr` |
| 13 | for-in on collections | Phase 34: HashSet, Queue, MutableList, Hashtable 지원 | `for x in coll do` 그대로 사용, `for (k,v) in ht do` 지원 |
| 14 | `.Append()` | `stringbuilder_append` | `sb.Append(s)` → `StringBuilder.add sb s` |
| 15 | `.ToString()` | `stringbuilder_tostring` | `sb.ToString()` → `StringBuilder.toString sb` |
| 16 | `.TryGetValue()` | `hashtable_trygetvalue` | `dict.TryGetValue(k)` → `Hashtable.tryGetValue ht k` |
| 17 | `.Add()` (HashSet/List) | `hashset_add` / `mutablelist_add` | `.Add(v)` → `HashSet.add hs v` / `MutableList.add ml v` |
| 18 | `.Enqueue()`/`.Dequeue()` | `queue_enqueue` / `queue_dequeue` | `.Enqueue(v)` → `Queue.enqueue q v`, `.Dequeue()` → `Queue.dequeue q ()` |
| 19 | `.Count` | 각 모듈의 `count` 함수 | `.Count` → `Hashtable.count ht` / `HashSet.count hs` / etc. |
| 20 | `.Keys` | `hashtable_keys` | `dict.Keys` → `Hashtable.keys ht` |
| 21 | `List.sort` | Prelude/List.fun (`list_sort_by`) | 그대로 사용 |
| 22 | `List.sortBy` | Prelude/List.fun | 그대로 사용 |
| 23 | `List.distinctBy` | Prelude/List.fun | 그대로 사용 |
| 24 | `List.mapi` | Prelude/List.fun | 그대로 사용 |
| 25 | `List.item` | Prelude/List.fun (`List.nth`) | `List.item i xs` → `List.nth xs i` (인자 순서 확인 필요) |
| 26 | `List.exists` | Prelude/List.fun (`List.any`) | `List.exists f xs` → `List.any f xs` |
| 27 | `List.tryFind` | Prelude/List.fun | 그대로 사용 |
| 28 | `List.choose` | Prelude/List.fun | 그대로 사용 |
| 29 | `List.ofSeq` | `list_of_seq` (Phase 32) | 그대로 사용 |
| 30 | `List.isEmpty/head/tail` | Prelude/List.fun | 그대로 사용 |
| 31 | `Array.sort` | `array_sort` (Phase 32) | 그대로 사용 |
| 32 | `Array.ofSeq` | `array_of_seq` (Phase 32) | 그대로 사용 |
| 33 | `Array.ofList` | 기존 구현 | 그대로 사용 |
| 34 | `Array.toList` | 기존 구현 | 그대로 사용 |
| 35 | `Array.map` | 기존 구현 | 그대로 사용 |
| 36 | `Array.init` | 기존 구현 | 그대로 사용 |
| 37 | `Array.create` | 기존 구현 | 그대로 사용 |
| 38 | `Char.IsDigit` | Prelude/Char.fun (Phase 31) | 그대로 사용 |
| 39 | `Char.ToUpper` | Prelude/Char.fun (Phase 31) | 그대로 사용 |
| 40 | `File.ReadAllText` | `read_file` 빌트인 | `File.ReadAllText(path)` → `read_file path` |
| 41 | `File.WriteAllText` | `write_file` 빌트인 | `File.WriteAllText(path, content)` → `write_file path content` |
| 42 | `File.Exists` | `file_exists` 빌트인 | `File.Exists(path)` → `file_exists path` |
| 43 | `.EndsWith()` | `string_endswith` + Prelude/String.fun | `s.EndsWith(x)` → `String.endsWith s x` |
| 44 | `.Trim()` | `string_trim` + Prelude/String.fun | `s.Trim()` → `String.trim s` |
| 45 | List comprehension | Phase 34 | `[for x in coll -> expr]` 그대로 사용 |
| 47 | `|> ignore` | `let _ = expr` 패턴으로 전환 | `.Add() |> ignore` → `let _ = HashSet.add hs v` |
| 48 | `String.concat` | Prelude/String.fun | 그대로 사용 |
| 49 | `eprintfn` | `eprint`/`eprintln` 빌트인 | `eprintfn "%s" msg` → `eprintln msg` (포맷 불필요한 경우) |
| 50 | `Array.sort` (copy) | `array_sort` | 그대로 사용 |
| 51 | `kvp.Key`/`kvp.Value` | Phase 34: tuple destructuring | `for kvp in dict do kvp.Key` → `for (k, v) in ht do k` |

---

## 2. 잔여 블로커 (3개)

FunLexYacc 소스를 네이티브 API로 전환해도 해결할 수 없는 항목:

### 블로커 1: `sprintf` / `printfn` 포맷 문자열 (원본 #1, #2)

**상태:** Out of Scope (PROJECT.md)
**사용처:** DfaMin.fun (hex 포맷), ParserTables.fun (테이블 정렬 출력)
**사용 패턴:**
- `sprintf "%02x" c` — 16진수 포맷
- `sprintf "%8s" name` — 우측 정렬 문자열
- `sprintf "%3d" num` — 정수 패딩
- `printfn "%d states x %d terminals" n m` — 다중 인자 포맷 출력

**워크어라운드 가능성:**
- FunLexYacc 소스에서 sprintf를 `to_string` + 문자열 연결로 대체 가능 (기능은 유지되나 정렬/패딩 없음)
- 또는 FunLangCompiler에 C runtime `lang_sprintf` 구현 (포맷 문자열 파싱 필요)

**권장:** FunLangCompiler에 sprintf 구현 (C runtime `snprintf` 위임)

### 블로커 2: `open "file.fun"` 멀티파일 import (원본 #3)

**상태:** Out of Scope (PROJECT.md)
**사용처:** FunLexYacc의 19개 .fun 파일 전부가 cross-file `open ModuleName` 사용
**영향:** 단일 파일로는 FunLexYacc 컴파일 불가

**워크어라운드 가능성:**
- (a) `--multi-file` CLI 모드: 여러 .fun 파일을 받아 AST 병합 후 elaboration
- (b) 빌드 시스템에서 의존성 순서로 파일 concatenation
- (c) `FileImportDecl` 처리 in Elaboration.fs

**권장:** (a) `--multi-file` 모드가 가장 현실적

### 블로커 3: `get_args ()` CLI 인자 접근 (원본 #4)

**상태:** Out of Scope (PROJECT.md)
**사용처:** FunlexMain.fun, FunyaccMain.fun 엔트리포인트
**영향:** 컴파일된 바이너리가 CLI 인자를 받을 수 없음

**워크어라운드 가능성:**
- `@main` 시그니처를 `(i32, ptr) -> i64` (argc, argv)로 변경
- C runtime `lang_get_args(argc, argv)` → `string list` 반환

**권장:** `@main` 시그니처 변경 + C runtime 헬퍼

---

## 3. 주의사항 (v9.0 기존 제약)

FunLexYacc 전환 시 영향을 줄 수 있는 FunLangCompiler v9.0의 기존 제약:

| 제약 | 영향 | 워크어라운드 |
|------|------|-------------|
| Hashtable 문자열 키 crash | FunLexYacc는 `Dictionary<string,int>` 등 문자열 키 다수 사용 | **반드시 수정 필요** — C runtime의 hash/compare를 문자열 지원으로 확장 |
| `let mut` + for-in 클로저 segfault | `for x in coll do sum <- sum + x` 패턴 crash | 루프 내 mutable 변수 캡처 회피, 또는 수정 |
| 연속 두 `if` 표현식 invalid MLIR | 두 if문이 연속 나오면 빈 entry block | if-else로 감싸거나, 코드 구조 변경 |
| Bool 값 I64 반환 (I1 아님) | 모듈 함수에서 반환된 bool을 조건문에서 사용 시 | `<> 0` 비교 필요 |
| `Unchecked.defaultof` 미지원 (#46) | Lalr.fun, ParserTables.fun에서 사용 | 초기값을 0 또는 빈 값으로 대체 |

특히 **Hashtable 문자열 키 지원**은 블로커 급 — FunLexYacc의 Symtab, DfaMin, GrammarParser 등 핵심 모듈이 문자열 키 Dictionary를 사용.

---

## 4. 전환 로드맵 제안

### Phase A: FunLexYacc 소스 전환 (FunLangCompiler 변경 불필요)

FunLexYacc `.fun` 소스를 `.NET interop` → `LangThree 네이티브 모듈 API`로 전환.
가이드: `../LangThree/survey/funlexyacc-workaround-guide.md`

- 전환 대상: 위 해결 완료 항목 44개
- 예상 범위: 19개 .fun 파일, ~384 call site
- LangThree 인터프리터에서 동작 검증 가능

### Phase B: FunLangCompiler 블로커 해소

우선순위 순:

1. **Hashtable 문자열 키 지원** — C runtime hash/compare 확장 (MEDIUM)
2. **`get_args ()`** — @main 시그니처 변경 + C runtime (MEDIUM)
3. **`open "file.fun"` / `--multi-file`** — CLI + AST 병합 (HIGH)
4. **`sprintf` / `printfn`** — C runtime sprintf 위임 (HIGH)

### Phase C: 통합 검증

- FunLexYacc 전체를 FunLangCompiler로 컴파일
- 생성된 네이티브 바이너리로 funlex/funyacc 테스트 실행

---

*Generated: 2026-03-30 — FunLangCompiler v9.0 (183 E2E tests) + LangThree v7.1 기준*
