# Prelude Diff Analysis: FunLang v14.0 vs FunLangCompiler

**Researched:** 2026-04-09
**Scope:** All 14 shared Prelude files

---

## Summary of Difference Categories

Two structural differences exist across all files:

1. **Type annotations removed** — FunLang v14.0 dropped all parameter/return type annotations. The compiler version never had them. This is cosmetic: zero semantic impact.

2. **Currying style diverged** — For multi-parameter functions, FunLang v14.0 uses explicit multi-param syntax `let f a b c = ...` while the compiler version uses explicit `fun` lambdas `let f a = fun b -> fun c -> ...`. This is a **behavioral difference** for partial application contexts.

3. **Compiler-only functions** — A small set of functions exist only in the compiler prelude and must be preserved during sync.

---

## File-by-File Analysis

### Array.fun

**Status:** COPY (safe)

**Diff summary:** Type annotations only. Every function body is identical. Currying style is the same (Array functions already used uncurried multi-param in both versions).

**Compiler-only functions:** None

**FunLang-only functions:** None

**Recommendation:** Copy FunLang version as-is. Zero risk.

---

### Char.fun

**Status:** COPY (safe)

**Diff summary:** Type annotations only. All 8 functions identical in body.

**Compiler-only functions:** None

**FunLang-only functions:** None

**Recommendation:** Copy FunLang version as-is.

---

### Core.fun

**Status:** MANUAL_MERGE

**Diff summary:** Three categories of changes:

1. Type annotations removed (all functions) — cosmetic
2. Currying style changed for `const`, `compose`, `flip`, `apply`, `min`, `max` — semantic (affects partial application)
3. Operator param names changed: `(|>)`, `(>>)`, `(<<)`, `(<|)` use `__pipe_x`, `__pipe_f`, `__comp_lhs`, `__comp_rhs`, `__comp_x` in compiler version vs typed names in FunLang version
4. **Two compiler-only functions at the end:** `char_to_int` and `int_to_char`

**Compiler-only functions:**
```
let char_to_int c = c
let int_to_char n = n
```
These are identity shims used internally by the compiler codegen. They must be preserved.

**FunLang-only functions:** None (FunLang v14.0 has fewer lines — 24 vs compiler's 26)

**Currying difference detail:**

| Function | FunLang v14.0 | Compiler |
|----------|---------------|----------|
| `const` | `let const x y = x` | `let const x = fun y -> x` |
| `compose` | `let compose f g x = f (g x)` | `let compose f = fun g -> fun x -> f (g x)` |
| `flip` | `let flip f x y = f y x` | `let flip f = fun x -> fun y -> f y x` |
| `apply` | `let apply f x = f x` | `let apply f = fun x -> f x` |
| `min` | `let min a b = ...` | `let min a = fun b -> ...` |
| `max` | `let max a b = ...` | `let max a = fun b -> ...` |
| `(|>)` | `let (|>) x f = f x` | `let (|>) __pipe_x __pipe_f = __pipe_f __pipe_x` |
| `(>>)` | `let (>>) f g x = g (f x)` | `let (>>) __comp_lhs __comp_rhs = fun __comp_x -> ...` |
| `(<<)` | `let (<<) f g x = f (g x)` | `let (<<) __comp_lhs __comp_rhs = fun __comp_x -> ...` |
| `(<|)` | `let (<|) f x = f x` | `let (<|) __pipe_f __pipe_x = __pipe_f __pipe_x` |

**Action:** Take FunLang v14.0 as base. Append `char_to_int` and `int_to_char`. Decide whether to keep compiler's curried style for `const`/`compose`/`flip`/`apply`/`min`/`max` and `__comp_*` param names for operators — these may be required by compiler internals or can be switched to FunLang style.

**Risk:** The `__comp_lhs`/`__comp_rhs`/`__comp_x` param names and explicit currying for operators may be load-bearing for codegen. Investigate before switching.

---

### HashSet.fun

**Status:** COPY+APPEND

**Diff summary:** Type annotations removed. Two compiler-only functions at the end.

**Compiler-only functions:**
```
let keys hs   = hashset_keys hs
let toList hs = hashset_keys hs
```
Both call the same builtin `hashset_keys`. `toList` is an alias. Must be preserved.

**FunLang-only functions:** None

**Recommendation:** Copy FunLang version (4 functions), append `keys` and `toList`.

---

### Hashtable.fun

**Status:** COPY (safe)

**Diff summary:** Type annotations only. All 8 functions identical in body. Both versions already use uncurried multi-param style.

**Compiler-only functions:** None

**FunLang-only functions:** None

**Recommendation:** Copy FunLang version as-is.

---

### Int.fun

**Status:** COPY (safe)

**Diff summary:** Type annotations only. Both have `parse` and `toString`, identical bodies.

**Compiler-only functions:** None

**FunLang-only functions:** None

**Recommendation:** Copy FunLang version as-is.

---

### List.fun

**Status:** MANUAL_MERGE (largest file, 113 lines)

**Diff summary:** Two categories:

1. Type annotations removed — cosmetic
2. Currying style changed for many functions — semantic

**Compiler-only functions:** None (both versions have identical function sets)

**FunLang-only functions:** None

**Currying differences (functions where callers may partially apply):**

| Function | FunLang v14.0 style | Compiler style |
|----------|---------------------|----------------|
| `map` | `let rec map f xs = ...` | `let rec map f = fun xs -> ...` |
| `filter` | `let rec filter pred xs = ...` | `let rec filter pred = fun xs -> ...` |
| `fold` | `let rec fold f acc xs = ...` | `let rec fold f = fun acc -> fun xs -> ...` |
| `reverse` | `let rec reverse acc xs = ...` | `let rec reverse acc = fun xs -> ...` |
| `append` | `let rec append xs ys = ...` | `let rec append xs = fun ys -> ...` |
| `zip` | `let rec zip xs ys = ...` | `let rec zip xs = fun ys -> ...` |
| `take` | `let rec take n xs = ...` | `let rec take n = fun xs -> ...` |
| `drop` | `let rec drop n xs = ...` | `let rec drop n = fun xs -> ...` |
| `any` | `let rec any pred xs = ...` | `let rec any pred = fun xs -> ...` |
| `all` | `let rec all pred xs = ...` | `let rec all pred = fun xs -> ...` |
| `nth` | `let rec nth n xs = ...` | `let rec nth n = fun xs -> ...` |
| `partition` (inner `go`) | `let rec go yes no xs = ...` | `let rec go yes no = fun xs -> ...` |
| `sumBy` lambda | `fun acc x -> acc + f x` | `fun acc -> fun x -> acc + f x` |
| `sum` lambda | `fun acc x -> acc + x` | `fun acc -> fun x -> acc + x` |
| `minBy` fold lambda | `fun best x -> ...` | `fun best -> fun x -> ...` |
| `maxBy` fold lambda | `fun best x -> ...` | `fun best -> fun x -> ...` |
| `iter` fold lambda | `fun _u x -> f x` | `fun _u -> fun x -> f x` |

**Critical semantic difference — fold lambda style:**

The inline lambdas passed to `fold` inside `sumBy`, `sum`, `minBy`, `maxBy`, `iter` are multi-param in FunLang v14.0 but explicit curried in the compiler. Since the compiler's `fold` expects a curried `f : 'b -> 'a -> 'b` (the argument is applied one at a time), the FunLang v14.0 style `fun acc x -> ...` may work or fail depending on whether the compiler treats `fun a b -> ...` as sugar for `fun a -> fun b -> ...`. This must be verified.

**Recommendation:** Take FunLang v14.0 as base. If the compiler accepts `fun a b -> ...` syntax as curried (it should, given it's a standard FP convention), the switch is safe. If not, keep compiler-style explicit currying for inline lambdas inside `fold` calls.

---

### MutableList.fun

**Status:** COPY+APPEND

**Diff summary:** Type annotations removed. One compiler-only function.

**Compiler-only functions:**
```
let toList ml = mutablelist_tolist ml
```

**FunLang-only functions:** None

**Recommendation:** Copy FunLang version (5 functions), append `toList`.

---

### Option.fun

**Status:** MANUAL_MERGE

**Diff summary:** Type annotations removed. Currying style changed for 6 functions.

**Compiler-only functions:** None

**FunLang-only functions:** None

**Currying differences:**

| Function | FunLang v14.0 | Compiler |
|----------|---------------|----------|
| `optionMap` | `let optionMap f opt = ...` | `let optionMap f = fun opt -> ...` |
| `optionBind` | `let optionBind f opt = ...` | `let optionBind f = fun opt -> ...` |
| `optionDefault` | `let optionDefault def opt = ...` | `let optionDefault def = fun opt -> ...` |
| `optionIter` | `let optionIter f opt = ...` | `let optionIter f = fun opt -> ...` |
| `optionFilter` | `let optionFilter pred opt = ...` | `let optionFilter pred = fun opt -> ...` |
| `optionDefaultValue` | `let optionDefaultValue def opt = ...` | `let optionDefaultValue def = fun opt -> ...` |

**Recommendation:** Take FunLang v14.0 as base if multi-param style is accepted. No functions to append.

---

### Queue.fun

**Status:** COPY (safe)

**Diff summary:** Type annotations only. All 4 functions identical.

**Compiler-only functions:** None

**FunLang-only functions:** None

**Recommendation:** Copy FunLang version as-is.

---

### Result.fun

**Status:** MANUAL_MERGE

**Diff summary:** Type annotations removed. Currying style changed for 5 functions.

**Compiler-only functions:** None

**FunLang-only functions:** None

**Currying differences:**

| Function | FunLang v14.0 | Compiler |
|----------|---------------|----------|
| `resultMap` | `let resultMap f r = ...` | `let resultMap f = fun r -> ...` |
| `resultBind` | `let resultBind f r = ...` | `let resultBind f = fun r -> ...` |
| `resultMapError` | `let resultMapError f r = ...` | `let resultMapError f = fun r -> ...` |
| `resultDefault` | `let resultDefault def r = ...` | `let resultDefault def = fun r -> ...` |
| `resultIter` | `let resultIter f r = ...` | `let resultIter f = fun r -> ...` |
| `resultDefaultValue` | `let resultDefaultValue def r = ...` | `let resultDefaultValue def = fun r -> ...` |

**Recommendation:** Take FunLang v14.0 as base if multi-param style is accepted.

---

### String.fun

**Status:** COPY (safe)

**Diff summary:** Type annotations only. All 15 functions identical.

Note: The compiler String.fun already had `toInt`/`ofInt` added previously; FunLang v14.0 now also has them, so they are in sync.

**Compiler-only functions:** None

**FunLang-only functions:** None

**Recommendation:** Copy FunLang version as-is. This resolves the previously-manual sync.

---

### StringBuilder.fun

**Status:** COPY (safe)

**Diff summary:** Type annotations only. Both functions identical.

**Compiler-only functions:** None

**FunLang-only functions:** None

**Recommendation:** Copy FunLang version as-is.

---

### Typeclass.fun

**Status:** MANUAL_MERGE

**Diff summary:** Type annotations removed. Currying style changed for `Eq` typeclass instances.

**Compiler-only functions:** None

**FunLang-only functions:** None

**Currying differences:**

All four `Eq` instances changed:

| Typeclass | FunLang v14.0 | Compiler |
|-----------|---------------|----------|
| `Eq<int>.eq` | `let eq x y = x = y` | `let eq x = fun y -> x = y` |
| `Eq<bool>.eq` | `let eq x y = x = y` | `let eq x = fun y -> x = y` |
| `Eq<string>.eq` | `let eq x y = x = y` | `let eq x = fun y -> x = y` |
| `Eq<char>.eq` | `let eq x y = x = y` | `let eq x = fun y -> x = y` |

`Show` instances unchanged (single-param, no currying difference).

**Recommendation:** Take FunLang v14.0 as base if multi-param is accepted. No functions to append.

---

## Compiler-Only Functions Catalogue

These functions exist only in the compiler prelude and must survive the sync:

| File | Function | Implementation | Purpose |
|------|----------|---------------|---------|
| Core.fun | `char_to_int` | `let char_to_int c = c` | Codegen shim — char as int |
| Core.fun | `int_to_char` | `let int_to_char n = n` | Codegen shim — int as char |
| HashSet.fun | `keys` | `let keys hs = hashset_keys hs` | Expose hashset_keys |
| HashSet.fun | `toList` | `let toList hs = hashset_keys hs` | Alias of keys |
| MutableList.fun | `toList` | `let toList ml = mutablelist_tolist ml` | Expose mutablelist_tolist |

**Total compiler-only functions: 5**

---

## FunLang-Only Functions Catalogue

There are **zero** functions that exist in FunLang v14.0 but not in the compiler prelude.

FunLang v14.0 is a strict subset of the compiler prelude (plus the style differences above).

---

## Critical Decision: Currying Style

The central question for this sync is whether to adopt FunLang v14.0's multi-param style or keep the compiler's explicit curried style.

**FunLang v14.0 style:** `let fold f acc xs = ...` / `fun acc x -> ...`

**Compiler style:** `let fold f = fun acc -> fun xs -> ...` / `fun acc -> fun x -> ...`

Both are semantically equivalent IF the compiler's parser/codegen treats `fun a b -> e` as sugar for `fun a -> fun b -> e`. If yes, the FunLang v14.0 style is safe everywhere. If not (if the compiler is strict about currying depth for partial application), the explicit style must be kept.

This decision affects: List.fun (17 functions), Option.fun (6), Result.fun (6), Core.fun (6), Typeclass.fun (4) — 39 function definitions.

**Recommendation:** Test one file (e.g., Option.fun copied with FunLang style) against the full E2E suite before committing to the approach for all files.

---

## Per-File Recommendations Summary

| File | Recommendation | Compiler-only to append | Complexity |
|------|---------------|------------------------|------------|
| Array.fun | COPY | none | trivial |
| Char.fun | COPY | none | trivial |
| Core.fun | MANUAL_MERGE | `char_to_int`, `int_to_char` | medium — operator param names + shims |
| HashSet.fun | COPY+APPEND | `keys`, `toList` | low |
| Hashtable.fun | COPY | none | trivial |
| Int.fun | COPY | none | trivial |
| List.fun | MANUAL_MERGE | none | high — 17 curried functions + inline lambdas |
| MutableList.fun | COPY+APPEND | `toList` | low |
| Option.fun | MANUAL_MERGE | none | medium — 6 curried functions |
| Queue.fun | COPY | none | trivial |
| Result.fun | MANUAL_MERGE | none | medium — 6 curried functions |
| String.fun | COPY | none | trivial |
| StringBuilder.fun | COPY | none | trivial |
| Typeclass.fun | MANUAL_MERGE | none | low — 4 Eq instances |

**COPY (trivial):** Array, Char, Hashtable, Int, Queue, String, StringBuilder — 7 files, ~60 lines total

**COPY+APPEND (low):** HashSet, MutableList — 2 files, ~12 lines total, 3 functions to append

**MANUAL_MERGE (needs currying decision):** Core, List, Option, Result, Typeclass — 5 files

---

## Total Change Scope Estimate

- Lines changed by type annotation removal: ~120 lines (cosmetic, search-replace)
- Lines changed by currying style: ~50 lines (semantic, requires decision)
- Lines added (compiler-only appends): 5 lines
- Total lines touched: ~175 of 264 total prelude lines

The sync is purely subtractive + cosmetic from FunLang's perspective. No new logic, no new builtins required.

---

## Recommended Sync Order

1. Copy the 7 trivial COPY files first — zero risk
2. COPY+APPEND HashSet and MutableList — low risk
3. Test currying style with one MANUAL_MERGE file (Option.fun recommended — small, isolated)
4. If E2E passes, apply FunLang style to remaining MANUAL_MERGE files (List, Result, Typeclass)
5. Handle Core.fun last — most complex due to operator param names and `char_to_int`/`int_to_char` shims
