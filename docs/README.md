# Eden documentation

[Project overview and build instructions](../README.md)

## Setup and hosting

| Guide | Use it for |
| --- | --- |
| [Windows client setup](development-client.md) | Stage build 3662168, prepare the catalog, manage saves, launch locally and use developer travel. Optional gameplay procedures are labeled separately. |
| [Desktop launcher hosting](launcher.md) | Register accounts, configure developer access, host remotely and provision native chat trust. |

## Development

| Guide | Use it for |
| --- | --- |
| [Contributor guide](../AGENTS.md) | Project boundaries, code style, native service ownership, testing policy and milestone publishing. |
| [Automated validation](testing.md) | Build order, focused test selection, infrastructure, reporting and documentation checks. |
| [Formatting](../README.md#formatting) | Pinned formatters, coverage, width exceptions and editor integration. |
| [Architecture and runtime](architecture.md) | Typed boundaries, initialization, dependency injection, diagnostics and resident checkpoints. |

## Long-term design

| Reference | Use it for |
| --- | --- |
| [Single-player and cooperative sandbox vision](sandbox-vision.md) | Current capsuleer community milestones, economic foundations, offline progression policy and historical gameplay audit. |

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
| [Carbon licensing notes](carbon-license.md) | Reference-source licensing and attribution. |

## Keeping documentation organized

Keep the root README focused on the project overview, current status, build/run commands and formatting. Put setup and launch instructions in the Windows client guide, hosting configuration in the launcher guide, and architecture details in the runtime guide.

Add native protocol findings and milestone evidence to the relevant page under `docs/protocol/`, then add a link to the [protocol index](protocol.md). Preserve existing section anchors when moving records, and update repository links to their new destination. Keep dates, source revisions, automated results and live observations with their record. Older observations are historical evidence, not acceptance of later changes.

The RPC inventory and hull JSON are generated audit artifacts; use their documented tools rather than reorganizing their rows by hand. Agents perform automated checks only unless live testing is explicitly requested.
