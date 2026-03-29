module StringBuilder =
    let create () = stringbuilder_create ()
    let add sb s  = stringbuilder_append sb s
    let toString sb = stringbuilder_tostring sb
