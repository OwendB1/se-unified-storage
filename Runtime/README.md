# Shared game adapters

Both plugin targets compile this directory. Existing `ClientPlugin.*` namespaces are retained to avoid a broad API rename.

These adapters read game definitions, inventories and group membership. They do not depend on either plugin entry point, GUI controls, or a local-player singleton. They are **not** the pure planning layer: distribution arithmetic, ore ordering, blueprint selection, co-product production assignment and loadout gaps live in `Shared/Planning`. Runtime adapters supply immutable values and resolve the returned choices against game objects.

Client and server executors remain separate. The server never accepts these adapters' client-side snapshots or allocations as authority; it resolves selectors and builds its own live state before execution.
