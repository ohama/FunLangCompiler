# FunLang v14.0 Type System Pitfalls

**Domain:** FunLangCompiler — syncing with FunLang v14.0 new mutable collection types
**Researched:** 2026-04-09

---

## Critical Pitfalls

### Pitfall 1: Incomplete pattern match in `typeNeedsPtr` — the known build warning

**File:** `src/FunLangCompiler.Compiler/ElabHelpers.fs:618`

**What goes wrong:**
`typeNeedsPtr` matches on `Type.Type` to decide whether a value needs a pointer-sized slot (Ptr) vs an integer slot (I64). The match is missing the four new cases added in FunLang v14.0:

```fsharp
// Current (line 618–624) — INCOMPLETE
let typeNeedsPtr (ty: Type.Type) : bool =
    match ty with
    | Type.TString | Type.TList _ | Type.TArray _ | Type.THashtable _
    | Type.TArrow _ | Type.TExn -> true
    | Type.TTuple elems -> not elems.IsEmpty
    | Type.TData _ -> true
    | Type.TInt | Type.TBool | Type.TChar | Type.TError -> false
    | Type.TVar _ -> false
    // MISSING: THashSet, TQueue, TMutableList, TStringBuilder
```

**Why it happens:**
FunLang v14.0 (Phase 94) added four new `Type.Type` discriminated union cases:
- `THashSet of Type` — hashset
- `TQueue of Type` — queue
- `TMutableList of Type` — mutablelist
- `TStringBuilder` — stringbuilder (no type arg)

F# DU exhaustiveness checking caught only `THashSet` in the warning, but all four are missing.

**Consequences:**
- F# falls through to the wildcard-free match — the compiler will throw a `MatchFailureException` at runtime if any of the four new types reach `typeNeedsPtr` through the annotation map path.
- Because `Bidir.fs` currently returns `TData("HashSet",[])` / `TData("Queue",[])` / etc. (not the new union cases), this only triggers when a variable/parameter has an **explicit type annotation** using `hashset`, `queue`, `mutablelist`, or `stringbuilder` keywords (which go through `Elaborate.fs` and resolve to the new union cases).

**Prevention / Fix:**
Add all four cases to the match. All four represent heap-allocated objects and must return `true`:

```fsharp
let typeNeedsPtr (ty: Type.Type) : bool =
    match ty with
    | Type.TString | Type.TList _ | Type.TArray _ | Type.THashtable _
    | Type.THashSet _ | Type.TQueue _ | Type.TMutableList _ | Type.TStringBuilder
    | Type.TArrow _ | Type.TExn -> true
    | Type.TTuple elems -> not elems.IsEmpty
    | Type.TData _ -> true
    | Type.TInt | Type.TBool | Type.TChar | Type.TError -> false
    | Type.TVar _ -> false
```

**Detection:** Build warning `FS0025` at `ElabHelpers.fs(618,11)`.

---

### Pitfall 2: `detectCollectionKind` does not handle new dedicated union cases

**File:** `src/FunLangCompiler.Compiler/ElabHelpers.fs:131–146`

**What goes wrong:**
`detectCollectionKind` uses the annotation map to infer collection kind, but currently only matches `TData("HashSet",_)`, `TData("Queue",_)`, `TData("MutableList",_)`. It does **not** match `THashSet`, `TQueue`, `TMutableList`.

```fsharp
// Current — only handles TData-based inference
| Some (Type.TData("HashSet", _)) -> Some HashSet
| Some (Type.TData("Queue", _))   -> Some Queue
| Some (Type.TData("MutableList", _)) -> Some MutableList
| Some (Type.THashtable _) -> Some Hashtable
| Some _ -> None     // <-- THashSet/TQueue/TMutableList fall here → None
```

**Why it happens:**
- `Bidir.fs` (constructor expression inference, e.g. `let x = HashSet()`) still produces `TData("HashSet", [])`.
- `Elaborate.fs` (explicit type annotations, e.g. `let x : int hashset = ...`) produces `THashSet (TInt)`.
- So a variable explicitly annotated with a hashset/queue/mutablelist type will return `None` from `detectCollectionKind`, causing incorrect method dispatch for `.add`, `.remove`, etc.

**Consequences:**
Method calls on explicitly-typed mutable collections may fail silently (fall into a default code path instead of the correct collection-specific one).

**Prevention / Fix:**
Add the new union cases alongside the existing `TData`-based cases:

```fsharp
| Some (Type.THashSet _)              -> Some HashSet
| Some (Type.TQueue _)                -> Some Queue
| Some (Type.TMutableList _)          -> Some MutableList
| Some (Type.TData("HashSet", _))     -> Some HashSet
| Some (Type.TData("Queue", _))       -> Some Queue
| Some (Type.TData("MutableList", _)) -> Some MutableList
| Some (Type.THashtable _)            -> Some Hashtable
```

**Detection:** No build warning (the `Some _ -> None` arm silently swallows these types). Will only manifest as a runtime bug when explicit type annotations are used.

---

## Moderate Pitfalls

### Pitfall 3: No compiler-side `TStringBuilder` detection — currently masked

**What goes wrong:**
`TStringBuilder` is used only via explicit type annotation (`stringbuilder`). The compiler currently has no path that needs to detect "this is a StringBuilder" from the annotation map, so there is no immediate crash. However, if future work adds `isStringBuilderExpr` or similar detection, the same gap will appear.

**Prevention:**
When writing any new type-dispatch function in `ElabHelpers.fs`, always handle all nine concrete collection types:
`TString`, `TList`, `TArray`, `THashtable`, `THashSet`, `TQueue`, `TMutableList`, `TStringBuilder`, `TData`.

---

### Pitfall 4: `Bidir.fs` and `Elaborate.fs` produce different types for the same concept

**What goes wrong:**
The same collection (e.g., a hashset) can appear in the annotation map as either `TData("HashSet",[])` (from constructor inference via Bidir) or `THashSet t` (from an explicit type annotation via Elaborate). Any compiler code that checks type from the annotation map must handle both representations.

**Why it happens:**
`Bidir.fs` predates Phase 94 and has not been updated to produce the new dedicated union cases. It produces `TData("HashSet",[])` for constructor expressions like `HashSet()`. `Elaborate.fs` was updated in v14.0 to map `hashset` type annotations to `THashSet`.

**Consequences:**
- Pattern matching only on `TData("HashSet",_)` misses annotated variables.
- Pattern matching only on `THashSet _` misses inferred variables.
- Both arms are needed until FunLang updates Bidir to produce `THashSet`/`TQueue`/`TMutableList`/`TStringBuilder` directly.

**Detection:** No build warning. Tests with explicit type annotations on mutable collection variables will expose the bug.

---

## Phase-Specific Warnings

| Phase Topic | Likely Pitfall | Mitigation |
|---|---|---|
| Fix `typeNeedsPtr` warning | Missing `TStringBuilder` is also absent — easy to miss | Add all four cases in one edit |
| `detectCollectionKind` | Silent wrong dispatch for annotated collections | Add both `THashSet`/`TData("HashSet")` arms |
| Future `isHashSetExpr` helpers | Will repeat the dual-representation problem | Always check both `THashSet _` and `TData("HashSet",_)` |
| `isPtrParamTyped` (line 629) | Calls `typeNeedsPtr` — will inherit the fix once `typeNeedsPtr` is updated | No extra work needed after Pitfall 1 fix |

---

## Files Requiring Changes

| File | Location | Change Required |
|---|---|---|
| `ElabHelpers.fs` | Line 618–624 (`typeNeedsPtr`) | Add `THashSet _`, `TQueue _`, `TMutableList _`, `TStringBuilder` to `true` branch |
| `ElabHelpers.fs` | Line 132–136 (`detectCollectionKind`) | Add `THashSet _`, `TQueue _`, `TMutableList _` match arms before `Some _ -> None` |

No changes are needed in `Elaboration.fs`, `ElabProgram.fs`, `MlirIR.fs`, or `Pipeline.fs` — none of them reference the new union cases directly, and they do not perform type-dispatch on Type.Type values.

---

## New Types Summary (FunLang v14.0 / Phase 94)

| Type | DU Case | Type Arg | Heap? | Already in Bidir output? |
|---|---|---|---|---|
| hashset | `THashSet of Type` | element type | Yes | No — Bidir emits `TData("HashSet",[])` |
| queue | `TQueue of Type` | element type | Yes | No — Bidir emits `TData("Queue",[])` |
| mutablelist | `TMutableList of Type` | element type | Yes | No — Bidir emits `TData("MutableList",[])` |
| stringbuilder | `TStringBuilder` | none | Yes | No — Bidir emits `TData("StringBuilder",[])` |

All four types originate only from **explicit type annotations** in the compiler pipeline (going through `Elaborate.fs`). Inference-only code paths (where FunLang Bidir resolves types) still produce `TData` variants.

---

## Sources

- `deps/FunLang/src/FunLang/Type.fs` — DU definition (lines 16–19)
- `deps/FunLang/src/FunLang/Elaborate.fs` — type annotation elaboration (lines 59, 78–80, 106, 122–124)
- `deps/FunLang/src/FunLang/Bidir.fs` — constructor inference (lines 199, 210, 221, 232 — still uses `TData`)
- `src/FunLangCompiler.Compiler/ElabHelpers.fs` — `typeNeedsPtr` (line 618), `detectCollectionKind` (line 131)
- Build warning: `FS0025` at `ElabHelpers.fs(618,11)`
