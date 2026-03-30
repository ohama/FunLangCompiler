module Option =
    type Option 'a = None | Some of 'a
    let optionMap f = fun opt -> match opt with | Some x -> Some (f x) | None -> None
    let optionBind f = fun opt -> match opt with | Some x -> f x | None -> None
    let optionDefault def = fun opt -> match opt with | Some x -> x | None -> def
    let isSome opt = match opt with | Some _ -> true | None -> false
    let isNone opt = match opt with | Some _ -> false | None -> true
    let (<|>) a b = match a with | Some x -> Some x | None -> b
    let optionIter f = fun opt -> match opt with | Some x -> f x | None -> ()
    let optionFilter pred = fun opt -> match opt with | Some x -> if pred x then Some x else None | None -> None
    let optionDefaultValue def = fun opt -> match opt with | Some x -> x | None -> def
    let optionIsSome opt = match opt with | Some _ -> true | None -> false
    let optionIsNone opt = match opt with | Some _ -> false | None -> true

open Option
