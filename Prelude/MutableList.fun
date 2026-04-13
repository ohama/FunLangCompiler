module MutableList =
    let create () = mutablelist_create ()
    let add (ml : 'a mutablelist) (v : 'a) : unit = mutablelist_add ml v
    let get (ml : 'a mutablelist) (i : int) : 'a = mutablelist_get ml i
    let set (ml : 'a mutablelist) (i : int) (v : 'a) : unit = mutablelist_set ml i v
    let count (ml : 'a mutablelist) : int = mutablelist_count ml
    let toList (ml : 'a mutablelist) : 'a list = mutablelist_tolist ml
