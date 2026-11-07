# Catalog and provenance

[Documentation](../README.md) / [Protocol index](../protocol.md)

Pinned-build data, export inputs and catalog verification.

These records preserve milestone evidence and historical observations. Dates, schema versions, test results and live acceptance apply to the recorded change; they do not establish acceptance of later changes.

## Contents

- [Build 3662168 game catalog](#build-3662168-game-catalog)

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
