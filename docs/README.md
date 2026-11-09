# Eden documentation

[Project overview and build instructions](../README.md)

## Setup and hosting

| Guide | Use it for |
| --- | --- |
| [Windows client setup](development-client.md) | Stage build 3662168, prepare the catalog, manage saves, launch locally and use developer travel. Optional gameplay procedures are labeled separately. |
| [Saved world configuration](world-configuration.md) | Use the local Blazor editor or commands to browse catalogs, stage settings, compare history and restore revisions. |
| [Desktop launcher hosting](launcher.md) | Register accounts, configure developer access, host remotely and provision native chat trust. |

## Development

Directional scanner behavior, off-grid names and automated coverage are recorded in [directional scanning](protocol/information.md#directional-scanning-completion-november-8-2026).

Native loyalty store behavior, offline offer provenance and exclusions are recorded in [LP stores](protocol/industry.md#loyalty-point-stores-november-8-2026).

| Guide | Use it for |
| --- | --- |
| [Contributor guide](../AGENTS.md) | Project boundaries, code style, native service ownership, testing policy and milestone publishing. |
| [Automated validation](testing.md) | Build order, focused test selection, infrastructure, reporting and documentation checks. |
| [Formatting](../README.md#formatting) | Pinned formatters, coverage, width exceptions and editor integration. |
| [Architecture and runtime](architecture.md) | Typed boundaries, initialization, dependency injection, diagnostics and resident checkpoints. |
| [Typed catalog schemas](../src/Eden.Data/Catalog/CatalogSchema.g.json) | Generated dataset coverage, key/value shapes, source fingerprints and declared relationships for build 3662168. |
| [Catalog adoption and remaining constants](catalog-adoption.md) | Shared access rules, build policy, compatibility exceptions and architectural checks. |
| [Catalog migration inventory](catalog-runtime-audit.json) | Reproducible inventory of catalog access and content-specific decisions; regenerate with `tools/catalog/audit_runtime.py`. |

## Long-term design

| Reference | Use it for |
| --- | --- |
| [Single-player and cooperative sandbox vision](sandbox-vision.md) | Deferred design for simulated capsuleers, a developing economy, and local administration; includes a source-backed gameplay gap audit and prioritized prerequisites. Core gameplay systems come first. |
| [Capsuleer encounters](capsuleer-population.md) | Observed mining/hauling, remembered identities, pursuit, shared clock and historical global simulation evidence. |

## Features and implementation records

| Reference | Use it for |
| --- | --- |
| [Feature implementation notes](feature-notes.md) | Detailed coverage, restrictions and examples grouped by feature. The [README status](../README.md#current-status) is the short overview. |
| [Protocol records by topic](protocol.md) | Native call/reply findings, source revisions, milestone validation and historical client observations. |
| [Cooperative fleets](protocol/fleets.md) | Fleet creation/invites, leadership, broadcasts, fleet chat, coordinated travel and current limits. |
| [Corporation roadmap](corporations.md) | Published standalone corporation milestones and remaining scope. |
| [Corporation inventory ownership](inventory-ownership.md) | Storage ownership, migration guarantees and the office/asset boundary. |
| [Client RPC route inventory](client-routes/README.md) | Generated handler candidates, compatibility routes, call sites and repeatable auditing. |
| [Economy creation settings](economy-settings.example.json) | Example saved price, manufacturing-index and renewal overrides for a new economy world. |
| [Starter production audit](starter-production.json) | Pinned Merlin/Venture/Badger recipes, starter fits, NPC suppliers, populated ore sources and stargate routes; reproduce with `tools/Export-StarterProduction.py`. |
| [Tech I hull inventory](tech-one-hulls.json) | Machine-readable hull cohorts and deferred roles. |
| [Ship and module completion](feature-notes.md#ship-and-module-completion-program) | Ordered role milestones and production-backed coverage inventory. |
| [Information modules and salvage](protocol/information.md) | Native scanners, targeting equipment, tractor movement, saved salvage and distribution policy. |
| [Carbon licensing notes](carbon-license.md) | Reference-source licensing and attribution. |

## Keeping documentation organized

Keep the root README focused on the project overview, current status, build/run commands and formatting. Put setup and launch instructions in the Windows client guide, hosting configuration in the launcher guide, and architecture details in the runtime guide.

Add native protocol findings and milestone evidence to the relevant page under `docs/protocol/`, then add a link to the [protocol index](protocol.md). Preserve existing section anchors when moving records, and update repository links to their new destination. Keep dates, source revisions, automated results and live observations with their record. Older observations are historical evidence, not acceptance of later changes.

The RPC inventory and hull JSON are generated audit artifacts; use their documented tools rather than reorganizing their rows by hand. Agents perform automated checks only unless live testing is explicitly requested.
