# String Hashtable Design Survey

## 현재 구조

### 인터프리터 (FunLang)
- **단일 구현**: `Dictionary<Value, Value>` — 모든 타입의 키/값 사용 가능
- `hashtable_create`, `hashtable_get`, `hashtable_set` 등 **하나의 API**
- 키 타입에 무관하게 동작 (string, int, tuple 등)

### 컴파일러 (FunLangCompiler)  
- **이중 구현**: `LangHashtable` (int key, tag=-1) + `LangHashtableStr` (string key, tag=-2)
- C 런타임에 별도 struct, 별도 hash/equality 함수

## 자동 dispatch vs 수동 선택

| 함수 | dispatch 방식 | 설명 |
|------|-------------|------|
| `hashtable_set ht key val` | **자동** (key의 elaborated type이 Ptr이면 `_str` 호출) | ✓ |
| `hashtable_get ht key` | **자동** | ✓ |
| `hashtable_containsKey ht key` | **자동** | ✓ |
| `hashtable_remove ht key` | **자동** | ✓ |
| `hashtable_trygetvalue ht key` | **자동** | ✓ |
| `hashtable_create ()` | **수동** — int HT만 생성 | ✗ `hashtable_create_str ()` 필요 |
| `hashtable_keys ht` | **수동** — int key만 반환 | ✗ `hashtable_keys_str ht` 필요 |
| `hashtable_count ht` | **자동** (GEP offset 2, 공통) | ✓ |

## 왜 create/keys만 수동인가?

- `create`: 생성 시점에 key 타입 정보가 없음 (인자가 unit)
- `keys`: 반환할 key 리스트의 원소 타입이 다름 (int list vs string list)
- 나머지: key 인자의 elaborated type (I64 vs Ptr)으로 자동 결정 가능

## Prelude 설계 결론

```fun
module Hashtable =
    let create ()           = hashtable_create ()       // int key HT
    let createStr ()        = hashtable_create_str ()   // string key HT
    let get ht key          = hashtable_get ht key      // 자동 dispatch
    let set ht key value    = hashtable_set ht key value
    let containsKey ht key  = hashtable_containsKey ht key
    let keys ht             = hashtable_keys ht         // int key HT only
    let keysStr ht          = hashtable_keys_str ht     // string key HT only
    let remove ht key       = hashtable_remove ht key
    let tryGetValue ht key  = hashtable_trygetvalue ht key
    let count ht            = hashtable_count ht        // 공통
```

**사용 예시:**
```fun
// String key hashtable
let ht = Hashtable.createStr ()   // ← 유일하게 다른 부분
Hashtable.set ht "hello" 42      // 자동 dispatch (key="hello" → Ptr → _str)
Hashtable.get ht "hello"          // 자동 dispatch
let keys = Hashtable.keysStr ht   // ← 유일하게 다른 부분
```

## 주의: Prelude wrapper를 통한 auto-dispatch는 동작하지 않음

`Hashtable.set ht "hello" 1` — wrapper 함수의 closure ABI가 모든 파라미터를 I64로 전달하므로 string key의 Ptr 타입 정보가 소실됩니다. 결과적으로 int key 변형이 호출되어 crash.

**동작하는 패턴:**
- 직접 builtin 호출: `hashtable_set ht "hello" 1` (elaboration이 "hello"의 Ptr 타입 감지)
- IndexGet/Set 구문: `ht.["hello"] <- 1`, `ht.["hello"]` (자동 dispatch)

**동작하지 않는 패턴:**
- Module wrapper: `Hashtable.set ht "hello" 1` (key가 I64로 전달되어 int HT 함수 호출 → crash)

## IndexGet/IndexSet (`ht.["key"]`)

`ht.[key]` 구문도 자동 dispatch:
- key가 Ptr → `lang_index_get_str` 호출
- key가 I64 → `lang_index_get` 호출 (runtime tag 기반 array/HT 구분)

---
*2026-04-02 — FunLangCompiler v22.0 기준*
