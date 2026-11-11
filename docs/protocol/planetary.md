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
