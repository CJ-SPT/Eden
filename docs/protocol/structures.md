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
| Tethering | Resident protection, native effects, access and timer checks, saved repair settlement | Implemented; validation below |
| Defense | Combat fitting/control, supported defensive weapons and target updates | In progress; resident control, fitting, targeting, ammunition, defensive missiles, energy neutralizers and targeted stasis webifiers implemented. Other EWAR, warp tackle and area defenses remain |
| Service completion | Remaining native structure profiles/access, economic/clone services, ownership and specialized structure consumers | Planned |

### Structures gap audit

The defense branch covers resident gunner leases, fitting, target locks, ammunition, defensive missiles against ships and
drones, capacitor warfare and targeted webs. The integration uses that lease for Standup fighter assignment and automatic
recall. Ship locks on the structure survive a change of gunner. Controlled flight snapshots use the structure as the
ballpark ego while the pilot's personal ship stays docked. Schema 214 adds structure tackle source support to main's
schema 213 and also accepts the earlier structure-branch format.

| Area | Existing coverage and remaining gaps |
| --- | --- |
| Defense | Launch and recall of Standup fighters, guided missiles, webs and neutralizers work. Scripted warp tackle, other targeted EWAR, point defense, area and superweapon modules, and fighter attack commands need dedicated gameplay and protocol coverage. |
| Dockable services | Tether, repair, manual moon extraction, compression and supported industry are implemented. Player market orders, jump clones, corporation offices and native structure deliveries remain open. |
| Access and ownership | A corporation-default profile and per-structure access overrides exist. Native editing, per-service ACLs and fees, and ownership transfer remain open. |
| Specialized structures | Pharolux, Tenebrex, reciprocal Ansiblex links, sovereignty hubs and Skyhook resource production already have gameplay authority. Metenox harvesting and Skyhook combat, theft and transfer remain open. |
| Deployment | Corporation age, collision separation, some restricted systems, cores and hull restrictions are enforced. The full restricted-system list and conquest-index anchoring rules still need pinned coverage. |

The service audit compared Eden's onlining allowlist and directory routes with EVE.js **261108.1** and CCP's published
profiles and hull rules. The EVE.js reference manifest records the checked profile and service module hashes. Service
coverage for the navigation hulls and Skyhooks is recorded on the [capital travel](capital-logistics.md#pharolux-and-tenebrex)
and [sovereignty](sovereignty.md#sovereignty-resources-and-upgrades--november-16-2026) pages.
[CCP Upwell quick facts](https://support.eveonline.com/hc/en-us/articles/209985225-Upwell-Structures-Quick-Facts) ·
[CCP structure profiles](https://support.eveonline.com/hc/en-us/articles/208289635-Profiles) ·
[CCP Equinox notes](https://www.eveonline.com/news/view/equinox-expansion-notes) ·
[UniWiki structure fitting](https://wiki.eveuniversity.org/Fitting_upwell_structures)

**Automated validation**

The focused integration filter joined the test namespaces or classes for `Structures`, `Docking`, `Ammunition`,
`Propulsion`, `Flight.CapitalTravelTests`, `GateCloakTests`, `GateActivityProtocolTests`, `AutoNavigationTests`,
`WarpCapacitorTests`, `Combat.CapacitorWarfare`, `ResidentFitTests`, `LiveSimulationTests`, `SessionPerformanceTests`,
`AreaWeaponInteractionTests`, `CrimewatchTests`, `Persistence.WorldMigrationTests`, `DetachedCheckpointTests`,
`PilotSessions.TypedSessionTests`, `Capsuleers.CapsuleerInboundGateAcceptanceTests` and `Hosting`.
It completed in 570.88 seconds with 889 passed and the existing opt-in Standard market Rhea replay skipped.
The solution built without warnings. Both Python policy suites (8 and 9 cases), CSharpier, line width and local links passed.

**Live acceptance**

Not performed; agent client testing is disabled.

**Optional maintainer checks**

Take control of an Astrahus, lock a nearby ship, launch fighters and release control. Verify the fighters return and
the other ship retains its lock on the Astrahus.

### Standup stasis webifiers

A gunner can slow a locked pilot ship or NPC with a fitted, online Standup Stasis Webifier. The module uses the structure's
capacitor and green safety. The operator's Structure Electronic Systems skill reduces activation cost by 3% per level.
Existing tackle rules apply the target's web resistance and combine incoming speed modifiers.

| Standup variant | Base cost | Range from hull surfaces | Base speed reduction |
| --- | ---: | ---: | ---: |
| Stasis Webifier I (35943) | 3,000 GJ | 200 km | 70% |
| Stasis Webifier II (47351) | 3,600 GJ | 300 km | 75% |

Both have a 30-second active cycle followed by 60 seconds of reactivation delay. A queued repeat checks the target,
range, safety and capacitor again when it can fire. A late callback starts at most one new cycle.
The target regains speed when the web expires, even if the gunner's connection has not advanced.
Releasing control ends the web. The next gunner inherits the cooldown. Restart clears active tackle and preserves
the capacitor payment and reactivation deadline. Schema 190 allows a tackle row to reference its source ship or structure.

Build 3662168 gives group 1441 default effect 6682 (`structureModuleEffectStasisWebifier`), graphic `effects.Laser`,
and reactivation attribute 669. Skill effect 6400 applies the capacitor modifier to the fitted group. Inert reads of
the signed client archive identified below found the structure webifier group in `moduleButtonTooltip.pyj`, the
`webify` slot in `buffBarConst.pyj`, and EWAR start/end callbacks in `tactical.pyj`. The older EVE.js reference maps the
same effect name to its web family in `space/modules/hostileModuleRuntime.js`
(SHA-256 `db747aa99975012526f576e6042ba2c1c6d7d664c58cc50e0dc31fc5e550d93f`).
[CCP Upwell 2.0 defense changes](https://www.eveonline.com/news/view/upwell-2.0-structures-changes-coming-on-february-13th),
[UniWiki structure fitting](https://wiki.eveuniversity.org/Fitting_upwell_structures).

**Automated validation**

The focused tests exercise both range boundaries, rejected payments, NPC movement, delayed repeats, lease release,
checkpoint reopen, and migration of an existing ship tackle row. Two simulated sessions exercise safety, the gunner's
paid cooldown and skill-adjusted capacitor cost, the victim's speed and EWAR notifications, and observer beam start/stop.

Initial development filtered `StructureWebifierTests.WebSlowsNpcForPaidCycleAndCooldownSurvivesRelease` and
`StructureWebifierTests.NativeWebShowsGunnerAndVictimThePaidEffectAndRestoresSpeedAtExpiry` individually.
The first migration-fixture run accessed the pilot's fit while the fixture still had the old schema. Capturing the pilot and
ship IDs before installing the old schema corrected the fixture. The exact migration case then passed.

The final filter joined `FullyQualifiedName~Eden.Protocol.Tests.` predicates with `|` for `Structures`,
`Docking.StructureServiceTests`, `Propulsion.TackleTests`, `Propulsion.HicInterdictionTests`, `Thermal.MobilityHeatTests`,
`Combat.CapacitorWarfare`, `Combat.ResidentFitTests`, `Combat.LiveSimulationTests`, `Combat.SessionPerformanceTests`,
`Combat.AreaWeaponInteractionTests`, `Combat.CrimewatchTests`, `Persistence.WorldMigrationTests`,
`Persistence.DetachedCheckpointTests` and `PilotSessions.TypedSessionTests`.
All 563 affected .NET cases passed in 4m 55s. Root validation took 5m 37s including tool and solution restore, the
zero-warning build, both Python policy suites (8 + 9), formatting (2,630 files), line width and diff checks.
All 782 local link destinations in the three edited Markdown files exist.

**Live acceptance**

Not performed; agent client testing is disabled.

**Optional maintainer check**

Fire a Standup web against a legal ship target and inspect its speed and EWAR icon as the gunner's cooldown begins.

### Standup energy neutralizers and structure capacitor

A gunner can activate a fitted, online Standup neutralizer against a locked ship or NPC. Each cycle checks the control
lease, structure state, target, green safety, module cooldown and available capacitor. A paid cycle immediately drains
the target and charges the structure. A duplicate activation keeps the current cycle. A late repeat pays once at the
current time, and a replacement gunner inherits the paid cooldown.

| Standup variant (type) | Base cost (GJ) | Base drain (GJ) | Cycle | Optimal / falloff |
| --- | ---: | ---: | ---: | ---: |
| Heavy I (35925) | 1,000 | 1,500 | 15 s | 100 / 100 km |
| Heavy II (47332) | 1,000 | 1,800 | 15 s | 120 / 120 km |
| Dark Blood Heavy (46575) | 1,000 | 1,900 | 15 s | 150 / 150 km |
| XL I (35924) | 6,000 | 10,000 | 30 s | 150 / 100 km |
| XL II (47330) | 6,000 | 12,000 | 30 s | 180 / 120 km |

The pinned group 1329 uses effect 6216 (`structureEnergyNeutralizerFalloff`). Structure Engineering Systems (37799)
reduces the fitted activation cost by 2% per trained level through effect 6401. Neutralization uses surface distance,
falloff and the target's energy warfare resistance. The XL variants also use 15,000 m signature resolution.
The target can only lose its remaining charge. A gunner's docked ship has its own capacitor.

Saved-world schema **189** adds a structure capacitor balance and timestamp. Existing structures start fully charged.
Reads calculate recharge at the supplied wall time. Changing capacitor capacity or recharge through a defensive refit
settles the old curve first and preserves the charged fraction. Dogma priming includes the current charge, the gunner's
activation cost, the active effect and the paid deadline. The target receives capacitor and EWAR updates, and nearby
observers receive timed effects for visible endpoints. Other electronic warfare, tackle and area defenses remain open.

The values above come from the build-3662168 type and effect records in the checked-in structure catalog. Inert inspection
of the original archive recorded below found the structure neutralizer group in `shipModuleButton/moduleButtonTooltip.pyj`.
The older EVE.js reference maps this effect to its hostile neutralizer family in `space/modules/hostileModuleRuntime.js`
(SHA-256 `db747aa99975012526f576e6042ba2c1c6d7d664c58cc50e0dc31fc5e550d93f`).
[CCP structure fitting and skills](https://www.eveonline.com/news/view/structure-fitting-in-the-citadel-expansion),
[UniWiki structure fitting](https://wiki.eveuniversity.org/Fitting_upwell_structures).

**Automated validation**

The focused flows cover all five variants, XL signature application, operator skill, insufficient capacitor rollback,
green safety, late repeats, native charge and effect updates, battery refits, and checkpoint reopen after lease release.
All 495 affected .NET cases passed in 4m 26s. Tool and solution restore, the zero-warning build (9.90s), both Python
policy suites (8 + 9), formatting (2,625 files), line width and diff verification passed. Root validation took 5m 9s.
The three edited Markdown files had 780 valid local link destinations.

The final filter joined `FullyQualifiedName~Eden.Protocol.Tests.` predicates with `|` for `Structures`,
`Docking.StructureServiceTests`, `Combat.CapacitorWarfare`, `Combat.NpcCapacitorRuleTests`, `Combat.PilotMissileTests`,
`Combat.CrimewatchTests`, `Combat.SessionPerformanceTests`, `Flight.WarpCapacitorTests`, `Persistence.WorldMigrationTests`,
`Persistence.DetachedCheckpointTests` and `PilotSessions.TypedSessionTests`.

**Live acceptance**

Not performed; agent client testing is disabled.

**Optional maintainer check**

Activate a Standup neutralizer against a legal ship target and inspect the gunner's capacitor gauge, target EWAR icon,
and beam on a nearby observer.

### Defensive missiles against drones

Standup anticapital and multirole launchers can fire on a locked, deployed drone. A controlled drone inherits its
controller's eligibility under the structure's green safety. A ship that attacks the structure can have its drones
engaged in retaliation, and a ship whose drone is attacked can return fire against that structure. Uncontrolled drones
use Eden's neutral-target safety check and grant no retaliation through their former controller.

The paid missile follows the drone's physical movement. On contact, the resident world applies missile damage using
the drone's signature radius and speed, then updates its shield, armor, hull and inventory through the same path used
by smartbombs. If two missiles reach a drone together, destruction by the first causes the second to lose its target.
Returning a drone to bay or scooping it clears pending and completed locks and sends incoming missiles along their
last direction. Even an immediate relaunch with the same item ID requires a new lock. The saved target kind and flight
snapshot carry these outcomes across checkpoints using the existing missile schema.

The original build-3662168 archive identified below contains the existing ship and target identifiers in
`eve/client/script/parklife/targetMgr.pyj`. The missile activation and impact wire shapes carry the drone item ID.
The older EVE.js reference resolves physical targets by item ID in `server/src/space/runtime/missileEntity.js`
(SHA-256 `7b27b19c4725272cd51b6d08f930755afc6934037d9c11d1c74deee8da7eda12`).
Carbon's `Ballpark::StopAllFollowers` supplies the lost-target motion. Gameplay references cover structure launchers
and drone recovery. [Carbon Destiny source at the referenced revision](https://github.com/carbonengine/destiny/blob/114e89fa584f0e1a81eb0a679aabf41af8870072/src/Ballpark.cpp),
[structure fitting](https://wiki.eveuniversity.org/Fitting_upwell_structures), [drone mechanics](https://wiki.eveuniversity.org/Drones).

**Automated validation**

All 437 affected .NET cases passed in 4m 7s. The four drone cases cover neutral rejection before payment, controller
retaliation, saved launches, surviving and fatal hits after reopen, same-frame destruction, and recall or scoop
followed by immediate relaunch. Tool and solution restore, the zero-warning build (7.69s), both Python suites
(8 + 9), formatting (2,621 files), line width and diff verification passed. The root validation took 4m 49s.
The three edited Markdown files had 778 valid local link destinations.

The final filter joined `FullyQualifiedName~Eden.Protocol.Tests.` predicates with `|` for `Structures.StructureWeaponTests`,
`Structures.StructureTarget`, `Structures.StructureAttackTests`, `Combat.MissileFlightTests`, `Combat.MissileRulesTests`,
`Combat.MissileCombatTests`, `Combat.PilotMissileTests`, `Combat.NpcMissileTests`, `Combat.NpcMissileTimingTests`,
`Combat.ResidentNpcMissileTests`, `Combat.DroneRecoveryTests`, `Combat.CombatDroneTests`, `Combat.DroneSynchronizationTests`,
`Combat.ResidentDroneTests`, `Combat.SmartbombTests` and `Flight.MissileBallparkTests`.

**Live acceptance**

Not performed; agent client testing is disabled.

**Optional maintainer check**

Fire at a hostile drone from a controlled structure and verify the impact or recall in the gunner's overview.

### Resident defensive missile firing

Standup anticapital and multirole launchers now fire through a resident structure authority. Each launch requires the
exact current control lease, a cored operational structure, an online fitted corporate launcher, a completed resident
lock, compatible loaded ammunition and an elapsed reload/cycle deadline. The four pinned published launcher variants
(35921, 47323, 35922 and 47298) use cycles of 15, 12, 3 and 2.4 seconds respectively, before fitted modifiers.
These families consume one round and no capacitor. Failed checks preserve ammunition, deadlines and projectile state.
Other defensive effects fail closed rather than using the gunner's docked ship.

Cycles and repeat counts belong to the resident control lease. Repeated activation on the same active target does not
pay twice; changing target requires stopping. A delayed callback starts at most one new paid cycle at its current time,
with no catch-up launches. Each repeated launch revalidates targeting and legality. Deactivation finishes the paid cycle;
loss of control clears connection cycles. The existing saved module cooldown still blocks firing through a replacement
controller, and already-paid missiles survive release, disconnect and checkpoint/reopen. Destruction or removal of the
source structure expires its remaining flights. Structure IDs use actual resident membership, not a numeric cutoff.

The common physical missile simulation handles NPC, player-ship and vulnerable structure targets, including missile
application, target motion, health, ordinary destruction/reward settlement and exactly-once impact reports. Structure
contacts settle before battle/repair deadlines. Launch payloads snapshot the actual source structure, operator,
corporation, alliance and weapon for later damage history. Existing item/cooldown/missile projection is reused; schema
**188** is unchanged. The catalog fixture now includes the four structure operating skills: **408 types**, SHA-256
`98b0a8fda3954ce3210cd2498eb572aedf93b19996f77631503bf7639eb33597`.
Structure Missile Systems (37796) reads its pinned 2% damage bonus per level; normal ship skills do not enter this graph.

Structure safety is always green, independently of the operator's saved preference and private limited engagements.
In empire space, player targets require a suspect/criminal flag, outlaw status or saved incoming hostility against this
exact structure. Ship attacks now record that structure-specific hostility at the paid weapon boundary. A ship attacked
by a structure can return fire against that exact structure, including in high security; this does not invent war rights
against other structures. Rights expire with their saved deadlines. Neutral structure attacks outside null security are
likewise rejected unless this structure is retaliating. CONCORD cannot be attacked. No suspect/criminal flag is assigned
to the operator by defensive firing.

Native Activate/Deactivate, Godma effect notifications, active-effect priming, saved next-activation times and magazine
updates use the structure identity. Controlled session ticks advance cycles and physical missiles. Fitted equipment
triples are present on structure slim items for native hardpoint/missile presentation. A resident fitted-membership event
invalidates only contact views containing the changed structure; ordinary cargo quantity changes do not invalidate them.
The structure safety view does not overwrite the operator's saved ship safety preference.

Remaining defense work includes capacitor-backed EWAR/tackle, guided bombs, point defenses, doomsdays, drone targets,
and NPC retaliation against structures. Ordinary NPC aggression currently requires a real ship target; defensive launches
and impacts intentionally do not create an invalid ship reference or a fake ship. Structure-targeting NPC AI needs its own
authority. Fighter/capital systems and war declarations remain separate dependencies. This missile milestone does not
complete the overall structure program or establish native visual acceptance.

References: [UniWiki structure fitting](https://wiki.eveuniversity.org/Fitting_upwell_structures),
[CCP structure quick facts](https://support.eveonline.com/hc/en-us/articles/209985225-Upwell-Structures-Quick-Facts)
and [CCP structure skill bonuses](https://www.eveonline.com/news/view/structure-fitting-in-the-citadel-expansion).
Inert inspection of the signed build-3662168 archive (hash recorded below) checks `structure.pyj` hardpoint fitting,
`eveSpaceObject/missile.pyj` launch-module lookup and the existing native module activation shapes. Existing Blue/Destiny
codecs are reused; Carbon Destiny reference remains revision
[`114e89fa584f0e1a81eb0a679aabf41af8870072`](https://github.com/carbonengine/destiny/tree/114e89fa584f0e1a81eb0a679aabf41af8870072).
No AGPL implementation was incorporated.

**Automated validation:** Final root checks passed **784** affected .NET cases (6m48s), tool/solution restore, a zero-warning/
error build (4.48s), both Python policy suites (8 + 9), formatting (2,621 files), line width and diff verification in
**447.12s**. Documentation UTF-8/whitespace and **1,460** local links passed. The seventeen-case weapon class covers both
launcher families/tiers, paid flight checkpoint/reopen, controller replacement, late and duplicate callbacks, physical
NPC/player/structure impacts, fatal NPC attribution and exactly-once bounty payment, green safety, personal-engagement
isolation, high-security retaliation and expiry, native session ticks/effect priming, contact invalidation, operating skill
damage and rejection rollback. Warm unchanged cycle reads assert zero SQL commands, connections and fitting calculations.
Development corrected a fixture's missing NPC wreck/home data, the native module-list wire type and an attempt to put a
structure into the ordinary NPC aggression table; final focused flows and root checks passed after those corrections.

The final filter joined `FullyQualifiedName~Eden.Protocol.Tests.` predicates with `|` for: `Structures`, `Ammunition`,
`Docking.StructureServiceTests`, `Persistence.ModuleFittingTests`, `Persistence.InventoryOwnershipTests`,
`Fittings.ApplyFittingTests`, `PilotSessions.TypedSessionTests`, `Combat.MissileFittingTests`, `Combat.MissileFlightTests`,
`Combat.PilotMissileTests`, `Combat.MissileCombatTests`, `Combat.MissileRulesTests`, `Combat.NpcMissileTests`,
`Combat.NpcMissileTimingTests`, `Combat.ResidentNpcMissileTests`, `Combat.PilotCombatTests`, `Combat.NpcCombatTests`,
`Combat.CrimewatchTests`, `Combat.CombatDroneTests`, `Combat.ExpandedCombatDroneTests`, `Combat.DroneCombatTriggerTests`
and `Combat.SessionPerformanceTests`. No full .NET suite was requested or run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Fire and stop a Standup launcher in the native controlled scene; release control with a
missile in flight; inspect hardpoints, ammunition counts and the green safety display.

### Resident structure ammunition and reloads

The native structure ammunition bay now stores corporation-owned charge-category items under cargo flag 5. Managers
deposit or withdraw whole/partial stacks while docked, including atomic batch deposits. The pinned controller declares
no capacity bar for this bay. Loading accepts accessible personal cargo/hangar stock or corporation bay stock, transfers
loaded ammunition into real stacks inside the fitted modules, and validates charge groups/sizes and magazine capacity
against catalog volume. Reloads save each module's deadline in the existing resident `WorldModuleCooldown` table.
Paid cycles/reloads prevent ammunition changes. Damaged structures can replenish ammunition independently of defensive
fitting's full-shield requirement. Batch failures preserve quantities, ownership, magazines and cooldowns. Exhausting
supplied stock leaves subsequent magazines unchanged.

Loaded charges participate in the structure dogma graph. Hull/ballistic/guidance modifiers reach missile charges, while
the pinned scripts' `otherID` modifiers reach their loaded module. Cached inputs include charge identity, parent module
and immutable profile; quantity changes do not require a fitting recalculation. Unloading a script restores the module's
unscripted attributes. Existing item and cooldown projection supplies checkpoint/reopen persistence; schema **188** is unchanged.

Native `LoadAmmo`/`UnloadAmmo`, tuple `ItemGetInfo`, GetAllInfo charge priming, slot quantities, reload notifications and
personal withdrawal now route through that authority. Routing uses resident structure membership because ordinary ship
and structure IDs overlap. Native replies retain the operator's actual docked ship. Warm magazine/charge reads assert
zero SQL commands, connections and fitting calculations. Defensive firing and automatic weapon cycles remain subsequent work.

References: [UniWiki structure fitting](https://wiki.eveuniversity.org/Fitting_upwell_structures) and
[missile mechanics](https://wiki.eveuniversity.org/Missile_mechanics). Inert build-3662168 inspection of
`inventorycommon/const.pyj` and `invControllers.pyj` verifies the controller's cargo flag, charge-category filter and
inherited `hasCapacity = False`. Pinned effects 6322-6325 and 7026 supply ECM/scrambler script modifiers; hull effect 7039
and module effects 6448/6449 supply missile damage/application modifiers. The corrected six-field charge-group export
reproduces the unchanged 404-type fixture and SHA-256 recorded below. Read-only EVE.js 261108.1 comparison:
`invBrokerService.js`, SHA-256 `8CEC4112DE1B312B547E39B789391B0D8E18DFCA95AA61170F82AEFADC1E9651`.
No AGPL source was incorporated; existing Blue charge and item codecs are reused.

**Automated validation:** Final root checks passed **349** affected cases (2m16s), tool/solution restore, a zero-warning/error
build (10.45s), both Python policy suites (8 + 9), formatting (2,616 files), line width and diff verification in **206.93s**.
The ten-case ammunition class covers native custody/priming, checkpoint/reopen, damage-time replenishment, modifier-profile
resolution, scripts, exhausted batch supply, rejection/rollback and capacity validation. The earlier combined ammunition
run passed 64 structure/ordinary cases before the final additions. The final filter joined `FullyQualifiedName~` predicates
with `|` for `Eden.Protocol.Tests.Structures`, `Eden.Protocol.Tests.Ammunition`,
`Eden.Protocol.Tests.Docking.StructureServiceTests`, `Eden.Protocol.Tests.Persistence.ModuleFittingTests`,
`Eden.Protocol.Tests.Persistence.InventoryOwnershipTests`, `Eden.Protocol.Tests.Fittings.ApplyFittingTests`,
`Eden.Protocol.Tests.Combat.MissileFittingTests`, `Eden.Protocol.Tests.Combat.MissileFlightTests` and
`Eden.Protocol.Tests.PilotSessions.TypedSessionTests`. No full .NET suite was requested or run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Deposit ammunition through the native bay; inspect a reload and a loaded script;
partially unload into the bay and withdraw a stack to the personal hangar.

### Resident structure targeting and controlled scenes

Target scans and completed locks now belong to the resident control lease. World validates the exact lease, current
docking/management authority, installed core and vulnerable operational state. The source is the structure rather than
the pilot's docked ship. Target limits, scan resolution and range come from the fitted hull; ordinary ship-targeting
skills do not apply. The pinned maximum-range attribute (797) caps acquisition at 490 km, with surface distance using
both collision radii. Pending scans count against the target limit. Range loss, unavailable/warping or tethered targets,
reinforcement and shared lock cancellation remove scans and completed locks. A cancelled pending edge cannot later
complete after its target becomes available again.

The native `AddTarget` RPC stays pending until the resident wall-clock deadline, while other calls remain available.
The protocol adapter owns only deferred replies and notification comparisons. Releasing or losing control cancels
pending replies and clears outgoing locks while preserving ships targeting the structure. Acquisition state is connection
state and is intentionally not checkpointed; old lease generations cannot restore it.

Controlled SetState now includes nearby ships, NPCs, drones, missiles and wrecks around an explicit structure observer.
The real docked ship is neither moved nor published as the structure. Ordinary entity codecs and stamped updates are
reused, including subsequent peer membership/movement and NPC refresh. Logged-off ships remain visible until their
existing logoff deadline; removal then streams without resetting the scene. Normal ship snapshots retain their existing
serialization order. This provides the targeting and observation boundary for defensive firing, which remains subsequent work.

References: [UniWiki targeting](https://wiki.eveuniversity.org/Targeting),
[structure fitting](https://wiki.eveuniversity.org/Fitting_upwell_structures) and
[CCP structure defense](https://support.eveonline.com/hc/en-us/articles/209985225-Upwell-Structures-Quick-Facts).
The original build-3662168 archive recorded below was inspected inertly: `targetMgr.pyj` uses the session ship's
`maxTargetRange` and native surface distance, and its lock completion/cancellation paths consume the existing replies.
The fixture retains targeting attributes 76, 192, 564 and maximum attribute 797. Consulted
[Carbon Destiny Thunkers.cpp, revision 114e89fa](https://github.com/carbonengine/destiny/blob/114e89fa584f0e1a81eb0a679aabf41af8870072/src/Thunkers.cpp)
for existing surface-distance and ball-stream behavior. Blue formats and Destiny entity encodings are unchanged.

**Automated validation:** Final root checks passed **288** affected cases (1m25s), tool/solution restore, a zero-warning/error
build (9.08s), both Python policy suites (8 + 9), formatting (2,612 files), line width and diff verification in **131.46s**.
Warm targeting reads assert zero SQL commands, connections and fitting calculations. Native coverage includes the deferred
reply, populated controlled SetState, a complete session tick, logged-off peer retention/expiry, streamed removal and
pending-reply cancellation on release. Development corrected a test NPC without a home anchor and a test expectation that
disconnect immediately removes a ship; the existing logoff deadline is preserved. No full .NET suite was requested or run.
The final filter joined `FullyQualifiedName~` predicates with `|` for `Eden.Protocol.Tests.Structures`,
`Eden.Protocol.Tests.Targeting`, `Eden.Protocol.Tests.PilotSessions.TargetLockSessionTests`,
`Eden.Protocol.Tests.Flight.TypedFlightTests`, `Eden.Protocol.Tests.Flight.FlightSessionTests`,
`Eden.Protocol.Tests.Flight.StaticSceneFrameTests`, `Eden.Protocol.Tests.Flight.MissileBallparkTests`,
`Eden.Protocol.Tests.Combat.NpcSceneReadTests`, `Eden.Protocol.Tests.Combat.NpcSceneUpdateTests`,
`Eden.Protocol.Tests.Combat.NpcSceneIndexTests`, `Eden.Protocol.Tests.Combat.ResidentQuerySessionTests` and
`Eden.Protocol.Tests.Docking.StructureServiceTests`.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Take control with another ship nearby; complete and cancel native target scans;
release control and verify the target UI and docked ship presentation.

### Resident defensive fitting and full-power dogma

Defensive equipment now fits through the native structure inventory alongside service modules. Resident authority
validates hull restrictions, rack occupancy, launcher/turret hardpoints, CPU, powergrid, calibration, module/group limits,
rig size and shared T1/T2 rig families. Batch fitting is atomic, splits packaged stacks and transfers fitted equipment to
the owning corporation. Removing a rig destroys it. Fitting changes require docking, management authority, an installed
core, full shields and no repair/reinforcement or decommission timer. Paid cycles and loaded equipment prevent removal.
Services retain their distinct online/fuel rules, including switching to full power while under shield attack.

The structure dogma graph evaluates fitted and online effects from the pinned catalog, with hull role modifiers and
security-specific rig coefficients. Ordinary ship fitting skills do not enter this graph. The immutable fitting cache
includes exact equipment, online state, hull definitions and system security; rolled-back candidates cannot replace a
committed view. Warmed unchanged reads perform no SQL, connections or fitting calculations.

Pinned effects 7008, 7040 and the service full-power assignment connect attributes 2743/2744, 2751 and 2804/2805.
An online service multiplies shield and armor capacity by four and enables layered armor plating; hull capacity stays
unchanged. Saved-world schema **188** retains the capacity image beside saved HP. Reads preserve the damage fraction
when fitting or power changes capacity. Legacy rows without an image use their original catalog capacities. Projection
and checkpoint/reopen retain the image, so power toggling cannot restore damage or apply the multiplier twice.

References: [UniWiki fitting Upwell structures](https://wiki.eveuniversity.org/Fitting_upwell_structures),
[CCP structure quick facts](https://support.eveonline.com/hc/en-us/articles/209985225-Upwell-Structures-Quick-Facts) and
[CCP vulnerability states](https://support.eveonline.com/hc/en-us/articles/208289385-Upwell-Structures-Vulnerability-States).
The expanded build-3662168 fixture contains 404 types, including published rack equipment and compatible charges;
SHA-256 `9bffc6be9a41cf4e42973439dc18586641065b25688bd1906ac9db4c0388c805`.
Read-only EVE.js 261108.1 comparison: `structureFullPowerDogma.js`, SHA-256
`5DADA7810FF3FF529CE35D6FAC3CEB54C95A0292C0F5C960D56124BDE549DF69`. No AGPL source was incorporated.
This milestone supplies fitting and authoritative defensive attributes; defensive firing is subsequent work.

**Automated validation:** Final root checks passed **218** affected cases, tool/solution restore, a zero-warning/error
build (0.71s), both Python policy suites (8 + 9), formatting (2,608 files), line width and diff verification in **98.59s**.
Documentation encoding, whitespace and 769 local links passed. The final filter joined `FullyQualifiedName~` predicates
with `|` for `Eden.Protocol.Tests.Structures`, `Eden.Protocol.Tests.Docking.StructureServiceTests`,
`Eden.Protocol.Tests.Persistence.WorldMigrationTests`, `Eden.Protocol.Tests.Persistence.WorldInitializationTests`,
`Eden.Protocol.Tests.Persistence.ModuleFittingTests`, `Eden.Protocol.Tests.Docking.TypedInventoryTests` and
`Eden.Protocol.Tests.Persistence.InventoryOwnershipTests`. The earlier 204-case batch preceded the final native
attribute notifications and migration regression. A subsequent root attempt was interrupted after two new assertions
resolved a scoped protocol service from the root test provider; the fixture scope was corrected, both exact variants
passed, and the final batch above passed. No full .NET suite was requested or run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Fit mixed rack equipment through the native fitting view; inspect module/hull attributes
while switching a service online/offline; destroy a fitted rig and confirm the native inventory presentation.

### Resident structure-control authority

Exclusive structure control is now owned by the resident world. The protocol adapter retains the native control/session
behavior while Gameplay exposes a typed lease token and World validates docked location and corporation authority.
Separate service instances share the same resident registry. Every acquisition receives a unique lease ID; a released
token cannot authorize a later acquisition, including after checkpoint/reopen. Disposing an old lease cannot release
its replacement. Control is connection state and is intentionally not restored from a checkpoint.

The current-controller observer is a cheap registry lookup. Mutation consumers must call the authoritative `Require`
operation, which verifies the exact token and current docking/role permissions under the persistence owner before
returning the structure. This avoids acquiring the persistence owner while holding a simulation-only lease. Disconnect,
logoff, ship changes and native release retain their existing cleanup paths. Defensive fitting and firing are subsequent
parts of the completion program; this authority boundary does not by itself implement those actions.

[UniWiki structure permissions](https://wiki.eveuniversity.org/Structure_permissions) and
[CCP structure management](https://support.eveonline.com/hc/en-us/articles/208289605-Structure-Management) were reviewed
alongside the pinned native control evidence recorded below. The read-only EVE.js 261108.1 comparison used
`structureControlService.js`, SHA-256 `795AD6F1DC9B697FAE9A22D95B0A6CF8F71576E7B00C9C6D466AE256A1B33E70`;
no AGPL source was incorporated. Native request and reply shapes are unchanged.

**Automated validation:** The exact stale-lease/service-instance flow passed before expansion. The expanded three-case
control class, including checkpoint/reopen and native disconnect, passed (build 14.55s, tests 11.17s including runner).
Final root validation passed **30** affected cases using the structure control, attack, tether and pilot-selection filters;
the matched cases belonged to the three structure classes. The build had zero warnings/errors (3.23s). Tool/solution
restore, both Python policy suites (8 + 9), formatting (2,602 files), line width, diff and documentation checks passed.
Root checks took **78.25s**. An initial native disconnect check exposed an attempt to enter persistence while holding a
simulation-only lease; acquisition/validation now take the persistence owner first, and the exact check and final batch
passed. No full .NET suite was requested or run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** None required for this authority-only milestone; native defensive interaction remains
unverified until the corresponding feature is implemented and exercised.
### Native Upwell repair shop

The November 21, 2026 EVE.js follow-up found two missing pieces in Eden's docked Upwell repair path. Pinned build
**3662168** `base_repairshop.RepairSvc.GetRemoteRepairMgr` binds `repairSvc` with
`(structureID, solarSystemID, groupSolarSystem)` while docked in an Upwell structure. The NPC station form uses
`(stationID, stationID, groupStation)`. Its repair button sends `RepairItemsInStructure(itemIDs)` for Upwell facilities.
This form has no payment argument.

Eden validates the current dockable location, system and group before opening the bound repair service. Structure quotes
show a zero ISK unit cost. The repair call commits through the resident world's existing repair transaction with its
core, access and assembled-item ownership checks. Active hull and module damage updates use the existing client
notifications. The free operation creates no wallet entry. NPC stations retain their paid repair path.

Compared EVE.js **261108.1** (build **3396210**) `server/src/services/station/repairService.js` and `repairRuntime.js`,
SHA-256 prefixes `49912b2b21a9` and `bec61da2c6cc`. The pinned client archive SHA-256 was
`37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`. Inspected bytecode as data.
[EVE University Upwell services](https://wiki.eveuniversity.org/Upwell_structure#Upwell_Structure_Services) ·
[EVE University docked repairs](https://wiki.eveuniversity.org/Overheating#Docked_repairs)

**Automated validation.** The exact `UpwellRepairMonikerQuotesAndRepairsForFreeThroughTheResidentWorld` case passed
in 6.94 seconds including the runner. It covers the native nested quote and repair requests, zero pricing, invalid
binding and item selection, missing core, damage notifications, the wallet, and checkpointed damage.
The solution build took 5.62 seconds with zero warnings or errors. The focused filter below passed 101 tests in
45.27 seconds including the runner. The catalog and formatting policy suites passed eight and nine cases respectively.

```text
FullyQualifiedName~Eden.Protocol.Tests.Repair.RepairTests|FullyQualifiedName~StructureServiceTests|FullyQualifiedName~PilotBindingsTests|FullyQualifiedName~RepairHeatTests|FullyQualifiedName~StructureTetherTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting
```

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Dock a damaged ship in a cored Upwell structure and use the repair shop's Repair All action.

### Resident tethering and repair

Saved-world schema **187** adds `WorldPilotTether`. A piloted ship engages an accessible, cored, dockable structure inside
its catalog tether range. Low power and reinforcement permit tethering; abandonment, loss of access/core, pending or
completed outgoing locks, weapons timers, high-security criminal restrictions, cloak, travel and effective warp disruption
prevent protection.
High-security standing restrictions use CCP's six police thresholds. Moving or bumping beyond the surface range removes
protection immediately on authoritative reads. Unchanged monitor reads use resident state without a transaction, SQL or
fitting recalculation once warmed.

Target acquisition, ordinary and resident player weapons, NPC fire, missiles and area damage honor the protection.
Engagement clears incoming target locks, NPC aggression and incoming tackle/EWAR atomically. Bomb launchers and
smartbombs reject activation while tethered. Existing weapons timers from command bursts and burst jammers break tether.
Native `effects.Tethering` and `effects.TetheringRepair` replay after SetState and to observers; the existing effect
sequencer supplies the native HUD indicators. Rendering and HUD presentation have not been exercised in the game.

Repair need is sampled on engagement. Eden settles that repair once after a saved ten-second deadline; this cadence is
an explicit emulator policy, not a measured CCP repair rate. Settlement restores ship HP/capacitor, fitted module heat
damage and bay-drone health. Burnt-out modules remain offline; cargo modules and deployed drones remain damaged.
Subsequent capacitor discharge does not restart repair until another engagement. Rollback and checkpoint/reopen retain
the engagement and exactly-once repair boundary.

Capital cynosural travel/fighters and faction-warfare tether restrictions depend on their separate authoritative systems;
this milestone does not introduce those missing systems or claim their gameplay is implemented. Existing native structure
defensive control/fitting and the remaining structure services are the next completion milestones.

References and evidence:

- [UniWiki tethering](https://wiki.eveuniversity.org/Tethering) and
  [CCP ship tethering](https://support.eveonline.com/hc/en-us/articles/207568639-Upwell-Structure-Ship-Tethering) establish
  protection, engagement-only repair checks, fitting/bay repair boundaries and disallowed actions.
- [CCP Uprising security changes](https://www.eveonline.com/news/view/changes-to-security-status-and-abyssal-filaments)
  establish the high-security standing thresholds; Alpha status alone does not prohibit tethering.
- The original build-3662168 archive hash recorded below was inspected inertly. `structures/const.pyj` defines
  `TETHER_ENABLED_STATES`; `shipRenderEffect.pyj` consumes the targeted effect's graphic-info dictionary;
  `fxSequencer.pyj` and `shipHud/buffBarContainer.pyj` track active FX for the HUD. Pinned `client/effects` maps
  `effects.Tethering` to graphic 21190 / ShipRenderTargetedEffect and TetheringRepair to ImpactEffect. Catalog dogma
  attributes 2268 and 2343 provide tether range and module restrictions. No client code was executed.
- Read-only EVE.js 261108.1 comparison: `structureTethering.js` SHA-256
  `E1655DBF2ED0D23729126713E324E386C8CBA75678081633B38B62612C3EE143` and
  `structureTetherRestrictionState.js` SHA-256
  `73A15E89D333CE13EF2B5285DF0A78B410ECD1FC3B34D57B111FBFF209A01441`. No AGPL source was incorporated.

**Automated validation:** Development checks passed range/protection, eligibility/deadline, checkpoint/reopen,
native replay/attack rejection, fitted/bay repair boundaries, Void Bomb immunity and rejection of fresh NPC disruption.
The broad affected run passed 282 core cases (1m27s) and 464 support cases (3m30s) before the last lock-index,
legacy-position and non-damage effect guards. Final root validation passed **174** cases covering those changes,
targeting, compression, area weapons, NPC EWAR, paired session frames, monitoring and populated fitting fleets.
The final build had zero warnings/errors (4.55s); restore, both Python policy suites (8 + 9), formatting (2,600 files),
line width and diff checks passed. Final root checks took **242.98s**, including formatting application.
Initial validation exposed cleanup after host shutdown; the corrected teardown case passed before the final run.
One intermediate run completed its .NET tests but its formatting check found the subsequent source edits; formatting
was applied and the final check passed. Interrupted development runs are not counted as complete validations.
Warmed tether reads/monitoring assert zero SQL commands, connections, fitting calculations and transaction/commit time.
No full .NET suite was requested or run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Observe native tether/repair effects and HUD indicators; start a target scan or move beyond
range; verify a repaired burnt-out module remains offline.

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
