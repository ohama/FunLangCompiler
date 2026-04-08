/// Main elaboration pass: AST → MLIR IR translation.
module Elaboration

open Ast
open MlirIR
open MatchCompiler
open ElabHelpers

let rec elaborateExpr (env: ElabEnv) (expr: Expr) : MlirValue * MlirOp list =
    match expr with
    | Number (n, _) ->
        let v = { Name = freshName env; Type = I64 }
        (v, [ArithConstantOp(v, tagConst (int64 n))])
    | Char (c, _) ->
        let v = { Name = freshName env; Type = I64 }
        (v, [ArithConstantOp(v, tagConst (int64 (int c)))])
    | Add (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let raw = { Name = freshName env; Type = I64 }
        let one = { Name = freshName env; Type = I64 }
        let result = { Name = freshName env; Type = I64 }
        (result, lops @ rops @ [
            ArithAddIOp(raw, lv, rv)
            ArithConstantOp(one, 1L)
            ArithSubIOp(result, raw, one)
        ])
    | Subtract (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let raw = { Name = freshName env; Type = I64 }
        let one = { Name = freshName env; Type = I64 }
        let result = { Name = freshName env; Type = I64 }
        (result, lops @ rops @ [
            ArithSubIOp(raw, lv, rv)
            ArithConstantOp(one, 1L)
            ArithAddIOp(result, raw, one)
        ])
    | Multiply (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let (la, untagL) = emitUntag env lv
        let (rb, untagR) = emitUntag env rv
        let raw = { Name = freshName env; Type = I64 }
        let (result, retagOps) = emitRetag env raw
        (result, lops @ rops @ untagL @ untagR @ [ArithMulIOp(raw, la, rb)] @ retagOps)
    | Divide (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let (la, untagL) = emitUntag env lv
        let (rb, untagR) = emitUntag env rv
        let raw = { Name = freshName env; Type = I64 }
        let (result, retagOps) = emitRetag env raw
        (result, lops @ rops @ untagL @ untagR @ [ArithDivSIOp(raw, la, rb)] @ retagOps)
    | Modulo (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let (la, untagL) = emitUntag env lv
        let (rb, untagR) = emitUntag env rv
        let raw = { Name = freshName env; Type = I64 }
        let (result, retagOps) = emitRetag env raw
        (result, lops @ rops @ untagL @ untagR @ [ArithRemSIOp(raw, la, rb)] @ retagOps)
    | Negate (inner, _) ->
        let (iv, iops) = elaborateExpr env inner
        let two = { Name = freshName env; Type = I64 }
        let result = { Name = freshName env; Type = I64 }
        (result, iops @ [ArithConstantOp(two, 2L); ArithSubIOp(result, two, iv)])
    | Var (name, span) ->
        match Map.tryFind name env.Vars with
        | Some v ->
            if Set.contains name env.MutableVars then
                let loaded = { Name = freshName env; Type = I64 }
                (loaded, [LlvmLoadOp(loaded, v)])
            else
                (v, [])
        | None ->
            // Auto eta-expand: if name is a KnownFunc (direct-call), wrap it in a closure
            // so it can be passed as a first-class value (e.g., `List.map double xs`, `5 |> f`).
            match Map.tryFind name env.KnownFuncs with
            | Some sig_ when sig_.ClosureInfo.IsNone ->
                let n = env.Counter.Value
                env.Counter.Value <- n + 1
                let etaParam = sprintf "__eta_%d" n
                elaborateExpr env (Lambda(etaParam, App(Var(name, span), Var(etaParam, span), span), span))
            | _ -> failWithSpan span "Elaboration: unbound variable '%s'" name
    // Phase 21: Mutable variable allocation
    | LetMut (name, initExpr, bodyExpr, _) ->
        let (initVal, initOps) = elaborateExpr env initExpr
        // Coerce init value to I64 for uniform mutable cell storage (I1→tagged, Ptr→ptrtoint)
        let (storeVal, coerceOps) = coerceToI64 env initVal
        let sizeVal  = { Name = freshName env; Type = I64 }
        let cellPtr  = { Name = freshName env; Type = Ptr }
        let allocOps = [
            ArithConstantOp(sizeVal, 8L)
            LlvmCallOp(cellPtr, "@GC_malloc", [sizeVal])
            LlvmStoreOp(storeVal, cellPtr)
        ]
        let env' = { env with
                        Vars        = Map.add name cellPtr env.Vars
                        MutableVars = Set.add name env.MutableVars }
        let (bodyVal, bodyOps) = elaborateExpr env' bodyExpr
        (bodyVal, initOps @ coerceOps @ allocOps @ bodyOps)
    // Phase 21: Mutable variable assignment
    | Assign (name, valExpr, span) ->
        let (newVal, valOps) = elaborateExpr env valExpr
        // Coerce assigned value to I64 for uniform mutable cell storage
        let (storeVal, coerceOps) = coerceToI64 env newVal
        let cellPtr =
            match Map.tryFind name env.Vars with
            | Some v -> v
            | None -> failWithSpan span "Elaboration: unbound mutable variable '%s' in Assign" name
        let unitVal = { Name = freshName env; Type = I64 }
        (unitVal, valOps @ coerceOps @ [LlvmStoreOp(storeVal, cellPtr); ArithConstantOp(unitVal, 1L)])
    // Phase 5: special-case Let(name, Lambda(outerParam, Lambda(innerParam, innerBody)), inExpr)
    // This compiles to an llvm.func body + func.func closure-maker + KnownFuncs entry
    // Phase 43: StripAnnot sees through Annot/LambdaAnnot wrappers from type annotations
    | Let (name, StripAnnot (Lambda (outerParam, StripAnnot (Lambda (innerParam, innerBody, innerLamSpan)), _)), inExpr, letSpan) ->
        // Step 1: Compute free variables of the inner lambda body relative to innerParam only.
        // These are variables that need to come from the closure environment struct.
        // outerParam IS one such variable — it's passed to the closure-maker and stored at env[1+i].
        // Using only {innerParam} as bound means outerParam appears free when it's used in innerBody.
        let captures =
            freeVars (Set.singleton innerParam) innerBody
            |> Set.filter (fun name -> Map.containsKey name env.Vars || name = outerParam)
            |> Set.toList
            |> List.sort
        let numCaptures = List.length captures

        // Step 2: Generate fresh closure function name
        let closureFnIdx = env.ClosureCounter.Value
        env.ClosureCounter.Value <- closureFnIdx + 1
        let closureFnName = sprintf "@closure_fn_%d" closureFnIdx

        // Step 3: Compile inner lambda body (llvm.func)
        // Build the initial vars: %arg0 = env ptr, %arg1 = innerParam (always ptr for uniform ABI)
        // If innerParam needs I64 type, coerce ptr arg1 to i64 via ptrtoint.
        let arg0Val = { Name = "%arg0"; Type = Ptr }
        let arg1Val = { Name = "%arg1"; Type = Ptr }
        // If innerParam needs I64 type, coerce ptr arg1 to i64 via ptrtoint.
        // The coerced value gets name %t0 (first SSA name after captures).
        let innerParamNeedsI64 = not (isPtrParamTyped env.AnnotationMap innerLamSpan innerParam innerBody)
        let (innerParamVal, paramCoerceOps, captureStartIdx) =
            if innerParamNeedsI64 then
                let i64Val = { Name = "%t0"; Type = I64 }
                (i64Val, [LlvmPtrToIntOp(i64Val, arg1Val)], 1)
            else
                (arg1Val, [], 0)
        let innerEnv : ElabEnv =
            { Vars = Map.ofList [(innerParam, innerParamVal)]
              Counter = ref captureStartIdx; LabelCounter = ref 0; Blocks = ref []
              KnownFuncs = env.KnownFuncs
              Funcs = env.Funcs
              ClosureCounter = env.ClosureCounter
              Globals = env.Globals
              GlobalCounter = env.GlobalCounter
              TplGlobals = env.TplGlobals
              TypeEnv = env.TypeEnv; RecordEnv = env.RecordEnv; ExnTags = env.ExnTags
              // Remove param name from MutableVars so lambda param shadows outer mutable var
              MutableVars = Set.remove innerParam env.MutableVars; ArrayVars = Set.empty; CollectionVars = Map.empty
              BoolVars = Set.empty; StringVars = Set.empty; StringFields = env.StringFields; AnnotationMap = env.AnnotationMap }

        // For each capture at index i: GEP to slot i+1, then load
        let captureLoadOps, innerEnvWithCaptures =
            captures |> List.mapi (fun i capName ->
                let gepVal = { Name = sprintf "%%t%d" (captureStartIdx + i); Type = Ptr }
                let capType = if Set.contains capName env.MutableVars then Ptr else I64
                let capVal = { Name = sprintf "%%t%d" (captureStartIdx + i + numCaptures); Type = capType }
                (gepVal, capVal, capName, i)
            )
            |> List.fold (fun (opsAcc, envAcc: ElabEnv) (gepVal, capVal, capName, i) ->
                // Advance counter past GEP and load names we pre-allocated
                let gepOp = LlvmGEPLinearOp(gepVal, arg0Val, i + 1)
                let loadOp = LlvmLoadOp(capVal, gepVal)
                let envAcc' = { envAcc with Vars = Map.add capName capVal envAcc.Vars }
                (opsAcc @ [gepOp; loadOp], envAcc')
            ) ([], innerEnv)

        // Advance inner env counter past the pre-allocated coerce + GEP/load SSA names
        innerEnvWithCaptures.Counter.Value <- captureStartIdx + numCaptures * 2

        // Elaborate inner body
        let (bodyVal, bodyEntryOps) = elaborateExpr innerEnvWithCaptures innerBody
        // Phase 35: Normalize body return to I64 (I1→zext, Ptr→ptrtoint) for uniform closure ABI.
        let (finalBodyVal, coerceRetOps) = coerceToI64 innerEnvWithCaptures bodyVal
        let bodySideBlocks = innerEnvWithCaptures.Blocks.Value

        let allBodyBlocks =
            if bodySideBlocks.IsEmpty then
                [ { Label = None; Args = []; Body = paramCoerceOps @ captureLoadOps @ bodyEntryOps @ coerceRetOps @ [LlvmReturnOp [finalBodyVal]] } ]
            else
                let entryBlock = { Label = None; Args = []; Body = paramCoerceOps @ captureLoadOps @ bodyEntryOps }
                let lastBlock = List.last bodySideBlocks
                let lastBlockWithReturn = { lastBlock with Body = lastBlock.Body @ coerceRetOps @ [LlvmReturnOp [finalBodyVal]] }
                let sideBlocksPatched = (List.take (bodySideBlocks.Length - 1) bodySideBlocks) @ [lastBlockWithReturn]
                entryBlock :: sideBlocksPatched

        let innerFuncOp : FuncOp =
            { Name        = closureFnName
              InputTypes  = [Ptr; Ptr]
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

        // Phase 64: Maker only stores outerParam at its capture slot.
        // All other captures are stored by the CALLER (caller-side env population).
        let outerParamStoreOps =
            match List.tryFindIndex ((=) outerParam) captures with
            | Some idx ->
                let slotVal = { Name = nextMakerName(); Type = Ptr }
                let ptrVal = { Name = nextMakerName(); Type = Ptr }
                [LlvmGEPLinearOp(slotVal, makerArg1, idx + 1)
                 LlvmIntToPtrOp(ptrVal, makerArg0)
                 LlvmStoreOp(ptrVal, slotVal)]
            | None -> []

        let makerBodyOps = makerOps @ outerParamStoreOps @ [ReturnOp [makerArg1]]

        let makerFuncOp : FuncOp =
            { Name        = "@" + name
              InputTypes  = [I64; Ptr]
              ReturnType  = Some Ptr
              Body        = { Blocks = [ { Label = None; Args = []; Body = makerBodyOps } ] }
              IsLlvmFunc  = false }

        // Step 5: Add both FuncOps to env.Funcs
        // Phase 53: If maker function with same name already exists (e.g., from Prelude InstanceDecl
        // shadowing of eq/show), replace it to avoid MLIR "redefinition of symbol" errors.
        let makerMlirName = "@" + name
        let existingFuncs = env.Funcs.Value
        if existingFuncs |> List.exists (fun f -> f.Name = makerMlirName) then
            env.Funcs.Value <- (existingFuncs |> List.filter (fun f -> f.Name <> makerMlirName)) @ [innerFuncOp; makerFuncOp]
        else
            env.Funcs.Value <- existingFuncs @ [innerFuncOp; makerFuncOp]

        // Step 6: Add to KnownFuncs
        let closureInfo = { InnerLambdaFn = closureFnName; NumCaptures = numCaptures
                            InnerReturnIsBool = bodyReturnsBoolTyped env.AnnotationMap innerBody
                            CaptureNames = captures; OuterParamName = outerParam }
        let sig_ : FuncSignature =
            { MlirName    = "@" + name
              ParamTypes  = [I64]
              ReturnType  = Ptr
              ClosureInfo = Some closureInfo
              ReturnIsBool = false
              InnerReturnIsBool = bodyReturnsBoolTyped env.AnnotationMap innerBody }
        // Phase 35: Module-qualified naming — also register short name alias in KnownFuncs
        // so that references within the same module body resolve correctly.
        let twoLambdaShortAlias =
            let idx = name.IndexOf('_')
            if idx > 0 && System.Char.IsUpper(name.[0]) then Some (name.Substring(idx + 1))
            else None
        let env' =
            let kf = Map.add name sig_ env.KnownFuncs
            let kf' = match twoLambdaShortAlias with Some sn -> Map.add sn sig_ kf | None -> kf
            { env with KnownFuncs = kf' }

        // Step 7: Phase 65 partial env pattern — if there are non-outerParam captures,
        // pre-allocate a "template env" at the definition site: GC_malloc, store fn_ptr at slot 0,
        // store each non-outerParam capture at its slot.
        // The template ptr is stored in an LLVM global (@__tenv_<name>) so that LetRec body
        // func.funcs (which cannot reference outer SSA values) can load it at call time.
        let hasNonOuterCaptures = captures |> List.exists (fun c -> c <> outerParam)
        let (templateEnvOps, envWithTemplateVar) =
            if not hasNonOuterCaptures then
                ([], env')
            else
                let tBytesVal  = { Name = freshName env; Type = I64 }
                let tEnvPtr    = { Name = freshName env; Type = Ptr }
                let tFnPtrVal  = { Name = freshName env; Type = Ptr }
                let allocOps = [
                    ArithConstantOp(tBytesVal, int64 ((numCaptures + 1) * 8))
                    LlvmCallOp(tEnvPtr, "@GC_malloc", [tBytesVal])
                    LlvmAddressOfOp(tFnPtrVal, closureFnName)
                    LlvmStoreOp(tFnPtrVal, tEnvPtr)   // env[0] = fn_ptr
                ]
                // Store each non-outerParam capture at its slot (skip outerParam — filled per-call).
                // Look up capture values in the OUTER env (env.Vars), where they are in scope.
                let captureStoreOps =
                    captures |> List.mapi (fun i capName ->
                        if capName = outerParam then []
                        else
                            match Map.tryFind capName env.Vars with
                            | None -> []  // not in scope — shouldn't happen at top-level Let
                            | Some capVal ->
                                let slotVal = { Name = freshName env; Type = Ptr }
                                if capVal.Type = I64 then
                                    let ptrVal = { Name = freshName env; Type = Ptr }
                                    [ LlvmGEPLinearOp(slotVal, tEnvPtr, i + 1)
                                      LlvmIntToPtrOp(ptrVal, capVal)
                                      LlvmStoreOp(ptrVal, slotVal) ]
                                else
                                    [ LlvmGEPLinearOp(slotVal, tEnvPtr, i + 1)
                                      LlvmStoreOp(capVal, slotVal) ]
                    ) |> List.concat
                // Store template ptr into a global var @__tenv_<name> so LetRec body
                // func.funcs (separate LLVM functions) can reference it by name.
                let globalName = "@__tenv_" + name.Replace(".", "_")
                env.TplGlobals.Value <- env.TplGlobals.Value @ [globalName]
                let globalAddrVal = { Name = freshName env; Type = Ptr }
                let storeToGlobalOps = [
                    LlvmAddressOfOp(globalAddrVal, globalName)
                    LlvmStoreOp(tEnvPtr, globalAddrVal)
                ]
                (allocOps @ captureStoreOps @ storeToGlobalOps, env')

        // Elaborate inExpr (env' unchanged — template accessible via TplGlobals, not Vars)
        let (inVal, inOps) = elaborateExpr envWithTemplateVar inExpr
        (inVal, templateEnvOps @ inOps)

    // Phase 41: OpenDecl alias — Let(shortName, Var(qualifiedName), cont) where qualifiedName is in
    // KnownFuncs (two-lambda direct-call function) but not Vars. Add shortName as KnownFuncs alias.
    | Let (name, Var (qualName, _), bodyExpr, _) when
        not (Map.containsKey qualName env.Vars) && Map.containsKey qualName env.KnownFuncs ->
        let sig_ = Map.find qualName env.KnownFuncs
        let env' = { env with KnownFuncs = Map.add name sig_ env.KnownFuncs }
        elaborateExpr env' bodyExpr
    // Single-arg Let-Lambda: compile as named func.func and add to KnownFuncs (not Vars).
    // This prevents the function value being captured as a closure in nested two-arg functions,
    // which would cause MLIR "value defined outside region" errors.
    | Let (name, StripAnnot (Lambda (param, body, lamSpan)), inExpr, _)
        when (freeVars (Set.singleton param) body
              |> Set.filter (fun v -> Map.containsKey v env.Vars)
              |> Set.isEmpty) ->
        let paramType = if isPtrParamTyped env.AnnotationMap lamSpan param body then Ptr else I64
        let preReturnType = match stripAnnot body with | Lambda _ -> Ptr | _ -> I64
        let retIsBool = preReturnType = I64 && bodyReturnsBoolTyped env.AnnotationMap body
        let innerRetIsBool = match stripAnnot body with Lambda(_, innerBody, _) -> bodyReturnsBoolTyped env.AnnotationMap innerBody | _ -> false
        let sig_ : FuncSignature =
            { MlirName = "@" + name; ParamTypes = [paramType]; ReturnType = preReturnType; ClosureInfo = None
              ReturnIsBool = retIsBool; InnerReturnIsBool = innerRetIsBool }
        let shortNameAlias =
            let idx = name.IndexOf('_')
            if idx > 0 && System.Char.IsUpper(name.[0]) then Some (name.Substring(idx + 1))
            else None
        let bodyEnv : ElabEnv =
            { Vars = Map.ofList [(param, { Name = "%arg0"; Type = paramType })]
              Counter = ref 0; LabelCounter = ref 0; Blocks = ref []
              KnownFuncs =
                let kf = env.KnownFuncs
                match shortNameAlias with Some sn -> Map.add sn sig_ kf | None -> kf
              Funcs = env.Funcs
              ClosureCounter = env.ClosureCounter
              Globals = env.Globals
              GlobalCounter = env.GlobalCounter
              TplGlobals = env.TplGlobals
              TypeEnv = env.TypeEnv; RecordEnv = env.RecordEnv; ExnTags = env.ExnTags
              MutableVars = Set.empty; ArrayVars = Set.empty; CollectionVars = Map.empty
              BoolVars = Set.empty; StringVars = Set.empty; StringFields = env.StringFields; AnnotationMap = env.AnnotationMap }
        let (bodyVal, bodyEntryOps) = elaborateExpr bodyEnv body
        // Phase 88: Retag I1 returns to I64 tagged for uniform ABI.
        // Only I1 needs retag; I64/Ptr stay as-is.
        let (finalBodyVal, coerceRetOps) =
            if bodyVal.Type = I1 then coerceToI64 bodyEnv bodyVal
            else (bodyVal, [])
        let bodySideBlocks = bodyEnv.Blocks.Value
        let allBodyBlocks =
            if bodySideBlocks.IsEmpty then
                [ { Label = None; Args = []; Body = bodyEntryOps @ coerceRetOps @ [ReturnOp [finalBodyVal]] } ]
            else
                let entryBlock = { Label = None; Args = []; Body = bodyEntryOps }
                let lastBlock = List.last bodySideBlocks
                let lastBlockWithReturn = { lastBlock with Body = lastBlock.Body @ coerceRetOps @ [ReturnOp [finalBodyVal]] }
                let sideBlocksPatched = (List.take (bodySideBlocks.Length - 1) bodySideBlocks) @ [lastBlockWithReturn]
                entryBlock :: sideBlocksPatched
        let funcOp : FuncOp =
            { Name = "@" + name
              InputTypes = [paramType]
              ReturnType = Some finalBodyVal.Type
              Body = { Blocks = allBodyBlocks }
              IsLlvmFunc = false }
        // Phase 53: If a function with the same name already exists (e.g., from Prelude InstanceDecl
        // shadowing), replace it rather than appending, to avoid MLIR "redefinition of symbol" errors.
        let mlirName = "@" + name
        let existingFuncs = env.Funcs.Value
        if existingFuncs |> List.exists (fun f -> f.Name = mlirName) then
            env.Funcs.Value <- existingFuncs |> List.map (fun f -> if f.Name = mlirName then funcOp else f)
        else
            env.Funcs.Value <- existingFuncs @ [funcOp]
        let finalSig = { sig_ with ReturnType = finalBodyVal.Type }
        let env' =
            let kf = Map.add name finalSig env.KnownFuncs
            let kf' = match shortNameAlias with Some sn -> Map.add sn finalSig kf | None -> kf
            { env with KnownFuncs = kf' }
        elaborateExpr env' inExpr
    | Let (name, bindExpr, bodyExpr, _) ->
        let blocksBeforeBind = env.Blocks.Value.Length
        let (bv, bops) = elaborateExpr env bindExpr
        // Phase 36 FIX-02: Capture block count AFTER bindExpr but BEFORE bodyExpr.
        // This records the index of the OUTER expression's merge block, so we patch
        // the correct block even when bodyExpr adds more side blocks (e.g. a second if).
        let blocksAfterBind = env.Blocks.Value.Length
        let arrayVars' = if isArrayExpr env.ArrayVars env.AnnotationMap bindExpr then Set.add name env.ArrayVars else env.ArrayVars
        let collVars' =
            match detectCollectionKind env.CollectionVars env.AnnotationMap bindExpr with
            | Some kind -> Map.add name kind env.CollectionVars
            | None -> env.CollectionVars
        // Phase 35: Module-qualified naming — if name is module-prefixed (e.g., "List_hd"),
        // also bind the short name ("hd") so internal cross-references within the module body work.
        let moduleShortName =
            let idx = name.IndexOf('_')
            if idx > 0 && System.Char.IsUpper(name.[0]) then Some (name.Substring(idx + 1))
            else None
        // Phase 43: Track bool-producing bindings for to_string dispatch
        // Phase 43: Track bool-producing bindings AND bool-returning closures for to_string dispatch
        let boolVars' =
            if bv.Type = I1 || isBoolExpr env.BoolVars env.KnownFuncs env.AnnotationMap bindExpr
               || (match stripAnnot bindExpr with Lambda(_, body, _) -> bodyReturnsBoolTyped env.AnnotationMap body | _ -> false)
            then Set.add name env.BoolVars
            else env.BoolVars
        // Phase 66: Track string-producing bindings for IndexGet dispatch
        let stringVars' =
            if isStringExpr env.StringVars env.StringFields env.AnnotationMap bindExpr
            then Set.add name env.StringVars
            else env.StringVars
        let baseVars = Map.add name bv env.Vars
        let varsWithAlias = match moduleShortName with Some sn -> Map.add sn bv baseVars | None -> baseVars
        let env' = { env with Vars = varsWithAlias; ArrayVars = arrayVars'; CollectionVars = collVars'; BoolVars = boolVars'; StringVars = stringVars' }
        let (rv, rops) = elaborateExpr env' bodyExpr
        // If bops ends with a block terminator (from nested Match/TryWith/If), the
        // continuation code (rops) must go into the outer if's merge block (at blocksAfterBind - 1),
        // not the last block in env.Blocks (which may be the INNER if's merge block).
        match List.tryLast bops with
        | Some op when isTerminatorOp op && blocksAfterBind > blocksBeforeBind ->
            // Place rops in the OUTER merge block (captured BEFORE bodyExpr elaboration)
            let innerBlocks = env.Blocks.Value
            let targetIdx = blocksAfterBind - 1
            let targetBlock = innerBlocks.[targetIdx]
            let patchedTarget = { targetBlock with Body = targetBlock.Body @ rops }
            env.Blocks.Value <- innerBlocks |> List.mapi (fun i b -> if i = targetIdx then patchedTarget else b)
            (rv, bops)  // bops alone (terminator as last op — valid MLIR block ending)
        | _ ->
            (rv, bops @ rops)
    // Phase 7: LetPat with wildcard or var pattern — enables "let _ = print ... in ..."
    // Phase 28: Also handles "e1; e2" (desugared to LetPat(WildcardPat, e1, e2)) where e1 is If(cond, then, unit).
    //   If bops ends with a block terminator (from nested If/Match), rops must go into the merge block,
    //   not appended inline (which would violate MLIR's block terminator rule).
    | LetPat (WildcardPat _, bindExpr, bodyExpr, _) ->
        let blocksBeforeBind = env.Blocks.Value.Length
        let (_bv, bops) = elaborateExpr env bindExpr
        // Phase 36 FIX-02: Capture block count AFTER bindExpr but BEFORE bodyExpr.
        // Same fix as Let case — targets the OUTER merge block, not the innermost one.
        let blocksAfterBind = env.Blocks.Value.Length
        let (rv, rops) = elaborateExpr env bodyExpr
        match List.tryLast bops with
        | Some op when isTerminatorOp op && blocksAfterBind > blocksBeforeBind ->
            // Place rops in the OUTER merge block (captured BEFORE bodyExpr elaboration)
            let innerBlocks = env.Blocks.Value
            let targetIdx = blocksAfterBind - 1
            let targetBlock = innerBlocks.[targetIdx]
            let patchedTarget = { targetBlock with Body = targetBlock.Body @ rops }
            env.Blocks.Value <- innerBlocks |> List.mapi (fun i b -> if i = targetIdx then patchedTarget else b)
            (rv, bops)
        | _ ->
            (rv, bops @ rops)
    | LetPat (VarPat (name, _), bindExpr, bodyExpr, _) ->
        let blocksBeforeBind = env.Blocks.Value.Length
        let (bv, bops) = elaborateExpr env bindExpr
        let blocksAfterBind = env.Blocks.Value.Length
        let arrayVars' = if isArrayExpr env.ArrayVars env.AnnotationMap bindExpr then Set.add name env.ArrayVars else env.ArrayVars
        let collVars' =
            match detectCollectionKind env.CollectionVars env.AnnotationMap bindExpr with
            | Some kind -> Map.add name kind env.CollectionVars
            | None -> env.CollectionVars
        let env' = { env with Vars = Map.add name bv env.Vars; ArrayVars = arrayVars'; CollectionVars = collVars' }
        let (rv, rops) = elaborateExpr env' bodyExpr
        match List.tryLast bops with
        | Some op when isTerminatorOp op && blocksAfterBind > blocksBeforeBind ->
            // bindExpr ended with a block terminator (e.g. If/Match).
            // Place rops in the merge block (captured BEFORE bodyExpr elaboration).
            let innerBlocks = env.Blocks.Value
            let targetIdx = blocksAfterBind - 1
            let targetBlock = innerBlocks.[targetIdx]
            let patchedTarget = { targetBlock with Body = targetBlock.Body @ rops }
            env.Blocks.Value <- innerBlocks |> List.mapi (fun i b -> if i = targetIdx then patchedTarget else b)
            (rv, bops)
        | _ ->
            (rv, bops @ rops)
    // Phase 9: LetPat with TuplePat — destructure a heap-allocated tuple via GEP + load
    | LetPat (TuplePat (pats, _), bindExpr, bodyExpr, _) ->
        let (rawTupVal, bindOps) = elaborateExpr env bindExpr
        // Phase 34-03: If the bind expression returns I64 (e.g., closure parameter from ForInExpr TuplePat),
        // coerce to Ptr before GEP-based destructuring.
        let (tupPtrVal, coerceOps) =
            if rawTupVal.Type = I64 then
                let pv = { Name = freshName env; Type = Ptr }
                (pv, [LlvmIntToPtrOp(pv, rawTupVal)])
            else
                (rawTupVal, [])
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
                let gepOp    = LlvmGEPLinearOp(slotVal, ptrVal, i + 2)  // Phase 93: +2 for tag+count header
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
                    failWithSpan (Ast.patternSpanOf pat) "Elaboration: unsupported sub-pattern in TuplePat: %A" pat
            ) ([], envAcc)
        let (extractOps, env') = bindTuplePat env tupPtrVal pats
        let (bodyVal, bodyOps) = elaborateExpr env' bodyExpr
        (bodyVal, bindOps @ coerceOps @ extractOps @ bodyOps)
    | Bool (b, _) ->
        let v = { Name = freshName env; Type = I1 }
        let n = if b then 1L else 0L
        (v, [ArithConstantOp(v, n)])
    | Equal (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        // Phase 62: Coerce mismatched types before comparison (e.g., I64 vs Ptr from null list)
        let (lv, lops, rv, rops) =
            if lv.Type = Ptr && rv.Type = I64 then
                let c = { Name = freshName env; Type = I64 }
                (c, lops @ [LlvmPtrToIntOp(c, lv)], rv, rops)
            elif lv.Type = I64 && rv.Type = Ptr then
                let c = { Name = freshName env; Type = I64 }
                (lv, lops, c, rops @ [LlvmPtrToIntOp(c, rv)])
            else (lv, lops, rv, rops)
        if lv.Type = Ptr then
            // Phase 93: Generic structural equality via lang_generic_eq
            let cmpResult  = { Name = freshName env; Type = I64 }
            let zero64     = { Name = freshName env; Type = I64 }
            let boolResult = { Name = freshName env; Type = I1 }
            let ops = [
                LlvmCallOp(cmpResult, "@lang_generic_eq", [lv; rv])
                ArithConstantOp(zero64, 0L)
                ArithCmpIOp(boolResult, "ne", cmpResult, zero64)
            ]
            (boolResult, lops @ rops @ ops)
        else
            let result = { Name = freshName env; Type = I1 }
            (result, lops @ rops @ [ArithCmpIOp(result, "eq", lv, rv)])
    | NotEqual (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        // Phase 62: Coerce mismatched types before comparison
        let (lv, lops, rv, rops) =
            if lv.Type = Ptr && rv.Type = I64 then
                let c = { Name = freshName env; Type = I64 }
                (c, lops @ [LlvmPtrToIntOp(c, lv)], rv, rops)
            elif lv.Type = I64 && rv.Type = Ptr then
                let c = { Name = freshName env; Type = I64 }
                (lv, lops, c, rops @ [LlvmPtrToIntOp(c, rv)])
            else (lv, lops, rv, rops)
        if lv.Type = Ptr then
            // Phase 93: Generic structural inequality via lang_generic_eq
            let cmpResult  = { Name = freshName env; Type = I64 }
            let zero64     = { Name = freshName env; Type = I64 }
            let boolResult = { Name = freshName env; Type = I1 }
            let ops = [
                LlvmCallOp(cmpResult, "@lang_generic_eq", [lv; rv])
                ArithConstantOp(zero64, 0L)
                ArithCmpIOp(boolResult, "eq", cmpResult, zero64)
            ]
            (boolResult, lops @ rops @ ops)
        else
            let result = { Name = freshName env; Type = I1 }
            (result, lops @ rops @ [ArithCmpIOp(result, "ne", lv, rv)])
    | LessThan (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let (lv64, lCoerce) = coerceToI64 env lv
        let (rv64, rCoerce) = coerceToI64 env rv
        let result = { Name = freshName env; Type = I1 }
        (result, lops @ rops @ lCoerce @ rCoerce @ [ArithCmpIOp(result, "slt", lv64, rv64)])
    | GreaterThan (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let (lv64, lCoerce) = coerceToI64 env lv
        let (rv64, rCoerce) = coerceToI64 env rv
        let result = { Name = freshName env; Type = I1 }
        (result, lops @ rops @ lCoerce @ rCoerce @ [ArithCmpIOp(result, "sgt", lv64, rv64)])
    | LessEqual (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let (lv64, lCoerce) = coerceToI64 env lv
        let (rv64, rCoerce) = coerceToI64 env rv
        let result = { Name = freshName env; Type = I1 }
        (result, lops @ rops @ lCoerce @ rCoerce @ [ArithCmpIOp(result, "sle", lv64, rv64)])
    | GreaterEqual (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let (lv64, lCoerce) = coerceToI64 env lv
        let (rv64, rCoerce) = coerceToI64 env rv
        let result = { Name = freshName env; Type = I1 }
        (result, lops @ rops @ lCoerce @ rCoerce @ [ArithCmpIOp(result, "sge", lv64, rv64)])
    | If (condExpr, thenExpr, elseExpr, _) ->
        let blocksBeforeCond = env.Blocks.Value.Length
        let (condVal, condOps) = elaborateExpr env condExpr
        let blocksAfterCond = env.Blocks.Value.Length
        // Phase 35: If condition is I64 (e.g. result from module-wrapped bool builtin),
        // compare != tagged(false) to produce I1 for cf.cond_br.
        let (i1CondVal, coerceCondOps) = coerceToI1 env condVal
        let thenLabel  = freshLabel env "then"
        let elseLabel  = freshLabel env "else"
        let mergeLabel = freshLabel env "merge"
        let blocksBeforeThen = env.Blocks.Value.Length
        let (thenVal, thenOps) = elaborateExpr env thenExpr
        let blocksAfterThen = env.Blocks.Value.Length
        let blocksBeforeElse = env.Blocks.Value.Length
        let (elseVal, elseOps) = elaborateExpr env elseExpr
        let blocksAfterElse = env.Blocks.Value.Length
        // If branch types differ, coerce both to I64 for uniform merge block type.
        // This handles cases like: then = Ptr (cons cell), else = I64 (closure result).
        let (finalThenVal, thenCoerceOps) =
            if thenVal.Type <> elseVal.Type then coerceToI64 env thenVal else (thenVal, [])
        let (finalElseVal, elseCoerceOps) =
            if thenVal.Type <> elseVal.Type then coerceToI64 env elseVal else (elseVal, [])
        let mergeArg = { Name = freshName env; Type = finalThenVal.Type }
        // Phase 42 FIX: When branch expr is a Match/If, its ops end with a terminator and
        // side blocks (match arms, match merge) are created in env.Blocks. The continuation
        // CfBrOp(mergeLabel) must go into the LAST side block (match's merge block), not
        // appended inline after the terminator (which would be unreachable).
        // Then block
        let thenBlockBody =
            match List.tryLast thenOps with
            | Some op when isTerminatorOp op && blocksAfterThen > blocksBeforeThen ->
                // thenExpr created side blocks (e.g. nested match).
                // Patch CfBrOp(mergeLabel) into the last side block (match's merge block).
                // IMPORTANT: append AFTER targetBlock.Body (which may contain ops computing thenVal).
                let targetIdx = blocksAfterThen - 1
                appendToBlock env targetIdx (thenCoerceOps @ [CfBrOp(mergeLabel, [finalThenVal])])
                thenOps  // dispatch ops only (terminator ends block)
            | _ ->
                thenOps @ thenCoerceOps @ [CfBrOp(mergeLabel, [finalThenVal])]
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some thenLabel; Args = []; Body = thenBlockBody } ]
        // Else block
        let elseBlockBody =
            match List.tryLast elseOps with
            | Some op when isTerminatorOp op && blocksAfterElse > blocksBeforeElse ->
                // elseExpr created side blocks (e.g. nested match).
                // Patch CfBrOp(mergeLabel) into the last side block (match's merge block).
                // IMPORTANT: append AFTER targetBlock.Body (which may contain ops computing elseVal).
                let targetIdx = blocksAfterElse - 1
                appendToBlock env targetIdx (elseCoerceOps @ [CfBrOp(mergeLabel, [finalElseVal])])
                elseOps  // dispatch ops only
            | _ ->
                elseOps @ elseCoerceOps @ [CfBrOp(mergeLabel, [finalElseVal])]
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some elseLabel; Args = []; Body = elseBlockBody } ]
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some mergeLabel; Args = [mergeArg]; Body = [] } ]
        // Phase 36 FIX-03: If condOps ends with a terminator (e.g. And/Or produced a CfCondBrOp),
        // the If's own CfCondBrOp must go into the And/Or's merge block (blocksAfterCond - 1),
        // not appended inline after the terminator.
        let ifBranchOp = CfCondBrOp(i1CondVal, thenLabel, [], elseLabel, [])
        match List.tryLast condOps with
        | Some op when isTerminatorOp op && blocksAfterCond > blocksBeforeCond ->
            // Patch the If's CfCondBrOp into the condition's merge block.
            // IMPORTANT: append AFTER targetBlock.Body — an App handler may have already
            // placed continuation ops (e.g. Core_not call) that must execute before the branch.
            let targetIdx = blocksAfterCond - 1
            appendToBlock env targetIdx (coerceCondOps @ [ifBranchOp])
            (mergeArg, condOps)
        | _ ->
            (mergeArg, condOps @ coerceCondOps @ [ifBranchOp])
    | And (lhsExpr, rhsExpr, _) ->
        let blocksBeforeAnd = List.length env.Blocks.Value
        let (leftVal, leftOps) = elaborateExpr env lhsExpr
        let blocksAfterLeft = List.length env.Blocks.Value
        // Phase 36 FIX-03: If leftVal is I64 (e.g. module Bool function returning I64),
        // coerce to I1 via != tagged(false) comparison before use in cf.cond_br.
        let (i1LeftVal, coerceLeftOps) = coerceToI1 env leftVal
        let evalRightLabel = freshLabel env "and_right"
        let mergeLabel     = freshLabel env "and_merge"
        let blocksBeforeRight = List.length env.Blocks.Value
        let (rightVal, rightOps) = elaborateExpr env rhsExpr
        let blocksAfterRight = List.length env.Blocks.Value
        // Phase 88: And returns I64 tagged bool directly (avoids coerceToI64 after terminator).
        // Right branch: coerce rightVal to I64 tagged.
        let (i64RightVal, coerceRightOps) = coerceToI64 env rightVal
        // Phase 88: tagged false constant for false branch
        let taggedFalse = { Name = freshName env; Type = I64 }
        let taggedFalseOp = ArithConstantOp(taggedFalse, 1L)
        let mergeArg = { Name = freshName env; Type = I64 }
        let rightEndsWithTerm = rightOps |> List.tryLast |> Option.map isTerminatorOp |> Option.defaultValue false
        if rightEndsWithTerm && blocksAfterRight > blocksBeforeRight then
            env.Blocks.Value <- env.Blocks.Value @
                [ { Label = Some evalRightLabel; Args = []; Body = rightOps } ]
            let innerMergeIdx = blocksAfterRight - 1
            appendToBlock env innerMergeIdx (coerceRightOps @ [CfBrOp(mergeLabel, [i64RightVal])])
        else
            env.Blocks.Value <- env.Blocks.Value @
                [ { Label = Some evalRightLabel; Args = []; Body = rightOps @ coerceRightOps @ [CfBrOp(mergeLabel, [i64RightVal])] } ]
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some mergeLabel; Args = [mergeArg]; Body = [] } ]
        let andBranchOp = CfCondBrOp(i1LeftVal, evalRightLabel, [], mergeLabel, [taggedFalse])
        match List.tryLast leftOps with
        | Some op when isTerminatorOp op && blocksAfterLeft > blocksBeforeAnd ->
            let targetIdx = blocksAfterLeft - 1
            appendToBlock env targetIdx (coerceLeftOps @ [taggedFalseOp; andBranchOp])
            (mergeArg, leftOps)
        | _ ->
            (mergeArg, leftOps @ coerceLeftOps @ [taggedFalseOp; andBranchOp])
    | Or (lhsExpr, rhsExpr, _) ->
        let blocksBeforeOr = List.length env.Blocks.Value
        let (leftVal, leftOps) = elaborateExpr env lhsExpr
        let blocksAfterLeft = List.length env.Blocks.Value
        // Phase 36 FIX-03: If leftVal is I64, coerce to I1 via != tagged(false).
        // Note: Or is short-circuit: true → merge (with tagged true), false → eval right.
        let (i1LeftVal, coerceLeftOps) = coerceToI1 env leftVal
        let evalRightLabel = freshLabel env "or_right"
        let mergeLabel     = freshLabel env "or_merge"
        let blocksBeforeRight = List.length env.Blocks.Value
        let (rightVal, rightOps) = elaborateExpr env rhsExpr
        let blocksAfterRight = List.length env.Blocks.Value
        // Phase 88: Or returns I64 tagged bool directly.
        let (i64RightVal, coerceRightOps) = coerceToI64 env rightVal
        // Phase 88: tagged true constant for true branch
        let taggedTrue = { Name = freshName env; Type = I64 }
        let taggedTrueOp = ArithConstantOp(taggedTrue, 3L)
        let mergeArg = { Name = freshName env; Type = I64 }
        let rightEndsWithTerm = rightOps |> List.tryLast |> Option.map isTerminatorOp |> Option.defaultValue false
        if rightEndsWithTerm && blocksAfterRight > blocksBeforeRight then
            env.Blocks.Value <- env.Blocks.Value @
                [ { Label = Some evalRightLabel; Args = []; Body = rightOps } ]
            let innerMergeIdx = blocksAfterRight - 1
            appendToBlock env innerMergeIdx (coerceRightOps @ [CfBrOp(mergeLabel, [i64RightVal])])
        else
            env.Blocks.Value <- env.Blocks.Value @
                [ { Label = Some evalRightLabel; Args = []; Body = rightOps @ coerceRightOps @ [CfBrOp(mergeLabel, [i64RightVal])] } ]
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some mergeLabel; Args = [mergeArg]; Body = [] } ]
        let orBranchOp = CfCondBrOp(i1LeftVal, mergeLabel, [taggedTrue], evalRightLabel, [])
        match List.tryLast leftOps with
        | Some op when isTerminatorOp op && blocksAfterLeft > blocksBeforeOr ->
            let targetIdx = blocksAfterLeft - 1
            appendToBlock env targetIdx (coerceLeftOps @ [taggedTrueOp; orBranchOp])
            (mergeArg, leftOps)
        | _ ->
            (mergeArg, leftOps @ coerceLeftOps @ [taggedTrueOp; orBranchOp])
    | LetRec (bindings, inExpr, _) ->
        // Phase 66: Two-pass elaboration for mutual recursion (let rec ... and ...).
        // Pass 1: Pre-register ALL binding signatures in KnownFuncs so every body can call every sibling.
        // Pass 2: Elaborate each body with the full KnownFuncs, then update with actual return types.
        let bindingInfos =
            bindings |> List.map (fun (name, param, _paramTypeAnnot, body, bindingSpan) ->
                let paramType = if isPtrParamTyped env.AnnotationMap bindingSpan param body then Ptr else I64
                let preReturnType = match stripAnnot body with | Lambda _ -> Ptr | _ -> I64
                let retIsBool = preReturnType = I64 && bodyReturnsBoolTyped env.AnnotationMap body
                let innerRetIsBool = match stripAnnot body with Lambda(_, ib, _) -> bodyReturnsBoolTyped env.AnnotationMap ib | _ -> false
                let shortNameAlias =
                    let idx = name.IndexOf('_')
                    if idx > 0 && System.Char.IsUpper(name.[0]) then Some (name.Substring(idx + 1))
                    else None
                let sig_ : FuncSignature =
                    { MlirName = "@" + name; ParamTypes = [paramType]; ReturnType = preReturnType; ClosureInfo = None
                      ReturnIsBool = retIsBool; InnerReturnIsBool = innerRetIsBool }
                (name, param, body, paramType, sig_, shortNameAlias))
        // Pass 1: Add all signatures to KnownFuncs (pre-return types)
        let allKnownFuncs =
            bindingInfos |> List.fold (fun kf (name, _, _, _, sig_, shortAlias) ->
                let kf' = Map.add name sig_ kf
                match shortAlias with Some sn -> Map.add sn sig_ kf' | None -> kf'
            ) env.KnownFuncs
        // Pass 2: Elaborate each body with full KnownFuncs, emit func.func, collect final sigs
        let finalKnownFuncs =
            bindingInfos |> List.fold (fun kfAcc (name, param, body, paramType, sig_, shortAlias) ->
                let bodyEnv : ElabEnv =
                    { Vars = Map.ofList [(param, { Name = "%arg0"; Type = paramType })]
                      Counter = ref 0; LabelCounter = ref 0; Blocks = ref []
                      KnownFuncs = allKnownFuncs
                      Funcs = env.Funcs
                      ClosureCounter = env.ClosureCounter
                      Globals = env.Globals
                      GlobalCounter = env.GlobalCounter
                      TplGlobals = env.TplGlobals
                      TypeEnv = env.TypeEnv; RecordEnv = env.RecordEnv; ExnTags = env.ExnTags
                      MutableVars = Set.empty; ArrayVars = Set.empty; CollectionVars = Map.empty
                      BoolVars = Set.empty; StringVars = Set.empty; StringFields = env.StringFields; AnnotationMap = env.AnnotationMap }
                let (bodyVal, bodyEntryOps) = elaborateExpr bodyEnv body
                // Phase 88: Retag I1 returns to I64 tagged for uniform ABI.
                let (finalBodyVal, coerceRetOps) =
                    if bodyVal.Type = I1 then coerceToI64 bodyEnv bodyVal
                    else (bodyVal, [])
                let bodySideBlocks = bodyEnv.Blocks.Value
                let allBodyBlocks =
                    if bodySideBlocks.IsEmpty then
                        [ { Label = None; Args = []; Body = bodyEntryOps @ coerceRetOps @ [ReturnOp [finalBodyVal]] } ]
                    else
                        let entryBlock = { Label = None; Args = []; Body = bodyEntryOps }
                        let lastBlock = List.last bodySideBlocks
                        let lastBlockWithReturn = { lastBlock with Body = lastBlock.Body @ coerceRetOps @ [ReturnOp [finalBodyVal]] }
                        let sideBlocksPatched = (List.take (bodySideBlocks.Length - 1) bodySideBlocks) @ [lastBlockWithReturn]
                        entryBlock :: sideBlocksPatched
                let funcOp : FuncOp =
                    { Name = "@" + name
                      InputTypes = [paramType]
                      ReturnType = Some finalBodyVal.Type
                      Body = { Blocks = allBodyBlocks }
                      IsLlvmFunc = false }
                env.Funcs.Value <- env.Funcs.Value @ [funcOp]
                let finalSig = { sig_ with ReturnType = finalBodyVal.Type }
                let kfAcc' = Map.add name finalSig kfAcc
                match shortAlias with Some sn -> Map.add sn finalSig kfAcc' | None -> kfAcc'
            ) env.KnownFuncs
        let env' = { env with KnownFuncs = finalKnownFuncs }
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
        let (strPtr, strCoerce) = coerceToPtrArg env strVal
        // Phase 92: C function now untags start and length internally
        let result = { Name = freshName env; Type = Ptr }
        (result, strOps @ startOps @ lenOps @ strCoerce @ [LlvmCallOp(result, "@lang_string_sub", [strPtr; startVal; lenVal])])

    // Phase 14: string_contains builtin — App(App(Var("string_contains"), s), sub)
    | App (App (Var ("string_contains", _), strExpr, _), subExpr, _) ->
        emitStrPredicate env "@lang_string_contains" strExpr subExpr elaborateExpr

    // Phase 31: string_endswith builtin — App(App(Var("string_endswith"), s), suffix)
    | App (App (Var ("string_endswith", _), strExpr, _), suffixExpr, _) ->
        emitStrPredicate env "@lang_string_endswith" strExpr suffixExpr elaborateExpr

    // Phase 31: string_startswith builtin — App(App(Var("string_startswith"), s), prefix)
    | App (App (Var ("string_startswith", _), strExpr, _), prefixExpr, _) ->
        emitStrPredicate env "@lang_string_startswith" strExpr prefixExpr elaborateExpr

    // Phase 31: string_trim builtin — App(Var("string_trim"), s)
    | App (Var ("string_trim", _), strExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let (strPtr, strCoerce) = coerceToPtrArg env strVal
        let result = { Name = freshName env; Type = Ptr }
        (result, strOps @ strCoerce @ [LlvmCallOp(result, "@lang_string_trim", [strPtr])])

    // Phase 54: string_split builtin — App(App(Var("string_split"), s), sep)
    | App (App (Var ("string_split", _), strExpr, _), sepExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let (sepVal, sepOps) = elaborateExpr env sepExpr
        let (strPtr, strCoerce) = coerceToPtrArg env strVal
        let (sepPtr, sepCoerce) = coerceToPtrArg env sepVal
        let result = { Name = freshName env; Type = Ptr }
        (result, strOps @ sepOps @ strCoerce @ sepCoerce @ [LlvmCallOp(result, "@lang_string_split", [strPtr; sepPtr])])

    // Phase 54: string_indexof builtin — App(App(Var("string_indexof"), s), sub)
    | App (App (Var ("string_indexof", _), strExpr, _), subExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let (subVal, subOps) = elaborateExpr env subExpr
        let (strPtr, strCoerce) = coerceToPtrArg env strVal
        let (subPtr, subCoerce) = coerceToPtrArg env subVal
        // Phase 92: C function now returns tagged result
        let result = { Name = freshName env; Type = I64 }
        (result, strOps @ subOps @ strCoerce @ subCoerce @ [LlvmCallOp(result, "@lang_string_indexof", [strPtr; subPtr])])

    // Phase 54: string_replace builtin — App(App(App(Var("string_replace"), s), old), rep)
    | App (App (App (Var ("string_replace", _), strExpr, _), oldExpr, _), repExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let (oldVal, oldOps) = elaborateExpr env oldExpr
        let (repVal, repOps) = elaborateExpr env repExpr
        let (strPtr, strCoerce) = coerceToPtrArg env strVal
        let (oldPtr, oldCoerce) = coerceToPtrArg env oldVal
        let (repPtr, repCoerce) = coerceToPtrArg env repVal
        let result = { Name = freshName env; Type = Ptr }
        (result, strOps @ oldOps @ repOps @ strCoerce @ oldCoerce @ repCoerce @ [LlvmCallOp(result, "@lang_string_replace", [strPtr; oldPtr; repPtr])])

    // Phase 54: string_toupper builtin — App(Var("string_toupper"), s)
    | App (Var ("string_toupper", _), strExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let (strPtr, strCoerce) = coerceToPtrArg env strVal
        let result = { Name = freshName env; Type = Ptr }
        (result, strOps @ strCoerce @ [LlvmCallOp(result, "@lang_string_toupper", [strPtr])])

    // Phase 54: string_tolower builtin — App(Var("string_tolower"), s)
    | App (Var ("string_tolower", _), strExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let (strPtr, strCoerce) = coerceToPtrArg env strVal
        let result = { Name = freshName env; Type = Ptr }
        (result, strOps @ strCoerce @ [LlvmCallOp(result, "@lang_string_tolower", [strPtr])])

    // Phase 31: string_concat_list builtin — App(App(Var("string_concat_list"), sep), list)
    | App (App (Var ("string_concat_list", _), sepExpr, _), listExpr, _) ->
        let (sepVal,  sepOps)  = elaborateExpr env sepExpr
        let (listVal, listOps) = elaborateExpr env listExpr
        let (sepPtr,  sepCoerce)  = coerceToPtrArg env sepVal
        let (listPtr, listCoerce) = coerceToPtrArg env listVal
        let result = { Name = freshName env; Type = Ptr }
        (result, sepOps @ listOps @ sepCoerce @ listCoerce @ [LlvmCallOp(result, "@lang_string_concat_list", [sepPtr; listPtr])])

    // Phase 8: string_concat builtin — App(App(Var("string_concat"), a), b)
    // Must be placed BEFORE general App to avoid being caught by general App dispatch
    | App (App (Var ("string_concat", _), aExpr, _), bExpr, _) ->
        let (aVal, aOps) = elaborateExpr env aExpr
        let (bVal, bOps) = elaborateExpr env bExpr
        let (aPtr, aCoerce) = coerceToPtrArg env aVal
        let (bPtr, bCoerce) = coerceToPtrArg env bVal
        let result = { Name = freshName env; Type = Ptr }
        (result, aOps @ bOps @ aCoerce @ bCoerce @ [LlvmCallOp(result, "@lang_string_concat", [aPtr; bPtr])])

    // Phase 53: show builtin — polymorphic display, dispatches on static argument type.
    // Fires when argument is statically a string literal OR an integer/bool expression.
    // If the argument is a constructor, ADT variable, or other Ptr, falls through to the
    // derived/user-defined show function in KnownFuncs.
    | App (Var ("show", _), String (s, _), appSpan) ->
        // show on string literal: return the string as-is (identity)
        elaborateExpr env (Ast.String(s, appSpan))
    | App (Var ("show", _), argExpr, _) when
            (let isStr = match stripAnnot argExpr with
                         | Ast.Var(v, _) -> Map.tryFind v env.Vars |> Option.exists (fun mv -> mv.Type = Ptr)
                                            || Map.tryFind v env.KnownFuncs |> Option.exists (fun s -> s.ReturnType = Ptr)
                         | _ -> false
             not isStr && not (Map.containsKey "show" env.KnownFuncs &&
                               (Map.find "show" env.KnownFuncs).ParamTypes = [Ptr])) ->
        let (argVal, argOps) = elaborateExpr env argExpr
        let result = { Name = freshName env; Type = Ptr }
        let isBool = argVal.Type = I1 || isBoolExpr env.BoolVars env.KnownFuncs env.AnnotationMap argExpr
        if isBool then
            if argVal.Type = I1 then
                // Phase 92: zext I1 to I64 then retag — C now expects tagged bool
                let extVal = { Name = freshName env; Type = I64 }
                let (taggedExt, retagOps) = emitRetag env extVal
                (result, argOps @ [ArithExtuIOp(extVal, argVal)] @ retagOps @ [LlvmCallOp(result, "@lang_to_string_bool", [taggedExt])])
            else
                // Phase 92: C function now untags internally
                (result, argOps @ [LlvmCallOp(result, "@lang_to_string_bool", [argVal])])
        elif argVal.Type = Ptr then
            (argVal, argOps)
        else
            // Phase 92: C function now untags internally
            (result, argOps @ [LlvmCallOp(result, "@lang_to_string_int", [argVal])])

    // Phase 53: eq builtin — polymorphic equality, dispatches on static argument type.
    // Fires when arguments are string literals or non-Ptr values.
    | App (App (Var ("eq", _), String(ls, lsSpan), _), String(rs, rsSpan), _) ->
        // eq on two string literals
        let (lv, lops) = elaborateExpr env (Ast.String(ls, lsSpan))
        let (rv, rops) = elaborateExpr env (Ast.String(rs, rsSpan))
        let lDataPtr   = { Name = freshName env; Type = Ptr }
        let lData      = { Name = freshName env; Type = Ptr }
        let rDataPtr   = { Name = freshName env; Type = Ptr }
        let rData      = { Name = freshName env; Type = Ptr }
        let cmpResult  = { Name = freshName env; Type = I32 }
        let zero32     = { Name = freshName env; Type = I32 }
        let boolResult = { Name = freshName env; Type = I1 }
        let ops = [
            LlvmGEPStructOp(lDataPtr, lv, 2)
            LlvmLoadOp(lData, lDataPtr)
            LlvmGEPStructOp(rDataPtr, rv, 2)
            LlvmLoadOp(rData, rDataPtr)
            LlvmCallOp(cmpResult, "@strcmp", [lData; rData])
            ArithConstantOp(zero32, 0L)
            ArithCmpIOp(boolResult, "eq", cmpResult, zero32)
        ]
        (boolResult, lops @ rops @ ops)
    | App (App (Var ("eq", _), lhsExpr, _), rhsExpr, _) when not (Map.containsKey "eq" env.KnownFuncs) ->
        let (lv, lops) = elaborateExpr env lhsExpr
        let (rv, rops) = elaborateExpr env rhsExpr
        if lv.Type = Ptr then
            // Phase 93: Generic structural equality via lang_generic_eq
            let cmpResult  = { Name = freshName env; Type = I64 }
            let zero64     = { Name = freshName env; Type = I64 }
            let boolResult = { Name = freshName env; Type = I1 }
            let ops = [
                LlvmCallOp(cmpResult, "@lang_generic_eq", [lv; rv])
                ArithConstantOp(zero64, 0L)
                ArithCmpIOp(boolResult, "ne", cmpResult, zero64)
            ]
            (boolResult, lops @ rops @ ops)
        else
            // I64 (int, bool, char) — integer comparison
            let result = { Name = freshName env; Type = I1 }
            (result, lops @ rops @ [ArithCmpIOp(result, "eq", lv, rv)])

    // Phase 8: to_string builtin — dispatch on elaborated arg type
    // Phase 43: also check BoolVars/KnownFuncs for bool-returning function calls
    | App (Var ("to_string", _), argExpr, _) ->
        let (argVal, argOps) = elaborateExpr env argExpr
        let result = { Name = freshName env; Type = Ptr }
        let isBool = argVal.Type = I1 || isBoolExpr env.BoolVars env.KnownFuncs env.AnnotationMap argExpr
        if isBool then
            if argVal.Type = I1 then
                // Phase 92: zext I1 to I64 then retag — C now expects tagged bool
                let extVal = { Name = freshName env; Type = I64 }
                let (taggedExt, retagOps) = emitRetag env extVal
                (result, argOps @ [ArithExtuIOp(extVal, argVal)] @ retagOps @ [LlvmCallOp(result, "@lang_to_string_bool", [taggedExt])])
            else
                // Phase 92: C function now untags internally
                (result, argOps @ [LlvmCallOp(result, "@lang_to_string_bool", [argVal])])
        elif argVal.Type = Ptr then
            (argVal, argOps)
        else
            // Phase 92: C function now untags internally
            (result, argOps @ [LlvmCallOp(result, "@lang_to_string_int", [argVal])])

    // Phase 92: string_length — C wrapper returns tagged length
    | App (Var ("string_length", _), strExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let (strPtr, strCoerce) = coerceToPtrArg env strVal
        let result = { Name = freshName env; Type = I64 }
        (result, strOps @ strCoerce @ [LlvmCallOp(result, "@lang_string_length", [strPtr])])

    // Phase 14: failwith builtin — extract char* from LangString, call lang_failwith (noreturn)
    | App (Var ("failwith", _), msgExpr, _) ->
        let (msgVal, msgOps) = elaborateExpr env msgExpr
        let (msgPtr, msgCoerce) = coerceToPtrArg env msgVal
        let dataPtrVal  = { Name = freshName env; Type = Ptr }
        let dataAddrVal = { Name = freshName env; Type = Ptr }
        let unitVal     = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmGEPStructOp(dataPtrVal, msgPtr, 2)
            LlvmLoadOp(dataAddrVal, dataPtrVal)
            LlvmCallVoidOp("@lang_failwith", [dataAddrVal])
            ArithConstantOp(unitVal, 1L)
        ]
        (unitVal, msgOps @ msgCoerce @ ops)

    // Phase 14: string_to_int builtin
    | App (Var ("string_to_int", _), strExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let (strPtr, strCoerce) = coerceToPtrArg env strVal
        // Phase 92: C function now returns tagged result
        let result = { Name = freshName env; Type = I64 }
        (result, strOps @ strCoerce @ [LlvmCallOp(result, "@lang_string_to_int", [strPtr])])

    // Phase 92: array_set — C function handles untag, bounds check, store
    | App (App (App (Var ("array_set", _), arrExpr, _), idxExpr, _), valExpr, _) ->
        let (arrVal, arrOps) = elaborateExpr env arrExpr
        let (idxVal, idxOps) = elaborateExpr env idxExpr
        let (valVal, valOps) = elaborateExpr env valExpr
        let (arrPtrVal, arrCoerceOps) = coerceToPtrArg env arrVal
        let (valV, valCoerce) = coerceToI64 env valVal
        let (unitVal, callOps) = emitVoidCall env "@lang_array_set" [arrPtrVal; idxVal; valV]
        (unitVal, arrOps @ arrCoerceOps @ idxOps @ valOps @ valCoerce @ callOps)

    // Phase 92: array_get — C function handles untag, bounds check, load
    | App (App (Var ("array_get", _), arrExpr, _), idxExpr, _) ->
        let (arrVal, arrOps) = elaborateExpr env arrExpr
        let (idxVal, idxOps) = elaborateExpr env idxExpr
        let (arrPtrVal, arrCoerceOps) = coerceToPtrArg env arrVal
        let result = { Name = freshName env; Type = I64 }
        (result, arrOps @ arrCoerceOps @ idxOps @ [LlvmCallOp(result, "@lang_array_get", [arrPtrVal; idxVal])])

    // Phase 92: array_create — C function untags count internally
    | App (App (Var ("array_create", _), nExpr, _), defExpr, _) ->
        let (nVal,   nOps)   = elaborateExpr env nExpr
        let (defVal, defOps) = elaborateExpr env defExpr
        let (nV, nCoerce)     = coerceToI64 env nVal
        let (defV, defCoerce) = coerceToI64 env defVal
        let result = { Name = freshName env; Type = Ptr }
        (result, nOps @ defOps @ nCoerce @ defCoerce @ [LlvmCallOp(result, "@lang_array_create", [nV; defV])])

    // Phase 92: array_length — C wrapper returns tagged length
    | App (Var ("array_length", _), arrExpr, _) ->
        let (arrVal, arrOps) = elaborateExpr env arrExpr
        let (arrPtrVal, arrCoerceOps) = coerceToPtrArg env arrVal
        let result = { Name = freshName env; Type = I64 }
        (result, arrOps @ arrCoerceOps @ [LlvmCallOp(result, "@lang_array_length", [arrPtrVal])])

    // Phase 22: array_of_list — one-arg
    | App (Var ("array_of_list", _), lstExpr, _) ->
        let (lstVal, lstOps) = elaborateExpr env lstExpr
        let (lstPtrVal, lstCoerceOps) = coerceToPtrArg env lstVal
        let result = { Name = freshName env; Type = Ptr }
        (result, lstOps @ lstCoerceOps @ [LlvmCallOp(result, "@lang_array_of_list", [lstPtrVal])])

    // Phase 22: array_to_list — one-arg
    | App (Var ("array_to_list", _), arrExpr, _) ->
        let (arrVal, arrOps) = elaborateExpr env arrExpr
        let (arrPtrVal, arrCoerceOps) = coerceToPtrArg env arrVal
        let result = { Name = freshName env; Type = Ptr }
        (result, arrOps @ arrCoerceOps @ [LlvmCallOp(result, "@lang_array_to_list", [arrPtrVal])])

    // Phase 23: hashtable builtins
    // coerceToI64: converts a MlirValue to I64 if it isn't already (Ptr→I64, I1→I64)
    // Returns (coerced value, coercion ops list)

    // Phase 90: Unified hashtable_set — three-arg, keys passed as-is (tagged int or PtrToInt string)
    | App (App (App (Var ("hashtable_set", _), htExpr, _), keyExpr, _), valExpr, _) ->
        let (htVal,  htOps)  = elaborateExpr env htExpr
        let (keyVal, keyOps) = elaborateExpr env keyExpr
        let (valVal, valOps) = elaborateExpr env valExpr
        let (htPtr, htCoerce) = coerceToPtrArg env htVal
        let (keyI64, keyCoerce) = coerceToI64 env keyVal
        let (valI64, valCoerce) = coerceToI64 env valVal
        let (unitVal, callOps) = emitVoidCall env "@lang_hashtable_set" [htPtr; keyI64; valI64]
        (unitVal, htOps @ keyOps @ valOps @ htCoerce @ keyCoerce @ valCoerce @ callOps)

    // Phase 90: Unified hashtable_get — two-arg, key passed as-is
    | App (App (Var ("hashtable_get", _), htExpr, _), keyExpr, _) ->
        let (htVal,  htOps)  = elaborateExpr env htExpr
        let (keyVal, keyOps) = elaborateExpr env keyExpr
        let (htPtr, htCoerce) = coerceToPtrArg env htVal
        let (keyI64, keyCoerce) = coerceToI64 env keyVal
        let result = { Name = freshName env; Type = I64 }
        (result, htOps @ keyOps @ htCoerce @ keyCoerce @ [LlvmCallOp(result, "@lang_hashtable_get", [htPtr; keyI64])])

    // Phase 28: IndexGet — arr.[i] or ht.[key] via runtime dispatch
    // Phase 37: dispatch to _str variant when index is Ptr (string key)
    // Phase 66: dispatch to lang_string_char_at when collection is a known string
    | IndexGet (collExpr, idxExpr, _) ->
        let (collVal, collOps) = elaborateExpr env collExpr
        let (idxVal, idxOps) = elaborateExpr env idxExpr
        let rawResult = { Name = freshName env; Type = I64 }
        // Coerce collection to Ptr if it was loaded from a record slot (I64 → Ptr)
        let (collPtr, collCoerce) =
            if collVal.Type = Ptr then (collVal, [])
            else let v = { Name = freshName env; Type = Ptr } in (v, [LlvmIntToPtrOp(v, collVal)])
        // Phase 66: String char-at dispatch — s.[i] returns the byte at index i as i64
        if isStringExpr env.StringVars env.StringFields env.AnnotationMap collExpr then
            // Phase 92: C function now untags index and tags result internally
            (rawResult, collOps @ collCoerce @ idxOps @ [LlvmCallOp(rawResult, "@lang_string_char_at", [collPtr; idxVal])])
        else
        match idxVal.Type with
        | Ptr ->
            (rawResult, collOps @ collCoerce @ idxOps @ [LlvmCallOp(rawResult, "@lang_index_get_str", [collPtr; idxVal])])
        | _ ->
            // Phase 92: C function untags index internally
            (rawResult, collOps @ collCoerce @ idxOps @ [LlvmCallOp(rawResult, "@lang_index_get", [collPtr; idxVal])])

    // Phase 28: IndexSet — arr.[i] <- v or ht.[key] <- v via runtime dispatch
    // Phase 37: dispatch to _str variant when index is Ptr (string key); also handle Ptr-typed values via LlvmPtrToIntOp
    | IndexSet (collExpr, idxExpr, valExpr, _) ->
        let (collVal, collOps) = elaborateExpr env collExpr
        let (idxVal, idxOps) = elaborateExpr env idxExpr
        let (valVal, valOps) = elaborateExpr env valExpr
        // Phase 66: Coerce collection to Ptr if loaded as I64 from closure env
        let (collPtr, collCoerce) =
            if collVal.Type = Ptr then (collVal, [])
            else let v = { Name = freshName env; Type = Ptr } in (v, [LlvmIntToPtrOp(v, collVal)])
        let (valV, valCoerce) = coerceToI64 env valVal
        match idxVal.Type with
        | Ptr ->
            let (unitVal, callOps) = emitVoidCall env "@lang_index_set_str" [collPtr; idxVal; valV]
            (unitVal, collOps @ collCoerce @ idxOps @ valOps @ valCoerce @ callOps)
        | _ ->
            // Phase 92: C function untags index internally
            let (unitVal, callOps) = emitVoidCall env "@lang_index_set" [collPtr; idxVal; valV]
            (unitVal, collOps @ collCoerce @ idxOps @ valOps @ valCoerce @ callOps)

    // Phase 90: Unified hashtable_containsKey — two-arg, key passed as-is
    | App (App (Var ("hashtable_containsKey", _), htExpr, _), keyExpr, _) ->
        let (htVal,  htOps)  = elaborateExpr env htExpr
        let (keyVal, keyOps) = elaborateExpr env keyExpr
        let (htPtr, htCoerce) = coerceToPtrArg env htVal
        let (keyI64, keyCoerce) = coerceToI64 env keyVal
        let rawVal  = { Name = freshName env; Type = I64 }
        let zeroVal = { Name = freshName env; Type = I64 }
        let boolVal = { Name = freshName env; Type = I1  }
        let ops = htCoerce @ keyCoerce @ [
            LlvmCallOp(rawVal, "@lang_hashtable_containsKey", [htPtr; keyI64])
            ArithConstantOp(zeroVal, 0L)
            ArithCmpIOp(boolVal, "ne", rawVal, zeroVal)
        ]
        (boolVal, htOps @ keyOps @ ops)

    // Phase 90: Unified hashtable_remove — two-arg, key passed as-is
    | App (App (Var ("hashtable_remove", _), htExpr, _), keyExpr, _) ->
        let (htVal,  htOps)  = elaborateExpr env htExpr
        let (keyVal, keyOps) = elaborateExpr env keyExpr
        let (htPtr, htCoerce) = coerceToPtrArg env htVal
        let (keyI64, keyCoerce) = coerceToI64 env keyVal
        let (unitVal, callOps) = emitVoidCall env "@lang_hashtable_remove" [htPtr; keyI64]
        (unitVal, htOps @ keyOps @ htCoerce @ keyCoerce @ callOps)

    // Phase 90: Unified hashtable_keys — one-arg, single C function
    | App (Var ("hashtable_keys", _), htExpr, _) ->
        let (htVal, htOps) = elaborateExpr env htExpr
        let (htPtr, htCoerce) = coerceToPtrArg env htVal
        let result = { Name = freshName env; Type = Ptr }
        (result, htOps @ htCoerce @ [LlvmCallOp(result, "@lang_hashtable_keys", [htPtr])])

    // Phase 90: Unified hashtable_create — single C function
    | App (Var ("hashtable_create", _), unitExpr, _appSpan) ->
        let (_uVal, uOps) = elaborateExpr env unitExpr
        let result = { Name = freshName env; Type = Ptr }
        (result, uOps @ [LlvmCallOp(result, "@lang_hashtable_create", [])])

    // Phase 90: Unified hashtable_trygetvalue — two-arg, key passed as-is
    | App (App (Var ("hashtable_trygetvalue", _), htExpr, _), keyExpr, _) ->
        let (htVal,  htOps)  = elaborateExpr env htExpr
        let (keyVal, keyOps) = elaborateExpr env keyExpr
        let (htPtr, htCoerce) = coerceToPtrArg env htVal
        let (keyI64, keyCoerce) = coerceToI64 env keyVal
        let result = { Name = freshName env; Type = Ptr }
        (result, htOps @ keyOps @ htCoerce @ keyCoerce @ [LlvmCallOp(result, "@lang_hashtable_trygetvalue", [htPtr; keyI64])])

    // Phase 92: hashtable_count — C wrapper returns tagged count
    | App (Var ("hashtable_count", _), htExpr, _) ->
        let (htVal, htOps) = elaborateExpr env htExpr
        let (htPtr, htCoerce) = coerceToPtrArg env htVal
        let result = { Name = freshName env; Type = I64 }
        (result, htOps @ htCoerce @ [LlvmCallOp(result, "@lang_hashtable_count", [htPtr])])

    // Phase 24: array HOF builtins
    // array_fold — three-arg (must appear before two-arg patterns)
    // array_fold closure init arr: coerce closure to Ptr, coerce init to I64, call lang_array_fold → I64
    | App (App (App (Var ("array_fold", _), closureExpr, _), initExpr, _), arrExpr, _) ->
        let (fVal,   fOps)   = elaborateExpr env closureExpr
        let (initVal, initOps) = elaborateExpr env initExpr
        let (arrVal, arrOps) = elaborateExpr env arrExpr
        let (closurePtrVal, closureOps) = coerceToPtrArg env fVal
        let (initV, initCoerce) = coerceToI64 env initVal
        let (arrPtrVal, arrCoerceOps) = coerceToPtrArg env arrVal
        let result = { Name = freshName env; Type = I64 }
        (result, fOps @ closureOps @ initOps @ initCoerce @ arrOps @ arrCoerceOps @ [LlvmCallOp(result, "@lang_array_fold", [closurePtrVal; initV; arrPtrVal])])

    // array_iter — two-arg
    // array_iter closure arr: coerce closure to Ptr, call lang_array_iter (void), return unit
    | App (App (Var ("array_iter", _), closureExpr, _), arrExpr, _) ->
        let (fVal,   fOps)   = elaborateExpr env closureExpr
        let (arrVal, arrOps) = elaborateExpr env arrExpr
        let (closurePtrVal, closureOps) = coerceToPtrArg env fVal
        let (arrPtrVal, arrCoerceOps) = coerceToPtrArg env arrVal
        let (unitVal, callOps) = emitVoidCall env "@lang_array_iter" [closurePtrVal; arrPtrVal]
        (unitVal, fOps @ closureOps @ arrOps @ arrCoerceOps @ callOps)

    // array_map — two-arg
    // array_map closure arr: coerce closure to Ptr, call lang_array_map → Ptr
    | App (App (Var ("array_map", _), closureExpr, _), arrExpr, _) ->
        let (fVal,   fOps)   = elaborateExpr env closureExpr
        let (arrVal, arrOps) = elaborateExpr env arrExpr
        let (closurePtrVal, closureOps) = coerceToPtrArg env fVal
        let (arrPtrVal, arrCoerceOps) = coerceToPtrArg env arrVal
        let result = { Name = freshName env; Type = Ptr }
        (result, fOps @ closureOps @ arrOps @ arrCoerceOps @ [LlvmCallOp(result, "@lang_array_map", [closurePtrVal; arrPtrVal])])

    // Phase 92: array_init — C function untags count internally
    | App (App (Var ("array_init", _), nExpr, _), closureExpr, _) ->
        let (nVal,   nOps)   = elaborateExpr env nExpr
        let (fVal,   fOps)   = elaborateExpr env closureExpr
        let (nV, nCoerce) = coerceToI64 env nVal
        let (closurePtrVal, closureOps) = coerceToPtrArg env fVal
        let result = { Name = freshName env; Type = Ptr }
        (result, nOps @ nCoerce @ fOps @ closureOps @ [LlvmCallOp(result, "@lang_array_init", [nV; closurePtrVal])])

    // list_sort_by — two-arg curried with closure coercion (mirrors array_map pattern)
    | App (App (Var ("list_sort_by", _), closureExpr, _), listExpr, _) ->
        let (fVal,    fOps)    = elaborateExpr env closureExpr
        let (listVal, listOps) = elaborateExpr env listExpr
        let (closurePtrVal, closureOps) = coerceToPtrArg env fVal
        let (listPtrVal, listCoerceOps) = coerceToPtrArg env listVal
        let result = { Name = freshName env; Type = Ptr }
        (result, fOps @ closureOps @ listOps @ listCoerceOps @ [LlvmCallOp(result, "@lang_list_sort_by", [closurePtrVal; listPtrVal])])

    // list_of_seq — one-arg identity pass-through
    | App (Var ("list_of_seq", _), seqExpr, _) ->
        let (seqVal, seqOps) = elaborateExpr env seqExpr
        let (seqPtr, coerceOps) = coerceToPtrArg env seqVal
        let result = { Name = freshName env; Type = Ptr }
        (result, seqOps @ coerceOps @ [LlvmCallOp(result, "@lang_list_of_seq", [seqPtr])])

    // Phase 32-03: array_sort — one-arg, void return (mirrors array_iter pattern)
    | App (Var ("array_sort", _), arrExpr, _) ->
        let (arrVal, arrOps) = elaborateExpr env arrExpr
        let (arrPtrVal, arrCoerceOps) = coerceToPtrArg env arrVal
        let (unitVal, callOps) = emitVoidCall env "@lang_array_sort" [arrPtrVal]
        (unitVal, arrOps @ arrCoerceOps @ callOps)

    // Phase 32-03: array_of_seq — one-arg returning Ptr
    | App (Var ("array_of_seq", _), seqExpr, _) ->
        let (seqVal, seqOps) = elaborateExpr env seqExpr
        let (seqPtrVal, seqCoerceOps) = coerceToPtrArg env seqVal
        let result = { Name = freshName env; Type = Ptr }
        (result, seqOps @ seqCoerceOps @ [LlvmCallOp(result, "@lang_array_of_seq", [seqPtrVal])])

    // Phase 33-01: COL-01 StringBuilder
    | App (Var ("stringbuilder_create", _), unitExpr, _) ->
        let (_uVal, uOps) = elaborateExpr env unitExpr
        let result = { Name = freshName env; Type = Ptr }
        (result, uOps @ [LlvmCallOp(result, "@lang_sb_create", [])])

    | App (App (Var ("stringbuilder_append", _), sbExpr, _), strExpr, _) ->
        let (sbVal,  sbOps)  = elaborateExpr env sbExpr
        let (strVal, strOps) = elaborateExpr env strExpr
        let (sbPtr,  sbCoerce)  = coerceToPtrArg env sbVal
        let (strPtr, strCoerce) = coerceToPtrArg env strVal
        let result = { Name = freshName env; Type = Ptr }
        (result, sbOps @ strOps @ sbCoerce @ strCoerce @ [LlvmCallOp(result, "@lang_sb_append", [sbPtr; strPtr])])

    | App (Var ("stringbuilder_tostring", _), sbExpr, _) ->
        let (sbVal, sbOps) = elaborateExpr env sbExpr
        let (sbPtr, sbCoerce) = coerceToPtrArg env sbVal
        let result = { Name = freshName env; Type = Ptr }
        (result, sbOps @ sbCoerce @ [LlvmCallOp(result, "@lang_sb_tostring", [sbPtr])])

    // Phase 33-01: COL-02 HashSet
    | App (Var ("hashset_create", _), unitExpr, _) ->
        let (_uVal, uOps) = elaborateExpr env unitExpr
        let result = { Name = freshName env; Type = Ptr }
        (result, uOps @ [LlvmCallOp(result, "@lang_hashset_create", [])])

    // Phase 91: HashSet values passed as-is (tagged) — unified LSB dispatch in C
    | App (App (Var ("hashset_add", _), hsExpr, _), valExpr, _) ->
        let (hsVal,  hsOps)  = elaborateExpr env hsExpr
        let (valVal, valOps) = elaborateExpr env valExpr
        let (hsPtr, hsCoerce) = coerceToPtrArg env hsVal
        let (valI64, valCoerce) = coerceToI64 env valVal
        let result = { Name = freshName env; Type = I64 }
        (result, hsOps @ valOps @ hsCoerce @ valCoerce @ [LlvmCallOp(result, "@lang_hashset_add", [hsPtr; valI64])])

    | App (App (Var ("hashset_contains", _), hsExpr, _), valExpr, _) ->
        let (hsVal,  hsOps)  = elaborateExpr env hsExpr
        let (valVal, valOps) = elaborateExpr env valExpr
        let (hsPtr, hsCoerce) = coerceToPtrArg env hsVal
        let (valI64, valCoerce) = coerceToI64 env valVal
        let rawResult = { Name = freshName env; Type = I64 }
        let zeroVal   = { Name = freshName env; Type = I64 }
        let boolResult = { Name = freshName env; Type = I1  }
        (boolResult, hsOps @ valOps @ hsCoerce @ valCoerce @ [
            LlvmCallOp(rawResult, "@lang_hashset_contains", [hsPtr; valI64])
            ArithConstantOp(zeroVal, 0L)
            ArithCmpIOp(boolResult, "ne", rawResult, zeroVal)
        ])

    | App (Var ("hashset_count", _), hsExpr, _) ->
        let (hsVal, hsOps) = elaborateExpr env hsExpr
        let (hsPtr, hsCoerce) = coerceToPtrArg env hsVal
        let result = { Name = freshName env; Type = I64 }
        (result, hsOps @ hsCoerce @ [LlvmCallOp(result, "@lang_hashset_count", [hsPtr])])

    | App (Var ("hashset_keys", _), hsExpr, _) ->
        let (hsVal, hsOps) = elaborateExpr env hsExpr
        let (hsPtr, hsCoerce) = coerceToPtrArg env hsVal
        let result = { Name = freshName env; Type = Ptr }
        (result, hsOps @ hsCoerce @ [LlvmCallOp(result, "@lang_hashset_keys", [hsPtr])])

    // Phase 33-02: COL-03 Queue
    | App (Var ("queue_create", _), unitExpr, _) ->
        let (_uVal, uOps) = elaborateExpr env unitExpr
        let result = { Name = freshName env; Type = Ptr }
        (result, uOps @ [LlvmCallOp(result, "@lang_queue_create", [])])

    | App (App (Var ("queue_enqueue", _), qExpr, _), valExpr, _) ->
        let (qVal,   qOps)   = elaborateExpr env qExpr
        let (valVal, valOps) = elaborateExpr env valExpr
        let (qPtr, qCoerce) = coerceToPtrArg env qVal
        let (unitVal, callOps) = emitVoidCall env "@lang_queue_enqueue" [qPtr; valVal]
        (unitVal, qOps @ valOps @ qCoerce @ callOps)

    | App (App (Var ("queue_dequeue", _), qExpr, _), unitExpr, _) ->
        let (qVal,  qOps) = elaborateExpr env qExpr
        let (_uVal, uOps) = elaborateExpr env unitExpr
        let (qPtr, qCoerce) = coerceToPtrArg env qVal
        let result = { Name = freshName env; Type = I64 }
        (result, qOps @ uOps @ qCoerce @ [LlvmCallOp(result, "@lang_queue_dequeue", [qPtr])])

    | App (Var ("queue_count", _), qExpr, _) ->
        let (qVal, qOps) = elaborateExpr env qExpr
        let (qPtr, qCoerce) = coerceToPtrArg env qVal
        // Phase 92: C function now returns tagged result
        let result = { Name = freshName env; Type = I64 }
        (result, qOps @ qCoerce @ [LlvmCallOp(result, "@lang_queue_count", [qPtr])])

    // Phase 33-02: COL-04 MutableList
    // NOTE: mutablelist_set (three-arg) MUST appear BEFORE mutablelist_get (two-arg)
    | App (App (App (Var ("mutablelist_set", _), mlExpr, _), idxExpr, _), valExpr, _) ->
        let (mlVal,  mlOps)  = elaborateExpr env mlExpr
        let (idxVal, idxOps) = elaborateExpr env idxExpr
        let (valVal, valOps) = elaborateExpr env valExpr
        let (mlPtr, mlCoerce) = coerceToPtrArg env mlVal
        // Phase 92: C function now untags index internally
        let (unitVal, callOps) = emitVoidCall env "@lang_mlist_set" [mlPtr; idxVal; valVal]
        (unitVal, mlOps @ idxOps @ valOps @ mlCoerce @ callOps)

    | App (App (Var ("mutablelist_get", _), mlExpr, _), idxExpr, _) ->
        let (mlVal,  mlOps)  = elaborateExpr env mlExpr
        let (idxVal, idxOps) = elaborateExpr env idxExpr
        let (mlPtr, mlCoerce) = coerceToPtrArg env mlVal
        // Phase 92: C function now untags index internally
        let result = { Name = freshName env; Type = I64 }
        (result, mlOps @ idxOps @ mlCoerce @ [LlvmCallOp(result, "@lang_mlist_get", [mlPtr; idxVal])])

    | App (Var ("mutablelist_create", _), unitExpr, _) ->
        let (_uVal, uOps) = elaborateExpr env unitExpr
        let result = { Name = freshName env; Type = Ptr }
        (result, uOps @ [LlvmCallOp(result, "@lang_mlist_create", [])])

    | App (App (Var ("mutablelist_add", _), mlExpr, _), valExpr, _) ->
        let (mlVal,  mlOps)  = elaborateExpr env mlExpr
        let (valVal, valOps) = elaborateExpr env valExpr
        let (mlPtr, mlCoerce) = coerceToPtrArg env mlVal
        let (unitVal, callOps) = emitVoidCall env "@lang_mlist_add" [mlPtr; valVal]
        (unitVal, mlOps @ valOps @ mlCoerce @ callOps)

    | App (Var ("mutablelist_count", _), mlExpr, _) ->
        let (mlVal, mlOps) = elaborateExpr env mlExpr
        let (mlPtr, mlCoerce) = coerceToPtrArg env mlVal
        let result = { Name = freshName env; Type = I64 }
        (result, mlOps @ mlCoerce @ [LlvmCallOp(result, "@lang_mlist_count", [mlPtr])])

    | App (Var ("mutablelist_tolist", _), mlExpr, _) ->
        let (mlVal, mlOps) = elaborateExpr env mlExpr
        let (mlPtr, mlCoerce) = coerceToPtrArg env mlVal
        let result = { Name = freshName env; Type = Ptr }
        (result, mlOps @ mlCoerce @ [LlvmCallOp(result, "@lang_mlist_to_list", [mlPtr])])

    // write_file — two-arg, void return
    | App (App (Var ("write_file", _), pathExpr, _), contentExpr, _) ->
        let (pathVal,    pathOps)    = elaborateExpr env pathExpr
        let (contentVal, contentOps) = elaborateExpr env contentExpr
        let (pathPtr, pathCast) = coerceToPtrArg env pathVal
        let (contentPtr, contentCast) = coerceToPtrArg env contentVal
        let (unitVal, callOps) = emitVoidCall env "@lang_file_write" [pathPtr; contentPtr]
        (unitVal, pathOps @ contentOps @ pathCast @ contentCast @ callOps)

    // append_file — two-arg, void return (identical shape to write_file)
    | App (App (Var ("append_file", _), pathExpr, _), contentExpr, _) ->
        let (pathVal,    pathOps)    = elaborateExpr env pathExpr
        let (contentVal, contentOps) = elaborateExpr env contentExpr
        let (pathPtr, pathCast) = coerceToPtrArg env pathVal
        let (contentPtr, contentCast) = coerceToPtrArg env contentVal
        let (unitVal, callOps) = emitVoidCall env "@lang_file_append" [pathPtr; contentPtr]
        (unitVal, pathOps @ contentOps @ pathCast @ contentCast @ callOps)

    // read_file — one-arg, returns Ptr (LangString*)
    | App (Var ("read_file", _), pathExpr, _) ->
        let (pathVal, pathOps) = elaborateExpr env pathExpr
        let (ptrVal, castOps) = coerceToPtrArg env pathVal
        let result = { Name = freshName env; Type = Ptr }
        (result, pathOps @ castOps @ [LlvmCallOp(result, "@lang_file_read", [ptrVal])])

    // file_exists — one-arg, returns bool (I1 via I64 comparison)
    | App (Var ("file_exists", _), pathExpr, _) ->
        let (pathVal, pathOps) = elaborateExpr env pathExpr
        let (ptrVal, castOps) = coerceToPtrArg env pathVal
        let rawVal  = { Name = freshName env; Type = I64 }
        let zeroVal = { Name = freshName env; Type = I64 }
        let boolVal = { Name = freshName env; Type = I1  }
        let ops = [
            LlvmCallOp(rawVal, "@lang_file_exists", [ptrVal])
            ArithConstantOp(zeroVal, 0L)
            ArithCmpIOp(boolVal, "ne", rawVal, zeroVal)
        ]
        (boolVal, pathOps @ castOps @ ops)

    // dbg — pass-through debug: prints "[file:line] value" to stderr, returns value unchanged
    | App (Var ("dbg", _), argExpr, dbgSpan) ->
        let (argVal, argOps) = elaborateExpr env argExpr
        // Build "[file:line] " prefix as LangString
        let locStr = sprintf "[%s:%d] " dbgSpan.FileName dbgSpan.StartLine
        let (locVal, locOps) = elaborateExpr env (String(locStr, dbgSpan))
        // Convert argVal to displayable string via lang_to_string_int
        // Phase 88: Untag I64 before passing to C
        let strVal = { Name = freshName env; Type = Ptr }
        let toStrOps =
            if argVal.Type = Ptr then
                let i64Val = { Name = freshName env; Type = I64 }
                [LlvmPtrToIntOp(i64Val, argVal); LlvmCallOp(strVal, "@lang_to_string_int", [i64Val])]
            else
                // Phase 92: C function now untags internally
                [LlvmCallOp(strVal, "@lang_to_string_int", [argVal])]
        // eprint("[file:line] "); eprintln(to_string(arg)); return original value
        (argVal, argOps @ locOps @ toStrOps @ [LlvmCallVoidOp("@lang_eprint", [locVal]); LlvmCallVoidOp("@lang_eprintln", [strVal])])

    // eprint — one-arg, void return
    | App (Var ("eprint", _), strExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let (ptrVal, castOps) = coerceToPtrArg env strVal
        let (unitVal, callOps) = emitVoidCall env "@lang_eprint" [ptrVal]
        (unitVal, strOps @ castOps @ callOps)

    // eprintln — one-arg, void return
    | App (Var ("eprintln", _), strExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let (ptrVal, castOps) = coerceToPtrArg env strVal
        let (unitVal, callOps) = emitVoidCall env "@lang_eprintln" [ptrVal]
        (unitVal, strOps @ castOps @ callOps)

    // Phase 39: printfn — 2-arg case (MUST come before 1-arg case)
    | App (App (App (Var ("printfn", _), String (fmt, _), _), arg1Expr, _), arg2Expr, s) ->
        let sprintfExpr = App(App(App(Var("sprintf", s), String(fmt, s), s), arg1Expr, s), arg2Expr, s)
        elaborateExpr env (App(Var("println", s), sprintfExpr, s))

    // Phase 39: printfn — 1-arg case (MUST come before 0-arg case)
    | App (App (Var ("printfn", _), String (fmt, _), _), argExpr, s) ->
        let sprintfExpr = App(App(Var("sprintf", s), String(fmt, s), s), argExpr, s)
        elaborateExpr env (App(Var("println", s), sprintfExpr, s))

    // Phase 39: printfn — 0-arg case: printfn "literal" (desugar to println "literal")
    | App (Var ("printfn", _), String (fmt, _), s) ->
        elaborateExpr env (App(Var("println", s), String(fmt, s), s))

    // Phase 39: sprintf — 2-arg case (MUST come BEFORE 1-arg cases — see Pitfall 1)
    | App (App (App (Var ("sprintf", _), String (fmt, _), _), arg1Expr, _), arg2Expr, sprintfSpan)
        when (let specs = fmtSpecTypes fmt in specs.Length = 2) ->
        let specs = fmtSpecTypes fmt
        let fmtGlobal  = addStringGlobal env fmt
        let fmtPtrVal  = { Name = freshName env; Type = Ptr }
        let (arg1Val, arg1Ops) = elaborateExpr env arg1Expr
        let (arg2Val, arg2Ops) = elaborateExpr env arg2Expr
        let result = { Name = freshName env; Type = Ptr }
        match specs with
        | [IntSpec; IntSpec] ->
            let (a1, c1) = coerceToI64Arg env arg1Val
            let (a2, c2) = coerceToI64Arg env arg2Val
            let ops = [ LlvmAddressOfOp(fmtPtrVal, fmtGlobal); LlvmCallOp(result, "@lang_sprintf_2ii", [fmtPtrVal; a1; a2]) ]
            (result, arg1Ops @ arg2Ops @ c1 @ c2 @ ops)
        | [StrSpec; IntSpec] ->
            let (a1Ptr, c1) = coerceToPtrArg env arg1Val
            let dp1 = { Name = freshName env; Type = Ptr }
            let da1 = { Name = freshName env; Type = Ptr }
            let (a2, c2) = coerceToI64Arg env arg2Val
            let ops = [
                LlvmAddressOfOp(fmtPtrVal, fmtGlobal)
                LlvmGEPStructOp(dp1, a1Ptr, 2); LlvmLoadOp(da1, dp1)
                LlvmCallOp(result, "@lang_sprintf_2si", [fmtPtrVal; da1; a2])
            ]
            (result, arg1Ops @ arg2Ops @ c1 @ c2 @ ops)
        | [IntSpec; StrSpec] ->
            let (a1, c1) = coerceToI64Arg env arg1Val
            let (a2Ptr, c2) = coerceToPtrArg env arg2Val
            let dp2 = { Name = freshName env; Type = Ptr }
            let da2 = { Name = freshName env; Type = Ptr }
            let ops = [
                LlvmAddressOfOp(fmtPtrVal, fmtGlobal)
                LlvmGEPStructOp(dp2, a2Ptr, 2); LlvmLoadOp(da2, dp2)
                LlvmCallOp(result, "@lang_sprintf_2is", [fmtPtrVal; a1; da2])
            ]
            (result, arg1Ops @ arg2Ops @ c1 @ c2 @ ops)
        | [StrSpec; StrSpec] ->
            let (a1Ptr, c1) = coerceToPtrArg env arg1Val
            let (a2Ptr, c2) = coerceToPtrArg env arg2Val
            let dp1 = { Name = freshName env; Type = Ptr }
            let da1 = { Name = freshName env; Type = Ptr }
            let dp2 = { Name = freshName env; Type = Ptr }
            let da2 = { Name = freshName env; Type = Ptr }
            let ops = [
                LlvmAddressOfOp(fmtPtrVal, fmtGlobal)
                LlvmGEPStructOp(dp1, a1Ptr, 2); LlvmLoadOp(da1, dp1)
                LlvmGEPStructOp(dp2, a2Ptr, 2); LlvmLoadOp(da2, dp2)
                LlvmCallOp(result, "@lang_sprintf_2ss", [fmtPtrVal; da1; da2])
            ]
            (result, arg1Ops @ arg2Ops @ c1 @ c2 @ ops)
        | _ -> failWithSpan sprintfSpan "sprintf: unsupported 2-arg specifier combo in '%s'" fmt

    // Phase 39: sprintf — 1-arg integer case (%d, %x, %02x, %c, etc.)
    | App (App (Var ("sprintf", _), String (fmt, _), _), argExpr, _)
        when (let specs = fmtSpecTypes fmt in specs.Length = 1 && specs.[0] = IntSpec) ->
        let fmtGlobal = addStringGlobal env fmt
        let fmtPtrVal = { Name = freshName env; Type = Ptr }
        let (argVal, argOps) = elaborateExpr env argExpr
        let (i64Val, coerceOps) = coerceToI64Arg env argVal
        let result = { Name = freshName env; Type = Ptr }
        let ops = [
            LlvmAddressOfOp(fmtPtrVal, fmtGlobal)
            LlvmCallOp(result, "@lang_sprintf_1i", [fmtPtrVal; i64Val])
        ]
        (result, argOps @ coerceOps @ ops)

    // Phase 39: sprintf — 1-arg string case (%s)
    | App (App (Var ("sprintf", _), String (fmt, _), _), argExpr, _)
        when (let specs = fmtSpecTypes fmt in specs.Length = 1 && specs.[0] = StrSpec) ->
        let fmtGlobal   = addStringGlobal env fmt
        let fmtPtrVal   = { Name = freshName env; Type = Ptr }
        let (argVal, argOps) = elaborateExpr env argExpr
        let (argPtr, coerce) = coerceToPtrArg env argVal
        let dataPtrVal  = { Name = freshName env; Type = Ptr }
        let dataAddrVal = { Name = freshName env; Type = Ptr }
        let result      = { Name = freshName env; Type = Ptr }
        let ops = [
            LlvmAddressOfOp(fmtPtrVal, fmtGlobal)
            LlvmGEPStructOp(dataPtrVal, argPtr, 2)
            LlvmLoadOp(dataAddrVal, dataPtrVal)
            LlvmCallOp(result, "@lang_sprintf_1s", [fmtPtrVal; dataAddrVal])
        ]
        (result, argOps @ coerce @ ops)

    // eprintfn — two-arg case: eprintfn "%s" str  (MUST come before one-arg case)
    | App (App (Var ("eprintfn", _), String (fmt, _), _), argExpr, _) when fmt = "%s" ->
        let (argVal, argOps) = elaborateExpr env argExpr
        let (ptrVal, castOps) = coerceToPtrArg env argVal
        let (unitVal, callOps) = emitVoidCall env "@lang_eprintln" [ptrVal]
        (unitVal, argOps @ castOps @ callOps)

    // eprintfn — one-arg case: eprintfn "literal" (desugar to eprintln "literal")
    | App (Var ("eprintfn", _), String (fmt, _), s) ->
        elaborateExpr env (App(Var("eprintln", s), String(fmt, s), s))

    // Phase 27: write_lines — two-arg, void return (MUST come before one-arg arms)
    | App (App (Var ("write_lines", _), pathExpr, _), linesExpr, _) ->
        let (pathVal,  pathOps)  = elaborateExpr env pathExpr
        let (linesVal, linesOps) = elaborateExpr env linesExpr
        let (pathPtr, pathCast) = coerceToPtrArg env pathVal
        let (linesPtr, linesCast) = coerceToPtrArg env linesVal
        let (unitVal, callOps) = emitVoidCall env "@lang_write_lines" [pathPtr; linesPtr]
        (unitVal, pathOps @ linesOps @ pathCast @ linesCast @ callOps)

    // Phase 27: path_combine — two-arg, returns Ptr (MUST come before one-arg arms)
    | App (App (Var ("path_combine", _), dirExpr, _), fileExpr, _) ->
        let (dirVal,  dirOps)  = elaborateExpr env dirExpr
        let (fileVal, fileOps) = elaborateExpr env fileExpr
        let (dirPtr, dirCast) = coerceToPtrArg env dirVal
        let (filePtr, fileCast) = coerceToPtrArg env fileVal
        let result = { Name = freshName env; Type = Ptr }
        (result, dirOps @ fileOps @ dirCast @ fileCast @ [LlvmCallOp(result, "@lang_path_combine", [dirPtr; filePtr])])

    // Phase 27: read_lines — one-arg, returns Ptr
    | App (Var ("read_lines", _), pathExpr, _) ->
        let (pathVal, pathOps) = elaborateExpr env pathExpr
        let (ptrVal, castOps) = coerceToPtrArg env pathVal
        let result = { Name = freshName env; Type = Ptr }
        (result, pathOps @ castOps @ [LlvmCallOp(result, "@lang_read_lines", [ptrVal])])

    // Phase 27: get_env — one-arg, returns Ptr
    | App (Var ("get_env", _), nameExpr, _) ->
        let (nameVal, nameOps) = elaborateExpr env nameExpr
        let (ptrVal, castOps) = coerceToPtrArg env nameVal
        let result = { Name = freshName env; Type = Ptr }
        (result, nameOps @ castOps @ [LlvmCallOp(result, "@lang_get_env", [ptrVal])])

    // Phase 27: dir_files — one-arg, returns Ptr
    | App (Var ("dir_files", _), pathExpr, _) ->
        let (pathVal, pathOps) = elaborateExpr env pathExpr
        let (ptrVal, castOps) = coerceToPtrArg env pathVal
        let result = { Name = freshName env; Type = Ptr }
        (result, pathOps @ castOps @ [LlvmCallOp(result, "@lang_dir_files", [ptrVal])])

    // Phase 27: stdin_read_line — unit-arg, returns Ptr (elaborate unit, discard, call with no args)
    | App (Var ("stdin_read_line", _), unitExpr, _) ->
        let (_uVal, uOps) = elaborateExpr env unitExpr
        let result = { Name = freshName env; Type = Ptr }
        (result, uOps @ [LlvmCallOp(result, "@lang_stdin_read_line", [])])

    // Phase 27: stdin_read_all — unit-arg, returns Ptr
    | App (Var ("stdin_read_all", _), unitExpr, _) ->
        let (_uVal, uOps) = elaborateExpr env unitExpr
        let result = { Name = freshName env; Type = Ptr }
        (result, uOps @ [LlvmCallOp(result, "@lang_stdin_read_all", [])])

    // Phase 27: get_cwd — unit-arg, returns Ptr
    | App (Var ("get_cwd", _), unitExpr, _) ->
        let (_uVal, uOps) = elaborateExpr env unitExpr
        let result = { Name = freshName env; Type = Ptr }
        (result, uOps @ [LlvmCallOp(result, "@lang_get_cwd", [])])

    // Phase 38: get_args — unit-arg, returns Ptr (LangCons* list of CLI arguments starting from argv[1])
    | App (Var ("get_args", _), unitExpr, _) ->
        let (_uVal, uOps) = elaborateExpr env unitExpr
        let result = { Name = freshName env; Type = Ptr }
        (result, uOps @ [LlvmCallOp(result, "@lang_get_args", [])])

    // Phase 14/67: char_to_int/int_to_char moved to Prelude/Core.fun as identity functions

    // Phase 31: char_is_digit — returns bool (bool-wrapping pattern)
    | App (Var ("char_is_digit", _), charExpr, _) ->
        emitCharPredicate env "@lang_char_is_digit" charExpr elaborateExpr

    // Phase 31: char_is_letter — returns bool (bool-wrapping pattern)
    | App (Var ("char_is_letter", _), charExpr, _) ->
        emitCharPredicate env "@lang_char_is_letter" charExpr elaborateExpr

    // Phase 31: char_is_upper — returns bool (bool-wrapping pattern)
    | App (Var ("char_is_upper", _), charExpr, _) ->
        emitCharPredicate env "@lang_char_is_upper" charExpr elaborateExpr

    // Phase 31: char_is_lower — returns bool (bool-wrapping pattern)
    | App (Var ("char_is_lower", _), charExpr, _) ->
        emitCharPredicate env "@lang_char_is_lower" charExpr elaborateExpr

    // Phase 31: char_to_upper — pass-through (returns i64 char code)
    | App (Var ("char_to_upper", _), charExpr, _) ->
        let (charVal, charOps) = elaborateExpr env charExpr
        // Phase 92: C function now untags input and tags result internally
        let result = { Name = freshName env; Type = I64 }
        (result, charOps @ [LlvmCallOp(result, "@lang_char_to_upper", [charVal])])

    // Phase 31: char_to_lower — pass-through (returns i64 char code)
    | App (Var ("char_to_lower", _), charExpr, _) ->
        let (charVal, charOps) = elaborateExpr env charExpr
        // Phase 92: C function now untags input and tags result internally
        let result = { Name = freshName env; Type = I64 }
        (result, charOps @ [LlvmCallOp(result, "@lang_char_to_lower", [charVal])])

    // Phase 7: print/println builtins — static literal fast path (keep before general case)
    | App (Var ("print", _), String (s, _), _) ->
        let globalName = addStringGlobal env s
        let ptrVal  = { Name = freshName env; Type = Ptr }
        let fmtRes  = { Name = freshName env; Type = I32 }
        let unitVal = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmAddressOfOp(ptrVal, globalName)
            LlvmCallOp(fmtRes, "@printf", [ptrVal])
            ArithConstantOp(unitVal, 1L)
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
            ArithConstantOp(unitVal, 1L)
        ]
        (unitVal, ops)

    // Phase 8: general print/println for string struct variables (Ptr type)
    // Phase 27: if strVal has I64 type (e.g., extracted from list cons cell head), cast to Ptr first
    | App (Var ("print", _), strExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let (ptrVal, castOps) =
            if strVal.Type = I64 then
                let p = { Name = freshName env; Type = Ptr }
                (p, [LlvmIntToPtrOp(p, strVal)])
            else
                (strVal, [])
        let dataPtrVal  = { Name = freshName env; Type = Ptr }
        let dataAddrVal = { Name = freshName env; Type = Ptr }
        let fmtRes      = { Name = freshName env; Type = I32 }
        let unitVal     = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmGEPStructOp(dataPtrVal, ptrVal, 2)
            LlvmLoadOp(dataAddrVal, dataPtrVal)
            LlvmCallOp(fmtRes, "@printf", [dataAddrVal])
            ArithConstantOp(unitVal, 1L)
        ]
        (unitVal, strOps @ castOps @ ops)

    | App (Var ("println", _), strExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let (ptrVal, castOps) =
            if strVal.Type = I64 then
                let p = { Name = freshName env; Type = Ptr }
                (p, [LlvmIntToPtrOp(p, strVal)])
            else
                (strVal, [])
        let dataPtrVal  = { Name = freshName env; Type = Ptr }
        let dataAddrVal = { Name = freshName env; Type = Ptr }
        let nlGlobal    = addStringGlobal env "\n"
        let nlPtrVal    = { Name = freshName env; Type = Ptr }
        let fmtRes1     = { Name = freshName env; Type = I32 }
        let fmtRes2     = { Name = freshName env; Type = I32 }
        let unitVal     = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmGEPStructOp(dataPtrVal, ptrVal, 2)
            LlvmLoadOp(dataAddrVal, dataPtrVal)
            LlvmCallOp(fmtRes1, "@printf", [dataAddrVal])
            LlvmAddressOfOp(nlPtrVal, nlGlobal)
            LlvmCallOp(fmtRes2, "@printf", [nlPtrVal])
            ArithConstantOp(unitVal, 1L)
        ]
        (unitVal, strOps @ castOps @ ops)

    // Phase 59: Nested qualified function call — Outer.Inner.f arg → App(Var("Outer_Inner_f"), arg)
    // Handles chains of any depth. Must come BEFORE the single-level Constructor arm below.
    | App(FieldAccess(FieldAccess(_, _, _) as innerExpr, memberName, fspan), argExpr, span)
        when (tryDecodeModulePath innerExpr).IsSome
          && not (Map.containsKey memberName env.TypeEnv) ->
        let segments = (tryDecodeModulePath innerExpr).Value @ [memberName]
        let varName = segments |> String.concat "_"
        elaborateExpr env (App(Var(varName, fspan), argExpr, span))

    // Phase 25: Qualified function call desugar — M.f arg → App(Var("f"), arg)
    // Only for non-constructor members (constructors are handled by the FieldAccess arm → Constructor node).
    // Phase 35: Use module-qualified name (modName + "_" + memberName) — matches flattenDecls prefixing.
    // Must come BEFORE the general App arm so direct-call dispatch applies.
    | App (FieldAccess (Constructor (modName, None, _), memberName, fspan), argExpr, span)
        when not (Map.containsKey memberName env.TypeEnv) ->
        elaborateExpr env (App (Var (modName + "_" + memberName, fspan), argExpr, span))

    | App (funcExpr, argExpr, appSpan) ->
        match funcExpr with
        | Var (name, _) ->
            match Map.tryFind name env.KnownFuncs with
            | Some sig_ when sig_.ClosureInfo.IsNone ->
                // DIRECT CALL (Phase 4 behavior) — known non-closure function
                let blocksBeforeArg = env.Blocks.Value.Length
                let (argVal, argOps) = elaborateExpr env argExpr
                let blocksAfterArg = env.Blocks.Value.Length
                // Coerce argument type to match function signature (e.g., Ptr→I64 for closure args).
                // This handles calls like `map closure_arg` where map expects I64 but closure is Ptr.
                let (coercedArgVal, coerceArgOps) =
                    match sig_.ParamTypes with
                    | [I64] when argVal.Type = Ptr ->
                        let coerced = { Name = freshName env; Type = I64 }
                        (coerced, [LlvmPtrToIntOp(coerced, argVal)])
                    | [I64] when argVal.Type = I1 ->
                        // Phase 88: zext I1→I64 then retag for tagged representation
                        let (coerced, coerceOps) = coerceToI64 env argVal
                        (coerced, coerceOps)
                    | [Ptr] when argVal.Type = I64 ->
                        let coerced = { Name = freshName env; Type = Ptr }
                        (coerced, [LlvmIntToPtrOp(coerced, argVal)])
                    | _ -> (argVal, [])
                let result = { Name = freshName env; Type = sig_.ReturnType }
                let contOps = coerceArgOps @ [DirectCallOp(result, sig_.MlirName, [coercedArgVal])]
                // Phase 66: If argOps ends with terminator (e.g. And/Or), continuation must
                // go into the arg's merge block, not appended after the terminator.
                match List.tryLast argOps with
                | Some op when isTerminatorOp op && blocksAfterArg > blocksBeforeArg ->
                    let targetIdx = blocksAfterArg - 1
                    appendToBlock env targetIdx contOps
                    (result, argOps)
                | _ ->
                    (result, argOps @ contOps)
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
                // Phase 64: Caller stores non-outerParam captures (SSA values are in scope here).
                // The maker handles fn_ptr (env[0]) and outerParam. We store the rest.
                // If any capture is not in scope (e.g., LetRec body), fall back to indirect call.
                let captureStoreResult =
                    ci.CaptureNames
                    |> List.mapi (fun i capName ->
                        if capName = ci.OuterParamName then Some []
                        else
                            match Map.tryFind capName env.Vars with
                            | Some capVal ->
                                let slotVal = { Name = freshName env; Type = Ptr }
                                if capVal.Type = I64 then
                                    let ptrVal = { Name = freshName env; Type = Ptr }
                                    Some [LlvmGEPLinearOp(slotVal, envPtrVal, i + 1)
                                          LlvmIntToPtrOp(ptrVal, capVal)
                                          LlvmStoreOp(ptrVal, slotVal)]
                                else
                                    Some [LlvmGEPLinearOp(slotVal, envPtrVal, i + 1)
                                          LlvmStoreOp(capVal, slotVal)]
                            | None -> None  // Capture not in scope — need fallback
                    )
                if captureStoreResult |> List.forall Option.isSome then
                    let captureStoreOps = captureStoreResult |> List.collect Option.get
                    let callOp = DirectCallOp(resultVal, sig_.MlirName, [i64ArgVal; envPtrVal])
                    (resultVal, argOps @ setupOps @ coerceOps @ captureStoreOps @ [callOp])
                else
                    // Fallback: captures not in scope at call site (e.g., LetRec body func.func).
                    // Phase 65: Load template env from global @__tenv_<name> (set at definition site).
                    // Clone template env, fill outerParam, load fn_ptr, then indirect call.
                    let globalName = "@__tenv_" + name.Replace(".", "_")
                    if List.contains globalName env.TplGlobals.Value then
                        // Load the template env ptr from the global variable
                        let globalAddrVal = { Name = freshName env; Type = Ptr }
                        let templatePtr   = { Name = freshName env; Type = Ptr }
                        let loadGlobalOps = [
                            LlvmAddressOfOp(globalAddrVal, globalName)
                            LlvmLoadOp(templatePtr, globalAddrVal)
                        ]
                        // Allocate a fresh env of the same size as the template
                        let copyBytesVal = { Name = freshName env; Type = I64 }
                        let newEnvPtr    = { Name = freshName env; Type = Ptr }
                        let copyOps = [
                            ArithConstantOp(copyBytesVal, int64 ((ci.NumCaptures + 1) * 8))
                            LlvmCallOp(newEnvPtr, "@GC_malloc", [copyBytesVal])
                        ]
                        // Copy all slots from template (slot 0 = fn_ptr, slots 1..n = captures)
                        let slotCopyOps =
                            [ 0 .. ci.NumCaptures ] |> List.collect (fun i ->
                                let srcSlot   = { Name = freshName env; Type = Ptr }
                                let loadedVal = { Name = freshName env; Type = Ptr }
                                let dstSlot   = { Name = freshName env; Type = Ptr }
                                [ LlvmGEPLinearOp(srcSlot, templatePtr, i)
                                  LlvmLoadOp(loadedVal, srcSlot)
                                  LlvmGEPLinearOp(dstSlot, newEnvPtr, i)
                                  LlvmStoreOp(loadedVal, dstSlot) ])
                        // Overwrite outerParam slot in new env with the actual call argument.
                        // Slot index = position of outerParam in CaptureNames (0-indexed) + 1 (slot 0 = fn_ptr).
                        let outerParamSlotIdx =
                            match List.tryFindIndex ((=) ci.OuterParamName) ci.CaptureNames with
                            | Some idx -> idx + 1
                            | None -> failwith (sprintf "Elaboration: outerParam '%s' not found in CaptureNames" ci.OuterParamName)
                        let outerSlotVal = { Name = freshName env; Type = Ptr }
                        let outerPtrVal  = { Name = freshName env; Type = Ptr }
                        let outerStoreOps =
                            [ LlvmGEPLinearOp(outerSlotVal, newEnvPtr, outerParamSlotIdx)
                              LlvmIntToPtrOp(outerPtrVal, i64ArgVal)
                              LlvmStoreOp(outerPtrVal, outerSlotVal) ]
                        // The cloned env IS the closure. Return it as Ptr (same as the maker would return).
                        // The next application (e.g., (combine3 n) 1) will do an IndirectCallOp via this closure.
                        (newEnvPtr, argOps @ coerceOps @ loadGlobalOps @ copyOps @ slotCopyOps @ outerStoreOps)
                    else
                        failwith (sprintf "Elaboration: '%s' has captures not in scope and no template env global available (Phase 65)" name)
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
                    // Collect all known names for "Did you mean?" suggestion
                    let builtinNames = [
                        "print"; "println"; "printfn"; "eprint"; "eprintln"; "eprintfn"; "sprintf"
                        "failwith"; "dbg"; "to_string"
                        "string_length"; "string_concat"; "string_sub"; "string_contains"
                        "string_startswith"; "string_endswith"; "string_trim"; "string_to_int"
                        "string_concat_list"; "string_split"; "string_indexof"; "string_replace"
                        "string_toupper"; "string_tolower"
                        "char_to_int"; "int_to_char"; "char_is_digit"; "char_is_letter"
                        "char_is_upper"; "char_is_lower"; "char_to_upper"; "char_to_lower"
                        "read_file"; "write_file"; "append_file"; "file_exists"
                        "read_lines"; "write_lines"; "stdin_read_line"; "stdin_read_all"
                        "get_env"; "get_cwd"; "path_combine"; "dir_files"; "get_args"
                        "array_create"; "array_init"; "array_get"; "array_set"; "array_length"
                        "array_of_list"; "array_to_list"; "array_iter"; "array_map"; "array_fold"
                        "array_sort"; "array_of_seq"
                        "hashtable_create"; "hashtable_get"; "hashtable_set"
                        "hashtable_containsKey"; "hashtable_keys"
                        "hashtable_remove"; "hashtable_trygetvalue"; "hashtable_count"
                        "stringbuilder_create"; "stringbuilder_append"; "stringbuilder_tostring"
                        "hashset_create"; "hashset_add"; "hashset_contains"; "hashset_count"; "hashset_keys"
                        "queue_create"; "queue_enqueue"; "queue_dequeue"; "queue_count"
                        "mutablelist_create"; "mutablelist_add"; "mutablelist_count"; "mutablelist_tolist"
                        "list_sort_by"; "list_of_seq"
                    ]
                    let allNames =
                        builtinNames @
                        (env.KnownFuncs |> Map.toList |> List.map fst) @
                        (env.Vars |> Map.toList |> List.map fst)
                        |> List.filter (fun n -> not (n.StartsWith("%")) && not (n.StartsWith("@")))
                    // Levenshtein edit distance for suggestions
                    let editDistance (s1: string) (s2: string) =
                        let m, n = s1.Length, s2.Length
                        let d = Array2D.init (m + 1) (n + 1) (fun i j -> if i = 0 then j elif j = 0 then i else 0)
                        for i in 1..m do
                            for j in 1..n do
                                let cost = if s1.[i-1] = s2.[j-1] then 0 else 1
                                d.[i, j] <- min (min (d.[i-1, j] + 1) (d.[i, j-1] + 1)) (d.[i-1, j-1] + cost)
                        d.[m, n]
                    // Convert internal Module_name to user-facing Module.name
                    let toUserName (n: string) =
                        let idx = n.IndexOf('_')
                        if idx > 0 && System.Char.IsUpper(n.[0]) then
                            n.[..idx-1] + "." + n.[idx+1..]
                        else n
                    let suggestions =
                        allNames
                        |> List.map (fun n -> (n, editDistance (name.ToLower()) (n.ToLower())))
                        |> List.filter (fun (_, d) -> d <= 3)
                        |> List.sortBy snd
                        |> List.truncate 3
                        |> List.map (fst >> toUserName)
                    let hint =
                        if suggestions.IsEmpty then ""
                        else sprintf "\n   Did you mean: %s?" (suggestions |> String.concat ", ")
                    failWithSpan appSpan "Elaboration: undefined function '%s'%s" name hint
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
            // Phase 88: Coerce arg to I64 for uniform closure ABI (I1→tagged I64, Ptr→I64)
            let (argI64, argCoerce) = coerceToI64 env argVal
            if funcVal.Type = Ptr then
                // Ptr-typed closure: load fn_ptr from slot 0, call indirect
                let fnPtrVal = { Name = freshName env; Type = Ptr }
                let result = { Name = freshName env; Type = I64 }
                let loadOp = LlvmLoadOp(fnPtrVal, funcVal)
                let callOp = IndirectCallOp(result, fnPtrVal, funcVal, argI64)
                (result, funcOps @ argOps @ argCoerce @ [loadOp; callOp])
            elif funcVal.Type = I64 then
                // I64-typed closure (passed through uniform ABI): inttoptr then indirect call
                let closurePtrVal = { Name = freshName env; Type = Ptr }
                let fnPtrVal = { Name = freshName env; Type = Ptr }
                let result = { Name = freshName env; Type = I64 }
                let castOp = LlvmIntToPtrOp(closurePtrVal, funcVal)
                let loadOp = LlvmLoadOp(fnPtrVal, closurePtrVal)
                let callOp = IndirectCallOp(result, fnPtrVal, closurePtrVal, argI64)
                (result, funcOps @ argOps @ argCoerce @ [castOp; loadOp; callOp])
            else
                failWithSpan appSpan "Elaboration: unsupported App — function expression elaborated to unsupported type %A" funcVal.Type
    // Phase 9: Tuple construction — GC_malloc(n*8) + sequential GEP + store
    // Phase 28: Tuple([]) = unit — return I64 0 (matches print/println unit convention; avoids type mismatch in if-then-without-else)
    | Tuple (exprs, _) ->
        let n = List.length exprs
        if n = 0 then
            // Empty tuple = unit value: return I64 0 (same as print/println)
            let unitVal = { Name = freshName env; Type = I64 }
            (unitVal, [ArithConstantOp(unitVal, 1L)])
        else
        // Elaborate all field expressions first
        let fieldResults = exprs |> List.map (fun e -> elaborateExpr env e)
        let allFieldOps  = fieldResults |> List.collect snd
        let fieldVals    = fieldResults |> List.map fst
        // Phase 93: GC_malloc((n+2) * 8) — slot 0 = heap tag, slot 1 = field count, slots 2+ = fields
        let bytesVal  = { Name = freshName env; Type = I64 }
        let tupPtrVal = { Name = freshName env; Type = Ptr }
        let tagSlot   = { Name = freshName env; Type = Ptr }
        let tagVal    = { Name = freshName env; Type = I64 }
        let countSlot = { Name = freshName env; Type = Ptr }
        let countVal  = { Name = freshName env; Type = I64 }
        let allocOps  = [
            ArithConstantOp(bytesVal, int64 ((n + 2) * 8))
            LlvmCallOp(tupPtrVal, "@GC_malloc", [bytesVal])
            LlvmGEPLinearOp(tagSlot, tupPtrVal, 0)
            ArithConstantOp(tagVal, 2L)  // LANG_HEAP_TAG_TUPLE
            LlvmStoreOp(tagVal, tagSlot)
            LlvmGEPLinearOp(countSlot, tupPtrVal, 1)
            ArithConstantOp(countVal, int64 n)
            LlvmStoreOp(countVal, countSlot)
        ]
        // Store each field: GEP ptr[i+2] + store value at slot
        let storeOps =
            fieldVals |> List.mapi (fun i fv ->
                let slotVal = { Name = freshName env; Type = Ptr }
                [ LlvmGEPLinearOp(slotVal, tupPtrVal, i + 2)
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

    // Phase 93: Cons — GC_malloc(24) cons cell with tag at slot 0, head at slot 1, tail at slot 2
    | Cons(headExpr, tailExpr, _) ->
        let (headVal, headOps) = elaborateExpr env headExpr
        let (tailVal, tailOps) = elaborateExpr env tailExpr
        let bytesVal = { Name = freshName env; Type = I64 }
        let cellPtr  = { Name = freshName env; Type = Ptr }
        let tagSlot  = { Name = freshName env; Type = Ptr }
        let tagVal   = { Name = freshName env; Type = I64 }
        let headSlot = { Name = freshName env; Type = Ptr }
        let tailSlot = { Name = freshName env; Type = Ptr }
        let allocOps = [
            ArithConstantOp(bytesVal, 24L)
            LlvmCallOp(cellPtr, "@GC_malloc", [bytesVal])
            LlvmGEPLinearOp(tagSlot, cellPtr, 0)
            ArithConstantOp(tagVal, 4L)  // LANG_HEAP_TAG_LIST
            LlvmStoreOp(tagVal, tagSlot)
            LlvmGEPLinearOp(headSlot, cellPtr, 1)       // head at slot 1
            LlvmStoreOp(headVal, headSlot)
            LlvmGEPLinearOp(tailSlot, cellPtr, 2)       // tail at slot 2
            LlvmStoreOp(tailVal, tailSlot)
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
                (v, [ArithConstantOp(v, tagConst 1L)])
        // Phase 88: Pass tagged start/stop directly to C so list elements are tagged.
        // Convert tagged step to raw double: tagged(k) = 2k+1, step for C = 2k = tagged(k) - 1.
        let rawStepOne = { Name = freshName env; Type = I64 }
        let rawStep = { Name = freshName env; Type = I64 }
        let stepConvOps = [ArithConstantOp(rawStepOne, 1L); ArithSubIOp(rawStep, stepVal, rawStepOne)]
        let result = { Name = freshName env; Type = Ptr }
        (result, startOps @ stopOps @ stepOps @ stepConvOps @ [LlvmCallOp(result, "@lang_range", [startVal; stopVal; rawStep])])

    // Phase 11 (v2): General Match compiler — Jules Jacobs decision tree algorithm.
    // Compiles pattern matching to a binary decision tree, then emits MLIR from the tree.
    // Handles: VarPat, WildcardPat, ConstPat(Int/Bool/String), EmptyListPat, ConsPat, TuplePat.
    // Phase 13: adds OrPat expansion (PAT-07), when-guard (PAT-06), CharConst (PAT-08).
    | Match(scrutineeExpr, clauses, matchSpan) ->
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
                let ops = [ ArithConstantOp(kVal, tagConst (int64 n)); ArithCmpIOp(cond, "eq", scrutVal, kVal) ]
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
                    LlvmGEPStructOp(patDataPtrVal, patStrVal, 2)
                    LlvmLoadOp(patDataVal, patDataPtrVal)
                    LlvmGEPStructOp(scrutDataPtrVal, scrutVal, 2)
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
                // Phase 93: ADT ctor tag at slot 1 (heap_tag at slot 0)
                let info     = Map.find name env.TypeEnv
                let tagSlot  = { Name = freshName env; Type = Ptr }
                let tagLoad  = { Name = freshName env; Type = I64 }
                let tagConst = { Name = freshName env; Type = I64 }
                let cond     = { Name = freshName env; Type = I1 }
                let ops = [
                    LlvmGEPLinearOp(tagSlot, scrutVal, 1)
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
        // Phase 93: field 1 = head (I64), field 2 = tail (Ptr).
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
                    let availableTypes = env.RecordEnv |> Map.toList |> List.map fst |> String.concat ", "
                    failWithSpan matchSpan "ensureRecordFieldTypes: cannot resolve record type for fields %A. Available record types: %s" fields availableTypes)
            let mutable ops = []
            fields |> List.iteri (fun i fieldName ->
                if i < argAccs.Length then
                    let declSlotIdx = Map.find fieldName fieldMap
                    let (parentVal, parentOps) = resolveAccessorTyped scrutAcc Ptr
                    let slotPtr  = { Name = freshName env; Type = Ptr }
                    let fieldVal = { Name = freshName env; Type = I64 }
                    let gepOp  = LlvmGEPLinearOp(slotPtr, parentVal, declSlotIdx + 2)  // Phase 93: +2 for tag+count
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
                let blocksBeforeBody = env.Blocks.Value.Length
                let (bodyVal, bodyOps) = elaborateExpr bindEnv bodyExpr
                // Coerce arm value to I64 for uniform merge block type (same as function body ABI)
                let (coercedVal, coerceOps) = coerceToI64 bindEnv bodyVal
                resultType.Value <- I64
                // Create a body block and branch to merge (skip merge branch if body already terminated)
                let terminatedOps =
                    match List.tryLast bodyOps with
                    | Some op when isTerminatorOp op && env.Blocks.Value.Length > blocksBeforeBody ->
                        // Body ended with a block terminator (e.g., nested if/match).
                        // Continuation (coerce + branch to merge) goes into the last side block (nested merge block).
                        // IMPORTANT: append AFTER lastBlock.Body — the Let handler may have already
                        // patched this block with continuation ops that must execute before our branch.
                        let targetIdx = blocksBeforeBody + (env.Blocks.Value.Length - blocksBeforeBody) - 1
                        let contOps = coerceOps @ [CfBrOp(mergeLabel, [coercedVal])]
                        appendToBlock env targetIdx contOps
                        bodyOps  // entry ops only (end with terminator); side blocks already patched
                    | Some LlvmUnreachableOp -> bodyOps
                    | Some (CfBrOp _) | Some (CfCondBrOp _) -> bodyOps
                    | _ -> bodyOps @ coerceOps @ [CfBrOp(mergeLabel, [coercedVal])]
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
                // Snapshot accessor cache before preloading branch-specific fields.
                // preloadOps are only valid in the matchLabel block (ifMatch path).
                // We must restore the cache before emitting ifNoMatch so it doesn't
                // incorrectly reuse values defined only in the ifMatch branch, which
                // would violate MLIR dominance constraints.
                let cacheSnapshot = System.Collections.Generic.Dictionary<_, _>(accessorCache)
                // Pre-load sub-fields with correct types into the cache (for ifMatch branch)
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
                // Restore accessor cache to pre-preload snapshot before emitting ifNoMatch.
                // This prevents the ifNoMatch branch from seeing field values that were only
                // loaded in the ifMatch block (which doesn't dominate the ifNoMatch block).
                accessorCache.Clear()
                for kvp in cacheSnapshot do accessorCache.[kvp.Key] <- kvp.Value
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
                let blocksBeforeBody = env.Blocks.Value.Length
                let (bodyVal, bodyOps) = elaborateExpr bindEnv bodyExpr
                // Coerce arm value to I64 for uniform merge block type (same as function body ABI)
                let (coercedVal, coerceOps) = coerceToI64 bindEnv bodyVal
                resultType.Value <- I64
                let terminatedOps =
                    match List.tryLast bodyOps with
                    | Some op when isTerminatorOp op && env.Blocks.Value.Length > blocksBeforeBody ->
                        // Body ended with a block terminator (e.g., nested if/match).
                        // Patch coerce + branch to merge into the last side block.
                        let targetIdx = env.Blocks.Value.Length - 1
                        let contOps = coerceOps @ [CfBrOp(mergeLabel, [coercedVal])]
                        appendToBlock env targetIdx contOps
                        bodyOps
                    | Some LlvmUnreachableOp -> bodyOps
                    | Some (CfBrOp _) | Some (CfCondBrOp _) -> bodyOps
                    | _ -> bodyOps @ coerceOps @ [CfBrOp(mergeLabel, [coercedVal])]
                let bodyLabel = freshLabel env (sprintf "match_body%d" bodyIdx)
                env.Blocks.Value <- env.Blocks.Value @
                    [ { Label = Some bodyLabel; Args = [];
                        Body = terminatedOps } ]
                // 5. Coerce guard to I1 (may be I64 from function call like List.contains)
                let (i1Guard, coerceGuardOps) = coerceToI1 bindEnv guardVal
                // 6. Return: bind ops + guard eval + coerce + conditional branch
                bindOps @ guardOps @ coerceGuardOps @ [CfCondBrOp(i1Guard, bodyLabel, [], guardFailLabel, [])]

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
    // Used by closures (e.g. compose operators defined in Prelude)
    | Lambda (param, body, lamSpan) ->
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

        // Build inner llvm.func: (%arg0: !llvm.ptr, %arg1: !llvm.ptr) -> i64
        // If param needs I64 type, coerce ptr arg1 to i64 via ptrtoint.
        let arg0Val = { Name = "%arg0"; Type = Ptr }
        let arg1Val = { Name = "%arg1"; Type = Ptr }
        let lambdaParamNeedsI64 = not (isPtrParamTyped env.AnnotationMap lamSpan param body)
        let (lambdaParamVal, lambdaParamCoerceOps, lambdaCaptureStart) =
            if lambdaParamNeedsI64 then
                let i64Val = { Name = "%t0"; Type = I64 }
                (i64Val, [LlvmPtrToIntOp(i64Val, arg1Val)], 1)
            else
                (arg1Val, [], 0)
        let innerEnv : ElabEnv =
            { Vars = Map.ofList [(param, lambdaParamVal)]
              Counter = ref lambdaCaptureStart; LabelCounter = ref 0; Blocks = ref []
              KnownFuncs = env.KnownFuncs
              Funcs = env.Funcs
              ClosureCounter = env.ClosureCounter
              Globals = env.Globals
              GlobalCounter = env.GlobalCounter
              TplGlobals = env.TplGlobals
              TypeEnv = env.TypeEnv; RecordEnv = env.RecordEnv; ExnTags = env.ExnTags
              // Remove param name from MutableVars so lambda param shadows outer mutable var
              MutableVars = Set.remove param env.MutableVars; ArrayVars = Set.empty; CollectionVars = Map.empty
              BoolVars = Set.empty; StringVars = Set.empty; StringFields = env.StringFields; AnnotationMap = env.AnnotationMap }
        let captureLoadOps, innerEnvWithCaptures =
            captures |> List.mapi (fun i capName ->
                let gepVal = { Name = sprintf "%%t%d" (lambdaCaptureStart + i); Type = Ptr }
                let capType = if Set.contains capName env.MutableVars then Ptr else I64
                let capVal = { Name = sprintf "%%t%d" (lambdaCaptureStart + i + numCaptures); Type = capType }
                (gepVal, capVal, capName, i)
            )
            |> List.fold (fun (opsAcc, envAcc: ElabEnv) (gepVal, capVal, capName, i) ->
                let gepOp  = LlvmGEPLinearOp(gepVal, arg0Val, i + 1)
                let loadOp = LlvmLoadOp(capVal, gepVal)
                let envAcc' = { envAcc with Vars = Map.add capName capVal envAcc.Vars }
                (opsAcc @ [gepOp; loadOp], envAcc')
            ) ([], innerEnv)
        innerEnvWithCaptures.Counter.Value <- lambdaCaptureStart + numCaptures * 2
        let (bodyVal, bodyEntryOps) = elaborateExpr innerEnvWithCaptures body
        // Phase 20/35: Normalize body return to I64 (Ptr→ptrtoint, I1→zext) for uniform closure return type.
        let (finalRetVal, normalizeRetOps) = coerceToI64 innerEnvWithCaptures bodyVal
        let bodySideBlocks = innerEnvWithCaptures.Blocks.Value
        let allBodyBlocks =
            if bodySideBlocks.IsEmpty then
                [ { Label = None; Args = []; Body = lambdaParamCoerceOps @ captureLoadOps @ bodyEntryOps @ normalizeRetOps @ [LlvmReturnOp [finalRetVal]] } ]
            else
                let entryBlock = { Label = None; Args = []; Body = lambdaParamCoerceOps @ captureLoadOps @ bodyEntryOps }
                let lastBlock = List.last bodySideBlocks
                let lastBlockWithReturn = { lastBlock with Body = lastBlock.Body @ normalizeRetOps @ [LlvmReturnOp [finalRetVal]] }
                let sideBlocksPatched = (List.take (bodySideBlocks.Length - 1) bodySideBlocks) @ [lastBlockWithReturn]
                entryBlock :: sideBlocksPatched
        let innerFuncOp : FuncOp =
            { Name = closureFnName; InputTypes = [Ptr; Ptr]; ReturnType = Some I64
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
                // Issue #4: Env slots are Ptr. If capVal is I64, insert inttoptr.
                if capVal.Type = I64 then
                    let ptrVal = { Name = freshName env; Type = Ptr }
                    [ LlvmGEPLinearOp(slotVal, envPtrVal, i + 1)
                      LlvmIntToPtrOp(ptrVal, capVal)
                      LlvmStoreOp(ptrVal, slotVal) ]
                else
                    [ LlvmGEPLinearOp(slotVal, envPtrVal, i + 1)
                      LlvmStoreOp(capVal, slotVal) ]
            ) |> List.concat
        (envPtrVal, allocOps @ captureStoreOps)

    // Phase 17: ADT constructor — nullary variant (e.g. Red in type Color = Red | Green | Blue)
    // Allocates a 16-byte block: slot 0 = i64 tag, slot 1 = null ptr.
    // Phase 20: If arity >= 1, the constructor is used as a first-class value (e.g. `apply Some 42`).
    // In that case, wrap as Lambda(param, Constructor(name, Some(Var(param)), _)) and re-elaborate.
    | Constructor(name, None, ctorSpan) ->
        match Map.tryFind name env.TypeEnv with
        | None ->
            failWithSpan ctorSpan "undefined constructor '%s'. Uppercase names are reserved for constructors — use lowercase for variables (e.g., 'const_val' instead of 'CONST_VAL')" name
        | Some info ->
        if info.Arity >= 1 then
            // First-class unary+ constructor: produce a closure fun __ctor_N_Name x -> Name x
            let n = env.Counter.Value
            env.Counter.Value <- n + 1
            let paramName = sprintf "__ctor_%d_%s" n name
            let s = ctorSpan
            elaborateExpr env (Lambda(paramName, Constructor(name, Some(Var(paramName, s)), s), s))
        else
            // Phase 93: Nullary constructor: allocate 24-byte block — heap_tag@0, ctor_tag@1, payload@2
            let sizeVal     = { Name = freshName env; Type = I64 }
            let blockPtr    = { Name = freshName env; Type = Ptr }
            let heapTagSlot = { Name = freshName env; Type = Ptr }
            let heapTagVal  = { Name = freshName env; Type = I64 }
            let tagSlot     = { Name = freshName env; Type = Ptr }
            let tagVal      = { Name = freshName env; Type = I64 }
            let paySlot     = { Name = freshName env; Type = Ptr }
            let nullPayload = { Name = freshName env; Type = Ptr }
            let ops = [
                ArithConstantOp(sizeVal, 24L)
                LlvmCallOp(blockPtr, "@GC_malloc", [sizeVal])
                LlvmGEPLinearOp(heapTagSlot, blockPtr, 0)
                ArithConstantOp(heapTagVal, 5L)  // LANG_HEAP_TAG_ADT
                LlvmStoreOp(heapTagVal, heapTagSlot)
                LlvmGEPLinearOp(tagSlot, blockPtr, 1)
                ArithConstantOp(tagVal, int64 info.Tag)
                LlvmStoreOp(tagVal, tagSlot)
                LlvmGEPLinearOp(paySlot, blockPtr, 2)
                LlvmNullOp(nullPayload)
                LlvmStoreOp(nullPayload, paySlot)
            ]
            (blockPtr, ops)

    // Phase 93: ADT constructor — unary/multi-arg variant (e.g. Some 42, Pair(3,4))
    // Allocates a 24-byte block: heap_tag@0, ctor_tag@1, payload@2.
    | Constructor(name, Some argExpr, ctorSpan) ->
        let info = match Map.tryFind name env.TypeEnv with
                   | Some i -> i
                   | None -> failWithSpan ctorSpan "undefined constructor '%s'. Uppercase names are reserved for constructors — use lowercase for variables" name
        let (argVal, argOps) = elaborateExpr env argExpr
        let sizeVal     = { Name = freshName env; Type = I64 }
        let blockPtr    = { Name = freshName env; Type = Ptr }
        let heapTagSlot = { Name = freshName env; Type = Ptr }
        let heapTagVal  = { Name = freshName env; Type = I64 }
        let tagSlot     = { Name = freshName env; Type = Ptr }
        let tagVal      = { Name = freshName env; Type = I64 }
        let paySlot     = { Name = freshName env; Type = Ptr }
        let allocOps = [
            ArithConstantOp(sizeVal, 24L)
            LlvmCallOp(blockPtr, "@GC_malloc", [sizeVal])
            LlvmGEPLinearOp(heapTagSlot, blockPtr, 0)
            ArithConstantOp(heapTagVal, 5L)  // LANG_HEAP_TAG_ADT
            LlvmStoreOp(heapTagVal, heapTagSlot)
            LlvmGEPLinearOp(tagSlot, blockPtr, 1)
            ArithConstantOp(tagVal, int64 info.Tag)
            LlvmStoreOp(tagVal, tagSlot)
            LlvmGEPLinearOp(paySlot, blockPtr, 2)
            LlvmStoreOp(argVal, paySlot)
        ]
        (blockPtr, argOps @ allocOps)

    // Phase 18: RecordExpr construction — allocate n-slot GC_malloc block, store fields in declaration order
    | RecordExpr(typeNameOpt, fields, recSpan) ->
        let fieldNames = fields |> List.map fst |> Set.ofList
        let typeName =
            match typeNameOpt with
            | Some n -> n
            | None ->
                env.RecordEnv
                |> Map.tryFindKey (fun _ fmap ->
                    Set.ofSeq (fmap |> Map.toSeq |> Seq.map fst) = fieldNames)
                |> Option.defaultWith (fun () ->
                    let availableTypes = env.RecordEnv |> Map.toList |> List.map fst |> String.concat ", "
                    failWithSpan recSpan "RecordExpr: cannot resolve record type for fields %A. Available record types: %s" (Set.toList fieldNames) availableTypes)
        let fieldMap = Map.find typeName env.RecordEnv
        let n = Map.count fieldMap
        let fieldResults = fields |> List.map (fun (_, e) -> elaborateExpr env e)
        let allFieldOps  = fieldResults |> List.collect snd
        let fieldVals    = fieldResults |> List.map fst
        // Phase 93: GC_malloc((n+2) * 8) — slot 0 = RECORD tag, slot 1 = field count, slots 2+ = fields
        let bytesVal    = { Name = freshName env; Type = I64 }
        let recPtrVal   = { Name = freshName env; Type = Ptr }
        let recTagSlot  = { Name = freshName env; Type = Ptr }
        let recTagVal   = { Name = freshName env; Type = I64 }
        let recCountSlot = { Name = freshName env; Type = Ptr }
        let recCountVal  = { Name = freshName env; Type = I64 }
        let allocOps  = [
            ArithConstantOp(bytesVal, int64 ((n + 2) * 8))
            LlvmCallOp(recPtrVal, "@GC_malloc", [bytesVal])
            LlvmGEPLinearOp(recTagSlot, recPtrVal, 0)
            ArithConstantOp(recTagVal, 3L)  // LANG_HEAP_TAG_RECORD
            LlvmStoreOp(recTagVal, recTagSlot)
            LlvmGEPLinearOp(recCountSlot, recPtrVal, 1)
            ArithConstantOp(recCountVal, int64 n)
            LlvmStoreOp(recCountVal, recCountSlot)
        ]
        let storeOps =
            fields |> List.collect (fun (fieldName, _) ->
                let slotIdx = Map.find fieldName fieldMap
                let fieldVal = List.item (fields |> List.findIndex (fun (fn, _) -> fn = fieldName)) fieldVals
                let slotPtr = { Name = freshName env; Type = Ptr }
                [ LlvmGEPLinearOp(slotPtr, recPtrVal, slotIdx + 2)
                  LlvmStoreOp(fieldVal, slotPtr) ]
            )
        (recPtrVal, allFieldOps @ allocOps @ storeOps)

    // Phase 59: Nested qualified value access — Outer.Inner.foo → Var("Outer_Inner_foo")
    // Handles chains of any depth. Must come BEFORE the single-level Constructor arm below.
    | FieldAccess(FieldAccess(_, _, _) as innerExpr, memberName, span)
        when (tryDecodeModulePath innerExpr).IsSome ->
        let segments = (tryDecodeModulePath innerExpr).Value @ [memberName]
        if Map.containsKey memberName env.TypeEnv then
            elaborateExpr env (Constructor(memberName, None, span))
        else
            elaborateExpr env (Var(segments |> String.concat "_", span))

    // Phase 25: Qualified name desugar — M.x → Var(M_x), M.Ctor → Constructor(Ctor)
    // Phase 35: Use module-qualified name (modName + "_" + memberName) to avoid collisions
    // when multiple modules define functions with the same name (e.g., Option.map vs Result.map).
    // Must come BEFORE the record FieldAccess arm.
    | FieldAccess(Constructor(modName, None, _), memberName, span) ->
        if Map.containsKey memberName env.TypeEnv then
            elaborateExpr env (Constructor(memberName, None, span))
        else
            elaborateExpr env (Var(modName + "_" + memberName, span))

    // Phase 18: FieldAccess — GEP into record block at declaration-order slot, load value
    | FieldAccess(recExpr, fieldName, faSpan) ->
        let (recVal, recOps) = elaborateExpr env recExpr
        let slotIdx =
            env.RecordEnv
            |> Map.toSeq
            |> Seq.tryPick (fun (_, fmap) -> Map.tryFind fieldName fmap)
            |> Option.defaultWith (fun () ->
                let hint =
                    env.RecordEnv |> Map.toList
                    |> List.map (fun (name, fields) -> sprintf "%s: {%s}" name (fields |> Map.toList |> List.map fst |> String.concat "; "))
                    |> String.concat " | "
                failWithSpan faSpan "FieldAccess: unknown field '%s'. Known records: %s" fieldName hint)
        // Coerce record to Ptr if it came in as I64 (e.g., through a closure argument)
        let (recPtr, recCoerce) =
            if recVal.Type = Ptr then (recVal, [])
            else let v = { Name = freshName env; Type = Ptr } in (v, [LlvmIntToPtrOp(v, recVal)])
        let slotPtr  = { Name = freshName env; Type = Ptr }
        let fieldVal = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmGEPLinearOp(slotPtr, recPtr, slotIdx + 2)  // Phase 93: +2 for tag+count header
            LlvmLoadOp(fieldVal, slotPtr)
        ]
        (fieldVal, recOps @ recCoerce @ ops)

    // Phase 18: RecordUpdate — allocate new block, copy non-overridden fields, write overridden fields
    | RecordUpdate(sourceExpr, overrides, ruSpan) ->
        let (srcValRaw, srcOpsRaw) = elaborateExpr env sourceExpr
        // Issue #3: If source record is I64 (e.g., extracted from list match), coerce to Ptr
        let (srcVal, srcOps) =
            if srcValRaw.Type = I64 then
                let ptrVal = { Name = freshName env; Type = Ptr }
                (ptrVal, srcOpsRaw @ [LlvmIntToPtrOp(ptrVal, srcValRaw)])
            else (srcValRaw, srcOpsRaw)
        let overrideNames = overrides |> List.map fst |> Set.ofList
        let (typeName, fieldMap) =
            env.RecordEnv
            |> Map.tryFindKey (fun _ fmap ->
                overrideNames |> Set.forall (fun fn -> Map.containsKey fn fmap))
            |> Option.map (fun tn -> (tn, Map.find tn env.RecordEnv))
            |> Option.defaultWith (fun () ->
                let availableTypes = env.RecordEnv |> Map.toList |> List.map fst |> String.concat ", "
                failWithSpan ruSpan "RecordUpdate: cannot resolve record type for fields %A. Available record types: %s" (Set.toList overrideNames) availableTypes)
        let n = Map.count fieldMap
        let overrideResults = overrides |> List.map (fun (fn, e) -> (fn, elaborateExpr env e))
        let overrideOps     = overrideResults |> List.collect (fun (_, (_, ops)) -> ops)
        let overrideVals    = overrideResults |> List.map (fun (fn, (v, _)) -> (fn, v)) |> Map.ofList
        // Phase 93: GC_malloc((n+2) * 8) — slot 0 = RECORD tag, slot 1 = field count, slots 2+ = fields
        let bytesVal     = { Name = freshName env; Type = I64 }
        let newPtrVal    = { Name = freshName env; Type = Ptr }
        let ruTagSlot    = { Name = freshName env; Type = Ptr }
        let ruTagVal     = { Name = freshName env; Type = I64 }
        let ruCountSlot  = { Name = freshName env; Type = Ptr }
        let ruCountVal   = { Name = freshName env; Type = I64 }
        let allocOps  = [
            ArithConstantOp(bytesVal, int64 ((n + 2) * 8))
            LlvmCallOp(newPtrVal, "@GC_malloc", [bytesVal])
            LlvmGEPLinearOp(ruTagSlot, newPtrVal, 0)
            ArithConstantOp(ruTagVal, 3L)  // LANG_HEAP_TAG_RECORD
            LlvmStoreOp(ruTagVal, ruTagSlot)
            LlvmGEPLinearOp(ruCountSlot, newPtrVal, 1)
            ArithConstantOp(ruCountVal, int64 n)
            LlvmStoreOp(ruCountVal, ruCountSlot)
        ]
        let copyOps =
            fieldMap |> Map.toList |> List.collect (fun (fieldName, slotIdx) ->
                let dstSlotPtr = { Name = freshName env; Type = Ptr }
                match Map.tryFind fieldName overrideVals with
                | Some newVal ->
                    [ LlvmGEPLinearOp(dstSlotPtr, newPtrVal, slotIdx + 2)
                      LlvmStoreOp(newVal, dstSlotPtr) ]
                | None ->
                    let srcSlotPtr = { Name = freshName env; Type = Ptr }
                    let srcFieldVal = { Name = freshName env; Type = I64 }
                    [ LlvmGEPLinearOp(srcSlotPtr, srcVal, slotIdx + 2)
                      LlvmLoadOp(srcFieldVal, srcSlotPtr)
                      LlvmGEPLinearOp(dstSlotPtr, newPtrVal, slotIdx + 2)
                      LlvmStoreOp(srcFieldVal, dstSlotPtr) ]
            )
        (newPtrVal, srcOps @ overrideOps @ allocOps @ copyOps)

    // Phase 18: SetField — store in-place at field slot, return unit (i64=0)
    | SetField(recExpr, fieldName, valueExpr, sfSpan) ->
        let (recVal, recOps)    = elaborateExpr env recExpr
        let (newVal, newValOps) = elaborateExpr env valueExpr
        let slotIdx =
            env.RecordEnv
            |> Map.toSeq
            |> Seq.tryPick (fun (_, fmap) -> Map.tryFind fieldName fmap)
            |> Option.defaultWith (fun () ->
                let hint =
                    env.RecordEnv |> Map.toList
                    |> List.map (fun (name, fields) -> sprintf "%s: {%s}" name (fields |> Map.toList |> List.map fst |> String.concat "; "))
                    |> String.concat " | "
                failWithSpan sfSpan "SetField: unknown field '%s'. Known records: %s" fieldName hint)
        // Coerce record to Ptr if it came in as I64 (e.g., through a closure argument)
        let (recPtr, recCoerce) =
            if recVal.Type = Ptr then (recVal, [])
            else let v = { Name = freshName env; Type = Ptr } in (v, [LlvmIntToPtrOp(v, recVal)])
        let slotPtr = { Name = freshName env; Type = Ptr }
        let unitVal = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmGEPLinearOp(slotPtr, recPtr, slotIdx + 2)  // Phase 93: +2 for tag+count header
            LlvmStoreOp(newVal, slotPtr)
            ArithConstantOp(unitVal, 1L)
        ]
        (unitVal, recOps @ recCoerce @ newValOps @ ops)

    // Phase 19: Raise — construct exception value, call @lang_throw, terminate block
    | Raise(exnExpr, _) ->
        let (exnVal, exnOps) = elaborateExpr env exnExpr
        // exnVal is a Ptr to an ADT DataValue block (e.g., {tag=0, payload=LangString*} for Failure "msg")
        // Call @lang_throw(exnVal) — noreturn void call
        // Emit llvm.unreachable after the call to terminate the block
        // deadVal must be defined by ArithConstantOp to satisfy MLIR SSA validity,
        // even though it is never used after llvm.unreachable
        let deadVal = { Name = freshName env; Type = I64 }
        (deadVal, exnOps @ [ ArithConstantOp(deadVal, 1L); LlvmCallVoidOp("@lang_throw", [exnVal]); LlvmUnreachableOp ])

    // Phase 19: TryWith — setjmp/longjmp exception handling
    // Control flow:
    //   [entry] alloc frame, call lang_try_enter (setjmp), branch on result
    //   ^try_body — elaborate body, pop handler, branch to merge
    //   ^exn_caught — pop handler (C-16), get exception, dispatch via MatchCompiler
    //   ^exn_fail — re-raise unmatched exception via @lang_throw
    //   ^merge(%result) — join point
    | TryWith(bodyExpr, clauses, trySpan) ->
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
                let ops = [ ArithConstantOp(kVal, tagConst (int64 n)); ArithCmpIOp(cond, "eq", scrutVal, kVal) ]
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
                    LlvmGEPStructOp(patDataPtrVal, patStrVal, 2)
                    LlvmLoadOp(patDataVal, patDataPtrVal)
                    LlvmGEPStructOp(scrutDataPtrVal, scrutVal, 2)
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
                // Phase 93: ADT ctor tag at slot 1 (heap_tag at slot 0)
                let info     = Map.find name env.TypeEnv
                let tagSlot  = { Name = freshName env; Type = Ptr }
                let tagLoad  = { Name = freshName env; Type = I64 }
                let tagConst = { Name = freshName env; Type = I64 }
                let cond     = { Name = freshName env; Type = I1 }
                let ops = [
                    LlvmGEPLinearOp(tagSlot, scrutVal, 1)
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
                    let availableTypes = env.RecordEnv |> Map.toList |> List.map fst |> String.concat ", "
                    failWithSpan trySpan "ensureRecordFieldTypes2: cannot resolve record type for fields %A. Available record types: %s" fields availableTypes)
            let mutable ops = []
            fields |> List.iteri (fun i fieldName ->
                if i < argAccs.Length then
                    let declSlotIdx = Map.find fieldName fieldMap
                    let (parentVal, parentOps) = resolveAccessorTyped2 scrutAcc Ptr
                    let slotPtr  = { Name = freshName env; Type = Ptr }
                    let fieldVal = { Name = freshName env; Type = I64 }
                    let gepOp  = LlvmGEPLinearOp(slotPtr, parentVal, declSlotIdx + 2)  // Phase 93: +2 for tag+count
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
                // Snapshot cache before preloading ifMatch-specific fields.
                let cacheSnapshot2 = System.Collections.Generic.Dictionary<_, _>(accessorCache2)
                let preloadOps =
                    match tag with
                    | MatchCompiler.ConsCtor -> ensureConsFieldTypes2 scrutAcc argAccs
                    | MatchCompiler.AdtCtor(_, _, arity) when arity > 0 -> ensureAdtFieldTypes2 scrutAcc argAccs
                    | MatchCompiler.RecordCtor fields -> ensureRecordFieldTypes2 fields scrutAcc argAccs
                    | _ -> []
                let matchLabel   = freshLabel env "try_yes"
                let noMatchLabel = freshLabel env "try_no"
                let matchOps   = emitDecisionTree2 ifMatch
                // Restore cache before emitting ifNoMatch branch.
                accessorCache2.Clear()
                for kvp in cacheSnapshot2 do accessorCache2.[kvp.Key] <- kvp.Value
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

    // Phase 29: WhileExpr — header-block CFG pattern
    // Control flow:
    //   [entry] define unitConst, cf.br ^while_header
    //   ^while_header — elaborate cond, cf.cond_br to ^while_body or ^while_exit
    //   ^while_body — elaborate body (discard value), re-elaborate cond, cf.cond_br back or exit
    //   ^while_exit(%exitArg : i64) — empty body, patched by LetPat/Let continuation
    | WhileExpr (condExpr, bodyExpr, _) ->
        // WhileExpr elaboration
        let headerLabel = freshLabel env "while_header"
        let bodyLabel   = freshLabel env "while_body"
        let exitLabel   = freshLabel env "while_exit"
        // Unit constant — defined in entry fragment so it dominates all loop blocks
        let unitConst = { Name = freshName env; Type = I64 }
        // Exit block argument carries the unit result out
        let exitArg = { Name = freshName env; Type = I64 }
        // Track blocks before elaborating the header condition (to detect short-circuit side blocks)
        let blocksBeforeCond = env.Blocks.Value.Length
        // Elaborate condition for the header block
        let (condVal, condOps)    = elaborateExpr env condExpr
        let condSideBlocks = env.Blocks.Value.Length - blocksBeforeCond
        // Phase 36 FIX-03: Coerce condVal to I1 if it is I64 (e.g. module Bool function).
        let (i1CondVal, coerceCondOps) = coerceToI1 env condVal
        let condBrOp = CfCondBrOp(i1CondVal, bodyLabel, [], exitLabel, [unitConst])
        // Track how many side blocks exist before elaborating body (for detecting inner blocks)
        let blocksBeforeBody = env.Blocks.Value.Length
        // Elaborate body for the body block
        let (_bodyVal, bodyOps)   = elaborateExpr env bodyExpr
        // Track blocks before elaborating back-edge condition
        let blocksBeforeBackCond = env.Blocks.Value.Length
        // Re-elaborate condition for mutable-safe back-edge evaluation
        let (condVal2, condOps2)  = elaborateExpr env condExpr
        let backCondSideBlocks = env.Blocks.Value.Length - blocksBeforeBackCond
        // Phase 36 FIX-03: Coerce condVal2 to I1 if it is I64 (same pattern as header cond).
        let (i1CondVal2, coerceCondOps2) = coerceToI1 env condVal2
        let backEdgeBrOp = CfCondBrOp(i1CondVal2, bodyLabel, [], exitLabel, [unitConst])
        // Determine where to place the back-edge branch op.
        // If bodyOps ends with a block terminator (from a nested while/if/match inside the body),
        // the back-edge must be appended to the LAST side block (the inner expression's merge/exit
        // block, which was left empty for patching), NOT inline in bodyOps.
        // Build the back-edge ops depending on whether back-cond created side blocks
        // If backCondSideBlocks > 0, condOps2 ends with a cf.br to its first side block,
        // and the back-edge branch must go into the last back-cond side block.
        let bodyBlockBody, needPatchLast, backEdgeForBody =
            if backCondSideBlocks > 0 then
                // Back-cond has side blocks (And/Or); coerceCondOps2 + backEdgeBrOp go into
                // the last side block via appendToBlock below — do NOT include inline.
                // Only condOps2 (entry fragment ending with And's cond_br) goes inline.
                match List.tryLast bodyOps with
                | Some op when isTerminatorOp op && env.Blocks.Value.Length > blocksBeforeBody ->
                    (bodyOps, true, condOps2)
                | _ ->
                    (bodyOps @ condOps2, false, [])
            else
                // Simple back-cond: no side blocks.
                let backEdgeOps = condOps2 @ coerceCondOps2 @ [backEdgeBrOp]
                match List.tryLast bodyOps with
                | Some op when isTerminatorOp op && env.Blocks.Value.Length > blocksBeforeBody ->
                    (bodyOps, true, backEdgeOps)
                | _ ->
                    (bodyOps @ backEdgeOps, false, [])
        // Build the while_header block.
        // If condOps created side blocks (short-circuit &&/||), coerceCondOps + condBrOp go into
        // the And's merge block via appendToBlock below — exclude from inline header body.
        let headerBody = if condSideBlocks > 0 then condOps else condOps @ coerceCondOps
        // Push the three while blocks AFTER elaborating both cond and body
        // (so any inner side blocks from nested expressions come first)
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some headerLabel
                Args  = []
                Body  = if condSideBlocks > 0 then headerBody
                        else headerBody @ [condBrOp] }
              { Label = Some bodyLabel
                Args  = []
                Body  = bodyBlockBody }
              { Label = Some exitLabel
                Args  = [exitArg]
                Body  = [] } ]
        // Patch the condBrOp into the last header-cond side block (if any)
        if condSideBlocks > 0 then
            // The last header-cond side block is at position
            // (env.Blocks.Value.Length - 3 - condSideBlocks) = blocksBeforeCond + condSideBlocks - 1
            // We need its index after the 3 while blocks were appended.
            // Before appending: it was at env.Blocks.Value.Length - 3 - condSideBlocks
            // But condSideBlocks blocks were pushed between blocksBeforeCond and blocksBeforeBody.
            // The last header-cond side block index (before the 3 while blocks) = blocksBeforeBody - 1
            let condLastIdx = blocksBeforeBody - 1  // last block pushed by cond elaboration
            if condLastIdx >= 0 then
                appendToBlock env condLastIdx (coerceCondOps @ [condBrOp])
        // Patch back-cond side blocks if needed
        if backCondSideBlocks > 0 then
            // The last back-cond side block is just before the 3 while blocks we appended.
            let backCondLastIdx = env.Blocks.Value.Length - 4  // 4th from end = just before header/body/exit
            if backCondLastIdx >= 0 then
                appendToBlock env backCondLastIdx (coerceCondOps2 @ [backEdgeBrOp])
        // If the body had inner side blocks, patch the back-edge into what is now the last
        // side block among the while's blocks (which is while_body itself — no, we need the
        // last block pushed before while_body that is the inner merge block).
        // Actually: inner blocks were already in env.Blocks.Value before we appended header/body/exit.
        // We need to patch the last block that was present AFTER elaborating body but BEFORE
        // we appended the three while blocks — i.e., the inner merge/exit block.
        if needPatchLast then
            // The inner merge block is at position (env.Blocks.Value.Length - 4 - backCondSideBlocks)
            let innerLastIdx = env.Blocks.Value.Length - 4 - backCondSideBlocks
            if innerLastIdx >= 0 then
                appendToBlock env innerLastIdx backEdgeForBody
            // If backCondSideBlocks > 0, also patch back-cond branch into the body's inner-last block
            // (This case: body has inner blocks AND back-cond has side blocks)
            // The back-cond side blocks are at indices (allBlocks.Length - 4 - backCondSideBlocks) to (allBlocks.Length - 4 - 1)
            // backEdgeBrOp should be in the last of those (already patched above in the backCondSideBlocks > 0 branch)
        // Entry fragment: define unit constant (dominates all loop blocks), then branch to header
        // Entry fragment: define unit constant, branch to header
        (exitArg, [ ArithConstantOp(unitConst, 1L); CfBrOp(headerLabel, []) ])

    // Phase 29: ForExpr — block-argument CFG pattern for loop counter
    // Control flow:
    //   [entry] elaborate start/stop, define unitConst, cf.br ^for_header(%start)
    //   ^for_header(%i : i64) — compare %i sle/%sge %stop, cf.cond_br to ^for_body or ^for_exit(%unitConst)
    //   ^for_body — elaborate body (discard value), compute %next = %i +/- 1, cf.br ^for_header(%next)
    //   ^for_exit(%exitArg : i64) — empty body, patched by LetPat/Let continuation
    | ForExpr (var, startExpr, isTo, stopExpr, bodyExpr, _) ->
        let headerLabel = freshLabel env "for_header"
        let bodyLabel   = freshLabel env "for_body"
        let exitLabel   = freshLabel env "for_exit"
        // Elaborate start and stop in entry fragment
        let (startVal, startOps) = elaborateExpr env startExpr
        let (stopVal, stopOps)   = elaborateExpr env stopExpr
        // Block argument for loop counter (carried into ^for_header)
        let iArg = { Name = freshName env; Type = I64 }
        // Unit constant — defined in entry fragment so it dominates all loop blocks
        let unitConst = { Name = freshName env; Type = I64 }
        // Exit block argument carries the unit result out
        let exitArg = { Name = freshName env; Type = I64 }
        // Body env: loop variable bound immutably (NOT in MutableVars — LOOP-04)
        let bodyEnv = { env with Vars = Map.add var iArg env.Vars }
        // Track side blocks before elaborating body (for nested loop detection)
        let blocksBeforeBody = env.Blocks.Value.Length
        // Elaborate body (value discarded — for loop returns unit)
        let (_bodyVal, bodyOps) = elaborateExpr bodyEnv bodyExpr
        // Increment/decrement: addi for `to`, subi for `downto`
        let oneConst = { Name = freshName env; Type = I64 }
        let nextVal  = { Name = freshName env; Type = I64 }
        let incrOp =
            if isTo then ArithAddIOp(nextVal, iArg, oneConst)
            else          ArithSubIOp(nextVal, iArg, oneConst)
        // Back-edge ops: increment counter by 2 (raw) and branch back to header
        // tagged(n) + 2 = (2n+1) + 2 = 2(n+1)+1 = tagged(n+1)
        let backEdgeOps = [ ArithConstantOp(oneConst, 2L); incrOp; CfBrOp(headerLabel, [nextVal]) ]
        // Comparison predicate: sle for ascending, sge for descending
        let predicate = if isTo then "sle" else "sge"
        let cmpVal = { Name = freshName env; Type = I1 }
        // Detect nested loop: same pattern as WhileExpr
        let bodyBlockBody, needPatchLast =
            match List.tryLast bodyOps with
            | Some op when isTerminatorOp op && env.Blocks.Value.Length > blocksBeforeBody ->
                // Body has inner side blocks — back-edge goes into the inner last block
                (bodyOps, true)
            | _ ->
                // Simple body — append back-edge inline
                (bodyOps @ backEdgeOps, false)
        // Push the three for blocks AFTER elaborating body
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some headerLabel
                Args  = [iArg]
                Body  = [ ArithCmpIOp(cmpVal, predicate, iArg, stopVal)
                          CfCondBrOp(cmpVal, bodyLabel, [], exitLabel, [unitConst]) ] }
              { Label = Some bodyLabel
                Args  = []
                Body  = bodyBlockBody }
              { Label = Some exitLabel
                Args  = [exitArg]
                Body  = [] } ]
        // Patch nested loop's inner merge block with back-edge ops if needed
        if needPatchLast then
            let innerLastIdx = env.Blocks.Value.Length - 4  // 4th from end = just before header/body/exit
            if innerLastIdx >= 0 then
                appendToBlock env innerLastIdx backEdgeOps
        // Entry fragment: elaborate start/stop, define unit constant, branch to header with start value
        (exitArg, startOps @ stopOps @ [ ArithConstantOp(unitConst, 1L); CfBrOp(headerLabel, [startVal]) ])

    // Phase 30: Type annotation pass-through — ignore type at codegen
    | Annot (expr, _, _) -> elaborateExpr env expr
    | LambdaAnnot (param, _, body, span) -> elaborateExpr env (Lambda(param, body, span))

    // Phase 30 + 34-03: ForInExpr — desugar to Lambda closure + lang_for_in_* runtime call.
    // Phase 34-03 extends: TuplePat support (for hashtable (k,v) destructuring) and
    // collection dispatch (HashSet/Queue/MutableList/Hashtable via detectCollectionKind).
    | ForInExpr (var, collExpr, bodyExpr, span) ->
        // Extract variable name from pattern; for TuplePat use a fresh param name.
        let varName = match var with Ast.VarPat(n, _) -> n | _ -> freshName env
        // For TuplePat: build lambda body that destructures the tuple param via LetPat.
        // The closure parameter (varName) arrives as I64 (pointer to heap tuple cast to int64_t).
        // LetPat(TuplePat, ...) now handles the I64->Ptr coercion internally (Phase 34-03 fix above).
        let lambdaBody =
            match var with
            | Ast.TuplePat _ ->
                LetPat(var, Var(varName, span), bodyExpr, span)
            | _ -> bodyExpr
        let closureLambda = Lambda(varName, lambdaBody, span)
        let (closureVal, closureOps) = elaborateExpr env closureLambda
        // Elaborate collection
        let (collVal, collOps) = elaborateExpr env collExpr
        // Coerce closure to Ptr if needed (same pattern as array_iter)
        let (closurePtrVal, closureCoerceOps) = coerceToPtrArg env closureVal
        // Coerce collection to Ptr if needed (list pointer may arrive as I64)
        let (collPtrVal, collCoerceOps) = coerceToPtrArg env collVal
        // Select runtime function based on collection type (Phase 34-03: collection dispatch)
        let forInFn =
            match detectCollectionKind env.CollectionVars env.AnnotationMap collExpr with
            | Some HashSet     -> "@lang_for_in_hashset"
            | Some Queue       -> "@lang_for_in_queue"
            | Some MutableList -> "@lang_for_in_mlist"
            | Some Hashtable   -> "@lang_for_in_hashtable"
            | None ->
                if isArrayExpr env.ArrayVars env.AnnotationMap collExpr
                then "@lang_for_in_array"
                else "@lang_for_in_list"
        // Call lang_for_in_*(closure, collection), return unit
        let (unitVal, callOps) = emitVoidCall env forInFn [closurePtrVal; collPtrVal]
        (unitVal, closureOps @ collOps @ closureCoerceOps @ collCoerceOps @ callOps)

    // Phase 34-01: LANG-01 StringSliceExpr — s.[start..stop] or s.[start..] (open-ended)
    // Compiles to lang_string_slice(s, start, stop) where stop=-1 means "to end"
    | StringSliceExpr (strExpr, startExpr, stopOpt, _) ->
        let (strVal, strOps)     = elaborateExpr env strExpr
        let (startVal, startOps) = elaborateExpr env startExpr
        let (stopVal, stopOps) =
            match stopOpt with
            | Some stopExpr -> elaborateExpr env stopExpr
            | None ->
                let sv = { Name = freshName env; Type = I64 }
                (sv, [ArithConstantOp(sv, -1L)])  // raw sentinel, not tagged
        let strPtrVal =
            if strVal.Type = I64 then { Name = freshName env; Type = Ptr } else strVal
        let strCoerceOps =
            if strVal.Type = I64 then [LlvmIntToPtrOp(strPtrVal, strVal)] else []
        // Phase 92: C function now untags start and stop internally
        // UNTAG(-1) = -1 (arithmetic right shift), so -1 sentinel passes through safely
        let result = { Name = freshName env; Type = Ptr }
        (result, strOps @ startOps @ stopOps @ strCoerceOps
                 @ [LlvmCallOp(result, "@lang_string_slice", [strPtrVal; startVal; stopVal])])

    // Phase 34-02: LANG-02 ListCompExpr — [for x in coll -> expr] or [for i in 0..n -> expr]
    // Wraps body as Lambda, calls lang_list_comp(closure, collection) → new LangCons* list.
    // Range form: 0..n elaborates to @lang_range which returns a LangCons* list —
    // so lang_list_comp works uniformly for both collection and range inputs.
    | ListCompExpr (var, collExpr, bodyExpr, span) ->
        let closureLambda = Lambda(var, bodyExpr, span)
        let (closureVal, closureOps) = elaborateExpr env closureLambda
        let (collVal, collOps) = elaborateExpr env collExpr
        // Coerce closure to Ptr if needed (same pattern as ForInExpr)
        let (closurePtrVal, closureCoerceOps) = coerceToPtrArg env closureVal
        // Coerce collection to Ptr if needed (list pointer may arrive as I64)
        let (collPtrVal, collCoerceOps) = coerceToPtrArg env collVal
        let result = { Name = freshName env; Type = Ptr }
        (result, closureOps @ collOps @ closureCoerceOps @ collCoerceOps
                 @ [LlvmCallOp(result, "@lang_list_comp", [closurePtrVal; collPtrVal])])

    | _ ->
        failWithSpan (Ast.spanOf expr) "Elaboration: unsupported expression %A" expr

/// Append ReturnOp to a block body only if the block does not already end with
/// a terminator (llvm.unreachable).  This allows Raise at the end of a function
/// without generating a dead `return` after `llvm.unreachable`.
let private appendReturnIfNeeded (ops: MlirOp list) (retVal: MlirValue) : MlirOp list =
    match List.tryLast ops with
    | Some LlvmUnreachableOp -> ops
    | _ -> ops @ [ReturnOp [retVal]]

