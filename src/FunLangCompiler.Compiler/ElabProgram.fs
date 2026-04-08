/// Program-level elaboration: prePassDecls, module flattening, elaborateProgram entry point.
module ElabProgram

open Ast
open MlirIR
open MatchCompiler
open ElabHelpers
open Elaboration

let private appendReturnIfNeeded (ops: MlirOp list) (retVal: MlirValue) : MlirOp list =
    match List.tryLast ops with
    | Some LlvmUnreachableOp -> ops
    | _ -> ops @ [ReturnOp [retVal]]

/// Phase 88: Append untag ops only if the block does NOT end with unreachable.
let private appendUntagIfSafe (ops: MlirOp list) (untagOps: MlirOp list) : MlirOp list =
    match List.tryLast ops with
    | Some LlvmUnreachableOp -> ops  // unreachable terminates; no untag needed
    | _ -> ops @ untagOps

let elaborateModule (expr: Expr) : MlirModule =
    let env = emptyEnv ()
    let (resultVal, entryOps) = elaborateExpr env expr
    // Phase 88: Convert @main return value to raw I64 for process exit code.
    // I64 (tagged): untag via >> 1; I1 (bool): zext to I64; Ptr: return 0.
    let (exitVal, untagOps) =
        match resultVal.Type with
        | I64 -> emitUntag env resultVal
        | I1  ->
            let ext = { Name = freshName env; Type = I64 }
            (ext, [ArithExtuIOp(ext, resultVal)])
        | _ ->
            let zero = { Name = freshName env; Type = I64 }
            (zero, [ArithConstantOp(zero, 0L)])
    let sideBlocks = env.Blocks.Value
    let allBlocks =
        if sideBlocks.IsEmpty then
            [ { Label = None; Args = []; Body = appendReturnIfNeeded (appendUntagIfSafe entryOps untagOps) exitVal } ]
        else
            let entryBlock = { Label = None; Args = []; Body = entryOps }
            let lastBlock = List.last sideBlocks
            let lastBlockWithReturn = { lastBlock with Body = appendReturnIfNeeded (appendUntagIfSafe lastBlock.Body untagOps) exitVal }
            let sideBlocksPatched = (List.take (sideBlocks.Length - 1) sideBlocks) @ [lastBlockWithReturn]
            entryBlock :: sideBlocksPatched
    // Phase 38: %arg0/%arg1 match Printer's func param naming convention
    let argcVal    = { Name = "%arg0"; Type = I64 }
    let argvVal    = { Name = "%arg1"; Type = Ptr }
    let initArgsOp = LlvmCallVoidOp("@lang_init_args", [argcVal; argvVal])
    let gcInitOp = LlvmCallVoidOp("@GC_init", [])
    let allBlocksWithGC =
        match allBlocks with
        | [] -> allBlocks
        | entryBlock :: rest ->
            { entryBlock with Body = initArgsOp :: gcInitOp :: entryBlock.Body } :: rest
    let mainFunc : FuncOp =
        { Name        = "@main"
          InputTypes  = [I64; Ptr]
          ReturnType  = Some exitVal.Type
          Body        = { Blocks = allBlocksWithGC }
          IsLlvmFunc  = false }
    let globals =
        (env.Globals.Value |> List.map (fun (name, value) -> StringConstant(name, value)))
        @ (env.TplGlobals.Value |> List.map MutablePtrGlobal)
    let externalFuncs = [
        { ExtName = "@GC_init";              ExtParams = [];         ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@GC_malloc";            ExtParams = [I64];      ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@printf";               ExtParams = [Ptr];      ExtReturn = Some I32; IsVarArg = true;  Attrs = [] }
        { ExtName = "@strcmp";               ExtParams = [Ptr; Ptr]; ExtReturn = Some I32; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_generic_eq";      ExtParams = [Ptr; Ptr]; ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
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
        { ExtName = "@lang_hashtable_create";      ExtParams = [];               ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_get";         ExtParams = [Ptr; I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_set";         ExtParams = [Ptr; I64; I64];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_containsKey"; ExtParams = [Ptr; I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_remove";      ExtParams = [Ptr; I64];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_keys";           ExtParams = [Ptr];            ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_trygetvalue";    ExtParams = [Ptr; I64];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_index_get_str";             ExtParams = [Ptr; Ptr];   ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_index_set_str";             ExtParams = [Ptr; Ptr; I64]; ExtReturn = None;  IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_iter";            ExtParams = [Ptr; Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_map";             ExtParams = [Ptr; Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_fold";            ExtParams = [Ptr; I64; Ptr];  ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_init";            ExtParams = [I64; Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_index_get";             ExtParams = [Ptr; I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_index_set";             ExtParams = [Ptr; I64; I64];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_for_in";                ExtParams = [Ptr; Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_for_in_list";           ExtParams = [Ptr; Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_for_in_array";          ExtParams = [Ptr; Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_file_read";    ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_file_write";   ExtParams = [Ptr; Ptr];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_file_append";  ExtParams = [Ptr; Ptr];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_file_exists";  ExtParams = [Ptr];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_eprint";       ExtParams = [Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_eprintln";     ExtParams = [Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_read_lines";       ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_write_lines";      ExtParams = [Ptr; Ptr];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_stdin_read_line";  ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_stdin_read_all";   ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_get_env";          ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_get_cwd";          ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_path_combine";     ExtParams = [Ptr; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_dir_files";        ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_endswith";   ExtParams = [Ptr; Ptr];  ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_startswith"; ExtParams = [Ptr; Ptr];  ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_trim";       ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_split";      ExtParams = [Ptr; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_indexof";    ExtParams = [Ptr; Ptr];  ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_replace";    ExtParams = [Ptr; Ptr; Ptr]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_toupper";    ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_tolower";    ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_concat_list";ExtParams = [Ptr; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_char_is_digit";     ExtParams = [I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_char_is_letter";    ExtParams = [I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_char_is_upper";     ExtParams = [I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_char_is_lower";     ExtParams = [I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_char_to_upper";     ExtParams = [I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_char_to_lower";     ExtParams = [I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_list_sort_by";      ExtParams = [Ptr; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_list_of_seq";       ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_sort";        ExtParams = [Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_of_seq";      ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        // Phase 33-01: COL-01 StringBuilder
        { ExtName = "@lang_sb_create";         ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_sb_append";         ExtParams = [Ptr; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_sb_tostring";       ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        // Phase 33-01: COL-02 HashSet
        { ExtName = "@lang_hashset_create";    ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashset_add";       ExtParams = [Ptr; I64];  ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashset_contains";  ExtParams = [Ptr; I64];  ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashset_count";     ExtParams = [Ptr];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashset_keys";      ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        // Phase 33-02: COL-03 Queue
        { ExtName = "@lang_queue_create";      ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_queue_enqueue";     ExtParams = [Ptr; I64];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_queue_dequeue";     ExtParams = [Ptr];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_queue_count";       ExtParams = [Ptr];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        // Phase 33-02: COL-04 MutableList
        { ExtName = "@lang_mlist_create";      ExtParams = [];              ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_mlist_add";         ExtParams = [Ptr; I64];      ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_mlist_get";         ExtParams = [Ptr; I64];      ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_mlist_set";         ExtParams = [Ptr; I64; I64]; ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_mlist_count";       ExtParams = [Ptr];           ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_mlist_to_list";   ExtParams = [Ptr];           ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        // Phase 34-01: LANG-01 String slicing
        { ExtName = "@lang_string_slice"; ExtParams = [Ptr; I64; I64]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        // Phase 66: String character access — s.[i] returns byte at index as i64
        { ExtName = "@lang_string_char_at"; ExtParams = [Ptr; I64]; ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        // Phase 92: C wrappers for length/count/array access
        { ExtName = "@lang_string_length";    ExtParams = [Ptr];            ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_length";     ExtParams = [Ptr];            ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_count";  ExtParams = [Ptr];            ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_get";        ExtParams = [Ptr; I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_set";        ExtParams = [Ptr; I64; I64];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        // Phase 34-02: LANG-02 List comprehension
        { ExtName = "@lang_list_comp"; ExtParams = [Ptr; Ptr]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        // Phase 34-03: LANG-03/04 for-in over Phase 33 collection types
        { ExtName = "@lang_for_in_hashset";   ExtParams = [Ptr; Ptr]; ExtReturn = None; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_for_in_queue";     ExtParams = [Ptr; Ptr]; ExtReturn = None; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_for_in_mlist";     ExtParams = [Ptr; Ptr]; ExtReturn = None; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_for_in_hashtable"; ExtParams = [Ptr; Ptr]; ExtReturn = None; IsVarArg = false; Attrs = [] }
        // Phase 38: CLI argument support
        { ExtName = "@lang_init_args"; ExtParams = [I64; Ptr]; ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_get_args";  ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        // Phase 39: Format string wrappers
        { ExtName = "@lang_sprintf_1i";  ExtParams = [Ptr; I64];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_sprintf_1s";  ExtParams = [Ptr; Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_sprintf_2ii"; ExtParams = [Ptr; I64; I64];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_sprintf_2si"; ExtParams = [Ptr; Ptr; I64];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_sprintf_2is"; ExtParams = [Ptr; I64; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_sprintf_2ss"; ExtParams = [Ptr; Ptr; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
    ]
    { Globals = globals; ExternalFuncs = externalFuncs; Funcs = env.Funcs.Value @ [mainFunc] }

// Phase 16: Declaration pre-pass — scans Decl list and populates TypeEnv, RecordEnv, ExnTags.
// No MLIR IR is emitted in this pass; only F# Map structures are built.
// Phase 25: Made recursive with shared exnCounter so nested modules share the same counter,
// preventing exception tag collisions across module boundaries.
let rec private prePassDecls (exnCounter: int ref) (decls: Ast.Decl list)
    : Map<string, TypeInfo> * Map<string, Map<string, int>> * Map<string, int> * Set<string> =
    let mutable typeEnv  = Map.empty<string, TypeInfo>
    let mutable recordEnv = Map.empty<string, Map<string, int>>
    let mutable exnTags  = Map.empty<string, int>
    let mutable stringFields = Set.empty<string>
    for decl in decls do
        match decl with
        | Ast.Decl.TypeDecl (Ast.TypeDecl(_, _, ctors, _, _)) ->
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
            // Phase 66: Collect string-typed fields for IndexGet dispatch
            for (Ast.RecordFieldDecl(name, fieldType, _, _)) in fields do
                match fieldType with
                | Ast.TEString -> stringFields <- Set.add name stringFields
                | _ -> ()
        | Ast.Decl.ExceptionDecl(name, dataTypeOpt, _) ->
            let tag = exnCounter.Value
            exnCounter.Value <- tag + 1
            exnTags <- Map.add name tag exnTags
            let arity = match dataTypeOpt with Some _ -> 1 | None -> 0
            typeEnv <- Map.add name { Tag = tag; Arity = arity } typeEnv
        | Ast.Decl.ModuleDecl(_, innerDecls, _) ->
            let (innerTypeEnv, innerRecordEnv, innerExnTags, innerStringFields) = prePassDecls exnCounter innerDecls
            typeEnv   <- Map.fold (fun acc k v -> Map.add k v acc) typeEnv   innerTypeEnv
            recordEnv <- Map.fold (fun acc k v -> Map.add k v acc) recordEnv innerRecordEnv
            exnTags   <- Map.fold (fun acc k v -> Map.add k v acc) exnTags   innerExnTags
            stringFields <- Set.union stringFields innerStringFields
        | Ast.Decl.TypeClassDecl _ -> ()   // Phase 52: typeclasses handled in elaborateTypeclasses
        | Ast.Decl.InstanceDecl _ -> ()    // Phase 52: instances handled in elaborateTypeclasses
        | Ast.Decl.DerivingDecl _ -> ()    // Phase 52: deriving handled in elaborateTypeclasses
        | _ -> ()
    (typeEnv, recordEnv, exnTags, stringFields)

// Phase 41/59: collectModuleMembers — first pass scan to build a map from dot-path module key
// to list of underscore-qualified member names.
// e.g., "Outer.Inner" -> ["Outer_Inner_foo"; "Outer_Inner_bar"]
//        "List"        -> ["List_map"; "List_filter"; ...]
// Used by flattenDecls to resolve OpenDecl at compile time.
let private collectModuleMembers (decls: Ast.Decl list) : Map<string, string list> =
    let mutable result = Map.empty<string, string list>
    let rec scan (dotPath: string) (underPath: string) (ds: Ast.Decl list) =
        for d in ds do
            match d with
            | Ast.Decl.ModuleDecl(name, innerDecls, _) ->
                let childDot   = if dotPath   = "" then name else dotPath   + "." + name
                let childUnder = if underPath = "" then name else underPath + "_" + name
                scan childDot childUnder innerDecls
            | Ast.Decl.LetDecl(name, _, _) when underPath <> "" && name <> "_" ->
                let qualifiedName = underPath + "_" + name
                let existing = match Map.tryFind dotPath result with Some xs -> xs | None -> []
                result <- Map.add dotPath (existing @ [qualifiedName]) result
            | Ast.Decl.InfixDecl(_, name, _, _) when underPath <> "" ->
                let qualifiedName = underPath + "_" + name
                let existing = match Map.tryFind dotPath result with Some xs -> xs | None -> []
                result <- Map.add dotPath (existing @ [qualifiedName]) result
            | Ast.Decl.LetRecDecl(bindings, _) when underPath <> "" ->
                for (name, _, _, _, _) in bindings do
                    let qualifiedName = underPath + "_" + name
                    let existing = match Map.tryFind dotPath result with Some xs -> xs | None -> []
                    result <- Map.add dotPath (existing @ [qualifiedName]) result
            | _ -> ()
    scan "" "" decls
    result

// Phase 25: flattenDecls — recursively flatten ModuleDecl into a single Decl list.
// This allows extractMainExpr to see all let bindings regardless of nesting depth.
// Phase 35: Module-qualified naming — when flattening a ModuleDecl, prefix all LetDecl/LetRecDecl
// names with the module name (e.g., `module Option = let map f opt = ...` → `let Option_map f opt = ...`).
// This prevents name collisions when multiple modules define functions with the same name (e.g., map, bind).
// Phase 41: OpenDecl handling — when `open ModuleName` is encountered, emit LetDecl aliases for each
// member of the module, making them available as unqualified names. Uses moduleMembers map from first pass.
let rec private flattenDecls (moduleMembers: Map<string, string list>) (modName: string) (decls: Ast.Decl list) : Ast.Decl list =
    decls |> List.collect (fun d ->
        match d with
        | Ast.Decl.ModuleDecl(name, innerDecls, _) ->
            let childPrefix = if modName = "" then name else modName + "_" + name
            flattenDecls moduleMembers childPrefix innerDecls
        | Ast.Decl.LetDecl(name, body, s) when modName <> "" && name <> "_" ->
            [Ast.Decl.LetDecl(modName + "_" + name, body, s)]
        | Ast.Decl.InfixDecl(attrs, name, body, s) when modName <> "" ->
            [Ast.Decl.InfixDecl(attrs, modName + "_" + name, body, s)]
        | Ast.Decl.LetRecDecl(bindings, s) when modName <> "" ->
            let prefixed = bindings |> List.map (fun (name, param, pt, body, s2) -> (modName + "_" + name, param, pt, body, s2))
            [Ast.Decl.LetRecDecl(prefixed, s)]
        | Ast.Decl.OpenDecl(path, s) when not (List.isEmpty path) ->
            // Phase 59: Join ALL path segments with "." for the map key (e.g., "Outer.Inner").
            // Previously used List.last which only worked for single-level open.
            let openedKey = path |> String.concat "."
            match Map.tryFind openedKey moduleMembers with
            | Some qualifiedNames ->
                let underscorePrefix = openedKey.Replace(".", "_")
                qualifiedNames |> List.map (fun qualifiedName ->
                    let shortName = qualifiedName.Substring(underscorePrefix.Length + 1)
                    Ast.Decl.LetDecl(shortName, Ast.Var(qualifiedName, s), s))
            | None -> []
        | Ast.Decl.OpenDecl(_, _) -> []
        | _ -> [d])

// Phase 16: Extract the main expression from a Decl list.
// LetDecl("_", expr) → use expr as the body; it already contains nested Let bindings.
// LetDecl(name, body) → wrap in Let(name, body, continuation).
// Non-expression decls (TypeDecl, RecordTypeDecl, ExceptionDecl) are skipped.
// LetRecDecl bindings are wrapped in LetRec expressions.
// Phase 25: Flattens ModuleDecl before processing; handles LetPatDecl;
// Phase 41: OpenDecl is handled by flattenDecls (two-pass: collect members first, then flatten).
let private extractMainExpr (moduleSpan: Ast.Span) (decls: Ast.Decl list) : Expr =
    let s = moduleSpan
    let moduleMembers = collectModuleMembers decls
    let flatDecls = flattenDecls moduleMembers "" decls
    let exprDecls =
        flatDecls |> List.filter (fun d ->
            match d with
            | Ast.Decl.LetDecl _ | Ast.Decl.LetRecDecl _ | Ast.Decl.LetMutDecl _
            | Ast.Decl.LetPatDecl _ | Ast.Decl.InfixDecl _ -> true
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
                // Single let rec with no continuation: return 0 as program exit sentinel
                if bindings.IsEmpty then Number(0, s)
                else LetRec(bindings, Number(0, s), s)
            | [Ast.Decl.LetPatDecl(pat, body, sp)] ->
                LetPat(pat, body, Number(0, s), sp)
            | Ast.Decl.LetDecl("_", body, _) :: rest ->
                // let _ = body → evaluate body for side effects, then rest
                // Represent as Let("_", body, continuation)
                Let("_", body, build rest, s)
            | Ast.Decl.LetDecl(name, body, _) :: rest ->
                Let(name, body, build rest, s)
            | Ast.Decl.LetRecDecl(bindings, _) :: rest ->
                if bindings.IsEmpty then build rest
                else LetRec(bindings, build rest, s)
            | Ast.Decl.LetMutDecl(name, body, _) :: rest ->
                LetMut(name, body, build rest, s)
            | Ast.Decl.LetPatDecl(pat, body, sp) :: rest ->
                LetPat(pat, body, build rest, sp)
            | [Ast.Decl.InfixDecl(_, name, body, _)] -> Let(name, body, Var(name, s), s)
            | Ast.Decl.InfixDecl(_, name, body, _) :: rest ->
                Let(name, body, build rest, s)
            | _ :: rest -> build rest
        build exprDecls

// Phase 52: Transform typeclass declarations before main elaboration.
// - TypeClassDecl: removed (class definitions are not needed at runtime)
// - InstanceDecl: each method becomes a plain LetDecl binding
// - DerivingDecl: removed (auto-derivation handled at parse time)
// - ModuleDecl: recurse into inner decls; hoist instance bindings to outer scope
let rec elaborateTypeclasses (decls: Ast.Decl list) : Ast.Decl list =
    // Pass 1: collect constructor info for DerivingDecl expansion
    let ctorMap =
        decls |> List.collect (fun d ->
            match d with
            | Ast.Decl.TypeDecl(Ast.TypeDecl(name, _, ctors, _, _)) ->
                [(name, ctors)]
            | _ -> [])
        |> Map.ofList
    // Pass 2: transform decls
    decls |> List.collect (fun decl ->
        match decl with
        | Ast.Decl.TypeClassDecl _ -> []
        | Ast.Decl.InstanceDecl(_className, _instType, methods, _constraints, span) ->
            methods |> List.map (fun (methodName, methodBody) ->
                Ast.Decl.LetDecl(methodName, methodBody, span))
        | Ast.Decl.ModuleDecl(name, innerDecls, span) ->
            let instanceBindings =
                innerDecls |> List.collect (fun d ->
                    match d with
                    | Ast.Decl.InstanceDecl(_, _, methods, _, ispan) ->
                        methods |> List.map (fun (methodName, methodBody) ->
                            Ast.Decl.LetDecl(methodName, methodBody, ispan))
                    | _ -> [])
            [Ast.Decl.ModuleDecl(name, elaborateTypeclasses innerDecls, span)] @ instanceBindings
        | Ast.Decl.DerivingDecl(typeName, classNames, span) ->
            classNames |> List.collect (fun className ->
                match className with
                | "Show" ->
                    match Map.tryFind typeName ctorMap with
                    | None -> []  // Unknown type: skip silently
                    | Some ctors ->
                        let clauses =
                            ctors |> List.map (fun ctor ->
                                match ctor with
                                | Ast.ConstructorDecl(ctorName, None, _) ->
                                    (Ast.ConstructorPat(ctorName, None, span), None,
                                     Ast.String(ctorName, span))
                                | Ast.ConstructorDecl(ctorName, Some _, _) ->
                                    let vPat = Ast.VarPat("__v", span)
                                    let body = Ast.Add(Ast.String(ctorName + " ", span),
                                                       Ast.App(Ast.Var("show", span), Ast.Var("__v", span), span),
                                                       span)
                                    (Ast.ConstructorPat(ctorName, Some vPat, span), None, body)
                                | Ast.GadtConstructorDecl(ctorName, [], _, _) ->
                                    (Ast.ConstructorPat(ctorName, None, span), None,
                                     Ast.String(ctorName, span))
                                | Ast.GadtConstructorDecl(ctorName, _, _, _) ->
                                    let vPat = Ast.VarPat("__v", span)
                                    let body = Ast.Add(Ast.String(ctorName + " ", span),
                                                       Ast.App(Ast.Var("show", span), Ast.Var("__v", span), span),
                                                       span)
                                    (Ast.ConstructorPat(ctorName, Some vPat, span), None, body))
                        let matchExpr = Ast.Match(Ast.Var("__x", span), clauses, span)
                        let showBody = Ast.Lambda("__x", matchExpr, span)
                        [Ast.Decl.LetDecl("show", showBody, span)]
                | "Eq" ->
                    match Map.tryFind typeName ctorMap with
                    | None -> []
                    | Some ctors ->
                        let clauses =
                            ctors |> List.map (fun ctor ->
                                match ctor with
                                | Ast.ConstructorDecl(ctorName, None, _) ->
                                    let pat = Ast.TuplePat([Ast.ConstructorPat(ctorName, None, span); Ast.ConstructorPat(ctorName, None, span)], span)
                                    (pat, None, Ast.Bool(true, span))
                                | Ast.ConstructorDecl(ctorName, Some _, _) ->
                                    let pat = Ast.TuplePat([Ast.ConstructorPat(ctorName, Some(Ast.VarPat("__a", span)), span);
                                                             Ast.ConstructorPat(ctorName, Some(Ast.VarPat("__b", span)), span)], span)
                                    let body = Ast.App(Ast.App(Ast.Var("eq", span), Ast.Var("__a", span), span), Ast.Var("__b", span), span)
                                    (pat, None, body)
                                | _ -> (Ast.WildcardPat span, None, Ast.Bool(false, span)))
                        let wildcard = (Ast.WildcardPat span, None, Ast.Bool(false, span))
                        let matchExpr = Ast.Match(Ast.Tuple([Ast.Var("__x", span); Ast.Var("__y", span)], span),
                                                   clauses @ [wildcard], span)
                        let eqBody = Ast.Lambda("__x", Ast.Lambda("__y", matchExpr, span), span)
                        [Ast.Decl.LetDecl("eq", eqBody, span)]
                | _ -> [])
        | other -> [other])

// Phase 16: elaborateProgram — new entry point accepting Ast.Module.
// Runs prePassDecls to populate TypeEnv/RecordEnv/ExnTags, then elaborates the program body.
let elaborateProgram (ast: Ast.Module) (annotationMap: Map<Ast.Span, Type.Type>) : MlirModule =
    let decls =
        match ast with
        | Ast.Module(decls, _) | Ast.NamedModule(_, decls, _) -> decls
        | Ast.EmptyModule _ -> []
    let (typeEnv, recordEnv, exnTags, stringFields) = prePassDecls (ref 0) decls
    let mainExpr = extractMainExpr (Ast.moduleSpanOf ast) decls
    // Lambda lifting: nested LetRec captures → explicit parameters (MLIR-style AST rewrite)
    let mainExpr = LambdaLift.liftExpr mainExpr
    // Let-normalization: extract control-flow sub-expressions from operand positions (partial ANF)
    let mainExpr = LetNormalize.normalizeExpr mainExpr
    let env = { emptyEnv () with TypeEnv = typeEnv; RecordEnv = recordEnv; ExnTags = exnTags; StringFields = stringFields; AnnotationMap = annotationMap }
    let (resultVal, entryOps) = elaborateExpr env mainExpr
    // Phase 88: Convert @main return value to raw I64 for process exit code.
    let (exitVal, untagOps) =
        match resultVal.Type with
        | I64 -> emitUntag env resultVal
        | I1  ->
            let ext = { Name = freshName env; Type = I64 }
            (ext, [ArithExtuIOp(ext, resultVal)])
        | _ ->
            let zero = { Name = freshName env; Type = I64 }
            (zero, [ArithConstantOp(zero, 0L)])
    let sideBlocks = env.Blocks.Value
    let allBlocks =
        if sideBlocks.IsEmpty then
            [ { Label = None; Args = []; Body = appendReturnIfNeeded (appendUntagIfSafe entryOps untagOps) exitVal } ]
        else
            let entryBlock = { Label = None; Args = []; Body = entryOps }
            let lastBlock = List.last sideBlocks
            let lastBlockWithReturn = { lastBlock with Body = appendReturnIfNeeded (appendUntagIfSafe lastBlock.Body untagOps) exitVal }
            let sideBlocksPatched = (List.take (sideBlocks.Length - 1) sideBlocks) @ [lastBlockWithReturn]
            entryBlock :: sideBlocksPatched
    // Phase 38: %arg0/%arg1 match Printer's func param naming convention
    let argcVal    = { Name = "%arg0"; Type = I64 }
    let argvVal    = { Name = "%arg1"; Type = Ptr }
    let initArgsOp = LlvmCallVoidOp("@lang_init_args", [argcVal; argvVal])
    let gcInitOp = LlvmCallVoidOp("@GC_init", [])
    let allBlocksWithGC =
        match allBlocks with
        | [] -> allBlocks
        | entryBlock :: rest ->
            { entryBlock with Body = initArgsOp :: gcInitOp :: entryBlock.Body } :: rest
    let mainFunc : FuncOp =
        { Name        = "@main"
          InputTypes  = [I64; Ptr]
          ReturnType  = Some exitVal.Type
          Body        = { Blocks = allBlocksWithGC }
          IsLlvmFunc  = false }
    let globals =
        (env.Globals.Value |> List.map (fun (name, value) -> StringConstant(name, value)))
        @ (env.TplGlobals.Value |> List.map MutablePtrGlobal)
    let externalFuncs = [
        { ExtName = "@GC_init";              ExtParams = [];         ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@GC_malloc";            ExtParams = [I64];      ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@printf";               ExtParams = [Ptr];      ExtReturn = Some I32; IsVarArg = true;  Attrs = [] }
        { ExtName = "@strcmp";               ExtParams = [Ptr; Ptr]; ExtReturn = Some I32; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_generic_eq";      ExtParams = [Ptr; Ptr]; ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
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
        { ExtName = "@lang_hashtable_create";      ExtParams = [];               ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_get";         ExtParams = [Ptr; I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_set";         ExtParams = [Ptr; I64; I64];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_containsKey"; ExtParams = [Ptr; I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_remove";      ExtParams = [Ptr; I64];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_keys";           ExtParams = [Ptr];            ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_trygetvalue";    ExtParams = [Ptr; I64];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_index_get_str";             ExtParams = [Ptr; Ptr];   ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_index_set_str";             ExtParams = [Ptr; Ptr; I64]; ExtReturn = None;  IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_iter";            ExtParams = [Ptr; Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_map";             ExtParams = [Ptr; Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_fold";            ExtParams = [Ptr; I64; Ptr];  ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_init";            ExtParams = [I64; Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_index_get";             ExtParams = [Ptr; I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_index_set";             ExtParams = [Ptr; I64; I64];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_for_in";                ExtParams = [Ptr; Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_for_in_list";           ExtParams = [Ptr; Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_for_in_array";          ExtParams = [Ptr; Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_file_read";    ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_file_write";   ExtParams = [Ptr; Ptr];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_file_append";  ExtParams = [Ptr; Ptr];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_file_exists";  ExtParams = [Ptr];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_eprint";       ExtParams = [Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_eprintln";     ExtParams = [Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_read_lines";       ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_write_lines";      ExtParams = [Ptr; Ptr];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_stdin_read_line";  ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_stdin_read_all";   ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_get_env";          ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_get_cwd";          ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_path_combine";     ExtParams = [Ptr; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_dir_files";        ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_endswith";   ExtParams = [Ptr; Ptr];  ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_startswith"; ExtParams = [Ptr; Ptr];  ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_trim";       ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_split";      ExtParams = [Ptr; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_indexof";    ExtParams = [Ptr; Ptr];  ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_replace";    ExtParams = [Ptr; Ptr; Ptr]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_toupper";    ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_tolower";    ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_concat_list";ExtParams = [Ptr; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_char_is_digit";     ExtParams = [I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_char_is_letter";    ExtParams = [I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_char_is_upper";     ExtParams = [I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_char_is_lower";     ExtParams = [I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_char_to_upper";     ExtParams = [I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_char_to_lower";     ExtParams = [I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_list_sort_by";      ExtParams = [Ptr; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_list_of_seq";       ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_sort";        ExtParams = [Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_of_seq";      ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        // Phase 33-01: COL-01 StringBuilder
        { ExtName = "@lang_sb_create";         ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_sb_append";         ExtParams = [Ptr; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_sb_tostring";       ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        // Phase 33-01: COL-02 HashSet
        { ExtName = "@lang_hashset_create";    ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashset_add";       ExtParams = [Ptr; I64];  ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashset_contains";  ExtParams = [Ptr; I64];  ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashset_count";     ExtParams = [Ptr];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashset_keys";      ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        // Phase 33-02: COL-03 Queue
        { ExtName = "@lang_queue_create";      ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_queue_enqueue";     ExtParams = [Ptr; I64];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_queue_dequeue";     ExtParams = [Ptr];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_queue_count";       ExtParams = [Ptr];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        // Phase 33-02: COL-04 MutableList
        { ExtName = "@lang_mlist_create";      ExtParams = [];              ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_mlist_add";         ExtParams = [Ptr; I64];      ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_mlist_get";         ExtParams = [Ptr; I64];      ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_mlist_set";         ExtParams = [Ptr; I64; I64]; ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_mlist_count";       ExtParams = [Ptr];           ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_mlist_to_list";   ExtParams = [Ptr];           ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        // Phase 34-01: LANG-01 String slicing
        { ExtName = "@lang_string_slice"; ExtParams = [Ptr; I64; I64]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        // Phase 66: String character access — s.[i] returns byte at index as i64
        { ExtName = "@lang_string_char_at"; ExtParams = [Ptr; I64]; ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        // Phase 92: C wrappers for length/count/array access
        { ExtName = "@lang_string_length";    ExtParams = [Ptr];            ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_length";     ExtParams = [Ptr];            ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_count";  ExtParams = [Ptr];            ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_get";        ExtParams = [Ptr; I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_set";        ExtParams = [Ptr; I64; I64];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        // Phase 34-02: LANG-02 List comprehension
        { ExtName = "@lang_list_comp"; ExtParams = [Ptr; Ptr]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        // Phase 34-03: LANG-03/04 for-in over Phase 33 collection types
        { ExtName = "@lang_for_in_hashset";   ExtParams = [Ptr; Ptr]; ExtReturn = None; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_for_in_queue";     ExtParams = [Ptr; Ptr]; ExtReturn = None; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_for_in_mlist";     ExtParams = [Ptr; Ptr]; ExtReturn = None; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_for_in_hashtable"; ExtParams = [Ptr; Ptr]; ExtReturn = None; IsVarArg = false; Attrs = [] }
        // Phase 38: CLI argument support
        { ExtName = "@lang_init_args"; ExtParams = [I64; Ptr]; ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_get_args";  ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        // Phase 39: Format string wrappers
        { ExtName = "@lang_sprintf_1i";  ExtParams = [Ptr; I64];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_sprintf_1s";  ExtParams = [Ptr; Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_sprintf_2ii"; ExtParams = [Ptr; I64; I64];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_sprintf_2si"; ExtParams = [Ptr; Ptr; I64];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_sprintf_2is"; ExtParams = [Ptr; I64; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_sprintf_2ss"; ExtParams = [Ptr; Ptr; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
    ]
    { Globals = globals; ExternalFuncs = externalFuncs; Funcs = env.Funcs.Value @ [mainFunc] }
