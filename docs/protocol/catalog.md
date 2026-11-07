# Catalog and provenance

[Documentation](../README.md) / [Protocol index](../protocol.md)

Pinned-build data, export inputs and catalog verification.

These records preserve milestone evidence and historical observations. Dates, schema versions, test results and live acceptance apply to the recorded change; they do not establish acceptance of later changes.

## Contents

- [Industry and economic catalog adoption, November 7, 2026](#industry-and-economic-catalog-adoption-november-7-2026)
- [Shared catalog provider, November 7, 2026](#shared-catalog-provider-november-7-2026)
- [Immutable catalog collections and world namespaces, November 7, 2026](#immutable-catalog-collections-and-world-namespaces-november-7-2026)
- [Complete typed catalog schemas, November 7, 2026](#complete-typed-catalog-schemas-november-7-2026)
- [Build 3662168 game catalog](#build-3662168-game-catalog)

## Industry and economic catalog adoption, November 7, 2026

Industry, insurance, ore reprocessing and economic-price setup now use the shared catalog boundary for their catalog inputs. Immutable station/system indexes supply facility location, ownership and region membership. Manufacturing recipes and attributes are shared definitions; installation still validates ownership, skills, quoted materials/time/cost, blueprint availability and supported products inside the existing world transaction. Saved jobs, inventory changes, money movements, cancellation and delivery retain their existing owners.

`content-support-3662168.json` separates the enabled manufacturing hull/utility sets from implemented module/ship mechanics. Its defaults preserve Merlin, Venture, Badger, Damage Control I and Expanded Cargohold I alongside the existing module/ammunition cohorts. Material-valued insurance has a separate explicit policy set with the same historical three hulls; it discovers their recipes through product-to-blueprint relationships instead of a hand-written hull/blueprint switch. Missing or ambiguous replacement recipes produce no material-based insurance value. Named item/blueprint IDs used only by tests now belong to test fixtures rather than Gameplay.

`CatalogLifetime` retains the latest catalog generation for each active host, including replacement generations after sanctioned fixture writes. This closes a gap in which direct service calls could lose their shared provider between garbage collections. Source registrations become weak again after the final host releases them. Core preload now includes the small station/system indexes; it still leaves the large celestial/localization datasets on demand.

The updated read-only catalog profile, after EF warmup, preloaded 125,398 records in 1,633.32 ms with a 69,964,672-byte managed-memory increase (66.72 MiB). The same 60 source lookups took 158.58 ms / 60 commands / 60 connections through direct readers and 1.79 ms / zero commands / zero connections through warmed shared readers. These measurements establish catalog behavior only, not a whole-gameplay frame improvement.

**Automated validation:** the exact `EconomicLifecycleTests.ReplacementInsuranceUsesTheCatalogProductRelationshipAfterABlueprintKeyChanges` regression passed, as did the host-retention/GC regression. Tool/solution restore, warning-free isolated build (22.09 seconds), 126 affected tests (10 minutes 32 seconds), six catalog Python tests, nine formatting policy tests and formatting verification passed. The final filter was `FullyQualifiedName~Eden.Protocol.Tests.Industry|FullyQualifiedName~Eden.Protocol.Tests.Economy.EconomicLifecycleTests|FullyQualifiedName~Eden.Protocol.Tests.Reprocessing|FullyQualifiedName~Eden.Protocol.Tests.Persistence.Insurance|FullyQualifiedName~Eden.Protocol.Tests.Combat.InsuranceLossTests|FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.InsuranceServiceTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.CatalogProviderTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.WorldInitializationTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting`, using isolated output under `artifacts/catalog-adoption`. This includes mine/refine/manufacture/trade/loss/replacement cycles for all three historical starter hulls and persistence/rejection cases. An earlier adoption run was deliberately interrupted after 111 completed tests when lifetime corrections were made; it is not counted as a completed validation. An overlapping intermediate build hit test-held file locks; isolated build/test output resolved that failure. The full .NET suite was not run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** None for this catalog-input migration. Native rendering and gameplay feel remain unverified. Fitting/ship/skill adoption, combat/resource adoption, remaining reference-data consumers, saved overrides and the local editor remain subsequent milestones.

Before publication, remote `main` advanced to `298f917` with slow-call fitting-snapshot and shared-connection fixes. The adoption commit was rebased onto that revision, preserving both documentation records and the incoming catalog invalidation/scope behavior. A warning-free isolated integration build (4.77 seconds) and 43 overlapping tests (approximately 3 seconds) passed with filter `FullyQualifiedName~Eden.Protocol.Tests.Persistence.CatalogProviderTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.CatalogFittingReadTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~Eden.Protocol.Tests.Combat.FittingSnapshotTests|FullyQualifiedName~Eden.Protocol.Tests.Combat.SlowCallReplayTests`. The successful 126-test adoption run covers the pre-rebase adoption changes; the 43-test integration run covers the merged catalog/scope/fitting boundaries.

## Shared catalog provider, November 7, 2026

`CatalogProvider` supplies generated typed source lookup, stable source-key paging, shared item/group/product indexes, immutable attributes, effects and required skills. Concurrent requests share in-flight loads and completed definitions, including missing results. Core item metadata, client types and manufacturing blueprint relationships are pinned at startup; other records use a cache with a default **64 MiB estimated-size limit**. Pinned definitions are additional to that limit. Estimates describe retained payload cost rather than an exact process-RAM quota. Paging is capped at 500 rows. Cache and generation diagnostics are available through typed properties.

Catalog identity combines canonical path, pinned build and ordered source provenance fingerprints. A scoped operation captures one provider generation. Sanctioned fixture/tooling writes invalidate future resolution; a captured generation retains immutable values and refuses uncached reads after invalidation. Unexpected installed-file replacement requires a server restart. Separate catalog paths remain isolated. The provider accepts generated source objects only, avoiding an alternate decoder under an existing dataset/cache key. Saved-world overrides are not part of the shared base cache and remain a subsequent milestone.

**Read-only full-catalog profile:** after warming EF metadata, core preload took **1,000.85 ms** for **53,299 item definitions** and **111,698 pinned records**. Retained managed memory increased by **67,191,512 bytes** (64.08 MiB); working set increased by **85,553,152 bytes** (81.59 MiB). These are one local measurement, not a fixed memory requirement. For the same 60 lookups (20 cycles over type IDs 601, 3638 and 3651), direct readers took **127.80 ms / 60 SQL commands / 60 connections**; warmed shared readers took **0.73 ms / zero SQL commands / zero connections**. Gameplay frame and checkpoint improvements are not claimed before feature adoption.

**Automated validation:** the exact `CatalogProviderTests.ConcurrentReadersShareDefinitionsAndWarmReadsPerformNoSql` method passed. Final tool/solution restore, warning-free build (0.93 seconds), 46 focused tests (6.19 seconds plus 1.79 seconds build), six catalog Python tests, nine formatting policy tests and formatting verification passed. The focused filter was `FullyQualifiedName~Eden.Protocol.Tests.Persistence.CatalogProviderTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.CatalogSchemaTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~Eden.Protocol.Tests.Persistence.WorldInitializationTests`. Coverage includes concurrent first loads, warmed SQL/connection/fitting counts, negative caching, cache bounds, source paging, namespace/DI ownership, isolated source files, writable invalidation and product indexes. The full .NET suite was not run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** None for this infrastructure milestone. Feature migration, saved configuration/overrides and the local editor remain subsequent milestones.

## Immutable catalog collections and world namespaces, November 7, 2026

Catalog payload arrays and maps now use immutable collections, including nested values and union alternatives. Nullable collection consumers explicitly unwrap validated present values; the existing rejection and missing-value behavior is retained. This prepares definitions for shared caching without allowing one caller to mutate another caller's inputs. Catalog payloads remain under `Eden.Data.Catalog`; all saved-world models and the launcher credential projection are grouped under `Eden.Data.World.<Feature>` and `src/Eden.Data/World`.

**Automated validation:** the exact `CatalogSchemaTests.ParsedCatalogCollectionsCannotBeMutatedThroughAnotherReference` method passed. Tool/solution restore, warning-free build (2.94 seconds), 435 affected tests (94.79 seconds plus 1.11 seconds build), six catalog Python tests, nine formatting policy tests and formatting verification passed. The focused filter was `FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~Eden.Protocol.Tests.Persistence.CatalogSchemaTests|FullyQualifiedName~Eden.Protocol.Tests.Fixtures.TestCatalogTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.CatalogFittingReadTests|FullyQualifiedName~Eden.Protocol.Tests.Flight.StationUndockTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.ImplantTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.JumpCloneTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.PilotTrainingTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.BoosterTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.ContractTests|FullyQualifiedName~Eden.Protocol.Tests.Industry.IndustryTests|FullyQualifiedName~Eden.Protocol.Tests.Reprocessing.ReprocessingTests|FullyQualifiedName~Eden.Protocol.Tests.Repair.HardenerTests|FullyQualifiedName~Eden.Protocol.Tests.Propulsion.PropulsionTests`. Full-catalog key/payload verification passed in 17.03 seconds, and the 146 persisted entity mappings still match the original baseline after accounting for CLR namespace changes. The full .NET suite was not run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** None for model organization and collection immutability. Shared runtime caching remains the next catalog milestone.

## Complete typed catalog schemas, November 7, 2026

The generator now emits typed keys and payloads for all 442 pinned datasets into `Eden.Data`, including 522 payload classes and 27 named union types for heterogeneous values. Numeric union converters select an exact Int64 representation before floating-point. Map promotion merges earlier observations instead of discarding their types. Known record types reject unknown fields. Fields or datasets that are entirely empty/null in this exact source inventory use `CatalogAbsent`: the marker rejects non-null invented payloads rather than guessing an unobserved structure. The original source JSON remains intact, including missing versus explicit null fields.

The [generated schema report](../../src/Eden.Data/Catalog/CatalogSchema.g.json) covers all 4,913,627 records with source URI/hash, typed keys, value shape and explicitly declared relationships. Relationship paths are checked against inferred schemas during generation; no relationship is inferred solely from an ID-like name. `--verify-catalog` checks descriptor provenance/counts and deserializes every original key and payload through its typed mapping. Runtime behavior, wire shapes and enabled content are unchanged; no new Blue/transport/Destiny implementation is introduced.

Regenerate using `python tools/catalog/generate_models.py "$env:LOCALAPPDATA/Eden/catalog.sqlite" src/Eden.World/Catalog/CatalogData.g.cs --models-output src/Eden.Data/Catalog/CatalogModels.g.cs`. Models and the schema report are emitted together. Optional `--schema-output` selects a report destination; `--schema-cache` reuses observed shapes only when dataset provenance/counts match. Empty markers describe the pinned corpus, not a promise to accept populated fields from a future build. The [runtime inventory](../catalog-runtime-audit.json) records remaining direct reads and content decisions for the later migration milestones; it is an inventory, not an instruction to remove protocol identifiers or gameplay rules.

**Automated validation:** all 442 datasets and 4,913,627 keys/payloads passed strict full-catalog verification in 27.70 seconds. Warning-free build, 309 affected .NET tests (47.70 seconds plus 2.06 seconds build), six catalog Python tests, nine formatting Python tests and formatting verification passed. The .NET filter was `FullyQualifiedName~Eden.Protocol.Tests.Persistence.CatalogSchemaTests|FullyQualifiedName~Eden.Protocol.Tests.Fixtures.TestCatalogTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.CatalogFittingReadTests|FullyQualifiedName~Eden.Protocol.Tests.Flight.StationUndockTests|FullyQualifiedName~Eden.Protocol.Tests.Industry.IndustryTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.BoosterTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting`, following the exact `CatalogSchemaTests.MixedCatalogValuesRoundTripWithoutLosingIntegerPrecision` method filter. A subsequent metadata-only relationship addition is covered by another focused `CatalogSchemaTests` run. The full .NET suite was not run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** None for this schema-only change. Shared runtime access, feature adoption, saved settings and the graphical editor remain subsequent milestones.

## Build 3662168 game catalog

The import inventory contains **4,913,627 source records across 442 datasets and supporting indexes, schemas, and metadata entries**. The total includes both representations when the client and export describe the same object. The universe projection includes 114 regions, 1,184 constellations, 8,490 systems, 5,210 NPC stations, and 53,299 item types.

| Source family | Entries | Records |
| --- | ---: | ---: |
| CCP JSON Lines export, including build metadata | 102 | 676,965 |
| Client definitions, localization, and loose game-data files | 211 | 3,244,505 |
| Client cache indexes | 14 | 55,478 |
| Client map database | 2 | 481,069 |
| Regional planet attribute files | 97 | 409,423 |
| External FSD schema descriptors | 6 | 6 |
| Exoplanet sample light curves | 10 | 46,181 |

Setup verifies the archive and client resource index against pinned SHA-256 digests. It resolves indexed files from the local cache, checks their content hashes, and uses the staged client's loaders for binary definitions. The [source inventory](../../tools/catalog/build-3662168.json) records the source, content hash, row count, and ordered key digest for every catalog entry. Models, textures, audio, navigation meshes, and other engine assets remain in the staged installation.

`SourceDataset` identifies each source inside the catalog. `SourceOverlap` counts matching record keys for paired datasets. `SourceDifference` identifies the keys and shared fields with different values. Both representations are available from `SourceRecord`. Client values supply positions, item physics, and gate destinations for gameplay. The export also supplies data absent from the client.

`CatalogDbContext` maps the universe and item relationships and exposes typed views of the decoded datasets. For example, an importer or server service can read a blueprint's manufacturing materials with the following query.

```csharp
using CatalogDbContext catalog = new(catalogPath);
var blueprint = CatalogData.Sde.Blueprints.Get(catalog, 681);
var materials = blueprint.Activities?.Manufacturing?.Materials;
```

The source models are generated from the pinned record shapes. Scalar fields, arrays, nested objects, and language variants retain their types. Heterogeneous source fields use `JsonElement`. To reproduce the model file after verifying the catalog, run `python tools/catalog/generate_models.py "$env:LOCALAPPDATA\Eden\catalog.sqlite" src/Eden.World/Catalog/CatalogData.g.cs` from the repository root.

World schema version 6 stores each owned ship's catalog type and home station by ID. Opening the save verifies its ship types, stations, and pilot corporations. The protocol receives a pilot snapshot with flight and hull attributes, the station designation, and the saved location. The host creates a short-lived EF context for each save operation.

### Catalog acceptance, November 2, 2026

**Automated checks.** Restore, build, and formatting verification passed with zero build warnings or errors. All 41 .NET tests and all three catalog setup tests passed. The documented setup command built and activated the local catalog after its verifier read all 4,913,627 records through their typed mappings.

**Live client.** Eden Pilot and Eden Wing each saw the other Ibis in Overview. Pilot docked at the Caldari Navy Assembly Plant, where Inventory showed **Ibis (Ibis)** with 125 m³ capacity. After undocking, Overview showed the station at 453 m. The maintainer confirmed the final restart check with Pilot docked and Wing in space.
