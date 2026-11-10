# Architecture and runtime

[Documentation index](README.md)

## Contents

- [Projects and conventions](#projects-and-conventions)
- [Architecture](#architecture)
- [Flight coordination](#flight-coordination)
- [Session performance diagnostics](#session-performance-diagnostics)
- [Resident world and background saves](#resident-world-and-background-saves)

## Projects and conventions

| Project | Responsibility |
| --- | --- |
| `src/Eden.Gameplay` | Typed gameplay rules, state, commands and persistence interfaces. |
| `src/Eden.Infrastructure` | Runtime-only registration metadata, operation scopes, logging and diagnostics. |
| `src/Eden.World` | Authoritative world state, SQLite persistence, catalog access and resident caches. |
| `src/Eden.Server` | Compose services, host connections/endpoints and schedule background work. |
| `src/Eden.Protocol` | Frame and decode messages, negotiate encrypted sessions, and adapt client calls and notifications to typed gameplay operations. |
| `tests/Eden.Protocol.Tests` | Verify wire formats, login, ship movement, docking, and SQLite persistence. |

Gameplay owns skill queue and character rules, fitting/targeting/module calculations, station economics, navigation transitions and NPC motion. World loads typed inputs using each operation's existing world/catalog contexts and commits related writes together. Protocol owns dispatch, immutable wire projections, ordered effect replay and connection-scoped queues. See the [architecture audit milestones](protocol/architecture.md#architecture-audit-milestone-1).

World feature folders follow Gameplay ownership: a feature's stores, schema helpers and specialized catalog readers live together. Saved entities follow the same feature grouping in `Eden.Data.World`. `Combat/Npcs` owns NPC operations and populations; `Combat/Weapons` owns weapon catalog readers. `Storage` holds contexts, connection plumbing, initialization and migration coordination. `Catalog` holds shared access, source registrations and import infrastructure; generated payloads and saved entities live in `Eden.Data`. `Runtime` holds resident state, caches and checkpoints; its `Combat` folder holds resident combat and simulation. Host timers live in `Server/Maintenance`.

Gameplay and Infrastructure depend only on .NET. World and Protocol each reference Gameplay and Infrastructure, with their own database or wire packages; neither references the other. Server composes all four. `Directory.Build.targets` enforces allowed project references, including references unused by compiled code. Generated catalog files are kept out of readability size comparisons.

Startup calls `WorldInitialization.Initialize` explicitly before starting either listener. Resolving `SqliteWorld` only creates a reader; it does not migrate a save. Development tests use `WorldInitialization.Open` for deliberate restart/migration checks and the shared `TestClock` for wall time and elapsed time. Integration coverage includes the production `FlightSessionFactory`; focused simulations supply explicit substitutes for the infrastructure they omit.

`Storage/WorldMigration` is the migration coordinator. Named steps under `Storage/Migrations` retain historical version ranges and receive the original saved version throughout the run. Legacy conversion and initial economy settings precede the numbered upgrades. NPC schema reconciliation precedes backfills that read current entity shapes. The coordinator keeps the explicit commit before inventory ownership conversion and the separate history/economy transaction. Current-version opens still run the required idempotent reconciliation; adding a migration must preserve these ordering boundaries.

Known protocol data uses named immutable models: inventory/dogma state, character cards, flight commands, combat notifications, market and industry replies, skill queues and brain effects. Handlers return `PilotCallResult`, distinguishing successful Python None, unsupported calls and errors. Protocol adapters use named properties such as `DogmaAllInfo.ShipState` and `PilotBrainState.ShipEffects`; explicit `BlueModel.ToBlue()` adapters preserve the native client formats. The low-level Blue codec retains its flexible Python value graph. See the [type-boundary audit and acceptance results](protocol/architecture.md#named-protocol-models-final-audit).

The server uses Microsoft DI to discover services marked with Infrastructure's `[Injectable]` attribute in an explicit Server/Protocol/World assembly list and creates one scope per client connection. Its explicit contract assembly list also includes Gameplay and Infrastructure. Each interface registration resolves the registered concrete instance, preserving scoped and singleton identity. The [contributor guide](../AGENTS.md#services-and-protocol-calls) explains service lifetimes and how to add a protocol handler.

Use the root EditorConfig and shared build properties for new projects. Follow [Microsoft's C# conventions](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions) and keep production and test projects separate as in [Microsoft's project layout guide](https://learn.microsoft.com/en-us/dotnet/core/tutorials/libraries).

## Architecture

Eden.Data owns database models: generated catalog payloads, relational catalog projections, saved world entities and authentication row projections. It references Gameplay for typed domain values and EF Core abstractions for mapping attributes. World owns the contexts, schema configuration, migrations, queries and transactions; Server owns authentication operations. Protocol continues to depend only on Gameplay and Infrastructure. Database model extraction preserves existing table names, columns, keys and relationships and does not advance the world schema version.

Catalog models use `Eden.Data.Catalog`. Saved-world models use `Eden.Data.World` and feature namespaces such as `Eden.Data.World.Inventory`; their files live under `src/Eden.Data/World`. The launcher credential row projection also belongs under the World group because authentication records are saved alongside world accounts.

Fitting, inventory and skill consumers use typed catalog adapters rather than decoding raw rows or repeating relational attribute reads. Static module eligibility, effects, passive/hull/rig/script modifiers and hull skill definitions share the provider's bounded derived-definition cache. Writable import/fixture contexts bypass that cache and invalidate it when disposed. Personal fitted graphs remain in the existing committed-revision resident cache; pilot skills, body/booster state, transactions and checkpoints retain their owners. Geography and other remaining reference readers migrate in later slices.

Combat, repair, propulsion, mining and drone readers also use typed shared catalog inputs. Canonical support decisions and immutable NPC hull/spawn/convoy/mining definitions share the same bounded cache; source generations invalidate them together. Spawn pools index faction/hull membership. A pinned NPC-corporation identity/name index avoids repeated catalog existence/name queries. NPC profiles contain immutable item metadata rather than EF entity graphs. Pilot-dependent fitted attributes and all mutable NPC activity remain owned by the existing world runtime and transactions.

Geography uses immutable station/system/gate indexes and bounded lazy celestial lookups/system pages. The latter share cached record instances and negative results. Static scene and undock definitions are source-scoped; asteroid state remains world-owned. Bookmark and translocation readers consume these snapshots rather than relational catalog entities.

### Database model extraction — November 7, 2026

The first catalog/configuration milestone moves all 146 mapped catalog/world entity types, the catalog-entry projection, the 506 generated catalog payload classes and the launcher credential projection into `Eden.Data`. Feature namespaces now use `Eden.Data.*`. Existing domain values still belong to Gameplay. `CatalogData` registrations, database contexts and EF configuration remain in World. Regeneration writes payload declarations to `src/Eden.Data/Catalog/CatalogModels.g.cs`; use `--models-output` to select a different output when testing the generator. The installed catalog and world files are not modified by this extraction.

**Automated validation:** tool/solution restore, warning-free solution build (3.26 seconds), 123 affected tests (29.67 seconds, plus 1.18 seconds build), four catalog publication/generation Python tests, nine formatting policy tests and formatting verification passed. The recorded .NET filter was `FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~Eden.Protocol.Tests.Persistence.WorldTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.WorldMigrationTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.WorldInitializationTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.InventoryPersistenceTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.LiveWorldTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.DetachedCheckpointTests|FullyQualifiedName~Eden.Protocol.Tests.Combat.RemoteRepairTests|FullyQualifiedName~Eden.Protocol.Tests.Industry.IndustryTests|FullyQualifiedName~Eden.Protocol.Tests.Launcher.LauncherAccountTests`. It followed the exact `DataModelOwnershipTests.RelationalEntitiesBelongToDataWhileContextsRemainInWorld` method test. Before/after metadata comparison confirmed unchanged tables, properties, keys, indexes and relationships for all 146 mapped entities; regenerated payload declarations match the previous declarations byte-for-byte after excluding their namespace/header. The full .NET suite was not run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** None for this ownership-only extraction. Shared catalog caching, complete field typing, feature migration and the graphical configuration editor are subsequent milestones.

Eden.Gameplay owns rules and feature models. Eden.Protocol references Gameplay to translate client requests and results. Eden.World implements catalog and SQLite operations. Eden.Server composes the host and both adapters. Gameplay references neither adapter assembly. Combat health, fitting and module modifiers, targeting, mining yield, propulsion, skill planning, character rules and feature persistence contracts use this boundary. Client effect names and notification construction stay in Protocol. Ship health becomes a native damage-state value only in the Protocol adapter. Database transactions remain owned by World.

Manufacturing illustrates the persistence boundary. The World store loads the recipe, skills, active jobs and materials inside its existing transaction. Gameplay calculates a typed manufacturing plan and validates the client quote. World then consumes materials, reserves the blueprint and saves the job in that transaction. Protocol builds the native reply and queues inventory notifications from the resulting changes. Pure calculations can be tested without a database, and storage tests verify transactional behavior.

Generated catalog arrays and maps use `ImmutableArray` and `ImmutableDictionary`. Sharing a definition therefore does not allow a caller to alter its collections for another reader. This applies to nested values and heterogeneous-value variants as well as dataset roots. `CatalogAbsent` retains its pinned-empty validation. Saved-world EF entities remain mutable transaction-owned models under `Eden.Data.World`; they are not catalog definitions or a replacement for the existing resident world owner.

`WorldDatabase.Catalog` resolves a shared `CatalogProvider` generation. New readers use generated sources (`catalog.Get(CatalogData.Client.Types, typeId)`), immutable attribute/effect/skill maps, item/group indexes or the product-to-blueprint index. `CatalogReadScope` captures one generation per operation, including nested/concurrent readers. The provider retains only weak registrations globally and a bounded lazy record cache; host ownership pins the active provider. Normal startup preloads core item metadata, client type definitions and blueprint indexes before opening listeners. It does not copy the entire catalog into another in-memory SQLite database.

Production catalog replacement requires restart. Writable fixture/tooling context disposal invalidates future provider resolution alongside existing resident caches. Captured immutable values remain valid; an invalidated generation rejects uncached loads rather than mixing old and new source records. World-specific economic projections and immutable configuration revisions are separate from this shared source cache; see [saved configuration](world-configuration.md). Feature readers now use the shared catalog boundary. Import, migration and verification retain explicit direct database access.

`CatalogLifetime` retains the latest source generation while any host using that catalog remains active, including generations loaded after fixture edits. `CatalogDbContext.Definitions` exposes that shared source boundary to migrating readers; writable tooling continues to use its owned EF context. Industry, insurance, reprocessing and economic-price setup use the new boundary. World prices, balances and installed jobs remain transaction-owned inputs; they are not cached as catalog data. Enabled production/insurance cohorts are typed build-specific policy data, distinct from catalog recipe relationships and supported mechanics.

### Flight coordination

Flight code lives in `Protocol/Flight`. The connection owns one navigation state and one ordered outbound queue. Components receive typed context and emit commands through that existing queue; they do not create independent sessions or timers.

| Owner | Responsibility |
| --- | --- |
| [FlightSession](../src/Eden.Protocol/Flight/FlightSession.cs) | Current pilot, navigation, lifecycle and persistence coordination, ordered outbound updates. |
| [FlightScene](../src/Eden.Protocol/Flight/FlightScene.cs) | Visible objects, target lookup, contact reconciliation, snapshots and observer course recovery. |
| [FlightNpcScene](../src/Eden.Protocol/Flight/FlightNpcScene.cs), [FlightDroneScene](../src/Eden.Protocol/Flight/FlightDroneScene.cs) | NPC and drone contact caches and their scene updates. |
| [FlightTravel](../src/Eden.Protocol/Flight/FlightTravel.cs) | Gate validation, pending gate/translocation state, deadlines and arrival decisions. |
| [FlightModuleEffects](../src/Eden.Protocol/Flight/FlightModuleEffects.cs) | Active effects, fitting/propulsion state and replay through the existing wire adapter. |
| [FlightEffects](../src/Eden.Protocol/Flight/FlightEffects.cs) | Stateless effect and position-correction command construction. |

Lifecycle methods expose the sequence directly: persist a transfer, leave the source scene, adopt the destination, and mark arrival. Snapshot construction publishes the same pilot contact it serializes before replaying effects. Propulsion changes apply flight properties before stop/start effects. These boundaries preserve the existing timing, cleanup and notification order; movement rules remain in Gameplay. See the [flight extraction validation](protocol/architecture.md#flight-coordination-extraction-november-6-2026).

### Session performance diagnostics

Native NPC scenes use a shared system geometry index to select nearby contacts before constructing observer records.
The existing inclusive visibility distance, ordered updates and off-grid warp tracking remain in effect. Geometry is
invalidated by committed population changes and resident position, membership or warp transitions; health and effects
are sampled at the caller's exact time. See [spatial scene measurements](protocol/performance.md#spatial-npc-scenes-november-9-2026).

The [NPC contact refresh measurements](protocol/performance.md#npc-contact-refresh-cost-november-9-2026) break down complete frames for ten pilots and 1,000 NPCs, compare observer costs, and rank the remaining performance work.

Set `EDEN_TRACE_PERFORMANCE=1` before startup for request/tick timings, database command/connection counts, and SQL execution, transaction-start and commit durations. Nested phases identify combat, weapon publication, health reads, NPC acquisition and background monitoring. See the isolated full-catalog replay in the [protocol notes](protocol/performance.md#shared-session-performance-diagnostics-november-5-2026) and the [ten-Brutix grouped-gun benchmark](protocol/performance.md#ten-brutix-grouped-gun-benchmark-november-5-2026).

Calculated ship and turret fits stay in a bounded per-world memory cache, with transactionally versioned invalidation in world schema 69. See [resident fit behavior and validation](protocol/performance.md#resident-ship-and-turret-fits-november-5-2026).

### Resident world and background saves

Capsuleer maintenance uses one-shot waits for the earliest job plus owner-coordinated input-change signals.
Nearby deadlines retain the existing 50 ms batching resolution; idle and paused queues need no timer.
See [deadline-driven maintenance](protocol/capsuleers.md#deadline-driven-maintenance-november-9-2026) for wake,
revision, retry, shutdown and checkpoint guarantees.

Normal server startup loads the saved world into an authoritative SQLite image in RAM. Existing gameplay transactions, constraints and revision triggers operate on that image. A background worker captures committed state every 15 seconds and writes a complete checkpoint after releasing the world owner. Graceful shutdown flushes remaining changes. Authentication mutations also request a durable checkpoint before returning.

Live systems have stable, independent ownership leases. Resident work enters its system directly; SQLite operations
enter the persistence coordinator before participating systems in ascending ID order. Live movement no longer adds a
second global `MotionLock`. Cold dependencies unwind their resident operation and restore changed keys before
re-entering persistence and validating current state. Frame scopes select a system without retaining ownership across
feature hooks or output. Shared scheduling and economic mutations remain coordinated. See
[independent system ownership](protocol/performance.md#independent-system-ownership-november-10-2026).
The November 8 movement-lock inversion remains historical evidence for the fixed acquisition order.

[Autonomous role jobs](capsuleer-population.md#shared-role-execution) share the human-active clock and a bounded due queue. Their next action, version and phase progress stay in resident memory between mutation boundaries. Economic and travel transitions use the same feature operations as humans inside one explicit transaction, then publish committed state. Detached checkpoints project immutable job and role progress arrays from the captured generation. World-owned system modes settle jobs before commands and system entry. Native scenes exist only in selected-human systems; background warps and gate delays use their saved event boundaries, while subwarp navigation and danger retain conservative one-second samples. Native flight and role jobs select the same system runtime; economic boundaries retain persistence coordination. See [handoff and recovery](capsuleer-population.md#active-and-background-handoff).

Activated communities retain remote deadlines after departure and restart. Unvisited prepared populations remain
dormant. Promotion settles relevant work with bounded continuations; demotion projects dirty state and evicts that
system's detailed caches while preserving compact actor inputs. Combat and unfinished missiles retain detail.
Shared immutable fitting profiles evict selectively and pin observed pilots; preparation blueprints pin the current
batch. These tiers preserve the existing human-active clock and offline rules. See
[tiered simulation and detail retention](protocol/performance.md#tiered-simulation-and-detail-retention-november-10-2026).

Active turret payments, ammunition, capacitor, cooldowns, health, incoming NPC gunfire and damage contributions use typed in-memory combat objects. Warm gunfire and health reads avoid SQL entirely. Classified persistence operations synchronize affected dirty objects into the resident relational image; checkpoint
capture retains immutable projections for the detached image; destruction and missile operations retain existing transactions. See [combat object behavior and the ten-Brutix comparison](protocol/performance.md#live-combat-objects-and-ten-brutix-replay-november-5-2026).

Shared flight positions, NPC motion, contacts and acquisition also use resident objects. Population scans run at respawn deadlines, while tackle changes and committed events refresh the affected state immediately. See [resident system simulation and the ten-Brutix comparison](protocol/performance.md#resident-system-motion-and-npc-acquisition-november-5-2026).

Stable character monitoring, balances, fitting attributes and scene contacts also stay in bounded typed memory views. Acknowledged corporation event streams skip SQL until committed change rows invalidate their cached heads. Stable stamped NPC records are shared across observers with exact weapon deadlines; recharging, timed and transitional state remains fresh. Peer scenes index previous identities and skip unchanged effects. See the [multi-player frame comparison](protocol/performance.md#shared-work-across-player-frames-november-7-2026). Committed changes invalidate the affected views; expiries and new mail/projectiles retain their existing delivery deadlines. Empty tackle, projectile and mail work skips database polling. See [resident monitoring and scene evidence](protocol/performance.md#resident-monitoring-and-scene-views-november-5-2026).

Corporation identity, missing corporation records and authorized corporation loyalty balances are bounded resident views. Their row dependencies include membership, effective roles, titles and wallet selection. Docking membership invalidates location roles; coordinate-only movement does not. Crimewatch read snapshots use the resident owner without opening a transaction that projects unrelated dirty combat. SQL mutation boundaries retain their existing synchronization. Damage contribution maps use immutable path-copy updates so many attackers do not require copying the full history after each shot. NPC scene contacts likewise use an identity index and persistent list to preserve earlier snapshots without rebuilding every contact for each shot notification. New monitoring and simulation changes must follow the [contributor performance rules](../AGENTS.md#simulation-performance) and measure full sessions as well as isolated operations.

Checkpoint capture copies the relational image and retains immutable combat records from the same generation under the owner. Projection applies those records to the detached image outside the owner, then durable file writing installs it atomically. Dirty live objects remain available for synchronization at SQL transaction and ownership/destruction boundaries. Diagnostics separate capture, detached projection and file writing; see the [checkpoint pause comparison](protocol/performance.md#detached-checkpoint-projection-november-7-2026).

The last completed checkpoint remains at the configured world path, with its predecessor at `world.sqlite.previous`. A crash restores the last completed checkpoint; the rollback window can exceed 15 seconds when saves are delayed or failing. Save failures retain resident changes and retry on the next interval. Stop the server before editing or restoring its saved world. See [checkpoint behavior and the ten-Brutix comparison](protocol/performance.md#resident-world-checkpoints-and-ten-brutix-replay-november-5-2026).

World startup now migrates and activates configuration before constructing resident services. A temporary ownership lease excludes an existing server while startup reconciles its save; the resident owner then retains its usual lease and checkpoint behavior. Revision saves use that owner and require durable checkpoint completion. The two new entities live under `Eden.Data.World.Configuration`, while Gameplay exposes typed immutable settings and the narrow `IWorldConfiguration` contract.

The local administration host is a separate Blazor server application under `Eden.Server/Administration`. It binds only loopback and uses a separate administrator cookie scheme, local credential provisioning, role checks, antiforgery validation and credential-stamp checks on HTTP and interactive operations. Typed settings and override commands delegate to World. `CatalogBrowser` is an explicit storage/display adapter: generated sources validate each key/payload, then original JSON spelling and field presence support schema-driven display. This dynamic boundary is not a gameplay data access interface.

The final catalog access policy is enforced by `tools/catalog/runtime_policy.py` through the required catalog Python suite. Source-backed facts use provider adapters; native operation identifiers and typed mechanical calculations remain in their owning feature. Build-specific enabled content lives in Gameplay's immutable embedded policy. The retired raw source reader/cache is removed, and `CatalogConnectionScope` only provides operation-owned transport reuse. Schema metadata and field compatibility are immutable. See [adoption and dispositions](catalog-adoption.md).

## Modding boundary

Eden.Modding contains versioned archive, dependency and trusted-plugin contracts, using only Gameplay and .NET. World owns mod identities, state and activation persistence; Server owns compilation and plugin hosting. Protocol may adapt mod commands but never references World. Gameplay remains independent of all mod hosting and serialization.
