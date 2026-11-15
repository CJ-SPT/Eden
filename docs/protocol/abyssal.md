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

## Milestone 1: resident solo foundation â€” November 14, 2026

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

## Milestone 2: environmental combat â€” November 15, 2026

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

The pinned catalog supplies weather types 47378â€“47392, clouds 47440/47472, suppressors
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
