# Eden: a fresh EVE universe populated by simulated capsuleers

[Documentation index](README.md)

**Status: Early design; implementation deferred until the required core gameplay systems are ready.**

Agreed design direction, November 6, 2026. Continue implementing core gameplay before starting this roadmap. The stages below describe the long-term destination and its dependencies; implementation details and tuning should be revisited against the available mechanics when this work begins.

## 1. The experience we are building

**Play ordinary EVE, alone or with friends, in a persistent universe where bots fill the roles normally occupied by other players.**

Keep New Eden's geography, empire infrastructure, and existing game mechanics. Begin with a new generation of low-skill capsuleers, modest starting assets, and an economy that develops through their activities.

The interesting experiences emerge naturally: encountering familiar miners, finding an underserved market, hunting a hauler, losing a ship to pirates, joining a corporation, or competing with a neighboring industrial group.

The agreed direction is:

- An open sandbox with freedom to pursue any supported career.
- Regional communities around empire hubs, with less populated frontiers.
- Persistent bot identities, skills, possessions, and corporation memberships.
- Normal EVE losses and recovery through ordinary mechanics.
- A shared, configurable training multiplier for humans and bots.
- Limited offline progression.
- An eventual Blazor editor for configuring the sandbox.

## 2. How the universe develops and creates a good flow

### The opening economy

Start capsuleers with ordinary new-pilot resources. Empire institutions supply the appropriate skillbooks and blueprints; starting equipment and corvettes make basic activity possible. Ordinary income sources, initially supported pirate bounties, introduce money.

Finished commercial stock must come from actual production, loot, or other legitimate EVE sources. Eden's development market must be separate from this world mode.

The initial dependency chain is:

**Starting equipment → mining and ratting → materials and income → manufacturing and trade → better ships → wider activity and losses → replacement demand.**

This follows EVE's established relationship between materials, blueprints, and production. See the [EVE industry overview](https://www.eveonline.com/news/view/eve-academy-basic-industrial-production).

Before enabling a production chain, verify that its required materials, blueprints, skills, facilities, and consumers all exist. A shortage can create gameplay; an unobtainable ingredient simply blocks it.

### The growing economy

Different bots pursue different livelihoods:

| Activity | Contribution to the world | Opportunities for you |
| --- | --- | --- |
| Mining and reprocessing | Supplies industrial materials | Mine alongside them, compete, buy materials, or hunt them |
| Manufacturing | Converts materials into usable equipment | Supply producers, buy replacements, or compete on price |
| Trading and hauling | Connects producers and buyers across stations | Trade, transport goods, intercept cargo, or protect routes |
| Ratting and missions, as implemented | Generates income, loot, and equipment demand | Share space, compete for resources, or hunt active pilots |
| Piracy and combat | Destroys assets and disrupts activity | Fight, evade, retaliate, loot, or profit from replacement demand |
| Corporation activity | Coordinates resources, fleets, and shared ambitions | Join, recruit, cooperate, or become a rival |

Prices, availability, routes, and danger should reflect actual activity. Bots can change careers, relocate, or alter their purchasing decisions when conditions change.

### The player's session

A normal session begins with an intention: earn money, replace equipment, manufacture something, explore, or hunt. Markets, Local, standings, contracts, corporations, and encounters reveal opportunities through existing EVE interfaces.

A player might notice expensive ammunition, supply the manufacturer, encounter a miner while hauling, and later recognize that pilot in a hostile fleet. The connection between those events provides continuity without requiring a separate campaign system.

## 3. How simulated capsuleers work

### Persistent pilots with ordinary constraints

Each bot has a character, skills and training queue, wallet, owned inventory, ships, location, corporation, and current plans. Losing its ship preserves the pilot and changes what it can afford to do next.

Bots choose activities according to skills, available equipment, expected profit, risk tolerance, and commitments. These are internal decision rules, not additional player-facing mechanics.

A bot must:

- Obtain and fit equipment it can actually use.
- Buy or produce replacements and consume ammunition and materials.
- Transport cargo between locations.
- Respect targeting, movement, capacitor, fitting, and combat rules.
- Acquire information through equivalent observations: Local, scans, sightings, market information, and shared intelligence.
- Assess humans and other bots through the same decision process.

Corporation membership and fleet cooperation use normal corporation and fleet systems. Relationships influence decisions through remembered interactions and ordinary standings.

### Security space matters

Highsec industrial activity, lowsec hunting, vulnerable mining operations, and eventually nullsec organizations should emerge from location, opportunity, and risk.

Crimewatch, security penalties, sentries, and CONCORD behavior are prerequisites for the corresponding aggression policies. Existing empire guards cannot substitute for those systems. EVE's differing consequences across security bands are part of the intended experience. See the [criminal timer rules](https://support.eveonline.com/hc/en-us/articles/203268591-Criminal-Timer).

### One authoritative simulation

Bot decisions should invoke the same validated gameplay operations as human actions. This requires shared operations that can run without a connected client.

Use detailed simulation where players are present and less frequent scheduling for distant activity. Background processing must preserve actual assets, travel times, resource consumption, and outcomes. Entering a system reveals an existing activity rather than generating its economic history on arrival.

## 4. Configuration, persistence, and hosting

Build typed, versioned world configuration before building the editor. The later **Blazor configuration editor** reads and updates those settings through an authenticated administration interface.

Configuration should cover:

- World seed, starting populations, regional distribution, and starting assets.
- Bot career preferences, risk tolerance, cooperation, and activity schedules.
- Economic starting conditions and supported NPC supply.
- Training rates, offline progression, and simulation budgets.
- Supported gameplay settings as their underlying mechanics become available.

Keep authentic EVE values as the baseline and make overrides explicit. Separate new-universe settings from changes that can safely affect an existing save. Editing population settings must not silently replace established pilots or reset their possessions.

For the initial offline policy, only queued skill training and already installed industry jobs advance while no humans are playing. Bots do not start new activities, trade, travel, or fight during that interval. When a friend is online, the shared universe advances normally.

Persist world settings, bot plans, economic state, and progression timestamps together so restarts cannot duplicate actions or rewards.

## 5. Build toward the vision in playable stages

| Stage | Deliverable | Completion evidence |
| --- | --- | --- |
| **1. Economic foundations** | Separate fresh-world configuration; normal market listings; complete production chains for an initial set of Tech I ships, fittings, and ammunition; usable income sources | Fresh pilots can obtain inputs, produce equipment, sell it, and purchase replacements without the testing market |
| **2. First working community** | A small population in a selected highsec area with persistent miners, ratters, manufacturers, and traders | Bots create real supply and demand, retain identities, and recover from ordinary losses |
| **3. Travel and danger** | Inter-system hauling, lowsec activity, hunting, escape decisions, and the required aggression/security mechanics | Cargo moves physically; bots and humans can intercept one another; losses affect subsequent behavior |
| **4. Social organization** | Bot corporation membership, recruitment, shared resources, fleets, and coordinated activity | You and friends can join or build an organization that functions through normal EVE systems |
| **5. Regional growth and administration** | Multiple economic communities, broader supported careers, larger populations, and the Blazor editor | Distinct regional markets develop, performance remains practical, and settings can be inspected and changed safely |

Start the prototype locally while keeping the rest of the map accessible. Expand after the small community can sustain itself.

Automated validation should cover resource and money accounting, skill and ownership restrictions, bot-to-bot interactions, loss and replacement, save/reopen behavior, offline progression, and simultaneous players in different systems. Use seeded simulations to measure time to first trade, first manufactured replacement, shortages, insolvency, and population concentration.

Follow Eden's [focused-test and milestone publishing policy](../AGENTS.md#testing). Native presentation and gameplay feel remain separate acceptance work.

**The first convincing demonstration:** you buy a ship that a simulated capsuleer manufactured from materials other pilots gathered and transported, then encounter those same pilots while flying it.
