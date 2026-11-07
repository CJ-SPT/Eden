# Typed catalog adoption and remaining constants

[Documentation index](README.md)

All 442 installed datasets have generated typed keys and payloads in `Eden.Data.Catalog`. The schema report accounts for 4,913,627 records, 522 payload classes and 27 named union types, with no unresolved fields. [Generated schemas and provenance](../src/Eden.Data/Catalog/CatalogSchema.g.json) describe the pinned source. [The runtime coverage report](catalog-runtime-audit.json) records access boundaries and a disposition for every remaining identifier/selection entry found by the inventory.

## Shared access

Ordinary features use the shared `CatalogProvider`, typed `CatalogDbContext` adapters and immutable feature definitions. Core item/system/station/corporation definitions and recipe relationships are indexed once per catalog identity. Other records and definitions load on demand within bounded retention. Resident objects retain the immutable inputs they use; existing world transactions, fitting snapshots, checkpoints and atomic ownership/destruction boundaries remain authoritative.

Source identity includes canonical path, pinned build and source fingerprints. An operation captures one source generation. Writable fixtures invalidate future resolution; production replacement requires restart. Saved prices, indices and settings belong to individual worlds and never modify the shared original source. See [configuration and local administration](world-configuration.md).

The retired raw `FindSource` API and its per-operation record cache are removed. `CatalogConnectionScope` now has only transport reuse for cold reads; warm feature methods do not force catalog connections open. Generated records, feature definitions and schema collections are immutable. Each schema field exposes its read-only compatibility policy. The browser's original JSON is an explicit storage/display boundary and is not a Gameplay input.

Catalog keys follow the importer's compact JSON representation, including unescaped Unicode/HTML characters and lowercase control escapes. Encoding stays inside catalog infrastructure, so a typed lookup, bulk lookup, page and relationship destination agree on source identity.

## Enabled content and implemented capabilities

[The build-specific policy](../src/Eden.Gameplay/Content/content-support-3662168.json) owns explicit hull cohorts, drone hulls, manufacturing/insurance cohorts, starter equipment, NPC guard rosters, special-type exclusions, regional pirate distribution and development loot pools. Its typed immutable definition belongs to Gameplay, independently of EF Core or hosting. Native identifiers remain named enums for readability; adding an enum member or generating a record does not add it to a policy set.

Group/effect/attribute predicates still describe implemented capabilities. Hull admission additionally checks category, publication and technology. Module and NPC profile builders still reject unsupported mechanics, restrictions and invalid inputs. A valid policy entry cannot make an unsupported calculator work, and catalog browsing does not enable content. The default policy exactly preserves the previous cohorts and distributions.

## Remaining decision dispositions

| Classification | Owning examples | Disposition |
| --- | --- | --- |
| Imported facts and relationships | Items, dogma, recipes, corporations, geography, locations and agents | Generated models and shared typed definitions; no ordinary direct catalog SQL or raw decoding. |
| Enabled content | Hull assembly cohorts, Brutix drone hulls, manufacturing/insurance sets, guard rosters, pirate regions, exclusions and loot choices | Immutable build policy data. Starter market offers remain their existing feature-owned JSON policy. |
| Configurable server policies | Economic prices/indices, development supply, grants, renewal, new populations, starting skills and scheduling | Versioned world settings and validated economic projections; stage for restart. |
| Native identifiers | Inventory flags, groups, skill/attribute/effect IDs, service/type codes, wallets, holds, owner forms and agent kinds | Keep named enums/constants and their existing source comments. They identify inputs or wire operations; they are not duplicated catalog records. |
| Implemented mechanics | Modifier operations, module-family calculators, hull capability interpretation, currency operations, skill extraction/injection and mathematical/timing rules | Keep typed Gameplay rules. Recognizing a record or operation does not install a new feature handler or expand a policy set. |
| Explicit compatibility exceptions | Legacy Venture saves without technology metadata, native effect order, scripted/abyssal restrictions, clone-soldier exclusions, default dogma values and native service quirks | Preserve the documented owning-feature branch. Do not infer new fields or substitute another catalog record. |
| Infrastructure metadata | DI lifetime kinds and serialization tags | Keep named metadata; these are not game-content choices. |
| Storage/verification | Import, migration, installed-build verification, hull export, fixtures and detached world transport | Explicit checked boundaries, with reasons in the coverage report. |

The report's broad `catalog-read` inventory includes legitimate shared typed lookups such as `FittingType`; it is not a count of bypasses. `coverage.catalogAccessViolations` is the enforcing result. Identifier/selection entries have individual paths, lines, source text, categories and reasons under `hardcodedDecisionDispositions`.

## Architectural checks and upkeep

`python tools/catalog/runtime_policy.py` rejects ordinary direct catalog sets, raw storage models/readers, source-view queries, catalog database access and unowned SQLite connections. Explicit import/migration/verification/fixture boundaries remain available. World-only connection transports have separate exceptions that do not authorize raw catalog models. The required catalog Python policy suite checks the repository and exercises bypass rejection, aliases, multiline calls and approved fixtures.

Regenerate the inventory with `python tools/catalog/audit_runtime.py docs/catalog-runtime-audit.json`. Run the focused owning-feature tests and both required Python suites for future changes. The source generator's deterministic schema tests remain in the catalog suite. The local editor stays under Server and delegates validation/durability to World; it owns neither catalog installation nor gameplay rules.

Startup activates a validated revision before resource reconciliation. New missing belt deadlines use its renewal hour; previously saved deadlines remain unchanged. The configuration regression covers this ordering separately from normal renewal behavior.
