# Autonomous capsuleers

[Protocol index](../protocol.md)

## Persistent local miner — November 8, 2026

Eden provisions one world-owned miner by default on new and upgraded saves. Its home is selected once from the earliest human pilot's saved home, searching by gate distance for a highsec system with a station and generated mineable ore. Systems and stations use catalog ID ordering, with preference for the pilot's home station. The founder is deferred when there is no human pilot or eligible catalog location. Normal character creation retries provisioning inside its transaction.

The one-time founder grant is a Venture, Miner I, required fitting skills and 10,000 ISK. The pilot inherits the founding human's racial background and uses the school NPC corporation, falling back to the home station owner. Its negative account ID has no launcher credentials. The community record prevents repeat grants. Existing development supply settings continue to apply to their world.

The miner fits through ordinary fitting operations, undocks, aligns and warps using shared NavigationState, approaches and locks ordinary ore, pays each laser cycle through PilotMiningStore, fills its mining hold, returns to its permanent home and unloads into its personal hangar. Ore remains stockpiled. Depleted targets are replaced. Mining payments, collection and saved progress share transactions. A failed collection preserves the ore and is retryable. Checkpoints capture navigation and active-time progress with the resident world generation, then project them outside the world owner.

Server scheduling advances one active second per admitted tick while a selected human pilot is online, including docked pilots and pilots elsewhere. There is no offline trip or mining catch-up. Local membership, station guests, Show Info, ship contacts, warp and laser effects use the existing native presentation. Player damage signals and visible NPC aggression initiate retreat through ordinary warp and docking. NPC fire and missile impacts use the existing loss settlement. NPC pursuit is released when the pilot enters warp or docks. Damage events for other ships preserve an already observed threat to the miner. A pilot whose Venture is lost returns in its capsule and waits docked for replacement equipment.

The miner currently retains its starting skills without autonomous training decisions. Replacement purchasing and autonomous pirate hunting are subsequent milestones. The hauler milestone below extends this first miner record with ordinary ore sales and delivery.

Gameplay reference checked against [EVE University mining](https://wiki.eveuniversity.org/Mining). Native flight presentation reuses the existing Destiny implementation and [Carbon Ball.cpp revision 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp). Build-3662168 Venture and Miner I attributes and required skills are exercised with the committed starter-production catalog extract.

**Automated validation** — Tool and solution restore passed. Solution build passed with zero warnings/errors in 1.13 seconds. The affected filter was `FullyQualifiedName~CapsuleerMinerTests|FullyQualifiedName~MiningTests|FullyQualifiedName~MiningTierTests|FullyQualifiedName~CharacterCreationTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~ChatWorldTests|FullyQualifiedName~TypedFlightTests|FullyQualifiedName~LiveWorldTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~PilotCombatTests`. This selected 202 cases, with 196 passing and six existing NPC-mining restart cases failing because their resident world was still open. Those two test methods now stop/checkpoint the resident owner before reconciliation. Their focused rerun passed all 12 variants in 17 seconds, 21.59 seconds including runner startup. Both Python policy suites passed, eight catalog and nine formatting cases. Formatting verification passed.

The measured warmed miner frame took 1.17 ms with zero SQL commands, connections or fitting calculations. The empty-server frame took less than 0.01 ms with the same zero-work counts. In that fixture, checkpoint capture took 1.95 ms, projection 23.65 ms and writing 14.81 ms. These are diagnostic fixture measurements, not capacity or native-client claims. Tests cover two observers, paid-cycle pause/restart, collection rollback, founder grants across concurrent character creation, retreat/restart, depletion and ordinary ship loss. Warm paid-cycle and empty-server frames assert zero SQL commands, connections and fitting calculations. Checkpoint capture, projection and writing are reported separately. The full .NET suite was not requested. A subsequent pursuit-release correction passed the 28-case miner/NPC-combat filter in 14 seconds, followed by the six miner regressions after adding unrelated-damage threat coverage.

**Live acceptance** — Not performed; agent client testing is disabled.

**Optional maintainer checks** — Observe the same pilot in Local, station guests and Overview. Follow one mining round trip and verify the Venture's warp and laser rendering.

## Local ore hauling — November 8, 2026

The default community adds Kira Auvinen, a persistent Badger pilot with a one-time 100,000 ISK wallet and the skills needed to fly the ship. Names receive a numeric suffix if already taken. World schema 115 adds a hauler founder ledger and saved work. Existing miner identities, home stations and possessions are retained. Startup or first-character provisioning selects the lowest-ID adjacent highsec system with reciprocal gates and a market station, then the lowest eligible station ID. Both source and destination must provide the catalog market service. A community without an eligible route defers the hauler grant. The route is saved permanently and repeated provisioning preserves the founder and starting assets.

After unloading, the miner lists a batch of actual ore through personal market orders. The initial quote policy uses the catalog type's reference price divided by its portion size. It caps each source offer at 100,000 ISK gross and checks ordinary brokerage funds and order limits. The hauler purchases an affordable quantity through the normal matching engine, retaining funds for destination brokerage. Cheaper competing orders and human buyers participate in the same matching rules. If another buyer takes the ore, the hauler waits. It does not receive replacement goods or money.

The Badger loads its own purchased stack, undocks, aligns and warps to the gate, approaches activation range, waits the native ten-second departure interval, transfers through the reciprocal gate and flies to the destination station. The hauler then unloads and lists the delivered stock at a target 25% markup over the source reference price. It returns for another trip. Unsold orders retain their goods in ordinary station escrow. Limited capital, fees, order limits and losses can stop further buying. Price targets are bot decision defaults and do not guarantee a profitable sale. Expiration, cancellation, fills, wallets and inventory retain the existing personal-market behavior.

Human and autonomous gate transfers share typed activation-range and arrival rules plus the World transfer operation, including active-ship ownership, reciprocal gate identity and Crimewatch validation. The existing position operation retains travel task tracking and heat cancellation. Protocol scene state is now held per capsuleer, allowing the miner's beam and the hauler's movement to coexist. Local membership changes only when the hauler crosses systems. Station guests, Show Info, gate departure/arrival and late observer snapshots use existing native forms.

Work advances through the same selected-human active clock as mining, including docked or remote human pilots. Empty worlds pause travel and buying. Checkpoints capture the hauler's navigation, current task and remaining jump time with the resident world generation. A committed purchase can be recovered from owned hangar inventory after a loading failure, preserving one payment. Normal NPC combat and player damage observations can prompt retreat to the station on the current side of the route. Tackle affects speed and warp eligibility. Ship and cargo destruction use normal settlement. A surviving capsule returns to the home station and waits for a replacement instead of receiving another founder grant.

Gameplay was checked against [EVE University's hauling reference](https://wiki.eveuniversity.org/Hauling). Navigation reuses Eden's existing implementation, checked against [Carbon Destiny Ball.cpp revision 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp). Gate presentation uses the existing build-3662168 JumpOut/JumpIn effects and peer departure/arrival flags. The pinned starter-production extract supplies Badger skills and cargo and the ore type metadata used in automated tests.

**Automated validation** — Tool and solution restores passed. Solution build passed with zero warnings/errors in 4.99 seconds. Filter `FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~PersonalMarketTests|FullyQualifiedName~PersonalMarketRulesTests|FullyQualifiedName~FlightSessionTests|FullyQualifiedName~FlightBoundaryTests|FullyQualifiedName~TypedFlightTests|FullyQualifiedName~MobilityHeatTests|FullyQualifiedName~LiveWorldTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~CharacterCreationTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~ChatWorldTests|FullyQualifiedName~Eden.Protocol.Tests.Combat.NpcCombatTests` passed 213 cases in 58 seconds, 60.70 seconds including runner startup. Both Python policy suites passed, eight catalog and nine formatting tests. Formatting verification passed.

A warmed combined community frame took 0.76 ms with zero SQL commands, connections or fitting calculations. The empty-server frame took less than 0.01 ms with the same zero-work counts. The shipment fixture checkpoint measured capture at 1.20 ms, projection at 29.71 ms and writing at 35.60 ms. These are fixture diagnostics rather than a capacity claim. The revised shipment regression, including warmed in-flight observer counts and waiting for an unsold order, passed separately in seven seconds after the 213-case affected run. The warmed in-flight observer frame took 0.77 ms with zero SQL commands, connections or fitting calculations. The warm-up includes the observer refresh after the warp transition. Tests trace a full mining trip through market purchase, cargo loading, a restart during gate departure, destination escrow and an empty return trip. They also cover a human buying first, migration from schema 114, a failed load after a committed purchase, lost cargo, gate rejection with unchanged state, Local/space observer transfer, active-time pause and warmed SQL/fitting counts. Checkpoint capture, projection and writing are measured separately. No full .NET suite was requested.

**Live acceptance** — Not performed; agent client testing is disabled.

**Optional maintainer checks** — Watch Kira's Badger depart and arrive at the paired gates, then inspect the delivered ore order at the destination market. Native rendering and flight feel remain unverified by the agent.

## Population and scheduling foundation — November 8, 2026

The first increment of the [global population plan](../capsuleer-population.md) introduces schema 116. Source-system community records and unique community/career founder records register the existing miner and hauler. Migration preserves their original identities, ships, grant metadata, wallets and authoritative paid/travel progress. Registry reads return metadata without fits or native scene loading. Fresh-character provisioning registers founders in the same transaction. No additional global communities are provisioned in this increment.

A saved human-active clock now runs from selected flight-session leases. Docked pilots count, transfers change the active-system set, and selection disposal releases presence even when chat transport is unavailable. Chat continues to present Local membership but no longer owns the activity clock. The clock counts overlapping humans once, preserves subsecond samples, pauses when the last selection leaves and resumes from a checkpoint without offline catch-up. Clock capture shares the resident generation with the relational image; detached projection and failed-save retry preserve later resident time. Maintenance uses this clock to admit the existing miner/hauler ticks.

Typed job records, stable due ordering, stale-revision rejection, a 64-action limit and a 10 ms soft execution budget establish the scheduling rules. One ordered queue entry per agent bounds storage during repeated rescheduling. An atomic settlement operation compares the complete expected job and commits feature writes with the next deadline/revision/sequence. Failure rolls both back; duplicate callbacks do not invoke the feature mutation. Jobs and the defined 50 ms wake interval are prepared for the role-execution increment. Current founders still execute through their existing state machines, and no scheduler jobs are created for them yet.

An interrupted-upgrade regression exposed an older migration step that wrote schema 68 even when opening a newer save. That step now writes its version only for saves older than 68, preserving the newer version when the later transaction fails so startup can retry safely.

Remaining work includes per-agent role execution, conversion of paid deadlines, synchronization barriers, active/background scene handoff and NPC encounter equivalence, global eligibility/provisioning, and administration/capacity reporting. The synthetic scheduling measurements cover queue mechanics only. They do not establish full-population simulation performance.

Gameplay references were checked against [EVE University mining](https://wiki.eveuniversity.org/Mining) and [hauling](https://wiki.eveuniversity.org/Hauling). This increment uses the existing navigation, native presentation and gameplay rules documented above; it introduces no new wire format or Carbon-owned simulation behavior.

**Automated validation** — Tool and solution restores passed. Solution build passed with zero warnings/errors in 4.45 seconds. Filter `FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~CharacterCreationTests|FullyQualifiedName~ServerServicesTests|FullyQualifiedName~DataModelOwnershipTests|FullyQualifiedName~GameplayDependenciesTests|FullyQualifiedName~TypedFlightTests|FullyQualifiedName~FlightSessionTests|FullyQualifiedName~XmppChatTests|FullyQualifiedName~LiveWorldTests` passed all 134 cases in 25 seconds, 27.20 seconds including runner startup. Both Python policy suites passed: eight catalog tests in 0.71 seconds and nine formatting tests in 0.02 seconds. Formatting verification passed. The full .NET suite was not requested.

New coverage exercises schema-115 migration and retry, unchanged founder assets and paid mining progress, atomic job rollback and duplicate callbacks, stable ordering, stale rescheduling, action/time budgets, overlapping selections, transfers, chat-independent docked presence, fractional clock precision, restart without offline progress, failed checkpoints and detached clock projection. Warm presence reads assert zero SQL commands, connections and fitting calculations; the existing warmed miner/hauler and observer checks remain in the affected filter. Synthetic queue restoration and the first bounded turn measured 14.80 ms for 1,804 jobs and 30.79 ms for 10,000 jobs in this run, including fixture assertions and initialization. These diagnostics measure queue preparation rather than a warmed frame or global simulation capacity.

**Live acceptance** — Not performed; agent client testing is disabled.

**Optional maintainer checks** — None for this foundation increment. Global population and native handoff acceptance remain pending implementation.

## Shared miner and hauler jobs — November 8, 2026

Both founders now run through `ICapsuleerScheduler` and its shared due queue. Server maintenance wakes every 50 ms; each turn retains the 64-action limit and 10 ms soft budget. The world owner serializes the queue, per-pilot workers and canonical job versions. Repeated/concurrent wakes cannot advance one deadline twice. Ship identity indexes route aggression without retaining destroyed hulls indefinitely. The dedicated role advancement methods are removed.

World schema 117 creates missing founder jobs once. Migration maps saved lock, mining, waiting and gate deadlines onto the existing human-active clock, retaining each phase's remaining duration and navigation origin. Navigation stamps derive from the saved clock offset. Paid mining retains its original capacitor payment and rebases the native cooldown when human activity resumes or the server reopens. Empty worlds pause, and restart reconstructs jobs without offline catch-up. Existing identities, ships, routes, cargo, wallets and one-time founder grants are preserved.

Economic and travel steps now share explicit transaction-aware feature operations with ordinary human commands. Fitting, inventory moves, market fills/fees, docking, gate transfers, funded mining and NPC disengagement commit with role progress and next-job version. A failed load rolls back the attempted purchase. A failed final job write likewise rolls back fitting, undocking, goods and money; retry commits one action. Resident positions, targets, object lists and navigation restore after rollback. Ordinary motion and nonfatal combat use resident state, while immutable job/progress arrays project with the captured checkpoint generation.

Docked retries sleep until their deadline. In-space navigation, locking, mining danger and jump departure retain one-second samples, with earlier subsecond completion deadlines honored. Native beam, travel and scene presentation use the existing adapters. This increment does not provide background event simulation, system activation barriers or global founder provisioning. Those remain the next increments of the [population plan](../capsuleer-population.md).

### Login freeze correction

During development, the maintainer reported the client freezing after character selection. A read-only managed stack capture of the running server showed a pilot update in `CombatAnomalyStore.List` waiting for `LiveWorld.Enter` while holding `WorldDatabase.MotionLock`. The scheduler simultaneously held the resident owner and waited for that movement lock in `NpcEncounterStore.List`; checkpoint and renewal threads also waited for the owner. NPC, anomaly, electronic-warfare, movement and translocation entry points now acquire the resident owner before the movement lock. The automated regression blocks the owner, starts each competing read, verifies the movement gate remains available, and releases all threads before asserting. A process already stuck in the old lock inversion requires a rebuilt-server restart. This diagnostic observation is not native gameplay acceptance.

Gameplay references were rechecked against [EVE University mining](https://wiki.eveuniversity.org/Mining) and the existing hauling record. The shared navigation sampler retains the behavior checked against [Carbon Destiny Ball.cpp revision 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp). Build 3662168 Miner I duration, capacitor/yield inputs, Venture/Badger capabilities, gates and market forms continue to use the pinned catalog and existing native models.

**Automated validation** — Tool and solution restores passed. The solution build passed with zero warnings/errors in 3.03 seconds. Final filter `FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~PersonalMarketTests|FullyQualifiedName~PersonalMarketRulesTests|FullyQualifiedName~InventoryTests|FullyQualifiedName~FittingTests|FullyQualifiedName~MiningTests|FullyQualifiedName~MiningTierTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~FlightBoundaryTests|FullyQualifiedName~TypedFlightTests|FullyQualifiedName~FlightSessionTests|FullyQualifiedName~LiveWorldTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~CharacterCreationTests|FullyQualifiedName~ServerServicesTests|FullyQualifiedName~DataModelOwnershipTests|FullyQualifiedName~GameplayDependenciesTests|FullyQualifiedName~InventoryStackSplitTests|FullyQualifiedName~InventoryPersistenceTests|FullyQualifiedName~FittingConstraintTests|FullyQualifiedName~FittingSnapshotTests|FullyQualifiedName~MobilityHeatTests|FullyQualifiedName~TranslocationTests|FullyQualifiedName~NpcEwarTests|FullyQualifiedName~Eden.Protocol.Tests.Exploration|FullyQualifiedName~PilotLoginTests|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~TargetLockSessionTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~ChatWorldTests` executed 646 cases: 645 passed and one existing anomaly-scenery assertion failed, in 4 minutes 50 seconds (292.57 seconds including runner startup). Every capsuleer and lock-order case passed. Both Python policy suites passed: eight catalog tests in 2.53 seconds and nine formatting tests in 0.04 seconds. Formatting verification and local documentation links passed. No full .NET suite was requested.

The known baseline failure is `IdleAnomalyFrameTests.IdlePodAndRemoteObserverHaveNoRecurringSqlOrSceneReloads(populated: True, nearScenery: True)`: capsule drift is 3,636.9244384765625 metres against its 0.01-metre assertion. The exact method filter was also run in an isolated unchanged `b772c424246a7a28267b490a2031a9607ced147c` checkout. It produced the identical drift failure, with two variants passing and one failing in 29 seconds (35.06 seconds including runner startup). This pre-existing scenery limitation is retained; the focused release run is not described as an entirely passing run. An earlier capsuleer-only run was interrupted on nested transactions; the transaction fixes and scheduler-fixture correction are covered by the final run.

New tests cover overlapping wakes, one settlement per revision, unchanged goods/funds/fitting/location after failed final job writes, purchase/loading rollback and restart, schema-116 paid-work migration, native cooldown rebasing, pause, and owner/movement-lock contention. Existing tests exercise complete mining and hauling trips, saved warp/gate progress, aggression retreat, ship/cargo loss, concurrent creation, notifications, and zero warmed SQL/connection/fitting counts. A not-due scheduler frame also asserts zero SQL, connections and fitting work. In the final concurrent test run, the miner checkpoint measured capture 1.30 ms, detached projection 51.88 ms and writing 21.35 ms; the hauler checkpoint measured 6.11 ms, 18.41 ms and 24.17 ms respectively. These fixture diagnostics do not establish global population capacity.

**Live acceptance** — Not performed; agent client testing is disabled. The existing blocked server was inspected through a managed stack capture only.

**Optional maintainer checks** — Restart the rebuilt server and retry character selection. Observe one miner and hauler round trip for native movement and beam presentation.

## Active and background handoff — November 8, 2026

The existing miner and hauler now share one canonical simulation across observed and unobserved systems. `SystemSimulationRegistry` records Background, Activating, Active and Deactivating modes, activation generations and the settled clock boundary. A selected human activates their system while docked or undocked; overlapping selections retain the same activation until the last observer leaves. Modes are connection lifetime state and rebuild after restart. Durable job revisions and the existing checkpoint generation continue to guard saved work, so schema 117 and existing founder grants remain unchanged.

Human dispatch, initial snapshots and system entry settle preceding due jobs through `ICapsuleerHandoff`. Asynchronous requests yield between bounded queue turns, then execute their synchronous mutation under the same owner as the final due check. Network output occurs outside that owner. Selection begins before the factory loads native objects; construction failure releases selection and presentation. Native flight motion now shares the authoritative synchronization owner, retaining owner-before-database-motion-lock ordering from the login correction. Failed entry preserves the old membership. Disconnect removes its lease even if a pending action fails, pausing the clock and retaining the due job for retry. Work already due at the frozen clock boundary can settle without advancing time.

Background jobs read cached collision/route geometry and resources instead of calling the native system-scene loader. Compact NPC simulation contacts carry identity, position and warp status without creating native NPC observer records. Shared acquisition, weapons, missiles, repairs and electronic warfare retain their authoritative records. Both careers advance NPC effects and honor shared tackle attributes; a disrupted miner cannot bypass its escape restriction by changing simulation mode.

Unobserved warps schedule the first strict exit sample of the existing analytic `WarpCourse`; gate departure schedules its saved transfer deadline. Entry partway through a scheduled warp samples its original origin, destination, start, position and velocity without executing arrival or revising the paid job. That resident sample participates in the ordinary immutable role checkpoint. Alignment, subwarp collisions, locking, mining danger and threatened work retain conservative one-second samples. Fully sparse combat scheduling, broader combat equivalence and global capacity are not established by this increment.

Committed presentation carries increasing batch revisions plus system generations. Background systems retain chat and station membership while native autonomous scene objects, movement orders and NPC observer caches are released. Entry rebuilds current native warp, mining beam and health state. An older captured batch cannot rematerialize a departed system after a newer batch is applied. Failed presentation retains committed observations for retry, merging newer observations by pilot; it does not execute the job or pay again. Canonical encounters, missiles and effect deadlines are never replaced with a presentation snapshot, and checkpoint capture/projection retains their existing generation rules.

Gameplay references were checked against [EVE University mining](https://wiki.eveuniversity.org/Mining), [hauling](https://wiki.eveuniversity.org/Hauling) and [NPC mining operations](https://wiki.eveuniversity.org/NPC_Mining_Operations). The shared navigation integration and analytic warp course retain the behavior documented against [Carbon Destiny Ball.cpp revision 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp). These are implementation references; no new native-client observations were collected.

**Automated validation** — Tool and solution restores passed. The solution built with zero warnings/errors; the final correction build took 3.57 seconds. Expanded filter `FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~NpcEncounterTests|FullyQualifiedName~NpcSceneUpdateTests|FullyQualifiedName~NpcPursuitTests|FullyQualifiedName~NpcOrbitTests|FullyQualifiedName~NpcEwarTests|FullyQualifiedName~NpcMissileTests|FullyQualifiedName~NpcCatalogSharingTests|FullyQualifiedName~NpcPopulationTests|FullyQualifiedName~NpcMiningTests|FullyQualifiedName~NpcMiningSessionTests|FullyQualifiedName~RegionalNpcTests|FullyQualifiedName~NpcConvoyTests|FullyQualifiedName~NpcGuardTests|FullyQualifiedName~NpcRepairTests|FullyQualifiedName~NpcAggressionTests|FullyQualifiedName~FlightSessionTests|FullyQualifiedName~FlightBoundaryTests|FullyQualifiedName~TypedFlightTests|FullyQualifiedName~TranslocationTests|FullyQualifiedName~LiveWorldTests|FullyQualifiedName~PilotLoginTests|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~ChatWorldTests|FullyQualifiedName~ServerServicesTests|FullyQualifiedName~GameplayDependenciesTests|FullyQualifiedName~DataModelOwnershipTests` passed all 566 cases in 3 minutes 42 seconds (223.72 seconds including runner startup). After tightening active warp sampling and adding metadata-only activation, filter `FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~FlightSessionTests|FullyQualifiedName~FlightBoundaryTests|FullyQualifiedName~TypedFlightTests|FullyQualifiedName~TranslocationTests|FullyQualifiedName~LiveWorldTests|FullyQualifiedName~PilotLoginTests|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~ServerServicesTests|FullyQualifiedName~GameplayDependenciesTests|FullyQualifiedName~DataModelOwnershipTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~NpcSceneUpdateTests|FullyQualifiedName~NpcEncounterTests|FullyQualifiedName~NpcEwarTests` passed all 313 cases in 2 minutes 3 seconds (124.76 seconds including startup). The catalog policy caught a direct station lookup; routing it through the shared typed catalog boundary was followed by filter `FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~ServerServicesTests|FullyQualifiedName~GameplayDependenciesTests|FullyQualifiedName~DataModelOwnershipTests`, which passed all 72 cases in 22 seconds (24.07 seconds including startup). The final named warp-limit cleanup passed exact filter `FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerHandoffTests.EnteringBackgroundWarpSamplesTheOriginalCourseWithoutExecutingItsArrival|FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerHandoffTests.ActiveAndBackgroundMiningHaveEqualOutcomesAtTheSameClockBoundary`: two cases in four seconds (6.74 seconds including startup), with the feature build passing in 4.92 seconds. Both Python suites passed after correction: eight catalog tests in 0.772 seconds and nine formatting-policy tests in 0.030 seconds. Formatting and local documentation targets were verified. No full .NET suite was requested.

New handoff coverage includes explicit generations, repeated entry/departure during alignment, approach, locking, mining and return, mid-warp sampling and continuing active observer refresh without executing arrival, exact gate transfer with unchanged cargo, overlapping humans, stale captured batches, paid-work restart, failed entry and initial scene construction, failed disconnect and presentation retry, retained NPC aggression/projectiles/effect deadlines, actual background warp disruption, and active/background mining equivalence at the same clock boundary. Metadata-only remote activation and ten warmed 50 ms maintenance wakes assert zero SQL commands, connections and fitting calculations, with no background native pilot scenes. Those counters cover warmed non-transactional frames; they do not claim that market fills, inventory transitions, combat settlement or initial hydration avoid SQL. Broader combat equivalence and full-population capacity remain unverified.

**Live acceptance** — Not performed; agent client testing is disabled.

**Optional maintainer checks** — Retry character selection with the rebuilt server. Enter and leave the miner's system during warp/mining, and the hauler's destination during a gate transfer, to inspect native movement, arrival and beam presentation.

## Global provisioning and login latency — November 8, 2026

Schema 118 removes the singleton role-table restriction and adds a saved provisioning seed plus catalog-wide, versioned eligibility. Startup creates missing communities in separate atomic transactions, with one Venture miner and one Badger hauler, actual supported deposits, catalog-valid origins and starting skills, owned equipment, ordinary wallets, fitted miner lasers and saved jobs staggered across 30 human-active seconds. New communities use deterministic unique names derived from the saved world seed, source system and career; case-insensitive collisions receive a suffix. Existing founders, owned assets, source routes, paid work and current jobs remain authoritative. Interrupted grants roll back the pair and resume once; provisioning neither replenishes saved depleted deposits nor replaces lost founder assets.

Every catalog system receives a policy/fingerprint record and an eligibility or deferral reason. The restart-only global setting controls additional grants while retaining established work. Provisioning is prohibited against a running resident world. Local administration now serves authenticated population coverage, per-system eligibility, waiting reasons and performance counters, including backlog age, failures and materialized pilot scenes. Routine diagnostics use immutable published snapshots; system transitions visit observed systems rather than cloning the entire catalog registry on every wake. Resident allowances include the autonomous population so a global pass does not continually evict fits and health views at the historical visitor-cache limit.

The reported login delay was investigated with read-only managed stacks and saved-state counts. The server was executing NPC danger/population work while character selection waited for synchronization; it was not the earlier owner/motion lock inversion. The selected-human clock had advanced about 156 seconds beyond the oldest jobs. Synchronization recomputed its target after every bounded turn, so an overloaded world could keep extending a login's catch-up window. It now freezes that request's entry deadline, releases ownership between turns and invokes the command after settling that boundary. Later advancement by maintenance cannot rewind settlement. Maintenance yields into further bounded turns while overdue instead of imposing another timer wait per turn.

NPC row changes previously cleared all systems' population schedules and membership caches. They now invalidate the affected old/current system, with a scoped refresh during seeding. Calm, stationary NPC miners with funded beams cache their next collection/pickup boundary. Changed ore and standings invalidate that deadline; hostile standings, navigation, alarms, hauling and unfinished mining retain conservative polling. Shared NPC mining, acquisition, weapons, missiles, repairs and effects continue to own their outcomes. No new native packet shape or Destiny motion rule is introduced.

Gameplay references: [EVE University NPC mining operations](https://wiki.eveuniversity.org/NPC_Mining_Operations) and [hauling](https://wiki.eveuniversity.org/Hauling). Existing navigation remains based on [Carbon Destiny Ball.cpp revision 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp). The installed build-3662168 catalog supplies the station, route, origin, ship, skill and ore metadata. The agent did not open the native client.

**Automated validation** — Tool and solution restores passed. The final solution build passed with zero warnings/errors in 4.16 seconds. Affected filter `FullyQualifiedName~Eden.Protocol.Tests.Mining.NpcMiningTests|FullyQualifiedName~Eden.Protocol.Tests.Administration.AdministrationTests|FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~Eden.Protocol.Tests.Combat.LiveSimulationTests` passed 141 cases in 69 seconds (71.79 seconds with runner startup). Separate exact checks passed the moving-clock login barrier, cross-system NPC cache retention, calm funded-mining/ore invalidation, both live/nonresident hostile-standing cases (two cases, 8.34 seconds including startup), and maintenance draining multiple turns from one timer wake (one case, 6.80 seconds including startup). Both Python policy suites passed: eight catalog tests in 0.724 seconds and nine formatting tests in 0.015 seconds. The final registry/entry-horizon exact filter passed two cases in 4 seconds (6.47 seconds including startup), following the final registry invariant correction. Formatting verification passed. No full .NET regression suite was requested.

The isolated installed-catalog diagnostic validated 8,490 registered systems, 902 eligible communities and 1,804 pilots with 959,077 prepared asteroid rows. Before the final calm-NPC scheduling correction, startup preparation took 104.98 seconds, host creation 602 ms, all-agent health warming 4.85 seconds and the cached all-agent pass 9.54 ms. Ten warmed full docked observer frames took 2.26 ms total with zero SQL commands, connections or fitting calculations; background native pilot scenes remained zero. The first count-bounded 64-action turn took 829 ms. Checkpoint capture/projection/write measured 50.60/30.27/393.34 ms separately. Those queue timings use TestClock and the action-count cap rather than a real elapsed 10 ms budget. A 35-second simulated run passed, but required 150.86 real seconds for 12,158 actions, motivating the subsequent calm-fleet deadline and maintenance catch-up fixes. These numbers are diagnostic evidence, not a final capacity claim.

At the user's request to push and continue in a new chat, the extended 60-second installed-catalog rerun was stopped. The larger synthetic population run was not performed. Repeat both after the final scheduler/NPC changes before claiming sustained global capacity. The default eight-system synthetic regression passed within the affected run. An earlier attempted concurrent feature build was blocked by the running diagnostic's DLL locks; the final build above succeeded after the diagnostic was stopped.

**Live acceptance** — Not performed; agent client testing is disabled.

**Optional maintainer checks** — Rebuild/restart the running server and retry character selection. Inspect population/backlog in local administration. Continue the installed-catalog and larger synthetic automated diagnostics in the next chat; use `EDEN_PROVISION_CATALOG`, `EDEN_PROVISION_SYSTEMS`, `EDEN_PROVISION_SECONDS` and `EDEN_PROVISION_OUTPUT` with `CapsuleerProvisioningScaleTests`.

## Startup and local login recovery — November 8, 2026

A disposable copy of the installed save contained 902 communities, 1,804 capsuleer jobs and 960,399 asteroid rows. All jobs were overdue, with an oldest delay of approximately 128 seconds. Before these changes, stopped-world preparation took 41.80 seconds with 20,050 commands and 19,952 connection opens. A global entry barrier remained pending after six seconds and issued 32,049 commands.

Eligibility reuse, batched catalog reads and indexed station/hull validation reduced measured preparation to 3.17 seconds on the first reconciliation and 2.88 seconds in a new process on the reconciled copy, with 181 commands and 43 connection opens. Resident host creation measured 1.23 seconds. A scoped entry into a system with no related capsuleer work dispatched in 13.22 milliseconds with one command and no fitting calculations. These are initialization and handoff measurements, not native end-to-end login timings.

The saved route index includes source and destination systems for each hauler. A local barrier runs the current residents and incoming route jobs through its frozen deadline. Account-level dispatch bypasses job advancement. Global maintenance can retain unrelated overdue work after a local command. Individual system settlement remains monotonic while the global watermark catches up. Gate transfer coverage asserts arrival, unchanged cargo and exactly one transfer even when unrelated jobs remain due.

Two 32-action maintenance samples on the copied save issued 3,932 and 4,627 commands, compared with 10,256 and 10,731 before the changes. The samples visited cold agents and systems and measured 6.99 and 2.38 seconds, respectively. They do not establish sustainable global throughput. NPC hydration now batches the four system-dependent row sets, position-only changes avoid full combat ship reloads, and background miner observations defer health hydration. Mining cargo volume is reused within each operation.

Checkpoint comparisons use the resident SQL versions. Detached saves retain differences across successive backups and failed projection retries. Role updates and job upserts use bounded parameterized batches. No wire packet or Destiny movement calculation changed. The existing gameplay references remain [EVE University NPC mining operations](https://wiki.eveuniversity.org/NPC_Mining_Operations) and [Carbon Destiny Ball.cpp revision 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp).

**Live acceptance** — Not performed; agent client testing is disabled.

The installed-catalog diagnostic completed with 8,490 registered systems, 902 communities and 1,804 agents. Fresh provisioning measured 66.26 seconds, resident host creation 530 milliseconds, and ten warmed docked frames 2.20 milliseconds with zero SQL commands, connection opens and fitting calculations. Background native scenes remained zero. The 59 subsequent one-second simulation windows completed 25,191 actions in 128.14 real seconds, with no simulation failures or due jobs remaining at the checked boundaries. Other automated tests ran concurrently with this diagnostic, so these timings do not establish isolated real-time capacity. Checkpoint capture, projection and write measured 84.59, 44.57 and 562.06 milliseconds, respectively.

**Automated validation** — The combined affected run passed 192 cases in 65 seconds. A subsequent funded-beam correction retained the existing early return before cargo reads while a beam is pending; its exact calm-mining, depletion/reopen and hold-capacity filter passed all ten cases in 17 seconds (18.81 seconds including runner startup). Its filter selected `Eden.Protocol.Tests.Capsuleers`, `Combat.LiveSimulationTests`, `Persistence.DetachedCheckpointTests`, `Persistence.LiveWorldTests`, `Mining.NpcMiningTests`, `PilotLoginTests`, `PilotBindingsTests`, `TypedSessionTests`, `CharacterLogoffTests` and `Hosting`. Exact regressions separately covered unchanged eligibility, renewed deposits, unrelated-system entry with a 128-second backlog, incoming gate transfer, bounded NPC hydration, moving-clock entry, detached projection retry and successive saves. The installed-catalog 60-second diagnostic passed in 3 minutes 39 seconds including provisioning and teardown. Tool/solution restores and builds passed, including the ordinary Debug output with zero warnings/errors. Both Python policy suites passed, eight catalog cases and nine formatting cases. Formatting verification passed.

An earlier broader PilotSessions selection was interrupted after the pre-existing `CharacterSheetServiceTests.NativeRemapUpdatesAttributesDogmaTrainingDeadlineAndClientCaches` assertion failed. The same exact failure was reproduced from unchanged commit `919bb37` in an isolated checkout. A subsequent broad selection was interrupted when the legacy nullable selection-update regression surfaced, then that regression was corrected and passed both its exact rerun and the final affected run. Build attempts overlapping diagnostic/IDE assembly ownership were blocked by file locks; later isolated and ordinary builds succeeded. No full .NET regression suite was requested.

**Optional maintainer checks** — Restart the rebuilt server and retry character selection, then enter a populated system to inspect native scene and movement presentation. Sustained global real-time capacity remains unverified.

## Observed capsuleer encounters — November 8, 2026

System entry now creates local supported encounters on demand. New miners begin a funded cycle at a real asteroid; haulers receive finite cargo once and fly their existing reciprocal station route. Existing founders, owned assets, orders and losses are retained. Empty systems execute no career work, startup grants no catalog-wide population, and command dispatch no longer drains jobs. Role checkpoints preserve remaining paid durations across absence and restart. One witnessed gate transition may complete after observers leave. Familiar hauling identities can reappear at the other reachable endpoint after an absence, preserving ship and cargo.

Native movement and serialization reuse the existing adapters and Carbon Destiny `27ee40b38a563d29054b32f64c3ebc87fabaec1f`, [Ball.cpp](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp). Gameplay references remain [EVE University mining](https://wiki.eveuniversity.org/Mining) and [hauling](https://wiki.eveuniversity.org/Hauling). These sources inform observed gameplay. Regional economic policy is independent of ambient career work.

**Automated validation** — Encounter coverage exercises shared first entry, already-funded mining, two-gate pursuit, unchanged owned cargo, inactive jobs, restart and zero warmed inactive SQL/connection/fitting/native-scene counts. The isolated installed-save copy retained 1,804 jobs: 100 inactive maintenance checks measured 0.96 ms total and zero SQL commands, connections and fitting calculations; shared dispatch measured 0.19 ms. These are isolated automation measurements, with existing active gameplay coverage checked separately. The affected capsuleer, pilot-login, administration and architecture filter passed 88 cases in 26 seconds (28.31 seconds including startup). The final presence-rollback correction passed the ten encounter/population/failed-entry cases in six seconds (8.18 seconds including startup). Tool/solution restores, solution build, both Python policy suites (eight catalog and nine formatting cases), formatting verification and diff checks passed. No full .NET suite was requested. The previous global-population capacity tests and economic bot-purchase tests were replaced because those operations were retired.

**Live acceptance** — Not performed; agent client testing is disabled.

**Optional maintainer checks** — Observe a miner's beam and follow a hauler through gates to inspect native presentation.

### Encounter profile and frame measurements

The completed profiles admit hauling through connected high-security markets without belts. Three-gate corridors receive traffic consistently; half of quieter systems receive a hauler, chosen from the saved seed. Miner opening phases vary among docked, returning and funded mining. A route re-encounter test retains the exact ship and cargo, with ordinary warp navigation toward the destination station. Revisits retry previously deferred local eligibility.

The copied 1,804-pilot save and a small synthetic world ran the same ten-docked-frame workflow. The copy measured 2.27 ms total and the fixture 2.05 ms, both with zero SQL commands, connections and fitting calculations. Cold entry measured 269.75 ms and 1,004.00 ms respectively, including initial preparation/JIT differences. These are separate catalog/save fixtures and do not isolate cold-entry scaling. The copy's checkpoint capture/projection/file-write phases measured 58.43/5.02/436.60 ms. No native rendering, socket backpressure or combat-heavy capacity claim is established by these docked automated samples.

Final local preparation reads the selected system and its immediate route neighbors. The earlier implementation still loaded global security payloads and asteroid counts during a local visit. A regression now places an invalid payload in an unrelated system and verifies that preparing the selected system succeeds. Population metadata invalidates on its actual saved row dependencies and is published before warmed observer frames. A late witnessed transfer is removed from the current career turn once it reaches an unobserved destination.

The final 99-case capsuleer/resident-world/simulation filter passed in 40 seconds (42.31 seconds including startup). The final copied-save sample measured 4.65 ms for 100 inactive wakes, 0.13 ms shared dispatch, 640.10 ms cold docked entry and 0.73 ms for ten warmed docked frames. The small fixture measured 764.82 ms cold entry and 4.27 ms for ten warmed frames. Both warm samples retained zero SQL commands, connections and fitting calculations. Copy checkpoint capture/projection/write measured 68.74/2.93/564.74 ms; the small fixture measured 1.23/17.74/11.01 ms. Earlier samples above document timing variation and successive corrections. Cold measurements include preparation and JIT, and different catalog/save fixtures limit direct scaling conclusions.

## Galaxy-wide station and gate traffic

World schema 120 adds durable career slots and a saved system sequence for new hauling routes. Existing agents occupy slot zero. Old station-pair routes remain readable. Migration recognizes generated pilot names and renames a matching founder ship. New roster grants commit ship, pilot, wallet, skills, cargo, job and route together on first visit. Revisits retain the slot after a ship loss. A failed expansion rolls back the additional roster members together.

The build-3662168 catalog supplies static station objects and reciprocal usable stargates. A stationless system may lie on a multi-hop route, including a return trip through a dead end. Isolated station pairs support local traffic. Initial placement is a one-time encounter choice. Saved navigation includes the route index and direction, allowing an observer to follow the same ship through repeated systems in either direction. The [population guide](../capsuleer-population.md#galaxy-wide-station-and-gate-traffic) gives the density rules.

Gameplay references include [EVE University stargates](https://wiki.eveuniversity.org/Stargates) and [system security](https://wiki.eveuniversity.org/System_security). Movement continues to use the implementation checked against [Carbon Destiny Ball.cpp at 27ee40b](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp).

A read-only census of all 8,490 installed systems with the development save's seed found 5,297 systems with routes and 3,193 with none. Of the routable systems, 321 planned zero home haulers, 1,166 planned one, 1,642 planned two, 1,352 planned three, 640 planned four, 91 planned five and 85 planned six. That amounts to 12,031 potential home haulers if every system is visited. The census created no pilots. It validated 42,220 route candidates and 746,998 consecutive reciprocal gate legs. Jita, Amarr and Rens planned six, Dodixie five, Hek three, Thera four and Kisogo one. The five empire trade hub approach slots selected the corresponding [main stations](https://wiki.eveuniversity.org/Trade_Hubs).

A gate observer previously saw departures but could miss arrivals indefinitely. The far-side career paused when its system emptied, and the earlier reappearance rule considered occasional route-endpoint visits. A gate watch now uses selected human pilots' actual ship positions. The nearest valid reciprocal gate within 250 km of its surface qualifies. Each gate pair can grant one persistent hauler when one or both endpoint traffic profiles support hauling. Multiple observers share that pilot. A pending inbound crossing has a saved deadline and uses the ordinary reciprocal gate transfer and Local membership. The far-side pilot takes ordinary flight actions when observed.

Two automated flight sessions 10 km from the Uoyonen gate in Vattuolen (50005333, reciprocal gate 50003987) observed the same individual haul from an empty Uoyonen. On an isolated upgrade of the installed version-118 world, the ship entered at simulated second 43, left at 179 and returned at 222. On a separate copy of the version-120 world it entered at second 99, left at 235 and returned at 278. Both observers received the ship contact, arrival indication and matching Local updates. These initial runs established the crossing. The maintainer reported a missing gate flash on arrival and departure.

The busy-station fixture produced five home haulers and one miner. A fixed 120-second observation at its station and then a gate saw three distinct traveling pilots, with its first station contact at second 0 and first gate contact at second 117. The quiet dead-end control had zero home agents and zero native scenes. The version-118 snapshot retained 1,804 pilot identities, 1,812 ships, 1,150 item stacks and 1,810 wallets across migration. The fixed Jita 4-4 observation saw a ship on the station grid at second 0. These are server-side samples.

With the [gate controller correction](navigation.md#observed-stargate-activation-in-build-3662168), the version-118 upgrade repeated the crossings at seconds 43 and 222 with a departure at 179. A later read-only copy of the current version-120 world produced arrivals at seconds 133 and 312 and a departure at 269. Each of the two independently selected observers received exactly two arrival and three departure gate commands on the Vattuolen gate across each complete copied-save run, including other departing traffic. The checks inspected the actual serialized gate ID, direction, value and optional arguments in each flight update queue, separately from the ship effects. The pending-wait restart case preserved the actor across 600 simulated offline seconds.

**Automated validation.** Tool and solution restore, build, 436 focused .NET cases (77 seconds), both Python policy suites (eight catalog and nine formatting cases), formatting verification and diff checks passed. The complete sequence took 101 seconds. The final .NET filter was `FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~Eden.Protocol.Tests.Flight|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~LiveWorldTests|FullyQualifiedName~DetachedCheckpointTests|FullyQualifiedName~PilotLoginTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~ChatWorldTests|FullyQualifiedName~AdministrationTests|FullyQualifiedName~CharacterCreationTests|FullyQualifiedName~DirectionalScanningTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting` with four test threads. Copied-world checks ran separately using the opt-in fixture. A preliminary affected run found two fixture mismatches: the human travel test supplied an incorrectly typed gate argument, and the inactive-system performance control had become a valid traffic route. The corrected tests retain the actual jump and zero-work assertions. Full .NET regression was not requested.

**Live acceptance.** Not performed; agent client testing is disabled. The maintainer's report of missing flashes predates the gate controller correction.

**Optional maintainer check.** Run the updated server and watch the Uoyonen gate from Vattuolen. Confirm the gate animates on incoming and outgoing crossings.

### Traffic improvement stage 1 — stationless lane routes

Gate encounters now use the saved route cursor and ordinary route targets after their inbound crossing. Previously the lane-specific target override always selected the watched gate when there was no local station, trapping a hauler between two stationless endpoints even when its saved home station was reachable farther along the route. Initial placement, arming and hidden transfer resolve the exact directed edge in the saved route. Subsequent flight follows the ordinary station/turnaround rules and retains the same owned hull.

References remain the [EVE University stargate reference](https://wiki.eveuniversity.org/Stargates), the pinned catalog and [Carbon Destiny Ball.cpp at 27ee40b](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp); movement and wire formats are unchanged.

**Automated validation.** The exact `CapsuleerInboundRuntimeTests.StationlessLaneContinuesToItsSavedRemoteStationAndPersistsCursor` method passed both gate orientations (6.56 seconds including runner startup). The focused filter `FullyQualifiedName~CapsuleerInboundRuntimeTests|FullyQualifiedName~CapsuleerInboundProvisioningTests|FullyQualifiedName~CapsuleerInboundGateAcceptanceTests|FullyQualifiedName~CapsuleerTrafficNavigationTests|FullyQualifiedName~CapsuleerHaulerTests` passed 48 cases in 13.47 seconds including runner startup. Coverage includes the real remote-station dock, saved route cursor, shared gate observations, rejection, rollback and lost ships. The copied-save gate case remains opt-in and was not exercised. No full .NET regression was run.

Tool/solution restore, solution build (zero warnings/errors), both Python policy suites (eight catalog and nine formatting cases), formatting verification and diff checks also passed.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** None required for this route correction beyond the historical presentation check above.

### Traffic improvement stage 2 — saved trip timing

`HaulerProgress.DwellSequence` defaults to zero for old JSON progress. New inbound waits and station layovers use a deterministic SplitMix64 choice keyed by pilot identity and this saved sequence, then commit the increment with the chosen deadline. The sequence does not advance on inbound pause/resume, and failed transactions retain the original sequence. A replacement hull retains the sequence. Existing waits retain their saved deadlines; there is no world-schema migration. Busy/medium/quiet inbound bounds remain 35–75, 65–105 and 95–135 seconds; station layovers remain 15–45 seconds. Hull selection is unchanged.

**Automated validation.** The exact method `CapsuleerInboundRuntimeTests.SavedWaitSequenceVariesTripsWithinBoundsAndRoundTripsThroughLegacyAndCurrentProgress` passed in 5.19 seconds including runner startup. The focused filter `FullyQualifiedName~CapsuleerInboundRuntimeTests|FullyQualifiedName~CapsuleerInboundGateAcceptanceTests|FullyQualifiedName~CapsuleerTrafficNavigationTests|FullyQualifiedName~CapsuleerHaulerTests` passed 40 cases in 11.77 seconds including runner startup. It covers variation/bounds, old progress, reopened sequence/deadline, paused remaining time, real station docking, rollback, destruction and repeated observed arrivals. The copied-save gate check remains opt-in and was not exercised. No full .NET regression was run.

Solution build (zero warnings/errors), both Python policy suites (eight catalog and nine formatting cases), formatting verification and diff checks also passed. Dependencies and tools are unchanged from the passing stage-1 restores.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** None; timing variation is established by the deterministic checks.

### Traffic improvement stage 3 — incremental scheduling

Ordinary reconciliation now removes withdrawn jobs and publishes only changed revisions into the existing sorted queue. Full restoration is retained for initial/full reload and exception recovery. Incremental population loading does not rebuild the queue. Active membership, turn selection and inbound-lane scratch collections are reused under the world owner. A not-due turn returns before constructing scoped sorted candidates; due jobs retain their stable deadline/pilot/sequence ordering. Same-tick membership and exact saved deadlines remain authoritative.

The same warmed workload before and after the change measured 200 complete maintenance wakes, including capture, shared native presentation and Local publication, with the test clock held below the next deadline. Every sample asserted zero SQL commands, connections and fitting calculations and unchanged canonical jobs.

| Fixture | Baseline allocation | Updated allocation | Baseline / updated elapsed |
| --- | ---: | ---: | ---: |
| Empty system, one observer | 5,143,272 B | 4,348,328 B | 22.03 / 22.09 ms |
| Occupied system, one observer | 7,245,704 B | 5,960,904 B | 30.54 / 14.59 ms |
| Occupied system, two observers | 10,079,040 B | 8,787,584 B | 19.20 / 16.31 ms |

These small-fixture samples include forced diagnostic instrumentation and JIT effects; allocation fell about 13–18%, while elapsed samples do not establish a production throughput claim. Separate updated checkpoint capture/projection/write samples measured 1.26/6.62/10.50 ms for the empty fixture, 0.43/1.67/15.77 ms with one occupied-system observer and 0.36/1.64/10.50 ms with two. A direct 10,000-selected-job check bounds 100 idle turns to 16 KiB of allocation and verifies withdrawn/re-enrolled work executes once in stable order.

**Automated validation.** The exact `CapsuleerTrafficPerformanceTests.WarmedMaintenanceIncludesSharedPresentationWithoutSqlOrFittingWork` method passed three cases before and after the optimization (6.97/6.64 seconds including runner startup). The final focused filter `FullyQualifiedName~CapsuleerSchedulingTests|FullyQualifiedName~CapsuleerRuntimeTests|FullyQualifiedName~CapsuleerHandoffTests|FullyQualifiedName~CapsuleerEncounterTests|FullyQualifiedName~CapsuleerInboundRuntimeTests|FullyQualifiedName~CapsuleerInboundGateAcceptanceTests|FullyQualifiedName~CapsuleerMaintenanceTests|FullyQualifiedName~CapsuleerTrafficPerformanceTests` passed 54 cases in 18.10 seconds including runner startup. Coverage includes concurrency, committed-generation recovery, rollback, observer release/re-entry, deferred inbound work and the late witnessed transfer. Copied-save evidence remains opt-in and was not exercised. No full .NET regression was run.

Solution build (zero warnings/errors), both Python policy suites (eight catalog and nine formatting cases), formatting verification and diff checks also passed. Dependencies and tools remain unchanged from the passing stage-1 restores.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** None required for the scheduling correction.

### Traffic improvement stage 4 — complete update diagnostics

`CapsuleerMaintenance.Tick` now records the whole maintenance update, from before acquiring its operation gate through scheduler execution, presentation capture, shared scene application and Local publication. A common monotonic completion timestamp defines total and presentation durations, including failed presentation attempts. The scheduler's existing `LastTurn` now starts before initial loading/reconciliation and covers settlement and result construction. Initial preparation failures are included in its failure diagnostics. All elapsed calculations use the injected monotonic `TimeProvider`; gameplay clocks and deadlines are unchanged.

The scheduler publishes immutable `LastUpdate` and `LastPresentation` values through the existing diagnostics snapshot, preserving them when a later simulation-only turn updates its counters. A narrow Gameplay diagnostics interface lets the Server maintenance owner supply these measurements. The performance page labels the complete traffic update, simulation/preparation and scene/Local costs separately. Checkpoint timings remain separate. Administration reads still consume the published snapshot without entering the world owner or opening SQLite.

**Automated validation.** The exact `CapsuleerMaintenanceTests.CompleteUpdateIncludesInitialReconciliationCaptureAndPresentationEvenOnFailure` method passed success and failure cases in 5.66 seconds including runner startup. Its simulated preparation/capture/scene costs of 11/3/7 ms produce an 11 ms scheduler turn, 10 ms presentation and 21 ms complete update, including when scene publication throws. The final focused filter `FullyQualifiedName~CapsuleerMaintenanceTests|FullyQualifiedName~CapsuleerHandoffTests|FullyQualifiedName~CapsuleerRuntimeTests|FullyQualifiedName~CapsuleerTrafficPerformanceTests|FullyQualifiedName~Eden.Protocol.Tests.Administration.ServerPerformanceTests|FullyQualifiedName=Eden.Protocol.Tests.Administration.AdministrationTests.PerformanceRequiresAdministratorAndServesLiveProcessDiagnosticsInDarkEditor|FullyQualifiedName~Eden.Protocol.Tests.Hosting.ServerServicesTests` passed 28 cases in 25.46 seconds including runner startup. It includes registered dependencies, failed-presentation recovery, concurrent job execution, warmed zero-SQL/fitting frames and the authenticated HTTP performance page with all three labels. No full .NET regression or copied-save run was performed.

Solution build (zero warnings/errors), both Python policy suites (eight catalog and nine formatting cases), formatting verification and diff checks also passed. Dependencies and tools remain unchanged from the passing stage-1 restores.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** View the performance page while traffic is active to inspect the new readings; native gameplay presentation remains unverified.

## zKill-backed traffic — November 8, 2026

Traffic can draw real pilot names, published supported hulls and complete victim fits from a local snapshot. The [population guide](../capsuleer-population.md#zkill-backed-travelers) documents the import command, date selection, cache, snapshot adoption and retained behavior. Density, saved wait sequences, station/gate routing, observer sharing and retreat remain the existing encounter policy. Imported travelers receive no starter ore or recorded cargo. A populated snapshot does not depend on the legacy freight-ore grant being available.

Source checks used [zKill's current API documentation](https://zkillboard.com/api/docs/), its daily full raw archives at `r2z2.zkillboard.com/history/raw/YYYYMMDD.json`, and [CCP's ESI API explorer](https://developers.eveonline.com/api-explorer#/operations/PostUniverseNames) for public character-name resolution. [EVE University killmails](https://wiki.eveuniversity.org/Killmail), [travel fits](https://wiki.eveuniversity.org/Travel_fits) and [capital ships](https://wiki.eveuniversity.org/Capital_ships) provide the gameplay references. Death records supply observations of pilots and equipment; they are not treated as ordinary travel counts. The pinned catalog and existing hull/module capability checks decide compatibility.

The importer combines destroyed and dropped quantities and retains explicit fitted flags and loaded ammunition, including nested magazine records. Cargo, drone bays and implants are excluded. Every remaining item must be legal: an unknown type, unsupported subsystem, invalid slot, hardpoint/calibration conflict, CPU/power excess, online-group limit or incompatible/oversized magazine rejects the complete observation. An isolated in-memory world runs the ordinary fitting, resource and ammunition operations. Complete modules come online together, avoiding ordering-dependent rejection of valid resource-provider fits. Rigs retain their ordinary fitted phase. No active combat effects are enabled.

Per-character/hull/region/security observations are processed newest first; a newer accepted fit supersedes only older observations of that same combination. Different hulls remain available for route and cargo compatibility. Fit decisions share a catalog/assembly/configuration cache, while quantity bounds remain per-record checks. Each validator uses the existing catalog operation scope to capture a generation instead of performing a file-generation check on every attribute read. Atomic output replacement follows SQLite integrity, manifest count and content-digest verification. A failed import retains the previous installed snapshot and completed download/name caches.

Schema 121 adds a unique source-character reservation keyed to the local pilot, with the source kill and snapshot identity. Reservations deliberately survive pilot deletion. Ships, modules, loaded charges and skills use ordinary saved entities and checkpoint capture. Existing imported pilots never reroll after a snapshot change. Only recognizable, uncustomized generated Badgers can convert while docked and undamaged; the original pilot/ship IDs, wallet, cargo and progress remain. The owning transaction includes equipment, name, hull and reservation changes, and rollback preserves the original state. Production startup shares the initialized world descriptor and captures the snapshot before serving sessions; login-ticket preparation does not load it.

Native packet formats and Destiny calculations are unchanged. The existing scene, Local, station guest, directory, fitting and gate adapters publish the resulting saved pilot and ship. Prior native observations in this page remain historical; no new live-client acceptance was conducted.

**Imported snapshot.** The requested range was reduced from thirty days to one week during implementation. The completed snapshot covers **November 1–7, 2026**; November 8's complete archive had not yet been published. It processed **114,244** kill records with zero duplicate kill IDs and retained **27,947** compatible observations for **17,722** unique pilots, **190** hull types and **18,895** distinct complete fits, including recorded magazine quantities. The **86,297** discarded records include **11,809** superseded observations; the remaining reasons are preserved in the manifest. The seven uncompressed source archives total **288,899,164 bytes**; the installed snapshot is **26,435,584 bytes** at `%LOCALAPPDATA%\Eden\traffic.sqlite`. Its content identity is `CEAF1C1C4520AC3E7E956772FC38DB5FC16123957424DF3AF4B2DE288096E1F0`. The completed weekly rebuild took **188.43 seconds** using already downloaded archives and cached fit decisions. Older archives downloaded during the interrupted monthly run, and the duplicate preflight archive, were removed. The default command now selects seven published complete days.


**Automated validation.** Tool and solution restore, the solution build (zero warnings/errors), eight catalog-policy cases, nine formatting-policy cases, formatting verification and diff checks passed. The final affected filter `FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~Eden.Protocol.Tests.Ammunition|FullyQualifiedName~PilotFittingTests|FullyQualifiedName~ModuleFittingTests|FullyQualifiedName~FittingConstraintTests|FullyQualifiedName~PassiveFittingTests|FullyQualifiedName~FittingSnapshotTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~LiveWorldTests|FullyQualifiedName~DetachedCheckpointTests|FullyQualifiedName~PilotLoginTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~ChatWorldTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting` passed **341 cases in 56 seconds**, using `-- xUnit.MaxParallelThreads=4`. An earlier broader affected run including the Flight namespace passed **545 cases in 3 minutes 49 seconds**. After final typed-parameter/status-constant cleanup, the exact deterministic provisioning and archive-import methods passed another two cases in six seconds (8.56 seconds including runner startup). The opt-in installed-snapshot check was exercised against the actual weekly artifact and verified its digest, counts, names and all 190 admitted hull types. The full .NET suite was not requested and was not run.

Two preliminary twelve-worker validation runs each finished 340/341: one login fixture exhausted its five-second timer during cold database preparation, and one separate ticket-preparation process exceeded its ten-second process budget under parallel load. The login fixture now constructs its world before starting the unchanged five-second exchange timer. Ticket preparation passed in isolation, and the final four-worker affected run passed without widening its process timeout. The original exact regression was `TrafficSnapshotTests.DeterministicCompleteFitProvisioningKeepsRealIdentityEquipmentAndNoStarterCargo`. Coverage includes complete equipment and loaded ammunition, passive statistics, record rejection, collisions, snapshot corruption/mismatch, cached/offline imports, atomic failures, safe docked conversion and cargo/customization preservation, observer sharing, repeated gate travel, losses and reopened checkpoints. Both original and imported warmed traffic controls assert zero SQL commands, connections and fitting calculations; complete update and checkpoint costs remain separate measurements.

**Live acceptance.** Not performed; agent client testing is disabled. Native rendering, input and gameplay feel remain unverified.

**Optional maintainer checks.** Restart the server to adopt the snapshot, then observe mixed ships at a station and gate and inspect a traveler's fitting.

## Destination journeys — November 9, 2026

Gate encounters now prepare a bounded group of four station travelers with staggered departures. One initial visitor can finish a moving gate approach on the unobserved source side. Remaining visitors wait docked at real source stations and fly to the gate when due, removing the previous far-side parking queue. Reciprocal watchers share the group. Observation preserves direction and due time. Each journey saves its kind, home, source, destination, ordered system path and sequence in the existing hauler progress payload. Subsequent station departures choose a compatible destination from the actual docking location. Local return trips retain their home. Visitors avoid their previous origin when another eligible destination exists.

Local journeys cover zero to two jumps, regional journeys prefer three to seven, and long-distance journeys prefer eight to twenty. Unsupported distance bands fall back to shorter real paths. New routes have distinct station endpoints and unique systems. Stationless dead ends receive no new shuttle routes. Hull admission checks every system on the path. Destination candidates are memoized and bounded per distance, with additional selection weight for hubs and station clusters.

The scheduler retains a system-to-route membership index as destinations change. Established travelers continue through empty systems on shared playtime, using alignment, analytic warp, approach and transfer deadlines. Selected-player time pauses in empty worlds. Existing movement, gate effects, transaction/version checks and detached checkpoints provide the travel boundaries. Existing pilots and assets persist, with a new journey chosen at a safe dock. Migration of a paused inbound wait retains its remaining delay. Loading materializes saved rows before committing those transitions so a streaming SQLite reader cannot lock the writer.

Gameplay reference was checked against [EVE University route planning](https://wiki.eveuniversity.org/Star_Map). Movement retains the existing implementation checked against [Carbon Destiny Ball.cpp revision 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp). Routing uses the installed build-3662168 catalog. No new live-client observations were collected.

### Local roster correction

A reported empty Dodixie Local exposed a role-allocation error. Its five home pilots had global IDs that selected regional/long-distance work, while its nearby legacy traveler remained paused. Roles now derive from stable community roster slots. A busy five-pilot roster reserves two locals, two regional travelers and one long-distance traveler. Locals use their community's station as home and prefer same-system station trips. A previously misassigned local returns over real gates after reaching a safe dock. Activation enrolls paused legacy routes into continuous travel without replaying elapsed absence.

Visitors choose an onward destination before routing back to their actual home station. The three-station circuit regression verifies the onward stop and subsequent homeward leg using one persistent ship.

### Automated comparison

The same seeded receiving-gate fixture was observed for 3,600 simulated seconds on baseline revision a7078e8d and the destination-journey implementation. The fresh opening produced the following results. The pending-wait restart retained an already delivered visitor and observed twelve subsequent arrivals from five pilots.

| Receiving-gate measurement | Baseline | Destination journeys |
| --- | ---: | ---: |
| Total inbound crossings | 15 | 13 |
| Distinct arriving pilots | 1 | 5 |
| Repeat arrivals | 14 | 8 |
| Reverse crossings without an intervening dock | 0 | 0 |
| First arrival, fresh fixture | 109 seconds | 4 seconds |

This fixture has station endpoints on both sides, so its baseline reversals include a dock. Separate stationless-corridor tests establish onward travel and quiet dead ends. The baseline comparison intentionally fails the new distinct-pilot requirement.

The same one-hour residential and quiet fixtures were also compared. The residential baseline had one pilot, one home visit and one crossing before becoming dormant. The destination implementation has five home visits and nine crossings by that same pilot. The quiet fixture stays empty in both versions.

An eight-jump fixture covers ordinary flight, background deadlines, a player waiting downstream, and reopening a checkpoint halfway through the trip. Route-generation cases exercise all distance bands and real reciprocal gates. Existing rejection, rollback, tackle, loss, shared observer, native notification and warmed observer tests remain in the affected validation set. An installed-catalog check verifies the Vattuolen/Uoyonen crossing has an onward station destination.

The final copied installed save retained 1,861 roster identities, 1,871 ships, 2,141 item stacks and 1,868 wallets across initialization and reopening. Initialization took 831 ms. Before warming another system, the reported Dodixie case reserved two local pilots and observed its first local in space after one simulated second. Jita preparation then took 972 ms. Its one-hour sweep saw six distinct gate pilots and six distinct station pilots. The two-observer path saw four distinct pilots. These are copied-world automated observations.

Warmed complete maintenance measured 11.27–37.33 ms across 200 unchanged wakes, including one or two observers and original/imported traffic. Each case asserted zero SQL commands, connections and fitting calculations. Checkpoint capture, projection and write measured 0.61–1.45 ms, 1.46–7.58 ms and 10.72–21.69 ms respectively. These are automated fixture measurements.

**Automated validation.** The affected validation filter includes Capsuleers, Flight, WorldMigrationTests, LiveWorldTests, DetachedCheckpointTests, PilotLoginTests, StationGuestTests, ChatWorldTests and Hosting. The final selection passed 494 cases in 135 seconds with four workers. The separate login-preparation process check passed in isolation in three seconds. A preliminary parallel run timed out that check while a copied-world run was competing for resources, so the final selection excluded that method and exercised it separately with its unchanged ten-second timeout. After moving the background timing calculation into Gameplay, the 14 scheduling, eight-jump, gate-boundary and station-wait cases passed in seven seconds. Both Python policy suites passed, with eight catalog and nine formatting cases, and formatting verification passed. The final opt-in copied-save and installed-route checks passed two cases in 85 seconds. Full .NET regression was not requested.

The affected filter was the following expression. The isolated method filter was FullyQualifiedName=Eden.Protocol.Tests.Persistence.LiveWorldTests.DevelopmentTicketPreparationDoesNotAcquireTheRunningWorld.

~~~text
(FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~Eden.Protocol.Tests.Flight|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~LiveWorldTests|FullyQualifiedName~DetachedCheckpointTests|FullyQualifiedName~PilotLoginTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~ChatWorldTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting)&FullyQualifiedName!~DevelopmentTicketPreparationDoesNotAcquireTheRunningWorld
~~~

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** After restarting with the new build, watch a connected gate for varied arrivals and follow one traveler across successive systems. Existing short trips can finish before adopting their next destination.

## Persistent traveler routines — November 9, 2026

Travelers now retain a deterministic pace, station affinities and three recent stops in their existing hauler progress payload.
Geographic destination weights combine with stable personal affinities and a separate per-trip mixer. Recent stops receive a
quarter of their ordinary weight. Existing journeys finish before routine assignment at a dock. New station dwell times use
half/normal/double pace with 20% variation, retaining population counts and journey distance bands. Security status is not a
travel preference; real hull restrictions remain enforced. All stations are considered before retaining bounded candidates.
Capital destination searches now apply hull restrictions during traversal, preserving detours hidden by a shorter highsec path.

Attack memory retains four systems for thirty human-active minutes. A retreat shelters at the next dock for five to fifteen
human-active minutes. Alternate paths and destinations avoid remembered systems while available, including postponing a
preferred homeward leg. Otherwise ordinary compatible routes remain available. Memory and shelter share the saved progress
and checkpoint generation. Rejected navigation retains the threat instead of silently consuming the attack notification.
Legacy saves retain their assets, identities, current navigation, deadlines and nullable progress compatibility.

References: [EVE University hauling](https://wiki.eveuniversity.org/Hauling), with Eden's explicit security-neutral routing policy;
unchanged navigation uses [Carbon Ball.cpp at 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp).
No client observations were collected.

**Automated validation.** Tool/solution restore and build passed with zero warnings/errors (final build 3.89 seconds).
Final affected filter `FullyQualifiedName~TravelerRoutineTests|FullyQualifiedName~CapsuleerTrafficRoutesTests|FullyQualifiedName~CapsuleerTrafficNavigationTests|FullyQualifiedName~CapsuleerInboundRuntimeTests|FullyQualifiedName~CapsuleerTrafficObservationTests|FullyQualifiedName~CapsuleerTrafficPerformanceTests|FullyQualifiedName~LiveSimulationTests`
passed **84 tests** in **20.57 seconds** (feature build 1.48 seconds). Catalog setup (**8**) and formatting policy (**9**)
Python tests passed. Formatting and diff verification passed. Tests cover station affinity and recent-stop weighting,
pace variation, compatible capital detours, threat avoidance/expiry/fallback, all station endpoints, retreat/shelter and
checkpoint persistence. Warmed complete traffic wakes retain zero SQL commands, connections and fitting calculations for
empty and occupied systems with multiple observers; capture/projection/write are measured separately.
Two hundred warmed complete wakes took **9.58–24.45 ms** across those cases. Checkpoint capture was **0.44–2.85 ms**,
projection **1.87–6.36 ms**, and writing **11.41–18.50 ms** on this run; these measurements are workload evidence, not latency guarantees.
Development first used exact filter `FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.TravelerRoutineTests.HabitsThreatsAndShelterSurviveCheckpointWithoutChangingLegacyDeadlines`.
The first fixture serialization omitted the production read-only-property setting and was corrected. The expanded retreat
regression exposed stale outbound alignment and dirty-position eviction on docking; both were corrected before final validation.
The dirty-position correction is exercised by a real resident checkpoint after retreat. No full .NET suite was requested or run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Observe recurring travelers and the sheltered return after an attack to assess visual pacing.

## Simulated-pilot retaliation — November 9, 2026

Travelers and miners now share a defense controller independently of their career routines. Typed Gameplay rules select
threats, activation thresholds, capacitor reserve and retreat. World executes normal authoritative operations; Protocol
publishes the existing targeting and module contacts. The committed hostility ledger and incoming combat triggers replace
the victim-only damage callback. Paid misses, launches, tackle and electronic warfare can provoke defense; target locks alone
cannot. Defense selects an active tackler, then the most recent attacker, then stable ship identity. It stays on the current
grid and forgets ships that depart or are lost, without following gates or selecting replacement capsules.

Actual hull cohorts, online usable equipment and remaining damage sources distinguish combat ships from escape-first
miners and transports. Locks retain normal scan delay, range and jamming restrictions and share the human target-lock
registry. Each offensive action reuses normal legality, payment, cooldown, damage and loss operations. Automated module
coverage includes turrets, launchers, drones, tackle/core stabilizers, ECM, dampeners, turret/guidance disruption, painters,
neutralizers/nosferatu, propulsion, local/ancillary shield and armor repairs, hull repair, hardeners, capacitor boosters,
assault damage controls, signature suppression and targeting/tracking/guidance/drone support.

Local repairs start below 80% of their layer, boosters below 30% capacitor, and emergency defenses below 40% of the fitted
tank layer. Defensive modules precede optional offensive support with a capacitor reserve. Ordinary navigation maintains
weapon range and advances at most once per movement stamp despite subsecond module deadlines. Combat ships retreat below
35% effective health, below 75% structure, or when usable damage sources run out. Retreat cancels offensive repeats and
recalls drones, allowing at most ten seconds before available escape takes priority. Normal weapons timers still apply.
An emergency core's strength ends at its paid duration while retaining its longer reactivation deadline; successful core
activation immediately updates the career's warp restriction.

One durable `WorldCapsuleerSupplies` marker per pilot shares the owning grant/departure transaction. New ships, and surviving
original ships at their next dock, receive at most five magazines of loaded consumable combat charges, bounded by free cargo.
Scripts and nonconsumable crystals are excluded. A legal homogeneous flight of supported T1 mobile combat drones prefers the
largest class permitting a full flight, with stable type ordering. Bay, bandwidth and control limits apply; existing cargo,
equipment and drone stacks are preserved. Required drone skills use the existing simulated-pilot grant mechanism. Consumption,
abandonment and loss remain finite. There is no hunting, fleet assistance, automatic overheating, market restocking or
replacement-ship grant. The schema reconciliation creates the marker table idempotently on existing version-131 saves.

Nullable defense progress preserves legacy payloads. Locks, cycle deadlines, recall and movement stamps checkpoint with
career progress. Standard local repair cycles and sustained-module renewal keep paid capacitor and cooldowns resident.
Renewed repair deadlines replace older projected completions, and checkpoint/restart tests complete the last paid repair once.
Existing initialization clears sustained bonuses; defense preserves paid deadlines without advertising a removed bonus or
paying again before another cycle is due. Ordinary turret combat against pilots now uses the existing resident combat math;
criminal/fatal actions retain transactional settlement. Failed and duplicate callbacks do not repeat payments or losses.

Performance keeps unchanged and absent reads cached, shares observer contacts, indexes pending hostility by victim and caches
defense fitting profiles by the actual pilot revision. Peaceful updates, movement, nonfatal turret/drone combat, standard
local repairs and sustained local renewals use resident state. Initial activation, charge loading, missile and remote-module
operations retain their established SQL boundaries. Their cost remains relevant when scaling mixed-module fights; the fixture
measurements below are not a large heterogeneous-fleet capacity claim. Checkpoint capture, projection and file writing are
measured separately from complete maintenance and two-observer session frames, including packet encoding.

Gameplay references: [EVE University timers](https://wiki.eveuniversity.org/Timers),
[turrets](https://wiki.eveuniversity.org/Turret_mechanics), [drones](https://wiki.eveuniversity.org/Drones), and
[tackling](https://wiki.eveuniversity.org/Tackling). Movement retains the established
[Carbon Ball.cpp revision 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp).
Catalog variants use committed build-3662168 extracts. No new live-client observations were collected.

**Automated validation.** Development began with exact method filters through `tools/Test-Feature.ps1`, including
`PilotTurretTests.WarmPilotVolleysKeepPaymentsAndHostilityResidentWithoutSql`,
`CapsuleerDefenseTests.CombatPilotLocksAndRetaliatesAgainstAMissingShotWithNormalCostsAndLegality`,
`CapsuleerDefenseModuleTests.DefenseUsesTheRealSupportedModuleAndCannotReplayItsPaidCycle`,
`CapsuleerDefenseLifecycleTests.EmptyMagazineReloadsOwnedCargoThenRetreatsWithoutCreatingCharges`,
`CapsuleerDefenseLifecycleTests.DroneRecallReturnsIndividualsOrYieldsToEscapeAfterTenSeconds`,
`CapsuleerDefenseLifecycleTests.LockingAloneDoesNotProvokeButTackleMakesAMiningFitFightWhileTrapped`,
`CapsuleerDefenseTests.ACommittedJamPreventsLocksOnOtherAttackersUntilItEnds`,
`CapsuleerDefensePerformanceTests.WarmLocalRepairCyclesUseNoSqlAndCheckpointOnePaidCompletionAcrossRestart`,
`CapsuleerDefenseFrameTests.WarmCompleteMaintenanceAndTwoObserverFramesKeepCombatResident`, and the three
`CapsuleerSupplyTests` methods. An intermediate affected selection passed 146 cases in 55.43 seconds including runner startup
(feature build 6.41 seconds). The wider affected selection checked 1,112 cases in 318.46 seconds: 1,111 passed and one old
snapshot assertion expected only a fitted gun at the ship location, excluding the newly required reserve cargo. The assertion
now checks the fitted slot, unchanged loaded ammunition, reserve stack and durable marker separately. Its exact method and the
updated capacitor-reserve rule passed two cases in 6.09 seconds (feature build 4.25 seconds). A subsequent exact NPC-miss filter
passed both turret and failed-electronic-warfare cases in 6.36 seconds (build 5.69 seconds).

The final affected follow-up passed **319 cases in 93.38 seconds**, with four workers, on the corrected code. Its filter was
`FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerDefense|FullyQualifiedName~CapsuleerSupplyTests|FullyQualifiedName~TrafficSnapshotTests|FullyQualifiedName~CapsuleerMinerTests|FullyQualifiedName~CapsuleerHaulerTests|FullyQualifiedName~CapsuleerInboundRuntimeTests|FullyQualifiedName~CapsuleerTrafficPerformanceTests|FullyQualifiedName~PilotTurretTests|FullyQualifiedName~PilotDroneTests|FullyQualifiedName~NpcEwar|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~NpcMissile|FullyQualifiedName~DroneCombatTrigger|FullyQualifiedName~LiveSimulationTests`.
The wider filter additionally selected Capsuleers, Propulsion, Repair and Defenses namespaces and MissileTests, PlayerEwar,
CapacitorWarfare, SupportModule, DetachedCheckpointTests, WorldMigrationTests and TargetingTests. Those unaffected checks were
not blindly repeated after the focused correction. No full .NET regression suite was requested or run.

Tool/solution restore and the final solution build passed with zero warnings/errors (4.31 seconds). Both Python policy suites
passed: eight catalog cases in 0.837 seconds and nine formatting cases in 0.026 seconds. Formatting verification checked 1,467
files and passed the 140-character policy; diff verification passed. Coverage includes human/NPC attackers, all security bands,
misses and failed electronic warfare, lock-only inactivity, tackle-only aggression, jamming, attacker selection, retreat,
finite ammunition/reloading, individual drone recovery and timeout, supported module families, duplicate paid cycles,
rollback, unchanged-state rejection, exactly-once losses, absent NPC rewards for capsuleer kills, two-observer effects,
bounded grants and restart continuity. Early focused failures corrected fixture setup and exposed paid-cycle retention,
renewed repair projection and emergency-core expiry issues before final validation.

The final deterministic workload uses 12 warmed, time-advancing complete maintenance/session frames and two observers,
with packet encoding. A quiet population fixture avoids unrelated unsupported NPC spawn retries while retaining ordinary
career jobs, commands, damage and session updates. Counts include periodic transaction work, rather than reporting only
the controller's inner loop. Timings are fixture evidence under a four-worker test run, not server-capacity guarantees.

| Complete frame or checkpoint | Peaceful | Retaliating |
| --- | ---: | ---: |
| Median frame | 0.53 ms | 0.76 ms |
| 95th-percentile frame | 1.03 ms | 3.14 ms |
| SQL commands per frame | 0–2 | 0–8 |
| Connections per frame | 0–2 | 0–8 |
| Fitting calculations per frame | 0 | 0 |
| Checkpoint capture | 0.50 ms | 0.74 ms |
| Detached projection | 1.69 ms | 9.86 ms |
| Checkpoint file writing | 23.45 ms | 20.01 ms |

Ten warmed paid local repair cycles took 0.88 ms and allocated 25,816 bytes, with zero SQL commands, connections or fitting
calculations. Their checkpoint capture/projection/write measured 0.52/5.29/11.02 ms. Separate warmed peaceful-controller,
pilot-volley, drone-combat and traffic-presentation checks retain zero SQL/connection/fitting budgets where no transaction
boundary is due. Captured locks/cycle deadlines, damage, charge consumption and the final paid repair are checked across
projection/restart; same-time callbacks cannot replay them.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Observe a retaliation encounter and a retreat to assess native effects and gameplay feel.

### Main integration — November 9, 2026

Integrated both pilot milestones with main revision `39fb0fc2`, retaining its gas/ice mining, map and mod-package changes.
The only conflict was the protocol index; both modding and simulated-pilot links are retained. Shared resident read-model
changes preserve the mining-site cache alongside defense-profile invalidation. Uncommitted work in the main checkout
was excluded from this merge and preserved.

**Automated validation.** Tool/solution restore and the merged solution build passed with zero warnings/errors
(6.04 seconds). Exact filter
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerDefenseFrameTests.WarmCompleteMaintenanceAndTwoObserverFramesKeepCombatResident`
passed both peaceful/combat observer cases in 8.14 seconds (feature build 1.39 seconds). The affected filter
`FullyQualifiedName~CapsuleerDefense|FullyQualifiedName~CapsuleerSupplyTests|FullyQualifiedName~TravelerRoutineTests|FullyQualifiedName~CapsuleerTrafficRoutesTests|FullyQualifiedName~CapsuleerTrafficNavigationTests|FullyQualifiedName~CapsuleerInboundRuntimeTests|FullyQualifiedName~CapsuleerMinerTests|FullyQualifiedName~LiveSimulationTests|FullyQualifiedName~GasMiningTests|FullyQualifiedName~IceMiningTests`
passed **151 tests in 49.24 seconds** (feature build 1.47 seconds). Both Python policy suites passed: eight catalog cases
in 0.854 seconds and nine formatting cases in 0.023 seconds. Formatting checked 1,491 files; line-width and diff checks passed.
No full .NET suite was requested or run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** The retaliation/retreat observations above remain optional.

## Belt ratting, resupply and ship variety — November 9, 2026

Combat-fit travelers now select bounded observed belt-ratting sorties, proactively engage only suitable ordinary belt
pirates, collect owned NPC loot and unload it at their actual return station. Pure typed Gameplay assessment evaluates
the complete visible group using fitted damage/application, ammunition, capacitor, layered resistances, enemy repairs
and conservative survival bounds. Unsupported NPC electronic-warfare estimates are rejected. The shared defense owner
accepts an explicit proactive NPC target without manufacturing incoming aggression; real attackers retain priority.
Normal module, targeting, damage, bounty/skill-point, drone, retreat and loss owners remain authoritative.

Optional hauler checkpoint fields retain activity choice, belts, elapsed budget, targets, loot identity, supply preferences,
shopping destination and visit spending. Ratting is removed from continuous unobserved traveler scheduling and resumes
remaining activity/controller deadlines, including when the global human clock was stopped. Committed effects retain
normal settlement; no unobserved farming loop or global NPC freeze is introduced.

Resupply uses owned hangar stock, then actual market purchases in the same owning transaction as progress advancement.
Arrival freezes a quarter-wallet budget. Unit prices are bounded by twice the bundled sell reference; missing prices
are skipped. Candidate shopping paths are limited to three jumps in the current region and avoid remembered threats.
Empty magazines reload through the ordinary paid ammunition operation. No new money, recurring supplies or lost ships
are granted. The population guide records the selection and capacity policies.

Traffic selection now draws hull groups before characters and bounds combined rookie/shuttle admission by the community
roster, including visitors. Idempotent `WorldTrafficCorrection` reconciliation records one-time untouched rookie hull/fit
corrections while retaining the original imported identity. Supply inventory changes invalidate the actual imported pilot's
fitting/assessment dependencies. Warm movement, direct combat and shared observer reads retain resident ownership;
market, loot, reload, missile and destruction operations retain their normal SQL boundaries.

Gameplay reference: [EVE University belt ratting](https://wiki.eveuniversity.org/Ratting).
Navigation continues to use the established
[Carbon Ball.cpp revision 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp)
and pinned build-3662168 catalog definitions. No new native-client observations were collected.

**Automated validation.** Development used exact method filters through `tools/Test-Feature.ps1`, starting with
`RattingRulesTests.ActualFitMustClearTheWholeGroupAndSurviveTwiceAsLong` and then real-world purchase, navigation,
proactive combat and reload cases. An intermediate affected run passed 103 cases in 37.29 seconds including runner
startup (build 5.35 seconds). Later additions cover real pirate destruction and exactly-once bounty settlement,
owned versus foreign wrecks, hangar unloading/restart, paused human-clock deadlines, three-jump boundaries,
price ceilings, threat memory, finite owned reloads, and correction rollback/idempotency.
Final integration with main `e31693d1` passed **471 affected tests** in **108.15 seconds** including runner startup,
using four workers. Tool/solution restore and the solution build passed with zero warnings/errors (build 5.59 seconds).
Both Python policy suites passed: eight catalog cases in 0.613 seconds and nine formatting cases in 0.015 seconds.
Formatting verification checked 1,554 files and passed the 140-character policy; diff verification passed.
No full .NET regression suite was requested or run.

Final affected filter:
`FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~WreckTests|FullyQualifiedName~PersonalMarketTests|FullyQualifiedName~RegionalMarketTests|FullyQualifiedName~NpcScene|FullyQualifiedName~LiveSimulationTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~PilotTurretTests|FullyQualifiedName~PilotDroneTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~DetachedCheckpointTests`.

Complete maintenance/session-frame measurements include two observers and packet encoding after twelve warmup frames.
The ratting sample measures an observed returning sortie; the combat sample exercises the shared retaliation controller.
These small deterministic fixtures establish their database/fitting budgets, not heterogeneous-fleet server capacity.
Checkpoint capture, detached projection and file writing are measured separately.

| Workload | Median / p95 frame | SQL / connections per frame | Fitting calculations | Capture / projection / write |
| --- | --- | --- | --- | --- |
| Peaceful traveler | 0.88 / 5.36 ms | 0-2 / 0-2 | 0 | 0.82 / 6.13 / 11.53 ms |
| Shared combat controller | 2.12 / 2.75 ms | 0-8 / 0-8 | 0 | 0.53 / 3.88 / 12.56 ms |
| Returning ratter | 0.57 / 2.46 ms | 0-2 / 0-2 | 0 | 0.50 / 2.16 / 12.75 ms |

Development failures exposed a nested departure read transaction and a schema-order dependency in supply invalidation;
both were corrected. Other early failures corrected fixture serialization, ownership queries, initial reload cooldowns
and remote ask prices. Focused checks were rerun after those corrections; the final affected result above includes them.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Observe a belt fight, wreck approach and subsequent resupply trip to assess native effects
and gameplay feel.
