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
