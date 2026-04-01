# LangThree (인터프리터) vs FunLangCompiler (컴파일러) 비교

**조사일:** 2026-03-30

## 1. 문법 (Grammar)

**결론: 동일하다.**

FunLangCompiler는 LangThree의 Parser/Lexer/AST를 **submodule로 직접 참조**한다.
- `FunLangCompiler.Compiler.fsproj` → `ProjectReference` → `LangThree`
- Parser.fsy, Lexer.fsl, Ast.fs 모두 LangThree 것을 그대로 사용
- 별도의 파서가 없으므로 문법 차이가 발생할 수 없음

지원하는 문법:
- let/let rec/let mut, match, if/else, try-with
- lambda, type annotation, tuple, list, record, ADT
- module/namespace/open
- for-in, for-to/downto, while
- user-defined operator (infix)
- pipe (|>), composition (>>)
- mutable assignment (<-)
- exception declaration/raise
- `open "file.fun"` (multi-file import)

## 2. Prelude 모듈 비교

### 공통 모듈 (8개)

| 모듈 | LangThree | FunLangCompiler | 비고 |
|------|-----------|-------------|------|
| Array.fun | O | O | 동일 |
| Char.fun | O | O | 동일 |
| Hashtable.fun | O | O | FunLangCompiler에 `createStr`, `keysStr` 추가 |
| List.fun | O | O | FunLangCompiler에 `sortBy`, `mapi`, `tryFind`, `choose`, `distinctBy` 추가 |
| Option.fun | O | O | 함수명 차이 (아래 참조) |
| Result.fun | O | O | 함수명 차이 (아래 참조) |
| String.fun | O | O | 동일 |
| StringBuilder.fun | O | O | 동일 |

### LangThree에만 있는 모듈 (4개)

| 모듈 | 내용 | FunLangCompiler 대응 |
|------|------|-------------------|
| **Core.fun** | id, const, compose, flip, not, min, max, abs, fst, snd, ignore | 빌트인으로 직접 사용 가능 |
| **HashSet.fun** | create, add, contains, count | 빌트인 존재 (hashset_*), Prelude 래퍼 미작성 |
| **MutableList.fun** | create, add, get, set, count | 빌트인 존재 (mutablelist_*), Prelude 래퍼 미작성 |
| **Queue.fun** | create, enqueue, dequeue, count | 빌트인 존재 (queue_*), Prelude 래퍼 미작성 |

### FunLangCompiler에만 있는 모듈

없음. FunLangCompiler는 LangThree의 부분집합.

### 함수명 차이 (Option / Result)

| LangThree | FunLangCompiler | 비고 |
|-----------|-------------|------|
| `optionMap` | `map` | FunLangCompiler가 모듈 컨텍스트에서 간결한 이름 사용 |
| `optionBind` | `bind` | |
| `optionDefault` | `defaultValue` | |
| `optionIter` | `iter` | |
| `optionFilter` | `filter` | |
| `resultMap` | `map` | |
| `resultBind` | `bind` | |
| `resultMapError` | `mapError` | |
| `resultDefault` | `defaultValue` | |
| `resultIter` | — | FunLangCompiler에 없음 |

## 3. 빌트인 함수 비교

### 공통 빌트인 (76개)

양쪽 모두 지원하는 빌트인:

**출력:** print, println, eprint, eprintln, eprintfn, printfn, sprintf
**파일 I/O:** read_file, write_file, read_lines, write_lines, append_file, file_exists, dir_files
**문자열:** string_length, string_sub, string_contains, string_startswith, string_endswith, string_concat, string_concat_list, string_trim, string_to_int, to_string
**배열:** array_create, array_get, array_set, array_length, array_init, array_map, array_fold, array_iter, array_sort, array_of_list, array_of_seq, array_to_list
**해시테이블:** hashtable_create, hashtable_get, hashtable_set, hashtable_containsKey, hashtable_remove, hashtable_keys, hashtable_count, hashtable_trygetvalue
**해시셋:** hashset_create, hashset_add, hashset_contains, hashset_count
**큐:** queue_create, queue_enqueue, queue_dequeue, queue_count
**뮤터블리스트:** mutablelist_create, mutablelist_add, mutablelist_get, mutablelist_set, mutablelist_count
**리스트:** list_of_seq, list_sort_by
**문자:** char_is_digit, char_is_letter, char_is_upper, char_is_lower, char_to_upper, char_to_lower, char_to_int, int_to_char
**StringBuilder:** stringbuilder_create, stringbuilder_append, stringbuilder_tostring
**시스템:** get_cwd, get_env, path_combine, stdin_read_line, stdin_read_all, get_args, failwith

### FunLangCompiler에만 있는 빌트인 (2개)

| 빌트인 | 용도 | 추가 시점 |
|--------|------|-----------|
| `hashtable_create_str` | 문자열 키 해시테이블 생성 | Phase 37 |
| `hashtable_keys_str` | 문자열 키 해시테이블 키 목록 | Phase 37 |

## 4. 갭 요약

FunLangCompiler에서 보완하면 완전 동일해지는 항목:

| 항목 | 작업량 | 우선순위 |
|------|--------|----------|
| Prelude/Core.fun 추가 | 래퍼 ~15줄 | 낮음 (빌트인으로 직접 사용 가능) |
| Prelude/HashSet.fun 추가 | 래퍼 ~5줄 | 중간 (FunLexYacc에서 사용 여부 확인 필요) |
| Prelude/MutableList.fun 추가 | 래퍼 ~6줄 | 중간 |
| Prelude/Queue.fun 추가 | 래퍼 ~5줄 | 낮음 |
| Option.fun 함수명 통일 | 이름 변경 또는 alias | 낮음 (모듈 컨텍스트에서 사용하면 무관) |

**핵심:** 문법은 100% 동일. 빌트인도 실질적으로 동일 (FunLangCompiler에 2개 추가). Prelude 래퍼 4개만 추가하면 모듈 수준에서도 완전 동일.
