module Queue =
    let create ()      = queue_create ()
    let enqueue q v    = queue_enqueue q v
    let dequeue q u    = queue_dequeue q u
    let count q        = queue_count q
