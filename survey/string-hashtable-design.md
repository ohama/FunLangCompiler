# String Hashtable Design Survey

## 현재 구조

### 인터프리터 (FunLang)
- **단일 구현**: `Dictionary<Value, Value>` — 모든 타입의 키/값 사용 가능
- `hashtable_create`, `hashtable_get`, `hashtable_set` 등 **하나의 API**
- 키 타입에 무관하게 동작 (string, int, tuple 등)

### 컴파일러 (FunLangCompiler)  
- **이중 구현**: `LangHashtable` (int key, tag=-1) + `LangHashtableStr` (string key, tag=-2)
- C 런타임에 별도 struct, 별도 hash/equality 함수

## Prelude wrapper의 한계

Prelude module 함수는 standalone func.func로 컴파일된다. 이때 **모든 파라미터가 i64**로 전달되므로, 함수 body 안에서 key의 MlirType(I64 vs Ptr)을 구분할 수 없다.

```
유저 코드:  Hashtable.set ht "hello" 1
          → Hashtable_set(ht, "hello", 1)     // func.func 호출
          
func.func body:  hashtable_set ht key value
                 key의 MlirType = I64         // Ptr 정보 소실!
                 → lang_hashtable_set 호출    // int 버전 → crash
```

**결론**: key dispatch가 필요한 **모든** 함수에 `*Str` 변종이 필요하다. `count`만 type-independent(GEP+load).

## 최종 Prelude 설계

```fun
module Hashtable =
    let create ()             = hashtable_create ()
    let createStr ()          = hashtable_create_str ()
    let get ht key            = hashtable_get ht key
    let getStr ht key         = hashtable_get_str ht key
    let set ht key value      = hashtable_set ht key value
    let setStr ht key value   = hashtable_set_str ht key value
    let containsKey ht key    = hashtable_containsKey ht key
    let containsKeyStr ht key = hashtable_containsKey_str ht key
    let keys ht               = hashtable_keys ht
    let keysStr ht            = hashtable_keys_str ht
    let remove ht key         = hashtable_remove ht key
    let removeStr ht key      = hashtable_remove_str ht key
    let tryGetValue ht key    = hashtable_trygetvalue ht key
    let tryGetValueStr ht key = hashtable_trygetvalue_str ht key
    let count ht              = hashtable_count ht        // 공통
```

**사용 예시:**
```fun
// Int key hashtable
let ht = Hashtable.create ()
Hashtable.set ht 1 42
Hashtable.get ht 1
let keys = Hashtable.keys ht

// String key hashtable — 모든 함수에 Str suffix
let ht = Hashtable.createStr ()
Hashtable.setStr ht "hello" 42
Hashtable.getStr ht "hello"
let keys = Hashtable.keysStr ht
```

## 직접 builtin 호출과 IndexGet/Set

**직접 builtin 호출** (Prelude 비경유):
- `hashtable_set ht "hello" 1` — elaboration이 key의 Ptr 타입 감지 → 자동 dispatch 동작

**IndexGet/IndexSet** (`ht.["key"]`):
- `ht.[key]` 구문은 inline elaboration → 자동 dispatch 동작
- key가 Ptr → `lang_index_get_str` 호출
- key가 I64 → `lang_index_get` 호출

## 근본적 해결: Uniform Tagged Representation

현재의 `*Str` 중복은 컴파일 타임 type dispatch + func.func의 i64 uniform ABI 사이의 불일치에서 발생한다. 근본적 해결은 OCaml 방식의 tagged representation 도입이다. 자세한 내용은 [uniform-tagged-representation.md](uniform-tagged-representation.md) 참조.

---
*2026-04-07 — Prelude wrapper type 소실 문제 반영, 전체 `*Str` 변종 확정*
