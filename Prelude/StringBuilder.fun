module StringBuilder =
    let create () = stringbuilder_create ()
    let add (sb : stringbuilder) (s : 'a) : stringbuilder = stringbuilder_append sb s
    let toString (sb : stringbuilder) : string = stringbuilder_tostring sb
