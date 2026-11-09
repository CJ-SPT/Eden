# Session performance and resident state

[Documentation](../README.md) / [Protocol index](../protocol.md)

Diagnostics, replay benchmarks, resident caches, combat objects and checkpoints.

These records preserve milestone evidence and historical observations. Dates, schema versions, test results and live acceptance apply to the recorded change; they do not establish acceptance of later changes.

## Offline startup belt renewal, November 9, 2026

A reported slow restart after roughly six hours offline led to the startup reconciliation path. The saved development world contained **9,096 initialized belts and 964,647 asteroids**. A restart crossing the saved daily renewal boundary previously loaded each asteroid separately, tracked every generated deposit and saved renewed deadlines individually before admitting connections. Unmodified belts still paid the lookup cost. Capsuleer elapsed time resumes from its saved clock, so ordinary capsuleer work does not replay the wall-clock shutdown interval.

Renewal now generates the same deterministic catalog-backed deposits and applies one parameterized SQLite upsert per existing bounded batch of at most 32 belts. A second bulk statement advances their saved deadlines in the same transaction. Existing coordinates, identities, types and radius remain intact; quantity only increases to the generated baseline, higher saved quantities survive, and missing rocks are restored. Cold catalog belt-count reads are batched, and already-full rocks are filtered before decoding their full insert projection. Unchanged rocks cause no write. Multiple missed days replenish once and advance directly to the next future boundary. Personal-order escrow expiration and the requirement to finish reconciliation before accepting clients remain in place.

Resident asteroid changes now invalidate population schedules with at most one location lookup per transaction, instead of opening a connection for every rock. Unrelated system deadlines stay warm; deleted identities retain conservative invalidation, and an empty schedule cache needs no lookup. Rollback coverage verifies that both renewed quantities and deadlines remain unchanged after a failed deadline update and that the resident view refreshes after a successful retry.

Reference: [EVE University mining](https://wiki.eveuniversity.org/Mining) describes finite belt deposits and renewal at downtime. The exact ore amounts, stable random placement and configurable UTC renewal boundary remain Eden policy for pinned build 3662168. Consulted [Carbon Destiny `Ballpark.cpp`, revision `27ee40b38a563d29054b32f64c3ebc87fabaec1f`](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ballpark.cpp); this change owns persistence/reconciliation and does not alter Destiny movement or client wire replies.

The matched warmed fixture has 16 belts and 2,048 rocks, with depleted rows, one missing row and one above-baseline deposit. Measurements below are single Debug samples on this desktop, excluding fixture setup, checkpoint file writing and complete session frames. Baseline is `e9fcd36c`; the final implementation additionally batches resident schedule invalidation. These are workload observations, not startup latency guarantees.

| Renewal workload | Baseline ms | Optimized ms | SQL commands before / after | Connections before / after |
| --- | ---: | ---: | ---: | ---: |
| File-backed startup store | 324.67 | 53.73 | 4,113 / 4 | 1 / 1 |
| Resident world | 1,198.31 | 39.00 | 6,160 / 4 | 2,048 / 1 |

The baseline intentionally failed the new bounded-command regression. An intermediate batch implementation exposed the remaining resident invalidation lookups; those were then batched as well. The eight persistent renewal cases and two warmed population-invalidation cases subsequently passed, including six-hour and thirty-day gaps, just-before/at deadline boundaries, batching, geometry and quantity preservation, failed transactions, checkpoint/reopen and immediate observer visibility.

`StartupPerformanceReplayTests.ReconcileSavedWorldAfterOfflineRenewal` is an opt-in automated replay. Set `EDEN_PERFORMANCE_SOURCE` to a directory containing `world.sqlite` and its catalog. The test opens the source save read-only, copies it using SQLite backup, copies the active catalog into a private test directory, moves its test clock six hours past the earliest saved renewal boundary and initializes twice. It reports `startup.schema`, `startup.beltRenewal`, `startup.marketExpiry` and total overdue/current reconciliation separately. It does not launch listeners or the client; traffic imports, launcher initialization and host catalog preloading are outside this replay. Temporary data is removed afterward.

The final isolated replay of the installed save renewed all 9,096 belts / 964,647 rocks in **14.15 seconds**, with **17.65 seconds** for complete overdue world reconciliation and **0.34 seconds** for the immediately repeated current reconciliation. These single Debug timings exclude the private catalog/save copy, traffic data, service construction, listeners and native login; there is no matched original-revision full-save startup measurement. Earlier intermediate batch implementations took 34.45–36.60 seconds and then 25.56 seconds before cold count batching and filtering already-full rows were both present. The existing save and its owner were never opened for writing by the replay.

Automated validation: tool/solution restore passed; the final root solution build passed with zero warnings/errors in 1.39 seconds. Both Python policy suites passed (8 catalog cases in 1.24 seconds; 9 formatting cases in 0.04 seconds), and formatting verification passed across 1,362 C# files. The final affected .NET selection passed 228 cases in 1 minute 32 seconds; its opt-in source was unset, with the full installed-save replay exercised separately as described above. The installed-save replay passed one case in 38 seconds including private data copying and assertions. The full .NET suite was not run.

Two earlier affected selections were interrupted after stale fixture assumptions: an asteroid restart test initialized an actively owned world instead of reopening its checkpoint, and the three-cycle economy test listed personal ammunition above the newly unlocked NPC competition. The former now checks a flushed checkpoint; the latter prices its personal offer below the catalog snapshot ask and preserves its wallet-conservation assertion. Their exact regressions passed after correction. An overlapping build was canceled when its output was locked by a test host; the subsequent clean root build above passed. The combined affected filter is:

```text
FullyQualifiedName~Eden.Protocol.Tests.Mining.|FullyQualifiedName~Eden.Protocol.Tests.Economy.EconomicLifecycleTests|FullyQualifiedName~Eden.Protocol.Tests.Combat.LiveSimulationTests|FullyQualifiedName~Eden.Protocol.Tests.Combat.NpcPopulationTests|FullyQualifiedName~Eden.Protocol.Tests.Combat.NpcMiningTests|FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerProvisioningTests|FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerMinerTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.WorldInitializationTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.WorldConfigurationTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.LiveWorldTests|FullyQualifiedName~Eden.Protocol.Tests.Market.PersonalMarketTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting.ServerServicesTests
```

Live acceptance: Not performed; agent client testing is disabled.

Optional maintainer check: restart the rebuilt server after a renewal boundary and compare the console's world-reconciliation timing. The real save and running server were not modified by the automated replays.

## Bound dogma and ship-call latency, November 7, 2026

The reported console capture showed repeated `dogmaIM.MachoBindObject` dispatches taking 0.77–1.62 seconds, a 421 ms ship binding and a 334 ms activation. The binding name concealed its embedded service method. Warnings and performance traces now include that method, such as `dogmaIM.MachoBindObject (GetAllInfo)`, and initial update-coordinator entry has its own `enter` phase.

An isolated replay of the saved, fully fitted Brutix Navy Issue reproduced the repeated fitting work: warmed `GetAllInfo` constructed 18 complete graphs and issued 3,603 SQL commands with 216 connection opens, including the surrounding session updates. Each module prime and the pilot brain reconstructed the same graph. Two independently registered `IPilotOperationScope` implementations also competed for the host's single scope, so the newer catalog generation scope displaced fitting-input sharing.

The host now registers one catalog operation scope that also opens the fitting-input scope. Read-only catalog contexts in that operation share one connection; nested scopes and awaits retain it, and the outer scope disposes it. Writable and standalone contexts retain their own connections. This does not share saved-world transactions or mutable catalog records between service operations.

The existing build-3662168 dogma/boarding adapters and their named replies remain in place. Reference consulted: [Carbon Blue `Marshal.cpp`, revision `fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae`](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp). This change does not alter Blue encoding or Destiny simulation rules.

The bounded resident fit cache now retains immutable evaluated attributes for all fitted modules and the associated skill bonuses. The existing fit revision covers membership, online state, skills, rigs, scripts and incoming effects. Snapshots are staged in the owning transaction and published only on commit. Pending changes bypass cached values, rollback/disposal cannot publish, catalog edits clear both fitting caches and reopening starts cold. Reload/cycling deadlines remain fresh SQL reads, while ammunition, resources, damage and active effect notifications retain their current owners. Pilot lookup now completes a read transaction so character selection and boarding can publish their calculated fits. Propulsion speed and signature reads use the existing fitting reader.

Automated validation uses `FittingSnapshotTests`, `CatalogFittingReadTests` and the affected fitting, ownership, session, propulsion, combat, mining, catalog and live-world tests. The opt-in `ServerPerformanceReplayTests.ReplaySavedDogmaCalls` copies the supplied databases before initializing a live world, uses a fixed `TestClock`, measures complete session calls, and checks selection, module online state, boarding, undocking, successful RPC output and the active propulsion effect. `EDEN_PERFORMANCE_SOURCE` selects a directory containing both SQLite files; `EDEN_PERFORMANCE_PILOT` selects the saved pilot, defaulting to the reported pilot `90000006`. The fixture expects a second owned hull of the same type and a fitted propulsion module. Reports go to `EDEN_PERFORMANCE_OUTPUT` when set. Checkpoint projection and file writing are outside these call measurements.

The before/after replay used baseline `bdefb7e` and the feature worktree, the same frozen database copies, pilot `90000006`, and a fixed November 7, 2026 12:00 UTC clock. Each process measured one first call and five warmed repetitions. These are Debug measurements on this desktop with other desktop work present, rather than production latency guarantees. Reports are retained in ignored `artifacts/slow-calls-baseline-fixed` and `artifacts/slow-calls-optimized-fixed`.

| Complete session call | Baseline ms | Optimized ms | SQL commands before / after | Connections before / after | Fitting graphs before / after |
| --- | ---: | ---: | ---: | ---: | ---: |
| Initial character selection | 725.65 | 577.46 | 331 / 156 | 116 / 48 | 1 / 0 |
| First bound `GetAllInfo` | 1467.72 | 399.51 | 3779 / 331 | 217 / 89 | 19 / 0 |
| Warmed bound `GetAllInfo`, median | 1001.23 | 93.31 | 3603 / 331 | 216 / 89 | 18 / 0 |
| Warmed bound ship `ItemGetInfo`, median | 42.74 | 10.81 | 34 / 34 | 22 / 10 | 0 / 0 |
| Take module offline | 1139.23 | 186.55 | 3562 / 491 | 188 / 27 | 18 / 1 |
| Bring module online | 1162.90 | 138.18 | 3605 / 516 | 201 / 27 | 19 / 2 |
| Bound ship `Board` | 592.27 | 189.59 | 817 / 436 | 198 / 65 | 3 / 1 |
| Bound ship `Undock` | 59.18 | 55.55 | 46 / 46 | 5 / 4 | 0 / 0 |
| Activate propulsion in space | 309.82 | 274.45 | 636 / 457 | 47 / 26 | 3 / 2 |

Warmed dogma median improved by about 91%; its five optimized samples ranged from 83.47 to 109.48 ms. Canonicalized full dogma replies match across revisions, including attributes, effects, magazines and pilot brain: 29,361 bytes, SHA-256 `F883D720B4598879C5FDB5DC2FF3594459BDF559CEEB1B1F10F6A0798A078708`. Only dictionary enumeration order is normalized for that comparison. Initial selection/first dogma calls can still exceed the warning threshold, and propulsion activation shows a smaller improvement. The change does not claim to eliminate every cold or busy-frame warning.

Automated validation: solution/tool restores and the solution build passed (final build 4.44 seconds, zero warnings/errors). The affected .NET selection passed 455 tests in 2 minutes 12 seconds; the final host-registration assertion passed separately. The final fixed-clock baseline and optimized replays passed in 29 and 20 seconds respectively. Both Python policy suites passed (6 catalog tests and 9 formatting tests, 0.70 seconds together); final formatting verification passed in 22.83 seconds. The full .NET suite was not run. The affected filter was:

```text
FullyQualifiedName~Eden.Protocol.Tests.Combat.FittingSnapshotTests|FullyQualifiedName~Eden.Protocol.Tests.Combat.ResidentFitTests|FullyQualifiedName~Eden.Protocol.Tests.Combat.SessionPerformanceTests|FullyQualifiedName~Eden.Protocol.Tests.Combat.PilotCombatTests|FullyQualifiedName~Eden.Protocol.Tests.Combat.WeaponBankTests|FullyQualifiedName~Eden.Protocol.Tests.Combat.PlayerEwarTests|FullyQualifiedName~Eden.Protocol.Tests.Combat.NpcEwarTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.CatalogFittingReadTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.CatalogProviderTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.PilotFittingTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.PassiveFittingTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.ModuleFittingTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.PilotFleetTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.ShipRigTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.LiveWorldTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting.ServerServicesTests|FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.PilotServicesTests|FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.TypedCallResultTests|FullyQualifiedName~Eden.Protocol.Tests.Propulsion.|FullyQualifiedName~Eden.Protocol.Tests.Repair.ShieldBoost|FullyQualifiedName~Eden.Protocol.Tests.Mining.MiningTests|FullyQualifiedName~Eden.Protocol.Tests.Mining.MiningTierTests
```

Live acceptance: Not performed; agent client testing is disabled. Cold startup, native rendering, network backpressure and the reported transport disconnect remain outside the established acceptance evidence. Optional maintainer check: restart the rebuilt server and revisit character selection, fitting and boarding to compare the newly identified call warnings.

## Shared work across player frames, November 7, 2026

Corporation-change polling now caches the highest saved event ID for each pilot. Acknowledged or empty streams return without SQL until the change table is modified. Existing page size, event ordering and payload conversion remain unchanged. Transaction-local reads bypass publication and rollback invalidates conservatively. The existing `(PilotId, Id)` index serves cold head queries.

Normal stamped simulation shares immutable NPC contacts across observers when their source state, aggression, motion stamp, collision flag and committed-change epoch match. Full shields are time independent; damaged/recharging shields, patrols, warp, mining activity and incoming EWAR retain fresh construction. Nonempty outgoing EWAR also bypasses sharing. Shared weapon views expire at their exact start/end boundaries and reject backward clock reuse. No clock quantization is introduced. Storage is bounded by the existing NPC retention limit and cleared with simulation state. Effective-attribute lookups reuse their resident index under the owner rather than cloning it for each view.

NPC scenes skip unchanged snapshots and unchanged shared records. Peer scenes index prior ships by identity instead of searching the old array for every peer. Empty or shared module-effect arrays skip redundant set comparisons; changed effects keep their original stop/start order. Each pilot still receives an individual, range-filtered scene and its own protocol output. Damage, health changes and native movement/effect commands retain their established adapters and ordering.

New regressions check event-stream pagination, empty/acknowledged reads with zero SQL, committed inserts and rollback, stable cross-observer records, immediate damage, subsecond recharge, exact weapon expiry, clock rewind and effect ordering. Two-thousand-NPC unchanged-scene reads and empty peer-effect passes have allocation budgets. The normal-session capacity replay now requires zero SQL commands, connection opens and fitting recalculations in every warmed measured frame as well as its existing no-projection assertion.

Six final Release cases each retained thirty measured frames. All passed the zero-SQL, zero-connection, zero-fitting and no-projection frame assertions; isolated warmed operations and observer probes were also zero. Setup and checkpoint timings remain separate from frame distributions. Reports are in ignored `artifacts/player-frame-optimization/final`.

| Players / NPCs | Mode | Median / p95 frame ms | MiB allocated per frame | MiB encoded per frame |
| --- | --- | --- | --- | --- |
| 1 / 0 | idle | 0.16 / 2.63 | 0.1 | 0.000 |
| 1 / 4,000 | combat | 185.04 / 223.91 | 119.8 | 4.499 |
| 1 / 8,000 | combat | 270.98 / 466.52 | 240.3 | 8.994 |
| 10 / 1,000 | combat | 89.33 / 147.33 | 75.6 | 2.703 |
| 10 / 4,000 | combat | 253.12 / 276.22 | 289.3 | 10.720 |
| 100 / 1 | combat | 74.61 / 113.70 | 66.8 | 1.859 |

Total final replay test duration was 516.77 seconds including fixture setup and component probes. One player with 8,000 NPCs remains below the one-second p95 cadence budget; it exceeds the 250 ms headroom budget. Ten players with 1,000 NPCs and 100 players with one NPC pass the headroom budget. Ten players with 4,000 NPCs narrowly exceed it at 276.22 ms p95. The existing 8,192-NPC / 512-ship retention limits are unchanged.

Compared with the earlier checkpoint-only measurements, ten players / 4,000 NPCs reduce frame allocations from 523.2 to 289.3 MiB. Their inclusive NPC-contact phase drops from 483.23 to 89.97 ms median. At 100 players / one NPC, allocations fall from 97.0 to 66.8 MiB, the background-monitoring phase falls from 58.15 to 4.18 ms and peer refresh from 36.13 to 12.75 ms. Encoded payload volumes remain approximately 10.720 and 1.859 MiB respectively. The reduction comes from server work rather than removing individual player views or changing the output format.

Single-player performance is mixed: at 4,000 NPCs the checkpoint-only frame median/p95 was 146.69/329.14 ms versus 185.04/223.91 ms here. Median regressed while p95 improved. Other desktop workloads remained active, so end-to-end comparisons are sensitive to scheduling and CPU contention. The repeated-work, allocation and SQL budgets provide additional evidence beyond wall time.

The original checkpoint-only runs preceded the optimized ladder. Because desktop load varied, two fresh checkpoint-only controls were also run afterward, serially and without concurrent builds/tests. Both controls use clean `def8fc2` in an isolated checkout; feature changes remain on `main`. The table retains both controls rather than selecting one result. Cells show median / p95 frame milliseconds.

| Players / NPCs | Checkpoint-only before | Checkpoint-only after | Shared player work | After-control / optimized mean SQL commands |
| --- | --- | --- | --- | --- |
| 10 / 4,000 | 685.68 / 1140.54 | 528.71 / 575.65 | 253.12 / 276.22 | 10 / 0 |
| 100 / 1 | 166.24 / 233.99 | 115.47 / 127.89 | 74.61 / 113.70 | 100 / 0 |

The two fresh control cases passed in 116.50 seconds including setup and probes, retaining thirty measured frames each. Ten players / 4,000 NPCs improve by approximately 52% in both median and p95 versus the fresh control. The hundred-player median improves by approximately 35%, with a smaller p95 improvement of approximately 11%. SQL and EF opens fall from 10/100 per frame respectively to zero; fits stay zero. Controls are in ignored `artifacts/player-frame-optimization/control-after`.

Remaining work includes individual output encoding, range filtering and whole-image RAM checkpoint copying. No network payload is shared across accounts. The fixture excludes encryption/network backpressure, concurrent request arrival, native clients, active missiles/EWAR/drones, losses/respawns and production catalog diversity. These are capacity observations on this machine, not a production NPC limit.

**Automated validation.** Tool/solution restores, Debug build and Release replay build passed with zero warnings/errors. The affected filter passed 442 tests in 167 seconds with two workers; five exact new regressions passed separately. The default two-player/eight-NPC idle session replay passed its zero-SQL frame budget in 8 seconds. Both Python policy suites passed (3 catalog, 9 formatting cases), and formatting checked 920 files and the 140-column limit. No full .NET suite was requested or run.

The affected filter was `FullyQualifiedName~PlayerFrameWorkTests|FullyQualifiedName~NpcSceneUpdateTests|FullyQualifiedName~LiveSimulationTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~NpcAggressionTests|FullyQualifiedName~NpcEwarTests|FullyQualifiedName~NpcWarpTests|FullyQualifiedName~NpcPursuitTests|FullyQualifiedName~NpcConvoyTests|FullyQualifiedName~NpcMiningSessionTests|FullyQualifiedName~NpcRepairTests|FullyQualifiedName~PlayerEwarNpcTests|FullyQualifiedName~TackleTests|FullyQualifiedName~CorporationMembershipTests|FullyQualifiedName~CorporationAdministrationTests|FullyQualifiedName~CorporationRoleTests|FullyQualifiedName~CorporationStorageTests|FullyQualifiedName~NpcMissileTests|FullyQualifiedName~Eden.Protocol.Tests.Flight`.

**Main integration.** Remote `main` advanced with cooperative-fleet merge `c51e3f7` before this milestone could push. The player-frame commit was rebased onto it without conflicts. Tool/solution restores, the integrated build (zero warnings/errors), both Python suites and formatting across 927 files passed. Filter `FullyQualifiedName~CooperativeFleetTests|FullyQualifiedName~FleetRulesTests|FullyQualifiedName~PlayerFrameWorkTests|FullyQualifiedName~NpcSceneUpdateTests|FullyQualifiedName~Eden.Protocol.Tests.Flight|FullyQualifiedName~NpcCapacityBenchmarkTests.MeasureNpcAndPlayerCapacityThroughNormalSessions` passed 187 tests in 54 seconds, including the default zero-SQL session replay. The large timing runs above were collected before fleet integration; they establish the isolated performance comparison against `def8fc2`, not a timed result for the subsequent fleet merge. No full .NET suite was requested or run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** None required for this performance milestone.

## Detached checkpoint projection, November 7, 2026

Checkpoints now capture the resident relational image and immutable combat records from the same generation under the world owner. NPC/ship health, ammunition, capacitor, damage attribution, movement, drones and weapon deadlines are then projected into that detached image outside the owner. SQLite constraints, triggers and related projection writes remain in one transaction. Durable file writing and atomic replacement retain the existing path and previous-save behavior. The saved schema and 15-second schedule are unchanged.

Resident dirty state remains pending for synchronization with ordinary SQL transactions, cache eviction and ownership/destruction boundaries. A checkpoint never acknowledges those live SQL projections on behalf of a newer generation. Direct SQL reads of crimewatch state now synchronize resident NPC timers, like the other mutable combat reads. Typed warmed crimewatch reads retain their SQL-free overlay. Capturing fails rather than copying an open resident transaction.

Diagnostics split `Capture` (under owner), `Projection` (detached SQL) and `Write` (durable file installation). Total checkpoint work still exists; the change moves SQL preparation and execution off the world lock. Generation-based flushing still waits for an adequate completed save, and failed projections/installations retain resident changes for retry.

A deterministic regression captures dirty combat with zero EF commands/opens/fitting calculations, blocks the detached SQLite engine inside projection, and advances live shots and a SQL mutation concurrently. The detached image retains its original ammunition, health and pilot state; a later reopened checkpoint contains the latest state. Another regression aborts projection mid-transaction, verifies ammunition rollback, retries and reopens. Existing coverage checks timers, shutdown, failed file installation, previous-save recovery, ownership transitions and drone state.

Matched Release replays compare the clean `aa3aa3e` baseline with this milestone, using thirty measured frames, 4,000 fighting NPCs, unchanged inputs and disabled tiered compilation. Each capture summary contains the last three checkpoints (two periodic samples and the explicit final flush), excluding the startup checkpoint. The small checkpoint sample count limits tail estimates. Full-frame distributions use all thirty frames.

| Players / NPCs | Baseline owner capture median / p95 ms | Detached owner capture median / p95 ms | Detached SQL projection median / p95 ms | Detached file writing median / p95 ms |
| --- | --- | --- | --- | --- |
| 1 / 4,000 | 181.77 / 211.42 | 2.10 / 2.13 | 239.62 / 445.35 | 19.83 / 24.76 |
| 10 / 4,000 | 226.44 / 232.94 | 1.79 / 3.83 | 362.64 / 422.29 | 21.90 / 25.70 |

The checkpoint SQL command count remains approximately 12,000 at this population. Those statements now execute outside the owner against the already-open private image, so they no longer require an EF connection open to the live world. Raw RAM-copy and file-backup connections remain beyond EF counters. The checkpoint still copies the complete relational image under the owner, so capture cost can grow with saved-world size.

| Players / NPCs | Baseline full-frame median / p95 ms | Detached full-frame median / p95 ms | Baseline / detached mean SQL commands |
| --- | --- | --- | --- |
| 1 / 4,000 | 102.10 / 272.58 | 146.69 / 329.14 | 1.40 / 1.00 |
| 10 / 4,000 | 656.68 / 1102.74 | 685.68 / 1140.54 | 14.07 / 10.00 |

These runs establish substantially shorter owner pauses; they do not establish a complete-frame speedup. Both full-frame comparisons regressed, and other desktop workloads were active during measurement. Detached projection still consumes CPU, allocates and can overlap a frame. The ten-player replay still exceeds the one-second p95 budget. Per-player monitoring and scene/output work is addressed by the next milestone.

The additional 100-player / one-NPC replay records 166.24 / 233.99 ms median / p95, 100 commands and 100 EF opens per frame, with zero fits. This is the checkpoint-only baseline for subsequent player-frame optimization. Each of the three final replays retained thirty samples, passed the no-projection assertion for warmed frames and verified zero SQL/opens/fits in isolated warmed simulation and observer probes. Reports are in ignored `artifacts/checkpoint-optimization/{baseline,detached-final}`. The failed multi-player development replay in `detached` is excluded; its cold-crimewatch projection feedback loop is fixed and covered by a dedicated regression.

**Automated validation.** Tool and solution restore, Debug solution build and Release replay build passed with zero warnings/errors. The final affected run passed 308 tests in 140 seconds with two workers. Both Python suites passed (3 catalog, 9 formatting cases); formatting checked 918 files and the 140-column limit. Exact new regressions cover detached projection concurrency, transactional rollback/retry and cold crimewatch reads. The initial snapshot context used an empty path that reached the fitting-cache lookup; detached contexts now explicitly bypass that cache. The expanded run also exposed the crimewatch read-boundary gap and the multi-player replay exposed its cold-read feedback loop; both are fixed in the final passing runs. One development assertion reused a tracked entity across external projection and was corrected to read untracked state. No full .NET suite was requested or run.

The final filter was `FullyQualifiedName~DetachedCheckpointTests|FullyQualifiedName~LiveWorldTests|FullyQualifiedName~LiveCombatObjectTests|FullyQualifiedName~LiveSimulationTests|FullyQualifiedName~ResidentCombatMonitoringTests|FullyQualifiedName~ResidentDroneTests|FullyQualifiedName~DroneRecoveryTests|FullyQualifiedName~DroneSynchronizationTests|FullyQualifiedName~NpcAggressionTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~CombatDroneTests|FullyQualifiedName~InteractionHistoryPersistenceTests|FullyQualifiedName~CrimewatchTests|FullyQualifiedName~ResidentReadTests|FullyQualifiedName~ResidentFitTests|FullyQualifiedName~InventoryOwnershipTests`.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** None required for this persistence milestone.

## Indexed NPC scene updates, November 6, 2026

After collision pruning and resident monitoring, the 4,000-NPC replay still spent 204.54 ms median in `update.npcCombat`. The protocol shot path scanned visible NPCs for each aggressor, searched again for its prior weapon, and copied the complete NPC contact array for every weapon and health notification. Incoming damage to the player copied that NPC array even though no NPC health changed.

`NpcCombatHandler` now builds one visibility identity set per pass, after electronic warfare has advanced. `FlightNpcScene` maintains an identity-to-index map and an immutable list. Weapon and NPC-health notifications replace one contact through a persistent update; player/drone health notifications retain the NPC list. Earlier observer snapshots remain immutable. Scene capture/refresh rebuilds the index in the existing input order. Stops, starts, per-shot damage notifications, unknown weapon events, destruction and warp handling retain their established ordering and adapters.

A focused scene regression checks exact stop/start order, same-tick health, earlier snapshot isolation, reordered and removed IDs, clearing and capture, and unknown/player health notifications. A separate 2,000-NPC volley bounds allocations below 8 KiB per NPC while updating every weapon and health contact. Contributor rules now explicitly forbid rebuilding a complete observer scene per shot.

Matched thirty-frame Release comparisons against `43f34cd` include all three optimization milestones. Both populations survive and keep firing throughout the sample. Baseline and final runs use the same machine, workload, runtime and disabled tiered compilation.

| Fighting NPCs / one player | Baseline median / p95 ms | Final median / p95 ms | Baseline / final mean SQL commands | Baseline / final MiB allocated per frame |
| --- | --- | --- | --- | --- |
| 1,000 | 118.43 / 144.84 | 20.94 / 70.41 | 3003.97 / 1.40 | 257.0 / 32.2 |
| 4,000 | 1302.22 / 1461.55 | 104.54 / 293.60 | 11841.77 / 1.40 | 3355.7 / 130.8 |

At 4,000 NPCs, the NPC-combat phase itself falls from 204.54 ms median on the preceding milestone to 31.65 ms. Total allocations now scale approximately linearly over the measured single-player ladder. The complete frame, including output and checkpoint contention, determines the capacity below.

| Connected players / fighting NPCs | Median / p95 ms | Mean SQL commands / EF opens | MiB allocated per frame |
| --- | --- | --- | --- |
| 1 / 100 | 2.78 / 9.70 | 1.40 / 1.40 | 3.5 |
| 1 / 1,000 | 20.94 / 70.41 | 1.40 / 1.40 | 32.2 |
| 1 / 2,000 | 47.57 / 138.48 | 1.40 / 1.40 | 65.0 |
| 1 / 4,000 | 104.54 / 293.60 | 1.40 / 1.40 | 130.8 |
| 1 / 8,000 | 231.74 / 566.03 | 1.40 / 1.40 | 262.2 |
| 10 / 1,000 | 106.73 / 168.80 | 14.07 / 14.07 | 135.4 |
| 10 / 4,000 | 532.44 / 693.13 | 14.07 / 14.07 | 524.7 |
| 100 / 1 | 110.62 / 283.10 | 140.07 / 140.07 | 98.8 |

All eight cases retained thirty measured frames and passed the assertion that measured session ticks do not project combat state. Combined replay test duration was 422.72 seconds, including setup and component probes. Every case recorded zero fitting recalculations. The isolated warmed simulation and observer probes used zero SQL commands and connections. Full-frame SQL remains small and independent of NPC population in this workload.

**Measured headroom:** one player with 2,000 NPCs and ten players with 1,000 NPCs meet the 250 ms p95 budget. One player with 4,000 NPCs exceeds it. **Measured cadence:** one player with 8,000 NPCs and ten players with 4,000 NPCs stay below the one-second p95 budget. These are the largest tested populations for their session counts, not universal capacity limits. The current 8,192-NPC / 512-ship active-cache retention thresholds remain unchanged; do not extrapolate beyond them.

The matched 101-ship comparison makes the session cost clear: 100 NPCs plus one player take 2.78 ms median, while 100 connected players plus one NPC take 110.62 ms. Connected pilots require personal monitoring, observer views and output. An unoccupied NPC in the saved world is not equivalent to an active combatant on this grid.

Checkpoint capture/project/copy still holds the world owner. The table shows the last three captures, excluding the early startup capture; file writing follows outside the owner. Frame p95 can include waiting for capture. Raw SQLite backup connections are represented by capture/write time and bytes beyond EF counters.

| Players / NPCs | Capture range ms | File-write range ms | EF commands per checkpoint | EF opens |
| --- | --- | --- | --- | --- |
| 1 / 1,000 | 44.85–49.75 | 12.74–14.09 | 3006 | 1 |
| 1 / 4,000 | 178.13–190.21 | 17.34–21.28 | 12006 | 1 |
| 1 / 8,000 | 359.75–384.54 | 23.25–23.74 | 24006 | 1 |
| 10 / 4,000 | 179.44–185.16 | 17.57–18.83 | 12052 / 12055 / 12059 | 1 |

The remaining costs are periodic checkpoint projection and per-session scene/output work. These fixture measurements exclude actual socket encryption/backpressure, competing requests, live-client processing, missiles, active EWAR, drones, loss/respawn settlement and production catalog diversity. The simulated clock advances without real-time idle gaps; background-save overlap can differ in a running host. Reports are in ignored `artifacts/npc-optimization/scene-final`; the preceding milestone reports are in `resident-final`.

Reproduce the final ladder with:

```powershell
.\tools\Measure-NpcCapacity.ps1 -NpcCounts @(100,1000,2000,4000,8000) -PlayerCounts @(100) -Modes combat -Output artifacts/npc-optimization/scene-final
.\tools\Measure-NpcCapacity.ps1 -NpcPlayers 10 -NpcCounts @(1000,4000) -PlayerCounts @() -Modes combat -NoBuild -Output artifacts/npc-optimization/scene-final
```

**Automated validation.** Tool/solution restores, Debug solution build and Release benchmark build passed with zero warnings/errors. The affected run passed 379 tests in 109 seconds with two workers; both new exact scene regressions also passed separately. Both Python policy suites passed (3 catalog, 9 formatting cases). Formatting checked 909 files and the 140-column limit. No full .NET suite was requested or run.

The affected filter was `FullyQualifiedName~NpcSceneUpdateTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~NpcAggressionTests|FullyQualifiedName~NpcMissileTests|FullyQualifiedName~NpcGuardTests|FullyQualifiedName~NpcEwarTests|FullyQualifiedName~NpcRepairTests|FullyQualifiedName~NpcOrbitTests|FullyQualifiedName~NpcPursuitTests|FullyQualifiedName~NpcJammingTargetTests|FullyQualifiedName~NpcConvoyTests|FullyQualifiedName~LiveSimulationTests|FullyQualifiedName~DroneSynchronizationTests|FullyQualifiedName~CombatDroneTests|FullyQualifiedName~NpcMiningSessionTests|FullyQualifiedName~PlayerEwarNpcTests|FullyQualifiedName~MotionSynchronizationTests|FullyQualifiedName~NpcWarpTests|FullyQualifiedName~PilotCombatTests`.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** None required for this performance milestone.

## Resident combat monitoring and damage history, November 6, 2026

Normal-session tracing found three read transactions forcing dirty combat projection on every tick: corporation identity, corporation lookup (including absent NPC corporations), and corporation loyalty balances. Bounded resident snapshots now retain these results against membership, role/title, wallet-selection and balance dependencies. Coordinate-only updates do not invalidate docking-dependent roles; position insertion, deletion and primary-key changes do. Nullable corporation results are cached explicitly. Rollback and schema changes invalidate conservatively without publishing transaction-local data.

Crimewatch reads use the resident world owner for a coherent snapshot. File-backed reads keep their transaction. The global transaction bridge and all mutation/kill settlement boundaries remain intact. Cold snapshots may still project state; unchanged warmed monitoring does not. Complete capacity replays now reject a `combat.project` phase inside measured session ticks. Checkpoint projection remains separately recorded, and frame latency can include waiting for its owner lock.

Player, NPC and drone damage histories use immutable dictionary updates. Each hit preserves earlier snapshots and replaces only the attacker's metadata and cumulative applied damage. Resident loading produces the same immutable representation; persistence and fatal settlement retain existing transactions and save format. A 2,000-attacker allocation regression bounds two complete volleys without timing-based assertions. Tests cover cached absent results, dirty combat across repeated reads and checkpoint, coordinate/docking changes, role/title/balance/membership changes, rollback, and contribution snapshot isolation.

Matched thirty-frame Release replays use baseline `43f34cd`, the same machine and workload described below, and disabled tiered compilation. Results include the collision milestone above. Full-frame metadata polling remains; isolated warmed combat and observer operations have zero SQL commands, connection opens and fitting calculations.

| Fighting NPCs / one player | Baseline median / p95 ms | Optimized median / p95 ms | Baseline / optimized mean SQL commands | Baseline / optimized MiB/frame |
| --- | --- | --- | --- | --- |
| 1,000 | 118.43 / 144.84 | 33.23 / 80.35 | 3003.97 / 1.40 | 257.0 / 47.3 |
| 4,000 | 1302.22 / 1461.55 | 279.50 / 461.92 | 11841.77 / 1.40 | 3355.7 / 374.0 |

Both cases average 1.40 full-frame EF connection opens and zero fit recalculations. Routine combat has moved off SQL, while periodic checkpoint capture still projects the dirty state. For 1,000 NPCs, the last three checkpoint captures take 47.29–49.08 ms under the owner, issue 3,006 commands through one EF connection, then write in 11.67–13.07 ms outside the owner. At 4,000 NPCs those figures are 185.07–195.23 ms, 12,006 commands / one connection and 17.65–20.68 ms writing. Raw SQLite backup connections are represented by capture/write time and bytes beyond EF counters.

The 4,000-NPC case now meets the 1,000 ms p95 cadence budget but exceeds the 250 ms headroom budget. Per-shot scene scans and array rebuilding remain visible in `update.npcCombat` (204.54 ms median at 4,000 NPCs), and many connected observers still require personal output. Active-object retention limits remain unchanged. These are synthetic measurements, not a production capacity guarantee; the original workload exclusions below still apply. Ignored reports are in `artifacts/npc-optimization/resident-final`.

The six capacity cases each retained thirty measured frames and passed the no-projection assertion. The additional ladder at this milestone is below; it has no matched `43f34cd` baseline for the new population rows. All fit counts were zero.

| Connected players / fighting NPCs | Median / p95 ms | Mean SQL commands / opens | MiB/frame |
| --- | --- | --- | --- |
| 100 / 1 | 106.91 / 280.27 | 140.07 / 140.07 | 98.7 |
| 10 / 1 | 6.20 / 24.98 | 14.07 / 14.07 | 3.6 |
| 1 / 1,000 | 33.23 / 80.35 | 1.40 / 1.40 | 47.3 |
| 1 / 1 | 0.55 / 5.54 | 1.40 / 1.40 | 0.3 |
| 1 / 4,000 | 279.50 / 461.92 | 1.40 / 1.40 | 374.0 |
| 1 / 8,000 | 854.69 / 1249.43 | 1.40 / 1.40 | 1236.5 |

The combined replay test duration was 308.16 seconds, including setup and component probes. At this milestone 8,000 NPCs exceed the one-second p95 cadence budget.

**Automated validation.** Tool/solution restores and the solution build passed with zero warnings/errors. The affected filter passed 332 tests in 76 seconds with two workers. Both Python policy suites passed (3 catalog, 9 formatting cases); formatting checked 908 files, the 140-column limit and diff whitespace. Five new exact regressions passed during development. No full .NET suite was requested or run.

The affected filter was `FullyQualifiedName~ResidentCombatMonitoringTests|FullyQualifiedName~CombatContributionTests|FullyQualifiedName~LiveCombatObjectTests|FullyQualifiedName~LiveSimulationTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~NpcAggressionTests|FullyQualifiedName~CombatDroneTests|FullyQualifiedName~ResidentDroneTests|FullyQualifiedName~LiveDroneRulesTests|FullyQualifiedName~CrimewatchTests|FullyQualifiedName~InteractionHistoryPersistenceTests|FullyQualifiedName~CharacterInteractionHistoryTests|FullyQualifiedName~CorporationRoleTests|FullyQualifiedName~CorporationWalletTests|FullyQualifiedName~CorporationMembershipTests|FullyQualifiedName~CorporationAdministrationTests|FullyQualifiedName~CorporationLoyaltyTests|FullyQualifiedName~PlayerCorporationTests|FullyQualifiedName~ResidentReadTests|FullyQualifiedName~PlayerEwarNpcTests`.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** None required for this performance milestone.

## Swept collision candidate index, November 6, 2026

`ShipCollisions.Resolve` builds a per-operation bounding hierarchy from solid bodies' complete start/end sweeps. Queries prune non-overlapping boxes, then restore each candidate's original input index before the existing pair response runs. Equal-time tie ordering, recursive overlap response, fixed bodies and output order retain the legacy algorithm. Static obstacle evaluation also uses the allocation-free scalar contact-time calculation.

Bounds expand the float radius by one float ULP and round each double box endpoint outward. This includes contacts produced by the legacy float addition of two radii. A focused regression reproduced a missed near-boundary overlap before this inflation. Negative, non-finite or unrepresentable bounds and radii whose pair sums can overflow retain all-pairs evaluation. The index uses linear working storage, including in dense scenes.

Reference source is Carbon Destiny revision `27ee40b38a563d29054b32f64c3ebc87fabaec1f`. `Gradient` and `CalculateCollisionResponse` request spatial collision candidates before evaluating pairs. Eden retains its established response calculation and introduces candidate pruning around it. [Carbon collision candidates](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ballpark.cpp#L2519-L2548)

The frozen `43f34cd` all-pairs test reference verifies exact results for 120 seeded randomized fleets with fractional radii, mixed solid/fixed bodies, varied inertia, arbitrary input orders and coordinates up to 1e15. Additional cases cover fast crossings, tangent contacts, overlaps, reversed order, empty/non-solid populations and unrepresentable coordinates. A sparse 4,000-body allocation regression requires less than 1 KiB of working allocation per body.

The same thirty-frame, one-player combat replays were refreshed on the integrated electronic-warfare base `43f34cd`. Both modes use Release with tiered compilation disabled. Rows show median / p95 milliseconds and average full-frame allocations. The separate collision component uses current NPC positions with zero velocities. SQL projection and contributor-map copying are still present at this milestone.

| Fighting NPCs | Baseline full frame ms | Indexed full frame ms | Baseline / indexed allocations MiB/frame | Baseline / indexed collision median ms |
| --- | --- | --- | --- | --- |
| 1,000 | 118.43 / 144.84 | 91.21 / 95.40 | 257.0 / 104.6 | 30.00 / 1.36 |
| 4,000 | 1302.22 / 1461.55 | 766.50 / 922.89 | 3355.7 / 914.7 | 420.16 / 8.35 |

Reports are in ignored `artifacts/npc-optimization/{baseline,collision}`. The two baseline cases passed in 171 seconds combined reported duration and the indexed cases in 109 seconds. These timings include fixture setup and component probes; the table uses measured frames only.

**Automated validation.** Tool/solution restore and solution build passed with zero warnings/errors. The final focused flight/NPC filter passed 176 tests in 59 seconds, followed by the strengthened randomized large-coordinate check. The filter was `FullyQualifiedName~Eden.Protocol.Tests.Flight|FullyQualifiedName~LiveSimulationTests|FullyQualifiedName~NpcPursuitTests|FullyQualifiedName~NpcWarpTests|FullyQualifiedName~PlayerEwarNpcTests`. Both Python policy suites passed (3 catalog, 9 formatting cases); formatting checked 905 files and the 140-column limit. No full .NET suite was requested or run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** None required for this calculation and simulation milestone.

## NPC and player capacity, November 6, 2026

`NpcCapacityBenchmarkTests.MeasureNpcAndPlayerCapacityThroughNormalSessions` compares NPC population and connected-player population through normal session ticks on isolated fixture databases. The reproducible runner is `tools/Measure-NpcCapacity.ps1`. This benchmark owns its exact population and disables the fixture's two-belt population seeder through changes to the temporary catalog.

Both ship populations use 50 m radii and 1,500,100 kg masses. Combat gives each ship one turret on a one-second firing cycle, large target ranges and large shields to retain every participant. Players lock and activate their real fitted civilian gun through the router. NPCs acquire the nearest player through normal AI and move, orbit and fire through their ordinary handlers. Every combat NPC must have fired in the final normal frame; every player gun must remain active. Checkpoint verification asserts the exact population, aggression membership and paid NPC deadlines. Idle cases disable NPC acquisition and leave player guns inactive.

The local machine has an Intel Core i5-14600KF (14 cores, 20 logical processors) and 31.8 GiB RAM. Final measurements use Release, .NET SDK 10.0.403, .NET 10.0.14, and `DOTNET_TieredCompilation=0`, retaining optimized compilation while avoiding population-dependent tier promotion during the short samples. The initial default-tiered exploratory sweep is retained separately and is excluded from the final comparison.

Setup time includes fixture creation, cloning, initial snapshots, target locking and first volleys. Four warmup frames precede thirty measured one-second simulation steps. A complete frame advances all connected clients serially, including shared system motion, NPC acquisition, gunfire, observer contacts, notifications and Blue output encoding. Raw output bytes, CPU time, allocations, collection counts and phase timings are recorded. Allocation, SQL and byte rates cover measured frames. Collection totals span warmups, frames, component probes and the final flush; working set is sampled at reporting. Background checkpoints fire on simulated 15-second deadlines, and their capture/write/SQL costs are recorded separately. Three consecutive frames above two seconds stop a case after at least ten samples; the report retains the actual sample count.

The 250 ms p95 threshold is an explicit headroom budget. The 1,000 ms p95 threshold represents the one-second simulation cadence. These are measured workload thresholds for this machine. The fixture excludes network encryption/backpressure, request arrival, missiles, EWAR, drones, death/respawn settlement, mining AI and real-catalog diversity. Production tiered PGO and background machine load can change the results. Existing NPCs in unoccupied systems are not equivalent to this active visible population.

After the normal-session sample, ten component samples measure the existing collision resolver on current NPC positions with zero velocities and ten repeat observer contact reads. A separate ten-sample resident-operation replay advances actual ship/NPC motion, acquisition, player volleys and NPC retaliation through the World APIs using fixed 0.5 accuracy draws, with session housekeeping and output excluded. Full frames retain their ordinary random draws. The latter asserts zero SQL commands, connections and fitting calculations once warmed. It measures component execution time. Complete frames include session housekeeping and output. Inclusive phase timings overlap and must not be added together.

### Measured capacity

Production code was pinned to `8d0882105c939a1e2b03b0b93011732642121cca`. All 28 controlled cases retained thirty measured frames. Every warmed resident-operation probe issued zero SQL commands, connection opens and fitting calculations. Full-frame SQL counts include projections triggered by session transactions.

With the 250 ms p95 headroom budget, the largest passing tested single-player battle had **1,000 NPCs** (161.1 ms p95); **1,250 exceeded it** (279.4 ms). Two players with 1,000 NPCs passed at 236.2 ms. Ten players with 500 NPCs passed near the threshold at 242.4 ms; ten players with 1,000 NPCs took 445.1 ms.

The 1,000 ms cadence budget held for 2,000 fighting NPCs with one player (428.5 ms p95) and ten players (892.4 ms). A 4,000-NPC battle with one player exceeded it at 1,593.5 ms p95. Four thousand idle NPCs with one player took 744.7 ms p95. Capacity between tested populations remains unmeasured.

The following pairs are **median / p95 full-frame milliseconds**.

| NPCs with one player | Idle | Fighting |
| --- | --- | --- |
| 0 | 2.6 / 5.8 | n/a |
| 1 | 3.2 / 8.1 | 4.8 / 7.3 |
| 100 | 7.2 / 12.2 | 14.5 / 19.4 |
| 500 | 26.8 / 40.0 | 55.0 / 66.7 |
| 1,000 | 69.3 / 81.0 | 135.0 / 161.1 |
| 1,250 | n/a | 246.6 / 279.4 |
| 1,500 | n/a | 320.9 / 370.1 |
| 2,000 | 211.2 / 228.9 | 393.1 / 428.5 |
| 4,000 | 685.9 / 744.7 | 1395.1 / 1593.5 |

The player sweep holds one NPC constant. In the fighting comparison with 101 total ships, 100 NPCs plus one connected player took 14.5 ms median, while 100 connected players plus one NPC took 527.4 ms median. The NPC and player cost curves have different scaling because players introduce additional sessions, personal monitoring and observer output.

| Connected players with one NPC | Idle median / p95 ms | Fighting median / p95 ms | Fighting mean SQL commands / opens |
| --- | --- | --- | --- |
| 1 | 3.2 / 8.1 | 4.8 / 7.3 | 25.9 / 9.0 |
| 10 | 31.1 / 41.3 | 50.9 / 61.1 | 228.6 / 81.9 |
| 25 | 64.4 / 67.7 | 123.6 / 134.2 | 569.1 / 202.0 |
| 50 | 132.7 / 135.9 | 244.8 / 270.6 | 1119.8 / 402.0 |
| 100 | 298.0 / 342.7 | 527.4 / 573.0 | 2238.5 / 802.0 |

| Mixed battle | Median / p95 ms | Mean SQL commands / opens | Mean allocations MiB/frame | Encoded output MiB/step |
| --- | --- | --- | --- | --- |
| 2 players, 1,000 NPCs | 207.7 / 236.2 | 3024.8 / 17.0 | 267.5 | 1.30 |
| 10 players, 100 NPCs | 62.5 / 65.5 | 521.7 / 81.9 | 26.7 | 0.30 |
| 10 players, 500 NPCs | 207.4 / 242.4 | 1713.7 / 81.9 | 131.0 | 1.37 |
| 10 players, 1,000 NPCs | 381.7 / 445.1 | 3204.5 / 81.9 | 352.1 | 2.70 |
| 10 players, 2,000 NPCs | 791.0 / 892.4 | 6192.5 / 81.9 | 1096.7 | 5.38 |

### Where the time goes

| One-player battle | Full-frame median ms | Motion median ms | Projection median ms | NPC-combat phase median ms | Resident-operation component median ms | Mean allocations MiB/frame |
| --- | --- | --- | --- | --- | --- | --- |
| 1,000 NPCs | 135.0 | 38.9 | 50.5 | 24.4 | 50.9 | 257.0 |
| 2,000 NPCs | 393.1 | 155.6 | 108.2 | 90.0 | 197.4 | 914.8 |
| 4,000 NPCs | 1395.1 | 576.0 | 212.8 | 511.2 | 958.9 | 3355.5 |

At 2,000 idle NPCs, the full frame allocates 657.0 MiB on average and the collision-only component takes 135.6 ms median. At 4,000 fighting NPCs, allocations reach approximately 3.28 GiB per frame; the collision-only component takes 473.5 ms median and the complete resident-operation component takes 958.9 ms. The paired collision loops, heap-allocated position arithmetic and per-hit contributor-map copies explain why both simulation cost and allocations grow faster than NPC count. Their individual CPU shares need a sampling profiler to separate completely.

SQL is also material. A one-player/1,000-NPC battle averages 3,006.4 EF commands and nine EF connection opens per complete frame, while its resident-operation component issues zero. `CombatTransactionBridge` flushes every dirty combat object before every EF transaction, including read snapshots. This moves projection work into session monitoring. Coalescing that work must retain transaction consistency and measure checkpoint capture pauses.

The isolated component omits output and session housekeeping; the full-frame measurement determines the capacity figures. Raw JSON reports include every frame, CPU/SQL/allocation/byte counters, inclusive phase distributions, cold setup and separate checkpoint capture/write/SQL records. Raw SQLite backup/file connections are represented by checkpoint capture/write times and bytes beyond the EF connection counters. Ignored reports live under `artifacts/npc-capacity/{final,mixed,boundary}`. Exploratory tiered runs and the separate development probe remain outside those final result directories.

### Optimization candidates supported by the profile

1. **Prune collision candidates and remove temporary vectors from pair rejection.** `ShipCollisions.Resolve` runs every body against every other body. `Potential` constructs four `SpacePosition` reference objects before rejecting a distant pair. A spatial index using swept bounds can reduce the number of candidates while retaining the existing response calculation and deterministic ordering. Scalar arithmetic in the rejection path can reduce allocations independently. The pinned Carbon engine obtains candidates from `mPartition->GetCollisionCandidates` before invoking `Potential`, in both `Gradient` and `CalculateCollisionResponse`. Preserve high-speed crossings, overlapping hulls and owner/observer ordering in the affected tests. [Eden collision loop](../../src/Eden.Gameplay/Flight/SystemMotion.cs), [position representation](../../src/Eden.Gameplay/Flight/SpacePosition.cs), [Carbon Destiny 27ee40b](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ballpark.cpp#L2519-L2548)
2. **Reduce whole-world projection at read transaction boundaries.** `CombatTransactionBridge.TransactionStarting` flushes every dirty combat object before every EF transaction. Read snapshots can therefore project NPC positions, aggression and combat contributions during ordinary monitoring. The `combat.project` phase and command counts expose this work in the full-frame benchmark, while the component replay remains SQL-free. Narrow the snapshot inputs and preserve coherent reads, rollback isolation, kill/loot settlement and checkpoint recovery. Batch projection work that remains necessary. [Transaction bridge](../../src/Eden.World/Runtime/CombatPersistenceBridge.cs), [resident projection](../../src/Eden.World/Runtime/Combat/LiveCombatProjection.cs)
3. **Accumulate damage history without copying the victim's entire contributor map per hit.** `LiveNpcCombatRules.Fire` copies `ship.Contributions` on each NPC shot. When many distinct NPCs repeatedly shoot one player, both the map and the number of copies grow with population. Return typed contribution changes and accumulate them under the world owner, or batch volleys against one victim while publishing immutable snapshots. Preserve damage totals, final-blow metadata and checkpoint/restart history. This is a source-supported candidate; the benchmark does not isolate its exact share of CPU time. [NPC volley rules](../../src/Eden.Gameplay/Combat/LiveNpcCombatRules.cs)
4. **Share versioned NPC contacts and index per-client lookups.** `SimulationContacts` rebuilds a system-wide contact array for callers, while handlers repeatedly test visible membership and locate earlier weapon contacts. Share immutable current views with invalidation on motion, damage, weapon deadlines, EWAR and membership changes. Retain same-tick notifications and visibility rules. The contact/observer component timings quantify the current combined cost. [Resident contacts](../../src/Eden.World/Runtime/Combat/LiveSimulation.cs), [NPC handler](../../src/Eden.Protocol/Combat/NpcCombatHandler.cs), [observer scene](../../src/Eden.Protocol/Flight/FlightNpcScene.cs)
5. **Review active-object eviction before increasing global populations.** The runtime's 8,192-NPC and 512-ship retention thresholds clear the complete combat cache. Crossing a threshold can cause repeated projection and hydration while those actors remain active. An eviction policy should distinguish active ownership from cold entries and project payments before evicting. The population ladder remains below these thresholds. [Runtime retention](../../src/Eden.World/Runtime/Combat/LiveCombatRuntime.cs)

### Reproduce

```powershell
.\tools\Measure-NpcCapacity.ps1 -NpcCounts @(0,1,100,500,1000,2000,4000) -PlayerCounts @(10,25,50,100) -Output artifacts/npc-capacity/final
.\tools\Measure-NpcCapacity.ps1 -NpcPlayers 10 -NpcCounts @(100,500,1000,2000) -PlayerCounts @() -Modes combat -NoBuild -Output artifacts/npc-capacity/mixed
.\tools\Measure-NpcCapacity.ps1 -NpcPlayers 2 -NpcCounts @(1000) -PlayerCounts @() -Modes combat -NoBuild -Output artifacts/npc-capacity/mixed
.\tools\Measure-NpcCapacity.ps1 -NpcCounts @(1250,1500) -PlayerCounts @() -Modes combat -NoBuild -Output artifacts/npc-capacity/boundary
```

The runner restores all environment variables it changes, stops at the first failed validation, and writes a JSON report plus TRX for every case. Normal test execution uses a small two-player/eight-NPC idle case. Large workloads require explicit runner parameters or the `EDEN_CAPACITY_NPCS`, `EDEN_CAPACITY_PLAYERS`, `EDEN_CAPACITY_MODE` and `EDEN_CAPACITY_FRAMES` environment variables. `EDEN_CAPACITY_OUTPUT` chooses the report directory. Benchmark state never uses the normal world save.

**Automated validation.** All 28 controlled population cases passed with thirty measured frames each, taking 778.02 seconds combined test time. The final affected filter `FullyQualifiedName~NpcCapacityBenchmarkTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~LiveSimulationTests|FullyQualifiedName~LiveCombatObjectTests|FullyQualifiedName~NpcAggressionTests` passed 59 tests in 22 seconds. Tool restore, solution restore, the Release solution build (zero warnings/errors), both Python policy suites (3 catalog and 9 formatting cases), formatting across 892 files, strict width checks and diff whitespace passed. The full .NET suite was not requested or run. Development corrected argument adapters, nullable coordinate assignment and an enum spelling before the passing runs. Assertions added after the capacity runs verify the matched masses and radii; the final small replay passed those checks.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** None required for this automated benchmark milestone.


## Resident drone flight and combat, November 6, 2026

Controlled drones now advance on typed resident state under the existing world owner. The system view shares current identities, profiles, positions and health across sessions. Pilot inputs retain skills, fit bandwidth/control distance, qualifications and aggression settings until the corresponding committed rows or catalog change. Contact comparison uses an identity lookup to avoid repeatedly scanning the old contact array for every visible drone.

The unchanged one-second movement step and Carbon-derived follow/orbit rules remain based on Destiny revision `27ee40b38a563d29054b32f64c3ebc87fabaec1f`, [Ballpark.cpp](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ballpark.cpp). Late callbacks take one bounded step. Saved weapon deadlines prevent duplicate payment. Ordinary drone damage and direct NPC retaliation update resident health, contribution history and shared weapon deadlines. NPC fire can stay resident while the ship controls drones.

Recall, mining NPC side effects, fatal drone volleys and fatal retaliation retain SQL transactions. A fallback projects prior dirty state and uses the already selected accuracy draws when settling the current interval. Checkpoints project position, velocity, health, controller/activity, command timestamps and paid shot deadlines. Existing saves need no schema change. Transaction-local reads bypass published views. Committed commands, skill/fit/settings changes, catalog updates and inventory/ownership changes invalidate the appropriate inputs; rolled-back damage cannot appear in later observer reads.

### Matched session replay

`DronePerformanceReplayTests.ReplayNormalSessions` runs normal session ticks, including motion, observer contacts, updates and owner output encoding, with one additional pilot who owns no drones. It uses the same fixture workload on baseline `c62a4a0` and the optimized branch, two warmups and thirty measured one-second frames. The isolated catalog gives NPCs and ships large shields to retain combat throughout the sample. Pursuit starts 40 km away. This is a synthetic fixture replay with serial pilot scheduling. Network backpressure, real catalog scale, native rendering and concurrent request arrival are outside this measurement.

| Owners / deployed drones | Scenario | Baseline median / p95 ms | Resident median / p95 ms | Baseline / resident mean SQL commands | Baseline / resident mean opens | Baseline / resident mean fits |
| --- | --- | --- | --- | --- | --- | --- |
| 1 / 0 | orbit | 5.70 / 8.11 | 9.50 / 11.25 | 30.50 / 26.53 | 16.07 / 12.07 | 0.00 / 0.00 |
| 1 / 1 | orbit | 46.89 / 56.81 | 5.25 / 6.54 | 184.33 / 29.67 | 79.63 / 13.73 | 1.23 / 0.00 |
| 1 / 5 | combat | 59.12 / 71.95 | 4.92 / 7.19 | 292.13 / 33.07 | 80.87 / 13.57 | 1.27 / 0.00 |
| 1 / 5 | orbit | 64.44 / 86.55 | 5.18 / 7.75 | 289.90 / 33.60 | 79.00 / 13.73 | 1.20 / 0.00 |
| 1 / 5 | pursuit | 91.81 / 110.78 | 11.12 / 22.50 | 273.50 / 35.33 | 76.20 / 14.47 | 1.00 / 0.00 |
| 10 / 50 | combat | 990.94 / 1149.90 | 40.99 / 52.87 | 7703.03 / 228.63 | 375.63 / 87.93 | 10.23 / 0.00 |
| 10 / 50 | orbit | 906.63 / 1052.81 | 55.91 / 75.00 | 7689.37 / 205.37 | 334.67 / 82.97 | 10.20 / 0.00 |
| 10 / 50 | pursuit | 893.14 / 1440.03 | 43.19 / 49.42 | 7704.10 / 242.93 | 379.33 / 93.10 | 10.00 / 0.00 |

Full frames still contain SQL from other session work and projections triggered when that work crosses SQL boundaries. These counts include those projections. The separate warmed drone-only steps and observer bursts issued zero SQL commands, connection opens or fitting calculations in all eight cases. A deterministic direct-combat regression separately exercises due nonfatal NPC retaliation and duplicate shot callbacks with zero SQL.

After the measured frames, the replay warms a direct drone operation, advances another interval without intervening session SQL, and explicitly checkpoints that dirty state. The fifty-drone orbit/pursuit/combat checkpoints respectively issued 54/78/81 EF commands through one connection. Capture/project/copy held the world owner for 3.84/2.13/4.23 ms; subsequent file writing took 14.34/11.55/16.65 ms outside that owner. Checkpoint costs include other outstanding combat resources. Raw SQLite backup connections are represented by capture/write time and bytes, beyond the EF counters.

Automated cases verify exact position/health round trips after checkpoint, startup control release, skill invalidation, retained raw damage, unchanged-state rejection, concurrent scoop conservation, rollback isolation, owner/observer notifications, fatal settlement, bounded late movement and duplicate callbacks. The contributor guide now requires shared warmed reads, explicit invalidation, transaction projection and measured SQL/connection/fit budgets for simulation changes.

The baseline replay passed eight scenarios in 114 seconds; the final resident replay passed eight in 16 seconds. Raw reports are in ignored `artifacts/drone-replay/drone-replay-final.trx`; the separate baseline checkout has `artifacts/drone-baseline-results/drone-baseline.trx`.

**Automated validation.** Tool and solution restore, solution build, both Python policy suites (3 catalog and 9 formatting cases), formatting across 891 files, the width check and diff whitespace passed. The final affected run passed 313 tests with zero failures/skips in 91 seconds. The eight final resident replay scenarios passed in 16 seconds. The full .NET suite was not requested or run. Development corrected controller-index invalidation, a prerequisite-sensitive test fixture, a distinct database instance in the retaliation fixture, and an asynchronous test flush. An overlapping development build encountered testhost file locks before the final serial build/check sequence.

The exact final affected filter was `FullyQualifiedName~ResidentDroneTests|FullyQualifiedName~LiveDroneRulesTests|FullyQualifiedName~CombatDroneTests|FullyQualifiedName~DroneSynchronizationTests|FullyQualifiedName~DroneRecoveryTests|FullyQualifiedName~LiveCombatObjectTests|FullyQualifiedName~LiveSimulationTests|FullyQualifiedName~NpcAggressionTests|FullyQualifiedName~NpcRepairTests|FullyQualifiedName~NpcMissileTests|FullyQualifiedName~LiveWorldTests|FullyQualifiedName~ResidentReadTests|FullyQualifiedName~ShipRigTests|FullyQualifiedName~InventoryOwnershipTests|FullyQualifiedName~ShipStorageTests`. Replay used `FullyQualifiedName~DronePerformanceReplayTests` with one worker; the affected run used two workers.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** None required for this performance milestone.

## Shared drone contacts, November 6, 2026

Drone contacts are shared per system in the resident world. Committed drone, inventory, pilot, ship and fit changes invalidate the view. Controller membership is indexed by pilot and ship. The owner advances its drones before refreshing its scene once; observers still refresh without owning drones. Transaction-local reads bypass publication.

Automated validation uses `ResidentDroneTests.WarmObserversAndControllerChecksShareContactsAndSeeCommittedOrders`. Ten warmed observer/controller reads issue zero SQL commands, connections or fitting calculations and see recall changes immediately. The baseline five-drone owner path issued 178 SQL commands with two contact reads. This milestone removes the initial owner read and shares unchanged observer contacts. Movement remains SQL-backed at this milestone.

The focused filter `FullyQualifiedName~ResidentDroneTests|FullyQualifiedName~CombatDroneTests|FullyQualifiedName~DroneSynchronizationTests|FullyQualifiedName~DroneRecoveryTests|FullyQualifiedName~ResidentReadTests` passed 164 tests in 67 seconds. Tool/solution restore and solution build passed with zero warnings/errors. Both Python suites passed (3 catalog, 9 formatting cases); formatting checked 887 files and the width limit. Initial development corrected a changed parameter type and fixture property reads included in the measurement. No full .NET suite was requested or run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** None.

## Contents

- [Swept collision candidate index (November 6, 2026)](#swept-collision-candidate-index-november-6-2026)

- [NPC and player capacity (November 6, 2026)](#npc-and-player-capacity-november-6-2026)

- [Resident drone flight and combat (November 6, 2026)](#resident-drone-flight-and-combat-november-6-2026)
- [Shared drone contacts (November 6, 2026)](#shared-drone-contacts-november-6-2026)

- [Resident system motion and NPC acquisition (November 5, 2026)](#resident-system-motion-and-npc-acquisition-november-5-2026)
- [Resident monitoring and scene views (November 5, 2026)](#resident-monitoring-and-scene-views-november-5-2026)
- [Live combat objects and ten-Brutix replay (November 5, 2026)](#live-combat-objects-and-ten-brutix-replay-november-5-2026)
- [Resident world checkpoints and ten-Brutix replay (November 5, 2026)](#resident-world-checkpoints-and-ten-brutix-replay-november-5-2026)
- [Ten-Brutix grouped-gun benchmark (November 5, 2026)](#ten-brutix-grouped-gun-benchmark-november-5-2026)
- [Resident ship and turret fits (November 5, 2026)](#resident-ship-and-turret-fits-november-5-2026)
- [Repeated turret cycle work (November 5, 2026)](#repeated-turret-cycle-work-november-5-2026)
- [Fitted combat catalog reads (November 5, 2026)](#fitted-combat-catalog-reads-november-5-2026)
- [Shared session performance diagnostics (November 5, 2026)](#shared-session-performance-diagnostics-november-5-2026)
- [Startup protocol and milestones](#startup-protocol-and-milestones)

## Resident system motion and NPC acquisition (November 5, 2026)

Shared ship positions, NPC positions/velocities/navigation and aggression now advance directly on typed live objects. `LiveSystemMotionRules` stages player navigation and NPC movement before resolving collisions and publishing a complete result under the existing world owner. `LiveNpcAcquisitionRules` selects the nearest eligible ship, retains reaction/cycle deadlines and stages all additions before publication. SQL remains the cold-load and persistence boundary. Normal hosting enables this path; the SQL adapters remain available for nonresident worlds and controlled comparisons.

Motion still uses Eden's existing one-second Destiny step, pursuit/orbit hysteresis, patrol destinations, wall-clock NPC movement cap and collision rules. The reference remains Carbon Destiny revision [`27ee40b38a563d29054b32f64c3ebc87fabaec1f`](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ballpark.cpp), `OnTick`, `Potential`, `CollideTwoSpheres` and `Integrate`. No wire format or simulation-rate change is introduced.

System membership, immutable hull profiles, effective tackle attributes and candidate identities are retained within the bounded world runtime. Tackle changes invalidate effective attributes immediately. Committed position/ship/pilot changes invalidate acquisition inputs; NPC membership, aggression and catalog changes invalidate the corresponding state. Same-tick contact reads rebuild health, weapon effects and courses from the current objects, without projecting a paid volley merely to read it back. Acquisition coalesces unchanged candidate scans within one second. SQL-backed destruction, missile impacts, transitions and warp task progress retain their transaction boundaries and see prior dirty state first.

Population refresh uses explicit deadlines: occupied pirate encounter deadlines and the latest dead guard slot determine the next scan. Historical dead guards with a living replacement and retired rows do not create perpetual work. The fixture seeder uses the same respawn schedule. Custom seeders retain a conservative one-second default unless they provide `NextUpdate`. Unsupported overdue catalog pools retry at most once per second. Membership or catalog changes invalidate the schedule. Guard patrols and NPC returns continue every simulation step; postponing the population scan does not postpone movement or committed changes.

Dirty positions and NPC motion join combat resources in the checkpoint transaction. A moving NPC retains unchanged saved damage, including damage above its current hull capacity. A checkpoint or legacy SQL operation projects each latest object state, rather than every intermediate simulation step. The 15-second capture still owns the world briefly while projecting/copying its RAM image; filesystem writing occurs after releasing that owner. Opt-in checkpoint diagnostics count EF commands/opens separately from session frames. This is deferred and coalesced SQL, not elimination of persistence work. No schema migration is required.

### Ten-Brutix comparison

Both modes use live combat objects, resident read views, six NPCs, ten Navy Issue Brutixes with six grouped guns/Void M each, two warmups and thirty measured one-second frames. `EDEN_PERFORMANCE_SIMULATION_OBJECTS=0` selects the same-build SQL-motion/acquisition control; `1` selects resident simulation. Keep `EDEN_PERFORMANCE_LIVE_WORLD=1`, `EDEN_PERFORMANCE_COMBAT_OBJECTS=1` and `EDEN_PERFORMANCE_RESIDENT_READS=1`. Run `FullyQualifiedName~TenBrutixesFire` with one test worker, the isolated saved-fit source and separate output directories. Build/test output uses `--artifacts-path artifacts/live-simulation/build`. Tests copy the source databases; the running host and live save remain untouched.

| Warm measurement | SQL simulation control | Resident simulation |
| --- | --- | --- |
| Concurrent pilot tick median / p95 | 21.18 / 27.00 ms | 4.42 / 12.95 ms |
| Concurrent complete frame median / p95 | 21.45 / 28.25 ms | 4.71 / 13.41 ms |
| Mean SQL commands / opens per concurrent frame, all 30 frames | 118.33 / 7.27 | 0 / 0 |
| Mean SQL commands / opens per concurrent firing frame | 162.53 / 7.67 | 0 / 0 |
| Serial complete frame median / p95 | 13.39 / 19.05 ms | 4.72 / 8.50 ms |
| Cold setup including snapshot copy, migration, cloning, grouping and locking | 12.87 seconds | 12.85 seconds |

Every one of the 60 guns consumed exactly 16 rounds across the two warmups and measured frames. All thirty warm frames, including the fifteen firing frames, issued no SQL commands or opens in both resident scheduling modes. This does not count background persistence as free: the concurrent resident run's three dirty checkpoints separately issued **153, 152 and 148 EF commands**, with one EF connection each. Capture/project/copy held the world owner for **7.54, 9.20 and 5.11 ms**; subsequent file writes took **34.69, 30.33 and 29.91 ms** outside that owner. The initial setup snapshot issued no EF commands. The saver is drained before verification queries, so a final verification read cannot hide dirty projection work outside the measured checkpoint. Raw SQLite backup/file connections are represented by capture/write time and bytes, rather than EF connection counters.

Both warm tick and frame p95 meet the initial 250 ms target in this workload. The largest phase in the slowest resident concurrent frame was `motion`, **10.69 ms**, including synchronization/ownership waiting; the complete frame maximum was **15.79 ms**. Checkpoint capture remains a short shared-owner pause, and these measurements do not isolate pure physics CPU from waiting. SQL-backed missiles, deaths and other transactional events can still cause projections; this sustained-gun workload does not measure their cost. The replay includes Blue output encoding and diagnostics, but excludes network backpressure, native rendering and ordinary request arrival-to-reply latency. Firing-frame SQL placement varies with worker/checkpoint scheduling in the control, so the all-frame counts and separate save costs are the more useful comparison.

Raw local logs and parsed phase summaries are in `artifacts/live-simulation/final/{control,after}/grouped-fleet-{concurrent,serial}.log[.json]` (ignored).

**Automated validation:** Tool/solution restore and the isolated solution build passed, with zero warnings/errors (final build approximately **7.1 seconds**). The broad focused run passed **821 tests in 2 minutes 56 seconds**, using two workers, before the final damage-preservation and acquisition-invalidation refinements. Subsequent affected checks passed **71 in 17 seconds**, then **46 in 11 seconds** after the last invalidation change. Both final benchmark modes passed their concurrent and serial cases (**2 tests each, approximately 23 seconds per mode**), including paid rounds, effects and checkpoint reopen assertions. Both Python policy suites passed (**3 catalog**, **9 formatting**); formatting and diff verification passed. No full .NET regression suite or client testing was run.

The broad focused filter was `FullyQualifiedName~LiveSimulationTests|FullyQualifiedName~LiveCombatObjectTests|FullyQualifiedName~LiveWorldTests|FullyQualifiedName~ResidentReadTests|FullyQualifiedName~ResidentFitTests|FullyQualifiedName~Eden.Protocol.Tests.Combat|FullyQualifiedName~Eden.Protocol.Tests.Flight|FullyQualifiedName~TypedSessionTests|FullyQualifiedName~CharacterLogoffTests|FullyQualifiedName~TargetingTests|FullyQualifiedName~TackleTests|FullyQualifiedName~PropulsionTests|FullyQualifiedName~SupportModuleTests|FullyQualifiedName~HardenerTests|FullyQualifiedName~ArmorRepairTests|FullyQualifiedName~HullRepairTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting`. Final filters selected `LiveSimulationTests`, `LiveCombatObjectTests`, `NpcMissileTests`, `NpcAggressionTests`, `NpcPursuitTests` and `LiveWorldTests`; the last invalidation run selected the first, second, fourth and fifth classes. Exact filters and TRX results remain in `artifacts/live-simulation`.

Deterministic tests assert **one tackle read and one aggression read** for a cold motion operation with either **one or 24 NPCs**, then **zero SQL commands, opens, tackle/aggression reads and fitting calculations** across twelve warm motion steps and repeated contact/acquisition bursts. Additional checks cover SQL/object motion parity for coasting, pursuit, returning and guard patrol; same-tick return-to-idle reacquisition; a full 600-second respawn deadline and same-tick reads after replacement; web invalidation/restoration; unchanged historical damage; current volley damage in contacts without projection; observer courses/disengagement; missiles and fatal contacts; lifecycle transitions; and persisted positions, velocities, navigation, aggression and combat resources after checkpoint reopening.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** On a copied save after an authorized host restart, compare grouped gunfire and NPC courses with two observers, then disengage/dock/reconnect. Native gameplay feel and network backpressure remain unverified.

## Resident monitoring and scene views (November 5, 2026)

The next pass works directly on `main` after `0e44208`. The preceding ten-Brutix replay still spent about 712 commands per firing frame, although its paid gunfire and health calculations were SQL-free. Detailed `read.*` phases now separate learning/skill monitors, wallet and loyalty reads, fitting attributes, asteroid/wreck/missile contacts, empty missile work, tackle sweeps and mail feeds. Timings remain opt-in through the existing diagnostics, and counts still include every world/catalog command executed by the ten simulated sessions.

Normal resident hosting now retains bounded, typed read views under the existing world owner. `LiveLearningState` derives booster expiry, effective learning attributes and remaining injection uses in Gameplay without another query at the deadline. Server retains immutable implant/learning snapshots, free-point balances, extraction revisions, wallet/loyalty balances, fitting attributes, asteroid/wreck/missile contacts, tackle effects and projectile activity indexes. The caches contain explicitly named views with fixed dependencies; they do not memoize arbitrary SQL. Each keyed view retains at most 512 entries. The missile index represents the saved projectile population and terminal history, and the tackle view represents saved tackle rows.

World row changes invalidate dependent views before releasing the serialized connection owner. Reads inside an existing connection/transaction bypass publication into the shared cache. Commits become visible on the next read; rollbacks can cause a conservative reload but cannot publish rolled-back state. Writable catalog disposal and schema changes clear dependent views. This keeps existing transactions as mutation owners for wallets, character services, fitting, inventory, mining, loot and projectiles. Views are reconstructed from saved inputs on reopen; the 15-second checkpoint and final shutdown paths remain unchanged.

Scene occupancy distinguishes item membership changes from ammunition quantity/crystal wear updates. SQLite's authorizer identifies the columns a statement may update; the row hook records actual writes. Insert/delete or ownership/location/flag/type changes invalidate wreck views, while quantity-only updates retain the same occupancy view. Wreck views expire at the next global wreck deadline, so cached empty scenes cannot suppress cleanup of expired contents or stored ships. Unqualified deletion retains row callbacks; schema operations retain ordinary SQLite DROP/ALTER behavior. The implementation follows SQLite's [authorizer action argument contract](https://www.sqlite.org/c3ref/c_alter_table.html) and [authorizer/truncate behavior](https://www.sqlite.org/c3ref/set_authorizer.html). Identifiers are compared without case sensitivity, and callbacks only record in-memory invalidation metadata.

An empty tackle/activity index skips repeated sweeps. Missile indexes skip inactive system advances, empty impact-report reads and inactive NPC missile scans while preserving the NPC history-cleanup deadline. Actual launches, movement, impacts and destruction retain their existing transaction/rule paths and invalidate the indexes. Contact lists refresh immediately after those mutations. Mail and mailing-list feeds retain their existing cursor/batch delivery: an in-memory latest change ID proves an unchanged feed empty, while any new feed row re-enters the normal notification/visibility path. Notifications keep their existing scheduled cadence and do not wait for a checkpoint.

Deterministic checks require zero SQL commands, connection opens and fit calculations for repeated warm monitoring, fitting, empty scene/activity and empty mail-feed reads. They also cover booster/injection expiry immediately before and at the deadline without SQL, committed/rolled-back balance changes and checkpoint reopening, refitting invalidation, and lowercase SQL that distinguishes quantity from membership changes. Existing automated catalog cohorts now exercise live views for all learning implant/booster profiles. Simulated native service tests cover booster consumption, injector allowances, mail across sessions, mailing-list welcome/broadcasts, missile launch/impact and observer delivery, core-strength expiry, wreck pickup/empty flags/expiry, and asteroid initialization/depletion/reopen.

### Ten-Brutix comparison

Both modes keep live combat objects enabled and the same six-NPC, ten-Navy-Issue/six-gun/Void-M workload, two warmups, thirty measured one-second steps, paid repetitions and real 15-second checkpoint callback. `EDEN_PERFORMANCE_RESIDENT_READS=0` is the same-build control; `1` enables the new views. Normal hosting enables them. Run `FullyQualifiedName~TenBrutixesFire` with `xUnit.MaxParallelThreads=1`, `EDEN_PERFORMANCE_LIVE_WORLD=1`, `EDEN_PERFORMANCE_COMBAT_OBJECTS=1`, the existing saved-fit `EDEN_PERFORMANCE_SOURCE` and separate output directories. Build and test use `--artifacts-path artifacts/resident-reads/build`, keeping the running host's normal binaries untouched. Source databases are copied, and both runs retain checkpoint reopen assertions. These Debug measurements include encoding and diagnostics but exclude network backpressure, native rendering and ordinary call arrival-to-reply latency.

| Warm measurement | Views disabled | Views enabled |
| --- | --- | --- |
| Concurrent pilot tick median / p95 | 111.11 / 154.23 ms | 17.10 / 19.87 ms |
| Concurrent ten-pilot frame median / p95 | 119.15 / 161.74 ms | 17.43 / 20.24 ms |
| Concurrent firing-only pilot tick p95 | 154.01 ms | 20.03 ms |
| Serial pilot tick median / p95 | 7.66 / 14.40 ms | 0.30 / 7.88 ms |
| Serial ten-pilot frame median / p95 | 83.84 / 89.31 ms | 10.95 / 14.23 ms |
| Concurrent EF commands per firing frame, mean | 714.60 | 114.60 |
| Concurrent opens per firing frame, mean | 392.20 | 7.67 |
| Concurrent EF commands across all frames, mean | 622.67 | 113.10 |
| Concurrent opens across all frames, mean | 369.50 | 7.30 |

The complete thirty-frame concurrent workload drops from **18,680 to 3,393 measured EF commands** (81.8% fewer) and **11,085 to 219 connection opens** (98.0% fewer). Firing-only counts fall by 84.0%, but that comparison includes a timing shift: removing unrelated transactions lets dirty combat state remain in objects until a later shared simulation operation or checkpoint. Serial firing frames average 65.60 commands while its all-frame mean is 116.80, showing the same deferred projection. Count all frames when assessing total work. Background checkpoint operations do not inherit the session diagnostic scope; their wait/capture/write timings are recorded separately.

Every measured background-monitoring phase now executes **zero SQL commands and opens zero connections**, including mail/list feeds. Warm fitting reads, asteroid/wreck/missile contacts, empty projectile advances/reports and empty tackle sweeps likewise execute zero SQL. The remaining all-frame work is concentrated in combat projection and the shared system motion, NPC contact and acquisition paths. Projection still maintains the relational compatibility image for older operations; moving shared NPC simulation/acquisition fully onto the live objects is the next architectural candidate. Active missile/tackle work and other actual mutations continue through their validated transaction paths.

The worst concurrent pilot tick is **20.69 ms**, including 4.27 ms in motion, 5.16 ms in contacts and 10.86 ms across updates. Its bank phase spends 7.45 ms including ownership wait, while the object calculation itself takes 0.03 ms; SQL time is 1.60 ms. Concurrent phase sums overlap across workers and must not be added as independent wall time. The warm pilot p95 is 230.13 ms below the initial 250 ms target. Results are from these simulated sessions and do not establish native gameplay feel or hundreds-of-pilots capacity.

Cold setup including copy/migration/cloning/grouping/locking/first volleys is **11,933.33 / 11,827.51 ms** for control/enabled concurrent modes and **7,789.34 / 8,166.70 ms** for serial modes. All sixty guns consume sixteen rounds each across warmups and measurements; every warm sample has zero fitting rebuilds and active guns, and reopened checkpoints match magazines, bank links, ship positions and target health. Enabled snapshots are 5,705,728 bytes, capture under ownership takes **1.57–4.30 ms**, and disk writes after release take **21.98–46.82 ms**. Encoded output is 2,826,089 concurrent and 2,793,198 serial bytes; damage draws and worker interleaving vary between runs. Both cases pass in each final mode: **26 seconds control, 21 seconds enabled**.


**Automated validation:** pinned tool/solution restore and the isolated-output build passed with zero warnings/errors (2.27 seconds). The affected class filter in `artifacts/resident-reads/final-filter.txt` passed **943 cases in 2 minutes 3 seconds** with two conservative workers. It selects resident views/world/fits, combat and missiles, targeting/navigation, inventory/storage/mining/loot, implants/boosters, free points/injectors/extraction, wallet/loyalty, mail/list delivery, module effects/repairs, session lifecycle and hosting. Both Python policy suites passed (3 catalog, 9 formatting), formatting verification covered 691 files, and diff checks passed. No full .NET regression suite was requested or run. Exact filters, TRX results, phase reports and fleet logs are retained under ignored `artifacts/resident-reads/`.

Intermediate checks exposed two legacy migration failures when the existing delete authorizer also affected DROP/ALTER operations; the hook now distinguishes schema work and invalidates both combat and read views. The two migration checks then passed. A wider filter found sixteen targeting/mining fixture failures that also reproduced against the unchanged `0e44208` build (16 failed / 9 passed in five seconds). Those fixtures used the former X-axis launch and fixed clearance assumptions. They now place asteroids using `StationUndocking.Launch`, stop initial undock drift where the test requires a stationary scene, provide the docking store and start the docking case inside its allowed range. The new-station test checks the actual launch model. No production undocking, targeting or mining rules changed. All thirty targeting/mining/resident-view checks passed after these fixture corrections, followed by the passing 943-case affected run.


**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** After a normal rebuild/restart, check grouped combat and loot changes with another pilot observing; check a booster expiry and externally delivered mail. The running host and live world were untouched.

## Live combat objects and ten-Brutix replay (November 5, 2026)

Normal resident hosting now keeps authoritative typed combat objects alongside the relational world image. `LiveCombatShip`, `LiveCombatNpc`, `LiveHealth`, turret/magazine state and aggression deadlines live in Gameplay models; `LiveCombatRules` and `LiveNpcCombatRules` calculate paid outcomes without persistence dependencies. Server owns hydration, serialized publication, invalidation and projection. A warm nonfatal turret volley, public health read or due direct NPC gunshot does no SQL and opens no database connection. Immutable replacements publish only after every bank member validates, so a rejected member cannot spend earlier members' ammunition or capacitor. Misses still pay; energy crystals retain their normal wear/breakage rules. Damage contributions accumulate in objects for subsequent interaction reports.

Objects share the existing world owner, independently of the requesting connection. Motion commits refresh loaded positions, velocities and NPC health before releasing their connection lease. Contact snapshots are shared within a simulation step and overlay current object health and weapon intervals; unchanged acquisition candidate sets share their one-second deadline, including interleaved sets. Contact construction retains population/retirement invalidation. The Destiny step remains one second and notification adapters retain the existing paid-effect, damage, observer and capsule ordering.

Legacy transactions first project dirty combat fields into the resident relational image; reads referencing mutable combat tables do the same. Their row changes invalidate affected profiles/resources through SQLite update hooks, including raw SQL and bulk deletion. Rolled-back changes may conservatively invalidate objects but cannot undo a previously paid object volley. Fitting revision triggers invalidate profiles on skills, fitted items, scripts, active modifiers, boarding and ownership changes. Weapon-bank changes and writable catalog disposal also invalidate dependent objects. Trusted shared motion has an explicit post-commit handoff; arbitrary SQL never refreshes objects from potentially stale tracked entities. Retention limits bound ship, NPC, item, guard-group, contact and acquisition caches; eviction first projects outstanding changes.

A fatal turret volley retains one SQL transaction for the entire bank payment and existing destruction, loot, bounty, security and history settlement. NPC hull loss, missile launch/flight/impact, tackle, fitting changes and other legacy operations still use established SQL boundaries. This milestone removes SQL from ordinary live gunfire and health calculations; it does not convert every world service or projectile into an object operation. Remaining operations can force a projection before the next 15-second disk checkpoint, but that projection targets the RAM image. The background saver projects outstanding objects before snapshot capture, observes object generations even without a SQL write, and retains atomic file replacement, retry and final-shutdown behavior. The shutdown lease permits the final projection while refusing new external operations. A crash still restores the last completed checkpoint.

Deterministic checks require **zero commands, zero connection opens and zero fitting calculations** for a warm shot plus ten health reads and for one- and two-gun volleys immediately after a real shared motion step. A due incoming NPC gunshot, duplicate same-deadline attempt and health read also require zero commands/opens, with exactly one paid cycle. Reopen checks inspect a copied completed checkpoint and the actual file after shutdown for ammunition, capacitor, cooldown, NPC health and damage contributions. Additional coverage exercises bank rejection/fatal settlement, repair deadlines and preserved damage on untouched layers, crystal tiers, projectile tiers, guard alerts, SQL readers, rolled-back refits, raw changes and repeated attempts against a removed NPC.

### Same-build RAM SQL versus object comparison

The ten-Brutix workload is unchanged: ten Navy Issues, six grouped Heavy Electron Blaster IIs and Void M each, six NPCs, two warmups and thirty measured simulation steps, including fifteen firing frames. Both modes use the resident world and actual 15-second checkpoint callback. Run `FullyQualifiedName~TenBrutixesFire` with `xUnit.MaxParallelThreads=1`, `EDEN_PERFORMANCE_LIVE_WORLD=1`, the same `EDEN_PERFORMANCE_SOURCE`, and separate `EDEN_PERFORMANCE_OUTPUT` directories. Set `EDEN_PERFORMANCE_COMBAT_OBJECTS=0` for the RAM-SQL control and `1` for live objects. The switch is a development host/test option; normal hosting enables combat objects. Runs are sequential and isolated from the running server and original save. Output includes Blue encoding, excludes socket backpressure/native rendering, and asserts zero warm fitting rebuilds, sixty paid magazines, active guns and reopened checkpoint agreement.

| Warm measurement, median / p95 | RAM SQL control | Live combat objects |
| --- | --- | --- |
| Concurrent individual pilot ticks | 206.34 / 314.69 ms | 105.49 / 126.42 ms |
| Concurrent complete ten-pilot frames | 213.38 / 318.56 ms | 111.12 / 135.33 ms |
| Concurrent firing-only pilot ticks | 265.85 / 376.92 ms | 112.69 / 132.30 ms |
| Serial individual pilot ticks | 24.51 / 34.35 ms | 8.64 / 16.28 ms |
| Serial complete ten-pilot frames | 206.84 / 281.07 ms | 94.16 / 105.69 ms |

Concurrent firing frames average **2,566.67 → 712.33 EF commands** (72.2% fewer) and **578 → 390.13 connection opens** (32.5% fewer). Every measured bank volley, health read and weapon publication has **zero commands and zero opens**, with no warm fit rebuilds. These counts include all ten pilots and the remaining projection/background work. The ten object calculations together take a median **0.28 ms** per firing frame; the surrounding bank phase also includes time waiting for ownership. Serial firing frames average 719.13 commands and 396.67 opens because dirty objects reach legacy transaction boundaries separately rather than coalescing across concurrent pilots.

The warm concurrent pilot p95 is **123.58 ms below** the initial 250 ms target, including a firing-only p95 of 132.30 ms. The slowest object pilot tick is **135.36 ms** and the slowest frame 135.46 ms. On that tick, motion takes 19.70 ms, contacts 29.16 ms and all update phases together 86.37 ms; within updates, background work takes 23.21 ms, combat 17.94 ms and missile handling 16.44 ms. SQL execution accounts for 3.91 ms of that pilot's tick. Shared ownership and remaining motion/contact/background operations are the next measured limits. Phase totals overlap across concurrent workers and must not be added as independent frame wall time. This replay measures tick completion and encoding, not ordinary request arrival-to-reply latency or network delay.

Cold setup, including snapshot copy, migration, cloning, grouping, locking and first volleys, takes **13,430.51 / 12,245.69 ms** for control/object concurrent cases and **8,518.65 / 8,116.79 ms** for serial cases. Every one of sixty guns consumes exactly sixteen rounds over the warmups and thirty samples; reopened checkpoints match magazines, bank links, player positions and target health. Object checkpoint captures hold ownership for **1.71–2.61 ms**, then disk writes take **23.43–25.70 ms** outside ownership; image size is 5,705,728 bytes. Concurrent encoded output is 2,800,881 bytes and serial output 2,793,565 bytes. Both cases pass in each mode (36 seconds control, 27 seconds objects). These are Debug measurements with diagnostics enabled on this workstation and a sustained-shield fixture, not hundreds-of-pilots capacity claims.

**Automated validation:** pinned tool restore, solution restore and build pass with zero warnings/errors (final integrated build 1.93 seconds). The affected class filter covers LiveCombatObject, WeaponBank, NpcCombat, NpcGuard, NpcAggression, NpcMissile, MissileCombat, PilotCombat, PilotHealth, EnergyTier, ProjectileTier, HybridTier, LiveWorld, ResidentFit, ShipCollision, TypedSession, CharacterLogoff, Ammunition, Inventory, ArmorRepair, HullRepair, Tackle, InteractionHistoryPersistence and Hosting: **454 cases pass in 1 minute 29 seconds**, using two conservative workers. After the final cache-coherence and ownership corrections, filter `FullyQualifiedName~LiveCombatObjectTests|FullyQualifiedName~WeaponBankTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~NpcGuardTests|FullyQualifiedName~EnergyTierTests|FullyQualifiedName~ProjectileTierTests|FullyQualifiedName~MissileCombatTests` passes **191 cases in 44 seconds**, using two workers. Integrating main through `eff2f6d` adds no combat changes; filter `FullyQualifiedName~LiveCombatObjectTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~Eden.Protocol.Tests.Chat|FullyQualifiedName~LiveWorldTests|FullyQualifiedName~ResidentFitTests` then passes **64 cases in 9 seconds**, using two workers. Both Python policy suites pass (3 catalog, 9 formatting); formatting verification covers 686 files, and diff checks pass. The full .NET suite was not requested or run. Exact filters, TRX results and both fleet reports are under ignored `artifacts/combat-objects/` in the implementation worktree.

Intermediate focused checks exposed a shutdown checkpoint rejecting its own projection and a motion commit releasing the connection before synchronization; both were corrected before passing validation. The last coherence checks also verify that removing an NPC cannot revive an old cached object on a later attempt and that SQLite replacement of a gun invalidates its previous owner's profile. An initial command-count assertion included SQL-performing fixture getters; it was corrected to measure the public combat/health operations with already-known pilot identifiers.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** After a normal rebuild/restart, compare grouped combat and incoming damage with another pilot observing; check native effects and ship-loss/capsule transitions. The running host and live world were left untouched.

## Resident world checkpoints and ten-Brutix replay (November 5, 2026)

Normal server startup now loads the saved world into a named, shared SQLite database held in RAM. `WorldDbContext` and launcher account operations route through the resident owner for that world path. This retains existing SQL/ORM operations, transactional rules, constraints, triggers, fitting revisions and schema 71. At that milestone it was a resident relational world; the [subsequent live-combat milestone](#live-combat-objects-and-ten-brutix-replay-november-5-2026) converts ordinary gunfire and health paths to objects. The catalog remains a separate read-only file and uses its existing caches. Development fixtures retain direct file mode unless they explicitly request a resident world.

The host retains an anchor connection for the image's lifetime and serializes synchronous world connection lifetimes through one owner. This prevents competing SQLite transactions from entering shared-cache lock retry paths. The owner currently covers the entire persistent world, so independent systems also share it. Provider disposal follows connection cleanup and performs a final flush. Normal session notification ordering and the one-second Destiny simulation step continue through the existing handlers.

Every **15 seconds**, one background worker checks for committed changes using the anchor connection's `data_version`. Unchanged worlds skip disk writes. Under the owner, the worker copies a consistent database image into a second private RAM database. It then releases the owner before backing that image up to a unique staging file, flushing the file and atomically replacing the saved world. The prior checkpoint remains at `world.sqlite.previous`. Writes made during disk persistence stay in the authoritative image and are included in a later generation. Only one checkpoint can be in flight; overlapping interval requests coalesce. Thirty-two recent checkpoint reports are retained, including owner wait, RAM capture, disk write duration and byte size.

Save failures leave resident changes pending, preserve the last completed checkpoint and retry on later intervals. Explicit flushes report failures, and graceful shutdown reports a final-flush failure rather than treating it as a successful save. Authentication mutations, including account/password/session changes and ticket consumption, request a durable checkpoint on connection close before returning. Ordinary gameplay commits become immediately visible in RAM and become durable at a completed checkpoint. A crash can rewind all gameplay since that checkpoint; delayed or failed saves can extend the interval beyond 15 seconds. Related ammunition, damage, destruction, wallet, inventory and history rows recover from the same transactional image. No separate durable gameplay journal is introduced.

A held `.owner` file lock rejects a second resident owner for the same saved-world path. The file may remain after shutdown; the OS lock, rather than its presence, establishes ownership. Startup finishes any previous WAL before loading the saved world. Maintenance scripts and external readers see the last saved checkpoint, so editing/restoring the file requires a stopped server. Sources are the official [Microsoft.Data.Sqlite shared-memory database documentation](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/in-memory-databases) and [SQLite backup API](https://www.sqlite.org/backup.html). This persistence change introduces no new client wire contract or engine simulation behavior.

### Same-build ten-Brutix comparison

The saved-fit replay retains the [ten-pilot workload](#ten-brutix-grouped-gun-benchmark-november-5-2026): ten Brutix Navy Issues, six grouped Heavy Electron Blaster IIs and Void M each, normal locking/activation, six active NPCs, two warmups and thirty measured one-second simulation steps. Fifteen measured frames contain ten paid six-gun volleys. Shields are raised only in the copies to sustain the test; other fitted modules remain online without active tanking. Output includes Blue encoding and excludes network backpressure and native rendering. Ten explicit simulated sessions bypass the listener's existing eight-client admission limit.

Run `FullyQualifiedName~TenBrutixesFire` with `xUnit.MaxParallelThreads=1`, `EDEN_PERFORMANCE_SOURCE` pointing to the saved-fit snapshot and `EDEN_PERFORMANCE_OUTPUT` pointing to an ignored report directory. Set `EDEN_PERFORMANCE_LIVE_WORLD=0` for direct file mode and `1` for resident mode. Resident mode uses a timer wrapper around `TestClock`, fires the actual save callback at simulated 15-second deadlines, and checks a reopened checkpoint for all sixty magazines, fifty bank links, ten ship-position rows and matching target health. Every gun consumes sixteen rounds across warmups and samples, stays active, and every warm scope requires zero fitting rebuilds. All source backups open read-only; the running server and live world were untouched.

Both modes were replayed sequentially from the same instrumented working build based on `ea74809`, including its schema-71 combat/standing history. Results from the earlier schema-70 benchmark remain historical; this comparison rebuilds both controls with the current paths. Each mode has thirty frame samples and three hundred individual tick samples. Frame timing covers release/start through last completion; individual timing begins on entry to its worker. Percentiles use nearest rank and medians average the middle two values.

| Warm measurement | Direct file median / p95 | Resident RAM median / p95 |
| --- | ---: | ---: |
| Concurrent individual ticks | 2,661.16 / 5,547.77 ms | 207.85 / 326.27 ms |
| Concurrent ten-session frames | 3,592.32 / 6,171.33 ms | 214.06 / 329.31 ms |
| Concurrent individual firing ticks | 3,452.19 / 6,171.16 ms | 253.88 / 373.71 ms |
| Serial individual ticks | 72.44 / 111.39 ms | 21.96 / 30.38 ms |
| Serial ten-session frames | 749.76 / 1,016.40 ms | 190.99 / 274.26 ms |

Concurrent individual-tick p95 decreased by **94.12%**. Its 250 ms target is still missed by **76.27 ms**, or 123.71 ms when considering firing ticks alone. The slowest resident tick took 384.67 ms, including 132.89 ms in combat, 110.21 ms in tackle handling and 35.95 ms in contacts. Its EF SQL execution total was only 20.46 ms; time waiting to enter the serialized world and other application work remain significant.

EF command/open counts remain approximately **2,568 / 578 per firing frame** across ten pilots, with zero warm fitting calculations. The resident image removes the per-action world-file operations, while the SQL/ORM and catalog work remain. Summed transaction-start time on concurrent firing frames averaged 7,483.01 ms in file mode and 0.77 ms resident; summed commit time fell from 1,749.60 to 0.76 ms. These totals overlap across workers and are not independent frame wall times. Weapon publication still performs sixty health-backed contact refreshes; the next candidates are batching those publications and shortening owner-held work through resident metadata and typed combat state.

Cold setup, including copy/migration/cloning/grouping/locking/first volleys, took 14,081.02 ms concurrent and 10,973.66 ms serial in file mode; resident mode took 12,687.04 and 8,234.34 ms. Resident snapshots were 5,705,728 bytes. Across the eight logged snapshots, RAM capture held the owner for **1.62–2.10 ms** and disk writes took **21.91–31.57 ms** after release. Reports cover the warmup checkpoint, two scheduled checkpoints during the measured frames and the explicit verification flush in each case; provider shutdown can also flush session cleanup. Concurrent encoded output was 2,800,758 bytes resident and serial output was 2,793,929 bytes. These timings describe the Debug replay with diagnostics enabled, not native client acceptance.

**Automated validation.** The same-build file control passed two cases in 2 minutes 51 seconds and resident mode passed two cases in 34 seconds, each with one worker. Filter `FullyQualifiedName~LiveWorldTests|FullyQualifiedName~WeaponBankTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~MissileCombatTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting` passed 97 cases in 30 seconds with two workers. Coverage includes timer deadlines, unchanged/rolled-back worlds, saves concurrent with newer transactions, retry after failure, failed atomic replacement, shutdown/reopen, related-row recovery, authentication durability, grouped payments/rejections, fatal turret and missile volleys, NPC ship loss and simulated fatal-message/capsule ordering. The full .NET suite was not requested or run.

After tightening authentication flushes to actual mutations and extending failure coverage to scheduled retries, filter `FullyQualifiedName~LiveWorldTests|FullyQualifiedName~SessionPerformanceTests|FullyQualifiedName~ResidentFitTests|FullyQualifiedName~ResidentFitFleetTests|FullyQualifiedName~PilotCombatTests|FullyQualifiedName~TypedSessionTests|FullyQualifiedName~AmmunitionTests|FullyQualifiedName~InventoryTests|FullyQualifiedName~MarketTests|FullyQualifiedName~InteractionHistoryPersistenceTests|FullyQualifiedName~CharacterInteractionHistoryTests|FullyQualifiedName~Eden.Protocol.Tests.Launcher` passed 155 cases in 32 seconds with two workers. The final build passed with zero warnings/errors. Tool/package restore, both Python policy suites (3 catalog and 9 formatting cases), formatting and diff checks passed. An intermediate formatting check found one overlong benchmark report string; it was split before the passing build and gameplay checks.

The startup audit caught the development ticket helper attempting to acquire the world's exclusive owner. Preparation now uses its signing-only path without opening a world. A headless CLI regression runs that helper against an owned scratch world and reaches its expected unprepared-client error without acquiring the owner or starting a listener/game. Filter `FullyQualifiedName~LiveWorldTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~Eden.Protocol.Tests.Launcher` passed **39 cases in 8 seconds** after this correction. Other world-opening maintenance commands require a stopped host. The benchmark paths do not invoke ticket preparation or launcher mutation operations.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** After a normal restart, check native grouped combat and observer effects. On a copied world, compare a graceful restart with a forced process exit to observe checkpoint rollback.

## Ten-Brutix grouped-gun benchmark (November 5, 2026)

The opt-in `ServerPerformanceReplayTests.TenBrutixesFireSixGunBanksOnOneGrid` replay clones saved pilot 90000006's Brutix Navy Issue, skills, fit and loaded charges into ten independent accounts and scoped production sessions. Each ship links six Heavy Electron Blaster IIs with Void M through `dogmaIM.LinkAllWeapons`, locks one NPC and activates the bank normally. Other fitted modules remain online without active tanking. The migrated snapshot's target is NPC 4000000000008, type 23267, in system 30003995, which has six active NPCs. Hull and target shield capacities are raised only in the scratch catalog/world to sustain the workload.

Both cases use the full catalog, schema-70 migration, `TestClock`, normal `PilotSessionTick`, normal output construction and Blue packet encoding. The serial case processes all ten sessions in order. The concurrent case releases ten thread-pool workers together, with one serialized owner per session. Each case excludes two warmup frames and measures 30 one-second simulation steps. The clock advances only after all ten sessions finish; this is a synchronized load replay, without real-time arrival pacing or overlapping frame backlogs. Fifteen measured frames contain ten simultaneous six-gun volleys. Every gun remains active, all sixty magazines consume sixteen rounds across warmups and samples, and every warm worker scope requires zero fitting calculations.

To reproduce, provide the saved-fit snapshot directory containing `world.sqlite` and `catalog.sqlite` through `EDEN_PERFORMANCE_SOURCE`, and an ignored report directory through `EDEN_PERFORMANCE_OUTPUT`. Run filter `FullyQualifiedName~TenBrutixesFire` with `xUnit.MaxParallelThreads=1`. The two theory cases write `grouped-fleet-serial.log` and `grouped-fleet-concurrent.log`. The one test worker does not restrict the concurrent case's ten explicit workers. Without a configured snapshot these opt-in cases return without running the workload. Source databases open read-only and are backed up into separate scratch directories; the running server and live world are untouched.

The completed fresh restart on parent `0453410` plus the diagnostic changes produced these results in the default Debug build. Frame latency covers release/start through the last session's completion. Individual tick latency covers its own session processing; worker scheduling before entry is included only in frame latency. Percentiles use nearest rank, and even-count medians average the two middle samples.

| Measurement | Serial median / p95 | Concurrent median / p95 |
| --- | ---: | ---: |
| All ten sessions, 30 warm frames | 688.06 / 838.58 ms | 3,721.33 / 5,740.59 ms |
| Individual ticks, 300 samples | 77.18 / 90.87 ms | 2,682.40 / 5,247.58 ms |
| Firing frames only, 15 samples | 835.64 / 838.66 ms | 5,013.83 / 6,332.78 ms |
| Individual firing ticks, 150 samples | 81.15 / 103.12 ms | 3,719.38 / 5,560.05 ms |

Cold setup, including backup, migration, cloning, grouping, locking and first volleys, took 9,762.17 ms serial and 14,946.15 ms concurrent, separately from the warm samples. Encoded output over the 30 measured frames was 2,793,122 bytes serial and 2,805,790 bytes concurrent. The sink completes immediately after encoding, so these timings omit transport encryption, socket backpressure, client rendering and native gameplay feel. The replay contains no ordinary request-arrival/reply samples. The 250 ms target is missed by **4,997.58 ms** at concurrent individual-tick p95; the serial case meets it for individual ticks while its full ten-session pass approaches the one-second budget.

Firing frames issue approximately **2,548 database commands and 578 connection opens across ten sessions**, with **zero fitting calculations**. Frames between paid volleys still issue 793 commands and 438 opens. Warm fitting reuse works; transaction traffic and shared refresh work remain substantial.

| Phase on firing frames | Serial median summed across ten sessions | Commands / opens across ten sessions |
| --- | ---: | ---: |
| Player combat, inclusive | 289.93 ms | about 1,754 / 140 |
| Paid bank settlement, inside combat | 135.79 ms | about 1,384 / 20 |
| Sixty weapon publications, inside combat | 90.67 ms | 300 / 60 |
| Pre-update contacts | 159.93 ms | 193 / 130 |
| Background monitoring | 132.57 ms | 210 / 110 |
| NPC combat, inclusive | 114.32 ms | about 241 / 86 |
| NPC acquisition, inside NPC combat | 36.84 ms | 130 / 20 |
| Motion | 39.73 ms | 49 / 32 |
| Final output flush | 0.90 ms | 0 / 0 |

These are separate phase medians with inclusive children, so they must not be summed as independent costs. Each bank still invokes per-member preparation and saves the member payments before the final settlement. Repeated weapon publication rebuilds a contact and reads health for each of sixty guns; those publications alone account for 300 commands and sixty opens. Overall, public health reads run seventy times on a firing frame. NPC acquisition still runs once per pilot each second, producing ten scans of the same candidate population. Contact reads recur before updates, after paid volleys and during NPC updates.

Opt-in diagnostics now include `sql`, `begin` and `commit` durations and nested combat, publication, health, contact, acquisition and background phases. SQL duration comes from EF's command-executed event and excludes subsequent reader materialization. Transaction-start duration includes time spent obtaining the transaction. The fresh firing frames average **4.92 ms** starting transactions in serial execution versus **6,351.91 ms accumulated across concurrent workers**. SQL execution similarly rises from 506.60 to 9,667.69 ms and commits from 31.71 to 1,561.51 ms. Concurrent durations overlap and can exceed frame wall time.

The slowest fresh frame took 6,332.78 ms. Its slowest pilot spent 2,945.92 ms in player combat, 1,464.85 ms in pre-update contacts and 1,233.57 ms in NPC combat. Its bank call took 1,417.71 ms, including 1,402.25 ms starting its transaction. `FlightSession.RefreshPeers` holds the shared-space motion lock while building a health-backed contact. `NpcEncounterStore.List` holds the database motion lock across contact reads and a transaction. This combination lets database delays hold up other sessions' refreshes. These measurements strongly implicate database and shared-lock contention; they do not isolate every lock wait individually.

The next candidates are moving database access outside shared motion locks using coherent snapshots, publishing one updated contact per bank, sharing system acquisition/contact work while preserving committed-change ordering, and replacing unchanged background reads with committed-change signals. A database migration remains a separate experiment. SQLite permits one writer at a time even with [WAL](https://www.sqlite.org/wal.html); [MySQL/InnoDB](https://dev.mysql.com/doc/refman/8.4/en/innodb-transaction-model.html) supports row-level locking and nonlocking reads. This can improve independent-ship concurrency, while updates to a shared NPC and Eden's own shared locks can still serialize. No MySQL performance result is established by this replay.

**Automated validation.** Tool and solution restore and the isolated build passed with zero warnings/errors. `FullyQualifiedName~SessionPerformanceTests` passed six cases in three seconds, including actual SQL/start/commit event capture, nested duration attribution and scope cleanup. The fresh benchmark filter passed both cases in 2 minutes 48 seconds with one worker. An earlier development run was interrupted while changing the shared report collector to a concurrent queue; its results are excluded. The full .NET suite was not requested or run.

Filter `FullyQualifiedName~SessionPerformanceTests|FullyQualifiedName~WeaponBankTests|FullyQualifiedName~ResidentFitTests|FullyQualifiedName~ResidentFitFleetTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~PilotCombatTests|FullyQualifiedName~FlightSessionTests|FullyQualifiedName~TypedSessionTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting` passed **110 cases in 34 seconds** with two workers. Both Python policy suites passed (three catalog setup and nine formatting cases). Formatting and diff verification passed.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Check grouped-gun timing and observer effects in the native client after a future server restart. Capture real transport/queue timings under ten connected clients if available.

## Resident ship and turret fits (November 5, 2026)

Calculated ship attributes and paid turret attributes now reside in a shared, bounded per-world cache. Entries are keyed by pilot, hull and attribute role/module identity, carry a committed pilot fitting revision, and contain frozen scalar attribute maps. Every caller receives its own mutable copy. The cache holds at most 8,192 maps and evicts individual entries in insertion order. It retains neither EF entities nor mutable dogma evaluators. Existing graph-building paths used for missile/support setup continue to evaluate their own inputs.

World version **69** introduces `WorldFitRevision`. Transactional SQLite triggers replace a pilot's 128-bit revision stamp when fitted module/charge identity, location, owner, online/assembly state, ship state, skills, active-module membership, incoming tackle, clone/booster state or escrow visibility changes. This conservatively invalidates all owned hull snapshots for that pilot. Ordinary magazine quantity and crystal-wear updates retain their live transaction paths; removal of the final charge invalidates through the item delete trigger. Health, capacitor, position, cycle/reload deadlines and damage remain live operation state. Turret firing still validates ownership, target, ammunition, cooldown, capacitor and damage in its original transaction. Public module inspection computes its live reload information separately.

Readers check the revision inside the same SQLite transaction as the gameplay operation. Contexts with pending tracked changes or without a transaction use fresh calculations. Newly computed entries are private to their transaction and publish after a successful commit. Rollback discards them. A transaction identity guard prevents a disposed/rolled-back transaction publishing through a later transaction on the same context. Old reader publications can cause a later cache miss, while the revision check preserves snapshot correctness. Random stamps prevent a rolled-back version number being reused for a different fit. Reopening the provider starts a cold cache. Mutable-catalog fixtures keep fresh reads by default; dedicated resident-fit cases explicitly enable the cache. Catalog rebuilds require the normal host restart.

The saved-fit benchmark now copies and migrates both direct-fit and session scenarios before constructing readers. Its source files remain read-only. Historical downgrade fixtures remove new cross-table escrow triggers before reconstructing the old schema. Focused resident cases cover pending/committed online changes, rollback, disposed transactions, reopen, immutable copies under concurrent readers, incoming webs/removal, active-effect membership, paid shots, skill changes and revision isolation between pilots. Shared-grid fixtures create separate scoped pilot sessions and drive synchronized volleys for **1, 5 and 10 one-gun Ibis pilots** over six cycles; all warm volleys require zero fitting builds. A mid-fight skill-level change rebuilds only the affected pilot's snapshot and changes the next paid weapon duration.

**Automated validation.** The final isolated build passed with zero warnings/errors. Filter `FullyQualifiedName~ResidentFitTests|FullyQualifiedName~ResidentFitFleetTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~PilotCombatTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting` passed **64 cases in 32 seconds**. The broader equipment filter covering the same resident cases plus hybrid/energy/projectile tiers, tackle, hardeners, typed sessions and catalog reads initially passed 243/244 in 97 seconds; its one new web fixture failure was corrected by supplying the pinned web-resistance input/definition, then the resident cases passed. Earlier migration checks exposed a wrong implant owner-column assumption, which was removed in favor of existing implant-state revision ownership, and new escrow trigger references in historical downgrade fixtures, which now explicitly drop those references. A benchmark helper initially opened its old source save directly and failed on the missing revision table; all helpers now copy and migrate before reading.

The saved-Brutix replay passed **four scenarios in 45 seconds** with one worker. Ordered ship attributes match the prior build's file exactly, and every measured warm combat tick plus its following request asserts **zero fitting calculations**. The final one-worker fleet timing pass ran three cases in 21 seconds; earlier parallel correctness timings were excluded from the following fleet totals. Tool/package restore, Python catalog/format policy suites (3 and 9 cases), formatting verification (640 files) and diff checks passed. The full .NET suite was not requested or run.

```text
docked ticks median=22.48ms p95=33.66ms
docked replies median=4.12ms p95=6.42ms
space ticks median=97.63ms p95=127.19ms
space replies median=21.64ms p95=33.70ms
combat ticks median=192.79ms p95=292.47ms
combat replies median=40.70ms p95=66.75ms
1 pilots, serialized shared-grid tick totals ms: 160.2, 210.0, 181.2, 145.3, 123.7, 120.4
5 pilots, serialized shared-grid tick totals ms: 611.1, 589.6, 679.7, 649.5, 594.6, 598.1
10 pilots, serialized shared-grid tick totals ms: 983.4, 961.0, 885.6, 856.5, 853.7, 892.9
```

The final solo Brutix combat tick measured **192.79 ms median / 292.47 ms p95**, versus **277.40 / 509.87 ms** in the preceding committed replay. Reply p95 measured **66.75 ms** versus 112.74 ms previously. The 250 ms tick target remains missed by 42.47 ms in this sample; an earlier development replay measured 240.02 ms p95, demonstrating timing variability. The fleet totals are serial processing time for all separate sessions on a shared grid, with one gun per ship and six measured volleys. They cover simultaneous scheduling, shared NPC state, committed skill-change refresh and cache isolation. They do not establish ten-Brutix or hundred-pilot capacity. Remaining per-session persistence/monitoring work is visible in the aggregate totals even when fitting builds are zero.

**Live acceptance.** Not performed; agent client testing is disabled. Validation uses copied full catalogs/worlds and fixture worlds, simulated clocks, immediate output sinks and controlled durability. The running server and live save were left running throughout validation.

**Optional maintainer checks.** Rebuild/restart and repeat the native six-blaster fight with performance tracing enabled. Verify module on/off or ammunition-type changes update the displayed fit and paid cycles.

## Repeated turret cycle work (November 5, 2026)

The maintainer reported improved native combat after `26254ce` and requested another optimization pass. A paid turret shot still built two fitting graphs in its owning transaction, once for ship state and again for module attributes. It now passes the same calculation to both readers. Combat skill levels are also loaded in one transaction-local batch. A new diagnostic `fittings` counter records graph builds alongside the existing timing/database counters.

Automatic repeats previously refreshed NPCs and wrecks after every paid gun. The controller now refreshes those contacts after the due shots have completed. Destruction refreshes immediately, before another gun can spend resources against the same target. Per-shot health, ammunition, capacitor and effects keep their existing notification paths. Initial/manual activation still refreshes immediately. Every shot keeps its own atomic payment/damage transaction. Repeated effects now emit the existing stop/start commands in order and publish one replacement ship contact, avoiding the second fitting/health read that previously accompanied the intermediate stopped contact. Observer state contains the new paid cycle, and the single publication still obtains fresh health.

Focused regressions assert one graph for a real paid shot, one contact read for two simultaneous repeats, and immediate removal/stop without advancing the second gun's paid deadline when the first gun kills their target. They also assert damage-state output precedes removal. The death fixture is prepared at the firing boundary to exclude recharge from its one-structure-HP setup.

**Automated validation.** Final isolated build passed with zero warnings/errors. Filter `FullyQualifiedName~PilotCombatTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~HybridTierTests|FullyQualifiedName~EnergyTierTests|FullyQualifiedName~ProjectileTierTests|FullyQualifiedName~MissileCombatTests|FullyQualifiedName~CatalogFittingReadTests|FullyQualifiedName~SessionPerformanceTests|FullyQualifiedName~TypedSessionTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting` passed 200 cases in 55 seconds with two workers. The separate single-worker saved-Brutix replay passed four cases in 38 seconds. Restore, Python catalog/format suites (3 and 9 cases), formatting across 636 files and diff checks passed. The replay's direct fitting attribute file remains identical to the previous milestone. No full .NET suite was requested or run.

```text
docked ticks median=19.43ms p95=30.81ms
docked replies median=5.84ms p95=13.19ms
space ticks median=65.71ms p95=79.23ms
space replies median=11.81ms p95=19.20ms
combat ticks median=277.40ms p95=509.87ms
combat replies median=45.77ms p95=112.74ms
```

Against the same saved-fit workload recorded for `26254ce`, combat tick median decreased from 351.56 to 277.40 ms. Tick p95 was 509.87 ms versus 516.17 ms previously, so the tail shows little improvement and still exceeds the 250 ms target by 259.87 ms. Reply p95 was 112.74 ms versus 56.39 ms, within the target but slower in this sample. Run-to-run timing variation remains material. An intermediate version with only graph reuse and contact-read batching measured 347.52 ms median / 519.14 ms p95; examining effect publication led to the final single-contact change. Deterministic tests establish the avoided work independently of timing variation: one graph per paid shot, one NPC refresh for simultaneous nonfatal repeats, and one fresh health read per repeated effect with the same ordered stop/start commands and final observer state. These remain isolated server timings with the previously documented immediate output, simulated clock and durability adjustments.

**Live acceptance.** Not performed; agent client testing is disabled. The maintainer's reported improvement applies to the preceding build.

**Optional maintainer checks.** Rebuild/restart and repeat the same fight with `EDEN_TRACE_PERFORMANCE=1`; inspect slow ticks with their new fitting counts.

## Fitted combat catalog reads (November 5, 2026)

The maintainer supplied a screenshot of a **3,057 ms** pilot update during combat. The running default Debug server had been rebuilt after the prior milestones. A read-only save inspection found pilot **90000006**, account **1000003**, flying a **Brutix Navy Issue (33151)** with 17 online modules, six loaded Heavy Electron Blaster IIs and 90 trained skills. The previous solo replay used a lightly fitted Ibis and did not represent this fitting workload. The screenshot gives a total elapsed time; its individual phase costs were not captured.

Fitting support/modifier checks repeatedly read the same source records and base type attributes. Read-only catalog contexts now reuse those inputs throughout one serialized request or tick through `IPilotOperationScope`. Source records, including missing entries, are keyed by dataset/key, hull/module identities and attributes by type, and catalogs by normalized absolute path. Frozen base attributes are copied before signature-default or gameplay modifications. The scope expires after that operation. Writable catalogs bypass these caches. Inventory, training, paid cycles, active effects, capacitor, damage and all other world inputs still use their owning transactions. Skill rows are loaded in a batch for each fitting calculation and its owning transaction, including tracked added/modified rows. Skill levels are reused within that calculation. There are no shared cached fitting results or wire-format changes.

`EDEN_PERFORMANCE_PILOT` now chooses the saved pilot for the opt-in replay, deriving its account from the snapshot. The replay supplies a civilian gun in a free high slot and also activates all six saved type-3130 blasters when present. Its clock is November 6, after the snapshot's saved cooldowns; the earlier replay using November 5 midnight could not activate a copied live weapon and was excluded. The combat copy still raises shield durability to preserve sustained samples. Repeated weapon activity and original ship identity are asserted. `MeasureSavedFitting` measures ten direct fitting reads and records the resulting ordered attribute values for exact before/after comparison.

**Automated validation.** The broad focused filter covering catalog reads, fitting, passives, support, hybrid weapons, hardeners, tackle, missiles, shield boosters, propulsion, NPC combat, typed sessions and hosting passed 242 cases in 60 seconds. After the final skill batching/eligibility and pending-entity corrections, `FullyQualifiedName~CatalogFittingReadTests|FullyQualifiedName~PassiveFittingTests|FullyQualifiedName~PilotFittingTests|FullyQualifiedName~HybridTierTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting` passed 49 cases in 18 seconds. Four opt-in replay cases passed in 38 seconds on the final code, including a direct fitting check and the three saved-pilot session scenarios. Tool/solution restore, isolated build (zero warnings/errors), Python catalog/format suites (3 and 9 cases), formatting across 636 files and diff checks passed. No full .NET regression suite was requested or run.

Direct fitting reads for the saved Brutix decreased from 475 to 163 database commands. Before/after ordered attribute files match exactly. The unchanged-parent replay uses `b744a40` with the same expanded replay copied into an isolated worktree, the same full-catalog snapshot, six saved blasters, durability settings and simulated clock. The final replay additionally asserts every saved blaster remains active after all 20 samples. Results follow.

```text
fitted-parent
docked ticks median=16.80ms p95=19.48ms
docked replies median=6.14ms p95=8.46ms
space ticks median=92.22ms p95=111.59ms
space replies median=13.40ms p95=15.66ms
combat ticks median=1423.07ms p95=1953.21ms
combat replies median=107.14ms p95=127.87ms
fitted-verified
docked ticks median=15.44ms p95=17.12ms
docked replies median=5.13ms p95=5.90ms
space ticks median=58.97ms p95=74.38ms
space replies median=10.66ms p95=11.42ms
combat ticks median=351.56ms p95=516.17ms
combat replies median=40.50ms p95=56.39ms
```

Final combat tick median decreased from 1,423.07 to 351.56 ms, and p95 from 1,953.21 to 516.17 ms. Reply p95 decreased from 127.87 to 56.39 ms. The 250 ms tick target remains missed by 266.17 ms. Update processing remains the largest slow phase. An intermediate candidate measured 882.61 ms tick p95, indicating substantial run-to-run timing variability. These are isolated server-side measurements with immediate output, controlled shield durability and a simulated clock; they do not establish native end-to-end responsiveness or the exact cause of the maintainer's individual 3,057 ms observation. The default Debug build's generated informational version identified `b744a40`; development validation used separate artifact outputs.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Rebuild/restart the default server and repeat six-blaster combat with `EDEN_TRACE_PERFORMANCE=1` to capture real update phases and queue waits.

## Shared session performance diagnostics (November 5, 2026)

Set `EDEN_TRACE_PERFORMANCE=1` before server startup for call/tick phase timings and EF command/connection-open counts. Reports cover dispatch, updates, contacts, motion and output. Queue time covers decoded packet delivery to session processing, including intervening ticks. Nested counts roll into their enclosing operation. Reports omit SQL values and packet payloads. Timing uses a monotonic clock and scopes follow asynchronous execution.

`ServerPerformanceReplayTests` is an opt-in isolated replay. Set `EDEN_PERFORMANCE_SOURCE` to a snapshot directory containing `world.sqlite` and the full `catalog.sqlite`, and `EDEN_PERFORMANCE_OUTPUT` to an ignored output directory. Run filter `FullyQualifiedName~ServerPerformanceReplayTests` with `xUnit.MaxParallelThreads=1`. Each scenario uses SQLite backup into separate scratch files, production scoped handlers, `PilotSessionCalls` and `PilotSessionTick`, a simulated clock and a completed output sink. It exercises docked idle, populated-space idle and locking/repeating civilian railgun combat. It uses saved pilot 90000001 and the first active pirate, supplying one online civilian gun in the copy. Combat raises pilot/target shield capacity and charge only in the copies to sustain 20 samples, asserting the original ship and repeating weapon survive. Cold setup is separate from warm median/p95. Snapshots are read-only. Without a configured source the opt-in replay returns without measuring a workload.

Replay timings exclude socket transport, native client processing and packet contention. Queue timing is instrumented in the normal session loop. An exploratory replay let the pilot die after three samples and is excluded from sustained-combat comparisons.

## Startup protocol and milestones

**Milestone 1 automated validation.** Isolated build passed with zero warnings/errors. Filter `FullyQualifiedName~SessionPerformanceTests|FullyQualifiedName~ServerPerformanceReplayTests|FullyQualifiedName~NpcCombatTests` passed 23 cases in 59 seconds. Tool/package restore, both Python policy suites (3 and 9 cases), formatting and diff checks passed. Sustained-combat baseline results follow.

```text
Performance docked cold: 36.71 ms, commands=56, connections=30, tackleReads=1, aggressionReads=1.
docked ticks median=13.16ms p95=18.14ms
docked replies median=2.56ms p95=4.06ms
Performance space cold: 4886.32 ms, commands=2922, connections=2588, tackleReads=26, aggressionReads=7.
space ticks median=121.24ms p95=132.63ms
space replies median=38.46ms p95=47.47ms
Performance combat cold: 3880.04 ms, commands=2946, connections=2600, tackleReads=29, aggressionReads=10.
combat ticks median=179.41ms p95=218.59ms
combat replies median=58.81ms p95=61.68ms
```

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Compare native combat responsiveness after the final milestone.

### Shared session scheduling

The session now retains a monotonic next-tick deadline across calls and tick processing. Slow ticks skip expired wakeups instead of adding processing time to every following one-second interval. Calls and ticks retain one serialized owner. Due targeting, module payments, damage and ship-loss consequences still run before replies. Existing background monitors retain their revision/deadline guards.

NPC acquisition runs at most once per second for an unchanged system/candidate set in a pilot session. Enter/leave and changed candidate membership invalidate that deadline. Paid shots and impacts still advance on calls, and changed combat state refreshes contacts before output. Deterministic coverage checks 20 repeated updates, the next deadline, candidate changes, reconnect and timer drift/overdue wakeups.

Three targeting cases failed identically on isolated parent `4712b8a` and the milestone: `DeclinedCallsCannotCheckpointOrCompleteALock`, `ChecksRangeSelfDockingAndPendingSlotsAndAllowsRetryAfterCancellation`, and the dock case of `DepartureOrRangeLossClearsBothSides`. They are recorded baseline failures. The initial expanded run had 45 passes and those three failures. A concurrent development rebuild encountered a locked test assembly; final validation runs builds and tests sequentially.

**Milestone 2 automated validation.** Final isolated build passed with zero warnings/errors. Filter `FullyQualifiedName~SessionPerformanceTests|FullyQualifiedName~ServerPerformanceReplayTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~NpcAggressionTests|FullyQualifiedName~TypedSessionTests` passed 38 cases in 51 seconds. Restore, both Python policy suites, formatting and diff checks passed. Timing variability remained substantial. Combat tick p95 was 279 ms, 29 ms above the target, with database-heavy updates still the largest phase.

```text
docked ticks median=11.63ms p95=20.57ms
docked replies median=2.47ms p95=4.82ms
space ticks median=93.97ms p95=101.11ms
space replies median=9.75ms p95=14.55ms
combat ticks median=192.85ms p95=278.98ms
combat replies median=58.03ms p95=72.11ms
```

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Compare native combat responsiveness after the final milestone.

### Batched NPC motion and catalog inputs

Motion now loads NPC tackle effects and aggression in one query each, builds effective attributes once per NPC, and reuses those inputs throughout the atomic motion step. Contact building also batches tackle effects and indexes identities instead of repeatedly searching the contact list. Production motion, acquisition, firing and contacts share a singleton NPC hull/profile catalog with frozen attribute maps. Mutable fixture catalogs explicitly use fresh reads. Unlisted/legacy types are read through the same catalog loader. Runtime catalog changes take effect after rebuilding the service provider/restarting the host.

Catalog connections stay open for their bounded motion/contact/combat operations and initial spawn-profile loading, closing on disposal. Contact refreshes after mutable combat phases remain fresh to preserve same-tick damage, kills, navigation and observer ordering. All motion writes still commit together. One-NPC and 24-NPC regressions each assert exactly one tackle read, one aggression read and two connection opens for a motion step.

The existing one-second Destiny step was checked against locally saved Carbon Destiny `Ballpark.cpp` revision `27ee40b38a563d29054b32f64c3ebc87fabaec1f`, including its 1000 ms interval and deadline-adjusted scheduling. The refactor retains the existing simulation and wire adapters.

**Milestone 3 automated validation.** Final isolated build passed with zero warnings/errors. Filter `FullyQualifiedName~SessionPerformanceTests|FullyQualifiedName~ServerPerformanceReplayTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~NpcAggressionTests|FullyQualifiedName~NpcPopulationTests|FullyQualifiedName~NpcOrbitTests|FullyQualifiedName~NpcPursuitTests|FullyQualifiedName~NpcMissileTests|FullyQualifiedName~MissileCombatTests|FullyQualifiedName~MissileFlightTests|FullyQualifiedName~ShipCollisionTests|FullyQualifiedName~MotionSynchronizationTests|FullyQualifiedName~TackleTests|FullyQualifiedName~TypedSessionTests` passed 191 cases in 61 seconds with two workers. Additional filter `FullyQualifiedName~RegionalNpcTests|FullyQualifiedName~NpcGuardTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting` passed 53 cases in 15 seconds. A separate single-worker full-catalog replay passed three scenarios in 27 seconds. Both Python suites (3 and 9 cases), tool/solution restore, formatting and diff checks passed. The full .NET suite was not requested or run. The known parent targeting failures above remain outside these passing filters.

```text
Performance docked cold: 32.12 ms, commands=56, connections=30, tackleReads=1, aggressionReads=1.
docked ticks median=10.78ms p95=12.52ms
docked replies median=2.34ms p95=2.58ms
Performance space cold: 209.21 ms, commands=2905, connections=73, tackleReads=11, aggressionReads=7.
space ticks median=53.83ms p95=57.26ms
space replies median=9.42ms p95=10.27ms
Performance combat cold: 854.18 ms, commands=2923, connections=79, tackleReads=14, aggressionReads=10.
combat ticks median=179.45ms p95=308.43ms
combat replies median=61.01ms p95=68.25ms
```

Combat motion median decreased from 22.57 to 8.49 ms, with median motion commands decreasing from 39 to 13 and connection opens from 14 to 5. Across a combat tick plus one following request, median command counts decreased from 1,341 to 1,266.5 and opens from 126 to 88. Cold setup connection counts also fell substantially. These figures include the controlled durability changes described above. Timing variability is material: final combat tick median was 179.45 ms versus baseline 179.41 ms, and p95 was 308.43 ms versus baseline 218.59 ms. The 250 ms target is therefore missed by 58.43 ms on the final replay. Update processing remains the largest slow phase. Reply p95 was 68.25 ms, under the target. The database and motion improvements do not establish an end-to-end latency improvement. Packet queue contention and native responsiveness remain unmeasured by this replay.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Compare native targeting/firing responsiveness with the opt-in performance diagnostics enabled after restarting onto the new build.

## Agent directory first-request latency (November 6, 2026)

The reported `agentMgr.GetAgents` warning spent **4,486 ms in dispatch** and **4 ms in updates**. The handler's first `INpcDirectory.Agents()` call initialized the singleton snapshot, including all NPC names and profiles. A read-only replay of the installed build-3662168 catalog reproduced cold loads of **4,382.78, 3,686.68 and 4,306.43 ms** on parent `ca7a480`.

The dominant operation was the localized-name query. EF Core 10's default collection translation expanded the **11,393 name keys** into individual SQLite parameters. Temporary phase instrumentation measured **4,268–5,219 ms** in the name-query phase in a separate run. `EF.Parameter(nameKeys).Contains(record.Key)` now selects the single JSON collection parameter and SQLite's existing `(Dataset, Key)` index. This is a query-local choice; see Microsoft's [EF Core 10 collection-translation change](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/breaking-changes#parameterized-collections-now-use-multiple-parameters-by-default). A regression captures actual database commands for empty and 1,200-name inputs, verifies one parameter, checks every resolved name and avoids parsing an unrelated malformed localization record. Restoring the original query made the 1,200-name regression fail with **1,200 parameters**.

The live host also initializes the directory in `ServerServices.Build(liveWorld: true)` before admitting clients. The remaining catalog materialization therefore belongs to startup. The host-build regression verifies that the first subsequent directory/profile requests perform **zero database commands and connection opens**. Fixture/non-live providers retain lazy loading and the existing restart-bound snapshot semantics.

Final read-only replay results, using a fresh directory for each sample with EF's model initialized as in a running host:

| Sample | Parent cold load | Fixed cold load, now performed at startup | Fixed cached directory/profile lookup |
| --- | ---: | ---: | ---: |
| 0 | 4,382.78 ms | 816.44 ms | 0.80 ms |
| 1 | 3,686.68 ms | 470.73 ms | 0.01 ms |
| 2 | 4,306.43 ms | 375.80 ms | <0.01 ms |

Every replay returned **10,966 unique agents** and an identical **391,159-byte** encoded rowset, SHA-256 `76D41E30A675C293F4C823C8BC846A51A252E07FCBF928D40152A3A53843BF46`. Timings measure directory/profile access, excluding socket output and native client work; they do not establish an end-to-end RPC latency. Startup still pays the reduced cold-load cost, which exceeds the 250 ms slow-call threshold in this replay. The live host performs this work before client dispatch.

To repeat the catalog-only replay, set `EDEN_AGENT_PERFORMANCE_SOURCE` to a directory containing the pinned `catalog.sqlite`, then run filter `FullyQualifiedName=Eden.Protocol.Tests.Persistence.AgentDirectoryPerformanceTests.ReadOnlyFullCatalogColdAndCachedDirectory` with one xUnit worker. It opens only the catalog in read-only mode; the `world.sqlite` path supplies the sibling catalog location and is never opened. Without that variable the opt-in test returns without measuring a workload. The expected rowset digest is pinned to the installed build-3662168 catalog.

The established client contract is recorded in [Catalog agent directory and saved agents](characters.md#catalog-agent-directory-and-saved-agents): the agent rowset columns and ordering remain identical. Consulted [Carbon Blue Marshal.cpp at fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp); the existing wire adapters are reused.

**Automated validation.** Exact development filters selected `ColdDirectoryBindsNameCollectionsOnceRegardlessOfNpcCount` (2 passing cases) and `LiveHostBuildWarmsDirectoryBeforePilotRequests` (1 passing case) through `tools/Test-Feature.ps1`. Final tool/solution restores, solution build with zero warnings/errors, focused tests, Python catalog policy **3/3**, Python formatting policy **9/9**, formatting/140-column verification and diff checks passed in **24.05 seconds**. The final .NET filter was `(FullyQualifiedName~AgentDirectory|FullyQualifiedName~PilotContactsTests|FullyQualifiedName~CharacterSheet|FullyQualifiedName~OnlineStatus|FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~SessionPerformanceTests)&FullyQualifiedName!~NativeRemapUpdatesAttributesDogmaTrainingDeadlineAndClientCaches`: **82 passed in approximately 5 seconds**, four workers. This includes the opt-in replay's unconfigured return; its separate configured, single-worker run passed in **4.67 seconds** and produced the measurements above. The initial expanded run had **82 passes and one failure** in `CharacterSheetServiceTests.NativeRemapUpdatesAttributesDogmaTrainingDeadlineAndClientCaches` at its dogma dictionary assertion. That exact failure was reproduced with both production files restored to parent `ca7a480`; it is excluded as a pre-existing unrelated failure. No full .NET suite was requested or run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Rebuild/restart the server and confirm the first agent-directory request no longer incurs catalog initialization during dispatch.
