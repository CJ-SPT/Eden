# Area weapons and electronic warfare

## Ship modules — November 16, 2026

Build **3662168** has explicit fitting and operational owners for **29 published static modules**:
11 burst jammers, seven burst projectors, two bomb launchers, one defender launcher and eight heavy stasis grapplers.
All seven ship bombs and the modern Defender Missile I have ammunition admission. Structure projectors and structure
bombs remain in the separate structure-combat program. Projector operation does not admit supercarrier hulls: their
seven profiles retain an explicit capital-hull consumer dependency in the coverage inventory.

### Gameplay and native contracts

- **Burst jammers:** `dogmaIM.Activate(moduleID, 'ECMBurstJammer', None, repeat)` pays a cycle and independently rolls
  every nearby player ship, NPC and drone, including friendlies. Successful pulses break outgoing locks without
  installing a sustained jam; relocking is immediately possible. Pending player lock scans are cancelled through a
  shared break revision. Highsec requires red safety and Omega. Player/drone aggression uses the existing crimewatch
  owner. The native module effect and `effects.ECMBurst` repeat/stop with the paid interval. Electronic Warfare,
  supported hull bonuses, overload strength and rack heat use the existing fitting graph.
- **Heavy grapplers:** the existing `remoteWebifierFalloff` tackle operation admits the eight restricted profiles.
  Edge distance beyond optimal continuously attenuates paid-cycle strength by `0.5 ^ ((distance-optimal)/falloff)^2`.
  A basic grappler applies -80% at optimal and -40% at one falloff. Existing web stacking, target resistance,
  aggression, lock checks, capacitor, saved cycles, overload and cleanup remain shared with tackle.
- **Bomb launchers:** targetless `useMissiles` activation spends one compatible fitted bomb, validates the hull,
  skills, movement and null/wormhole security, and records its cycle plus earned reactivation delay. An untargeted
  straight flight uses the catalog speed and lifetime, independently of later source movement. Detonation affects
  ships, NPCs and drones, including the source and friendlies. Damage scales with target signature and normal
  resistances, without target-speed reduction. Lockbreakers break locks; Void variants drain capacitor using their
  signature resolution and energy resistance. Ordinary damage, NPC reward/loss and player loss/capsule settlement
  use their existing owners. Bombs damage other bombs using saved armor/structure and catalog resistances; smartbombs
  can damage or destroy them before detonation.
- **Defenders:** a targetless sweep pays capacitor and the cycle/reactivation deadline even when no threat exists.
  Ammunition is consumed only when a reachable, unassigned hostile bomb threatens the ship's remaining flight corridor.
  Owner, corporation, alliance and fleet affiliation exclude friendly bombs. Candidates use distance, expiry and ID
  ordering. Earned Defender Missiles speed feeds the intercept calculation. Native command-destroyer defender cycle bonuses
  use their required-skill selector. A paid defender travels to its selected
  bomb and destroys that identity once; another launcher cannot reserve the same bomb concurrently.
- **Burst projectors:** inert client inspection confirms the positional tool calls
  `superWeaponMgr.ActivateSinglePointTargetedModule(moduleID, effectID, (x,y,z))`, rather than passing a coordinate
  tuple to ordinary `dogmaIM.Activate`. Coordinates, range, effect identity, hull, skills, capacitor and cargo fuel
  are validated together. A paid 10-second spool produces the pinned ECM, web, neutralizer, bubble, dampener, painter
  or combined turret/missile disruption program. Fields track entrants and departures at their fixed point;
  ECM rolls once per entry and grants no source-targeting exception. Neutralization is one pulse, not a free drain
  every frame. The projected warp sphere uses the resident interdiction owner and preserves nullification.
  Field duration and the longer module cycle remain distinct; Burst Projector Operation affects the module cycle.
  Native module/fuel notifications and incoming EWAR/jam indicators use existing adapters.

### Resident ownership and persistence

Schema **179** adds `SavedAreaWeapon`, with immutable typed flight/spool snapshots registered in resident economy
capture, hydration and detached checkpoint projection. `WorldPlayerEwar` changes from one row per module to a composite
`(ModuleId, TargetId)` key. Migration retains earlier contacts and recreates their indexes/triggers; independent area
recipients share the existing fitting invalidation and observer versions. An indexed deadline set removes timed
contacts before boundary reads. Empty-system area monitoring returns through a cheap resident membership count.

Activation, fuel/ammunition spending, capacitor, cooldowns, heat and flight identity commit atomically. Rejected commands
publish no lock breaks or effects. Deterministic deadline/ID ordering handles bomb destruction before a later detonation.
Checkpoint capture retains coherent snapshots while the world advances. Startup cancels flights, spools and connection
effects without refunding ammunition/fuel or resetting cooldowns, so downtime never replays a blast or grants a free pulse.
Bombs already launched can finish after their source leaves; module controllers cancel repetition during travel or burnout.
Ordinary gameplay requires the resident owner and has no offline SQL implementation. SQL is used for schema migration,
initial hydration and checkpoint projection.

### Evidence

Gameplay references checked against the pinned catalog:
[ECM](https://wiki.eveuniversity.org/ECM), [bombs](https://wiki.eveuniversity.org/Bombs),
[missiles](https://wiki.eveuniversity.org/Missiles), [tackling](https://wiki.eveuniversity.org/Tackling) and
[capital modules](https://wiki.eveuniversity.org/Capital_Ship_Modules).
The wiki's current burst-jammer optimal/falloff description differs from this build's effect **6714**, which names
only range attribute **142**. Burst Jammer I therefore uses its catalog **10,000 m** circular reach. Grapplers use
effect **6426**, optimal **54** and effectiveness falloff **2044**. Bombs use effect **2971** and actual catalog
armor/structure values; utility bombs differ from ordinary damage bombs in flight time and defenses.

`tools/Export-AreaWeaponCatalog.py` reproduces the committed fixture from the read-only build-3662168 catalog.
Static bytecode inspection used the original `E:/EdenDev/3662168/.eden-original-code.ccp`, SHA-256
`37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`:
`shipmodulebutton.pyj.ActivateEffect`, `positionalControl.pyj._StartTargetedEffect` and
`godma.pyj.StateManager.Activate`. No client was launched. Projectile presentation reuses the established native
free-ball/Goto encoding and position/velocity corrections; consulted
[Carbon Destiny Thunkers.cpp at 114e89fa584f0e1a81eb0a679aabf41af8870072](https://github.com/carbonengine/destiny/blob/114e89fa584f0e1a81eb0a679aabf41af8870072/src/Thunkers.cpp).

The local AGPL-3.0-only evejs tree was read for behavioral comparison; no implementation was copied. It has no Git
revision metadata. SHA-256 source snapshots under `server/src/space/modules/`:

| File | SHA-256 |
| --- | --- |
| `bombRuntime.js` | `66f3209f2bdb0e4a4c4b2dfc4dcdfa90625713f941d35f03e7aae023ebae40f4` |
| `defenderMissileRuntime.js` | `461bfce87337cea4d11202ef32730ab4367aea8e70af7c91170c52330bc196fe` |
| `jammerModuleRuntime.js` | `4f88c0dabc3859e97f0e9f3a1e425f5b7256c4831c2e4be195814f36b5d7f531` |
| `hostileModuleRuntime.js` | `db747aa99975012526f576e6042ba2c1c6d7d664c58cc50e0dc31fc5e550d93f` |
| `superweapons/superweaponRuntime.js` | `b586a388951ad6135b2f6c105d86d1738c31cec475f405d61238d7a8b92e747b` |

### Automated validation

Development started with exact filter
`FullyQualifiedName=Eden.Protocol.Tests.Combat.AreaWeaponTests.PublishedAreaWeaponAndGrapplerProfilesHaveProductionFittingAndAmmunitionOwners`.
The new family filter `FullyQualifiedName~Eden.Protocol.Tests.Combat.AreaWeaponTests` passed 19 cases before adding
lock-revision, heat, warmed-drone, smartbomb interception and rejection boundary coverage. It covers profile cohorts,
resources, duplicate payment, lock breaking, grappler falloff,
untargeted bomb flight, utility payloads, paid defender interception, all seven projector programs, native aimed calls,
field membership/expiry, rejected fuel/coordinates, composite-key migration and checkpoint/restart cancellation.
The wider 765-case integration run selected combat, EWAR, tackle/interdiction, missiles, targeting, ammunition,
drones, fitting/hull coverage, checkpoints and service registration. It completed in **655.96 seconds** with **756 passes
and nine fixture/coverage failures**. The failing cases were the missile cohort's pre-rapid-torpedo count, the Tech II
coverage assertion that still rejected burst jammers, and seven hull probes missing advanced/interdiction consumers
or native defaults for omitted attributes. Corrections retain strict result assertions, use the proper web drone for
its owner modifiers, and preserve the earlier dedicated validation branch's advanced-weapon/command-burst probes.
A 40-case correction run passed 39; replacing the remaining inappropriate rocket-launcher probe with its native web
drone passed the exact hull regression in **13.59 seconds** (9.93-second build).

The initial expanded bomber/command-destroyer probe invoked a generic all-feature hull helper and failed all 11 cases
on unrelated probe coverage. Dedicated resident calculations now check just the new module consumers: loaded bomb
charges and defender modules with their actual hull/skill selectors. All **11** passed in **20.85 seconds**
(4.52-second build). No generic helper failures are represented as passing module validation.

The 765-case run used task changes on `ab8c3e6e`; final follow-up includes `5f4b37c9` and subsequent corrections.
Final stable validation: tool restore, solution restore and solution build passed in **8.19 seconds**, with zero
warnings/errors. The final follow-up filter below passed **192/192** cases in **84.09 seconds**, including all **42**
new area-family cases and every earlier failing integration case. Both Python policy suites passed (**8 + 9** tests,
**1.28 seconds**). Formatting verification passed in **26.63 seconds**. Native catalog coverage exports were
regenerated and local documentation links checked.

After integrating the final concurrent structure/login update `b05fa598`, solution build passed in **9.09 seconds**
and **102/102** affected cases passed in **36.43 seconds**. This run selected the five classes below, each using
`FullyQualifiedName~Eden.Protocol.Tests.` and joined with `|`:
`Combat.AreaWeaponTests`, `Structures.StructureTests`, `Maps.MapNavigationTests`, `Hosting.ServerServicesTests`,
`Flight.MissileBallparkTests`. The earlier 192-case filter remains the validation for the unchanged shared paths.

<details>
<summary>Exact integration filters</summary>

```powershell
$broad = @(
    'Combat.AreaWeaponTests', 'Combat.PlayerEwarTests', 'Combat.PlayerEwarNpcTests',
    'Combat.PlayerEwarLifecycleTests', 'Combat.SmartbombTests', 'Propulsion.TackleTests',
    'Propulsion.ResidentTackleTests', 'Propulsion.InterdictionTests', 'Propulsion.HicInterdictionTests',
    'Propulsion.NullifierTests', 'Combat.PilotMissileTests', 'Combat.MissileFittingTests',
    'Combat.MissileFlightTests', 'Combat.ResidentMissileLaunchTests', 'Flight.MissileBallparkTests',
    'Targeting.TargetingTests', 'Ammunition.AmmunitionTests', 'Ammunition.AmmunitionLookupTests',
    'Combat.PilotDroneTests', 'Combat.ResidentDroneTests', 'Combat.LiveDroneRulesTests',
    'Combat.DroneSynchronizationTests', 'Combat.DronePerformanceReplayTests', 'Combat.UtilityDroneIntegrationTests',
    'Combat.NpcEwarDroneTriggerTests', 'Thermal.ElectronicSupportHeatTests', 'Thermal.ElectronicSupportResidentCheckpointTests',
    'Persistence.ShipContentCoverageTests', 'Persistence.FittingCapabilityRulesTests', 'Persistence.SmallHullBonusTests',
    'Persistence.TechTwoHullTests', 'Persistence.DetachedCheckpointTests', 'Persistence.IncrementalCheckpointTests',
    'Hosting.ServerServicesTests', 'Hosting.GameplayDependenciesTests', 'Hosting.DataModelOwnershipTests'
)
$broadFilter = ($broad | ForEach-Object { 'FullyQualifiedName~Eden.Protocol.Tests.' + $_ }) -join '|'

$final = @(
    'Combat.AreaWeaponTests', 'Combat.MissileFittingTests', 'Flight.MissileBallparkTests',
    'Structures.StructureTests', 'Propulsion.InterdictionTests', 'Propulsion.HicInterdictionTests',
    'Propulsion.NullifierTests', 'Persistence.FittingCapabilityRulesTests', 'Combat.DronePerformanceReplayTests'
)
$hulls = (@('47269','91849','49710','606','35779','33081','54731') | ForEach-Object { 'DisplayName~' + $_ }) -join '|'
$finalFilter = (($final | ForEach-Object { 'FullyQualifiedName~Eden.Protocol.Tests.' + $_ }) -join '|')
$finalFilter += '|(FullyQualifiedName=Eden.Protocol.Tests.Persistence.SmallHullBonusTests.' +
    'EveryOperationalBonusChangesItsCatalogTargetByItsEarnedSkillAndRoleAmount&(' + $hulls + '))'
$finalFilter += '|FullyQualifiedName=Eden.Protocol.Tests.Persistence.TechTwoHullTests.' +
    'EveryPublishedTechTwoSubcapitalHasAnExplicitOperationalOrDeferredOwner'
```

</details>

Early development runs exposed a burst-jammer passive parser exclusion, missing bomb/defender ammunition routing and
an unconfigured JSON snapshot serializer. A wider 208-case run then exposed 14 failures: the composite-key
resident table still advertised module identity as unique, and one earlier coverage assertion expected burst jammers
to remain unsupported. Removing that optimization and updating the obsolete assertion passed the 11 selected failing
cases (11.16 s build, 15.92 s tests). The initial warmed-drone view failed to update; explicit EWAR profile invalidation
then passed its exact regression (12.85 s build, 7.89 s tests). Rejection fixtures were corrected to capture ship IDs
before entering resident inspection, avoiding their own SQL-backed fixture getters inside speculative transactions.
Fixture corrections
also replaced an incorrect bomber type, an unsupported artificial capacity attribute and a same-corporation defender
target. A native coordinate test now aims relative to the session's actual published position. These initial failures
are not represented as passing validation. No full .NET regression suite was requested or run.

### Live acceptance

Not performed; agent client testing is disabled. Native rendering, aimed input and gameplay feel remain unverified.
Projector operational tests use explicitly artificial subcapital fitting budgets/restrictions; they do not establish
supercarrier admission or actual-budget capital fits.

### Optional maintainer checks

1. Check burst-jammer lock loss/relocking and grappler strength while moving through optimal and falloff.
2. In nullsec, observe a bomber flight, blast and defender interception from two clients.
3. After eligible capital hulls are implemented, check the native aimed-projector tool and field rendering.
