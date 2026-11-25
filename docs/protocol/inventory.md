# Inventory and ship ownership

[Documentation](../README.md) / [Protocol index](../protocol.md)

Stacks, personal holds, assembled ships, insurance, dirt and recovery hulls.

These records preserve milestone evidence and historical observations. Dates, schema versions, test results and live acceptance apply to the recorded change; they do not establish acceptance of later changes.

## Contents

- [Crystal damage in inventory](#crystal-damage-in-inventory)
- [Fitted ship repackaging](#fitted-ship-repackaging)
- [Inactive ship transfers and move-rule audit, November 15, 2026](#inactive-ship-transfers-and-move-rule-audit-november-15-2026)

- [Container stacking and fitting, November 15, 2026](#container-stacking-and-fitting-november-15-2026)

- [Container item context menus, November 15, 2026](#container-item-context-menus-november-15-2026)
- [Resident cargo container access, November 15, 2026](#resident-cargo-container-access-november-15-2026)
- [Cargo container assembly and personal asset worth](#cargo-container-assembly-and-personal-asset-worth)
- [Criminal loot and lawful recovery, November 14, 2026](#criminal-loot-and-lawful-recovery-november-14-2026)
- [NPC wreck ownership from damage](#npc-wreck-ownership-from-damage)

- [Loot abandonment and theft, November 14, 2026](#loot-abandonment-and-theft-november-14-2026)

- [Direct player trading](#direct-player-trading)
- [Cargo jettison, November 13, 2026](#cargo-jettison-november-13-2026)
- [Targeted stack merging, November 9, 2026](#targeted-stack-merging-november-9-2026)

- [Trashing personal items and ships (November 8, 2026)](#trashing-personal-items-and-ships-november-8-2026)
- [Remote Personal Assets expansion (November 14, 2026)](#remote-personal-assets-expansion-november-14-2026)
- [Personal Assets window (November 8, 2026)](#personal-assets-window-november-8-2026)
- [Capsule retirement on boarding (November 6, 2026)](#capsule-retirement-on-boarding-november-6-2026)
- [Personal ship insurance](#personal-ship-insurance)
- [Native inventory stack division](#native-inventory-stack-division)
- [Persistent ship dirt and native cleaning](#persistent-ship-dirt-and-native-cleaning)
- [First-party ship SKIN licenses](#first-party-ship-skin-licenses)
- [First-party ship SKIN application](#first-party-ship-skin-application)
- [Persistent cargo and hangar items, November 2, 2026](#persistent-cargo-and-hangar-items-november-2-2026)
- [Partial-stack capacity recovery, November 2, 2026](#partial-stack-capacity-recovery-november-2-2026)
- [Venture ownership, boarding and mining holds](#venture-ownership-boarding-and-mining-holds)
- [Native recovery corvettes, November 2, 2026](#native-recovery-corvettes-november-2-2026)
- [Inventory refresh during capsule adoption, November 2, 2026](#inventory-refresh-during-capsule-adoption-november-2-2026)

## Crystal damage in inventory

Build **3662168** `CrystalDamageTracker.PrimeCrystals` in `eve/client/script/ui/services/inventorysvc.pyj` passes a Python
set of unprimed item IDs to the bound inventory manager's `GetDamageForCrystals` and merges the returned dictionary.
The original client archive has SHA-256 prefix `37fb2af4fa0e`. EVE.js **261108.1**, build **3396210**, uses `invBrokerService.js`
(`8cec4112de1b`) and `crystalDurability.js` (`f6e598591778`) to return damage divided by catalog hit points.
[Crystal damage](https://wiki.eveuniversity.org/Crystal_damage)

Eden answers the bound call from the selected pilot's resident inventory. The reply contains an item ID and a damage ratio
from zero to one for each personally owned wear-enabled crystal. Unused crystals return zero. The read preserves item state
and uses the same committed damage that survives unloading, transfers and checkpoints.

**Automated validation.** `NativeCrystalDamageBatchReadsOnlyOwnedWearEnabledCrystalsFromTheResidentWorld` passed in
31.64 seconds through `Test-Feature.ps1` after a 44.95-second build. The test marshals the native Python set, calls
through the inventory manager binding, changes resident wear, rejects malformed arguments, and observes zero SQL commands,
connections or fitting calculations on the warm read. The solution built in 9.74 seconds with zero warnings or errors.
The following filter passed 20 tests in 42.08 seconds. The catalog setup and formatting policy suites passed 8 and 9 cases.
`Format.ps1 -Check` and the whitespace check passed. The exact test passed again on `95ebcb42` in 12.51 seconds after a
40.26-second build.

```text
FullyQualifiedName~EnergyTierTests.NativeCrystalDamageBatchReadsOnlyOwnedWearEnabledCrystalsFromTheResidentWorld|FullyQualifiedName~EnergyTierTests.NativeItemInfoReadsOwnedLooseCrystalConditionWithoutPrimingAnActiveModule|FullyQualifiedName~EnergyTierTests.NativePrimeIncludesSkilledLaserAttributesAndSavedCrystalDamage|FullyQualifiedName~EnergyTierTests.CrystalWearUsesTheCatalogFlagProbabilityAndIncrement|FullyQualifiedName~AmmunitionLookupTests|FullyQualifiedName~TypedInventoryTests|FullyQualifiedName~InventoryOwnershipTests
```

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Unload a partly worn crystal and inspect its damage in the Item Hangar alongside a fresh crystal.

## Inactive ship transfers and move-rule audit, November 15, 2026

The reported inability to remove items from an inactive ship came from the resident move operation's active-ship-only
source gate. The protocol also rejected additions to inactive ships, and `InventoryTransfers` always chose the active
ship as its ship destination. Stack splitting, merging, Stack All, corporation transfers and cargo-container access
repeated the same restriction independently.

Owned ships directly in the current station or structure hangar now support cargo transfers without boarding them.
`Add` and `MultiAdd` retain the actual bound ship ID. Resident access checks validate both inventories independently;
capacity and hold eligibility use the destination hull and its own fitting. Transfers can run between two local ships,
between a local ship and the hangar, or between a local ship and an authorized corporation division. Inactive cargo
containers can be opened and used, and container withdrawals use the selected destination hold's rules. SplitStack,
MultiMerge and Stack All share the same local access rule. Whole-batch rejection and checkpoint persistence are retained.

Additional corrections from the audit:

- A same-hold ordinary drag is an unchanged-state success even when that hold is overloaded.
- Unfitting from an inactive ship captures and resizes that ship's resources. Online-effect notifications identify the
  source hull, and the active ship does not receive an unrelated attribute refresh.
- Corporation and container withdrawals reject unavailable specialized holds even for zero-volume cargo.
- Partial resident loot pickups retain singleton state, volume override, crystal condition and blueprint metadata.
  Ordinary resident splits/transfers and the shared split operation also retain blueprint research/run metadata.
- A ship cannot be placed inside itself. Structure-service withdrawals explicitly reject unsupported inactive
  destinations instead of silently delivering to the active ship.

The reviewed boundaries are recorded here to distinguish ordinary inventory access from feature-specific operations:

| Move path | Rules checked and resulting boundary |
| --- | --- |
| Personal Add/MultiAdd | Owned direct local ships and personal hangar; real source, distinct IDs, positive available quantities, installed-blueprint rejection, destination filter/capacity and atomic rejection. |
| Split/MultiMerge/Stack All | Same access scope; packaged compatible stacks, metadata compatibility, selected quantities, overflow, occupied-container rejection and transaction rollback. Same-hold changes consume no extra capacity. |
| Fitted modules, rigs and magazines | Unfit uses the source hull's fitting checkpoint, returns loaded rounds atomically and destroys removed rigs. Direct fitting remains routed to the active ship; saved-fitting operations retain their separate contract. |
| Assembled ship storage | Native hold eligibility, permitted retained cargo, nested-ship rejection, escape-bay skill/one-ship limits, active-hull rejection and self-containment rejection. Carried/wreck ships remain readable under the existing rules but must be unloaded before their contents can be changed. |
| Cargo containers | Local owned parent, password/open permissions, audit locks, no assembled-container nesting, destination hold eligibility/capacity and unchanged batch rejection. In-space container transfers retain the ordinary-cargo boundary. |
| Corporation storage | Current local office and division permissions, ownership changes, industry/locked-asset protections, local owned ship access and destination capacity. Corporation-owned hull internals remain outside personal inventory access. |
| Wreck, jetcan, mission and hacking loot | Active in-space ship, relevant source rights/hacking state, range/system/deadlines, cloak/warp guards, destination hold filter/capacity and atomic transfer. Partial pickup metadata corrected. |
| Planetary orbit and structure-service stores | Existing range/access, commodity eligibility, customs-office capacity and specialized service boundaries. Structure-service withdrawals support personal hangar/active ordinary cargo; use personal inventory as an intermediate for inactive ships. |
| Direct trade, contracts and industry | Existing quote/settlement or job rules remain separate from Add/MultiAdd. Direct trade still requires offered loose stacks in the hangar or active ship; move inactive cargo to the hangar first. Courier packages, installed blueprints and corporate drops keep their feature-specific restrictions. |

No SQLite gameplay implementation or new offline fallback was added. New regressions use resident worlds, native
request round trips and the existing checkpoint/reopen mechanism. Existing offline compatibility tests remain historical
coverage; the inactive-ship behavior in this record is resident behavior.

**References.** [EVE University Neocom inventory](https://wiki.eveuniversity.org/Neocom#Inventory) describes access to
contents of docked ships, and [Managing Your Assets](https://wiki.eveuniversity.org/Managing_Your_Assets) distinguishes
remote inspection from local movement. The existing build-3662168 native Add/MultiAdd, SplitStack and MultiMerge evidence
in this page supplies the unchanged call shapes; `NativeShipInventory.json` supplies pinned hold filters and capacities.
The recorded archive SHA-256 is `37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368`.
[Carbon Blue Marshal.cpp at af0d65b](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/Marshal.cpp)
was consulted for the existing list/tuple/None boundary; serialization was unchanged. EveJS package 261108.1 was compared
for local ship custody ancestry in `server/src/services/inventory/invBrokerService.js`, SHA-256
`8CEC4112DE1B312B547E39B789391B0D8E18DFCA95AA61170F82AEFADC1E9651`. That AGPL reference supplied behavioral comparison;
no source was copied. No new live observations were collected.

**Automated validation.** The final affected run passed **805 tests**, including 17 new cases, with zero failures or skips (269.37 seconds for the test command). Tool restore (0.21 seconds), solution restore (1.34 seconds), solution build (9.75 seconds; zero warnings/errors), both Python policy suites (8 catalog and 9 formatting cases), and formatting verification (26.29 seconds) passed. The final validation sequence took 308.31 seconds. Documentation links and the new section anchor passed. The final affected filter was:

```text
FullyQualifiedName~InactiveShipInventoryTests|FullyQualifiedName~PilotInventoryTests|FullyQualifiedName~InventoryPersistenceTests|FullyQualifiedName~InventoryOwnershipTests|FullyQualifiedName~InventoryStackMergeTests|FullyQualifiedName~InventoryStackSplitTests|FullyQualifiedName~ShipInventoryRulesTests|FullyQualifiedName~TypedInventoryTests|FullyQualifiedName~CargoContainer|FullyQualifiedName~CorporationStorageTests|FullyQualifiedName~ShipStorageTests|FullyQualifiedName~HullHoldTests|FullyQualifiedName~MiningHoldConfigurationTests|FullyQualifiedName~PilotFittingTests|FullyQualifiedName~PassiveFittingTests|FullyQualifiedName~ModuleFittingTests|FullyQualifiedName~ApplyFittingTests|FullyQualifiedName~WreckTests|FullyQualifiedName~Jetcan|FullyQualifiedName~PlayerTradeTests|FullyQualifiedName~PlanetaryIndustryTests|FullyQualifiedName~MissionSingletonTests|FullyQualifiedName~ProductionInventoryTests|FullyQualifiedName~IndustryContainerTests|FullyQualifiedName~Structures.StructureTests|FullyQualifiedName~Hosting.GameplayDependenciesTests|FullyQualifiedName~Hosting.DataModelOwnershipTests
```

Development began with the exact filter
`FullyQualifiedName=Eden.Protocol.Tests.Persistence.InactiveShipInventoryTests.NativeMovesUseTheBoundInactiveShipAndSurviveCheckpointReopening`.
An initial expression-tree build error was corrected. Early test runs exposed fixture errors (an over-capacity success
case, missing spare-hull structure attributes and an absent drone category); those fixtures were corrected before the
final affected run. The dedicated partial-loot metadata/checkpoint regression also passed. The full .NET suite was not run.

Merge validation against main `444d88fb` passed 21 focused inventory/gateway cases, the solution build (zero warnings/errors),
both Python policy suites and formatting verification in 47.11 seconds. The only merge conflict was the protocol index;
both gateway and inventory entries were retained. The full .NET suite was not rerun.

**Live acceptance.** Not performed; agent client testing is disabled. Native repaint and drag/drop input remain unverified.

**Optional maintainer checks.** Drag cargo from an inactive docked ship to the hangar and back; split and merge one stack
in that ship; open a container aboard it and transfer an unlocked item.

## Container stacking and fitting, November 15, 2026

Cargo-container bindings now own native `StackAll` and same-container `MultiMerge` calls. Both operations
use resident transactions and the existing stack rules, preserve metadata and quantities, reject locked
stacks, and publish committed item changes. Stack All skips locked and singleton rows. Invalid later
merge operations roll back earlier changes, and opening-password checks use the connection's current
container authorization.

Fitting from a personal container now routes into the authoritative fitting operation in the same
resident transaction. It retains skill, slot, resource, docked-service, ownership and unlocked-content
checks, splits module stacks as required, and queues both inventory and ship-dogma updates. Ordinary
containers receive items at flag None; audit-log containers use their configured Locked/Unlocked flag,
matching build 3662168 `industryCommon.ItemOutputFlag`. The existing invCache `StackAll` and `MultiMerge`
call shapes were checked in the inert pinned archive identified in the linked industry record.
See [EVE University containers](https://wiki.eveuniversity.org/Containers) and
[assembling and repackaging](https://wiki.eveuniversity.org/Assembling_and_Repackaging).

**Automated validation.** See the [shared container workflow validation](industry.md#container-industry-workflows-november-15-2026).
Regressions cover Stack All, partial MultiMerge, unchanged locked stacks, merge rollback, password checks,
checkpoint reopen, module-stack splitting, occupied-slot rejection and dogma notifications.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Stack unlocked container contents and drag a module onto the docked ship's fitting window.

## Container item context menus, November 15, 2026

The follow-up client screenshot reported unsupported `invbroker.MachoBindObject(GetInventoryFromId)`
when right-clicking an item inside a container. In stock build **3662168**,
`menucheckers.ItemChecker.IsInAssetSafety` and `IsInTradeSession` walk upward with
`invCache.GetInventoryFromId(locationID).GetItem()`. The local `GetItem` fetches `GetSelfInvItem`.
A container in a station leads to a second inventory bind for the station; a container in cargo
leads through its ship first. The earlier container implementation supported the container and ship
steps but had no station-by-ID binding. The automated ancestry regression reproduced that exact
unsupported result for both locked and unlocked contents before the correction.

The selected-pilot inventory handler now maps the pilot's current docked location ID to the existing
personal hangar inventory. This supplies the existing station metadata row and terminates the native
menu walk. The binding uses location lifetime and expires on departure. Remote IDs, extra arguments
and station binds while undocked remain unsupported. Parent reads preserve contained item flags and
ownership, and the station listing continues to show only direct hangar contents.

The reference is [EVE University Containers](https://wiki.eveuniversity.org/Containers).
Inert inspection used the pinned archive SHA-256
`37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`.
The local EveJS `invBrokerService.js` (SHA-256 prefix `8CEC4112DE1B312B`, no Git metadata)
also recognizes the current station as an inventory target. No reference implementation was copied.
The existing Blue codec and inventory row models are reused.

**Automated validation.** The final affected run passed **220 tests** in 1 minute 46 seconds
(110.5 seconds including runner startup), including the 11 new container-menu/facility cases.
Tool/solution restore and solution build passed; the build took 19.2 seconds with no warnings.
Both Python policy suites passed (eight catalog and nine formatting tests), and formatting verification
and the 140-column check passed. Policy/format verification took 49.5 seconds. Documentation links,
anchors, UTF-8, final newlines, whitespace and `git diff --check` passed. The final required check sequence
took about three minutes, excluding diagnostic runs. No full .NET suite was requested or run.
The exact initial filter was
`FullyQualifiedName=Eden.Protocol.Tests.Inventory.CargoContainerAssemblyTests.NativeItemMenuWalksFromContainerThroughItsParentsToTheDockedStation`.
All four cases first failed at the missing station bind, then passed with the fix. The container class
subsequently passed 30 tests. Warm repeated station ancestry reads assert zero database commands,
connections and fitting calculations. No saved-world SQL implementation or fallback was added.

The first expanded selection included `PilotServicesTests` and was stopped after 163 passes and four
failures (167 completed tests, 1 minute 12 seconds). These failures came from its offline fixture reaching
the resident-only Abyssal tick. On unchanged main `f83a659c`, the first failing method passed in isolation,
but a broader baseline reproduced the same four failures and three additional failures with the identical
`Abyssal runs require the resident world` error; that run was stopped at 82 passes / seven failures
(89 completed tests, 54 seconds). These timing-sensitive baseline fixture failures remain outside this
change. The successful final selection excluded `PilotServicesTests` and used:

```text
FullyQualifiedName~CargoContainerAssemblyTests|FullyQualifiedName~PilotBindingsTests|FullyQualifiedName~TypedInventoryTests|FullyQualifiedName~PilotInventoryTests|FullyQualifiedName~ShipStorageTests|FullyQualifiedName~CorporationStorageTests|FullyQualifiedName~ServerServicesTests|FullyQualifiedName~IndustryFacilityLookupTests|FullyQualifiedName~Eden.Protocol.Tests.Industry.IndustryTests|FullyQualifiedName~BulkIndustryDeliveryTests|FullyQualifiedName~ProductionCatalogTests|FullyQualifiedName~ReactionJobTests|FullyQualifiedName~Eden.Protocol.Tests.Structures.StructureTests
```

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** On the updated server, right-click locked and unlocked items inside
a station container, then repeat with a container in ship cargo.

## Resident cargo container access, November 15, 2026

The supplied screenshots showed the ship-only rename rejection and unsupported bound
`invbroker.GetInventoryFromId` / `GetContainerContents` calls for assembled containers.
Cargo, secure cargo, audit log secure and freight containers now receive location-bound inventories.
`SetLabel` retains its single dispatch owner and routes containers to resident naming.
`GetSelfInvItem`, `List`, `GetCapacity`, content previews and native pre-open validation use
the authoritative resident item, including when the client supplies a packed row with stale fields.
Names publish `OnCfgDataChanged(evelocations, ...)` and survive subsequent name-cache reads.

General and configuration passwords support existence checks, change with old-password verification,
entry and authorized retrieval. Audit-container configuration and item lock/unlock calls use the pinned
bit flags. Every operation rechecks ownership, assembly and physical location; corporation reads
require current division query rights and administration also requires equipment configuration rights.
Passwords entered in one client context do not authorize another. Rotation revokes previously entered
passwords, and another pilot selection clears the session cache. Retrieve Password displays a native
information dialog because the stock client discards the RPC return value.

Personal containers accept whole or partial stacks from the current hangar or active cargo hold and
return unlocked contents through those inventories. Capacity, lock state, quantities and batch rejection
are checked before publication. Assembled containers cannot be nested. Corporation container content
transfers, audit history/log viewing and container stacking remain outside this change;
existing container repackaging restrictions remain in force.
Personal asset previews may read a remote station container after validating its real owner and station;
remote previews do not authorize opening a writable inventory or changing its metadata.

All new gameplay reads and mutations run through `LiveWorld.Economy`. Schema **165** adds
`WorldCargoContainer` solely as the checkpoint projection of resident names, passwords and configuration.
There is no gameplay SQL implementation or offline fallback. Item deletion cascades the resident settings
and their checkpoint row. Detached projection failure rolls back the checkpoint without losing resident
changes; retries capture the same committed metadata.

Gameplay references: [EVE University Containers](https://wiki.eveuniversity.org/Containers) and
[Corporation logistics](https://wiki.eveuniversity.org/Corporation_logistics).
Inert inspection used stock client build **3662168**, archive SHA-256
`37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`, specifically
`invCache.PasswordHandlerCallWrapper`, `menuFunctions.AskNewContainerPassword`, `ConfigureALSC`,
`RetrievePasswordALSC`, `containerContentWindow.GetContentsList` and `appConst.SCCPasswordType*` / `ALSC*`.
The existing Blue inventory descriptor and packed-row decoder are reused.
The local EveJS reference has no Git metadata: `invBroker/secureContainers.js` SHA-256 prefix
`E0231FF9A9FEA596` and `invBrokerService.js` prefix `8CEC4112DE1B312B` were compared for service behavior;
no implementation was copied. [Carbon Blue Marshal.cpp](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp)
at revision `fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae` was consulted for the serialization boundary;
native service contracts come from the pinned client.

**Automated validation.** Tool restore, solution restore and build passed in 10.2 seconds with no warnings.
The affected .NET run passed **256 tests** in 101.2 seconds including runner startup. Both Python policy
suites passed (**8 catalog tests**, **9 formatting tests**); formatting and the 140-column check passed.
Documentation links, the new anchor, UTF-8, final newlines, whitespace and `git diff --check` passed.
The full .NET suite was not requested or run. Earlier fixture setup omissions were corrected before this run.

After incorporating the unrelated chat keepalive commit `cae61118`, the integrated solution build passed
in 9.6 seconds and **27 container/service-registration tests** passed in 9.4 seconds including runner startup.
Container source was unchanged by that integration. The complete required check sequence took about
2 minutes 18 seconds; the additional integration build/tests took about 20 seconds.

Development started with exact method filters, including
`FullyQualifiedName=Eden.Protocol.Tests.Inventory.CargoContainerAssemblyTests.NativeContainerCallsRenameOpenViewContentsAndPersistBothPasswords`.
The final affected filter was:

```text
FullyQualifiedName~CargoContainerAssemblyTests|FullyQualifiedName~CorporationStorageTests|FullyQualifiedName~PersonalAssetServiceTests|FullyQualifiedName~PilotFleetTests|FullyQualifiedName~PilotInventoryTests|FullyQualifiedName~ShipStorageTests|FullyQualifiedName~TypedInventoryTests|FullyQualifiedName~InventoryStackSplitTests|FullyQualifiedName~InventoryStackMergeTests|FullyQualifiedName~ItemTrashTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~PilotBindingsTests|FullyQualifiedName~PlayerTradeTests|FullyQualifiedName~ServerServicesTests|FullyQualifiedName~GameplayDependenciesTests|FullyQualifiedName~DataModelOwnershipTests|FullyQualifiedName~CorporationShowInfoTests
```

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** On the updated server, rename and reopen a station container; set both
passwords and retrieve them; verify the native content preview and password prompt in a fresh login.

## Cargo container assembly and personal asset worth

The November 15, 2026 client screenshots reported `Invalid Packet Destination` on the first calls embedded in
`invbroker.MachoBindObject` and `charMgr.MachoBindObject`. Inert inspection of stock build 3662168 `code.ccp`
(`37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368`) confirmed both requests.

`menusvc.MenuSvc.AssembleContainer` sends `AssembleCargoContainer(itemID, None, 0.0)` for each selected row and discards
the result. The resident world assembles one unit per call in the pilot's local personal hangar, a local owned docked
ship's cargo hold, or a local corporation division with query and take rights. A one-unit package keeps its ID. A stack
retains its original ID and remainder and gets a new singleton row for the assembled unit. The transaction enforces hold
capacity using the difference between assembled and packaged volume. Packaged overrides come from the pinned
`inventorycommon.const`. Eden returns Python None and sends `OnItemChange` for committed rows. Changes persist in the
existing checkpoint. [EVE University containers](https://wiki.eveuniversity.org/Containers)

`invCache.inv5MinCacheContainer.GetAssetWorth` binds the global inventory moniker and calls `GetAssetWorth()` without
arguments. `AssetNetWorthNotifier.calculate_progression` unpacks two numbers in order, asset worth and PLEX worth. Eden
values the selected pilot's owned assets rooted in a station or structure, including contents of assembled containers
and docked ships. The calculation excludes blueprints, courier wraps, escrow and in-space assets. It prefers the same
reference estimates as the inventory price feed and uses positive catalog base prices for other priced types. Physical
PLEX contributes to the second total. [EVE University personal assets](https://wiki.eveuniversity.org/Managing_Your_Assets)

The older EveJS package **261108.1** supplied behavioral comparisons. Local SHA-256 values are
`0935A89DE41FCB48F892AB1E8651478C2983181B0F45776123AE46818A338191` for `invBroker/packaging.js` and
`65FD0F253351BA899B97EA9C2C2F76C67285621C18947A9A4C6EA26C3C4E289C` for `charMgrGlobalAssets.js`. The pinned client
supplied the request arguments. Eden reuses its existing tuple, None and inventory DBRow adapters.

**Automated validation.** The initial exact-method filter passed 4 cases covering first-call binding, checkpoint reopen,
corporation permissions and the valuation reply (25.92 seconds build, 8.08 seconds tests). The final focused run passed
443 cases in 34.17 seconds, including capacity rejection, inventory notifications and ownership. Tool restore (0.19 seconds),
solution restore (1.00 second), solution build (1.64 seconds, zero warnings and errors), catalog policy (8 cases, 1.27 seconds),
formatting policy (9 cases, 0.15 seconds) and formatting verification (32.95 seconds) passed. Full-suite coverage was outside
this focused run. The final filter was:

```text
FullyQualifiedName~CargoContainerAssemblyTests|FullyQualifiedName~PersonalAssetServiceTests|FullyQualifiedName~PersonalAssetRulesTests|FullyQualifiedName~CorporationStorageTests|FullyQualifiedName~PilotInventoryTests|FullyQualifiedName~InventoryOwnershipTests|FullyQualifiedName~InventoryPersistenceTests|FullyQualifiedName~InventoryStackSplitTests|FullyQualifiedName~InventoryStackMergeTests|FullyQualifiedName~ShipInventoryRulesTests|FullyQualifiedName~TypedInventoryTests|FullyQualifiedName~PilotBindingsTests|FullyQualifiedName~TypedCallResultTests|FullyQualifiedName~MarketValuationTests|FullyQualifiedName~ServerServicesTests|FullyQualifiedName~GameplayDependenciesTests|FullyQualifiedName~DataModelOwnershipTests
```

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** On the updated server, assemble a packaged station container and reopen the personal
inventory to check estimated worth.

## NPC wreck ownership from damage

Ordinary NPC wrecks and their dropped items now belong to the capsuleer with the highest total applied damage. Damage
from different hulls or drones is summed by character. Eden breaks exact ties by character ID, as in the reference
engine. A wreck in a mission with a saved owner still belongs to that mission owner. The final blow stays credited to
the pilot who fired it in Eden's combat history.

The resident kill settlement picks the wreck owner from the target's indexed damage contributions after the lethal hit
and before the report clears the contributions. Wreck ownership, contents and loot rights commit with the kill. The pinned
build **3662168** uses `HaveLootRight(itemID)` in `bracketVarious.IsForbiddenContainer` and
`celestialCheckers.OfferAbandonWreck`. Both use the existing wreck metadata and loot-rights path.

Sources include [CCP's container and wreck rules](https://support.eveonline.com/hc/en-us/articles/115000597345-Container-and-Wreck-Ownership),
[EVE University on salvaging and theft](https://wiki.eveuniversity.org/Ninja_Salvaging_and_Stealing), and the local EVE.js
`261108.1` snapshot's `combatDestruction.js` and `killmailTracker.js`. The pinned archive was inspected as inert code
objects and matched SHA-256 `37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368`.

**Automated validation.** `NpcWreckBelongsToTheHighestDamagePilotAcrossShipsAndKeepsTheFinalBlow` failed on the original
ownership assertion and passed after the fix in **7.24 seconds**. It checks resident damage aggregation, wreck and item
ownership, the separate final-blow record, and checkpointed loot after the target's damage rows are cleared. The
`NpcCombatTests|WreckTests|MissionGateAccessTests|LiveCombatObjectTests` filter passed **82 tests** in **45.60 seconds**.
The solution build had zero warnings and errors, and the catalog and formatting policy suites passed.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Have two pilots damage the same NPC, with the lower-damage pilot landing the final shot.
Check the wreck's ownership and the combat report.

## Criminal loot and lawful recovery, November 14, 2026

The next evejs comparison closed three connected recovery gaps:

- Wrecks and jetcans created while their owner has an active criminal timer are permanently public.
  A destroyed outlaw pilot's ship also leaves a public wreck. The saved `Abandoned` marker survives timer
  expiry, security recovery, restart and checkpoints; the loot keeps its original owner until taken.
- Pilots can take loot with full safety when its owner is suspect, criminal, outlaw (security status at
  or below -5), or in an active limited engagement with them. Temporary rights are checked at transfer
  time and expire at the exact deadline. They do not permanently abandon existing containers, create
  combat flags for the looter, or extend the owner's timer. Suspect-created containers remain private.
- Tractor beams accept player-corporation and lawful-owner access, in addition to owner/fleet/public
  rights. Activation and ongoing movement use current permissions. Leaving the corporation or losing
  the temporary right stops the pull without changing wreck ownership. NPC corporation membership
  grants no sharing rights.

The client's outlaw check requires `securityStatus` on the wreck/container slim. Contacts now include
that value from the owner's saved status. Reads batch owner lookups, and resident views invalidate only
for affected owners. A security-status change sends `OnSlimItemChange`; warmed observers and tractor
permission checks perform no SQL or fitting calculations. Existing Blue encoding and tractor movement
math remain unchanged. Schema stays **157**; this uses the abandonment field added in the prior milestone.
The prior record's automatic-abandonment limitation is superseded for newly created loot. War/faction
legality and additional deployable types are not introduced by this change.

The original build-3662168 archive was inspected inertly (SHA-256
`37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`).
`eve/client/script/remote/michelle.pyj`'s `Park.HaveLootRight` checks the four-field loot-rights tuple,
excludes NPC corporation sharing, and then calls `crimewatchSvc.CanAttackFreely`.
`eve/client/script/ui/services/crimewatchSvc.pyj` checks owner flags, limited engagement and the slim's
security status. `Park.IsAbandoned` reads only the saved public flag. The michelle module required inert
CPython 2.7 unmarshalling after xdis could not decode the complete module; only code objects were inspected.

The local evejs `261108.1` snapshot has no Git revision. `inventoryLootRights.js` and
`space/modules/tractorBeamRuntime.js` connect tractor access to shared loot entitlement;
`services/security/crimewatchState.js` implements legal-owner theft exemptions. Their SHA-256 values are:

- `server/src/space/runtime/inventoryLootRights.js`:
  `e308a33d85a403fc9b2a250d326c7e177e35d4d74699958b6ccfba33d0bc46d9`
- `server/src/space/modules/tractorBeamRuntime.js`:
  `f04058f4d7ee2f6685fbc342a538d846d136842127f3aef9dd12196200816717`
- `server/src/services/security/crimewatchState.js`:
  `46cf97d25ba2f99b925a3b4f1b5da80596be783ac6fcb1a3869628989a950f84`

Eden's implementation follows its typed world state and the pinned client, with gameplay confirmation from
[EVE University timers](https://wiki.eveuniversity.org/Timers),
[salvaging](https://wiki.eveuniversity.org/Salvaging),
[outlaw wreck recovery](https://wiki.eveuniversity.org/Hisec_Suspect_Hunting),
[CCP criminal consequences](https://support.eveonline.com/hc/en-us/articles/203268591-Criminal-Timer), and
[CCP's legal-container-access rule](https://www.eveonline.com/news/view/patch-notes-for-retribution-1).

**Automated validation** — Tool restore, solution restore/build (zero warnings/errors), 261 focused .NET tests,
both Python policy suites (8 catalog and 9 formatting tests), and formatting/140-column verification passed.
The final sequence took 137.3 seconds; the .NET test command took 102.1 seconds. Development started with the exact method filter
`FullyQualifiedName~CriminalJettisonIsPublicAfterTheFlagExpiresAndSurvivesCheckpoint`. Tests cover file and
resident worlds, real fatal damage, persisted public loot, full-safety access and deadline rejection,
corporation join/leave, timer expiry during a tractor cycle, observer slim fields, rollback and unrelated
cache invalidation. Build-time duplicate/missing imports were corrected before passing development runs.
No full .NET suite was requested or run.

The final filter combined `FullyQualifiedName~Eden.Protocol.Tests.Loot`,
`FullyQualifiedName~Eden.Protocol.Tests.Information`,
`FullyQualifiedName~Eden.Protocol.Tests.Combat.CrimewatchTests`,
`FullyQualifiedName~Eden.Protocol.Tests.Combat.NpcCombatTests`,
`FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.CooperativeFleetTests`,
`FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.CorporationMembershipTests`,
`FullyQualifiedName~Eden.Protocol.Tests.Persistence.InventoryStackSplitTests`,
`FullyQualifiedName~Eden.Protocol.Tests.Persistence.ResidentReadTests`, and
`FullyQualifiedName~Eden.Protocol.Tests.Planetary.PlanetaryProtocolTests` with `|`.

**Live acceptance** — Not performed; agent client testing is disabled.

**Optional maintainer checks** — Check public loot markers after a criminal ship loss and tractor access
between two corporation members; verify the tractor stops when the acting pilot leaves the corporation.

## Loot abandonment and theft, November 14, 2026

The evejs comparison identified two missing gameplay paths: the native **Abandon Loot / Abandon All**
command and taking another pilot's wreck or jetcan contents with Crimewatch consequences.
`beyonce.CmdAbandonLoot([itemIDs])` now makes the caller's nearby wrecks and jetcans public. It preserves
item ownership and expiry, accepts at most 1,000 distinct positive IDs, and skips foreign, missing,
expired or out-of-range entries. Empty selections are harmless. The pilot must be uncloaked and in space.

Build 3662168's `menuSvcExtras/menuFunctions.pyj` sends a Python list for both menu actions.
`AbandonAllLoot` selects by `HaveLootRight`, which can include fleet/corporation loot; the server must
skip entries the caller does not own instead of rejecting the entire selection. The existing slim
`lootRights=(ownerID, corporationID, fleetID, abandoned)` now carries the saved public flag, and
`OnSlimItemChange` publishes the update without replacing the ball. Public wrecks also accept tractor
beams from other pilots, including during resident advancement.

Nearby pilots can inspect foreign contents without a timer. Taking them checks the current owner,
player-corporation rights, fleet access and abandoned state. Sharing an NPC corporation grants no rights.
Unauthorized taking in high/low security space is blocked by full safety; partial/disabled safety
permits it and starts or refreshes the 15-minute suspect timer. Alpha pilots retain Eden's enforced full
safety in high security even if a lower setting was saved elsewhere. Null-security theft creates no flag.
An active criminal timer is extended instead of creating a suspect timer, following CCP's documented
rule. This intentionally differs from the inspected evejs implementation, which skips that extension.
Theft alone creates no weapons, PvP or limited-engagement timer. Existing Crimewatch polling sends the
native timer/system-flag notifications and clears them at the saved deadline.

Inventory transfer and the consequence commit together. Invalid quantities, capacity failures and
failed writes leave ownership and timers unchanged. Empty jetcans disappear after committed transfer.
Schema **157** adds `Abandoned` to wrecks and jetcans, defaulting existing records to private. Resident
snapshots, SQL projection and disk checkpoints preserve it, including updates to already-checkpointed
containers. The migration adds the column before historical salvage backfill materializes wrecks.

Scope remains wrecks and jetcans: this does not add automatic abandonment for criminal owners,
player-deployable launches, protected corporation property, or changes to salvage mechanics.
The historical owner-only limitation in the cargo-jettison record below is superseded by this milestone.

Reference evidence:

- Inert inspection used the original build-3662168 archive with SHA-256
  `37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`.
- The local evejs snapshot has no Git metadata, advertises version `261108.1` and targets client build
  3396210. It was used to identify gaps; Eden's C# implementation was independently authored against
  its typed state and the pinned client contract. Inspected SHA-256 values:
  - `server/src/services/ship/beyonceService.js`:
    `5c13b78749bd02c31d7844d631fc88b818b299deff4ac49058a860eeb3f9027d`
  - `server/src/space/runtime/spaceRuntime/pilotCommands.js`:
    `6148a777fec47f3598a1a4f2e4616ac4ff1e399a556629723b16f45aa91de272`
  - `server/src/space/runtime/inventoryLootRights.js`:
    `e308a33d85a403fc9b2a250d326c7e177e35d4d74699958b6ccfba33d0bc46d9`
  - `server/src/services/_shared/spaceLootEntitlement.js`:
    `242f50a504fb8c7e9240a3d04a657b2373ba0e9098187eecba8c4855720272f0`
  - `server/src/services/security/crimewatchState.js`:
    `46cf97d25ba2f99b925a3b4f1b5da80596be783ac6fcb1a3869628989a950f84`
- Gameplay references: [EVE University salvaging](https://wiki.eveuniversity.org/Salvaging),
  [ninja salvaging and stealing](https://wiki.eveuniversity.org/Ninja_Salvaging_and_Stealing),
  [CCP suspect timer](https://support.eveonline.com/hc/en-us/articles/203268571-Suspect-Timer),
  [ship safety](https://support.eveonline.com/hc/en-us/articles/205268171-Ship-Safety-Setting), and
  [criminal timer precedence](https://support.eveonline.com/hc/en-us/articles/203268591-Criminal-Timer).
  Eden reuses its existing Blue and Destiny encoders and tractor movement rules; no engine algorithm changed.

**Automated validation** — Tool/solution restore, solution build with zero warnings or errors,
**204 focused .NET tests**, both Python policy suites (**8** catalog and **9** formatting cases), and
`tools/Format.ps1 -Check` passed. The final sequence took **107.56 seconds**, including **66.77 seconds**
for .NET tests. It used `--artifacts-path artifacts/loot-rights-tests` to isolate outputs from concurrent
checkout work. No full .NET suite was run.

The initial exact method filter was
`FullyQualifiedName~ForeignLootIsReadableButTakingRequiresSafetyAndCommitsOnlyASuspectTimer`.
The final filter joined `FullyQualifiedName~` clauses for `Eden.Protocol.Tests.Loot`,
`Eden.Protocol.Tests.Information`, `Eden.Protocol.Tests.Combat.CrimewatchTests`,
`Eden.Protocol.Tests.Persistence.InventoryStackSplitTests`, `Eden.Protocol.Tests.Persistence.ResidentReadTests`,
`Eden.Protocol.Tests.Planetary.PlanetaryProtocolTests`, `Eden.Protocol.Tests.Combat.NpcCombatTests`, and
`OnGridFleetSharesBountyAndCanLootWreckUntilMembershipEnds` with `|`.

Coverage includes native RPCs and slim serialization, observer updates, safety and Alpha restrictions,
partial/full transfers, rollback after an inventory write failure, repeat-taking rejection, exact timer
expiry, criminal precedence, fleet permissions, public tractor movement, schema-156 migration, reopened
worlds and disk checkpoints of existing containers. Development runs found and corrected missing fixture
security data and SQL jetcan deletion ordering. An older salvage test was updated to assert that inspection
is legal while taking remains safety-gated. One early build failed on incomplete concurrent AutoMining edits;
a rerun reported the old salvage assertion after its source changed, so final validation used isolated outputs.

**Live acceptance** — Not performed; agent client testing is disabled.

**Optional maintainer checks** — With two pilots, check the blue wreck/container marker after **Abandon All**
and the suspect HUD timer after looting a yellow container with partial safety.

## Cargo jettison, November 13, 2026

The native Inventory **Jettison** action sends the selected item IDs to the location-bound `ship.Jettison`.
The response is a tuple of two Python lists. The first contains the original IDs moved into the cargo
container. The second is reserved for standalone launches. The client reports any IDs missing from both
lists. Each successful call creates one type-23 **Cargo Container** within 2,500 m of the active ship.
The pinned catalog supplies its 55,000 m³ capacity. A pilot can create another container after three
minutes and can add cargo to an existing one during that interval.

Schema 146 saves the container, expiry, and pilot cooldown. The container and inventory changes commit
together. Nearby pilots see the container in space, and it appears on directional scan. Its owner can open
it, add stacks, and recover partial or full stacks within transfer range. An emptied container disappears
immediately. At two hours, the container becomes inaccessible and its remaining contents are deleted
during system contact refresh or world startup. The transfer policy follows Eden's owner-only wreck loot
rights. Standalone deployable launches, theft, and Crimewatch consequences are outside this implementation.

Inert inspection of build 3662168 used the original archive with SHA-256
`37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`.
The relevant client entries are `invItemFunctions.Jettison`, `eveMoniker.GetShipAccess`,
`inventorycommon.const.jettisonableFlags`, and `invControllers.BaseCelestialContainer`. The local evejs
reference had no Git metadata. Its inspected `shipService.js` had SHA-256
`c34c3573c11cbaeca041d2cdc4791e19db7db6759a94f73125ad71e6b62848f1`.
The wire adapter uses Eden's existing tuple/list and free-ball encoding.
[Carbon Blue af0d65b](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/Marshal.cpp)
[EVE University mining guide](https://wiki.eveuniversity.org/Mining#Jetcan_and_GSC_mining)
[CCP jettison cooldown](https://www.eveonline.com/de/news/view/new-features-config-changes-and-bug-fixes-6)

**Automated validation** — Tool and solution restore, build with zero warnings and errors, 86 focused
.NET tests, both Python policy suites (8 catalog and 9 formatting cases), and formatting verification
passed in 89 seconds. The .NET run took 51 seconds. Tests covered the bound ship RPC, cargo notification,
observer spawn and removal, partial deposit and pickup, capacity, ownership, transfer range, persisted
cooldown and expiry, concurrent jettison, and durable checkpoint contents. The full suite was not run.

The initial exact filter was
`FullyQualifiedName=Eden.Protocol.Tests.Loot.JettisonTests.BoundShipJettisonPublishesTheContainerAndSupportsNativeDepositAndLootAll`.
The final filter joined `FullyQualifiedName~` clauses for `Eden.Protocol.Tests.Loot.JettisonTests`,
`Eden.Protocol.Tests.Loot.WreckTests`, `Eden.Protocol.Tests.Persistence.InventoryStackSplitTests`,
`Eden.Protocol.Tests.Information.InformationTests`, `Eden.Protocol.Tests.Persistence.ResidentReadTests`,
and `Eden.Protocol.Tests.Planetary.PlanetaryProtocolTests` with `|`.

**Live acceptance** — Not performed; agent client testing is disabled.

**Optional maintainer check** — Undock with a cargo stack, select **Jettison**, then open the new cargo
container from the overview and use **Loot All**.

## Trashing personal items and ships (November 8, 2026)

Implemented selected-pilot `invbroker.TrashItems([itemID, ...], stationID)` through the native inventory-manager binding. It returns Python `None` on success and a native RPC failure for invalid requests. The list must contain one to 1,000 distinct positive IDs, without extra arguments or keywords. A batch must share an immediate location and flag. Trashing permanently removes complete stacks, packaged ships, inactive assembled ships and station containers. Personal assets at remote NPC stations are supported, including while the caller is elsewhere; loose cargo inside docked ships and containers resolves through its authoritative parent chain to the supplied station.

The operation discovers and validates the entire contents graph before deletion, including fittings, rigs, loaded ammunition and ships stored in bays. It checks all owners and active-ship references, rejects ships in space, direct fitted-item selections, installed industry blueprints, contract escrow, in-progress courier packages and the pinned catalog's non-trashable types. Hidden escrow or foreign descendants reject the entire batch instead of becoming orphaned inventory. A malformed, duplicate, missing, mixed-location or protected selection leaves the full batch unchanged. Corporation trashing and trashing space inventory are unsupported.

All removals and insurance invalidations commit in one existing world transaction. Trashing a hull voids its insurance without refund, payout or combat-loss report. Existing foreign-key cascades remove module condition, health and other dependent runtime rows, while inventory descendants are explicitly removed. The normal world transaction bridge and checkpoint machinery remain authoritative. Only committed results generate named `OnItemChange` notifications; removal rows use the existing graveyard-location adapter and preserve the former stack quantity. Blueprint removal also invalidates the native blueprint cache with `OnBlueprintsUpdated`.

**Pinned-client evidence.** Inert inspection of build **3662168** `code.ccp` (SHA-256 **37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**) confirms `invItemFunctions.TrashInvItems` owns the confirmation prompt, calls `CheckItemsInSamePlace`, supplies a Python list of item IDs and the containing station ID to `GetInventoryMgr().TrashItems`, and treats a falsy response as success before invalidating its local location cache. `inventoryrestrictions.common.checks.is_trashable` references `evetypes.const.TYPE_LIST_NON_TRASHABLE_ITEMS` **298**. `evetypes.GetTypeIDsFromTypeList` subtracts excluded categories, then unions included groups and subtracts excluded groups, then unions included types and subtracts excluded types. Eden preserves that precedence in typed Gameplay rules. The installed catalog's record 298 includes types **60033** and **73484**; implementation reads the catalog rather than hardcoding those IDs.

**References.** [EVE University: Managing Your Assets](https://wiki.eveuniversity.org/Managing_Your_Assets) describes station inventory, personal assets and remote location caching; it is a gameplay reference, not evidence for this RPC or exact deletion rules. [Carbon Blue Marshal.cpp, revision af0d65b29f951260ea09140ca11adf5d40566997](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/Marshal.cpp) was consulted for the existing list/tuple and None boundary. No codec changes or live-client observations were introduced.

**Automated validation.** Tool restore, solution restore and build passed with zero warnings and errors; **236 affected .NET cases passed**, including **41 trashing cases**. Both Python policy suites passed (**8** catalog setup and **9** formatting-policy cases), and `tools/Format.ps1 -Check` passed. The complete stable validation sequence took **118.76 seconds**, with the .NET run reporting **1 minute 28 seconds**. The full .NET suite was not run. Validation uses temporary worlds only, covering local and remote stacks, remote trashing from space, assembled and stored hulls, fittings and loaded charges, container/blueprint notifications, ownership and commitment rejections, database rollback, foreign-key integrity, and stopped-world reopen after resident shutdown/checkpoint persistence.

The initial exact filter was `FullyQualifiedName=Eden.Protocol.Tests.Inventory.ItemTrashTests.TrashRemovesCompleteStacksAndPackagedShipsAtLocalAndRemoteStationsAcrossReopen` (two cases passed at that revision). Development fixture-only ownership-key, catalog-tracking and live-world shutdown failures were corrected before the final passing run. The final combined filter was:

```text
FullyQualifiedName~Eden.Protocol.Tests.Inventory|FullyQualifiedName~Eden.Protocol.Tests.Docking.TypedInventoryTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.Inventory|FullyQualifiedName~Eden.Protocol.Tests.Persistence.PilotInventoryTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.ShipStorageTests|FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.PersonalAssetServiceTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.ContractAssetTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.ContractTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.CourierContractTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.InsuranceTests|FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.InsuranceServiceTests|FullyQualifiedName~Eden.Protocol.Tests.Industry.IndustryTests|FullyQualifiedName~Eden.Protocol.Tests.Industry.ProductionInventoryTests
```

**Live acceptance.** Not performed; agent client testing is disabled. Native menus, confirmation presentation, cache repaint and input remain unverified.

**Optional maintainer checks.** Trash a disposable local stack and inactive fitted hull through the native menu; trash a remote personal asset and refresh its station listing; inspect removal of a container containing a blueprint.

## Capsule retirement on boarding (November 6, 2026)

Boarding a docked ship previously changed only the active-ship identity, leaving the old capsule in the hangar. Ordinary `ship.Board` and `dogmaIM.CreateNewbieShip` now retire the capsule in the same transaction as adopting the target hull. The active-ship foreign key is saved before capsule deletion; failure rolls back both the switch and task progress. Ordinary previous hulls, their cargo/fittings and unrelated capsules remain intact. This fixes subsequent boarding operations; it does not purge existing inactive capsules from saves.

`IPilotFleet.Board` now returns the existing named `ShipBoarding` model so the protocol handler receives the committed inventory changes. The client gets an `OnItemChange` moving the retired singleton to Graveyard location 8, with its previous station and hangar flag. The assembled row keeps quantity -1 and omits a quantity-change entry. Repeated boarding is a no-op for inventory; recovery retries also remain harmless after the previous capsule has been deleted. Existing capsule reuse when leaving a hull remains supported.

Inert inspection of build **3662168** `code.ccp` (SHA-256 **37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**) rechecked `clientDogmaLocation._MakeShipActive`: it calls `Board(shipID, session.shipid)` and unpacks the existing four-part dogma reply. Station `CreateNewbieShip` passes the previous ship and location to the recovery call. `invCache.OnItemChange` removes junk-location rows and rejects quantity-change entries on negative-quantity assembled rows. The installed catalog confirms capsule type **670**, group **29**, volume **1,000 m³**. Consulted local [Carbon Blue Marshal.cpp at fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp), including its DBRow read/write adapters; this change uses the existing Blue row encoding and changes no codec or Destiny format.

**Automated validation.** The final affected-test run passed **458/458 cases**, including **10 new regression cases**, in **44 seconds**. Tool and solution restore, build with zero warnings/errors, the Python catalog (**3**) and formatting-policy (**9**) suites passed. Formatting verification (**871 files**), changed-file text hygiene, documentation links/anchors and `git diff --check` passed. The final required sequence took **66.46 seconds** overall. No full .NET suite was authorized or run. Coverage includes three repeated leave/board and recovery cycles, new/reused recovery hulls, restart, current-ship and repeated-request no-ops, unchanged cargo and unrelated pods, foreign/remote/packaged/untrained/stale/in-space rejection, atomic rollback on capsule deletion failure, and native removal notification fields with the unchanged dogma reply. The development run exposed two test setup errors, corrected before its **41-case** pass; the first wider run passed **435/436** cases and exposed a missing capsule volume in the shared combat fixture. That fixture now includes the catalog volume required to construct the removal notification. The next wider run passed **456/458** cases and exposed two existing recovery assertions that expected only the three creation changes; both now assert the fourth capsule-removal change and the capsule's absence.

Final affected-test filter (`dotnet test --no-build --filter`, `xUnit.MaxParallelThreads=4`):

```text
FullyQualifiedName~PilotFleetTests|FullyQualifiedName~CorvetteRecoveryTests|FullyQualifiedName~JumpCloneTests|FullyQualifiedName~JumpCloneServiceTests|FullyQualifiedName~HomeStationTests|FullyQualifiedName~ShipStorageTests|FullyQualifiedName~ShipInventoryRulesTests|FullyQualifiedName~PilotInventoryTests|FullyQualifiedName~InventoryPersistenceTests|FullyQualifiedName~CourierContractTests|FullyQualifiedName~PilotHealthTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting
```

**Live acceptance.** Not performed; agent client testing is disabled. Native rendering, input and hangar refresh remain unverified.

**Optional maintainer checks.** With Ship Hangar open, leave a hull and board it again; confirm the pod tile disappears. Repeat using Board my Corvette from a pod.

## Personal ship insurance

Replaced insurance compatibility replies with catalog quotes, durable personal policies and wallet settlement. Build **3662168** inert inspection confirms `GetInsurancePrice(typeID)`, `GetInsurancePrices(typeIDs)`, `GetContracts(isCorp=False)`, `GetContractForShip(shipID)`, `InsureShip(shipID, amount, isCorp)` and `UnInsureShip(shipID)`. Native station binding uses the three-field `(stationID, stationID, locationGroup=15)` tuple; mismatched locations and in-space binding are rejected. Station service **21** is required for purchase and cancellation. Native replacement first returns `InsureShipFailedSingleContract` with `ownerName`; the client confirms a second purchase with keyword `voidOld=True`.

`insuranceConst` defines six paid coverages, **50–100%**, with premiums **5–30%** of the quote and a free **40%** baseline. Eden policies last **84 days**. Only owned assembled hulls in the caller's station hangar qualify; carried hulls, other owners, invalid amounts, missing service and insufficient funds fail without mutation. Replacement charges a new full premium without refunding the old one; cancellation has no refund. Contracts expire at their exact end timestamp and do not appear in active queries afterward. Corporation purchase is explicitly unavailable.

**Valuation policy:** Eden uses pinned `client/types.basePrice`, separately from testing-market offers. Missing/zero values, values below 1 ISK and capsules yield zero and cannot be insured. This is a local reproducible policy: [CCP's insurance description](https://www.eveonline.com/news/view/the-circle-of-life) uses periodically updated manufacturing-material prices and hull multipliers; Eden has no equivalent live index. In particular, pinned corvettes with zero base price have no default payout. Prices and premiums use integer cents with midpoint-to-even rounding.

World version **43** adds policy history and an insurance ledger. Purchase, debit and journal entry commit together. Fatal NPC damage settles active coverage or the free baseline inside the same transaction as hull destruction, wreck/drop creation and the capsule transition. The payout uses the saved policy valuation when covered. A unique per-hull payout key prevents duplicate credits; failed ledger writes roll back the entire loss. Removing an unpiloted/carried hull closes its policy without a payout. Hull identities and type IDs remain in history after deletion; reopening retains policies and wallet entries. Client balance notifications follow committed payments. Wallet entry type **19** uses Pend Insurance **1000113**, negative ship references for premiums and positive hull-type references for payouts, as required by native `FormatReference`.

References: unchanged pinned archive hash and Carbon Blue revision recorded above; existing KeyVal/list/numeric adapters are reused without codec changes. Native insurance service, insurance constants, moniker binding and wallet formatting were inspected as inert code objects. No gameplay Python executed or client launched. Insurance mail, corporation policies, structures and a retail valuation index remain pending.

**Automated validation.** Final filter `FullyQualifiedName~Insurance|FullyQualifiedName~ShipStorageTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~NpcPursuitTests|FullyQualifiedName~NpcOrbitTests|FullyQualifiedName~NpcAggressionTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~Eden.Protocol.Tests.PilotSessions|FullyQualifiedName~Eden.Protocol.Tests.Market|FullyQualifiedName~Eden.Protocol.Tests.Repair.RepairTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting`, run with `dotnet test --no-build` and `-- xUnit.MaxParallelThreads=4`: **215 passed**, approximately **48 seconds**. Coverage includes all six packages, catalog hull variants, native calls/binding and wallet references, replacement/cancellation/expiry, rejection with unchanged state, concurrent purchase and loss settlement, exact expiry, rollback and restart/migration persistence. Tool/solution restores, solution build (zero warnings/errors), both Python policy suites (3 catalog and 9 formatting tests), formatting and diff checks passed in **69.50 seconds** combined. Early development failures identified no-tracking fixture edits, shield recharge at the 84-day expiry boundary and an obsolete station binding fixture; these were corrected before passing runs. Final client inspection also corrected premium/payout journal reference signs and retained hull type after deletion. No full .NET suite was requested or run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** At an insurance station, buy/replace/cancel a policy on a nonzero-value hull and reconnect to inspect it. Check the wallet premium description and payout description after an NPC loss. Native rendering and interactions remain unverified.

## Native inventory stack division

The user reported that dividing a **50-unit Large Skill Injector stack** did not work and supplied a second Information dialog: **Items can only move between your docked ship's cargo and its station hangar.** That text identifies the station/ship transfer gate in `PilotInventoryStore.Move`. A same-hangar quantity move was rejected even though its purpose was to create a second stack in the same location. The contract wizard's separate five-argument `contractProxy.SplitStack` already existed and did not cover these ordinary inventory paths.

Inert inspection of pinned build **3662168** identifies both native requests. `invControllers.BaseInvContainer._AddItem` prompts for Shift/forced quantity, and `__AddItem` forwards bound-container **Add(itemID, sourceLocationID, qty=quantity, flag=locationFlag)**. When dropped back into the same inventory, source and destination are identical. Separately, `menuSvcExtras/invItemFunctions.SplitStack` sends the inventory manager **SplitStack(stationID, itemID, quantity, ownerID)**; the fourth argument is the owner, not the corporation boolean used by the contract wizard. The menu rejects items without a station before making this call and ignores the result.

`InventoryHandler` now owns that four-argument `invbroker.SplitStack`, checks the selected owner/current docked station and resolves the actual hangar or active ship hold. Both UI paths feed the existing typed `InventoryMoveRequest` and transaction. Same-location, same-flag quantity moves separate the selected units into a new ID without merging into a matching existing stack. The original row retains its ID and remainder; both rows retain type, owner, location, flag, crystal condition and volume override. A full-stack quantity is a successful unchanged-state no-op. Capacity is unchanged, so an existing full/overloaded hold can still divide its contents.

Packaged-stack validation lives in `InventoryStackRules` in Gameplay and is reused by the contract split rule without changing that API or its strict two-positive-stacks behavior. Singleton/online stacks, containers with contents, installed manufacturing inputs, foreign or unavailable items, invalid quantities, remote hangar moves and forged owner/keyword requests reject without stock changes. Shift-drag also supports splitting in the active hold while in space, but changing holds, transferring to a station, touching stored ships or fitting items remains disallowed there. The menu follows the client's station-only rule. The existing remote contract-preparation split remains separate and unchanged.

Persistence saves the source decrement and new row in one SQLite transaction. Failed insertion rolls back both. Existing `OnItemChange` adapters publish the original's old quantity/stacksize and the new stack's absent old location/stacksize, which inserts the new tile into invCache. No inventory codec, schema, skill points, item grants, running world or client files are changed.

References: staged `E:/EdenDev/3662168/tq/code.ccp`, SHA-256 **37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**, inspected as inert code objects; the functions above and `invCache.GetInventoryMgr`. Consulted the saved [Carbon Blue Marshal.cpp at fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp), `Py_None`/`TY_NONE`, for the successful ignored None result. Existing DBRow inventory descriptors and notification adapters are reused; no gameplay Python was imported or executed.

**Automated validation.** On the combined tree, final filter `FullyQualifiedName~InventoryStackSplitTests|FullyQualifiedName~PilotInventoryTests|FullyQualifiedName~ContractAssetTests|FullyQualifiedName~ContractAssetServiceTests|FullyQualifiedName~DockedServicesTests|FullyQualifiedName~InventoryPersistenceTests|FullyQualifiedName~MailComposerDefaultsTests` passed **57 tests** (zero failed/skipped) in **7 seconds**. Tool/solution restores, solution build (zero warnings/errors), both Python policy suites (**3 catalog**, **9 formatting**), formatting and diff checks passed. Final validation took **26 seconds** combined. Initial development corrected a local variable-name collision and test assertions that referenced persisted metadata on the narrower wire row; no tests ran on those failed builds. The first corrected focused run passed **41 tests** in **6 seconds**. After adding the space-only same-hold boundary, filter `FullyQualifiedName~InventoryStackSplitTests|FullyQualifiedName~PilotInventoryTests|FullyQualifiedName~ContractAssetTests|FullyQualifiedName~ContractAssetServiceTests|FullyQualifiedName~DockedServicesTests|FullyQualifiedName~HullHoldTests|FullyQualifiedName~ShipInventoryRulesTests|FullyQualifiedName~InventoryPersistenceTests` passed **507 tests** in **84 seconds**, including all existing personal-hold variants, packaged-volume and cargo-filter checks. The milestone then integrated main's unrelated native-mail-default correction (`70ae1ca`) before final checks. No full .NET suite was run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Rebuild/restart the server, split one unit from a 50-unit stack through the menu and Shift-drag, then reconnect and inspect both stacks. Optionally repeat Shift-drag in ship cargo while in space. Native tile rendering, input and dialog behavior remain unverified.

## Persistent ship dirt and native cleaning

The old `ship.GetDirtTimestamp(shipID)` compatibility reply returned the current time on every call, effectively keeping hulls perpetually new. `ShipAppearanceHandler` now owns that read and native `ship.ResetDirtTimestamp(shipID)`. Both take exactly one positive ship ID with no extra arguments/keywords, require pilot selection, and return the authoritative native FILETIME timestamp as a 64-bit integer. The typed appearance store loads only the selected pilot and ship ownership/location from persistence, without requiring cargo-volume catalog data. A shared named `ShipStorageLocation` projection applies the existing `ShipStorageRules.CanRead` access rules: active owned hulls in space/station, owned local hangar hulls and reachable stored hulls. Unknown, foreign, remote and contract-escrowed hulls reject before any initialization or reset.

World version **53** adds `WorldShipDirt`, keyed by assembled ship identity with a cascading foreign key to `WorldShip`. Eden has no reliable historical assembly/cleaning date for hulls that lack this state. Their first authorized read or clean establishes a baseline once; subsequent reads retain it, including across restarts. A clean advances that value to authoritative server time and never backdates it if the clock moves backward. It changes neither wallet, ship selection, health nor fitting. Ownership transfer retains the hull's dirt row; the former owner loses access, while the receiver sees the same timestamp. Removing the assembled hull cascades the row rather than leaving orphan appearance state. The version-52 migration creates the table without altering ships or pilots.

The pinned hangar behaviour calls `GetDirtTimestamp` and lets `evegraphics.utils.CalcDirtLevelFromAge` derive the visual age. The fitting `CleanShipButton.CleanShip` calls `ResetDirtTimestamp` and then invokes its local hangar's `OnDirtLevelChanged`, so the server does not duplicate that hangar event. Player ships in space read **`slimItem.dirtTime`**. Production flight sessions load/cache the saved value once for the current hull, include it in owner/observer snapshots and refresh that cache after a successful clean. Replacement hulls get their own baseline. Cleaning an active visible hull publishes a full `OnSlimItemChange` to its owner and, on the next peer refresh, visible observers. Unchanged peer refreshes do not repeat the update, and dirt updates do not issue position corrections or reset navigation. Hand-built test flight sessions can omit the appearance interface and preserve the intentional None boundary; production factory construction always supplies it.

References: inert inspection of build **3662168** `code.ccp`, with the same SHA-256 recorded above, including `eve/client/script/ui/shared/fitting/cleanShipButton.pyj`, `ui/view/hangarBehaviours/baseHangarBehaviours.pyj`, `environment/spaceObject/playerShip.pyj` and `evegraphics/utils.pyj`. Native `PlayerShip.UpdateDirtLevel` reads `dirtTime`, while the client owns the visual age calculation. Consulted [Carbon Blue Marshal.cpp at fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp), `ReadObjectNewobj`, alongside Eden's existing named slim adapters and primitive timestamp handling. No serialization or transport change, game launch or gameplay Python execution was needed.

**Automated validation.** Initial focused appearance checks passed **eight cases** in approximately **three seconds**. Development compilation corrected a test's nonexistent repair-payment type reference and a later local-variable name collision before test execution. The first expanded affected run passed **407 of 409** cases in approximately **1 minute 3 seconds**, then stopped: two simulated fatal-hit/capsule session cases showed that appearance access unnecessarily read cargo volume for capsule type 670. Access now uses a shared typed ownership/location projection rather than loading volume/catalog data; a focused correction filter `FullyQualifiedName~ShipAppearance|FullyQualifiedName~SessionSendsTheFatalMessageAndCapsuleBallBeforeChangingShipId|FullyQualifiedName~ShipStorageTests|FullyQualifiedName~ShipInventoryRulesTests` passed **343 cases** in approximately **38 seconds**. The final required root sequence, using `FullyQualifiedName~ShipAppearance|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~Contract|FullyQualifiedName~Courier|FullyQualifiedName~Auction|FullyQualifiedName~ShipStorageTests|FullyQualifiedName~PilotFleetTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~CorvetteRecoveryTests|FullyQualifiedName~Eden.Protocol.Tests.Flight|FullyQualifiedName~Eden.Protocol.Tests.PilotSessions|FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~BlueMarshalTests|FullyQualifiedName~ShipInventoryRulesTests|FullyQualifiedName~PilotInventoryTests|FullyQualifiedName~InventoryPersistenceTests` with `xUnit.MaxParallelThreads=4`, passed **715 affected tests**, including **11 new appearance cases**, in **1 minute 1 second**. Tool/solution restores, build with zero warnings/errors, Python catalog (**3**) and formatting-policy (**9**) suites, formatting (**507 files**), diff verification and documentation status/unique-anchor checks passed. The corrected sequence took **83.26 seconds** overall. Coverage includes repeated aging/cleaning cycles with `TestClock`, backward-clock protection, concurrent baseline creation, ownership/reachability rejection with unchanged state, contract transfer, restart, real version-52 migration, removal cascades, wallet/health preservation, native arguments/64-bit replies and production-factory owner/observer metadata without navigation corrections. Ignored TRX artifacts retain the failed and corrected affected-run results. No full .NET suite was authorized or run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Revisit a hull after aging and reconnecting, then use native Clean Ship and inspect the hangar model. Clean a visible hull in space and compare owner/observer models. Native rendering and the perceived aging/cleaning effect remain unverified.

## First-party ship SKIN licenses

In build **3662168**, `cosmetics/client/cosmeticsSvc.pyj` sends `shipCosmeticsMgr.ActivateSkinLicense(itemIDs)` and reads
`GetLicencedSkins`, `GetLicencedSkinsForShipType`, and `GetFirstPartySkinData`. License rows contain `skinID`, `expires`,
and `isSingleUse`. A permanent license uses Python `None` for expiration. Timed licenses use an absolute FILETIME.
`OnSkinLicenseActivated(skinID, licenseeID)` refreshes the character's native license views. The original client archive
has SHA-256 prefix `37fb2af4fa0e`. EVE.js **261108.1**, build **3396210**, implements activation in
`shipCosmeticsMgrService.js` (`9a76cdd2114f`) and saved grants in `shipCosmeticsState.js` (`4fb9ae689234`).
[CCP ship SKINs](https://support.eveonline.com/hc/en-us/articles/203280701-Ship-SKINs) ·
[EVE University New Eden Store](https://wiki.eveuniversity.org/New_Eden_Store)

Activation consumes one unit of each selected license stack and saves the character's entitlements in the same
resident transaction. Pilots can use licenses from their accessible personal hangar or ship holds. An invalid item or
a duplicate active SKIN rejects the batch. Timed licenses expire at their saved deadline and can be activated again with
a fresh item. The fitting window uses the [ship SKIN application](#first-party-ship-skin-application) routes.

**Automated validation.** The exact filter
`Eden.Protocol.Tests.PilotSessions.ShipSkinLicenseTests.NativeActivationCommitsInventoryAndLicensesTogetherAndRenewsAtExpiryAfterRestart`
passed in 9.84 s after a 6.90 s build. It verifies permanent, timed, and single-use license fields, native character
notification routing, mixed-batch rollback, isolated inventory custody, and renewal at the exact deadline after restart.
The same case passed in 9.71 s after wrapping a long test fixture line.

The solution built in 13.87 s with no warnings or errors. The following filter passed all 56 tests in 31.78 s. The
catalog and formatting policy suites passed all 17 Python tests.

```text
FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.ShipSkinLicenseTests|FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.ShipCosmeticsServiceTests|FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.ShipAppearanceServiceTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.ShipAppearanceTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.InventoryOwnershipTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.InventoryStackSplitTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.WorldInitializationTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.WorldMigrationTests|FullyQualifiedName~BiomassPreparationCancellationAndFinalDeletionUseNativeRepliesAcrossSessions
```

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Activate a first-party license from inventory, then open the character's SKIN collection
and reconnect to verify the unlocked entry.

## First-party ship SKIN application

Build **3662168** `cosmeticsSvc.pyj` sends `shipCosmeticsMgr.ApplySkinToShip(shipID, skinID)` and sends `None` to clear.
`shipSkinStateRequestMessenger.pyj` reads `eve_public.cosmetic.ship.api.Get` and `GetAllInBubble`. The state holds
the physical ship identifier, the character identifier, and either a first-party SKIN identifier or `no_skin`.
`shipSkinStateNoticeMessenger.pyj` listens for `SetNotice`. The pinned client resolves the material from its own SKIN
catalog. EVE.js **261108.1**, build **3396210**, writes the ship state in `shipCosmeticsState.js` (`4fb9ae689234`)
and publishes it through `publicGatewayLocal.js` (`2af418ccfcdf`).
[CCP ship SKINs](https://support.eveonline.com/hc/en-us/articles/203280701-Ship-SKINs)

Pilots can apply a matching active license to an owned, accessible assembled ship. The selected SKIN is saved on the
physical hull for that owner. Its appearance clears on repackaging or on expiry of the license used to apply it.
Repeating an application returns `SkinAlreadyApplied`, which the pinned fitting client uses to refresh its selection.

The gateway authorizes the current pilot through the authenticated account. In space, the initial bubble response and
subsequent character-targeted notices follow the same ship visibility as Destiny. A hull's new owner sees its default
appearance. Custom SKINR and structure SKINs remain open.

**Automated validation.** The exact filter
`Eden.Protocol.Tests.PilotSessions.ShipSkinLicenseTests.NativeApplicationAndGatewayReflectOwnershipVisibilityAndExpiryAcrossCheckpoint`
passed in 20.90 s after a 20.48 s build. It covers apply, clear, replay, authenticated read and notice shapes, off-grid
isolation, ownership transfer, restart, exact expiry, and renewal without reinstating the expired appearance.

The solution built in 1.05 s with no warnings or errors. The following filter passed all 100 tests in 111.70 s. The
catalog and formatting policy suites passed all 17 Python tests.

```text
FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.ShipSkinLicenseTests|FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.ShipCosmeticsServiceTests|FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.ShipAppearanceServiceTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.ShipAppearanceTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.ShipStorageTests|FullyQualifiedName~Eden.Protocol.Tests.Gateway|FullyQualifiedName~Eden.Protocol.Tests.Persistence.WorldMigrationTests|FullyQualifiedName~BiomassPreparationCancellationAndFinalDeletionUseNativeRepliesAcrossSessions
```

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** In fitting, apply a compatible first-party SKIN. Undock with a nearby observer, then
clear the SKIN and verify both views return to the default appearance.

## Persistent cargo and hangar items, November 2, 2026

World version 13 adds pilot-owned item stacks with stable IDs, type, location, inventory flag and positive quantity. Upgrading grants each existing pilot one Civilian Miner at the ship's saved station; character creation grants one at the school station in the same transaction. Reopening a migrated world does not repeat the grant. This is an Eden starter grant, not a claim about the stock server's starting fit.

Build 3662168's `invCache.Add` sends `(itemID, sourceID)` to the destination inventory handle with `flag` and optional `qty` keywords. `MultiAdd` sends a list of item IDs and a source. The client queries `itemLocking.GetItemsByLocation` before moving items; personal items have no corporation locks. Eden implements docked transfers between the active ship's cargo (flag 5) and the current station hangar (flag 4). It rejects foreign items, remote stations, fitting flags, space transfers, duplicate IDs and invalid quantities. Capacity comes from the hull and item volume from the pinned `client/types` record (Civilian Miner 3651: 5 m³). Multi-item moves validate capacity before writing; partial moves preserve the original remainder and create a new stack ID in one transaction. Stacking, fitting, mining and combat remain separate milestones.

The inventory list and `OnItemChange` reuse native `blue.DBRow` reductions with the existing descriptor. The notification takes `(newItemRow, oldValuesByColumnIndex, location)`, with `location=None` for these transfers. The client's `invCache.OnItemChange` requires all three arguments. Omitting the last argument saved the move but left the client's cached views unchanged during the first check. Column indices 3 and 4 carry the previous location and flag; quantity changes carry indices 5 and 9 (corrected after checking the pinned inventory constants). [Carbon Blue marshaller, revision af0d65b](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/Marshal.cpp). The build-specific call and notification behavior was checked against the pinned `eve/client/script/environment/invCache.pyj` and `eve/client/script/ui/services/corporation/itemLocking.pyj`.

**Live Windows client.** On an isolated local world, selected Eden Pilot and opened Inventory. The active ship read **Ibis (Ibis)**. Item Hangar showed one **Civilian Miner**. Dragging it to the Ibis moved the saved item into cargo. After restarting both host and client, Item Hangar was empty and Ibis cargo showed the same single miner with **5.0/125.0 m³** used. On the corrected notification build, dragging it back to Item Hangar immediately emptied the cargo view and reduced used space to zero; Item Hangar displayed the miner. Loading it again immediately emptied the hangar view. The host recorded native inventory Add calls and SQLite retained one miner per pilot.

**Automated checks.** Restore, build (zero warnings/errors), all 85 tests and formatting verification passed. Tests cover stable item identity across restart, one-time migration, isolation, invalid quantities and sources, fitting-flag rejection, space-transfer rejection, atomic capacity failure, full-capacity partial moves, conserved stack quantities, and item-change notifications with previous location and flag.

## Partial-stack capacity recovery, November 2, 2026

Build 3662168 defines `ixStackSize=9` and `ixSingleton=10` in `inventorycommon/const.pyj`. Inventory descriptors and quantity notifications now follow that order. The original single-item check did not expose the swapped trailing columns because it changed only location and flag.

The client's `BaseInvContainer._AddItem` catches `exceptions.UserError("NotEnoughCargoSpace", values)`, calculates a maximum from local capacity and item volume, and retries with a user-selected `qty`. Eden now returns this as an ErrorResponse whose parameters are `(originalCommand=6, code=2, payload=(serializedException,))`. `ExceptionWrapperGPCS.CallDown` loads the nested exception with `LoadsSanitized`. The exception uses a Blue reduction calling `exceptions.UserError` with the message name and dictionary; values include `available` and `volume`. Unsupported calls continue to use the existing destination error. Source checks used the pinned `cluster` constants, `machoNetPacket`, `ExceptionWrapperGPCS`, `eveexceptions` and `invControllers`, alongside the Carbon Blue revision linked above.

**Live Windows client.** On the isolated cargo test save only, prepared a stack of 30 Civilian Miners in Eden Pilot's station hangar. This fixture does not change the normal one-miner grant. Selected Eden Pilot and checked **Ibis (Ibis)** in Inventory. Dragging the 150 m³ stack onto the Ibis opened **Set Quantity**, with the notice **No room for more in destination container** and maximum 25. Accepted 25. The hangar immediately displayed five miners (25.0 m³); Ibis cargo displayed 25 miners and **125.0/125.0 m³**. SQLite held both distinct item IDs with quantities five and 25, preserving all 30 units.

**Automated checks.** Restore, warning-free build, all 87 tests and formatting verification passed. Additional checks assert the pinned descriptor indices, remainder and new-stack notifications including their third argument, and the nested capacity-error response through the session loop. Fitting was traced to `@ship.MultiAdd` with auto-fit flag zero but remains unsupported; it requires slot, skill, CPU/power and dogma-state rules before module activation and mining.

## Venture ownership, boarding and mining holds

The starter market now offers ten Venture hulls (32880) per visited market station for 10,000 ISK each. This is Eden's fixed progression policy, below the catalog's 300,000 ISK base price. Existing wallets receive no grant. Packaged hulls are owned inventory stacks; assembling one atomically removes a unit and creates an owned WorldShip with its name and station. A single packaged unit retains its item ID; splitting a stack allocates a new ID. Both WorldItem and WorldShip AUTOINCREMENT sequences participate in the shared allocator, including character creation, so assembling, consuming or deleting an item cannot make another entity reuse its ID. World schema remains 17.

Pinned `eve/client/script/ui/services/menuSvcExtras/invItemFunctions.pyj` sends `ship.AssembleShip(list(itemIDs), name=..., subSystems=None)`. The response is a list of assembled ship IDs. Eden supports Venture assembly, validates a distinct local owned packaged selection and the native 32-character name limit, and sends singleton/stack `OnItemChange` notifications. Retrying an assembled ID returns that ship without consuming another stack unit. Other purchased hulls, repackaging and assembled-ship trading remain unsupported.

`eve/client/script/ui/station/base.pyj` and `eve/client/script/dogma/clientDogmaLocation.pyj` activate an assembled ship through `ship.Board(shipID, oldShipID)`, returning `(instanceCache, instanceFlagQuantityCache, wbData, heatStates)`. Boarding validates ownership, both hulls at the same docked station, the previous active identity and all catalog skill requirements. The saved active ship changes without relocating either hull's cargo, modules or energy. The session sends its ship-ID change even though station/location stay the same, retains station service bindings, and rebuilds the selected flight context from the new hull. Ship inventory references bind to `@ship:<shipID>`; a cached old-ship inventory never aliases the new ship. Inactive owned local inventories remain readable; item mutations require the active hull. Ship Hangar's `itemShip.pyj` calls inventory `ListByFlags(flags=list(...))`; the response iterates ordinary inventory DBRows for those flags.

The Venture's catalog values include 50 m³ general cargo, attribute 1556 (`generalMiningHoldCapacity`) of 5,000 m³, and `flagGeneralMiningHold=134`. Mining automatically uses that hold; ordinary cargo capacity does not reduce it. Only asteroid-category ore may enter it. Docked moves, immediate sale requests and reprocessing accept the active hold, while reprocessed minerals go to ordinary cargo or Item Hangar. The pinned native menu omits Sell This Item for Mining Hold: unload to Item Hangar to sell through that UI. For direct hold reprocessing, select Item Hangar as output. Catalog effects 5058 and 5139 multiply ore-laser yield by hull attribute 207 (2), then apply attribute 1842 (5 percent) per trained Mining Frigate level (32918), in addition to Mining/Astrogeology. A Mining III / Mining Frigate I Miner I cycle therefore yields floor(10 × 1.15 × 2 × 1.05 / 0.1) = 241 Veldspar. Gas, boosts and unrelated hull modifiers remain separate work.

During this acceptance, the native docking navigator repeated `CmdWarpToStuff('item', stationID, minRange=0)` while the first order was aligning/in warp. Eden now acknowledges a repeat of the same target and range without restarting the course or emitting another warp effect; retargeting remains rejected during warp. This prevents an unsupported-bound-call dialog from cancelling native automatic docking.

Gameplay call shapes, flags, hull attributes and effect operators were read from build 3662168's packaged scripts and verified catalog. `code.ccp` SHA-256 remains `37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368`. Blue reductions and Destiny formats reuse the existing implementations checked against Carbon Blue `af0d65b29f951260ea09140ca11adf5d40566997` and Carbon Destiny `27ee40b38a563d29054b32f64c3ebc87fabaec1f`; this milestone changes neither format.


**Automated checks, November 2, 2026.** Restore, build with zero warnings/errors, all **154 tests** and formatting verification passed. Coverage includes buying and splitting packaged hull stacks, assembly retry identity and atomic rollback on an invalid batch, name limits, saved active hulls and names, ownership/station/skill/space boarding rejection, distinct bound inventories and filtered flag reads, local owned ship names without exposing another pilot's hangar, shared item/ship allocation, separate cargo and mining-hold capacity, ore-only input, hold unloading, mining with full ordinary cargo, Venture yield and saved extraction, hold sales, reprocessing from either hold into capacity-checked ordinary cargo, and idempotent warp retries without additional effects.

**Live Windows client, November 2, 2026.** Used the Computer Use skill with an isolated copy of the preceding mining-upgrade save. The acceptance fixture set Eden Pilot's wallet to **20,000 ISK** to focus on ship progression; this run does not establish earning that purchase price from scratch, and production seeding grants no ISK. Selected Eden Pilot with the saved Ibis, 350 Veldspar, 18 spare miners and its two fitted lasers. Returned to Perimeter II – Moon 1 – Caldari Navy Assembly Plant. Regional Market search `venture` showed **10 at 10,000.00 ISK**. Bought one, leaving **10,000 ISK**, assembled it through Ship Hangar and named it **prospector**. Double-clicking it changed the active hull and rendered the Venture.

The first run exposed missing `ListByFlags` calls from the ship cards and an automatic-docking warp retry error. Added both paths and restarted. Inventory then showed **prospector (Venture)** active, **0/50.0 m³** cargo and **0/5,000.0 m³ Mining Hold**. The inactive Ibis's cargo still displayed **350 Veldspar and 18 Civilian Miners**, filling **125/125 m³**. Boarded the Ibis again and opened Fitting with the circled ship icon: Miner I and Civilian Miner remained fitted, with **37/162 CPU available**. Unfitted Miner I into Item Hangar, returned to the Venture and fitted the same item to its first high slot.

Undocked the Venture, warped to Perimeter II – Asteroid Belt 1, locked Veldspar at **5,156 m** and activated F1. The laser beam appeared and Mining Hold reached **723 Veldspar / 72.3 m³** after three full cycles, agreeing with **241 per cycle**. A fourth full cycle and a manually stopped partial cycle left **1,171 ore / 117.1 m³**, while ordinary cargo remained separate. Used the distant station's Dock menu once; the corrected native navigator warped and docked automatically without an unsupported-call dialog. Reprocess directly from Mining Hold previewed and produced **2,200 Tritanium** in Item Hangar from 1,100 ore, leaving **71** in the hold. Dragged that remainder to Item Hangar, verified both stacks, and dragged the ore back into Mining Hold.

Restarted host and client on the final build. Inventory retained the active **prospector (Venture)**, **71 Veldspar / 7.1 of 5,000 m³**, and **2,200 Tritanium** in Item Hangar. Fitting retained Miner I and displayed **228/288 CPU available**, **52/54 power grid available**, **335 m/s**, and **16.50 km** targeting. SQLite confirmed active hull **2000000000012**, Miner I **2000000000010** online in flag **27**, ore **2000000000013** in flag **134**, wallet **1,000,000 cents**, and **9** Venture offers left. The corrected mining/docking/reprocessing run and restart had no assembly, boarding, inventory, dogma, mining or reprocessing exception. An unrelated periodic `pingService.Ping` remains unsupported; optional external gateway calls also remain unavailable. Capacity exhaustion and foreign/untrained/remote/space rejection are automated coverage; this live run used a single Venture and Miner I, and direct hold sales were not offered by the native menu.

## Native recovery corvettes, November 2, 2026

Build 3662168's `eve/client/script/ui/shared/dockedUI/corvetteButton.pyj` calls `station.CreateNewbieShip`. In `eve/client/script/ui/station/base.pyj`, that method requires a docked location and a current dogma ship item, refuses a corvette, and asks for confirmation when leaving a non-capsule hull. It calls `dogmaIM.CreateNewbieShip(previousShipID, locationID)` and ignores the return value. Eden implements this selected-pilot RPC in the fleet handler, returning Python None and sending the normal active-ship session delta. The pinned code.ccp SHA-256 remains `37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368`. No Blue or Destiny format changes are involved; the previously recorded Carbon revisions remain applicable.

Recovery is one SQLite world transaction. The server checks the owned current ship, docked station and required skills, then reads `shipTypeID` from the pilot's catalog race. Eden's policy reuses the first same-race corvette at that station with undamaged armor/structure and an online Civilian Miner and Civilian Gatling Railgun in high slots 27 and 28. Shield charge is not reset. Stripped or damaged corvettes and ships elsewhere remain intact. If no ready hull exists, the server checks the kit against base high slots, turret hardpoints, CPU and power grid, then allocates one hull and two online singleton modules. No ISK or skills are granted. The previous hull, its contents and health remain saved. The universal railgun and reuse rules are explicit Eden policy, pending other racial weapon families. A repeated request naming the same previous owned hull returns the current recovery corvette without creating more items. Concurrent requests serialize through the transaction. World schema remains version 20.

Live investigation identified two requirements beyond persistence. `hangarView.GetShipItemFromHangar` resolves the active hull from cached station inventory during `ProcessActiveShipChanged`, so inventory additions must precede the ship session delta. Also, `invCache.OnItemChange` rejects any `ixQuantity` change when the new row's quantity is negative. Assembled hulls and modules use quantity -1, singleton 1 and stacksize 1. New singleton notifications now announce prior location/flag and stacksize zero without an `ixQuantity` marker. Ordinary stack notifications retain their quantity behavior. This avoids rejecting the new hull before the hangar can replace its model.

**Automated checks.** Restore, build with zero warnings/errors, all **190 tests**, and formatting verification passed. Added coverage checks zero-ISK recovery, an equipped online kit, preservation of the previous hull's name/cargo/damage and wallet, restart, reuse, competing and repeated requests, ownership/station/space/active-hull rejection, unsupported fitting rollback, selected-pilot routing and Python None. Session-level coverage checks that the three inventory additions precede the ship delta; singleton notifications are checked for stacksize without the rejected quantity marker.

**Live Windows client.** Used the Computer Use skill on isolated lab `recovery-final-20261102`, copied from the verified combat/mining save. Prepared an explicit station-start fixture by removing only the active Venture's space-position row; this did not demonstrate travel or docking. The original save remained untouched. Selected **Eden Pilot** and checked active **prospector (Venture)** in Inventory at Perimeter II – Moon 1. Pressed **Board my Corvette** and accepted the native confirmation. Inventory changed to **Ibis (Ibis)**, the station played the Ibis boarding scene, and the button became disabled. Fitting showed both civilian modules online, 3.7 dps and stable capacitor. The earlier stale Venture model and negative-quantity errors were absent on the final build.

Opened Ship Hangar and boarded **prospector** again. The Venture's model returned, and Mining Hold retained **8,599 Scordite** and **71 Veldspar**, using **1,296.9 / 5,000 m³**. Pressed Board my Corvette again: the same Ibis **2000000000018** became active, with no additional hull or modules. My Wallet still showed **19,000 ISK**. Restarted both host and client on the same save; Inventory and the hangar retained the Ibis, Fitting retained both online modules, and the wallet tooltip retained 19,000 ISK. SQLite comparisons confirmed every pre-existing ship/item/wallet row unchanged, exactly one new hull and two modules, and schema 20. No recovery, active-ship, model-loading or negative-quantity exception appeared in the final run or restart log. Optional external gateway features remain unsupported. This live run covered the Caldari Ibis; other race hulls use their catalog definitions but were not tried in the client. NPC retaliation, player destruction and capsule escape remain separate milestones.

## Inventory refresh during capsule adoption, November 2, 2026

Investigated the old-hull cargo refresh observed above against the same pinned client/build/hash. `invCache.ProcessSessionChange` discards the previous ship's cached inventory when `shipid` changes in space. `holdTracker.OnCapacityChange(moduleID)` does not filter by the supplied module ID: it calls its still-retiring cargo controller, which can rebind the lost hull after the session already names the capsule. The captured nested call was `invbroker.MachoBindObject((systemID, groupSolarSystem), ('GetInventoryFromId', (lostHullID, 0), {}))`. Hard-deleting the hull correctly prevented asset access, but that transient read failed with `UnMachoDestination` and the tracker logged `BlinkAndUpdateIfChanged Failed`.

`PilotCallContext` now retains the just-destroyed ship's already authenticated metadata during capsule adoption. That session's existing ship-inventory binding can return an empty list and a singleton self row at **Graveyard location 8**, preserving the old identity for this retiring controller. All mutations are rejected. No hull, inventory stack, capacity grant or persistent record is created. Arbitrary IDs and another pilot's ships remain inaccessible. Selecting/boarding another ship or leaving the location clears this read compatibility state; reconnect does not recreate it. The existing capsule, wreck, health, lock and ship-session event ordering is unchanged. No new schema or wire format is added; Carbon Blue/Destiny source revisions remain unchanged.

**Automated checks.** Restore, build with zero warnings/errors, all **232 tests**, and formatting verification passed. Regression coverage uses the observed nested bind, an already-bound old cargo reference, empty List/ListByFlags, the assembled graveyard self row, rejected Add/MultiAdd, foreign-ship rejection, expiry on departure and ship change, and unchanged durable loss/wreck/active capsule with no resurrected hull or contents.

**Live Windows client.** Used the Computer Use skill on a new isolated `ejection-refresh-20261102` save copied from the same pre-loss Ibis save, with a separate physical catalog's **100× NPC damage** override. Selected **Eden Pilot** and left Inventory open showing Ibis **2000000000018** with **100 Tritanium / 1 of 125 m³**. Verified gun auto-repeat was off, locked the Rookie near **53 m**, and fired once. After retaliation, Inventory changed to **Capsule (Capsule), 0/0 m³, Nothing Found**, both lock indicators cleared and the player wreck appeared. The natural drop retained the miner and Tritanium again; one loss/capsule and the original manifest/drop audit were saved.

The host trace recorded successful `@ship:2000000000018.GetSelfInvItem` and `.List` reads after the capsule's normal inventory calls. The client log contained **zero** `BlinkAndUpdateIfChanged Failed`, unsupported `invbroker.MachoBindObject`, negative-quantity or relevant inventory/wreck/ship-effect exceptions. This exercised the previously failing retiring controller rather than avoiding it by closing Inventory. The durable hull remained deleted and its two survivors remained in the wreck. Restored the test catalog to normal **0.625** NPC damage afterward. Normal catalog data and the previously verified recovered-ship save were preserved.

Finally launched the updated host/client against `player-wreck-20261102` with normal catalog damage. Inventory again showed recovered Ibis **2000000000023** with **100 Tritanium and the recovered Civilian Miner / 6 of 125 m³**. This leaves the playable recovery save running, while the separate destructive regression save retains its evidence.

## Production stack consolidation and repackaging (November 6, 2026)

Production needs to combine ore delivered by repeated cargo trips before reprocessing. `IPilotInventoryStacks.StackAll` now consolidates compatible non-singleton personal stacks in the current docked station's Item Hangar or an active ship hold. It retains the lowest item ID, preserves quantity/volume/owner and rejects overflow atomically. Different type, volume override and crystal wear remain distinct; singleton items, occupied containers and installed blueprints are excluded. Native bound `StackAll` accepts an optional inventory flag through the positional argument or `flag` keyword, with ordinary item-change notifications for the retained and removed rows. Duplicate or malformed flags reject without changing state. Selective `MultiMerge` remains separate work.

`IPilotRepackaging` converts inactive, empty, unfitted, undamaged supported assembled hulls into ordinary packaged stacks, retaining item identity while removing instance state. Supported offline modules in Item Hangar may also be repackaged. Used blueprints, active ships, armor/hull damage, worn crystals, fitted/loaded contents, stored ships, foreign assets and remote stations reject. The complete batch is transactional: a later rejection leaves earlier items unchanged. Repeating an already packaged item is inert. The existing insurance removal operation voids active coverage with no refund; [CCP's insurance rules](https://support.eveonline.com/hc/en-us/articles/212726885-Insurance) identify repackaging as a reason coverage ends. Native notifications expose the changed singleton/volume form, so the packaged item can be listed on the personal market.

Inert build **3662168** `code.ccp` inspection (SHA-256 **37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**) confirms `invCacheContainer.StackAll` forwards its bound arguments/keywords, and `invItemFunctions.RepackageItems` calls `repackagingSvc.RepackageItems({stationID: [(itemID, locationID), ...]})`. The ordinary client confirmation is already owned by the client. Optional destructive retry forms (`destroyable`/rig or container handling) remain unsupported; the server requires contents and fittings to be removed first. The adapter uses the existing distinct Python integer-keyed dictionaries, lists and tuples, named replies and item-change models. Reference: [Carbon Blue Marshal.cpp af0d65b29f951260ea09140ca11adf5d40566997](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/Marshal.cpp). No codec change is introduced.

**Automated validation:** Included with the [starter production milestone](industry.md#tech-i-starter-production-chain-november-6-2026), including persistence, native wire forms, insurance, ownership, overflow and whole-batch rejection checks.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Inspect Stack All quantity/capacity refresh and repackaging's packaged tile/insurance display on a copied world. Native rendering and input remain unverified.

### Fitted ship repackaging

Repackaging an inactive, undamaged ship in the current personal hangar now returns its high, medium, and low slot modules and their loaded charges to that same hangar. Returned modules retain their identity, condition, and assembled form and go offline. The hull keeps its item ID in packaged form and loses its insurance without refund. The resident world commits these changes with the ordinary item notifications after the whole selection passes validation. The fitted hull must have an empty cargo hold, empty bays and no rigs or subsystems. [EVE University assembling and repackaging](https://wiki.eveuniversity.org/Assembling_and_Repackaging)

In build **3662168**, `invItemFunctions.RepackageItems` confirms the ordinary request on the client and sends one station-keyed dictionary of item/location tuples. The client only sends `destroyable=True` on the separate upgrade-destruction retry. The local EVE.js **261108.1** `repackagingSupport.js` moves recoverable ship contents to Hangar during the packaging transaction. Eden uses its existing fitted-module inventory move, which also unloads saved charge stacks, and the existing resident hull-removal cleanup. The ordinary wire reply remains Python None.

**Automated validation.** The exact test `FullyQualifiedName=Eden.Protocol.Tests.Industry.ProductionInventoryTests.NativeRepackagingReturnsFittedModulesAndChargesAcrossCheckpoint` passed in **1.14 seconds**. It checks native item notifications, module and charge recovery, the packaged hull, rollback when a later rigged hull fails, checkpoint reopening, and retry.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer check.** Repackage a spare hull carrying a fitted and loaded module, then inspect the packaged hull, offline module and restored charges in Item Hangar.

## Personal Assets window (November 8, 2026)

The native window uses the same pilot-lifetime global inventory binding as the contract picker: `charMgr` with `(selectedPilotID, containerGlobal=10002)`. Previously `ListStations()` exposed only `stationID` and `itemCount`. Inert inspection shows `AssetsSvc.GetAll` immediately reads `solarSystemID`, while `AssetsWindow.GetStationSolarSystemIDAndTypeID` also consumes `typeID`. The reply now retains the original columns and adds both catalog-backed fields. Contract picker counts use the same directory query and remain row counts, including the active docked ship.

The bound inventory now implements `ListStationItems(stationID)`, `List()` and `ListIncludingContainers()` with the existing native nine-physical-column inventory DBRow descriptor and virtual singleton/stack-size getters. Root station reads include personal hangar stacks and docked assembled ships. Search adds reachable singleton cargo, secure cargo, audit-log secure and freight-container contents with native None/Locked/Unlocked flags (0/63/64). It preserves each item's original container/location identity, flags and stack quantity. The server never treats ship cargo, fitted modules or ships carried in bays as station assets. Foreign ownership, invalid/orphan locations, installed blueprints, escrow and ships in space are excluded.

`IPilotAssets` owns the typed read boundary; World loads a coherent relational snapshot and Gameplay selects the reachable asset graph. The protocol handler validates arguments and returns named models. Remote reads require character selection and work while docked or in space; the global binding survives location-reference release. Reads emit no inventory notifications or mutations. Repeated server reads reflect committed ownership/escrow changes; `inv5MinCacheContainer` retains the native client's existing five-minute caches. Location filtering, sorting, name/type checks and advanced search conditions remain client behavior.

`structureAssetSafety.GetItemsInSafetyForCharacter()` returns a real empty Python list for the Safety tab. Eden has no Upwell storage or safety-wrap persistence, so no transfer, delivery or recovery calls are advertised. Corporation safety and `GetAssetWorth` remain unsupported; no artificial asset valuation is introduced. There is no schema or client-file change.

References: [EVE University: Managing Your Assets](https://wiki.eveuniversity.org/Managing_Your_Assets), [Neocom Personal Assets](https://wiki.eveuniversity.org/Neocom#Personal_Assets) and [Asset Safety](https://wiki.eveuniversity.org/Asset_Safety). Exact call signatures and field casing were checked against build **3662168** `E:/EdenDev/3662168/tq/code.ccp`, SHA-256 **37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368**, including `invCache.pyj`, `assetsinfo.pyj`, `assetsWindow.pyj`, `eveAssets/assetSearching.pyj`, `inventorycommon/const.pyj` and `assetSafetySvc.pyj`. Inspection decoded inert code objects without importing or executing gameplay Python. Consulted [Carbon Blue PyRowSet.cpp at fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/PyRowSet.cpp) for descriptor/DBRow construction; existing encoding is reused.

**Automated validation:** `dotnet tool restore`, solution restore and build passed (build **7.76 seconds**, zero warnings/errors). The final focused run passed **79 tests in 27 seconds** using `FullyQualifiedName~PersonalAsset|FullyQualifiedName~ContractAsset|FullyQualifiedName~ContractServiceTests|FullyQualifiedName~Hosting.ServerServicesTests|FullyQualifiedName~Hosting.GameplayDependenciesTests|FullyQualifiedName~Hosting.DataModelOwnershipTests`. The initial exact-method filter was `FullyQualifiedName=Eden.Protocol.Tests.PilotSessions.PersonalAssetServiceTests.NativePersonalAssetsListLocationsAndExpandRemoteStations`; the escrow regression was also run with its exact method filter. Coverage includes all four container groups, root/stack counts, station metadata, native inventory row encoding, embedded binding calls, location-independent references, space reads, committed ownership/escrow changes, foreign/orphan/installed/stored exclusions, rejected arguments, empty safety and reopened persistence. Initial development failures in the new test fixture (request-decoding an outbound DBRow and reinserting an existing catalog category) were corrected before the successful final run. Both Python policy suites passed (**8 catalog tests in 0.606 seconds**, **9 formatting tests in 0.020 seconds**); `Format.ps1 -Check` and documentation/diff checks passed. The full .NET suite was not requested or run. Native client rendering and filtering execution remain unverified.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** With the updated server, reopen Personal Assets, expand a remote station and search for an owned item; confirm location filters/sorting and the empty Asset Safety tab. Wait for the native cache expiry when inspecting recent asset changes.

## Remote Personal Assets expansion (November 14, 2026)

Build 3662168's `AssetsWindow.GetSubContent` gets remote inventory from `ListStationItems` and primes named items before
creating the scroll entries. `uix.GetItemName` then reads `cfg.evelocations.Get(itemID).name` for assembled ships and
nameable containers. The earlier `config.GetMultiLocationsEx` handler only resolved ships within the normal inventory
access scope. For a remote ship whose row was already counted and returned, the missing cache entry caused
`Recordset.Get` to raise `KeyError("RecordNotFound", itemID)` while building the expanded station list.

The location cache lookup now resolves missing IDs for the selected pilot's resident, top-level docked ships and assembled
cargo containers in valid personal hangars. It returns the ship's saved name. An unnamed container receives a cache row
with Python `None` for the name so the client displays its localized type name. Supplemental rows have a null system and
zero coordinates. The ordinary inventory access rules continue to govern requests for the ship and its contents.

Inspected the original build-3662168 archive recorded above, including `assetsWindow.pyj`, `uix.pyj`, `eveCfg.pyj` and
Carbon's `cfg.pyj`. The local EveJS 261108.1 reference's `configService.js` records the native ship location row layout.
The existing inventory DBRow representation was checked against [Carbon Blue at fcbf0c2](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/PyRowSet.cpp).
Personal Assets station browsing is described in [EVE University's inventory guide](https://wiki.eveuniversity.org/Managing_Your_Assets).

**Automated validation.** The two cases under
`FullyQualifiedName=Eden.Protocol.Tests.PilotSessions.PersonalAssetServiceTests.RemoteAssetExpansionPrimesOwnedItemNames`
reproduced the absent cache rows before the fix and passed afterward. The tests check the one-item station count,
the returned assembled inventory row and its name cache entry. They also check that foreign, carried and unknown IDs
are absent from the cache reply. Tool restore and solution restore passed. The solution build passed in 1.77 seconds
with zero warnings or errors. Both Python policy suites passed, and formatting verification passed in 29.97 seconds.
The final focused run passed 199 tests in 101.46 seconds with this filter. The full .NET suite was outside this run.

```text
FullyQualifiedName~PersonalAsset|FullyQualifiedName~ContractAsset|FullyQualifiedName~PilotFleetTests|FullyQualifiedName~ShipStorageTests|FullyQualifiedName~Information.InformationTests|FullyQualifiedName~Structures.StructureTests|FullyQualifiedName~Hosting.ServerServicesTests|FullyQualifiedName~Hosting.GameplayDependenciesTests|FullyQualifiedName~Hosting.DataModelOwnershipTests
```

**Live acceptance.** Not performed. Agent client testing is disabled.

**Optional maintainer checks.** After restarting the client against the updated server, expand the affected remote stations.

## Targeted stack merging, November 9, 2026

A user-provided screenshot showed two ammunition stacks (560 and 80 units) in the active ship's inventory while the fitting window was open. Drag-merging them failed with `Unsupported local call @ship:200000003700.MultiMerge`. Eden supported `StackAll`, but had no handler for selected-stack merges. The ship binding is a saved inventory container, even when a fitting simulation is visible; simulation does not grant access to another ship's inventory.

Added selected-pilot `MultiMerge([(sourceItemID, targetItemID, quantity), ...], sourceContainerID)` for the current personal station hangar and active ship holds, including item-specific `@ship:<id>` bindings. The exact target ID survives; unselected compatible stacks remain separate. Full merges remove only exhausted sources; partial merges retain the source remainder. The entire batch commits atomically, then emits normal `OnItemChange` updates. Exhausted sources use the existing graveyard-location representation with their former quantity so the native inventory cache can remove the tile. Temporary split/move rows never appear in notifications.

Typed Gameplay rules validate compatible packaged stacks, positive available quantities, overflow and distinct non-chained selections. World operations validate both owners, declared source/destination locations, supported flags, volume overrides, crystal wear, occupied containers and installed blueprints. A same-hold merge adds no capacity and is valid even when that hold is already over capacity; cross-container/hold merges reuse ordinary movement, capacity, hold-eligibility and docking checks inside the same transaction. Unsupported/remote/inactive ship inventories, malformed tuples/lists, duplicate sources, self/chained merges, foreign items and any later batch failure leave inventory unchanged. Corporate stack merging and loaded-ammunition tuple sublocations are outside this handler; the native client routes loaded ammunition through its dogma unload path before issuing ordinary inventory merges.

**Pinned-client evidence.** Inert inspection of build **3662168** `code.ccp`, SHA-256 **37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**, confirms `BaseInvContainer.MultiMerge` converts drag data into a Python list of three-field tuples. `invCacheContainer.MultiMerge` locks source items, separately handles tuple-key loaded ammunition via `UnloadAmmoToContainer`, and sends the remaining operations and source container ID to the bound inventory. The call returns Python None. [Carbon Blue Marshal.cpp at af0d65b29f951260ea09140ca11adf5d40566997](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/Marshal.cpp) remains the list/tuple/None representation reference; no codec changes are needed. [EVE University: Assembling and repackaging](https://wiki.eveuniversity.org/Assembling_and_Repackaging) documents packaged stackability, and [Neocom inventory](https://wiki.eveuniversity.org/Neocom#Inventory) describes station and active-ship hold access. These gameplay references do not define the build-specific RPC.

**Automated validation.** Development started with the exact method filter `FullyQualifiedName=Eden.Protocol.Tests.Persistence.InventoryStackMergeTests.NativeMultiMergeRetainsChosenTargetAndPublishesPersistentQuantities`. Four cases passed and two station cases exposed a test assertion that included unrelated starter items in its quantity total; the assertion was scoped to the selected and deliberately unselected test stacks. The combined filter `FullyQualifiedName~Eden.Protocol.Tests.Persistence.InventoryStackMergeTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.InventoryStackSplitTests|FullyQualifiedName~Eden.Protocol.Tests.Industry.ProductionInventoryTests` then passed **52 cases** (6.86 seconds build, 17.43 seconds test command). Final affected validation passed **366 cases** in 19.13 seconds. Tool restore (0.18 seconds), solution restore (0.66 seconds), solution build (4.84 seconds, zero warnings/errors), catalog policy (8 cases, 1.32 seconds), formatting policy (9 cases, 0.28 seconds), and `Format.ps1 -Check` (18.35 seconds) passed. The final sequence took 44.76 seconds; the full .NET suite was not run. The final affected filter was:

```text
FullyQualifiedName~Eden.Protocol.Tests.Persistence.InventoryStackMergeTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.InventoryStackSplitTests|FullyQualifiedName~Eden.Protocol.Tests.Industry.ProductionInventoryTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.PilotInventoryTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.InventoryPersistenceTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.InventoryOwnershipTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.ShipInventoryRulesTests|FullyQualifiedName~Eden.Protocol.Tests.Docking.TypedInventoryTests
```

**Live acceptance.** Not performed; agent client testing is disabled. Native drag/drop interaction and inventory repaint remain unverified.

**Optional maintainer checks.** After updating the server and reconnecting, drag the 80-round stack onto the 560-round stack and confirm one 640-round stack; repeat with a chosen partial quantity if desired.

## Direct player trading

The station Guests menu calls `trademgr.InitiateTrade(characterID)`. Eden returns a native trade moniker and sends `OnTradeInitiate` to the other connected pilot at the same docked location. The bound object lists both offers, accepts single or multiple item drops and partial stacks, updates ISK offers, and handles acceptance and cancellation. Both clients receive trade-state updates and the committed inventory changes.

Offers use temporary display rows. The assets stay in the owner's personal inventory until both pilots accept the same manifest. Adding items or changing an ISK amount clears both acceptances. The resident world verifies both docked locations, available funds, ownership and the quoted asset contents before moving any items or ISK. Settlement transfers assets into the recipients' personal hangars and writes both wallet journals in one resident action. Completed trades persist through the existing checkpoint. Open trades close on cancellation, disconnect or departure and expire on server restart.

Trade accepts personal hangar items, stacks in the docked active ship's holds, and inactive assembled ships with their contents. Direct drops from fitting slots and corporation storage are outside the supported sources. Installed blueprints, courier packages, asset safety wraps, and catalog-restricted items are rejected.

Inert inspection used build **3662168** `code.ccp`, SHA-256 `37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368`. The client paths are `pvptradesvc`, `pvptradewnd`, `invControllers.PlayerTrade`, `inventoryrestrictions.common.checks` and `moniker.Moniker`. The wallet journal uses `appConst.refPlayerTrading` (**1**). EveJS package **261108.1** supplied a comparison for the older client's trade calls and notifications. Its local service source has SHA-256 `37F12AEA4369CE56EBE705C0DF2DF394A4EA50C2FE777E2DB826F1887E16FD56`. The native accept call carries a KeyVal with a set of packed inventory rows. The decoder retains those rows as inert values for manifest comparison. [Carbon Blue Marshal.cpp, revision af0d65b](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/Marshal.cpp) · [Carbon Blue PyRowSet.cpp, revision af0d65b](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/PyRowSet.cpp) · [EVE University direct-trade reference](https://wiki.eveuniversity.org/Alternate_characters#Transferring_goods_between_characters)

**Automated validation.** The focused .NET run passed 238 cases in 181.22 seconds. Trade coverage includes native manifest encoding, partial and assembled items, dual acceptance, unchanged assets and balances after rejection, disconnect cleanup, duplicate-acceptance rejection and reopened checkpoints. The warmed exchange issued zero SQL commands and opened zero connections. Tool and solution restore passed. The solution build passed in 15.17 seconds with zero warnings and errors. Both Python policy suites passed, with 8 catalog cases and 9 formatting cases. Formatting verification passed in 41.36 seconds.

The focused filter was:

```text
FullyQualifiedName~PlayerTradeTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~BlueMarshalTests|FullyQualifiedName~PilotBindingsTests|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~TypedInventoryTests|FullyQualifiedName~InventoryOwnershipTests|FullyQualifiedName~InventoryStackSplitTests|FullyQualifiedName~ShipStorageTests|FullyQualifiedName~ContractAssetTests|FullyQualifiedName~ContractServiceTests|FullyQualifiedName~Eden.Protocol.Tests.Market.MarketTests|FullyQualifiedName~Hosting.ServerServicesTests|FullyQualifiedName~Hosting.GameplayDependenciesTests|FullyQualifiedName~Hosting.DataModelOwnershipTests
```

After rebasing onto `78a2a69a`, restore, build and the `FullyQualifiedName~PlayerTradeTests|FullyQualifiedName~MiningHoldConfigurationTests` filter passed in 34.03 seconds with 7 cases. Formatting verification passed on the rebased worktree.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** From Guests, start a trade between two docked clients, exchange a partial stack for ISK, and verify both hangars and wallets. Repeat with a changed offer, then close one window and verify cancellation on the other client.
