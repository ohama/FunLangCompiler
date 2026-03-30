module MutableList =
    let create ()     = mutablelist_create ()
    let add ml v      = mutablelist_add ml v
    let get ml i      = mutablelist_get ml i
    let set ml i v    = mutablelist_set ml i v
    let count ml      = mutablelist_count ml
