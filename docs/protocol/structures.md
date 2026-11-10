# Player Upwell structures — November 10, 2026

[Protocol index](../protocol.md) · [Documentation index](../README.md)

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
**Core onlining speed** and **Decommissioning speed** controls. Each accepts **0.1–100×**,
defaults to **1×**, and participates in revision comparison, JSON import/export and save for
restart. A speed of 2× halves the duration. Existing deadlines retain their original terms;
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
an explicit shared Eden policy of 30,000 m³ until specialized bays are modeled. No structure
rig bonuses or configurable service taxes are claimed. These limits must remain visible in
status reports; support for a hull's lifecycle does not imply its specialized service works.

## Protocol and persistence evidence

World schema 136 adds `WorldStructure`, `WorldStructureService`, `WorldStructureAccess`,
nullable `WorldShip.StructureId` and `WorldIndustryJob.SuspendedAt`. Saved ships retain their
last NPC station as a migration/system fallback and use `DockedLocationId` for actual storage
and service ownership. Structure identities share the inventory allocator and high-water marks;
they cannot collide with ships, items or later recovered deployments. Inventory, core, fuel,
service and recovery mutations commit together with rejection/unchanged-state checks.

Build-specific inventory flags are service slots 164–171, structure fuel bay 172 and core room
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
