# Corporation inventory ownership

[Documentation index](README.md) / [Corporation roadmap](corporations.md) / [Protocol record](protocol/corporations.md#corporation-inventory-ownership-storage)

World schema 78 registers pilot and player-corporation IDs as valid inventory owners. Insert triggers register new pilots/corporations in the same transaction as their creation. Inventory rows and inactive hulls may reference either owner kind. The active-ship foreign key still requires a hull with the selected pilot's own owner ID.

Existing saves rebuild `WorldItem` and `WorldShip` together from the current authored EF schema. This runs after older migrations have reached schema 77, on a held startup connection with foreign-key enforcement temporarily disabled. SQLite's legacy ALTER mode keeps other tables' fitting triggers valid while replacements take the original names. Rows, custom indexes/triggers, autoincrement sequence high-water marks and child references are retained. A foreign-key check must pass before schema 78 commits; failure rolls back both replacements and leaves the save retryable at 77. Enforcement and ALTER settings are restored on the connection.

This is storage support for the office/asset milestone. It exposes no corporation inventory call or new permission by itself. Personal list/ship operations retain their owner filters. Corporate transfers must enforce authenticated membership, station/office reachability and division rights before changing ownership.

Automated coverage includes disk/resident reopen, corporation ownership, new-owner registration, unknown-owner rejection, active-ship rejection, version-77 replacement with existing fitting triggers/indexes and corruption rollback. No normal runtime save is modified by agent validation.
