/// Types, environment, heuristic functions, and helpers for the elaboration pass.
module ElabHelpers

open Ast
open MlirIR
open MatchCompiler

// Phase 5: Closure metadata — distinguishes closure-making funcs from direct-call funcs
type ClosureInfo = {
    InnerLambdaFn: string    // MLIR name of the llvm.func body, e.g. "@closure_fn_0"
    NumCaptures:   int       // number of captured variables
    InnerReturnIsBool: bool  // Phase 43: inner closure returns bool (for to_string dispatch)
    CaptureNames: string list   // Phase 64: ordered capture variable names (for caller-side stores)
    OuterParamName: string      // Phase 64: which capture the maker stores (skip at call site)
}

type FuncSignature = {
    MlirName:    string
    ParamTypes:  MlirType list
    ReturnType:  MlirType
    ClosureInfo: ClosureInfo option  // None = direct-call func; Some = closure-maker
    ReturnIsBool: bool       // Phase 43: direct return is bool (for to_string dispatch)
    InnerReturnIsBool: bool  // Phase 43: 2-param func's inner closure returns bool
}

// Phase 16: TypeInfo for ADT constructor entries in TypeEnv
type TypeInfo = { Tag: int; Arity: int }

// Phase 34-03: LANG-03/04 — collection kind for ForInExpr dispatch
type CollectionKind = HashSet | Queue | MutableList | Hashtable

type ElabEnv = {
    Vars:           Map<string, MlirValue>
    Counter:        int ref
    LabelCounter:   int ref
    Blocks:         MlirBlock list ref
    KnownFuncs:     Map<string, FuncSignature>
    Funcs:          FuncOp list ref
    ClosureCounter: int ref   // Phase 5: generates unique closure function names
    Globals:        (string * string) list ref   // Phase 7: (name, rawValue) pairs for string constants
    GlobalCounter:  int ref                       // Phase 7: counter for unique global names
    // Phase 65: Mutable global ptr vars for template envs (so LetRec body func.funcs can load them)
    TplGlobals:     string list ref               // names of MutablePtrGlobal entries emitted
    // Phase 16: Declaration environment — populated by prePassDecls before elaboration
    TypeEnv:        Map<string, TypeInfo>            // constructor name -> tag + arity
    RecordEnv:      Map<string, Map<string, int>>    // record type name -> (field name -> index)
    ExnTags:        Map<string, int>                 // exception ctor name -> tag index
    // Phase 21: Mutable variable tracking — names that live in GC ref cells
    MutableVars:    Set<string>
    // Phase 30: Array variable tracking — names bound to array-type collections (for for-in dispatch)
    ArrayVars:      Set<string>
    // Phase 34-03: Collection variable tracking — names bound to Phase 33 collection types
    CollectionVars: Map<string, CollectionKind>
    // Phase 43: Bool variable tracking — names bound to boolean-producing expressions
    BoolVars: Set<string>
    // Phase 66: String variable tracking — names bound to string-typed values (for IndexGet dispatch)
    StringVars: Set<string>
    // Phase 66: Record field string tracking — field names known to be string-typed (for IndexGet dispatch)
    StringFields: Set<string>
    // Phase 67: Typed AST annotation map from FunLang type inference (Span → Type)
    AnnotationMap: Map<Ast.Span, Type.Type>
}

let emptyEnv () : ElabEnv =
    { Vars = Map.empty; Counter = ref 0; LabelCounter = ref 0; Blocks = ref []
      KnownFuncs = Map.empty; Funcs = ref []; ClosureCounter = ref 0
      Globals = ref []; GlobalCounter = ref 0; TplGlobals = ref []
      TypeEnv = Map.empty; RecordEnv = Map.empty; ExnTags = Map.empty
      MutableVars = Set.empty; ArrayVars = Set.empty; CollectionVars = Map.empty
      BoolVars = Set.empty; StringVars = Set.empty; StringFields = Set.empty
      AnnotationMap = Map.empty }

/// Phase 44: Raise an error with source location in "file:line:col: message" format.
/// Uses Printf.ksprintf to support format strings like failwithf.
let inline failWithSpan (span: Ast.Span) fmt =
    Printf.ksprintf (fun msg ->
        let loc = sprintf "%s:%d:%d" span.FileName span.StartLine span.StartColumn
        failwith (sprintf "[Elaboration] %s: %s" loc msg)
    ) fmt

/// Phase 67: Look up the inferred type of an expression from the AnnotationMap.
/// Returns None if the expression's span is not in the map (type inference failed or unavailable).
let inferredType (annotationMap: Map<Ast.Span, Type.Type>) (expr: Ast.Expr) : Type.Type option =
    Map.tryFind (Ast.spanOf expr) annotationMap

/// Phase 67: Check if an expression's inferred type satisfies a predicate.
/// Returns Some true/false if type is known, None if not in AnnotationMap (use fallback).
let checkInferredType (annotationMap: Map<Ast.Span, Type.Type>) (predicate: Type.Type -> bool) (expr: Ast.Expr) : bool option =
    match inferredType annotationMap expr with
    | Some ty -> Some (predicate ty)
    | None -> None

/// Phase 30: Determine if an expression is statically known to produce an array (not a list).
/// Used by ForInExpr to select lang_for_in_array vs lang_for_in_list at compile time.
/// Conservative: returns false (assume list) for variables or unknown expressions.
let rec isArrayExpr (arrayVars: Set<string>) (annotationMap: Map<Ast.Span, Type.Type>) (expr: Ast.Expr) : bool =
    match checkInferredType annotationMap (function Type.TArray _ -> true | _ -> false) expr with
    | Some result -> result
    | None ->
    match expr with
    | Ast.App (Ast.Var ("array_of_list", _), _, _)
    | Ast.App (Ast.Var ("array_create", _), _, _)
    | Ast.App (Ast.Var ("array_init", _), _, _)
    | Ast.App (Ast.App (Ast.Var ("array_create", _), _, _), _, _)
    | Ast.App (Ast.App (Ast.Var ("array_init", _), _, _), _, _) -> true
    | Ast.Var (name, _) -> Set.contains name arrayVars
    | Ast.Annot (inner, _, _) -> isArrayExpr arrayVars annotationMap inner
    | _ -> false

/// Phase 66/67: Detect if an expression produces a string value.
/// Phase 67: First checks AnnotationMap from type inference; falls back to heuristic.
let rec isStringExpr (stringVars: Set<string>) (stringFields: Set<string>) (annotationMap: Map<Ast.Span, Type.Type>) (expr: Ast.Expr) : bool =
    match checkInferredType annotationMap ((=) Type.TString) expr with
    | Some result -> result
    | None ->
    match expr with
    | Ast.String _ -> true
    | Ast.Var (name, _) -> Set.contains name stringVars
    | Ast.Annot (inner, _, _) -> isStringExpr stringVars stringFields annotationMap inner
    | Ast.FieldAccess (_, fieldName, _) -> Set.contains fieldName stringFields
    | _ -> false

/// Phase 34-03: Detect which Phase 33 collection kind an expression produces.
/// Returns Some kind if the expression is a known collection-creating call or variable.
let rec detectCollectionKind (collVars: Map<string, CollectionKind>) (annotationMap: Map<Ast.Span, Type.Type>) (expr: Ast.Expr) : CollectionKind option =
    match inferredType annotationMap expr with
    | Some (Type.TData("HashSet", _)) -> Some HashSet
    | Some (Type.TData("Queue", _)) -> Some Queue
    | Some (Type.TData("MutableList", _)) -> Some MutableList
    | Some (Type.THashtable _) -> Some Hashtable
    | Some _ -> None
    | None ->
    match expr with
    | Ast.App (Ast.Var ("hashset_create", _), _, _)   -> Some HashSet
    | Ast.App (Ast.Var ("queue_create", _), _, _)     -> Some Queue
    | Ast.App (Ast.Var ("mutablelist_create", _), _, _) -> Some MutableList
    | Ast.App (Ast.Var ("hashtable_create", _), _, _)     -> Some Hashtable
    | Ast.Var (name, _)                                   -> Map.tryFind name collVars
    | Ast.Annot (inner, _, _)                         -> detectCollectionKind collVars annotationMap inner
    | _ -> None

let addStringGlobal (env: ElabEnv) (rawValue: string) : string =
    match env.Globals.Value |> List.tryFind (fun (_, v) -> v = rawValue) with
    | Some (name, _) -> name
    | None ->
        let idx = env.GlobalCounter.Value
        env.GlobalCounter.Value <- idx + 1
        let name = sprintf "@__str_%d" idx
        env.Globals.Value <- env.Globals.Value @ [(name, rawValue)]
        name

let freshName (env: ElabEnv) : string =
    let n = env.Counter.Value
    env.Counter.Value <- n + 1
    sprintf "%%t%d" n

let freshLabel (env: ElabEnv) (prefix: string) : string =
    let n = env.LabelCounter.Value
    env.LabelCounter.Value <- n + 1
    sprintf "%s%d" prefix n

/// Phase 88: Tag a compile-time constant: 2n+1
let tagConst (n: int64) : int64 = n * 2L + 1L

/// Phase 88: Emit untag sequence: (tagged_val >> 1) -> raw value
let emitUntag (env: ElabEnv) (v: MlirValue) : MlirValue * MlirOp list =
    let one = { Name = freshName env; Type = I64 }
    let result = { Name = freshName env; Type = I64 }
    (result, [ArithConstantOp(one, 1L); ArithShRSIOp(result, v, one)])

/// Phase 88: Emit retag sequence: (raw_val << 1) | 1 -> tagged value
let emitRetag (env: ElabEnv) (v: MlirValue) : MlirValue * MlirOp list =
    let one = { Name = freshName env; Type = I64 }
    let shifted = { Name = freshName env; Type = I64 }
    let result = { Name = freshName env; Type = I64 }
    (result, [ArithConstantOp(one, 1L); ArithShLIOp(shifted, v, one); ArithOrIOp(result, shifted, one)])

// Phase 8: Allocate a heap string struct {i64 length, ptr data} for a compile-time string literal.
let elaborateStringLiteral (env: ElabEnv) (s: string) : MlirValue * MlirOp list =
    let globalName  = addStringGlobal env s
    let sizeVal     = { Name = freshName env; Type = I64 }
    let headerVal   = { Name = freshName env; Type = Ptr }
    let lenPtrVal   = { Name = freshName env; Type = Ptr }
    let lenVal      = { Name = freshName env; Type = I64 }
    let dataPtrVal  = { Name = freshName env; Type = Ptr }
    let dataAddrVal = { Name = freshName env; Type = Ptr }
    let ops = [
        ArithConstantOp(sizeVal, 16L)                              // struct size = 16 bytes
        LlvmCallOp(headerVal, "@GC_malloc", [sizeVal])             // alloc header
        LlvmGEPStructOp(lenPtrVal, headerVal, 0)                   // &header.length
        ArithConstantOp(lenVal, int64 s.Length)                    // compile-time length
        LlvmStoreOp(lenVal, lenPtrVal)                             // header.length = len
        LlvmGEPStructOp(dataPtrVal, headerVal, 1)                  // &header.data
        LlvmAddressOfOp(dataAddrVal, globalName)                   // addressof @__str_N
        LlvmStoreOp(dataAddrVal, dataPtrVal)                       // header.data = &global
    ]
    (headerVal, ops)

// Phase 5: Free variable analysis — computes free variables in expr relative to boundVars
let rec freeVars (boundVars: Set<string>) (expr: Expr) : Set<string> =
    match expr with
    | Number _ | Bool _ -> Set.empty
    | Var (name, _) ->
        if Set.contains name boundVars then Set.empty else Set.singleton name
    | Add (l, r, _) | Subtract (l, r, _) | Multiply (l, r, _) | Divide (l, r, _)
    | Equal (l, r, _) | NotEqual (l, r, _) | LessThan (l, r, _) | GreaterThan (l, r, _)
    | LessEqual (l, r, _) | GreaterEqual (l, r, _) | And (l, r, _) | Or (l, r, _) ->
        Set.union (freeVars boundVars l) (freeVars boundVars r)
    | Negate (e, _) -> freeVars boundVars e
    | If (c, t, e, _) ->
        Set.unionMany [ freeVars boundVars c; freeVars boundVars t; freeVars boundVars e ]
    | Let (name, e1, e2, _) ->
        Set.union (freeVars boundVars e1) (freeVars (Set.add name boundVars) e2)
    | Lambda (param, body, _) ->
        freeVars (Set.add param boundVars) body
    | App (f, a, _) ->
        Set.union (freeVars boundVars f) (freeVars boundVars a)
    | LetRec (bindings, inExpr, _) ->
        let names = bindings |> List.map (fun (n, _, _, _, _) -> n) |> Set.ofList
        let innerBound = bindings |> List.fold (fun s (n, p, _, _, _) -> Set.add n (Set.add p s)) boundVars
        let bodyFree = bindings |> List.map (fun (_, _, _, body, _) -> freeVars innerBound body) |> Set.unionMany
        let inFree = freeVars (Set.union boundVars names) inExpr
        Set.union bodyFree inFree
    | String _ -> Set.empty
    | Tuple (exprs, _) ->
        exprs |> List.map (freeVars boundVars) |> Set.unionMany
    | LetPat (WildcardPat _, bindExpr, bodyExpr, _) ->
        Set.union (freeVars boundVars bindExpr) (freeVars boundVars bodyExpr)
    | LetPat (VarPat (name, _), bindExpr, bodyExpr, _) ->
        Set.union (freeVars boundVars bindExpr) (freeVars (Set.add name boundVars) bodyExpr)
    | LetPat (TuplePat (pats, _), bindExpr, bodyExpr, _) ->
        let bindFree = freeVars boundVars bindExpr
        let extractVarName p = match p with | VarPat (name, _) -> [name] | _ -> []
        let patBound = pats |> List.collect extractVarName |> Set.ofList
        let bodyFree = freeVars (Set.union boundVars patBound) bodyExpr
        Set.union bindFree bodyFree
    | Modulo (l, r, _) ->
        Set.union (freeVars boundVars l) (freeVars boundVars r)
    | Constructor(_, None, _) -> Set.empty
    | Constructor(_, Some argExpr, _) -> freeVars boundVars argExpr
    | RecordExpr(_, fields, _) ->
        fields |> List.map (fun (_, e) -> freeVars boundVars e) |> Set.unionMany
    | FieldAccess(recExpr, _, _) ->
        freeVars boundVars recExpr
    | RecordUpdate(sourceExpr, overrides, _) ->
        let srcFree = freeVars boundVars sourceExpr
        let overFree = overrides |> List.map (fun (_, e) -> freeVars boundVars e) |> Set.unionMany
        Set.union srcFree overFree
    | SetField(recExpr, _, valueExpr, _) ->
        Set.union (freeVars boundVars recExpr) (freeVars boundVars valueExpr)
    | Raise(e, _) -> freeVars boundVars e
    | TryWith(body, clauses, _) ->
        let bodyFree = freeVars boundVars body
        let clauseFree =
            clauses |> List.map (fun (pat, guardOpt, armBody) ->
                let rec patBoundVars p =
                    match p with
                    | VarPat(n, _) -> Set.singleton n
                    | TuplePat(ps, _) -> ps |> List.map patBoundVars |> Set.unionMany
                    | ConsPat(h, t, _) -> Set.union (patBoundVars h) (patBoundVars t)
                    | ConstructorPat(_, Some inner, _) -> patBoundVars inner
                    | _ -> Set.empty
                let patBound = patBoundVars pat
                let armBound = Set.union boundVars patBound
                let guardFree = match guardOpt with Some g -> freeVars armBound g | None -> Set.empty
                Set.union guardFree (freeVars armBound armBody)
            ) |> Set.unionMany
        Set.union bodyFree clauseFree
    | LetMut (name, initExpr, bodyExpr, _) ->
        Set.union (freeVars boundVars initExpr)
                  (freeVars (Set.add name boundVars) bodyExpr)
    | Assign (name, valExpr, _) ->
        let nameFree =
            if Set.contains name boundVars then Set.empty
            else Set.singleton name
        Set.union nameFree (freeVars boundVars valExpr)
    | IndexGet (collExpr, idxExpr, _) ->
        Set.union (freeVars boundVars collExpr) (freeVars boundVars idxExpr)
    | IndexSet (collExpr, idxExpr, valExpr, _) ->
        Set.unionMany [freeVars boundVars collExpr; freeVars boundVars idxExpr; freeVars boundVars valExpr]
    | WhileExpr (cond, body, _) ->
        Set.union (freeVars boundVars cond) (freeVars boundVars body)
    | ForExpr (var, startExpr, _, stopExpr, body, _) ->
        Set.unionMany [
            freeVars boundVars startExpr
            freeVars boundVars stopExpr
            freeVars (Set.add var boundVars) body   // var is bound inside body
        ]
    | ForInExpr (var, collExpr, body, _) ->
        let varName = match var with Ast.VarPat(n, _) -> n | _ -> "_"
        Set.union (freeVars boundVars collExpr) (freeVars (Set.add varName boundVars) body)
    | Annot (expr, _, _) -> freeVars boundVars expr
    | LambdaAnnot (param, _, body, _) -> freeVars (Set.add param boundVars) body
    | Match(scrutineeExpr, clauses, _) ->
        let scrutFree = freeVars boundVars scrutineeExpr
        let clauseFree =
            clauses |> List.map (fun (pat, guardOpt, armBody) ->
                let rec patBoundVars p =
                    match p with
                    | VarPat(n, _) -> Set.singleton n
                    | TuplePat(ps, _) -> ps |> List.map patBoundVars |> Set.unionMany
                    | ConsPat(h, t, _) -> Set.union (patBoundVars h) (patBoundVars t)
                    | ConstructorPat(_, Some inner, _) -> patBoundVars inner
                    | _ -> Set.empty
                let patBound = patBoundVars pat
                let armBound = Set.union boundVars patBound
                let guardFree = match guardOpt with Some g -> freeVars armBound g | None -> Set.empty
                Set.union guardFree (freeVars armBound armBody)
            ) |> Set.unionMany
        Set.union scrutFree clauseFree
    | Cons (headExpr, tailExpr, _) ->
        Set.union (freeVars boundVars headExpr) (freeVars boundVars tailExpr)
    | List (elems, _) ->
        elems |> List.map (freeVars boundVars) |> Set.unionMany
    | EmptyList _ -> Set.empty
    | Char _ -> Set.empty
    | _ -> Set.empty  // conservative: other exprs (Char, etc.) have no free vars

/// Phase 35: Coerce a value to I64 for uniform closure return type.
/// I1 → zext to I64; Ptr → ptrtoint to I64; I64 → no-op.
let coerceToI64 (env: ElabEnv) (v: MlirValue) : (MlirValue * MlirOp list) =
    match v.Type with
    | I64 -> (v, [])
    | I1  ->
        // Phase 88: zext I1 to I64, then retag (0→1, 1→3) for tagged representation.
        // This produces 4 ops: zext + const(1) + shl + ori
        let ext = { Name = freshName env; Type = I64 }
        let (tagged, retagOps) = emitRetag env ext
        (tagged, [ArithExtuIOp(ext, v)] @ retagOps)
    | Ptr ->
        let r = { Name = freshName env; Type = I64 }
        (r, [LlvmPtrToIntOp(r, v)])
    | _ -> (v, [])  // I32 or other — pass through unchanged

/// Phase 35: Coerce a value to Ptr for builtin functions that expect pointer arguments.
/// I64 → inttoptr; Ptr → no-op.
let coerceToPtrArg (env: ElabEnv) (v: MlirValue) : (MlirValue * MlirOp list) =
    match v.Type with
    | Ptr -> (v, [])
    | I64 ->
        let r = { Name = freshName env; Type = Ptr }
        (r, [LlvmIntToPtrOp(r, v)])
    | _ -> (v, [])

/// Phase 67: Check if an MLIR op is a block terminator (branch or unreachable).
/// Used by If/And/Or/Let/Match/App handlers to detect when continuation ops
/// must go into a merge block rather than being appended inline.
let isTerminatorOp (op: MlirOp) : bool =
    match op with
    | CfBrOp _ | CfCondBrOp _ | LlvmUnreachableOp -> true
    | _ -> false

/// Phase 67: Append ops to a specific block in env.Blocks by index.
/// Common pattern: when elaborated ops end with a terminator, continuation ops
/// must go into the merge block (at targetIdx) rather than being appended inline.
let appendToBlock (env: ElabEnv) (targetIdx: int) (ops: MlirOp list) : unit =
    let allBlocks = env.Blocks.Value
    let target = allBlocks.[targetIdx]
    let patched = { target with Body = target.Body @ ops }
    env.Blocks.Value <- allBlocks |> List.mapi (fun i b -> if i = targetIdx then patched else b)

/// Phase 67: Emit a void C runtime call and return unit (0 : i64).
/// Common pattern for builtins like write_file, eprint, hashtable_remove, etc.
let emitVoidCall (env: ElabEnv) (funcName: string) (args: MlirValue list) : MlirValue * MlirOp list =
    let unitVal = { Name = freshName env; Type = I64 }
    (unitVal, [LlvmCallVoidOp(funcName, args); ArithConstantOp(unitVal, 1L)])

/// Phase 67: Elaborate a sub-expression, tracking block count before/after.
/// If the result ops end with a terminator and blocks were created,
/// append contOps to the merge block and return only the entry ops.
/// Otherwise, append contOps inline after the result ops.
let elaborateWithCont (env: ElabEnv) (elaborateExpr: ElabEnv -> Ast.Expr -> MlirValue * MlirOp list)
                      (expr: Ast.Expr) (contOps: MlirValue -> MlirOp list) : MlirValue * MlirOp list =
    let blocksBefore = env.Blocks.Value.Length
    let (v, ops) = elaborateExpr env expr
    let blocksAfter = env.Blocks.Value.Length
    let cont = contOps v
    match List.tryLast ops with
    | Some op when isTerminatorOp op && blocksAfter > blocksBefore ->
        appendToBlock env (blocksAfter - 1) cont
        (v, ops)
    | _ ->
        (v, ops @ cont)

/// Phase 39: Format specifier type for compile-time dispatch.
type FmtSpec = IntSpec | StrSpec

/// Phase 39: Return ordered list of format specifier types in a format string.
/// %d, %x, %02x, %c, %ld, %lx → IntSpec; %s → StrSpec; %% → ignored.
let fmtSpecTypes (fmt: string) : FmtSpec list =
    let specs = System.Collections.Generic.List<FmtSpec>()
    let mutable i = 0
    while i < fmt.Length do
        if fmt.[i] = '%' && i + 1 < fmt.Length then
            if fmt.[i+1] = '%' then
                i <- i + 2
            else
                let mutable j = i + 1
                // Skip flags: -, +, space, 0, #
                while j < fmt.Length && "-+ 0#".Contains(fmt.[j]) do j <- j + 1
                // Skip width digits
                while j < fmt.Length && fmt.[j] >= '0' && fmt.[j] <= '9' do j <- j + 1
                // Skip precision
                if j < fmt.Length && fmt.[j] = '.' then
                    j <- j + 1
                    while j < fmt.Length && fmt.[j] >= '0' && fmt.[j] <= '9' do j <- j + 1
                // Skip length modifiers: l, h, z, t
                while j < fmt.Length && "lhzt".Contains(fmt.[j]) do j <- j + 1
                // Conversion character
                if j < fmt.Length then
                    match fmt.[j] with
                    | 's' -> specs.Add(StrSpec)
                    | _   -> specs.Add(IntSpec)  // d, i, o, u, x, X, c, p all map to i64
                    j <- j + 1
                i <- j
        else
            i <- i + 1
    specs |> Seq.toList

/// Phase 39: Coerce a value to raw I64 for int-typed sprintf wrapper args (C boundary).
/// Phase 88: All integer args are now tagged — untag before passing to C.
/// I1 → zext to I64 (raw 0/1); I64 → untag; Ptr → ptrtoint; I32 → pass through.
let coerceToI64Arg (env: ElabEnv) (v: MlirValue) : (MlirValue * MlirOp list) =
    match v.Type with
    | I64 -> emitUntag env v
    | I1  ->
        let ext = { Name = freshName env; Type = I64 }
        (ext, [ArithExtuIOp(ext, v)])
    | Ptr ->
        let i = { Name = freshName env; Type = I64 }
        (i, [LlvmPtrToIntOp(i, v)])
    | I32 ->
        (v, [])  // treat as I64 (should not normally arise)

/// Phase 43: Strip Annot/LambdaAnnot wrappers from the outermost position of an expression.
/// Converts LambdaAnnot to Lambda, removing type annotations.
/// Used for pattern matching that needs to see through type annotation wrappers.
let rec stripAnnot (expr: Expr) : Expr =
    match expr with
    | Annot (inner, _, _) -> stripAnnot inner
    | LambdaAnnot (param, _, body, span) -> Lambda(param, stripAnnot body, span)
    | _ -> expr

/// Phase 43: Active pattern for stripAnnot — use in match expressions to see through annotations.
let (|StripAnnot|) (expr: Expr) = stripAnnot expr

/// Detect whether a LetRec/Lambda body uses list patterns on the parameter,
/// indicating the parameter should be typed Ptr (list pointer) rather than I64.
let isListParamBody (paramName: string) (bodyExpr: Expr) : bool =
    match stripAnnot bodyExpr with
    | Match(Var(scrutinee, _), clauses, _) when scrutinee = paramName ->
        clauses |> List.exists (fun (pat, _, _) ->
            match pat with
            | EmptyListPat _ | ConsPat _ -> true
            | _ -> false)
    | _ -> false

/// Detect whether a function-call expression likely returns a string (Ptr).
/// Phase 67: Checks AnnotationMap first, falls back to function-name heuristic.
let isStringReturningExpr (annotationMap: Map<Ast.Span, Type.Type>) (expr: Expr) : bool =
    match checkInferredType annotationMap ((=) Type.TString) expr with
    | Some result -> result
    | None ->
    match stripAnnot expr with
    | App(Var(fname, _), _, _) ->
        fname = "readIdent" || fname = "lexeme" || fname = "to_string" ||
        fname.Contains("toString") || fname.Contains("string") ||
        fname = "string_concat" || fname = "String.concat" || fname = "String.sub"
    | App(App(Var(fname, _), _, _), _, _) ->
        fname = "string_concat" || fname = "String.concat" || fname = "^"
    | String _ -> true
    | _ -> false

/// Phase 43-fix: Detect whether a param needs Ptr type (list OR record OR string access in body).
/// Used to correctly type function parameters in the single-arg Lambda case.
let rec isPtrParamBody (paramName: string) (bodyExpr: Expr) : bool =
    // First check the existing list-pattern detection
    if isListParamBody paramName bodyExpr then true
    else
    // Also check if body accesses param as a record or uses it in string ops (needs Ptr).
    // strVars: set of let-bound variable names known to hold string values.
    let rec hasParamPtrUse (strVars: Set<string>) (expr: Expr) : bool =
        match stripAnnot expr with
        // Record field access on param directly → param must be Ptr
        | FieldAccess(Var(v, _), _, _) when v = paramName -> true
        // String builtins that expect Ptr (string concat, etc.) applied to param
        | App(App(Var("string_concat", _), Var(v, _), _), _, _) when v = paramName -> true
        | App(App(Var("string_concat", _), _, _), Var(v, _), _) when v = paramName -> true
        | App(Var("string_length", _), Var(v, _), _) when v = paramName -> true
        | App(Var("to_string", _), Var(v, _), _) when v = paramName -> false  // to_string works on I64 too
        // fst/snd on param → param is a tuple (Ptr)
        | App(Var("fst", _), Var(v, _), _) when v = paramName -> true
        | App(Var("snd", _), Var(v, _), _) when v = paramName -> true
        // Passing param to eprintfn/eprintln/println/printfn → param is likely a string (Ptr)
        | App(Var("eprintln", _), Var(v, _), _) when v = paramName -> true
        | App(Var("eprint", _), Var(v, _), _) when v = paramName -> true
        | App(Var("println", _), Var(v, _), _) when v = paramName -> true
        | App(App(Var("eprintfn", _), _, _), Var(v, _), _) when v = paramName -> true
        | App(App(Var("printfn", _), _, _), Var(v, _), _) when v = paramName -> true
        // Note: Add(Var(v), _) is NOT treated as Ptr — Add is integer arith (arith.addi).
        // String concat uses string_concat builtin, not Add.
        // Check match arms on param (for string or non-list param)
        | Match(Var(scrutinee, _), clauses, _) when scrutinee = paramName ->
            clauses |> List.exists (fun (pat, _, arm) ->
                match pat with
                | WildcardPat _ | VarPat _ -> false  // neutral patterns
                | ConstructorPat _ -> true  // ADT constructor pattern → param is Ptr
                | ConstPat(StringConst _, _) -> true  // string constant pattern → param is string (Ptr)
                | _ -> false)
        // Constructor call with param as argument → param is ADT/Ptr
        | Constructor(_, Some(Var(v, _)), _) when v = paramName -> true
        | Constructor(_, Some arg, _) -> hasParamPtrUse strVars arg
        // Equality comparison on param with a string literal → param is Ptr
        | Equal(Var(v, _), String(_, _), _) when v = paramName -> true
        | Equal(String(_, _), Var(v, _), _) when v = paramName -> true
        | NotEqual(Var(v, _), String(_, _), _) when v = paramName -> true
        | NotEqual(String(_, _), Var(v, _), _) when v = paramName -> true
        // Equality comparison on param with a known-string variable → param is Ptr
        | Equal(Var(v, _), Var(w, _), _) when v = paramName && Set.contains w strVars -> true
        | Equal(Var(w, _), Var(v, _), _) when v = paramName && Set.contains w strVars -> true
        | NotEqual(Var(v, _), Var(w, _), _) when v = paramName && Set.contains w strVars -> true
        | NotEqual(Var(w, _), Var(v, _), _) when v = paramName && Set.contains w strVars -> true
        // param compared to non-string var — recurse in case there are other hints
        | Equal(Var(v, _), other, _) when v = paramName -> hasParamPtrUse strVars other
        | Equal(other, Var(v, _), _) when v = paramName -> hasParamPtrUse strVars other
        | NotEqual(Var(v, _), other, _) when v = paramName -> hasParamPtrUse strVars other
        | NotEqual(other, Var(v, _), _) when v = paramName -> hasParamPtrUse strVars other
        // Recurse through let bindings; accumulate known-string vars
        | Let(name, bindE, bodyE, _) ->
            let strVars' = if isStringReturningExpr Map.empty bindE then Set.add name strVars else strVars
            hasParamPtrUse strVars bindE || hasParamPtrUse strVars' bodyE
        // Phase 62: LetPat(WildcardPat, e1, e2) is the desugared form of e1; e2 (statement sequence).
        // Must traverse to find param usage deeper in the body (was causing premature false).
        | LetPat(VarPat(vname, _), bindE, bodyE, _) ->
            let strVars' = if isStringReturningExpr Map.empty bindE then Set.add vname strVars else strVars
            hasParamPtrUse strVars bindE || hasParamPtrUse strVars' bodyE
        | LetPat(_, bindE, bodyE, _) ->
            hasParamPtrUse strVars bindE || hasParamPtrUse strVars bodyE
        | Match(scrut, clauses, _) ->
            hasParamPtrUse strVars scrut || clauses |> List.exists (fun (_, _, arm) -> hasParamPtrUse strVars arm)
        | If(c, t, e, _) -> hasParamPtrUse strVars c || hasParamPtrUse strVars t || hasParamPtrUse strVars e
        | Equal(l, r, _) -> hasParamPtrUse strVars l || hasParamPtrUse strVars r
        | NotEqual(l, r, _) -> hasParamPtrUse strVars l || hasParamPtrUse strVars r
        | App(f, a, _) -> hasParamPtrUse strVars f || hasParamPtrUse strVars a
        | Lambda(p, b, _) when p <> paramName -> hasParamPtrUse strVars b
        | Annot(inner, _, _) -> hasParamPtrUse strVars inner
        // Phase 62: Recurse through SetField, LetMut, Assign, TryWith, LetRec, ForIn, While, For, Tuple, List
        | SetField(e, _, v, _) -> hasParamPtrUse strVars e || hasParamPtrUse strVars v
        | LetMut(_, v, b, _) -> hasParamPtrUse strVars v || hasParamPtrUse strVars b
        | Assign(_, v, _) -> hasParamPtrUse strVars v
        | TryWith(b, clauses, _) -> hasParamPtrUse strVars b || clauses |> List.exists (fun (_, _, arm) -> hasParamPtrUse strVars arm)
        | LetRec(bindings, body, _) -> hasParamPtrUse strVars body || bindings |> List.exists (fun (_, _, _, b, _) -> hasParamPtrUse strVars b)
        | ForInExpr(_, coll, body, _) -> hasParamPtrUse strVars coll || hasParamPtrUse strVars body
        | WhileExpr(cond, body, _) -> hasParamPtrUse strVars cond || hasParamPtrUse strVars body
        | ForExpr(_, s, _, e, body, _) -> hasParamPtrUse strVars s || hasParamPtrUse strVars e || hasParamPtrUse strVars body
        | Tuple(elems, _) -> elems |> List.exists (hasParamPtrUse strVars)
        | List(elems, _) -> elems |> List.exists (hasParamPtrUse strVars)
        | Cons(h, t, _) -> hasParamPtrUse strVars h || hasParamPtrUse strVars t
        | Raise(e, _) -> hasParamPtrUse strVars e
        | _ -> false
    hasParamPtrUse Set.empty bodyExpr

/// Phase 67: Determine if a FunLang Type needs Ptr representation (vs I64).
/// String, list, array, tuple (non-unit), record, ADT, hashtable, closures → Ptr.
/// Int, bool, char, unit → I64.
let typeNeedsPtr (ty: Type.Type) : bool =
    match ty with
    | Type.TString | Type.TList _ | Type.TArray _ | Type.THashtable _
    | Type.TArrow _ | Type.TExn -> true
    | Type.TTuple elems -> not elems.IsEmpty  // non-unit tuple → Ptr
    | Type.TData _ -> true                    // ADT/record → Ptr
    | Type.TInt | Type.TBool | Type.TChar | Type.TError -> false
    | Type.TVar _ -> false                    // unresolved type var → fallback I64

/// Phase 67: Type-aware isPtrParam — checks AnnotationMap for Lambda type, falls back to heuristic.
let isPtrParamTyped (annotationMap: Map<Ast.Span, Type.Type>) (lambdaSpan: Ast.Span) (paramName: string) (bodyExpr: Expr) : bool =
    match Map.tryFind lambdaSpan annotationMap with
    | Some (Type.TArrow(paramType, _)) -> typeNeedsPtr paramType
    | _ -> isPtrParamBody paramName bodyExpr

/// Phase 43: Detect whether an expression statically produces a boolean value.
/// Used to populate BoolVars for correct to_string dispatch.
let rec isBoolExpr (boolVars: Set<string>) (knownFuncs: Map<string, FuncSignature>) (annotationMap: Map<Ast.Span, Type.Type>) (expr: Ast.Expr) : bool =
    match checkInferredType annotationMap ((=) Type.TBool) expr with
    | Some result -> result
    | None ->
    match expr with
    | Ast.Bool _ -> true
    | Ast.Equal _ | Ast.NotEqual _ | Ast.LessThan _ | Ast.GreaterThan _
    | Ast.LessEqual _ | Ast.GreaterEqual _ -> true
    | Ast.And _ | Ast.Or _ -> true
    | Ast.Var (name, _) -> Set.contains name boolVars
    | Ast.App (Ast.Var (name, _), _, _) ->
        match Map.tryFind name knownFuncs with
        | Some sig_ -> sig_.ReturnIsBool
        | None -> Set.contains name boolVars
    | Ast.App (Ast.App (Ast.Var (name, _), _, _), _, _) ->
        match Map.tryFind name knownFuncs with
        | Some sig_ -> sig_.InnerReturnIsBool
        | None -> false
    | Ast.Annot (inner, _, _) -> isBoolExpr boolVars knownFuncs annotationMap inner
    | Ast.LambdaAnnot _ -> false
    | _ -> false

/// Phase 43: Detect whether a function body's final expression returns bool.
/// Traverses through let bindings to find the tail expression.
let rec bodyReturnsBool (expr: Expr) : bool =
    match stripAnnot expr with
    | Bool _ -> true
    | Equal _ | NotEqual _ | LessThan _ | GreaterThan _
    | LessEqual _ | GreaterEqual _ -> true
    | And _ | Or _ -> true
    | If (_, thenE, elseE, _) -> bodyReturnsBool thenE || bodyReturnsBool elseE
    | Let (_, _, cont, _) | LetMut (_, _, cont, _) -> bodyReturnsBool cont
    | LetRec (_, cont, _) -> bodyReturnsBool cont
    | LetPat (_, _, cont, _) -> bodyReturnsBool cont
    | Match (_, clauses, _) -> clauses |> List.exists (fun (_, _, arm) -> bodyReturnsBool arm)
    | _ -> false

/// Phase 67: Type-aware bodyReturnsBool — checks AnnotationMap first, falls back to heuristic.
let bodyReturnsBoolTyped (annotationMap: Map<Ast.Span, Type.Type>) (expr: Expr) : bool =
    match checkInferredType annotationMap ((=) Type.TBool) expr with
    | Some result -> result
    | None -> bodyReturnsBool expr

/// Phase 11: Test a pattern against a scrutinee value.
/// Returns (condOpt, testOps, bodySetupOps, bindEnv) where:
///   condOpt      = None  -> unconditional match (WildcardPat / VarPat)
///   condOpt      = Some v -> I1 condition value; test ops must be emitted before the cond_br
///   testOps      = ops that compute the condition (run in the test/entry block)
///   bodySetupOps = ops that run at the START of the body block (e.g. ConsPat head/tail loads)
///   bindEnv      = env with any variable bindings from the pattern added
let rec testPattern (env: ElabEnv) (scrutVal: MlirValue) (pat: Pattern)
    : MlirValue option * MlirOp list * MlirOp list * ElabEnv =
    match pat with
    | WildcardPat _ ->
        (None, [], [], env)
    | VarPat(name, _) ->
        let env' = { env with Vars = Map.add name scrutVal env.Vars }
        (None, [], [], env')
    | ConstPat(IntConst n, _) ->
        let kVal = { Name = freshName env; Type = I64 }
        let cond = { Name = freshName env; Type = I1 }
        let ops  = [ ArithConstantOp(kVal, tagConst (int64 n)); ArithCmpIOp(cond, "eq", scrutVal, kVal) ]
        (Some cond, ops, [], env)
    | ConstPat(BoolConst b, _) ->
        let kVal = { Name = freshName env; Type = I1 }
        let cond = { Name = freshName env; Type = I1 }
        let n    = if b then 1L else 0L
        let ops  = [ ArithConstantOp(kVal, n); ArithCmpIOp(cond, "eq", scrutVal, kVal) ]
        (Some cond, ops, [], env)
    | EmptyListPat _ ->
        // Test: scrutVal == null (is empty list)
        let nullVal = { Name = freshName env; Type = Ptr }
        let cond    = { Name = freshName env; Type = I1 }
        let ops     = [ LlvmNullOp(nullVal); LlvmIcmpOp(cond, "eq", scrutVal, nullVal) ]
        (Some cond, ops, [], env)
    | ConsPat(hPat, tPat, _) ->
        // Test: scrutVal != null (is a cons cell)
        let nullVal  = { Name = freshName env; Type = Ptr }
        let cond     = { Name = freshName env; Type = I1 }
        let testOps  = [ LlvmNullOp(nullVal); LlvmIcmpOp(cond, "ne", scrutVal, nullVal) ]
        // Body setup: load head and tail from cons cell
        let headVal  = { Name = freshName env; Type = I64 }
        let tailSlot = { Name = freshName env; Type = Ptr }
        let tailVal  = { Name = freshName env; Type = Ptr }
        let setupOps = [
            LlvmLoadOp(headVal, scrutVal)
            LlvmGEPLinearOp(tailSlot, scrutVal, 1)
            LlvmLoadOp(tailVal, tailSlot)
        ]
        // Bind head and tail names
        let headName = match hPat with VarPat(n, _) -> Some n | WildcardPat _ -> None | _ -> failWithSpan (Ast.patternSpanOf hPat) "Elaboration: ConsPat head must be VarPat or WildcardPat"
        let tailName = match tPat with VarPat(n, _) -> Some n | WildcardPat _ -> None | _ -> failWithSpan (Ast.patternSpanOf tPat) "Elaboration: ConsPat tail must be VarPat or WildcardPat"
        let env' =
            env
            |> (fun e -> match headName with Some n -> { e with Vars = Map.add n headVal e.Vars } | None -> e)
            |> (fun e -> match tailName with Some n -> { e with Vars = Map.add n tailVal e.Vars } | None -> e)
        (Some cond, testOps, setupOps, env')
    | ConstPat(StringConst s, _) ->
        // Elaborate the string literal for the pattern constant
        let (patStrVal, patStrOps) = elaborateStringLiteral env s
        // Get data ptr for pattern string: GEP field 1
        let patDataPtrVal   = { Name = freshName env; Type = Ptr }
        let patDataVal      = { Name = freshName env; Type = Ptr }
        // Get data ptr for scrutinee string: GEP field 1
        let scrutDataPtrVal = { Name = freshName env; Type = Ptr }
        let scrutDataVal    = { Name = freshName env; Type = Ptr }
        // strcmp result
        let cmpRes  = { Name = freshName env; Type = I32 }
        let zero32  = { Name = freshName env; Type = I32 }
        let cond    = { Name = freshName env; Type = I1 }
        let ops = patStrOps @ [
            LlvmGEPStructOp(patDataPtrVal, patStrVal, 1)
            LlvmLoadOp(patDataVal, patDataPtrVal)
            LlvmGEPStructOp(scrutDataPtrVal, scrutVal, 1)
            LlvmLoadOp(scrutDataVal, scrutDataPtrVal)
            LlvmCallOp(cmpRes, "@strcmp", [scrutDataVal; patDataVal])
            ArithConstantOp(zero32, 0L)
            ArithCmpIOp(cond, "eq", cmpRes, zero32)
        ]
        (Some cond, ops, [], env)

    | TuplePat(pats, _) ->
        // Tuple always matches structurally — unconditional.
        // GEP each field, load with appropriate type, bind sub-patterns.
        // All ops go into bodySetupOps (no condition needed).
        let loadTypeOfPat = function
            | TuplePat _ -> Ptr
            | _          -> I64
        let (setupOps, bindEnv) =
            pats
            |> List.mapi (fun i subPat -> (i, subPat))
            |> List.fold (fun (opsAcc, eAcc: ElabEnv) (i, subPat) ->
                let slotVal  = { Name = freshName env; Type = Ptr }
                let fieldVal = { Name = freshName env; Type = loadTypeOfPat subPat }
                let gepOp    = LlvmGEPLinearOp(slotVal, scrutVal, i)
                let loadOp   = LlvmLoadOp(fieldVal, slotVal)
                match subPat with
                | VarPat(vname, _) ->
                    let eAcc' = { eAcc with Vars = Map.add vname fieldVal eAcc.Vars }
                    (opsAcc @ [gepOp; loadOp], eAcc')
                | WildcardPat _ ->
                    (opsAcc @ [gepOp; loadOp], eAcc)
                | TuplePat _ ->
                    // Nested tuple — recurse with fieldVal as the new scrutinee
                    let (_, innerTestOps, innerSetupOps, innerEnv) = testPattern eAcc fieldVal subPat
                    (opsAcc @ [gepOp; loadOp] @ innerTestOps @ innerSetupOps, innerEnv)
                | _ ->
                    failWithSpan (Ast.patternSpanOf subPat) "testPattern: TuplePat sub-pattern %A not supported" subPat
            ) ([], env)
        (None, [], setupOps, bindEnv)

    | _ ->
        failWithSpan (Ast.patternSpanOf pat) "testPattern: pattern %A not supported in v2" pat

// Phase 59: Decode a nested module path expression into a segment list.
// FieldAccess(FieldAccess(Constructor("Outer"), "Inner"), "foo") → Some ["Outer"; "Inner"; "foo"]
// Returns None if innermost expression is not a no-arg Constructor (i.e., it's a real record access).
// The Constructor(name, None, _) guard is critical — excludes data constructors with arguments.
let rec tryDecodeModulePath (expr: Expr) : string list option =
    match expr with
    | Constructor(name, None, _) -> Some [name]
    | FieldAccess(inner, field, _) ->
        match tryDecodeModulePath inner with
        | Some segments -> Some (segments @ [field])
        | None -> None
    | _ -> None

