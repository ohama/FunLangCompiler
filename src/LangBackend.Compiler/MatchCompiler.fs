/// Jules Jacobs pattern matching compilation algorithm.
/// Compiles pattern match expressions into binary decision trees
/// that test one constructor at a time: match# a with | C(a1,...,an) => [A] | _ => [B]
///
/// Key ideas from the paper:
/// 1. Clause representation: {a1 is pat1, a2 is pat2, ...} => body
/// 2. Variable elimination: push variable/wildcard bindings into the body as let x = a
/// 3. Branching heuristic: pick the test from the first clause present in the max number of clauses
/// 4. Splitting: for selected test a is C(P1,...,Pn):
///    - Case (a): clause has a is C(Q1,...,Qn) → expand sub-patterns → add to [A]
///    - Case (b): clause has a is D(...) where D≠C → add to [B]
///    - Case (c): clause has no test for a → add to both [A] and [B]
/// 5. Base cases: empty clauses → Fail; first clause has no tests → Leaf
/// 6. Recurse on [A] and [B]
module MatchCompiler

open Ast

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

/// How to access a value: the scrutinee root or a sub-field extracted via GEP.
type Accessor =
    | Root of string       // a top-level SSA name (e.g. "%t3")
    | Field of Accessor * int  // field i of an accessor (GEP + load)

/// Constructor tags for our language's runtime representation.
type CtorTag =
    | IntLit of int
    | BoolLit of bool
    | StringLit of string
    | ConsCtor            // h :: t  (2 fields: head, tail)
    | NilCtor             // []      (0 fields)
    | TupleCtor of int    // tuple of arity n (n fields)
    | AdtCtor of name: string * tag: int * arity: int
                          // ADT constructor: name for equality, tag placeholder (Phase 17), arity
    | RecordCtor of fields: string list
                          // Record constructor: sorted field names (canonical identity)

/// A single test: scrutinee must match a constructor with sub-patterns.
type TestPattern =
    | CtorTest of CtorTag * Pattern list

type Test = {
    Scrutinee: Accessor
    Pattern: TestPattern
}

/// Internal clause representation for the algorithm.
type Clause = {
    Tests: Test list
    Bindings: (string * Accessor) list
    BodyIndex: int
    HasGuard: bool   // true when the arm has a when-guard
}

/// Decision tree — the output of compilation.
type DecisionTree =
    | Leaf of bindings: (string * Accessor) list * bodyIndex: int
    | Fail
    | Switch of scrutinee: Accessor * constructor: CtorTag * args: Accessor list
               * ifMatch: DecisionTree * ifNoMatch: DecisionTree
    | Guard of bindings: (string * Accessor) list * bodyIndex: int * ifFalse: DecisionTree
    // Guard: resolve bindings, evaluate when-guard for clauses.[bodyIndex];
    //        if guard true → execute body (Leaf-like); if false → try ifFalse subtree

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/// Return the number of sub-fields for a given constructor tag.
let private ctorArity (tag: CtorTag) : int =
    match tag with
    | IntLit _    -> 0
    | BoolLit _   -> 0
    | StringLit _ -> 0
    | NilCtor     -> 0
    | ConsCtor    -> 2
    | TupleCtor n -> n
    | AdtCtor(_, _, arity) -> arity
    | RecordCtor fields    -> List.length fields

// ---------------------------------------------------------------------------
// Pattern desugaring: Ast.Pattern → (Test list * bindings)
// ---------------------------------------------------------------------------

/// Convert an Ast.Pattern applied to a given Accessor into a list of Tests
/// and variable bindings.  Recurses into nested patterns.
let rec desugarPattern (acc: Accessor) (pat: Pattern) : Test list * (string * Accessor) list =
    match pat with
    | WildcardPat _ ->
        ([], [])
    | VarPat (name, _) ->
        ([], [(name, acc)])
    | ConstPat (IntConst n, _) ->
        ([{ Scrutinee = acc; Pattern = CtorTest(IntLit n, []) }], [])
    | ConstPat (BoolConst b, _) ->
        ([{ Scrutinee = acc; Pattern = CtorTest(BoolLit b, []) }], [])
    | ConstPat (StringConst s, _) ->
        ([{ Scrutinee = acc; Pattern = CtorTest(StringLit s, []) }], [])
    | ConstPat (CharConst c, _) ->
        ([{ Scrutinee = acc; Pattern = CtorTest(IntLit (int c), []) }], [])
    | EmptyListPat _ ->
        ([{ Scrutinee = acc; Pattern = CtorTest(NilCtor, []) }], [])
    | ConsPat (hPat, tPat, _) ->
        // The cons constructor test with two sub-patterns
        ([{ Scrutinee = acc; Pattern = CtorTest(ConsCtor, [hPat; tPat]) }], [])
    | TuplePat (pats, _) ->
        let n = List.length pats
        // A tuple is always a constructor match (TupleCtor n) with n sub-patterns
        ([{ Scrutinee = acc; Pattern = CtorTest(TupleCtor n, []) }], [])
        // Actually, tuples are *always* structural matches (they always match),
        // but we need to descend into sub-patterns. We treat TupleCtor as an
        // always-matching constructor where the "ifNoMatch" path is unreachable.
        // However, to extract bindings from sub-patterns we need to desugar them.
        // Let's expand inline: the tuple test itself, plus sub-pattern tests.
        |> ignore  // discard the above
        let subResults =
            pats |> List.mapi (fun i subPat ->
                desugarPattern (Field(acc, i)) subPat
            )
        let subTests = subResults |> List.collect fst
        let subBinds = subResults |> List.collect snd
        (subTests, subBinds)
    | ConstructorPat _ ->
        failwith "MatchCompiler: ConstructorPat not yet supported in backend"
    | RecordPat _ ->
        failwith "MatchCompiler: RecordPat not yet supported in backend"
    | OrPat _ ->
        failwith "MatchCompiler: OrPat not yet supported in backend"

// ---------------------------------------------------------------------------
// Algorithm core
// ---------------------------------------------------------------------------

/// Step 2: Substitute out variable/wildcard equations.
/// Any test whose pattern is effectively a variable (no constructor) gets
/// pushed into bindings.  Since we already handle VarPat/WildcardPat in
/// desugarPattern by producing bindings directly (not tests), this function
/// is a no-op for now.  It exists for completeness with the paper.
let private substVarEqs (clause: Clause) : Clause = clause

/// Step 3: Branching heuristic.
/// From the first clause's tests, select the one that appears in the
/// maximum number of other clauses (by matching on (Scrutinee, CtorTag)).
let private branchingHeuristic (clauses: Clause list) : Test =
    match clauses with
    | [] -> failwith "branchingHeuristic: no clauses"
    | first :: rest ->
        match first.Tests with
        | [] -> failwith "branchingHeuristic: first clause has no tests"
        | tests ->
            // For each test in the first clause, count how many of the *other*
            // clauses also have a test on the same (Scrutinee, same CtorTag).
            let scoreTest (t: Test) =
                let tag = match t.Pattern with CtorTest(tag, _) -> tag
                rest |> List.sumBy (fun c ->
                    if c.Tests |> List.exists (fun t2 ->
                        t2.Scrutinee = t.Scrutinee &&
                        (match t2.Pattern with CtorTest(tag2, _) -> tag2 = tag))
                    then 1 else 0
                )
            tests
            |> List.maxBy scoreTest

/// Step 4: Split clauses given a selected test.
/// Returns (argsAccessors, matchClauses, noMatchClauses).
let private splitClauses (selected: Test) (clauses: Clause list)
    : Accessor list * Clause list * Clause list =
    let selAcc = selected.Scrutinee
    let (selTag, selSubPats) =
        match selected.Pattern with
        | CtorTest(tag, subPats) -> (tag, subPats)
    let arity = ctorArity selTag
    // Generate accessor paths for constructor arguments
    let argAccessors = List.init arity (fun i -> Field(selAcc, i))

    let matchClauses = ResizeArray<Clause>()
    let noMatchClauses = ResizeArray<Clause>()

    for clause in clauses do
        // Find if this clause has a test on the same scrutinee
        let testOnSameScrutinee =
            clause.Tests |> List.tryFind (fun t -> t.Scrutinee = selAcc)
        match testOnSameScrutinee with
        | None ->
            // Case (c): no test for this scrutinee → add to BOTH
            matchClauses.Add(clause)
            noMatchClauses.Add(clause)
        | Some t ->
            let (tTag, tSubPats) =
                match t.Pattern with CtorTest(tag, pats) -> (tag, pats)
            let remainingTests = clause.Tests |> List.filter (fun t2 -> t2 <> t)
            if tTag = selTag then
                // Case (a): same constructor → expand sub-patterns
                let expandedResults =
                    tSubPats |> List.mapi (fun i subPat ->
                        desugarPattern argAccessors.[i] subPat
                    )
                let expandedTests = expandedResults |> List.collect fst
                let expandedBinds = expandedResults |> List.collect snd
                matchClauses.Add({
                    Tests = remainingTests @ expandedTests
                    Bindings = clause.Bindings @ expandedBinds
                    BodyIndex = clause.BodyIndex
                    HasGuard = clause.HasGuard
                })
            else
                // Case (b): different constructor → add to noMatch UNCHANGED
                // The test is kept because it hasn't been resolved yet; it will
                // be resolved in a future recursive step.
                noMatchClauses.Add(clause)

    (argAccessors, Seq.toList matchClauses, Seq.toList noMatchClauses)

/// Main recursive decision tree generator.
let rec private genMatch (clauses: Clause list) : DecisionTree =
    // Apply variable substitution to all clauses
    let clauses = clauses |> List.map substVarEqs

    match clauses with
    | [] ->
        // No clauses left → match failure
        Fail
    | first :: _ ->
        if first.Tests.IsEmpty then
            // First clause has no more tests → it matches unconditionally
            if first.HasGuard then
                // Guard: evaluate guard; on failure try remaining clauses
                Guard(first.Bindings, first.BodyIndex, genMatch (List.tail clauses))
            else
                Leaf(first.Bindings, first.BodyIndex)
        else
            // Pick the best test via heuristic
            let selected = branchingHeuristic clauses
            let selAcc = selected.Scrutinee
            let selTag = match selected.Pattern with CtorTest(tag, _) -> tag
            // Split all clauses
            let (argAccessors, matchClauses, noMatchClauses) = splitClauses selected clauses
            // Recurse
            let ifMatch   = genMatch matchClauses
            let ifNoMatch = genMatch noMatchClauses
            Switch(selAcc, selTag, argAccessors, ifMatch, ifNoMatch)

// ---------------------------------------------------------------------------
// Public entry point
// ---------------------------------------------------------------------------

/// Compile a list of match arms into a decision tree.
/// Each arm is (Ast.Pattern, hasGuard, bodyIndex) where bodyIndex identifies which
/// arm body to execute.  The scrutinee is given as a root Accessor.
let compile (scrutinee: Accessor) (arms: (Pattern * bool * int) list) : DecisionTree =
    let clauses =
        arms |> List.map (fun (pat, hasGuard, bodyIdx) ->
            let (tests, bindings) = desugarPattern scrutinee pat
            { Tests = tests; Bindings = bindings; BodyIndex = bodyIdx; HasGuard = hasGuard }
        )
    genMatch clauses
