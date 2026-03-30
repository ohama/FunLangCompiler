module Result =
    type Result 'a 'b = Ok of 'a | Error of 'b
    let resultMap f = fun r -> match r with | Ok x -> Ok (f x) | Error e -> Error e
    let resultBind f = fun r -> match r with | Ok x -> f x | Error e -> Error e
    let resultMapError f = fun r -> match r with | Ok x -> Ok x | Error e -> Error (f e)
    let resultDefault def = fun r -> match r with | Ok x -> x | Error _ -> def
    let isOk r = match r with | Ok _ -> true | Error _ -> false
    let isError r = match r with | Ok _ -> false | Error _ -> true
    let resultIter f = fun r -> match r with | Ok x -> f x | Error _ -> ()
    let resultToOption r = match r with | Ok x -> Some x | Error _ -> None
    let resultDefaultValue def = fun r -> match r with | Ok x -> x | Error _ -> def

open Result
