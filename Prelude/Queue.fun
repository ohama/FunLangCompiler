module Queue =
    let create () = queue_create ()
    let enqueue (q : 'a queue) (v : 'a) : unit = queue_enqueue q v
    let dequeue (q : 'a queue) (u : unit) : 'a = queue_dequeue q u
    let count (q : 'a queue) : int = queue_count q
