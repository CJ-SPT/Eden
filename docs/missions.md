# Supported mission content

[Documentation index](README.md) / [Implementation and validation record](protocol/agents.md#supported-mission-content-expansion--november-9-2026)

The build-3662168 audit admits **508 definitions** across the ordinary, storyline, Data Center, Paragon, research, and militia pools.
That includes **83 combat encounters, 194 ordinary deliveries, 92 delivery storylines, four trade storylines, 50 Data Center
missions, 23 mining missions, 41 Paragon hull exchanges, five R&D bonus missions, and 16 militia encounters**.
The [complete catalog audit](data/mission-support-3662168.csv) lists all **2,892** records and explains why the other
**2,384** are blocked. Militia missions use their own [enlistment and offer rules](protocol/faction-warfare.md).

New offers with a base ISK reward include a timed bonus of **50% of the quoted base reward** for turn-in within **six hours of acceptance**.
The base reward remains available after that window. Existing saved offers keep their original rewards.
See [timed bonus rules and validation](protocol/agents.md#timed-mission-bonuses--november-14-2026).

## Mining content

| Level | Admitted definitions |
| --- | --- |
| 1 | Seven ore missions, including Starting Simple, Bountiful Banidine and Asteroid Catastrophe |
| 2 | Eight ore missions, including three Mercium Belt layouts and Unknown Events |
| 3 | Stay Frosty (ice), available through eligible Minmatar agents |
| 4 | Five ore missions, Cheap Chills (ice) and Gas Injections |

Request and accept an offer from a Mining division agent. Warp to the pocket from the mission tracker, harvest every
objective deposit, and return the required stack quantity to the agent's station. Turn-in accepts cargo from the active
ship's eligible holds and the personal station hangar. Bring the appropriate ore laser, ice harvester, or gas tool.
Each deposit retains its saved quantity across visits and restarts. Eden suppresses residue and critical bonus units on
these deposits so the exact stock can satisfy the objective. Scripted hostile waves, incidental belt-rat spawns and
mission chains are outside the admitted mining pool. See the [implementation record](protocol/agents.md#mining-missions---november-12-2026).

## Combat content

| Family | Admitted variants | Definitions |
| --- | --- | --- |
| Avenge a Fallen Comrade | Angel level 1 (two templates), Guristas level 2, Serpentis level 2 (two templates), DED levels 1–2 (two templates each) | 9 |
| Retribution | Angel, Guristas, Sansha (two templates) and Serpentis (two templates); level 3 | 6 |
| Angel Cartel Spies | Levels 1–4 | 4 |
| Guristas Spies | Levels 1–2 | 2 |
| Serpentis Spies | Levels 2–3 | 2 |
| Sansha Spies | Levels 2–4 | 3 |
| Eliminate a Pirate Nuisance | Angel, Blood, Guristas, Sansha, Serpentis; level 1 | 5 |
| The Blockade | Guristas, level 1 | 1 |
| The Assault | Guristas, level 4 | 1 |
| Dread Pirate Scarlet | Both catalog templates, level 4, same reviewed dungeon | 2 |
| The Spy Stash | Guristas, level 3 | 1 |
| The Hidden Stash | Angel, Blood, Guristas, Sansha, Serpentis; levels 1 and 2 | 10 |
| Duo of Death | Angel, Blood, Guristas, Sansha, Serpentis; levels 3 and 4 | 10 |
| Eliminate the Pirate Campers | Sansha and Serpentis level 1; Thukker level 2; Angel level 3 | 4 |
| Seek and Destroy | Angel and Guristas levels 1–3; Sansha levels 1–2; Blood levels 1 and 3; Serpentis levels 2–3; two Thukker-supplier templates using the same Sansha dungeons | 14 |
| The Score | Angel and Guristas level 1, Blood and Serpentis level 2, Guristas level 3 | 5 |
| The Roving Rogue Drones and The Drone Infestation | Level 1 | 2 |
| Smuggler Interception | Angel and Sansha level 2 | 2 |

Avenge a Fallen Comrade requires destruction of the **Habitat** in the Angel, Guristas, Serpentis and level-two DED pockets; guards are optional and no cargo is required. Level-one DED instead requires all six DED soldiers. Each variant retains its empty entrance room and acceleration gate. The remaining variants with unverified reinforcement triggers stay blocked. See the [evejs comparison and validation](protocol/agents.md#avenge-a-fallen-comrade--november-14-2026).

The [spy-content milestone](protocol/agents.md#verified-spy-objectives--november-15-2026) adds Guristas level two
(destroy group 2), Serpentis level three (destroy group 3), and Sansha levels two through four.
Sansha levels two and three require the spy hulls; level four requires the entire third group, including its battleship.
Other guards and sentries are optional. Sansha spies display their pinned hull names: Centior Cannibal, Centatis Specter,
and Centatis Devil/Wraith, respectively. Report-specific group aggro and custom spy labels are not reconstructed.

Retribution completes when the **outpost** is destroyed; clearing its guards alone does not complete the mission.
The empty entrance and acceleration gate remain present. Optional armory loot and report-specific group aggro are
not reconstructed. See the [verified outpost bindings and evejs comparison](protocol/agents.md#retribution-outpost-missions--november-15-2026).
The [second combat-content milestone](protocol/agents.md#level-three-combat-content-and-sansha-outposts--november-15-2026)
adds the Sansha outposts and four level-three Seek and Destroy variants. Guristas/Blood Seek requires only the
reported second group; Angel/Serpentis requires every initial enemy. Serpentis's optional post-completion Chief Guard
spawn remains excluded because the report supplies no timing or quantity.

The numeric definitions and report revisions are in [the embedded manifest](../src/Eden.World/Agents/missions-3662168.json). The [exporter](../tools/Export-MissionCatalog.py) and [additional content compiler](../tools/mission_content.py) bind report names to exact pinned catalog types, factions, geometry, objective types and quantities. Unknown or ambiguous names fail export. Mixed rogue-drone groups use their own faction. The Pirate Nuisance Angel/Guristas definitions now match the catalog's actual faction assignments: **920 Angel, 916 Guristas**.

The Hidden Stash permits the warehouse blitz: guards remain optional, the warehouse releases **15 Small Sealed Cargo Containers**, and turn-in consumes the recovered **150 m³** objective. Optional scenery loot remains excluded. The Sansha level-two report lists duplicated structures; the pinned dungeon has one objective warehouse, and that catalog geometry takes precedence.

Duo of Death requires both named combatants, with optional sentries/spider drones. Only the last objective carrier releases **one Special Delivery**, type **11068**, through ordinary personally owned wreck inventory. The Guristas level-three report's nonexistent `Pithatis Usurper` alternative is excluded; the catalog resolves its reported Death Dealer and Revolter alternatives. The required Seek and Destroy groups follow the reports' blitz descriptions; unspecified reinforcement scripts are not fabricated.

Each encounter selects one deterministic alternative from each reported group. Variable counts use the report's upper bound. Reported distances guide selection among the pinned room's native NPC anchors; missing distances retain explicit Eden hints (typically 20–25 km). Fixed batteries use their own catalog positions, and ship groups use deterministic spacing around the selected anchors. See [mission layout corrections](protocol/agents.md#mission-layout-corrections--november-9-2026) for placement policy and save compatibility. Catalog twins are admitted only when their full `killMission` payload matches a reviewed encounter and they do not declare chains or extra scripts. This is a content reconstruction, not recovery of CCP's hidden mission selection or NPC-group tables.

The level-three Guristas Score locks the second gate behind the first combat pocket's guards. The four ships
at the radio telescope complete the final objective. The Drone Infestation requires destroying its Drone Silo.
In both Smuggler Interception variants the last of two destroyed transports drops ten Militants for turn-in.
[Encounter evidence and validation](protocol/agents.md#mission-gap-expansion--november-22-2026)

## Paragon hull exchanges

Visit IRIS at a Paragon station and bring the requested packaged hull. The issuing station's personal hangar and
the active ship's Cargo Hold are eligible. Acceptance issues no hull. The pinned catalog sets each hull's Alpha and
Omega payouts. Completion credits the pilot's EverMarks wallet and matches the amount for their player corporation.

Offers and accepted missions each have a 24-hour window. The character can decline and request another hull without
a penalty. A completed turn-in closes the character's other Paragon offers and starts the same 23-hour cooldown at
every IRIS location. Eden rotates through the verified hulls by mission ID for each agent. The 32 ammunition, four
standing-reward, and one singleton Heraldry records remain blocked pending gameplay evidence.
[CCP's Paragon mission rules](https://support.eveonline.com/hc/en-us/articles/6807979979036-Heraldry-and-Emblems)

## R&D bonus assignments

With a research project running, request a bonus assignment from the R&D agent. Mineral Acquisition consumes
300, 900, 2,700, or 8,100 Tritanium for levels one through four. The Special Delivery issues one singleton package
for delivery to the marked station. Eden alternates between the two and starts the next assignment's deterministic
12–23-hour cooldown when the current one ends. An assignment is available immediately when starting eligible research
without prior assignment history.

Turn-in grants one day's research points at the current project rate, direct agent and corporation standing,
and one completion toward the ordinary storyline tally. Passive research and datacore purchases use the same
project as before. Cancelling research closes its current mission. [Research assignment reports](https://wiki.eveuniversity.org/index.php?title=Research_missions&oldid=216855)

## Trade missions and item rewards

Materials For War Preparation joins the ordinary sixteen-completion storyline pool at levels one through four.
Its level comes from the completed missions, while the storyline agent itself remains level one.

| Level | Catalog mission | Required ore | Normal item reward |
| --- | --- | --- | --- |
| 1 | 15553 | 1,000 Veldspar | One random +1 Limited attribute implant |
| 2 | 15554 | 1,500 Scordite | One random +2 Limited Beta attribute implant |
| 3 | 15555 | 10,000 Omber | One random +3 Basic attribute implant |
| 4 | 15556 | 8,000 Kernite | One random +4 Standard attribute implant |

Mine or buy the exact regular ore. Acceptance issues no ore. Deliver personally owned packaged stacks from the
active ship's Cargo Hold or the agent station's personal hangar and dock at that station to turn in. Other owners,
stations, specialized holds, nested containers, singleton lookalikes and different ore types do not qualify.
Eligible split stacks combine, only the required quantity is consumed, and excess ore remains.

The offer quotes one of the five attribute implants uniformly and saves its type and quantity immediately. Reads,
acceptance and restarts retain that exact quote. Turn-in atomically consumes ore, pays the mission and adds the
implant to the issuing pilot's personal hangar at the agent station. Duplicate turn-in cannot grant it twice.
The implant is a normal reward and remains payable after the six-hour ISK bonus expires, until mission expiry.
Decline, quit and expiry grant nothing. Native objectives use fetch, with no acceptance gift or dungeon bookmark.

The report supplies the ore and implant grades; existing Eden ISK, direct standing and bonus policy also applies,
with no storyline LP. Item rewards are personal. Standard storyline faction gains also apply capped, single-hop derived
standing changes for known faction relationships. Level-five storylines, other special-agent trade lifecycles, blueprint-copy rewards and timed item rewards
remain unavailable. See the [trade engine and reward checkpoint record](protocol/agents.md#resident-trade-objectives-and-item-rewards--november-15-2026).

Trade templates account for 460 catalog definitions. This milestone supplies shared player-sourced fetch objectives
and saved normal item rewards. Four generic storylines and the 50 Data Center definitions below are admitted;
the remaining 406 retain their additional blockers.

## Data Center agents

All **50** native Keeping Crime In Check tag-turn-in definitions are available through their **57** assigned agents.
These are one-time offers tied to those agents, rather than missions in the ordinary sixteen-completion storyline pool.
Find the public Data Center beacon in the agent's actual solar system, then approach the named agent ship.
Its catalog home station does not provide this mission service.

| Pirate tag family | Grades | Required quantity |
| --- | --- | --- |
| Angel, Blood, Guristas, Sansha and Serpentis | Copper | 3 tags |
| Each of the same five families | Bronze, Silver, Brass, Palladium, Gold, Electrum, Crystal, Platinum and Diamond | 20 tags |

The agent's level sets effective standing thresholds of **0 / 1 / 3 / 5** for levels one through four, under existing
standing-skill and clone-grade rules. Request, accept and turn in within **2,500 m of the agent hull surface**.
Bring personally owned packaged tags in the active ship's Cargo Hold. Remote hangars, other ships and foreign cargo
do not qualify. Acceptance issues no free tags. Only the required quantity is consumed, and excess remains.

Completion, decline, quitting and offer/accepted expiry permanently end that pilot's assignment with that agent.
Reopening the conversation or restarting the server cannot reset it. Agents sharing a Copper mission definition
still have separate one-time assignments. Existing Eden base ISK, six-hour bonus and direct corporation/faction
standing gains apply, with zero LP and personal rewards. Accepted quits and expirations apply the Eden direct
agent and corporation loss baseline. Data Center assignments apply direct standing changes only.

Ordinary pirates now supply all 50 required tag types through lootable wrecks. Eden uses a stable **25%** chance of
one matching-faction tag: frigates supply Copper/Bronze, destroyers Silver/Brass, cruisers Palladium/Gold,
battlecruisers Electrum/Crystal and battleships Platinum/Diamond. These are explicit Eden drop rules; hidden CCP
drop tables are not reproduced. Rogue drones and unrelated factions do not supply pirate tags.

Native agent hulls and offsets from each Data Center beacon are preserved. Each public dungeon has one stable Eden
system placement shared by all visitors. Other COSMOS, career, research and epic-arc assignments retain their own
blockers. See the [complete-family implementation and validation record](protocol/agents.md#data-center-family-and-one-time-space-agents--november-15-2026).

## Mission chains

Ordinary mission chains now have reusable, resident-backed sequential progression. Four complete level-one
Distribution chains contribute **12 stages**, included in the 194 ordinary deliveries above:

| Chain | Ordered catalog mission IDs | Supplier faction |
| --- | --- | --- |
| Save the Trees | 4838 → 4839 → 4840 | Minmatar |
| Hauling Fur | 4841 → 4842 → 4843 | Gallente |
| Hunting a Heretic | 4844 → 4845 → 4846 | Amarr |
| For the Troops | 4847 → 4849 → 4853 | Caldari |

Only the first stage enters the ordinary offer pool. Completing a stage atomically pays its rewards, consumes its
cargo and offers the next stage through the same agent. Reopen the conversation or request the next mission to view
that saved offer. Its cargo is issued only when accepted. Later stages require the immediately preceding completed
stage for the same pilot and agent; they cannot be rolled independently. Missing or ineligible later content excludes
the whole chain. Checkpoints retain offers, progression and exactly-once settlement.

Declining, quitting or expiring a stage ends that run; the agent returns to its ordinary root pool. Existing Eden
reward, supplier and route rules apply per stage, including the issuing-station pickup policy. Each completed ordinary
stage advances the storyline counter once. Each stage uses its own seven-day offer and acceptance deadline and
six-hour bonus; shared chain timers and alternate branches are not inferred from mission titles.
See the [engine, evidence and validation record](protocol/agents.md#resident-mission-chain-progression--november-15-2026).

The previous blanket chain-progression exclusion covered 278 ordinary definitions. The engine now handles complete
linear sequences, while the remaining **266** require complete verified bindings or additional mechanics for their
parts. Their unsupported encounters are not bypassed. Special-agent and epic-arc lifecycles remain separate blockers.

## Singleton courier cargo

Nine definitions require a single indivisible item rather than a packaged stack:

| Mission | Catalog IDs | Definitions | Cargo |
| --- | --- | --- | --- |
| Transaction Data Delivery | 2305–2312 | 8 delivery storylines | One Transaction And Salary Logs |
| Salve for Sis | 4837 | 1 ordinary delivery | One Secure Coded Package |

Acceptance creates a distinct item in the issuing station's personal hangar. It cannot be stacked or repackaged.
Move it through the normal ship cargo inventory and deliver it from the active ship's Cargo Hold or the personal
destination hangar. Packaged lookalikes, foreign-owned items, nested containers and specialized holds do not satisfy
these objectives. Its identity and singleton state survive checkpoints; turn-in consumes one eligible item once.

These definitions use Eden's existing volume-based level policy (level one for these cargo volumes), station route,
ISK/LP/standing rewards and six-hour bonus. Transaction Data Delivery still requires sixteen completed missions of
the same faction and level. Published reports also describe different pickup routes, collateral and random implant
rewards; those are not implemented by this inventory milestone. See the [implementation record](protocol/agents.md#singleton-mission-cargo--november-14-2026),
[Transaction Data Delivery report](https://wiki.eveuniversity.org/Transaction_Data_Delivery) and
[distribution mission list](https://wiki.eveuniversity.org/Distribution_mission_reports).

## Short-deadline deliveries

Four previously blocked catalog definitions now use their authored deadlines:

| Mission | Catalog ID | Eden level | Offer / completion window |
| --- | --- | --- | --- |
| A Change in Management | 2723 | 1 | 2 hours |
| A Change in Management | 2742 | 4 | 4 hours |
| Medical Delivery | 13771 | 1 | 8 hours |
| Wee Bug Problem | 13773 | 2 | 12 hours |

The completion window starts at acceptance. Turn-in must occur strictly before its deadline; restart does not extend it.
Levels retain the cargo-volume policy below. Timed ISK bonuses still use the separate Eden six-hour policy; the mission's
completion deadline always takes precedence. See [deadline validation](protocol/agents.md#catalog-mission-deadlines--november-14-2026).

## Delivery policy

All standalone stack-based or single-item singleton `BasicCourierMission` and `GenericStorylineCourierMission` records whose cargo and lifecycle fit the existing implementation are admitted. The catalog supplies the exact item and quantity. Original names, briefing localization and supplier faction/corporation restrictions are retained. Supplier restrictions apply both to generating offers and accepting saved offers; combat offers also exclude attacks against the issuing agent's faction.

The client catalog omits courier agent-level assignments. Existing eight level assignments remain unchanged. Additional deliveries use this explicit **Eden policy**, based on total cargo volume:

| Cargo volume | Agent level |
| --- | --- |
| Up to 320 m³ | 1 |
| Above 320 through 1,000 m³ | 2 |
| Above 1,000 through 5,000 m³ | 3 |
| Above 5,000 m³ | 4 |

These bands are not CCP level assignments. Rewards use Eden's existing level/difficulty/social-skill rules. Pickup is the issuing agent's station; the existing route policy selects a neighboring station, preferring the corporation, then a local fallback. Missing catalog deadlines retain seven days. Authored positive deadlines apply to both the offer and the completion window; acceptance starts a fresh window. The existing twenty definitions stay first in their eligible offer pools, followed by the expansion in stable order. Supplier restrictions mean an individual agent offers only a subset of the total pool. Generic delivery storylines still require sixteen completed missions for the same faction and level.

Chain parts outside the complete reviewed sequences above, scripted objectives/node graphs, initial gifts, other singleton forms, non-expiring or longer-than-seven-day lifecycles and fixed item rewards are excluded from the ordinary courier pool. Specialized lifecycles use their own agents and eligibility. Career, epic-arc, non-Data-Center COSMOS and level-five combat remain unavailable through this milestone. Many excluded combat records reference hidden `npcGroupingID` values without publishing roster contents or objective triggers. Those need verified reports and explicit bindings.

Gameplay references: [University distribution reports](https://wiki.eveuniversity.org/Distribution_mission_reports), [agent types and divisions](https://wiki.eveuniversity.org/Agent), and the revision-specific encounter links recorded per definition. The source reports describe gameplay; pinned catalog records determine build-specific identities. Source inspection and automated checks do not establish native client acceptance.

## Reproducing the content

Run from the repository root against the read-only catalog:

```powershell
python tools/Export-MissionCatalog.py --catalog src/Eden.Server/data/catalog.sqlite
python tools/Export-MissionFixture.py --catalog src/Eden.Server/data/catalog.sqlite
python tests/Eden.Protocol.Tests/Persistence/mission_content_test.py
```

The first command emits the runtime manifest and exhaustive CSV audit. The second captures **1,025 types and 6,209 source records**, plus Data Center system geography, for automated tests. These include authored NPC alternatives, objective cargo, reward alternatives, geometry, faction and corporation references, Paragon and R&D missions, and loot dependencies. SDE mission records retain the English name fallback. The fixture captures native English messages separately. Chain, trade, Data Center, Paragon, and R&D execution tests use the resident world.
