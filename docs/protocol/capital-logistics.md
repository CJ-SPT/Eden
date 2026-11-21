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
transactions and existing active-module deadline index. Pirate capitals and Lancer Dreadnought specialist modes
remain future work.

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

## Milestones

| Milestone | Playable outcome and acceptance |
| --- | --- |
| **1. Jump freighters and industrial cynos** | Implemented. A fleetmate lights a cyno in lowsec. A loaded jump freighter jumps from another system, docks, and unloads. The ship, cargo, fuel, capacitor and pilot timers reconcile across disconnect and checkpoint reopen. |
| **2. Standard cynos and sovereignty navigation** | Implemented. Standard module and mobile cynos, mobile inhibitors, Pharolux beacons and Tenebrex jammers use the paid jump-freighter path and resident sovereignty checks. |
| **3. Ansiblex jump gates** | Implemented. Link two aligned endpoints, enforce alliance access and the ship type list, and settle the source gate's capacitor at departure. |
| **4. Black Ops and covert travel** | Implemented. Black Ops direct jumps, covert module and mobile beacons, and eligible fleet passenger travel through a paid portal. |
| **5. Dreadnoughts and force auxiliaries** | Jump a dreadnought and a force auxiliary to a standard cyno. Exercise paid siege, capital weapons and structure damage, then triage and lawful remote repairs. Prove dock and gate restrictions, loss settlement, and survival of paid mode deadlines on reopen. |
| **6. Carriers and fighters** | Launch, control, recall and replace fighter squadrons from a carrier using native tubes and abilities. Verify targeting, observer updates and fighter losses. Reuse squadron rules for Standup fighters once the structure control and fitting branch is integrated. |
| **7. Capital industry and fleet services** | Fly a Rorqual with an industrial core, Excavators, compression and P.A.N.I.C. Add supported fleet hangar, ship maintenance, refit and clone services. Exercise a real production and resupply route, then let bot haulers use the same paid jump and access operations. |
| **8. Supercapitals and production** | Deliver the verified Keepstar/Sotiyo docking and construction rules, supercarrier and titan roles, and supported portals, superweapons and counters. Exercise construction, deployment, combat, and irreversible loss with real inputs and sovereignty requirements. |

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
