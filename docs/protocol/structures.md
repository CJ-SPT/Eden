# Player Upwell structures â€” November 10, 2026

[Protocol index](../protocol.md) Â· [Documentation index](../README.md)

## Upwell completion program â€” November 16, 2026

The completion program continues the existing foundation with resident authority and checkpoint persistence.
Historical foundation limitations below describe their original milestones; this program records subsequent work explicitly.

| Milestone | Scope | Status |
| --- | --- | --- |
| Battle state | Catalog layer caps/resistances and pause thresholds, repairs, reinforcement, abandonment, native status and saved deadlines | Implemented; validation below |
| Asset safety | Personal/corporate collection, timed delivery, paid recovery, native inventory and public loot | Implemented; validation below |
| Incoming combat and loss | Paid weapons against structures, legal targeting, destruction settlement and automatic custody transitions | Implemented; validation below |
| Defense and tethering | Combat fitting/control, supported defensive weapons, target updates, access-checked tether and repairs | Planned |
| Service completion | Remaining native structure profiles/access, economic/clone services, ownership and specialized structure consumers | Planned |

### Battle state and native status

Saved-world schema **185** adds `WorldStructureBattle`. Hit points, the shared damage window, repair deadline and pause
deadline are captured with the owning structure's state and reinforcement deadline in the same resident transaction.
The new battle operation requires the resident world; it has no offline SQL gameplay implementation or damage RPC.
Player weapon routing and destruction/asset settlement belong to the next milestone and are not enabled by this foundation.

Damage uses catalog shield/armor/hull resonances, layer damage limits **2034â€“2036**, pause thresholds **3354â€“3356** and
`CanHaveAutoRepair` **5770**. Caps apply to actual layer HP lost, shared across all attackers. The initial timing policy
uses one-second UTC windows and one-second repair holds on qualifying damage; idle time cannot bank burst damage.
This discretization is Eden's explicit server policy, not a claim about CCP's undisclosed damage-sampling implementation.
Unopposed shield/armor repairs take 15 minutes and hull repairs take 30 minutes. Repairs restore all layers; profiles
without automatic repairs retain their damage. A coreless onlining structure cannot repair itself into operation.

Medium structures retain their armor reinforcement while low power and have no hull reinforcement. Large/XL low-power
structures skip armor reinforcement. Flex structures skip armor reinforcement and, when low power, hull reinforcement.
Abandoned structures have no reinforcement. Exits use the pinned security/size delay followed by the selected UTC hour
and bounded jitter. The offset is deterministically mixed from structure identity and attack time, and the resulting
deadline is saved once. Hull exposure/reinforcement offlines services and suspends unfinished industry at that transition.
Low-power structures become abandoned after seven days; onlining a fueled service restores full power.

Native slim, directory and hangar replies publish actual layer fractions, repair/reinforcement timers, pause state and
abandonment. Initial/AddBalls damage states and subsequent damage/slim updates share those values. Contact caching
includes battle changes and exact repair, pause and abandonment deadlines; warmed unchanged views retain their identity.

References and evidence:

- [UniWiki vulnerability](https://wiki.eveuniversity.org/Vulnerability),
  [CCP vulnerability states](https://support.eveonline.com/hc/en-us/articles/208289385-Upwell-Structures-Vulnerability-States),
  and [CCP asset safety](https://support.eveonline.com/hc/en-us/articles/208289365-Asset-Safety) were reviewed against the pinned client.
- Inert inspection of the original build-3662168 archive, SHA-256
  `37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368`, used `structures/const.pyj`,
  `structures/structure.pyj` and `structures/reinforce.pyj`. The native state transitions, repair durations, reinforcement
  delays and exit jitter are present in these modules. No client code was executed.
- Local EVE.js **261108.1** has no Git revision. `structureState.js` SHA-256
  `E12D487E9E2E0E5486CE03A60B1178EAADC5A74C0BE39813B026F16206376C1C` and `structureConstants.js` SHA-256
  `C15E3373280732B15B01500E18139AC1A4D3A04FAE53384AC1744BD8F7149B09` identify the checked reference.
  Its layer-cap/pause and auto-repair boundaries informed the audit; no AGPL source was incorporated.
- Blue codecs and Destiny serialization remain existing adapters. The Carbon revisions recorded in the hull-geometry
  section below remain the reference for those boundaries.

**Automated validation:** Exact cap development passed one case; the expanded rule checks passed 12 cases.
The exact resident rollback/checkpoint/reopen scenario passed in 7.07 seconds including runner overhead (build 5.21 seconds).
An intermediate 80-case affected run passed 79 and failed an old assertion that expected Low Power after a year without
fuel. It now expects Abandoned. After this and native damage-state additions, the focused correction filter passed
21 cases in 7.97 seconds (build 4.84 seconds). The final hull-exposure/service-offline flow passed its exact filter
`FullyQualifiedName=Eden.Protocol.Tests.Structures.StructureBattleTests.ResidentReinforcementExitsAtTheSavedDeadline`
in 7.02 seconds (build 8.48 seconds).

Final root tool/solution restore and build passed with zero warnings/errors (build 8.11 seconds). The affected filter
combined `FullyQualifiedName~` selectors for `Eden.Protocol.Tests.Structures`, `Eden.Protocol.Tests.Docking.StructureServiceTests`,
`Eden.Protocol.Tests.Hosting`, `Eden.Protocol.Tests.Persistence.WorldMigrationTests`,
`Eden.Protocol.Tests.Persistence.WorldInitializationTests`, `Eden.Protocol.Tests.Persistence.CheckpointOwnershipTests`, and
`Eden.Protocol.Tests.Flight.MissileBallparkTests` using `|`. It completed **213 cases: 203 passed, 10 failed** in 62.59 seconds.
All ten failures are checkpoint-copy concurrency cases: four variants of
`WarmDockedCallsCompleteDuringCheckpointCopyOnFirstPeriodicPoll` and six of
`PausedCopyAllowsWarmFramesAndIndependentMovementWhileSqlWritersWait`. The same exact ten cases were independently run
on unchanged main **fa2e1c6e577c8c3b65568b78acf0fb3a7eb17852** and all ten failed there as well (48.56 seconds;
build 10.49 seconds). They stall in the existing `update.tackle` phase during paused checkpoint copying; the structure
foundation does not establish a fix for that baseline issue. No test was disabled or weakened. Validation stopped after
the failed affected run, investigated the exact failures against main, then completed the remaining policy checks.

Both Python policy suites passed (**8 catalog**, **9 formatting**). Formatting/140-column verification passed for
**2,578 files**; these policy and formatting checks took 28.15 seconds. Changed documentation local links, UTF-8,
final-newline/trailing-whitespace checks and `git diff --check` passed. The full .NET suite was not requested or run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** None for this foundation; incoming player weapon routing is not yet enabled.

### Asset safety and public structure loot

Schema **186** adds resident `WorldAssetSafety` and `WorldStructureLoot` records. Personal and corporation assets can be
manually collected without docking permission after leaving the source structure. Active boarded ships, installed
industry blueprints and fitted structure equipment are not collected by the manual operation. Automatic collection
during destruction/decommissioning is recorded in the incoming-combat/loss milestone below.

Collection preserves inventory identity, nested contents, singleton state, blueprint runs/efficiencies, stored hulls,
fitted equipment and loaded charges. Corporation roots retain their original hangar-division flags. Pending wraps do
not appear as ordinary usable inventory. After five days, an authorized owner can deliver to an accessible dockable
structure or NPC station in the original system. After twenty days, an undelivered wrap relocates to its saved NPC
destination exactly once. Selection uses straight-line catalog system coordinates: high-security origins use the
nearest high-security station system, while low/null origins use the nearest low-security station system. Stable station
identity breaks ties. Eligible system centers are batch-read and the resulting destination is cached by catalog generation.

Delivered wraps use native inventory type **60**, group **1319**. Native inventory bindings list the contents and
support ordinary `Add`/`MultiAdd` recovery to personal hangar/cargo or authorized corporation office divisions/deliveries.
Recovery charges **0.5%** of reference value in the original system or **15%** after remote relocation, rounded up to
cents. Valuation includes nested containers, fitted equipment and loaded charges; Eden's existing reference market
estimates are used with a positive catalog-base-price fallback. Personal recovery debits its owner; corporation recovery
requires the current CEO/Director and debits the master division. Journal reference type **123** comes from the pinned
accounting-entry catalog. Custody, fees, partial-stack metadata, wallet history and wrap cleanup commit atomically.

Recovery rechecks ownership, location, quantities, current authority, available funds and destination capacity. Wraps
cannot be moved, trashed, traded or contracted to bypass recovery. A completed selection cannot be recovered a second
time. Native created/delivered notifications invalidate the relevant asset views. Session monitors use owner-specific
resident revisions and the next saved relocation deadline; unchanged/absent results require no SQL or fitting work.

Wormhole and Pochven collection ejects assets into public structure-loot containers instead of protective wraps. These
records retain their actual structure source and personal/corporate owner, independently of NPC or ship wreck records.
They use ordinary range-checked inventory looting, abandoned-loot presentation, scanner discovery and checkpoint capture.
Container lifetime follows Eden's existing two-hour loot policy. Exact expiry removes the container and its nested
contents; taking a fitted hull or container transfers its surviving contents with it. There is no offline SQL gameplay path.

Pinned-client inspection used `assetSafetySvc.pyj`, `assetSafetyControllers.pyj`, `assetSafetyEntry.pyj` and
`assetSafetyDeliverWindow.pyj` from the original archive identified above. Native methods include character/corporation
wrap lists, wrap names, destination lookup, personal/corporate collection and `MoveSafetyWrapToStructure` with its
`destinationID` keyword. The Blue codecs remain unchanged. Gameplay references are
[UniWiki asset safety](https://wiki.eveuniversity.org/Asset_safety) and the CCP asset-safety article linked above.
EVE.js **261108.1** was checked as a reference: `structureAssetSafetyService.js` SHA-256
`E501D18B46C52530FD8B77F6EC2B13208BE694ACDE35D5BA01493E835869E421` and `structureAssetSafetyRecovery.js` SHA-256
`37980597DBF4A5A978F2FB9D09E1820BF2E17710E23D1D168DACB1F22015BAFD`. No AGPL source was incorporated.
The reproducible structure catalog fixture now includes type 60 through `tools/Export-StructureCatalog.py`.

**Automated validation:** Development began with
`FullyQualifiedName=Eden.Protocol.Tests.Structures.AssetSafetyTests.NativeSafetyPreservesNestedAssetsAndShipsThenChargesAtomicPartialRecoveryAfterReopen`.
The native/checkpoint/recovery flow passed after adding real fitted-module and loaded-charge custody, bound inventory
reads, ordinary drag recovery and the zero-SQL revision monitor (one case, 7.60 seconds; build 5.10 seconds).
The expanded safety class passed nine cases in 9.51 seconds (build 10.05 seconds). A failed initial wormhole check exposed
the NPC/ship source requirement in ordinary wreck storage; dedicated structure-loot records replaced that attempted path.
Its final exact case covers personal/corporate ejection, lawful third-party looting, warm contact reuse and exact expiry,
passing in 7.20 seconds (build 4.91 seconds). The additional contract-bypass rejection passed in 7.21 seconds
(build 5.10 seconds).

Final root tool/solution restore, build (zero warnings/errors; 8.57 seconds), and **426 affected .NET cases** passed.
The filter combined `FullyQualifiedName~` selectors for `Eden.Protocol.Tests.Structures`,
`Eden.Protocol.Tests.Inventory.CargoContainer`, `Eden.Protocol.Tests.Inventory.ItemTrashTests`,
`Eden.Protocol.Tests.Persistence.PilotInventoryTests`, `Eden.Protocol.Tests.Persistence.InactiveShipInventoryTests`,
`Eden.Protocol.Tests.Docking.TypedInventoryTests`, `Eden.Protocol.Tests.PilotSessions.CorporationStorageTests`,
`Eden.Protocol.Tests.PilotSessions.PlayerTradeTests`, `Eden.Protocol.Tests.Persistence.ContractAssetTests`,
`Eden.Protocol.Tests.PilotSessions.ContractAssetServiceTests`, `Eden.Protocol.Tests.Loot.WreckTests`,
`Eden.Protocol.Tests.Information.InformationTests`, `Eden.Protocol.Tests.Persistence.WorldMigrationTests`,
`Eden.Protocol.Tests.Persistence.WorldInitializationTests` and `Eden.Protocol.Tests.Hosting` using `|`.
Both Python policy suites passed (**8 catalog**, **9 formatting**), as did formatting/140-column verification
(**2,588 files**) and `git diff --check`. The complete root validation sequence took **166.22 seconds**.
Changed documentation local links, UTF-8, final newlines and trailing whitespace were checked separately.
No full .NET suite was requested or run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Inspect a delivered wrap's native contents and recover one partial stack;
verify corporation recovery in an office division. Native rendering and interactions remain unverified.

### Incoming weapons, destruction and automatic custody

Resident ship turrets (including the existing grouped/advanced weapon path), ordinary missiles and combat drones now
attack vulnerable Upwell structures. Native targeting recognizes structure contacts; turret/launcher activation and drone
orders retain their lock checks. Catalog range, hit/application calculations, skills, capacitor, ammunition, cooldowns
and overload costs stay in their existing paid operations. All attackers share the saved layer cap/repair window.
Native damage/slim updates follow committed hits, reinforcement removes eligibility and fatal removal uses destruction
presentation. Missile targets retain their distinct saved kind; launch pays immediately and physical contact applies damage
once. Earlier structure-bound missile contacts settle before structure reads, repair deadlines and later paid attacks.
The resolver can publish an actual contact before the next full movement boundary; it never publishes a predicted future
contact. A per-system resident flight index and per-action advancement guard avoid scanning combat history or replaying
the same work for every attacker. Reopened checkpoints rebuild that index from saved flights. Shots against invulnerable,
unavailable or out-of-range targets do not pay their weapon costs.

High-security structure attacks are rejected before payment because CCP requires an active declared war and Eden has no
war authority yet. Low/null-security structure offense extends the attacker's weapons/PvP timers. Sovereignty hubs retain
their Entosis ownership mechanics. Existing area-weapon and utility-EWAR target sets are unchanged by this milestone.

Fatal damage and completed unanchoring settle custody in the same resident transaction as the terminal structure state.
Boarded ships retain their pilot, cargo and fittings and enter space outside the structure's range sphere. Connected docked
pilots adopt the saved evacuation and discard old location bindings. Stored personal/corporate roots enter timed asset
safety; wormholes/Pochven eject them, and abandoned destruction also ejects them. Already delivered wraps at a departing
destination return to pending custody under their original identity, or eject their contents where safety is disabled.
They cannot become inaccessible nested wraps. Unanchoring completes at its saved deadline despite docked ships or personal
assets; the packaged hull remains in space for the existing capacity-checked recovery operation.

Installed industry jobs cancel atomically, release their blueprint and consume reserved copy runs consistently with Eden's
existing cancellation semantics. Completed manufacturing/reaction output, or reserved inputs for unfinished jobs, receives
Eden's normal independent 50% stack-survival roll and surviving stacks become public loot. Rigs and integral equipment are
removed; ordinary structure fittings/fuel use the same 50% policy, and the installed quantum core always survives as its
original item. Nested contents of destroyed fittings are removed with them. The wreck survival policy is Eden's documented
choice, not an assertion of CCP's private loot implementation. Services/access records are cleared on destruction, active
moon extractions end, and the destroyed structure remains a nonusable tombstone for saved custody/history. Rejected or
rolled-back settlements cannot publish custody, consume a shot or create a duplicate drop.

The vulnerability/deployment/asset-safety CCP articles and UniWiki pages linked above remain the gameplay references.
In particular, CCP's vulnerability states explicitly require wars for high-security attacks; its deployment article
specifies core survival and recovery of the decommissioned hull from space. Local EVE.js **261108.1** lifecycle/loot
boundaries were checked in `structureState.js` (hash above) and `structureDestructionLootState.js`, SHA-256
`B41A9AB1B70C69AE0FB7A25F8993FE513096F2B208DE940DD1DF07A89AF55A75`. The reference drops all non-rig fittings;
Eden instead uses its existing ship-loot survival policy. No AGPL source was incorporated. Native Blue/Destiny adapters
remain unchanged; no client code was executed and there is no offline SQL structure-combat/departure implementation.

**Automated validation:** The initial exact filter was
`FullyQualifiedName=Eden.Protocol.Tests.Structures.StructureAttackTests.PaidTurretDamagesTheStructureAndRejectedReinforcementCannotPayAgain`.
The paid-shot check passed after training its real catalog requirement (7.34 seconds; build 6.17 seconds). The expanded
warm check initially exposed that the minimal NPC test fixture deliberately disables the production fitting cache; it
now explicitly supplies that cache and asserts zero SQL commands, connections and fitting calculations. Native lock/fire,
reinforcement lock loss and delivered-wrap relocation passed together; fatal settlement/checkpoint and exact-deadline
evacuation/unanchoring also passed. Physical missile payment/contact/persisted-target checks passed in 7.29 seconds
(build 4.87 seconds), and combat drone attack/reinforcement-stop passed in 7.34 seconds (build 8.19 seconds).
The abandonment/rollback and connected docked-session evacuation checks passed two cases in 8.21 seconds (build 5.05 seconds).
The combined attack/rule filter passed **33 cases** in 15.49 seconds (build 8.49 seconds) before the final timing additions.
The same ordinary/repair-pending/subsecond missile flow passed three cases in 8.85 seconds (build 5.13 seconds);
checkpoint reopening is also exercised by its final form. Assisted attacks retain the owner's surface-distance control
limit and cannot replay a rejected trigger after returning into range.

An initial broad root run built successfully (4.56 seconds) but ended without a final TRX and is recorded as incomplete.
Its observed failure was `PilotCombatTests.FatalShotSendsCombatTextBeforeRemovingTheTargetsClientRecord`, whose offline
fixture failed in the unchanged resident-only Abyssal monitor before the combat assertion. The exact test reproduced on
unchanged main **fa2e1c6e577c8c3b65568b78acf0fb3a7eb17852** (11.16 seconds; build 3.46 seconds). Its fixture now starts
a resident world, matching the existing correction on `codex/full-suite-fixes`; no assertion or gameplay fallback was
weakened. That case and the assisted-structure range/retrigger case passed together in 7.76 seconds (build 7.02 seconds).
The broad root validation restored tools/solution, built with zero warnings/errors (11.15 seconds), and passed
**1,061 affected cases** in three independent batches:

| Batch | Passed | Elapsed including runner |
| --- | ---: | ---: |
| Core structure/combat/missile flows | 340 | 124.69 seconds |
| Drones/delegation/session replays | 364 | 221.53 seconds |
| Supporting services/persistence/hosting | 357 | 362.97 seconds |

Each selector below was prefixed with `FullyQualifiedName~Eden.Protocol.Tests.` and combined with `|`:

- Core: `Structures`, `Docking.StructureServiceTests`, `Combat.PilotCombatTests`, `Combat.MissileCombatTests`,
  `Combat.PilotMissileTests`, `Combat.ResidentMissileLaunchTests`, `Combat.MissileFlightTests`, `Combat.MissileRulesTests`,
  `Combat.NpcMissileTimingTests`, `Combat.AdvancedWeaponTests`, `Combat.WeaponBankTests`, `Flight.MissileBallparkTests`.
- Drones: `Combat.ResidentDroneTests`, `Combat.PilotDroneTests`, `Combat.CombatDroneTests`, `Combat.DroneDelegation`,
  `Combat.DroneSynchronizationTests`, `Combat.LiveDroneRulesTests`, `Combat.DronePerformanceReplayTests`,
  `Combat.DroneDamageRangeTests`.
- Support: `Thermal.Overload`, `Loot.WreckTests`, `Industry`, `Hosting`, `Persistence.WorldMigrationTests`,
  `Persistence.WorldInitializationTests`, `Sovereignty.SovereigntyResource`, `Sovereignty.SovereigntyDeploymentTests`.

Both Python policy suites passed (**8 catalog**, **9 formatting**), as did formatting/140-column verification
(**2,593 files**) and `git diff --check`. The broad root sequence took **750.64 seconds**. Its compiled code precedes
three final changes: skipping structure lookup in legacy SQL combat, avoiding global flight participants for unknown
structure IDs, and accepting an actual zero-time contact inside the structure range ball.

Final root tool/solution restore, build (zero warnings/errors; 7.64 seconds), and **213 focused cases** passed after
those changes. Its selectors were `Structures.StructureAttackTests`, `Combat.PilotCombatTests`,
`Combat.MissileCombatTests`, `Combat.PilotDroneTests`, `Combat.DroneDelegationTests`, `Combat.DroneDamageRangeTests`, and
`Combat.MissileFlightTests`, using the same prefix/join convention. This includes all four ordinary, pending-repair,
subsecond and inside-range-ball missile variants, each reopened from a saved flight. Both policy suites and formatting
verification passed again; the final root sequence took **125.66 seconds**. Documentation local links, UTF-8, final
newlines and trailing whitespace passed separate checks. The full .NET suite was not requested or run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Observe native structure targeting/damage/reinforcement; observe a docked session's
forced departure and public core loot. Rendering and gameplay feel remain unverified.

## Station and structure hull geometry â€” November 16, 2026

The Athanor's catalog radius is a 45 km docking/range ball, while its visible model extends roughly 15 km from its
origin. Treating that range ball as solid blocked the docking-ring interior and pushed undocking ships away from the
authored exits. Stations and player structures now retain their existing range radius but use conservative local hull
spheres and capsules for physical collision. Their native rigid parent is non-massive; only its collision children are
massive, preserving clear passages. The server uses the same primitives for swept ship collisions, warp-in clearance,
login recovery, developer translocation and nearby-object decloaking. Compound profiles have spatial trees; warmed
queries visit local candidates rather than every primitive. Catalog profiles and rotated structure views are shared.

Undocking starts at the build-3662168 `client/graphiclocations` directional locator selected for the ship category.
Saved structure yaw rotates both exit and collider. Clearance extends along the exit ray only when required by actual
hull primitives, with room for the ship and its launch motion. It no longer extends every exit beyond the range sphere.
The existing native speed multiplier/cap, real-ship launch, session handoff and checkpoint paths remain in use.
Objects without matching graphical metadata retain the previous sphere fallback. No saved-world schema changes are needed.

`tools/Export-StaticCollisionGeometry.ps1` automates the offline pipeline described in the
[client guide](../development-client.md#exporting-station-and-structure-geometry). It enumerates catalog categories
3 (stations), 22 (deployables), 23 (starbases), 40 (sovereignty), 46 (orbitals) and 65 (structures), resolves their SOF or
direct graphics, verifies cached resource MD5s and converts unique meshes. Surface sampling and deterministic capsule
merging cap each profile at 2,000 primitives; the radius conservatively covers the sampled triangles and grid rounding.
Smaller objects use finer grids. Animation and private CCP collision authoring are not reproduced; these are approximate
visible-hull envelopes. Turrets, attached effects and procedural effects are outside the mesh bake. Specialized gameplay
for an exported type is not implied by the presence of its geometry. Moving ships retain native spherical collisions.

Coverage is **399 candidate types, 384 model-backed types and 117 unique meshes**. The 15 graphics-free exclusions are
listed in the [generated provenance manifest](static-collision-3662168.json). The embedded data occupies **816,699 bytes**
compressed (**7,483,737 bytes** raw JSON). Mesh inputs total **413,967,242 bytes** before conversion; these inputs and
the much larger intermediate glTF data remain outside version control. Repeated exports reuse resource hashes and are
byte-identical; failures preserve the previous complete payload. The server requires neither client files nor a converter
at runtime. The ordinary catalog setup can opt into this export with `-CollisionGeometry`.

References and provenance:

- [EVE University Upwell structures](https://wiki.eveuniversity.org/Upwell_structures) and
  [warp mechanics](https://wiki.eveuniversity.org/Warp_mechanics) distinguish docking access/range from physical obstruction.
- Pinned client resource index SHA-256: `CBA0852B46E3A1501C6C0653546C4520DBD6ABA02A51E1DA3409F5EFEB253F03`.
  The manifest records each source geometry URI, MD5, SHA-256, bounds and primitive count.
- Inert native build-3662168 client inspection used the original archive SHA-256
  `37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368` and its directional locator catalog.
- [Carbon Destiny](https://github.com/carbonengine/destiny) revision
  `114e89fa584f0e1a81eb0a679aabf41af8870072`, `Thunkers.cpp` and `Ball.cpp`, establishes rigid mini-ball/capsule tails,
  non-massive parents and local primitive coordinates. Its moving/free balls explicitly skip compound child colliders.
- External [Knit](https://github.com/neptuwunium/Knit) revision `30c0b48255bd3e8c845dd6de77aefded1eb7fd4e`
  (EUPL-1.2), with Neptune `7904bdf045ace2f0be0b7c0f87151a9c3fb48490`, is used only as an offline converter in ignored
  artifacts. No converter source is incorporated or linked into Eden. The wrapper adapts its target framework to .NET 10.
- Local EVE.js **261108.1** supplies 44 station and 19 structure exit records and captured Abyssal collision examples,
  but no general station/structure hull dataset. It was checked as a behavior reference; no AGPL code or data was copied.

**Automated validation:** The final affected filter joined `StaticCollisionGeometryTests`,
`StaticCollisionAllocationTests`, `ShipCollisionTests`, `CollisionIndexTests`, `StationUndockTests`,
`StructureControlTests`, `Eden.Protocol.Tests.Structures.StructureTests`, `SiteWarpInRuleTests`, `TranslocationTests`,
`TranslocationTargetTests`, `SpatialModuleTests`, `SpatialModuleProtocolTests`, `SpatialModuleOperationTests`,
`AnomalyWarpInTests` and `MovementPersistenceTests` with `FullyQualifiedName~` and `|` through `tools/Test-Feature.ps1`.
All **248 cases passed in 125.90 seconds** (build 1.28 seconds), after integrating main `b3f052d2`.
Coverage includes all 117 baked profiles, native child serialization, open passages and capsule boundaries,
allocation/candidate bounds, all 44 existing station exit variants, all 20 published Upwell hulls with rotated exits,
resident Athanor setup/checkpoint/control, immediate undock-to-warp and saved arrival. Existing flow checks were extended;
four new pure cases cover the compound geometry feature. Six earlier native stream checks were corrected to parse
optional child tails instead of assuming fixed 39-byte rigid records. Exact-method development checks passed before
the combined run. An additional actual-Athanor open/solid point assertion passed in the existing control flow
(one case, 13.47 seconds; build 10.04 seconds).

Tool/solution restore and the solution build passed with zero warnings/errors (0.76 seconds).
The catalog Python policy suite passed eight cases in 1.044 seconds and formatting policy passed nine in 0.015 seconds.
Formatting verified 2,471 files and the 140-column rule. Full export/conversion, pinned-converter bootstrap, preflight,
fresh-profile equivalence, byte-identical incremental export, wrong-client rejection and output preservation passed;
the latter combined exporter smoke checks took 2.42 seconds. Documentation links and `git diff --check` passed.
The full .NET suite was not requested or run.

**Live acceptance:** Not performed; agent client testing is disabled. Native rendering and bumping feel remain unverified.

**Optional maintainer checks:** Fly through the Athanor ring, undock without relogging and inspect the exit direction,
and compare a station's visible hull with its bump boundary after deploying the updated server.

## Manual moon extraction

Build 3662168 supplies the mining-beacon coordinates in `client/solarsystemcontent`. A refinery placed within 250 km binds to
the nearest eligible moon. Deployment checks the saved occupancy before consuming the hull. Moon Drill onlining requires
a bound refinery. The version-one Eden composition assigns a stable primary ore and optional ubiquitous ore by moon identity.
Systems displayed as 0.5 receive ubiquitous ore. Low and null security provide all five tiers. Both survey probes and the
refinery planner use this composition.

The `moonExtractions` service reads a refinery's composition and current extraction and lets its managers schedule, cancel,
and fracture a pull. An extraction lasts 6 to 56 days. Fuel interruption before arrival ends the pull. A ready chunk
fractures automatically after three hours, and its finite ore field expires two days after fracture. Extraction deadlines
and composition are saved in schema 148. A committed fracture creates ordinary asteroid records at stable identities.
Moon ore uses existing mining tools and fixed-material reprocessing. Personal mining logs and the refinery corporation's
observer ledger record the completed mining quantity and waste.

The native survey launcher consumes a fitted charge, resolves the ship's heading against the moon's catalog radius,
and reports the composition after the charge's scan time. Outstanding survey notifications are connection-scoped.
The structure service supports direct native scheduling calls. The full structure-control view, chunk movement and effects,
calendar events, and notifications to other corporation members are follow-up work.

Inert inspection of the installed build-3662168 archive used `moonmining/miningBeacons.pyj`, `moonmining/const.pyj`,
`schedulingSvc.pyj` and `ledgerSvc.pyj`. The archive SHA-256 is
`2DBC29987D01515F4F40C319F89F0276F05AC3D175D79776A03F1F8E2BAB5C6A`. The local evejs build-3396210
`moonExtractionsService.js`, `moonMiningSpaceRuntime.js`, and `surveyProbeRuntime.js` informed deadline, site, and wire behavior.
References include [CCP Moon Mining](https://support.eveonline.com/hc/en-us/articles/115005404229-Moon-Mining) and
[CCP Mining Ledger](https://support.eveonline.com/hc/en-us/articles/115005402265-Mining-Ledger).

**Automated validation.** The final affected filter covered MoonExtractionTests, StructureTests, StructureRulesTests,
StructureFuelRulesTests, ProbeScanningTests, ReprocessingTests and PersonalMiningLedgerTests. All 133 cases passed in
124.79 seconds after correcting the world-version write. The exact extraction and migration filter passed five cases
in 16.43 seconds. The solution built without warnings or errors. Both Python policy suites and format verification passed.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Inspect the beacon and placement preview at a catalog moon, align a survey launcher and inspect
the delayed result, and check the scheduling and ledger views on a copied save.

### Correct moon ore identities â€” November 16, 2026

The composition table now uses the build-3662168 asteroid identities rather than assuming consecutive type IDs.
The rare tier is Carnotite **45502**, Zircon **45503**, Pollucite **45504** and Cinnabar **45506**. The exceptional tier is
Xenotime **45510**, Monazite **45511**, Loparite **45512** and Ytterbite **45513**. The previous table selected SKINs
**45505** and **45507â€“45509**, placed Cinnabar in the exceptional tier and omitted all four exceptional ores.
Named ore values document the gaps, and the shared AutoMining ore list receives the corrected identities as well.

All 20 base moon ores are reachable in both lowsec and nullsec, supplying all 20 raw moon materials through the existing
reprocessing recipes, including Dysprosium, Neodymium, Promethium and Thulium. This closes the ore-identity gap in the
moon-material supply path; it does not claim end-to-end acceptance of every T2 recipe. Highsec remains restricted to
ubiquitous ore. The deterministic seed, tier selection, ore order and 70/30 split are unchanged. Equal tier selection in
lowsec and nullsec and the fixed shares remain Eden policy, not a reproduction of Tranquility's moon distribution.

New extractions use the corrected composition after the updated server starts. Existing extractions retain their saved
composition, and existing asteroid fields are not rewritten. Cancel and restart an unfinished pull to use the corrected
table. In particular, Efa V â€“ Moon 2 now supplies 70% Xenotime and 30% Zeolites instead of the old Cinnabar result.

References: [CCP Moon Mining](https://support.eveonline.com/hc/en-us/articles/115005404229-Moon-Mining) and
[EVE University moon ores](https://wiki.eveuniversity.org/Moon_mining#Moon-specific_Ore). IDs and yields were checked against
the local build-3662168 `client/types` and `client/typematerials`; the automated checks reuse the pinned compression
catalog fixture without creating a database. The local EVE.js **261108.1** `moonCompositionPackGenerator.js` tier table
was checked as a reference (SHA-256 **66AB96A8C96FA7ADAC96EFF39ECE82845A509072C203E8524BEEB08F4B55C36E**).

**Automated validation:** The exact-method filter
`FullyQualifiedName~MoonMiningRulesTests.CompositionSuppliesEveryPinnedMoonOreAndRawReactionMaterial` reproduced the
incorrect identities in all three security cases before the fix and passed afterward. The final affected filter joined
`MoonMiningRulesTests`, `MoonExtractionTests`, `StructureTests`, `StructureRulesTests`, `StructureFuelRulesTests`,
`ReprocessingTests`, `ReactionJobTests` and
`MiningTests.AutoMiningOrePickerListsSpawnableGradesAndTargetsTheSavedType` with `FullyQualifiedName~` and `|`.
All **117 cases passed in 38.74 seconds**, including resident extraction/checkpoint coverage and the existing reaction
and T2 manufacturing lifecycle. The nine new pure cases check pinned catalog identities, reprocessing yields for all
20 raw moon materials, fixed seed/share examples, highsec restriction and rejected inputs. A read-only audit of the
local pinned catalog also found all 20 corrected ores across Efa's 71 eligible moons.
Tool and solution restore, the solution build (zero warnings/errors, 6.23 seconds), both Python policy suites
(8 catalog and 9 formatting cases), formatting verification and whitespace checks passed. The full .NET suite was not run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Inspect a corrected rare or exceptional moon's survey/planner composition in the native client.

### Native refinery setup calls â€” November 16, 2026

User screenshots reported `Unsupported local call beyonce.MachoBindObject (CmdWarpToStuff)` when choosing a moon's
mining point, followed by `corpRegistry.MachoBindObject (GetStructureReinforceDefault)` during structure setup.
The native warp subject is **`moonMiningPoint`**, with a **moon ID**, rather than the synthetic beacon item ID.
The wire adapter now parses a named command. Static mining beacons carry their typed `MoonMiningPoint` association;
the handler resolves only an eligible beacon in the pilot's current system and then uses ordinary item warp.
Missing, foreign-system, beaconless and non-moon targets fail without changing navigation. The existing range,
fleet, capacitor and interdiction paths remain in force; the caller supplies no destination coordinates.

`corpRegistry.GetStructureReinforceDefault` now returns the native tuple **`(255, 18)`**, meaning no preferred weekday
and Eden's existing default **18:00 UTC** hour. It rejects unexpected arguments and does not change corporation state.
The build-3662168 deployment picker sends weekday **255** back to `structureDeployment.Anchor`. Deployment now accepts
and checkpoints that sentinel alongside the existing weekday values **0â€“6**, while rejecting other days and invalid
hours. Corporation-wide customization of the default hour is outside this repair.

Inert inspection of the installed build-3662168 archive confirmed
`movementFunctions._WarpToMoonMiningPoint`, `ballparkCommon.parkhelpers.warpSubjects.WarpSubjects.MOON_MINING_POINT`,
`deploymentCont.BuildUI`, `ReinforcementTimeCont.GetReinforcementTime` and
`structures.const.NO_REINFORCEMENT_WEEKDAY`. Archive SHA-256:
**2DBC29987D01515F4F40C319F89F0276F05AC3D175D79776A03F1F8E2BAB5C6A**.
The local EVE.js **261108.1** reference was checked for moon-ID resolution and the reinforcement tuple:
`beyonceService.js` SHA-256 **5C13B78749BD02C31D7844D631FC88B818B299DEFF4AC49058A860EEB3F9027D**;
`corpRegistryRuntime.js` SHA-256 **41D691387FF45DCE3CF4680DA40291557B868F0B3F589234D0EB244B77C4E35C**.
These dispatch fixes do not change Destiny simulation.
[EVE University refinery placement](https://wiki.eveuniversity.org/Upwell_structure#Moon_mining).

**Automated validation:** Exact-method filters
`FullyQualifiedName~MoonMiningWarpTests.NativeFirstBoundWarpUsesTheMoonsMiningBeacon` and
`FullyQualifiedName~StructureDeploymentDefaultsTests.NativeFirstBoundReinforcementDefaultsRoundTripIntoResidentDeploymentAndCheckpoint`
reproduced the two reported unsupported-call results before their respective fixes. Seven new resident cases cover
first-bound string/byte subjects, warp distances and arrival inside placement range, missing/foreign/beaconless targets,
unchanged state after rejection, native reinforcement tuple shape, checkpointed weekday 255 and retained hulls on
invalid timing. The first combined run passed 205 cases and failed one new fixture setup assertion: an existing catalog
record edit was not tracked. After explicitly persisting that edit, its exact rejection test passed in 6.50 seconds.

The final affected filter joined `MoonMiningWarpTests`, `StructureDeploymentDefaultsTests`, `MoonExtractionTests`,
`MoonMiningRulesTests`, `StructureTests`, `StructureRulesTests`, `StructureFuelRulesTests`, `FlightSessionTests`,
`AutoNavigationTests`, `WarpCapacitorTests`, `CooperativeFleetTests` and `PilotServicesTests` with
`FullyQualifiedName~` and `|`. All **206 cases passed in 61.35 seconds**. Tool and solution restore, the solution build
(zero warnings/errors; final build 0.81 seconds), both Python policy suites (8 catalog and 9 formatting cases), formatting
verification and whitespace checks passed. The full .NET suite was not run.

**Live acceptance:** Not performed; agent client testing is disabled. The screenshots above are user observations of
the failures, not acceptance of the corrected behavior.

**Optional maintainer checks:** Warp to a moon's mining point through its native menu, then open the refinery deployment
screen and verify that its reinforcement hour loads and the chosen configuration can be anchored.

### Native anchor configuration container â€” November 16, 2026

The user's follow-up anchoring screenshot reported "This deployment configuration is not supported."
Build-3662168 `structureDeployment.ConfirmDeployment` creates a `util.KeyVal` and sends it as the tenth `Anchor`
argument. For refineries it contains no fields. The adapter now unwraps the native `util.KeyVal`/`utillib.KeyVal`
container before validating the configuration. Nonempty unsupported configuration fields remain rejected.
Inert inspection used the same installed client archive fingerprint recorded above; the local EVE.js **261108.1**
`structureDeploymentService.js` and `sovPlayerDeployment.normalizeExtraConfig` were consulted for the argument boundary.

**Automated validation:** The existing exact-method filter
`FullyQualifiedName~StructureTests.NativeDeploymentValidatesProfileAndPersistsRequestedMetadata` passed in 6.94 seconds.
The final filter joined `StructureTests`, `StructureDeploymentDefaultsTests`, `StructureRulesTests` and `MoonExtractionTests`
with `FullyQualifiedName~` and `|`; all **87 existing cases passed in 34.18 seconds**. Tool and solution restore, solution
build (zero warnings/errors, 0.86 seconds), both Python policy suites (8 catalog and 9 formatting cases), formatting
verification and whitespace checks passed. No new regression tests were added, as requested; the full .NET suite was not run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Retry confirming the Athanor deployment after the updated server starts.

### Anchored structure scene publication â€” November 16, 2026

The user reported that confirming Anchor consumed the hull without showing a structure. Read-only remote verification
on revision `ab8c3e6e` confirmed a saved Athanor in state **Anchoring (2)** with a running saved deadline. No inventory
or world data was edited during diagnosis.

The native space-object `Structure._OnSlimItemUpdated` reads **`timer`** and **`upkeepState`**. Eden's slim item supplied
`stateTimer` but omitted those two fields, leaving the construction animation without its timing input. Slim items now
include the native `(start, end, None)` timer and a typed full/low power state derived from online service modules.
The existing `stateTimer` field remains available. Successful deployment calls also refresh the caller's structure
contacts immediately after commit, publishing `AddBalls` in the call's update flush. Other observers retain the normal
session contact refresh. Existing deployments use the corrected data on subsequent snapshots without resetting timers.

Shared contact views now depend on service state changes and fuel deadlines as well as structure state. Warm unchanged
reads remain shared, and newly online/offline service changes update the native power-state field coherently.

Inert inspection used the original signed build-3662168 archive, SHA-256
**37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**:
`eve/client/script/environment/spaceObject/structure.pyj` and `structures/const.pyj`.
The local EVE.js **261108.1** `structureLifecycleFanout.js` reference records matching state/power fanout dependencies
(SHA-256 **773279022600E6C3A9158154A05D8E04072A1CC59F8E2239E84B0B9CD77D5B48**).
Carbon Destiny `Thunkers.cpp` from the local **27ee40b** mirror was consulted; rigid-ball transport is unchanged.
[EVE University anchoring lifecycle](https://wiki.eveuniversity.org/Anchoring_structures).

**Automated validation:** The existing exact-method filter
`FullyQualifiedName~StructureTests.NativeDeploymentValidatesProfileAndPersistsRequestedMetadata` passed in 8.67 seconds.
Its resident native-call flow now verifies immediate `AddBalls` publication and the serialized timer/power fields.
Existing fuel checks also assert full-to-low power contacts. The final filter joined `StructureTests`, `StructureRulesTests`,
`StructureFuelRulesTests`, `StructureDeploymentDefaultsTests`, `MoonExtractionTests`, `FlightSessionTests`,
`StaticSceneFrameTests`, `BallparkTests` and `PilotServicesTests` using `FullyQualifiedName~` and `|`.
All **165 existing cases passed in 41.13 seconds**, including warm contact SQL/connection counts, structure checkpoints,
native calls and scene snapshots. Tool and solution restore, solution build (zero warnings/errors, 5.50 seconds), both
Python policy suites (8 catalog and 9 formatting cases), formatting and whitespace checks passed. No test cases or classes
were added, and the full .NET suite was not run.

**Live acceptance:** Not performed; agent client testing is disabled. The user's report and saved-state verification
establish the failure context, not acceptance of the corrected rendering.

**Optional maintainer checks:** After updating the server, reconnect and check the existing Athanor's construction model
and timer; confirm a future deployment appears immediately after Anchor.

## Structure discovery, docked login and access reads â€” November 16, 2026

The user's docking attempt persisted the ship inside the Athanor, then the docked UI queried the missing
`structureSettings.CharacterHasSetting` method. On reconnect, the selection card exposed the compatibility `StationId`
property, which is zero for long structure IDs. Native `ccSvc.PrimeCharacterDetails` added that zero to location priming,
which correctly rejected it with "Supply a list of positive location IDs." Cards now send `DockedLocationId`; before
selection, location priming resolves the structures occupied by this account's docked roster. Arbitrary saved structure
names remain outside that account-level lookup. No save repair or inventory replacement is needed.

Comparison with the local EVE.js package and inert inspection of the pinned client found these additional gaps:

| Native call or display | Finding and resulting behavior |
| --- | --- |
| `GetMyDockableStructures(systemID)` | The empty compatibility stub shadowed the real handler, which also accepted only zero arguments and returned info dictionaries. One owner now returns accessible structure IDs in the requested system; zero arguments use the current system. |
| `CheckMyDockingAccessToStructures(ids)` | Added the missing batch read, returning distinct allowed IDs through the existing authoritative docking service checks. |
| `GetStructureMapData(systemID)` | Replaced the unconditional empty payload with deployed structure map rows, including long item ID, Unicode name, system, sun orbit and coordinates. Empty systems retain the existing empty list and cache envelope. |
| `CharacterHasSetting(structureID, settingID)` | Added the boolean read with explicit native setting-to-service mapping. Settings and services have different numeric namespaces. Unknown settings are denied. |
| Structure control | Eden has no player structure-control session. Setting 17 therefore returns false, keeping the docked Control button disabled instead of advertising an unavailable feature. The native control/coring UI remains a gap. |
| Construction timer | The native bracket shows a valid countdown when selected/hovered or damaged, while visible on grid. It is not a persistent ship HUD timer. Onlining without a quantum core has no end deadline. |

The map wire adapter uses the existing `BlueRows`/`blue.DBRow`/`CRowset` constructors; no Blue codec or transport behavior
changed. Directory and access reads use `IPilotStructures`, including its resident world path and lifecycle advancement.

Read-only inspection of the active saved checkpoint confirmed the reported Athanor was `Onlining` (102), had no quantum
core, and had no state end deadline; the active ship's `StructureId` referenced that Athanor. This supports the failure
diagnosis, not client acceptance. [EVE University quantum cores](https://wiki.eveuniversity.org/Quantum_Cores) explains that
the post-core 15-minute repair timer starts after installation; the core-free waiting stage has no running countdown.

**Reference evidence:** The signed original build-3662168 archive at `.eden-original-code.ccp` has SHA-256
`37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368` (V24.01). Inert inspection covered `ccSvc.pyj`,
`structureDirectory.pyj`, `structureBracket.pyj`, `environment/spaceObject/structure.pyj`, and `structures/settings.pyj`.
The local EVE.js package `261108.1` has no Git revision metadata; independently implemented behavior references are:

- `structureDirectoryService.js`: SHA-256 `FB55BC45C7EB5DF1C39C908197BAF2C72EFCADCD2DA416E6968C0D265411A8FD`.
- `structurePayloads.js`: SHA-256 `A2FC5FFFBBE84386EC003F441414EE9F311AE945A945B9E4EB30713481CF7BA9`.
- `structureSettingsService.js`: SHA-256 `863BD687A8B353C9702D0A60576E46118C12FA34469C8836A97D6CAFB5CA0A2D`.

EVE.js is an older-client AGPL reference; no source was copied. The archive hash differs from historical V24.02 archive
records above; this pass checked the signed original used by the running prepared client.

**Automated validation:** The exact existing method
`FullyQualifiedName=Eden.Protocol.Tests.Structures.StructureTests.NativeStructureDirectoryHangarAndUndockUseLongStructureIds`
passed in 6.27 seconds. Initial runs exposed test assertions attempting to decode outbound-only constructor shapes and read
an unencoded Unicode wrapper; those assertions were corrected without broadening the inbound codec. The final filter was:

```text
FullyQualifiedName~Eden.Protocol.Tests.Structures|
FullyQualifiedName~Eden.Protocol.Tests.Maps.MapNavigationTests|
FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.PilotServicesTests|
FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.StationDirectoryTests|
FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.CharacterLogoffTests|
FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.CharacterCreationTests|
FullyQualifiedName~Eden.Protocol.Tests.Market.MarketReadCompatibilityTests
```

Joined without line breaks, this passed **152 existing cases in 42.76 seconds**, including resident structure reads,
long-ID docking/undocking and character-card priming, native directory shapes, map caching with zero warmed SQL commands,
and immediate startup dependencies. No new cases or classes were added. Tool and solution restore and solution build
(zero warnings/errors, 0.70 seconds) passed. Both Python policy suites passed (8 catalog cases in 1.034 seconds and
9 formatting cases in 0.017 seconds); formatting and whitespace checks passed. The full .NET suite was not run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** After updating the server, log back into the docked character; check the Athanor in the
system map/right-click menu and off-grid overview; select its bracket during a timed lifecycle phase.

## Docked structure control and quantum-core setup â€” November 16, 2026

Docked managers can now take and release control of an Upwell structure through the native `structureControl` service,
install its quantum core, deposit fuel and fit/online supported service modules. The earlier control limitation in the
discovery record above describes that revision; this milestone replaces it for docked setup.

The user's subsequent black-screen log (`character-selection-2216.log`, November 16 at 23:47 UTC) identified a second
startup blocker: `eveMoniker.GetLocationBindParams` raised "You have no place to go", and `hangarView` failed to acquire
its inventory. Eden supplied `structureid` but neither `stationid` nor `solarsystemid`. Native Upwell sessions bind dogma
and inventory in the solar system: both `solarsystemid` and `locationid` now contain that system while `structureid`
retains the long structure identity. The authoritative docked location remains the structure; NPC station behavior is
unchanged. This corrects the observed missing binding rather than attributing the black screen to rendering assets.

**Control and authority.** `CharacterHasSetting(..., 17)` now checks the saved owning corporation and effective Station
Manager/Director authority. `TakeControl(structureID)` also validates that the pilot's actual active ship is docked in
that structure and that its lifecycle permits docking. Exclusive connection leases prevent two controllers, and
`GetStructurePilot` reports the current controller. Repeated takeover by the same connection is idempotent; a different
connection receives a busy rejection. Release, logoff, ship change and disconnect dispose the lease. Membership/role or
structure-state changes revoke control during session updates.

Taking control changes the native session's `shipid` to the structure and supplies a rigid structure ego snapshot. It
does not change the saved active ship, move it into space, or insert a fake ship row for the structure. Releasing control
restores the real ship's session identity. Ship movement, boarding and undocking are rejected until control is released.
This is docked management control: boarding from space, controller takeover and manual structure weapons/fighters remain
outside this milestone. The management view contains static system/structure contacts, not a combat observer stream.

**Dogma and inventories.** Docked `GetAllInfo` now primes `structureInfo` as well as the actual ship. During control,
`shipInfo`, `activeShipID`, activation state and character location identify the structure, including corporation-owned
service modules. The structure binding takes precedence over the personal-hangar compatibility binding while controlled.
`GetSelfInvItem` supplies the structure's assembled corporation-owned inventory row in the system.

The pinned client calls its quantum-core bay `StructureDeedBay`, using `flagStructureDeed` (180). Its native drag/drop
uses `MultiAdd`; single-item selections now reach the existing atomic core/service/fuel operations. Core installation
checks hull-specific type, lifecycle, ownership and availability, consumes exactly one item and starts the saved repair
deadline. Fuel transfers without an explicit quantity deposit the whole selected stack. Multi-item selections are
rejected before mutation; install one item or fuel stack at a time. Controller slim updates publish core/timer and
service power changes, and service onlining publishes the native online effect.

All durable work uses the existing resident world command/checkpoint path. The control lease is intentionally connection
state and does not survive reconnect. No SQL fallback, saved-world schema migration, client patch or core grant was added.
See [EVE University quantum cores](https://wiki.eveuniversity.org/Quantum_Cores) and
[anchoring structures](https://wiki.eveuniversity.org/Anchoring_structures) for the core, repair timer and fuel/setup flow.

**Reference evidence:** Inert inspection used the signed V24.01 build-3662168 archive with SHA-256
`37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368`: native `structureControl.pyj`, `structureController.pyj`,
`eveMoniker.pyj`, `godma.pyj`, `invControllers.pyj`, `invCache.pyj` and `inventorycommon/const.pyj`.
The local EVE.js package is `261108.1`, without Git metadata. Behavior was independently implemented, with these source
hashes recording the comparison; no AGPL source was copied:

- `structureControlService.js`: SHA-256 `795AD6F1DC9B697FAE9A22D95B0A6CF8F71576E7B00C9C6D466AE256A1B33E70`.
- `structureControlState.js`: SHA-256 `DF59A6E72C50725F041717461A66FE1D806028CE1D877B4E766B464B77EF00C5`.
- `dogmaService/itemInfo.js`: SHA-256 `9314B5F2D12B998EC53A338248DD7514B0ACD07BD2CA341C3046D37FEB6E0C54`.

[Carbon Destiny `Thunkers.cpp`, revision 27ee40b](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Thunkers.cpp)
was consulted; the view reuses the existing rigid-ball/SetState adapters, without changing their binary formats.

**Automated validation:** The exact filter joined the two methods of `StructureControlTests` with `FullyQualifiedName=`
and `|`; both passed in 7.20 seconds. These are feature checks for native setup/persistence and session authority/lifetime,
not new cases for each preceding small bug. The first uses a resident Athanor and native `MultiAdd`, validates rejection
and duplicate installation without inventory changes, verifies the saved core/timer/fuel/service state and reopens a
checkpoint showing the original active ship. The second exercises exclusive leases, logoff/disconnect cleanup and an
actual simulated `IPilotSession` login/control/dogma/release sequence with native session deltas. Initial fixture failures
were corrected for wire integer normalization, the newly enabled permission and fitting after the repair timer.
The broader affected filter joined `Structures`, `PilotServicesTests`, `CharacterLogoffTests`, `TypedInventoryTests`,
`TypedSessionTests`, `ShipStorageTests`, `ApplyFittingTests`, `FlightSessionTests` and `BallparkTests`, using their full
`Eden.Protocol.Tests` namespace prefixes with `FullyQualifiedName~` and `|`. It passed **217 cases in 86.00 seconds**.
After rebasing onto main's area-weapon milestone, the combined `Structures`, `PilotServicesTests`, `CharacterLogoffTests`
and `Combat.AreaWeaponTests` filter passed **188 cases in 57.43 seconds**. The two exact control feature cases passed again
in 6.69 seconds after adding control release to destroyed-ship replacement. The six immediate ejection/fatal-session
cases passed in **9.39 seconds**, using `FullyQualifiedName~Eden.Protocol.Tests.Combat.NpcCombatTests.` with
`OldCargoRefreshDuringEjectionReadsAnEmptyInventoryAndNeverRestoresAssets` and
`SessionSendsTheFatalMessageAndCapsuleBallBeforeChangingShipId`, joined with `|`.

Tool restore, solution restore and build passed; the integrated solution built with zero warnings/errors in 1.30 seconds.
Both Python policy suites passed (8 catalog cases in 1.307 seconds and 9 formatting cases in 0.016 seconds). Formatting
was applied and verification checked 2,451 files; line-width and whitespace checks passed. The full .NET suite was not run.

**Live acceptance:** Not performed; agent client testing is disabled. The user's black-screen log is failure evidence,
not acceptance of the corrected hangar or control view.

**Optional maintainer checks:** Update/restart and reconnect to the docked Athanor; take control and place its matching
quantum core in the Core Room; after the repair timer, deposit fuel and fit/online the desired service modules.

## Structure moniker and docked-system correction â€” November 16, 2026

The next user login reached inventory startup but failed with `invbroker.MachoBindObject (GetInventoryFromId)`.
`character-selection-25108.log` (November 17 at 00:11 UTC, November 16 local time) also recorded "The ship's system
membership changed" during dogma priming. These were distinct server-side gaps exposed after the prior session-field fix.

The native inventory/ship/clone moniker tuple is `(solarSystemID, groupSolarSystem)` for an Upwell session. The router
still validated the structure's ID/group because the physical ship was docked. It now validates the solar system and its
named group constant, matching `eveMoniker.GetLocationBindParams`; NPC station monikers retain their station binding.
The first call embedded inside `MachoBindObject` still runs through its ordinary owner and permission checks.

Resident combat loading and economic membership updates, and resident heat loading, also used the last NPC station's
system when a ship had no space-position row. They now resolve the saved docked structure before falling back to the NPC
station. The active ship, its stored location and its inventory are unchanged. This fixes the ownership mismatch during
`GetAllInfo` without moving the ship, modifying the save or bypassing the system-owner check.

The controller's scalar system bind and `UpdateStateRequest` now admit its existing rigid structure observer snapshot
while the real ship stays docked. Ordinary docked pilots still cannot acquire a flight ballpark or issue movement commands.

References remain the signed V24.01 archive/hash and native moniker/inventory modules recorded in the preceding milestone.
The local EVE.js package `261108.1` was checked again at `structureControlState.js` (its retained system location during
control) and `inventory/invBrokerService.js` (`GetInventoryFromId`'s bound inventory context). No source was copied and no
Blue/Destiny binary format changed. The gameplay context remains
[EVE University quantum-core setup](https://wiki.eveuniversity.org/Quantum_Cores).

**Automated validation:** The existing exact method
`FullyQualifiedName=Eden.Protocol.Tests.Structures.StructureControlTests.ControlRequiresAuthorityAndExclusiveSessionAndReleasesOnLogoffAndDisconnect`
passed in 6.48 seconds. Its existing simulated session now sends native embedded inventory and dogma binds and a
controller ballpark bind, with the last NPC station deliberately in another system. Initial runs identified the remaining
controller refresh rejection, which was corrected. No test cases or classes were added. The final filter joined
`Eden.Protocol.Tests.Structures`, `Eden.Protocol.Tests.PilotSessions.PilotServicesTests`,
`Eden.Protocol.Tests.Flight.FlightSessionTests`, `Eden.Protocol.Tests.Thermal.ShipHeatTests` and
`Eden.Protocol.Tests.Combat.ResidentQuerySessionTests` with `FullyQualifiedName~` and `|`.
All **193 cases passed in 55.04 seconds**. Tool and solution restore, solution build (zero warnings/errors, 0.85 seconds),
both Python policy suites (8 catalog cases in 1.198 seconds, 9 formatting cases in 0.018 seconds), formatting and whitespace
checks passed. The full .NET suite was not run.

**Live acceptance:** Not performed; agent client testing is disabled. The supplied screenshot and log establish the
failure, not successful native rendering after this change.

**Optional maintainer checks:** Update/restart, reconnect to the docked character, then open its inventory and take control.

## Structure hangar container and controlled undocking â€” November 16, 2026

The next two screenshots showed `invbroker.MachoBindObject (GetInventory)` during hangar loading and taking control.
Native `invCache.GetInventory` rewrites `containerHangar` (10004) to `containerStructure` (10014) whenever `structureid`
is present. Eden handled only 10004. Both named container IDs now resolve to the pilot's existing personal hangar while
docked at a structure. Its `GetSelfInvItem` supplies structure type/owner metadata, and `List(flagHangar)` includes the
real docked ship needed by `hangarView.GetShipItemFromHangar`.

The user's undock report had two related causes. `character-selection-26632.log` showed `AbortUndock` failing in
`hangarView.GetStationType` on the same missing container read. Separately, native
`structureDocking.TryUndockOnServer` sends `(structureID, None)` with `ignoreContraband` when controlling a structure.
The control guard rejected that request. The structure-control owner now validates the structure and requested ship,
launches the pilot's actual saved ship, then releases control and sends its normal flight snapshot. Rejected requests
retain the control lease. The existing command handling continues to reject moving or boarding the ship through other
control-mode calls.

Undocking now supplies the known system ID with the position write. Otherwise clearing `StructureId` could leave a new
position without a system and select the last NPC station's system during subsequent reads. Native location-based bound
references also use the same wire-location calculation as session fields: docking/undocking at an Upwell structure
retains the system location. This keeps the cached inventory manager usable across the transition.

Inert inspection used the original signed V24.01 build-3662168 archive, SHA-256
`37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368`: `invCache.pyj`, `inventorycommon/const.pyj`,
`hangarView.pyj`, `undockingSvc.pyj` and `structureDocking.pyj`. Local EVE.js package `261108.1` was checked at
`inventory/invBrokerService.js` (`Handle_GetInventory`) and `structure/structureDockingService.js` (`Handle_Undock`).
Its structure-container alias and active-ship fallback agree with the pinned client. No source was copied.
[EVE University Upwell services](https://wiki.eveuniversity.org/Upwell_structure#Upwell_Structure_Services) provides the
gameplay context for personal hangars and docking. This change uses existing inventory and movement operations and
does not alter Blue or Destiny binary formats.

**Automated validation:** The two existing `StructureControlTests` methods passed in 7.40 seconds after adding native
container requests before and during control, structure metadata and hangar ship assertions, native controlled undock
with `shipID=None`, and retained system bindings after the transition. The simulated `IPilotSession` includes both
embedded `GetInventory` binds and the controlled undock request. No cases or classes were added. Development runs caught
the missing system ID on undock. An initial affected run had four NPC movement snapshot mismatches after an unconditional
system write; the correction retains existing NPC location representation and resolves the structure's system when
leaving it. The two structure methods and four affected movement methods then passed in 12.27 seconds.

The final `FullyQualifiedName~` filter joined `Eden.Protocol.Tests.Structures`,
`Eden.Protocol.Tests.Docking.TypedInventoryTests`, `Eden.Protocol.Tests.PilotSessions.PilotServicesTests`,
`Eden.Protocol.Tests.PilotSessions.TypedSessionTests`, `Eden.Protocol.Tests.PilotSessions.CharacterLogoffTests`,
`Eden.Protocol.Tests.Flight.FlightSessionTests` and `Eden.Protocol.Tests.Persistence.ShipStorageTests` with `|`.
All **211 cases passed in 87.90 seconds**. Tool and solution restore, final solution build (zero warnings/errors,
0.64 seconds), both Python policy suites (8 catalog cases in 1.161 seconds, 9 formatting cases in 0.016 seconds),
formatting verification across 2,451 files and whitespace checks passed. The full .NET suite was not run.

**Live acceptance:** Not performed; agent client testing is disabled. The supplied screenshots and logs are failure
evidence; rendering, button behavior and the native undock animation remain unverified after this change.

**Optional maintainer checks:** Update/restart and reconnect; open the hangar and take control; undock from control and
confirm the original ship appears outside the Athanor.

## Athanor setup flow audit â€” November 16, 2026

The user's native lobby now opened without RPC popups, but its outside view remained black; undocking did not complete
until a relog, and the displayed exit was wrong. The same setup exposed missing personal ship/hangar rows while controlling
the structure, rejected core transfers through the hangar, and fitting calls that differed from the synthetic explicit-slot
path. These observations are pre-fix failure evidence, not agent live acceptance.

The audit checked the local EVE.js package **261108.1** and inert disassembly of the original signed V24.01 client archive,
SHA-256 `37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368`. The relevant native modules are
`structureView.pyj`, `structureDocking.pyj`, `structureHangarViewSvc.pyj`, `invCache.pyj`, `invControllers.pyj`,
`fittingUtil.pyj`, `fittingSlotController.pyj`, `fittingDogmaLocationUtil.pyj` and moon-mining `schedulingSvc.pyj`.
The C# changes are independent implementations; EVE.js source was not copied.

| Native setup step | Owning behavior after this audit |
| --- | --- |
| Docked outside view / `beyonce.MachoBindObject` and `UpdateStateRequest` | A docked pilot can bind the system park and receive a rigid scene with the structure as camera ego, without publishing the docked ship as a combat contact. Empty native binds also send this snapshot. |
| `structureDocking.Undock(structureID, shipID)` | Validates the current structure and real ship, accepts `None` only while controlling, releases control and explicitly sends the real ship's space snapshot. Upwell docking retains the system session field, so this cannot rely on an NPC-station system-entry callback. |
| Exit locator and orientation | Uses the structure type's pinned directional locators, hull size and saved yaw; slims carry the same yaw in native degrees. Launches still extend past the enclosing collision sphere; authored hull collision shapes remain a separate gap. |
| Structure `GetInventoryFromId` / `List` | Authorized managers get their personal items and docked ships alongside corporation service bays; guests retain personal hangar access. Container `10014` remains a personal-hangar alias. |
| Hangar `Add` / `MultiAdd`, including `qty=None` | Transfers personal cargo into the hangar through the existing inventory operation. Core/fuel/service-bay transfers retain their authoritative structure operations. |
| `MultiAdd(..., flag=flagAutoFit)` | Atomically fits a batch into free service slots, checking type restrictions, occupied slots, duplicate IDs and module limits. A failed batch leaves inventory unchanged. |
| Native unfit `Add(moduleID, structureID, flag=flagHangar)` | Removes an offline removable service into the pilot's personal hangar; an online service is rejected. |
| `dogmaIM.Activate` / `Deactivate` with effect `online` | Uses the controlled structure identity; the real docked ship remains the saved active ship. Explicit `SetModuleOnline` / `TakeModuleOffline` share the same effect and attribute notifications. |
| `GetAllInfo`, hull/module `ItemGetInfo`, `GetShipConfiguration` | Primes installed corporation-owned services, online effects, actual CPU/power load and the adjusted hourly fuel amount used by the native scheduler. |
| Service and hangar-state updates | Sends native `OnStructureServiceChanged(structureID)` and `OnHangarViewStateUpdated(structureID, state)`. A scoped monitor updates docked timer/power changes and returns immediately before pilot selection. |
| Moon resource, extraction, schedule and cancel reads | Existing RPCs match the native planner; the extended resident setup flow now exercises them after onlining a Standup Moon Drill. Existing extraction tests cover saved deadlines, fracture and expiry. Calendar creation and moving chunk visuals remain unimplemented. |

The locator fixture now includes the pinned hull graphics and directional-locator records exported from catalog build
**3662168**. Its exporter remains read-only. New gameplay state uses the resident structure/inventory owner and existing
checkpoint projection; this audit adds no SQLite gameplay path, migration or test case.

Reference EVE.js files include `structureLocatorGeometry.js` (SHA-256
`40D816E82149345A4B82E681A17BE0E435BE2B1B83EB1936F5DD958B11D33C46`), `inventory/invBrokerService.js`
(`8CEC4112DE1B312B547E39B789391B0D8E18DFCA95AA61170F82AEFADC1E9651`) and `structureHangarViewManagerService.js`
(`55C9ECD1EE647D7FBF71EEA945EA3AF2DEE82D4347F42A3DBE37838966980299`). Carbon Destiny revision
`114e89fa584f0e1a81eb0a679aabf41af8870072` was consulted for rigid-ball and compound-collision behavior;
this setup change leaves its binary ball format unchanged.

[EVE University structure fitting](https://wiki.eveuniversity.org/Fitting_upwell_structures),
[moon mining](https://wiki.eveuniversity.org/Moon_mining) and
[quantum cores](https://wiki.eveuniversity.org/Quantum_Cores) provide gameplay context. The screenshot still shows
Onlining; a structure without a core cannot tether. Tethering itself and detailed station/structure hull collisions remain
unimplemented Eden features, as distinct from missing setup RPCs. See
[CCP tethering](https://support.eveonline.com/hc/en-us/articles/207568639-Upwell-Structure-Ship-Tethering).

**Automated validation:** The two existing structure-control cases were extended; no new
cases were added for individual bugs. They cover native personal hangar/cargo access, core installation, automatic batch
fitting and rollback, online/offline/unfit/refit, service dogma/fuel values, moon planning, checkpoint reopening, a docked
outside-view bind, and both controlled and ordinary undocking. An initial broader run exposed an account-phase monitor
guard omission and was stopped after 142 reported cases (125 passed, 17 failed); the guard was corrected, and the four
focused selection/control cases then passed in 6.94 seconds. A subsequent affected run was stopped after 250 reported
cases (248 passed, 2 failed): the new fuel display queried a service profile for the Sovereignty Hub's integral equipment.
Non-service integral modules now retain an absent fuel value, and the 25 affected hull/recovery/control cases passed in
17.00 seconds after the correction.

The final `FullyQualifiedName~` filter joined `Eden.Protocol.Tests.Structures`, `Docking.TypedInventoryTests`,
`PilotSessions.PilotServicesTests`, `PilotSessions.TypedSessionTests`, `PilotSessions.CharacterLogoffTests`,
`Flight.FlightSessionTests`, `Flight.StationUndockTests`, `Persistence.ShipStorageTests` and `Mining.MiningTests`, each
under `Eden.Protocol.Tests`, with `|`. All **321 cases passed in 125.49 seconds** (build 2.91 seconds). Tool and solution
restore, solution build (zero warnings/errors, 0.91 seconds), both Python policy suites (8 catalog cases in 2.270 seconds,
9 formatting cases in 0.084 seconds), formatting verification and whitespace checks passed. The full .NET suite was not run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** After updating and reconnecting, verify the docked outside scene and undock transition;
open Ship/Item Hangars and transfer the core; take control, fit/online the drill and open the moon planner.

## Structure compression and gas decompression â€” November 11, 2026

Milestone 7 implements all three pinned `structureCompressionMgr` calls: `CompressItemInStructure(itemID)`,
`DecompressGasInStructure(itemID)` and `GetMyGasDecompressionEfficiency()`. The two mutations return the native tuple
`(sourceItemID, sourceTypeID, sourceQuantity, outputItemID, outputTypeID, outputQuantity)`; the efficiency read returns
`(structureEfficiency, characterEfficiency)`. Unavailable facilities report zero efficiencies; mutation requests fail
without converting inventory. Item IDs/argument forms are validated at the selected-pilot boundary.

Compression uses the pinned **client/compressibletypes** mapping and **type list 336**, rather than a name match or a
batch conversion guess. The current fixture has **212 mappings**, of which **127** are admitted for structure
compression. Units are converted **1:1** while volume drops. Gas and moon ore are excluded by the structure type list;
legacy batch-compressed ore is not manufactured. Inputs are owned, non-singleton stacks in the current personal
structure hangar or an eligible active-ship hold. Output retains the stack ID, owner, location and flag; existing
compressed stacks remain separate until ordinary stacking is requested.

Gas decompression accepts the **25** uniquely mapped compressed harvestable gases, from the current personal structure
hangar. It returns raw gas and floors the resulting whole-unit quantity. The Standup Reprocessing Facility I supplies
catalog attribute **3262 = 0.8**; Athanor/Tatara hull attribute **3261** adds **0.04/0.10**, and skill **62452** adds its
attribute **3260 = 1%** per effective level. Total efficiency is capped at 100%. Typed Gameplay rules perform checked
quantity calculation with decimal fractions. A one-unit stack can yield zero: that compressed stack is removed and the
reply has `outputItemID=None`, matching the pinned client's zero-output branch. Compression/decompression do not credit
mining yield, mining history or skill rewards and do not charge a conversion fee.

Both operations require actual docking at an accessible Upwell structure with its reprocessing service online.
Authoritative structure/fuel deadlines advance before eligibility is checked. NPC stations, ships in space, inaccessible
or offline structures, foreign ownership, remote inventories, singleton items and invalid source holds reject. Gas
cannot be decompressed from ship cargo. Resource conversion, fuel advancement and removal of a zero-output stack share
one transaction; failed writes preserve the prior fuel deadline, fuel quantity and source stack. Replays cannot convert
the same already-converted/deleted input twice. Ordinary saved inventory provides restart/checkpoint persistence; no
schema change or acquisition substitute is introduced.

`OnItemChange` now includes the old **typeID at inventory column 1** when an existing stack changes type. This was absent
from Eden's shared inventory adapter and is necessary for compression's in-place type/volume update. Changed group and
category fields also retain their old column-6/7 values, including compressed-gas to raw-gas group changes. Gas-loss quantity
changes retain the existing quantity/stacksize delta fields; zero-output removal uses the existing graveyard-location
form. The low-level codec and transport are unchanged.

Scope: this milestone exposes personal structure sources. Corporation-office sources require structure-office support,
which Eden does not currently provide. Ship/fleet compression remains a separate service, and excluded gas/moon ore is
not enabled at structures. The service does not seed materials or invent compressed-gas acquisition.

References: [EVE University compression](https://wiki.eveuniversity.org/Compression) describes facility eligibility,
1:1 compression, exclusions and gas losses; [Resource Processing skills](https://wiki.eveuniversity.org/Skills:Resource_Processing)
describes the decompression skill. Inert build **3662168** inspection covered `itemcompression/client/itemCompression`,
`itemcompression/client/gasDecompression`, `dogma.const` and pinned catalog mappings/attributes. The archive SHA-256 is
`37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368`; no client code was executed.
[CompressionCatalog.json](../../tests/Eden.Protocol.Tests/Fixtures/CompressionCatalog.json) was exported read-only from
the build-verified catalog using [Export-CompressionCatalog.py](../../tools/Export-CompressionCatalog.py): **430 types**,
SHA-256 `1437B04E02BE44DF6A7BE6832D309A191B4A68A8CDF0C21F348D16D7A5B4D023`. Existing explicit Blue tuple/dictionary/DBRow
adapters remain based on [Carbon Blue revision fcbf0c2](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp).

EveJs installed source **261108.1**, without Git metadata, was used as a behavioral reference and independently reconciled
with the client/catalog. SHA-256 source identifiers:

- `services/mining/structureCompressionMgrService.js`: `FFA2DF95DA3D131F9617783A98668AAC609BA1192DD0EE139113A825D8CF1E22`
- `services/mining/miningIndustry.js`: `DB43B0480F1D552BEC7FEAF8AA8ACCBDA88062A8667B954E171D5436FBFFF95A`
- `services/reprocessing/reprocessingRuntime.js`: `5DC0522D36B9BA0499FB6ED2142E6A5D18AB7C90A1301AF5F48581A653D0E4FA`
- `services/reprocessing/reprocessingConstants.js`: `0FA783F1158FFDFDA579A0302A105B25A8CA2AF7A920C09AF507F097B7DD6B43`

**Automated validation:** Tool/solution restore and solution build passed with zero warnings/errors. Final filter
`FullyQualifiedName~StructureCompressionTests|FullyQualifiedName~Eden.Protocol.Tests.Structures.StructureTests|FullyQualifiedName~Eden.Protocol.Tests.Reprocessing|FullyQualifiedName~TypedInventoryTests|FullyQualifiedName~PilotInventoryTests|FullyQualifiedName~InventoryPersistenceTests|FullyQualifiedName~ShipInventoryRulesTests|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting`
passed **449 cases**, including **23 compression cases**, zero failed/skipped, in **67.68 seconds** with four test workers.
Both Python policy suites passed (**8 catalog**, **9 formatting**), formatting verification checked **1,724 files**, and
changed-document text/link/anchor/whitespace checks passed. Required checks took **95.85 seconds** on base `7fbf0592`
plus this milestone. Data-driven cases exercise all **212 mappings**, **127** accepted structure types, **25 gas types**,
and three hull bonus variants at all six skill levels. Other coverage includes disk/resident native replies and inventory
identity updates, whole-unit loss/zero output, wrong owners/locations/holds, NPC and in-space rejection, access revocation,
offline/exact-fuel-deadline rejection, forced update/delete rollback including fuel, replay and reopen persistence.
Development began with the exact native compression/reopen method (2 cases), followed by catalog, boundary and forced
failure filters. Fixture corrections addressed outbound DBRow inspection, onlining fuel and trigger DDL parameters;
final checks cover the resulting tree. No full .NET suite was requested or run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** At a fueled structure reprocessing service, compress an ore stack and inspect its updated
name/volume; decompress a gas stack and compare output with the shown efficiency. Native rendering remains unverified.

## Implemented structure foundation

All 20 published player Upwell hulls in build 3662168 share persistent deployment,
anchoring, core installation where required, onlining, corporation ownership, access checks,
service slots, fuel storage, decommissioning, cancellation and packaged recovery.
This includes citadels, engineering complexes, refineries, navigation structures, faction
Fortizars, the Metenox Moon Drill, Orbital Skyhook and Sovereignty Hub. Legacy starbases and their modules are excluded.
The reproducible [catalog fixture](../../tests/Eden.Protocol.Tests/Fixtures/StructureCatalog.json)
contains the exact hull, integral equipment, core and service profiles used by automated tests.

Dockable hulls provide personal hangars, assembled ship storage/boarding, guests, docking and
undocking at their actual 64-bit structure identity. Core-dependent fitting and free repair,
insurance, personal manufacturing, research, copying, invention and refinery ore reprocessing
use existing Eden operations with structure access and service availability enforced.
Engineering hull time/material bonuses and refinery ore yield bonuses come from the catalog.
Fuel expiry pauses unfinished industry at the exact expiry deadline; refueling and onlining
resume it without granting progress during suspension. Completed jobs retain their completion.

The native directory, default corporation profile, deployment call, structure hangar state,
inventory binding, service online/offline controls, docking and undocking have named wire
adapters. Structure appearance, native input and window behavior remain unverified.
Warmed populated and empty system contact reads share immutable views without SQL connections,
commands or fitting calculations; deployment, rename and saved lifecycle deadlines refresh them.
Native location/asset name lookups include deployed structures. Existing and absent structure
names share resident caching and refresh when their owning structure row changes.

## Administration

The Settings page has a **Structures** tab with independent **Anchoring speed**,
**Core onlining speed** and **Decommissioning speed** controls. Each accepts **0.1â€“100Ã—**,
defaults to **1Ã—**, and participates in revision comparison, JSON import/export and save for
restart. A speed of 2Ã— halves the duration. Existing deadlines retain their original terms;
a newly started phase uses the active setting at that time. Fuel's hourly cadence is unchanged.

Ordinary dockable hulls anchor for 24 hours and online for 15 minutes after core installation.
Flex hulls use 45-minute anchoring and decommissioning; ordinary dockable hulls decommission
for seven days and Metenox for three days. These base timers are scaled when started.
An overdue decommission waits for all docked ships and personal assets to leave rather than
deleting them. Service modules stop at the saved deadline while assets delay packaging; fuel
settlement stops at that deadline as well. The owner then recovers the packaged hull and remaining corporate contents
into nearby cargo with a capacity check. Integral equipment returns inside its packaged hull,
never as a second loose copy. Other fitted equipment and an installed loose quantum core
return as contents. Duplicate recovery is rejected.

## Admission policy and remaining mechanics

Deployment requires a packaged hull in the active ship's cargo, a player corporation at least
seven days old and Station Manager or Director authority. Coordinates must be finite, within
800 km of the ship and separated from other structures and catalog scenery by 1,000 km plus
radii. Native relative and absolute coordinate paths are accepted. Trade-hub exclusions and
catalog high-security restrictions apply. Non-dockable hulls require low/null security.
This is an Eden foundation policy: sovereignty eligibility, complete restricted-system lists,
moon/planet resource placement and navigation-specific placement rules are not implemented.
Skyhooks and Sovereignty Hubs use the same 45-minute core-free foundation timers as flex
structures; their specialized lifecycle and sovereignty/resource simulation remain pending.

Access uses individual overrides, then corporation overrides, then ownership/public access.
The native profile picker currently exposes one owning-corporation profile. Custom native ACL
editing, service-specific profiles, taxes, structure trading and corporation office rental at
structures remain pending. Personal assets belong to the pilot; management authority is not
required to move a visitor's own items. Last-NPC-station fallback data cannot authorize NPC
market, clone, corporation storage or mission delivery actions while docked at a structure.

The common service inventory admits the catalog modules, but onlining explicitly rejects
services whose consumers are absent. Navigation links, jump bridges, cyno beacons/jammers,
moon extraction, automatic moon mining, skyhook resources, sovereignty claims/upgrades, reactions,
structure market/clone/loyalty services and
supercapital production are not implemented by this foundation. Navigation hulls and Metenox
can deploy, persist and recover; their specialized services do not produce gameplay effects.
Moon mining and reactions are still required for the missing T2 material sources.

Weapon/control fitting, structure combat, damage caps, reinforcement battles, tethering,
abandonment/destruction, loot and asset safety are also pending. Reinforcement day/hour choices
are validated and saved as metadata; no reinforcement combat is simulated. Fuel capacity is
an explicit shared Eden policy of 30,000 mÂ³ until specialized bays are modeled. No structure
rig bonuses or configurable service taxes are claimed. These limits must remain visible in
status reports; support for a hull's lifecycle does not imply its specialized service works.

## Protocol and persistence evidence

World schema 136 adds `WorldStructure`, `WorldStructureService`, `WorldStructureAccess`,
nullable `WorldShip.StructureId` and `WorldIndustryJob.SuspendedAt`. Saved ships retain their
last NPC station as a migration/system fallback and use `DockedLocationId` for actual storage
and service ownership. Structure identities share the inventory allocator and high-water marks;
they cannot collide with ships, items or later recovered deployments. Inventory, core, fuel,
service and recovery mutations commit together with rejection/unchanged-state checks.

Build-specific inventory flags are service slots 164â€“171, structure fuel bay 172 and core room
180. Native session changes set `structureid`, clear `stationid`/`stationid2` and retain the true
64-bit location. The hangar reply is the inert native
`structures.structureHangarViewState.StructureHangarViewState` new-object form. Its class is
explicitly allowlisted by the Blue codec; no client code is executed to deserialize it.
Service dictionaries use integer keys with explicit wire conversion.

Native industry facility modifiers use the six-part activity record and five-part modifier
tuples `(amount, category, group, type, reference)` with hull reference 12. The structure
directory and native inventory changes retain tuple/list distinctions and real ownership.

References inspected:

- EVE University: [Upwell structures](https://wiki.eveuniversity.org/Upwell_structure),
  [anchoring](https://wiki.eveuniversity.org/Anchoring_structures),
  [quantum cores](https://wiki.eveuniversity.org/Quantum_Cores),
  [structure fitting](https://wiki.eveuniversity.org/Fitting_upwell_structures),
  [permissions](https://wiki.eveuniversity.org/Structure_permissions) and
  [reprocessing](https://wiki.eveuniversity.org/Reprocessing).
- Local evejs reference version **261108.1**. This snapshot has no Git metadata;
  [source fingerprints](../structure-reference.json) identify the consulted files.
  It was a read-only behavior/wire reference; no evejs source or content is distributed here.
- Carbon Blue marshal reference at
  [`af0d65b29f951260ea09140ca11adf5d40566997`](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/Marshal.cpp).
- Inert inspection of installed prepared build-3662168 `code.ccp`, SHA-256
  `051A2F027309AA9E806273200C9F5E969CAD8675F49EF97313FF57A6B4549BB9`.
  This identifies the inspected prepared archive, not an unmodified CCP archive.
  Relevant modules: `inventorycommon.const`, `structures.const`, `structures.services`,
  structure deployment/hangar services, `industry.facility`, `industry.const`,
  `industryCommon` and `facilitySvc`.

## Validation record

Development began with exact filter
`FullyQualifiedName=Eden.Protocol.Tests.Structures.StructureRulesTests.StructureSpeedChangesDurationWithoutAllowingZeroOrUnboundedRates`
through `tools/Test-Feature.ps1` (one pass; build 5.88 s, runner 2.39 s).
The first combined structures, migration, initialization, tuning and settings-page run passed
105 cases (build 8.13 s, runner 49.41 s). Subsequent visitor inventory, native deployment and
settings validation regressions passed three cases (build 8.42 s, runner 10.13 s).
Development caught and corrected an allocator collision, an unsupported integer dictionary
wire shape, built-in recovery duplication risks and visitor inventory permission routing.
An earlier broader run was deliberately stopped after four failures in the existing module
online-state rejection test (507 other cases passed; 218.55 s runner). Structure routing had
mistaken ordinary ship IDs for structure identities; the corrected four variants passed in
an affected 75-case run (74 passed, one new clone test failed because its fixture lacked a
capsule). The fixture now exports the real capsule; that exact test passed (8.58 s runner).
Checkpoint and version-135 upgrade tests passed in that run. After integrating main
`0083bdf0`, the exact overdue-decommission contact regression passed (7.06 s runner).
These intermediate and interrupted runs are not the final validation result.

A later broad run was stopped after its market-service rejection fixture tried to fit a
market module to an Astrahus (630 passes, one failure; 529.11 s runner). The catalog correctly
requires a larger citadel; the fixture now uses a Fortizar. The corrected rejection and new
shared-fuel/decommissioning rules passed seven focused cases (7.08 s runner). These checks
cover deadline/slot ordering, independent fuel groups, zero-cost services and saved cutoffs.

**Automated validation:** After integrating main `0083bdf0`, the stable affected run passed
671 tests with no failures or skips (build 1.21 s, runner 623.43 s). Following the final location
name projection/cache patch, the solution build passed with zero warnings/errors (5.98 s),
and 21 affected structure-name, fleet, stored-ship and scanner checks passed with no skips
(build 1.23 s, runner 21.34 s). The latter checks cover the final patch; the broader run covers
the preceding integrated structure/fuel implementation. Tool and solution restore passed.
Both Python policy suites passed: eight catalog cases in 0.807 s and nine formatting-policy
cases in 0.014 s. Formatting verification checked 1,614 files and passed the 140-column check.
Local documentation links and `git diff --check` passed. No full .NET suite was requested or run.

The [recorded filters](../structure-validation.json) contain the exact base suffixes and final
location-name filter. Combine each base suffix with `testFilterPrefix` and join with `|` for
`tools/Test-Feature.ps1 -Filter`; join `postLocationNameFilters` directly with `|` for the follow-up.
TRX evidence is retained locally under `artifacts/structures-validation-20261110-070b4508` in
the primary checkout, including the interrupted runs. Automated checks establish the documented
foundation only; the specialized systems listed above remain pending.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Deploy and core a dockable hull through the native UI; dock a
second pilot and transfer personal assets; inspect structure inventory and lifecycle timers.
