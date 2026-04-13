module Option =
    type Option 'a = None | Some of 'a
    let optionMap (f : 'a -> 'b) (opt : 'a option) : 'b option = match opt with | Some x -> Some (f x) | None -> None
    let optionBind (f : 'a -> 'b option) (opt : 'a option) : 'b option = match opt with | Some x -> f x | None -> None
    let optionDefault (def : 'a) (opt : 'a option) : 'a = match opt with | Some x -> x | None -> def
    let isSome (opt : 'a option) : bool = match opt with | Some _ -> true | None -> false
    let isNone (opt : 'a option) : bool = match opt with | Some _ -> false | None -> true
    let (<|>) (a : 'a option) (b : 'a option) : 'a option = match a with | Some x -> Some x | None -> b
    let optionIter (f : 'a -> unit) (opt : 'a option) : unit = match opt with | Some x -> f x | None -> ()
    let optionFilter (pred : 'a -> bool) (opt : 'a option) : 'a option = match opt with | Some x -> if pred x then Some x else None | None -> None
    let optionDefaultValue (def : 'a) (opt : 'a option) : 'a = match opt with | Some x -> x | None -> def
    let optionIsSome (opt : 'a option) : bool = match opt with | Some _ -> true | None -> false
    let optionIsNone (opt : 'a option) : bool = match opt with | Some _ -> false | None -> true

open Option
