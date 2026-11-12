# Session performance and resident state

[Documentation](../README.md) / [Protocol index](../protocol.md)

Diagnostics, replay benchmarks, resident caches, combat objects and checkpoints.

These records preserve milestone evidence and historical observations. Dates, schema versions, test results and live acceptance apply to the recorded change; they do not establish acceptance of later changes.

## Arrival movement and fitting costs, November 11, 2026

The supplied 03:22:56–03:23:20 sample contains 48 budget warnings: 32 Arrival units (38.90 ms mean,
118.28 ms maximum), 14 Trade units (30.37 ms mean, 75.50 ms maximum) and two Lock units. Arrival units
average 84.88 SQL commands; Trade includes one 499-command unit. These are warning-only samples, not the
distribution of all actions. They cannot establish overall frame latency or queue capacity.

An automated remote hauler reproduction identified repeated pilot/crimewatch queries for every analytic
movement boundary, fitting invalidation caused by station changes, and a redundant first-departure fit:

- Existing positions moving within the same explicit system now update coordinates directly. Undocking,
  legacy positions without a system, inter-system transfer and removal retain membership, task-tracking,
  heat and CONCORD handling. Position writes remain in the owning transaction, visible to subsequent
  docking/jump checks and rolled back with a rejected settlement.
- Schema 141 replaces the two WorldShip UPDATE fitting triggers. Changes to hull identity, type or owner
  still invalidate the appropriate owners; station, storage and name changes preserve the fitted graph.
  Actual module, skill and active-effect invalidation is unchanged. Station geometry is still read by the
  pilot snapshot, separately from the reused fitting attributes.
- Founder supply provisioning reads the unchanged fit before adding its permanent supplies marker. Pending
  tracked writes intentionally disable fitting-cache reuse; the marker previously forced an unnecessary
  recalculation. The marker and inventory still commit atomically, preserving finite one-time supplies.

The same deterministic remote fixture compared the baseline at `da5a3a17` with this implementation:

| Selected complete scheduler turn | Baseline SQL / connections / fits | Updated SQL / connections / fits |
| --- | --- | --- |
| Cold first Trade | 79 / 3 / 2 | 70 / 3 / 1 |
| Warm movement Arrival (pass 3) | 21 / 1 / 0 | 11 / 1 / 0 |
| First station docking Arrival | 49 / 1 / 1 | 30 / 1 / 0 |
| Second station docking Arrival | 43 / 1 / 1 | 28 / 1 / 0 |

Baseline and updated trace reports are retained locally at
`artifacts/feature-tests/results/7a42621181a84b028e56cb02e87d05c3/tests.trx` and
`artifacts/feature-tests/results/1bef9305eaf4438e9836991268bc2ebc/tests.trx`.
The final regression asserts work counts and successful repeated arrivals/dockings. Separate tests cover
same-transaction coordinate visibility, commit/rollback and reopened positions, unchanged crimewatch state,
real hull/ownership invalidation, rolled-back fitting changes, and upgrading/reopening version-140 saves.

These reductions do not guarantee that atomic work completes within 5 ms. Cold catalog/profile loading,
resupply and multi-boundary transactions can still overrun. Population, activity and budget settings are
unchanged. Installed-world warning frequency, complete-session frame latency, sustained queue freshness
and checkpoint costs remain unmeasured. No live server was restarted or save modified for this investigation.

The existing matched maintenance/session fixture also measured 200 maintenance windows and ten complete
session ticks per observer, after warmup, with checkpoint capture/projection/write measured separately.
The baseline and updated deadline-driven runs retained identical jobs, pilot positions and cargo:

| Fixture | Baseline / updated combined maintenance and session time, ms | Baseline / updated SQL / connections / fits |
| --- | --- | --- |
| Empty observer system, one observer | 6.14 / 5.80 | 0 / 0 / 0 versus 0 / 0 / 0 |
| Populated system, one observer | 23.42 / 21.47 | 23 / 10 / 0 versus 22 / 10 / 0 |
| Populated system, two observers | 25.59 / 26.50 | 22 / 10 / 0 versus 22 / 10 / 0 |

| Fixture | Baseline checkpoint capture / projection / write, ms | Updated checkpoint capture / projection / write, ms |
| --- | --- | --- |
| Empty observer system, one observer | 0.117 / 0.335 / 39.939 | 0.116 / 0.322 / 40.366 |
| Populated system, one observer | 0.081 / 0.524 / 39.078 | 0.304 / 0.605 / 39.800 |
| Populated system, two observers | 0.279 / 0.495 / 41.888 | 0.267 / 0.446 / 39.890 |

These single-process fixture measurements show mixed timing and do not establish a frame or checkpoint
speedup. They do not recreate the installed population or sustained remote backlog. Full reports are in
`artifacts/budget-frame-costs/baseline/` and `artifacts/budget-frame-costs/updated/`.
The baseline frame fixture originally passed two cases and failed one comparison because independent
catalog warming added one SourceRecord read (report `d18deef1f4d14abebc0434e8b9c93827`). Jobs, positions and
cargo matched. Its assertion now compares saved-world SQL while retaining all SQL in telemetry. The updated
three cases passed in 14.12 seconds (build 4.79 seconds), report `daae55ee8a4e4eaca742484b4697a8ff`.
An intermediate invocation reused baseline binaries before rebuilding and is excluded from these comparisons.

References: [EVE University server ticks](https://wiki.eveuniversity.org/Server_tick),
[Carbon Destiny Ballpark.cpp at `114e89fa584f0e1a81eb0a679aabf41af8870072`](https://github.com/carbonengine/destiny/blob/114e89fa584f0e1a81eb0a679aabf41af8870072/src/Ballpark.cpp),
and the local evejs `server/src/space/destiny/simulation/README.md` were consulted for simulation/gameplay
boundaries. Movement laws and wire formats are unchanged; no reference implementation was copied.

**Automated validation:** the five exact regression cases passed in 7.74 seconds including runner startup
(feature build 4.19 seconds). An initial broader run passed 206/207 in 90.86 seconds; its cold Trade assertion
allowed 72 commands but saw 73 with shared catalog warming. The corrected bound allows 76 while retaining
the one-fit assertion and strict warm movement/docking limits. Its exact rerun passed in 9.47 seconds.
The final affected run passed **261/261** in **111.04 seconds**, including runner startup
(feature build 2.59 seconds), with the filter:

```text
FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerCoarseTests|FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerHaulerTests|FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerMinerTests|FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerTierTests|FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerHandoffTests|FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerInboundRuntimeTests|FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerSupplyTests|FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerDeadlinePerformanceTests|FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerTrafficNavigationTests|FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerGatePerformanceTests|FullyQualifiedName~Eden.Protocol.Tests.Combat.ResidentFitTests|FullyQualifiedName~Eden.Protocol.Tests.Combat.FittingSnapshotTests|FullyQualifiedName~Eden.Protocol.Tests.Combat.CrimewatchTests|FullyQualifiedName~Eden.Protocol.Tests.Combat.ConcordTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.WorldMigrationTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.TaskTrackingTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.DetachedCheckpointTests|FullyQualifiedName~Eden.Protocol.Tests.Docking.MovementPersistenceTests|FullyQualifiedName~Eden.Protocol.Tests.Docking.DockedServicesTests|FullyQualifiedName~Eden.Protocol.Tests.Flight.GateActivityProtocolTests|FullyQualifiedName~Eden.Protocol.Tests.Flight.GateTravelRulesTests|FullyQualifiedName~Eden.Protocol.Tests.Flight.AutoNavigationTests
```

Its report is `artifacts/feature-tests/results/f5cc16f2d10f4f07a37c826dd1368f1e/tests.trx`.
The final broad run measured 73 cold Trade commands; warm movement and docking counts matched the table.
Tool restore, solution restore and solution build passed (zero warnings/errors; final solution build 1.02 seconds).
Both Python policy suites passed: eight catalog tests in 0.996 seconds and nine formatting tests in 0.285 seconds.
Formatting verification, local documentation links/anchor checks and Git whitespace checks passed.
The full .NET suite was not run.

Integration with main at `2df70f8f` retained the Neocom startup correction and resolved only a documentation-index
conflict. The integrated solution built with zero warnings/errors in 7.64 seconds. Six targeted regression cases
passed in 9.23 seconds (feature build 1.58 seconds), using:

```text
FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerCoarseTests.RemoteArrivalsReuseFitsAndAvoidRepeatedMovementMembershipReads|FullyQualifiedName=Eden.Protocol.Tests.Combat.ResidentFitTests.ShipTravelAndNamesReuseFitsButHullAndOwnershipChangesInvalidateBothOwners|FullyQualifiedName=Eden.Protocol.Tests.Persistence.WorldMigrationTests.Version140ShipFitTriggersUpgradeAndRemainEffectiveAfterReopen|FullyQualifiedName=Eden.Protocol.Tests.Docking.MovementPersistenceTests.SameSystemCoordinatesAvoidMembershipReadsAndRespectTransactionOutcome|FullyQualifiedName=Eden.Protocol.Tests.PilotSessions.FreePointClientTests.FreePointStartupResolvesTheBuiltinManagerWithoutClientFiles
```

The integrated report is `artifacts/feature-tests/results/4b4ea0634cd54350a07934f2452c5818/tests.trx`.
Both Python policy suites passed again: eight catalog tests in 1.148 seconds and nine formatting tests in 0.085 seconds.
Integrated formatting verification and Git whitespace checks passed; the full .NET suite was not rerun.
The branch's test reports and frame comparisons were preserved in the primary checkout under
`artifacts/budget-overruns-validation-b7d3107a/results/` and
`artifacts/budget-overruns-validation-b7d3107a/budget-frame-costs/` before removing the completed worktree.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer check:** after deploying the rebuilt server, compare enriched overrun warnings under
the same saved population and activity settings.

## Remote action budget costs, November 11, 2026

The supplied 03:01:33–03:01:54 warning sample contains remote Trade, Arrival, GateTransfer and Lock actions.
The largest Lock units issued 181–185 SQL commands and calculated three fits; some travel units opened 28–30
connections. Many other units started with less than 1 ms of the shared 5 ms allowance remaining.

Remote haulers now load only immutable station/gate/celestial targets while detailed simulation is released.
Their coarse navigation already excludes collision obstacles; it no longer loads or populates asteroid belts
just to select a station or gate. Mining still loads authoritative resources, and detailed/pinned haulers retain
their normal geometry. Cold remote roles select this mode before initial loading.

Mining validation carries the ship attributes it already read into hold-capacity and capacitor calculations.
Remote paid collection and its optional aggregate of additional paid cycles reuse one validation within the
same transaction. Cargo, capacitor, cooldowns and finite ore remain authoritative mutable reads; crystal changes
trigger another validation. Mining skill levels load in one batch instead of individual absent-skill lookups.
Every new command validates again; the snapshot cannot escape its transaction.

Admission reserves at least 1 ms and, for measured actions, the previous cost capped at the full 5 ms allowance.
An unmeasured action reserves a full allowance. A unit that cannot fit the remainder waits for replenishment. Larger atomic actions remain eligible
with a full allowance and repay their debt over subsequent windows. The budget, population and activity settings
are unchanged, and actual overruns still log warnings.

Matched deterministic fixtures compared the original implementation at `1ecacb81` with these changes:

| Measured work | Original SQL / connections / fits | Updated SQL / connections / fits |
| --- | --- | --- |
| Warm paid mining plus additional coarse cycles, inside an already-open transaction | 150 / 0 / 3 | 59 / 0 / 0 |
| Cold remote hauler turn, using the production catalog-operation scope | 83 / 4 / 2 | 79 / 3 / 2 |

The hauler fixture already has populated belts; its regression additionally rejects any asteroid or renewal-table
read. It does not reproduce every cold installed-world system. Mining SQL fell by 61% in this fixture. The original
implementation fails both new cost/resource-loading assertions. Those deliberate baseline failures are recorded in
`artifacts/feature-tests/results/7b16b78d8412468393676c6f126c8930/tests.trx`; the initial passing comparison is in
`artifacts/feature-tests/results/798de33b12524672a08f97a4f6e8a5d1/tests.trx`.
These are work-count measurements, not complete-session latency or checkpoint benchmarks. Installed-world warning
frequency, frame latency and sustained queue freshness remain unmeasured; indivisible transactions can still exceed 5 ms.

References: [EVE University server ticks](https://wiki.eveuniversity.org/Server_tick),
[Carbon Destiny Ballpark.cpp at `114e89fa584f0e1a81eb0a679aabf41af8870072`](https://github.com/carbonengine/destiny/blob/114e89fa584f0e1a81eb0a679aabf41af8870072/src/Ballpark.cpp),
and the local evejs `server/src/space/destiny/simulation/README.md` (Carbon v3.1.1 reference) were consulted for
simulation ownership and movement boundaries. Eden's coarse scheduling remains its own policy. No movement laws,
wire formats or reference implementation were copied or changed.

**Automated validation:** tool restore, solution restore and solution build passed with zero warnings/errors
(final solution build 12.02 seconds). The broader affected run passed **261/261** in **173.02 seconds** including
runner startup (feature build 8.66 seconds), covering coarse/tier scheduling, handoff, haulers/miners, inbound and
route travel, supplies, ordinary/crystal/ice/gas mining, rewards and typed rules. Its filter was:

```text
FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerCoarseTests|FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerTierTests|FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerHandoffTests|FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerHaulerTests|FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerMinerTests|FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerInboundRuntimeTests|FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerTrafficNavigationTests|FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerSupplyTests|FullyQualifiedName~Eden.Protocol.Tests.Mining.MiningTests|FullyQualifiedName~Eden.Protocol.Tests.Mining.MiningCrystalTests|FullyQualifiedName~Eden.Protocol.Tests.Mining.IceMiningTests|FullyQualifiedName~Eden.Protocol.Tests.Mining.GasMiningTests|FullyQualifiedName~Eden.Protocol.Tests.Mining.MiningSkillPointRewardTests|FullyQualifiedName~Eden.Protocol.Tests.Mining.MiningTierTests|FullyQualifiedName~Eden.Protocol.Tests.Mining.MiningRulesTests
```

After reserving the full allowance for unmeasured actions and restoring full geometry immediately in observed
systems, the final affected rerun passed **88/88** in **68.04 seconds** (feature build 3.17 seconds), using:

```text
FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerCoarseTests|FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerTierTests|FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerHandoffTests|FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerHaulerTests|FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerInboundRuntimeTests
```

The reports are retained locally at `artifacts/feature-tests/results/400f10e6b15e455db2dd9e5db6464ead/tests.trx`
and `artifacts/feature-tests/results/397f17dee7e04ba6ae3ff167da1e5b7d/tests.trx`. New regressions assert SQL,
connection and fitting counts, paid-cycle rejection with unchanged ore, rollback/reopen, resource-free remote
navigation, exact allowance admission, unmeasured-action reservation and eventual admission after overrun debt.
Both Python policy suites passed: **8 catalog tests (1.707 seconds)** and **9 formatting tests (0.024 seconds)**.
Formatting verification and Git whitespace checks passed. The full .NET suite and sustained installed-world replays
were not run. These tests do not establish complete-session latency or checkpoint cost improvements.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer check:** after restarting the rebuilt server, inspect the remaining enriched overrun warnings.

## Reusing compact remote roles, November 11, 2026

The reported coarse-simulation warnings included indivisible units taking 15–180 ms against at most 5 ms of remaining
allowance. The allowance is a soft admission budget: a bot's transaction must finish or roll back before ownership can
yield. Postponing subsequent units repays the debt but cannot preempt the current action. Units that begin near the end
of the allowance can also exceed its smaller remainder without exceeding 5 ms themselves.

One avoidable cost was discarding each role object after every remote settlement. Its next action repeated pilot,
inventory, route and geometry loading even when the inputs were unchanged. Successful demotion now clears the role's
geometry and obstacle arrays and detaches it from the detailed system runtime, while keeping compact pilot, navigation,
target and role state in the scheduler. A returning miner loads geometry on demand before locating its home station.
The bounded shared fitting cache retains its existing limits; observer scenes and combat contacts are still released.

Committed pilot/fitting, agent membership and known-ship/position changes invalidate compact inputs by identity. Skill changes use the
existing fitting-revision triggers. Catalog generations and schema/settings resets also invalidate them. Validation
runs before remote settlement, explicit role reads and promotion back to detailed scheduling. Rolled-back writes do
not publish revisions. Successful settlements capture their own committed revisions, preventing self-invalidation.
Durable cursors, job revisions, resource/payment transactions, budget size and population settings are unchanged.

Overrun warnings now identify the action, pilot and system (or completed system pass), together with SQL commands,
connections and fitting calculations. This distinguishes future cold loads or expensive atomic boundaries from small
units admitted with little remaining allowance. The change does not establish that every atomic action fits in 5 ms.

References: [EVE University server ticks](https://wiki.eveuniversity.org/Server_tick) and
[Carbon Destiny Ballpark.cpp at `114e89fa584f0e1a81eb0a679aabf41af8870072`](https://github.com/carbonengine/destiny/blob/114e89fa584f0e1a81eb0a679aabf41af8870072/src/Ballpark.cpp).
The local evejs simulation README (configuration version `261108.1`) was also consulted for the boundary between
one-second Destiny motion and gameplay eligibility. These references do not define Eden's remote population policy;
movement laws and wire formats are unchanged, and no reference implementation was copied.

**Automated validation:** tool restore, solution restore and solution build passed (zero warnings/errors; final solution
build 1.11 seconds). The final affected run passed **73/73** in **60.02 seconds** including runner startup; its feature
build took 2.28 seconds. Exact regressions cover warm role reuse without founder reloads, repeated committed pilot changes,
ship/position changes, fitting/catalog invalidation, rollback and lazy return geometry. The earlier affected run exposed
a paid-cycle return-deadline regression; the final result includes explicit re-anchoring when a retained role resumes.
The final filter was:

```text
FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerCoarseTests|FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerTierTests|FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerHandoffTests|FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerHaulerTests|FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerInboundRuntimeTests.StationlessLaneContinuesToItsSavedRemoteStationAndPersistsCursor|FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerInboundRuntimeTests.FailedInboundTransitionPreservesResidentAndSavedProgress
```

The report is retained locally at `artifacts/feature-tests/results/f02d8689d7854bd4a2df9b44d88cf9e6/tests.trx`.
Both Python policy suites passed: **8 catalog tests (2.979 seconds)** and **9 formatting tests (0.035 seconds)**.
Formatting verification and Git whitespace checks passed. The full .NET suite was not run.
Final validation uses affected-area automated tests; long-running simulation replays were not run. Runtime latency
and the frequency of budget overruns on the installed world remain unmeasured for the final revision.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer check:** after deploying the rebuilt server, inspect any remaining enriched overrun warnings.

## Coarse distant populations, November 10, 2026

Regional population settings and creation remain unchanged. Detailed jobs now follow selected humans, watched arrivals and
unfinished interactions. Eligible distant pilots share one scheduled entry per physical system, a deterministic initial
stagger and a durable cursor. Settlements rotate between systems and target a minute between complete passes. Congestion
postpones work without creating historical action entries. See [population behavior](capsuleers.md#coarse-distant-populations--november-10-2026).

Schema 138 adds system cursors, explicit traveler admission and each pilot's last committed remote settlement time. The
canonical job, resource/payment changes, pilot settlement time and cursor advancement share one transaction. A rejected
cursor write rolls everything back. On return, bounded maintenance samples committed state at the current human-online
clock, preserves the paid remainder and forgoes unsettled remote time. No stopped-server production is introduced.
Demotion records the detailed cutoff, so time already spent near a player cannot extend a future paid deadline. Restored
defense/ratting state pins detail before the first remote visit. Expired gate-arrival presentation never becomes a new
arrival after restart; its committed transfer identity is retained.

Coarse settlement and regional creation share a soft 5 ms per 50 ms allowance. Atomic overruns borrow subsequent windows;
unused idle time never funds a burst. Immutable route geometry and traffic template calculations prepare outside ownership.
Demotion releases loaded roles, contacts and fitting references while retaining compact identity, route and scheduling metadata.
Each allowance overrun logs its work type, unit duration and remaining allowance.

The Performance page now distinguishes saved population, detailed/urgent pilots, coarse pilots/systems, pending preparation
and pinned systems. The old background count incorrectly subtracted observed systems from every known system. Simulation
and preparation timing now subtracts scene/Local publication from the entire maintenance pass; it previously displayed only
the final scheduler turn. These are elapsed wall times, including waits, rather than frame latency. Background preparation
runs separately. Remote age reports the oldest incomplete full-system pass. Budget overruns are cumulative. Return details
on the Population page identify the system, affected bots and longest skipped interval for that visit. A global sum across
unrelated systems is deliberately omitted from the page because it obscures the amount skipped on an individual return.

Reference: [EVE University, Server tick](https://wiki.eveuniversity.org/Server_tick) describes one-second visible ticks and
separate heavy work. [Carbon Destiny, Ballpark.cpp](https://github.com/carbonengine/destiny/blob/114e89fa584f0e1a81eb0a679aabf41af8870072/src/Ballpark.cpp)
was consulted at pinned revision `114e89fa584f0e1a81eb0a679aabf41af8870072` for simulation ownership and movement boundaries.
The minute-scale economy policy is Eden's approximation; these references do not establish native EVE remote-bot behavior.

Optional replays use frozen saved inputs, identical independently arriving local frame/market requests and periodic checkpoints.
`tools/Measure-CoarseSimulation.ps1` selects 1/2/4 observers with 45/225/450 remote communities (1×/5×/10×), ongoing
regional preparation, a 70-second backlog and 900-community stress. `tools/Summarize-CoarseSimulation.py` reports each
workload separately, including SQL/connections/fitting counts, allocations, freshness, ownership and checkpoint stages.
The comparison engine was prepared from `efbf03739a06ee19410c20ff118d27630cf627c3`. The matched ten-minute matrix was
not run: on November 11 the maintainer requested that agent performance testing stop and retained real-world measurement.
Performance thresholds and sustained freshness remain unverified. The tools retain every scheduled checkpoint report,
separately from complete frames and market calls. Optional `EDEN_BOT_REPLAY_ENTRY_TRACE=1` captures the largest cold-entry
phases; it is disabled during ordinary timing runs.

The completed 60-second `probe-boundaries` diagnostic measured one observer and 450 visited communities with preparation.
After its 30-second warmup, 20 frames had p95 **21.79 ms**, p99 **28.17 ms**; 10 market requests had p95/p99 **188.37 ms**.
Twelve cold traffic handoff holds exceeded 250 ms. A slow market request waited behind checkpoint capture. These are
diagnostic results from before the final compact-entry changes, not acceptance of the delivered revision. They led to
removing legacy traveler fit/health hydration from membership reconciliation and keeping population metadata reads out of
resident projection. The subsequent `probe-compact-entry` was interrupted at the maintainer's request and supplies no
completed latency result. The runner's other three theory rows were unselected, not additional measured workloads.

The separate one-second `entry-trace` run profiles setup only and establishes no warmed latency result. Earlier probes
and interrupted regression runs were used to diagnose cursor, deadline, arrival and loading behavior; their intermediate
results do not establish final performance acceptance.

**Automated validation, November 11, 2026:** tool restore, solution restore and solution build passed with zero warnings
and errors. The final solution build took 3.94 seconds. Exact new regressions ran before their affected classes, including
paid-cycle remainder, cursor rollback/retry, schema migration, restored interaction retention, expired arrivals and cold
membership/metadata reads. The final two cold-entry regressions passed in 7.02 seconds including runner startup.

The wider **686/686** run took **378.60 seconds** and included immediate mining, migration, transaction, checkpoint,
system-isolation, ownership and hosting dependencies. It preceded the final compact-entry adjustment. Its filter was:

```text
FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~Eden.Protocol.Tests.Mining|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~TransactionParticipantTests|FullyQualifiedName~CheckpointOwnershipTests|FullyQualifiedName~DetachedCheckpointTests|FullyQualifiedName~SystemRuntimeIsolationTests|FullyQualifiedName~OwnershipTimingTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~Eden.Protocol.Tests.Administration.AdministrationTests
```

After that adjustment, the final affected run passed **422/422** in **282.01 seconds**; its feature build took **1.70 seconds**:

```text
FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~Eden.Protocol.Tests.Administration.AdministrationTests|FullyQualifiedName=Eden.Protocol.Tests.Combat.ServerPerformanceReplayTests.SustainedPercentilesSortArrivalOrder
```

Reports are retained locally as `artifacts/feature-tests/results/834205708c8f428081def36e5c91e83a/tests.trx` and
`artifacts/feature-tests/results/8392a30b2bcf4be89df886a1cce01427/tests.trx`. Both Python policy suites passed: **8 catalog
tests (1.999 seconds)** and **9 formatting tests (0.317 seconds)**. Formatting verification checked **1,645 files**, and the
140-column check and Git whitespace verification passed. The full .NET suite was not run. Opt-in saved-world replay cases
without configured inputs do not establish measured acceptance.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer check:** revisit a populated region to assess native arrivals and gameplay feel.

## World-loading and ammunition lookup hotspots, November 10, 2026

The reported Rider DPA screenshot identified `PilotAmmunitionStore.Contents` taking 53 seconds in the captured run.
The installed save had 232,512 inventory rows when first inspected and only the `(OwnerId,LocationId)` index.
`Contents` queries by `LocationId` alone, so `EXPLAIN QUERY PLAN` confirmed `SCAN WorldItem`. Every loaded-magazine
read could traverse the entire inventory during fitting, supply creation and combat-ship hydration.

Schema 135 adds the nonunique `IX_WorldItem_LocationId` index to new worlds and upgrades existing worlds.
Migration establishes it before saved-world validation and restores it after historical table upgrades.
The owner/location index remains available for personal inventory queries. The lookup and its duplicate detection,
ownership checks, ammunition quantities and cooldowns retain their existing behavior. No running save was modified.

A fresh read-only backup of the running save contained **250,835 rows**. In a disposable memory image, 200 identical
magazine queries returned identical data in **1,359.69 ms before** and **0.58 ms after** adding the index.
The plan changed to `SEARCH WorldItem USING INDEX IX_WorldItem_LocationId (LocationId=?)`.
These are SQL-only timings in one copied-image sample, not a whole-server speedup or a repeat of the 53-second capture.
The raw local summary is `artifacts/population-startup/ammo-index-measurement.json`.

Other reported hotspots were fitting application, cold combat-ship hydration, skill levels, ship attributes and
`LiveWorld.Find`. Registered canonical world paths now use the dictionary directly, preserving normalized alias
fallback; 10,000 warmed direct lookups allocate no path strings. New-ship blueprints retain validated final attributes
and stage them through the existing revision-checked, transaction-published fitting cache. Initial navigation can reuse
those values without recalculating the new fit. Cold combat hydration still constructs its weapon profile when needed;
its magazine lookups now use the container index.

The [background population record](capsuleers.md#batched-population-startup--november-10-2026) describes the associated
login-readiness, memory preparation and batch changes. Flight simulation and wire representations are unchanged.

**Automated validation.** Focused tests cover fresh and schema-134-upgraded worlds with 25,000 unrelated inventory items,
the actual query plan, preserved magazine ownership/type/quantity, repeated reads, reopened persistence, canonical and
aliased world lookup, and the zero-allocation direct lookup. Blueprint/background tests cover cached attributes, exact
grants, rollback, cancellation, scheduler registration and resumed preparation. Final check results are recorded with
the population milestone.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Restart a rebuilt server to apply the index, then capture the same ammunition/loading
workload in Rider and compare the reported hotspots. No running server was restarted by the agent.

## Spatial NPC scenes, November 9, 2026

`INpcEncounters.ReadScene` returns nearby native contacts and an immutable set of every warping NPC in the system.
The default interface adapter filters the existing full contact read, preserving alternate implementations and the
nonresident path. The resident implementation advances the complete authoritative system before selecting contacts.
It then builds native records only for nearby NPCs, using the existing shared contact and exact-time effect logic.
Full-system gameplay and information reads retain `List`.

Gameplay's geometry index preserves source order and the existing inclusive 250 km spherical visibility policy.
It narrows dispersed candidates along the widest coordinate and checks the original three-dimensional distance.
Dense, entirely visible scenes reuse their complete identity list. World shares the index and off-grid warp identities
between observers; resident position, availability, system or warp transitions invalidate the affected geometry.
SQL population invalidation, observer deactivation and runtime clearing release it too. NPC cache writes use one
geometry-aware setter, including loading, committed SQL synchronization, gunfire, repair and drone damage.
Health, weapons, repairs and electronic warfare remain fresh at the requested time, including repeated same-tick reads.

The reference is [Carbon Destiny's bubble updater at revision 114e89fa584f0e1a81eb0a679aabf41af8870072](https://github.com/carbonengine/destiny/blob/114e89fa584f0e1a81eb0a679aabf41af8870072/python/destiny/net/server/_bubbleupdater.py),
which separates shared bubble membership changes from player-specific transitions, and CCP's
[Drakes of Destiny optimization](https://www.eveonline.com/news/view/fixing-lag-drakes-of-destiny-part-2-1).
[EVE University grids](https://wiki.eveuniversity.org/Grid) describe current native grid behavior; this increment retains
Eden's existing visibility policy rather than adopting modern EVE grid geometry. No wire fields or client changes are
introduced. Common update serialization and independently executing system workers remain separate future work.

### Matched scene replay

The exact capacity method ran with baseline production `bad7ddaa` and the retained changes on the same machine,
in Debug with .NET 10.0.14 and tiered compilation disabled. Each case used four warmups and thirty measured frames.
The dispersed baseline used the same new layout harness with unchanged baseline production code: ten groups of
100 stationary NPCs, separated by 1,000 km, with ten observers near the first group. The dense combat case retained
the existing workload. One baseline and one retained process per case are reported; earlier development trials are
excluded. The output sink includes Blue encoding but excludes encryption, network backpressure and native rendering.

| Workload | Baseline frame median / p95 / maximum, ms | Retained frame median / p95 / maximum, ms | Baseline / retained allocation, MiB/frame |
| --- | ---: | ---: | ---: |
| Ten observers, 1,000 dispersed idle NPCs | 18.75 / 25.70 / 31.13 | 14.48 / 16.35 / 17.11 | 9.96 / 7.86 |
| Ten players, 1,000 NPCs fighting on one grid | 79.80 / 86.21 / 119.91 | 80.20 / 87.64 / 195.49 | 50.19 / 49.93 |
| One player, empty system | 0.29 / 0.79 / 3.48 | 0.29 / 0.46 / 1.78 | 0.201 / 0.200 |

The dispersed median frame improved about 23%, and managed allocation fell about 21%. Its NPC contact phase fell
from 7.85 to 3.98 ms median; ten observers refreshing the already-advanced scene fell from 2.07 to 1.15 ms.
Dense combat's contact phase fell from 20.34 to 19.50 ms, and its observer-only refresh fell from 3.07 to 2.79 ms.
Complete dense-combat frames did not improve; the retained maximum was higher in these single-process samples.
These results support the dispersed-scene improvement, not a dense battle capacity or tail-latency guarantee.
Encoded combat output remained approximately 2.70 MiB/frame. Every warmed frame retained zero SQL commands,
connection opens and fitting calculations. The full system still advances; candidate selection uses one coordinate
before the exact distance test, so its worst case remains a full scan.

Checkpoint medians use the final three populated captures. The dispersed capture/projection/write phases were
0.47 / 22.24 / 15.01 ms before and 0.44 / 22.34 / 13.47 ms after. Dense combat was
0.59 / 46.14 / 15.57 ms before and 0.71 / 50.07 / 14.53 ms after. Projection and writing remain outside the owner;
their durations are not counted as simulation stalls. No checkpoint improvement is claimed.

Reproduce with the exact filter below and `DOTNET_TieredCompilation=0`, `EDEN_CAPACITY_NPCS=1000`,
`EDEN_CAPACITY_PLAYERS=10`, `EDEN_CAPACITY_MODE=idle` and `EDEN_CAPACITY_LAYOUT=spread`. Use `combat` and
`dense` for the populated battle, or zero NPCs and one player for the empty case. `EDEN_CAPACITY_OUTPUT` accepts an
absolute report directory. Raw matched reports are under ignored `artifacts/spatial-scenes/{baseline-*,retained-*}`.

**Automated validation.** The first exact filter was
`FullyQualifiedName=Eden.Protocol.Tests.Combat.NpcCapacityBenchmarkTests.MeasureNpcAndPlayerCapacityThroughNormalSessions`.
The new index boundary theory and local-contact preparation regression also passed individually. Stable tool/solution
restore, the warning-free solution build, 422 affected .NET tests (116.59 seconds including test host startup/shutdown),
eight catalog policy cases and nine formatting policy cases passed in 127.02 seconds. Formatting verification passed
in another 20.96 seconds; documentation link/anchor, UTF-8, retained-heading and whitespace checks also passed.
The validation sequence took approximately 148 seconds, excluding the separate benchmark replays. The affected filter was:

```text
FullyQualifiedName~Combat.Npc|FullyQualifiedName~Combat.LiveCombatObjectTests|FullyQualifiedName~Combat.LiveSimulationTests|FullyQualifiedName~Combat.ResidentDroneTests|FullyQualifiedName~Combat.ResidentCombatMonitoringTests|FullyQualifiedName~Combat.PlayerEwarNpcTests|FullyQualifiedName~Mining.NpcMiningTests|FullyQualifiedName~Capsuleers.CapsuleerTrafficPerformanceTests|FullyQualifiedName~Capsuleers.CapsuleerDefensePerformanceTests|FullyQualifiedName~Persistence.DetachedCheckpointTests|FullyQualifiedName~Persistence.LiveWorldTests|FullyQualifiedName~Hosting
```

Tests exercise exact distance boundaries on all axes, large/negative coordinates, ordering, immutable snapshots,
same-tick movement and rollback, subsecond health and weapon expiry, local-only native contact construction,
zero-SQL observers, NPC mining response warps, destruction and reopened checkpoints. The full .NET suite was not run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** None for this server timing comparison.

## NPC contact refresh cost, November 9, 2026

In the 10-pilot, 1,000-NPC combat replay, each client asks for contacts during flight and combat updates.
The resident world already shares individual NPC records, but each request enumerated the system twice, advanced
repair checks twice and built a filtered scene for the observer. The protocol then rebuilt three identity lookups
when a ship's state changed. Baseline contact refreshes consumed a median 25–27 ms across the ten sessions per frame.

World now returns the ordered NPCs it has just advanced under the owner. Publishing an equivalent record retains
the resident instance. Repair evaluation skips health sampling for hulls without repair cycles, clears any saved
deadlines when appropriate and preserves the revision. The protocol uses the observer's existing identity index to
compare against the previous snapshot. It computes arrivals and departures only when membership changes, and compares
electronic warfare effects only when their source lists differ. Each read still uses the requested time for shield
recharge, weapon expiry and timed motion. The observer regression covers equal-size membership replacement,
outbound command order and an immediate repeat read.

The movement reference was [Carbon Destiny Ballpark.cpp at 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ballpark.cpp).
The [NPC repair record](combat.md#catalog-driven-npc-self-repair-november-5-2026) documents the pinned catalog rules.

### Frame and checkpoint measurements

The Release capacity replay ran twice on baseline `b0ec6f9b` and twice on the retained changes, on the same machine
with .NET 10.0.14 and tiered compilation disabled. Each process ran four warmups and thirty measured frames per case.
The test clock drives motion and deadlines. Weapon rolls and desktop scheduling can vary. Output includes encoding
to an in-process sink. Encryption, network backpressure and client rendering were outside the replay.

| Combat workload | Baseline runs | Optimized runs |
| --- | ---: | ---: |
| Complete frame median, ms | 62.70 / 65.08 | 49.67 / 49.89 |
| Complete frame p95, ms | 98.61 / 70.10 | 90.39 / 52.77 |
| NPC contact refresh median, ms per frame | 25.35 / 26.54 | 12.08 / 12.11 |
| Ten observers refreshing an already updated scene, median ms | 4.65 / 4.91 | 1.70 / 1.78 |
| Managed allocations, mean MiB per frame | 75.09 / 75.09 | 50.17 / 50.17 |
| Encoded output, mean MiB per frame | 2.70 / 2.70 | 2.70 / 2.70 |

Each measured frame issued zero SQL commands, opened zero connections and performed zero fitting calculations.
One pilot in an empty system measured 0.24 / 0.26 ms median before and 0.24 / 0.21 ms after.
Idle timings and combat p95 varied enough that these runs establish a gain only in typical populated-frame latency.

Checkpoint medians below use the last three populated checkpoints per run. The owner copies the relational image
and captures resident state. Projection and the durable write run outside the owner.

| Checkpoint phase, ms | Baseline runs | Optimized runs |
| --- | ---: | ---: |
| Capture under owner | 0.60 / 0.53 | 0.60 / 0.56 |
| Detached projection | 49.38 / 49.13 | 47.27 / 45.22 |
| Durable write | 14.12 / 14.38 | 15.47 / 14.28 |

### Next performance priorities

1. Profile combat notification construction and encoding. Session output consumed 22.75 ms median in both optimized
   combat runs. The current packet builder encodes an inner object-call payload into an owned byte array before the
   outer packet is encoded. Measure those two stages separately through the encrypted send path before changing either.
2. Count contact requests by caller and reason. Flight, weapon and enforcement updates can ask for the same system
   several times during a frame. Any shared result must account for same-tick commands, visibility, subsecond shield
   recharge and exact effect expiry. The remaining 12 ms includes world reads and per-observer differences.
3. Measure checkpoint capture against representative large saves before changing world locking. The small fixture
   spent under 1 ms in capture in these samples. Detached projection can overlap simulation. Its full duration cannot
   be counted as a frame stall.

**Automated validation.** Tool and solution restore, the solution build (zero warnings and errors), 197 focused tests
(68.60 s including test host startup and shutdown), eight catalog policy cases, nine formatting policy cases and formatting
verification passed. The validation sequence took 121.02 s. The first exact filter was
`FullyQualifiedName=Eden.Protocol.Tests.Combat.NpcCapacityBenchmarkTests.MeasureNpcAndPlayerCapacityThroughNormalSessions`.
The equal-size replacement regression also passed individually. The combined filter was

```text
FullyQualifiedName~Combat.NpcSceneUpdateTests|FullyQualifiedName~Combat.NpcRepairTests|FullyQualifiedName~Combat.NpcEwarTests|FullyQualifiedName~Combat.PlayerEwarNpcTests|FullyQualifiedName~Combat.NpcEncounterTests|FullyQualifiedName~Combat.NpcPursuitTests|FullyQualifiedName~Combat.NpcOrbitTests|FullyQualifiedName~Combat.NpcAggressionTests|FullyQualifiedName~Combat.NpcCombatTests|FullyQualifiedName~Combat.LiveSimulationTests|FullyQualifiedName~Combat.LiveCombatObjectTests|FullyQualifiedName~Combat.ResidentCombatMonitoringTests|FullyQualifiedName~Combat.ResidentDroneTests|FullyQualifiedName~Combat.NpcCapacityBenchmarkTests|FullyQualifiedName~Persistence.DetachedCheckpointTests|FullyQualifiedName~Capsuleers.CapsuleerDefensePerformanceTests
```

The full .NET suite was outside this focused run. Raw replay measurements are under the ignored
`artifacts/scene-speed/{before,control,retained-1,retained-2}` directories.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** None for this server timing comparison.

## Pooled protocol buffers and selective libraries, November 9, 2026

The library evaluation uses .NET SDK 10.0.403 and retains Eden's native Blue/Destiny wire formats and SQLite save path.
Runtime additions are [CommunityToolkit.HighPerformance 8.4.2](https://github.com/CommunityToolkit/dotnet),
[ZLinq 1.6.1](https://github.com/Cysharp/ZLinq) and [ZLogger 2.6.1](https://github.com/Cysharp/ZLogger).
[BenchmarkDotNet 0.15.8](https://github.com/dotnet/BenchmarkDotNet) is confined to development tooling.
Gameplay and Infrastructure retain their .NET-only dependency boundaries.

Blue encoding can write into a caller-owned `IBufferWriter<byte>`. Its primitive writer emits explicit little-endian values;
text encodes directly into the destination rather than allocating a byte array per field. The existing owned-array entry point
remains available for immutable snapshots and callers that retain bytes. Native ballpark builders rent their temporary storage
but return independent arrays. The encrypted send path retains rented plaintext, compression and ciphertext buffers until
the asynchronous write completes. Compression thresholds, zlib envelope, AES mode and frame limits retain their existing rules.

Consulted [Carbon Blue `Marshal.cpp`, revision af0d65b29f951260ea09140ca11adf5d40566997](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/Marshal.cpp)
for byte-string and Unicode forms. Golden fixtures cover native string tokens, length transitions, all integer-width boundaries,
negative zero, infinities and a NaN payload. Other regressions cover invalid-surrogate replacement, packed rows, nested values,
independent owned output, allocation budgets, compression fallback and delayed writes with concurrent pool reuse.

ZLinq is limited to in-memory NPC/contact and static-object visibility filtering. EF queries remain translated to SQL.
The benchmark uses the same visibility predicate and source order for both implementations; selected scene regressions cover
exact range boundaries, wrong-system contacts, undocked/docked views and off-grid warp tracking. This is selective adoption,
not a repository-wide LINQ replacement.

The executable attaches a host-owned ZLogger sink to the existing typed Infrastructure log. Quiet/verbose behavior,
explicit tracing, event timestamps, Unicode output and warning/error routing to stderr are preserved. Independent output/error
queues each hold at most 4,096 entries. A full queue waits rather than dropping messages or growing without a bound.
Normal shutdown detaches the sink and drains both queues. Existing callbacks can still construct strings before logging;
this integration does not claim that every log call is allocation-free. Tests stall output, continue producers while queue
space remains, fill the bounded queue and verify per-stream ordered delivery and shutdown flushing.

RecyclableMemoryStream was not added alongside the chosen pooled writer. Dapper is deferred because the measured warmed
simulation already avoids SQL; changing row mappers would not accelerate that work. UniTask is deferred because Eden uses
`AsyncLocal` catalog scopes and diagnostics. MemoryPack would require a separate internal format use case; it does not replace
the native client protocol or SQLite checkpoints. No separate ZString package is needed for the selected changes.

### Measurements

The baseline production source was `3029517e`. Baseline and final runs used Release builds, the same generated workloads,
.NET 10.0.14 and the same desktop. Benchmarks and replay runs were serial, without concurrent builds/tests. Other desktop
activity was not controlled. Reports are retained under ignored `artifacts/library-performance`.
These measurements preceded integration of the separate mod-administration changes through `18082c4b`; the combined main
checkout receives its own affected validation below.

BenchmarkDotNet ShortRun used one process, three warmups and three measured iterations per case. The four serialization cases
use native market-order rows and either return owned encoded bytes or send through compression/encryption to `Stream.Null`.
Numbers are mean microseconds and managed KiB allocated per operation; short-run confidence intervals are broad.

| Operation / orders | Baseline us | Final us | Baseline KiB | Final KiB |
| --- | ---: | ---: | ---: | ---: |
| Encode / 32 | 76.22 | 51.54 | 136.71 | 83.49 |
| Encrypted send / 32 | 66.55 | 53.88 | 147.54 | 73.85 |
| Encode / 4,096 | 15,038.05 | 8,974.02 | 17,485.38 | 12,097.77 |
| Encrypted send / 4,096 | 13,375.17 | 6,795.68 | 17,722.34 | 9,517.61 |

The large encrypted-reply case allocates approximately 46% less and its sampled mean time falls approximately 49%.
Raw reports are in `micro-baseline`, `micro-optimized` and `micro-final`; the middle run used the initial stream-adapter
encoder and is not the final primitive writer. Only the baseline and final columns above describe the retained encoder.

The scene-filter comparison is deliberately retained as mixed evidence. At 64 objects, standard LINQ measured 409.2 ns / 648 B
against ZLinq's 444.5 ns / 600 B. At 4,096 objects it measured 7,505.1 ns / 2,144 B against 7,147.3 ns / 2,096 B.
That is a small allocation reduction in both cases, a slower small-scene sample and a faster larger-scene sample. Final result
arrays still allocate. This does not justify a global replacement or establish a material full-frame speedup.

Complete-session replays retain four warmups and thirty measured frames per case. They exercise owners, observers, output
encoding and periodic checkpoints with a fixed test clock. Every measured warmed frame retains the assertions for zero SQL
commands, zero connection opens, zero fitting calculations and no live-world projection. A clean archived baseline source copy
was built under `baseline-source` for a fresh control immediately before the final populated run.

| Case | Initial baseline median / p95 ms | Fresh baseline control ms | Final median / p95 ms | Allocated MiB before / final |
| --- | ---: | ---: | ---: | ---: |
| 1 player / 0 NPCs, idle | 0.18 / 0.34 | Not repeated | 0.24 / 1.13 | 0.159 / 0.160 |
| 10 players / 1,000 NPCs, combat | 65.08 / 98.46 | 71.09 / 109.69 | 72.99 / 112.74 | 89.27 / 73.85 |

The populated frame allocates approximately 17% less; encoded output remains approximately 2.703 MiB per frame. Full-frame
timing did not improve: the final median/p95 is slightly slower than the fresh control and slower than the initial baseline.
The baseline controls themselves differ, so these runs establish allocation savings rather than a complete-session latency win.
The idle case also establishes no latency gain. The fixture's socket sink encodes output but excludes encryption and network
backpressure; the separate encrypted-send benchmark covers that CPU/allocation path without real network I/O.

Checkpoint costs remain separate from frame timing. Medians of the last three populated checkpoints were:

| Checkpoint phase | Fresh baseline control ms | Final ms |
| --- | ---: | ---: |
| Owner capture | 0.62 | 0.56 |
| Detached projection | 64.57 | 53.18 |
| Durable write | 17.60 | 17.16 |

These three samples do not establish a checkpoint optimization; its persistence implementation and schema did not change.
Whole-database checkpoint copying and repeated scene work remain independent optimization targets. Pooling also retains
reusable buffers, so reduced allocated bytes are not a measurement of reduced process working set.

### Validation

**Automated validation.** After integrating remote main through `18082c4b`, tool restore, solution restore and the complete
solution build passed (7.92 seconds, zero warnings/errors). The affected selection passed 401 tests with three optional
installed-client mod package tests skipped because `EDEN_TEST_CLIENT_ROOT` was unset (404 selected, 89.76 seconds).
Both Python policy suites passed: eight catalog cases in 0.785 seconds and nine formatting cases in 0.015 seconds.
Formatting and the 140-column policy passed. The full .NET suite was not run.

The first exact feature filter was
`FullyQualifiedName=Eden.Protocol.Tests.Transport.EncryptedPacketsTests.FullCatalogSummaryCompressesIntoOneEncryptedFrameWithoutChangingNativeRows`.
The combined final filter was:

```text
FullyQualifiedName~Eden.Protocol.Tests.Serialization.|FullyQualifiedName~Eden.Protocol.Tests.Transport.|FullyQualifiedName~Eden.Protocol.Tests.Flight.|FullyQualifiedName~Eden.Protocol.Tests.Hosting.|FullyQualifiedName~Eden.Protocol.Tests.Combat.NpcSceneUpdateTests|FullyQualifiedName~Eden.Protocol.Tests.Combat.NpcCapacityBenchmarkTests|FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.TypedCallResultTests|FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.PilotServicesTests|FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.CooperativeFleetTests|FullyQualifiedName~Eden.Protocol.Tests.Administration.ModRuntimeTests|FullyQualifiedName~Eden.Protocol.Tests.Administration.ModFoundationTests|FullyQualifiedName~Eden.Protocol.Tests.Administration.ModLauncherApiTests
```

The final four encoding benchmarks completed in 46.39 seconds; the initial four-case baseline completed in 54.50 seconds.
The combined stream-adapter/scene-filter comparison completed eight cases in 80.92 seconds. The final populated/empty
Release replays and the fresh baseline control passed their frame budgets and database/fitting counters. Their frame and
checkpoint measurements above precede the mod-administration integration; the combined validation also passes the ordinary
small normal-session capacity replay. No new live-client evidence is claimed.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** None.

## Hauler return-route stalls, November 9, 2026

A subsequent maintainer capture showed a **5,586.11 ms** traffic maintenance hold and **5,147.53 ms** of world wait on the next tick. The running server already had the per-job lock change described below. That capture did not identify an individual NPC action. Slow owner warnings now include the NPC job, pilot and starting system when a job ran during the hold.

The earlier copied replay omitted the installed traffic snapshot and stopped after 44 wakes. A new frozen checkpoint retained the selected pilot at station 60011866, 1,861 jobs and the matching 27,947 imported traffic profiles. `ReplayReportedSessionWithDueTrafficAndCheckpoint` now copies an optional sibling `traffic.sqlite`. `EDEN_TRAFFIC_REPLAY_SECONDS=180` runs 180 measured one-second steps after four warmups. The configured copies and output are under ignored `artifacts/traffic-holds-latest-source`, `artifacts/traffic-holds-imported-before` and `artifacts/traffic-holds-retained-final`.

The checkpoint contained a 17-jump return journey from station 60000505 to 60009682. Replaying its cold route calculation in isolation took **3,541.17 ms**, with **1,035 catalog commands**. The search read full system-content records, including planetary data, while checking candidate gates. The resident server now prepares reciprocal links and a compact index of system security before accepting clients. Both live for the catalog generation. Homeward searches skip security checks for unrestricted hulls, and restricted hulls apply the same security rule to the source, destination and candidate gates. An explicit test covers a carrier detour, both rejected endpoints and the same warmed reads with a catalog cache too small to hold the graph.

The installed-catalog regression checks every system in the saved return route and requires zero commands, connection opens and fitting calculations for the route after preparation. In an isolated run, cold preparation took **2,731.48 ms** outside world ownership and the route took **0.37 ms**. This establishes a multi-second traffic path. The maintainer's original five-second event remains unattributed.

With the final retained graph, the combined replay measured cold preparation at **3,053.52 ms** and the route at **5.18 ms**, again with zero database or fitting work during routing. On the newer checkpoint, the longest hold in each of 184 concurrent traffic wakes measured **36.43 ms p95 / 265.61 ms maximum**, compared with **44.44 / 340.20 ms** before the route change. Warmed calls measured **31.98 / 81.37 ms** across 540 samples, and ticks measured **20.71 / 63.56 ms** across 180 samples. The slowest remaining traffic warning identified an Arrival for pilot 90001866 in system 30002659. The copied replay did not reproduce a five-second player wait.

The maintainer also reported Guests [4] with blank rows, then saw the list recover. The pinned client populates the count before it primes character names and builds the scroll entries. Both copied-station replays resolved every present character ID to a nonempty name through `station.GetGuests` and `config.GetMultiOwnersEx`, with two guests present after 44 steps. The cause of the temporary blank panel was not established. No guest protocol changes were made.

**Automated validation.** The configured 17-jump route and two 184-wake session replays passed in 66.95 seconds including test startup. The two copied-station roster replays passed in 40.69 seconds. An initial affected run had 397 passes and one failure in a test expecting two independent contexts to retain the same cached route plan. The fixture now retains its catalog generation across both contexts, as the running host does, and still checks that editing the fixture invalidates the plan. That exact regression passed. On base `f2c8ad1a`, the corrected affected filter passed **398 tests** with zero skips in 2 minutes 38 seconds. The filter was the traffic maintenance selection below plus `FullyQualifiedName~CatalogGeographyTests|FullyQualifiedName~CatalogProviderTests|FullyQualifiedName~CatalogAdoptionTests`. Both restores, the solution build, both Python policy suites (8 and 9 cases) and formatting verification (1,384 files) passed. The ordered validation took 3 minutes 12 seconds. Full .NET regression was not requested.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer check.** Restart the rebuilt server and capture the full traffic owner warning and nearby tick if another long stall occurs.

## Traffic maintenance lock duration, November 9, 2026

The maintainer's next console capture showed a 967.73 ms traffic hold and a 388.60 ms tick with 379.31 ms spent waiting for world ownership. The resident server now restores saved traffic indexes during startup, before listeners accept players. Maintenance releases the shared owner after each committed NPC action and acquires it separately for presentation. The existing action cap and elapsed-time budget still bound each turn. Every acquisition uses current jobs, locations and observer membership. Scene capture, application and Local publication stay ordered under the shared owner. Performance tracing exposes miner and hauler action costs separately.

The copied-world replay starts the first traffic wake as character selection arrives. It then measures 44 traffic wakes with the same due jobs and checkpoints in the concurrent and isolated cases. The hold measure starts after ownership acquisition and excludes time waiting to enter. Each wake contributes its longest maintenance or presentation hold. Call and tick timings use the final 40 wakes. Baseline production code was `1714af68`. The baseline and final reports are in the ignored `artifacts/traffic-holds-baseline` and `artifacts/traffic-holds-final` directories.

| Concurrent workload | Before | After |
| --- | ---: | ---: |
| Character selection world wait | 114.71 ms | 11.18 ms |
| Longest traffic hold per wake, p95 / maximum | 168.29 / 352.03 ms | 105.47 / 132.60 ms |
| Full warmed call, p95 / maximum | 75.68 / 352.44 ms | 84.33 / 160.63 ms |
| Full warmed tick, p95 / maximum | 1.27 / 63.67 ms | 0.51 / 0.75 ms |

A traced hauler entering a system issued 2,347 individual asteroid inserts during one action. Transactional field initialization now writes the generated deposits together per belt. That action fell from 2,487 commands to 141, with three connection opens and zero fitting calculations in both cases. A regression compares all 183 generated rows against the existing standalone save path and rolls back the new deposits with the enclosing transaction. Another regression opens a checkpoint between two due jobs and verifies that only the first job advanced on disk, then both finished once. Existing checkpoints and the save format continue to work. The per-wake measurements come from one local Debug replay per revision. Neither run reproduced the supplied 967.73 ms hold. Full call p95 increased in this sample.

**Automated validation.** The configured exact filter `FullyQualifiedName=Eden.Protocol.Tests.Combat.ServerPerformanceReplayTests.ReplayReportedSessionWithDueTrafficAndCheckpoint` passed both replay cases in 34.73 seconds including test startup. The checkpoint-interleaving and transactional belt regressions were also run individually. The final affected filter passed 382 tests with zero skips in 155 seconds including test startup:

```text
FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~Eden.Protocol.Tests.Mining|FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~DetachedCheckpointTests|FullyQualifiedName~LiveWorldTests|FullyQualifiedName~SessionLatencyTests|FullyQualifiedName~PilotLoginTests|FullyQualifiedName~StationGuestTests
```

Tool and solution restore, solution build and both Python policy suites passed (8 and 9 cases). The first formatting check identified a missing blank line in the test clock. The correction built with zero warnings and formatting passed. This validation predates the return-route changes above. Full .NET regression was not requested.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer check.** After restarting on this revision, compare the next traffic hold and nearby session world wait under normal play.

## Session stalls and world ownership, November 9, 2026

Slow calls and ticks now report queue delay, world-lock wait, traffic refresh/reconciliation, pending damage, handler, entry, updates, motion, contacts and output separately. Successful, unsupported and rejected calls record completion of their reply write. Deferred target-lock replies retain their existing delayed delivery. Total processing includes output and notification flushing. Phase times are inclusive and can overlap, so they must not be added. The threshold remains 250 ms, including queue delay. Faster operations remain quiet unless performance tracing is enabled.

World ownership records the operation observed before the longest acquisition wait. Outer ownership leases also warn when a hold exceeds 250 ms. Traffic maintenance/presentation and checkpoint capture have explicit labels. An observed owner describes the acquisition sample, and another operation can acquire ownership before the waiter resumes. The labels identify contention without changing the shared authoritative owner or moving mutation boundaries.

Ordinary traffic turns use the maintained ordered queue. Membership changes publish new jobs incrementally, and an unobserved completed transfer withdraws its next job until re-entry. Scoped queue callers retain their existing candidate selection. A deterministic 1,804-agent comparison across twenty due turns allocated 2,604,824 bytes with scoped candidate rebuilding and 144,000 bytes with the canonical queue. Ordering, failed actions, withdrawal and re-entry are covered directly.

Traffic captures reuse the last visible revision when observations and observer generations match. Maintenance skips repeated scene application and chat publication after a successful application. Failed applications retain their retry batch. Settlement metadata remains current. Health is still read at the exact requested time to preserve damage, recharge and repair completion between calls. This deliberately retains observation reads while removing repeated publication. Six fixed-clock maintenance cases cover empty/occupied systems, one/two observers and imported traffic, asserting no repeated application and zero SQL, connections or fitting calculations.

The opt-in `ServerPerformanceReplayTests.ReplayReportedSessionWithDueTrafficAndCheckpoint` copies `EDEN_PERFORMANCE_SOURCE`, selects pilot `90001835` at station `60011866`, advances 44 one-second steps and exercises `onlineStatus.GetInitialState`, `ship.MachoResolveObject`, `pingService.Ping` and normal ticks. Four steps warm the session. The last forty produce 120 call and forty tick samples. The ping retains its existing unsupported reply. Traffic work and checkpoints run either concurrently with requests or finish before requests begin. Reports are written to `EDEN_PERFORMANCE_OUTPUT`. The source save is opened read-only.

The baseline is `f4210d5d` with only the replay added. The frozen source is retained in ignored `artifacts/stalls-source`, with initial reports in `artifacts/stalls-baseline` and intermediate/final reports in `artifacts/stalls-optimized` and `artifacts/stalls-final-report`. Baseline concurrent call p95/maximum was 134.69/248.78 ms, and tick p95/maximum was 0.77/200.82 ms. Isolated call p95 was 0.27 ms. An intermediate optimization run measured concurrent calls at 219.82/364.93 ms and ticks at 0.48/61.65 ms. Its slowest call attributed 364.49 ms to world wait and 0.01 ms to its handler. These observations establish the contention path and allocation reduction. They do not establish a call-latency improvement. Neither run reproduced a one-second warmed stall. Listener startup is outside this replay.

The maintainer's subsequent console capture showed a 1,204.94 ms tick with 1,189.58 ms of world wait, and a 1,005.07 ms tick with 664.85 ms of world wait. A following 1.90 ms call had queued for 1,006.06 ms. This is supplied console evidence. Interactive/native acceptance was not conducted by the agent.

Travel references were reviewed against [EVE University stargates](https://wiki.eveuniversity.org/Stargates) and [Carbon Destiny Ball.cpp revision 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp). Existing build-3662168 wire replies and travel boundaries are exercised by the affected tests.

Traffic maintenance now uses the existing catalog operation scope for both its turn and presentation. The copied-save comparison retained identical SQL command counts while the first maintenance sample fell from 117 connections to seven and SQL execution from 157.61 to 12.87 ms. Later active samples fell from 28/58/78 connections to 8/8/15. A synthetic catalog fixture already served these reads from memory, so its connection count stayed at fourteen in both modes. Its regression checks scope lifetime, disposal and identical canonical jobs.

The final scoped-maintenance replay measured concurrent calls at **65.99 ms p95 / 215.75 ms maximum**, and ticks at **0.85 / 56.93 ms**. Isolated calls measured **0.74 / 1.62 ms** and ticks **0.38 / 0.53 ms**. Cold selection was **877.64 ms** in the first case and **279.68 ms** in the subsequent case, reflecting process warmup. Final concurrent checkpoint capture/projection/write was **68.81 / 3.83 / 458.91 ms**. Both warmed p95 targets pass in this replay, with zero one-second warmed samples. These are single local Debug observations, with reports in ignored `artifacts/stalls-scoped-report`.

The maintainer supplied another console capture identifying **traffic maintenance** as the observed owner, including a **1,568.15 ms** hold. The scope correction followed that evidence. Native behavior after this correction remains unverified.

**Automated validation.** The initial affected selection passed 881 tests with one optional pinned-Python portrait test skipped in 460.89 seconds. It covered Capsuleers, PilotSessions, Flight, detached checkpoints, session performance, pilot login, station guests and resident polling before the later ownership/catalog-scope refinements. After the ownership and catalog-scope refinements, the final filter `FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~DetachedCheckpointTests|FullyQualifiedName~SessionLatencyTests|FullyQualifiedName~PilotLoginTests|FullyQualifiedName~StationGuestTests` passed **226 tests in 91.94 seconds**. The separate copied-save replay passed both overlap/isolation cases in 27 seconds. Pinned tool/solution restores and the final solution build passed with zero warnings/errors. Both Python policy suites passed eight and nine cases in 0.637 and 0.017 seconds. Formatting verification passed across 1,371 files, and the added protocol index link was checked. The initial replay setup lacked its copied catalog after an attempted cross-volume hard link. Copying the catalog resolved that setup failure before the reported baseline. A development build overlapped that running test process and encountered locked output files. Subsequent builds use a free output directory. The first scope regression incorrectly required fewer connections from the already-cached synthetic catalog. Investigation established equal fourteen-connection counts there and the reduction on the installed catalog above. The corrected regression checks actual scope lifetime and unchanged jobs. Full .NET regression was not requested.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** On the rebuilt server, retain the next slow ownership and session warning together to identify the holder during a native stall.

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

## Gate-area traffic stalls, November 9, 2026

The supplied tenfold-population log showed background traffic retaining the world owner for up to **2,699.79 ms**,
with a player frame waiting **2,587.20 ms**. These are shared-world stalls: waiting near a gate exposes delays caused
by traffic actions elsewhere, including station trades. The same log also contains shorter dogma, initial-output and
contact-refresh spikes; this change does not establish that every logged delay has been eliminated.

Starter-drone discovery now uses the catalog's combat-drone group index and batches that group's attributes, type
payloads and dogma payloads. It previously pinned the complete type-payload dataset during first resupply. The cold
lookup now uses three queries in the focused fixture, with no full type-payload pin; repeated lookup uses no SQL,
connections or fitting calculation. Hulls without drone support skip the unused fitting calculation. Resupply also
keeps its initial requirements when loading, purchasing and reloading have left onboard inventory unchanged. Actual
inventory changes still recalculate requirements within the same payment transaction.

Resident motion projection now writes at most 128 positions per command. Gate transfers and trades still synchronize
pending resident state before their SQL transaction, but no longer prepare one command for every moving traveler.
The existing conflict update changes coordinates only: membership changes remain owned by docking and gate transfer.
The same batching applies to detached checkpoints. Regression cases cover exact large/fractional coordinates, failed
later batches with complete rollback, retry, and reopening the checkpoint.

| Matched warmed workload | Previous projection | Batched projection | Projection commands before / after |
| --- | ---: | ---: | ---: |
| 512 moving ships | 10.60 ms | 3.41 ms | 512 / 4 |
| 2,048 moving ships | 44.15 ms | 13.07 ms | 2,048 / 16 |

These are workstation observations of projection at a transaction boundary, not end-to-end client latency. Both runs
used the same deterministic fixture. Connection counts stayed at two for projection plus the verifying read, and
fitting counts stayed at zero. Detached capture, projection and file writing are measured separately by the test.

An opt-in diagnostic copies the reported save, catalog and traffic snapshot before profiling the four reported pilots,
the three systems with the largest gate-transfer warnings, and five seconds of ordinary maintenance. An exploratory
scoped replay reproduced a **1,763.60 ms** station departure, of which **1,669.26 ms** was resupply. It did not reproduce
the full 2.7-second gate transfer. Source copies and cold-cache effects make these diagnostic samples unsuitable as a
matched whole-world before/after benchmark. The installed save and game client are never modified by the test.
The final isolated diagnostic on the integrated code measured the first sampled resupply at **76.83 ms** and five
sampled gate transfers at **25.95–48.81 ms**. These measurements do not prove a bound for all populated-world actions.

To reproduce, set `EDEN_GATE_PERFORMANCE_SOURCE` to that save's `world.sqlite` and select
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerGatePerformanceTests.CopiedSaveProfilesReportedTrafficActions`.
Without that variable the diagnostic reports its opt-in requirement and returns without measuring a workload. The
ordinary tenfold fixture separately asserts that real scheduled gate transfers and station departures occur.
`EDEN_TRACE_PERFORMANCE=1` also enables maintenance phase, SQL, connection and fitting diagnostics in the server;
the added phases distinguish template refresh, resupply, route selection, geometry and gate completion.

Gameplay references were rechecked against [EVE University stargates](https://wiki.eveuniversity.org/Stargates).
[Carbon Destiny Ballpark.cpp revision 114e89fa584f0e1a81eb0a679aabf41af8870072](https://github.com/carbonengine/destiny/blob/114e89fa584f0e1a81eb0a679aabf41af8870072/src/Ballpark.cpp)
retains separate evolution and collision work for live actors and skips dead-bubble actors. Eden retains its existing
movement, collision, gate validation and wire rules; these changes concern catalog access and SQL projection.

**Automated validation.** Tool and solution restores, solution build with zero warnings/errors, catalog policy **8/8**,
formatting policy **9/9**, formatting/140-column verification of 1,567 files and diff checks passed. Exact development regressions used
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerGatePerformanceTests.StarterDroneDiscoveryDoesNotPinTheEntireTypeCatalog`
and `FullyQualifiedName=Eden.Protocol.Tests.Persistence.MotionProjectionTests.TransactionProjectsMovingTrafficInBoundedBatchesAndCheckpointsLatestPositions`.
The former failed on the original full-catalog pin before passing with the indexed lookup. The broader pre-integration
filter `FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~Eden.Protocol.Tests.Persistence.MotionProjectionTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.DetachedCheckpointTests`
passed **326 tests in 181.86 seconds**, including the configured copied-save diagnostic. After integrating main
`77b3fd5c` and the maintenance tracing entry point, an initial **97-pass/1-failure** run exposed a diagnostic sink
routing issue. Preserving the caller's ambient reporting scope fixed it; the exact tenfold regression then passed.
Final filter `FullyQualifiedName~CapsuleerGatePerformanceTests|FullyQualifiedName~MotionProjectionTests|FullyQualifiedName~DetachedCheckpointTests|FullyQualifiedName~CapsuleerSupplyTests|FullyQualifiedName~PilotSpaceActivitiesTests|FullyQualifiedName~CapsuleerHaulerTests|FullyQualifiedName~CapsuleerTrafficPerformanceTests|FullyQualifiedName~CapsuleerDeadlinePerformanceTests|FullyQualifiedName~CapsuleerTrafficNavigationTests|FullyQualifiedName~CapsuleerInboundRuntimeTests|FullyQualifiedName~CapsuleerMaintenanceTests|FullyQualifiedName~CapsuleerEncounterTests`
passed **98/98 in 49.65 seconds**. That run includes the opt-in diagnostic's unconfigured return; a separate configured
run passed in approximately **14 seconds**. That diagnostic plus the Python policies and formatting verification took
**37.23 seconds**. Existing complete-frame tests include owners/observers, occupied and empty systems, output encoding
and separate checkpoints. No full .NET suite was requested or run. Native rendering, input and gameplay feel remain unverified.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Rebuild/restart the server and revisit the same gate with the tenfold setting; compare
world-owner warnings and frame waits. If another long atomic action remains, capture its enabled maintenance phases.


## Shared-owner stall relief, November 10, 2026

This is the first rollout stage of the solar-system ownership work. The resident SQLite connection still owns the
shared world from open through close. Per-system simulation ownership, classified transaction participants and
checkpoint copying without system ownership are pending; this stage does not establish system independence.

The additional supplied `49cf2afc` log shows why the earlier resupply/index improvements were insufficient. After
regional population preparation completed, frames still waited roughly 0.5 to 1.5 seconds behind traffic. A checkpoint
capture held ownership for 800.28 ms. Calls also accumulated queue time before dispatch; a fast handler alone cannot
establish player-visible latency.

The shared owner now admits outer acquisitions in arrival order. A job that releases ownership cannot repeatedly
reacquire ahead of an already waiting flight command. Nested calls remain reentrant, retain the same transaction and
count as part of the outer hold. Flight, traffic/activity and the separate database movement boundary use measured
leases; Protocol no longer receives a raw world synchronization object. Timings name the operation, request and known
system, and separate waiting from holding. Traffic jobs attribute their source system to the outer lease. Checkpoint
subscription survives detached execution context, and capture reports its SQLite copy duration separately from
projection and file writing. Session diagnostics include arrival-to-reply, ownership, timed-effect and projection times.
Phase totals remain inclusive and must not be added together.

A frame's synchronous contacts and update work now occupies one position in that queue instead of joining again for
each monitor. Socket output remains outside ownership, with automated assertions at the real output callback.
Fully supplied magazines of the same ammunition type reload together, using the existing typed ammunition operation.
The two-magazine fixture reduces fitting calculations from four to two: one reload calculation and the required cargo
check. Limited reserves retain sequential allocation. Completed requirements and exhausted budgets stop scanning
later stacks/orders; a zero-quantity capacity request performs no fitting or volume work. Drone resupply skips
candidate selection when the existing count reaches the control limit, and otherwise reads all candidate skill levels
in one batch, including absent skills. The typed Gameplay predicate preserves the original requirement comparisons.
Quantities, cooldown validation, payment/inventory transactions and rollback remain authoritative.

### Frozen two-player replay

`tools/Freeze-SystemReplay.py` takes read-only SQLite backups of the source save, original catalog and traffic snapshot,
and retains the active skill package and its editor state. It writes file sizes/hashes and the baseline revision to
`manifest.json`. The installed save and game client are never modified. The measured frozen world was **339,345,408
bytes**, SHA-256 `54e8bf59930854912ca61e70f4a861e5835dfbb779ccf696d53cb2fbf619ecde`; original catalog SHA-256
`df5e5ac02a31c1f30d9b70cd8da6f094ff9f8c8d9317595296317b1b88212d0b`; traffic SHA-256
`9c6fcec26ca8625a44be6dfa430ec8694d1b7ffa16c35149dacb843422297574`. The frozen settings use **5x traffic** and **12 gate
visitors**, with a fixed November 10, 2026 12:00 UTC simulation clock. Baseline gameplay is `49cf2afc`, including the
regional population and ammunition-container index improvements, with the measurement harness applied.

Two independently scoped real player sessions occupy system B: one issues `beyonce.CmdGotoDirection` and completes
normal observer frames, including the production anomaly, probe and mission hooks; the other calls
`station.GetGuests`. System A (`30002659`) has a separate presence lease,
real scheduled traffic and regional population preparation. The replay asserts actual station departures and gate
transfers. Checkpoints run at the existing 15-second simulation interval. Each run uses a new copy, four warmups and
40 measured samples after entry and a ten-second protection warmup. Both players and the background worker start
together for each sample; the simulation clock
advances after all finish. These are measured server tasks and encoded native packets, excluding a real socket,
client rendering and inbound calls arriving during an already running player frame.

Set `EDEN_SYSTEM_REPLAY_SOURCE` to the frozen directory and `EDEN_PERFORMANCE_OUTPUT` to a fresh report directory;
select `FullyQualifiedName=Eden.Protocol.Tests.Combat.ServerPerformanceReplayTests.ReplayTwoPlayersUnderRemoteTrafficAndPopulationLoad`.
`EDEN_SYSTEM_REPLAY_SAMPLES` overrides 40 for investigation. An unconfigured opt-in test returns without measuring.
Local inputs/reports are retained under ignored `artifacts/system-isolation/`. The final matched comparison uses
`baseline-complete-*` and `optimized-complete-*`. Earlier `before-*`, `after-*`, `final-*` and `release-*` diagnostics
preceded the complete-frame harness and final resupply corrections; they are not release acceptance. The isolated
baseline source image retains the same measurement harness and restores the parent resupply and frame behavior,
with unordered monitor admission. `baseline-code.json` records the relevant source hashes.

| Operation | Baseline p95 / p99 / max (ms) | Optimized p95 / p99 / max (ms) | Baseline / optimized >=1s |
| --- | ---: | ---: | ---: |
| CmdGotoDirection | 72.82 / 138.56 / 157.41 | 9.31 / 9.98 / 10.66 | 0 / 0 |
| frame | 334.52 / 529.48 / 891.24 | 95.33 / 321.54 / 341.80 | 0 / 0 |
| GetGuests | 6.40 / 24.76 / 55.26 | 3.45 / 4.81 / 4.90 | 0 / 0 |

The slowest measured owner remained a traffic action in system `30000138`: **748.29 ms before / 328.97 ms after**.
The final checkpoint's SQLite copy took **150.77-152.59 ms** across optimized runs; capture was **150.81-152.62 ms**,
detached projection **20.78-22.81 ms**, and file writing **990.64-992.23 ms**. Background task elapsed totals, including
scheduled saves, were **12.05 / 6.87 / 6.79 seconds before** and **6.80 / 6.59 / 6.68 seconds after**. The same desktop
also ran other applications; these Debug observations establish this replay, rather than a worst-case latency bound.
Neither version reproduced the historical 2.59-second wait in the final matched workload.

| Player operation (120 measured samples) | SQL before / after | Connections before / after | Fits before / after |
| --- | ---: | ---: | ---: |
| CmdGotoDirection | 803 / 1869 | 219 / 363 | 0 / 0 |
| frame | 1927 / 1464 | 1365 / 1241 | 0 / 2 |
| GetGuests | 701 / 633 | 293 / 327 | 0 / 0 |

Grouping ownership changes where projection and shared invalidation costs are paid; lower latency does not mean every
SQL/connection counter improves. The unchanged small-world warmed frame regression separately retains zero SQL,
connections and fitting calculations. Two optimized loaded-frame fits reflect invalidation during remote work.

Each run completed **663 traffic actions**, with no failures and zero due jobs/oldest delay at each sampled turn
boundary. This demonstrates equal simulated work; the fixed-clock replay does not establish unlimited real-time
throughput. The reports retain every background phase plus SQL, connection and fitting counts per player operation.
Checkpoint copying remains a shared-owner stall and must be addressed by the later writer/system-owner split.

### Remaining rollout

- Give World an on-demand system registry and migrate movement, contacts, NPC acquisition, nonfatal combat, heat,
  timed modules, repairs, traffic publication and complete warmed player paths. Deterministically pause A in each
  migrated phase and require B's command, observer and complete frame to finish before releasing A.
- Add explicit transaction participant sets with the conservative full-projection fallback; coordinate transfers by
  entering persistence before ascending system IDs, revalidating membership/revisions and publishing only after commit.
- Capture one immutable resident generation while briefly coordinating the writer/system owners, release system
  owners before SQLite copying, and retain detached projection, atomic replacement and failed-save retry. Prove
  opposing jumps, failed/retried trades/transfers, destruction during capture, save failure and shutdown/reopen.

The behavior reference remains [EVE University server ticks](https://wiki.eveuniversity.org/Server_tick), reconciled
with Eden's exact saved deadlines and existing same-tick behavior. [Carbon Destiny Ballpark.cpp revision
114e89fa584f0e1a81eb0a679aabf41af8870072](https://github.com/carbonengine/destiny/blob/114e89fa584f0e1a81eb0a679aabf41af8870072/src/Ballpark.cpp)
was rechecked for system-local evolution/collision boundaries; no movement constants, timing rules or native messages
change here. [SQLite's backup documentation](https://www.sqlite.org/backup.html) describes why copying remains a
separate persistence cost. No new live-client observations were made.

**Automated validation.** Tool/solution restores and the solution build passed (8.58 seconds, zero warnings/errors).
The corrected focused selection passed **158/158 in 52.31 seconds**, using four workers. Both Python policies passed
(**8 catalog** tests in 0.855 seconds and **9 formatting** tests in 0.022 seconds). The configured complete-frame
comparison passed six runs: baseline processes took approximately **85, 53 and 47 seconds**, optimized processes
approximately **49, 46 and 45 seconds**, including copy/startup/cleanup rather than just measured frames.
The exact development filters covered `OwnershipTimingTests` admission, nesting, detached checkpoint timing,
failed diagnostic sinks and system attribution; `CapsuleerGatePerformanceTests` covered bounded hangar work,
batched reloads, limited reserves and batched drone eligibility; `SkillRequirementRulesTests` covered direct typed
eligibility boundaries. The focused class selection additionally covered supplies, maintenance, lock order,
background preparation, encounters, handoff, navigation, inbound recovery, provisioning, assault-defense rules,
live/detached checkpoints, motion projection, session counters and timed-defense frames.

An earlier expanded selection was deliberately interrupted after **1,189 passes, 17 failures and one existing skip**;
it is not a passing completed run. Sixteen failures were stale expectations/setup after the already merged T2 and
regional-startup changes; one was the new diagnostic-failure test assuming a wallet row existed. Test setup now
explicitly registers prepared communities for direct-job scenarios, the audit expects admitted T2 consumers, and the
wallet regression performs and verifies a real persisted upsert. The corrected cases passed in the 158-test selection.
The final limited-reserve guard was followed by **55/55 resupply and energy-weapon tests in 30.92 seconds** with
one worker. A preceding parallel run had 54 passes and one warmed SQL-counter failure while catalog fixtures were
being modified; the serialized run passed without another code change. Final formatting verification checked
1,591 files and the 140-column policy, and diff checks passed. No full .NET suite was requested or run.
Native rendering, input and gameplay feel remain unverified.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Rebuild/restart the server and compare the same route's frame waits, arrival-to-reply
latency and oldest due-job age. Native rendering, input and gameplay feel remain unverified.


## Region population under contention, November 12, 2026

The regional population worker previously took the world persistence coordinator before waiting for the target system's
simulation owner. This could block unrelated account roster reads for as long as the target system stayed busy. The
worker now tries to acquire the coordinator and target system in the existing order. If either is busy, the system stays
pending and the worker yields until the next maintenance interval. Founder creation and its first job still commit together.

The concurrency regression holds a system open during region preparation and checks that the worker yields, a second
account can read its roster, and the retried founder and its first job survive checkpoint and reopen.

**Automated validation.** Tool and cached solution restore passed. The solution built without warnings in 30.33 seconds.
The exact regression
`CapsuleerBackgroundPreparationTests.BusyRegionPreparationLeavesAnotherAccountsRosterResponsiveAndRetries` passed
in 7.10 seconds. All 37 cases in `CapsuleerBackgroundPreparationTests`, `ResidentAdmissionTests`, and
`SystemRuntimeIsolationTests` passed in 21.37 seconds. The testhost connection timed out, so these tests ran through
the in-process xUnit runner. Eight catalog policy tests, nine formatting policy tests, CSharpier, and the line-width
check passed. The standalone .NET style check could not connect to its build host pipe.

**Live acceptance.** Agent client testing is disabled.

**Optional maintainer checks.** Enter a previously unprepared region while a second client stays active.


## Incremental population admission, November 10, 2026

Live human entry registers saved residents and requests background population through presence changes; it no longer
creates a home community in scheduler reconciliation. Gate watches likewise enqueue idempotent lane requests instead
of creating every visitor on the player path. The existing background worker processes one complete founder per step,
with occupied systems ahead of their pending reciprocal neighbours and other systems in activated regions. Its 25 ms
turn budget is checked between steps, not presented as a bound on one atomic founder or initial asteroid-field creation.

Every founder's identity, inventory, skills, wallet, role progress and first job commit together. The system fingerprint
stays incomplete while more slots remain. Restart uses saved slots to resume without replacing existing pilots. New home
residents begin docked and depart normally; previously saved openings and explicit offline reconciliation retain their
existing behavior. A failed founder does not roll back earlier committed founders. Failed regions are deferred for the
run and retried after restart, as before. Gate failures no longer block other queued work for that run.

Hauler and miner blueprints are constructed in the isolated validation image outside world ownership. Prepared blueprints
are pinned for the current step independently of the bounded reusable cache. Reservation views survive the worker's own
successful commits and reload after external pilot/traffic identity changes; failure discards speculative reservations.
The inserted ship and its blueprint use one contiguous inventory ID allocation. Each completed founder is registered
individually with the scheduler, avoiding a complete community reload after every slot.

This is the population-admission slice of the simulation scaling plan. Global transaction projection, table-wide resident
cache invalidation, per-system simulation ownership, remote mining progression, selective runtime eviction, and checkpoint
copying outside system owners remain outstanding. No latency or capacity target for the full installed world is claimed
by these fixture regressions. Existing replay reports are historical measurements and are not acceptance for this change.

The gameplay reference is [EVE University server ticks](https://wiki.eveuniversity.org/Server_tick). This slice changes
population scheduling and persistence, not native movement or serialization. The previously recorded Carbon Destiny
revision remains the engine reference. SQLite batch work follows Microsoft's
[transaction and command reuse guidance](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/bulk-insert).

**Automated validation.** Tool and solution restore, warning-free solution build (5.96 seconds), 362 affected .NET tests
(3 minutes 30 seconds), eight catalog Python tests, nine formatting policy tests and formatting verification passed.
The final filter was `FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~Eden.Protocol.Tests.Persistence.OwnershipTimingTests`.
Exact development regressions were `CapsuleerBackgroundPreparationTests.LiveEntryDefersCreationAndEachBatchCommitsOneFounderAcrossRestart`,
`WatchingGateQueuesVisitorsWithoutCreatingThemOnPlayerPath`, and
`MinerBlueprintIsPreparedOutsideOwnershipAndFailedNextFounderPreservesIt`. The gate regression also verifies one identity
snapshot across three founder commits. Opt-in installed-save replays without configured inputs did not measure workloads.
Initial affected tests exposed fixtures depending on synchronous
entry creation; encounter/performance fixtures now explicitly seed saved populations, and background-admission tests
exercise incremental creation. A concurrent feature build was rejected by Windows because the test assembly was in use;
subsequent validation is serialized. No full .NET suite was requested or run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Rebuild/restart and enter an unprepared region; compare responsiveness while new residents
depart or arrive. Native rendering, input and gameplay feel remain unverified.


## Classified transactions and scheduler reuse, November 10, 2026

Milestone 2 of the simulation scaling plan introduces immutable transaction participants for systems, pilots,
ships and shared NPC/wreck identities. Capsuleer actions declare their pilot and current ship; incremental founder
admission and belt renewal declare that they do not consume existing resident combat state. Timed-module expiry
selects its affected ships. Combat, motion, drone timers, heat and tractor projection remove only successfully
projected records from their dirty sets. Internal projection cannot recursively trigger a world-wide projection.
Unclassified operations retain the conservative path, reported separately as `projection.fallback`; classified
work reports `projection.participants`, including SQL, connection, fitting and elapsed counters when tracing is active.
A transfer's participant declaration must cover both owners/endpoints and every shared resource it mutates.

SQLite update callbacks now stage row identities until commit, discard rolled-back transactions, and retain earlier
commits when a later transaction rolls back on the same connection. Membership notifications include the actual
item/ship identity. Frequently read wallets, free SP, fitted attributes, module condition, module activity and spatial
membership invalidate affected keys. Shared directional, drone and wreck views account for old cached identities
and current committed locations, including transfers into a previously empty system. Wreck inventory dependencies
include both old item membership and new container membership. Remote inventory changes preserve unrelated dirty
drone flight and warmed observations. Rare unclassified dependencies retain conservative invalidation.

Scheduler reconciliation is keyed by committed membership, route changes, human activity/resumes and watched gates.
Individual unchanged job turns and observer captures reuse that membership. Prepared founders register incrementally;
population metadata refreshes changed systems instead of reloading every saved system. Presentation retains exact
health/recharge and expiry reads, shares equal snapshots, and continues to apply personal visibility in protocol code.
Coherent learning/heat reads no longer open an otherwise unnecessary mutation transaction.

Existing maintenance continuations still release ownership between atomic jobs, with action limits and elapsed checks
between actions. Scheduler diagnostics now retain overrun count and maximum overrun; the 10 ms budget is not a bound
on a single action. Idle resource renewal caches its next deadline and wakes on committed renewal/policy changes.
Market expiry and demand already use deadlines/cursors; enforcement and timed-module reads retain their existing
change/expiry caches. These changes do not add offline production or alter paid deadlines.

The deterministic projection matrix uses 1, 2 and 4 observers and 128, 640 and 1,280 remote moving ships (1×/5×/10×).
It compares the same resident position workload with conservative and classified transactions: the classified path
issues exactly one position command at every size, keeps all remote positions dirty for checkpoint capture, and adds
no SQL, connection opens or fitting calculations for warmed observer reads. This is a focused counter regression,
not an installed-save latency/capacity benchmark. Complete-frame p95/p99 targets, independently arriving request
backlogs, per-system execution, tiered remote simulation and checkpoint copying outside simulation ownership are
not established by this milestone. Earlier replay reports remain historical evidence.

The reference behavior remains nonlinear, continuously evaluated recharge as described by
[EVE University capacitor mechanics](https://wiki.eveuniversity.org/Capacitor), with the pinned client's existing
wire fields and deadlines unchanged. [Carbon Destiny Ballpark.cpp revision
114e89fa584f0e1a81eb0a679aabf41af8870072](https://github.com/carbonengine/destiny/blob/114e89fa584f0e1a81eb0a679aabf41af8870072/src/Ballpark.cpp)
was consulted for local simulation boundaries; no movement/collision rules or wire formats change here. No new
live-client observations were made.

**Automated validation.** Tool/solution restore and the warning-free solution build passed in 7.19 seconds
(build itself 5.20 seconds). The final focused selection passed **397/397** in **164.38 seconds** wall time
(runner duration 2 minutes 36 seconds). Both Python suites passed: eight catalog policy tests in 1.066 seconds
and nine formatting policy tests in 0.014 seconds. Formatting verification checked 1,594 files and the
140-column policy in 21.24 seconds. Documentation links, UTF-8/final-newline and diff checks passed.
Exact development filters started with `TransactionParticipantTests.ClassifiedTransactionProjectsOnlyItsSystemAndCheckpointRetainsTheOther`.
Additional regressions cover commit/rollback ordering, system transfers, independent personal caches, unchanged
resource maintenance, unchanged scheduler membership and dirty drone checkpoint/reopen. Development checks exposed
and corrected an extra cold renewal query, a wallet fixture with no saved balance row, and the historical expectation
that unrelated inventory membership must invalidate empty wreck scenes. An initial final selection passed 352/353;
the remaining catalog-scope comparison mixed cold hydration with warmed turns (21 versus 22 connections). Its
measurement now warms both fixture worlds before comparing equivalent due turns, and the exact case passed.

The final filter uses `FullyQualifiedName~` joined with `|` for these affected classes and immediate dependencies:
`TransactionParticipantTests`, `ResidentReadTests`, `ResidentPollingTests`, `ResidentCombatMonitoringTests`,
`LiveCombatObjectTests`, `LiveSimulationTests`, `ResidentDroneTests`, `DetachedCheckpointTests`, `ShipHeatTests`,
`PaidNaniteRepairTests`, `ModuleConditionTests`, `OverloadControlTests`, `DirectionalScanningTests`,
`DirectionalSessionTests`, `WreckTests`, `CapsuleerRuntimeTests`, `CapsuleerSchedulingTests`, `CapsuleerDeadlineTests`,
`CapsuleerBackgroundPreparationTests`, `CapsuleerInboundRuntimeTests`, `CapsuleerTrafficObservationTests`,
`CapsuleerPopulationTests`, `CapsuleerGlobalPopulationTests`, `CapsuleerTrafficPerformanceTests`,
`CapsuleerDeadlinePerformanceTests`, `ResourceRenewalTests`, `SpatialModuleTests`, `SpatialModuleOperationTests`,
`SpatialModuleProtocolTests`, `DroneSynchronizationTests`, `DroneDelegationCombatTests`,
`DroneDelegationSettlementTests`, `DroneCombatTriggerTests`, `ConcordTests`, `InformationTests`,
`InformationBoundaryTests`, and `InformationProtocolTests`.

Retained local artifacts are `artifacts/milestone2-validation/milestone2-final.trx`, `final-filter.txt` and
`published-source-manifest.json`; the initial failed report is retained separately as `milestone2.trx`.
The source manifest records base revision `9c5f597ba3d5b9a303cf73a752991746164122d5`, SHA-256 hashes of source,
tests and fixture inputs, the workload matrix, and the final filter. One test file received formatting-only changes
after the initial manifest capture; both hashes are retained. Published manifest SHA-256: `f2f9cdcb276cc203122b4d9cfe70b7a3b4e20d874e15e618fd5b35f8a66a1641`.
 No full .NET suite was requested or run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Rebuild/restart the server and compare the same busy travel route. Native rendering,
input and gameplay feel remain unverified.

## Independent system ownership, November 10, 2026

Simulation scaling milestone 3 introduces an on-demand runtime registry with stable system ownership identities.
Each runtime contains movement/combat state, exact deadlines, shared contacts, read snapshots, heat, tractors and
nanite schedules. Detailed miner and hauler instances are assigned to their current runtime; the scheduler retains
identity indexes and the shared due queue. Bot actions and observations select their actual system, including after
gate transfers. SQLite and shared economic transitions retain one persistence coordinator.

Resident operations enter their system directly. Opening a live SQLite connection acquires only persistence;
classified SQL boundaries resolve participating systems/pilots/ships/shared resources and retain those system
owners until connection close. Acquisition is persistence first, then ascending system ID. Unclassified mutation
and resident-query boundaries retain the measured conservative projection path. Ordinary unrelated read queries
need no system owners. Committed changes publish immediately to already-owned runtimes or enqueue immutable
row-identity batches for the destination owner's next read. Classified commits and prepared admissions limit these
batches to their affected systems; unrelated warmed contacts remain SQL-free. Rollbacks never publish those changes.

A cold dependency cannot upgrade a retained system lease. The operation unwinds before entering persistence,
then reruns typed membership, revision and deadline validation. Changed-key journals restore staged combat,
resource and position mutations before releasing ownership. They retain only changed keys rather than copying
the scene. The staged-shot regression forces SQL after a resident payment and verifies exactly one ammunition
charge, the returned damage result and its persisted state. Cross-system resident acquisition without persistence
is rejected. Personal visibility, continuously evaluated recharge and existing client timing remain unchanged.

Restored paid heat moves from startup state into one system owner on admission. Committed docking, destruction
and inter-system membership changes migrate or discard that ownership and cancel the appropriate preheat requests.
Shared coordination does not implicitly own unrelated systems. Durability and shutdown reject retained resident
leases rather than upgrading them.

Player call and tick scopes select a runtime without owning the whole frame. Mission, anomaly, probe, repair,
information and timed-effect hooks use operation boundaries; movement history and orders publish immutable arrays.
Traffic presentation uses the last published snapshot when shared capture is busy, and maintenance retries the
pending batch. Human membership changes still reconcile immediately through persistence. Protocol encoding and
socket output occur after simulation ownership is released. The reported traffic-refresh disconnect was reproduced
with the actual handoff/presentation services and fixed by entering shared scheduling through persistence first.

Checkpoint capture locks the current runtimes with persistence and captures immutable projections from the same
generation. It preserves the detached projection/atomic replacement and failed-save retry rules; dirty system state
is not flushed into live SQLite merely to take a checkpoint. Copying still retains system owners in milestone 3.
Tiered detail eviction and copying outside simulation ownership are the following milestones.

The reference is [Carbon Destiny Ballpark.cpp revision
114e89fa584f0e1a81eb0a679aabf41af8870072](https://github.com/carbonengine/destiny/blob/114e89fa584f0e1a81eb0a679aabf41af8870072/src/Ballpark.cpp),
with [EVE University server ticks](https://wiki.eveuniversity.org/Server_tick) as gameplay context. Movement and collision
calculations and the pinned build-3662168 wire contracts are unchanged. SQL/resident parity tests remain authoritative
automated evidence; no new client observations were made.

**Automated validation.** Tool/solution restore and the warning-free build passed; the final affected-behavior
filter passed **619/619 tests in 4m20s** (`milestone3-published.trx`). Both Python policy suites passed (8 and 9 tests),
and formatting verification passed. Development exact filters began with
`SystemRuntimeIsolationTests.PausedSystemCannotBlockAnotherSystemsWarmedMovement`. The concurrency matrix covers
1, 2 and 4 independently scheduled warmed requests in the same or separate systems while a remote owner is paused.
A complete native session frame includes real traffic refresh and anomaly hooks and asserts zero SQL, opens and fits.
Classified transactions also complete while an unrelated owner remains paused. The retained participant matrix
covers 128/640/1,280 remote positions (1×/5×/10×) and 1/2/4 observers. These are deterministic counter/isolation tests,
not an installed-save p95/p99 capacity claim. No full .NET suite was requested or run.

The retained filter and reports are under `artifacts/milestone3-validation/`. The source/input manifest records
parent revision `0083bdf0`, all source/test/fixture SHA-256 hashes and the workload settings. Published manifest SHA-256:
`3b18d6283c15e53df490be07933c72eea0a1aee4efc748b8bf48058f1c3766a7`.
Concurrent structure work through `86487bfa` was then merged at `c7c1ec3b`; the combined build and formatting check
passed, followed by **133/133 overlapping flight, structure, mission, checkpoint and ownership tests in 1m05s**
(`milestone3-integration.trx`). The integration source/input manifest SHA-256 is
`2c97380f25b3c2bf6c24c8f1f566cd600c95480b8d9db4c3c64fed2d04a09cee`.

**Live acceptance.** Not performed; agent client testing is disabled. The user's supplied exception is evidence of
a running development revision, not acceptance of the completed milestone.

**Optional maintainer checks.** Rebuild/restart the server and revisit the affected route. Native rendering, input
and gameplay feel remain unverified.

## Tiered simulation and detail retention, November 10, 2026

Simulation scaling milestone 4 distinguishes uncreated simulation, remote scheduled work and observed detailed
systems. World schema 137 records the first activation in `WorldCapsuleerSimulation`. Prepared populations in
unvisited systems remain dormant; previously activated communities and existing saved journeys retain their
canonical jobs after departure and restart. All advancement still uses the human-active clock. An empty server
and elapsed wall time while stopped do not create additional production.

Remote miners sleep until lock, paid collection and retry deadlines; remote travel uses the existing analytic
warp boundary and scheduled approach events. The role retains compact identity, flight, navigation and paid-work
inputs rather than repeatedly loading a fit or evolving an observer scene each second. Mining, cargo, trade,
gate transfer and resupply retain their ordinary atomic operations and canonical job revisions. Remote observation
does not request health/fitting hydration. Detailed defense and active ratting combat keep their existing
simulation rather than assigning simplified combat outcomes.

Promotion settles relevant due work in a bounded eight-action continuation before publishing detailed contacts;
an unfinished continuation keeps the scene activating for maintenance. Movement sampling never wakes the same
native stamp twice, while exact subsecond event deadlines remain unchanged. Demotion retains identities,
destinations, resource payments and deadlines, projects dirty motion/combat at its SQL boundary, then discards only
that system's scene and fitting working set. Projection failure retains dirty state for retry. Active combat,
targeting drones and unfinished missiles pin detail until safe to release. Stable system ownership shells and
lightweight saved work remain resident.

Shared immutable fitting profiles retain bounded selective FIFO eviction, with observed human pilots and detailed
bot pilots pinned. Blueprint eviction removes individual unpinned profiles while preserving the current preparation
batch. Compatibility-owner history also evicts cold systems selectively, after projection, rather than clearing
every scene when one limit is reached. Active or combat-pinned working sets may exceed a cache's soft limit;
activated-region history alone no longer expands the detailed allowance. Membership scans occur on membership
changes; ordinary turns update only touched actors and reuse the canonical due queue.

Gameplay references were checked against [EVE University mining](https://wiki.eveuniversity.org/Mining) and
[warp mechanics](https://wiki.eveuniversity.org/Warp_Mechanics). Eden's existing build-3662168 calculations and the
[pinned Carbon Ballpark.cpp](https://github.com/carbonengine/destiny/blob/114e89fa584f0e1a81eb0a679aabf41af8870072/src/Ballpark.cpp)
remain the local movement reference; this change adds no client wire contracts or alternate combat rules.

**Automated validation.** Tool/solution restore, a warning-free build, **360/360 affected tests in 2m18s**,
both Python policy suites (8 and 9 tests) and formatting verification passed. A final selective NPC-assessment
cache edit passed a fresh build and **16/16 immediate ratting, defense and retention tests in 11s**. The exact development filter began with
`CapsuleerTierTests.PaidMiningContinuesRemotelyAndReopensWithoutOfflineProduction`. Added checks cover one paid
collection remotely, persisted activation/job identity, no offline replay, detailed combat retention, zero detailed
entries after demotion, projection failure/retry, unrelated warm contacts and selective fitting retention.
The existing handoff variants retain all five miner travel/payment phases. This is deterministic automated
evidence, not a new installed-save p95/p99 capacity measurement. No full .NET suite was requested or run.

Retained reports and `final-filter.txt` are in `artifacts/milestone4-validation/`. The 360-test source/input manifest
SHA-256 is `fe4c0463cffc1cf6c9c24e0418f4af26d3b50ad4496c87c7bdca8bb769856593`; the final source/input manifest
is `568991474b796f853fb66a5e8bdae45ebc7b58ad4ffad8aa83a3d16a09bd4458`. Both record parent `4edbf994`, fixture
hashes and workload settings. `milestone4-retention-final.trx` covers the last cache edit; the earlier retention
run used the preceding compiled binaries after a missing-import build failure and is not counted as its validation.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Revisit a mining system after playing elsewhere and check the visible arrival and
mining presentation. Native rendering and gameplay feel remain unverified.

## Checkpoint copy outside system ownership, November 10, 2026

Simulation scaling milestone 5 captures immutable resident state and its common generation under persistence and
sorted system owners, then releases every system owner before copying SQLite. A brief registry-admission barrier
prevents a newly created runtime escaping that capture. Existing resident requests continue to use their system
owner; new runtime admission is open again during copying. A deadline that admits another runtime causes the
capture group to release and reacquire the expanded sorted set. Dormant runtime shells do not initialize module,
repair or fitting inputs merely because a save occurs.

Persistence coordination remains held throughout the relational backup. SQL writes and cold SQL dependencies
therefore still wait for copying; warmed movement, complete frames and nonfatal resident payments can finish
before the copy resumes. Atomic generation increments preserve concurrent resident changes. Applying projections
and writing the replacement file remain detached. A copy, projection or installation failure retains pending
changes for retry; successful saves do not discard dirty objects needed by later SQL boundaries. Shutdown flushing,
durable authentication, atomic replacement and the existing **15-second** interval are preserved.

Checkpoint reports now separate resident capture time, relational copy time, detached projection time and file
writing time. They record copied SQLite page bytes, installed file bytes and managed allocations on each stage's
executing thread. These allocation counters exclude SQLite's native allocations; copied page bytes describe that
image separately. A bounded 128-record persistence-wait history identifies operations that arrived during copying,
including explicit coordinated mutations and direct SQL entry. SQL admission also has its own performance phase.
Queue delay includes the copy and any earlier admitted operations; it is not a claim that copying is free for writers.

The consistency boundary follows [SQLite's backup API documentation](https://www.sqlite.org/backup.html). Automated
copy isolation pauses a real incremental SQLite backup after its first page, while independently arriving requests
must complete before release. The workload includes 1/2/4 same-system or separate-system movement requests alongside
a complete session frame with traffic and anomaly hooks, and both direct and explicitly coordinated SQL writers.
Additional tests cover a paid shot during copy, immutable earlier ammunition/damage, new runtime admission, queued
destruction without resurrection, failed-copy retry, reopened checkpoints and 32/160/320 dormant runtime shells.

**Automated validation.** Tool/solution restore, the warning-free build, **512/512 affected tests**
(202.73 seconds TRX wall time), both Python policy suites (8 and 9 tests) and formatting verification passed.
Exact development filters began with
`CheckpointOwnershipTests.CopyAllowsAnotherPaidShotAndKeepsEachCapturedGenerationCoherent`. Warm isolated paths
assert zero live SQL commands, connection opens and fitting calculations. The copy-pause tests use real elapsed-time
completion limits and retain stage/queue measurements in their reports. Deliberately paused-copy durations are
isolation evidence, not capacity benchmarks. No installed-save p95/p99 claim or full .NET run is made.

`artifacts/milestone5-validation/` retains `milestone5-published.trx`, `final-filter.txt`, `published-summary.json`
and the source/input manifest. The summary is generated directly from the retained TRX. Parent revision is
`cb48dd8a`; manifest SHA-256 is `8a978ec44edd962be62b3ee9a5bb185ed641fc277b76ea662300a14c92b75672`.
Six subsequent unpaused fixture saves copied **1,728,512 bytes** each: resident capture **0.03–0.05 ms**, copy
**0.60–0.71 ms**, and projection **0.09–0.11 ms**. Managed stage allocations were capture **15,792–29,464 bytes**,
copy **1,320**, projection **64,064**, and writing **7,032**. SQL-writer waits during the deliberately paused
earlier copies were **2.10–17.82 ms**. These small fixtures and artificial pauses do not establish installed-save
throughput or session latency percentiles.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** None; persistence boundaries and recovery are covered automatically. Native rendering,
input and gameplay feel remain unverified by this milestone.


## Traffic presentation and ownership accounting, November 10, 2026

The supplied log at `b2a3c0ae8d8a3425503f334da52c153a17d59932` records repeated traffic presentation holds
of approximately 260–810 ms and session ticks of 300–870 ms. Its `Ownership=145222.69 ms` inside an
866.79 ms tick sums overlapping system leases; it is not 145 seconds of elapsed player latency.
Ownership diagnostics now merge completed acquisition intervals, counting their union once. Individual
boundary wait/hold records remain available, and the 250 ms warning threshold is unchanged.

Traffic presentation admission now takes persistence coordination without acquiring every registered system.
Presentation then tries the explicit observed/entering system set in ascending order and retains those owners
through capture and publication. A busy participating system leaves the previous presentation available; unrelated
systems are excluded. `ICapsuleerHandoff.PresentationSystems` and `IWorldSynchronization.TryEnterSystems` carry this
internal contract. Capture also reads each active pilot under its actual system owner. It no longer invokes `RunCore` for up to eight
promotion actions: due systems remain activating until bounded maintenance settles their deadlines.
The existing ordered batch revision, publication retry and presence updates remain in use. Busy persistence
still leaves the player's previous committed presentation available. Cold pilot health transactions explicitly
participate in their pilot's system. Cold structure contact reads settle only the requested system's deadlines
and classify that system for projection; ordinary structure mutation/maintenance keeps its existing broader scope.
Unclassified persistence operations retain the conservative fallback.

The automated replay can select the reported pilot with `EDEN_REPORTED_TRAFFIC=1`, which uses pilot `90000006`,
account `1000003`, and verifies system `30002187`. It includes the production structure-contact hook, a second
player, remote traffic, incremental population preparation and periodic checkpoints. Use
`EDEN_SYSTEM_REPLAY_SOURCE` with a directory frozen by `tools/Freeze-SystemReplay.py`, and select
`FullyQualifiedName=Eden.Protocol.Tests.Combat.ServerPerformanceReplayTests.ReplayTwoPlayersUnderRemoteTrafficAndPopulationLoad`.
Each iteration records process allocation growth across its concurrent work; this is not a per-player allocation count.
Checkpoint capture, copy, projection, write and their allocations remain separately reported.

Baseline and updated replays use the same frozen databases, saved settings and clock, with four warmups and forty
measured iterations. The frozen manifest records the baseline revision and SHA-256 hashes. Artifacts are retained
under `artifacts/traffic-stalls/`, including `frozen/manifest.json`, replay logs, ownership records, samples and the
published test filter and source hashes. The baseline did not reproduce the supplied log's worst spikes, so these results establish
improvement for this workload rather than elimination of every source of live lag.

| Matched copied-save measurement | Baseline p95 / p99 (ms) | Final p95 / p99 (ms) |
| --- | ---: | ---: |
| Movement reply | 134.90 / 235.13 | 18.76 / 20.07 |
| Complete session frame | 27.27 / 36.62 | 23.20 / 25.80 |
| Station Guests reply | 69.05 / 221.15 | 18.55 / 28.15 |

The final isolated replay passed in 1m07s. Both versions completed **698 actions**, with zero due jobs and zero
failures at the last sample. No measured request/frame reached one second; the final frame p95 met the 250 ms
target. Movement SQL/connection/fitting counts were 329/71/2 before and 147/47/0 after; Guests counts were
1256/130/18 before and 23/11/0 after. Frame counts were 98/90/0 before and 98/98/0 after. Complete session hooks
can still perform persistence work; repeated warmed traffic observations are separately verified SQL-free.

Final process allocation growth per concurrent iteration had a median of 22,559,776 bytes and maximum of
136,451,176 bytes, including background population work and occasional checkpoint writing. The baseline did not
record this process allocation probe, so this is an absolute final measurement, not an allocation improvement claim.
The last checkpoint's capture/copy/projection/write times were 0.49/318.60/13.92/1502.23 ms, compared with
0.21/201.56/12.22/1267.93 ms in the baseline. Copying still delays SQLite writers while warmed resident frames can
continue. These samples do not establish a checkpoint speed improvement or a bound for all production actions.

The gameplay reference remains [EVE University stargates](https://wiki.eveuniversity.org/Stargates) and
[Carbon Destiny Ballpark.cpp revision 114e89fa584f0e1a81eb0a679aabf41af8870072](https://github.com/carbonengine/destiny/blob/114e89fa584f0e1a81eb0a679aabf41af8870072/src/Ballpark.cpp).
Movement calculations, wire formats, travel timing and saved schema are unchanged.

**Automated validation.** Exact development filters cover capture without executing due jobs, unrelated-system
presentation admission, cold structure contact isolation, overlapping ownership, and advancing observations.
The 1/2/4-observer matrix exercises 1/5/10 traffic multipliers and remote runtime counts, checking completed
jobs and zero SQL commands, connection opens and fitting calculations for warmed observations. A preliminary
168-test affected selection passed in 164.10 seconds. The final affected selection passed **185/185** in 2m38s using the exact filter in
`artifacts/traffic-stalls/published-filter.txt`; it includes publication retry, gate arrival/departure, docking,
same-tick health/subsecond recharge, structure deadline, rollback, isolation and checkpoint/reopen coverage.
Tool/solution restore and the final solution build passed with zero warnings/errors. Both Python policy suites
passed (8 catalog and 9 formatting tests), formatting verification checked 1,628 files, and line-width, local
documentation link, UTF-8/final-newline and diff checks passed. A missing import in the expanded population test
was corrected before the final successful build and test selection.
An intermediate build collided with the running testhost's DLL lock; subsequent builds run after test completion.
No full .NET suite was requested or run. The installed save and running server were not modified.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Rebuild/restart the server and compare pauses on the same busy route.
Native rendering, input and gameplay feel remain unverified.


## Bot queue throughput, November 10, 2026

The reported population panel had 5,262 jobs, 1,839 due jobs and a 68,931.99 ms oldest delay.
Its 170.79 ms simulation value describes the last scheduler call, including admission and bookkeeping,
not the sum of an entire maintenance batch. Maintenance requests one atomic action per scheduler call;
the existing 10 ms execution budget cannot interrupt that action.

The scheduler now samples the overdue count at most every 250 ms while work remains due. Empty/current
queues, inactive human time and failures force a new sample. Completed-action/failure counters and oldest
queue delay continue updating on every turn; queue ordering, exact deadlines and action budgets are unchanged.
An activated remote community's ordinary location/route changes update identity indexes without rebuilding
all running membership. Human membership, population revisions and fresh activation retain full reconciliation.

Demotion uses a shared immutable missile-presence cache, including absent results. Committed WorldMissile
changes invalidate it; pending transactions bypass caching and rollback cannot publish a replacement.
The shared cache survives per-system read-cache eviction. Projection failure still retains dirty state.
Remote actors retain a known-absent tackle/EWAR result against the committed WorldTackle/WorldNpcEwar revision;
active effects still use their ordinary timed reads, and loading resets the actor's absence revision.
Gate transfers return a typed authoritative location. Haulers reuse their snapshot only when a nonnull fitting
revision and catalog generation still match; other cases rebuild through the existing pilot reader.
Initial loading publishes the fitting cache through its owning transaction. Committed scheduler admission and
completed actions also publish their known pilot/ship membership, avoiding repeated SQL membership resolution.
Runtime traffic candidate pools are bounded to 128 catalog-generation/region/security/light-hull keys. Their
immutable tiers are materialized before the catalog reader is released. Cargo-dependent selection, reservations,
names and the roster cap continue to use their current transaction inputs.

Classified commit publication selects existing affected runtimes directly instead of sorting every runtime and
then filtering. Recipient selection never admits a new runtime. Cached participant membership no longer constructs
an unused EF context. Cold miner/hauler and asteroid reads declare their pilot/system so an unrelated owner cannot
force a whole-world projection. Miner initialization reuses its transaction for the pilot and laser snapshot.

Saved belt preparation loads existing renewal/deposit identities in two batch queries rather than two queries
per belt. Empty catalog systems perform neither query. Every existing saved deposit remains authoritative,
including depleted quantities. Resupply and reload preparation load magazines together and retain duplicate
magazine rejection and ordinary ownership, capacity, market, payment and cooldown operations.
Static geometry already uses the catalog generation cache; pending transaction geometry retains its SQL path.

The gameplay context is [EVE University server ticks](https://wiki.eveuniversity.org/Server_tick).
The movement reference remains [Carbon Destiny bubble updater, revision
114e89fa584f0e1a81eb0a679aabf41af8870072](https://github.com/carbonengine/destiny/blob/114e89fa584f0e1a81eb0a679aabf41af8870072/python/destiny/net/server/_bubbleupdater.py).
No new wire fields, save schema, movement rules or offline production policy are introduced.

### Sustained replay

`ServerPerformanceReplayTests.ReplaySustainedBotThroughput` is opt-in through `EDEN_BOT_REPLAY_SOURCE`.
Use a frozen directory from `tools/Freeze-SystemReplay.py` and set `EDEN_PERFORMANCE_OUTPUT` to retain results.
`EDEN_BOT_REPLAY_SECONDS` defaults to 600. The four cases activate 450/900 existing communities through
human-presence handoffs and exercise ordinary work or a 70-second active-clock backlog. Communities are selected
near fourteen saved pilots each; actual scheduled counts are reported rather than assumed to equal saved totals.
The replay runs production maintenance with a real elapsed clock, native session frames and periodic 15-second
checkpoints. Source databases and the running server are never modified.

Reports retain per-sample completions, due counts, oldest delay, SQL/connection/fitting frame counters,
phase timings, checkpoint stages and the frozen input manifest. Phase durations include nested children and
must not be summed. At the smaller size, the complete 600-second acceptance requires recovery within 120 seconds
and less than one second oldest delay throughout the final five minutes.
The discarded first setup enrolled dormant systems directly, exposing almost ten hours of historical deadlines;
its interrupted short run under `artifacts/bot-throughput/short` is not capacity evidence.

**Automated validation.** The focused demotion, missile-cache, belt-query, travel, resource, defense and ownership
checks passed during development. The broad capsuleer run was stopped at an existing presentation test that
expected Refresh to execute jobs; its identical failure was reproduced on unchanged parent `33ac8372`.
The corrected test checks presentation immutability followed by maintenance settlement. The subsequent exact
correction/defense filter passed 7 cases in 22.70 seconds including host startup/shutdown. A later ownership,
miner/hauler, resource, tier and defense filter passed 52 cases in 35.73 seconds including host startup/shutdown.
Final publication checks passed: **102/102 focused cases in 48.28 seconds** including host startup/shutdown,
tool and isolated solution restores, solution build with zero warnings/errors (23.21 seconds), both Python policy
suites (8 and 9 cases), formatting verification across 1,629 files, line-width, new documentation links/anchor,
UTF-8/final-newline, whitespace and diff checks. The exact final filter and retained TRX are
`artifacts/bot-throughput/final-filter.txt` and `final-validation.trx`. The isolated build initially lacked assets
in its new output directory; restoring into that directory resolved the setup failure. The final source/input
manifest is `artifacts/bot-throughput/published-source-manifest.json`, SHA-256 `87d487d9fe6198a7a2b37d0ee076a25209e5e4d79723d9020ad49a27c505be80`.
The final filter was:

```text
FullyQualifiedName~TrafficSnapshotTests|FullyQualifiedName~CapsuleerTrafficNavigationTests|FullyQualifiedName~CapsuleerHandoffTests|FullyQualifiedName~CapsuleerTierTests|FullyQualifiedName~CapsuleerTrafficPerformanceTests|FullyQualifiedName~SystemRuntimeIsolationTests
```

**Capacity limitation.** Intermediate sustained runs did not meet the recovery target. The first was interrupted
at 288.6 seconds with 5,001 scheduled pilots, 4,238 due jobs, 312,665.5 ms oldest delay and 13,532 completed actions.
After classified cold-read/publication changes, the next run reached 25,067 completions at 299.7 seconds, with
4,138 due jobs and 258,748.5 ms oldest delay before interruption. These are diagnostic runs, not a matched completed
600-second comparison. The second included a 15-second sampled-thread trace; its inclusive samples identified
membership resolution and template selection as substantial costs. The final committed-membership and immutable
candidate-pool changes followed that trace and have not completed sustained capacity acceptance. The 900-system
and full final-five-minute targets remain unverified. No full .NET suite or live game was run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** None required for the automated throughput comparison.

## Traffic ownership and market summaries, November 10, 2026

The supplied warnings occurred after the queue-throughput changes above. The 377.79 ms session tick
spent 372.04 ms waiting for ownership; the market request instead spent 222.06 ms in its handler,
with 35.19 ms of world wait. These inclusive phases overlap and must not be added. An “Included Trade”
owner label identifies a job executed during that hold, not its individual contribution.

The implementation retains atomic traffic settlement and the existing activity rate. Maintenance captures
an immutable expected job and catalog/resource/fitting revisions, prepares route and institutional offers
without ownership, and revalidates before applying it. Stale preparation does not advance the job.
Gate settlement includes both endpoints. Committed resource geometry is reusable inside a transaction;
pending resource writes use a private SQL view, and rollback cannot publish that view. Prepared missing
belts insert only absent deposits and preserve depleted rows and existing renewal deadlines. Belt ownership
is recorded before first insertion so preparing a new system does not evict unrelated committed geometry.

Resupply batches magazines, hangar requirements and eligible orders across types. Institutional offer
catalogs are immutable per catalog generation; successful preparation is published for the exact committed
EF transaction. Rollback, including rollback followed by another transaction on the same connection, cannot
publish a preparation marker. Actual purchases retain live stock, price, wallet, capacity and ownership checks.

Best-ask summaries share immutable committed results by station/system/region, catalog and settings identity,
with exact wall-clock expiry and active-time stock/growth deadlines. A fractional-tick-preserving immutable
activity-clock snapshot allows warmed reads without persistence ownership. Committed changes invalidate
relevant summaries; stock changes in other regions preserve warmed views. Bid-only changes do not invalidate best asks. Membership changes, deletion or
unknown identity retain conservative invalidation. Market-only transactions declare no simulation participants.
There is no client response-field or tie-breaking change.

The existing coherent checkpoint boundary is retained: resident capture, database copy, projection and file
write are measured separately, and persistence coordination spans the complete copy. Warmed market reads
can complete during a paused partial copy. Writers unwind resident ownership before waiting for persistence.
An internal projection/membership read no longer requests every system when making that handoff.
The full-image database copy can still exceed 250 ms; this change does not claim to remove that cost.

### Measured causes and replay method

The initial diagnostic replay exposed an additional cause hidden by the owner label: one `Collect` action
opened 11,006 connections, executed 11,184 SQL commands and allocated approximately 7.01 GB in 6,442.79 ms.
A separate opt-in SQL call-stack capture identified repeated `ObserveEncounterPopulation` and
`ObserveNpcPopulation` membership lookups. Unclassified NPC population transactions broadcast their changed
identities to every resident system. The transaction and its cold system reads now declare their actual system;
committed location-only invalidation reads do not project unrelated combat state.

The subsequent 120-second diagnostic replay had no slow traffic action records. After the first 30 seconds,
complete frame arrival latency was 21.95 ms p95 / 43.57 ms p99; `GetRegionBest` was 55.65 / 78.97 ms.
It had not cleared its initial backlog at 120 seconds, so it is not capacity acceptance. Its phase totals
identified repeated preparation capture as the next cost; recording ownership before missing-belt insertion
was added afterward. Short diagnostic results are not substituted for the required ten-minute comparison.

The sustained replay prioritizes the communities of all fifteen pilots in the supplied warnings, then fills
450/900 community cases using the previous near-fourteen-pilots rule. Actual scheduled counts are retained.
Requests arrive independently each second (every third request is `GetRegionBest`) and serialize only through
the simulated player's own queue. Checkpoints are scheduled independently every fifteen seconds. Reports retain
queue time, arrival-to-completion time, service/frame counters, owner holds, checkpoint stages and due-job age.
Cold setup/session samples and the first thirty seconds are reported separately from warmed percentiles.
Arrival latency includes producer lateness against the absolute request schedule. Backlog cases advance
the active clock seventy seconds before starting maintenance. The input is a frozen copied save; the installed
save and running server are not modified. `EDEN_BOT_REPLAY_CASE` selects one theory, `EDEN_BOT_REPLAY_BASELINE=1`
records baseline results without asserting optimized latency, and `EDEN_SQL_HOTSPOTS=1` enables the optional
query/call-stack histogram. Disable that histogram for capacity measurements.

The movement reference remains [Carbon's bubble updater at revision
114e89fa584f0e1a81eb0a679aabf41af8870072](https://github.com/carbonengine/destiny/blob/114e89fa584f0e1a81eb0a679aabf41af8870072/python/destiny/net/server/_bubbleupdater.py).
Gameplay references are [EVE University server ticks](https://wiki.eveuniversity.org/Server_tick) and
[EVE buy and sell orders](https://support.eveonline.com/hc/en-us/articles/203218932-Buy-and-Sell-Orders).
Movement, gameplay deadlines and market settlement policy remain unchanged.

The first complete candidate recovered in 118.39 seconds and kept sampled oldest due-job age below
331.55 ms in its final five minutes. Frames measured 26.36 ms p95 / 62.23 ms p99, but market requests were
134.38 / 176.12 ms and three traffic holds exceeded 250 ms (maximum 393.36 ms). This candidate failed
acceptance. Its query/call-stack traces and phase timings identified generated-name recognition in
`TrafficProvisioning.Refresh`: enumerating the occupied-name prefix allocated tens of megabytes per
check. Recognition now inverts the same name permutation and preserves the exact attempt bound.
Best-ask invalidation now resolves committed order locations once and ignores bid-only changes;
preparing one station no longer discards another station's committed supply preparation. Regional resupply
advances all requested types in one batch.

The candidate report also exposed an assertion-helper collision: another partial replay class had an
array overload that expected already-sorted samples. Sustained replay now uses a uniquely named sorting
helper, covered by a descending-arrival-order regression. All quoted candidate percentiles above were
calculated independently from retained request records; the failing owner-hold assertion was never treated
as completed acceptance.

### Closing measurements and remaining work

The closing 600-second steady replay used 5,071 scheduled pilots in 450 communities and completed
135,306 actions. Setup took 48.24 seconds; the separately recorded first frame took 719.67 ms and first
market call 237.81 ms. Warmed values below exclude the first thirty seconds and include lateness against
the absolute request-arrival schedule. Reports and the measured source manifest are retained locally under
`artifacts/traffic-latency/final/`; the frozen save is under `artifacts/traffic-stalls/frozen/`.

| Measurement | Closing result | Target/result |
| --- | --- | --- |
| Complete frame p95 / p99 | 34.62 / 52.92 ms | Pass: below 100 / 250 ms |
| `GetRegionBest` p95 / p99 | 126.70 / 153.42 ms | **Fail: p95 exceeds 100 ms** |
| Maximum warmed frame / market request | 83.38 / 210.35 ms | Pass: no one-second samples |
| Initial recovery | 119.41 seconds | Pass: within 120 seconds, with little margin |
| Maximum sampled oldest due-job age, final five minutes | 402.09 ms | Pass: below one second |
| Traffic-maintenance holds at least 250 ms | 3; maximum 383.38 ms | **Fail: must be zero** |
| Zero-SQL/connection/fitting warmed frame / market samples | 280/380 and 137/190 | Actual changes still require work |

The last 32 retained checkpoints had maxima of 7.88 ms resident capture, 142.49 ms database copy,
97.46 ms projection and 1,258.89 ms file writing. These are separate stages; writing remains outside
simulation ownership. A larger image or slower storage may still produce copies over 250 ms.

The remaining traffic holds were Trade for pilots 90021899 (275.56 ms), 90022677 (262.32 ms), and
90021743 (383.38 ms). Their phase records attribute the work to legacy template refresh/correction:
92.25 + 180.09 ms, 258.98 + 0.56 ms, and 357.04 + 23.08 ms respectively. Refresh still performs
cargo-dependent candidate selection and fitting settlement, and correction performs its own candidate
selection and fitting. Further preparation must preserve candidate ordering, current reservations,
capacity checks and the single atomic settlement. Slow-action messages now omit inclusive scheduler/action
wrappers from their four displayed phases so these inner costs are visible; full bounded phase records remain.
That final log-format change followed the replay and does not change simulation behavior.

Most remaining slow market requests have a warmed handler around 0.1 ms but spend approximately 92–191 ms
waiting in surrounding session updates during checkpoint access. One cold/invalidated summary also waits
on persistence. The warmed summary itself is copy-independent; the complete production session still has
periodic reads that propagate copy latency. Removing those reads requires dependency-specific resident
caches and must not skip due updates or alter gameplay timing.

**Acceptance is incomplete.** The replay failed its market-p95 assertion, and independent inspection also
found the three owner holds above. The requested 70-second backlog case, twice-load stress cases and matched
baseline/final ten-minute comparison were not completed before closeout. The earlier baseline used a different
community selection and is diagnostic evidence only. Neither the short replay nor the closing steady result
establishes the full capacity guarantee. Activity rates and acceptance thresholds were not reduced.

**Automated validation.** Tool restore and solution restore passed. The solution built without warnings or
errors. The affected .NET filter below passed all 646 tests in 245.81 seconds (build 1.26 seconds), including
stale preparation, duplicate callback, rollback/retry, gate visibility, depletion, resupply/payment, market
expiry/invalidation, populated-system isolation, and checkpoint reopen/retry coverage. Unchanged warmed
summary/observation tests assert zero SQL commands, connections and fitting calculations. The catalog Python
suite passed 8 tests in 0.72 seconds, the formatting-policy suite passed 9 in 0.07 seconds, and formatting
verification passed. The full .NET suite was not run. The sustained test failed as recorded above; its three
unselected theory cases return without running and are not counted as measured workloads.

```text
FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~Eden.Protocol.Tests.Market|FullyQualifiedName~CheckpointOwnershipTests|FullyQualifiedName~OwnershipTimingTests|FullyQualifiedName~SystemRuntimeIsolationTests|FullyQualifiedName~ResourceRenewalTests|FullyQualifiedName~TransactionParticipantTests|FullyQualifiedName~SessionLatencyTests|FullyQualifiedName~NpcPopulationTests|FullyQualifiedName~NpcEncounterTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~SustainedPercentilesSortArrivalOrder
```

The broad result is `artifacts/feature-tests/results/e3fdd32c25f34f1cafdfb1acf5122ea4/tests.trx`.
After the final slow-log formatting change, the exact filter
`FullyQualifiedName~CapsuleerMaintenanceTests|FullyQualifiedName~TrafficPreparationTests|FullyQualifiedName~OwnershipTimingTests`
passed all 22 tests in 11.63 seconds (build 16.30 seconds), with results in
`artifacts/feature-tests/results/fd0295b7ab9d48e3a6feb798bbc58b2c/tests.trx`; formatting verification passed again.
The closing replay result is `artifacts/traffic-latency/final/450-False.trx`; measurements are in
`throughput-450-False.json` and its independently calculated `.summary.json` alongside that report.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** After deployment, compare warning frequency and responsiveness on the same busy route.


## Resident remote bot routines (November 11, 2026)

This is the first resident-object migration in the requested order. The saved world already runs in an in-memory
SQLite image; this change removes SQL work from warmed ordinary remote navigation and routine settlement. It does
not claim that bot economic actions, initial hydration or cold catalog loads are SQL-free.

Remote work cursors, settled clocks, community activation and traveler admission now publish typed resident records
under the shared scheduler owner. Role actions validate the canonical job and cursor before commit. Economic mutations
retain their transaction; only after success are the next job, role progress and cursor published under the same owner.
They no longer serialize progress or update a job/cursor row merely to finish a live action. A stale cursor rejects the
commit and rolls back the economic mutation. Offline tooling retains its SQL job/progress contract.

Remote movement publishes the existing resident position object until an action actually opens a mutation. Scene
release evicts contacts, combat inputs and geometry while retaining compact positions and their dirty projection state.
An ensuing docking or transfer transaction projects the latest resident position before validating SQL membership;
subsequent movement within an open mutation uses that transaction's private view. Ordinary remote warp entry skips
NPC-disengagement SQL only while the existing background-safety checks establish that detail is unnecessary.

Checkpoint capture freezes admission and captures cursor/settlement/admission/activation arrays with the existing
immutable job/progress/position arrays and the same relational generation. All projection and file writing remain
outside system ownership. Batched routine projection uses the detached database's transaction, so a projection failure
cannot leave a partly advanced job or cursor. Successful file saves retain differences from the live SQL image, and
failed saves retain changes for retry. No schema migration or changes to saved timing, rates or gameplay rules are needed.
Legacy saved-row readers and transactions synchronize resident routines before using the SQL view. Classified
transactions use a system membership index and project only their participating pilots; they do not scan or flush
every bot or acquire unrelated system owners. Committed external cursor edits refresh the resident cursor before the next selection; rolled-back changes do not publish.

Reference checks: [EVE University trading](https://wiki.eveuniversity.org/Market) describes finite buy/sell orders and
wallet taxes; those economic semantics are retained. The local evejs reference identifies itself as `261108.1` in
`config/version.json`. Its `server/src/services/market/marketRuntimeState.js` delegates runtime updates to its game store
and offers no applicable Eden owner/checkpoint contract. The inspected file's SHA-256 is
`A848B21E2E2BF4FF8B6CF9846AB05BB09ACC606062F3FC15958A463AA9BBDD36`; the local reference has no Git metadata.
No source was copied, and wire formats, navigation formulas and Carbon-owned Destiny behavior are unchanged.

### Automated validation

Tool restore and solution restore passed. The closing solution build on top of main `c921bc9e` passed in
44.37 seconds with zero warnings/errors. The final focused run passed **88 tests** in **82.02 seconds**, including
its runner overhead (focused build 3.47 seconds). Both Python policy suites passed: catalog setup **8 tests / 1.384 s**
and formatting policy **9 tests / 0.020 s**. Formatting verification passed in **32.65 seconds**; no width exceptions were added.

The final filter was the OR of `FullyQualifiedName~` and each complete class name below:

```text
Eden.Protocol.Tests.Capsuleers.CapsuleerCoarseTests
Eden.Protocol.Tests.Capsuleers.CapsuleerResidentRoutineTests
Eden.Protocol.Tests.Capsuleers.CapsuleerTierTests
Eden.Protocol.Tests.Persistence.DetachedCheckpointTests
Eden.Protocol.Tests.Persistence.CheckpointOwnershipTests
Eden.Protocol.Tests.Persistence.MotionProjectionTests
Eden.Protocol.Tests.Combat.SessionPerformanceTests
Eden.Protocol.Tests.Agents.MissionTrackerTests
```

Before the final action-ownership guard and main integration, an expanded focused selection covered all
`Eden.Protocol.Tests.Capsuleers` plus `DetachedCheckpointTests`, `CheckpointOwnershipTests`, `MotionProjectionTests`
and `ResidentCombatMonitoringTests`. It completed **453 tests: 452 passed / 1 failed** in **341.65 seconds**
(focused build 6.43 seconds). The failure was
`PilotSpaceActivitiesTests.ShoppingChoosesRealAffordableStockAndHonorsThreatMemory(overpriced: true, avoided: false)`.
The same three-case method filter on unchanged main `c921bc9e` reproduced **2 passed / 1 failed** in 14.56 seconds:
its expected missing shopping route is present. No shopping implementation or expectation was changed in this migration.
The closing 88-test selection covers the final ownership guard and integration changes. The full .NET suite and
installed-save stress replay were not run.

Main's bookmarking change `2b785fe8` was then merged without source conflicts; the integrated solution build passed
in **11.43 seconds**. The additional filter selected `CapsuleerResidentRoutineTests`, `SessionPerformanceTests`,
`BookmarkServiceTests` and `ProbeScanningTests` with their complete namespaces. It completed **57 passed / 3 setup
failures** in **97.12 seconds** (build 2.26 seconds). The three failures shared a denied Windows staging-directory
rename in `TestWorldSeed.Load`, before gameplay assertions. A create/rename/remove probe passed in that cache directory.
`EDEN_TEST_TEMP_DIRECTORY=artifacts/test-data-resident-frames` isolated scratch data for the affected
`FullyQualifiedName~Eden.Protocol.Tests.Combat.SessionPerformanceTests` filter: **all 6 tests passed** in **12.35 seconds**
(build 2.45 seconds), including the previously blocked frame checks. No fixture implementation was changed.
Reports are preserved under `artifacts/resident-bot-state-20261111` in the primary checkout, including the baseline,
expanded selection, 88-test close, bookmarking integration attempt and isolated frame retry.

The subsequent main blueprint-supply update `44fc0db6` was also integrated without source conflicts. Its combined
solution build passed in **7.74 seconds**. The OR filter for `CapsuleerResidentRoutineTests`, the exact
`CapsuleerCoarseTests.RemoteArrivalsReuseFitsAndAvoidRepeatedMovementMembershipReads` method and `BlueprintMarketTests`
passed **all 11 tests** in **13.77 seconds** (build 1.62 seconds). The warmed remote movement assertion remains **0/0/0**.
Both Python policy suites and formatting verification were repeated after this final integration.

Development began with the exact method filter
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerCoarseTests.CoarseCursorAndJobsReopenWithoutOfflineProduction`.
The exact-method baseline for `RemoteArrivalsReuseFitsAndAvoidRepeatedMovementMembershipReads` at
`a9e0596b79d09360d066d42f02b9bb2cfb1df54d` used the same fixture, clock advances and two-role workload. Its warmed movement
sample issued **11 SQL commands / 1 connection / 0 fittings**; the resident version asserts **0 / 0 / 0**. This is a
counter comparison, not a claim about installed-save latency or total simulation capacity. Across the same 17 reported
role-action samples, commands fell **337 → 223**, connection opens changed **25 → 32**, and fitting calculations remained
**1 → 1**. Cold/projection boundaries are still measurable: mutation samples can open additional connections to
project retained positions before their transaction. Complete session-frame and checkpoint
checks are included in the affected test selection; checkpoint costs remain separate from simulation counters.

New regressions exercise a detached old generation while newer cursor/job/position state advances, atomic rollback of
failed cursor projection, retry across repeated successful saves, reopened cursors/jobs, stale resident cursor rejection
of an economic mutation, zero-SQL scene release, participant-only projection while an unrelated owner is held, and
immediate committed external cursor visibility with rollback isolation. Existing mining
and fitting/undocking rejection tests now fail the actual economic/resource mutation instead of relying on a progress
row that live simulation no longer writes.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

None for this state/persistence migration. Native rendering, input and gameplay feel remain unverified.

### Remaining resident migration

Inventory, ownership and economic transactions still use SQL, including bot resupply, ore payments/collection, cargo
moves, market fills, fitting, docking and gate membership. They must move to one shared typed resident transaction
owner, rather than a separate bot-only copy, with atomic wallet/order/escrow/item changes, rejection without partial
publication, coherent human/bot reads, destruction/transfer invalidation and reopened checkpoint coverage. Cold catalog
hydration remains separately measurable. The overall goal of eliminating every bot SQL call is not complete here.


## Resident inventory and market transactions (November 11, 2026)

This migration adds the shared resident economic transaction needed after the remote-routine work above.
Live public market commands, ordinary cargo/hangar moves and splits, stack merges, personal asset reads, wallet balance
reads and standalone capsuleer stock listings now use typed resident rows. It does **not** finish removal of every bot
SQL call: mixed role actions still use their SQL overloads for paid resupply/reloading, mining, fitting and travel.
Those actions must migrate as complete transactions; publishing a purchase before a later undock rejection would
change the established rollback behavior. The resident owner explicitly rejects opening an economic view inside an
existing SQL transaction. This is a validated shared-transaction milestone, not completion of that broader goal.

Each economic action owns private mutable materializations of immutable scalar snapshots. Related wallets, buy-order
escrow, listed stock, inventory ownership/quantities, tax and payment ledgers, execution history, task counters and
notification records publish only after every staged table validates. Rejected batches discard the entire view,
including staged identities. The inventory-owner registry and active-pilot/hull ownership constraints are checked before
publication; contract escrow stays excluded. Persistent high-water marks include inventory, ships, implants, boosters
and structures, including deleted newest rows and temporary split/merge identities.

The relational image remains a compatibility and checkpoint boundary. SQL transactions project earlier committed
resident changes before using them, then refresh only committed row identities; rollback never publishes speculative
SQL changes. Unrelated read-only SQL queries do not force economic projection. Resident commits publish membership
invalidations without querying SQL. Current combat magazine quantities overlay private reads, and economic scalar
changes preserve unrelated dirty combat health, energy and deadlines. Fitting transitions retain their complete SQL
boundary rather than partially applying the ordinary cargo implementation.

A captured economic image and sequence array share the same generation as the relational copy, remote routines and
combat state. Economic projection, followed by newer combat deltas, runs in the detached checkpoint transaction outside
the world owner. A failed projection rolls back its complete image. A failed file save retains dirty state and a later
save captures the newest committed generation. Successful file saves do not acknowledge changes absent from the live
SQL image, so later compatibility reads and repeated saves remain coherent.

Queries push complete scalar predicates into explicit snapshot indexes before materializing private entities. Small
predicates are interpreted instead of compiling fresh IL for each request. Open-order expiration and acknowledged
market-change heads have resident indexes, so idle polling does not rebuild order/history objects. Tests with 1, 100
and 1,000 unrelated stacks materialize just the selected row. Primary-key queries use the main row map, and secondary
indexes allocate a shared-value set only when a key has multiple rows; unique keys do not create per-row hash sets. This is an allocation assertion, not a claim that every
unindexed filter has constant CPU cost.

Reference checks: [EVE University trading](https://wiki.eveuniversity.org/Trading) was consulted for orders, escrow,
broker fees, cancellation and fills. Eden's existing typed rules and build-3662168 wire behavior remain authoritative;
this migration changes no rates or eligibility. The local evejs reference reports version `261108.1`; its
`server/src/services/market/marketRuntimeState.js` stores runtime cursors through its game store and does not define
Eden's owner/checkpoint contract. Its inspected SHA-256 is
`A848B21E2E2BF4FF8B6CF9846AB05BB09ACC606062F3FC15958A463AA9BBDD36`. No reference source was copied.

### Matched warmed economic replay

The baseline is unchanged `0d778f76c0eedd3b8e48223b7147fc0fb8bcb765`. Both versions use the same injected test source,
fixture, clock, resident SQLite world, listing, buyer budget and 40 one-unit purchases. Each complete economic frame
includes the trade and one or four inventory, balance and personal-order reads. Both finish with 41 purchased rounds
and 59 listed rounds. Builds are separate from timing; the final comparison executes the versions sequentially.
This small deterministic workload measures shared economic commands, not whole-server bot capacity or installed-save
stress. An initial replay exposed a slower expression-compilation implementation; it was corrected before acceptance.

| Readers per frame | SQL commands, baseline → resident | Connection opens, baseline → resident | Fittings | Mean frame ms, baseline → resident | p95 ms, baseline → resident | Allocated bytes, baseline → resident |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 1,000 → 0 | 240 → 0 | 0 → 0 | 5.383 → 1.536 | 6.166 → 2.182 | 32,933,128 → 10,717,832 |
| 4 | 1,360 → 0 | 600 → 0 | 0 → 0 | 9.859 → 2.575 | 15.738 → 2.658 | 58,264,192 → 18,555,640 |

Checkpoint work is reported separately. In these samples, resident capture/copy/projection/write took respectively
0.214/0.534/2.456/72.542 ms (one reader) and 6.196/0.371/24.562/39.764 ms (four readers). The matching baseline samples
were 0.047/0.374/0.255/63.259 ms and 3.821/0.420/3.762/41.462 ms. Resident detached projection issued 19 commands;
baseline economic rows had already been written during the frames. First-use projection and file-write variance are
visible here; no checkpoint throughput improvement is claimed. Both frame and save costs need remeasurement on the
installed population before assigning a supported larger bot count.

### Automated validation

Development began with the exact method filter
`FullyQualifiedName=Eden.Protocol.Tests.Market.ResidentEconomyTests.RejectedPaymentDiscardsEveryStagedInventoryAndWalletChange`.
Focused regressions cover rejection without partial payment or stock, competing buyers, escrow price improvements,
order modification/cancellation/expiration, remote stock and blueprints, ownership rejection, cargo capacity,
split/merge rollback, committed SQL visibility, old-generation snapshots, failed projection/file-save retries,
reopened state and sequence high-water marks. A warmed fill/cargo/read workload asserts zero SQL commands, connection
opens and fitting calculations with one and four readers. Full .NET regression and live-client acceptance are not run.

Closing tool/solution restore passed. The integrated solution build on top of main `7fbf0592` passed in **2.21 s**
with zero warnings/errors. The affected-class selection passed **239 tests** in **156.66 s** including runner overhead
(focused build **1.31 s**). Its exact OR filter is preserved in
`artifacts/resident-economy-20261111/focused-filter.txt`; it selects the resident economy, personal/remote/blueprint/
industrial/testing/history market classes, inventory moves/merge/split/ownership and personal assets, detached and
concurrent checkpoint owners, complete session frames, resident combat/fitting observers, capsuleer miner/hauler/
routine/supply/defense frames, bulk industry delivery and personal mining-ledger integration. Other space-activity
methods are included; the previously recorded unrelated `ShoppingChoosesRealAffordableStockAndHonorsThreatMemory`
method is excluded and was not rerun. This does not replace a full regression check.

The catalog policy suite passed **8 tests / 0.871 s** and formatting policy passed **9 tests / 0.015 s**. Formatting
verification passed, including **1,740 CSharpier files / 2.896 s** and the 140-column policy; total style-check elapsed
time was not captured. No width exceptions were added. Two final replay cases also passed; their separate benchmark
build took **7.60 s** and run **6.81 s**. Replay source, targets, summaries and reports are preserved with the focused
reports under `artifacts/resident-economy-20261111`. Earlier setup, query and null-index failures were corrected before
these closing runs. The shared transaction slice is merged separately from the outstanding mixed bot-action migration described above.

The merge integration includes main `d45c1d7d` (shared bookmarks/access lists and structure compression). Only the
protocol/documentation indexes conflicted; both records were retained. Tool and solution restore passed, and the
integrated solution build passed in **47.54 s** with zero warnings/errors. The OR filter for `ResidentEconomyTests`,
`PersonalMarketTests`, `PilotInventoryTests`, `InventoryOwnershipTests`, `PersonalAssetServiceTests`,
`DetachedCheckpointTests`, `SessionPerformanceTests`, `StructureCompressionTests`, `SharedBookmarksTests` and
`SharedBookmarkServiceTests`, using their complete namespaces, passed **105 tests / 57.77 s** (focused build **1.86 s**).
Catalog policy passed **8 tests / 0.944 s**, formatting policy passed **9 tests / 0.110 s**, and formatting verification
passed for **1,758 files** with no width exceptions. Combined policy/formatting elapsed time was **25.13 s**.
The integration report is preserved as `artifacts/resident-economy-20261111/merge-integration.trx` in the primary checkout.
No full .NET regression or installed-save stress run was performed for this merge.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

Compare a fixed installed-population workload before assigning a larger supported population. Native rendering,
input and gameplay feel remain unverified.

## Resident bot action transactions (November 11, 2026)

Live miner and hauler actions now stage their inventory, market payments, supply fitting, skill grants, ammunition,
mining deposits, capacitor costs, paid cooldowns, mining ledger entries and loot transfers in one private resident
view. Validation precedes publication. A later rejection discards the complete action, including its identity
reservations and sequence allocations. The scheduler checks the expected job and routine cursor before publishing
that action and its new progress under the same owner. Offline tools retain an adapter to their existing SQL
transaction.

These actions use explicit scalar snapshots for their additional saved inputs. Current combat resources and paid
heat are captured into the private view; their old dirty baselines are acknowledged only after commit. Coordinates
retain the movement owner's current value, while docking and gate commands own membership. A retained movement
baseline cannot restore an earlier system after a gate transfer. Both endpoints remain in the publication scope.
The relational image, economic changes, movement, jobs and routine cursor are captured from the same generation;
SQL projection and file writing remain outside the owner. Failed projections retain the resident state for retry.
Legacy SQL reads synchronize the economic image before projecting overlapping combat rows. SQL dependency checks
match whole table identifiers: reading `WorldNpcEwar` must not project `WorldNpc` and every retained position.

### Template refresh and resident reads

Pilot name changes now commit with the fitted hull, trained skills and traffic identity. Pilot appearance and
portrait bytes remain private copies and are projected as BLOB values. Invalid account, name or active-hull
ownership rejects the action. Name and imported-character reservations use resident indexes, including staged
changes; an unrelated pilot's appearance is not a template-selection input. Roster selection first restricts agents
to the system and career and then reads only the selected hull identities.

Resident scalar projections and aggregates operate on immutable snapshots when no linked or mutable-array data is
required. Array and navigation projections retain private materializations, as do item and coordinate reads that
need combat/movement overlays. The warmed template regression includes 64 unrelated pilots, each with 256 KiB
appearance and 256 KiB portrait payloads. It asserts that only the refreshed pilot is materialized, no SQL command
or connection is opened, and the complete refresh allocates less than 8 MiB. Commit and rollback both preserve the
expected hull, name, identity and original appearance bytes when the captured database is reopened. This is a
bounded automated workload, not a measured larger-population capacity claim.

The gameplay references remain [Mining](https://wiki.eveuniversity.org/Mining) and
[Trading](https://wiki.eveuniversity.org/Trading). No gameplay rates, cycle eligibility or Destiny movement formulas
change in this storage migration.

### Automated validation

Development started with the exact method filter
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerResidentRoutineTests.StaleResidentCursorRejectsAnEconomicCommitWithoutPublishingProgress`.
Additional exact checks covered complete miner save/reopen/unload and hauler gate travel. New tests assert SQL-free
paid collection with unchanged deadlines after rollback, duplicate-collection rejection, coherent captured deposits
and cooldowns, and template refresh with unrelated portrait payloads. Undock rejection stages a real fit and launch
before rejecting the transaction; failed checkpoint projection is tested on a detached image and cannot replay a
committed collection. Existing handoff and remote-arrival tests cover the current coordinate and membership bridge.

Tool and solution restore passed, followed by a solution build with zero warnings/errors in **6.55 s**. The closing
selection passed **502 tests / 215.27 s** including runner overhead. The exact OR filter is preserved in
`artifacts/bot-state-migration/closing-filter.txt`; it selects Capsuleers, ResidentEconomyTests, FittingSnapshotTests,
MiningTests and AmmunitionTests by their complete namespaces and excludes the previously documented unrelated
`ShoppingChoosesRealAffordableStockAndHonorsThreatMemory` method. No full .NET suite was run.

The warmed portrait-payload fixture allocated **2,740,992 bytes** for commit and **2,595,576 bytes** for rollback,
with zero SQL commands/connections in both cases. Its SQL image preserved the pilot's original binary appearance
and portrait. These numbers describe that fixture, not a replay of the installed population in the reported log.
Catalog policy passed **8 tests / 1.159 s** and formatting policy passed **9 tests / 0.016 s**. Formatting verification
passed for **1,865 files / 3.488 s** and the 140-column policy; complete style-check elapsed time was not captured.
Reports are preserved under `artifacts/bot-state-migration`.

This milestone removes SQL from the warm mixed economic/production actions above. Fatal combat settlement,
community admission and several legacy effect stores still retain their SQL boundaries; it does not claim that
all simulation paths are SQL-free.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

Repeat the installed-save trade workload to measure owner hold time and allocation after loading the new build.

## Resident fatal NPC settlement and filtered lookups (November 11, 2026)

Live fatal NPC turret volleys now import the participating dirty ship, NPC, aggression and contribution snapshots
into one private resident action. Ammunition, capacitor, cooldowns, heat, health, destruction, wreck, loot, combat
report, bounty, corporation tax, silent free skill points, security reward and standing changes publish only after
validation. Mission structures, mission waves and anomaly waves use typed versions of their existing rules.
Rejection preserves prior paid volleys for the next coherent checkpoint. Successful publication acknowledges only
the imported generation; checkpoint projection still uses the detached image and can retry after failure.

NPC hydration, guard membership and aggression loading inspect resident rows when no SQL transaction is active.
Inspection does not publish imported state or invalidate another dirty combat baseline. Participant membership
uses resident ship/NPC/wreck rows once loaded. Compatibility SQL readers of health, energy and cooldowns synchronize
prior resident payments before reading, including grouped reload-to-cycle transitions. Anomaly cache invalidations
publish after commit. Resident unique-index checks and mapped foreign-key cascades preserve rejection behavior.

The later installed-world report identifies revision `164186f0`: its worst recorded trade took **7,942.69 ms**,
issued **295 SQL commands**, and allocated **5,653,409,304 bytes**. That is evidence of an incomplete migration.
Predicate terminal operators such as `Single(predicate)` were materializing the entire resident source before
filtering. The query adapter now filters before materialization, preserving staged-row visibility and terminal
semantics. The portrait-payload regression now exercises predicate aggregates and `Single(predicate)` as well as
template refresh; only the selected pilot may materialize and allocation must stay below 8 MiB. Initial canonical
hydration also runs before production listeners and bot maintenance, rather than on the first bot action.

References: [Missions](https://wiki.eveuniversity.org/Missions),
[Salvaging](https://wiki.eveuniversity.org/Salvaging), and
[NPC mining operations](https://wiki.eveuniversity.org/NPC_Mining_Operations). Existing gameplay calculations remain
unchanged; these changes concern storage and publication.

### Automated validation

Development used exact fatal-shot, grouped-reload, template-roster, corporation-tax and role-reward method filters.
Fatal-shot tests cover warmed zero SQL commands/connections, rejection with unchanged payment and NPC state, retry,
duplicate rejection, reward/history outcomes, and failed checkpoint projection followed by successful retry without
replay. Resident mission fixture kills now use typed actions. Military role rewards and corporate tax settlement
exercise both adapters. The closing filter is recorded in `artifacts/bot-combat-migration/closing-filter.txt`.

Tool/solution restore passed. The solution build passed with zero warnings/errors in **7.66 s**. Closing validation
passed **774 affected tests / 364.94 s** including runner overhead. Catalog policy passed **8 tests / 1.008 s**;
formatting policy passed **9 tests / 0.015 s**. Formatting verification checked **1,892 files / 3.287 s** and the
140-column policy. No full .NET suite was run. Reports are under `artifacts/bot-combat-migration/validated`.

The widened portrait fixture allocated **2,654,256 bytes** for rollback and **2,875,616 bytes** for commit, with zero
SQL commands/connections. This is a bounded regression fixture, not a replay of the installed population.
An earlier widened run was deliberately stopped after seven failures and 568 passes; it is incomplete. Those
failures exposed aggression/cooldown compatibility synchronization and changed SQL-count expectations. The corrected
boundaries passed 31 focused checks before the complete 774-test closing selection above.

The full no-SQL bot goal remains open. Background population preparation/admission, scheduler preparation and
several combat loss/effect paths still have SQL boundaries. The reported installed-world workload has not been
replayed on this revision; no larger supported simulation size is claimed.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

Repeat the reported installed-save trade/collection workload after rebuilding this revision, comparing owner hold
time, SQL commands/connections and allocation. This is required to establish the live improvement.

## Resident detail retirement and drone publication (November 11, 2026)

System demotion now publishes its captured dirty combat generation into the canonical resident image before
releasing detailed objects. Eligible shared-owner detail trimming follows the same path. Rejection keeps the
original dirty records; active interaction, unfinished missiles and speculative economic actions still prevent
unsafe eviction. Compact movement coordinates remain immediately visible and join the next detached checkpoint.

Resident publication now covers drone position, velocity, health, control/delegation, paid shot deadlines, offensive
and incoming trigger revisions, and player timers, engagement and hostility records. Acknowledgement compares each
captured immutable record with its current owner. Drone hydration, controller membership, qualifications and trigger
reads use typed resident views. Unchanged control membership retains its fitted profile and inputs when publishing
motion/health, avoiding fit reconstruction and a global controller rebuild on the next tick.

Scoped publication also preserves other pending aggression entries and invalidates contribution inputs by victim.
The two-NPC regression reproduced a checkpoint capture failure when publishing one NPC cleared the pending map for
a different dirty NPC; it now retains both the other shot's payment and aggression. Committed coordinate changes
refresh cached ship positions directly from the canonical image for both resident and SQL compatibility commands.
The existing delegated attack range regression verifies the updated position on the following shot.

The gameplay reference is [Drones](https://wiki.eveuniversity.org/Drones). Existing pinned profiles, control limits,
range checks, aggression decisions, motion and damage calculations remain unchanged.

### Automated validation

Development began with exact motion-demotion, paid-drone publication, scoped-NPC publication, delegated-range and
trigger-revision method filters. Tests assert zero SQL commands/connections and zero fit calculations for warmed
handoff and paid-drone publication plus the following tick. Invalid private coordinates reject the complete image
without clearing dirty work. Reopened checkpoints preserve coordinates, damage, ammunition, aggression and paid
state. The closing filter is recorded in `artifacts/bot-resident-handoff/closing-filter.txt`.

Tool and solution restore passed. The solution build passed with zero warnings/errors in **5.69 s**. Closing
validation passed **475 affected tests / 254.27 s** including runner overhead. Catalog policy passed **8 tests /
0.979 s**; formatting policy passed **9 tests / 0.014 s**. Formatting verification checked **1,901 files / 3.366 s**
and the 140-column limit. Reports are under `artifacts/bot-resident-handoff/validated`. No full .NET suite was run.

An earlier selection was deliberately stopped after one failure and 66 passes and is incomplete. It exposed a
missing trigger-table compatibility synchronization check. A subsequent 379-case selection had 378 passes and
exposed the delegated-range stale-position issue; the new two-NPC test independently reproduced the discarded
aggression-map failure. The corrected cases passed targeted checks before the complete closing selection above.

This removes a recurring SQL projection from remote detail retirement. Background preparation/admission, scheduler
metadata and several initial combat/effect/destruction operations still retain SQL paths. The installed-world log
has not been replayed on this revision; no live latency or supported-population improvement is claimed.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

Repeat the reported installed-save trade/collection workload after rebuilding this revision; compare action and
owner hold time, allocation and SQL command/connection counts.

## Resident scheduler metadata and remote preparation (November 11, 2026)

Scheduler enrollment, incremental population refresh, community/agent summaries and preparation capture now read
typed resident tables while the server is running. Stopped-world tools retain the SQL adapter. Enrollment resolves
only the selected pilots and their active hulls instead of joining the complete pilot roster, including portrait
payloads. Metadata inspections reuse an existing private action view without committing it or clearing pending
physical work.

Scheduler recreation merges saved coarse cursors, settlements, admissions and activated systems with current
resident work. It cannot replace a newer unprojected cursor with its older relational image. Suspended and resumed
deadlines use the authoritative activity clock. Detailed miner restoration preserves the previous valid-JSON and
defense-object selection rules.

Remote coarse jobs now take the existing preparation path before entering the world owner. Geometry, template,
market and available journey preparation can run outside ownership. Publication rechecks the job, location,
catalog/input revisions and the coarse cursor generation; a concurrent settlement invalidates old preparation.
One coarse action still publishes its economic changes, deadline and cursor together.

### Automated validation

Exact-method development validation began with
`PopulationMetadataRefreshLeavesUnrelatedResidentMotionDirty`. New resident population tests cover private-view
visibility, rollback, committed metadata, reopened checkpoints, scheduler recreation with pending admissions and
stale coarse preparation. Warmed metadata/recreation/preparation checks assert zero SQL commands, connections and
fitting calculations. The immediate 65-case selection passed. The final filter is recorded in
`artifacts/bot-resident-metadata/closing-filter.txt`; it selects capsuleer behavior and immediate checkpoint/economy
dependencies, excluding the previously reproduced baseline shopping test.

Tool and solution restore passed. The final solution build passed with zero warnings/errors in **6.26 s**.
The final affected selection passed **478 tests / 197.10 s**, including runner overhead. Catalog policy passed
**8 tests / 1.010 s** and formatting policy passed **9 tests / 0.016 s**. Reports are under
`artifacts/bot-resident-metadata/stable`. Formatting verification checks the pinned formatter and 140-column limit.

The first four-case development selection had three passes and one fixture assertion failure: a completely
rolled-back action correctly produced no changed snapshot. The test now explicitly requests a new snapshot before
checking unchanged saved rows. An earlier 478-case run also passed before the final private-summary cache and
resident journey-preparation refinements. No full .NET suite was run.

This removes scheduler metadata SQL and the remote preparation bypass. Live population preparation/admission and
several initial combat/effect/destruction boundaries still need migration. Cold catalog queries contribute to the
existing combined SQL counters. The attached installed-world log was from revision `164186f`; it has not been
replayed with these changes, and no installed-world latency improvement is claimed.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

Repeat the reported remote trade/collection workload after rebuilding this revision; compare action allocations,
SQL/connection counts and world-owner hold time.

## Resident population admission and owned blueprints (November 11, 2026)

Live community and gate-visitor admission now stage pilots, accounts, inventory owners, hulls, skills, owned items,
wallets, home stations, role progress and scheduled jobs in one resident action. The existing world owner serializes
the action; validation precedes publication. Failed actions discard identity reservations and do not register a
speculative ship in the runtime membership maps. New pilot membership is registered after its canonical rows
publish. New hull graphs retain their supplied flight-position child in the same action.

Background preparation captures selected community members and scalar identities from resident state. Candidate
selection uses the captured reservation/roster view outside ownership, then refreshes reservations before commit.
Prepared blueprints copy immutable validated skills, cargo, modules, cooldowns, health and capacitor through typed
rows without world SQL. Stopped-world provisioning keeps its SQL adapter. Account username uniqueness and clone
grade checks are enforced before resident publication; host authentication behavior is unchanged.

The existing encounter openings and inbound gate-transfer behavior are preserved. Background preparation warms
the task and security catalog dependencies of the community's routes before admitting a founder, so an immediate
in-space opening does not load those catalog records while holding the world owner.

### Automated validation

Development began with the exact receiving-only inbound-transfer method, including pending-transfer restart.
Resident tests cover SQL-free blueprint copying and complete warmed background batches, including zero connection
opens and fit calculations. They assert private graph visibility, unchanged runtime membership before commit,
rejection and ID reuse, single grants on retry, original encounter openings, and reopened checkpoint contents.
A failed detached founder projection rolls back the complete saved image and a later capture retries both pilots.

The focused 14-case admission/background selection and the additional failed-projection case passed. Initial
development checks exposed the missing home-station mapping and cold movement-catalog inputs. SQL-trigger fault
injection remains on the SQL adapter; resident rejection is exercised against private actions and detached saves.
The closing filter is recorded in `artifacts/bot-resident-admission/closing-filter.txt`, with reports under
`artifacts/bot-resident-admission/corrected`. Tool and solution restore passed. The final solution build passed with
zero warnings/errors in **2.78 s**. Closing validation passed **875 affected tests / 406.35 s**, including runner
overhead. Catalog policy passed **8 tests / 0.984 s** and formatting policy passed **9 tests / 0.029 s**. Formatting
verification checks the pinned formatter and 140-column limit. No full .NET suite was run.

An earlier affected selection was deliberately stopped after **415 passes and one failure** and is incomplete.
A changed opening delayed early station contacts in the fresh-traffic fixture. The original opening behavior was
restored, movement catalog preparation was moved outside ownership, and all five targeted traffic/admission cases
passed before the closing selection.

Initial catalog reads and the isolated static blueprint-validation image can still use SQL outside the world owner.
They are separate from live saved-world mutation. Initial combat/effect/destruction boundaries also retain SQL
paths; this record does not claim the entire migration is complete or an installed-world latency improvement.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

Repeat the reported installed-save workload after rebuilding; compare warmed admission and trade allocations,
world-owner hold time, SQL commands and connections.

## Resident repair, propulsion and timed defenses (November 11, 2026)

Shield boosts, local armor/hull repairs, capacitor injections, hardeners, support modules, propulsion and temporary
signature/assault defenses now use resident actions while the server is running. Fuel, capacitor, paid completion,
cooldown, active effects and fitting changes publish together. Stopped-world operations retain the SQL adapter.
An unfitted repairer includes the hull of its earlier paid cycle among the action participants before reuse.

Local repair schedule reads and timed-defense membership reads resolve selected module identities rather than
joining complete world inventory. Fitting, module, skill-bonus and signature reads use current resident revisions
once loaded, including configurations with optional combat/read caches disabled. A projected SQL revision cannot
keep an already expired resident bonus alive in those fallback reads.

Timed bonuses have resident deadline indexes globally, by system and by ship. Activation, removal and committed
membership changes update them with the canonical generation; rollback leaves the index unchanged. Empty-system
checks do not scan other systems' modules. Expiry removes only effects due at the current clock and preserves the
paid reactivation cooldown. The system-index directory permits concurrent independent system reads. Only the
current persistence owner skips recursive expiry inside its own action; an unrelated transaction cannot make
another system publish an already expired cached bonus. Heat profile hydration also uses resident inputs after
initial loading. Paid heat
registration stays in the activation action; subsequent module burnout still retains its existing SQL boundary.
Participant heat capture resolves each named ship directly instead of repeatedly copying its system's dirty heat
list during multi-ship work.

The existing capacitor, fuel, timing and stacking rules are unchanged. Gameplay reference:
[EVE University tanking](https://wiki.eveuniversity.org/Tanking), reconciled with the existing catalog-backed rules
and automated fixtures. Shields restore at activation; local armor repair retains its funded completion deadline.

### Automated validation

Development started with exact shield-payment and armor-completion methods. Added warmed checks assert zero SQL
commands, connections and fitting calculations for a boost plus rejected repeat, and for paid repair through its
exact completion and a later duplicate read. Reopened checkpoints preserve health, capacitor, completion and
cooldown. The resident deadline check covers rejected/committed system transfers, 256 empty-system frames,
SQL-free expiry and preserved cooldown. Optional-cache-disabled and signature-consumer expiry checks also passed.

Two earlier affected runs were deliberately stopped and are incomplete: **127 passes / 47 failures**, then
**228 passes / 2 failures**. The first exposed array-expression compatibility in newly resident queries. The next
exposed fallback reads reusing an older SQL fitting revision after canonical expiry. Targeted corrected checks
passed before closing validation. Development measurement also identified a fixture property performing SQL and
an unwarmed standalone fitting cache; those fixture reads now happen before the measured calls.

A subsequent affected selection was stopped after **330 passes / 3 failures** and is incomplete. Two propulsion
SQL-trigger tests were moved to detached checkpoint failure/retry checks, while tackle retains its current SQL
adapter checks. The remaining catalog-admission assertion also failed on an untouched `1746b2f5` archive; that exact
baseline test is excluded, together with the previously reproduced shopping baseline. Corrected checkpoint and
offline-stop checks passed before the final selection. No unrelated catalog behavior was changed.

A later selection was stopped after **438 passes / 10 failures** and is incomplete. Nine failures exposed committed
heat publication advancing an invalidated prior image through an already completed action. Publication now installs
the committed source under the held owner before subsequent advancement. The remaining local ancillary-repair
SQL-trigger check now exercises detached checkpoint rollback and retry, retaining fuel payment, capacitor, heat
and the funded completion together. All **43 repair/remaining-module heat checks passed in 32.60 seconds** after
these corrections.

A further selection reached **946 passes / 1 failure** before being stopped. The remaining shield-boost test now
retains its stopped-world SQL rejection case and adds resident checkpoint rollback/retry coverage for paid health,
capacitor, cooldown and heat. Both exact corrected methods passed in **7.12 seconds**; this correction changed the
test boundary only.

The complete affected selection then passed **1,481 tests in 8m46s**. Subsequent concurrency review added a regression
that failed against the global active-action skip and passed after expiry checks were limited to the current owner.
All **195 affected expiry, system-frame, bot-defense and checkpoint checks passed in 1m10s** against this final
correction. The final warning-free solution build took **0.89 seconds**. Tool/solution restore, both Python policy
suites (8 catalog and 9 formatting cases) and formatting verification passed. Filters and results are recorded
under `artifacts/bot-resident-modules`.
No full .NET suite was run. Initial missile, remote-effect, loss-settlement and burnout operations still retain SQL paths;
the installed-world workload has not been replayed and no supported-population or live-latency gain is claimed.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

After rebuilding, repeat the reported workload and a bot defense cycle; compare world-owner hold time, action
allocation, SQL commands and connections.

## Resident monitoring indexes and cold scene reads (November 11, 2026)

Missile presence, impact and pending-report guards now read incremental resident indexes rather than rebuilding
the complete missile table after a change. Snapshot/coordinate-only updates do not rebuild those indexes. NPC
history cleanup keeps its exact strict boundary, including the one-tick transition after the retention interval.
Unknown-pilot rejection uses resident identity membership instead of loading character attributes, implants,
boosters and learning state merely to establish existence.

NPC effects have separate target and source versions; tackle and player effects have target versions. Immutable
contact lists and absent results survive unrelated changes. Queries materialize only the selected target/source
rows. Private action reads see their own changes without publishing speculative caches; rollback preserves the
earlier shared view. Loaded identity and work indexes also follow committed legacy SQL synchronization.

Cold NPC motion membership and tackle inputs now come from resident tables. Warp task progress joins a resident
action for the named ships before their motion publishes. Founder enrollment, trade availability and gate-watcher
identity/position lookups use resident inputs. Resident drone arrivals no longer query the saved drone table just
to invalidate observer membership. A missing runtime ship mapping can also resolve from loaded hull/position data.
Unscoped drone advancement, control and contact reads resolve the pilot/ship/system owner consistently; a shared
runtime must not retain updates while crimewatch/health readers consult a different system runtime.

Deadline index hydration now resolves memberships after all tables load: extra bot tables are registered before
base hull and position tables. Reopening an initialized checkpoint image must not strand an active deadline under
system zero. Host startup still separately removes connection-owned bonuses; this does not change disconnect or
restart gameplay policy. Paid cooldowns retain their existing behavior.

Gameplay references: [EVE University electronic warfare](https://wiki.eveuniversity.org/Electronic_warfare) and
[missile mechanics](https://wiki.eveuniversity.org/Missile_mechanics), reconciled with the existing catalog-backed
fixtures. This changes storage/observation, not weapon application, effect strength, range or payment rules.

### Automated validation

The exact `ResidentMonitor` selection initially reproduced four SQL-read failures with two existing checks passing.
Corrected checks cover actual missile launch/impact/report commits, rejected private changes, effect removal,
unrelated-view retention, unknown identity rejection, the exact history boundary and cold scene reconstruction.
They assert zero SQL commands, connections and fitting calculations after input hydration. Founder metadata also
exercises private/rejected/committed removal and reopened checkpoint rows; gate monitoring measures its cold lookup.

An initialized-image deadline hydration check failed on an untouched `94100006` source archive and passed with the
post-load membership resolution. This case deliberately omits the host's distinct disconnect reconciliation.
An expanded development selection had 9 passes and one fixture failure: its scene assertion addressed the shared
runtime rather than the system runtime being advanced. The corrected 10-case selection passed. A further player
effect check corrected its fixture lookup to the module's single-column key and passed. Motion tests now require
zero cold tackle queries/SQL/connections rather than requiring the old positive SQL cost; the resident warp case
also verifies alignment/cancellation, two distinct entries, duplicate reads and checkpointed history.

A broader selection was stopped after **242 passes / 1 failure** and is incomplete. Drone eligibility observed
53 commands instead of its warmed one-query budget; the exact check passed in isolation on both revisions.
Its standalone fixture now retains the catalog generation through the same lifetime used by the host. All
16 focused eligibility/catalog retention checks then passed. The production catalog lifetime was unchanged.

A later selection was stopped after **80 passes / 2 reported failures** and is incomplete. Removing an incidental
drone observer SQL read exposed split shared/system runtime ownership, including stale crimewatch deadlines.
The corrected routing passed all four exact drone damage/timer/checkpoint cases, including a warmed zero-SQL
observer workload and detached snapshot rollback/retry across subsequent advancement.

Two drone-focused selections were stopped and are incomplete: **89 passes / 1 failure**, then **207 passes / 1
failure**. System departure now rejects/cancels a delegated member before hydrating a ship through the wrong system
owner, and logoff-deadline observation uses the requested system owner. Cold drone-player metadata also reads
resident identity, position, engagements and timers. The 20 membership/departure variants and 8 continued-attack/
logoff variants passed after correction, including exact expiry without resurrecting a late shot.

A subsequent broader selection was stopped after **134 passes / 1 failure** and is incomplete. The equipped-drone
observer trace identified a cold spatial/cloak status lookup forcing projection of dirty drone rows. Spatial
navigation now has a resident mapping and status/membership reads resolve the selected ship/module directly.
Both exact omnidirectional-link variants passed, including zero-SQL warmed bay/space observers and paid-cycle expiry.

Another broad selection was stopped after **737 passes / 2 failures** and is incomplete. Both logistics hull
availability assertions failed identically on the untouched `94100006` source archive; their catalog admission
behavior is outside this storage change. Remaining affected classes exclude those two known baseline cases and
reuse completed results from the unchanged build. One continuation was interrupted for filter review after 16
passes; it is incomplete and is not counted as a successful closing run. The next continuation stopped after
**327 passes / 1 failure**: another hull-availability assertion also failed identically on the untouched source
archive. Its unchanged-state fitting rejection checks were not altered. The remaining selection excludes that
known baseline method as well and retains passing results from the same unchanged build.

A further continuation stopped after **730 passes / 1 failure** and is incomplete. The scene contact-count
assertion also failed on the untouched archive: it inspected the shared runtime while contacts belonged to the
system runtime. The corrected fixture retains all contact identity, bounded preparation and zero-SQL assertions;
all five scene checks passed in **7.82 seconds**. No production change was needed for this fixture correction.

The reported run and the existing Release executable both identify revision `164186f`; they predate these later
resident fixes. The running server and its Release output were left untouched. A separate Release build is for
maintainer acceptance; source validation does not establish the installed-world latency or SQL counts.

Closing coverage totals **2,747 distinct passing affected tests**, retaining completed results from the unchanged
production build and the corrected scene fixture. The last **996-case continuation passed in 8m02s**; no selected
case is missing from the combined reports. The three newly confirmed baseline hull cases are excluded in addition
to the previously documented shopping and mutadaptive catalog-admission cases. Failed/aborted selections above
remain incomplete; these numbers do not describe a single uninterrupted passing run.

The final solution build passed without warnings in **1.21 seconds**. Tool/solution restore, both Python policy
suites (8 catalog and 9 formatting checks), formatting verification (1,937 files) and line-width verification
passed. Production code was unchanged during the later baseline isolation and scene-fixture correction.

Closing filters and reports are recorded under `artifacts/bot-resident-views`. No full .NET suite
was run. Missile launch/advancement, remote module mutations, loss settlement and burnout still retain SQL paths;
their monitoring reads are resident here. The installed-world workload has not been replayed and no live latency
or supported-population increase is claimed.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

After rebuilding, repeat the captured workload and compare SQL commands, connections, allocation and world-owner
hold time; separate first-use catalog/validation work from ordinary simulation work.

## Resident NPC electronic-warfare cycles (November 11, 2026)

NPC electronic-warfare advancement, cancellation and public disengagement now mutate the resident action view. Capacitor spending,
ability/contact rows, drone hostility triggers and crimewatch deadlines publish only after the same atomic commit.
Participant discovery retains the coordinator while selecting the actor, affected targets and NPC resources,
then acquires their actual system owners. It does not request every system owner merely to discover participants.
The SQL adapter remains available for offline worlds.

Repeated callbacks before an ability deadline do not recalculate the target fitting. The target attributes are
loaded lazily once when the next ability attempt is due. Ability rolls, range/leash rules, source ordering and
saved deadlines retain their existing behavior. References remain the pinned catalog-backed fixtures and
[EVE University electronic warfare](https://wiki.eveuniversity.org/Electronic_warfare).

### Automated validation

The new two-mode paid-cycle regression failed against `76b2e305` because repeated active-effect callbacks opened
SQL transactions. The first resident conversion removed those commands/connections but exposed **32 fitting
calculations in 32 early callbacks**. Lazy attribute loading removed those recalculations; both corrected cases
passed in **8.26 seconds**. They cover simulation objects enabled/disabled, repeated callback identity, invalid
roll rejection, unchanged charge/deadline, a reopened rejected-state checkpoint, successful exact-deadline retry,
cancellation and a reopened paid-state checkpoint. Idle callbacks require zero commands, connections and fitting
calculations; due/rejected operations require zero commands/connections with at most one target fitting calculation.
Initial development builds corrected a missing fixture namespace, an ambiguous constructor overload and the
explicit numeric composite-key boundary before these behavioral checks ran.

The first broader selection was stopped after **255 passes / 3 failures** and is incomplete. Offline composite
lookups needed the saved effect kind converted to the EF enum key type; the SQL action adapter now converts
that persistence boundary using its primary-key metadata. All seven exact guard, reaction/range and new paid-cycle
cases then passed in **13.06 seconds**. Participant selection also limits cancellation to existing effect sources
and advancement to its supplied sources (using aggression membership only for the fallback), rather than importing
unrelated attackers of the same target.

A later selection stopped after **125 passes / 1 failure** and is incomplete. A legacy SQL disengagement removed
aggression while the newly resident child effect could remain unprojected. Public disengagement now uses the
resident action and its mapped cascades, returning NPC motion and expiring matching pending missiles in the same
commit. Source discovery includes current resident aggression and prior affected targets before participant capture.
All **17 effect/cancellation checks passed in 19.90 seconds** after correction, including zero-SQL cancellation for
all tested NPC ability kinds and a reopened checkpoint with no surviving aggression/effect. The wider closing
selection adds missile tests for this newly resident expiration boundary.

A further selection stopped after **127 passes / 1 failure** and is incomplete. Aggression-only captures were
missing from the system acknowledgement set; a newly acquired encounter could also be imported and cancelled
with no net canonical row delta. Publication now names the aggression's NPC/target system owners and reconciles
an acknowledged aggression against the committed resident row. The existing destroyed-NPC detection regression
now asserts immediate cancellation before any SQL read can mask a stale cache. Its first strengthened run had
3 passes / 1 failure; all **four exact detection, cancellation and cold-acquisition cases passed in 8.14 seconds**
after the final reconciliation.

The cold acquisition pilot lookup also reads resident identities and hull/position inputs, selecting scalar pilot
identities before linking hulls. The new check hydrates NPC population inputs, leaves the actor lookup cold, then
requires zero commands, connections and fitting calculations and verifies saved aggression in a reopened image.

The final **414 affected NPC, missile, bot-defense, ownership and checkpoint tests passed in 2m53s**. The final
solution build passed without warnings in **2.63 seconds**. Tool/solution restore, both Python policy suites
(8 catalog and 9 formatting checks), formatting verification (1,938 files) and line-width verification passed.
Closing filters/results are recorded under `artifacts/bot-resident-effects`. No full .NET suite
was run. Missile mutations, player/remote effect mutations, pilot-loss settlement and burnout still retain SQL
paths. The installed-world workload has not been replayed; no live latency or supported-population gain is claimed.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

After rebuilding, repeat the reported workload with an NPC effect cycle and compare SQL commands, allocation and
world-owner hold time separately from cold catalog preparation and checkpoint capture.

## Resident capacitor transactions and resource reads (November 11, 2026)

Capacitor warfare now reads and mutates a resident action for the actor and actual player/NPC endpoint. Both energy
pools, cooldown, active marker, hostility/crimewatch and assistance state commit together. Stop and cold online
membership also use resident inputs. NPC energy has a typed snapshot, finite/reference guards, mapped cascade and
checkpoint projection. Offline worlds retain the SQL action adapter.

Normal activation reuses committed module/ship fitting views. A cold or overloaded activation builds one source
calculation and shares it with both readers; cache-disabled operation retains that single-source fallback. Heat
and capacitor notifications remain after commit. Participant discovery includes the source, recipient and any
previous active-marker hull before importing physical state.

Reference: [EVE University capacitor warfare](https://wiki.eveuniversity.org/Capacitor_warfare), reconciled with the
pinned rules/catalog fixtures. This storage change preserves the existing native NOS absolute-GJ gate, module
payment, range, application, legality and cycle timing.

### Automated validation

The initial paired-pool checks failed against `c995b037` on SQL commands. A preserved file-copy timestamp left the
first post-change run on the older test assembly; that run did not validate the conversion. After rebuilding,
development checks added the missing typed assistance helper and NPC-energy mapping. A traced remaining query was
the fixture's ship-ID getter inside the measured block; it now reads that input before measurement. The fixture
also retains the fitting cache used by the host, so its warmed budget measures the production configuration.

Both initial cases then passed in **8.38 seconds** with zero commands, connections and fitting calculations across
payment, rejected immediate repeat and stop. The expanded matrix had 4 passes / 1 fixture failure: the NPC NOS case
correctly transfers zero against the smaller absolute pool. The check now asserts that exact gate instead of
requiring positive transfer. All **five corrected player/NPC neutralizer, NOS and player-transfer cases passed in
11.11 seconds**, including the reopened source/target balances, cooldown and absent stopped marker.

The first broader selection stopped after **225 passes / 2 failures** and is incomplete. Its SQL heat-insertion
and stop triggers now belong to checkpoint projection for resident capacitor commands. Those two existing
rollback methods retain their capacitor cases through the offline SQL adapter; their original rejection and
unchanged-state assertions remain. New resident cases inject failure only into the detached save image, verify
that its previous balances/heat/marker roll back together, keep the committed live generation and retry without
republishing events. A draft fixture incorrectly installed trigger DDL on the dirty live image and was rejected
by the schema guard; it now targets the detached image.

The next fixture run had **6 passes / 2 failures**: public resource getters were still opening SQL transactions,
which also projected pending state before capture. `IPilotShipResources` resource and capacitor reads now use
named resident actions and preserve paid-repair settlement. All **13 boundary/payment cases passed in 16.46
seconds** after correction. A further observer check passed in **6.59 seconds**, requiring zero commands,
connections and fitting calculations for 32 repeated full/scalar reads and exact one-tick recharge.

A later selection stopped after **245 passes / 1 known baseline failure** and is incomplete: the previously
recorded mutadaptive hull-admission expectation is unchanged by this milestone. The remaining **678 cases passed
in 4m58s**. Combined closing coverage contains **915 distinct passing affected tests**, with no selected case
missing after that known exclusion; this is not a single uninterrupted passing run. The solution build passed
without warnings in **5.94 seconds**.

Tool/solution restore, both Python policy suites (8 catalog and 9 formatting checks), formatting verification
(1,942 files) and line-width verification passed.

Closing filters/results are recorded under `artifacts/bot-resident-player-effects`. No full .NET
suite was run. Player/remote-effect mutations, missile launch/advancement, pilot-loss settlement, heat burnout and
NPC population maintenance still retain SQL paths. The installed-world workload has not been replayed and no live
latency or supported-population gain is claimed.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

After rebuilding, repeat the reported workload and compare SQL commands, connections, fitting calculations,
allocation and owner hold time; separate cold preparation and checkpoint capture.

## Resident player electronic warfare (November 11, 2026)

Player electronic-warfare activation, sweep, stop, cancellation and cold membership now use resident actions.
Participant selection includes the actor, existing source/target endpoints and the new target before importing
physical state. Cleanup includes the saved effect system as well as current memberships. Capacitor, replacement
contact, cooldown, marker, hostility/crimewatch and paid heat publish together. The previous target's modifiers
and heat source are removed through the same action.

Normal activation shares cached ship/module inputs; cold or overloaded activation shares one source calculation.
Private replacement remains atomic with the offline SQL adapter as well. Existing source-controlled repeat
boundaries, stacking, legality, rolls, timing and range rules are preserved. Reference remains
[EVE University electronic warfare](https://wiki.eveuniversity.org/Electronic_warfare), reconciled with the pinned
catalog/rules fixtures.

### Automated validation

The exact resident lifecycle check failed against `7083bc36` on SQL commands and passed after conversion in
**6.42 seconds**. The expanded ECM, dampener, tracking/guidance disruptor and target-painter matrix passed all
**five cases in 9.01 seconds**. Each requires zero commands, connections and fitting calculations after warm-up
for paid repeat/rejection, sweep and stop, and reopens active and stopped checkpoint generations.

Existing SQL heat insertion/stop rejection checks retain the EWAR family through the offline adapter. The detached
save-failure checks now include ECM contact rollback and retry together with heat, energy and marker state; all
**ten boundary cases passed in 14.90 seconds**. Live state stays committed and events are not republished on retry.

All **375 affected electronic-warfare, combat, bot-defense, ownership and checkpoint tests passed in 2m59s**.
The solution build passed without warnings in **5.64 seconds**.
Tool/solution restore, both Python policy suites (8 catalog and 9 formatting checks), formatting verification
(1,943 files) and line-width verification passed.

Closing filters/results are recorded under `artifacts/bot-resident-offensive-effects`. No full
.NET suite was run. Tackle, other remote-effect mutations, missile launch/advancement, pilot loss, burnout and
population maintenance still retain SQL paths. The installed-world workload has not been replayed and no live
latency or supported-population increase is claimed.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

After rebuilding, compare the reported workload with repeated electronic-warfare cycles and cancellation; measure
owner hold time, SQL, fitting calculations and allocation separately from checkpoint work.

## Resident tackle transactions and core deadlines (November 11, 2026)

Tackle activation, sweep, stop, travel cancellation and disconnect now use resident actions. Participant discovery
includes the actor, previous source/target endpoints, saved systems, newly selected target and active core hulls.
Capacitor, contact, hostility/crimewatch, cooldown and paid heat publish together. Incoming effects and cold ship
attributes use canonical reads, with normal activation sharing cached module/ship inputs and one calculation for
cold or overloaded inputs. Rejected cooldowns are checked before constructing a fit.

Core strength expires at its paid boundary, independently of the retained reactivation deadline. The deadline
comparison now subtracts integer ticks; the previous floating-point comparison could expire strength a tick early
at modern file-time magnitudes. Source-controlled remote repeats retain their existing ownership semantics.
Reference: [EVE University tackling](https://wiki.eveuniversity.org/Tackling) and
[warp core strength](https://wiki.eveuniversity.org/Warp_core_strength), reconciled with the pinned catalog fixtures.

### Automated validation

The exact resident lifecycle regression failed on SQL against `ad5ab94f` and passed after conversion. Expanded
coverage checks web, scrambler and disruptor paid repeats, rejected duplicate payments, sweep, cancellation and
reopened checkpoints with zero SQL commands, connections and fitting calculations after warm-up.

Development boundary selections each finished with **9 passes / 1 failure**: the first exposed early core expiry,
and the second confirmed an unnecessary fit on the rejected reactivation. Both were corrected; the exact core
boundary regression then passed in **6.04 seconds**. It retains core strength one tick before expiry, removes it at
the deadline and reopens both captured generations with the paid cooldown intact.

SQL heat insertion/removal rejection checks remain covered through the offline adapter. Detached save failure and
retry now cover both tackle and propulsion, retaining the complete paid or stopped generation and coherent heat.

All **357 affected tackle, propulsion, mobility-heat, combat, bot-defense, ownership and checkpoint tests passed
in 2m42s**. The solution build passed without warnings in **5.68 seconds**. Tool/solution restore, both Python
policy suites (8 catalog and 9 formatting checks), formatting verification (1,944 files) and line-width checks passed.

Closing filters/results are recorded under `artifacts/bot-resident-tackle`. No full .NET suite was run. Other remote-effect mutations, missile launch/advancement, pilot loss, burnout and population maintenance
still retain SQL paths. The installed-world workload has not been replayed; no live latency or population gain is claimed.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

After rebuilding, compare the reported workload with repeated tackle cycles and cancellation; measure owner hold
time, SQL, fitting calculations and allocation separately from checkpoint work.

## Resident missile launches and report acknowledgement (November 11, 2026)

Pilot missile launch and bank payment, contact reads, impact acknowledgement and cancellation now use the resident
world. A volley publishes the projectile IDs, rounds, cooldowns, hostility and paid heat as one transaction.
Target selection declares the source, the explicit target, and, for automatic ammunition, current hostile ships
and NPCs before importing their state. Unchanged fits share evaluated missile-charge values and the corresponding
hull defense, targeting range and damage multiplier. Only fits with loaded missiles capture these extra inputs.
Cold and overloaded volleys use the original graph once. Flight and damage rules are unchanged; see the pinned
catalog cases and [EVE University missile mechanics](https://wiki.eveuniversity.org/Missile_mechanics).

### Automated validation

Both NPC and pilot target lifecycle cases reproduced SQL on `11fbd0a8` (two failures in 7.57 seconds). After
conversion both passed in 7.40 seconds, requiring zero commands, connections and fits for warmed payment,
duplicate rejection, cancellation and contact reads. A real impact report is acknowledged without SQL or repeat
publication, and the checkpoint before acknowledgement retains the pending report.

The expanded boundary selection passed **14 cases in 16.20 seconds**, including automatic acquisition and
expired hostility through both resident and offline paths. A rejection on the second launcher discards the first
member's private changes. An injected failure on the second projectile rolls back the detached save image;
retry projects the full volley with its rounds, deadlines, crimewatch and heat. The offline SQL rejection check
still verifies failed heat insertion during a bank transaction.

All **351 affected missile, fitting, bot-defense, combat, ownership and checkpoint tests passed in 2m22s**.
The solution build passed without warnings in **0.92 seconds** after the feature build. Tool/solution restore,
both Python policy suites (8 catalog and 9 formatting checks), formatting verification (1,946 files) and line-width checks passed.

Closing filters/results are recorded under `artifacts/bot-resident-missile-launch`.
No full .NET suite was run. Player and NPC missile advancement, NPC missile launch, pilot loss, burnout and
other maintenance still retain SQL paths. The installed-world workload has not been replayed and no live
latency or supported-population increase is claimed.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

After rebuilding, compare launch and cancellation counts during a missile-heavy encounter, separating them from
flight advancement, settlement and checkpoint work.

## Resident NPC missiles and hull loss (November 11, 2026)

NPC launch, incoming missile advancement and NPC weapon settlement now use resident world actions. The launch
and direct-fire deadlines remain independent. Flight uses the existing step order and contact time, and duplicate
callbacks cannot replay an acknowledged hit. Selection names the victim, source NPCs and any other aggressors
or Concord responders whose state may change on destruction. Existing fast paths for ordinary NPC turret fire
and drone retaliation continue to use resident combat objects.

Fatal NPC damage commits the replacement capsule, inventory transfers, wreck, report, task progress, insurance
policy and wallet together. A detached checkpoint captures the same generation and rolls back as a unit on
failure. Adjusted prices retain decimal precision, and a material-valued payout reads only its recipe's prices.

### Automated validation

On `d6846c84`, the incoming flight regression counted **51 SQL commands for the nonfatal path and 104 for the
fatal path** in the fixture (two failures, 7.21 seconds). After conversion the nonfatal case passed and the cold
fatal case retained **five static catalog commands**; the commands were on `TypeAttribute` and `SourceRecord`.
With the wreck, capsule and task inputs warmed, both passed in **7.20 seconds**, requiring zero SQL commands
and connections, zero nonfatal fits, and at most one fit for the replacement capsule. The cases reopen the
checkpoint and verify the paid impact or the complete replacement hull and wreck.

The incoming missile and insurance selection passed **26 cases in 18.05 seconds**. Two further cases passed in
**7.04 seconds**, covering zero-SQL launch exactly at the saved deadline and failure/retry on the insurance
ledger insert with the capsule, wreck, combat report, wallet and policy in the same save generation.

All **379 affected combat, insurance, wreck, bot-defense, ownership and checkpoint tests passed in 2m34s**.
The solution build passed without warnings in **5.78 seconds**. Tool/solution restore, both Python policy suites
(8 catalog and 9 formatting checks), formatting verification (1,952 files) and line-width checks passed.

Closing filters/results are recorded under `artifacts/bot-resident-npc-missiles`. No full .NET suite was run. Player missile flight and player loss settlement, NPC population maintenance, and some other
combat/maintenance boundaries retain SQL paths. The installed-world workload has not been replayed; no live
latency or supported-population increase is claimed.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

After rebuilding, compare NPC launch, flight and destruction against the same workload, separating cold catalog
preparation and checkpoint work from the steady frame timings.

## Resident missile impacts, drone commands and NPC population (November 11, 2026)

Outgoing missile flight preserves saved step boundaries and orders impacts by contact time and projectile ID.
The resident action settles damage, NPC rewards, wrecks, combat history and pilot hull loss before publishing
notifications. Missile history uses ordered expiry indexes so a paid flight does not scan every recorded projectile.
Unacknowledged pilot reports remain eligible for retrieval. Capsule loss also removes the active clone and its
implants and rebases the training queue at the impact time. The same settlement handles fatal turret and drone
damage, while existing resident combat objects continue to process ordinary volleys.

Live drone commands, recall into the drone bay, criminal-response deadlines and squad retirement use resident
actions. Regional pirate, guard, convoy and mining updates import the participating NPC state and commit their
membership, deposits, cargo and deadlines together. The running scene reads the committed generation and
calculates its next deadline from the same action. Stopped-world tools use the SQL adapter. Cold catalog reads
and detached SQLite checkpoint projection remain separate from the warmed simulation measurements.

The pinned build-3662168 fixtures preserve the existing gameplay rules for missile application, regional spawns,
drone recall, home-station respawn and criminal enforcement. Gameplay references include
[missile mechanics](https://wiki.eveuniversity.org/Missile_mechanics),
[belt ratting](https://wiki.eveuniversity.org/Ratting), [drone mechanics](https://wiki.eveuniversity.org/Drone_mechanics),
[home station](https://wiki.eveuniversity.org/Home_Station) and [CONCORD](https://wiki.eveuniversity.org/CONCORD).
The local evejs reference's `server/src/space/runtime/missileEntity.js` was checked for impact ordering and
target handling. The snapshot did not contain Git metadata. Its file SHA-256 was
`7b27b19c4725272cd51b6d08f930755afc6934037d9c11d1c74deee8da7eda12`.

### Automated validation

Identical warmed fixtures compared the previous `main` revision `1fc6046c` and the resident paths.
The four zero-SQL assertions failed on the baseline with these counts.

| Fixture | Baseline SQL commands | Baseline connection opens | Resident SQL commands and opens |
| --- | ---: | ---: | ---: |
| Outgoing missile against an NPC | 27 | 3 | 0 |
| Outgoing missile against a pilot | 75 | 15 | 0 |
| Regional NPC respawn at the saved deadline | 28 | 4 | 0 |
| Five drones ordered to return to the bay | 232 | 10 | 0 |

The missile and respawn fixtures also require zero warmed fitting calculations. Detached checkpoint tests
inject a failed write during capsule respawn and CONCORD enforcement, then verify the old generation and retry
the save. A duplicate callback produces no second loss or notification. The capsule case reopens the rebased
training queue with the paid skill points and implant removal in the same generation.

The broader selection ran 1,112 affected cases in 9m50s. It passed 1,110 and found two rollback fixtures
that still injected SQL errors during live combat. Their stopped-world checks retain transaction rollback,
and the resident cases inject failure during detached checkpoint projection. The final targeted selection
passed all 17 cases in 9 seconds, including those corrections and character remapping. The solution build
passed without warnings in 16.74 seconds. Tool and solution restores, the eight catalog policy checks,
nine formatting policy checks and repository formatting verification passed. No full .NET suite ran.

The complete-frame comparison used 12 warmed frames per case with two observers. Both revisions recorded
0–2 SQL commands and 0–2 connection opens per frame, with zero fitting calculations. Checkpoint costs are
reported separately in milliseconds for capture, projection and file write.

| Fixture | Frame p95, baseline → resident (ms) | Checkpoint, baseline → resident (ms) |
| --- | ---: | ---: |
| Idle traffic | 7.78 → 22.43 | 3.71 / 2.23 / 20.25 → 3.80 / 3.63 / 19.40 |
| Player retaliation | 10.92 → 7.45 | 0.42 / 2.10 / 15.73 → 0.27 / 2.11 / 14.59 |
| Ratting return | 5.96 → 7.22 | 0.45 / 0.70 / 15.40 → 0.39 / 0.84 / 16.58 |

The timings vary by fixture. Installed-world latency and supported population remain unmeasured for this change.
Filters, counts and both complete-frame runs are recorded under `artifacts/bot-resident-completion`.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

Replay the installed-world workload with the updated build and compare warmed SQL counts, world-owner wait time,
and checkpoint cost.

## Resident economy memory, November 12, 2026

Startup hydrates the resident economy from the saved SQLite image. The resident tables retain the original SQLite
value only while a row differs from it. Reverting a change removes its pending projection. Projecting or observing
a committed SQL change updates the baseline, and an earlier checkpoint keeps its captured values. Items, market
orders and asteroids use their integer primary keys as SQLite row IDs. Skill lookups retain the pilot index and
direct composite-key lookups. Asteroid lookups retain the belt and system indexes and use the primary map for IDs.

### Automated validation

An isolated startup probe used fresh copies of the same installed 376 MiB save and catalog with server GC and a
fixed clock. The baseline was `84c3f4cf`. The save contained 2,377,385 skill rows and 1,460,535 asteroid rows.
The probe collected before sampling retained memory and working set. It opened no listeners or game client.

| Measurement | Baseline | Updated |
| --- | ---: | ---: |
| Managed memory after economy hydration | 2,165.4 MiB | 1,341.7 MiB |
| Working set after catalog preload | 3,473.4 MiB | 2,565.3 MiB |
| Working set while holding the detached checkpoint | 3,899.7 MiB | 2,969.4 MiB |
| Economy hydration | 7.25 s | 6.72 s |
| Detached checkpoint copy | 227.7 ms | 157.5 ms |
| Save file write | 1,323.8 ms | 1,270.1 ms |

Both checkpoint images contained 375.71 MiB of SQLite pages. Four hundred paired skill and asteroid reads
returned 59,700 rows in both runs with zero SQL commands, connection opens or fitting calculations. Median
time per batch of 40 paired reads was 10.64 ms on the baseline and 5.83 ms after the change.

The complete session replay ran 30 warmed combat frames with four players and eight NPCs in both revisions.
Median frame time was 4.45 / 4.42 ms and p95 was 6.24 / 5.49 ms, baseline / updated. Every measured frame used
zero SQL commands, connection opens or fitting calculations. The four checkpoints per run measured relational
copy at 0.38–0.44 / 0.36–0.47 ms and file write at 12.61–16.26 / 11.69–15.64 ms. The local probes cover saved-world
hydration, targeted warmed reads, checkpoints and fixture sessions. Live multiplayer latency remains unmeasured.

The solution built without warnings. The exact regression
`ResidentEconomyTests.ResidentRowsTrackReversionsAndUseCommittedSqlRowIdsAfterProjection` passed in 3.85 seconds.
The focused xUnit run passed 164 of 169 cases in 68.84 seconds. The five failures reproduced on the baseline in
17 seconds across two cold system isolation cases, two resident belt renewal SQL budgets and the regional payment
rollback fixture. Both Python policy suites, repository CSharpier checks and line-width checks passed. The sandbox
blocked the standard VSTest and .NET format style process connections. The focused tests ran through xUnit
in-process. No full .NET suite ran.

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer checks

Compare working set and private memory on the administration Performance page after startup and during a save.

## Compact economy indexes, November 12, 2026

Resident items, NPC orders and asteroids store their integer primary keys directly. Their secondary indexes
keep row IDs in shared buckets. A pilot's skill bucket stores skill type IDs and restores the shared pilot ID
when enumerated. Membership lookups still use hash indexes. Market summaries now keep only location and ship
identity per pilot, so replacing or invalidating a shared market view releases its order list when readers finish.

### Automated validation

The saved-world comparison used the same frozen 376 MiB save and catalog, server GC and a fixed clock. Baseline
`db0e942d` already includes the sparse SQLite baselines. Memory samples were taken after a forced collection
in the probe.

| Measurement | Baseline | Updated |
| --- | ---: | ---: |
| Managed memory after economy hydration | 1,341.7 MiB | 1,110.8 MiB |
| Working set after catalog preload | 2,481.1 MiB | 2,231.8 MiB |
| Working set while holding the detached checkpoint | 2,902.9 MiB | 2,624.9 MiB |
| Economy hydration | 4.84 s | 4.62 s |
| Detached checkpoint copy | 158.0 ms | 149.0 ms |

Four hundred warmed skill and asteroid read pairs returned 59,700 rows in both runs with zero SQL commands,
connection opens or fitting calculations. The median batch of 40 pairs took 5.60 / 5.80 ms, baseline / updated.
The checkpoint copies each contained 375.71 MiB of SQLite pages. An independent SQLite backup probe measured
median copy into memory at 169.5 ms and median copy to a temporary file at 302.9 ms over three samples each,
with file journaling and synchronization disabled for the comparison. That copy runs under SQL persistence admission.

Two runs per revision used four player sessions and eight combat NPCs, with 30 warmed frames per run. Median
frame time ranged from 4.22–4.39 ms on the baseline and 4.24–5.00 ms with compact indexes. Frame p95 ranged
from 5.47–7.58 ms and 5.90–6.29 ms, respectively. Every measured frame used zero SQL commands, connection
opens and fitting calculations. The fixture checkpoint copy ranges were 0.38–0.45 ms and 0.34–0.57 ms.
Installed save writes varied across the two local probes, from 1.23–1.26 s on the baseline to 1.27–1.99 s
with compact indexes. Live multiplayer capacity remains unmeasured.

The warning-free solution build and both Python policy suites passed. The two new regressions passed in
0.07 seconds. The market retention test reproduced the retained order array on the baseline. The focused
xUnit run passed 166 of 171 cases in 68.03 seconds and reproduced the same five pre-existing failures
listed in the previous entry. CSharpier and line-width verification passed. The sandbox blocked the .NET
style process connection, and tests ran through xUnit in-process. No full .NET suite ran.

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer checks

Compare the administration Performance page after startup, during a save and after browsing several markets.
