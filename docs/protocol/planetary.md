# Planetary industry

[Documentation index](../README.md) / [Protocol index](../protocol.md)

## Persistent colonies and production — November 11, 2026

Planetary industry now supplies a personal, persistent production loop across the eight ordinary planet types.
It uses all **68 pinned planetary schematics**, producing P1 through P4 from the **15 raw resources**. Actual catalog
inputs, quantities, cycles, building restrictions, volumes and dogma defaults determine production. The fixture
export contains 144 types and 359 source records and is reproducible with
`python tools/Export-PlanetaryCatalog.py --catalog <catalog.sqlite>`.

### Player behavior

- An Omega pilot carries the matching published command center in ordinary cargo or the specialized command-center
  hold and deploys it while undocked in that planet's system. A basic colony needs no scanning skill. The saved
  colony count is limited to one plus Interplanetary Consolidation. The pinned ordinary planet types and UniWiki's
  restricted system list govern colonization; shattered/scorched planets have no admitted building/resource profile.
- Native Submit supports all 13 ordinary command-stream operations: build/remove pins, create/remove/upgrade links,
  create/remove routes, assign schematics, upgrade the command center, edit extractor heads and install/stop programs.
  CPU/power include buildings, heads, spherical link distance and link upgrades. Building and command upgrades charge
  the wallet. Demolition destroys the building's contents; abandoning removes the complete surface colony.
- Extractors support up to ten heads within the catalog area of influence, overlap penalties, native one-hour to
  fourteen-day programs, cycle intervals and the client's decay/noise output formula. Quotes also accept temporary
  extractor identities before Submit; actual construction and program yield are validated against the final layout.
- Connected, non-repeating routes reserve link bandwidth and feed only valid products/ingredients. Processors consume
  complete catalog batches and retain at most one additional input batch. Producers discard unrouted/overflow output.
  Storage and launchpad capacity use actual commodity volumes. Expedited transfers validate the entire path and
  impose a source cooldown of at least five minutes, increased by volume and bottleneck bandwidth.
- Colonies advance by saved deadlines on access. Offline progress uses the same chronological events as stepped
  simulation, stops at extraction expiry and preserves pending processor cycles. Repeated reads at one timestamp
  cannot start additional production or pull another batch. Ordinary flight frames do not run colony simulation.
- Command-center launches remove real goods, charge the 15% export tariff and create owned orbital containers with
  five-day expiry. The journal lists their coordinates and supports the native `launch` warp selector. Containers
  use ordinary nearby cargo collection, including specialized hold restrictions. Launch deletion removes its cargo.
- The first colony creates a shared Interbus customs office around that planet. Each pilot has a private 35,000 m³
  compartment. Ship transfers require an uncloaked ship within 2,500 m in the same system. Launchpad import/export
  consumes actual compartment/surface inventory and charges the 17% Interbus tariff; imports cost half exports.
  Customs Code Expertise does not reduce Interbus tariffs. Client-supplied tax quotes cannot change charges.
- Survey scans require Remote Sensing. Colonized planets and the current system get local proximity; other scans
  enforce the 1/3/5/7/9-light-year ranges and known-space restrictions. Planetology and Advanced Planetology bound
  the returned harmonic bands using the pinned proximity table. Client skill/proximity claims are not authoritative.
- Skill extraction protects Interplanetary Consolidation levels needed by current colonies and Command Center
  Upgrades levels needed by existing command centers. Surface edits, wallet charges, cargo consumption, identity
  allocation and colony saves share one transaction. Recent duplicate Submit requests reuse the committed result.

### Protocol and persistence

Gameplay owns typed colony state, commands, geometry, routing, capacities, taxes and event advancement. World loads
the immutable catalog definition and persists named colony state, global pin allocation, launch records and offices.
Data owns saved entities. Protocol parses inert Blue values and emits explicit models; no client code is executed.

`planetMgr` binds by planet ID, with pilot-lifetime references retained across docking. The router validates binding
shape, gates selection and preserves nested first-call failures. Native replies include KeyVals, CRowset colony and
launch directories, integer commodity dictionary keys, list/tuple distinctions, float coordinates/radius, raw
float32 harmonic coefficients and FILETIME cycle/deadline values. Public colony overlays expose positions and links
without inventory, program or production information. GM mutation routes remain unsupported.

Orbital contacts reuse the existing stationary contact and inventory boundaries, retaining the actual orbital
category, planet identity and idle state. Customs offices remain visible throughout their system for the native
launchpad's `planetInfo.GetFunctionalCustomOfficeOrbitalsForPlanet` lookup. Warmed observer reads use the existing
resident snapshot; office/launch commits, inventory membership and exact launch expiry invalidate that snapshot.
Checkpoint capture includes all planetary tables. Schema 139 preserves the preceding coarse-population upgrade;
planetary schema creation is idempotent for existing and newly created saves.

### Reference evidence and intentional approximations

Gameplay references consulted on November 11, 2026:
[Planetary Industry](https://wiki.eveuniversity.org/Planetary_Industry),
[Planetary buildings](https://wiki.eveuniversity.org/Planetary_buildings),
[Colony management](https://wiki.eveuniversity.org/Colony_management),
[Planet Management skills](https://wiki.eveuniversity.org/Skills:Planet_Management), and
[Customs Code Expertise](https://wiki.eveuniversity.org/Skills:Trade#Customs_Code_Expertise).
The pinned catalog and client resolve build-specific wire behavior and values.

The local EveJs reference at `F:\Git Repos\evejs` identifies client build **3396210**, whereas Eden uses **3662168**.
The supplied source snapshot has no Git metadata, so its reference identity is recorded by SHA-256:

| Reference file under `server/src/services/` | SHA-256 |
| --- | --- |
| `planet/planetMgrService.js` | `726ee5e88e5b7009ace10b535bef39057ec761846984bd3c37df456c47912151` |
| `planet/planetRuntimeStore.js` | `cb74256d55a9add0fa5fedd022362118a1fe0d67b27caaff302f30a7a8ce45da` |
| `planet/planetStaticData.js` | `3cca293b7a98bfe4c4a9559ec3b109d0a661652ab441d8b005cef3751b3ebbc9` |
| `inventory/invBroker/planetaryImportExport.js` | `b267d900cdb14b90fb4e8d677820079c013dd67e782559c7b49597765c44ff97` |

The comparison informed service coverage, explicit reply fields, temporary identities, construction settlement,
orbital cargo and lazy production. Eden's implementation is independently authored against typed gameplay rules
and the pinned client/catalog, without copying EveJs implementation code.

Inert inspection of build 3662168's `code.ccp` covered `clientPlanet`, `planetSvc`, `colonyData`, `commandStream`,
`ecuPin`, `processPin`, `planetCommon`, `planetInfoSvc`, launchpad/import-export UI and the planetary journal.
The stock archive SHA-256 is `37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`.
The staged client includes Eden's skill overlay and has archive SHA-256
`051a2f027309aa9e806273200c9f5e969cad8675f49ef97313ff57a6b4549bb9`; all **27** selected planetary/client modules
were compared against the stock backup and are byte-identical. The stock archive identity therefore covers the
inspected native behavior despite the staged archive's additional overlay.
Evidence includes the temporary-pin survey call, FILETIME program parameters, `launch` warp selector, actual
orbital idle state, 0.3-radian default head range, commodity tiers and native band-resolution tables.
The customs contact also supplies the `level` attribute required by `CustomsOffice.LoadModel`; None selects the
type's ordinary catalog graphic rather than a special upgrade graphic.
Existing Blue and Destiny codecs are reused; no serialization or movement algorithm is replaced.

Resource geography and extraction base yield are explicit **Eden simulation policy**: a reproducible real spherical
harmonic field shared by scan bytes and authoritative yield, footprint scaling and symmetric head-overlap penalties.
They are not Tranquility's proprietary distributions. Dynamic geological depletion/regeneration, security-dependent
abundance, player-owned customs gantries, orbital combat/ownership/tax configuration, skyhooks and PI templates are
outside this implementation. Offices are supplied on first colonization and remain NPC infrastructure. Native
presentation, rendering and input remain unverified.

### Automated validation

Development began with the exact filter
`FullyQualifiedName=Eden.Protocol.Tests.Planetary.PlanetaryIndustryTests.ExtractsProcessesAndPersistsWithoutReplayingCycles`.
Coverage includes all 68 recipes, all eight planet profiles, expiry boundaries, stepped versus offline production,
same-timestamp idempotence, atomic rejection, Submit retry, real customs cargo/taxes, command launches, collection,
restart/checkpoint persistence, skill extraction floors, inert wire shapes, temporary ECU quotes and warmed empty
and populated observer reads with zero SQL commands, connections and fitting calculations.

Validation incorporates main's `36f4e2e611316c414efd043ba3e3c45389cbf773` coarse-population changes, the
subsequent `aeb1fc357bd5070663a1b1b82889ccce7d8b3045` mining-reward changes, and the idempotent migration
from schema 138 to 139:

| Automated check | Result | Elapsed |
| --- | --- | ---: |
| Pinned tool restore, solution restore and merged solution build | Passed; zero warnings/errors | 10.51 s |
| Affected .NET tests and immediate dependencies | 190 passed; none failed/skipped | 83.95 s |
| Final incremental solution build after the customs model-level correction | Passed; zero warnings/errors | 1.16 s |
| Final planetary/orbital tests after that correction | 50 passed, including all 37 planetary cases and 13 wreck cases | 42.29 s |
| Latest main integration restore/build | Passed; zero warnings/errors | 23.82 s |
| Latest main integration: planetary, mining rewards and world tuning | 114 passed; none failed/skipped | 54.77 s |
| Catalog policy and formatting policy Python suites | 8 + 9 passed | 5.89 s |
| `tools/Format.ps1 -Check` | Passed, including 140-column verification | 21.59 s |
| Stock/staged module comparison and `git diff --check` | Passed | — |

The 190-test filter combined these exact prefixes with `|`:

```text
FullyQualifiedName~Eden.Protocol.Tests.Planetary
FullyQualifiedName~Eden.Protocol.Tests.Loot.WreckTests
FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.PilotBindingsTests
FullyQualifiedName~Eden.Protocol.Tests.Persistence.SkillExtraction
FullyQualifiedName~Eden.Protocol.Tests.Persistence.WorldMigrationTests
FullyQualifiedName~Eden.Protocol.Tests.Hosting
FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerCoarseTests
```

The final 50-test filter combined the first two prefixes. An exact method run first established the new
`PlanetaryProtocolTests.CustomsContactSuppliesTheRequiredNativeModelLevelAndOrbitalCategory` regression.
After the mining-reward merge, the 114-test filter combined `FullyQualifiedName~Eden.Protocol.Tests.Planetary`,
`FullyQualifiedName~Eden.Protocol.Tests.Mining.MiningSkillPointRewardTests`, and
`FullyQualifiedName~Eden.Protocol.Tests.Persistence.WorldTuningTests` with `|`.
The catalog policy's initial direct-read rejection was corrected through `FindCelestial`, `RequireSystem` and
the shared type-group index, retaining the existing policy. The final planetary tests exercised that correction.
The full .NET suite was not requested and was not run. Automated checks establish protocol values and saved
outcomes; they do not establish native rendering, input or gameplay feel.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

1. Deploy a command center, build an extractor/basic-processing layout and verify scan, Submit and production visuals.
2. Import/export through a launchpad and collect a command-center launch through its journal warp destination.
3. Reconnect after an extraction cycle and inspect retained layout, goods, timers and the Planets directory.

## Resident planetary industry

Colony edits and advancement, skill checks, taxes, customs storage and launch cargo use resident state.
Colony advancement, commodity changes, wallet fees, launch IDs and pin IDs commit in the same action.
Office and launch publication includes the affected system, so a previously empty orbital view refreshes
as soon as the action commits. The orbital view uses the next saved launch expiry and includes resident
cargo changes. Existing saves retain the same tables and identity sequences.

Reference checks covered saved colony state in the local EveJS `261108.1` `planetRuntimeStore.js`
(SHA-256 prefix `CB74256D55A9ADD0`) and its separate customs settlement operation.
[EVE University planetary industry](https://wiki.eveuniversity.org/Planetary_Industry)

### Automated validation

The focused cases cover resident and file-backed customs import/export and taxation, launch collection
and expiry, same-tick orbital publication, and colony production after reopening a checkpoint. Warmed
empty and populated orbital reads assert zero SQL commands, connection opens and fitting calculations.
[Combined validation](social.md#resident-mail-inbox-and-calendar)

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer checks

None.

## Command-center market supply — November 14, 2026

Regional hubs now include the pinned catalog's NPC command-center sell assortment alongside blueprint originals.
Previously, hub expansion included only blueprints; command centers appeared only when their original supplier's
station was prepared, leaving ordinary regional market browsing without the items needed to begin PI. The existing
resident institutional seeder adds the missing offers on market preparation, including for existing saves.
No manufacturing unlock, database reset or catalog rebuild is required.

Build 3662168 has eight published market types in group 1027: Temperate 2254, Barren 2524, Oceanic 2525,
Ice 2533, Gas 2534, Lava 2549, Storm 2550 and Plasma 2551. Their catalog NPC sellers are corporations
1000010, 1000054, 1000065 and 1000109, with positive fractional `corporationTrades` values of 0.333.
Each already has a sell quote in Eden's embedded market snapshot. The fix preserves those prices, configured
institutional stock, replenishment and original supplier stations. Buy-only, unlisted, unpublished and
non-market types remain ineligible for hub sell supply; other planetary buildings are not added.

[EVE University: setting up a planetary colony](https://wiki.eveuniversity.org/Setting_up_a_planetary_colony)
confirms that the matching command center is purchased from NPC merchants before deployment. The local EveJS
`261108.1` reference's `tools/market-seederv3/src/overlay.rs` identifies all eight command centers as NPC overlay
goods (SHA-256 prefix `BD03B18F186AECC6`). This source snapshot has no Git metadata; no code was copied.
The hub distribution is Eden policy, rather than a claim about retail station distribution.

### Automated validation

The exact initial filter was
`FullyQualifiedName=Eden.Protocol.Tests.Market.PlanetaryMarketTests.HubCommandCentersCanBePurchasedAndSurviveCheckpointReopen`.
All eight cases passed after correcting the test's stock expectation to match existing immediate institutional
replenishment (build 5.52 s, tests 7.98 s). The first run failed only that incorrect stock assertion.
The resident tests exercise regional discovery from a non-supplier station, snapshot prices, rejected underpriced
purchases with unchanged balances/history/orders, hangar delivery, wallet charges, stable order identity and
inventory/history after checkpoint reopen. Additional cases cover original suppliers and eligibility rejection.

Final validation used the following combined filter:

```text
FullyQualifiedName~Eden.Protocol.Tests.Market.PlanetaryMarketTests|FullyQualifiedName~Eden.Protocol.Tests.Market.BlueprintMarketTests|FullyQualifiedName~Eden.Protocol.Tests.Market.NpcSupplyTests|FullyQualifiedName~Eden.Protocol.Tests.Planetary
```

All 70 tests passed in 30.23 s. The earlier combined run passed 69 cases and exposed a missing NPC corporation row
in the new supplier-station fixture; after supplying that catalog row, the exact supplier test passed (build 5.08 s,
tests 6.44 s) before the final combined run. Tool/solution restore and solution build passed in 1.84 s with zero
warnings/errors; both Python policy suites passed (8 + 9 tests, 1.30 s), and `tools/Format.ps1 -Check` passed in
27.65 s. `git diff --check` passed. The full .NET suite was not requested or run.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

After deploying and restarting the updated server, browse Command Centers with regional market range and buy the
type matching the intended planet. Native market presentation and deployment remain unverified by this change.

## World configuration — November 14, 2026

The administration page now has a **Planetary industry** category with independently validated 0.1–100× extraction,
factory product, command-center CPU and command-center powergrid multipliers. All default to 1×; older configuration
revisions without the group deserialize to those defaults. Draft activation remains restart-only.
See [operator settings and route limitations](../world-configuration.md#planetary-industry).

Typed Gameplay rules scale new extraction base quantities before saving the program and scale factory products when
advancing deadlines. World passes the active saved policy through every colony advancement, edit and quote boundary.
Program base quantities already serialize as `qtyPerCycle`; program deadlines and previously installed yields remain
unchanged. Factory multipliers affect all subsequently simulated completions, including offline catch-up. Configured
CPU and power affect final-layout admission. Colony state remains under the resident owner and existing checkpoint;
no second persistence implementation or SQL fallback was introduced.

Factory routes may carry up to the greater of original and configured product output, retaining ordinary input limits
and bandwidth accounting. Existing routes are never silently increased; users resize them and upgrade links to carry
additional output. Overflow and storage limits still apply. Reducing capacity preserves existing buildings but requires
a fitting final layout on the next edit.

The existing host-signed login expression supplies the active capacity and factory policies. Its planetary adapter
wraps `planetCommon.GetCPUOutput`, `GetPowerOutput` and `ProcessPin.SetSchematic`; the latter scales each pin's own
products so native simulation, output previews and route creation use the same quantity. Catalog recipes, ingredients
and cycle times are untouched. Reinstallation retains the original functions and reconnecting to default settings
restores 1× behavior. Default logins avoid eagerly loading planetary modules.

References rechecked: [UniWiki colony setup](https://wiki.eveuniversity.org/Setting_up_a_planetary_colony) and
[planetary buildings](https://wiki.eveuniversity.org/Planetary_buildings) explain command-center supply and building,
head and link constraints. These multipliers are Eden operator policy. The local EveJs files and SHA-256 identities
listed above remain the reference snapshot; its factory-output routing and capacity behavior were consulted without
copying implementation. No Carbon-owned codec, transport or Destiny behavior changed.

Inert inspection of the installed build-3662168 archive (SHA-256
`2dbc29987d01515f4f40c319f89f0276f05ac3d175d79776a03f1f8e2bab5c6a`) confirmed the command-pin calls into
`planetCommon` and `ProcessPin.SetSchematic`'s per-pin product dictionary, consumed by `Run` and `GetProducts`.
This archive is a locally prepared client, not the stock archive recorded earlier.

**Automated validation:** Tool/solution restore and isolated solution build passed (23.85 seconds, zero warnings).
Focused .NET validation passed 84 tests in 18 seconds; three optional native-client/browser checks were skipped because
their opt-in environments were unset. The catalog and formatting Python policy suites passed 8 and 9 tests respectively
(0.993 and 0.017 seconds). Formatting verification passed. Full .NET regression was not requested.

Coverage includes all 68 schematics at 0.1×, 1×, 2.5× and 100×, independent capacity rejection, legacy imports,
draft activation, resident rollback/checkpoint/reopen, and isolated signed adapter scaling, repeated installation and
default restoration. The initial exact-method filter through `tools/Test-Feature.ps1` was
`FullyQualifiedName=Eden.Protocol.Tests.Planetary.PlanetaryConfigurationTests.CapacityMultipliersIndependentlyAdmitAndRejectColonyLayouts`.
The final focused filter was:

```text
FullyQualifiedName~Eden.Protocol.Tests.Planetary|FullyQualifiedName~Eden.Protocol.Tests.Persistence.WorldConfigurationTests|FullyQualifiedName~Eden.Protocol.Tests.Administration.WorldSettingsPageTests|FullyQualifiedName~Eden.Protocol.Tests.Administration.AdministrationTests|FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.PilotLoginTests|FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.ConcordOverviewTests|FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.FreePointClientTests
```

A preceding shared-output run failed before executing tests with a test-assembly version/loading mismatch. Validation
then used `--artifacts-path artifacts/planetary-configuration` for an isolated restore/build and the successful run above.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** After restart and reconnect, compare a tuned colony's CPU/power display and factory
output preview, then resize a factory route and confirm Submit accepts a layout within the configured capacity.
