# Eden: a fresh EVE universe populated by simulated capsuleers

[Documentation index](README.md)

**Status: Early design; implementation deferred until the required core gameplay systems are ready.**

Agreed design direction, November 6, 2026. Continue implementing core gameplay before starting this roadmap. The stages below describe the long-term destination and its dependencies; implementation details and tuning should be revisited against the available mechanics when this work begins.

The [core gameplay audit and implementation priorities](#6-core-gameplay-audit-and-implementation-priorities) identify the missing foundations and the recommended next work. Persistent station-local personal orders and saved economic starting settings are implemented. The initial Merlin/Venture/Badger production cohort, supporting utility modules, institutional blueprint/skillbook supply, stack consolidation and ordinary repackaging are implemented. Daily belt renewal and saved economic valuations/industry/reprocessing charges are implemented for new economy worlds. Enforced player-tackle safety and persistent aggression/logoff timers establish the initial security foundation. Pilot turret/missile damage and shared loss settlement are implemented; drone pilot targets, capsule destruction and security enforcement remain next combat work; actor execution and clock-policy prerequisites still precede an autonomous community. See the [personal market milestone](protocol/industry.md#persistent-personal-market-orders-november-6-2026).

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
- A local Blazor editor for validated settings and catalog browsing.

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

**Revised administration decision, November 7, 2026:** the approved catalog/configuration plan brought the local Blazor editor forward. Typed versioned settings and the authenticated loopback editor are implemented; see [local administration](world-configuration.md). This changes the earlier sequencing without enabling additional gameplay or capsuleer bots. The editor stages changes for restart and does not own gameplay rules.

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
| **5. Regional growth and administration** | Multiple economic communities, broader supported careers, larger populations, and broader administration tools | Distinct regional markets develop, performance remains practical, and settings can be inspected and changed safely |

Start the prototype locally while keeping the rest of the map accessible. Expand after the small community can sustain itself.

Automated validation should cover resource and money accounting, skill and ownership restrictions, bot-to-bot interactions, loss and replacement, save/reopen behavior, offline progression, and simultaneous players in different systems. Use seeded simulations to measure time to first trade, first manufactured replacement, shortages, insolvency, and population concentration.

Follow Eden's [focused-test and milestone publishing policy](../AGENTS.md#testing). Native presentation and gameplay feel remain separate acceptance work.

**The first convincing demonstration:** you buy a ship that a simulated capsuleer manufactured from materials other pilots gathered and transported, then encounter those same pilots while flying it.

## 6. Core gameplay audit and implementation priorities

### Audit baseline

Reviewed November 6, 2026 against commit [`58b1eec`](https://github.com/CJ-SPT/Eden/commit/58b1eecc55b53173f6adb624d0ac8de518a03366). Findings come from the current handlers, gameplay contracts, persistence operations, and feature records. This is a source audit, not a new gameplay test run or live acceptance result. Recheck the owning implementation before starting each milestone.

Useful foundations already exist: persistent characters and skill queues, shared flight and gate travel, docking and owned inventory, substantial fitting and NPC-combat support, mining and fixed-recipe ore reprocessing, wallets, insurance, personal contracts, chat, and substantial corporation administration. These should be extended through their current owners. See [current feature coverage](feature-notes.md) and the [corporation roadmap](corporations.md).

Some historical compatibility records and generated capability lists lag newer implementations. In particular, corporations and combat drones are partially implemented; they must not be treated as entirely absent. Conversely, catalog availability and a successful empty compatibility reply do not establish functioning gameplay.

### Missing or incomplete foundations

| System | Verified current boundary | What the sandbox still needs |
| --- | --- | --- |
| **Normal player markets** | Immediate purchases/sales against NPC orders and trade history exist. Personal/corporation outstanding-order reads return empty lists; the gameplay interface has no listing lifecycle. See [market handler](../src/Eden.Protocol/Market/MarketHandler.cs) and [market operations](../src/Eden.Gameplay/Market/PilotMarket.cs). | Persistent capsuleer buy/sell orders, reserved goods and buy-order escrow, matching and partial fills, modification, cancellation, expiry, fees, ownership checks, and delivery. Producers and consumers must be able to trade while the other party is offline. |
| **Complete ship-production chains** | Manufacturing consumes materials and preserves jobs, but its product whitelist covers small Tech I hybrid ammunition/turrets, mining lasers, shield boosters, and afterburners. It excludes ships. Blueprints are originals with no research/copy lifecycle. See [industry store](../src/Eden.World/Industry/PilotIndustryStore.cs) and [industry replies](../src/Eden.Protocol/Industry/IndustryWire.cs). | Build supported starter combat ships, mining ships, and basic haulers together with their complete usable fittings and consumables. Verify every ingredient has an obtainable source. Research, blueprint copies, remote jobs, and more advanced production can follow the first complete chain. |
| **Renewable raw-material supply** | Catalog-backed belts persist their mined quantities. Any existing belt row, including depleted rows, prevents reseeding; no scheduled replenishment path was found. Human and faction-NPC mining both consume this finite supply. Fixed-recipe ore reprocessing works; equipment recycling does not. See [asteroid population](../src/Eden.World/Mining/AsteroidFieldStore.cs) and [reprocessing](../src/Eden.World/Reprocessing/PilotReprocessingStore.cs). | A persisted, scheduled resource-renewal lifecycle appropriate to the supported EVE content, plus material availability checks for each production cohort. Renewal must not depend on reconnecting or revisiting a system. Recycling and salvage can broaden supply as their normal mechanics are implemented. |
| **Production-world economic rules** | The default market replenishes one million units per published type at 1 ISK; each pilot receives a one-time testing grant. Industry/reprocessing currently report zero ISK costs, while insurance uses catalog base values. See [market seeding](../src/Eden.World/Market/PilotMarketStore.cs), [testing grants](../src/Eden.World/Market/TestingWalletFunding.cs), and [insurance pricing](../src/Eden.World/Insurance/PilotInsuranceStore.cs). | Separate development supply/grants from fresh-world starting conditions; provide ordinary NPC blueprint/skillbook supply and earned income. Implement the missing normal price inputs, installation/reprocessing charges, and applicable trade fees. Validate insurance and replacement costs together; development prices cannot be used to assess economic balance. |
| **Pilot combat, Crimewatch, and loot rights** | Turrets and missiles now resolve pilot-owned ships with fitted defenses and persistent loss/insurance/history settlement; drones still resolve NPC targets. Player targeting and tackle already exist. Player tackle now enforces safety, saved weapons/PvP/NPC/suspect/criminal timers and limited engagements. Native state/updates, travel restrictions and timed offline presence are implemented; highsec criminal activation remains blocked until CONCORD exists. Wreck access requires the requesting pilot to be the owner. See [turret targets](../src/Eden.World/Combat/PilotCombatStore.cs), [missiles](../src/Eden.World/Combat/PilotMissileStore.cs), [Crimewatch rules](../src/Eden.Gameplay/Combat/Crimewatch.cs), and [wreck access](../src/Eden.World/Loot/WreckStore.cs). | Pilot-versus-pilot damage and loss settlement, capsule consequences, player kill attribution, legal aggression checks, weapon/logoff/suspect/criminal timers, security penalties, sentries/CONCORD, and ordinary loot/assistance rights. Apply the same rules to human and simulated capsuleers; capsuleer kills must not automatically receive NPC pirate rewards. |
| **Real cooperative fleets and corporation assets** | Corporation creation, membership, roles, wallets, tax, and shares exist. Offices, seven shared hangar divisions and permission-checked asset access are implemented; recurring rental billing and impound recovery remain pending. The personal `FleetHandler`/`IPilotFleet` remain separate from the new cooperative fleets: online membership, command hierarchy, broadcasts, fleet chat and point fleet warp are implemented. Shared rewards/loot, watchlist damage and command bonuses remain pending. See [corporation milestones](corporations.md) and [personal ship operations](../src/Eden.Gameplay/Docking/PilotFleet.cs). | Fleet creation/invites, roles, membership, broadcasts and coordinated travel, appropriate reward/loot sharing, and corporate offices/hangar access with role enforcement. Shared assets and later corporation trading/industry let human or bot groups actually operate together. |
| **Scanning, exploration, and encounter sites** | The inspected scan route only acknowledges signal-tracker registration; current encounters are predominantly belt/station populations. No functioning directional-scan, probe-scan, or exploration-site lifecycle was found. See [compatibility routes](../src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs). | Directional scanning first for ordinary situational awareness, then probes, discoverable signatures/anomalies, warp destinations, and site completion/renewal. Hacking, salvaging, and related rewards unlock exploration careers. Hunters must use available information rather than privileged knowledge of another pilot's position. |
| **Agent missions and progression through standings** | The agent directory exists, but the mission journal is empty. Bounties, some kill-related standings/history, and saved loyalty balances exist; these do not provide mission offers, objectives, or mission-driven agent access. See [agent directory](../src/Eden.Protocol/Agents/AgentDirectoryHandler.cs) and [compatibility routes](../src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs). | Ordinary mission acceptance, objectives, completion/failure, deadlines, rewards, standings-based access, and later LP-store purchases. Start with a small complete courier/combat mission cohort; bounties can fund the first economic prototype before a broad mission system exists. |
| **Complete roles for supported hulls** | Many weapons, defenses, propulsion modules, and tackle already work. Combat drones are explicitly limited to Brutix and Brutix Navy Issue. Unrestricted player ECM, dampeners, weapon disruptors, target painters, ECM amplifiers and T1/T2 rigs are implemented; player remote shield/armor repairs are implemented. Overheating, cloaking, other remote-support families, salvage tools, and several specialist roles remain unfinished. See [drone hull admission](../src/Eden.Gameplay/Drones/DroneRules.cs) and [fitting coverage](feature-notes.md#fitting-weapons-and-modules). | Finish the equipment and bonuses required by each selected starter fit, including drones on early drone hulls. Add player logistics, restricted EWAR equipment and utility modules as cooperative and hunting roles expand. Admit complete playable roles rather than counting purchasable hulls or catalog module entries as completed careers. |

### Recommended implementation order

Implement these as human-playable gameplay milestones first. Each step adds useful play now and provides operations that future bots can reuse.

1. **Persistent personal market orders: implemented for personal station-local trading.** Begin with station-local personal sell listings and escrow-backed buy orders, including partial fills, modify/cancel/expire behavior, fees, and native order/history notifications. Extract the current development seeding and grant choices into explicit saved economic settings so an isolated fresh-world fixture can run without them. Preserve existing development saves. Prove that one pilot can list manufactured ammunition, disconnect, and receive another pilot's payment with exactly-once goods and money settlement. Add remote/ranged and corporation orders in later market milestones.

2. **A complete Tech I replacement chain: implemented for the initial Merlin/Venture/Badger cohort.** Expand manufacturing to a small supported cohort of combat frigates, the Venture, and a basic hauler, plus their usable fittings and ammunition. Audit the exact pinned recipes and available ore/mineral sources before choosing the cohort. Include necessary early drone-hull support where a selected fit depends on it, and ordinary repackaging for eligible used equipment/ships. Prove that two pilots can mine, refine, manufacture, list, buy, fit, lose, and replace a ship without development purchases or item grants. Complete one cohort before expanding all races and hull classes.

3. **Sustainable resources and ordinary economic costs: implemented for catalog belts and the initial production cohort.** Add persisted resource renewal, validate reachable supply for every required mineral, and implement the missing normal industry/reprocessing price inputs and charges. Audit starting assets, recurring income, fees, insurance, and development-only loot rules together. Use the pinned client/catalog and documented EVE behavior; make any unavailable server-side values explicit configuration rather than silently inventing a new economy mechanic. Prove repeated production/replacement cycles remain possible after depletion and save reopen. This completes the minimum economic foundation for the first community.

4. **Capsuleer combat and security consequences: aggression and pilot turret/missile foundations implemented.** Player tackle now shares legality checks, saved timers and native Crimewatch updates; see [the milestone](protocol/combat.md#enforced-safety-and-persistent-aggression-november-6-2026). Turrets now damage pilot-owned ships and share atomic loss settlement; see [pilot turret combat](protocol/combat.md#pilot-turret-combat-and-loss-settlement-november-6-2026). Missiles now share aggression and loss settlement, including automatic retaliation; see [pilot missile combat](protocol/combat.md#pilot-missile-combat-and-automatic-retaliation-november-6-2026). Extend drone targets and capsule consequences, then complete the applicable legality, safety, timers, enforcement, wreck access, and loss rules before enabling hostile simulated pilots. Route existing player tackle through those same checks. Cover human-versus-human, human-versus-bot, and bot-versus-bot identities through shared rules. Prove the distinct highsec/lowsec/nullsec outcomes, valid retaliation, and persistent losses; existing defensive empire guards are not a substitute for Crimewatch or CONCORD.

5. **Cooperative fleets and useful corporation storage: initial office/storage access and core cooperative fleets implemented.** Online fleet membership, hierarchy, broadcasts, fleet chat and point fleet travel now build on the shared-office foundation. See the [fleet milestone](protocol/fleets.md#cooperative-fleet-foundation-november-7-2026). Player remote shield/armor repairs now use assistance safety and inherited combat timers. Extend reward/loot sharing and the remaining remote-support families with the aggression rules from step 4. Prove two pilots can share work, organize a fleet, supply replacement equipment, and reject unauthorized withdrawals. This supports friends immediately and later permits bot recruitment through normal EVE systems.

6. **Information and additional careers.** Deliver directional scanning, then probe scanning and repeatable sites; separately deliver a small complete standard mission cohort and its standings/reward lifecycle. Add salvage, hacking, cloaking, broader drones, and other equipment when the corresponding career needs them. These are separate focused milestones, not one combined feature release. Directional scanning is a requirement for the first hunting-bot milestone; full exploration and broad mission coverage are not prerequisites for peaceful mining/manufacturing/trading bots.

Blueprint research/copying, Tech II production, ice/gas/moon and planetary industry, player structures, alliances, sovereignty, faction warfare, capitals, and advanced fleet systems belong to later expansion. They should not block a convincing early Tech I community.

### Engineering prerequisites for the later bot milestone

The existing faction NPC miners, convoys, and pirates are useful encounter behavior, but they are not capsuleers with ordinary player skill queues, wallets, fitted owned ships, and market plans. Keep that distinction when reusing their navigation or combat behavior.

- **Shared gameplay operations:** extend the existing typed Gameplay contracts and Server transactions; keep native client adaptation in Protocol. Add market-order lifecycle and pilot-target combat contracts through their owning features. A bot must invoke the same validated operations without needing a fabricated client connection.
- **World-owned actor execution:** current player activity still advances through connection-scoped coordination. Separate actor presence and scheduled actions from client presence while retaining one authoritative world. Verify two observers do not advance one bot twice and that background travel/mining cannot create or lose assets during visibility changes. See [current update coordination](../src/Eden.Protocol/PilotSessions/PilotUpdateCoordinator.cs).
- **Explicit clock policy:** training and industry currently use UTC-based deadlines. Introduce the agreed shared training-rate and empty-world progression policy with persisted timestamps, restart behavior, and correct native deadline presentation. Preserve durations and already-paid work; do not let starting the host or reconnecting replay rewards or manufacture extra time.
- **Saved configuration and diagnostics:** establish versioned settings for world creation and bot behavior, deterministic seeds for tests, and economic/performance measurements. The local Blazor editor now handles supported validated policies. Bot-specific configuration remains deferred until its mechanics exist.

**Readiness gates:** an initial peaceful community needs steps 1-3 plus the actor/configuration prerequisites, and only uses its completed careers. Hunting capsuleers additionally need step 4 and directional scanning. Recruitable organized groups need step 5. Broader mission/exploration bots wait for their own step-6 gameplay. Do not start the large bot population before its gameplay prerequisites. The local editor was brought forward under the revised decision above.

### Validation and source discipline

For each future code milestone, use focused tests covering the changed feature and its immediate dependencies, then the required restore/build, Python policy, and formatting checks. Reopen/restart coverage is essential for orders, escrow, production, resource renewal, timers, and losses. Rejection cases must leave assets and balances unchanged. Timing and multi-observer cases should use the existing simulated sessions and `TestClock`. Follow the [testing and publishing policy](../AGENTS.md#testing), including a verified commit/push before the next milestone.

The acceptance target is a repeatable human-playable production, trade, travel, and replacement loop. Record automated validation separately from live gameplay feel; this audit adds no live acceptance claims.

Official references checked for the intended mechanics: [buy/sell orders](https://support.eveonline.com/hc/en-us/articles/203218932-Buy-and-Sell-Orders), [manufacturing](https://support.eveonline.com/hc/en-us/articles/203210292-Manufacturing), and [criminal timers](https://support.eveonline.com/hc/en-us/articles/203268591-Criminal-Timer). Confirm build-specific calls, values, and recipes against Eden's pinned client/catalog when implementing. Consult the [Carbon reference repositories](https://github.com/carbonengine) for their owned serialization and simulation behavior and record exact source revisions in the appropriate protocol topic when those mechanisms change.
