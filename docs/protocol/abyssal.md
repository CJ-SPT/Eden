# Abyssal Deadspace

## Sources and implementation boundaries

The gameplay references are [EVE University](https://wiki.eveuniversity.org/Abyssal_Deadspace)
and [CCP's Abyssal Deadspace guide](https://support.eveonline.com/hc/en-us/articles/360000852629-Abyssal-Deadspace).
They establish the three rooms, shared twenty-minute deadline, ship and capsule loss,
filament admission, restricted hulls and cache-based rewards. Current descriptions are
reconciled with Eden's build-3662168 catalog and original client archive.

Pinned client inspection used `E:\EdenDev\3662168\.eden-original-code.ccp`, SHA-256
`37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`.
Inert bytecode inspection covered `abyss/client/abyssal_service.pyj`,
`abyss/game_mode.pyj`, `abyss/const.pyj` and `movementFunctions.pyj`.
`AbyssalEntranceDeployment` takes item and game-mode IDs; modes are 1 cruiser,
2 frigates and 3 destroyers, distinct from content IDs. `ClientIsReady` has no arguments.
Gate methods take the gate item ID. These methods ignore a successful None reply.
`OnAbyssalLocationEntered` receives a dictionary with content ID, one-based location ID,
difficulty tier, nebula graphic and room count. The expiry notification receives an
absolute filetime; completion receives content ID and normal PvE timer type 11.

The local evejs reference was inspected for admission, room progression, timeout,
gate presentation, notifications and the reconstructed T0 Dark encounter catalog.
It has no Git metadata. Fingerprints of the inspected source are:

- `server/src/abyssal/runtime/abyssalRoomProgression.js`:
  `55e211db9384d35e585668c4f810205dc5a3aac2728674742b4952640664b1db`.
- `server/src/abyssal/runtime/abyssalTimeoutLifecycle.js`:
  `4ab1af317acabf1468adc1d7a1a25ee0f6676e0ba0a8a1c0f1e93d7add3cb43b`.

No reference implementation code is copied. The implementation uses Eden's existing
resident transaction, NPC combat, loot, ship-loss, flight and checkpoint infrastructure.
Carbon Destiny's [Ballpark.cpp at 27ee40b](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ballpark.cpp)
was consulted for ballpark lifecycle context; no new Blue or Destiny codec is introduced.

`tools/Export-AbyssalCatalog.py` exports the relevant pinned catalog records into the
automated fixture. The catalog is authoritative for hulls, NPC dogma and graphics;
encounter compositions and reward quantities are explicit Eden policies, not a claim
to reproduce CCP's private encounter or drop distributions.

## Milestone 1: resident solo foundation — November 14, 2026

Tranquil Dark filament 56132 admits a solo cruiser in open space. Admission validates
ownership, cargo, hull, cloak and travel state, plus 1,000 km clearance from gates,
stations and active traces. It consumes the filament atomically with saved run,
participant, encounter and position creation. It allocates a real catalog Abyssal
system and keeps its return position, room and original twenty-minute deadline.

Each of three rooms contains a pinned rogue-drone hostile and a bioadaptive cache.
The conduit requires all hostiles dead and the ship within 2,500 m; a cache is optional.
NPC deaths give no normal wreck, bounty or standing rewards. Destroying and looting
the cache uses the existing inventory/wreck path. The initial per-room reward policy
is one survey database, two condensates and one replacement filament. Leaving a room
expires abandoned loot before the pocket can be reused.

Run and participant rows are resident-authoritative, captured by normal checkpoints.
Reopening preserves the deadline. Disconnect retains the ship through that deadline;
maintenance advances offline combat and settles expiration even without a session.
Timeout invokes existing hull and capsule settlement in one transaction, including
implant loss and home-clone replacement. Repeated maintenance cannot settle twice.
Normal ship loss also ends the participant's run and destroys the remaining capsule.

Native service calls publish room, timer and completion notifications. Room transfers
refresh static contacts without changing the deadline. Known-space traces persist while
the run is active. Warp and cloak are rejected inside the pocket.

Environmental effects, broader encounters and filament tiers, co-op and mutaplasmids
belong to subsequent milestones. Native rendering and gameplay feel remain unverified.

### Automated validation

Foundation tests cover three-room kills, locked and stale gates, real cache looting,
return position, checkpointed rewards, rejected admission without consumption,
disconnect persistence and idempotent timeout settlement after checkpoint reopening.
A simulated native session verifies room/timer notifications and warm observer reads
with zero SQL commands, connections and fitting calculations.

Passed tool restore, solution restore and build (6.71 s, zero warnings/errors),
93 focused tests (37.34 s), both Python policy suites (8 catalog and 9 formatting tests),
and `tools/Format.ps1 -Check`. The final focused filter was
`FullyQualifiedName~AbyssalTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~NpcEncounterTests|FullyQualifiedName~InsuranceLossTests|FullyQualifiedName~CrimewatchTests|FullyQualifiedName~WorldMigrationTests`.
The initial exact disconnect regression passed in 6.21 s. Full .NET regression was not
requested or run. An attempted `-NoBuild` argument was rejected by the feature-test
wrapper before tests ran; the documented supported invocation produced the results above.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

- Check native entry, timer, conduit activation and return rendering with a Tranquil Dark filament.
- Confirm reconnect shows the original timer and room.

## Milestone 2: environmental combat — November 15, 2026

Resident combat now applies all five weather rule sets, with seeded penalty strength
and one or two rogue-drone hostiles selected per room. T0 Dark remains the admitted
filament at this milestone. Clouds rotate between filament, bioluminescence and tachyon
layouts. Tracking pylons and suppressors are destructible optional targets; destroying
a pylon invalidates the affected fits and NPC profiles without blocking the conduit.

Weather and cloud modifiers feed actual ship/module dogma, NPC movement and weapons,
and deployed combat-drone profiles. Same-field movement retains the fit cache; field
crossings publish a new fit revision under the resident owner. Shared catalog profiles
are never mutated. Short/medium suppressors use pinned ranges, damage and target classes,
including the excluded drone types. Their pulses damage NPC drones and player drones,
and accumulate damage on missile payloads until the projectile expires. Cache structures
and player hulls are excluded. Saved pulse timestamps prevent duplicate callbacks.

The visible 75 km boundary applies resistance-independent depth damage on a one-second
cadence. Continuous exposure ends in ship/capsule settlement after fifteen seconds;
exposure state and pulse state survive checkpoints. The depth curve, terminal exposure,
layout selection and encounter probabilities are explicit Eden policies informed by the
evejs boundary profile, not authenticated CCP formulas. Long server gaps do not replay an
unbounded backlog of environmental damage; the saved exposure deadline still settles.

The pinned catalog supplies weather types 47378–47392, clouds 47440/47472, suppressors
47437/47438, tracking pylons 47469/47470 and boundary object 47465. Tachyon cloud 80876
has no dogma program in this catalog; its x4 speed/x0.5 agility gameplay rule follows the
public gameplay reference and is explicitly authored. The catalog-export fixture includes
the actual room/dungeon and graphic records. Original-client inspection also verified
`EnvironmentService.OnEnteringDungeonRoom(dungeonID, roomID, position, instanceID, values)`:
room entry sends the matched 6204/24754, 6207/24753 or 6205/24751 identity, coordinate tuple
and None for the two optional arguments. Exit clears that environment template.

### Automated validation

Focused tests exercise typed weather values, inclusive cloud/boundary edges, real resident
fit invalidation with zero warm SQL/connections/fitting calculations, cumulative suppressor
damage and exclusions, native environment notifications, checkpointed terminal exposure
and the complete three-room foundation. Tool and solution restore passed; the final
solution build passed in 3.03 s with zero warnings/errors. The final focused run passed
78 tests in 42.27 s (build 5.62 s). Both Python policy suites passed (8 catalog and
9 formatting tests), and formatting verification passed across 2,158 files.

The focused filter was
`(FullyQualifiedName~AbyssalTests|FullyQualifiedName~AbyssalEnvironmentTests|FullyQualifiedName~FittingSnapshotTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~NpcOrbitTests|FullyQualifiedName~LiveDroneRulesTests|FullyQualifiedName~ResidentDroneTests|FullyQualifiedName~SmartbombTests|FullyQualifiedName~ResidentMissileLaunchTests|FullyQualifiedName~WorldMigrationTests)&FullyQualifiedName!~ModuleAndBrainReadsShareTheCommittedGraphAndReturnIndependentAttributes`.
The initial combined run passed 78 and failed one existing offline fitting SQL-budget
assertion (10 commands versus a maximum of 9). An isolated checkout of unchanged
`8ad33204` reproduced that exact failure; its evidence is retained locally at
`artifacts/abyssal/milestone2-baseline-fitting.trx`. No SQL fallback was added or changed
to satisfy that assertion. Full .NET regression was not requested or run.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

- Inspect room backdrop, cloud/boundary rendering and conduit transitions in the native client.
- Check displayed module attributes when crossing clouds and destroying a tracking pylon.

## Milestone 3: tiers, supply and cooperative recovery — November 15, 2026

All 35 pinned filaments (tiers 0–6, five weathers) now admit ships through client type
lists 57, 92 and 233. These include T2/faction cruisers and permitted advanced frigates
and destroyers, and exclude strategic cruisers. Original `abyss/game_mode.pyj` confirms
mode IDs 1/2/3, costs and capacities 1/3/2, and deferred entry for cooperative modes.
Cooperative deployment consumes the matching stack and creates trace 48964; its first
activation starts the shared twenty-minute deadline. Unused traces expire after thirty
minutes without destroying ships or refunding consumed filaments. The fleet roster at
deployment supplies saved admission rights, which survive restart. Capacity is enforced
under the resident owner, and entry closes when the first pilot advances beyond room one.

Each participant stores entry state and current room. Conduits move only their activator;
cleared rooms remain for teammates to loot, then retire when their last participant leaves.
Room-local pylons, suppressors, boundary exposure and gate locks remain independent.
Abandoned deployed drones are removed when their room retires. Death/timeout settles each
entered pilot independently, and unused invitations never cause ship destruction.

Ordinary successful data hacks can supply Tranquil filaments. Cache rewards increase with
tier and cooperative capacity, include next-tier filaments in the last room, and supply
stasis-webifier mutaplasmids for the next milestone. Higher tiers add their pinned Overmind
and more rogue drones. These encounter/reward distributions are explicit Eden policies,
not a claim to reproduce CCP's private spawn/drop tables. Other retail encounter families,
side-cache distributions and exhaustive scenery layouts are not represented. Weather-specific
nebula IDs are pinned; the three existing dungeon geometry templates remain shared.

Admission checks capsuleer combat timers, security/suspect restrictions and safety settings,
station/gate/structure clearance, and nearby active mission/anomaly sites. Public gameplay
reference: [EVE University Abyssal Deadspace](https://wiki.eveuniversity.org/Abyssal_Deadspace).
The local evejs filament/admission/environment catalogs were compared read-only; its T0
restriction is older than the pinned client and is not copied as the supported tier limit.

### Automated validation

Focused development checks passed 26 tests in 18.91 s, including both cooperative modes,
independent room travel across checkpoint reopening, exact pinned filament mappings,
higher-tier encounter admission, unchanged inventory on rejection and actual data-hack
filament rewards. Final tool/solution restore and build passed (7.03 s, zero warnings/errors).
The focused run passed all 108 tests in 49.85 s using
`FullyQualifiedName~AbyssalTests|FullyQualifiedName~AbyssalEnvironmentTests|FullyQualifiedName~AbyssalCooperativeTests|FullyQualifiedName~HackingWorldTests|FullyQualifiedName~CooperativeFleetTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~WorldMigrationTests`.
Both Python policy suites passed (8 catalog and 9 formatting tests), and formatting
verification passed across 2,172 files. Full .NET regression was not requested or run.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

- Exercise two native clients entering one trace and crossing conduits at different times.
- Inspect weather backdrops and cooperative cache rendering.

## Milestone 4: persistent mutaplasmids — November 15, 2026

The native `dynamicItemService.CreateDynamicItem(mutatorID, sourceID)` consumes one
mutaplasmid and one compatible owned module atomically in the resident world, creates a
unique singleton Abyssal module, and saves its source identities, creator, timestamp,
normalized rolls and complete resulting attributes in the same checkpoint generation.
Inputs must be in the current docked personal hangar. Wrong ownership/location, incompatible
mappings, already-mutated items and unsupported module families reject without consumption.

Build 3662168 `dynamicItemSvc.pyj`, crafting UI and dynamic-item tooltip bytecode confirm
the two-argument create call, returned item ID, and `GetDynamicItemInfo(itemID)` KeyVal
fields. Attribute and modifier dictionaries use numeric dogma keys. Published client
`dynamicitemattributes` mappings supply allowed source types, result types and per-attribute
bounds. The calculation is source value multiplied by the interpolated bound, including
negative-valued bonuses. Client module requirements override source requirements.

Abyssal result types contain partial dogma records. Catalog validation now resolves an
ordinary source with matching group and effect program; saved per-item attributes supply
actual fitting nodes. Two modules with the same Abyssal type retain independent rolls.
Fits, activation and module condition use these values, while shared type profiles remain
immutable. Items retain identity and attributes through inventory moves, player trading
and checkpoint reopening. Repackaging is rejected, and ordinary market singleton rejection
continues to apply. The public mutation read supports native item links and inspection.

The service admits module mappings whose source/result fitting programs are supported;
it does not introduce mutated drones, fighters or new unsupported effect programs. Current
Abyssal cache supply includes the three webifier mutaplasmid qualities; exhaustive retail
mutaplasmid loot distributions remain outside the authored encounter/reward policy.

References: [EVE University Abyssal modules](https://wiki.eveuniversity.org/Abyssal_modules),
the original client archive/revision recorded above, and read-only local evejs
`server/src/services/dogma/dynamicItemService.js` (SHA-256
`a5cba6f04661ebdeef0f53e00277464f4bdb264f120c3fbd3d8c483efbd4a3ac`).
No AGPL implementation source was copied. Eden uses a single resident transaction for
both consumption and result creation, with no offline SQL gameplay implementation.
The final lifecycle review also moved high-tier suspect application to successful return:
admission checks safety, while the saved run determines the full return-time flag. The
local evejs participant-return/final-exit owners corroborate this ordering.

### Automated validation

The first 15 focused tests passed in 14.98 s. They cover the native create/read payload,
rejection with unchanged inputs, published bounds for all three qualities of webifiers
and afterburners, actual mutated web activation, fitting cost, warm zero-SQL/zero-fit
reads, repackaging rejection, trade preservation and checkpoint reopening.

Tool restore, solution restore and solution build passed (6.92 s, zero warnings/errors).
The combined affected-dependency run passed 341 tests and failed one new test assertion
in 129.34 s: it rounded total CPU instead of summing individually rounded module CPU.
After correcting only that assertion, its exact-method run passed (9.51 s), followed by
all 16 `FullyQualifiedName~MutaplasmidTests` cases (11.58 s). No production correction
was needed. Both Python policy suites passed (8 catalog and 9 formatting tests), and
`tools/Format.ps1 -Check` passed across 2,188 files after formatting.

The combined filter was
`(FullyQualifiedName~MutaplasmidTests|FullyQualifiedName~AbyssalTests|FullyQualifiedName~AbyssalEnvironmentTests|FullyQualifiedName~AbyssalCooperativeTests|FullyQualifiedName~FittingSnapshotTests|FullyQualifiedName~PassiveFittingTests|FullyQualifiedName~TackleTests|FullyQualifiedName~PropulsionTests|FullyQualifiedName~Overload|FullyQualifiedName~PlayerTradeTests|FullyQualifiedName~ContractAssetTests|FullyQualifiedName~ShipContentCoverageTests|FullyQualifiedName~WorldMigrationTests)&FullyQualifiedName!~ModuleAndBrainReadsShareTheCommittedGraphAndReturnIndependentAttributes`.
The excluded offline fitting SQL-budget failure was independently reproduced on unchanged
baseline code during milestone 2, as recorded above. Full .NET regression was not requested
or run. Native rendering, input and gameplay feel remain unverified.

After merging remote corporation-leadership and loyalty-store changes, all 59 integration
tests passed in 22.81 s (build 10.73 s, zero warnings/errors). The filter was
`FullyQualifiedName~MutaplasmidTests|FullyQualifiedName~CorporationLeadershipTests|FullyQualifiedName~LoyaltyStoreTests|FullyQualifiedName~WorldMigrationTests`.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

- Open the native mutation window and inspect the resulting module tooltip.
- Fit and trade a rolled module between two clients, then reconnect and inspect its values.
