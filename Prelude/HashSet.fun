module HashSet =
    let create () = hashset_create ()
    let add (hs : 'a hashset) (v : 'a) : bool = hashset_add hs v
    let contains (hs : 'a hashset) (v : 'a) : bool = hashset_contains hs v
    let count (hs : 'a hashset) : int = hashset_count hs
    let keys (hs : 'a hashset) : 'a list = hashset_keys hs
    let toList (hs : 'a hashset) : 'a list = hashset_keys hs
