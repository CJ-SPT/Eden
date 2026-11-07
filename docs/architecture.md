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

World feature folders follow Gameplay ownership: a feature's stores, entities, schema helpers and specialized catalog readers live together. `Combat/Npcs` owns NPC operations and populations; `Combat/Weapons` owns weapon catalog readers. `Storage` holds contexts, connection plumbing, initialization and migration coordination. `Catalog` holds shared catalog models and readers, including generated data. `Runtime` holds resident state, caches and checkpoints; its `Combat` folder holds resident combat and simulation. Host timers live in `Server/Maintenance`.

Gameplay and Infrastructure depend only on .NET. World and Protocol each reference Gameplay and Infrastructure, with their own database or wire packages; neither references the other. Server composes all four. `Directory.Build.targets` enforces allowed project references, including references unused by compiled code. Generated catalog files are kept out of readability size comparisons.

Startup calls `WorldInitialization.Initialize` explicitly before starting either listener. Resolving `SqliteWorld` only creates a reader; it does not migrate a save. Development tests use `WorldInitialization.Open` for deliberate restart/migration checks and the shared `TestClock` for wall time and elapsed time. Integration coverage includes the production `FlightSessionFactory`; focused simulations supply explicit substitutes for the infrastructure they omit.

`Storage/WorldMigration` is the migration coordinator. Named steps under `Storage/Migrations` retain historical version ranges and receive the original saved version throughout the run. Legacy conversion and initial economy settings precede the numbered upgrades. NPC schema reconciliation precedes backfills that read current entity shapes. The coordinator keeps the explicit commit before inventory ownership conversion and the separate history/economy transaction. Current-version opens still run the required idempotent reconciliation; adding a migration must preserve these ordering boundaries.

Known protocol data uses named immutable models: inventory/dogma state, character cards, flight commands, combat notifications, market and industry replies, skill queues and brain effects. Handlers return `PilotCallResult`, distinguishing successful Python None, unsupported calls and errors. Protocol adapters use named properties such as `DogmaAllInfo.ShipState` and `PilotBrainState.ShipEffects`; explicit `BlueModel.ToBlue()` adapters preserve the native client formats. The low-level Blue codec retains its flexible Python value graph. See the [type-boundary audit and acceptance results](protocol/architecture.md#named-protocol-models-final-audit).

The server uses Microsoft DI to discover services marked with Infrastructure's `[Injectable]` attribute in an explicit Server/Protocol/World assembly list and creates one scope per client connection. Its explicit contract assembly list also includes Gameplay and Infrastructure. Each interface registration resolves the registered concrete instance, preserving scoped and singleton identity. The [contributor guide](../AGENTS.md#services-and-protocol-calls) explains service lifetimes and how to add a protocol handler.

Use the root EditorConfig and shared build properties for new projects. Follow [Microsoft's C# conventions](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions) and keep production and test projects separate as in [Microsoft's project layout guide](https://learn.microsoft.com/en-us/dotnet/core/tutorials/libraries).

## Architecture

Eden.Gameplay owns rules and feature models. Eden.Protocol references Gameplay to translate client requests and results. Eden.World implements catalog and SQLite operations. Eden.Server composes the host and both adapters. Gameplay references neither adapter assembly. Combat health, fitting and module modifiers, targeting, mining yield, propulsion, skill planning, character rules and feature persistence contracts use this boundary. Client effect names and notification construction stay in Protocol. Ship health becomes a native damage-state value only in the Protocol adapter. Database transactions remain owned by World.

Manufacturing illustrates the persistence boundary. The World store loads the recipe, skills, active jobs and materials inside its existing transaction. Gameplay calculates a typed manufacturing plan and validates the client quote. World then consumes materials, reserves the blueprint and saves the job in that transaction. Protocol builds the native reply and queues inventory notifications from the resulting changes. Pure calculations can be tested without a database, and storage tests verify transactional behavior.

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

Set `EDEN_TRACE_PERFORMANCE=1` before startup for request/tick timings, database command/connection counts, and SQL execution, transaction-start and commit durations. Nested phases identify combat, weapon publication, health reads, NPC acquisition and background monitoring. See the isolated full-catalog replay in the [protocol notes](protocol/performance.md#shared-session-performance-diagnostics-november-5-2026) and the [ten-Brutix grouped-gun benchmark](protocol/performance.md#ten-brutix-grouped-gun-benchmark-november-5-2026).

Calculated ship and turret fits stay in a bounded per-world memory cache, with transactionally versioned invalidation in world schema 69. See [resident fit behavior and validation](protocol/performance.md#resident-ship-and-turret-fits-november-5-2026).

### Resident world and background saves

Normal server startup loads the saved world into an authoritative SQLite image in RAM. Existing gameplay transactions, constraints and revision triggers operate on that image. A background worker captures committed state every 15 seconds and writes a complete checkpoint after releasing the world owner. Graceful shutdown flushes remaining changes. Authentication mutations also request a durable checkpoint before returning.

Active turret payments, ammunition, capacitor, cooldowns, health, incoming NPC gunfire and damage contributions use typed in-memory combat objects. Warm gunfire and health reads avoid SQL entirely. Legacy operations and checkpoint capture synchronize dirty objects into the resident relational image; destruction and missile operations retain existing transactions. See [combat object behavior and the ten-Brutix comparison](protocol/performance.md#live-combat-objects-and-ten-brutix-replay-november-5-2026).

Shared flight positions, NPC motion, contacts and acquisition also use resident objects. Population scans run at respawn deadlines, while tackle changes and committed events refresh the affected state immediately. See [resident system simulation and the ten-Brutix comparison](protocol/performance.md#resident-system-motion-and-npc-acquisition-november-5-2026).

Stable character monitoring, balances, fitting attributes and scene contacts also stay in bounded typed memory views. Committed changes invalidate the affected views; expiries and new mail/projectiles retain their existing delivery deadlines. Empty tackle, projectile and mail work skips database polling. See [resident monitoring and scene evidence](protocol/performance.md#resident-monitoring-and-scene-views-november-5-2026).

Corporation identity, missing corporation records and authorized corporation loyalty balances are bounded resident views. Their row dependencies include membership, effective roles, titles and wallet selection. Docking membership invalidates location roles; coordinate-only movement does not. Crimewatch read snapshots use the resident owner without opening a transaction that projects unrelated dirty combat. SQL mutation boundaries retain their existing synchronization. Damage contribution maps use immutable path-copy updates so many attackers do not require copying the full history after each shot. NPC scene contacts likewise use an identity index and persistent list to preserve earlier snapshots without rebuilding every contact for each shot notification. New monitoring and simulation changes must follow the [contributor performance rules](../AGENTS.md#simulation-performance) and measure full sessions as well as isolated operations.

The last completed checkpoint remains at the configured world path, with its predecessor at `world.sqlite.previous`. A crash restores the last completed checkpoint; the rollback window can exceed 15 seconds when saves are delayed or failing. Save failures retain resident changes and retry on the next interval. Stop the server before editing or restoring its saved world. See [checkpoint behavior and the ten-Brutix comparison](protocol/performance.md#resident-world-checkpoints-and-ten-brutix-replay-november-5-2026).
