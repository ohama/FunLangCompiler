module Result =
    type Result 'a 'b = Ok of 'a | Error of 'b
    let map f r = match r with | Ok x -> Ok (f x) | Error e -> Error e
    let bind f r = match r with | Ok x -> f x | Error e -> Error e
    let mapError f r = match r with | Ok x -> Ok x | Error e -> Error (f e)
    let defaultValue def r = match r with | Ok x -> x | Error _ -> def
    let toOption r = match r with | Ok x -> Some x | Error _ -> None
