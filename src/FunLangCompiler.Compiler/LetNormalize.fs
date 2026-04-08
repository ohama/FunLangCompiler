/// Let-Normalization — AST-to-AST transformation pass (partial ANF).
///
/// Extracts sub-expressions that produce control flow (If, Match, And, Or, TryWith)
/// from operand positions of compound expressions (Cons, Add, Tuple, etc.).
/// This ensures that terminator ops (cf.cond_br, cf.br) only appear at block ends.
///
/// Before:
///   acc :: (match xs with | [] -> [] | h :: t -> ...)
///
/// After:
///   let __anf_N = match xs with | [] -> [] | h :: t -> ...
///   acc :: __anf_N
///
/// The pass is conservative: only extracts sub-expressions that are known to produce
/// control flow. Simple expressions (Var, Number, App, Lambda, etc.) are left in place.
module LetNormalize

open Ast

let private counter = ref 0

let private freshName () =
    let n = counter.Value
    counter.Value <- n + 1
    sprintf "__anf_%d" n

/// Returns true if an expression may produce control flow (multiple basic blocks).
/// Also detects Let/LetPat/LetMut wrappers around complex bodies, since they pass through
/// the terminator from the inner expression.
let rec private isComplexExpr (e: Expr) : bool =
    match e with
    | If _ | Match _ | And _ | Or _ | TryWith _ -> true
    | Let(_, bind, body, _) | LetPat(_, bind, body, _) | LetMut(_, bind, body, _) -> isComplexExpr bind || isComplexExpr body
    | _ -> false

/// Wrap a complex expression in a let-binding, returning the bound variable.
/// If the expression is simple, return it unchanged.
let private letBind (span: Span) (e: Expr) : Expr * (Expr -> Expr) =
    if isComplexExpr e then
        let name = freshName ()
        (Var(name, span), fun body -> Let(name, e, body, span))
    else
        (e, id)

/// Normalize all compound expressions so their operands are never complex.
let rec private norm (e: Expr) : Expr =
    match e with
    // Binary arithmetic/comparison — normalize both operands
    | Add(a, b, s) ->
        let a' = norm a
        let b' = norm b
        let (bv, bwrap) = letBind s b'
        bwrap (Add(a', bv, s))
    | Subtract(a, b, s) ->
        let a' = norm a
        let b' = norm b
        let (bv, bwrap) = letBind s b'
        bwrap (Subtract(a', bv, s))
    | Multiply(a, b, s) ->
        let a' = norm a
        let b' = norm b
        let (bv, bwrap) = letBind s b'
        bwrap (Multiply(a', bv, s))
    | Divide(a, b, s) ->
        let a' = norm a
        let b' = norm b
        let (bv, bwrap) = letBind s b'
        bwrap (Divide(a', bv, s))
    | Modulo(a, b, s) ->
        let a' = norm a
        let b' = norm b
        let (bv, bwrap) = letBind s b'
        bwrap (Modulo(a', bv, s))

    // Cons — the key case for `acc :: (match ...)`
    | Cons(head, tail, s) ->
        let head' = norm head
        let tail' = norm tail
        let (hv, hwrap) = letBind s head'
        let (tv, twrap) = letBind s tail'
        hwrap (twrap (Cons(hv, tv, s)))

    // Tuple — normalize each element
    | Tuple(elems, s) ->
        let elems' = List.map norm elems
        let (vals, wraps) = elems' |> List.map (fun e -> letBind s e) |> List.unzip
        let applyAll body = List.foldBack (fun w acc -> w acc) wraps body
        applyAll (Tuple(vals, s))

    // Comparison — normalize operands
    | Equal(a, b, s) ->
        let a' = norm a
        let b' = norm b
        let (av, awrap) = letBind s a'
        let (bv, bwrap) = letBind s b'
        awrap (bwrap (Equal(av, bv, s)))
    | NotEqual(a, b, s) ->
        let a' = norm a
        let b' = norm b
        let (av, awrap) = letBind s a'
        let (bv, bwrap) = letBind s b'
        awrap (bwrap (NotEqual(av, bv, s)))
    | LessThan(a, b, s) ->
        let a' = norm a
        let b' = norm b
        let (av, awrap) = letBind s a'
        let (bv, bwrap) = letBind s b'
        awrap (bwrap (LessThan(av, bv, s)))
    | GreaterThan(a, b, s) ->
        let a' = norm a
        let b' = norm b
        let (av, awrap) = letBind s a'
        let (bv, bwrap) = letBind s b'
        awrap (bwrap (GreaterThan(av, bv, s)))
    | LessEqual(a, b, s) ->
        let a' = norm a
        let b' = norm b
        let (av, awrap) = letBind s a'
        let (bv, bwrap) = letBind s b'
        awrap (bwrap (LessEqual(av, bv, s)))
    | GreaterEqual(a, b, s) ->
        let a' = norm a
        let b' = norm b
        let (av, awrap) = letBind s a'
        let (bv, bwrap) = letBind s b'
        awrap (bwrap (GreaterEqual(av, bv, s)))

    // Negate
    | Negate(e, s) ->
        let e' = norm e
        let (ev, ewrap) = letBind s e'
        ewrap (Negate(ev, s))

    // App — normalize arg (func is usually Var or already simple)
    | App(func, arg, s) ->
        let func' = norm func
        let arg' = norm arg
        let (av, awrap) = letBind s arg'
        awrap (App(func', av, s))

    // Constructor with arg
    | Constructor(name, Some arg, s) ->
        let arg' = norm arg
        let (av, awrap) = letBind s arg'
        awrap (Constructor(name, Some av, s))

    // Record fields
    | RecordExpr(tn, fields, s) ->
        let fields' = fields |> List.map (fun (n, e) -> (n, norm e))
        let bound = fields' |> List.map (fun (n, e) -> let (v, w) = letBind s e in ((n, v), w))
        let pairs = bound |> List.map fst
        let wraps = bound |> List.map snd
        let applyAll body = List.foldBack (fun w acc -> w acc) wraps body
        applyAll (RecordExpr(tn, pairs, s))

    // SetField
    | SetField(recExpr, fieldName, v, s) ->
        SetField(norm recExpr, fieldName, norm v, s)

    // RecordUpdate
    | RecordUpdate(src, overrides, s) ->
        RecordUpdate(norm src, overrides |> List.map (fun (n, e) -> (n, norm e)), s)

    // Raise
    | Raise(e, s) -> Raise(norm e, s)

    // Assign
    | Assign(n, v, s) -> Assign(n, norm v, s)

    // Index ops
    | IndexGet(c, i, s) -> IndexGet(norm c, norm i, s)
    | IndexSet(c, i, v, s) -> IndexSet(norm c, norm i, norm v, s)

    // String slice
    | StringSliceExpr(str, start, stop, s) ->
        StringSliceExpr(norm str, norm start, Option.map norm stop, s)

    // Binding forms — recurse into sub-expressions
    | Let(name, bind, body, s) -> Let(name, norm bind, norm body, s)
    | LetPat(pat, bind, body, s) -> LetPat(pat, norm bind, norm body, s)
    | LetMut(name, init, body, s) -> LetMut(name, norm init, norm body, s)
    | LetRec(bindings, body, s) ->
        let bindings' = bindings |> List.map (fun (n, p, pt, b, bs) -> (n, p, pt, norm b, bs))
        LetRec(bindings', norm body, s)
    | Lambda(p, body, s) -> Lambda(p, norm body, s)
    | LambdaAnnot(p, ty, body, s) -> LambdaAnnot(p, ty, norm body, s)

    // Control flow — recurse into branches
    | If(c, t, e, s) -> If(norm c, norm t, norm e, s)
    | And(a, b, s) -> And(norm a, norm b, s)
    | Or(a, b, s) -> Or(norm a, norm b, s)
    | Match(scr, clauses, s) ->
        Match(norm scr, clauses |> List.map (fun (p, g, b) -> (p, Option.map norm g, norm b)), s)
    | TryWith(body, clauses, s) ->
        TryWith(norm body, clauses |> List.map (fun (p, g, b) -> (p, Option.map norm g, norm b)), s)

    // Loops
    | WhileExpr(c, b, s) -> WhileExpr(norm c, norm b, s)
    | ForExpr(v, start, isTo, stop, body, s) -> ForExpr(v, norm start, isTo, norm stop, norm body, s)
    | ForInExpr(v, coll, body, s) -> ForInExpr(v, norm coll, norm body, s)
    | ListCompExpr(v, coll, body, s) -> ListCompExpr(v, norm coll, norm body, s)

    // Collection literals
    | List(elems, s) -> List(List.map norm elems, s)
    | Range(start, stop, step, s) -> Range(norm start, norm stop, Option.map norm step, s)

    // Annotations
    | Annot(e, ty, s) -> Annot(norm e, ty, s)
    | FieldAccess(e, fieldName, s) -> FieldAccess(norm e, fieldName, s)

    // Leaves — no transformation needed
    | _ -> e

/// Apply let-normalization to an expression tree.
/// Resets the counter for each invocation.
let normalizeExpr (expr: Expr) : Expr =
    counter.Value <- 0
    norm expr
