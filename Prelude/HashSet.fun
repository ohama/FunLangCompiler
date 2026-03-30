module HashSet =
    let create ()     = hashset_create ()
    let add hs v      = hashset_add hs v
    let contains hs v = hashset_contains hs v
    let count hs      = hashset_count hs
