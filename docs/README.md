# Eden documentation

[Project overview and quick start](../README.md)

## Setup and hosting

| Guide | Use it for |
| --- | --- |
| [Server data and Windows client setup](development-client.md) | Install the catalog on Windows or Linux, move saves, and stage the pinned client for developer testing. |
| [Desktop launcher and remote hosting](launcher.md) | Connect accounts, configure developer access and set up remote chat trust. |
| [Docker deployment](docker.md) | Build and run the Linux server with persistent data and TLS. |
| [Saved world configuration](world-configuration.md) | Set market presets, bot populations, progression and other world rules. |
| [World wipe](world-wipe.md) | Reset a world while retaining accounts, and restore the backup if needed. |
| [Modding](modding.md) | Author, install and activate skill, server and client mod packages. |

## Gameplay and implementation

| Reference | Use it for |
| --- | --- |
| [Gameplay coverage](feature-notes.md) | Supported activities, features and current limitations. |
| [Supported agent missions](missions.md) | Supported mission definitions and the catalog audit. |
| [Capsuleer encounters](capsuleer-population.md) | Bot careers, organizations, traffic and player encounters. |
| [Corporation features](corporations.md) | Membership, administration, offices, wallets and governance. |
| [Protocol findings by topic](protocol.md) | Native client calls, source revisions, milestones and validation evidence. |
| [Client RPC route inventory](client-routes/README.md) | Generated handler candidates, compatibility routes and client call sites. |

## Development

- [Contributor guide](../AGENTS.md)
- [Architecture and runtime](architecture.md)
- [Automated validation](testing.md)
- [Optional maintainer client checks](live-checks.md)
- [Formatting](../README.md#formatting)
- [Typed catalog adoption](catalog-adoption.md)
- [Carbon licensing notes](carbon-license.md)

## Long-term design

- [Bot development milestones](bot-development-plan.md)
- [Original sandbox design and its successors](sandbox-vision.md)

## Data and audit artifacts

- [Generated catalog schemas](../src/Eden.Data/Catalog/CatalogSchema.g.json)
- [Catalog runtime audit](catalog-runtime-audit.json)
- [Example economy creation settings](economy-settings.example.json)
- [Starter ship production audit](starter-production.json)
- [Tech I hull inventory](tech-one-hulls.json)
- [Ship content coverage](ship-content-coverage.json)

## Keeping documentation organized

Use the root README for quick start, the guides for procedures, and the feature notes for current coverage.
Put native protocol findings, source revisions and validation on the owning topic page and link them from the
[protocol index](protocol.md). Preserve section anchors, dated results and live observations when moving records.
The RPC inventory and hull JSON document how to regenerate their audit data.
