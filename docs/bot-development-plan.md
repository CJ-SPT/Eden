# Bot development milestones

Eden's world setup starts with an established economy seeded from Standard market orders. Bots use the same wallets, orders,
ships and inventories as other capsuleers. Ordinary work earns the ISK they need for a fitted PvP ship. Existing firms
can still earn income through production, trade and deliveries, and alliances can fund claims and defense.
[Population guide](capsuleer-population.md) · [World settings](world-configuration.md)

## Activity income and paid PvP

1. **Earn ISK doing ordinary work.** Ratting pays NPC bounties. Miners and producers earn ISK when their goods sell.
   A bot can put spendable earnings toward a separate combat fit while retaining its work ship. Track net wallet gains
   after fees, ammunition and other costs when measuring how long the first fit takes.
2. **Buy, fit and fly a ship.** Choose from affordable zKillboard fits the pilot can fly and bring online. Buy the hull,
   modules, rigs and charges through ordinary market matching. Check that an insufficient balance or missing component
   lets the bot keep working, and that the purchase consumes the actual stock. Use the existing roam, camp and gang
   behavior after the pilot has boarded its paid fit.
3. **Recover after loss.** A pilot can reboard its parked work ship when it returns to the same station. Verify that
   another fit costs ISK, the new purchase uses real stock, and the pilot can return to combat after a checkpoint reopen.
   Measure fleet losses and recovery before increasing fleet size.

[Earned PvP ships](protocol/capsuleers.md#activity-funded-pvp-ships) ·
[Paid replacement and loss settlement](protocol/capsuleers.md#dynamic-paid-ship-replacement)

## Follow-up milestones

| Milestone | Acceptance |
| --- | --- |
| Income and replacement pacing | Observe miners, ratters and producers earning and spending through the normal world. Record time to first PvP purchase, sortie and replacement, the effect of local market prices and stock, and the time a pilot spends working again after a loss. Use those timings to tune the seeded economy. |
| Paid PvP variety | Add further catalog-valid fits only where pilots can train, find stock and save the operating cost. Exercise solo patrols, gangs and camps with actual paid losses and replacements. |
| Regional operations | Use existing ships, orders, freight and corporate funding to support patrols and defense away from headquarters. Base operations on reachable stock, shipping risk and operating reserves. |
| Dedicated-server capacity | Compare sustained, matched workloads with economic activity and combat. Measure complete session frames, backlog age, distant work, memory and checkpoint phases on the existing dedicated server. |

## Existing economic and territorial work

- Bot procurement and production use funded station orders, owned materials and paid industrial jobs. Regional routes
  buy or deliver ammunition, ship parts and production inputs. Current routes include research, planetary production,
  Mercoxit, and Athanor mining and reactions. [Regional procurement](protocol/capsuleers.md#regional-procurement-for-bot-corporations) ·
  [Demand-driven manufacturing](protocol/capsuleers.md#demand-driven-bot-manufacturing) ·
  [Offscreen work](protocol/capsuleers.md#offscreen-mining-belt-ratting-and-industry)
- Bot corporations can recruit, form alliances and finance a first claim. Qualified pilots can deploy a hub, run paid
  Entosis cycles, defend the home system and supply supported upgrades. [First claims](protocol/sovereignty.md#autonomous-first-claims) ·
  [Home defense and upkeep](protocol/sovereignty.md#bot-home-defense-and-upkeep)
- The zero-ISK start and corvette supply-chain replays are historical records for saves created under the earlier
  Fresh Universe policy. [Starter industry](protocol/capsuleers.md#fresh-universe-starter-industry) ·
  [Frontier work](protocol/capsuleers.md#fresh-world-frontier-patrols)

## Validation and publishing

Use focused resident replays through the existing gameplay owners. Check inventory, wallet, orders, useful work,
combat and loss across checkpoint reopen. Record the catalog build, exact test filter, elapsed time and limitations.
Verify milestones against the remote branch and report native client acceptance separately. [Contributor guide](../AGENTS.md#testing)
