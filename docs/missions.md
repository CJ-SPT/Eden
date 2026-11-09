# Supported mission content

[Documentation index](README.md) / [Implementation and validation record](protocol/agents.md#supported-mission-content-expansion--november-9-2026)

The build-3662168 pool contains **311 definitions: 50 combat encounters, 177 ordinary deliveries and 84 delivery storylines**. These are reconstructed Eden offers, using the existing mission systems. The [complete catalog audit](data/mission-support-3662168.csv) lists all **2,892** source records, including the **2,581** that remain unavailable. A blocked encounter may need verified content rather than a new engine capability; the audit does not claim that every excluded combat mission needs new mechanics.

## Combat content

| Family | Admitted variants | Definitions |
| --- | --- | --- |
| Angel Cartel Spies | Levels 1–4 | 4 |
| Guristas Spies | Level 1 | 1 |
| Serpentis Spies | Level 2 | 1 |
| Eliminate a Pirate Nuisance | Angel, Blood, Guristas, Sansha, Serpentis; level 1 | 5 |
| The Blockade | Guristas, level 1 | 1 |
| The Assault | Guristas, level 4 | 1 |
| Dread Pirate Scarlet | Both catalog templates, level 4, same reviewed dungeon | 2 |
| The Spy Stash | Guristas, level 3 | 1 |
| The Hidden Stash | Angel, Blood, Guristas, Sansha, Serpentis; levels 1 and 2 | 10 |
| Duo of Death | Angel, Blood, Guristas, Sansha, Serpentis; levels 3 and 4 | 10 |
| Eliminate the Pirate Campers | Sansha and Serpentis level 1; Thukker level 2; Angel level 3 | 4 |
| Seek and Destroy | Angel, Guristas and Sansha levels 1–2; Blood level 1; Serpentis level 2; two Thukker-supplier templates using the same Sansha dungeons | 10 |

The numeric definitions and report revisions are in [the embedded manifest](../src/Eden.World/Agents/missions-3662168.json). The [exporter](../tools/Export-MissionCatalog.py) and [additional content compiler](../tools/mission_content.py) bind report names to exact pinned catalog types, factions, geometry, objective types and quantities. Unknown or ambiguous names fail export. Mixed rogue-drone groups use their own faction. The Pirate Nuisance Angel/Guristas definitions now match the catalog's actual faction assignments: **920 Angel, 916 Guristas**.

The Hidden Stash permits the warehouse blitz: guards remain optional, the warehouse releases **15 Small Sealed Cargo Containers**, and turn-in consumes the recovered **150 m³** objective. Optional scenery loot remains excluded. The Sansha level-two report lists duplicated structures; the pinned dungeon has one objective warehouse, and that catalog geometry takes precedence.

Duo of Death requires both named combatants, with optional sentries/spider drones. Only the last objective carrier releases **one Special Delivery**, type **11068**, through ordinary personally owned wreck inventory. The Guristas level-three report's nonexistent `Pithatis Usurper` alternative is excluded; the catalog resolves its reported Death Dealer and Revolter alternatives. The required Seek and Destroy groups follow the reports' blitz descriptions; unspecified reinforcement scripts are not fabricated.

Each encounter selects one deterministic alternative from each reported group. Variable counts use the report's upper bound. Reported distances guide selection among the pinned room's native NPC anchors; missing distances retain explicit Eden hints (typically 20–25 km). Fixed batteries use their own catalog positions, and ship groups use deterministic spacing around the selected anchors. See [mission layout corrections](protocol/agents.md#mission-layout-corrections--november-9-2026) for placement policy and save compatibility. Catalog twins are admitted only when their full `killMission` payload matches a reviewed encounter and they do not declare chains or extra scripts. This is a content reconstruction, not recovery of CCP's hidden mission selection or NPC-group tables.

## Delivery policy

All standalone, stack-based `BasicCourierMission` and `GenericStorylineCourierMission` records whose cargo and lifecycle fit the existing implementation are admitted. The catalog supplies the exact item and quantity. Original names, briefing localization and supplier faction/corporation restrictions are retained. Supplier restrictions apply both to generating offers and accepting saved offers; combat offers also exclude attacks against the issuing agent's faction.

The client catalog omits courier agent-level assignments. Existing eight level assignments remain unchanged. Additional deliveries use this explicit **Eden policy**, based on total cargo volume:

| Cargo volume | Agent level |
| --- | --- |
| Up to 320 m³ | 1 |
| Above 320 through 1,000 m³ | 2 |
| Above 1,000 through 5,000 m³ | 3 |
| Above 5,000 m³ | 4 |

These bands are not CCP level assignments. Rewards use Eden's existing level/difficulty/social-skill rules. Pickup is the issuing agent's station; the existing route policy selects a neighboring station, preferring the corporation, then a local fallback. Mission duration remains seven days. The existing twenty definitions stay first in their eligible offer pools, followed by the expansion in stable order. Supplier restrictions mean an individual agent offers only a subset of the total pool. Generic delivery storylines still require sixteen completed missions for the same faction and level.

Chain parts, scripted objectives/node graphs, initial gifts, singleton cargo, differing deadlines and fixed item rewards are excluded. Non-generic storyline, career, epic-arc, COSMOS, research, mining and other specialized agent lifecycles are not admitted merely because a record contains cargo. Many excluded combat records reference hidden `npcGroupingID` values without publishing roster contents or objective triggers; those require verified reports and explicit bindings. Level-five combat remains outside the admitted encounter policy.

Gameplay references: [University distribution reports](https://wiki.eveuniversity.org/Distribution_mission_reports), [agent types and divisions](https://wiki.eveuniversity.org/Agent), and the revision-specific encounter links recorded per definition. The source reports describe gameplay; pinned catalog records determine build-specific identities. Source inspection and automated checks do not establish native client acceptance.

## Reproducing the content

Run from the repository root against the read-only catalog:

```powershell
python tools/Export-MissionCatalog.py --catalog "$env:LOCALAPPDATA/Eden/catalog.sqlite"
python tools/Export-MissionFixture.py --catalog "$env:LOCALAPPDATA/Eden/catalog.sqlite"
python tests/Eden.Protocol.Tests/Persistence/mission_content_test.py
```

The first command emits the runtime manifest and exhaustive CSV audit. The second captures **553 types and 3,472 source records** for offline tests, including all authored NPC alternatives, objective cargo, geometry, faction/corporation references and ordinary/commander loot dependencies. SDE mission records retain only the English name fallback; native English messages are captured separately. No game launch or runtime network access is involved.
