/// Lambda Lifting — AST-to-AST transformation pass (MLIR-style rewrite).
///
/// Nested LetRec bindings whose bodies reference variables from enclosing scopes
/// cannot be compiled to standalone func.func (MLIR functions don't capture SSA values
/// from enclosing regions). This pass lifts captured variables into explicit parameters,
/// eliminating the scope dependency.
///
/// Before:
///   let rec init n f =
///       let rec helper i = if i = n then [] else f i :: helper (i + 1)
///       helper 0
///
/// After:
///   let rec init n f =
///       let rec helper n f i = if i = n then [] else f i :: helper n f (i + 1)
///       helper n f 0
module LambdaLift

open Ast

/// Prepend captures as extra arguments to all free references of `targetName`.
/// Var(targetName) → App(...App(Var(targetName), Var(c1))..., Var(cN))
/// Respects shadowing: stops when targetName is rebound by Let/Lambda/LetRec.
let rec private prependCaptures (target: string) (caps: string list) (e: Expr) : Expr =
    let f = prependCaptures target caps
    match e with
    | Var(n, s) when n = target ->
        caps |> List.fold (fun acc c -> App(acc, Var(c, s), s)) (Var(n, s))
    // Binding forms that may shadow targetName
    | Let(n, bind, body, s) ->
        Let(n, f bind, (if n = target then body else f body), s)
    | Lambda(p, body, s) ->
        Lambda(p, (if p = target then body else f body), s)
    | LambdaAnnot(p, ty, body, s) ->
        LambdaAnnot(p, ty, (if p = target then body else f body), s)
    | LetRec(bindings, body, s) ->
        let names = bindings |> List.map (fun (n,_,_,_,_) -> n)
        if List.contains target names then LetRec(bindings, body, s)  // shadowed
        else
            let bindings' = bindings |> List.map (fun (n, p, pt, b, bs) ->
                (n, p, pt, (if p = target then b else f b), bs))
            LetRec(bindings', f body, s)
    | LetMut(n, v, body, s) ->
        LetMut(n, f v, (if n = target then body else f body), s)
    | LetPat(pat, bind, body, s) -> LetPat(pat, f bind, f body, s)
    // Recursive traversal for all other expression forms
    | Add(a, b, s) -> Add(f a, f b, s)
    | Subtract(a, b, s) -> Subtract(f a, f b, s)
    | Multiply(a, b, s) -> Multiply(f a, f b, s)
    | Divide(a, b, s) -> Divide(f a, f b, s)
    | Modulo(a, b, s) -> Modulo(f a, f b, s)
    | Negate(e, s) -> Negate(f e, s)
    | Equal(a, b, s) -> Equal(f a, f b, s)
    | NotEqual(a, b, s) -> NotEqual(f a, f b, s)
    | LessThan(a, b, s) -> LessThan(f a, f b, s)
    | GreaterThan(a, b, s) -> GreaterThan(f a, f b, s)
    | LessEqual(a, b, s) -> LessEqual(f a, f b, s)
    | GreaterEqual(a, b, s) -> GreaterEqual(f a, f b, s)
    | And(a, b, s) -> And(f a, f b, s)
    | Or(a, b, s) -> Or(f a, f b, s)
    | If(c, t, e, s) -> If(f c, f t, f e, s)
    | App(fn, arg, s) -> App(f fn, f arg, s)
    | Tuple(es, s) -> Tuple(List.map f es, s)
    | List(es, s) -> List(List.map f es, s)
    | Cons(h, t, s) -> Cons(f h, f t, s)
    | Match(scr, clauses, s) ->
        Match(f scr, clauses |> List.map (fun (p, g, b) -> (p, Option.map f g, f b)), s)
    | Constructor(n, Some arg, s) -> Constructor(n, Some (f arg), s)
    | Annot(e, ty, s) -> Annot(f e, ty, s)
    | RecordExpr(tn, fields, s) -> RecordExpr(tn, fields |> List.map (fun (n, e) -> (n, f e)), s)
    | FieldAccess(e, fn, s) -> FieldAccess(f e, fn, s)
    | RecordUpdate(src, fields, s) -> RecordUpdate(f src, fields |> List.map (fun (n, e) -> (n, f e)), s)
    | SetField(e, fn, v, s) -> SetField(f e, fn, f v, s)
    | Raise(e, s) -> Raise(f e, s)
    | TryWith(body, clauses, s) ->
        TryWith(f body, clauses |> List.map (fun (p, g, b) -> (p, Option.map f g, f b)), s)
    | Range(start, stop, step, s) -> Range(f start, f stop, Option.map f step, s)
    | Assign(n, v, s) -> Assign(n, f v, s)
    | WhileExpr(cond, body, s) -> WhileExpr(f cond, f body, s)
    | ForExpr(v, start, isTo, stop, body, s) -> ForExpr(v, f start, isTo, f stop, f body, s)
    | ForInExpr(v, coll, body, s) -> ForInExpr(v, f coll, f body, s)
    | IndexGet(coll, idx, s) -> IndexGet(f coll, f idx, s)
    | IndexSet(coll, idx, v, s) -> IndexSet(f coll, f idx, f v, s)
    | StringSliceExpr(str, start, stop, s) -> StringSliceExpr(f str, f start, Option.map f stop, s)
    | ListCompExpr(v, coll, body, s) -> ListCompExpr(v, f coll, f body, s)
    | _ -> e  // Number, Bool, String, Char, Unit, EmptyList, Var (non-target), Constructor(_, None, _)

/// Main lambda-lifting pass. Walks the AST tracking locally-bound variables.
/// When a nested LetRec binding captures local variables, it lifts them into
/// explicit parameters and rewrites all references.
let liftExpr (expr: Expr) : Expr =
    let rec lift (localVars: Set<string>) (e: Expr) : Expr =
        match e with
        | LetRec(bindings, body, span) ->
            // All binding names in this LetRec group
            let bindingNames = bindings |> List.map (fun (n,_,_,_,_) -> n) |> Set.ofList

            // Compute captures for each binding: free vars ∩ localVars
            let perBindingCaptures =
                bindings |> List.map (fun (name, param, _, bindBody, _) ->
                    let boundInBinding = Set.union bindingNames (Set.singleton param)
                    let freeInBody = ElabHelpers.freeVars boundInBinding bindBody
                    Set.intersect freeInBody localVars)

            // For mutual recursion: union all captures across the group
            let allCaptures = perBindingCaptures |> Set.unionMany |> Set.toList |> List.sort

            if allCaptures.IsEmpty then
                // No captures — just recurse into binding bodies and continuation.
                // LetRec binding names become KnownFuncs, NOT localVars.
                let bindings' = bindings |> List.map (fun (n, p, pt, b, s) ->
                    let localVars' = Set.add p localVars
                    (n, p, pt, lift localVars' b, s))
                LetRec(bindings', lift localVars body, span)
            else
                // Lambda-lift: add captures as prepended parameters
                let bindings' = bindings |> List.map (fun (name, param, paramType, bindBody, bspan) ->
                    // 1. Rewrite self/sibling references in body to pass captures
                    let rewrittenBody =
                        bindingNames |> Set.fold (fun acc bname ->
                            prependCaptures bname allCaptures acc) bindBody

                    // 2. Recurse into the rewritten body (may have deeper nested LetRecs)
                    let innerLocalVars = Set.union localVars (Set.union bindingNames (Set.ofList (allCaptures @ [param])))
                    let liftedBody = lift innerLocalVars rewrittenBody

                    // 3. Wrap in Lambda chain: captures become extra parameters
                    //    Original: (name, param, paramType, body)
                    //    Lifted:   (name, cap1, None, Lambda(cap2, ..., Lambda(param, body')))
                    let wrappedBody =
                        (List.tail allCaptures @ [param])
                        |> List.foldBack (fun p acc -> Lambda(p, acc, bspan)) <| liftedBody
                    (name, List.head allCaptures, None, wrappedBody, bspan))

                // 4. Rewrite references in continuation to pass captures
                let rewrittenCont =
                    bindingNames |> Set.fold (fun acc bname ->
                        prependCaptures bname allCaptures acc) body
                // LetRec binding names become KnownFuncs, NOT localVars
                LetRec(bindings', lift localVars rewrittenCont, span)

        | Let(name, bind, body, s) ->
            let bind' = lift localVars bind
            // Let-Lambda bindings become KnownFuncs in elaboration — accessible from any
            // func.func without capture. Only non-function Let bindings are local values.
            // Let-Var (from open/alias) also becomes KnownFuncs when the target is a known function.
            let isFunction =
                match bind with
                | Lambda _ | LambdaAnnot _ -> true
                | Annot(Lambda _, _, _) | Annot(LambdaAnnot _, _, _) -> true
                | Var _ -> true  // open-alias: Let(shortName, Var(qualifiedName)) — references a KnownFunc
                | _ -> false
            let localVars' = if isFunction then localVars else Set.add name localVars
            Let(name, bind', lift localVars' body, s)
        | Lambda(param, body, s) ->
            Lambda(param, lift (Set.add param localVars) body, s)
        | LambdaAnnot(param, ty, body, s) ->
            LambdaAnnot(param, ty, lift (Set.add param localVars) body, s)
        | LetMut(name, init, body, s) ->
            LetMut(name, lift localVars init, lift (Set.add name localVars) body, s)
        | LetPat(pat, bind, body, s) ->
            let patVars =
                let rec extract p = match p with
                                    | VarPat(n, _) -> Set.singleton n
                                    | TuplePat(ps, _) -> ps |> List.map extract |> Set.unionMany
                                    | ConsPat(h, t, _) -> Set.union (extract h) (extract t)
                                    | ConstructorPat(_, Some inner, _) -> extract inner
                                    | _ -> Set.empty
                extract pat
            LetPat(pat, lift localVars bind, lift (Set.union localVars patVars) body, s)
        // Recursive traversal
        | If(c, t, e, s) -> If(lift localVars c, lift localVars t, lift localVars e, s)
        | App(fn, arg, s) -> App(lift localVars fn, lift localVars arg, s)
        | Match(scr, clauses, s) ->
            Match(lift localVars scr,
                  clauses |> List.map (fun (p, g, b) ->
                      (p, Option.map (lift localVars) g, lift localVars b)), s)
        | TryWith(body, clauses, s) ->
            TryWith(lift localVars body,
                    clauses |> List.map (fun (p, g, b) ->
                        (p, Option.map (lift localVars) g, lift localVars b)), s)
        | Tuple(es, s) -> Tuple(List.map (lift localVars) es, s)
        | List(es, s) -> List(List.map (lift localVars) es, s)
        | Cons(h, t, s) -> Cons(lift localVars h, lift localVars t, s)
        | Add(a, b, s) -> Add(lift localVars a, lift localVars b, s)
        | Subtract(a, b, s) -> Subtract(lift localVars a, lift localVars b, s)
        | Multiply(a, b, s) -> Multiply(lift localVars a, lift localVars b, s)
        | Divide(a, b, s) -> Divide(lift localVars a, lift localVars b, s)
        | Modulo(a, b, s) -> Modulo(lift localVars a, lift localVars b, s)
        | Negate(e, s) -> Negate(lift localVars e, s)
        | Equal(a, b, s) -> Equal(lift localVars a, lift localVars b, s)
        | NotEqual(a, b, s) -> NotEqual(lift localVars a, lift localVars b, s)
        | LessThan(a, b, s) -> LessThan(lift localVars a, lift localVars b, s)
        | GreaterThan(a, b, s) -> GreaterThan(lift localVars a, lift localVars b, s)
        | LessEqual(a, b, s) -> LessEqual(lift localVars a, lift localVars b, s)
        | GreaterEqual(a, b, s) -> GreaterEqual(lift localVars a, lift localVars b, s)
        | And(a, b, s) -> And(lift localVars a, lift localVars b, s)
        | Or(a, b, s) -> Or(lift localVars a, lift localVars b, s)
        | Constructor(n, Some arg, s) -> Constructor(n, Some (lift localVars arg), s)
        | Annot(e, ty, s) -> Annot(lift localVars e, ty, s)
        | RecordExpr(tn, fields, s) -> RecordExpr(tn, fields |> List.map (fun (n, e) -> (n, lift localVars e)), s)
        | FieldAccess(e, fn, s) -> FieldAccess(lift localVars e, fn, s)
        | RecordUpdate(src, fields, s) -> RecordUpdate(lift localVars src, fields |> List.map (fun (n, e) -> (n, lift localVars e)), s)
        | SetField(e, fn, v, s) -> SetField(lift localVars e, fn, lift localVars v, s)
        | Raise(e, s) -> Raise(lift localVars e, s)
        | Range(start, stop, step, s) -> Range(lift localVars start, lift localVars stop, Option.map (lift localVars) step, s)
        | Assign(n, v, s) -> Assign(n, lift localVars v, s)
        | WhileExpr(c, b, s) -> WhileExpr(lift localVars c, lift localVars b, s)
        | ForExpr(v, start, isTo, stop, body, s) ->
            ForExpr(v, lift localVars start, isTo, lift localVars stop, lift (Set.add v localVars) body, s)
        | ForInExpr(v, coll, body, s) ->
            let vn = match v with VarPat(n, _) -> n | _ -> "_"
            ForInExpr(v, lift localVars coll, lift (Set.add vn localVars) body, s)
        | IndexGet(c, i, s) -> IndexGet(lift localVars c, lift localVars i, s)
        | IndexSet(c, i, v, s) -> IndexSet(lift localVars c, lift localVars i, lift localVars v, s)
        | StringSliceExpr(str, start, stop, s) -> StringSliceExpr(lift localVars str, lift localVars start, Option.map (lift localVars) stop, s)
        | ListCompExpr(v, coll, body, s) -> ListCompExpr(v, lift localVars coll, lift localVars body, s)
        | _ -> e

    lift Set.empty expr
