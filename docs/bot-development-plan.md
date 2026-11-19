# Bot development milestones

Eden's bot economy should give corporations different reasons to produce, buy, and trade. Alliances should start small,
claim their first systems, finance their operations from that economy, and challenge larger rivals when the opportunity
justifies the risk. These activities should progress across New Eden while any player is online. The simulation should
publish the same owned ships, goods, orders and sovereignty state a player would encounter. [Population guide](capsuleer-population.md)

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
