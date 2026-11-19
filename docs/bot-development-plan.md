# Bot development milestones

Eden's bot economy should give corporations different reasons to produce, buy, and trade. Alliances should start small,
claim their first systems, finance their operations from that economy, and challenge larger rivals when the opportunity
justifies the risk. These activities should progress across New Eden while any player is online. The simulation should
publish the same owned ships, goods, orders and sovereignty state a player would encounter. [Population guide](capsuleer-population.md)

The [fresh-universe plan](#fresh-universe-progression) below defines how those pilots earn the assets and skills
these milestones depend on.

| Milestone | Scope and acceptance |
| --- | --- |
| **1. First-claim procurement** | Fund one ordinary Sovereignty Hub order at a bot alliance's executor headquarters if a reachable, empty claimable system exists. Keep the monthly alliance reserve intact. Prove that an unrelated online player enables the review, invalid territory and insufficient funds leave the wallet unchanged, and reopen preserves the order and inventory without a duplicate purchase. |
| **2. First claim and home defense** | Commit the chosen route and assign a hauler with cargo capacity plus a qualified Entosis pilot with an owned fit and fuel. Use normal flight, neutral deployment, target locks, paid cycles, and native claim publication. Set a modest initial footprint and fund defense and resource upkeep. Prove two alliances can claim separate systems, contested and interrupted cycles respect normal rules, and a restart or loss preserves costs and ownership. |
| **3. Sustained fleet replacement** | Reconcile `codex/dynamic-ship-replacement` (`8305a5d4`) with current `main`. Retain its loss-manifest fitting, fallback fits, and stalled-bid refunds. Exercise losses spanning combat, mining and haulage. Prove repeated loss and replacement with real stock, verify durable refunds and cargo custody, then cover any unsupported founding or specialist fit the branch leaves behind. |
| **4. Corporate purchasing and regional trade** | Add buyers driven by fleet attrition, stock targets, trade margin and operating costs. Let different corporations specialize in production or buying. Use funded bids, station-specific stock and listings, independent facility and pilot job capacity, and established freight. Start with ammunition and replacement kits, then show a recurring two-region trade loop with no manually supplied buy orders or development stock. |
| **5. Offscreen industry and staging** | Make routine mining, local NPC ratting and trade production eligible in unobserved systems while anyone is online. Admit work in bounded batches and keep detailed flight and combat for active interactions. Let bots use accessible, fitted player structures for industry and staging. Verify checkpoint and observer handoff and the pause with zero humans online. |
| **6. Close the Athanor supply chain and broaden production** | Start from merged commission commit `1e9ee33c`. Finish fractured-field mining, reprocessing and shipping, ice and fuel, and paid reaction jobs. Deliver a Miner II from these inputs across restart. Turn the specialist blueprint, skill, facility and upstream procurement decisions into shared catalog planning. Expand through all producible catalog items, recording and resolving each missing source or unsupported production rule, including loot, gas, and research dependencies. |
| **7. Territorial expansion and campaigns** | Evaluate neighboring routes, infrastructure, supply, force strength, and vulnerability windows. Use those inputs to reinforce, contest, defend, expand or abandon claims using existing sovereignty and combat operations. Test a smaller alliance taking a justified opportunity against a larger rival and declining an unaffordable campaign. Prove economic shortages and replacement losses affect later decisions. |
| **8. Dedicated-server capacity** | Establish a paired baseline early and repeat it after offscreen and industry milestones on the existing dedicated Eden server. Measure complete session frames, due backlog age, starvation, work completed by distant systems, memory and checkpoint phases under a sustained mix of peaceful traffic, industry, and combat. Record the configuration and workload, and fix measurable bottlenecks until the target population sustains progress without starving distant jobs or regressing session latency. |

The initial procurement radius, price cap, and order duration are Eden policy. The buy order creates a native
market signal that players and other factories can fill. The first-claim expedition persists the chosen target and
route with the courier's assignment. Build 3662168 gives the packaged hub a volume of 60,000 m³. A fitted Squall can
carry it and run the link on its own. Once the first system is claimed, a fitted guard stands by at headquarters and
can defend the hub or associated command nodes with paid Entosis. The home operation pays normal alliance dues and
restocks reagent-consuming upgrades that are configured online. [Autonomous first claims](protocol/sovereignty.md#autonomous-first-claims) ·
[Home defense and upkeep](protocol/sovereignty.md#bot-home-defense-and-upkeep) ·
[Sovereignty mechanics](https://wiki.eveuniversity.org/Sovereignty) ·
[CCP sovereignty deployment and capture](https://www.eveonline.com/news/view/sovereignty-structures-and-transition)

Milestone 2 landed on `main` in `28fd1a84`. The reconciled replacement branch landed in `ba4e8ee4`. Milestone 3
verifies two paid replacements for one patrol pilot across restarts, a mining fallback with an escrow refund, and a
fitted Squall replacement that can defend its alliance's first claim. When a refinery courier is destroyed, the original
package survives only as wreck loot if it passes the freight survival roll. The factory can finance a new package and
the courier can buy a replacement Tayra and resume deliveries. Each purchase, job, loss and cargo transfer has a
durable record. [Replacement protocol and acceptance](protocol/capsuleers.md#dynamic-paid-ship-replacement)

Milestone 4 adds regional replenishment for ammunition and the starter Merlin kit. A buyer visits the supplier's
region, posts funded station bids, and brings home the stock that fills. In the two-loss replay, bounty taxes pay
for both purchases and the supplier's sales cover paid inputs, manufacturing and market costs. Both treasuries
grow after each cycle. The stock planner covers the last Merlin's loss, and two factory pilots work different
funded orders at the same station. Full and partial deliveries also survive reopen.
[Regional procurement](protocol/capsuleers.md#regional-procurement-for-bot-corporations)

Milestone 5 starts with established miners and belt ratters working while the only humans are in other systems.
Bot factories use the same clock for paid jobs at NPC stations and keep their deadlines across restart.
Player-structure industry and staging remain part of the next slice.
[Offscreen operations](protocol/capsuleers.md#offscreen-mining-belt-ratting-and-industry)

Apply the focused validation and publishing policy at each milestone. Collect capacity evidence at the baseline and
each workload increase. Use the same world, server settings and action mix for before-and-after comparisons, and report
native client acceptance separately. [Contributor guide](../AGENTS.md#testing)

## Fresh-universe progression

The first implementation covers a matched human and bot baseline and the autonomous sale of a manufactured
Miner I. Bots now spend activity SP on their ship goal, work qualifications, and the Magic 14. The broader
career and regional economy milestones below follow that opening loop.
[Opening replay](protocol/capsuleers.md#fresh-universe-starter-industry) · [Earned training](protocol/capsuleers.md#earned-bot-career-training)

Every human and simulated capsuleer starts as a new character. The first miners, couriers and mission runners
earn working capital in their corvettes. Their first trades fund local production. Finished ships and fittings reach
the market as pilots list what they build. The economy can grow as quickly as pilots can acquire inputs, train,
travel, finance jobs and fill orders.

### What currently gives bots a head start

Human creation already uses racial corvettes and configurable starting skills and civilian gear. The resident bot
provisioner can create fitted Venture or more advanced miners, freight hulls and imported combat fits. Its miner and
hauler branches start with 10,000 and 100,000 ISK respectively. Snapshot fitting can grant level V skills.
Corporation, alliance and specialist industry paths can also grant prerequisites directly. Birth, fit conversion,
recruitment and specialist assignment all need to follow the same progression policy. The local evejs reference
also provisions a racial rookie ship during character creation in `charService.js` and `rookieShipRuntime.js`.
[Human creation](../src/Eden.World/Characters/CharacterCreation.cs) ·
[Bot provisioning](../src/Eden.World/Capsuleers/CapsuleerProvisioner.Resident.cs) ·
[Snapshot fitting](../src/Eden.World/Capsuleers/TrafficFitting.Resident.cs)

The existing Progressive market can generate beginner stock before any manufacture and replenish other finished
products after a production claim. The starter production replay begins with trained pilots and working capital.
It establishes the production and replacement chain. An empty economy needs a separate test of how new pilots
finance that chain and get its first real listing onto the market.
[Beginner supply](protocol/industry.md#beginner-market-supply--november-11-2026) ·
[Starter production](protocol/industry.md#tech-i-starter-production-chain-november-6-2026)

### Starting rules and the first market

- **One character baseline.** Use the same racial corvette, civilian miner and weapon policy, racial starting skills,
  and 0 ISK for new humans and bots. Use the same default clone grade for the opening cohort and account for
  retained clone entitlements in the launch settings. Give both the same docked corvette recovery after loss.
  Grant the starting package once per character and exclude free recovery hulls and kits from profitable sale,
  reprocessing and insurance loops. Choose bot home stations near high-security schools with comparable access
  to ore, services and beginner income.
  Save each bot's identity, home and goals. Visiting a system can reveal its pilots or admit more new characters
  from the same baseline. A traffic fit may serve as a future fitting target.
- **A small NPC economy gives the first ISK somewhere to come from.** Begin with catalog skillbooks and Tech I
  blueprint originals, basic station services, existing pirate bounties, and bounded NPC bids for freshly harvested
  resources at reachable stations. Verify an immediate ore sale and a viable corvette-scale courier or mission
  payout. If existing activities leave a gap, add a small shared beginner procurement or courier offer that pays for
  completed work. Set bid budgets and floor prices after measuring first earnings, costs and whether capsuleer
  orders can compete for inputs. Check blueprint access and enough demand to afford reprocessing, installation fees
  and the first non-immediate order.
- **A listing represents real stock or funded demand.** Keep captured orders, development supply, rotating NPC
  beginner hulls and manufacture-triggered NPC replenishment out of this profile. Keep the NPC sell allowlist to
  institutional skillbooks, blueprints and explicitly identified inputs sourced from NPCs in the ordinary economy.
  Ships, ammunition, modules and finished industrial goods come from capsuleer inventory, including normal loot
  where applicable. A first manufacturing delivery creates owned stock. A funded bid can show demand before a sell
  order exists, and a listing takes its units out of the seller's available stock. Matching, fees, escrow and delivery
  use the resident market at the actual station.
- **The same economy grows after opening day.** A later joiner starts with the same package and can buy from the
  market that earlier pilots built. The fresh-universe policy applies to a newly created or explicitly reset world.
  World wipe retains configuration, so selecting a wipe and selecting this policy are separate administrator
  decisions. Bound offscreen admissions and work while any human is playing, with the same assets, travel and job
  deadlines when a human arrives to observe. Save progress and resume when players return.
  [World wipe](world-wipe.md) · [Industrial NPC demand](protocol/industry.md#industrial-npc-demand--november-9-2026)

### Careers as goals

Give each bot a near-term task and a longer-term goal. Evaluate goals against owned gear, skill prerequisites,
wallet reserves, local demand and the routes the pilot can fly. Buy and inject skillbooks through the existing
operations and apply earned SP to missing levels. When a task stalls, the pilot can earn money, post a
bid, train, take a different route, or switch work. Let humans pursue the same opportunities through existing
gameplay. Both can work in multiple careers and change direction.
[CCP's AIR career paths](https://support.eveonline.com/hc/en-us/articles/5194673685148-AIR-Career-Program) ·
[EVE University career agents](https://wiki.eveuniversity.org/Career_Agents)

| Career | First work and an example long-term goal |
| --- | --- |
| Mining | Mine with the civilian laser and sell ore or supply a refiner. Earn and fit a Venture, then support a regular refinery or factory route. |
| Manufacturing | Fund the first blueprint and job with corvette work. Refine or buy the materials, sell an ammunition or basic module batch, and reinvest in hull production when local orders justify it. |
| Hauling and trade | Use the corvette's cargo for small deliveries. Train towards an industrial and take larger loads once the expected payout covers the ship, fees and route risk. |
| Enforcer | Earn bounties or suitable mission rewards in the corvette. Order and train for a combat frigate, keep a replacement reserve and eventually escort a freight route. |
| Exploration and salvage | Start with reachable travel and starter income. Buy a player-produced scanning or salvaging fit as soon as it is available, then sell recovered inputs to manufacturers. |
| Fleet and corporation work | Begin as an individual pilot with an earned fit. Train for fleet or management roles, pool earned resources, choose a corporate specialty and eventually supply an alliance or defend its home. |

### Delivery order and acceptance

1. **Prove first income and provisioning.** Create a human and bots headed for mining, hauling and combat in a clean
   world. Compare their starting assets, wallet and skills by race and clone grade. Exercise visit, inbound and
   background admission, snapshot imports, gang and camp recruitment, and corvette recovery across reopen.
   Demonstrate real early income from a zero balance and enough spendable ISK to buy the first blueprint or post
   a funded order.
2. **Close the first supply loop.** Start with empty finished-goods books. Have miners sell and retain ore, a
   producer obtain inputs, pay for reprocessing and deliver a basic item a corvette can use, and another pilot
   buy it through a funded order on the same market humans use. Try Miner I as the first upgrade and Iron
   Charge S once a buyer has a compatible railgun. Verify repeat sales cover operating costs. Exercise two
   stations, pickup and delivery, competing orders and a restart while a job or order is open.
3. **Earn and replace the first fitted ship.** Extend the chain to Venture and a supported racial frigate, then to
   the first industrial. Include the actual supplier routes, skills, blueprint costs and harder mineral inputs.
   Have a bot train, obtain, assemble, fit and field a ship, lose it, recover in a corvette and replace the fit
   from paid stock or orders. Reuse the dynamic replacement work for broader fit procurement once integrated.
   Verify that missing stock or an unaffordable bid leads to a new earning or procurement goal.
   [Catalog recipes and supplier routes](starter-production.json) ·
   [Replacement branch](https://github.com/CJ-SPT/Eden/commit/ba4e8ee4b222d472e71b2f075c23ba2e1f26dbad)
4. **Scale careers, trade and organizations.** Let different corporations specialize in producing, purchasing
   or moving goods. Require corporations to earn and pool funds, and use qualified pilots, owned inventory,
   freight capacity and cash reserves to expand into specialist industry and claims. Demonstrate repeatable trade
   between two regions, then measure backlog and session latency on the existing dedicated Eden server as distant
   cohorts and industry become active. Preserve the gap list until each producible catalog item has a verified
   route through its inputs, skills and facilities.

Use a repeatable clean-world simulation in resident state with checkpoint reopen at each major handoff. Report
active play time to first earned ISK, first funded bid and fill, first delivered batch, first fitted non-corvette
and first paid replacement. Report cash and stock balances and the longest delay at each stage. Break out earning,
travel, training and production time so pacing changes address the actual bottleneck. Set launch pacing targets
from those measurements. Cover a one-human world, simultaneous new pilots and one active human in a distant region.
The acceptance condition is a repeatable production and trade cycle starting from new characters, with durable
evidence for the source of every tested wallet credit, item and trained skill. [Contributor guide](../AGENTS.md#testing)
