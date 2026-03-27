module Elaboration

open Ast
open MlirIR
open MatchCompiler

// Phase 5: Closure metadata — distinguishes closure-making funcs from direct-call funcs
type ClosureInfo = {
    InnerLambdaFn: string    // MLIR name of the llvm.func body, e.g. "@closure_fn_0"
    NumCaptures:   int       // number of captured variables
}

type FuncSignature = {
    MlirName:    string
    ParamTypes:  MlirType list
    ReturnType:  MlirType
    ClosureInfo: ClosureInfo option  // None = direct-call func; Some = closure-maker
}

// Phase 16: TypeInfo for ADT constructor entries in TypeEnv
type TypeInfo = { Tag: int; Arity: int }

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
    // Phase 16: Declaration environment — populated by prePassDecls before elaboration
    TypeEnv:        Map<string, TypeInfo>            // constructor name -> tag + arity
    RecordEnv:      Map<string, Map<string, int>>    // record type name -> (field name -> index)
    ExnTags:        Map<string, int>                 // exception ctor name -> tag index
    // Phase 21: Mutable variable tracking — names that live in GC ref cells
    MutableVars:    Set<string>
}

let emptyEnv () : ElabEnv =
    { Vars = Map.empty; Counter = ref 0; LabelCounter = ref 0; Blocks = ref []
      KnownFuncs = Map.empty; Funcs = ref []; ClosureCounter = ref 0
      Globals = ref []; GlobalCounter = ref 0
      TypeEnv = Map.empty; RecordEnv = Map.empty; ExnTags = Map.empty
      MutableVars = Set.empty }

let private addStringGlobal (env: ElabEnv) (rawValue: string) : string =
    match env.Globals.Value |> List.tryFind (fun (_, v) -> v = rawValue) with
    | Some (name, _) -> name
    | None ->
        let idx = env.GlobalCounter.Value
        env.GlobalCounter.Value <- idx + 1
        let name = sprintf "@__str_%d" idx
        env.Globals.Value <- env.Globals.Value @ [(name, rawValue)]
        name

let private freshName (env: ElabEnv) : string =
    let n = env.Counter.Value
    env.Counter.Value <- n + 1
    sprintf "%%t%d" n

let private freshLabel (env: ElabEnv) (prefix: string) : string =
    let n = env.LabelCounter.Value
    env.LabelCounter.Value <- n + 1
    sprintf "%s%d" prefix n

// Phase 8: Allocate a heap string struct {i64 length, ptr data} for a compile-time string literal.
let private elaborateStringLiteral (env: ElabEnv) (s: string) : MlirValue * MlirOp list =
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
    | LetRec (name, param, body, inExpr, _) ->
        let innerBound = Set.add name (Set.add param boundVars)
        Set.union (freeVars innerBound body) (freeVars (Set.add name boundVars) inExpr)
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
    | PipeRight (l, r, _) | ComposeRight (l, r, _) | ComposeLeft (l, r, _) ->
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
    | _ -> Set.empty  // conservative: other exprs (Char, etc.) have no free vars

/// Detect whether a LetRec body uses list patterns on the parameter,
/// indicating the parameter should be typed Ptr (list pointer) rather than I64.
let private isListParamBody (paramName: string) (bodyExpr: Expr) : bool =
    match bodyExpr with
    | Match(Var(scrutinee, _), clauses, _) when scrutinee = paramName ->
        clauses |> List.exists (fun (pat, _, _) ->
            match pat with
            | EmptyListPat _ | ConsPat _ -> true
            | _ -> false)
    | _ -> false

/// Phase 11: Test a pattern against a scrutinee value.
/// Returns (condOpt, testOps, bodySetupOps, bindEnv) where:
///   condOpt      = None  -> unconditional match (WildcardPat / VarPat)
///   condOpt      = Some v -> I1 condition value; test ops must be emitted before the cond_br
///   testOps      = ops that compute the condition (run in the test/entry block)
///   bodySetupOps = ops that run at the START of the body block (e.g. ConsPat head/tail loads)
///   bindEnv      = env with any variable bindings from the pattern added
let rec private testPattern (env: ElabEnv) (scrutVal: MlirValue) (pat: Pattern)
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
        let ops  = [ ArithConstantOp(kVal, int64 n); ArithCmpIOp(cond, "eq", scrutVal, kVal) ]
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
        let headName = match hPat with VarPat(n, _) -> Some n | WildcardPat _ -> None | _ -> failwithf "Elaboration: ConsPat head must be VarPat or WildcardPat"
        let tailName = match tPat with VarPat(n, _) -> Some n | WildcardPat _ -> None | _ -> failwithf "Elaboration: ConsPat tail must be VarPat or WildcardPat"
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
                    failwithf "testPattern: TuplePat sub-pattern %A not supported" subPat
            ) ([], env)
        (None, [], setupOps, bindEnv)

    | _ ->
        failwithf "testPattern: pattern %A not supported in v2" pat

let rec elaborateExpr (env: ElabEnv) (expr: Expr) : MlirValue * MlirOp list =
    match expr with
    | Number (n, _) ->
        let v = { Name = freshName env; Type = I64 }
        (v, [ArithConstantOp(v, int64 n)])
    | Char (c, _) ->
        let v = { Name = freshName env; Type = I64 }
        (v, [ArithConstantOp(v, int64 (int c))])
    | Add (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let result = { Name = freshName env; Type = I64 }
        (result, lops @ rops @ [ArithAddIOp(result, lv, rv)])
    | Subtract (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let result = { Name = freshName env; Type = I64 }
        (result, lops @ rops @ [ArithSubIOp(result, lv, rv)])
    | Multiply (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let result = { Name = freshName env; Type = I64 }
        (result, lops @ rops @ [ArithMulIOp(result, lv, rv)])
    | Divide (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let result = { Name = freshName env; Type = I64 }
        (result, lops @ rops @ [ArithDivSIOp(result, lv, rv)])
    | Modulo (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let result = { Name = freshName env; Type = I64 }
        (result, lops @ rops @ [ArithRemSIOp(result, lv, rv)])
    | PipeRight (left, right, span) ->
        // x |> f  ≡  f x  ≡  App(right, left)
        elaborateExpr env (App(right, left, span))
    | ComposeRight (f, g, span) ->
        // f >> g  ≡  fun x -> g (f x)
        let n = env.Counter.Value
        env.Counter.Value <- n + 1
        let paramName = sprintf "__comp_%d" n
        let innerApp = App(f, Var(paramName, span), span)
        let outerApp = App(g, innerApp, span)
        elaborateExpr env (Lambda(paramName, outerApp, span))
    | ComposeLeft (f, g, span) ->
        // f << g  ≡  fun x -> f (g x)
        let n = env.Counter.Value
        env.Counter.Value <- n + 1
        let paramName = sprintf "__comp_%d" n
        let innerApp = App(g, Var(paramName, span), span)
        let outerApp = App(f, innerApp, span)
        elaborateExpr env (Lambda(paramName, outerApp, span))
    | Negate (inner, _) ->
        let (iv, iops) = elaborateExpr env inner
        let zero = { Name = freshName env; Type = I64 }
        let result = { Name = freshName env; Type = I64 }
        (result, iops @ [ArithConstantOp(zero, 0L); ArithSubIOp(result, zero, iv)])
    | Var (name, _) ->
        match Map.tryFind name env.Vars with
        | Some v ->
            if Set.contains name env.MutableVars then
                let loaded = { Name = freshName env; Type = I64 }
                (loaded, [LlvmLoadOp(loaded, v)])
            else
                (v, [])
        | None -> failwithf "Elaboration: unbound variable '%s'" name
    // Phase 21: Mutable variable allocation
    | LetMut (name, initExpr, bodyExpr, _) ->
        let (initVal, initOps) = elaborateExpr env initExpr
        let sizeVal  = { Name = freshName env; Type = I64 }
        let cellPtr  = { Name = freshName env; Type = Ptr }
        let allocOps = [
            ArithConstantOp(sizeVal, 8L)
            LlvmCallOp(cellPtr, "@GC_malloc", [sizeVal])
            LlvmStoreOp(initVal, cellPtr)
        ]
        let env' = { env with
                        Vars        = Map.add name cellPtr env.Vars
                        MutableVars = Set.add name env.MutableVars }
        let (bodyVal, bodyOps) = elaborateExpr env' bodyExpr
        (bodyVal, initOps @ allocOps @ bodyOps)
    // Phase 21: Mutable variable assignment
    | Assign (name, valExpr, _) ->
        let (newVal, valOps) = elaborateExpr env valExpr
        let cellPtr =
            match Map.tryFind name env.Vars with
            | Some v -> v
            | None -> failwithf "Elaboration: unbound mutable variable '%s' in Assign" name
        let unitVal = { Name = freshName env; Type = I64 }
        (unitVal, valOps @ [LlvmStoreOp(newVal, cellPtr); ArithConstantOp(unitVal, 0L)])
    // Phase 5: special-case Let(name, Lambda(outerParam, Lambda(innerParam, innerBody)), inExpr)
    // This compiles to an llvm.func body + func.func closure-maker + KnownFuncs entry
    | Let (name, Lambda (outerParam, Lambda (innerParam, innerBody, _), _), inExpr, _) ->
        // Step 1: Compute free variables of the inner lambda body relative to innerParam only.
        // These are variables that need to come from the closure environment struct.
        // outerParam IS one such variable — it's passed to the closure-maker and stored at env[1+i].
        // Using only {innerParam} as bound means outerParam appears free when it's used in innerBody.
        let captures =
            freeVars (Set.singleton innerParam) innerBody
            |> Set.toList
            |> List.sort
        let numCaptures = List.length captures

        // Step 2: Generate fresh closure function name
        let closureFnIdx = env.ClosureCounter.Value
        env.ClosureCounter.Value <- closureFnIdx + 1
        let closureFnName = sprintf "@closure_fn_%d" closureFnIdx

        // Step 3: Compile inner lambda body (llvm.func)
        // Build the initial vars: %arg0 = env ptr, %arg1 = innerParam
        let arg0Val = { Name = "%arg0"; Type = Ptr }
        let arg1Val = { Name = "%arg1"; Type = I64 }
        let innerEnv : ElabEnv =
            { Vars = Map.ofList [(innerParam, arg1Val)]
              Counter = ref 0; LabelCounter = ref 0; Blocks = ref []
              KnownFuncs = env.KnownFuncs
              Funcs = env.Funcs
              ClosureCounter = env.ClosureCounter
              Globals = env.Globals
              GlobalCounter = env.GlobalCounter
              TypeEnv = env.TypeEnv; RecordEnv = env.RecordEnv; ExnTags = env.ExnTags
              MutableVars = env.MutableVars }

        // For each capture at index i: GEP to slot i+1, then load
        let captureLoadOps, innerEnvWithCaptures =
            captures |> List.mapi (fun i capName ->
                let gepVal = { Name = sprintf "%%t%d" i; Type = Ptr }
                let capType = if Set.contains capName env.MutableVars then Ptr else I64
                let capVal = { Name = sprintf "%%t%d" (i + numCaptures); Type = capType }
                (gepVal, capVal, capName, i)
            )
            |> List.fold (fun (opsAcc, envAcc: ElabEnv) (gepVal, capVal, capName, i) ->
                // Advance counter past GEP and load names we pre-allocated
                let gepOp = LlvmGEPLinearOp(gepVal, arg0Val, i + 1)
                let loadOp = LlvmLoadOp(capVal, gepVal)
                let envAcc' = { envAcc with Vars = Map.add capName capVal envAcc.Vars }
                (opsAcc @ [gepOp; loadOp], envAcc')
            ) ([], innerEnv)

        // Advance inner env counter past the pre-allocated GEP/load SSA names
        innerEnvWithCaptures.Counter.Value <- numCaptures * 2

        // Elaborate inner body
        let (bodyVal, bodyEntryOps) = elaborateExpr innerEnvWithCaptures innerBody
        let bodySideBlocks = innerEnvWithCaptures.Blocks.Value

        let allBodyBlocks =
            if bodySideBlocks.IsEmpty then
                [ { Label = None; Args = []; Body = captureLoadOps @ bodyEntryOps @ [LlvmReturnOp [bodyVal]] } ]
            else
                let entryBlock = { Label = None; Args = []; Body = captureLoadOps @ bodyEntryOps }
                let lastBlock = List.last bodySideBlocks
                let lastBlockWithReturn = { lastBlock with Body = lastBlock.Body @ [LlvmReturnOp [bodyVal]] }
                let sideBlocksPatched = (List.take (bodySideBlocks.Length - 1) bodySideBlocks) @ [lastBlockWithReturn]
                entryBlock :: sideBlocksPatched

        let innerFuncOp : FuncOp =
            { Name        = closureFnName
              InputTypes  = [Ptr; I64]
              ReturnType  = Some I64
              Body        = { Blocks = allBodyBlocks }
              IsLlvmFunc  = true }

        // Step 4: Compile closure-maker func.func
        // %arg0 = outerParam (I64), %arg1 = env ptr (Ptr, caller-allocated)
        let makerArg0 = { Name = "%arg0"; Type = I64 }
        let makerArg1 = { Name = "%arg1"; Type = Ptr }

        // Use a fresh counter starting after 0 for the closure-maker body ops
        let makerCounter = ref 0
        let nextMakerName () =
            let n = makerCounter.Value
            makerCounter.Value <- n + 1
            sprintf "%%t%d" n

        let fnPtrName = nextMakerName ()
        let fnPtrVal = { Name = fnPtrName; Type = Ptr }

        // addressof + store fn_ptr at env[0]
        let makerOps =
            [ LlvmAddressOfOp(fnPtrVal, closureFnName)
              LlvmStoreOp(fnPtrVal, makerArg1) ]

        // For each capture at index i: GEP to slot i+1, store the captured value
        let captureStoreOps =
            captures |> List.mapi (fun i capName ->
                let slotName = nextMakerName ()
                let slotVal = { Name = slotName; Type = Ptr }
                let gepOp = LlvmGEPLinearOp(slotVal, makerArg1, i + 1)
                // Look up capture value in the OUTER env
                // If the capture name is the outerParam, it IS %arg0
                let captureVal =
                    if capName = outerParam then makerArg0
                    else
                        match Map.tryFind capName env.Vars with
                        | Some v -> v
                        | None -> failwithf "Elaboration: closure capture '%s' not found in outer scope" capName
                let storeOp = LlvmStoreOp(captureVal, slotVal)
                [gepOp; storeOp]
            ) |> List.concat

        let makerBodyOps = makerOps @ captureStoreOps @ [ReturnOp [makerArg1]]

        let makerFuncOp : FuncOp =
            { Name        = "@" + name
              InputTypes  = [I64; Ptr]
              ReturnType  = Some Ptr
              Body        = { Blocks = [ { Label = None; Args = []; Body = makerBodyOps } ] }
              IsLlvmFunc  = false }

        // Step 5: Add both FuncOps to env.Funcs
        env.Funcs.Value <- env.Funcs.Value @ [innerFuncOp; makerFuncOp]

        // Step 6: Add to KnownFuncs
        let closureInfo = { InnerLambdaFn = closureFnName; NumCaptures = numCaptures }
        let sig_ : FuncSignature =
            { MlirName    = "@" + name
              ParamTypes  = [I64]
              ReturnType  = Ptr
              ClosureInfo = Some closureInfo }
        let env' = { env with KnownFuncs = Map.add name sig_ env.KnownFuncs }

        // Step 7: Elaborate inExpr with updated env
        elaborateExpr env' inExpr

    | Let (name, bindExpr, bodyExpr, _) ->
        let blocksBeforeBind = env.Blocks.Value.Length
        let (bv, bops) = elaborateExpr env bindExpr
        let env' = { env with Vars = Map.add name bv env.Vars }
        let (rv, rops) = elaborateExpr env' bodyExpr
        // If bops ends with a block terminator (from nested Match/TryWith), the
        // continuation code (rops) must go into the last side block that was added
        // (the inner expression's merge block), not after the terminator inline.
        let isTerminator op =
            match op with
            | CfBrOp _ | CfCondBrOp _ | LlvmUnreachableOp -> true
            | _ -> false
        match List.tryLast bops with
        | Some op when isTerminator op && env.Blocks.Value.Length > blocksBeforeBind ->
            // Place rops in the last side block (inner merge block) before its existing ops
            let innerBlocks = env.Blocks.Value
            let lastBlock = List.last innerBlocks
            let patchedLast = { lastBlock with Body = rops @ lastBlock.Body }
            env.Blocks.Value <- (List.take (innerBlocks.Length - 1) innerBlocks) @ [patchedLast]
            (rv, bops)  // bops alone (terminator as last op — valid MLIR block ending)
        | _ ->
            (rv, bops @ rops)
    // Phase 7: LetPat with wildcard or var pattern — enables "let _ = print ... in ..."
    | LetPat (WildcardPat _, bindExpr, bodyExpr, _) ->
        let (_bv, bops) = elaborateExpr env bindExpr
        let (rv, rops) = elaborateExpr env bodyExpr
        (rv, bops @ rops)
    | LetPat (VarPat (name, _), bindExpr, bodyExpr, _) ->
        let (bv, bops) = elaborateExpr env bindExpr
        let env' = { env with Vars = Map.add name bv env.Vars }
        let (rv, rops) = elaborateExpr env' bodyExpr
        (rv, bops @ rops)
    // Phase 9: LetPat with TuplePat — destructure a heap-allocated tuple via GEP + load
    | LetPat (TuplePat (pats, _), bindExpr, bodyExpr, _) ->
        let (tupPtrVal, bindOps) = elaborateExpr env bindExpr
        // Recursively bind patterns to extracted values from a tuple ptr
        let rec bindTuplePat (envAcc: ElabEnv) (ptrVal: MlirValue) (subPats: Pattern list) : MlirOp list * ElabEnv =
            let loadTypeOfPat = function
                | TuplePat _ -> Ptr
                | _          -> I64
            subPats
            |> List.mapi (fun i pat -> (i, pat))
            |> List.fold (fun (opsAcc, eAcc: ElabEnv) (i, pat) ->
                let slotVal  = { Name = freshName env; Type = Ptr }
                let fieldVal = { Name = freshName env; Type = loadTypeOfPat pat }
                let gepOp    = LlvmGEPLinearOp(slotVal, ptrVal, i)
                let loadOp   = LlvmLoadOp(fieldVal, slotVal)
                match pat with
                | VarPat (vname, _) ->
                    let eAcc' = { eAcc with Vars = Map.add vname fieldVal eAcc.Vars }
                    (opsAcc @ [gepOp; loadOp], eAcc')
                | WildcardPat _ ->
                    (opsAcc @ [gepOp; loadOp], eAcc)
                | TuplePat (innerPats, _) ->
                    // fieldVal is a Ptr to the inner tuple — recurse
                    let (innerOps, innerEnv) = bindTuplePat eAcc fieldVal innerPats
                    (opsAcc @ [gepOp; loadOp] @ innerOps, innerEnv)
                | _ ->
                    failwithf "Elaboration: unsupported sub-pattern in TuplePat: %A" pat
            ) ([], envAcc)
        let (extractOps, env') = bindTuplePat env tupPtrVal pats
        let (bodyVal, bodyOps) = elaborateExpr env' bodyExpr
        (bodyVal, bindOps @ extractOps @ bodyOps)
    | Bool (b, _) ->
        let v = { Name = freshName env; Type = I1 }
        let n = if b then 1L else 0L
        (v, [ArithConstantOp(v, n)])
    | Equal (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        if lv.Type = Ptr then
            // String equality via strcmp
            let lDataPtr   = { Name = freshName env; Type = Ptr }
            let lData      = { Name = freshName env; Type = Ptr }
            let rDataPtr   = { Name = freshName env; Type = Ptr }
            let rData      = { Name = freshName env; Type = Ptr }
            let cmpResult  = { Name = freshName env; Type = I32 }
            let zero32     = { Name = freshName env; Type = I32 }
            let boolResult = { Name = freshName env; Type = I1 }
            let ops = [
                LlvmGEPStructOp(lDataPtr, lv, 1)
                LlvmLoadOp(lData, lDataPtr)
                LlvmGEPStructOp(rDataPtr, rv, 1)
                LlvmLoadOp(rData, rDataPtr)
                LlvmCallOp(cmpResult, "@strcmp", [lData; rData])
                ArithConstantOp(zero32, 0L)
                ArithCmpIOp(boolResult, "eq", cmpResult, zero32)
            ]
            (boolResult, lops @ rops @ ops)
        else
            let result = { Name = freshName env; Type = I1 }
            (result, lops @ rops @ [ArithCmpIOp(result, "eq", lv, rv)])
    | NotEqual (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        if lv.Type = Ptr then
            // String inequality via strcmp != 0
            let lDataPtr   = { Name = freshName env; Type = Ptr }
            let lData      = { Name = freshName env; Type = Ptr }
            let rDataPtr   = { Name = freshName env; Type = Ptr }
            let rData      = { Name = freshName env; Type = Ptr }
            let cmpResult  = { Name = freshName env; Type = I32 }
            let zero32     = { Name = freshName env; Type = I32 }
            let boolResult = { Name = freshName env; Type = I1 }
            let ops = [
                LlvmGEPStructOp(lDataPtr, lv, 1)
                LlvmLoadOp(lData, lDataPtr)
                LlvmGEPStructOp(rDataPtr, rv, 1)
                LlvmLoadOp(rData, rDataPtr)
                LlvmCallOp(cmpResult, "@strcmp", [lData; rData])
                ArithConstantOp(zero32, 0L)
                ArithCmpIOp(boolResult, "ne", cmpResult, zero32)
            ]
            (boolResult, lops @ rops @ ops)
        else
            let result = { Name = freshName env; Type = I1 }
            (result, lops @ rops @ [ArithCmpIOp(result, "ne", lv, rv)])
    | LessThan (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let result = { Name = freshName env; Type = I1 }
        (result, lops @ rops @ [ArithCmpIOp(result, "slt", lv, rv)])
    | GreaterThan (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let result = { Name = freshName env; Type = I1 }
        (result, lops @ rops @ [ArithCmpIOp(result, "sgt", lv, rv)])
    | LessEqual (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let result = { Name = freshName env; Type = I1 }
        (result, lops @ rops @ [ArithCmpIOp(result, "sle", lv, rv)])
    | GreaterEqual (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let result = { Name = freshName env; Type = I1 }
        (result, lops @ rops @ [ArithCmpIOp(result, "sge", lv, rv)])
    | If (condExpr, thenExpr, elseExpr, _) ->
        let (condVal, condOps) = elaborateExpr env condExpr
        let thenLabel  = freshLabel env "then"
        let elseLabel  = freshLabel env "else"
        let mergeLabel = freshLabel env "merge"
        let (thenVal, thenOps) = elaborateExpr env thenExpr
        let (elseVal, elseOps) = elaborateExpr env elseExpr
        let mergeArg = { Name = freshName env; Type = thenVal.Type }
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some thenLabel; Args = []; Body = thenOps @ [CfBrOp(mergeLabel, [thenVal])] } ]
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some elseLabel; Args = []; Body = elseOps @ [CfBrOp(mergeLabel, [elseVal])] } ]
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some mergeLabel; Args = [mergeArg]; Body = [] } ]
        (mergeArg, condOps @ [CfCondBrOp(condVal, thenLabel, [], elseLabel, [])])
    | And (lhsExpr, rhsExpr, _) ->
        let (leftVal, leftOps) = elaborateExpr env lhsExpr
        let evalRightLabel = freshLabel env "and_right"
        let mergeLabel     = freshLabel env "and_merge"
        let (rightVal, rightOps) = elaborateExpr env rhsExpr
        let mergeArg = { Name = freshName env; Type = I1 }
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some evalRightLabel; Args = []; Body = rightOps @ [CfBrOp(mergeLabel, [rightVal])] } ]
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some mergeLabel; Args = [mergeArg]; Body = [] } ]
        (mergeArg, leftOps @ [CfCondBrOp(leftVal, evalRightLabel, [], mergeLabel, [leftVal])])
    | Or (lhsExpr, rhsExpr, _) ->
        let (leftVal, leftOps) = elaborateExpr env lhsExpr
        let evalRightLabel = freshLabel env "or_right"
        let mergeLabel     = freshLabel env "or_merge"
        let (rightVal, rightOps) = elaborateExpr env rhsExpr
        let mergeArg = { Name = freshName env; Type = I1 }
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some evalRightLabel; Args = []; Body = rightOps @ [CfBrOp(mergeLabel, [rightVal])] } ]
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some mergeLabel; Args = [mergeArg]; Body = [] } ]
        (mergeArg, leftOps @ [CfCondBrOp(leftVal, mergeLabel, [leftVal], evalRightLabel, [])])
    | LetRec (name, param, body, inExpr, _) ->
        let paramType = if isListParamBody param body then Ptr else I64
        let sig_ : FuncSignature =
            { MlirName = "@" + name; ParamTypes = [paramType]; ReturnType = I64; ClosureInfo = None }
        let bodyEnv : ElabEnv =
            { Vars = Map.ofList [(param, { Name = "%arg0"; Type = paramType })]
              Counter = ref 0; LabelCounter = ref 0; Blocks = ref []
              KnownFuncs = Map.ofList [(name, sig_)]
              Funcs = env.Funcs
              ClosureCounter = env.ClosureCounter
              Globals = env.Globals
              GlobalCounter = env.GlobalCounter
              TypeEnv = env.TypeEnv; RecordEnv = env.RecordEnv; ExnTags = env.ExnTags
              MutableVars = Set.empty }
        let (bodyVal, bodyEntryOps) = elaborateExpr bodyEnv body
        let bodySideBlocks = bodyEnv.Blocks.Value
        let allBodyBlocks =
            if bodySideBlocks.IsEmpty then
                [ { Label = None; Args = []; Body = bodyEntryOps @ [ReturnOp [bodyVal]] } ]
            else
                let entryBlock = { Label = None; Args = []; Body = bodyEntryOps }
                let lastBlock = List.last bodySideBlocks
                let lastBlockWithReturn = { lastBlock with Body = lastBlock.Body @ [ReturnOp [bodyVal]] }
                let sideBlocksPatched = (List.take (bodySideBlocks.Length - 1) bodySideBlocks) @ [lastBlockWithReturn]
                entryBlock :: sideBlocksPatched
        let funcOp : FuncOp =
            { Name = "@" + name
              InputTypes = [paramType]
              ReturnType = Some bodyVal.Type
              Body = { Blocks = allBodyBlocks }
              IsLlvmFunc = false }
        env.Funcs.Value <- env.Funcs.Value @ [funcOp]
        let env' = { env with KnownFuncs = Map.add name { sig_ with ReturnType = bodyVal.Type } env.KnownFuncs }
        elaborateExpr env' inExpr
    // Phase 8: String literal → GC_malloc'd header struct
    | String (s, _) ->
        elaborateStringLiteral env s

    // Phase 14: string_sub builtin — App(App(App(Var("string_sub"), s), start), len)
    // Three-arg curried: must be matched before two-arg and one-arg App patterns
    | App (App (App (Var ("string_sub", _), strExpr, _), startExpr, _), lenExpr, _) ->
        let (strVal,   strOps)   = elaborateExpr env strExpr
        let (startVal, startOps) = elaborateExpr env startExpr
        let (lenVal,   lenOps)   = elaborateExpr env lenExpr
        let result = { Name = freshName env; Type = Ptr }
        (result, strOps @ startOps @ lenOps @ [LlvmCallOp(result, "@lang_string_sub", [strVal; startVal; lenVal])])

    // Phase 14: string_contains builtin — App(App(Var("string_contains"), s), sub)
    | App (App (Var ("string_contains", _), strExpr, _), subExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let (subVal, subOps) = elaborateExpr env subExpr
        let rawResult  = { Name = freshName env; Type = I64 }
        let zeroVal    = { Name = freshName env; Type = I64 }
        let boolResult = { Name = freshName env; Type = I1 }
        let ops = [
            LlvmCallOp(rawResult, "@lang_string_contains", [strVal; subVal])
            ArithConstantOp(zeroVal, 0L)
            ArithCmpIOp(boolResult, "ne", rawResult, zeroVal)
        ]
        (boolResult, strOps @ subOps @ ops)

    // Phase 8: string_concat builtin — App(App(Var("string_concat"), a), b)
    // Must be placed BEFORE general App to avoid being caught by general App dispatch
    | App (App (Var ("string_concat", _), aExpr, _), bExpr, _) ->
        let (aVal, aOps) = elaborateExpr env aExpr
        let (bVal, bOps) = elaborateExpr env bExpr
        let result = { Name = freshName env; Type = Ptr }
        (result, aOps @ bOps @ [LlvmCallOp(result, "@lang_string_concat", [aVal; bVal])])

    // Phase 8: to_string builtin — dispatch on elaborated arg type
    | App (Var ("to_string", _), argExpr, _) ->
        let (argVal, argOps) = elaborateExpr env argExpr
        let result = { Name = freshName env; Type = Ptr }
        match argVal.Type with
        | I1 ->
            // Zero-extend I1 to I64 for C ABI compatibility (lang_to_string_bool takes int64_t)
            let extVal = { Name = freshName env; Type = I64 }
            (result, argOps @ [ArithExtuIOp(extVal, argVal); LlvmCallOp(result, "@lang_to_string_bool", [extVal])])
        | _ ->
            // I64 and other numeric types — call lang_to_string_int directly
            (result, argOps @ [LlvmCallOp(result, "@lang_to_string_int", [argVal])])

    // Phase 8: string_length builtin — GEP field 0 and load
    | App (Var ("string_length", _), strExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let lenPtrVal = { Name = freshName env; Type = Ptr }
        let lenVal    = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmGEPStructOp(lenPtrVal, strVal, 0)
            LlvmLoadOp(lenVal, lenPtrVal)
        ]
        (lenVal, strOps @ ops)

    // Phase 14: failwith builtin — extract char* from LangString, call lang_failwith (noreturn)
    | App (Var ("failwith", _), msgExpr, _) ->
        let (msgVal, msgOps) = elaborateExpr env msgExpr
        let dataPtrVal  = { Name = freshName env; Type = Ptr }
        let dataAddrVal = { Name = freshName env; Type = Ptr }
        let unitVal     = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmGEPStructOp(dataPtrVal, msgVal, 1)
            LlvmLoadOp(dataAddrVal, dataPtrVal)
            LlvmCallVoidOp("@lang_failwith", [dataAddrVal])
            ArithConstantOp(unitVal, 0L)
        ]
        (unitVal, msgOps @ ops)

    // Phase 14: string_to_int builtin
    | App (Var ("string_to_int", _), strExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let result = { Name = freshName env; Type = I64 }
        (result, strOps @ [LlvmCallOp(result, "@lang_string_to_int", [strVal])])

    // Phase 22: array_set — three-arg (must appear before two-arg and one-arg patterns)
    // array_set arr idx val: bounds check, compute slot idx+1, GEP, store, return unit
    | App (App (App (Var ("array_set", _), arrExpr, _), idxExpr, _), valExpr, _) ->
        let (arrVal, arrOps) = elaborateExpr env arrExpr
        let (idxVal, idxOps) = elaborateExpr env idxExpr
        let (valVal, valOps) = elaborateExpr env valExpr
        let oneVal    = { Name = freshName env; Type = I64 }
        let slotVal   = { Name = freshName env; Type = I64 }
        let elemPtr   = { Name = freshName env; Type = Ptr }
        let unitVal   = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmCallVoidOp("@lang_array_bounds_check", [arrVal; idxVal])
            ArithConstantOp(oneVal, 1L)
            ArithAddIOp(slotVal, idxVal, oneVal)
            LlvmGEPDynamicOp(elemPtr, arrVal, slotVal)
            LlvmStoreOp(valVal, elemPtr)
            ArithConstantOp(unitVal, 0L)
        ]
        (unitVal, arrOps @ idxOps @ valOps @ ops)

    // Phase 22: array_get — two-arg
    // array_get arr idx: bounds check, compute slot idx+1, GEP, load
    | App (App (Var ("array_get", _), arrExpr, _), idxExpr, _) ->
        let (arrVal, arrOps) = elaborateExpr env arrExpr
        let (idxVal, idxOps) = elaborateExpr env idxExpr
        let oneVal  = { Name = freshName env; Type = I64 }
        let slotVal = { Name = freshName env; Type = I64 }
        let elemPtr = { Name = freshName env; Type = Ptr }
        let result  = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmCallVoidOp("@lang_array_bounds_check", [arrVal; idxVal])
            ArithConstantOp(oneVal, 1L)
            ArithAddIOp(slotVal, idxVal, oneVal)
            LlvmGEPDynamicOp(elemPtr, arrVal, slotVal)
            LlvmLoadOp(result, elemPtr)
        ]
        (result, arrOps @ idxOps @ ops)

    // Phase 22: array_create — two-arg
    // array_create n defVal: call lang_array_create(n, defVal)
    | App (App (Var ("array_create", _), nExpr, _), defExpr, _) ->
        let (nVal,   nOps)   = elaborateExpr env nExpr
        let (defVal, defOps) = elaborateExpr env defExpr
        // Ensure both args are I64 (defVal may be I1 from bool literals)
        let nI64 =
            if nVal.Type = I64 then (nVal, [])
            else let v = { Name = freshName env; Type = I64 } in (v, [ArithExtuIOp(v, nVal)])
        let defI64 =
            if defVal.Type = I64 then (defVal, [])
            else let v = { Name = freshName env; Type = I64 } in (v, [ArithExtuIOp(v, defVal)])
        let result = { Name = freshName env; Type = Ptr }
        let (nV, nCoerce)     = nI64
        let (defV, defCoerce) = defI64
        (result, nOps @ defOps @ nCoerce @ defCoerce @ [LlvmCallOp(result, "@lang_array_create", [nV; defV])])

    // Phase 22: array_length — one-arg
    // array_length arr: GEP slot 0 (length slot), load
    | App (Var ("array_length", _), arrExpr, _) ->
        let (arrVal, arrOps) = elaborateExpr env arrExpr
        let lenPtr = { Name = freshName env; Type = Ptr }
        let result = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmGEPLinearOp(lenPtr, arrVal, 0)
            LlvmLoadOp(result, lenPtr)
        ]
        (result, arrOps @ ops)

    // Phase 22: array_of_list — one-arg
    | App (Var ("array_of_list", _), lstExpr, _) ->
        let (lstVal, lstOps) = elaborateExpr env lstExpr
        let result = { Name = freshName env; Type = Ptr }
        (result, lstOps @ [LlvmCallOp(result, "@lang_array_of_list", [lstVal])])

    // Phase 22: array_to_list — one-arg
    | App (Var ("array_to_list", _), arrExpr, _) ->
        let (arrVal, arrOps) = elaborateExpr env arrExpr
        let result = { Name = freshName env; Type = Ptr }
        (result, arrOps @ [LlvmCallOp(result, "@lang_array_to_list", [arrVal])])

    // Phase 14: char_to_int — identity (char is already i64)
    | App (Var ("char_to_int", _), charExpr, _) ->
        elaborateExpr env charExpr

    // Phase 14: int_to_char — identity (int treated as char code point)
    | App (Var ("int_to_char", _), intExpr, _) ->
        elaborateExpr env intExpr

    // Phase 7: print/println builtins — static literal fast path (keep before general case)
    | App (Var ("print", _), String (s, _), _) ->
        let globalName = addStringGlobal env s
        let ptrVal  = { Name = freshName env; Type = Ptr }
        let fmtRes  = { Name = freshName env; Type = I32 }
        let unitVal = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmAddressOfOp(ptrVal, globalName)
            LlvmCallOp(fmtRes, "@printf", [ptrVal])
            ArithConstantOp(unitVal, 0L)
        ]
        (unitVal, ops)

    | App (Var ("println", _), String (s, _), _) ->
        let globalName = addStringGlobal env (s + "\n")
        let ptrVal  = { Name = freshName env; Type = Ptr }
        let fmtRes  = { Name = freshName env; Type = I32 }
        let unitVal = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmAddressOfOp(ptrVal, globalName)
            LlvmCallOp(fmtRes, "@printf", [ptrVal])
            ArithConstantOp(unitVal, 0L)
        ]
        (unitVal, ops)

    // Phase 8: general print/println for string struct variables (Ptr type)
    | App (Var ("print", _), strExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let dataPtrVal  = { Name = freshName env; Type = Ptr }
        let dataAddrVal = { Name = freshName env; Type = Ptr }
        let fmtRes      = { Name = freshName env; Type = I32 }
        let unitVal     = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmGEPStructOp(dataPtrVal, strVal, 1)
            LlvmLoadOp(dataAddrVal, dataPtrVal)
            LlvmCallOp(fmtRes, "@printf", [dataAddrVal])
            ArithConstantOp(unitVal, 0L)
        ]
        (unitVal, strOps @ ops)

    | App (Var ("println", _), strExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let dataPtrVal  = { Name = freshName env; Type = Ptr }
        let dataAddrVal = { Name = freshName env; Type = Ptr }
        let nlGlobal    = addStringGlobal env "\n"
        let nlPtrVal    = { Name = freshName env; Type = Ptr }
        let fmtRes1     = { Name = freshName env; Type = I32 }
        let fmtRes2     = { Name = freshName env; Type = I32 }
        let unitVal     = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmGEPStructOp(dataPtrVal, strVal, 1)
            LlvmLoadOp(dataAddrVal, dataPtrVal)
            LlvmCallOp(fmtRes1, "@printf", [dataAddrVal])
            LlvmAddressOfOp(nlPtrVal, nlGlobal)
            LlvmCallOp(fmtRes2, "@printf", [nlPtrVal])
            ArithConstantOp(unitVal, 0L)
        ]
        (unitVal, strOps @ ops)

    | App (funcExpr, argExpr, _) ->
        match funcExpr with
        | Var (name, _) ->
            match Map.tryFind name env.KnownFuncs with
            | Some sig_ when sig_.ClosureInfo.IsNone ->
                // DIRECT CALL (Phase 4 behavior) — known non-closure function
                let (argVal, argOps) = elaborateExpr env argExpr
                let result = { Name = freshName env; Type = sig_.ReturnType }
                (result, argOps @ [DirectCallOp(result, sig_.MlirName, [argVal])])
            | Some sig_ ->
                // CLOSURE-MAKING CALL — allocate env on GC heap, then call
                let ci = sig_.ClosureInfo.Value
                let (argVal, argOps) = elaborateExpr env argExpr
                let bytesVal = { Name = freshName env; Type = I64 }
                let envPtrVal = { Name = freshName env; Type = Ptr }
                let resultVal = { Name = freshName env; Type = Ptr }
                let setupOps = [
                    ArithConstantOp(bytesVal, int64 ((ci.NumCaptures + 1) * 8))
                    LlvmCallOp(envPtrVal, "@GC_malloc", [bytesVal])
                ]
                // Closure-maker always expects I64 as first arg (uniform ABI).
                // If argVal is Ptr (e.g., a constructor closure), emit ptrtoint first.
                let (i64ArgVal, coerceOps) =
                    if argVal.Type = Ptr then
                        let coerced = { Name = freshName env; Type = I64 }
                        (coerced, [LlvmPtrToIntOp(coerced, argVal)])
                    else
                        (argVal, [])
                let callOp = DirectCallOp(resultVal, sig_.MlirName, [i64ArgVal; envPtrVal])
                (resultVal, argOps @ setupOps @ coerceOps @ [callOp])
            | None ->
                // Check Vars for closure value (type Ptr)
                match Map.tryFind name env.Vars with
                | Some closureVal when closureVal.Type = Ptr ->
                    // INDIRECT/CLOSURE CALL
                    let (argVal, argOps) = elaborateExpr env argExpr
                    let fnPtrVal = { Name = freshName env; Type = Ptr }
                    let result = { Name = freshName env; Type = I64 }
                    let loadOp = LlvmLoadOp(fnPtrVal, closureVal)
                    let callOp = IndirectCallOp(result, fnPtrVal, closureVal, argVal)
                    (result, argOps @ [loadOp; callOp])
                | Some closureVal when closureVal.Type = I64 ->
                    // I64 value may be a closure passed through uniform ABI — cast to Ptr and attempt indirect call
                    let (argVal, argOps) = elaborateExpr env argExpr
                    let closurePtrVal = { Name = freshName env; Type = Ptr }
                    let fnPtrVal = { Name = freshName env; Type = Ptr }
                    let result = { Name = freshName env; Type = I64 }
                    let castOp = LlvmIntToPtrOp(closurePtrVal, closureVal)
                    let loadOp = LlvmLoadOp(fnPtrVal, closurePtrVal)
                    let callOp = IndirectCallOp(result, fnPtrVal, closurePtrVal, argVal)
                    (result, argOps @ [castOp; loadOp; callOp])
                | _ ->
                    failwithf "Elaboration: unsupported App — '%s' is not a known function or closure value" name
        | Lambda(param, body, _) ->
            // Inline lambda application: (fun x -> body) arg  ≡  let x = arg in body
            let (argVal, argOps) = elaborateExpr env argExpr
            let env' = { env with Vars = Map.add param argVal env.Vars }
            let (bodyVal, bodyOps) = elaborateExpr env' body
            (bodyVal, argOps @ bodyOps)
        | _ ->
            // General case: evaluate funcExpr to get a closure value, then dispatch
            let (funcVal, funcOps) = elaborateExpr env funcExpr
            let (argVal, argOps) = elaborateExpr env argExpr
            if funcVal.Type = Ptr then
                // Ptr-typed closure: load fn_ptr from slot 0, call indirect
                let fnPtrVal = { Name = freshName env; Type = Ptr }
                let result = { Name = freshName env; Type = I64 }
                let loadOp = LlvmLoadOp(fnPtrVal, funcVal)
                let callOp = IndirectCallOp(result, fnPtrVal, funcVal, argVal)
                (result, funcOps @ argOps @ [loadOp; callOp])
            elif funcVal.Type = I64 then
                // I64-typed closure (passed through uniform ABI): inttoptr then indirect call
                let closurePtrVal = { Name = freshName env; Type = Ptr }
                let fnPtrVal = { Name = freshName env; Type = Ptr }
                let result = { Name = freshName env; Type = I64 }
                let castOp = LlvmIntToPtrOp(closurePtrVal, funcVal)
                let loadOp = LlvmLoadOp(fnPtrVal, closurePtrVal)
                let callOp = IndirectCallOp(result, fnPtrVal, closurePtrVal, argVal)
                (result, funcOps @ argOps @ [castOp; loadOp; callOp])
            else
                failwithf "Elaboration: unsupported App — function expression elaborated to unsupported type %A" funcVal.Type
    // Phase 9: Tuple construction — GC_malloc(n*8) + sequential GEP + store
    | Tuple (exprs, _) ->
        let n = List.length exprs
        // Elaborate all field expressions first
        let fieldResults = exprs |> List.map (fun e -> elaborateExpr env e)
        let allFieldOps  = fieldResults |> List.collect snd
        let fieldVals    = fieldResults |> List.map fst
        // GC_malloc(n * 8) to allocate the tuple struct
        let bytesVal  = { Name = freshName env; Type = I64 }
        let tupPtrVal = { Name = freshName env; Type = Ptr }
        let allocOps  = [
            ArithConstantOp(bytesVal, int64 (n * 8))
            LlvmCallOp(tupPtrVal, "@GC_malloc", [bytesVal])
        ]
        // Store each field: GEP ptr[i] + store value at slot
        let storeOps =
            fieldVals |> List.mapi (fun i fv ->
                let slotVal = { Name = freshName env; Type = Ptr }
                [ LlvmGEPLinearOp(slotVal, tupPtrVal, i)
                  LlvmStoreOp(fv, slotVal) ]
            ) |> List.concat
        (tupPtrVal, allFieldOps @ allocOps @ storeOps)

    // Phase 9: Match with single TuplePat arm — desugar to LetPat(TuplePat)
    | Match (scrutinee, [(TuplePat (pats, patSpan), None, body)], span) ->
        let syntheticPat = TuplePat(pats, patSpan)
        elaborateExpr env (LetPat(syntheticPat, scrutinee, body, span))

    // Phase 10: EmptyList — null pointer constant
    | EmptyList _ ->
        let v = { Name = freshName env; Type = Ptr }
        (v, [LlvmNullOp(v)])

    // Phase 10: Cons — GC_malloc(16) cons cell with head at slot 0, tail at slot 1
    | Cons(headExpr, tailExpr, _) ->
        let (headVal, headOps) = elaborateExpr env headExpr
        let (tailVal, tailOps) = elaborateExpr env tailExpr
        let bytesVal = { Name = freshName env; Type = I64 }
        let cellPtr  = { Name = freshName env; Type = Ptr }
        let tailSlot = { Name = freshName env; Type = Ptr }
        let allocOps = [
            ArithConstantOp(bytesVal, 16L)
            LlvmCallOp(cellPtr, "@GC_malloc", [bytesVal])
            LlvmStoreOp(headVal, cellPtr)               // head at slot 0 (base ptr)
            LlvmGEPLinearOp(tailSlot, cellPtr, 1)       // slot 1 for tail
            LlvmStoreOp(tailVal, tailSlot)              // store tail ptr at slot 1
        ]
        (cellPtr, headOps @ tailOps @ allocOps)

    // Phase 10: List literal — desugar [e1; e2; e3] to Cons(e1, Cons(e2, Cons(e3, EmptyList)))
    | List(elems, span) ->
        let desugared = List.foldBack (fun elem acc -> Cons(elem, acc, span)) elems (EmptyList span)
        elaborateExpr env desugared

    // Phase 15: Range [start..stop] or [start..step..stop]
    // Compiled to a call to C runtime lang_range(start, stop, step).
    // Default step is 1 when not specified.
    | Range(startExpr, stopExpr, stepOpt, _) ->
        let (startVal, startOps) = elaborateExpr env startExpr
        let (stopVal,  stopOps)  = elaborateExpr env stopExpr
        let (stepVal,  stepOps)  =
            match stepOpt with
            | Some stepExpr -> elaborateExpr env stepExpr
            | None ->
                let v = { Name = freshName env; Type = I64 }
                (v, [ArithConstantOp(v, 1L)])
        let result = { Name = freshName env; Type = Ptr }
        (result, startOps @ stopOps @ stepOps @ [LlvmCallOp(result, "@lang_range", [startVal; stopVal; stepVal])])

    // Phase 11 (v2): General Match compiler — Jules Jacobs decision tree algorithm.
    // Compiles pattern matching to a binary decision tree, then emits MLIR from the tree.
    // Handles: VarPat, WildcardPat, ConstPat(Int/Bool/String), EmptyListPat, ConsPat, TuplePat.
    // Phase 13: adds OrPat expansion (PAT-07), when-guard (PAT-06), CharConst (PAT-08).
    | Match(scrutineeExpr, clauses, _) ->
        let (scrutVal, scrutOps) = elaborateExpr env scrutineeExpr
        let mergeLabel = freshLabel env "match_merge"
        let failLabel  = freshLabel env "match_fail"

        // PAT-07: Expand OrPat arms into individual arms sharing same guard and body
        let clauses =
            clauses |> List.collect (fun (pat, guard, body) ->
                match pat with
                | OrPat (alts, _) -> alts |> List.map (fun altPat -> (altPat, guard, body))
                | _ -> [(pat, guard, body)]
            )

        // Build arms for MatchCompiler: (Pattern * hasGuard * bodyIndex) list
        let arms = clauses |> List.mapi (fun i (pat, guardOpt, _body) -> (pat, guardOpt.IsSome, i))
        let rootAcc = MatchCompiler.Root scrutVal.Name

        // Compile to decision tree
        let tree = MatchCompiler.compile rootAcc arms

        // Map from Accessor to MlirValue, built up as we emit GEP/load ops.
        // Start with the root accessor mapped to the scrutinee value.
        let accessorCache = System.Collections.Generic.Dictionary<MatchCompiler.Accessor, MlirValue>()
        accessorCache.[rootAcc] <- scrutVal

        // Resolve an accessor to an MlirValue, emitting GEP + load ops as needed.
        // Returns (value, ops).
        let rec resolveAccessor (acc: MatchCompiler.Accessor) : MlirValue * MlirOp list =
            match accessorCache.TryGetValue(acc) with
            | true, v -> (v, [])
            | false, _ ->
                match acc with
                | MatchCompiler.Root _ -> failwith "resolveAccessor: Root not in cache"
                | MatchCompiler.Field (parent, idx) ->
                    let (rawParentVal, parentOps) = resolveAccessor parent
                    // Guard: GEP requires a Ptr parent. If the parent was cached as I64
                    // (e.g., ADT slot 1 loaded as I64 but holding a tuple Ptr), re-resolve
                    // the parent as Ptr so we can GEP into the pointed-to block.
                    let (parentVal, retypeOps) =
                        if rawParentVal.Type = Ptr then (rawParentVal, [])
                        else resolveAccessorTyped parent Ptr
                    let slotVal  = { Name = freshName env; Type = Ptr }
                    let gepOp    = LlvmGEPLinearOp(slotVal, parentVal, idx)
                    // Default: load field as I64. resolveAccessorTyped handles re-loads
                    // when a specific type is needed (e.g., Ptr for list tail, tuple payload).
                    let fieldVal = { Name = freshName env; Type = I64 }
                    let loadOp   = LlvmLoadOp(fieldVal, slotVal)
                    accessorCache.[acc] <- fieldVal
                    (fieldVal, parentOps @ retypeOps @ [gepOp; loadOp])

        // Resolve an accessor, but load with a specific type override.
        // This is needed when we know the field should be Ptr (e.g. list tail, nested tuple).
        and resolveAccessorTyped (acc: MatchCompiler.Accessor) (ty: MlirType) : MlirValue * MlirOp list =
            match accessorCache.TryGetValue(acc) with
            | true, v when v.Type = ty -> (v, [])
            | true, v ->
                // Cached with wrong type — we need to re-load with correct type.
                // This happens when a field was first loaded as I64 but actually needs to be Ptr.
                // Emit a new load from the parent.
                match acc with
                | MatchCompiler.Root _ ->
                    // Phase 20: Root accessor type mismatch — emit inttoptr when root is I64 but Ptr needed.
                    // This occurs when a first-class constructor closure returns ptrtoint(ADT block),
                    // and the match scrutinee is the i64 result of the indirect call.
                    if v.Type = I64 && ty = Ptr then
                        let ptrVal = { Name = freshName env; Type = Ptr }
                        accessorCache.[acc] <- ptrVal
                        (ptrVal, [LlvmIntToPtrOp(ptrVal, v)])
                    else (v, [])
                | MatchCompiler.Field (parent, idx) ->
                    // GEP requires a Ptr base — ensure parent is resolved as Ptr.
                    let (parentVal, parentOps) = resolveAccessorTyped parent Ptr
                    let slotVal  = { Name = freshName env; Type = Ptr }
                    let fieldVal = { Name = freshName env; Type = ty }
                    let gepOp    = LlvmGEPLinearOp(slotVal, parentVal, idx)
                    let loadOp   = LlvmLoadOp(fieldVal, slotVal)
                    accessorCache.[acc] <- fieldVal
                    (fieldVal, parentOps @ [gepOp; loadOp])
            | false, _ ->
                match acc with
                | MatchCompiler.Root _ -> failwith "resolveAccessorTyped: Root not in cache"
                | MatchCompiler.Field (parent, idx) ->
                    // GEP requires a Ptr base — ensure parent is resolved as Ptr.
                    let (parentVal, parentOps) = resolveAccessorTyped parent Ptr
                    let slotVal  = { Name = freshName env; Type = Ptr }
                    let fieldVal = { Name = freshName env; Type = ty }
                    let gepOp    = LlvmGEPLinearOp(slotVal, parentVal, idx)
                    let loadOp   = LlvmLoadOp(fieldVal, slotVal)
                    accessorCache.[acc] <- fieldVal
                    (fieldVal, parentOps @ [gepOp; loadOp])

        // Determine what MlirType an accessor should have based on the constructor tag
        // being tested.  For ConsCtor: scrutinee is Ptr, field 0 = I64, field 1 = Ptr.
        // For NilCtor: scrutinee is Ptr.
        // For TupleCtor: scrutinee is Ptr, all fields default to I64 (nested tuples Ptr).
        // For IntLit/BoolLit: scrutinee is I64/I1 respectively.
        // For StringLit: scrutinee is Ptr.
        let scrutineeTypeForTag (tag: MatchCompiler.CtorTag) : MlirType =
            match tag with
            | MatchCompiler.IntLit _    -> I64
            | MatchCompiler.BoolLit _   -> I1
            | MatchCompiler.StringLit _ -> Ptr
            | MatchCompiler.ConsCtor    -> Ptr
            | MatchCompiler.NilCtor     -> Ptr
            | MatchCompiler.TupleCtor _ -> Ptr
            | MatchCompiler.AdtCtor _    -> Ptr
            | MatchCompiler.RecordCtor _ -> Ptr

        // Emit test ops for a constructor match against a scrutinee value.
        // Returns (condValue, testOps).
        let emitCtorTest (scrutVal: MlirValue) (tag: MatchCompiler.CtorTag) : MlirValue * MlirOp list =
            match tag with
            | MatchCompiler.IntLit n ->
                let kVal = { Name = freshName env; Type = I64 }
                let cond = { Name = freshName env; Type = I1 }
                let ops = [ ArithConstantOp(kVal, int64 n); ArithCmpIOp(cond, "eq", scrutVal, kVal) ]
                (cond, ops)
            | MatchCompiler.BoolLit b ->
                let kVal = { Name = freshName env; Type = I1 }
                let cond = { Name = freshName env; Type = I1 }
                let n = if b then 1L else 0L
                let ops = [ ArithConstantOp(kVal, n); ArithCmpIOp(cond, "eq", scrutVal, kVal) ]
                (cond, ops)
            | MatchCompiler.StringLit s ->
                let (patStrVal, patStrOps) = elaborateStringLiteral env s
                let patDataPtrVal   = { Name = freshName env; Type = Ptr }
                let patDataVal      = { Name = freshName env; Type = Ptr }
                let scrutDataPtrVal = { Name = freshName env; Type = Ptr }
                let scrutDataVal    = { Name = freshName env; Type = Ptr }
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
                (cond, ops)
            | MatchCompiler.NilCtor ->
                let nullVal = { Name = freshName env; Type = Ptr }
                let cond    = { Name = freshName env; Type = I1 }
                let ops = [ LlvmNullOp(nullVal); LlvmIcmpOp(cond, "eq", scrutVal, nullVal) ]
                (cond, ops)
            | MatchCompiler.ConsCtor ->
                let nullVal = { Name = freshName env; Type = Ptr }
                let cond    = { Name = freshName env; Type = I1 }
                let ops = [ LlvmNullOp(nullVal); LlvmIcmpOp(cond, "ne", scrutVal, nullVal) ]
                (cond, ops)
            | MatchCompiler.TupleCtor _ ->
                // Tuples always match structurally — emit an unconditional true
                let cond = { Name = freshName env; Type = I1 }
                let ops = [ ArithConstantOp(cond, 1L) ]
                (cond, ops)
            | MatchCompiler.AdtCtor(name, _, _) ->
                // Look up real tag from TypeEnv; load tag slot 0 and compare
                let info     = Map.find name env.TypeEnv
                let tagSlot  = { Name = freshName env; Type = Ptr }
                let tagLoad  = { Name = freshName env; Type = I64 }
                let tagConst = { Name = freshName env; Type = I64 }
                let cond     = { Name = freshName env; Type = I1 }
                let ops = [
                    LlvmGEPLinearOp(tagSlot, scrutVal, 0)
                    LlvmLoadOp(tagLoad, tagSlot)
                    ArithConstantOp(tagConst, int64 info.Tag)
                    ArithCmpIOp(cond, "eq", tagLoad, tagConst)
                ]
                (cond, ops)
            | MatchCompiler.RecordCtor _ ->
                // Records always match structurally — emit unconditional true
                let cond = { Name = freshName env; Type = I1 }
                let ops  = [ ArithConstantOp(cond, 1L) ]
                (cond, ops)

        // Pre-populate accessor cache for ConsCtor sub-fields with correct types.
        // field 0 = head (I64), field 1 = tail (Ptr).
        let ensureConsFieldTypes (scrutAcc: MatchCompiler.Accessor) (argAccs: MatchCompiler.Accessor list) : MlirOp list =
            let mutable ops = []
            if argAccs.Length >= 1 then
                let (_, headOps) = resolveAccessorTyped argAccs.[0] I64
                ops <- ops @ headOps
            if argAccs.Length >= 2 then
                let (_, tailOps) = resolveAccessorTyped argAccs.[1] Ptr
                ops <- ops @ tailOps
            ops

        // Pre-populate accessor cache for AdtCtor sub-fields with correct types.
        // ADT blocks: slot 0 = tag (I64), slot 1 = payload (stored directly — I64 for int, Ptr for tuple/string).
        // For unary constructors the payload accessor is argAccs.[0] = Field(scrutAcc, 1).
        // We pre-load it as I64 (the default), which is correct for integer payloads.
        // For Ptr payloads the resolveAccessorTyped call in emitDecisionTree will re-load with the right type.
        let ensureAdtFieldTypes (_scrutAcc: MatchCompiler.Accessor) (argAccs: MatchCompiler.Accessor list) : MlirOp list =
            let mutable ops = []
            if argAccs.Length >= 1 then
                // Payload is at slot 1; default load as I64
                let (_, payOps) = resolveAccessorTyped argAccs.[0] I64
                ops <- ops @ payOps
            ops

        // Pre-populate accessor cache for RecordCtor sub-fields with declaration-order slot indices.
        // MatchCompiler generates argAccessors in alphabetical field-name order, but RecordEnv stores
        // fields using declaration-order slot indices.  We must remap: for each field name (in
        // alphabetical order, matching argAccs.[i]), look up its declaration-order slot index in
        // RecordEnv and emit GEP+load using that slot so the correct heap word is read.
        let ensureRecordFieldTypes (fields: string list) (scrutAcc: MatchCompiler.Accessor) (argAccs: MatchCompiler.Accessor list) : MlirOp list =
            let fieldSet = Set.ofList fields
            let fieldMap =
                env.RecordEnv
                |> Map.toSeq
                |> Seq.tryPick (fun (_, fm) ->
                    let fmFields = fm |> Map.toSeq |> Seq.map fst |> Set.ofSeq
                    if fieldSet |> Set.forall (fun f -> Set.contains f fmFields) then Some fm
                    else None)
                |> Option.defaultWith (fun () ->
                    failwithf "ensureRecordFieldTypes: cannot resolve record type for fields %A" fields)
            let mutable ops = []
            fields |> List.iteri (fun i fieldName ->
                if i < argAccs.Length then
                    let declSlotIdx = Map.find fieldName fieldMap
                    let (parentVal, parentOps) = resolveAccessorTyped scrutAcc Ptr
                    let slotPtr  = { Name = freshName env; Type = Ptr }
                    let fieldVal = { Name = freshName env; Type = I64 }
                    let gepOp  = LlvmGEPLinearOp(slotPtr, parentVal, declSlotIdx)
                    let loadOp = LlvmLoadOp(fieldVal, slotPtr)
                    accessorCache.[argAccs.[i]] <- fieldVal
                    ops <- ops @ parentOps @ [gepOp; loadOp]
            )
            ops

        // Track the result type (determined by first leaf reached)
        let resultType = ref I64  // default; will be set by first arm body

        // Recursively emit blocks for a DecisionTree node.
        // Returns the list of ops for the current/entry block.
        let rec emitDecisionTree (tree: MatchCompiler.DecisionTree) : MlirOp list =
            match tree with
            | MatchCompiler.Fail ->
                [ CfBrOp(failLabel, []) ]
            | MatchCompiler.Leaf (bindings, bodyIdx) ->
                // Resolve all bindings: each (varName, accessor) → env binding
                let mutable bindOps = []
                let mutable bindEnv = env
                for (varName, acc) in bindings do
                    let (v, ops) = resolveAccessor acc
                    bindOps <- bindOps @ ops
                    bindEnv <- { bindEnv with Vars = Map.add varName v bindEnv.Vars }
                // Get the body expression from the original clauses
                let (_pat, _guard, bodyExpr) = clauses.[bodyIdx]
                let (bodyVal, bodyOps) = elaborateExpr bindEnv bodyExpr
                resultType.Value <- bodyVal.Type
                // Create a body block and branch to merge (skip merge branch if body already terminated)
                let terminatedOps =
                    match List.tryLast bodyOps with
                    | Some LlvmUnreachableOp -> bodyOps
                    | Some (CfBrOp _) | Some (CfCondBrOp _) -> bodyOps
                    | _ -> bodyOps @ [CfBrOp(mergeLabel, [bodyVal])]
                let bodyLabel = freshLabel env (sprintf "match_body%d" bodyIdx)
                env.Blocks.Value <- env.Blocks.Value @
                    [ { Label = Some bodyLabel
                        Args  = []
                        Body  = terminatedOps } ]
                bindOps @ [ CfBrOp(bodyLabel, []) ]
            | MatchCompiler.Switch (scrutAcc, tag, argAccs, ifMatch, ifNoMatch) ->
                // Resolve the scrutinee accessor with the correct type for this tag
                let expectedType = scrutineeTypeForTag tag
                let (sVal, resolveOps) = resolveAccessorTyped scrutAcc expectedType
                // Emit the test
                let (cond, testOps) = emitCtorTest sVal tag
                // Pre-load sub-fields with correct types into the cache
                let preloadOps =
                    match tag with
                    | MatchCompiler.ConsCtor -> ensureConsFieldTypes scrutAcc argAccs
                    | MatchCompiler.AdtCtor(_, _, arity) when arity > 0 -> ensureAdtFieldTypes scrutAcc argAccs
                    | MatchCompiler.RecordCtor fields -> ensureRecordFieldTypes fields scrutAcc argAccs
                    | _ -> []
                // Emit ifMatch and ifNoMatch as separate blocks
                let matchLabel   = freshLabel env "match_yes"
                let noMatchLabel = freshLabel env "match_no"
                let matchOps   = emitDecisionTree ifMatch
                let noMatchOps = emitDecisionTree ifNoMatch
                env.Blocks.Value <- env.Blocks.Value @
                    [ { Label = Some matchLabel; Args = []; Body = preloadOps @ matchOps }
                      { Label = Some noMatchLabel; Args = []; Body = noMatchOps } ]
                resolveOps @ testOps @ [ CfCondBrOp(cond, matchLabel, [], noMatchLabel, []) ]
            | MatchCompiler.Guard (bindings, bodyIdx, ifFalse) ->
                // 1. Resolve variable bindings (same as Leaf)
                let mutable bindOps = []
                let mutable bindEnv = env
                for (varName, acc) in bindings do
                    let (v, ops) = resolveAccessor acc
                    bindOps <- bindOps @ ops
                    bindEnv <- { bindEnv with Vars = Map.add varName v bindEnv.Vars }
                // 2. Evaluate the when-guard under bindEnv → I1 value
                let (_pat, guardOpt, bodyExpr) = clauses.[bodyIdx]
                let guard = guardOpt.Value  // HasGuard=true guarantees Some
                let (guardVal, guardOps) = elaborateExpr bindEnv guard
                // 3. Emit the ifFalse subtree as a separate block
                let guardFailLabel = freshLabel env (sprintf "guard_fail%d" bodyIdx)
                let guardFailOps = emitDecisionTree ifFalse
                env.Blocks.Value <- env.Blocks.Value @
                    [ { Label = Some guardFailLabel; Args = []; Body = guardFailOps } ]
                // 4. Emit the body block (same as Leaf, skip merge branch if body already terminated)
                let (bodyVal, bodyOps) = elaborateExpr bindEnv bodyExpr
                resultType.Value <- bodyVal.Type
                let terminatedOps =
                    match List.tryLast bodyOps with
                    | Some LlvmUnreachableOp -> bodyOps
                    | Some (CfBrOp _) | Some (CfCondBrOp _) -> bodyOps
                    | _ -> bodyOps @ [CfBrOp(mergeLabel, [bodyVal])]
                let bodyLabel = freshLabel env (sprintf "match_body%d" bodyIdx)
                env.Blocks.Value <- env.Blocks.Value @
                    [ { Label = Some bodyLabel; Args = [];
                        Body = terminatedOps } ]
                // 5. Return: bind ops + guard eval + conditional branch
                bindOps @ guardOps @ [CfCondBrOp(guardVal, bodyLabel, [], guardFailLabel, [])]

        let chainEntryOps = emitDecisionTree tree

        // Failure block
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some failLabel
                Args   = []
                Body   = [ LlvmCallVoidOp("@lang_match_failure", [])
                           LlvmUnreachableOp ] } ]

        // Merge block
        let mergeArg = { Name = freshName env; Type = resultType.Value }
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some mergeLabel; Args = [mergeArg]; Body = [] } ]

        (mergeArg, scrutOps @ chainEntryOps)

    // Phase 12: bare Lambda as a value — create inline closure struct
    // Used by ComposeRight/ComposeLeft which desugar to Lambda(...) as expressions
    | Lambda (param, body, _) ->
        // Free variables relative to param, restricted to those in env.Vars (not KnownFuncs — they are direct calls)
        let allFree = freeVars (Set.singleton param) body
        let captures =
            allFree
            |> Set.filter (fun name -> Map.containsKey name env.Vars)
            |> Set.toList
            |> List.sort
        let numCaptures = captures.Length

        // Generate closure function name
        let closureFnIdx = env.ClosureCounter.Value
        env.ClosureCounter.Value <- closureFnIdx + 1
        let closureFnName = sprintf "@closure_fn_%d" closureFnIdx

        // Build inner llvm.func: (%arg0: !llvm.ptr, %arg1: i64) -> i64
        let arg0Val = { Name = "%arg0"; Type = Ptr }
        let arg1Val = { Name = "%arg1"; Type = I64 }
        let innerEnv : ElabEnv =
            { Vars = Map.ofList [(param, arg1Val)]
              Counter = ref 0; LabelCounter = ref 0; Blocks = ref []
              KnownFuncs = env.KnownFuncs
              Funcs = env.Funcs
              ClosureCounter = env.ClosureCounter
              Globals = env.Globals
              GlobalCounter = env.GlobalCounter
              TypeEnv = env.TypeEnv; RecordEnv = env.RecordEnv; ExnTags = env.ExnTags
              MutableVars = env.MutableVars }
        let captureLoadOps, innerEnvWithCaptures =
            captures |> List.mapi (fun i capName ->
                let gepVal = { Name = sprintf "%%t%d" i; Type = Ptr }
                let capType = if Set.contains capName env.MutableVars then Ptr else I64
                let capVal = { Name = sprintf "%%t%d" (i + numCaptures); Type = capType }
                (gepVal, capVal, capName, i)
            )
            |> List.fold (fun (opsAcc, envAcc: ElabEnv) (gepVal, capVal, capName, i) ->
                let gepOp  = LlvmGEPLinearOp(gepVal, arg0Val, i + 1)
                let loadOp = LlvmLoadOp(capVal, gepVal)
                let envAcc' = { envAcc with Vars = Map.add capName capVal envAcc.Vars }
                (opsAcc @ [gepOp; loadOp], envAcc')
            ) ([], innerEnv)
        innerEnvWithCaptures.Counter.Value <- numCaptures * 2
        let (bodyVal, bodyEntryOps) = elaborateExpr innerEnvWithCaptures body
        // Phase 20: if body returns Ptr (e.g. constructor closure), ptrtoint to I64 for uniform closure return type.
        // The call site uses inttoptr to recover the pointer when matched against ADT patterns.
        let (finalRetVal, ptrToIntOps) =
            if bodyVal.Type = Ptr then
                let i64Val = { Name = freshName innerEnvWithCaptures; Type = I64 }
                (i64Val, [LlvmPtrToIntOp(i64Val, bodyVal)])
            else (bodyVal, [])
        let bodySideBlocks = innerEnvWithCaptures.Blocks.Value
        let allBodyBlocks =
            if bodySideBlocks.IsEmpty then
                [ { Label = None; Args = []; Body = captureLoadOps @ bodyEntryOps @ ptrToIntOps @ [LlvmReturnOp [finalRetVal]] } ]
            else
                let entryBlock = { Label = None; Args = []; Body = captureLoadOps @ bodyEntryOps }
                let lastBlock = List.last bodySideBlocks
                let lastBlockWithReturn = { lastBlock with Body = lastBlock.Body @ ptrToIntOps @ [LlvmReturnOp [finalRetVal]] }
                let sideBlocksPatched = (List.take (bodySideBlocks.Length - 1) bodySideBlocks) @ [lastBlockWithReturn]
                entryBlock :: sideBlocksPatched
        let innerFuncOp : FuncOp =
            { Name = closureFnName; InputTypes = [Ptr; I64]; ReturnType = Some I64
              Body = { Blocks = allBodyBlocks }; IsLlvmFunc = true }
        env.Funcs.Value <- env.Funcs.Value @ [innerFuncOp]

        // Inline-allocate closure struct and populate it
        let bytesVal  = { Name = freshName env; Type = I64 }
        let envPtrVal = { Name = freshName env; Type = Ptr }
        let fnPtrVal  = { Name = freshName env; Type = Ptr }
        let allocOps = [
            ArithConstantOp(bytesVal, int64 ((numCaptures + 1) * 8))
            LlvmCallOp(envPtrVal, "@GC_malloc", [bytesVal])
            LlvmAddressOfOp(fnPtrVal, closureFnName)
            LlvmStoreOp(fnPtrVal, envPtrVal)
        ]
        let captureStoreOps =
            captures |> List.mapi (fun i capName ->
                let slotVal = { Name = freshName env; Type = Ptr }
                let capVal  = Map.find capName env.Vars
                [ LlvmGEPLinearOp(slotVal, envPtrVal, i + 1)
                  LlvmStoreOp(capVal, slotVal) ]
            ) |> List.concat
        (envPtrVal, allocOps @ captureStoreOps)

    // Phase 17: ADT constructor — nullary variant (e.g. Red in type Color = Red | Green | Blue)
    // Allocates a 16-byte block: slot 0 = i64 tag, slot 1 = null ptr.
    // Phase 20: If arity >= 1, the constructor is used as a first-class value (e.g. `apply Some 42`).
    // In that case, wrap as Lambda(param, Constructor(name, Some(Var(param)), _)) and re-elaborate.
    | Constructor(name, None, _) ->
        let info = Map.find name env.TypeEnv
        if info.Arity >= 1 then
            // First-class unary+ constructor: produce a closure fun __ctor_N_Name x -> Name x
            let n = env.Counter.Value
            env.Counter.Value <- n + 1
            let paramName = sprintf "__ctor_%d_%s" n name
            let s = Ast.unknownSpan
            elaborateExpr env (Lambda(paramName, Constructor(name, Some(Var(paramName, s)), s), s))
        else
            // Nullary constructor: allocate 16-byte block with tag and null payload
            let sizeVal     = { Name = freshName env; Type = I64 }
            let blockPtr    = { Name = freshName env; Type = Ptr }
            let tagSlot     = { Name = freshName env; Type = Ptr }
            let tagVal      = { Name = freshName env; Type = I64 }
            let paySlot     = { Name = freshName env; Type = Ptr }
            let nullPayload = { Name = freshName env; Type = Ptr }
            let ops = [
                ArithConstantOp(sizeVal, 16L)
                LlvmCallOp(blockPtr, "@GC_malloc", [sizeVal])
                LlvmGEPLinearOp(tagSlot, blockPtr, 0)
                ArithConstantOp(tagVal, int64 info.Tag)
                LlvmStoreOp(tagVal, tagSlot)
                LlvmGEPLinearOp(paySlot, blockPtr, 1)
                LlvmNullOp(nullPayload)
                LlvmStoreOp(nullPayload, paySlot)
            ]
            (blockPtr, ops)

    // Phase 17: ADT constructor — unary/multi-arg variant (e.g. Some 42, Pair(3,4))
    // Allocates a 16-byte block: slot 0 = i64 tag, slot 1 = argVal (I64 or Ptr).
    // Multi-arg constructors: the parser produces Constructor("Pair", Some(Tuple([3;4])), _);
    // elaborating the Tuple arg already yields a heap-allocated Ptr — stored directly at slot 1.
    | Constructor(name, Some argExpr, _) ->
        let info     = Map.find name env.TypeEnv
        let (argVal, argOps) = elaborateExpr env argExpr
        let sizeVal  = { Name = freshName env; Type = I64 }
        let blockPtr = { Name = freshName env; Type = Ptr }
        let tagSlot  = { Name = freshName env; Type = Ptr }
        let tagVal   = { Name = freshName env; Type = I64 }
        let paySlot  = { Name = freshName env; Type = Ptr }
        let allocOps = [
            ArithConstantOp(sizeVal, 16L)
            LlvmCallOp(blockPtr, "@GC_malloc", [sizeVal])
            LlvmGEPLinearOp(tagSlot, blockPtr, 0)
            ArithConstantOp(tagVal, int64 info.Tag)
            LlvmStoreOp(tagVal, tagSlot)
            LlvmGEPLinearOp(paySlot, blockPtr, 1)
            LlvmStoreOp(argVal, paySlot)
        ]
        (blockPtr, argOps @ allocOps)

    // Phase 18: RecordExpr construction — allocate n-slot GC_malloc block, store fields in declaration order
    | RecordExpr(typeNameOpt, fields, _) ->
        let fieldNames = fields |> List.map fst |> Set.ofList
        let typeName =
            match typeNameOpt with
            | Some n -> n
            | None ->
                env.RecordEnv
                |> Map.tryFindKey (fun _ fmap ->
                    Set.ofSeq (fmap |> Map.toSeq |> Seq.map fst) = fieldNames)
                |> Option.defaultWith (fun () ->
                    failwithf "RecordExpr: cannot resolve record type for fields %A" (Set.toList fieldNames))
        let fieldMap = Map.find typeName env.RecordEnv
        let n = Map.count fieldMap
        let fieldResults = fields |> List.map (fun (_, e) -> elaborateExpr env e)
        let allFieldOps  = fieldResults |> List.collect snd
        let fieldVals    = fieldResults |> List.map fst
        let bytesVal  = { Name = freshName env; Type = I64 }
        let recPtrVal = { Name = freshName env; Type = Ptr }
        let allocOps  = [
            ArithConstantOp(bytesVal, int64 (n * 8))
            LlvmCallOp(recPtrVal, "@GC_malloc", [bytesVal])
        ]
        let storeOps =
            fields |> List.collect (fun (fieldName, _) ->
                let slotIdx = Map.find fieldName fieldMap
                let fieldVal = List.item (fields |> List.findIndex (fun (fn, _) -> fn = fieldName)) fieldVals
                let slotPtr = { Name = freshName env; Type = Ptr }
                [ LlvmGEPLinearOp(slotPtr, recPtrVal, slotIdx)
                  LlvmStoreOp(fieldVal, slotPtr) ]
            )
        (recPtrVal, allFieldOps @ allocOps @ storeOps)

    // Phase 18: FieldAccess — GEP into record block at declaration-order slot, load value
    | FieldAccess(recExpr, fieldName, _) ->
        let (recVal, recOps) = elaborateExpr env recExpr
        let slotIdx =
            env.RecordEnv
            |> Map.toSeq
            |> Seq.tryPick (fun (_, fmap) -> Map.tryFind fieldName fmap)
            |> Option.defaultWith (fun () ->
                failwithf "FieldAccess: unknown field '%s'" fieldName)
        let slotPtr  = { Name = freshName env; Type = Ptr }
        let fieldVal = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmGEPLinearOp(slotPtr, recVal, slotIdx)
            LlvmLoadOp(fieldVal, slotPtr)
        ]
        (fieldVal, recOps @ ops)

    // Phase 18: RecordUpdate — allocate new block, copy non-overridden fields, write overridden fields
    | RecordUpdate(sourceExpr, overrides, _) ->
        let (srcVal, srcOps) = elaborateExpr env sourceExpr
        let overrideNames = overrides |> List.map fst |> Set.ofList
        let (typeName, fieldMap) =
            env.RecordEnv
            |> Map.tryFindKey (fun _ fmap ->
                overrideNames |> Set.forall (fun fn -> Map.containsKey fn fmap))
            |> Option.map (fun tn -> (tn, Map.find tn env.RecordEnv))
            |> Option.defaultWith (fun () ->
                failwithf "RecordUpdate: cannot resolve record type for fields %A" (Set.toList overrideNames))
        let n = Map.count fieldMap
        let overrideResults = overrides |> List.map (fun (fn, e) -> (fn, elaborateExpr env e))
        let overrideOps     = overrideResults |> List.collect (fun (_, (_, ops)) -> ops)
        let overrideVals    = overrideResults |> List.map (fun (fn, (v, _)) -> (fn, v)) |> Map.ofList
        let bytesVal  = { Name = freshName env; Type = I64 }
        let newPtrVal = { Name = freshName env; Type = Ptr }
        let allocOps  = [
            ArithConstantOp(bytesVal, int64 (n * 8))
            LlvmCallOp(newPtrVal, "@GC_malloc", [bytesVal])
        ]
        let copyOps =
            fieldMap |> Map.toList |> List.collect (fun (fieldName, slotIdx) ->
                let dstSlotPtr = { Name = freshName env; Type = Ptr }
                match Map.tryFind fieldName overrideVals with
                | Some newVal ->
                    [ LlvmGEPLinearOp(dstSlotPtr, newPtrVal, slotIdx)
                      LlvmStoreOp(newVal, dstSlotPtr) ]
                | None ->
                    let srcSlotPtr = { Name = freshName env; Type = Ptr }
                    let srcFieldVal = { Name = freshName env; Type = I64 }
                    [ LlvmGEPLinearOp(srcSlotPtr, srcVal, slotIdx)
                      LlvmLoadOp(srcFieldVal, srcSlotPtr)
                      LlvmGEPLinearOp(dstSlotPtr, newPtrVal, slotIdx)
                      LlvmStoreOp(srcFieldVal, dstSlotPtr) ]
            )
        (newPtrVal, srcOps @ overrideOps @ allocOps @ copyOps)

    // Phase 18: SetField — store in-place at field slot, return unit (i64=0)
    | SetField(recExpr, fieldName, valueExpr, _) ->
        let (recVal, recOps)    = elaborateExpr env recExpr
        let (newVal, newValOps) = elaborateExpr env valueExpr
        let slotIdx =
            env.RecordEnv
            |> Map.toSeq
            |> Seq.tryPick (fun (_, fmap) -> Map.tryFind fieldName fmap)
            |> Option.defaultWith (fun () ->
                failwithf "SetField: unknown field '%s'" fieldName)
        let slotPtr = { Name = freshName env; Type = Ptr }
        let unitVal = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmGEPLinearOp(slotPtr, recVal, slotIdx)
            LlvmStoreOp(newVal, slotPtr)
            ArithConstantOp(unitVal, 0L)
        ]
        (unitVal, recOps @ newValOps @ ops)

    // Phase 19: Raise — construct exception value, call @lang_throw, terminate block
    | Raise(exnExpr, _) ->
        let (exnVal, exnOps) = elaborateExpr env exnExpr
        // exnVal is a Ptr to an ADT DataValue block (e.g., {tag=0, payload=LangString*} for Failure "msg")
        // Call @lang_throw(exnVal) — noreturn void call
        // Emit llvm.unreachable after the call to terminate the block
        // deadVal must be defined by ArithConstantOp to satisfy MLIR SSA validity,
        // even though it is never used after llvm.unreachable
        let deadVal = { Name = freshName env; Type = I64 }
        (deadVal, exnOps @ [ ArithConstantOp(deadVal, 0L); LlvmCallVoidOp("@lang_throw", [exnVal]); LlvmUnreachableOp ])

    // Phase 19: TryWith — setjmp/longjmp exception handling
    // Control flow:
    //   [entry] alloc frame, call lang_try_enter (setjmp), branch on result
    //   ^try_body — elaborate body, pop handler, branch to merge
    //   ^exn_caught — pop handler (C-16), get exception, dispatch via MatchCompiler
    //   ^exn_fail — re-raise unmatched exception via @lang_throw
    //   ^merge(%result) — join point
    | TryWith(bodyExpr, clauses, _) ->
        let mergeLabel     = freshLabel env "try_merge"
        let tryBodyLabel   = freshLabel env "try_body"
        let exnCaughtLabel = freshLabel env "exn_caught"
        let exnFailLabel   = freshLabel env "exn_fail"

        // --- Entry ops: allocate frame, push handler, call _setjmp directly, branch ---
        // IMPORTANT: _setjmp must be called in the SAME function body (not a wrapper),
        // so the saved jmp_buf refers to THIS function's stack frame.
        // lang_try_push just links the frame into the handler stack.
        // _setjmp returns i32 (int); we zero-extend to i64 for comparison.
        let frameSizeVal = { Name = freshName env; Type = I64 }
        let framePtr     = { Name = freshName env; Type = Ptr }
        let sjResult32   = { Name = freshName env; Type = I32 }
        let sjResult64   = { Name = freshName env; Type = I64 }
        let zero64       = { Name = freshName env; Type = I64 }
        let isNormal     = { Name = freshName env; Type = I1 }
        let entryOps = [
            ArithConstantOp(frameSizeVal, 272L)
            LlvmCallOp(framePtr, "@GC_malloc", [frameSizeVal])
            LlvmCallVoidOp("@lang_try_push", [framePtr])
            LlvmCallOp(sjResult32, "@_setjmp", [framePtr])
            ArithExtuIOp(sjResult64, sjResult32)
            ArithConstantOp(zero64, 0L)
            ArithCmpIOp(isNormal, "eq", sjResult64, zero64)
            CfCondBrOp(isNormal, tryBodyLabel, [], exnCaughtLabel, [])
        ]

        // --- ^try_body block: elaborate body, pop handler, branch to merge ---
        let bodyBlocksBeforeCount = env.Blocks.Value.Length
        let (bodyVal, bodyOps) = elaborateExpr env bodyExpr
        let resultType = ref bodyVal.Type
        // Determine how the body terminates:
        //   - LlvmUnreachableOp: body raises (noreturn), try_body needs no exit or merge branch
        //   - CfBrOp / CfCondBrOp: body itself has multi-block control flow (nested Match/TryWith)
        //     In this case the lang_try_exit + CfBrOp(mergeLabel) must go into the body's
        //     LAST side block (its own merge block), not after bodyOps (which terminates the block).
        //   - Otherwise: append exit + branch normally
        let tryBodyBlock =
            match List.tryLast bodyOps with
            | Some LlvmUnreachableOp ->
                // Body raises unconditionally — no exit or merge branch needed
                { Label = Some tryBodyLabel; Args = []; Body = bodyOps }
            | Some (CfBrOp _) | Some (CfCondBrOp _) ->
                // Body itself terminates with a branch (e.g. nested Match/TryWith).
                // The branch to outer merge must be appended to the last side block
                // that was added by the inner expression (its merge block, currently empty body).
                // NOTE: do NOT append lang_try_exit here — the inner expression's normal-path
                // block already contains lang_try_exit before branching to its own merge.
                let innerBlocks = env.Blocks.Value
                if innerBlocks.Length > bodyBlocksBeforeCount then
                    let lastBlock = List.last innerBlocks
                    // Check if the last block (inner merge) has any predecessors.
                    // If all inner handler arms raised (noreturn), the inner merge is dead.
                    // In that case, emit llvm.unreachable instead of cf.br to avoid
                    // MLIR type errors from undefined block arguments in dead code.
                    let innerMergeLabel = lastBlock.Label
                    let hasPredecessors =
                        let allBlocks = innerBlocks |> List.take (innerBlocks.Length - 1)
                        allBlocks |> List.exists (fun b ->
                            b.Body |> List.exists (fun op ->
                                match op with
                                | CfBrOp(lbl, _) -> Some lbl = innerMergeLabel
                                | CfCondBrOp(_, tLbl, _, fLbl, _) ->
                                    Some tLbl = innerMergeLabel || Some fLbl = innerMergeLabel
                                | _ -> false))
                    let patchedOps =
                        if hasPredecessors then
                            lastBlock.Body @ [CfBrOp(mergeLabel, [bodyVal])]
                        else
                            lastBlock.Body @ [LlvmUnreachableOp]
                    let patchedLast = { lastBlock with Body = patchedOps }
                    env.Blocks.Value <- (List.take (innerBlocks.Length - 1) innerBlocks) @ [patchedLast]
                // The try_body block just holds the inner entry ops (which branch to inner blocks)
                { Label = Some tryBodyLabel; Args = []; Body = bodyOps }
            | _ ->
                // Normal case: body is a simple expression, append exit + merge branch
                { Label = Some tryBodyLabel; Args = [];
                  Body = bodyOps @ [
                    LlvmCallVoidOp("@lang_try_exit", [])
                    CfBrOp(mergeLabel, [bodyVal])
                  ] }
        env.Blocks.Value <- env.Blocks.Value @ [tryBodyBlock]

        // --- ^exn_caught block: pop handler (C-16), get exception, dispatch ---
        // Expand OrPat in handler clauses
        let clauses =
            clauses |> List.collect (fun (pat, guard, body) ->
                match pat with
                | OrPat(alts, _) -> alts |> List.map (fun altPat -> (altPat, guard, body))
                | _ -> [(pat, guard, body)]
            )

        let exnPtrVal = { Name = freshName env; Type = Ptr }
        let exnPreambleOps = [
            LlvmCallVoidOp("@lang_try_exit", [])   // C-16: pop handler before dispatch
            LlvmCallOp(exnPtrVal, "@lang_current_exception", [])
        ]

        // Build arms for MatchCompiler: (Pattern * hasGuard * bodyIndex) list
        let arms = clauses |> List.mapi (fun i (pat, guardOpt, _body) -> (pat, guardOpt.IsSome, i))
        let rootAcc = MatchCompiler.Root exnPtrVal.Name
        let tree = MatchCompiler.compile rootAcc arms

        // Accessor cache — initialize root to exnPtrVal
        let accessorCache2 = System.Collections.Generic.Dictionary<MatchCompiler.Accessor, MlirValue>()
        accessorCache2.[rootAcc] <- exnPtrVal

        // Resolve an accessor to an MlirValue (duplicated from Match case; root = exnPtrVal)
        let rec resolveAccessor2 (acc: MatchCompiler.Accessor) : MlirValue * MlirOp list =
            match accessorCache2.TryGetValue(acc) with
            | true, v -> (v, [])
            | false, _ ->
                match acc with
                | MatchCompiler.Root _ -> failwith "resolveAccessor2: Root not in cache"
                | MatchCompiler.Field (parent, idx) ->
                    let (rawParentVal, parentOps) = resolveAccessor2 parent
                    let (parentVal, retypeOps) =
                        if rawParentVal.Type = Ptr then (rawParentVal, [])
                        else resolveAccessorTyped2 parent Ptr
                    let slotVal  = { Name = freshName env; Type = Ptr }
                    let gepOp    = LlvmGEPLinearOp(slotVal, parentVal, idx)
                    let fieldVal = { Name = freshName env; Type = I64 }
                    let loadOp   = LlvmLoadOp(fieldVal, slotVal)
                    accessorCache2.[acc] <- fieldVal
                    (fieldVal, parentOps @ retypeOps @ [gepOp; loadOp])

        and resolveAccessorTyped2 (acc: MatchCompiler.Accessor) (ty: MlirType) : MlirValue * MlirOp list =
            match accessorCache2.TryGetValue(acc) with
            | true, v when v.Type = ty -> (v, [])
            | true, v ->
                match acc with
                | MatchCompiler.Root _ ->
                    if v.Type = I64 && ty = Ptr then
                        let ptrVal = { Name = freshName env; Type = Ptr }
                        accessorCache2.[acc] <- ptrVal
                        (ptrVal, [LlvmIntToPtrOp(ptrVal, v)])
                    else (v, [])
                | MatchCompiler.Field (parent, idx) ->
                    // GEP requires a Ptr base — ensure parent is resolved as Ptr.
                    let (parentVal, parentOps) = resolveAccessorTyped2 parent Ptr
                    let slotVal  = { Name = freshName env; Type = Ptr }
                    let fieldVal = { Name = freshName env; Type = ty }
                    let gepOp    = LlvmGEPLinearOp(slotVal, parentVal, idx)
                    let loadOp   = LlvmLoadOp(fieldVal, slotVal)
                    accessorCache2.[acc] <- fieldVal
                    (fieldVal, parentOps @ [gepOp; loadOp])
            | false, _ ->
                match acc with
                | MatchCompiler.Root _ -> failwith "resolveAccessorTyped2: Root not in cache"
                | MatchCompiler.Field (parent, idx) ->
                    // GEP requires a Ptr base — ensure parent is resolved as Ptr.
                    let (parentVal, parentOps) = resolveAccessorTyped2 parent Ptr
                    let slotVal  = { Name = freshName env; Type = Ptr }
                    let fieldVal = { Name = freshName env; Type = ty }
                    let gepOp    = LlvmGEPLinearOp(slotVal, parentVal, idx)
                    let loadOp   = LlvmLoadOp(fieldVal, slotVal)
                    accessorCache2.[acc] <- fieldVal
                    (fieldVal, parentOps @ [gepOp; loadOp])

        let scrutineeTypeForTag2 (tag: MatchCompiler.CtorTag) : MlirType =
            match tag with
            | MatchCompiler.IntLit _    -> I64
            | MatchCompiler.BoolLit _   -> I1
            | MatchCompiler.StringLit _ -> Ptr
            | MatchCompiler.ConsCtor    -> Ptr
            | MatchCompiler.NilCtor     -> Ptr
            | MatchCompiler.TupleCtor _ -> Ptr
            | MatchCompiler.AdtCtor _   -> Ptr
            | MatchCompiler.RecordCtor _ -> Ptr

        let emitCtorTest2 (scrutVal: MlirValue) (tag: MatchCompiler.CtorTag) : MlirValue * MlirOp list =
            match tag with
            | MatchCompiler.IntLit n ->
                let kVal = { Name = freshName env; Type = I64 }
                let cond = { Name = freshName env; Type = I1 }
                let ops = [ ArithConstantOp(kVal, int64 n); ArithCmpIOp(cond, "eq", scrutVal, kVal) ]
                (cond, ops)
            | MatchCompiler.BoolLit b ->
                let kVal = { Name = freshName env; Type = I1 }
                let cond = { Name = freshName env; Type = I1 }
                let n = if b then 1L else 0L
                let ops = [ ArithConstantOp(kVal, n); ArithCmpIOp(cond, "eq", scrutVal, kVal) ]
                (cond, ops)
            | MatchCompiler.StringLit s ->
                let (patStrVal, patStrOps) = elaborateStringLiteral env s
                let patDataPtrVal   = { Name = freshName env; Type = Ptr }
                let patDataVal      = { Name = freshName env; Type = Ptr }
                let scrutDataPtrVal = { Name = freshName env; Type = Ptr }
                let scrutDataVal    = { Name = freshName env; Type = Ptr }
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
                (cond, ops)
            | MatchCompiler.NilCtor ->
                let nullVal = { Name = freshName env; Type = Ptr }
                let cond    = { Name = freshName env; Type = I1 }
                let ops = [ LlvmNullOp(nullVal); LlvmIcmpOp(cond, "eq", scrutVal, nullVal) ]
                (cond, ops)
            | MatchCompiler.ConsCtor ->
                let nullVal = { Name = freshName env; Type = Ptr }
                let cond    = { Name = freshName env; Type = I1 }
                let ops = [ LlvmNullOp(nullVal); LlvmIcmpOp(cond, "ne", scrutVal, nullVal) ]
                (cond, ops)
            | MatchCompiler.TupleCtor _ ->
                let cond = { Name = freshName env; Type = I1 }
                let ops = [ ArithConstantOp(cond, 1L) ]
                (cond, ops)
            | MatchCompiler.AdtCtor(name, _, _) ->
                let info     = Map.find name env.TypeEnv
                let tagSlot  = { Name = freshName env; Type = Ptr }
                let tagLoad  = { Name = freshName env; Type = I64 }
                let tagConst = { Name = freshName env; Type = I64 }
                let cond     = { Name = freshName env; Type = I1 }
                let ops = [
                    LlvmGEPLinearOp(tagSlot, scrutVal, 0)
                    LlvmLoadOp(tagLoad, tagSlot)
                    ArithConstantOp(tagConst, int64 info.Tag)
                    ArithCmpIOp(cond, "eq", tagLoad, tagConst)
                ]
                (cond, ops)
            | MatchCompiler.RecordCtor _ ->
                let cond = { Name = freshName env; Type = I1 }
                let ops  = [ ArithConstantOp(cond, 1L) ]
                (cond, ops)

        let ensureConsFieldTypes2 (_scrutAcc: MatchCompiler.Accessor) (argAccs: MatchCompiler.Accessor list) : MlirOp list =
            let mutable ops = []
            if argAccs.Length >= 1 then
                let (_, headOps) = resolveAccessorTyped2 argAccs.[0] I64
                ops <- ops @ headOps
            if argAccs.Length >= 2 then
                let (_, tailOps) = resolveAccessorTyped2 argAccs.[1] Ptr
                ops <- ops @ tailOps
            ops

        // Exception payloads are always heap objects (Ptr): strings, tuples, ADTs.
        // Load as Ptr so handler bindings are correctly typed for GEP/load usage.
        let ensureAdtFieldTypes2 (_scrutAcc: MatchCompiler.Accessor) (argAccs: MatchCompiler.Accessor list) : MlirOp list =
            let mutable ops = []
            if argAccs.Length >= 1 then
                let (_, payOps) = resolveAccessorTyped2 argAccs.[0] Ptr
                ops <- ops @ payOps
            ops

        let ensureRecordFieldTypes2 (fields: string list) (scrutAcc: MatchCompiler.Accessor) (argAccs: MatchCompiler.Accessor list) : MlirOp list =
            let fieldSet = Set.ofList fields
            let fieldMap =
                env.RecordEnv
                |> Map.toSeq
                |> Seq.tryPick (fun (_, fm) ->
                    let fmFields = fm |> Map.toSeq |> Seq.map fst |> Set.ofSeq
                    if fieldSet |> Set.forall (fun f -> Set.contains f fmFields) then Some fm
                    else None)
                |> Option.defaultWith (fun () ->
                    failwithf "ensureRecordFieldTypes2: cannot resolve record type for fields %A" fields)
            let mutable ops = []
            fields |> List.iteri (fun i fieldName ->
                if i < argAccs.Length then
                    let declSlotIdx = Map.find fieldName fieldMap
                    let (parentVal, parentOps) = resolveAccessorTyped2 scrutAcc Ptr
                    let slotPtr  = { Name = freshName env; Type = Ptr }
                    let fieldVal = { Name = freshName env; Type = I64 }
                    let gepOp  = LlvmGEPLinearOp(slotPtr, parentVal, declSlotIdx)
                    let loadOp = LlvmLoadOp(fieldVal, slotPtr)
                    accessorCache2.[argAccs.[i]] <- fieldVal
                    ops <- ops @ parentOps @ [gepOp; loadOp]
            )
            ops

        // Recursively emit blocks for the TryWith decision tree.
        // Fail case: branch to exnFailLabel (re-raise via @lang_throw).
        let rec emitDecisionTree2 (tree: MatchCompiler.DecisionTree) : MlirOp list =
            match tree with
            | MatchCompiler.Fail ->
                [ CfBrOp(exnFailLabel, []) ]
            | MatchCompiler.Leaf (bindings, bodyIdx) ->
                let mutable bindOps = []
                let mutable bindEnv = env
                for (varName, acc) in bindings do
                    let (v, ops) = resolveAccessor2 acc
                    bindOps <- bindOps @ ops
                    bindEnv <- { bindEnv with Vars = Map.add varName v bindEnv.Vars }
                let (_pat, _guard, bodyExpr) = clauses.[bodyIdx]
                let (bodyVal, bodyOps) = elaborateExpr bindEnv bodyExpr
                resultType.Value <- bodyVal.Type
                // Skip merge branch if body already terminated (e.g. raise inside handler arm)
                let terminatedOps =
                    match List.tryLast bodyOps with
                    | Some LlvmUnreachableOp -> bodyOps
                    | Some (CfBrOp _) | Some (CfCondBrOp _) -> bodyOps
                    | _ -> bodyOps @ [CfBrOp(mergeLabel, [bodyVal])]
                let bodyLabel = freshLabel env (sprintf "try_arm%d" bodyIdx)
                env.Blocks.Value <- env.Blocks.Value @
                    [ { Label = Some bodyLabel
                        Args  = []
                        Body  = terminatedOps } ]
                bindOps @ [ CfBrOp(bodyLabel, []) ]
            | MatchCompiler.Switch (scrutAcc, tag, argAccs, ifMatch, ifNoMatch) ->
                let expectedType = scrutineeTypeForTag2 tag
                let (sVal, resolveOps) = resolveAccessorTyped2 scrutAcc expectedType
                let (cond, testOps) = emitCtorTest2 sVal tag
                let preloadOps =
                    match tag with
                    | MatchCompiler.ConsCtor -> ensureConsFieldTypes2 scrutAcc argAccs
                    | MatchCompiler.AdtCtor(_, _, arity) when arity > 0 -> ensureAdtFieldTypes2 scrutAcc argAccs
                    | MatchCompiler.RecordCtor fields -> ensureRecordFieldTypes2 fields scrutAcc argAccs
                    | _ -> []
                let matchLabel   = freshLabel env "try_yes"
                let noMatchLabel = freshLabel env "try_no"
                let matchOps   = emitDecisionTree2 ifMatch
                let noMatchOps = emitDecisionTree2 ifNoMatch
                env.Blocks.Value <- env.Blocks.Value @
                    [ { Label = Some matchLabel; Args = []; Body = preloadOps @ matchOps }
                      { Label = Some noMatchLabel; Args = []; Body = noMatchOps } ]
                resolveOps @ testOps @ [ CfCondBrOp(cond, matchLabel, [], noMatchLabel, []) ]
            | MatchCompiler.Guard (bindings, bodyIdx, ifFalse) ->
                let mutable bindOps = []
                let mutable bindEnv = env
                for (varName, acc) in bindings do
                    let (v, ops) = resolveAccessor2 acc
                    bindOps <- bindOps @ ops
                    bindEnv <- { bindEnv with Vars = Map.add varName v bindEnv.Vars }
                let (_pat, guardOpt, bodyExpr) = clauses.[bodyIdx]
                let guard = guardOpt.Value
                let (guardVal, guardOps) = elaborateExpr bindEnv guard
                let guardFailLabel = freshLabel env (sprintf "try_guard_fail%d" bodyIdx)
                let guardFailOps = emitDecisionTree2 ifFalse
                env.Blocks.Value <- env.Blocks.Value @
                    [ { Label = Some guardFailLabel; Args = []; Body = guardFailOps } ]
                let (bodyVal, bodyOps) = elaborateExpr bindEnv bodyExpr
                resultType.Value <- bodyVal.Type
                // Skip merge branch if body already terminated (e.g. raise inside handler guard)
                let terminatedOps =
                    match List.tryLast bodyOps with
                    | Some LlvmUnreachableOp -> bodyOps
                    | Some (CfBrOp _) | Some (CfCondBrOp _) -> bodyOps
                    | _ -> bodyOps @ [CfBrOp(mergeLabel, [bodyVal])]
                let bodyLabel = freshLabel env (sprintf "try_arm%d" bodyIdx)
                env.Blocks.Value <- env.Blocks.Value @
                    [ { Label = Some bodyLabel; Args = [];
                        Body = terminatedOps } ]
                bindOps @ guardOps @ [CfCondBrOp(guardVal, bodyLabel, [], guardFailLabel, [])]

        let chainEntryOps = emitDecisionTree2 tree

        // ^exn_caught block
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some exnCaughtLabel
                Args  = []
                Body  = exnPreambleOps @ chainEntryOps } ]

        // ^exn_fail block — re-raise to outer handler (or abort if none)
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some exnFailLabel
                Args  = []
                Body  = [ LlvmCallVoidOp("@lang_throw", [exnPtrVal]); LlvmUnreachableOp ] } ]

        // ^merge block — MUST be last so appendReturnIfNeeded targets it
        let mergeArg = { Name = freshName env; Type = resultType.Value }
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some mergeLabel; Args = [mergeArg]; Body = [] } ]

        (mergeArg, entryOps)

    | _ ->
        failwithf "Elaboration: unsupported expression %A" expr

/// Append ReturnOp to a block body only if the block does not already end with
/// a terminator (llvm.unreachable).  This allows Raise at the end of a function
/// without generating a dead `return` after `llvm.unreachable`.
let private appendReturnIfNeeded (ops: MlirOp list) (retVal: MlirValue) : MlirOp list =
    match List.tryLast ops with
    | Some LlvmUnreachableOp -> ops
    | _ -> ops @ [ReturnOp [retVal]]

let elaborateModule (expr: Expr) : MlirModule =
    let env = emptyEnv ()
    let (resultVal, entryOps) = elaborateExpr env expr
    let sideBlocks = env.Blocks.Value
    let allBlocks =
        if sideBlocks.IsEmpty then
            [ { Label = None; Args = []; Body = appendReturnIfNeeded entryOps resultVal } ]
        else
            let entryBlock = { Label = None; Args = []; Body = entryOps }
            let lastBlock = List.last sideBlocks
            let lastBlockWithReturn = { lastBlock with Body = appendReturnIfNeeded lastBlock.Body resultVal }
            let sideBlocksPatched = (List.take (sideBlocks.Length - 1) sideBlocks) @ [lastBlockWithReturn]
            entryBlock :: sideBlocksPatched
    let gcInitOp = LlvmCallVoidOp("@GC_init", [])
    let allBlocksWithGC =
        match allBlocks with
        | [] -> allBlocks
        | entryBlock :: rest ->
            { entryBlock with Body = gcInitOp :: entryBlock.Body } :: rest
    let mainFunc : FuncOp =
        { Name        = "@main"
          InputTypes  = []
          ReturnType  = Some resultVal.Type
          Body        = { Blocks = allBlocksWithGC }
          IsLlvmFunc  = false }
    let globals = env.Globals.Value |> List.map (fun (name, value) -> StringConstant(name, value))
    let externalFuncs = [
        { ExtName = "@GC_init";              ExtParams = [];         ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@GC_malloc";            ExtParams = [I64];      ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@printf";               ExtParams = [Ptr];      ExtReturn = Some I32; IsVarArg = true;  Attrs = [] }
        { ExtName = "@strcmp";               ExtParams = [Ptr; Ptr]; ExtReturn = Some I32; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_concat";   ExtParams = [Ptr; Ptr]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_to_string_int";   ExtParams = [I64];      ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_to_string_bool";  ExtParams = [I64];      ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_match_failure";   ExtParams = [];         ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_failwith";        ExtParams = [Ptr];               ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_sub";      ExtParams = [Ptr; I64; I64];     ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_contains"; ExtParams = [Ptr; Ptr];          ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_to_int";   ExtParams = [Ptr];               ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_range";           ExtParams = [I64; I64; I64];     ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_try_push";           ExtParams = [Ptr];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_try_exit";           ExtParams = [];     ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_throw";              ExtParams = [Ptr];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_current_exception";  ExtParams = [];     ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@_setjmp";                 ExtParams = [Ptr];  ExtReturn = Some I32; IsVarArg = false; Attrs = ["returns_twice"] }
        { ExtName = "@lang_array_create";       ExtParams = [I64; I64]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_bounds_check"; ExtParams = [Ptr; I64]; ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_of_list";      ExtParams = [Ptr];      ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_to_list";      ExtParams = [Ptr];      ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
    ]
    { Globals = globals; ExternalFuncs = externalFuncs; Funcs = env.Funcs.Value @ [mainFunc] }

// Phase 16: Declaration pre-pass — scans Decl list and populates TypeEnv, RecordEnv, ExnTags.
// No MLIR IR is emitted in this pass; only F# Map structures are built.
let private prePassDecls (decls: Ast.Decl list)
    : Map<string, TypeInfo> * Map<string, Map<string, int>> * Map<string, int> =
    let mutable typeEnv  = Map.empty<string, TypeInfo>
    let mutable recordEnv = Map.empty<string, Map<string, int>>
    let mutable exnTags  = Map.empty<string, int>
    let exnCounter = ref 0
    for decl in decls do
        match decl with
        | Ast.Decl.TypeDecl (Ast.TypeDecl(_, _, ctors, _)) ->
            ctors |> List.iteri (fun idx ctor ->
                match ctor with
                | Ast.ConstructorDecl(name, dataType, _) ->
                    let arity = match dataType with None -> 0 | Some _ -> 1
                    typeEnv <- Map.add name { Tag = idx; Arity = arity } typeEnv
                | Ast.GadtConstructorDecl(name, argTypes, _, _) ->
                    let arity = if argTypes.IsEmpty then 0 else 1
                    typeEnv <- Map.add name { Tag = idx; Arity = arity } typeEnv
            )
        | Ast.Decl.RecordTypeDecl (Ast.RecordDecl(typeName, _, fields, _)) ->
            let fieldMap =
                fields
                |> List.mapi (fun idx (Ast.RecordFieldDecl(name, _, _, _)) -> (name, idx))
                |> Map.ofList
            recordEnv <- Map.add typeName fieldMap recordEnv
        | Ast.Decl.ExceptionDecl(name, dataTypeOpt, _) ->
            let tag = exnCounter.Value
            exnCounter.Value <- tag + 1
            exnTags <- Map.add name tag exnTags
            let arity = match dataTypeOpt with Some _ -> 1 | None -> 0
            typeEnv <- Map.add name { Tag = tag; Arity = arity } typeEnv
        | _ -> ()
    (typeEnv, recordEnv, exnTags)

// Phase 16: Extract the main expression from a Decl list.
// LetDecl("_", expr) → use expr as the body; it already contains nested Let bindings.
// LetDecl(name, body) → wrap in Let(name, body, continuation).
// Non-expression decls (TypeDecl, RecordTypeDecl, ExceptionDecl) are skipped.
// LetRecDecl bindings are wrapped in LetRec expressions.
let private extractMainExpr (decls: Ast.Decl list) : Expr =
    let s = unknownSpan
    let exprDecls =
        decls |> List.filter (fun d ->
            match d with
            | Ast.Decl.LetDecl _ | Ast.Decl.LetRecDecl _ | Ast.Decl.LetMutDecl _ -> true
            | _ -> false)
    match exprDecls with
    | [] -> Number(0, s)  // empty module → produce 0 as unit sentinel
    | _ ->
        // Fold right: each decl wraps the continuation
        let rec build (ds: Ast.Decl list) : Expr =
            match ds with
            | [] -> Number(0, s)
            | [Ast.Decl.LetDecl("_", body, _)] -> body
            | [Ast.Decl.LetDecl(name, body, _)] -> Let(name, body, Var(name, s), s)
            | [Ast.Decl.LetRecDecl(bindings, _)] ->
                // Single let rec with no continuation: wrap body in (fun _ -> body) 0 sentinel
                // Just return 0 as the program's exit value; Phase 17 will need real support
                match bindings with
                | (name, param, body, _) :: _ -> LetRec(name, param, body, Number(0, s), s)
                | [] -> Number(0, s)
            | Ast.Decl.LetDecl("_", body, _) :: rest ->
                // let _ = body → evaluate body for side effects, then rest
                // Represent as Let("_", body, continuation)
                Let("_", body, build rest, s)
            | Ast.Decl.LetDecl(name, body, _) :: rest ->
                Let(name, body, build rest, s)
            | Ast.Decl.LetRecDecl(bindings, _) :: rest ->
                match bindings with
                | (name, param, body, _) :: _ -> LetRec(name, param, body, build rest, s)
                | [] -> build rest
            | Ast.Decl.LetMutDecl(name, body, _) :: rest ->
                LetMut(name, body, build rest, s)
            | _ :: rest -> build rest
        build exprDecls

// Phase 16: elaborateProgram — new entry point accepting Ast.Module.
// Runs prePassDecls to populate TypeEnv/RecordEnv/ExnTags, then elaborates the program body.
let elaborateProgram (ast: Ast.Module) : MlirModule =
    let decls =
        match ast with
        | Ast.Module(decls, _) | Ast.NamedModule(_, decls, _) | Ast.NamespacedModule(_, decls, _) -> decls
        | Ast.EmptyModule _ -> []
    let (typeEnv, recordEnv, exnTags) = prePassDecls decls
    let mainExpr = extractMainExpr decls
    let env = { emptyEnv () with TypeEnv = typeEnv; RecordEnv = recordEnv; ExnTags = exnTags }
    let (resultVal, entryOps) = elaborateExpr env mainExpr
    let sideBlocks = env.Blocks.Value
    let allBlocks =
        if sideBlocks.IsEmpty then
            [ { Label = None; Args = []; Body = appendReturnIfNeeded entryOps resultVal } ]
        else
            let entryBlock = { Label = None; Args = []; Body = entryOps }
            let lastBlock = List.last sideBlocks
            let lastBlockWithReturn = { lastBlock with Body = appendReturnIfNeeded lastBlock.Body resultVal }
            let sideBlocksPatched = (List.take (sideBlocks.Length - 1) sideBlocks) @ [lastBlockWithReturn]
            entryBlock :: sideBlocksPatched
    let gcInitOp = LlvmCallVoidOp("@GC_init", [])
    let allBlocksWithGC =
        match allBlocks with
        | [] -> allBlocks
        | entryBlock :: rest ->
            { entryBlock with Body = gcInitOp :: entryBlock.Body } :: rest
    let mainFunc : FuncOp =
        { Name        = "@main"
          InputTypes  = []
          ReturnType  = Some resultVal.Type
          Body        = { Blocks = allBlocksWithGC }
          IsLlvmFunc  = false }
    let globals = env.Globals.Value |> List.map (fun (name, value) -> StringConstant(name, value))
    let externalFuncs = [
        { ExtName = "@GC_init";              ExtParams = [];         ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@GC_malloc";            ExtParams = [I64];      ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@printf";               ExtParams = [Ptr];      ExtReturn = Some I32; IsVarArg = true;  Attrs = [] }
        { ExtName = "@strcmp";               ExtParams = [Ptr; Ptr]; ExtReturn = Some I32; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_concat";   ExtParams = [Ptr; Ptr]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_to_string_int";   ExtParams = [I64];      ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_to_string_bool";  ExtParams = [I64];      ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_match_failure";   ExtParams = [];         ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_failwith";        ExtParams = [Ptr];               ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_sub";      ExtParams = [Ptr; I64; I64];     ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_contains"; ExtParams = [Ptr; Ptr];          ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_to_int";   ExtParams = [Ptr];               ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_range";           ExtParams = [I64; I64; I64];     ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_try_push";           ExtParams = [Ptr];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_try_exit";           ExtParams = [];     ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_throw";              ExtParams = [Ptr];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_current_exception";  ExtParams = [];     ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@_setjmp";                 ExtParams = [Ptr];  ExtReturn = Some I32; IsVarArg = false; Attrs = ["returns_twice"] }
        { ExtName = "@lang_array_create";       ExtParams = [I64; I64]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_bounds_check"; ExtParams = [Ptr; I64]; ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_of_list";      ExtParams = [Ptr];      ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_to_list";      ExtParams = [Ptr];      ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
    ]
    { Globals = globals; ExternalFuncs = externalFuncs; Funcs = env.Funcs.Value @ [mainFunc] }
