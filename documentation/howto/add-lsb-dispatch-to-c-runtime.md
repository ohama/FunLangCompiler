---
created: 2026-04-07
description: C 런타임에서 LSB 1-bit로 int/string 키를 구분하여 함수 중복을 제거하는 패턴
---

# C 런타임에 LSB Dispatch 추가하기

하나의 C 함수가 `key & 1`로 int/string을 구분하여 별도의 `_str` 변종 없이 동작한다.

## The Insight

Tagged representation에서 정수는 LSB=1 (홀수), 포인터는 LSB=0 (짝수, 힙 정렬 보장). C 함수 안에서 `key & 1` 한 번으로 타입을 구분할 수 있으므로, int용/string용 두 벌의 함수가 불필요하다.

## Why This Matters

함수 쌍이 7개면 14개 C 함수 + 14개 컴파일러 패턴 + 14개 Prelude wrapper. 통합하면 7+7+7로 줄어든다. API 사용자도 `Hashtable.create()` 하나만 기억하면 된다.

## Recognition Pattern

- C 런타임에 `_str` 접미사 변종이 있다
- 컴파일러에서 `keyVal.Type = Ptr` 분기가 있다
- Prelude에 `*Str` wrapper가 있다

## The Approach

### Step 1: TAG 매크로 정의

```c
#define LANG_TAG_INT(n)    (((int64_t)(n) << 1) | 1)
#define LANG_UNTAG_INT(v)  ((int64_t)(v) >> 1)
```

### Step 2: Unified hash 함수

```c
static uint64_t lang_ht_hash(int64_t key) {
    if (key & 1) {
        // Tagged int — murmurhash3 finalizer
        uint64_t h = (uint64_t)key;
        h ^= h >> 33; h *= 0xff51afd7ed558ccdULL;
        h ^= h >> 33; h *= 0xc4ceb9fe1a85ec53ULL;
        h ^= h >> 33;
        return h;
    } else {
        // Pointer (string) — FNV-1a
        LangString* s = (LangString*)(uintptr_t)key;
        uint64_t h = 14695981039346656037ULL;
        for (int64_t i = 0; i < s->length; i++) {
            h ^= (uint8_t)s->data[i];
            h *= 1099511628211ULL;
        }
        return h;
    }
}
```

### Step 3: Unified equality

```c
static int lang_ht_eq(int64_t a, int64_t b) {
    if ((a & 1) != (b & 1)) return 0;  // 다른 타입
    if (a & 1) return a == b;           // 둘 다 tagged int
    // 둘 다 string pointer
    LangString* sa = (LangString*)(uintptr_t)a;
    LangString* sb = (LangString*)(uintptr_t)b;
    return sa->length == sb->length && memcmp(sa->data, sb->data, sa->length) == 0;
}
```

### Step 4: 키 저장 방식 변경

**이전:** 컴파일러가 int 키를 untag 후 저장 → C가 raw int 저장 → 읽을 때 retag 필요
**이후:** 키를 **as-is** 저장 (tagged int 또는 raw pointer) → 읽을 때 변환 불필요

### Step 5: C→FunLang callback에서 retag

C가 FunLang 클로저를 호출할 때, raw int를 전달하면 안 된다:

```c
// ❌ BAD: raw index
fn(closure, i);

// ✅ GOOD: tagged index
fn(closure, LANG_TAG_INT(i));
```

해당 함수: `lang_array_init`, `lang_for_in_hashtable`, `lang_for_in_hashset`

### Step 6: C가 만드는 tuple/struct의 값도 tagged

```c
// ❌ BAD: raw bool
tup[0] = 1;  // true

// ✅ GOOD: tagged bool
tup[0] = LANG_TAG_INT(1);  // tagged true = 3
```

해당 함수: `lang_hashtable_trygetvalue`, `lang_hashtable_trygetvalue_str`

## 체크리스트

- [ ] LANG_TAG_INT/LANG_UNTAG_INT 매크로 정의
- [ ] Unified hash 함수 (LSB dispatch)
- [ ] Unified equality 함수 (LSB dispatch)
- [ ] 모든 `_str` 함수 제거 또는 redirect
- [ ] Struct를 하나로 통합 (LangHashtableStr 제거)
- [ ] 키 저장: as-is (untag 제거)
- [ ] Callback에 LANG_TAG_INT 적용
- [ ] Tuple 생성에 LANG_TAG_INT 적용

## Pitfalls

**Double-tagging:** C가 FunLang에서 받은 값(이미 tagged)을 다시 `LANG_TAG_INT`하면 값이 깨진다. Tag는 C에서 **생성한** 값에만 적용.

**HashSet 별도 처리:** HashSet이 raw int를 저장하고 있으면 hash/equality가 다른 경로를 타야 한다. 통합 전에 저장 방식을 확인.

## 관련 문서

- `add-tagged-value-representation.md` — 컴파일러 측 tagging
