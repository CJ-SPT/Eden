# Capital ships and jump logistics

[Protocol index](../protocol.md) · [Ship and module completion](../feature-notes.md#ship-and-module-completion-program)

Planning baseline, November 20, 2026, Eden `6898acc7`, client build **3662168**. Start with a two-pilot jump-freighter
delivery to an industrial cynosural field. The shared travel rules from that delivery support later combat capitals,
fleet travel, and sovereignty infrastructure.

## Industrial cynos and jump freighters

Rhea, Nomad, Anshar, and Ark can use the native fleet-member jump to a paid industrial cyno. A Venture, Venture
Consortium Issue, or a catalog-eligible hauler, blockade runner, or deep space transport can ignite the field. Ordinary
fitting and fuel-bay inventory own hull admission, skills, economizers and the carried fuel. The freighter uses the
correct isotope from its fuel bay. The emitter pays Liquid Ozone from cargo for one ten-minute cycle. Its field is a
public warp target and appears in the map's beacon count. Fleet members receive the module beacon state and can use the
native Jump Beacon broadcast to find it.

The resident transfer revalidates the selected ship, exact beacon and both current fleet memberships at departure.
Fuel, capacitor, location, map activity and pilot timers commit together. Eden uses whole-second cooldown and fatigue
durations, with the previous fatigue read at commit. The source publishes the native departure effect before the
transfer, then the destination sends a fresh ballpark and one arrival effect. A beacon that expires before commit
cancels the jump at origin. A successful transfer's inventory and timer changes survive checkpoint reopen.

Schema 193 records fields and timers. An offline emitter remains in space through the paid field deadline. The public
field survives a reconnect or restart while its anchor is valid. Eden's online-only fleet membership invalidates jump
access when the emitter leaves or disconnects. Cargo isotopes can be carried separately and must be moved into the fuel
bay before jumping. Placement uses the pinned 5 km spawn radius and checks static destination geometry. Arrival visuals
and the precise behavior of the live game's beacon-loss race remain unverified.

The independent pinned input fixture can be regenerated with `tools/Export-CapitalTravelCatalog.py` against the
build-3662168 catalog. Runtime and persistence tests provision and move the ships and items through the resident world.
The fixture uses owned hulls and inputs. The existing production audit confirms the five recipe definitions, with the
full fresh-world sourcing chain still to verify.

**Automated validation.** Tool and solution restores passed. The final solution build finished in 2.80 seconds with
zero warnings and errors. The exact method filter
`FullyQualifiedName=Eden.Protocol.Tests.Flight.CapitalTravelTests.RheaDeliversCargoFromJitaToJanThroughVentureCyno`
passed in 11.09 seconds. The capital travel class passed 10 cases in 17.44 seconds. The final combined filter below
passed all **216** cases in **127.12 seconds** with no skips. It covers the delivery and unload, all four jump freighters,
the eligible emitter cohorts, fuel and capacitor settlement, expiry during departure, the native timer and scene
handoff, and checkpoint reopen. The catalog setup and formatting policy suites passed all 17 cases. The fixture
export matched byte for byte. Formatting, 13 local links and anchors, and `git diff --check` passed.

```text
FullyQualifiedName~CapitalTravelTests|FullyQualifiedName~TranslocationTests|FullyQualifiedName~FlightSessionTests|FullyQualifiedName~FlightBoundaryTests|FullyQualifiedName~StationUndockTests|FullyQualifiedName~CooperativeFleetTests|FullyQualifiedName~PilotInventoryTests|FullyQualifiedName~ShipContentCoverageTests|FullyQualifiedName~TypedCallResultTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~DetachedCheckpointTests
```

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Run the native Jita–Jan delivery. Verify the jump animation, capacitor gauge and
timers after reconnect. Check beacon visibility and expiry from a third pilot.

## Standard module cynos

Standard Cynosural Field Generator I (`21096`) uses the same pinned activation effect (`2857`) as the industrial
generator. Its field (`21094`) is in the jump freighter's compatibility list (`343`). Fitting and activation share the
normal catalog checks, including the hull whitelist and Cynosural Field Theory. The pinned Rapier effects `2008` and
`3526` reduce cycle time and ozone use. In the Jita–Jan fixture, a Rapier with Cynosural Field Theory I pays 90 Liquid
Ozone for a five-minute standard cycle. The Rhea takes the native fleet jump and pays its normal drive costs.

Schema 194 records the type and start of the paid field. Older industrial rows retain their existing identity and
deadline. The exact `CapitalTravelTests.RapierStandardCynoAcceptsFleetJumpFreighter` case passed in 7.34 seconds,
including the fleet notification, fuel charge, destination and equality of the field after checkpoint reopen.
Capital travel, migration, detached checkpoints, ship coverage and fleet tests passed all 58 focused cases in 20.62
seconds. Both Python validation suites passed all 17 cases.
[CCP September 2019 release notes](https://www.eveonline.com/news/view/patch-notes-for-september-2019-release)

## Pharolux and Tenebrex

Pharolux (`35840`) requires the online Cynosural Navigation upgrade (`81615`). Tenebrex (`37534`) requires
Cynosural Suppression (`81619`). The structure owner must belong to the controlling alliance. A system can anchor
one of each, including the jammer limit from the Cradle of War update. The existing Upwell service cycle
uses the pinned module fuel costs. Pharolux consumes 1,080 blocks at startup and 15 per hour. Tenebrex consumes
2,880 at startup and 40 per hour. Its five-minute spool deadline comes from attribute `2794` on service module
`35914` and survives a checkpoint. Disabling the service, loss of fuel or loss of the required sovereignty state
removes the effect. An active jammer blocks ordinary field ignition and inbound jumps to already-lit ordinary fields.

Pinned build 3662168 `cynoTravelSvc.pyj`, `structureDirectory.pyj`, `capitalnavigation.pyj` and `menusvc.pyj` confirm
`CmdJumpToStructureBeacon(structureID, solarSystemID)` and the directory's six ordered fields: structure ID,
type ID, owner ID, system ID, structure state and name. The system map uses a separate list of beacon systems.
`sovDashboard/cynoJammerIcon.pyj` consumes the `beyonce.GetCynoJammerState` deadline, or Python None for off. Eden
uses the existing Upwell access entries for beacon access and checks them again at departure. The jump shares the
existing drive costs and timers. Arrival uses the structure and ship radii plus the 12 km clearance described
by the EVE.js structural beacon runtime, with Eden's existing static geometry check.
[CCP navigation structures](https://www.eveonline.com/news/view/navigation-structures-inbound) ·
[CCP Upwell structures](https://support.eveonline.com/hc/en-us/articles/213021829-Upwell-Structures) ·
[CCP 24.01 notes](https://www.eveonline.com/news/view/patch-notes-version-24-01)

The resident Jita–49-0LI case exercises anchoring, both module fuel deposits, access, the native directory rows, a
jammer deadline reached during departure, an unpaid cancellation, persistence across checkpoint reopen, sovereignty
upgrade loss, a subsequent successful jump and expiry of the beacon's fuel. The exact
`CapitalTravelTests.PharoluxJumpRechecksAccessAndJammerDeadline` case passed in 8.23 seconds. Schema 195 adds
the optional service start timestamp. The focused capital travel, Upwell structures, sovereignty, entosis,
translocation and checkpoint suites passed all 325 cases in 130.06 seconds. Formatting, both Python policy suites
and the whitespace check passed.

## Mobile cynos and inhibitors

In build 3662168, `eveMisc.LaunchFromShip` calls `ship.Drop(items, whoseBehalfID, ignoreWarning)` and expects a
map from source stacks to the IDs of launched objects. The standard beacon (`57319`) and inhibitor (`33476`)
use the pinned `client/spacecomponentsbytype` launch distances, warm-up deadlines and one-hour decay. Both
require Anchoring III. The beacon starts a two-minute warm-up and binds to the launching fleet. It then
projects field `57425` and publishes `OnFleetJumpBeaconDeployableChange(deployableID, solarSystemID, beaconID,
ownerID, active)`. The client jumps with `CmdJumpToFleetDeployableBeacon(deployableID, solarSystemID, beaconID)`.
The inhibitor activates after one minute and blocks standard and industrial field ignition in its 100 km range.
[CCP fleet formations](https://www.eveonline.com/news/view/fleet-formations-now-live) ·
[CCP Onslaught patch notes](https://www.eveonline.com/news/view/patch-notes-for-eve-online-onslaught)

Schema 196 records deployable ownership, position, damage and deadlines. The existing turret path can damage or
destroy both deployables. Destruction removes the projected field and invalidates new or pending jumps.
The client activation and decay data comes from `spacecomponents/client/components/activate.pyj` and
`spacecomponents/client/components/decay.pyj`. EVE.js `shipService.js`, `deployableCynoRuntime.js` and
`beyonceService.js` confirm the native launch, fleet and jump paths.

The two exact cases `MobileCynoWarmsForLaunchFleetAndInhibitsModuleIgnition` and
`TurretDamagePersistsAndDestructionRetiresFleetDeployable` passed. They exercise the launch reply, cargo charges,
fleet binding after the deployer leaves, inhibitor range at the fixture position, fuel payment, a native jump,
warm-up and decay, turret damage, checkpoint reopen and loss of the jump destination on destruction.
The focused capital travel, fleet, translocation, migration, checkpoint, Crimewatch and turret tests passed all 181
cases in 102.00 seconds. Both Python validation suites passed all 17 cases.
All 14 capital travel cases passed in 14.64 seconds after adding the build's one-jammer limit.
After integrating the Faction Warfare merge on `main`, the combined capital travel, militia complexes,
enlistment and navy, fleet, Crimewatch, translocation and migration filter passed all 152 cases in 90.73 seconds.
Schema 201 opens both features on existing saves.

## Ansiblex jump gates

An Ansiblex (`35841`) locks its destination system at deployment. Deployment needs active alliance sovereignty,
the Advanced Logistics Network (`81621`), a target null-security system within 5 light years, and separation of
at least 100,000 km from each stargate. A system can anchor one Ansiblex. Each direction requires reciprocal
alignment and both services online. The source checks the pilot's membership in the alliance that holds sovereignty
there and its existing Upwell access entries. Losing a service, upgrade or claim hides the link from route discovery
and blocks departure.

Build 3662168 sets the gate capacitor to 1,250 TJ and the recharge attribute to 22,500,000 ms. Each jump charges the
source gate using the ship's base cost (`6364`) and the destination's distance from the source alliance's capital.
The zone boundaries are 5, 10, 15 and 20 light years with multipliers of 0, 2, 6, 9 and 15. An alliance without a
capital uses zone 5. The pinned restriction type list (`1000`) permits Freighters, Jump Freighters and Rorquals
and excludes most other capitals. Gate operation consumes the existing structure service fuel, and the jump applies
normal gate cloak without updating the pilot's jump-drive timers.
[CCP 24.01 notes](https://www.eveonline.com/news/view/patch-notes-version-24-01) ·
[CCP Ansiblex capacitor design](https://www.eveonline.com/news/view/force-projection-ansiblex-capacitor-update)

Pinned `structureDeployment.pyj` sends `destinationSolarsystemID` in a KeyVal passed to `Anchor`.
`mapView.pyj` consumes endpoint pairs plus allowed and denied IDs. `jumpBridge.pyj` uses the
`structureJumpBridge` component for capacitor and ship eligibility. The menus and autopilot call
`structureJumpBridgeMgr.CmdJumpThroughStructureStargate(structureID)` and expect a boolean result. The
EVE.js directory and manager reference the same endpoint and access shapes. The charge model uses the
newer pinned client and catalog.

Schema 202 records the alignment and the source gate's capacitor and timestamp. A previously deployed
Ansiblex needs to be redeployed with a destination to gain a link. The exact
`AnsiblexChargesGateCapacitorAndRechecksBothEndpointsAtDeparture` test passed in 7.51 seconds. It anchors
two endpoints through the native request, checks discovery and alliance access, cancels a jump without charge
when the destination goes offline, jumps a Rhea from 49-0LI to V-OJEN for 15 TJ, reopens the save and confirms
that the link clears when sovereignty power is lost. Decommissioning recovers one packaged hull and removes
the saved alignment without duplicating the integral service. The focused capital travel, gate, structures, map,
sovereignty and checkpoint suites passed all 201 cases in 110.24 seconds.

## Black Ops and covert travel

Black Ops drives use the pinned beacon type list (`342`). Covert modules (`28646`) create field `28650`, and
the covert mobile deployable (`59630`) creates field `59631` after one minute. Covert fields stay off the
system-wide overview and map count. They remain usable during Tenebrex jamming and inside mobile inhibitor range.
The Rapier's fitted covert cycle lasts 30 seconds. A Sin can take the native jump to that field. Jump freighter
compatibility uses its own beacon type list.
[CCP covert cyno exemption](https://www.eveonline.com/news/view/patch-notes-for-apocrypha-1.1-1) ·
[CCP covert mobile beacon](https://www.eveonline.com/news/view/patch-notes-version-19-06)

A fitted Covert Jump Portal Generator (`28652`) opens a 20-second portal for 100 GJ. The host stays at origin.
Passengers must have the eligibility attribute named by the fitted module (`3320`), belong to the current
fleet, and be within 2,500 m of the host hull surface. The resident jump pays the host's isotopes in fuel-bay
order with cargo fallback, using the EVE.js rounding rule and the pinned mass factor. The passenger receives the
jump timers. The host receives an inventory update when the fuel debit commits. If the portal, beacon,
or fleet authorization disappears before departure, the passenger remains at origin and the host keeps its fuel.

Pinned `cynoTravelSvc.pyj` gives the native bridge-open calls and
`CmdJumpThroughFleet(ownerID, shipID, beaconID, solarSystemID)`. `fleetSvc.pyj` consumes
`OnBridgeModeChange(shipID, solarSystemID, beaconID, active)`. The existing ship effect pipeline publishes
`effects.JumpPortalBO`. EVE.js `beyonceService.js` and `fleetRuntime.js` provide corresponding bridge and
fuel behavior. Schema 203 saves the paid portal cycle and its target. Fleet membership is still transient.

The exact Sin direct-jump case passed in 9.20 seconds. The covert mobile portal test passed in 8.57 seconds,
including the fleet menu notification, portal visuals, checkpoint reopen, expiry during departure, the Rapier's
paid transfer, and the host's fuel inventory update.
The broader filter ran 157 tests in 118.07 seconds. It passed 155 cases and reproduced two failures
on Ansiblex baseline `242b7ec1`: the warm fitting command budget and the resident capsule settlement rollback
fixture. Both Python validation suites and formatting passed.

## Dreadnoughts and force auxiliaries

The four empire dreadnoughts, their Navy or Fleet variants, and the four empire force auxiliaries can assemble and use
their own jump drives with standard fields. Stargates block entry to high-security space using the 0.45 boundary.
The capital branch includes the resident Upwell battle, asset safety and tethering changes through structure commit
`33a97e5e`. Dreadnought weapons use the existing paid combat path and the same Upwell vulnerability and damage rules.

The build-3662168 catalog defines Siege I/II (`20280`, `4292`) and Triage I/II (`27951`, `4294`) in group
`515`. Activation uses native effects `6582` and `6581`, and the pinned client displays the SiegeMode and TriageMode
graphics. Either mode pays Strontium Clathrates from cargo for a fixed 300-second cycle. The appropriate tactical skill
reduces fuel by 25 units per level using the pinned skill and module attributes. A committed cycle blocks navigation,
gate and jump-drive travel, docking, remote repairs and capacitor transfers to the ship. It ends at the saved
deadline after a restart or when the ship is destroyed. Fitting uses the catalog's modifiers for weapon damage,
repair amount, repair speed and range, movement, and assistance impedance. Each activation buys one cycle.
[EVE University capital modules](https://wiki.eveuniversity.org/Capital_Ship_Modules) ·
[CCP capital ship changes](https://www.eveonline.com/news/view/reworking-capital-ships-and-thus-it-begins)

EVE.js `shipActionRestrictions.js` and `activeModuleSpecialFx.js` provided a behavior and graphics cross-check.
The pinned catalog supplies the type restrictions, fuel formula, and modifiers. Eden uses its own resident
transactions and the existing active-module deadline index. Lancer Dreadnoughts and the faction dreadnoughts
below use the same siege owner.

**Automated validation.** The Revelation case covers the paid jump, a rejected short fuel stack, an eightfold
siege multiplier, a paid turret volley against an Astrahus, and restart across the exact mode deadline.
The Apostle case covers the paid jump, Triage II, a fourfold reduction in repair cycle time, an enhanced legal armor
repair, and normal loss and capsule settlement. The Vaajaita gate case covers the high-security and siege checks. All
three cases passed on the final fixture. The final integrated run passed 176 cases in 121.92 seconds with
`CapitalTravelTests`, `StructureAttackTests`, `StructureTetherTests`, `TranslocationTests`, `WorldMigrationTests`
and `StationUndockTests`. The wider 458-case run passed 456 cases in 352.70 seconds.
The Apostle case initially lacked the loss catalog records and attacker in its fixture. It passed after both were added.
The mutadaptive fitting assertion about Zarmazd assembly also failed on the pre-capital baseline `4a1c95cd`.
The two Python suites passed all 17 cases. Formatting and the 140-column check passed.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Inspect siege and triage effects from a second pilot. Verify mode timer replay
after reconnect.

## Carrier launch decks and light attack fighters

Archon, Chimera, Thanatos and Nidhoggur use the capital jump drive and the pinned carrier launch deck. Build 3662168
gives each four tubes, with at most three light and two support squadrons occupying those tubes. The fighter bay holds
reserve fighters. Loading splits only the units needed to fill the squadron. Topping up a partially depleted squadron
retains its identity. A multi-tube launch commits as a unit. Invalid selections leave the loaded tubes intact.

`fightersSvc.pyj`, `shipFighterState.pyj`, and `fighterConst.pyj` specify `fighterMgr` calls, tube states, and the
order of the rows in `GetFightersForShip`. The controller sees the native launch, count, recall and landed
notifications. Other pilots see the fighter ball and the pinned `squadronSize` in the slim item. Eve.js
`fighterMgrService.js` and `fighterRuntime.js` were used to cross-check the lifecycle and wire shapes. The underlying
inventory and movement implementation uses Eden's resident world.
[CCP fighter controls](https://support.eveonline.com/hc/en-us/articles/207659599-Fighter-Controls) ·
[CCP Citadel release notes](https://www.eveonline.com/news/view/patch-notes-for-eve-online-citadel)

Light attack squadrons use their slot-zero attack missile with pinned duration, damage, optimal range, falloff and
application. A pilot can toggle the attack, order the squadron to return and orbit, and recall it to its tube. A smartbomb
hit that destroys an individual fighter reduces the squadron count and carries excess damage into the next fighter.
Squadrons retain their tube assignment on a clean restart and stop attacking on disconnect. Recall and replenishment
reuse the same squadron item.

The initial focused cases cover a short squadron, capacity and class rejection, checkpointed
tube contents, a second pilot observing launch, and an Archon using a light attack squadron against an Astrahus. The
second test also verifies atomic rollback for a batch with an empty tube, the loss of one fighter, restart, recall and
replacement. The wider run passed all 403 focused cases in 211.49 seconds across capital travel, drone combat,
structure attacks, typed calls and migration. Both Python suites passed all 17 cases, and formatting passed.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Inspect tube and fighter HUD updates from the carrier. Confirm the visible squadron
shrinks from a second client's grid.

### Support fighter electronic warfare

The pinned `fighterabilitiesbytype` table puts the Cenobite neutralizer, Scarab ECM, Siren warp
disruptor and Dromi web in slot zero on both Tech I and Tech II variants. EVE.js `fighterRuntime.js`
cross-checks the effect families and range checks. Eden uses the same launch and recall calls as
attack fighters and applies the pinned ranges, strengths and durations through its resident
capacitor, electronic warfare and tackle owners. Strength uses the number of surviving fighters.
The Support Fighters skill applies the pinned shield capacity bonus.

The Archon test loads two support squadrons and rejects a third. It activates each racial ability
against a locked Venture, verifies the capacitor drain or active disruption and checks effect
cleanup on recall. Additional ability slots and Standup fighters remain open.

**Automated validation.** The four exact support fighter cases passed in 11.04 seconds. All 309 cases
in the final capital travel, utility drone, resident drone, pilot drone, electronic warfare and
tackle filter passed in 118.70 seconds after an 8.60-second build. Both Python suites, formatting
and `git diff --check` passed.

**Live acceptance.** Not performed; agent client testing is disabled.

### Space-superiority fighter attacks

Build 3662168's `fighterabilitiesbytype` declares ability `29` in slot zero for light
space-superiority squadrons. It uses the Micro Missile Swarm damage, range and application attributes.
The pinned anti-fighter resistance attribute `2189` has a default multiplier of 0.05. Fighter targets
declare a multiplier of 1. EVE.js `fighterAbilities.js` and the pinned `fightersSvc.pyj` support the
same item-targeted activation shape.

Eden admits published Tech I, Tech II and navy space-superiority types. These fighters can lock and
attack eligible hostile drones and squadrons through their carrier. Damage uses the existing per-fighter
health and loss accounting. The Equite acceptance case exercises the native lock and slot-zero command
against a Templar squadron. Evasive maneuvers and the secondary tackle ability remain open.

**Automated validation.** The exact Equite case passed in 7.98 seconds. All 307 cases in the final
capital travel, combat drones, resident drones, targeting and smartbomb filter passed in 173.70 seconds
after a 9.40-second build. Both Python suites, formatting and `git diff --check` passed.

**Live acceptance.** Not performed; agent client testing is disabled.

## Rorqual industrial core and shipboard compression

The Rorqual can assemble, use its jump drive and launch Excavators through the resident drone lifecycle. Capital
Industrial Core I (`28583`) and II (`42890`) use effect `4575` and the pinned 300-second cycle. Fuel comes from the
fuel bay first and then cargo. The activation applies Capital Industrial Reconfiguration's 50-unit reduction per level
and the Rorqual's 5% per level fuel bonus. Eden rounds fractional fuel requirements to a whole unit. The core locks
travel and applies the pinned ship, local repair, command burst and mining-drone modifiers. Its paid deadline
survives a server restart.

An online capital compressor requires a paid core cycle. The five capital compressor types use effect `8364`, their
respective resource type lists, a 60-second cycle and a 1,500 GJ activation cost. The 144 km surface range increases
with Fleet Compression Logistics. The core and compressor must both be active at settlement. In-space compression
checks ownership, fleet membership, range and the particular compressor's allowed input before converting the stack
in the same hold without changing its units. It uses the catalog mapping shared with structure compression.
[CCP shipboard compression release notes](https://www.eveonline.com/news/view/patch-notes-version-20-03) ·
[EVE University Rorqual](https://wiki.eveuniversity.org/Rorqual)

Pinned `itemcompression/client/inSpaceCompression.pyj` looks up `compression_facility_typelists` in the facility
ship's slim item. `itemCompression.pyj` uses `inSpaceCompressionMgr.CompressItemInSpace(itemID, facilityBallID)`.
EVE.js `industrialCore.js`, `miningIndustry.js` and `inSpaceCompressionMgrService.js` served as behavior and wire
cross-checks. Both modules support paid single cycles. Shared fleet storage and refitting, clone services, and the
full capital production and resupply chain remain open.

**Automated validation.** `RorqualPaysForIndustrialCoreAndImprovesExcavatorYield` checks insufficient fuel before
activation, a 903-unit charge and a 2.7× core yield multiplier, flight blocking and exact expiry on restart.
`RorqualAdvertisesPaidFleetCompressionAndRejectsAtTheExactDeadline` checks the slim item, a successful
100-unit conversion of Veldspar for a fleet Venture, and a rejection without conversion at expiry. All 90 cases
passed in 66.69 seconds with `CapitalTravelTests`, `StructureCompressionTests`, `MiningDroneLifecycleTests`,
`UtilityDroneIntegrationTests`, `WorldMigrationTests` and `TypedCallResultTests`.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Check the core and compressor HUD timers and compress ore from the fleet member's hold.

## P.A.N.I.C. fleet protection

The Rorqual's Pulse Activated Nexus Invulnerability Core requires a completed lock on a live ore or ice asteroid.
Effect `6719` burns out module `42522`, starts a weapons timer and applies the pinned protection buffs to the Rorqual
and eligible untethered fleet ships within 200 km. The base interval is 240 seconds, extended 10% per level of
Invulnerability Core Operation. The shield-resistance buff uses the pinned -99.99% value. Protected pilots cannot
warp, dock, jump, cloak, or use offensive modules. Combat drones cease dealing damage for the interval. Paid protection
survives a clean restart and expires at its saved deadline. Schema 205 stores it using the existing timed buff scheduler.
[CCP P.A.N.I.C. mechanics](https://support.eveonline.com/hc/en-us/articles/213529785-Pulse-Activated-Nexus-Invulnerability-Core-P-A-N-I-C)

`RorqualPanicRequiresLockedAsteroidBurnsOutAndProtectsItsFleet` covers native activation and burnout, a Venture's
shield resonance and warp restriction, and the saved protection before and after exact expiry. The broader
`CapitalTravelTests`, `CommandBurstTests`, `WorldMigrationTests`, `StructureTetherTests` and `TargetingTests`
filter passed all 73 cases in 70.13 seconds. The final P.A.N.I.C., burst and spatial-module rerun passed 15 cases
in 18 seconds of test execution. Both Python suites and formatting passed.

**Live acceptance.** Not performed; agent client testing is disabled.

## Supercapital docking, construction and fleet bridging

The four empire Titans and four empire supercarriers use their published hulls, racial isotopes and standard
cynosural destinations. A Keepstar-family citadel accepts them. A Sotiyo can assemble and launch a supercapital
but cannot receive it back from space. NPC stations and smaller Upwell structures reject docking. [CCP Upwell structures](https://support.eveonline.com/hc/en-us/articles/213021829-Upwell-Structures)

A Sotiyo's Standup Supercapital Shipyard I (`35877`) requires the matching corporation or alliance claim in
nullsec and an online Supercapital Construction Facilities upgrade (`81623`). It uses existing Upwell
onlining and hourly fuel costs. Loss of the upgrade takes the yard offline and pauses installed manufacturing.
Re-onlining the yard resumes the same job with its remaining duration and inputs. NPC facilities exclude these
recipes. [EVE University manufacturing](https://wiki.eveuniversity.org/Manufacturing)

The Titan's Jump Portal Generator I (`23953`) uses effect `2152` (`effects.JumpPortal`), a 60-second cycle and
1,000 GJ at base fit. Opening consumes 500 Strontium Clathrates from cargo, reduced by 10% per level of Jump
Portal Generation. Each completed passenger transfer consumes the Titan's racial isotope fuel using the pinned
mass factor. Stock passenger attribute `3319` defaults to eligible and preserves each hull's explicit exclusions.
The destination, fleet access, range, portal deadline, and fuel are checked again at departure settlement.
The Titan remains in its source system until it performs its own jump. The native fleet and structure bridge
calls follow the same pinned capital navigation paths as Black Ops, cross-checked with EVE.js
`beyonceService.js`.

Supercarriers use the native fighter tubes introduced for carriers, with class limits from the active hull and
published light and heavy attack squadrons. Slot zero uses the attack missile profile. The Aeon's racial Carrier
and Heavy Fighters skills apply to Cyclops attack damage. Supercarriers also use the existing
`superWeaponMgr.ActivateSinglePointTargetedModule` path for burst projectors. The racial effects `6615`–`6618`
modify their associated projector cycles, alongside Burst Projector Operation. Additional fighter abilities and
Standup fighter control remain open.

The pinned catalog cases exercise all eight hulls. The Avatar pays to bridge a Rapier from Jan to a Pharolux,
then jumps itself and docks at the Keepstar. The Aeon fires Cyclops attack missiles at an Astrahus and applies a
fuel-paid Weapon Disruption Burst Projector after its ten-second spool. With Amarr Carrier IV and Burst Projector
Operation I, the projector cycles in 228 seconds. The Sotiyo uses all 18 inputs from the pinned Aeon blueprint,
reopens during a sovereignty outage, resumes the same job, then assembles and undocks the ship it delivers.

**Automated validation.** The final filter passed all 118 cases in 63.75 seconds of test execution. It covered
`CapitalTravelTests`, `AreaWeaponTests`, `AreaWeaponBoundaryTests`, `StructureRulesTests`,
`SovereigntyResourceProtocolTests`, `IndustryFacilityLookupTests` and `FlightBoundaryTests`. The catalog and
formatting policy suites passed. An earlier broader run passed 672 of 673 in 798.39 seconds. Its drone-bay
cohort fixture omits Rorqual `28352` and fails the policy comparison. The same failure was reproduced at
`ad9dc658`.

**Live acceptance.** Not performed; agent client testing is disabled.

## Targeted Titan doomsdays

The four racial single-target doomsdays use pinned build 3662168 effects `4489`–`4492`. The fitted weapon
requires a current lock on a capital-size player ship in range. Legal activation pays 50,000 racial isotopes
from cargo and the fitted capacitor cost once, then sends the targeted effect to the grid. The resident area
weapon scheduler records the paid shot together with its deadline. At settlement, the shooter must still be in
the same ship with the weapon online, and the target must still be eligible and locked. A lost lock prevents the
hit and the spent fuel remains spent. The pinned base shot deals 2.4 million racial damage before Doomsday
Operation and the target's resistances. Damage and capital losses use the normal resident settlement and
checkpoint path. [CCP Legion expansion notes](https://www.eveonline.com/news/view/legion-expansion-notes)

The four modules have different windups. Judgment's pinned attributes set a nine-second windup, 35,000 GJ
activation cost, 30 seconds of immobility and 300 seconds without docking, tethering, cloaking, or jump-drive
travel. Doomsday Rapid Firing changes the weapon cycle independently of the travel lock. The pinned effect
names and catalog attributes drive activation. In the pinned client, `shipmodulebutton.pyj` acquires the
active target before calling the fitted effect's activation. `environment/effects/superWeapon.pyj` plays the
targeted warning. EVE.js `superweaponRuntime.js` served as a behavior cross-check.
The current targeted damage path covers piloted capitals. Lances use the point-aim path below. Reapers, the
Bosonic Field Generator, the Gravitational Transportation Field Oscillator and Phenomena Generators are separate roles.

`AvatarJudgmentPaysOnceAndSettlesAgainstALockedRevelation` exercises combat and the lost-lock counter.
Both cases verify the fuel charge, warning effect, no early hit and exact travel-lock deadlines. One verifies
the Revelation loss and capsule settlement. The other verifies that losing the lock leaves the Revelation intact.
Both reopen a checkpoint before the docking lock expires.

**Automated validation.** The exact Judgment and P.A.N.I.C. rerun passed all three cases in 15.39 seconds.
The final `CapitalTravelTests`, `AreaWeaponTests`, `SmartbombTests`, `TargetingTests`,
`SpatialModuleOperationTests`, `StructureTetherTests`, `FlightBoundaryTests`, `TypedCallResultTests` and
`WorldMigrationTests` filter passed all 180 cases in 90.86 seconds. Both Python suites, formatting and
`git diff --check` passed.

**Live acceptance.** Not performed; agent client testing is disabled.

## Titan lances

Holy Destiny, Iron Pike, Phalarica and Geiravor (`40631`, `41439`–`41441`) share pinned effect `6472`.
The client sends `superWeaponMgr.ActivateSinglePointTargetedModule(moduleID, effectID, destination)` from
`positionalControl.pyj`. `tacticalNavigation/controlPaths.pyj` anchors the fixed-length cylinder at the firing
hull's surface. `inventorycommon/const.pyj` supplies Modular Effect Beacon `41233`, and
`environment/effects/superWeapon.pyj` uses a target ball to stretch the effect.
EVE.js `superweaponRuntime.js` cross-checks the faction graphics, beacon-add order and on-grid pulse behavior.
[CCP capital rework](https://www.eveonline.com/news/view/reworking-capital-ships-and-thus-it-begins)

The fitted lance pays its isotope and capacitor costs atomically in nullsec or wormhole space. It immediately
neutralizes nearby pilots with signature and energy-warfare resistance applied. The 200 km beam warms up for
15 seconds, then checks occupants once per second for 15 seconds. Hulls have to intersect the 2.5 km cylinder.
Targets can fly out during the warning or between pulses. Signatures below 5 km take proportionally less damage.
A Titan short of capacitor cannot start the shot. The normal doomsday movement and travel restrictions apply.

The resident scheduler stores the next pulse and the fixed aim. If a frame arrives late, it samples current
occupants once and skips earlier pulses whose positions are unavailable. A full server restart cancels live
fire while preserving the paid movement and travel deadlines. Damage already committed survives restart.

**Automated validation.** Both exact `TitanLanceGivesAnEvadeWindowAndAppliesSignatureScaledPulses` cases
passed in 10.14 seconds. The final run passed all 182 cases in 77.20 seconds after an 11.12-second build. It
included `CapitalTravelTests`, `AreaWeaponTests`, `SmartbombTests`, `TargetingTests`,
`SpatialModuleOperationTests`, `StructureTetherTests`, `FlightBoundaryTests`, `TypedCallResultTests` and
`WorldMigrationTests`. Both Python suites, formatting and `git diff --check` passed.

**Live acceptance.** Not performed; agent client testing is disabled.

## Reapers and Bosonic fields

The four Reapers use effect `6201`. The pinned `positionalControl.pyj` calls
`superWeaponMgr.ActivateSlashModule(moduleID, firstPoint, lastPoint)` with the two endpoints from
`tacticalNavigation.ArcPath`. Eden validates their 30-degree sweep and the fixed 150 km midpoint before
payment. After the ten-second warning window, the current beam position advances along that sweep. The
pinned client reads `startTargetOffset`, `endTargetOffset` and `duration` to render the racial effect.
Late observers receive the remaining segment. EVE.js `superWeaponMgrService.js` and
`superweaponRuntime.js` cross-check the activation and sweep timing.
[CCP capital rework](https://www.eveonline.com/news/view/reworking-capital-ships-and-thus-it-begins)

The Bosonic Field Generator (`40633`, effect `6473`) uses the single-point call. The pinned catalog sets
a 30 km reach, a ten-second warning, a 20-second damage window and a 10 km damage signature. It pays
2,000 Strontium Clathrates and the fitted capacitor cost. The server evaluates the cone against current
ship positions. The pinned `client/effects` registry yielded no verified Bosonic-specific grid graphic.
Eden emits the existing `effects.IndicatorBeamMoving` along the aim axis during windup. The exact
Bosonic grid graphic remains open.

Both weapon families share the resident cooldown, neutralization and loss settlement used by Titan
lances. The Reaper test proves that a Venture outside the first pulse is lost when the beam reaches it.
The Bosonic cases verify an inside hit and an outside miss around the cone boundary.

**Automated validation.** All five exact Reaper, Bosonic and Titan-lance cases passed in 11.79 seconds.
The wider capital travel, area weapons, smartbombs, targeting, spatial modules, tethering, flight, typed
protocol and migration filter passed 185 cases in 74.28 seconds after a 9.92-second build. Both Python
suites, formatting and `git diff --check` passed.

**Live acceptance.** Not performed; agent client testing is disabled.

## Lancer Dreadnoughts and disruptive lances

Bane, Hubris, Karura and Valravn are admitted from the pinned Tech II hull catalog. They can use standard
capital jumps and siege. Their racial disruptive lances use effect `11691` (`debuffLance`) and the same
`superWeaponMgr.ActivateSinglePointTargetedModule` path as Titan lances. The pinned hull and skill effects
modify range, damage and capacitor consumption. A Lancer needs siege to fire and cannot cloak while the
lance is fitted. [CCP Viridian expansion notes](https://www.eveonline.com/news/view/viridian-expansion-notes)

Eden requires red safety for an untargeted lance in lowsec. A ship caught in the beam loses access to warp,
docking, tethering, gates and jump travel for 60 seconds. Incoming remote repairs apply at half strength for
the same interval. EVE.js `disruptiveLanceDebuff.js` cross-checks the refresh behavior. Schema 206 checkpoints
the recipient's current deadline. Restart preserves that deadline and cancels pending beam pulses.
[CCP Lancer Dreadnought mechanics](https://www.eveonline.com/news/view/new-lancer-dreadnoughts)

The Bane acceptance case verifies siege, cloaking, payment and racial range, then reopens the save while
the Rapier is disrupted. It verifies half-strength armor repair and that warp resumes at exact expiry.
The same case checks catalog admission for all four hulls.

**Automated validation.** The exact Bane case passed in 8.23 seconds. All 232 cases in the final
capital travel, area weapons, spatial modules, tethering, flight, gates, remote repair and migration
filter passed in 104.85 seconds after a 10.54-second build. Both Python suites, formatting and
`git diff --check` passed.

**Live acceptance.** Not performed; agent client testing is disabled.

## Gravitational Transportation Field Oscillator

Type `40634` uses `doomsdayHOG` (`6474`). Build 3662168's `positionalControl.pyj` uses
`ActivateSinglePointTargetedModule` and a projected sphere. Its catalog sets a 100 km aim range, 10 km sphere,
15 seconds of warning, 2,000 Strontium Clathrates and 30,000 GJ. The Titan pays before the warning and
reuses the signature-scaled capacitor wave. Eden requires red safety in lowsec.
[CCP Citadel patch notes](https://www.eveonline.com/news/view/patch-notes-for-eve-online-citadel)

At settlement, eligible piloted subcapitals move together to an offset sampled 1 to 4 AU away in the
same system. The coordinator updates their motion and sends absolute position corrections. Capitals
stay in place and receive a 60-second warp lock. The capital deadline and the relocated positions
survive restart, while pending pulses stop at restart. A tethered capsuleer is protected. The pinned
localization entry `516260` specifies the capital lock. CCP removed the warp scramble from transported
ships in February 2018. [CCP February 2018 patch notes](https://www.eveonline.com/news/view/patch-notes-for-february-2018-release-1-0)

EVE.js had no oscillator settlement path to reuse. The pinned effect has no effect GUID, so Eden uses the
stock point indicator for the warning. The transport applies to piloted ships. NPC encounters and drones
keep their current positions.

**Automated validation.** The three exact oscillator cases passed in 10.26 seconds. All 142 cases in the
final capital travel, area weapons, tether, warp payment, flight boundary and migration filter passed in
57.69 seconds after a 10.03-second build. Both Python suites, formatting and `git diff --check` passed.

**Live acceptance.** Not performed; agent client testing is disabled.

## Pirate capitals and the Zirnitra

The pinned published roster adds Caiman, Chemosh, Sarathiel and Vehement dreadnoughts, Dagon and
Loggerhead force auxiliaries, Revenant and Vendetta supercarriers, and Azariel, Komodo, Molok and
Vanquisher Titans. The Zirnitra uses the same dreadnought rules with its own precursor weapon modifiers.
Existing jump, docking, siege, triage and combat rules enforce the hull class and fitted catalog attributes.
The fixture also records the stock wreck types for all thirteen hulls. EVE.js `wreckUtils.js` provided a
second reference for the named pirate wrecks.
[CCP Havoc expansion notes](https://www.eveonline.com/news/view/havoc-expansion-notes) ·
[CCP Legion expansion notes](https://www.eveonline.com/news/view/legion-expansion-notes)

Caiman and Komodo each have two launch tubes in build 3662168. Caiman loads light attack squadrons and
Komodo can load one heavy attack squadron alongside a light squadron. Fighter fitting uses pinned effect
`6984` for the Guristas hull bonuses and the default multiplier values from `12869`. Revenant attack
missiles also use its explosion radius and velocity modifiers. The Sarathiel can steer and change speed
in siege because its hull modifies the siege module's speed penalty. Warp and docking still wait for
the cycle to expire. Capital micro jump drives and fleet jump fields, additional
fighter abilities and pirate blueprint acquisition remain open. The Revenant and Vendetta share the
carrier conduit rules below.

**Automated validation.** The pirate roster and two-deck launch case passed in 8.12 seconds.
The Sarathiel siege movement case passed in 7.76 seconds. All 200 cases in the final capital travel,
fighter deck, hull audit, capacitor warfare, spatial modules and flight boundary filter passed in
162.35 seconds after a 7.94-second build. Both Python suites, formatting and `git diff --check` passed.

**Live acceptance.** Not performed; agent client testing is disabled.

## Titan Phenomena Generators

Build 3662168 uses `dogmaIM.Activate` with `moduleTitanEffectGenerator` (`6753`) for the four racial
modules. The pinned fitting restrictions admit the corresponding empire Titan and the listed pirate Titans.
Each activation spends 25,000 GJ and 40,000 racial isotopes. The five-minute module cycle blocks docking and
tethering. Titans can keep moving. Spatial Phenomena Generation adds 10% buff duration per level.
[CCP Ascension patch notes](https://www.eveonline.com/news/view/patch-notes-for-eve-online-ascension)

The pulse applies the four pinned DBuff collections from each module to piloted ships within 20,000 km.
Fleet membership does not affect eligibility. Eden reuses the command burst DBuff notification and
fitting graph to apply both bonuses and penalties. Schema 208 preserves the active module cycle and
recipient deadlines across startup. Existing command burst rules clear the recipient's buff on docking
or a system change. A disconnected source does not pay for new cycles.

EVE.js `commandBurstRuntime.js` provided the module effect and range cross-check. The pinned effect registry
lacks its `effects.TitanEffectGenerator` GUID, so Eden uses the stock information burst visual and the native
DBuff entries for presentation. The effects currently apply to piloted ships.

**Automated validation.** The two exact Amarr cases passed in 9.12 seconds. All 92 cases in the final
capital travel, command burst, tether, flight boundary and migration filter passed in 50.38 seconds after
a 10.68-second build. Both Python suites, formatting and `git diff --check` passed.

**Live acceptance.** Not performed; agent client testing is disabled.

## Fleet conduit jumps

Build 3662168's `cynoTravelSvc.pyj` uses the group jump variants of the fleet module, fleet deployable
and structure beacon calls. The pinned `fleetSvc.pyj` sends `SetAcceptsConduitJumpsValue`. Fleet member
replies and change notifications carry each pilot's choice. EVE.js `conduitJumpRuntime.js` provided a
cross-check for the 10 km surface range, consent, online passengers and flat group fuel cost.
[CCP Black Ops update](https://www.eveonline.com/news/view/enter-the-portal-black-ops-update-tq)

An eligible Black Ops with a fitted online covert portal carries up to 30 covert hulls, and a Rorqual
with an online industrial portal carries up to 30 mining ships. Carriers and supercarriers use the pinned
combat passenger flag and Capital Jump Portal Generation's five extra seats per level. They start with
five seats. Only hulls admitted by the pinned catalog and Eden's fitting rules enter this path.
[CCP 24.01 notes](https://www.eveonline.com/news/view/patch-notes-version-24-01)

The anchor uses its fitted jump range and normal capacitor payment. Group fuel uses the hull's pinned rate
independently of the selected passengers' mass or number. Eligible passengers move without paying fuel
or capacitor. Each passenger receives individual jump timers. The anchor, passengers, paid fuel and timers
commit in one resident transaction. The flight coordinator retires passenger contacts in the origin system
and delivers the committed arrivals to their connections. Offline pilots, opt-outs and blocked or ineligible
hulls stay at the origin. The industrial portal's standalone bridge remains open.

**Automated validation.** The five exact Sin, Archon and Rorqual cases passed in 12.41 seconds. All
146 cases in the final capital travel, fleet, translocation, gates and call result filter passed in
98.81 seconds after an 11.21-second build. The final cases check the exact paid isotope costs and
separation at arrival. Both Python suites, formatting and `git diff --check` passed.

**Live acceptance.** Not performed; agent client testing is disabled.

## Tech II command carriers

Build 3662168 publishes the Salvation, Simurgh, Gaia and Ymir in group `5120`. EVE.js
`transitions.js` corroborates their capital travel classification. Eden admits these four hulls
with their pinned volume, wreck, skill and fitting records. They use the carrier fighter deck,
standard capital jumps, and the existing command burst fitting rules. Their base conduit seats
and the seats added by the Capital Jump Portal Generation skill use the same hull multiplier.
[CCP Cradle of War expansion notes](https://www.eveonline.com/news/view/cradle-of-war-expansion-notes)

The support fighter graph uses the pinned shield, speed, neutralizer, ECM and web hull modifiers.
The Salvation case verifies assembly of the pinned cohort, a boosted Cenobite launch, ten base
conduit seats, and paid travel to a Pharolux. Fighter mobility abilities and capital micro jump
fields remain separate milestones.

**Automated validation.** The exact Salvation case passed in 12.68 seconds. The final three-case
filter covering the Salvation, Tech II hull admission and drone coverage passed in 12.20 seconds
after a 6.41-second build. The regenerated Rorqual fixture also passed the drone audit and the
lifecycle test across all 150 admitted drone-bay hulls in 50.42 seconds. Both Python suites passed.

**Live acceptance.** Not performed; agent client testing is disabled.

## Capital micro jump drive

Build 3662168 type `83465` uses `dogmaIM.Activate` with `microJumpDrive` (effect `4921`). The pinned
`MicroJumpDrive.pyj` defines the native spool and landing effects. EVE.js `microJumpDriveRuntime.js`
reads attribute `2066` for distance and confirms the 250 km capital displacement. The same Eden
spatial module cycle handles capacitor, signature bloom, scrambler cancellation, and reactivation.

The pinned module admits carriers, command carriers, and the Sarathiel. Skill `83464` reduces capacitor
cost by 5% per level. The required Micro Jump Drive Operation V reduces the 12-second spool to 9 seconds
and the Command Carriers skill further reduces it. The Sarathiel can complete the jump during siege
while its siege warp restriction stays active.
[CCP Equinox notes](https://www.eveonline.com/news/view/equinox-expansion-notes) ·
[CCP Legion notes](https://www.eveonline.com/news/view/legion-expansion-notes)

**Automated validation.** All three exact capital drive cases passed in 9.36 seconds. The
47-case spatial module, flight boundary, Sarathiel and command carrier filter passed in 76.64
seconds after a 3.32-second build. Both Python suites and formatting passed.

**Live acceptance.** Not performed; agent client testing is disabled.

## Capital micro jump field generator

Build 3662168 type `83485` and effect `12126` use `dogmaIM.Activate` with `microJumpPortalDriveCapital`
and `effects.MicroJumpFieldEngageCapital`. The pinned dogma gives a 10-second spool, 30,000 GJ before skills,
250 km displacement, 10 km surface range, and separate budgets of 50 ships and four capital ships. Command
carriers apply their spool and non-capital capacity bonuses. The field cannot activate in highsec.
[CCP Equinox notes](https://www.eveonline.com/news/view/equinox-expansion-notes) ·
[CCP Cradle of War notes](https://www.eveonline.com/news/view/cradle-of-war-expansion-notes)

EVE.js `microJumpDriveRuntime.js` and `microJumpDriveEligibility.js` corroborate the simultaneous displacement,
separate budgets and five-second scramble. Eden checks nearby connected capsuleers at spool completion. Eligible
ships keep their separation and motion, including capitals in siege. Cloak, scram, tether and travel protection
exclude passengers. Crimewatch applies to each moved passenger and safety can exclude individual targets. Schema
209 preserves the scramble across reconnect and checkpoint reopen. It blocks warp and further micro jumps until
the pinned deadline.

**Automated validation.** All three exact field cases passed in 10.59 seconds. The 87-case capital
micro jump, oscillator, spatial module, tether, warp capacitor and migration filter passed in
50.33 seconds after a 1.25-second build. Both Python suites and formatting passed.

**Live acceptance.** Not performed; agent client testing is disabled.

## Rorqual industrial bridge

The Rorqual opens a 20-second portal with an online Industrial Jump Portal Generator (`63140`).
Build 3662168 `cynoTravelSvc.pyj` uses the ordinary `CmdBridgeToFleetModuleBeacon`,
`CmdBridgeToFleetDeployableBeacon`, `CmdBridgeToStructureBeacon` and `CmdJumpThroughFleet` calls.
The module publishes `jumpPortalGeneration` (`2152`), which uses the stock `effects.JumpPortal`.
EVE.js `beyonceService.js` corroborates the `1e-9` per-kilogram fuel factor and source-owned fuel payment.

The pinned destination list includes industrial and standard beacons. A passenger must have attribute
`3325`, be in the fleet and be within the portal's activation range. The Venture and Porpoise have the
attribute. The Badger and Orca do not. Opening costs 100 GJ. Each successful transit charges the host
isotopes according to passenger mass, effective host fuel rate and distance. The host stays behind and
each passenger receives their own jump timers.
[CCP Version 20.04 notes](https://www.eveonline.com/news/view/patch-notes-version-20-04)

**Automated validation.** Two exact cases passed in 8.86 seconds. A Venture paid 14 oxygen isotopes
from the Rorqual's fuel bay for the pinned Jan-to-null route at the trained rate. The Badger was
refused without spending isotopes. The 36-case portal, conduit, core, fleet and call result filter
passed in 21.05 seconds after a 1.23-second build. Both Python suites passed.

**Live acceptance.** Not performed; agent client testing is disabled.

## Milestones

| Milestone | Playable outcome and acceptance |
| --- | --- |
| **1. Jump freighters and industrial cynos** | Implemented. A fleetmate lights a cyno in lowsec. A loaded jump freighter jumps from another system, docks, and unloads. The ship, cargo, fuel, capacitor and pilot timers reconcile across disconnect and checkpoint reopen. |
| **2. Standard cynos and sovereignty navigation** | Implemented. Standard module and mobile cynos, mobile inhibitors, Pharolux beacons and Tenebrex jammers use the paid jump-freighter path and resident sovereignty checks. |
| **3. Ansiblex jump gates** | Implemented. Link two aligned endpoints, enforce alliance access and the ship type list, and settle the source gate's capacitor at departure. |
| **4. Black Ops and covert travel** | Implemented. Black Ops direct jumps, covert module and mobile beacons, eligible fleet passenger travel through a paid portal, and fleet conduit jumps. |
| **5. Dreadnoughts and force auxiliaries** | Implemented. Dreadnoughts and force auxiliaries use standard cynos, paid siege or triage, and normal docking, repair and loss settlement. The acceptance cases reopen a paid siege and settle an Apostle loss. |
| **6. Carriers and fighters** | Carrier tubes support light attack, space superiority and support squadrons, with launch, targeting, recall and replacement. Squadron losses persist. Standup fighters and additional combat abilities remain open. |
| **7. Capital industry and fleet services** | Rorquals use paid industrial cores, Excavators, fleet compression and P.A.N.I.C. Shared fleet storage, mobile refitting, clone bays and fresh-world resupply remain open. |
| **8. Supercapitals and production** | Keepstar docking, Sotiyo construction, direct jumps, Titan bridging, supercarrier heavy attack fighters and burst projectors are implemented. Targeted Titan doomsdays have paid windups, counters and durable loss settlement. Titan lances, Reapers and Bosonic fields support paid aiming and positional counters. The oscillator transports subcapitals and locks capital warp. Phenomena Generators apply the pinned racial boosts and penalties. |

Milestone 5 needs the Upwell battle and destruction paths for its siege acceptance.
[Sovereignty upgrades](sovereignty.md#sovereignty-resources-and-upgrades--november-16-2026) ·
[Upwell implementation](structures.md) · [CCP Upwell structures](https://support.eveonline.com/hc/en-us/articles/213021829-Upwell-Structures) ·
[CCP 24.01 notes](https://www.eveonline.com/news/view/patch-notes-version-24-01)

## Milestone 1 jump-freighter delivery

### A route to build and verify

Begin with a **Rhea and Venture**, then verify the same operation for the Ark, Anshar and Nomad and each supported emitter
cohort. The initial two-pilot fixture uses Jita `30000142` and Jan `30001385`. The pinned `client/systems` centers put them
**5.2682 light years** apart, inside the Rhea's **6 LY** range at Jump Drive Calibration I. Jan's raw security is **0.41695**
and the catalog includes NPC station `60001714`. Use a Jan beacon with a clear arrival point then dock by ordinary approach.
Load cargo separately from the correct isotope and run the stock fleet-menu or Jump Beacon broadcast path from space.
Finish by unloading the same cargo at the Jan station. With Jump Freighters I, Jump Drive Operation V and no fuel skill
or economizer, the provisional fixture costs are **47,413 nitrogen isotopes** and **24,937.5 GJ** on an unmodified Rhea.
With no prior fatigue, the client formula predicts **91 seconds** of activation cooldown and **916 seconds** of fatigue
at whole-second precision. The fixture starts with owned assets. Eden's production audit admits the industrial cyno
blueprint `52695` and all four jump freighter recipes. Trace blueprint access, invention inputs, construction components,
skillbooks, Liquid Ozone and ice-derived isotopes through existing acquisition paths for a fresh-world delivery.
[Production catalog](../production-catalog.json) · [Ice and industry](industry.md)

### Catalog inputs and gameplay rules

| Input | Build 3662168 evidence and implication |
| --- | --- |
| **Industrial cyno** | Generator `52694`, group `658`, effect `2857` (`cynosuralGeneration`) creates Industrial Cynosural Field `52696`, group `2020`. The module declares 600,000 ms duration, one active in group, 500 m/s activation limit, and 5,000 m spawn radius. It consumes 800 Liquid Ozone (`16273`) before bonuses and requires Cynosural Field Theory I (`21603`). It declares no early deactivation, activation during warp, docking or tethering. |
| **Emitter hulls** | Module fitting allows hauler group `28`, blockade runner `1202`, deep space transport `380`, Venture `32880` and Venture Consortium Issue `89648`. Both Ventures carry the 50% ozone modifier. Verify its combination with the skill modifier and admit hulls where their other role checks pass. |
| **Jump freighters** | Group `902`, base range 5 LY (`867`), jump capacitor fraction 0.95 (`898`), fatigue distance multiplier 0.1 (`1971`), 12,000 m³ fuel bay (`1549`). Rhea `28844` uses nitrogen at 10,000 units/LY, Nomad `28846` hydrogen at 8,200, Anshar `28848` oxygen at 9,400, Ark `28850` helium at 8,800. The rate (`868`) and fuel type (`866`) come from the effective fit. |
| **Effective jump fit** | Calibration effects `1581/1582` modify range, Operation `1722/1723` modifies capacitor, and Fuel Conservation `3532` plus the hull's `3593` modify isotope rate. Verify the three published jump-drive economizers in group `1299` and each hull's fitting and trait rules before admission. `3317` points to beacon type list `343`, which includes `52696`, `21094` and `57425`. Only the industrial field is produced in this milestone. |

- **Light a field.** Validate the active ship, space state, effective fit/skills, speed, local security, reachable grid,
  and cyno restrictions before charging ozone. Pay once for one cycle. Publish a warpable system-wide beacon, expose
  its jump action to authorized fleetmates, and stop voluntary movement, warp, cloak, docking and tethering during the
  live cycle. Reject requests to stop the module early. Confirm the speed comparator and movement/effect interactions at
  their boundaries. Expiry, ship loss and observer joins must reconcile even without updates from the emitter's client.
  Highsec rejects ignition. Scope initial
  field placement to reachable low/null known space, with Pochven, restricted pockets and wormhole systems excluded.
  [CCP industrial cynos](https://www.eveonline.com/news/view/patch-notes-for-september-2019-release) ·
  [CCP jammer rule](https://forums.eveonline.com/t/chaos-era-continues-cyno-update/182197) ·
  [CCP Venture change](https://www.eveonline.com/news/view/patch-notes-for-november-2019-release) ·
  [EVE University jump freighters](https://wiki.eveuniversity.org/Jump_Freighters)
- **Take the jump.** Resolve the destination by the fleet access and the exact beacon identity; the supplied system is a
  cross-check. Require an eligible active hull in space, a live field in another system, in-range catalog centers, valid
  travel state, sufficient capacitor and usable isotope stacks. Use the fuel bay for isotopes in the initial delivery.
  Confirm cargo fallback before including it as a rule for jump drives. Check Crimewatch, interdiction, active jump cooldown and
  overlapping transitions at commit. A highsec origin can jump to an eligible lowsec destination. Reuse ordinary dock and
  gate handling for an admitted freighter and keep tackle and arrival protection consistent with the verified jump rules.
- **Apply costs and timers.** The pinned map tooltip predicts fuel as `max(1, trunc(distance LY × effective fuel/LY))`.
  With `D = 1 + distance LY × fatigue multiplier` and `R` the seconds of fatigue remaining before the jump, the pinned
  tooltip and hints derive fatigue from `clamp(R × D, 600 × D, 18000)` and activation from `max(60 × D, R / 10)`.
  Apply CCP's 1,800-second activation cap. Use committed wall-clock deadlines owned by the pilot, with a single timestamp
  for both timers. Fix rounding at the boundary and compare it with the client predictions before settling the fixtures.
  [CCP jump timer rules](https://support.eveonline.com/hc/en-us/articles/212726865-Jump-Activation-Cooldown-and-Jump-Fatigue)

### Resident ownership and reuse

- **Gameplay and World.** Put the typed commands, results and pure range/cost checks in Gameplay. Use the existing
  catalog and fitting graph, `InventoryFlag.FuelBay` (`133`) and [ship hold rules](../../src/Eden.Gameplay/Inventory/ShipHoldRules.cs).
  Resolve paid stack withdrawals and capacitor from resident state. The [resident gate transfer](../../src/Eden.World/Docking/PilotGateTravelStore.Resident.cs)
  shows how to reuse in-world travel checks, position settlement and map activity. Keep jump state authoritative in
  World and include it in coherent checkpoints, including for saves created before capital travel.
- **Prepare and commit.** Reserve a single pending operation for the active ship, with the fleet, ship and field identity
  checked again at departure. Commit isotope withdrawal, capacitor debit, position, jump timers and travel activity as one
  resident change. Publish inventory, session and observer changes after commit. If departure fails, keep the ship and its
  resources at origin. If transport fails after commit, reconnect reads the committed destination and timers. Use existing
  ownership ordering for source and destination and settle field expiry or ship loss against the same clock. For the first
  milestone, a field removed before the jump commits causes an unpaid refusal. The exact live-game destruction race is
  server-side behavior and may differ.
- **Field lifecycle.** Store the active field identity, anchor and deadline with the paid cycle. A disappearing or destroyed
  anchor retires the field. A disconnect by itself must not free a live cyno ship from the cycle. Rebuild surviving fields
  from valid in-space anchors on reopen without paying again, and retire expired or invalid fields. Native fleet access is
  transient. It is tied to the issuing fleet instance and current authorized recipients, with explicit leave and disband
  invalidating access. A surviving public field cannot grant a new fleet jump access through a reused fleet or beacon ID.
  Define and test access on an emitter disconnect against Eden's existing online-only fleet policy.
- **Protocol.** Use the existing Dogma activation boundary for effect `2857` and the cooperative fleet notification queue
  for beacon changes, with a snapshot for late joiners. Reuse the cross-system camera handoff
  for departure and destination scene ordering. Adapt its session/Destiny mechanics to the paid resident operation. Use
  the native jump-drive departure effect, one refreshed destination ballpark, and one arrival effect. Verify source and
  destination observers as well as the traveler. Give the timer stub's selected-pilot route a single authoritative owner.
  [Cooperative fleet delivery](fleets.md#cooperative-fleet-foundation-november-7-2026) ·
  [Cross-system camera handoff](navigation.md#developer-translocation)

### Native calls and source evidence

The original 3662168 archive at `E:/EdenDev/downpatch-3662168/tq/code.ccp` has SHA-256
`37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368`. The contracts below come from its Python 2 code
objects read as data with `xdis` and the read-only catalog. The generated route inventory at the planning baseline
listed the jump call as unhandled and the timer route as a compatibility handler. [Client RPC inventory](../client-routes/README.md)

| Client entry point | Verified contract |
| --- | --- |
| `environment/godma.pyj` | `StateManager.Activate` forwards `(itemID, effectName, target, repeat)` to the Dogma location. Match the industrial module and its effect at the existing `dogmaIM.Activate` dispatch. |
| `parklife/cynoTravelSvc.pyj` | `JumpToFleetModuleBeacon` calls `PerformSessionChange('jump', remotePark.CmdJumpToFleetModuleBeacon, charID, beaconID, solarsystemID)`. The owner character precedes the beacon ID and system ID on the wire. |
| `parklife/fleetSvc.pyj` | `OnFleetJumpBeaconModuleChange(charID, solarsystemID, beaconID, typeID, active)` populates the menu cache keyed by character, with those three middle values in a tuple. `SendBroadcast_JumpBeacon` calls fleet `SendBroadcast('JumpBeacon', scope, beaconID, typeID)`. Validate the broadcast against active field state. |
| `ui/services/crimewatchSvc.pyj`, `ui/crimewatch/crimewatchTimers.pyj` | `jumpTimers.GetTimers(session.charid)` returns `(jumpActivation, jumpFatigue, lastUpdated)`. `OnJumpTimersUpdated` supplies the same three positional values. Active timestamps are Blue wall-clock values. The UI subtracts `lastUpdated` from each active expiry. Inspect inactive values against both timer and map consumers. |
| `ui/shared/mapView/filters/mapFilterCynosuralFields.pyj` | `map.GetBeaconCount()` supplies a dictionary of system ID to module beacon count. Derive counts from live public fields. The same filter consults the structure directory for structure beacons in a later milestone. |
| `ui/shared/mapView/markers/markerTooltipUtil.pyj`, `ui/crimewatch/crimewatchHints.pyj`, `jumpFatigue/const.pyj` | Fuel, fatigue and activation projections use the ship's effective dogma values. The preview converts results to integer seconds for display. This is evidence for client parity, with server settlement details still to verify. |
| `environment/effects/Jump.pyj` | `JumpDriveOut` reads the destination system from `graphicInfo[0]` and prepares `JumpTransitionCyno`. `JumpDriveIn` animates the arriving ship. Eden already carries the destination system in this effect for cross-system translocation. |

EVE.js `261108.1` is a reference for behavior and wire shapes. The local checkout is a source snapshot without Git metadata
and declares AGPL-3.0-only. Eden's MIT implementation uses its own owners and code. These are the relevant snapshot files.

| EVE.js file and SHA-256 prefix | Reusable finding and limit |
| --- | --- |
| `server/src/services/ship/beyonceService.js` `5C13B78749BD` | `resolveFleetModuleBeacon` checks the requested owner, beacon and system against live fleet state. Use the pinned type list for hull compatibility. For example, the reference's hard-coded industrial list omits Black Ops, while the pinned Sin uses list `342`, which includes `52696`. |
| `server/src/space/runtime/cynosuralField.js` `FE6D370EE6DD` | An active module creates a distinct scene object with expiry, source ship/module and spawn radius, paired with fleet cache updates. Eden can project both from one committed field. |
| `server/src/services/_shared/jumpTimerRuntime.js` `3A4508CEA3B6` | The timer tuple and three-argument notification agree with the pinned client. Calculate these deadlines inside the paid transfer, since the reference applies timers as a separate best-effort step. |
| `server/src/space/modules/sharedFuelRuntime.js` `767330CC9032` | Fuel lookup and debit recognize fuel bay and cargo. Confirm gameplay precedence and whether cargo can supplement the fuel bay before making that a supported Eden rule. |
| `server/src/space/transitions.js` `89AE36918B01` | `jumpSessionToCynosuralBeacon` guards the original session/ship and sequences departure before the handoff. Reuse Eden's existing session generation and Destiny code for the corresponding checks. |

[Carbon Destiny `27ee40b`](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ballpark.cpp)
backs the existing movement and explicit relocation primitives. The relevant Carbon behaviors are position and velocity
updates for a ball. The native jump effect and ordering come from the pinned client and Eden's existing handoff tests.

### Implementation checks

1. Build the exact end-to-end resident fixture first. Use two selected pilots, the pinned Jita–Jan vectors, and an actual
   station inventory move after arrival. Assert that a failed admission cannot spend ozone, isotope or capacitor, and
   that a successful jump moves the same ship and cargo with exactly one debit and timer update.
2. Cover range, security, speed and deadline edges, wrong isotope, fuel stack splits and rounding, insufficient capacitor,
   stale/wrong/foreign beacons, concurrent duplicate clicks, fleet changes, anchor death and expiry during departure,
   tackle and Crimewatch. Exercise checkpoint generation/reopen, old saves, and reconnect both before and after commit.
3. Replay the native call with Blue numeric types and authenticate the timer read against the selected character. Verify
   fleet cache add/remove and late join, public overview and map data, departure-before-session-change, fresh destination
   state and one arrival event. Reuse focused ship fitting, inventory, fleet, Crimewatch, flight and detached checkpoint
   tests for immediate dependencies. On warmed reads, check command, connection and fit-rebuild counts.

**Planning validation.** Static checks passed for 13 local links and anchors, UTF-8, final newlines, whitespace, the pinned
archive hash, catalog values for the four jump freighters and the cyno, the Jita–Jan vectors, and all five production recipes
(under one second). `git diff --check` passed.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** After implementation, light a Venture cyno in Jan and jump from the native fleet menu;
verify arrival camera, cargo and timers. Reconnect to verify the timer display. Observe the beacon from a third pilot.
