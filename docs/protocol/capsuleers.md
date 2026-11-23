# Autonomous capsuleers

[Protocol index](../protocol.md)

## Unattended capsuleer simulation — November 23, 2026

A production log from the November 23 incident showed active background jobs before the last player disconnected and almost
no checkpoint generation change between 10:02 and 10:07 UTC. The world clock previously froze when the last selection lease
ended. The live server now starts the resident bot clock from the saved elapsed time when its listeners come up. The scheduler
wakes to admit existing jobs even with no selections, and eligible unvisited rosters can prepare in their existing bounded turns.
Entering or leaving a system still changes only which grids need native scene presentation. The existing checkpoint cadence
persists clock and job progress during unattended operation. Enable or disable this through **Run bots without players**.

Unattended scheduling is Eden policy. The local EVE.js reference advances NPC work from its scene simulation tick in
`server/src/space/runtime/scene/tick.js`. Eden continues to use the resident deadlines and catalog gate geometry for unobserved
flights. [EVE University stargates](https://wiki.eveuniversity.org/Stargates)

**Automated validation**

The exact `Test-Feature.ps1` filter matched `SavedTrafficRunsBeforeTheFirstPlayerAndAfterTheLastLeavesAcrossRestart`,
`SavedTrafficCrossesUnobservedSystemsAndResumesAfterRestart`, `UnattendedRuntimePreparesUnvisitedRostersWithoutAnObserver`,
and both cases of `SettingsSaveUpdatesStatusAndKeepsControlsInteractiveAcrossRepeatedSaves`. All five passed in 16.03 seconds
after an 8.07-second build. The replay checked a reciprocal route, zero native pilot contacts with no observers, and a resume
from the saved clock and job across one simulated hour of downtime.

**Live acceptance**

Not performed; agent client testing is disabled.

**Optional maintainer checks**

While nobody is logged in, refresh Population and compare one bot's location or progress across two checkpoints.

## Ship state after gate transfers

An incident reported a capsuleer `GateTransfer` with “The ship's system membership changed,” followed by
maintenance failures and a client disconnect. Regression tests built from `50aa7128` reproduced the exception
in both health and heat reads scoped to the departure system after a committed transfer.
The health service now reads the current ship through the
resident world when the scoped combat runtime cannot serve it. Heat reads resolve the current system under
persistence coordination. A departing ship's clean combat cache is discarded so its next visit loads any damage or
fitting changes made while away. Combat actions still require the ship to belong to the system executing the action.

[EVE University stargates](https://wiki.eveuniversity.org/Stargates)

**Automated validation**

The solution built in 4.14 seconds without warnings or errors. Both new `GateCloakTests` passed and verified
that heat can be read across a committed gate transfer and armor damage survives a return to the original system.
The 30-second focused run passed 49 of 50 cases covering ship heat, gate cloak, capsuleer presentation, inbound
gate traffic, offscreen nullsec expeditions, translocation, and system isolation. The shared-coordinator case of
`PausedSystemCannotBlockAnotherSystemsCompleteWarmFrame` timed out in isolation on both `50aa7128` and the fix.
All 17 Python policy cases and the repository formatting check passed.

**Live acceptance**

Not performed; agent client testing is disabled.

## Independent nullsec expeditions

Eligible armed travelers can choose an independent expedition from a station using the build 3662168 catalog's
reciprocal stargates, the world seed and the resident pilot's ship and work settings. The departure draw combines
the seed, pilot and saved station visit. The picker favors multi-system nullsec routes and gives quieter eligible
destinations more weight.

Independent roams use the configured roam chance at the station departure. The existing 10% belt-ratting draw is
independent and can provide belt work when a roam cannot launch. A ready roaming gang departs before the solo
expedition check. Eden's default chances favor combat traffic, while existing saved preferences still apply.
[EVE University small-gang fleet roles](https://wiki.eveuniversity.org/Fleet_and_Gang_Types)
Belt expeditions need an actual belt. Roaming expeditions need an eligible PvP fit and use ordinary patrol grids.
The return dock can be the starting station or a reachable refuge if work policy, docking access or local danger
changes. Transit-only systems can be crossed. The active route and direction use the existing travel checkpoint.
Gate crossings and activity effects use ordinary gameplay owners, including map statistics, ammunition, combat and bounties.

The pinned catalog audit found asteroid counts for all 21,831 known-space nullsec belt anchors.
EVE.js 261108.1 separates ordinary gate travel (`spaceRuntime/stargateJump.js`) from NPC station and structure docking
(`npcStationDirectory.js` and `structureDockingService.js`). Eden's route uses its own catalog geometry and access rules.
[CCP route calculation](https://developers.eveonline.com/docs/guides/route-calculation/) ·
[EVE University sovereignty and NPC nullsec](https://wiki.eveuniversity.org/Sovereignty)

**Automated validation**

`IndependentTravelerResumesSeededStationlessNullsecExpeditionOffscreen` passed both resident cases in 11 seconds
with a 6.03-second cached build. Each case launched two saved world seeds on the same graph and observed different
destinations. In each mode one pilot resumed from the checkpoint, did its chosen work and returned in the same hull.
The PvP case had no belts, and both crossed a transit-only system while the human remained elsewhere.

The extended replay exercises a roaming departure outside the belt-ratting draw and a departure where both draws
select work and reachable belts exist. The deterministic policy case sampled 1,000 eligible departures and selected
743 roaming expeditions and 25 belt-ratting expeditions. The combat provisioning case verified that a snapshot fit
with a mining laser yields to a generated combat fit. These cases and the route diversity check passed in 15.37
seconds after a 4.52-second cached build.

The legacy configuration restart test passed in 6.34 seconds after a 7.40-second build. It verified that saved
roaming and combat-fit percentages survive import and that an absent unattended setting takes the enabled default.
The shopping price and pirate reward assertions exposed by the wider selection reproduced on baseline `89d77c15`.
`ExpeditionPathsDiversifyAcrossSeedsAndReplanAroundObservedThreats` covered three real gate branches across 32 draws
and verified that the path picker avoids the reported threat.

The solution built in 13 seconds with zero warnings or errors. The combined focus run passed 24 cases in 31 seconds
using the two expedition replays, `CapsuleerMilitiaPatrolTests`, `MilitiaEnlistmentTests`,
`EstablishedPilotBuysItsFirstPvpFitFromBountiesAndKeepsItAfterReopen`,
`TravelerRatsUnobservedAndPausesWhenTheLastHumanLeaves`,
`PopulationInspectorLinksResidentBotTrainingCorporationAndAllianceWork`,
`MilitiaPatrolsAreOptInAndRoundTripThroughAdministration`, and
`PopulationControlsRoundTripPreserveLegacyPreferencesAndValidateRanges`.
The offscreen ratting replay advances the scheduled bounty payout before verifying the paid record after reopening.
The Python catalog and formatting policy suites passed all 17 cases.

**Live acceptance**

Not performed; agent client testing is disabled.

## Population inspector

The administrator Population page reads directory results and selected details from the resident world. Pilot details
prefer the latest published routine and job, with retained resident records for dormant pilots. Corporation membership,
skill queue entries, industry jobs, alliance bills and sovereignty claims come from their owning tables. Directory
results are sorted and paginated after the resident read. Each linked detail has a direct URL and refreshes on request.

The administration API exposes `GET /api/admin/population/directory?directory=Bots` with optional `search` and
zero-based `page`, and detail endpoints under `/api/admin/population/bots/{id}`, `/corporations/{id}` and
`/alliances/{id}` relative to the same population prefix. These reads require a current administrator session.

EVE.js 261108.1 provides skill queue and corporation membership reference behavior in
`server/src/services/skills/training/skillQueueState.js` and `server/src/services/corporation/allianceViewState.js`.
Eden uses its own build 3662168 definitions and saved automation state for the inspector.
[EVE University skill training](https://wiki.eveuniversity.org/Skills_and_Learning) · [Alliances](https://wiki.eveuniversity.org/Alliance)

**Automated validation**

The inspector test covers administrator access, direct links, current and dormant work, skill queues, organization
membership, dues and sovereignty assignments. Warm reads assert zero SQL commands, connections and fitting calculations.
The combined feature run passed 23 tests in 22.93 seconds using
`PopulationInspectorLinksResidentBotTrainingCorporationAndAllianceWork`,
`PopulationRequiresAdministratorAndReportsSavedCoverageWithoutAdvancingJobs`, `ResidentPopulationTests`,
`CapsuleerCorporationTests` and `CapsuleerAllianceTests`.

**Live acceptance**

Not performed; agent client testing is disabled.

## Pilot creation and offscreen admission under load

After `62f36b60` enabled unvisited-system preparation in Established Universe, the server repeatedly deferred
systems with “This Abyssal module has no saved mutation.” The operator log reported 4,231 occurrences across 384
systems. A copied traffic snapshot contains 417 profiles that reference known mutation output types. Killmail fittings
contain equipment types and slots. Mutated equipment also requires the particular item's saved attribute rolls.

Runtime snapshot loading verifies the saved checksum, then excludes fits that require missing mutations from new
selections. An older compatible fit or generated traffic can fill the slot. Existing identity lookups retain their
recorded fit. New imports reject these fits before reusing a cached validation result. The local EVE.js 261108.1
reference exposes per-item dynamic attributes in `server/src/services/dogma/dynamicItemService.js`. Eden resolves the
actual module types using the build 3662168 mutation catalog.

An Established Universe scheduler previously required the due queue to empty before starting offscreen pilots.
After advancing existing work, the scheduler can now admit one waiting pilot per 50 ms interval while the oldest
deadline is less than 100 ms late. A drained queue still permits up to four starts per interval. Older backlogs get
priority, and the existing pause, resume, and saved deadline rules apply.

### Automated validation

The exact filters for `ResidentPopulationSkipsLegacyAbyssalFitAndUsesTheCompatibleObservation`,
`EstablishedOffscreenStartsContinueWhileRecentlyDueJobsAdvance`, and
`LoginDrainsLocalJobsBeforeStartingBoundedBatchesOfOffscreenTravelers` passed all three cases in 9.06 seconds.
They verify fallback to a compatible observation, paced starts alongside recently due work, pausing starts under an
older backlog, and the original login and departure behavior.

The broader `FullyQualifiedName~` filter joined `TrafficSnapshotTests`, `CapsuleerGlobalPopulationTests`,
`CapsuleerBackgroundPreparationTests`, `CapsuleerPopulationRetryTests`, `CapsuleerPopulationSettingsTests`,
`CapsuleerMaintenanceTests`, `MutaplasmidTests` and `InstalledSnapshotSelectsAndValidatesEveryCombatCapablePatrolHull`
with `|`. All 109 cases passed in 94.06 seconds, including the test against the copied catalog and traffic snapshot.
The solution built in 7.46 seconds with zero warnings or errors. Both Python policy suites, formatting, and the diff check passed.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

After updating the server, select a character and check **Offscreen pilots waiting to start** on **Performance**.

## Unvisited systems in Established Universe

The resident worker honors the unvisited-system preparation setting while a player has a selected character.
It prepares occupied systems before eligible unvisited high-security station systems. Work-access rules and the
active cap apply. Background batches defer while the oldest scheduled
bot job is at least 250 ms late. The last player leaving pauses preparation.

The worker uses the pinned build 3662168 system security and station catalog. EVE.js 261108.1 (build 3396210) was
checked for its separate lazy on-arrival NPC spawns in `server/src/config/schema/npc.js`. No client calls change.
[UniWiki NPC Mining Operations](https://wiki.eveuniversity.org/NPC_Mining_Operations) · [Population controls](../capsuleer-population.md#population-controls)

### Automated validation

The exact filters for `EstablishedPopulationPreparesUnvisitedSystemsWhileSomeonePlaysAndStopsAtTheCap` and
`OnlyOccupiedSystemsArePreparedAndLeavingStopsPendingFounders` passed both cases in 10.49 seconds. The established
replay covers occupied-system priority, pausing when the last player leaves, resuming into an unvisited system, the
active cap and saved jobs after checkpoint reopen. The switch-off case prepares only the visited systems.

The broader `FullyQualifiedName~` filter joined `CapsuleerBackgroundPreparationTests`, `CapsuleerPopulationRetryTests`,
`CapsuleerPopulationSettingsTests`, `CapsuleerGlobalPopulationTests`, `ResidentPopulationTests`, `CapsuleerMiningFleetTests`,
`CapsuleerInboundGateAcceptanceTests`, `CapsuleerPresentationTests`, `CapsuleerTrafficAcceptanceTests`, and the methods
`PopulationControlsRenderAndSavePendingValuesThroughTheAuthenticatedEditor`,
`PopulationRequiresAdministratorAndReportsSavedCoverageWithoutAdvancingJobs`,
`SettingsSaveUpdatesStatusAndKeepsControlsInteractiveAcrossRepeatedSaves`. The historical selection also included a
retired catalog-backed population case and passed 65 cases in 65.63 seconds. The solution built in 6.95 seconds
with zero warnings or errors. The 8 catalog and 9 formatting policy tests, `Format.ps1 -Check`, and `git diff --check`
passed. The full .NET suite was outside this run.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

With active bot slots available, select a character after updating and restarting the server, then refresh **Population**
to check **Prepared systems** as the worker runs.

## Bot PvP fit progression and rat damage

With activity-funded PvP enabled, eligible bots save the exact hull, fitting slots and magazines from a supported
zKillboard fit. Goal selection uses complete market stock at the current station or a reachable supplier. A pilot can
commit to a goal before it can fly or afford it. At dock it uses the normal skillbook, unallocated SP and training
queue services. Tight fits can prompt CPU, powergrid and rigging training. It buys the entire goal with liquid
personal ISK above its starting grant once the full fit can stay online. Buying, fitting, loading, boarding and
saving the next locally available goal share the resident transaction. The saved goal survives checkpoint reopen
and retains its fitting when the imported snapshot refreshes.

After the first purchase, the bot prefers the next available career stage, or more Tech II modules or more fitted
slots at the same stage. It keeps flying its current ship until the upgrade is ready. It can rat between PvP sorties
while working toward its goal. The original parked work ship remains available for the existing recovery path.
When a market review finds no next goal, an eligible bot checks again after five minutes.

**Rat damage reduction (%)** is a saved bot setting with a default of 25 and a range of 0–100. Normal pirate NPC
weapon damage is multiplied by the remaining percentage against bot ships. The same rule applies to resident turret
fire, settled turret and missile hits, and the bot's incoming-DPS estimate. Human pilots and other damage sources
use their ordinary damage rules.

UniWiki documents planning the skills for a whole fitting. The local EVE.js build 3396210 uses ownership and skill
checks in `shipService.js` when boarding. Eden resolves the complete fit and damage against pinned build 3662168.
[UniWiki fitting plans](https://wiki.eveuniversity.org/Guide_to_using_EVEMon) · [Population settings](../capsuleer-population.md)

### Automated validation

The exact `EstablishedPilotBuysItsFirstPvpFitFromBountiesAndKeepsItAfterReopen`,
`ResidentPirateDamageIsReducedOnlyForBotShips`, `PirateDamageReductionCanMakeAnEncounterSurvivable`
and `SettingsSaveUpdatesStatusAndKeepsControlsInteractiveAcrossRepeatedSaves` filters passed eight cases
in 11.91 seconds, after an 8.91-second warning-free build. The upgrade case saved a Catalyst goal after an
Incursus purchase, reopened, trained the missing hull skill, held the Incursus while the wallet was short,
and then bought and boarded the Catalyst while keeping the original work ship.

The affected ammunition supply, ratting, NPC aggression, missiles, gangs, traffic navigation, population
settings, administration and bot filter passed 136 cases in 99.96 seconds. Eight catalog-backed cases were skipped
in that run. Two were then exercised with the installed build-3662168 catalog.
`NativeCombatKitsPayForManufactureAndBoardTwoCruisersABattlecruiserAndABattleship` passed. The courier and ratting
replay hit a world-owner file lock during cleanup on the first run and passed the isolated retry in 29.93 seconds.
The same cleanup failure reproduced on unchanged main at `575a08eb`. The catalog and formatting policy suites and
formatting checks passed.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

None.

## Activity-funded PvP ships

New worlds use the Established Universe profile with the Standard market and **Buy PvP ships with activity
income** enabled. Existing worlds load their saved profile and policy. Administrators can enable the setting for an
existing save. The original purchase rules and validation are recorded below. Current pilots use the
[saved progression goal](#bot-pvp-fit-progression-and-rat-damage). [Population settings](../capsuleer-population.md#activity-funded-pvp-ships)

A pilot at an eligible NPC market in an active system can spend the liquid personal ISK above its saved starting cash.
Ratting pays ordinary personal NPC bounties. Miners can sell surplus supported ore into existing orders after unloading
and checking station production and assigned moon supply. Production contributes when the pilot's sales settle. Ambient
gate and station travel has no payout of its own. Existing local work and cargo commitments take precedence. Purchase
requires sufficient ISK and a quote for the complete fit from ordinary captured or player-listed orders. A hauler may
visit a stocked NPC market on a nearby traffic route or at a reachable trade hub and return to its staging system.
It rechecks price, stock and fittings on arrival. If the full fit is unavailable it can return in its work ship.

The installed zKillboard traffic snapshot supplies supported combat hulls, full recorded module and rig slots, and loaded
charge types. The fitting owner checks each pilot's current skills and the online fitting limits before purchase. Charging
fills the native magazines. The source capture describes fitted slots and charges. Cargo, drones and unsupported slots
are outside the snapshot fit. Among affordable fits available at a dock, a stable per-pilot choice favors fits that
can deal damage. Support fits qualify where gangs can use them. The resident transaction buys, assembles, fits and
loads the ship, then saves its identity alongside the parked work ship. Paid fits qualify for roams, camps and gangs
under ordinary security and contact rules. Capsule recovery can reboard the parked work ship at the same station.

EVE University documents earning ISK through mining, trade and ratting, and the regional market's station-based orders.
The local EVE.js 261108.1 reference targets build 3396210. `doc/MARKET_SETUP.md` describes synthetic market seeding over
ordinary market orders and `externalservices/market-server/README.md` describes order precedence. Eden's Standard market
uses compatible captured orders and validated traffic fits from catalog build 3662168. [Making Money](https://wiki.eveuniversity.org/Making_Money) ·
[Trading](https://wiki.eveuniversity.org/Market) · [Development catalog](../development-client.md#creating-and-recreating-the-databases)

### Automated validation

The two cases of `EstablishedPilotBuysItsFirstPvpFitFromBountiesAndKeepsItAfterReopen` passed in 11.08 seconds.
A scripted kill funds an Incursus from exact zKillboard fit slots and Standard market stock. A cheaper Rifter stays
listed because the pilot lacks the skill. The local replay covers a scheduled roam, ship loss and reboarding the
parked work ship. The remote replay shops in highsec, checkpoints on the return trip, then resumes and roams from
nullsec. The scripted bounty verifies settlement. Autonomous earning rates remain unmeasured.

The combined filter covered capsuleer supply, traffic navigation, patrols, roaming gangs, traffic snapshots, Standard
and starter markets, fitting apply and constraints, administration, world wipe and configuration. All 192 cases passed
in 131.80 seconds of test-run time. After the shopping journey and import command refinements, the solution built in
15.29 seconds with zero warnings and errors. The final 40-case run covered the bounty-funded replays, traffic navigation,
first-start setup, optional data and world wipe in 29.53 seconds. The catalog and formatting policy suites and format
check passed.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

Observe a pilot acquire a fit at a seeded station after earning ISK, then verify that its work ship is parked in the same
station and the paid ship participates in a roam, camp or gang.

## Demand-driven bot work

At an NPC station, pilots in bot-led corporations review funded local buy orders, corporate replacement needs,
stock already for sale or in the depot, and paid production already underway. A courier or combat pilot can take
factory work when it has access to the product blueprint and can satisfy the job requirements. An operator with
a paid job or a factory request stays available to complete it. Other pilots can collect reachable inputs through
the existing corporate freight planner. An order with an active factory request keeps its operator. Production
lists hulls, modules and ammunition individually through the native market and settles against funded orders.

Pilots can board their own fitted mining or combat ship at the same station. They keep the old ship and its fit.
Paid work in space, freight, active expeditions and other cargo commitments keep their existing worker. A switch
records the incoming worker and its deadline in the resident world alongside the boarding transaction. Reopen
restores that worker from the checkpoint using the original character, skills, wallet, and corporate membership.

With activity-funded PvP disabled, discretionary corporate hunts require a usable combat fit and **two complete spare
kits** in the pilot’s own local hangar. The calculation uses the fitted hull, modules, drones and ammunition. Funded bids and installed jobs count
toward production coverage. Hunt readiness comes from owned inventory. Pilots can buy one unfunded shortage at a
time through normal paid transactions, keeping working capital, or place a funded bid for a missing part. A pilot
whose reserve falls can return to support work. A positive roam percentage enables these corporate decisions.
Hunting uses existing null-security and observation rules, visible contacts, target selection, and ship-loss handling.

When choosing a new higher-stage training goal, an established corporate pilot favors a complete kit it already
owns, then a kit it can afford from current local listings. Supply timing and nearby orders can lead neighboring
pilots toward different ships. The existing training and manufacturing systems settle the related skills, fees,
materials and orders. This station-local path serves established corporate pilots with qualified ships they can reach.

Reference checked against EVE.js 261108.1 (`miningNpcCatalog.js`, `npcBehaviorLoop.js`, and its market service
README). Eden uses pinned build 3662168 and its own resident economic rules. [EVE University mining operations](https://wiki.eveuniversity.org/NPC_Mining_Operations) ·
[CCP fitting requirements](https://support.eveonline.com/hc/en-us/articles/213287845-Fitting-Window) ·
[CCP market orders](https://support.eveonline.com/hc/en-us/articles/203218932-Buy-and-Sell-Orders)

The resident replays cover two decisions through the normal scheduler. A hauling pilot installs a paid batch,
reopens with the same deadline, and fills a customer’s order. A miner boards its combat ship with two complete
spares, reopens into a patrol, and returns to active mining after a spare gun is removed. The focused selection
also covers factory demand, regional freight, the two-batch moon chain, replacement, observation handoffs,
roaming combat and two historical replays with the pinned catalog. All 96 targeted cases passed in
102.61 seconds. Live acceptance and dedicated-server capacity were outside this validation.

## Queue recovery at 10,000 pilots

On the historical 90-second workstation replay, the follow-up reduced the final overdue queue from 2,973 to 411 jobs.
P95 age over the final 30 seconds fell from 1.290 to 0.209 seconds, while warm full-session frame p99 increased from
18.73 to 28.74 ms. Both runs used the same save, a simulated player, and active market maintenance. Warm requests issued
zero SQL commands, opened zero database connections and rebuilt zero fits.
[Original comparison and workload](https://github.com/CJ-SPT/Eden/blob/c31ac05af42c133567e203bb6859799c19af3852/docs/protocol/capsuleers.md#queue-recovery-at-10000-pilots)

## Miner approach stalls

In a November 22 dedicated-server checkpoint, 28 of 30 Ventures approaching ore were within 50 m outside the
8 km bot locking threshold and moving below 0.1 m/s. Garin Elden was 16.6 m outside it with about 13,000 m³
of ore in a 100,000 m³ mining hold. The pinned build 3662168 catalog gives the Venture a 5,000 m³ base hold
and Miner I a 10 km range. The server applied a 20× hold multiplier.

Navigation tapers follow thrust near the ordered distance. Ordering the same distance as the lock threshold
left miners crawling toward that threshold. The bot now aims at 90% of its locking threshold and reissues
saved approach orders that still point at the old range. It can lock as soon as it crosses the threshold.
EVE.js checks the compatible mining hold independently of approach in `server/src/services/mining/miningRuntime.js`
(SHA-256 `0A4A64EE74D915099DC268ACD4FF0CF6EEB5DE4E11011797AC04FD0372AA93B5`).
[EVE University Venture data](https://wiki.eveuniversity.org/Venture)

### Automated validation

`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerMinerTests.MinerResumesSavedApproachThatTapersOutsideLockRange`
reopens a resident checkpoint with a Venture 20 m outside its 8 km threshold. At `88905c68`, the bot remained
in `ApproachingOre` through ten simulated seconds (7.64 seconds for the runner). With the fix it locked the
same rock within that window and started a paid mining cycle (7.78 seconds for the runner).

The solution built with zero warnings or errors. All 97 cases passed in 49.78 seconds under the combined
`FullyQualifiedName~` filter for `CapsuleerMinerTests`, `CapsuleerHandoffTests`, `CapsuleerTrafficNavigationTests`,
`MiningRulesTests`, `PilotAwarenessTests`, `CapsuleerDeadlineTests`, and `CapsuleerMiningFleetTests`.
The catalog and formatting policy suites passed all 17 cases.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

Watch a previously stalled Venture resume mining and dock to unload after it fills its hold.

## Autonomous pilot creation under load

The server log from revision `abdddb2d` reported 186 slow market-demand owner holds with a median of
496.36 ms and a maximum of 810.07 ms. Traffic actions reported waits to reacquire the owner during
the same period. The performance page also reported an 8.05 s checkpoint projection outside the owner,
which is measured separately from the market holds.

Resident founder preparation now uses the target system's asteroid and renewal indexes. A founder
starting docked skips sorting the belt for an in-space opening. The same transaction records the
pilot, equipment, and initial job. Depleted deposits keep their existing renewal deadlines.

At the end of a [regional demand](industry.md#progressive-regional-markets--november-8-2026) maintenance
sweep, Eden reads the remaining sale locations once and looks up each regional bid by its indexed
assortment. The earliest applicable stock recovery boundary still determines the next wake. The batch
cursor and deadline are published after settlement commits.

The local EVE.js build 3396210 reference groups ambient startup controllers by system in
`server/src/space/npc/npcAmbientMaterialization.js` (SHA-256 prefix `4A0424F0772374FD`).
Eden's autonomous founders and regional stock recovery are server policies. [EVE University on regional markets](https://wiki.eveuniversity.org/Trading)

### Automated validation

The Windows Debug replay used the build 3662168 catalog, a fixed world seed and one selected pilot.
It reached the configured cap, checked that occupied and unvisited systems in the permitted region
received founders, and reopened the same roster after a checkpoint. Its measurement covers population
preparation and scheduler registration. Checkpoint work occurs after that measurement.

The market regression measured four warmed demand sweeps against one resting listing and 4,096
unrelated regional bids. It checked the exact recovery deadline each pass and the remaining order
quantity after the workload. The passes used zero SQL commands, connections or fitting calculations.

| Local replay | Baseline `b929f314` | Updated |
| --- | ---: | ---: |
| Prepare 64 pilots | 4.00 s / 1,092,498,080 bytes allocated | 3.24 s / 1,046,476,416 bytes allocated |
| Four market sweeps with unrelated bids | 1,001.60 ms / 634,242,104 bytes allocated | 11.54 ms / 4,941,576 bytes allocated |

Both founder replays recorded 858 SQL commands, 49 connections and 18 fitting calculations.
These are local workloads. The server log supplies the session and checkpoint context.

The solution build passed in 16.32 s with zero warnings or errors. The two exact replay methods passed
in 26.78 s. The affected filter passed 79 tests in 94.77 s. It covers founder retries and restart,
population limits, asteroid creation and renewal, progressive stock and order settlement. It omits
the previously documented SQL-trigger case `MaintenanceUsesPricePriorityAndRejectsDuplicateConsumptionAfterRollback`.
All 17 Python policy checks passed, along with formatting and the new documentation links.

```text
(FullyQualifiedName~CapsuleerBackgroundPreparationTests|FullyQualifiedName~CapsuleerGlobalPopulationTests|FullyQualifiedName~CapsuleerPopulationSettingsTests|FullyQualifiedName~ResidentPopulationTests|FullyQualifiedName~AsteroidFieldTests|FullyQualifiedName~ResourceRenewalTests|FullyQualifiedName~NpcRestingOrderTests|FullyQualifiedName~ResidentMarketHistoryTests|FullyQualifiedName~IndustrialDemandTests)&FullyQualifiedName!~MaintenanceUsesPricePriorityAndRejectsDuplicateConsumptionAfterRollback
```

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

- During first population on the dedicated server, compare market-demand owner holds and frame start delay with the supplied log.

## Bot roster and activity controls

The web editor groups settings by bot population, geography, mining, hauling and deliveries, PvE, exploration, PvP,
and organizations. **Ambient traffic** contains the station-roster multiplier and watched-gate visitor limit.
The **Population** page reports admitted slots and effective work access.

The installed build 3662168 catalog supplies system IDs, names and regions. System overrides take precedence over
region rules. Existing admission, destination selection and work checks use the resolved rule. Reciprocal
stargate paths still cross transit-only systems. The local EVE.js build 3396210 reference
`server/src/space/runtime/spaceRuntime/stargateJump.js` (SHA-256 prefix `6E84A01FB47C541A`) handles gate
transfers and arrivals. The work access layer is Eden policy.

Eligible occupied systems can still prepare founders when unvisited-system preparation is disabled. The shipment
switch gates loading trade goods through the shared freight loader. Loaded manifests continue through delivery.

Automatic corporation formation and recruitment, alliance growth, and first-claim procurement and departures
read independent settings. Existing bot applications and alliance admissions proceed on the usual schedules.
Active sovereignty expeditions, defense and dues continue. All switches default to the previous behavior on
legacy revisions. [Administrator settings](../world-configuration.md#autonomous-population)

**Automated validation**

The solution build completed with zero warnings and errors. The exact-method filter passed 24 configuration,
admission, routing, freight, corporation, alliance and historical cases in a 79.26-second run. It included
the installed build 3662168 catalog. The browser method passed its separate 40.77-second rerun with Firefox and
Chromium, including the 390-pixel viewport and expanding Ambient traffic. Both Python policy suites
passed all 17 checks. Formatting and local documentation links passed.

**Live acceptance**

Not performed; agent client testing is disabled.

**Optional maintainer checks**

None.

## Bot population limits and work regions

The active cap counts admitted pilots world-wide, including saved pilots with offscreen or idle work. On activation
Eden keeps eligible admitted pilots in pilot-ID order, restores eligible paused pilots into any reopened slots, and
preserves the start of each remaining suspension. Resident founder and gate-visitor turns check capacity before
allocating a pilot. The existing suspension removes excess pilots from presentation, scheduling and combat.

Regions default to **Work and transit**. An override can make a region **Transit only**, or an administrator can set
transit-only as the default and open selected regions for work. The route graph still permits reciprocal stargate
links through transit systems. Station stops and local activities require work access. A saved ship already following a
gate leg can leave the region. Saved work blocked by policy retains its checkpoint and appears among waiting pilots.
Already committed world timers keep their normal rules. [Administrator settings](../world-configuration.md#autonomous-population)

Region membership uses the build 3662168 catalog's system and constellation records. Traffic uses the existing
reciprocal gates. The local EVE.js build 3396210 reference handles validated gate transfer and arrival in
`server/src/space/runtime/spaceRuntime/stargateJump.js` (SHA-256 prefix `6E84A01FB47C541A`). Eden's work-versus-transit
distinction is server policy. [EVE University on the star map](https://wiki.eveuniversity.org/Star_Map)

**Automated validation**

The exact-method filter covered resident founder and gate-visitor capacity, a restart with a lowered and restored cap,
route selection and eight-jump flight through a transit-only corridor, the authenticated Blazor save, and a historical
population replay with the pinned catalog. The replay reached the cap while the human stayed in the starter system, observed
founders in unvisited systems within the work region, and reopened the same saved roster.

The final solution build passed with zero warnings and errors. The focused filter passed **243** cases in
**313.16 seconds**. It covered capsuleer population settings, global and background preparation, retry,
traffic routes and navigation, inbound provisioning and runtime, ammunition supply, corporations, alliances,
regional conflicts and pilot space activities. It also covered world settings, administration population and
save interaction, configuration persistence, and the historical population replay.
The filter omitted the previously recorded `ShoppingChoosesRealAffordableStockAndHonorsThreatMemory` theory.
The smaller population, retry, inbound and configuration filter passed 57 cases in 50.61 seconds.
Both Python policy suites passed all 17 checks. Formatting and local documentation link checks passed.

**Live acceptance**

Not performed; agent client testing is disabled.

**Optional maintainer checks**

- Save a cap and a region override, restart, and check the active values under **Population**.
- Select a character and refresh **Population** to see coverage grow as unvisited systems prepare.

## Bot activity settings

The web editor stages `capsuleers.exploration` and `capsuleers.wreckSalvage` in the world configuration revision.
Both default to on, including when older documents omit them. The switches apply at startup and appear in the
active settings on Population. Changing either activity keeps the existing population eligibility and provisioning
fingerprint. [Settings and roadmap](../world-configuration.md#autonomous-population)

When exploration is disabled, a bot skips exploration acquisitions and new sorties. Its existing sortie uses the
normal return path, including probe recall and cleanup of pending scan and hacking state. When wreck salvage is
disabled, a ratter skips salvager procurement and cancels an outstanding cycle via the information module owner.
The pilot retains the fitted module, can pick up ordinary wreck loot, and can unload goods it recovered earlier.
These gates use Eden's resident activity routines and the build 3662168 behavior recorded under
[exploration](#bot-data-and-relic-exploration) and [wreck salvage](#bot-wreck-salvage-and-trade).

**Automated validation**

The exploration replay covers both site types and a restart during a scan with exploration disabled. It verifies
probe recovery, departure-station docking and no new sortie. The salvage replay covers both resuming and canceling
a paid cycle after restart, including ordinary loot recovery with salvage disabled. Administration tests cover
the authenticated form, active and pending revisions, comparisons, and export. A legacy import test removes both
properties and verifies that they default to on.

The solution build finished without warnings or errors. The final focused filter passed 175 cases in 64.85 seconds.
It selected `WorldSettingsPageTests`, `AdministrationComponentTests`, the `SettingsSaveUpdatesStatus` and
`Population` methods on `AdministrationTests`, `WorldConfigurationTests`, `PilotSpaceActivitiesTests`,
`CapsuleerPopulationSettingsTests`, `CapsuleerHaulerTests`, `CapsuleerMinerTests`, and
`Eden.Protocol.Tests.Information`. The filter omitted the previously recorded
`ShoppingChoosesRealAffordableStockAndHonorsThreatMemory` theory. Both Python policy suites passed all 17 checks.
Formatting and all five new documentation links passed.

**Live acceptance**

Not performed; agent client testing is disabled.

**Optional maintainer checks**

In administration, turn off one activity under Player bots, save for restart, and verify its active value on Population
after restart.

## Bot traffic distribution

In the local November 12 world checkpoint, Amamake held 23 simulated haulers, including 16 docked and 9 from its own
roster. Jakanerva and Penirgman held 72 and 68 respectively. The pictured Local count of 127 is a separate observation.
Local includes docked pilots. EVE University documents the same membership behavior and identifies Amamake as a lowsec
chokepoint. EVE.js 24.01 build 3396210 projects visible sessions into Local in
`server/src/_secondary/chat/chatRuntime.js` (`getLocalGatewayMembershipPayload`, SHA-256 prefix `CA84ADB4`). Eden's
route planning uses the reciprocal gates and station positions of its build-3662168 catalog.
[EVE University on Local](https://wiki.eveuniversity.org/Scouting#Local) ·
[EVE University on topology](https://wiki.eveuniversity.org/Topology#Low_sec_topology)

The ordinary planner at `31abd819` retained eight stations per exact jump count. It ranked trade hubs and station count
before sampling, so one station-rich system could take every slot at that distance. Trips also used one breadth-first
path through equal-length forks. The planner now draws the destination system first, with less weight for recently visited
systems. It draws a station within that system using the pilot's preferences. Outbound and homeward trips vary between
equally short paths using the saved pilot seed and trip sequence. Initial watched-gate visitors spread their stops within
each distance band while still crossing the watched gate.

One catalog map per origin system and hull-access class supplies the reachable systems and distances. The planner builds
only the chosen route for an ordinary trip. The saved current route and deadlines continue through docking. The new
selection applies when the pilot plans a new ordinary trip. Threat detours, capital restrictions and fixed work destinations
retain their existing rules.

**Automated validation**

A matched build-3662168 routing replay generated 48 trips per station system within seven jumps of each named
system, with identical pilots, sequences and distance roles on both revisions. It ran 22,128 routes in each version.

| System | Routes sampled | Stops, before → after | Through trips, before → after | Distinct destinations, before → after |
|---|---:|---:|---:|---:|
| Amamake | 8,976 | 1,089 → 62 | 856 → 1,293 | 206 → 1,333 |
| Jakanerva | 3,360 | 238 → 32 | 481 → 468 | 107 → 837 |
| Penirgman | 9,792 | 1,224 → 72 | 736 → 831 | 193 → 1,220 |

Mean trip length increased by 0.67–0.87 jumps across the three samples. The replay measures routing choices and
through traffic. Local counts also depend on the deployed population, the time pilots spend in each system and work
that requires a specific destination. Raw replay results are in `artifacts/traffic-distribution/catalog-*.trx`.

The exact `CapsuleerTrafficRoutesTests.RegionalTrafficSpreadsAcrossSystemsAndEqualLengthGatePaths` filter sent all
256 baseline trips to the dense system in a two-destination fixture (5.65 seconds). The corrected planner split the trips
across both systems and both equal-length gate paths (4.48 seconds). The fixture also checks two initial gate visitors.

The focused .NET filter covered `CapsuleerTrafficRoutesTests`, `CapsuleerTrafficNavigationTests`,
`CapsuleerInboundProvisioningTests`, `CapsuleerInboundRuntimeTests`, `CapsuleerTrafficPerformanceTests`,
`RegionalConflictWorldTests`, and the fresh-traffic and receiving-only inbound acceptance methods. All 126 cases passed
in 133.24 seconds, including the installed-catalog Vattuolen route and zero-SQL warm reads.
The final build took 9.18 seconds. The route class and exact visitor-return and eight-jump restart filters passed
28 cases in 24.73 seconds. Catalog setup, formatting policy, formatter and the new documentation links passed their checks.

**Live acceptance**

Not performed; agent client testing is disabled.

**Optional maintainer checks**

Compare Local and docked guest counts across several traffic cycles in Amamake, Jakanerva and Penirgman on the deployed
world, with the same configured population.

## Bot data and relic exploration

An available first-slot hauler in an established world can take up exploration while docked at a high-security NPC
station with pirate sites enabled. The pilot qualifies through owned or purchased skillbooks, earned skill points and
the training queue. It assembles and boards a Heron fitted with a Core Probe Launcher I, Data Analyzer I and
Relic Analyzer I. It uses owned stock first and buys the remaining kit and eight Core Scanner Probe Is from station
offers when the total fits within a quarter of its wallet. A Heron with the three modules already online can enter the
same loop. The pilot reuses recovered probes for its next trip.

The pilot launches real probes, starts with a wide formation at the first catalog celestial, and centers each narrower
formation on the previous measured position. The candidate type is used only once the signature crosses the scan
group threshold. The pilot recalls its probes and warps after a fully resolved ordinary data or relic signature.
Sorties stay in the departure system. High-security pirate sites use the existing exploration definition and layout.

At the site, the pilot approaches and locks an available container and pays capacitor for its matching analyzer.
The decision policy chooses moves using revealed tiles, reachable frontier coordinates and available utilities.
The native hacking owner resolves each move, applies the normal failure limit and generates inventory on success.
The pilot takes the loot its hold can fit, returns to station and sells marketable stock into funded buy orders. Unsold
and non-marketable goods go into its hangar. A pending probe scan and the flight routine checkpoint together.
After restart the native hacking owner counts a previously active board as an abandoned attempt.

Build 3662168 catalog item IDs are Heron 605, Core Probe Launcher I 17938, Core Scanner Probe I 30013,
Data Analyzer I 22175 and Relic Analyzer I 22177. The local build 3396210 EVE.js reference computes coverage and
perfect warp targets in `server/src/services/exploration/probes/probeScanRuntime.js` and separates public board state
from transition logic in `server/src/services/exploration/hackingBoardEngine.js`. The inspected SHA-256 hashes are
`4973D4AFE1C46906C82EDBEA2AE24A144C946C89C3E5551E4FCAF898E8CC738C` and
`09646B00AC9BA92BE81A60DD82542BC6F8B2482664052B49487C6A269852AE3E`.
[Probe scanning](https://wiki.eveuniversity.org/Probe_scanning) ·
[Hacking](https://wiki.eveuniversity.org/Hacking) ·
[Resident probes and pirate exploration](information.md#resident-probes-and-pirate-exploration)

### Automated validation

`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.PilotSpaceActivitiesTests.ExplorerBuysFitsScansHacksAndSellsSiteLoot`
passed both cases in 9.58 seconds including runner overhead. Each replay buys and fits the kit through the market,
pauses with a scan in flight, resumes with the human in another system, resolves the target and retrieves all eight
probes. It plays the appropriate native data or relic board, retrieves its generated loot, and fills a funded order
after docking. The reduced build 3662168 fixture qualifies the test pilot in advance and selects a repeatable
container seed. The replays cover one ordinary site of each kind.

The broader test filter passed 157 cases in 116.07 seconds before the final change to roll back a failed native hack
action. Both exploration replays were rerun after that change. The solution built without warnings in 0.91 seconds
with warm outputs. Both Python policy suites passed 17 checks, and formatting passed.

```text
(FullyQualifiedName~ProbeScanning|FullyQualifiedName~Hacking|FullyQualifiedName~DedAndGhostSiteTests|FullyQualifiedName~PilotSpaceActivitiesTests|FullyQualifiedName~CapsuleerInboundRuntimeTests|FullyQualifiedName~CapsuleerPopulationSettingsTests)&FullyQualifiedName!~ShoppingChoosesRealAffordableStockAndHonorsThreatMemory
```

The filter omits the pre-existing shopping theory.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

- Follow a fitted explorer from a high-security station to a data or relic site, then check the station market for its sale.

## Bot wreck salvage and trade

Combat pilots can recover salvage from their own NPC wrecks on belt and public anomaly sorties. A pilot with a free
high slot and enough CPU and power can fit an owned Salvager I or spend up to a quarter of its current wallet on a
station offer. Pilots can qualify through normal skillbooks, earned skill points and the training queue. The same
recovery routine recognizes supported online salvagers already fitted to the ship.
The activity follows the existing belt-ratting and anomaly-ratting settings.

The pilot clears nearby pirates, approaches the wreck and collects any ordinary loot it can carry. It can also salvage
a wreck with an empty cargo hold. The standard targeting formula governs the lock delay. Eden's information module
owner charges capacitor, starts the cycle and rolls for access at completion. A failed access attempt can be retried
within the sortie budget. The pilot leaves other owners' wrecks alone and aborts recovery when it leaves the site or
retreats. The world checkpoints the paid cycle with the pilot's routine. Its remaining simulation time is preserved
when the last human leaves and across a restart. A wreck can still expire on its normal deadline.

The pilot unloads recovered stock into its station hangar. When a local personal or corporate buy order has funded
demand, the pilot submits an immediate sale through the market. The normal market rules apply to matching, escrow,
tax and delivery. Any unsold quantity stays in the hangar.

Build 3662168 supplies Salvager I (25861), fitting and cycle attributes, and NPC wreck access difficulty. The local
EVE.js build 3396210 reference splits activation validation, cycle resolution, salvage inventory and preservation of
unlooted contents in `server/src/space/modules/salvagerRuntime.js` (SHA-256
`9659E071D1D199889F3CA4688BA81F8329EADCB9B738DAEE94089AA606ACCBE9`). Eden uses its existing
[information and salvage operations](information.md#scanners-and-salvage-operations-november-8-2026).
[EVE University Salvaging](https://wiki.eveuniversity.org/Salvaging)

### Automated validation

`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.PilotSpaceActivitiesTests.RatterBuysSalvagerAndResumesPaidRecoveryBeforeSellingIntoFundedDemand`
passed in 8.97 seconds including runner overhead. The replay uses a reduced build 3662168 catalog with a deterministic
salvage roll and an already qualified pilot. It follows a purchased Salvager I from fitting through a paid cycle,
an empty-world pause, and a checkpoint reopen. With the observer in a different system, the pilot recovers three
units from its own wreck and returns to station. The funded bid takes one unit and the pilot retains two. The other
pilot's wreck remains untouched.

The solution built without warnings in 14.37 seconds. The focused fixture run passed 129 cases in 61.84 seconds,
including runner overhead. It covered ratting, traffic settings, inbound traffic, information modules and world
startup. The pre-existing `ShoppingChoosesRealAffordableStockAndHonorsThreatMemory` theory was excluded. Both
Python policy suites passed 17 checks, and formatting passed.

The historical catalog-backed run passed four cases in 166.86 seconds. The preceding revision records the exact filter.

```

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

- Watch a fitted combat bot finish a belt or anomaly sortie, stop near its wreck, and dock with recovered salvage.

## Catalog-driven fleet supply and production

Headquarters calculates reserves from the largest active fit per item and the combined shortages of docked
recovery fits. The kit comes from the hull, slots, magazines or compatible cargo charges, and drone bay. A loss
uses the saved manifest and normal fitting and skill checks. Marketable parts can drive purchases. Pilots can
also fit published parts they already own or receive from their corporation.

The planner deducts depot stock, outstanding local bids, and manufacturing output due at the depot by quantity.
Eligible industrials can deliver to headquarters or a stranded pilot's station. The planner checks cargo room,
fees, treasury reserves, and gate access. After an empty or partial fill, the next review considers other suppliers
first. The existing bid and freight checkpoints cover escrow, partial fills, restart, and cargo loss.

Funded orders can drive catalog manufacturing, copying and invention of a selected result, and manufacturing
of missing components. Operators use native skills, facilities, and owned licenses. The market can supply
source originals and relics. Factories can also build affordable reserves for active fits from blueprints they
already own. A profitable run can fill a small order and retain the excess in corporate stock.

Manufacturing shortages can use qualified operators, existing online reactors for the catalog formulas, and
the existing freight loop. Upstream materials, blueprints, and reactor access vary by location. Player-structure
manufacturing uses dedicated staging paths. Inputs from exploration, salvage, and gas harvesting still depend
on available supply.

Build 3662168 lists the Small Shield Booster II invention path under blueprint 1032 and includes the Tech I
booster in blueprint 1033's materials. EVE.js indexes recipes by blueprint in
`server/src/services/industry/industryStaticData.js`
(SHA-256 `19D0D5692F68D6E19570174F79AC2B81FEEC24B2305D93DAB19F7A90D0F1B567`).
[CCP manufacturing](https://support.eveonline.com/hc/en-us/articles/203210292-Manufacturing) ·
[CCP invention](https://support.eveonline.com/hc/en-us/articles/203210642-Invention) ·
[CCP market orders](https://support.eveonline.com/hc/en-us/articles/203218932-Buy-and-Sell-Orders)

### Automated validation

`CatalogDemandCopiesAndInventsThenManufacturesAMissingComponentAcrossRestart` copies and invents a Small Shield
Booster II license, reopens with the science job on disk, builds the Tech I component, sells one unit from a
two-unit run, and produces a spare for an operating fleet. The catalog fixture controls material amounts, job times,
and invention chance. `BotFundsRegionalKitOrdersAndHaulsManufacturedStockAcrossRestarts` covers full and partial
Merlin-fit deliveries and a Venture fit with two mining lasers and a damage control.

The solution build passed without warnings in 17.88 seconds. The following focused selection passed 149 cases
in 132.18 seconds including runner overhead. Both Python policy suites passed 17 checks in total. Formatting,
line width, and documentation checks passed.

```text
(FullyQualifiedName~CapsuleerAmmunitionSupplyTests|FullyQualifiedName~CapsuleerSupplyTests|FullyQualifiedName~ScienceJobTests|FullyQualifiedName~ReactionJobTests|FullyQualifiedName~ProductionCatalogTests|FullyQualifiedName~MissileFittingTests|FullyQualifiedName~AmmunitionTests)&FullyQualifiedName!~BotsMineAndHaulMercoxitToTheMinerTwoFactoryAcrossRestarts
```

The Mercoxit replay was excluded after it timed out at the same asteroid belt step on both this branch and
unmodified `be9e571f`. The focused cases exercise representative items and activity handoffs. Catalog-wide
production pacing and supply availability on a dedicated server remain unmeasured.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

Follow a funded order for a Tech II product from the factory to market settlement. Inspect the destination depot
after a courier restocks a fleet that recently took losses.

## Regional procurement for bot corporations

This section records the initial deployment. [Current fleet supply rules](#catalog-driven-fleet-supply-and-production)

An eligible corporate hauler checks its headquarters reserve while docked. A corporation flying a Merlin or
recovering one from its current capsule's loss record targets one packaged hull, two 75mm Gatling Rail Is and
400 Iron Charge S. Other fleets with compatible guns target 200 charges.
Existing corporate stock, open headquarters bids and the corporation's own production reduce demand. A corporation
dispatches one supply trip at a time. Established factory and direct ammunition supply runs keep priority.

The hauler looks up to three jumps away for a market station in another region. An eligible station has a relevant
sell listing or hosts a bot manufacturer with a usable blueprint type for the item. The planner considers the courier's
available cargo volume and excludes avoided systems. It prefers the station that can supply the most requested types,
then the shortest route. Each trip can spend up to half the available corporate treasury, including broker fees, at
a limit of twice the reference price. The courier checks its budget and listing slots again on arrival and uses an
affordable local quote when the full requested quantity is on the book.

The courier posts ordinary seven-day station bids and waits up to six hours at the supplier station. Escrow and
broker fees debit the buyer's corporation. The order IDs and cutoff checkpoint with the trip. When the orders fill
or the cutoff passes, the courier cancels its unfilled bids, releases their escrow, and moves only the filled units
from the station hangar into its hold. That cargo uses the usual freight loss and checkpoint rules. It enters the
corporation's headquarters Market Deliveries only when the courier arrives. Existing resupply and ship recovery can
draw from that reserve. The courier waits ten minutes after the trip before reviewing the reserve again.

A funded ammunition bid uses the same production margin check as a hull or module bid. The factory compares net
sales proceeds with purchased or owned inputs and the manufacturing charge. Different operators can install jobs for
different products while pending output covers its product at that station. Stock and sell listings are counted at
the current station. While a Merlin kit bid is open, factories defer speculative ammo batches and prioritize paid
orders for the hull, modules and charges.

New bot-founded corporations use a 10% bounty tax in their charter. Eligible bounty payments credit the master
wallet through the existing corporation tax settlement. Saved corporations use the tax rate in their charter. The
buyer commits only ISK it already holds and can repeat the trip while its income covers restocking. The supplier
earns the net proceeds when its products sell, after native taxes and broker charges.

CCP's market rules describe funded bids with station range and local delivery. Eden reuses its resident corporate
market purchases for this bot workflow. The EVE.js build 3396210 reference records wallet-owner transactions in
`server/src/services/market/marketProxyService.js` (SHA-256
`F457CB1B181CEB570A5FA346C718D71A7CE7B84B8734C88CEE7F2546A8CE175B`).
[CCP Buy and Sell Orders](https://support.eveonline.com/hc/en-us/articles/203218932-Buy-and-Sell-Orders) ·
[UniWiki Trading](https://wiki.eveuniversity.org/Trading)

The EVE.js build 3396210 reference locks in corporate tax at NPC bounty settlement in
`server/src/services/bounty/bountyRuntime.js` (SHA-256
`978BB72212F51718AB43D4DDF786F4A00EF3B961BA6A5A693A0C8C8F73E678D0`).
[CCP Corporation Wallet and Corporation Taxes](https://support.eveonline.com/hc/en-us/articles/203209582-Corporation-Wallet-and-Corporation-Taxes) ·
[CCP Broker Fee and Sales Tax](https://support.eveonline.com/hc/en-us/articles/203218962-Broker-Fee-and-Sales-Tax)

### Automated validation

`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerAmmunitionSupplyTests.BotFundsRegionalKitOrdersAndHaulsManufacturedStockAcrossRestarts`
passed two cases in 14.55 seconds. The fixture rejects an unaffordable trip and an avoided route. It starts with
supplier blueprints and materials, and the courier posts all three buy orders through its normal docked routine.
Docked manufacturing fulfills the orders without development stock. The full-fill case checks one complete kit.
The partial case ships one Merlin and verifies the remaining escrow refunds at the persisted cutoff. Both cases
reopen with the bids active and again with the cargo in flight. They verify the wallet balance, paid market fills,
unrelated personal stock, and the final station and corporate owner of each delivered stack.

The broader selection had 180 passes and one failure in 5 minutes 53 seconds. The filter used
`FullyQualifiedName~.<Class>.` for these classes:

```text
CapsuleerAmmunitionSupplyTests, CapsuleerAllianceTests, CapsuleerHaulerTests, CapsuleerMinerTests,
CapsuleerSupplyTests, FreightTests, RegionalMarketTests, PersonalMarketTests, RemoteMarketTests,
StarterProductionTests, ResidentEconomyTests, ApplyFittingTests, MoonExtractionTests,
CapsuleerInboundRuntimeTests, PilotFleetTests, CapsuleerResidentRoutineTests
```

`RegionalMarketTests.FailedPaymentRollsBackStockGoodsAndHistoryAndRetrySettlesOnce` failed during fixture cleanup
with `world.sqlite.owner` still locked. The exact method reproduced the same failure on `main` at `4794edc0`.

The solution built with zero warnings and errors in 1.02 seconds. Both Python policy suites passed all 17 cases.
Style, CSharpier, line-width and diff checks passed.

#### Recurring paid trade after attrition

`RegionalTradePaysForTwoLossesWithBountyTaxAndProductionMargin` exercises two complete trips from the same saved
corporations. The buyer begins with an empty treasury. The fixture settles a controlled NPC bounty through the native
combat payout, then destroys the patrol's only Merlin. A Tayra posts three corporate bids at the supplier's regional
market. Two operators install the hull and ammunition jobs concurrently, and the factory uses paid mineral purchases
and delivers the railgun job as well. The courier returns the package, modules and charges across the regional gate.
The patrol uses that depot stock to board a newly fitted Merlin. The replay reopens the world between the losses and
again after the second replacement.

The buyer's only ledger credits are bounty taxes. The supplier starts with working capital and buys finite raw inputs
from ordinary sell orders. Sales receipts cover those inputs, manufacturing charges, broker fees and sales tax each
cycle. The test verifies two native losses and bounties, six distinct filled corporate bids with zero remaining
escrow, six paid manufacturing deliveries, and a positive balance increase for each corporation in each cycle.
The fixture sets the bounty amount and input prices to exercise viable economics. Earnings and margins in play
depend on combat income and the local market.

The exact `FullyQualifiedName=` selection passed four cases in 20.21 seconds. It included the recurring trade method,
both cases of `BotFundsRegionalKitOrdersAndHaulsManufacturedStockAcrossRestarts`, and
`LostMerlinBuysPaidReplacementsAndReturnsToDutyAcrossRestarts` in `Eden.Protocol.Tests.Capsuleers.CapsuleerAmmunitionSupplyTests`.
The separate exact `CapsuleerCorporationTests.ExistingSchoolPilotsFoundAndJoinOnceAcrossCheckpointAndRestart`
case passed in 8 seconds and verified the new charter's tax rate after reopen.

The restored solution built with zero warnings and errors in 1.00 second. The focused run passed 146 tests in
129.27 seconds using `FullyQualifiedName~.<Class>.` for these classes:

```text
CapsuleerAmmunitionSupplyTests, CapsuleerCorporationTests, CapsuleerAllianceTests, CapsuleerHaulerTests,
CapsuleerMinerTests, CapsuleerSupplyTests, CorporationWalletTests, FreightTests, PersonalMarketTests,
ResidentEconomyTests, ApplyFittingTests, MoonExtractionTests, CapsuleerResidentRoutineTests, PilotFleetTests,
CapsuleerShipBlueprintTests
```

Both Python policy suites passed all 17 cases. Style, CSharpier, line-width and diff checks passed.

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer checks

None.

## Dynamic paid ship replacement

After a loss, a miner or traffic pilot in a bot-led corporation uses the loss manifest to plan its replacement at the
NPC market station where its capsule docks. The plan uses the catalog hull, fitted slots and rigs, online state, drones
remaining in the bay, and the observed charge types. Known magazines are refilled to catalog capacity. Cargo and stored
ships are separate possessions. Ordinary assembly, fitting, skill, and market checks apply.

The pilot fills shortfalls from local corporate deliveries, affordable local sell quotes and funded buy orders. It
uses an immediate buy when a local quote covers the full missing quantity of a part. The capsule's routine checkpoint
holds a 30-minute wall-clock deadline and the order IDs for that recovery. Older checkpoints can recover matching bids
for the saved fit or the previous fixed Merlin kit. An older save without a deadline uses the earliest recovered bid's
issue time as the start of the window. At the first recovery action after the deadline, the bot cancels its outstanding
replacement bids and releases their remaining escrow. Orders for other work stay open. Ordinary broker fees are spent
when the orders are placed.

After the refund, the pilot takes a complete kit it can cover with station inventory, corporate deliveries and live sell
orders. It considers the original fit, the original hull with high-slot modules and drones, and a basic role fit
(Venture with a mining laser, Badger, or Merlin with railguns). The current pilot must have the required skills.
Purchases and boarding for a complete in-stock kit commit together. If a complete fit is unavailable or unaffordable
at that station, the pilot checks again after five simulated minutes. That loss stays on the stock-check path.

UniWiki describes recovering a fitting from a loss record. For the EVE.js build 3396210 reference, the ship destruction
snapshot also distinguishes fitted flags from cargo (`server/src/space/shipDestruction.js`, SHA-256
`23AA50F04175DA023DE9FB84B67E8E2E724AF1648F3AC23E14E07E9825CA4970`).
[UniWiki Killmail](https://wiki.eveuniversity.org/Killmail) · [UniWiki Fitting ships](https://wiki.eveuniversity.org/Fitting_ships)

### Automated validation

The Merlin fixture recreates the recorded railgun fit, loads both magazines and returns the pilot to flight after
reopening the paid kit without another charge. The Venture fixture restarts with two outstanding bids, checks the
saved deadline, verifies both escrow refunds, and restores a mining pilot with the one-laser fit that can be
purchased locally. The corporate wallet ends at the starting amount minus the original fees and actual fallback
purchases. The canceled bids stay closed.

Before reconciliation, the exact `LostMerlinBuysOneReplacementAndReturnsToDutyAcrossRestart` and
`StalledVentureBidsRefundAfterRestartAndMinerUndocksWithAnInStockFit` filters passed during development.
The solution built with zero warnings and errors in 7.69 seconds. The final focused run passed 138 tests in 61.75
seconds. It selected the following classes using `FullyQualifiedName~.<Class>.`:

```text
CapsuleerAmmunitionSupplyTests, CapsuleerHaulerTests, CapsuleerMinerTests, CapsuleerMiningFleetTests,
CapsuleerSupplyTests, CapsuleerFitRetentionTests, CapsuleerSchedulingTests, CapsuleerResidentRoutineTests,
PersonalMarketTests, StarterMarketTests, ApplyFittingTests, MultifitTests, PilotFleetTests, CorvetteRecoveryTests
```

The two Python policy suites passed all 17 cases. Style, CSharpier and line-width verification passed.

#### Milestone 3 reconciliation

The exact filter selected the two recovery methods above, `BotsBuildAndCommissionTheirMoonRefineryAcrossRestarts`, and
both cases of `FundedAllianceStagesOneHubAcrossRestartForAnUnobservedClaimableSystem`. All five passed in 19.21 seconds.
The Venture fixture verified that the persisted recovery bids refunded their escrow while the same pilot's hub bid
stayed open and fully funded. After claiming its first system, the alliance lost its Squall, bought the hull and
Entosis Link at the local sell quotes, and fitted and boarded the replacement. The test checked the exact wallet debit,
absence of duplicated hub or fuel, then exercised paid hub and command-node defense, reagent delivery and alliance dues
through reopen. The refinery fixture exercised freight, deployment, service and fuel commissioning through reopen.

The restored solution built with zero warnings and errors in 0.75 seconds. The final focused run passed **351 tests**
in 176.87 seconds. The filter used `FullyQualifiedName~Eden.Protocol.Tests.Sovereignty.` and
`FullyQualifiedName~.<Class>.` for these classes:

```text
CapsuleerAmmunitionSupplyTests, CapsuleerAllianceTests, CapsuleerHaulerTests, CapsuleerMinerTests,
CapsuleerMiningFleetTests, CapsuleerInboundRuntimeTests, CapsuleerSupplyTests, CapsuleerFitRetentionTests,
CapsuleerSchedulingTests, CapsuleerResidentRoutineTests, PersonalMarketTests, ResidentEconomyTests,
StarterMarketTests, ApplyFittingTests, MultifitTests, PilotFleetTests, CorvetteRecoveryTests,
MoonExtractionTests, MoonMiningRulesTests, AllianceBillingTests
```

Both Python policy suites passed all 17 cases. Style, CSharpier and line-width verification passed. The .NET run was
scoped to affected features and immediate dependencies.

#### Repeated losses and refinery freight

Each loss starts recovery from the newly destroyed hull's fit and a fresh set of market orders. The Merlin fixture
fills the first replacement from paid manufacturing, station stock and market orders. It then destroys the first
replacement, reopens the world with the second escrow still active, fills fresh orders and verifies the second ready
ship after another reopen. It checks both fit and magazine loads, two loss reports, six filled bids with zero escrow,
and the exact wallet debits and broker fees.

The refinery fixture exercises an uninterrupted deployment and two courier losses with freight survival set to 0% and
100%. After reopen it checks the lost Athanor's original stack, drop status and wreck custody. The factory starts a
new paid job while the old courier checkpoint still describes the destroyed hull. The courier buys a Tayra, loads a
newly manufactured package with a different item ID, and commissions one refinery. The original package is accounted
for once in the loss. Surviving freight stays in the wreck until it is looted or expires.

The exact selection passed eight cases in 28.65 seconds. It used `FullyQualifiedName=` for these methods under
`Eden.Protocol.Tests.Capsuleers`:

```text
CapsuleerAmmunitionSupplyTests.LostMerlinBuysPaidReplacementsAndReturnsToDutyAcrossRestarts
CapsuleerAmmunitionSupplyTests.BotsBuildAndCommissionTheirMoonRefineryAcrossRestarts
CapsuleerAmmunitionSupplyTests.StalledVentureBidsRefundAfterRestartAndMinerUndocksWithAnInStockFit
FreightTests.HaulerWreckRetainsTheScannableStacksAcrossCheckpoint
```

The solution built with zero warnings and errors in 0.70 seconds. The broader run had 331 passes and one failure in
3 minutes 50 seconds. It selected `FullyQualifiedName~Eden.Protocol.Tests.Loot.` and `FullyQualifiedName~.<Class>.` for:

```text
CapsuleerAmmunitionSupplyTests, CapsuleerAllianceTests, CapsuleerHaulerTests, CapsuleerMinerTests,
CapsuleerSupplyTests, CapsuleerCorporationTests, FreightTests, MoonExtractionTests, EntosisCaptureTests,
SovereigntyDeploymentTests, PersonalMarketTests, ResidentEconomyTests, ApplyFittingTests, PilotFleetTests,
NpcCombatTests, PilotTurretTests, CapsuleCombatTests, InsuranceLossTests, ShipStorageTests, ShipRigTests,
CourierContractTests
```

`CapsuleCombatTests.FailedCapsuleSettlementRollsBackBodyTrainingHealthPaymentAndNotifications` failed at the saved
training-queue assertion in its resident case. An isolated run of that method on `main` at `ba4e8ee4` reproduced the
same failure. The file-backed case passed on both revisions. Capsule respawn takes a separate loss path from hull and
freight destruction.

Both Python policy suites passed all 17 cases. Style, CSharpier, line-width and diff checks passed.

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer check

Observe a miner undock with a basic Venture after its original kit's bids are canceled.

## Autonomous Miner II production

At an NPC station with market, laboratory and manufacturing services, a miner in a bot-led corporation can buy the
Miner I original, the required datacores and the materials for up to five Miner II modules. It can reuse available
inventory. The planner checks the corporation's funds, market quantities and projected proceeds before starting.
The starting estimate includes the cost of failed invention attempts. The existing bot skill provisioning
qualifies the operator for the catalog recipe when the funded plan starts.

The bot installs an ordinary one-run copy job, then runs invention for a licensed Miner II copy with the normal chance roll.
It takes the completed copies from corporate Market Deliveries and uses the invented copy for manufacturing. The
original and unused licensed runs stay available to the operator. Market purchases and job installation settle in
the same resident action, with the master wallet paying the market and industry charges. The corporation stocks
one batch at a time and lists the resulting modules on the public station market. It can plan a batch from the
reference sell price even without an open buy order.

Industry jobs already checkpoint the reserved blueprint, completion time and invention outcome. The factory
advances the saved job on its docked routine, including at a factory with no usable asteroid belt. A failed
invention spends its inputs and starts a one-hour wall-clock retry delay, recorded by the delivered job's
timestamp.

The fixed product, batch size and retry delay are Eden policy. The pinned build 3662168 catalog supplies the input
quantities, skills, licensed runs and success chance. The local build 3396210 EVE.js
`server/src/services/industry/industryRuntimeState.js` snapshot distinguishes copying from invention and records the
successful runs before delivery (SHA-256 `BF6AB290BD622ED029FD8ED6E2214B4604FF874F6BECD585842FFE4EAC01D63C`).
[CCP Invention](https://support.eveonline.com/hc/en-us/articles/203210642-Invention) ·
[CCP Copying](https://support.eveonline.com/hc/en-us/articles/203210602-Copying) ·
[UniWiki Invention](https://wiki.eveuniversity.org/Invention)

### Automated validation

The factory fixture starts with player sell orders and no corporate funding, then funds the bot and exercises
procurement, copying, a failed invention, the paid retry, manufacturing and a public sale. It reopens the world
with each job installed and compares the job ID, deadline, outcome and owner before continuing. It checks the
remaining licensed runs, the unique wallet entry for each paid job and delivery of all five modules to the buyer.

The exact `MinerTwoFactoryBuysCopiesInventsManufacturesAndSellsAcrossRestarts` filter passed in 8.38 seconds.
The solution built with zero warnings and errors in 7.43 seconds. The affected run passed 202 tests in 91.90 seconds
with these class filters using `FullyQualifiedName~.<Class>.`:

```text
CapsuleerAmmunitionSupplyTests, CapsuleerMinerTests, CapsuleerHaulerTests, CapsuleerMiningFleetTests,
PilotAwarenessTests, IndustryTests, ScienceRulesTests, ScienceJobTests, BulkIndustryDeliveryTests,
IndustryContainerTests, IndustryFacilityLookupTests, ProductionCatalogTests, ReactionJobTests,
PersonalMarketTests, CorporationWalletTests, ResidentBotEconomyTests
```

Both Python policy suites passed all 17 cases. Style, CSharpier, line-width and diff checks passed.

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer check

Stock the required inputs at a bot factory with laboratory service and buy its finished Miner II modules.

## Miner II datacores from R&D agents

A Miner II factory requests datacore deliveries when station offers cannot cover the invention recipe. A corporate
hauler checks the catalog for accessible R&D agents who offer the requested research fields. It visits the agents,
qualifies for the fields through the existing bot skill provisioning and starts up to two ordinary research projects.
The hauler can take other work while points accrue by wall-clock time. Eden limits research supply routes to twelve
gates per leg and uses the normal Omega, standings and docking checks.

Once an agent has enough points, the hauler returns and redeems only the outstanding amount it can afford and carry.
Each datacore consumes 100 points and costs 10,000 ISK from the corporate wallet. The loaded cargo, wallet entries,
project balances and delivery route use the same resident checkpoint. Normal local and remote market supply still
works when offers appear. The pinned catalog maps Laser Physics and Mechanical Engineering to the corresponding
datacore types and supplies both invention quantities.

The local EVE.js build 3396210 `server/src/services/agent/researchRuntime.js` reference accrues points and redeems
them at the research agent (SHA-256 `D6E8E398BB3FF9BA1F0711BA86BA1DCCA4707CD935D287C9760A8A8BDD0C6EE8`).
[CCP Datacores](https://support.eveonline.com/hc/en-us/articles/203210652-Datacores) ·
[UniWiki Datacore farming](https://wiki.eveuniversity.org/Datacore_Farming)

The wider [build 3662168 supply audit](../tech-two-supply-audit.json) identifies the remaining market dependencies.
Independent Miner II production still needs bots to harvest fractured moon ore and ice, refine them and run reaction
jobs for the advanced components. [Bot moon refineries](#bot-moon-refineries) build and start the extraction facility.
Reusable originals and reaction formulae also need a reliable initial source. [Mercoxit expeditions](#mercoxit-mining-and-factory-freight)
supply the ore for Morphite, [planetary colonies](#bot-planetary-production-and-factory-freight) supply Mechanical Parts,
and [component and refinery jobs](#miner-ii-components-from-owned-materials) process materials at the factory.
[UniWiki Minerals](https://wiki.eveuniversity.org/Minerals) · [UniWiki Reactions](https://wiki.eveuniversity.org/Reactions)

### Automated validation

`MissingMinerTwoDatacoresComeFromPaidResearchAndSurviveCargoRestart` covers the real factory shortage, courier
dispatch and arrival, and both research projects without market offers or testing grants. It reopens the world
before redemption and again with paid cargo in the hold, then checks both types and quantities in the factory
hangar and verifies one corporate charge per redemption.

The exact filter passed in 9.92 seconds. The solution built in 7.29 seconds with zero warnings and errors. The
affected run passed all 121 tests in 83.40 seconds with these class filters using `FullyQualifiedName~.<Class>.`:

```text
ResearchAgentTests, AgentShowInfoTests, CapsuleerAmmunitionSupplyTests, CapsuleerHaulerTests, CapsuleerMinerTests,
ScienceJobTests, ResidentBotEconomyTests, PersonalMarketTests, CorporationWalletTests
```

Both Python policy suites passed all 17 cases. Style, CSharpier, line-width and diff checks passed.

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer checks

None.

## Miner II components from owned materials

When station offers cannot fill a Miner II material shortfall, the factory checks its own ore first and can reprocess
it through the station service. It can also make a missing component with a catalog manufacturing blueprint and
the inputs already in its hangar. The factory can buy a blueprint locally when one is available. The corporation
pays the manufacturing fee and the job delivers to the operator's hangar for the next step in the chain. Finished
Miner II modules still enter Market Deliveries for public sale.

The build 3662168 catalog gives Mercoxit 140 units of base Morphite per 100 ore, before applying reprocessing yield
and taxes. The component planner uses the same material calculations and restrictions as normal manufacturing. The
shared resident checkpoint records the manufacturing job and its output location. See the
[existing industry protocol notes](industry.md) for the pinned-client and EVE.js evidence on manufacturing jobs.

### Automated validation

`MinerTwoFactoryRefinesOreAndMakesItsOwnMinerOneInputsAcrossRestart` starts without Morphite or Miner I sell orders.
It verifies the factory reprocesses Mercoxit into Morphite, reserves minerals for a Miner I job, reopens the running
job and consumes the privately delivered Miner I in Miner II assembly. It checks the corporate charges and confirms
the intermediate product stays off the public market.

The exact filter passed in 8.44 seconds. The solution built in 7.89 seconds with zero warnings and errors. The
affected run passed all 153 tests in 82.05 seconds with these class filters using `FullyQualifiedName~.<Class>.`:

```text
CapsuleerAmmunitionSupplyTests, CapsuleerMinerTests, CapsuleerHaulerTests, ResidentBotEconomyTests,
IndustryTests, ScienceJobTests, IndustryContainerTests, BulkIndustryDeliveryTests, ReactionJobTests,
ReprocessingTests, CorporationWalletTests, ResearchAgentTests
```

Both Python policy suites passed all 17 cases. Style, CSharpier, line-width and diff checks passed.

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer checks

None.

## Mercoxit mining and factory freight

The build 3662168 catalog has Mercoxit belts in 368 systems. Those systems have no NPC stations, so an eligible
founder Venture stages at a market station and takes reciprocal gates to and from a nullsec belt within eight jumps.
The specialist uses a Deep Core Mining Laser I and the ordinary mining cycle, fittings, ore hold and defenses. Existing
and new founder Ventures can receive a single starter laser while on their original hull. Subsequent trips use the owned
fit. As with other mining in Eden, deep-core pollution clouds are outside the native simulation's current support.

Before departing, the specialist checks the destination's catalog belts against the resident asteroid index and its
crew's belt memory. A previously populated belt must still contain Mercoxit, and a recently stripped or threatened
belt is skipped while the corresponding memory is active. The specialist can try local work until a destination belt
is eligible. An unpopulated belt can still generate its deposits on arrival.

In a read-only host checkpoint spot sample on November 23, 2026, running revision `2fefe2156`, six of 20 queued
gate-transfer jobs belonged to Venture pilots. All six had the Mining career and an active Mercoxit expedition.
The sample describes the queued jobs at that checkpoint. It provides no count of completed jumps or repeated crossings.

The miner unloads at the staging station and lists one reprocessing portion from its mined inventory. The miner pays
the normal broker fee and can post a lower ask if its wallet cannot fund the reference price. While that listing is
open it can resume ordinary mining. A Miner II factory's Morphite shortfall can trigger a normal corporate purchase
of raw Mercoxit at a nearby station, or a courier trip to a supplier within 20 gates. The hauler loads what fits, delivers
to the factory's hangar and can make repeat trips when one load cannot cover a whole portion. It turns around promptly
at supply stops and retains the existing threat logic. The factory uses the normal reprocessing service.

The miner's route, paid cycle and current system are checkpointed with resident ship and asteroid state. Courier cargo
and market wallet entries use the same resident checkpoint as the shipment. The pinned catalog supplies belt composition,
the launch module and the reprocessing portion. The local EVE.js build 3396210 `server/src/space/asteroids/asteroidService.js`
reference identifies the same three grades (SHA-256 `8CA75BC0E2CA005F2E719D7ED9CEC1C0051460E1F1C6BC06110E645C699CF6B6`).
[UniWiki Mining](https://wiki.eveuniversity.org/Mining#Mercoxit_mining) ·
[CCP Introduction to Mining](https://support.eveonline.com/hc/en-us/articles/23655785488668-Introduction-to-Mining)

### Automated validation

`VentureLaunchesForMercoxitOnlyWhenSavedDepositsContainOre` runs the docked founder's departure with a viable and
a depleted resident belt. The viable case starts its gate leg, while the depleted case skips the Mercoxit trip.
Both cases and the existing restart and freight replay passed together in 19.55 seconds including the build.

`BotsMineAndHaulMercoxitToTheMinerTwoFactoryAcrossRestarts` starts with no Mercoxit or Morphite sellers, reopens the
world with the miner operating in a stationless system and again after a hauler buys the first half of a refining
portion. It checks the native asteroid loses ore, the miner uses a single deep-core tool, the only seller is the bot,
and the two partial shipments are paid for once each and refined into Morphite at the factory.

The exact filter passed in 11.36 seconds. The final solution built in 11.45 seconds with zero warnings and errors.
The final affected run passed all 127 selected tests in 82.14 seconds with these class filters using
`FullyQualifiedName~.<Class>.`:

```text
CapsuleerAmmunitionSupplyTests, CapsuleerMinerTests, CapsuleerMiningFleetTests, CapsuleerHaulerTests,
CapsuleerProvisioningTests, CapsuleerGatePerformanceTests, CapsuleerDeadlinePerformanceTests,
ResidentBotEconomyTests, MiningTierTests, ReprocessingTests, ResearchAgentTests
```

The initial run included both cases of `ResupplyStopsInspectingHangarAfterStockFillsTheRequirement`. Each counted
61 SQL commands against an expectation of 60. Both cases reproduced on main at `017dab25` and were excluded by
method name from the final affected run. Both Python policy suites passed all 17 cases. Style, line-width and diff
checks passed.

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer checks

None.

## Bot planetary production and factory freight

When a bot factory is short of a planetary commodity, a corporate hauler can make it from the catalog's extraction
and processing dependencies. The planner works backward from the requested quantity and consumes stock already
held at the factory's station before scheduling more extraction. It uses the native schematics up through P4, the
current world multipliers and the processor types allowed on each planet. A raw leaf must be native to the selected
planet. Sites can be up to eight jumps from the factory and must have an NPC market station in their system. The
planner can reuse a compatible owned colony or open another within the pilot's normal colony limit.

The pilot carries the correct command center and deploys it using ordinary planetary edits. The corporation pays
for the infrastructure, upgrades and customs. A published command center can be purchased from the staging station
at its reference price when an order cannot fill it. The pilot creates or reuses a launchpad, an extractor or compatible
processor and the links and routes for the selected step. It flies to the customs office to import intermediates and
export products. The native colony simulation advances during the resulting wall-clock wait, and the hauler can do
other work between visits. The sale book remains an alternative source. Raw and intermediate stock comes back to the
hauler's hangar in the factory station, and finished materials go into the manufacturer's hangar.

Mechanical Parts first applies this path to Miner II. Its two raw inputs come from Base Metals and Noble Metals.
The catalog schedules Reactive Metals, Precious Metals and finally schematic 73 for Mechanical Parts. The local
EVE.js build 3396210 `server/src/services/planet/planetRuntimeStore.js` reference models installed extractor programs,
schematic cycles, route delivery and customs transfers (SHA-256 `CB74256D55A9ADD0FA5FEDD022362118A1FE0D67B27CAAFF302F30A7A8CE45DA`).
[UniWiki Planetary Commodities](https://wiki.eveuniversity.org/Planetary_Commodities) ·
[CCP Planetary Interaction](https://support.eveonline.com/hc/en-us/articles/203269871-Planetary-Interaction)

### Automated validation

`BotProductionPlansEveryCatalogCommodityFromExtraction` walks all 68 production schematics and 15 raw resources
from empty inventories and checks every simulated step consumes available ingredients. The runtime check
`BotsProduceAndHaulPlanetaryInputsAcrossRestarts` covers two factory shortfalls. Mechanical Parts starts without any
planetary stock or sell offers and verifies the full chain from extraction to Miner II assembly. Broadcast Node verifies
P4 processing from supplied P3 intermediates using a high-tech production plant. Both cases reopen the world during
production and with exported cargo in flight. They check the saved pin and deadline, one command center purchase,
customs charges and consumption of the delivered input by the factory.

The exact filters passed all three cases in 14.15 seconds. The solution built with zero warnings and errors in
4.79 seconds. The affected run passed all 190 tests in 99.67 seconds using `FullyQualifiedName~.<Class>.` for:

```text
CapsuleerAmmunitionSupplyTests, CapsuleerHaulerTests, CapsuleerMinerTests, ResidentBotEconomyTests,
PlanetaryRulesTests, PlanetaryIndustryTests, PlanetaryProtocolTests, PlanetaryConfigurationTests,
CustomsConfigurationTests, PlanetaryMarketTests, PlanetarySkillExtractionTests, CorporationWalletTests,
ResearchAgentTests
```

Both Python policy suites passed all 17 cases. Style, CSharpier, line-width and diff checks passed.

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer checks

None.

## Bot moon refineries

When a Miner II factory runs short of Photon Microprocessors or Laser Focusing Crystals, it can build an Athanor.
The planner traces the pinned component and reaction recipes back to moon ores. It prefers free moons whose composition
contains more of the needed ores, then shorter routes and stable moon IDs. The staging system must have an NPC station.
The route can run up to eight gates from the factory and the site must permit the composite reactor.

The factory installs paid manufacturing jobs for the Athanor, Standup Moon Drill I, Standup Reprocessing Facility I
and Standup Composite Reactor I. It uses owned stock and blueprints, and can request any missing originals, raw
materials and manufactured parts through existing corporate freight. It can buy the quantum core and bootstrap fuel
from sell orders, and use a fuel blueprint when it has the inputs. A corporate courier that can fit the cargo carries
the equipment to the site. A Tayra courier can qualify for the Caldari Hauler cargo bonus and stows existing cargo
in its own station hangar when needed. The bot-led corporation assigns Station Manager authority through its ordinary role store.
The courier uses the native deployment, anchoring, core, fitting and fuel rules. It stocks fuel through the automatic
fracture deadline before starting a six-day pull. The saved job, structure and courier shipment each retain their
existing ownership and deadlines across restart.

If a courier is destroyed, the loss manifest records the original cargo. Tracked refinery cargo uses the configured
freight survival rate for the active hull, including replacement hulls. Surviving cargo keeps its item ID and is left
in the pilot's wreck under the native loot rules. A destroyed stack leaves the inventory. A factory or dispatcher
counts a saved shipment only while its hull and corporation still match the courier. The factory can procure a new
shipment without waiting for the capsule to dock. [Repeated-loss validation](#repeated-losses-and-refinery-freight) ·
[CCP container and wreck ownership](https://support.eveonline.com/hc/en-us/articles/115000597345-Container-and-Wreck-Ownership)

The planner maintains one refinery per corporation. It needs a suitable unoccupied moon, an available hauler with
enough cargo space, construction originals and materials, a quantum core, and starter fuel. Mining the fractured field
and ice belts, making fuel from mined ice, and running reaction jobs for the factory remain separate work.

The local EVE.js build 3396210 `server/src/services/structure/structureAutoState.js` reference preserves the anchoring
state and its deadline (SHA-256 `6F40FB6650BA30F70CF837C822A9DE18CD822295EABA94C95F37DBFF716EDED6`).
[CCP Moon Mining](https://support.eveonline.com/hc/en-us/articles/115005404229-Moon-Mining) ·
[CCP Reactions](https://support.eveonline.com/hc/en-us/articles/115005405785-Reactions) ·
[UniWiki Reactions](https://wiki.eveuniversity.org/Reactions)

### Automated validation

`BotsBuildAndCommissionTheirMoonRefineryAcrossRestarts` passed in 14.79 seconds. It uses the pinned structure and Tayra
definitions with short fixture manufacturing recipes. It starts with construction materials, blueprint originals, a
core and fuel, and reopens the world once during hull manufacturing and again while the courier is in flight. It
checks five delivered jobs and their individual corporate charges, one deployed refinery with three online services,
one extraction, and one automatically fractured asteroid field. The solution built with zero warnings and errors in
10.36 seconds. All 145 affected tests passed in 91.27 seconds using `FullyQualifiedName~.<Class>.` for:

```text
CapsuleerAmmunitionSupplyTests, CapsuleerHaulerTests, CapsuleerMinerTests, CapsuleerCorporationTests,
ResidentBotEconomyTests, MoonExtractionTests, MoonMiningRulesTests, MoonMiningWarpTests, StructureTests,
StructureControlTests, StructureFuelRulesTests, ReactionJobTests, CorporationRoleTests, CorporationWalletTests,
PlanetaryIndustryTests
```

Both Python policy suites passed all 17 cases. Style, CSharpier, line-width and diff checks passed.

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer checks

None.

## Paid Merlin replacement

A bot pilot in a bot-led corporation can replace a lost Merlin after its capsule reaches an NPC market station.
The loss record must identify the docked capsule and the destroyed Merlin. The replacement uses two 75mm Gatling
Rail Is and a load of Iron Charge S for each. The buyer uses its own station inventory and corporate Market Deliveries
first. For any shortfall, it places a station buy order for up to seven days, at a limit of twice the reference sell
price. The master wallet must cover the escrow and broker fee. The ordinary market lets players and other corporations
fill these orders, and a local factory can act on the Merlin demand through its existing manufacturing checks.

The docked job leaves an open bid in place while waiting for a fill. When the kit is ready, it refunds any remaining
open bid, assembles the owned hull, fits the modules, boards and loads ammo. Purchases commit on an earlier docked job
or in the seller's market action. On restart, the same capsule, open orders and delivered hangar items establish what
is still needed. Boarding consumes the kit, removes the capsule and commits the new active ship with the bot's routine
under the resident owner. Orders and inventory transfers are local to the recovery station.

The kit, bid limit and seven-day duration are Eden policy. Market custody and fitting follow the usual station rules
in [UniWiki Trading](https://wiki.eveuniversity.org/Trading) and
[UniWiki Fitting ships](https://wiki.eveuniversity.org/Fitting_ships). The local EVE.js build 3396210 snapshot
`server/src/services/ship/shipService.js` was checked for assembly and boarding
(SHA-256 `C34C3573C11CBAECA041D2CDC4791E19DB7DB6759A94F73125AD71E6B62848F1`).
The implementation reuses Eden's resident market, assembly and fitting operations.

### Automated validation

The integration fixture settles a real Merlin loss, docks the capsule, and verifies that an empty wallet leaves
the buyer waiting. After funding, it uses one corporate spare turret and creates one bid for each shortfall. A player
fills the turret and ammunition bids and a separate bot corporation manufactures and sells the hull. It checkpoints
the paid kit before boarding. After restart, the patrol pilot boards and undocks with two loaded turrets. The buyer's
order IDs, wallet entry IDs and balance agree across the restart.

The exact `LostMerlinBuysOneReplacementAndReturnsToDutyAcrossRestart` filter passed. The final isolated solution
build passed with zero warnings and errors in 10.42 seconds. The combined filter passed 122 tests in 49.47 seconds.
It selected the following classes using `FullyQualifiedName~.<Class>.`:

```text
CapsuleerAmmunitionSupplyTests, CapsuleerHaulerTests, CapsuleerSupplyTests, CapsuleerFitRetentionTests,
CapsuleerSchedulingTests, CapsuleerResidentRoutineTests, PersonalMarketTests, StarterMarketTests,
MultifitTests, PilotFleetTests, CorvetteRecoveryTests
```

Both Python policy suites passed all 17 cases. Style, CSharpier and line-width checks passed. An earlier build stopped at a Windows test-host
DLL lock before running tests. The final build and tests used a separate artifacts directory.

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer check

Fill a replacement bid at the recovery station and observe the patrol pilot undock in the fitted Merlin.

## Capsuleer mining fleets

Resident population preparation keeps the saved Venture in slot zero. For a high-security system with supported
ore and a market route, the local roster adds a seeded choice of Covetor, Retriever or Procurer. At three usable
belts it adds a seeded Hulk, Mackinaw or Skiff, with a barge as the catalog fallback for that slot. Fits use the
ordinary ship and Strip Miner I skill requirements, fitting checks, capacitor payment, extraction and hold limits.
Crew membership uses the saved community roster and each miner's existing checkpoint. The first visit after
upgrade prepares missing ships one founder at a time. The saved ship, job and assets of existing miners carry forward.

Miners rendezvous at an occupied safe belt, prefer unclaimed deposits there, and target at 80% of the shorter of their
effective laser reach and lock range. They avoid belts reported stripped or threatened by their crew and abort a paid
cycle when a crew warning calls for retreat. The scheduler uses local roster lookups and committed work records.
Ore stays in the pilot's hold until the pilot docks and unloads it. The operation uses existing ship, module and
movement presentation. It creates an autonomous crew with individual jobs and ore holds.

Build 3662168 supplies the hull and strip miner definitions. EVE University describes mixed crews and their mining
hull roles. The local evejs NPC reference uses shared field state to coordinate mining and responses to depletion,
in `server/src/services/mining/miningNpcOperations.js` (SHA-256 `5ce327785991ee6ffb18b523e4af1862cccc93e2be6e13e09bdefc2855aff4cd`).
The crew sizes and threat sharing are Eden policy. [Mining](https://wiki.eveuniversity.org/Mining) ·
[ORE ship and skill overview](https://wiki.eveuniversity.org/ORE_Basic_Ship_and_Skill_Overview)

**Automated validation**

The exact case was `CapsuleerMiningFleetTests.ExistingMinerGainsStripMiningCrewThatSharesABeltAndRespondsTogetherAcrossRestart`.
It exercised saved Venture enrollment, barge and exhumer fits, ore collection from distinct rocks in the same belt,
checkpoint reload, and retreat after one member reports aggression. The milestone filter used OR across the following
class names with `FullyQualifiedName~`. It passed 162 cases in 71 seconds. The solution built in 8 seconds with zero
warnings. Catalog policy passed 8 checks, formatting policy passed 9, and formatting verification passed.
The exact case passed again in 9 seconds after its upgrade fixture switched to resident setup.

```text
CapsuleerMiningFleetTests, CapsuleerMinerTests, PilotAwarenessTests, CapsuleerProvisioningTests,
CapsuleerBackgroundPreparationTests, CapsuleerShipBlueprintTests, CapsuleerProvisioningScaleTests,
CapsuleerDeadlineTests, CapsuleerDeadlinePerformanceTests, CapsuleerEncounterTests, CapsuleerTierTests,
CapsuleerDefenseLifecycleTests, Eden.Protocol.Tests.Mining.MiningTests, Eden.Protocol.Tests.Mining.MiningRulesTests
```

**Live acceptance**

Not performed. Agent client testing is disabled.

**Optional maintainer checks**

Watch a Venture and barge work separate rocks in the same belt and verify their beams render on the correct targets.

## Rival gang interception at gates

In the build 3662168 replay, the first raider scouted the right gate while its damage wing was still
patrolling the other side. Defenders already on the reported grid used the ordinary 18–40 second stop
and started leaving before that wing could engage. A separate replay sent two roaming gangs through
opposite ends of the same gate. Raiders moving under their arrival cloak could see rivals who could
not see them.

Defenders now hold their assigned grid for the two-minute gang rendezvous window, subject to the
operation deadline. A hunting wing skips or ends an idle outbound gate patrol when its gang has a
fresh sighting at that gate's paired exit. The pilots still travel through the gate, acquire a visible
target and spend their own ammunition. A newly arrived ship loses its gate cloak when it executes
a movement order or begins acquiring a target. An idle arrival can remain cloaked.

The encounter tests start the two fleets at different stations. One scenario supplies a witnessed
incursion to the defenders. In the other, each side launches its own raid and discovers its opponent
in flight. The snapshot-enabled cases use imported Merlin pilots with railguns, Iron Charge S and
warp scramblers. Each replay checks both damage attribution and magazine consumption for each side,
with no human observer in the engagement system. A separate server-side observer test sees a hauler
hidden on arrival and present in the gate observer's peer list within 20 simulated seconds.
The movement check also covers a bot resuming a journey after an unwitnessed gate transfer.

The sighting and support model follows [EVE University scouting](https://wiki.eveuniversity.org/Scouting).
The local evejs NPC reference checks grid visibility in `server/src/space/npc/npcBehaviorLoop.js`
(SHA-256 `21ad46ab54d10507b6811c2ff14a49b0e0181d5aa38a1cccfcee0b97f6da843b`).
Gang rendezvous timing is an Eden gameplay choice.

**Automated validation.** The initial reproduction used
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.RegionalConflictWorldTests.RivalDefenseEngagesAVisibleRaidWithOwnedWeaponsAndRealDamage`.
The wider filter joined the following class names using `FullyQualifiedName~` and OR:

```text
RegionalConflictWorldTests, RegionalConflictTests, RoamingGangTests, PatrolHullTests, PatrolCatalogTests,
PatrolTests, PvpTacticsTests, GateCampTests, CapsuleerHaulerTests, CapsuleerInboundRuntimeTests,
CapsuleerDefenseTests, CapsuleerDefenseLifecycleTests, CapsuleerDefensePerformanceTests, CapsuleerDefenseModuleTests,
CapsuleerDefenseFrameTests, TrafficSnapshotTests, CapsuleerAllianceTests, GateCloakTests, GateTravelRulesTests,
CapsuleerTrafficNavigationTests, CapsuleerHandoffTests
```

It also included both cases of `ReceivingOnlyObserversSeeRealInboundTransfersAndRepeatedArrivalAtTheWatchedGate`.
The installed catalog and matching traffic snapshot were supplied to the catalog-wide fit audit.
The final run passed 310 of 311 cases in 297.96 seconds including runner startup. All three interception
cases passed. The remaining failure, `RestartRetainsPaidWorkAndJobsButRebuildsObserverModesFromSelections`,
also failed in isolation on unchanged main `89c1f625` (7.65 seconds). On both revisions the expected
miner job resumed alongside a hauler arrival job. The solution built with zero warnings or errors,
both Python policy suites passed (8 catalog and 9 formatting checks), and formatting verification passed.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** From a nullsec gate, watch a newly arrived gang member appear in the overview
as it moves and confirm its hull renders when it engages.

## Combined factory pickups

A hauler can collect several missing materials from the same supplier station. Supplier selection favors the station
that can fill the most distinct shortages in one load. Ties favor more units, fewer jumps, then a lower quoted cost.
The buyer rechecks factory stock, local offers, supplier orders, corporate funds and cargo on arrival. It fills smaller
loads first and uses any remaining space and budget for the rest. A shortage can be partially filled.

The hauler checks the whole purchase against one cargo allowance and one corporate balance. The resident market action
pays for the materials and puts them into real cargo in the same transaction, with one manifest stack per material
even when several orders supply it. Delivery uses the checkpointed manifest. Existing saved routes still identify the
supplier and factory, and paid shipments preserve their saved cargo.

Packing follows the existing cargo capacity rules. EVE University describes the role of hold space and station-to-station
travel in hauling. The existing market custody reference is recorded below. [EVE University Hauling](https://wiki.eveuniversity.org/Hauling)

### Automated validation

The factory supply integration test covers a single material, two materials on one trip, and a hold where either material
fits on its own and the combined load requires a partial purchase. It checks corporate spending, seller order quantities,
multiple fills for the same material, restart recovery, and the remaining factory shortage.

The exact filter
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerAmmunitionSupplyTests.HaulsOnlyTheRemoteShortfallAndResumesDeliveryAfterRestart`
passed all three cases in 14 seconds. The final isolated run on top of `249446e6` passed 87 tests in 42 seconds with
`FullyQualifiedName~` filters for `CapsuleerAmmunitionSupplyTests`, `CapsuleerHaulerTests`, `CapsuleerInboundRuntimeTests`, `CapsuleerMinerTests`,
`PersonalMarketTests` and `Eden.Protocol.Tests.Industry.IndustryTests`. The build completed with zero warnings or errors.
Tool and solution restore, 17 Python policy tests, formatting, documentation links and diff whitespace checks passed.

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer checks

List two missing ingredients at the same nearby station and verify both sale transactions come from one pickup.

## Nearby procurement for bot factories

A factory turns a material shortage into a supply request once it has checked its own hangar, suitable ore and local sell
offers. Demand-driven jobs still require an owned blueprint or a local original listing. Their remote purchase ceilings
account for projected proceeds, local expenses and corporate funds. The existing Iron Charge S stocking run can also
request nearby materials when it has an original at the factory.

A hauling corpmate searches open player and NPC orders at stations reachable within three gate jumps of both the hauler
and the factory. It checks the known threat systems. Before purchasing, the courier rechecks the factory's stock, local
offers and any supporting buy order. It then buys from the current supplier book using the corporation wallet and the
ship's free cargo space. Subsequent work extended the original single-material dispatch policy.
[Combined factory pickups](#combined-factory-pickups)

The factory request is stored in the existing miner checkpoint and the assignment, cargo and route are stored in the
hauler checkpoint. Each corporation sends one supply courier at a time. The hauler deposits surviving cargo into the
manufacturer's factory hangar, or corporate Market Deliveries if that manufacturer has left the corporation. After
delivery, the next factory check combines the shipment with local inputs and installs an ordinary industry job when the
batch is ready. The existing market and flight services handle order matching, gate travel and docking.

The hauling policy uses ordinary station trading and physical cargo described by EVE University. The existing industry
and market custody references are listed in the manufacturing and ammunition records below.
[EVE University Hauling](https://wiki.eveuniversity.org/Hauling)

### Automated validation

The integration fixture uses the pinned 150mm Railgun I recipe with a partial sell order at the factory and the balance
one gate away. It verifies local restock precedence, route avoidance, spending from the treasury, actual cargo custody,
checkpoint recovery, duplicate delivery protection and installation using the delivered and local materials. The final exact
filter passed in 6 seconds:

`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerAmmunitionSupplyTests.HaulsOnlyTheRemoteShortfallAndResumesDeliveryAfterRestart`

The final isolated run passed 85 tests in 45 seconds with `FullyQualifiedName~` filters for
`CapsuleerAmmunitionSupplyTests`, `CapsuleerHaulerTests`, `CapsuleerInboundRuntimeTests`, `CapsuleerMinerTests`,
`PersonalMarketTests` and `Eden.Protocol.Tests.Industry.IndustryTests`. The solution build completed in 12 seconds with
zero warnings or errors. Tool and solution restore, 17 Python policy tests, formatting, documentation links and diff
whitespace checks passed.

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer checks

List a missing input at a neighboring station, place a buy order for the finished item at the factory and verify the
supplier sale and subsequent finished-item delivery.

## Demand-driven bot manufacturing

At an NPC station with factory and market services, a miner in a bot-led corporation considers open local buy orders from
other pilots. The product-to-recipe lookup comes from the published build 3662168 manufacturing catalog. It uses the same
facility restrictions and required skills as player jobs. The miner can qualify at Industry I, use an owned original or
licensed copy, buy an original locally, and source missing inputs from ordinary local sell orders. It can also refine
owned ore that yields a missing ingredient while working from an owned blueprint.

The planner tries up to five runs per job, capped by the buy order's quantity, recipe timing and any copy license. It
checks that projected proceeds after broker fees and tax exceed the estimated cost of the job, owned inputs and purchases,
including the cost of any blueprint it needs to buy. The corporation pays for purchases and installation. An operator can
cover a treasury shortfall from its own ISK, using the existing contribution cap. The resident transaction settles purchases,
moves goods into the operator's manufacturing hangar, and consumes job inputs together. A corporation runs one supply job at
a time and skips products it already has in local deliveries or on the market.

The regular docked routine delivers finished jobs into Market Deliveries. It lists finished stock at a current bid price
when available or at a catalog-backed reference price. The ordinary market handles escrow, matching, taxes and fees. Iron
Charge S also keeps its established supply route and can be produced for stocking while local demand offers no eligible job.
[Autonomous Miner II production](#autonomous-miner-ii-production) adds copying and invention for that chain. Nearby
procurement extends the general manufacturing workflow.
[Nearby procurement for bot factories](#nearby-procurement-for-bot-factories)

EVE University describes the blueprint, material and skill prerequisites. The feature reuses Eden's station manufacturing
lifecycle. The EVE.js reference for industry install and wallet settlement is `industryInstallSettlementState.js` at SHA-256
`8bfe741f7afce165241becc22549118cf95633bc36cb58099968193d1d2ee3dd`. The existing market and industry wire records cover
the client calls used here. [EVE University manufacturing](https://wiki.eveuniversity.org/Manufacturing) ·
[Eden station manufacturing](industry.md)

### Automated validation

The exact filter
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerAmmunitionSupplyTests.LocalDemandFundsCatalogManufacturingAndDeliveryAfterRestart`
passed both cases in 10 seconds. The tests use the pinned 150mm Railgun I and Merlin recipes, exercise local blueprint
and materials purchases, enforce limited copy runs, and reopen jobs before delivery. They verify the buyer's order
and inventory, corporate ledger and retained blueprint.

The broader development run passed 140 tests in 6 minutes 11 seconds before the final blueprint run-limit adjustment and
funding regression assertions. It used `FullyQualifiedName~` filters for `CapsuleerAmmunitionSupplyTests`, `CapsuleerMinerTests`,
`CapsuleerHaulerTests`, `CapsuleerInboundRuntimeTests`, `Eden.Protocol.Tests.Industry.IndustryTests`,
`StarterProductionTests`, `ReprocessingTests`, `PersonalMarketTests`, `StandardMarketTests`,
`MarketReadCompatibilityTests` and `CorporationWalletTests`.

The final focused run passed 107 tests in 5 minutes 18 seconds with the same class filters apart from
`CapsuleerMinerTests`, `CapsuleerHaulerTests` and `CapsuleerInboundRuntimeTests`. It verifies that a corporation with
insufficient funds leaves the input orders untouched. Tool and solution restore passed, and the build completed in 14
seconds with zero warnings or errors. Both Python policy suites passed all 17 cases. Formatting, documentation links and
diff whitespace checks passed.

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer checks

Place a buy order at a station where a funded bot corporation has factory access and local inputs. Verify the resulting
purchase in the wallet and inventory after the job time has elapsed.

## Corporation ammunition supply

World schema 189 adds optional corporation ownership to industry output and sell orders. Existing personal records retain
their owner and behavior. The first chain uses **Iron Charge S (215)** and **Iron Charge S Blueprint (1130)**. A five-run
batch follows the pinned catalog, using 135 Tritanium and 105 Pyerite at base material efficiency and producing 500 rounds.
Industry I is granted as the bot's manufacturing qualification. The miner provides actual refined or market-bought materials
and a purchased original through its personal hangar. The corporation pays manufacturing and broker costs, owns the output
and receives net sale proceeds. Treasury shortfalls can be funded from the assigned pilot's existing wallet, capped at
a quarter of the available personal balance per expense. Both sides record the contribution.

A manufacturer with stock can list one outstanding seven-day ask. Public buyers use the existing order book and matching
rules. Authorized corporate financial staff can read the open corporate offers through `GetCorporationOrders`. Its order rows
send the corporation owner in `charID` with `isCorp` set. On expiration, unsold escrow returns to corporate Market Deliveries. Shared market execution, industry delivery and
station service checks remain in their owning World features.

A separate purchasing corporation with a compatible ship and fewer than 100 reserve rounds seeks a supplier within three
gate jumps. One courier at a time travels from the headquarters to the supplier. It purchases at most 200 rounds from the
specified finite sell order at the supplier station. Billing and cargo loading share a resident action. The rounds travel
in real cargo, and only surviving units are credited to the buyer's headquarters deliveries. The saved route, order ID and
cargo manifest resume after a checkpoint. Purchased ammunition uses separate custody from generated traffic manifests.
The courier can finish through unobserved systems while anyone is playing. Compatible combat bots draw from local stock
at their normal docked resupply step, and existing fitting and ammunition operations handle cargo and magazine loading.

Sources include [EVE University manufacturing](https://wiki.eveuniversity.org/Production), the pinned build 3662168 recipe and
[station manufacturing record](industry.md), and the local EVE.js industry and market custody references.
`industryInstallSettlementState.js` SHA-256 is `8bfe741f7afce165241becc22549118cf95633bc36cb58099968193d1d2ee3dd`.
`marketItemCustody.js` SHA-256 is `3f3ce4dfd28289f8a08b078ea55799435d86608408507331b74632ec426d72a6`.
`marketProxyService.js` SHA-256 is `f457cb1b181ceb570a5fa346c718d71a7ce7b84b8734c88cee7f2546a8ce175b`.
Batch size, the reserve threshold, route length, the first recipe and corporate spending are Eden policy.

### Automated validation

The supply-chain fixture opens a schema 188 save, buys a blueprint through the public market, reprocesses Scordite and
delivers a timed manufacturing job. It checks the public offer and corporate order visibility, sells to a player and a
corporation, verifies the treasury entries, and checkpoints the pilot with paid cargo aboard. A reopened scheduler
returns the cargo through a gate, rejects duplicate delivery, and feeds the reserve into an empty compatible railgun.

- Exact method filter `FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerAmmunitionSupplyTests.MinedInputsBecomePublicOrdersAndPaidCourierStockSurvivesRestart`
  passed in 9 seconds before the final corporate order wire change.
- The final solution build passed in 9 seconds. The exact method plus the `StandardMarketTests`, `PersonalMarketTests`
  and `MarketReadCompatibilityTests` class filters passed all 47 tests in 18 seconds, including the corporate order row.
- The broader run took 7 minutes 51 seconds before the wire change and passed 429 of 431 tests. It used
  `FullyQualifiedName~` filters for `CapsuleerAmmunitionSupplyTests`, `CapsuleerCorporationTests`, `CapsuleerHaulerTests`,
  `CapsuleerInboundRuntimeTests`, `CapsuleerDefenseLifecycleTests`, `StarterProductionTests`, `IndustryTests`,
  `ReprocessingTests`, `Eden.Protocol.Tests.Market`, `CorporationWalletTests`, `CorporationFinancialPanelTests`
  and `WorldMigrationTests`. The existing rollback failures in `RegionalMarketTests` and `NpcRestingOrderTests`
  reproduced on both the final code and baseline `53a2b94b`.
- The catalog and formatting policy suites passed all 17 Python tests. Formatting and the diff whitespace check passed.

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer check

In the client, find a bot-made Iron Charge S ask and verify the resulting trade in the corporation wallet.

## Bot corporation identities

New charters generate a name and ticker from the saved seed and founder, with independent palette selection.
The generator draws compound names, named companies, cooperatives, and two-word names. Role and system go in
the description. Founding checks names and tickers against the existing resident and catalog owner namespaces.
The charter stores the accepted candidate, so changing headquarters leaves the identity intact.

Existing charters keep their names. A signed-in administrator can preview one charter with
`GET /api/admin/population/corporations/{id}/name`. The preview requires an exact match against the legacy generator,
a current bot CEO, and zero human members. To confirm, send the preview as JSON to
`POST /api/admin/population/corporations/name` with the administrator antiforgery token. Eden rechecks eligibility,
the old name, and the proposed name in the resident transaction. The corporation ID and ticker stay the same.
The name update is published through the existing owner-directory revision and saved in the next world checkpoint.
[Recruitment, relocation, and identity plan](../bot-corporation-plan.md#1-give-new-corporations-distinct-identities)

**Automated validation.** The baseline founding checkpoint replay passed. The fixed-seed 500-founder sample produced
500 distinct accepted names and tickers after collision retries. The legacy replay checked stale and successful
confirmation, immediate directory reads, and checkpoint reopen. The focused filter
`FullyQualifiedName~CapsuleerCorporationTests|FullyQualifiedName~PlayerCorporationTests|FullyQualifiedName~CorporationHeadquartersTests|FullyQualifiedName~AdministrationInteractionTests`
passed 26 tests in 18 seconds. The solution built in 3.61 seconds. Both Python policy suites and format verification passed.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** None.

## Autonomous corporation recruitment

World schema 192 checkpoints each pilot's recruitment deadline, profession and current autonomous application separately
from its immutable provisioning identity. The due docked role owns the review. The existing resident scheduling queue
admits miners and haulers when any human has selected a pilot, including work outside observed systems. Admission and
ordinary service requests no longer recruit bots directly.

The pilot checks reachable charters near the current dock and paid ads in the same region, with a bounded 64-system search
and a seven-jump limit. Vacant profession slots, useful station services, tax, travel danger and a stable pilot preference
produce the candidate score. The recruiter rechecks its authority and available role before offering. Acceptance separately
checks capacity, departure permissions and current work, and uses the ordinary membership transition and notification path.
Invitations from humans use that same review and departure logic. Bot CEOs buy ordinary ads from their treasury when there
are roster openings and funds.
[EVE University recruitment](https://wiki.eveuniversity.org/Recruiting_corporation_members) ·
[Native invitation contract](corporations.md#direct-invitations-and-corporation-window-reads-november-14-2026)

**Automated validation.** The paid-ad replay held applications from two different two-jump origins across a checkpoint.
Two offers competed for the final berth and only one took it. The native invitation replay preserved a deferred offer
and published the eventual employment change. The focused filter
`FullyQualifiedName~CapsuleerCorporationTests|FullyQualifiedName~CorporationMembershipTests|FullyQualifiedName~CorporationRecruitmentTests|FullyQualifiedName~CapsuleerBackgroundPreparationTests|FullyQualifiedName~WorldMigrationTests`
passed 64 tests in 30 seconds. The solution built in 3.77 seconds and both Python policy suites passed.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Check a bot corporation's recruitment history after several active-play reviews.

## Bot corporation relocation

Acceptance can commit a relocation to the headquarters or to a mining station in its system. A miner whose new
corporation lacks a workable local belt stays where it can work. The existing pilot and fitted ship travel using the
regular undock, warp, gate and dock operations. The pilot packs up to three compatible stacks of personal supplies into
the existing hold. Capacity limits leave excess supplies at the source. The freight gate and existing assignment checks
protect cargo and paid work from premature departures.

The corporation assignment, ship, route and packed item quantities are checkpointed with the pilot. The operating station
changes only at the destination dock. The miner starts work with the destination crew, and the hauler plans its regular
journeys from the new base. The original system and provisioning slot still identify the same saved pilot. The medical
home station and corporate property use their existing rules. [Relocation plan](../bot-corporation-plan.md#3-move-recruits-and-rebase-their-work) ·
[Eden home-station mechanics](characters.md#persistent-home-stations-and-docked-clone-transfer)

**Automated validation.** The miner and hauler replays each reloaded an unobserved departure from a checkpoint, kept
the same ship and moved 13 rounds from the old hangar to the new one. The active clock paused with no selected human,
and the same pilot started its next cycle from the new base. The focused filter
`FullyQualifiedName~CapsuleerCorporationTests|FullyQualifiedName~CapsuleerHandoffTests|FullyQualifiedName~CapsuleerTrafficNavigationTests|FullyQualifiedName~CapsuleerMinerTests|FullyQualifiedName~CapsuleerHaulerTests`
passed 65 tests in 32 seconds. The solution built in 0.74 seconds with warm build outputs. Both Python policy suites
passed. The paid mining checkpoint test also failed on unmodified `3229a079`, where the helper compared the starter ore
type array by reference. It now compares its contents and still checks the remaining saved state.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Follow a recruit through a gate, then observe its next work cycle at the new base.

## Bot corporation retention

Members of bot-led corporations need eight active-play hours of tenure and at least 30 active minutes of continued
interest before pursuing a better employer. The new offer must improve on their existing work base and tax by at least
eight points on the recruitment score. Ordinary role permissions, existing assignments, and last combat staffing in an
alliance member corporation can defer autonomous departures. A human-led corporation continues to control its roster.

An intact delivery through the regional escort operation can record a working contact with another corporation. The
contact expires after 24 active hours. It can bring a charter into the search and lead to an ordinary invitation from
its authorized recruiter. Autonomous applications and invitations expire after two active hours. A committed headquarters
change reopens the work-base decision and schedules one member at a time. The usual movement and custody rules apply.

An administrator can read `GET /api/admin/population/corporations/{id}/roster` for the charter's current member count,
the number and proportion of bots by provisioning origin, pending applications, and the number of bots moving or based
at a chosen work station. The repeat-move total counts relocations after each current member's first one. The report
uses a resident roster snapshot. [Retention plan](../bot-corporation-plan.md#4-add-retention-later-moves-and-population-checks)

**Automated validation.** One paid-ad fixture hired from three provisioning systems. The largest origin supplied two
of four members, and two recruits were scheduled to move. The legacy one-jump policy excluded both two-jump hires in
this topology. The retention replay checked the tenure threshold, a checkpoint during consideration, an escort-contact
invitation, and a later headquarters move. Its warmed employer review opened no SQL connections, issued no SQL commands,
and performed no fitting calculations. The initial integration filter passed 77 tests in 44 seconds, including paid trade,
alliances, escorted freight, checkpoint migrations and the matched session-frame workload. Build took 21.27 seconds and
both Python policy suites passed.

The merge with the Athanor production work on `6f62966d` passed 75 focused tests in 73 seconds. The filter combined
`CapsuleerCorporationTests`, `CapsuleerAllianceTests`, `CorporationMembershipTests`, `CorporationRecruitmentTests`,
`AdministrationInteractionTests`, `CapsuleerHandoffTests`, `WorldMigrationTests` and `AreaWeaponPersistenceTests`
with `EscortFollowsOwnedFreightAndCompletesOnlyAfterItsActualDelivery`, `RegionalTradePaysForTwoLossesWithBountyTaxAndProductionMargin`,
`UnobservedMinerAndPaidFactoryPauseAndResumeAcrossRestart` and `BotsDeliverTwoMinerTwoBatchesAcrossRestartsAndReplaceBootstrapReactionFuel`.
The solution built in 20.90 seconds. Both Python policy suites and format verification passed.

`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerDeadlinePerformanceTests.MatchedMaintenanceFramesPreserveJobsAndAssetsWithFewerIdleTurns`
passed three cases in nine seconds on the `3229a079` baseline and on the integrated tree. Both revisions ran ten due
turns per case. SQL commands, connections and fitting calculations matched for each case. The table compares total
measured session-frame time and checkpoint phases in local fixtures. Longer dedicated-server capacity and roster pacing
remain unmeasured.

| Observation | Session frames, ms | Allocation, bytes | SQL commands / connections / fits | Checkpoint capture / project / write, ms |
| --- | --- | --- | --- | --- |
| One distant observer | 57.79 → 65.43 | 17,956,328 → 18,151,648 | 31 / 2 / 8 | 0.50 / 0.94 / 16.08 → 0.52 / 0.98 / 16.01 |
| One local observer | 27.61 → 21.66 | 10,210,200 → 10,040,816 | 1 / 1 / 0 | 0.76 / 0.89 / 16.57 → 0.67 / 0.84 / 18.77 |
| Two local observers | 36.65 → 38.14 | 16,533,648 → 16,656,984 | 1 / 1 / 0 | 0.56 / 0.93 / 15.47 → 0.49 / 0.87 / 18.45 |

The final integration with star-map statistics on `71af02a0` passed 58 tests in 48 seconds. The filter combined
`CapsuleerCorporationTests`, `MapNavigationTests`, `WorldMigrationTests`, `CapsuleerHandoffTests`, and `CapsuleerHaulerTests`
with the two-batch Athanor replay and the three matched session-frame cases. Build took 20.88 seconds. Both Python policy
suites, format verification and the documentation link check passed.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Read a working corporation's roster report after a longer dedicated-server session.

## Bot corporation recruitment and gangs

Schema 188 opens applications for existing active bot-led charters. New bot charters start open. The bot CEO uses the existing
Personnel Manager authority and member limit to issue an offer after a valid player application. Application and offer events
share a transaction. The player accepts separately through `UpdateApplicationOffer`. After a human takes over as CEO,
the corporation follows its ordinary application settings and the human reviews new requests.

Native `InsertInvitation` checks the selected recruiter as usual. Bots settle invites in ID order when docked at an NPC station,
using their saved home system and neighboring gate destinations to decide whether the destination headquarters is suitable.
Suspension, active gang duties, regional escorts and freight still in transit defer the decision. An ordinary member can leave a bot-led
charter under existing role and CEO restrictions. A human-led employer keeps its bot members. Acceptance uses the normal
application transition, preserving the member's ship and property and publishing membership, employment and recruitment events.
A pending invitation survives a checkpoint and gets another evaluation during community preparation or docked pilot work.

Gangs and escorts use corporation and alliance affiliation for recruitment. Constellation grouping still guides regional
objectives and cooperation between bots in NPC corporations. Gate camps verify that participants can fly together.
Active gangs recheck surviving members and the escorted pilot, clear shared scouting reports when affiliations split,
and publish a return-home order through their saved command. Cached patrol targets are rechecked against committed
affiliations before the next attack decision. Existing ship and crimewatch checks still apply.

The recruitment flow follows [EVE University recruiting](https://wiki.eveuniversity.org/Recruiting_corporation_members),
the [pinned build 3662168 invitation record](corporations.md#direct-invitations-and-corporation-window-reads-november-14-2026)
and the local EVE.js `corpApplicationNotifications.js` reference
(SHA-256 `452c8bd61246b728c76ec8bdb50c2912da3c1059933dd12aee706eb2825b6599`).
The one-jump recruitment area and assignment checks are Eden policy.

**Automated validation.** The first exact filter was `FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerCorporationTests.NativeApplicationAndDeferredInvitationUseOrdinaryMembershipHistory`.
The native application test checked the player-controlled acceptance step and observed recruitment notifications. It also checked
schema-187 migration, checkpoint reopen, a pending invite in flight and at the wrong station, and acceptance at the delivery
station. The active-gang test excluded a rival at formation, changed a gang member's corporation and verified the shared
return order on both pilots' saved commands.

The broader filter `FullyQualifiedName~CapsuleerCorporationTests|FullyQualifiedName~CapsuleerBackgroundPreparationTests|FullyQualifiedName~CapsuleerPopulationRetryTests|FullyQualifiedName~PlayerCorporationTests|FullyQualifiedName~CorporationMembershipTests|FullyQualifiedName~CorporationLeadershipTests|FullyQualifiedName~CorporationRoleTests|FullyQualifiedName~CorporationClosureTests|FullyQualifiedName~RoamingGangTests|FullyQualifiedName~RegionalConflict|FullyQualifiedName~GateCampTests|FullyQualifiedName~PatrolTests|FullyQualifiedName~CapsuleerDefenseTests|FullyQualifiedName~CapsuleerDefenseLifecycleTests|FullyQualifiedName~CapsuleerDefensePerformanceTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~WorldMigrationTests` passed 164 of 165 cases in 139.13 seconds before the final freight and gang-return adjustments.
`RegionalConflictWorldTests.RivalDefenseEngagesAVisibleRaidWithOwnedWeaponsAndRealDamage` failed on its targeting assertion.
The exact same assertion failed on unmodified milestone 1 commit `50d73b0a` in 9.99 seconds.

The final filter `FullyQualifiedName~CapsuleerCorporationTests|FullyQualifiedName~CorporationMembershipTests|FullyQualifiedName~RoamingGangTests|FullyQualifiedName~GateCampTests|FullyQualifiedName~PatrolTests` passed **45 of 45** in **21.92 seconds**. Tool and solution restore passed, and the
solution built with zero warnings and errors in **7.61 seconds**. Both Python policy suites and formatting verification passed.
The full .NET suite and dedicated server deployment were outside this validation.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Apply to a bot corporation and accept the offer in the native corporation window.
Invite a docked bot near your corporation headquarters and verify the status and member list refresh.

## Bot-led player corporations

On November 18, 2026, Eden added automatic founding and enrollment to existing and newly provisioned capsuleer communities.
World schema 187 adds a nullable founding-system ID to the resident corporation record and its checkpoint projection.
Ordinary player corporations retain a null marker. Headquarters moves preserve the original system's allowance history.
Closure retains the archived charter. The population worker visits six saved agent IDs per page even when the provisioning
fingerprint is current. A miner or hauler can also enroll during a dock, departure or docked retry.

Only unsuspended simulated pilots still in their school corporation with no previous player-corporation employment qualify.
Home communities within one gate jump share available bot-led corporations. A founder docks at a local NPC station
with six qualifying pilots based in range. Recruitment prefers the pilot's home system, then the oldest eligible corporation
by ID, and respects both the 12-member target and the normal member limit. Corporate assets start at zero.
New corporations have a seeded local name, ticker, logo and description. Recruitment applications and friendly fire start disabled.

The initial setup credit, normal fee, trained skill, CEO appointment, shares, inventory owner, employment history and founding
marker share a resident transaction. Later founders in the same system need their own ISK and qualification. A human CEO
takes the corporation out of automatic enrollment. Workers refresh loaded pilot identities and republish the
usual Local and station-guest records after placement. The existing regional operation and sovereignty rules still apply.

Gameplay references include [EVE University corporation formation](https://wiki.eveuniversity.org/Forming_a_corporation)
and the local evejs reference's `server/src/services/corporation/corporationFoundingState.js`
(SHA-256 `0f785493ccbb397c8ced8f5c3bde32c732c6588a70405772fc295023871dc58b`), which records founding and
payment against duplicate requests. Eden reuses its own transaction and the pinned build 3662168 corporation service
contracts documented under [corporations](corporations.md). The roster thresholds and initial funding are Eden population policy.

**Automated validation.** A schema-186 fixture migrated with its existing pilots, ships and jobs. Observers with a cached
school corporation saw the new membership in Local and station guests. Corporation Show Info, directory, member and employment
reads agreed. A checkpoint flush, reopen and repeat placement preserved the same charter and single fee. Focused rejection checks
covered in-space pilots, paused pilots, sparse rosters, a full corporation, a human CEO, insufficient funds or skill, rollback
and a second, self-funded charter with a conflicting ticker. A development case exposed a stale in-space navigation property
inside a transaction. Placement and founding now check the authoritative position row.

The initial exact filter was `FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerCorporationTests.ExistingSchoolPilotsFoundAndJoinOnceAcrossCheckpointAndRestart`.
The broader filter was `FullyQualifiedName~CapsuleerCorporationTests|FullyQualifiedName~CapsuleerBackgroundPreparationTests|FullyQualifiedName~CapsuleerPopulationRetryTests|FullyQualifiedName~CapsuleerProvisioningTests|FullyQualifiedName~ResidentPopulationTests|FullyQualifiedName~CapsuleerPresentationTests|FullyQualifiedName~CapsuleerMinerTests|FullyQualifiedName~CapsuleerHaulerTests|FullyQualifiedName~RoamingGangTests|FullyQualifiedName~PlayerCorporationTests|FullyQualifiedName~CorporationMembershipTests|FullyQualifiedName~CorporationClosureTests|FullyQualifiedName~CorporationShowInfoTests|FullyQualifiedName~CorporationWalletTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~ChatWorldTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~WorldInitializationTests`. It passed **194 of 196** in **135.48 seconds**.
Two one-observer variants of `UnchangedPresentationSharesObservationsAcrossAdvancingFrames` exceeded their 6 MiB
allocation budget. Both also failed on the unmodified parent `b91f2b32`. The empty and occupied cases allocated
6,968,248 and 6,796,136 bytes on the branch and 6,866,208 and 6,865,728 bytes on the parent. They recorded zero
SQL commands, connections, fitting calculations and additional presentation reads. The four-observer case passed on both.

After retaining preparation backoff through corporation placement and adding the cached-observer assertions, the final filter
`FullyQualifiedName~CapsuleerCorporationTests|FullyQualifiedName~CapsuleerBackgroundPreparationTests|FullyQualifiedName~CapsuleerPopulationRetryTests` passed **21 of 21** in **17.54 seconds**. Tool and solution restore succeeded and the solution
built with zero warnings and errors in **8.04 seconds**. Both Python policy suites and formatting verification passed.
Validation used disposable saves. The dedicated server was not deployed or benchmarked.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer check.** In an established populated system, open a bot from Local or station guests and inspect
its corporation logo, CEO and employment history in the native client.

## Catalog-wide frigate through battleship patrols

Build 3662168 has 312 published hulls across the frigate, destroyer, cruiser, battlecruiser and battleship
classes and their specialist variants. Solo patrols and roaming gangs can draw 296 of them, including
faction, pirate, tournament and armed utility hulls. The pool contains 101 frigates, 34 destroyers,
79 cruisers, 36 battlecruisers and 46 battleships. This selection applies when a new combat pilot is
provisioned. Saved pilots keep their ships. A Venture with combat weapons can patrol, while mining lasers
keep a working Venture in its mining role.

Of the eligible non-interdictor combat draws, 40% use the sixteen authored fits, 40% draw evenly from the
280 additional hulls, and 20% take the imported traffic fit or the Merlin fallback. The saved seed makes
each draw repeatable. The expanded fits use the catalog's hardpoints, slots, CPU and powergrid. They load
a weapon and ammunition first, then fit tackle, propulsion, local repair and damage control as space and
resources permit. Attack battlecruisers can use large weapons with medium propulsion. These are Eden patrol
fits. Specialist hulls can fly with ordinary weapons even when their signature modules have no bot behavior.

| Exceptions | Hulls | Reason |
| --- | --- | --- |
| Tactical destroyers (5) | Confessor, Hecate, Jackdaw, Skua, Svipul | Eden's assembly policy excludes these Tech III hulls. |
| Strategic cruisers (4) | Legion, Loki, Proteus, Tengu | Eden's assembly policy excludes these Tech III hulls. Their fittings depend on subsystems. |
| No patrol-supported damage source (7) | Echelon, Zephyr, Monitor, Victorieux Luxury Yacht, Moreau YC128 Campaign Bus, Roden YC128 Campaign Bus, Tenzin YC128 Campaign Bus | The pinned hulls lack both weapon hardpoints and a combat-drone bay. |

Yacht utility high slots do not give these bots a targeted weapon. The patrol controller can fire turrets,
launchers and combat drones. Smartbomb area attacks are handled separately for players.

EVE University's ship and strategic cruiser guides describe the hull classes and subsystem fittings.
Selection and assembly coverage above come from Eden's pinned catalog. The evejs behavior reference is
recorded in the [roaming gang notes](#smarter-pilots-and-persistent-roaming-gangs--november-17-2026).
[Ships](https://wiki.eveuniversity.org/Ships), [Strategic cruisers](https://wiki.eveuniversity.org/Strategic_Cruiser).

With the installed 27,947-profile traffic snapshot available, 32,768 provisioning draws in 1DQ1-A with
default settings and seed 45 yielded all 296 generated hull types alongside imported traffic. The audit
built every fit in an isolated resident world and checked its combat profile, damage source and module
online state. Snapshot-enabled scheduler tests formed a gang of a Venture, Bifrost, Silver Magnate and
Deacon and recovered the same ships and pilots after reopening its checkpoint. Each of the four also
tackled and damaged a simulated player and a rival bot, both as a gang member and on a solo patrol.
Two regional raids also verified that the gang acted on its scout's sighting, tackled each target and
dealt damage.

**Automated validation.** The 250 focused tests passed in 4 minutes 44 seconds including runner setup.
The solution built with zero warnings and errors in 3.25 seconds. Tool and solution restore, both Python
policy suites (8 catalog and 9 formatting cases), formatting verification and diff checks passed.
The wider filter added `PatrolCatalogTests`, `CapsuleerDefenseLifecycleTests` and `TrafficActivityTests`
to the fourteen classes in the [destroyer validation record](#destroyers-for-solo-patrols-and-gangs).
Set `EDEN_ENCOUNTER_CATALOG` to the installed catalog path with a matching sibling `traffic.sqlite` to
exercise the catalog-wide check. The exact reduced-catalog regression filter was
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.RoamingGangTests.StationFormsAndLaunchesThreeShipsWithOneFittedTackler`.
The separate `RivalDefenseEngagesAVisibleRaidWithOwnedWeaponsAndRealDamage` case failed on both the task
branch and unchanged main `c2d8ae2c`. The independently routed fleets acquired no target within 350 ticks.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Observe a newly provisioned nullsec patrol in the overview to confirm
native hull appearance.

## Battlecruisers and battleships for solo patrols and gangs

On November 18, 2026, two Tech I battlecruisers and two Tech I battleships joined the generated PvP fits for
build 3662168. The four carry Damage Control I. The battlecruisers use 10MN Afterburner I and the battleships
use 100MN Afterburner I.

| Hull | Weapons and initial load per weapon | Tackle | Local repair |
| --- | --- | --- | --- |
| Drake | 6 heavy missile launchers, 30 Scourge Heavy Missiles each | Warp disruptor | Large shield booster |
| Hurricane | 6 220mm Vulcan autocannons, 40 EMP M each | Scrambler | Medium armor repairer |
| Raven | 6 cruise missile launchers, 20 Scourge Cruise Missiles each | Warp disruptor | Large shield booster |
| Megathron | 7 electron blaster cannons, 40 Antimatter Charge L each | Scrambler | Large armor repairer |

New eligible non-interdictor combat pilots draw from sixteen fitted hulls and four snapshot or Merlin outcomes.
Each new hull receives 5% of those draws. A missing hull or fitting component takes the existing fallback.
These odds and fits are Eden population choices. Weapon sizes, hardpoints, fitting costs and magazine capacities
come from the pinned catalog. The weapon families follow the hulls' combat roles. [Drake](https://wiki.eveuniversity.org/Drake),
[Hurricane](https://wiki.eveuniversity.org/Hurricane), [Raven](https://wiki.eveuniversity.org/Raven) and
[Megathron](https://wiki.eveuniversity.org/Megathron). The evejs NPC behavior reference is recorded in the
[roaming gang notes](#smarter-pilots-and-persistent-roaming-gangs--november-17-2026).

A read-only sample used default settings, seed 45 and the installed 27,947-profile snapshot in 1DQ1-A. Its
first 512 slots contained three of the four generated hulls. Across 1,024 slots it selected 17 Drakes,
22 Hurricanes, 14 Ravens, two Megathrons and imported pilots. All four generated fits passed the traffic fit
validator against the installed catalog in an isolated world.

With a snapshot of imported haulers, the resident population scheduler recruited all four heavy hulls into one
gang. It retained their ships and roles after a checkpoint restart. Each hull applied its own tackle, spent
loaded ammunition and dealt damage against a simulated player and a rival bot at 5 km in a Moa. With gangs
disabled, each hull also undocked, acquired and tackled both targets on solo patrol and dealt damage. Two
regional raid replays exercised scout reporting and gang tackle, ammunition use and damage against both targets.

**Automated validation.** All 218 focused tests passed in 2 minutes 54 seconds. The solution built in
7.53 seconds with zero warnings or errors. Tool and solution restore and both Python policy suites
(8 catalog cases and 9 formatting cases) passed. The first exact development filter was
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.PatrolHullTests.SnapshotAndSpecialistGangCoexistAndKeepTheirOriginalHullsOnReopen`.
The wider run used the fourteen capsuleer test classes listed in the [destroyer validation record](#destroyers-for-solo-patrols-and-gangs)
and excluded the documented baseline failure `RivalDefenseEngagesAVisibleRaidWithOwnedWeaponsAndRealDamage`.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Observe a newly populated nullsec gang to check battlecruiser and battleship
visuals in the overview.

## Cruisers for solo patrols and gangs

On November 18, 2026, Eden added four fitted Tech I cruisers to solo patrols and roaming gangs.
Each carries a 10MN Afterburner I and Damage Control I. The pinned build 3662168 catalog supplies the fitting
requirements, skills, magazine capacities and module attributes.

| Hull | Fitted weapons and ammunition | Tackle | Local repair |
| --- | --- | --- | --- |
| Caracal | 5 rapid light missile launchers, 19 Scourge Light Missiles each | Warp disruptor | Medium shield booster |
| Stabber | 4 dual 180mm autocannons, 40 EMP M each | Scrambler | Medium shield booster |
| Moa | 5 dual 150mm railguns, 40 Iron Charge M each | Warp disruptor | Medium shield booster |
| Thorax | 5 heavy electron blasters, 40 Antimatter Charge M each | Scrambler | Medium armor repairer |

The four previously repeated frigate draws now yield cruisers. The sixteen seeded outcomes allocate four
each to Tech II frigates, destroyers, cruisers, and snapshot selection or the generated Merlin fallback.
This split applies to new combat slots after the security, generated-combat and interdictor checks.
A missing fit component uses the existing fallback. The roles follow the weapon families described by
EVE University. [Caracal](https://wiki.eveuniversity.org/Caracal), [Stabber](https://wiki.eveuniversity.org/Stabber),
[Moa](https://wiki.eveuniversity.org/Moa) and [Thorax](https://wiki.eveuniversity.org/Thorax).
The evejs NPC behavior loop reference is recorded in the [roaming gang notes](#smarter-pilots-and-persistent-roaming-gangs--november-17-2026).

A read-only sample of 512 slots in 1DQ1-A used seed 45, default settings, the pinned catalog and the installed
27,947-profile traffic snapshot. It selected each generated cruiser and imported pilots. All four cruiser fits
passed the traffic fit validator in an isolated world. In the resident population tests, four cruisers formed
a gang alongside imported traffic and retained their ships and roles after a checkpoint restart.

Each hull tackled a simulated player and a rival bot and dealt damage in both solo and gang tests. The solo
encounters used a frigate 5 km away and gave the rival time on grid for a cruiser lock. At 1 km the rail Moa had
declined a crossing frigate on tracking grounds. In the regional raid tests, a scout reported the target and
the gang applied tackle, consumed ammunition from fitted weapons and damaged the target.

**Automated validation.** All 199 focused tests passed in 2 minutes 35 seconds. The solution built in
4.88 seconds with zero warnings or errors. Tool and solution restore and both Python policy suites
(8 catalog cases and 9 formatting cases) passed. The first exact development filter was
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.PatrolHullTests.SnapshotAndSpecialistGangCoexistAndKeepTheirOriginalHullsOnReopen`.
The wider run used the fourteen capsuleer test classes and the exclusion listed in the
[destroyer validation record](#destroyers-for-solo-patrols-and-gangs).
`RivalDefenseEngagesAVisibleRaidWithOwnedWeaponsAndRealDamage` remains a documented baseline failure for an
independently routed encounter.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Observe a newly populated nullsec patrol in the overview to check cruiser hull
and weapon visuals.

## Destroyers for solo patrols and gangs

On November 18, 2026, four Tech I destroyers joined the generated PvP fits for build 3662168.
Each has a 1MN afterburner and damage control. Weapon count, magazine contents, tackle and tank vary by hull.

| Hull | Weapons and loaded charge | Tackle | Local repair |
| --- | --- | --- | --- |
| Catalyst | 8 light electron blasters with Antimatter Charge S | Scrambler | Small armor repairer |
| Cormorant | 6 125mm railguns with Iron Charge S | Warp disruptor | Small shield booster |
| Thrasher | 7 125mm gatling autocannons with EMP S | Scrambler | Small shield booster |
| Corax | 5 light missile launchers with Scourge Light Missiles | Warp disruptor | Small shield booster |

Eligible non-interdictor combat slots have sixteen seeded outcomes. Eight retain their previous Tech II frigate
assignments, four select a destroyer, and four use snapshot selection or the generated Merlin fallback. Missing
catalog equipment takes the same fallback. These weights are Eden population policy. The weapon choices follow
the pinned catalog and the hulls' combat roles. [EVE University destroyers](https://wiki.eveuniversity.org/Destroyers).
The local evejs NPC behavior loop revision is recorded in the [roaming gang notes](#smarter-pilots-and-persistent-roaming-gangs--november-17-2026).

With default population settings and seed 45, a 512-slot sample in 1DQ1-A using the installed 27,947-profile
snapshot selected all four generated destroyers and imported profiles. Each of the four fits passed the pinned
catalog traffic fit validator in its isolated world. The resident fixture uses a snapshot of haulers to show that
generated combat pilots can coexist with imported identities. All four destroyers formed a gang and kept their
hulls and membership across a checkpoint restart. Each used its own tackle, consumed ammunition and dealt damage
against both a player and a rival bot. With gangs disabled, each destroyer also launched alone, acquired each
target type on patrol, applied tackle and dealt damage through ordinary scheduler ticks.

Two regional raid replays also verify the destroyer gang reports a sighting, points the target and spends
ammunition to deal damage. In the simulated player encounter, another member held the target while the scout
continued reporting. The four earlier scout-specific cases still require the scout's own point.

**Automated validation.** The wider run passed 178 tests in 2 minutes 7 seconds. The class filter joined
`FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.` with each of `PatrolHullTests`, `RoamingGangTests`,
`RegionalConflictWorldTests`, `PatrolTests`, `PvpTacticsTests`, `CapsuleerDefenseTests`, `CapsuleerSupplyTests`,
`CapsuleerShipBlueprintTests`, `TrafficSnapshotTests`, `TrafficPreparationTests`, `CapsuleerBackgroundPreparationTests`,
`CapsuleerProvisioningTests`, `CapsuleerInboundProvisioningTests` and `CapsuleerFitRetentionTests` using OR.
It excluded the documented baseline failure `RivalDefenseEngagesAVisibleRaidWithOwnedWeaponsAndRealDamage`.
The first exact filter was
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.GangFrigateTests.SnapshotAndSpecialistGangCoexistAndKeepTheirOriginalHullsOnReopen`
before the test class was renamed to `PatrolHullTests`. The final raid filter combined exact method names
`OffensiveScoutTacklesAVisiblePlayerOrRivalWithItsGang` and `DestroyerGangActsOnItsScoutsSightingToTackleAndDamageAPlayerOrRival`
in `RegionalConflictWorldTests`. All six passed in 18 seconds. The final solution build took 4.72 seconds with
zero warnings or errors. Both Python policy suites passed (8 catalog checks and 9 formatting checks), as did
format verification and the Git diff check.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Observe a newly populated nullsec patrol or gang in the overview to check native
hull and weapon presentation.

## Tech II frigates in bot gangs

On November 18, 2026, Eden gained four generated PvP fits for client build 3662168. All use an afterburner and
damage control. The Stiletto has two 125mm autocannons, the Crow three light missile launchers, the Wolf four
125mm autocannons and the Harpy four 125mm railguns. Both interceptors have a warp disruptor. Both assault
frigates have a scrambler. The Wolf uses a small armor repairer and the other three use small shield boosters.
The weapons load EMP S, Scourge Light Missiles and Iron Charge S as appropriate. Type IDs, slots, hardpoints
and magazine sizes were checked against the pinned catalog. [EVE University PvP hull bonuses](https://wiki.eveuniversity.org/List_of_PvP_Ships_by_Bonus)

The existing security, roam/camp and generated-combat percentage rules select eligible slots. When the
interdictor draw does not select an available Sabre, four of eight seeded outcomes prefer one of these fits.
Other outcomes and incomplete catalog fits use the existing snapshot selection and generated fallback.
Imported character and kill identities are reserved only for pilots that use the imported fit.
Residents, background fit preparation and gate visitors use the same decision. Founders, owned equipment,
skills and checkpoints use the existing population and fitting operations. The four hull IDs are admitted
by the combat rule used by gang selection, sorties and resident supply preparation.

A read-only check loaded the installed 27,947-profile snapshot with the pinned catalog and sampled 128
new-pilot slots in 1DQ1-A with default settings. It selected all four specialist fits and retained imported
draws. Each generated fit passed the normal traffic fit validator in an isolated world.
The local evejs NPC behavior loop and the Carbon movement reference are recorded in the
[roaming gang record](#smarter-pilots-and-persistent-roaming-gangs--november-17-2026).

The first exact development filter was
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.GangFrigateTests.SnapshotAndSpecialistGangCoexistAndKeepTheirOriginalHullsOnReopen`.
The resident fixture prepares home-system founders in bounded background batches. It verifies that all four hulls
enter one gang while imported profiles remain available, that other slots consume snapshot identities, and that
the same pilots, ships and roles reopen. Eight cases of `FittedGangMemberLocksPointsAndDamagesAPlayerOrRival`
verify that each hull applies its own point, expends loaded ammunition and damages both target types. Two
additional scheduler replays exercise the mixed gang on a regional raid against a visible player and a rival bot.

**Automated validation.** The wider run passed 161 tests in 1 minute 49 seconds. Its filter joined
`FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.` with each of `GangFrigateTests`, `RoamingGangTests`,
`RegionalConflictWorldTests`, `PatrolTests`, `PvpTacticsTests`, `CapsuleerDefenseTests`, `CapsuleerSupplyTests`,
`CapsuleerShipBlueprintTests`, `TrafficSnapshotTests`, `TrafficPreparationTests`, `CapsuleerBackgroundPreparationTests`,
`CapsuleerProvisioningTests`, `CapsuleerInboundProvisioningTests` and `CapsuleerFitRetentionTests` using OR.
It excluded `RivalDefenseEngagesAVisibleRaidWithOwnedWeaponsAndRealDamage`, the independently routed encounter
with a documented baseline failure in the November 18 gang formation record below.
After retaining the original public combat-eligibility overload, 14 final cases passed in 19 seconds with filter
`FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.GangFrigateTests|FullyQualifiedName~OffensiveScoutTacklesAVisiblePlayerOrRivalWithItsGang|FullyQualifiedName~TacticalRulesPrioritizeTackleAndRetreatAtTheDefinedBoundaries`.
The final solution build took 9.68 seconds with zero warnings and errors. Both Python suites passed (8 catalog
and 9 formatting cases), along with formatting and diff verification. Final validation took 62.38 seconds.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Observe a newly populated nullsec gang and inspect the interceptor points
and assault-frigate weapons on grid.

## Roaming gang formation and scout tackle — November 18, 2026

Station recruitment scans up to 32 other home-station roster entries in pilot order. It stops as soon as it can
fill the configured size with the initiator, a scout, a distinct fitted tackler and damage outside the scout role.
A smaller viable crew of at least three can depart when the scan is exhausted. Scout and tackler selection prefer
speed and preserve electronic warfare specialists when alternatives exist. Only selected pilots receive the saved command.

An offensive scout reports visible contacts under the existing patrol, friendship and regional engagement rules.
It can initiate tackle when its fitted point and lock range are within the existing bounded pursuit estimate and
at least one active, damage-capable wingmate is visible within 30 km of the target. The visible opposition must
fit the combined hull strength of the scout and those nearby wingmates. The scout uses tackle movement and the
ordinary targeting, capacitor and module-cycle owners. A point-equipped scout watching a contact can stay up to
the existing two-minute rendezvous interval beyond its original grid dwell. Repeated sightings do not extend
that limit. Loss of contact restores the ordinary dwell at the next observation.

These distances and timing are Eden policies. [EVE University scouting](https://wiki.eveuniversity.org/Scouting)
describes first tackle and calling in the main fleet. The pinned client remains build 3662168. The Carbon
movement reference and local evejs source revision are recorded in the
[roaming gang implementation](#smarter-pilots-and-persistent-roaming-gangs--november-17-2026).
The new decisions use existing movement and protocol operations.

The first development filter was
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.RoamingGangTests.FormationReservesTheOnlyTacklerAndDamageDealerWithinTheConfiguredSize`.
The integration filter
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.RegionalConflictWorldTests.OffensiveScoutTacklesAVisiblePlayerOrRivalWithItsGang`
covers the separately sourced player and autonomous rival contacts. The scout reports first and later applies its
own fitted point. The player moves beyond range, clears warp disruption and leaves the grid. Station replays also
launch a three-ship roster with its only scrambler on the first or fourth available pilot.

**Automated validation.** Tool and solution restore passed. The solution built with zero warnings and errors in
10.02 seconds. The wider filter joined `FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.` with each of
`RoamingGangTests`, `RegionalConflictWorldTests`, `RegionalConflictTests`, `PatrolTests`, `PvpTacticsTests`,
`PvpResourceTests`, `GateCampTests`, `CapsuleerDefenseTests`, `CapsuleerDefenseLifecycleTests`,
`CapsuleerDefenseModuleTests`, `CapsuleerDefenseFrameTests`, `CapsuleerSchedulingTests` and `CapsuleerHandoffTests`
using OR. Of 128 cases, 126 passed and two failed in 61.63 seconds including runner setup.
Both failures reproduced at the same assertions on unchanged main `498d0590` in an isolated two-test run
(8.59 seconds). `RestartRetainsPaidWorkAndJobsButRebuildsObserverModesFromSelections` also admitted a hauler
on restart. `RivalDefenseEngagesAVisibleRaidWithOwnedWeaponsAndRealDamage` observed no raider target in its
350-tick window. That independently routed fleet encounter remains unverified. Both policy suites passed
(8 catalog and 9 formatting tests), as did formatting and diff verification. No full regression suite ran.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** In nullsec, observe a scout report while alone and attempt to hold a target
when a damage ship joins it on grid.

## Civilian danger and stripped belts — November 17, 2026

With situational awareness enabled, miners at belts and freight pilots approaching gates in lowsec and nullsec use
their existing on-grid sightings. Player corporation, alliance and friendly regional contacts are excluded. Civilian
pilots recognize the same combat hull estimates as roaming gangs and distinguish a Venture by its visible type.
A nearby tackle hull, an interceptor or interdictor holding the destination gate, or an armed ship closing into
tackle range can prompt immediate withdrawal. A cautious miner also leaves on an ordinary armed sighting.
A bolder miner can finish its paid cycle and still interrupt it if the threat closes. Freight pilots also withdraw
from groups of two or three armed ships according to their usual risk tolerance. Gate decisions use the ship's
position after movement, including the turn it lands from warp, before committing to a jump.

The 24 km danger range and 100 m/s closing threshold are Eden policies. Pilots project the visible relative motion
only to their next scheduled look. The same rules apply to human and autonomous contacts. Hull estimates do not
require access to the other ship's fitted modules or cargo. Existing direct aggression and travel rules still govern
combat and escape. Gameplay references include [lowsec safety](https://wiki.eveuniversity.org/Safety_Tips_for_Operating_in_Low_Sec)
and [hauling](https://wiki.eveuniversity.org/Hauling).

A miner checks for visible ordinary ore when it reaches a belt or loses its rock. It selects another rock on grid
when possible. When the grid is stripped, it takes its existing cargo to another eligible belt directly. The pilot
keeps all stripped belts for the outbound circuit, docks when every candidate is stripped or dangerous, and unloads
through the ordinary inventory operation. While docked it can reconsider a stripped belt twelve active minutes after
the observation. Empty-belt memory works independently of the danger setting. Checkpoints retain the belt, memory,
navigation and paid cycle. Existing mid-shift saves can recover the belt from the recorded deposit.

The local evejs reference handles an exhausted mining operation as a normal hauling or retreat decision in
`server/src/services/mining/miningNpcOperations.js` (file SHA-256 `5ce327785991ee6ffb18b523e4af1862cccc93e2be6e13e09bdefc2855aff4cd`).
Eden's civilian miners choose their own next belt. Both behaviors use existing flight and mining operations.

**Automated validation.** The exact development filter was
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.PilotAwarenessTests.MinerChangesBeltsWithCargoAndRemembersTheStrippedCircuitAfterRestart`.
The final affected filter was
`FullyQualifiedName~PilotAwarenessTests|FullyQualifiedName~CapsuleerMinerTests|FullyQualifiedName~CapsuleerHaulerTests|FullyQualifiedName~CapsuleerTrafficNavigationTests|FullyQualifiedName~PvpTacticsTests|FullyQualifiedName~TravelerRoutineTests|FullyQualifiedName~OccupiedCapsuleerSimulationTests|FullyQualifiedName~CapsuleerRuntimeTests|FullyQualifiedName~CapsuleerHandoffTests|FullyQualifiedName~ResidentBotEconomyTests`.
All 87 cases passed in 49.29 seconds including runner startup. They cover stripping a belt before arrival,
collecting its last unit, losing a paid target to another miner, returning with the original cargo, reopening a
checkpoint in flight, a visible interdictor at a gate and a protected contact at the same gate. An older paid-cycle
restart assertion also failed on the unmodified base. The affected restart fixtures now compare array contents
before checking the remaining progress fields. Tool and solution restore passed. The solution built with zero warnings
and errors in 18.71 seconds. The catalog and formatting policy suites passed 8 and 9 checks respectively.
Formatting verification passed in 36.41 seconds.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** In lowsec, approach a mining bot in a tackle ship or follow its next warp after
stripping the belt. Native timing and presentation remain unverified.

## Regional loss accounting across refuge regions — November 17, 2026

A reported missile-kill failure was reproduced at `RegionalConflictStore.Loss`: a nullsec community may use a
home/refuge station in a different region. Loss accounting created conflict state using that station's region,
then looked up the community's actual region and dereferenced a missing row. Loss initialization now uses the
typed community home system and affiliation, while retaining its refuge station as metadata. Ordinary station-based
planning keeps its existing behavior. The ship loss, capsule, wreck and regional counters still commit atomically
through the existing resident transaction; no database fallback or new persistence path is added.

**Automated validation:** Both new cases passed in 10.23 seconds wall time (7 seconds test duration).
Tool/solution restore and solution build passed (zero warnings/errors, 0.82 seconds). The affected filter combined
`FullyQualifiedName~` for `RegionalConflictWorldTests`, `RegionalConflictTests`, `MissileCombatTests`,
`PilotCombatTests`, `GameplayDependenciesTests` and `DataModelOwnershipTests`: 76 passed and one pre-existing
fixture failure in 33.94 seconds wall time. The failing
`PilotCombatTests.FatalShotSendsCombatTextBeforeRemovingTheTargetsClientRecord` also fails unchanged on main
`128b0a13`: its nonresident session reaches the resident-only Abyssal handler. It does not reach regional loss
accounting; this unrelated fixture is unchanged. Both Python policy suites passed (8 catalog cases in 1.221 seconds,
9 formatting cases in 0.021 seconds); formatting and `git diff --check` passed. No full suite was requested or run.
Exact development filter:
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.RegionalConflictWorldTests.FirstLossUsesTheCommunityRegionWhenItsHomeStationIsInAnotherRegion`.
The bot-victim variant reproduced the reported null reference at line 297 before the fix. Coverage exercises the
first regional loss before any gang has initialized state, both bot-victim and bot-attacker settlement, rollback,
real hull destruction, counters in the community's region, and checkpoint/restart retention.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** None required. Deployment and live missile-kill acceptance remain unverified.

## Bot PvP improvements — November 17, 2026

The implementation proceeds in six independently verified and pushed milestones:

1. Capacitor management and fighting retreats.
2. Shared primary authority, tackle-aware range bands and bounded interception estimates.
3. Nullsec engagement and finite reaction windows for hunted pilots.
4. Matchup-aware movement and damage application.
5. Independent support assignments and multiple target locks.
6. Deliberate ammunition selection and controlled heat.

### Milestone 1: capacitor management and fighting retreats

Zero-capacitor weapons remain usable below the ordinary 20% offensive reserve. Paid tackle, webs and Nosferatu
can use their affordable capacitor rather than surrendering tackle to preserve that reserve. Repairs, hardeners,
escape modules and propulsion retain priority; tackle and webs precede other offensive payments. A trapped
retreating pilot can spend its remaining affordable capacitor on weapons and disabling effects against its attacker.
All payments, legality checks, reloads and cycle deadlines still use the ordinary resident action owners.

Combat ships now fight while pointed, as civilian pilots already did. They continue escape navigation while
attacking a tackler and cancel offensive repeats as soon as their fitted warp-blocking status clears. This avoids
renewing aggression unnecessarily during a free escape. A pilot below the existing health retreat thresholds
does not stop to wait for drones; healthy ordered withdrawals retain their ten-second recall window. Existing
checkpoint state retains paid deadlines and the retreat flag; no new persistence or SQL gameplay path is added.

References: [EVE University capacitor warfare](https://wiki.eveuniversity.org/Capacitor_warfare),
[CCP warp scrambling and disruption](https://support.eveonline.com/hc/en-us/articles/115004925705-Warp-Scrambling-and-Warp-Disruption),
and [CCP weapons timers](https://support.eveonline.com/hc/en-us/articles/203208932-Weapon-and-Logoff-Timers).
The reserve and recall policies are Eden decisions; fitted costs and strengths come from client build 3662168.
The existing Carbon movement owner is unchanged. Local evejs behavior remains a behavioral reference only;
the source revision and hash are recorded under the roaming-gang milestone below. No reference code was copied.

**Automated validation:** Tool and solution restore passed. Solution build passed with zero warnings/errors
in 1.40 seconds. All 78 affected tests passed in 53.16 seconds wall time (50 seconds reported test duration).
The final filter combined `FullyQualifiedName~` for `CapsuleerDefenseTests`, `CapsuleerDefenseLifecycleTests`,
`CapsuleerDefenseModuleTests`, `CapsuleerDefenseFrameTests`, `PvpTacticsTests`, `RoamingGangTests`,
`GameplayDependenciesTests` and `DataModelOwnershipTests`. Both Python policy suites passed (8 catalog cases
in 1.192 seconds; 9 formatting cases in 0.018 seconds). Formatting and `git diff --check` passed. The full
.NET suite was not requested or run. Development began with exact filter
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerDefenseTests.ActivationRulesUseTheTankLayerAndReserveCapacitor`.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Observe a damaged pointed combat ship firing during its escape, then ceasing fire
after tackle breaks. Native rendering and combat feel remain unverified.

### Milestone 2: primary authority and finite pursuit

A visible, legal, applicable shared primary takes precedence over individual target commitment. An incoming
attacker no longer overrides the patrol's proactive primary. A retreating pointed ship can still switch to an
actual incoming tackler, preserving the fighting-retreat exception. Normal target legality and range checks remain.

Kiting ships with their own tackle keep a margin inside its fitted range until another active gang member has
actually applied a positive, unexpired tackle effect to the same target. A role label or distant gang member alone
does not count as coverage. The same desired range feeds motion and propulsion; the typed range band retains a
15% tolerance. Losing allied coverage returns the ship to its own tackle range on the next defense evaluation.

Target scoring estimates at most twenty seconds of pursuit using observed target velocity and half the pilot's
current fitted maximum speed as a conservative acceleration allowance. Faster hulls no longer score remote
targets as though they were already at ideal range. This is an estimate, not a prediction of hidden destinations.
References: [EVE University tackling](https://wiki.eveuniversity.org/Tackling) and
[solo PvP range control](https://wiki.eveuniversity.org/Solo_PvP). The horizon and allowance are Eden policies.

The cached defense profile now shares its already-calculated ship attributes with defense activation and movement.
Late engagement therefore does not cause a second cold fitting calculation. Normal fit-revision invalidation still
refreshes skills, equipment and incoming effects. A remote patrol whose return station is outside its current system
resumes ordinary route travel instead of attempting a direct warp to a nonexistent local station object.

**Automated validation:** Tool/solution restore passed; solution build passed with zero warnings/errors in
3.17 seconds. All 57 affected tests passed in 49.90 seconds wall time (45 seconds reported test duration), including
unchanged zero-SQL/connection/fitting assertions in the 10/50/100-pilot frame workloads and separate checkpoint
capture/projection/write measurements. The filter combined `FullyQualifiedName~` for `PvpTacticsTests`,
`RoamingGangTests`, `PatrolTests`, `GateCampTests`, `CapsuleerDefenseTests`, `CapsuleerDefenseLifecycleTests`,
`CapsuleerDefenseFrameTests`, `GameplayDependenciesTests` and `DataModelOwnershipTests`. Both Python policy suites
passed (8 catalog cases in 1.364 seconds; 9 formatting cases in 0.019 seconds); formatting and diff checks passed.
Initial affected validation had 55 passes and two warmed-fitting failures. Instrumentation traced the duplicate
attribute calculation; the corrected run then exposed a remote-return assumption, which was fixed as described
above. Temporary tracing was removed. The final run covers both corrections. No full suite was requested or run.
Development began with exact filter
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.PvpTacticsTests.PursuitUsesABoundedHorizonAndObservedVelocity`.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Observe a gang retaining its primary while taking fire, and a kiter returning
inside its own tackle range after the gang's tackler loses coverage. Native presentation remains unverified.

### Milestone 3: nullsec engagement and warp preparation

Offensive regional raids and interceptions may initiate combat against visible neutral pilots without a prior
attack report. Same-group, corporation/alliance friendship, visible strength, application and ordinary combat legality
still gate each actual attack. Escort and defensive objectives retain their narrower target policy. This corrects
the earlier explicit neutral exclusion that could make nullsec gangs ignore a human hunter.

Every new autonomous miner or traveler warp order now has a deterministic four-to-eight human-active-second
preparation window. The ship moves under an ordinary Follow order during that window, leaving time to lock and
tackle it without publishing a premature native warp alignment. After the saved deadline, ordinary alignment,
interdiction and warp mechanics apply. Tackle continues to prevent release after the deadline. Repeated evaluations
do not renew the delay; a warp already in progress is not interrupted. Nearby subwarp journeys do not incur a warp
delay. Restoring a pending legacy alignment converts it to delayed preparation before publishing the resumed scene.

The deadline is saved in the existing typed miner/hauler checkpoint. Paused work shifts it with its existing active
clock, and unobserved travel wakes at the pending preparation deadline rather than skipping to a distant approach.
Resume prepares a private navigation copy and publishes it only after its owning action commits. There is no new
schema, SQL gameplay path, teleport or special immunity for a bot that is being hunted.

The reaction window is Eden policy requested for hunting, rather than an EVE server timing claim. Gameplay reference:
[EVE University tackling](https://wiki.eveuniversity.org/Tackling). Movement reuses the existing Follow and Warp owners,
checked against [Carbon Ball.cpp revision 27ee40b](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp).

**Automated validation:** Tool/solution restore and solution build passed (zero warnings/errors, 0.66 seconds).
All 107 affected cases passed in 52.55 seconds wall time (50 seconds reported test duration). The final filter
combined `FullyQualifiedName~` for `CapsuleerTrafficNavigationTests`, `CapsuleerSchedulingTests`,
`CapsuleerDeadlineTests`, `CapsuleerMinerTests`, `CapsuleerHaulerTests`, `RoamingGangTests`,
`RegionalConflictWorldTests`, `RegionalConflictTests`, `CapsuleerDefenseFrameTests`, `GameplayDependenciesTests`
and `DataModelOwnershipTests`. Both Python policy suites passed (8 catalog cases in 1.317 seconds; 9 formatting
cases in 0.017 seconds). Formatting verification and diff checks passed. No full suite was requested or run.
An initial affected run had 101 passes and six failures: five miner flows treated preparation as arrival, and
the bot-versus-bot fixture compared pre-resupply magazines with post-resupply quantities. Miner travel now waits
for preparation; both battle fixtures sample each shooter's resident magazine after acquisition. The final run
covers these corrections and preserves real damage, paid ammunition and warmed performance assertions.
Exact development filters used
`CapsuleerTrafficNavigationTests.PendingWarpGivesAHunterTimeAndTackleStillBlocksRelease`,
`CapsuleerTrafficNavigationTests.StationlessOpeningStartsAtARealGateAndContinuesToNextGate`, and
`RegionalConflictWorldTests.OffensiveRaidInitiatesPaidCombatAgainstAVisibleNeutralPilot`, each with the full
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.` prefix. The last two passed together in 8.47 seconds wall time.
The neutral-raid fixture initializes the victim's new hull at full health and compares each attacker's resident
magazine from first target acquisition, so pre-departure resupply and shield recharge cannot masquerade as firing.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Jump into a nullsec hunting grid and attempt lock/tackle before a bot leaves;
observe an offensive gang initiating a fight with a neutral pilot. Native presentation and combat feel remain unverified.

### Milestone 4: movement and weapon application

PvP movement now follows typed decisions for range keeping, close orbit, tackle approach spirals, velocity matching
when turret tracking is poor, and early separation when a kiter's observed relative motion predicts an overshoot.
The two-second prediction uses visible positions and velocities. Tacklers use a conservative spiral on approach;
the bot does not inspect the opponent's hidden weapons or fit to classify that approach. Acceleration, inertia,
collision and speed changes still use the existing navigation owner rather than changing positions directly.

Desired gun range uses a damage-weighted median rather than the shortest fitted auxiliary weapon. Mobile-drone
weapon range no longer pulls a mixed fit into the drones' own firing orbit. Drone-only fits use their own skilled
control range; target scoring checks control reach, pursuit speed and an approximate drone-orbit tracking estimate
independently of the parent ship's angular velocity. Actual drone combat remains with its ordinary command owner.

Visible hull types supply cached, unfitted catalog signature estimates. Missing catalog attributes retain the earlier
visible-radius fallback. Opponent fitted signature, capacitor and resistance values are not read for tactical estimates.
Range estimates use surface separation while angular velocity retains center-to-center geometry. Own skills,
weapons, drone capability and propulsion use the existing invalidated defense profile.

References: [EVE University turret mechanics](https://wiki.eveuniversity.org/Turret_mechanics),
[manual piloting](https://wiki.eveuniversity.org/Manual_piloting), and
[drone mechanics](https://wiki.eveuniversity.org/Drone_mechanics). Native movement still uses the Carbon revision
recorded above. Steering thresholds and prediction horizons are Eden policies, not claims of server-identical AI.

**Automated validation:** Tool/solution restore and solution build passed (zero warnings/errors, 9.36 seconds).
All 98 affected tests passed in 52.28 seconds wall time (49 seconds reported test duration), including paid player
and bot battles, drone lifecycle checks and warmed frame SQL/connection/fitting budgets. The final filter combined
`FullyQualifiedName~` for `PvpTacticsTests`, `CapsuleerDefenseTests`, `CapsuleerDefenseLifecycleTests`,
`CapsuleerDefenseModuleTests`, `CapsuleerDefenseFrameTests`, `RoamingGangTests`, `RegionalConflictWorldTests`,
`PatrolTests`, `GateCampTests`, `GameplayDependenciesTests` and `DataModelOwnershipTests`. Both Python policy suites
passed (8 catalog cases in 1.068 seconds; 9 formatting cases in 0.016 seconds); formatting and diff checks passed.
No full suite was requested or run. Exact development filters used
`PvpTacticsTests.MovementSpiralsForTackleMatchesVelocityForTrackingAndAnticipatesKitingOvershoot` and
`PvpTacticsTests.RangeUsesMainWeaponsAndDroneControlRatherThanAuxiliaryOrDroneGunRange`, each prefixed by
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.`.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Observe a tackler approaching obliquely and a turret brawler reducing its own
transversal when tracking suffers. Native motion and combat feel remain unverified.

### Milestone 5: independent support and multiple locks

EWAR and neutralizer modules may maintain their own targets while guns and drones retain the gang primary.
Assignments favor visible logistics and electronic-support hulls, retain useful existing assignments and reduce
duplicate coverage claimed by active gangmates. Failed ECM attempts do not claim successful suppression. Target
painters and tackle continue to support the damage primary. Each assignment needs its own ordinary lock, with
fitted/skill-clamped target capacity, scan time, targeting range and incoming ECM restrictions. A one-lock fit
falls back to the primary. Module range is checked before activation so an out-of-range attempt cannot impose a
long failure retry. Paid cycles, capacitor use, effect probability and checkpoints remain with the existing owners.

Gunless support ships can join gangs that retain a damage-capable main body. Losing every main-body damage ship
orders a return. Pure support pilots use their own support reach for navigation and no longer retreat merely
because they lack ammunition. Old rosters retain their prior damage-capable interpretation when loaded.

Weapon hints come only from damage already received by the bot, using the resident contribution lookup and a
shared immutable catalog classification. Tracking/guidance disruption avoids a known incompatible weapon family;
neutralizers favor an observed capacitor weapon. An observed missile/drone opponent permits a direct tackle
approach; unknown opponents retain the conservative spiral. No opponent fit, cargo or hidden resource is inspected.
These priorities and coverage penalties are Eden AI policy. Gameplay references:
[EVE University EWAR tactics](https://wiki.eveuniversity.org/Electronic_warfare#EWAR_tactics),
[fleet operations](https://wiki.eveuniversity.org/The_Rookie%27s_Guide_to_Fleet_Ops) and
[ECM](https://wiki.eveuniversity.org/ECM). Actual module behavior remains pinned to client build 3662168/catalog.

**Automated validation:** Tool/solution restore passed; solution build passed with zero warnings/errors in
7.47 seconds. All 186 affected cases passed in 83.20 seconds wall time (80 seconds reported test duration).
The filter combined `FullyQualifiedName~` for `PvpTacticsTests`, `CapsuleerDefenseTests`,
`CapsuleerDefenseLifecycleTests`, `CapsuleerDefenseModuleTests`, `CapsuleerDefenseFrameTests`, `RoamingGangTests`,
`RegionalConflictWorldTests`, `PatrolTests`, `GateCampTests`, `PlayerEwarTests`, `TargetingTests`,
`GameplayDependenciesTests` and `DataModelOwnershipTests`. Both Python policy suites passed (8 catalog cases
in 1.130 seconds; 9 formatting cases in 0.017 seconds); formatting and diff checks passed. No full suite was run.
Exact development filters used `PvpTacticsTests.SupportPrioritizesLogisticsSpreadsAssignmentsAndAvoidsObservedWeaponMismatches`
and `RegionalConflictWorldTests.EwarKeepsASecondLockAndItsOwnAssignmentAcrossRestart`, each prefixed by
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.`. The latter passed all three mixed/gunless/one-lock variants
and retains paid ECM deadlines and locks through checkpoint/restart. Existing warmed frame budgets remain asserted.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Observe a gun/EWAR ship retaining a gun primary while jamming another target,
and a gunless support pilot staying with its gang. Native lock/effect presentation remains unverified.

### Milestone 6: ammunition choice and controlled heat

Standard turret fits compare compatible ammunition already in their own cargo using observed range, angular motion
and visible signature. The estimate adjusts their own fitted optimal, falloff and tracking for each catalog charge.
A swap must improve estimated applied damage by more than 25% after amortizing the actual reload delay over thirty
seconds. A saved thirty-second recheck deadline prevents oscillation. The ordinary ammunition owner still validates
skills, bank membership, quantities and paid cycles, transfers the owned rounds and enforces reload time. A rejected
optional swap preserves the current weapon. The comparison reads the resident magazine quantity rather than a projected
database quantity and skips synthetic civilian charges. No ammunition is created or bought. Missile reloads retain the ordinary compatible
loader; tactical missile-type changes, spool-reset tradeoffs and Vorton application remain outside this heuristic.

Earned, supported overheating is limited to three successful cycles per module per defense encounter. Points and
scrams can heat to catch a target just outside normal reach; weapons can heat during a trapped fighting retreat.
The bot declines new heat at 45% rack heat, at 35% damage on any module in that rack, or when two more source damage
pulses could burn its module. These are conservative AI limits, not guarantees against stochastic neighbor damage.
Selections use the ordinary overload owner; successful weapon-bank cycles share the budget accounting. Failed cycles
do not consume a success budget. Stopping a module or leaving combat clears its request while the ordinary owner
settles any funded heat interval. Normal capacitor, ammunition, damage and cooldown payments remain mandatory.

Docked combat traffic receives Thermodynamics III and its catalog prerequisites during resident population
preparation, matching existing fit-skill preparation. Existing higher skills are preserved. Pilots already in space
must return to dock for preparation; pilots without the earned level cannot bypass the ordinary overload checks.
Module budgets and ammo deadlines join the existing typed hauler/miner checkpoint. Pause/resume shifts the wall-time
ammo deadline while preserving consumed heat cycles. Actual heat and module damage use their existing checkpoints.

References: [EVE University turret mechanics](https://wiki.eveuniversity.org/Turret_mechanics),
[ammunition tradeoffs](https://wiki.eveuniversity.org/Turrets) and
[overheating](https://wiki.eveuniversity.org/Overheating). Charge modifiers, reloads, overload bonuses and damage programs
come from the pinned build-3662168 catalog; the existing movement/Carbon owner is unchanged. The comparison horizon,
switch threshold, preparation level and heat limits are Eden policies. The AI does not infer hidden enemy resistances.

**Automated validation:** Tool/solution restore passed; solution build passed with zero warnings/errors in
0.68 seconds. All 228 final affected cases passed in 138.35 seconds wall time (135 seconds reported test duration).
The filter combined `FullyQualifiedName~` for `PvpResourceTests`, `PvpTacticsTests`, `CapsuleerDefenseTests`,
`CapsuleerDefenseLifecycleTests`, `CapsuleerDefenseModuleTests`, `CapsuleerDefenseFrameTests`, `RoamingGangTests`,
`RegionalConflictWorldTests`, `PatrolTests`, `GateCampTests`, `CapsuleerSupplyTests`, `CapsuleerPopulationSettingsTests`,
`AmmunitionTests`, `OverloadControlTests`, `OverloadFittingTests`, `ShipHeatTests`, `MissileHeatTests`,
`GameplayDependenciesTests` and `DataModelOwnershipTests`, with the explicit exclusion below. Both Python policy
suites passed (8 catalog cases in 1.701 seconds; 9 formatting cases in 0.066 seconds); formatting and diff checks passed.
Existing warmed SQL/connection/fitting budgets remain asserted. No full suite was requested or run.

The excluded `FullyQualifiedName!=Eden.Protocol.Tests.Capsuleers.CapsuleerPopulationSettingsTests.`
`RestartSuspendsAndReusesSavedPilotsWithoutChangingTheirPossessionsOrReplayingJobs` fails at its empty visible-hauler
assertion unchanged on main `a182106a` (8.41 seconds wall time). Its code is unchanged. The first expanded run was
interrupted after that failure and civilian-charge failures. The latter were traced to the civilian gun's synthetic
charge type 20132: optional selection now requires a real loaded charge item, and absent/noncharge catalog definitions
are safely skipped. The corrected activation/retaliation rerun passed 31 cases; its only failure was the baseline
population fixture. Temporary diagnostics were removed before final validation.

Development used exact `FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.` filters for both `PvpResourceTests`
methods, `CapsuleerDefenseModuleTests.DefenseChangesOwnedAmmunitionPaysReloadAndDoesNotRepeatIt` (single/banked guns),
`CapsuleerDefenseModuleTests.DefenseHeatsTackleThroughThePaidOwnerAndHonorsItsSavedBudget`,
`RegionalConflictWorldTests.PatrolPreparesThermodynamicsThroughTheResidentCheckpoint` and the existing pause/retaliation
flows. The heat fixture initially withdrew after its only civilian gun failed to reach the target; it now carries an
owned, paid long-range railgun and asserts actual heat generation. Thermodynamics preparation, exhausted heat budgets
and ammo deadlines survive a reopened checkpoint. The thermal fixture imports only the skill and prerequisites,
avoiding unrelated priced-material changes to its active configuration.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Observe a range-driven owned-ammo change and a short overheated tackle catch.
Native reload/heat indicators and combat feel remain unverified.

## Regional bot conflicts — November 17, 2026

Regional operations extend the saved roaming-gang command with constellation-based patrol groups in null security.
These are Eden population affiliations, separate from player corporations, alliances, faction warfare and sovereignty.
A community discovers nearby prepared communities through existing connected station routes of at most three nullsec
jumps, within its region. Up to two neighboring groups become rivals. Discovery adds metadata for existing populations;
operation planning never creates pilots, ships, fits, ammunition, freight, money or replacements. Members of a regional
gang must share their community affiliation; existing player corporation/alliance restrictions still apply.

An available gang chooses its next objective in this order:

1. **Defend** a recent witnessed attack or loss, or a rival combat ship actually sighted in its home constellation.
   Local defense can launch and visit a public grid without leaving the home system.
2. **Escort** an existing owned shipment that is already loaded and still in its source system. The guards follow the
   shipment's actual connected route. The freighter waits at gates for the main body, with a finite rendezvous deadline.
3. **Intercept** recently sighted rival freight, or alternate raids on a rival extraction site with patrols of its
   transport corridor. Destination selection uses public catalog geography and prepared community locations.

Pilots publish bounded, expiring contact reports from their existing observations. Reports retain a pilot, ship,
system and grid; they expire after three human-active minutes and repeat at most every thirty seconds per unchanged
contact. Opponent cargo contents, fitting, resources and future destinations are not inspected. Ordinary gang reports
remain shorter-lived tactical target reports. Each attacker still needs a visible, legal target through the normal
resident combat path. Regional objectives exclude friendly regional pilots and unrelated neutrals; a human pilot can
become a defensive target through a witnessed attack or loss. Individual nonregional roams retain their existing policy.
Regional affiliations also contribute to visible opposing fleet strength and civilian friend filtering. The saved home
community determines affiliation before the first report, so discovery cannot change friends during an ongoing fight.

Raids visit the selected grid using normal navigation and patrol behavior. Escort guards cover transit gates and wait
at the endpoint until the shipment is delivered. Completion requires the original freight stacks to arrive with their
owned quantities; missing cargo or destruction of the escorted hull aborts the operation. Operation expiry, too few
surviving guards, retreat or rendezvous failure orders a finite withdrawal. No encounter is resolved through virtual
offscreen damage. Active gangs and escorted shipments continue into unoccupied systems while someone is playing;
all pause when the last selected human leaves and while the server is stopped.

Ship destruction records regional losses, kills, threat pressure, the public loss grid and a five-minute offensive
recovery deadline in the same resident transaction as the actual ship loss. A rejected action leaves both unchanged.
Defensive and escort work can still be selected during offensive recovery. Pressure decays by two points per active
minute; incident tension rises while established rivalries retain their baseline. A two-minute launch interval and
one active operation per group by default bound escalation. The configurable limit accepts one to three. A completed
raid means its patrol reached the objective and returned without losing a member; it does not claim a battle victory,
capture territory, or replace lost equipment.

World schema **185** adds `WorldRegionalConflict`. Typed resident records retain groups, rivalry tension, reports,
operations, sequence receipts, loss counters and deadlines. SQL only loads or projects checkpoints. There is no offline
SQL implementation of regional gameplay. A region admits at most 64 groups, 128 relationships, 64 reports and 192
operation records; inactive operation history and loss receipts are bounded. Existing databases gain an empty conflict
table without changing their pilots or possessions. Saved gang and freight assignments resume against the canonical
operation after restart. Warm diagnostic reads and target filtering perform no SQL, connections or fitting calculations.

**Administration:** Population shows group homes, pressure, losses, kills and operation objectives, status and reasons.
World Configuration exposes `settings.capsuleers.regionalConflicts` (default on) and
`settings.capsuleers.regionalOperationsPerGroup` (default 1). They follow the normal save-and-restart workflow. Disabling
regional conflicts withdraws regional gangs and releases escorted freight to its ordinary routine. Disabling all bots
pauses the population. Sparse communities continue ordinary individual patrols; additional ships are never granted to
meet an operation's crew requirements.

**References:** [EVE University fleet and gang types](https://wiki.eveuniversity.org/Fleet_and_Gang_Types) and
[scouting](https://wiki.eveuniversity.org/Scouting) inform roaming, raids, escort and defensive roles. Persistent regional
affiliations, cooldowns and operation budgets are Eden policy. Reviewed local evejs
`server/src/space/npc/npcBehaviorLoop.js`, SHA-256
`21AD46AB54D10507B6811C2FF14A49B0E0181D5AA38A1CCCFCEE0B97F6DA843B`; this checkout has no Git metadata.
The implementation reuses Eden's existing native movement and combat rather than adding protocol commands or changing
Destiny serialization. The prior gang milestone records the pinned Carbon movement reference and client build 3662168.

**Automated validation:** Tool and solution restore passed. The final solution build passed with zero warnings/errors
in 4.55 seconds. **123 focused .NET tests passed** in 70.27 seconds wall time (66 seconds reported test duration).
Both Python policy suites passed (8 catalog checks and 9 formatting checks, 1.56 seconds combined). Formatting verification passed in 35.93 seconds; new documentation links, anchors and UTF-8 checks also passed. The full .NET suite was not requested or run.
The final TRX is `artifacts/regional-conflicts/regional-conflicts-verified.trx`.

The final filter was the union of `RegionalConflictTests`, `RegionalConflictWorldTests`, `RoamingGangTests`,
`PvpTacticsTests`, `PatrolTests`, `GateCampTests`, `CapsuleerDefenseFrameTests`, `CapsuleerDefenseTests`,
`CapsuleerDefenseModuleTests`, `CapsuleerDefenseLifecycleTests`, `FreightTests`, `WorldSettingsPageTests`,
`GameplayDependenciesTests` and `DataModelOwnershipTests` using `FullyQualifiedName~` filters, plus the exact
`AdministrationTests.PopulationControlsRenderAndSavePendingValuesThroughTheAuthenticatedEditor` and
`AdministrationTests.PopulationRequiresAdministratorAndReportsSavedCoverageWithoutAdvancingJobs` methods using their
full `Eden.Protocol.Tests.Administration` namespace. Initial development started with exact method filter
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.RegionalConflictTests.WitnessedAttacksTakePriorityAndLossesDelayOffensiveOperations`.

Coverage includes pure priorities, report expiry, limits and duplicate launch rejection; real resident raid and
local-defense navigation; autonomous rival combat with sightings, real damage and paid ammunition; freight delivery
and missing cargo; atomic destruction and rollback; checkpoint/restart restoration; quiet-world pause; old-schema
upgrade; warm SQL/connection/fitting budgets; authenticated configuration and population rendering.

An initial affected run completed 117 passes and five failures in the independent-neutral patrol frame workload.
It exposed affiliation being established after the first attack report. Affiliation now comes directly from the saved
home community; the independent-neutral frame fixture explicitly disables regional conflicts so its original
combat/performance assertions continue measuring that workload. All five pass in the final run. Regional enabled-world
tests separately cover stable affiliation, rival targeting and friend rejection. Earlier routine implementation and
fixture corrections were followed by focused reruns; they are not reported as successful validation of earlier code.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Observe a raid reaching a rival grid; follow an escorted shipment through a gate; inspect
Population after an actual loss and restart. Native visuals and combat feel remain unverified.

## Smarter pilots and persistent roaming gangs — November 17, 2026

Eligible nullsec pilots can form persistent hunting gangs from three to six existing, fitted ships. The default size
is four; the existing roaming chance controls formation. The saved command contains a home, a connected route of up
to three nullsec jumps, an endpoint, leader, hull-specific roster and roles, formation and return deadlines, current
phase, and an expiring target report. Formation requires a usable damage source, sufficient health, and a fitted
secondary tackler as well as a scout. It excludes freight deliveries, shopping trips and suspended pilots. Generated
pilots from NPC corporations can cooperate locally; player corporation/alliance affiliations remain compatible.
No ships, modules, ammunition, wallets or replacement grants are created by gang formation.

The scout leaves eight seconds ahead of the main body after a thirty-second formation. The main body regroups at
outbound gates, with a two-minute rendezvous limit. Members reuse ordinary gate travel, gate protection, hunting,
locks, legality, weapon payments and loss settlement. They share a route and recent reports rather than teleporting.
Reports expire after twelve human-active seconds and identify one system and grid. Scouts report contacts without
initiating attacks; the leader and followers use those reports when selecting a primary. Each attacker must still
see and legally target the victim. A gang waits for surviving members at the endpoint and returns home. Ship loss
removes that hull from the active roster and elects a surviving leader; insufficient strength, a failed rendezvous,
retreat, or the ten-minute sortie deadline orders a return. Arrivals use a short operational turnaround instead of
civilian layovers; recovered members rest for two minutes. Replacement purchasing and regional campaigns remain later work.

Individual hunting decisions now consider visible hull-class threat, nearby allies, the pilot's own available weapons,
range, motion and estimated damage application. Corporation, alliance and camp affiliations identify visible opposing
groups; unrelated neutrals are assessed separately. An eight-second commitment and score tolerance reduce unnecessary
target switching. Enemy hull size provides an uncertain signature estimate; hidden enemy fits, resources and destinations
are not inspected. Kiting and support roles retain weapon range instead of always closing to their own scrambler.
Tackle and brawling roles close within tackle range. Propulsion pulses outside the desired range band while retaining
capacitor reserves; an exhausted capacitor-dependent fit withdraws. Camps compare visible hull threat rather than counting
all armed hulls equally. New generated Merlins also receive catalog-available starter propulsion, shield repair and damage
control through the normal validated blueprint path. Existing owned fits are retained.

Gang commands are immutable typed Gameplay records inside the existing resident hauler checkpoints. Membership and command
revisions publish with the successful owning action, and stale worker snapshots cannot replace a newer command. Failed or
stale actions publish no enrollment. The existing capture/projection path saves the roster, command, each ship's navigation,
health, paid deadlines and job together. No new SQL gameplay path, offline gang implementation or schema table is added.

Active gang members can continue through unoccupied systems on the existing bounded queue while any human has selected a
pilot. Ordinary civilian population retains its occupied-system policy. Gang jobs are admitted again after a restart even
when their current system is unoccupied. The shared human-active clock pauses while nobody is selected and while stopped;
there is no offline sortie advancement or reward replay. Canonical autonomous contacts feed the ordinary combat-presence
boundary independently of native scene materialization, preserving warp, arrival, jump, cloak and suspension protection.
Resident lookups sample at most 32 local identities and return at most twelve contacts. Ordinary target and observer reads
never reconstruct a fit or open a world database connection once warmed.

The gameplay references are [EVE University solo PvP](https://wiki.eveuniversity.org/Solo_PvP) and
[fleet command](https://wiki.eveuniversity.org/Fleet_Command_Guide). These explain range control, scouting, common objectives,
primary targets and succession; gang sizes, estimates and timings above are Eden policies for pinned client build 3662168.
Movement continues through Eden's existing Follow/Orbit/gate owners, with the
[Carbon Ball.cpp reference at 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp).
The local `evejs/server/src/space/npc/npcBehaviorLoop.js` target-switch cooldown and explicit movement orders were also
reviewed as behavioral references. That local snapshot has no Git metadata; its SHA-256 is
`21ad46ab54d10507b6811c2ff14a49b0e0181d5aa38a1cccfcee0b97f6da843b`. No reference implementation code was copied.

**Automated validation.** Tool restore and isolated solution restore passed. The final solution build passed with zero
warnings/errors in 12.97 seconds. The final affected .NET filter below passed all **101 cases in 46 seconds** (48.59 seconds
including runner startup). Its nine new cases cover target threat/application and commitment, role range and propulsion,
resource exhaustion, settings, reports, loss succession, real paid unobserved combat, gang formation and unobserved travel,
checkpoint/restart return, real hull destruction, stale/abandoned publication, and the last-human pause. Warm unobserved
lock/tackle/damage cycles and shared command publication retain zero SQL commands, connections and fitting calculations.
Both Python policy suites passed (eight catalog cases in 1.066 seconds and nine formatting cases in 0.015 seconds).
Formatting and local documentation links were verified. The full .NET suite was not requested or run.

Final filter:
`FullyQualifiedName~PvpTacticsTests|FullyQualifiedName~RoamingGangTests|FullyQualifiedName~CapsuleerDefenseFrameTests|FullyQualifiedName~CapsuleerDefenseTests|FullyQualifiedName~CapsuleerDefenseModuleTests|FullyQualifiedName~CapsuleerDefenseLifecycleTests|FullyQualifiedName~PatrolTests|FullyQualifiedName~GateCampTests|FullyQualifiedName~WorldSettingsPageTests|FullyQualifiedName~GameplayDependenciesTests|FullyQualifiedName~DataModelOwnershipTests`.

Before the neutral-affiliation correction, the broader affected filter completed **681 passes, 21 failures and one skip
in 7 minutes 16 seconds**. All five added failures were the existing armed-patrol frame cases: refusing the whole neutral
field led those artificially relocated pilots into a return path with no local home station. The affiliation correction
restored their intended combat workload; all five passed in the final run without changing their assertions. All sixteen
other failures reproduced with the same assertions on unchanged starting revision `fa2e1c6e`. They comprise two allocation
limits, population suspension, residential visits, two mining restart comparisons, two old SQL resupply command counts,
two receiving-only gate fixtures, two disconnected founder projections, two belt-activity sovereignty reads, one overpriced
shopping case, and the fatal-shot session fixture's resident Abyssal requirement. Baseline comparisons selected 26 cases;
sixteen failed and ten passed. Those existing fixtures and features were not changed for this milestone. The final focused
run does not claim the broader suite is green.

Broader filter:
`FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~TackleTests|FullyQualifiedName~TargetingTests|FullyQualifiedName~MissileCombatTests|FullyQualifiedName~PilotCombatTests|FullyQualifiedName~PilotDroneTests|FullyQualifiedName~FlightSessionTests|FullyQualifiedName~GameplayDependenciesTests|FullyQualifiedName~DataModelOwnershipTests|FullyQualifiedName~WorldSettingsPageTests|FullyQualifiedName~AdministrationTests`.

The existing complete combat-frame workload retains every expected shooter and zero warmed SQL/connection/fitting budgets.
Matched inputs produced the following baseline/final p95 frame times. Concurrent test load differed, so these samples are
validation evidence, not an optimization or server-capacity claim. Checkpoint costs are measured separately.

| Pilots / observers | Starting revision p95 | Final p95 | Final checkpoint capture / projection / write |
| --- | --- | --- | --- |
| 10 / 2 | 21.00 ms | 21.22 ms | 0.59 / 12.56 / 117.56 ms |
| 50 / 2 | 31.57 ms | 21.22 ms | 0.37 / 12.20 / 14.51 ms |
| 50 / 4 | 41.40 ms | 23.83 ms | 0.40 / 10.13 / 14.02 ms |
| 100 / 2 | 61.82 ms | 40.88 ms | 0.47 / 22.62 / 14.60 ms |
| 100 / 4 | 71.85 ms | 39.52 ms | 0.40 / 20.40 / 18.43 ms |

**Live acceptance.** Not performed; agent client testing is disabled. Native rendering and gameplay feel remain unverified.

**Optional maintainer checks.** Observe a gang departure and regroup, and assess a kiter/tackler encounter in the native client.

## Nullsec transit encounters

On revision `b3f052d2`, a pilot reported a Sabre and Merlins leaving L3-I3K through the K-L690 gate.
The K-L690 checkpoint showed one Sabre and three Merlins with ordinary onward orders to NDII-Q. Neither system has an
NPC station. The decision to start a hunt previously required a station departure, and combat classification excluded
Tech II interdictors even when the generated Sabre had a paid and loaded combat fit.

On reaching jump range, eligible nullsec travelers now roll the existing roaming chance once per gate and journey.
A successful pilot watches that gate using the ordinary roam search interval, sight grid, target selection, lock time,
pursuit distance, ammunition and aggression rules. The fit must support combat and have usable weapons, and the pilot
must have enough health to fight. An active freight delivery, supply trip or retreat takes precedence. Transit hunters
reuse their saved journey, stay on their original leg through a checkpoint, and pay the normal weapons timer before jumping.
The last watched gate is saved until the crossing, so approaching it again after combat cannot start another watch.
Tech II interdictors qualify for combat when armed. Gate camps retain their separate roles and probe-launch policy.
[EVE University gate camps](https://wiki.eveuniversity.org/Gatecamps).

**Automated validation.** The solution build passed in 4.15 seconds. All 88 focused .NET cases passed in 44 seconds,
including provisioned Sabre and Merlin travel through a stationless system, gate cloak, combat, freight and disabled roaming.
Both Python policy suites passed (17 cases), and formatting and line-width verification passed.

The focused .NET filter was
`FullyQualifiedName~CapsuleerTrafficNavigationTests|FullyQualifiedName~Capsuleers.PatrolTests|FullyQualifiedName~GateCampTests|FullyQualifiedName~CapsuleerDefenseTests|FullyQualifiedName~CapsuleerDefenseModuleTests|FullyQualifiedName~CapsuleerDefenseLifecycleTests|FullyQualifiedName~PilotAwarenessTests|FullyQualifiedName~CapsuleerFitRetentionTests|FullyQualifiedName~CapsuleerDefenseFrameTests.ScoutReportsAndCampPartnersTackleAndFireOnTheSameVisibleShip|FullyQualifiedName~CapsuleerDefenseFrameTests.ArmedPatrolsOnOneGridWithFullObservers`.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer check.** After deployment, observe an outbound gate in a stationless nullsec system with roaming enabled
and a visible ship in fighting range.

## Stalled gate traffic and belt pursuit

A pilot reported apparent instant group departures on landing cloaked in Z-UZZN. On server revision `7c8f69b8`,
two successive checkpoints showed five enabled travelers at the same positions near the YW-SYT and C-7SBM gates.
They had ordinary travel orders to the opposite gate. Four had a pending alignment at movement stamp zero.
Their next job was over 31 hours ahead of the saved population clock. The fifth traveler was over 14 hours from its next update.

A pilot entering the grid receives the saved position, alignment destination and elapsed alignment time.
Destiny can start predicting warp locally while the server still has the ship at its departure gate. A later observer
can then receive the same starting position and warp order again. This explains the reported appearance without a sighting
of the cloaked pilot. The checkpoints do not record the individual packets the client received.
[Carbon alignment and warp entry, revision 27ee40b](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp#L1776-L1814).

When an occupied system resumes, an in-flight pilot now receives a movement update within one second.
The saved ship, position and course continue through the ordinary flight scheduler and checkpoint path.

Belt ratters used to consider any visible belt pirate and owned NPC wrecks up to 250 km from their ship.
They now match the pirate's assigned belt and apply the existing 50 km NPC pursuit distance around the belt center
to proactive combat and wreck selection. The belt remains the reference point while the ship approaches a wreck.
The server checkpoints did not identify the particular ratter in the report.
[EVE University belt ratting](https://wiki.eveuniversity.org/NullSec_Ratting).

**Automated validation.** The exact development filter
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerTrafficNavigationTests.StationlessOpeningStartsAtARealGateAndContinuesToNextGate`
reproduced the 31-hour deadline. The corrected case checks the observer's alignment and warp contacts, departure from the grid,
continued travel to the original gate, and reopened checkpoint. Ratting tests cover a pirate from a different belt, a distant
belt, local owned loot, another pilot's loot, and an owned wreck 120 km away. The solution built with zero warnings or errors
in 5.72 seconds. The affected run passed 97 of 105 cases in 44 seconds. The same eight cases failed with the same assertions
on unmodified `7c8f69b8` in a 12-case comparison that took 15 seconds. They concern one overpriced shopping case, two mining
restart comparisons, two presentation allocation limits, one residential traffic observation, and two receiving-only gate
visitor fixtures. Both Python suites passed (8 catalog and 9 formatting cases). Formatting checked 2,466 files.

Affected filter:
`FullyQualifiedName~CapsuleerTrafficNavigationTests|FullyQualifiedName~CapsuleerTrafficObservationTests|FullyQualifiedName~CapsuleerHandoffTests|FullyQualifiedName~CapsuleerTierTests|FullyQualifiedName~OccupiedCapsuleerSimulationTests|FullyQualifiedName~CapsuleerSchedulingTests|FullyQualifiedName~CapsuleerDeadlineTests|FullyQualifiedName~CapsuleerInboundGateAcceptanceTests|FullyQualifiedName~CapsuleerPresentationTests|FullyQualifiedName~PilotSpaceActivitiesTests|FullyQualifiedName~PilotAwarenessTests|FullyQualifiedName~PatrolTests|FullyQualifiedName~GateCampTests|FullyQualifiedName~RattingRulesTests`.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** After updating, revisit the YW-SYT and C-7SBM gates while cloaked and watch a traveling ship
leave. Watch a belt ratter finish a nearby wreck and move on.

## Temporary gate camps

An eligible departure can start or join a three-position local watch. Members choose the same gate and shift.
Their fitted tackle, damage and scouting ships warp to different stand-off ranges. Scouts report visible ships
for 12 seconds. Combatants verify presence, distance and Crimewatch legality before locking or firing.
The watch disperses under superior visible numbers and expires at the end of its configured shift.
Lowsec camps are opt-in and take legal targets. Warp, jump and docking use the normal travel and weapons timers.

A generated nullsec Sabre starts with three disruption probes, a scrambler, a 125mm autocannon and EMP S.
The fitted launcher spends a charge and capacitor. A trapped simulated pilot can burn out of a field, retry
its original warp, or jump a gate within reach. The same interdiction rules govern warp drag and catch.

Gameplay references include [EVE University gate camps](https://wiki.eveuniversity.org/Gatecamps),
[EVE University interdictors](https://wiki.eveuniversity.org/Interdictors) and the [Eden interdiction protocol record](interdiction.md).

**Automated validation.** The first focused role test was
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.GateCampTests.ThreePilotsCoordinateOnOneGateAndDisperseAtTheShiftBoundary`.
The resident integration cases cover a saved camp sharing one target after startup, normal scan and gun cycles, paid tackle,
generated Sabre fit and magazine, paid probe deployment, sub-warp escape, ordinary docking, and configuration round trips.
The scout, tackler and damage ship test took 9.23 ms median, 14.72 ms p95 and 17.69 ms maximum during the combined run.
It recorded zero SQL commands and connections. Tackle changes caused eight fit evaluations across four of the 20 warmed frames.
All 216 affected .NET cases passed in 77 seconds. The solution built cleanly in 10.02 seconds, and both Python policy suites
passed (8 and 9 cases). The full .NET suite was outside the requested validation scope.

After the camp changes, an isolated repeat of the five-case patrol matrix passed in 36 seconds. Each requested case
completed every due job and updated every observer in each of 20 warmed frames, with zero SQL commands, connections and
fit calculations. Formatting checked 2,461 files and the line-width and diff checks passed.

| Pilots / observers | Median ms | p95 ms | Maximum ms | Checkpoint capture / projection / write ms |
| --- | ---: | ---: | ---: | --- |
| 50 / 2 | 11.92 | 14.61 | 14.65 | 0.41 / 9.83 / 14.52 |
| 50 / 4 | 19.63 | 22.93 | 24.19 | 0.34 / 16.05 / 16.96 |
| 100 / 2 | 34.23 | 49.32 | 52.33 | 0.76 / 31.40 / 18.35 |
| 100 / 4 | 40.31 | 44.21 | 45.35 | 0.51 / 28.68 / 18.47 |

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Observe the scout and tackler landing, then try to leave a camp by gate or warp.

## Nullsec PvP roams

Armed pilots can undock and patrol up to four local belts and gates. Hunters wait for visible targets, a real lock and
a legal shot. They spend ammunition and capacitor and give up pursuit beyond 75 km. Capsules and player corporation or
alliance mates are excluded. Patrols, targets and their deadlines survive checkpoints and population pauses. Generated
nullsec combat pilots use owned rail Merlins. Imported pilots retain their recorded fits. The circuit and fitting mix
are Eden policies. [EVE University solo PvP](https://wiki.eveuniversity.org/Solo_PvP).

**Automated validation.** The exact legality test covers nullsec attacks, target lock delay, damage, lost presence,
low/high security refusals and 100 warm revalidations without SQL or fit work. The scheduler test covers undock,
multiple grids and return. Ten armed pilots and two full observer sessions ran 36 one-second frames. All ten fired.
The last 20 frames took 6.77 ms median, 7.98 ms p95 and 8.97 ms maximum in the isolated run, with zero SQL commands,
connections or fitting calculations. Checkpoint writing was outside this measurement. The earlier 28.01 ms freight timing
covers 200 unchanged-presentation ticks.

The isolated scale run measured the entire one-second frame, draining all due jobs before completing every distinct
observer session update and encoding its packets. All pilots fired. Two origin communities supplied the largest fleet.
The 20 warmed frames in each case recorded zero SQL commands, connections and fit calculations.

| Pilots | Observers | Median ms | p95 ms | Max ms | Max maintenance wakes | Checkpoint capture / projection / write ms |
| ---: | ---: | ---: | ---: | ---: | ---: | --- |
| 50 | 2 | 21.92 | 32.56 | 34.02 | 2 | 0.53 / 20.69 / 16.55 |
| 50 | 4 | 12.26 | 15.03 | 15.16 | 2 | 0.29 / 7.65 / 14.72 |
| 100 | 2 | 32.44 | 37.46 | 38.02 | 4 | 0.80 / 18.04 / 14.00 |
| 100 | 4 | 28.41 | 38.97 | 39.04 | 4 | 0.50 / 17.84 / 19.21 |

The isolated matrix used the exact filter
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerDefenseFrameTests.ArmedPatrolsOnOneGridWithFullObservers`.
Results reflect one run per combination on the synthetic railgun fleet. File saves were timed separately.

The combined patrol, awareness, defense, provisioning, fit, population pause, performance and administration selection passed
121 cases in 42 seconds. The clean solution build took 7.72 seconds. The Python catalog and formatting policy suites passed
8 and 9 cases respectively. Local security selection caches only the requested system and passes the isolated provisioning
regression with an unreadable unrelated system record.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Observe a nullsec hunter locking a visible ship and breaking pursuit after it warps away.

## Pilot awareness and ratting

Visible contacts are indexed per system and spatial cell. A pilot samples up to 16 entries per neighboring cell and
retains at most 12 sightings. Arriving, departing, cloaked and warping ships are excluded. Mining decisions and traveler
threat memory depend on local observations. Ratters can visit public combat anomalies and choose fighting distance from
their available weapons. Checkpoints store the selected site and bounded belt memory.

**Automated validation.** The exact populated spatial-index regression and the combined awareness, mining, defense,
traveler, ratting, anomaly, settings and traffic performance filter passed. The combined run passed 87 tests in 47 seconds.
A 2,000-ship scene exercised bounded visibility with zero SQL commands, connections or fit calculations on the observation
path. The solution built cleanly in 8.55 seconds. Both Python policy suites passed (8 and 9 cases), as did formatting and width checks.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Observe a miner changing belts after a combat ship lands, or follow a ratter into a public anomaly.

## Hauler freight settings

The population settings editor and pending revision API persist hauler hull, cargo pool, budget, frequency
and wreck-survival controls. Old saves deserialize with standard freight settings. Custom pools cache catalog
records by type and skip goods lacking a usable volume or price. Budgets use the bundled sell-price snapshot
with a catalog base-price fallback. Wreck overrides apply only to the recorded manifest on the original pilot ship.
[Settings and defaults](../capsuleer-population.md#hauler-freight).

**Automated validation.** The editor round-trip, ordered budgets and invalid type lists passed the exact filter
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.FreightTests.FreightEditorRoundTripsAndRejectsInvalidBudgetsAndTypeIds`.
The combined freight, configuration/editor, population, resident admission, traffic performance and NPC combat selection
passed 99 cases in 35 seconds with one opt-in browser test skipped. Loss tests exercise 0%, 50% and 100% survival using a
saved custom cargo pool. The solution built in 4.65 seconds with zero warnings and errors.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer check.** Save a custom freight pool and inspect a new shipment after restart.

## Generated hauler hulls and freight

Generated traffic chooses among the twelve ordinary Tech I haulers present in the build 3662168 catalog. Regional empire
hulls receive double weight. Specialist ships carry compatible goods in a supported hold. Freighters can carry bulk materials,
local supplies or higher-value courier cargo. Shipment quantities depend on free volume and a bounded catalog-value budget.
Some return legs run empty. Selection, delivery and the saved manifest share the pilot's existing transaction and checkpoint.
Cargo scanners, kill reports and wreck settlement read these actual stacks. Docking consumes only cargo still owned and carried
from the delivered manifest. Other possessions remain aboard. No trading or live price lookup participates in freight generation.

In existing saves the next safe dock recognizes the original pilot, ship and exact starter ore stack. It replaces that stack
once and grants the skills for the selected hull. Altered names, fits, damage, losses and cargo that cannot transfer defer conversion.
Imported fits retain their identity. Catalog hull and cargo choices are memoized and prepared ahead of live admission.
Movement, observer reads and gate transit do no shipment planning.

Gameplay references: [hauling](https://wiki.eveuniversity.org/Hauling) and [hauler roles](https://wiki.eveuniversity.org/Tech_I_Role_Reference).
The local evejs NPC equipment and movement loop was inspected. Freight uses Eden's resident inventory and existing movement.
The published [Carbon Destiny reference](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp)
continues to apply to normal hull flight.

**Automated validation.** Exact initial filter `FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.FreightTests.DifferentShipmentsRespectVolumeValueAndReturnTrips`.
The capsuleer namespace comparison ran 436 baseline and 439 candidate cases before the final regional weighting and wreck test.
Both had the same eleven failures across gate traffic, population handoff, shopping, resupply and presentation fixtures.
The matched four-observer 10× traffic workload had zero observation reads. For eleven pilots the baseline/candidate frame batches
took 57.59/28.01 ms, allocating 9.74/9.90 MB. Checkpoint capture took 1.56/0.75 ms, projection 14.60/22.87 ms and writing
106.41/12.67 ms. These are individual noisy samples. Freight tests cover saved conversion, custom ships, capacity, destination
variation and checkpointed wreck contents. Resident founder admission asserts zero SQL, connections and fitting calculations.

The final focused selection covered freight, admission, inbound provisioning, encounters, blueprints, snapshot import and migration,
global population, startup and traffic performance, and information scanners. All 149 passed in 53 seconds. The build passed
without warnings or errors in 7.23 seconds. Both Python policy suites passed (8 catalog and 9 formatting cases).

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer check.** Cargo-scan several haulers and compare a destroyed ship's wreck with its scan.

## Shared bot presentation - November 16, 2026

Each active system retains immutable miner and hauler observations against resident shared/local versions, its activation
generation, the catalog generation and an exact validity interval. Repeated captures reuse those observations and the
combined batch contents before walking the pilots. Membership/activation changes and pending committed transition
observations retain the existing batch revision and presentation-retry rules. Demotion evicts the system's observation
cache. The system registry shares unchanged immutable observed-state arrays; previous snapshots remain unchanged.

Runtime movement and combat invalidate their owning system. Economic commits use their actual participant systems rather
than the transaction coordinator's shared context. Unknown scope, account/settings changes and catalog invalidation remain
conservative shared invalidations. Rollback does not publish invalidations or speculative health. The readiness check also
retains each system's earliest canonical job deadline against its resident/membership versions, avoiding repeated full
pilot scans while preserving activation barriers. Capture never executes scheduled actions.

The system owner is entered before checking its cache, so timed effects expire at their ordinary authoritative boundary.
Damaged shields restrict observation reuse to the exact sampled clock instant; full shields can reuse a view until its next
paid repair or invalidation. Repair deadlines retain their saved precision. No clock rounding, delayed same-tick damage or
shared personal output is introduced. If a health read publishes a change during capture, the starting version is retained
so the next capture cannot hide that change beneath a newer cache key.

Gameplay context was checked against [EVE University shield recharge](https://wiki.eveuniversity.org/Shield_tanking).
Native packet forms and navigation are unchanged for build 3662168. The existing
[Carbon Destiny revision 114e89fa584f0e1a81eb0a679aabf41af8870072](https://github.com/carbonengine/destiny/blob/114e89fa584f0e1a81eb0a679aabf41af8870072/src/Ballpark.cpp)
remains the simulation reference. The local reference engine's `space/runtime/scene/destinyHistory.js` was inspected for
shared scene history versus observer-specific output; no source was copied.

### Matched presentation measurements

The same deterministic resident fixtures, population settings, observer count and 200 advancing one-millisecond frames
were measured before and after the cache. Every frame includes complete bot maintenance/presentation followed by each
observer's capture. Jobs stay unchanged. Empty, two-pilot and 11-pilot populations assert zero SQL commands, connections
and fitting calculations; warmed observation reads stay at zero after optimization. Allocation budgets detect recurrence
of the population-dependent work. Full protocol session-frame and checkpoint measurements are recorded with final validation.

Baselines use `d45c5a0c` capture behavior with the same test and read-count instrumentation. Managed allocation is cumulative
across the measured frames, in decimal MB.

| Unchanged presentation workload | Observation reads before / after | Allocated MB before / after | Processing ms before / after |
| --- | --- | --- | --- |
| Empty bot population, one observer | 0 / 0 | 5.25 / 4.70 | 29.59 / 27.15 |
| Two pilots, one observer | 800 / 0 | 8.32 / 4.70 | 53.08 / 23.68 |
| 11 pilots, four observers, 10× traffic | 11,000 / 0 | 48.33 / 6.20 | 149.57 / 40.11 |

The last workload reduces managed allocation by approximately 87%. Its checkpoint capture/projection/write measured
2.03 / 53.69 / 18.24 ms before and 0.96 / 30.87 / 18.05 ms after, outside the frame measurement.

The existing matched replay separately includes real protocol session ticks and native encoding over 200 50-ms frames.
Both its polling and deadline arms retain identical jobs, pilot positions/role progress, cargo, encoded byte counts and
maintenance turn counts before/after this milestone. Deadline-arm samples are:

| Complete session workload | Allocated MB before / after | Processing ms before / after |
| --- | --- | --- |
| Empty bot population, one observer | 3.32 / 3.32 | 26.40 / 29.73 |
| Populated, one observer | 5.02 / 5.12 | 34.09 / 32.86 |
| Populated, two observers | 7.72 / 7.70 | 52.50 / 58.16 |

All arms issue zero SQL commands, connections and fitting calculations in this run. The two-observer deadline replay's
checkpoint capture/projection/write measured 0.72 / 1.47 / 17.15 ms before and 0.83 / 2.38 / 17.59 ms after. Small-scene
complete-frame measurements are mixed; the substantial reduction is in repeated observation work. Treat timing and
checkpoint samples as fixture diagnostics. Live latency, global population capacity and save-speed improvement remain
unverified.

**Automated validation:** The baseline exact presentation filter passed 3/3 in 40.68 seconds including its build. The first
optimized exact run passed 3/3 in 28.86 seconds. An initial broader run passed 44/45: the local movement test detected shared
economic-coordinator invalidation of both systems. An exact diagnostic reproduced that mismatch; participant-scoped
publication corrected it and the exact regression passed 1/1 in 19.30 seconds including its build. The subsequent affected
presentation, handoff, failure, resident economy/fitting, checkpoint and command-burst filter passed 90/90 in 79.86 seconds.
Formatting initially reported two long test-report literals; they were split and width verification passed. Final checks and
matched measurements follow below. No full .NET suite is authorized or claimed.

The added commit-during-capture exact regression passed 1/1 in 22.17 seconds including its build. Final tool/solution
restores, the solution build with zero warnings/errors, 191/191 affected .NET cases, matched replay equivalence, both
Python policy suites (8 and 9 cases), formatting verification and added documentation links passed in 194.67 seconds.
The final focused test command took 137.83 seconds. New resident-world coverage includes rollback, committed same-tick
damage, precise recharge, timed-effect expiry, paid repair completion, local-versus-remote invalidation, catalog invalidation
and publication during capture. The final filter was:

```text
(FullyQualifiedName~CapsuleerPresentationTests|FullyQualifiedName~CapsuleerTrafficPerformanceTests|FullyQualifiedName~CapsuleerHandoffTests|FullyQualifiedName~OccupiedCapsuleerSimulationTests|FullyQualifiedName~CapsuleerFailureTests|FullyQualifiedName~CapsuleerMaintenanceTests|FullyQualifiedName~CapsuleerDeadlineTests|FullyQualifiedName~CapsuleerDeadlinePerformanceTests|FullyQualifiedName~CapsuleerTrafficNavigationTests|FullyQualifiedName~CapsuleerInboundGateAcceptanceTests|FullyQualifiedName~CapsuleerSchedulingTests|FullyQualifiedName~CapsuleerDefenseFrameTests|FullyQualifiedName~CapsuleerDefensePerformanceTests|FullyQualifiedName~ResidentBotEconomyTests|FullyQualifiedName~LiveWorldTests|FullyQualifiedName~ResidentEconomyTests|FullyQualifiedName~ResidentFitTests|FullyQualifiedName~ShieldBoostTests|FullyQualifiedName~ArmorRepairTests|FullyQualifiedName~CommandBurstTests)&FullyQualifiedName!~ReceivingOnlyObserversSeeRealInboundTransfersAndRepeatedArrivalAtTheWatchedGate
```

The first final-validation sequence was stopped when both variants of
`CapsuleerInboundGateAcceptanceTests.ReceivingOnlyObserversSeeRealInboundTransfersAndRepeatedArrivalAtTheWatchedGate`
failed. The exact method was then run in an isolated unchanged `d45c5a0c96649cbe5a39161f78de45c9d8a1a443` checkout:
both failures reproduced in 41.82 seconds including its build. The restart variant has no second visitor row at line 399;
the fresh variant has no arrivals at line 318. These receiving-only expectations predate the occupied-system rule requiring
both ends of a visitor lane to be occupied. The temporary baseline checkout was removed after verification. Final validation
excludes only that method's two variants and retains the limitation; it does not describe them as passing acceptance.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** None for server observation reuse. Native rendering remains unverified.

## Bot failure isolation - November 16, 2026

Unexpected role failures now defer only the failing pilot. Retry eligibility uses the active simulation clock with
1, 2, 4, 8, 16 and then 30 second delays. The queue still orders eligible work by deadline and pilot identity, retains
one entry per pilot and honors its action/time limits. Healthy pilots can execute while another pilot is backing off.
Population diagnostics retain the pilot, action, exception type and reason; maintenance keeps its existing error log.

Retry eligibility is runtime scheduling metadata. It never replaces a saved action deadline, revision, paid cycle or
economic transaction. Queue rebuilds preserve backoff for unchanged jobs; a successful action, newer revision or withdrawn
pilot clears it. Restart resets backoff and retains the checkpointed authoritative job. A failure after settlement restores
the committed job before deferring it, so observation failure cannot replay a payment, undock or inventory change.
Role workers reload authoritative checkpoints on retry. Preparation capture and unexpected optional-planning failures also
defer their owning pilot; a stale optional plan cannot penalize a newer committed job. Expected gameplay rejections retain
their existing typed waiting behavior.

This is Eden host scheduling policy. The [EVE University NPC mining reference](https://wiki.eveuniversity.org/NPC_Mining_Operations)
was checked for gameplay context. Navigation and wire formats are unchanged; the existing
[Carbon Destiny revision 114e89fa584f0e1a81eb0a679aabf41af8870072](https://github.com/carbonengine/destiny/blob/114e89fa584f0e1a81eb0a679aabf41af8870072/src/Ballpark.cpp)
and build-3662168 evidence below still apply. The local reference engine's ambient materialization and Destiny tick driver
were also inspected; no reference implementation was copied.

**Automated validation:** The initial exact filter
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerFailureTests.PostCommitObservationFailureDoesNotReplayTheActionAndHealthyPilotStillRuns`
passed 1/1 in 27.01 seconds including its build. The first combined failure, scheduling, runtime, deadline and preparation
filter passed 45/45 in 32.32 seconds including its build. New regressions cover repeated capped backoff, healthy-job progress,
canonical deadline retention, queue rebuilds, stale failures, successful recovery, overflow, withdrawal, failures before
settlement, failures after settlement and reopened checkpoints. Final tool/solution restores, the solution build with zero
warnings/errors, 92/92 affected .NET cases, both Python policy suites (8 and 9 cases), and formatting verification passed
in 102.41 seconds. No full .NET suite was run. The final filter was:

```text
FullyQualifiedName~CapsuleerFailureTests|FullyQualifiedName~CapsuleerSchedulingTests|FullyQualifiedName~CapsuleerRuntimeTests|FullyQualifiedName~CapsuleerDeadlineTests|FullyQualifiedName~CapsuleerDeadlinePerformanceTests|FullyQualifiedName~TrafficPreparationTests|FullyQualifiedName~CapsuleerTrafficPerformanceTests|FullyQualifiedName~OccupiedCapsuleerSimulationTests|FullyQualifiedName~CapsuleerMaintenanceTests|FullyQualifiedName~CapsuleerHandoffTests|FullyQualifiedName~CapsuleerLockOrderTests|FullyQualifiedName~ResidentBotEconomyTests
```

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** None. This milestone changes server retry scheduling only.

## Population preparation retries - November 16, 2026

Unexpected system-preparation errors leave the system pending instead of marking it completed for the server session.
System errors and changed-blueprint retries use the same capped 1, 2, 4, 8, 16 and 30 second backoff as failed bot actions,
measured on the host's monotonic clock. Preparation visits eligible occupied systems in rotation, so a busy system yields
to another system. Successful partial batches clear their retry history and continue from committed roster slots.

Gate-visitor errors retain the exact pending request, including its visitor cursor, rather than marking the lane prepared.
Other gate requests and ordinary system preparation can run during its backoff. Actual unsupported-route/fit results retain
their existing explicit deferral behavior. Cancellation propagates without converting the request into a failure or completion.
Leaving removes retry state for inactive systems and gate requests; re-entry uses committed identities and jobs.
An observer leaving during an in-flight visitor plan does not mark that lane prepared; re-entry can resume its cursor.

When only cooling retries remain, preparation reports their next deadline. The maintenance worker waits until that deadline
or a membership change, canceling its previous timer on early wake or shutdown. Pending retries never report completion,
so the worker cannot sleep indefinitely waiting for an unrelated visit. Cooldown probes do not open SQL connections,
issue SQL commands or recalculate fits. Retry metadata remains transient; checkpointed founder grants, wallets, inventory
and job revisions remain authoritative. A restarted server reuses committed slots before preparing missing founders.

These are Eden scheduling rules. Gameplay references, pinned build and Carbon revision remain those recorded in
[bot failure isolation](#bot-failure-isolation---november-16-2026); this milestone adds no native wire or movement behavior.

**Automated validation:** The initial exact filter
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerPopulationRetryTests.FailedSystemRetriesWithoutCompletingOrBlockingAnotherOccupiedSystem`
passed 1/1 in 20.89 seconds including its build. New resident-world tests cover partial-roster recovery, healthy-system
progress, exact retry boundaries, doubled backoff, zero-work cooldown probes, checkpoint reopen, visitor identity/cursor
retention, cancellation, leaving/re-entry, busy-system rotation and retry-timer interruption/shutdown. A development check
command was interrupted to update the historical failure fixture, which previously expected a failed system to be complete;
that fixture now expects a pending retry. No result is claimed for the interrupted command. The combined retry, background,
inbound-provisioning, failure and scheduling filter then passed 45/45 in 37.73 seconds including its build. An additional
exact visitor filter passed 2/2 in 18.02 seconds, covering both exceptions and observer departure during preparation.
Final tool/solution restores, the solution build with zero warnings/errors, 87/87 affected .NET cases, both Python policy
suites (8 and 9 cases), formatting verification and added local documentation links passed in 94.72 seconds. The final
focused test command took 54.40 seconds. No full .NET suite was run. The final filter was:

```text
FullyQualifiedName~CapsuleerPopulationRetryTests|FullyQualifiedName~CapsuleerBackgroundPreparationTests|FullyQualifiedName~CapsuleerInboundProvisioningTests|FullyQualifiedName~CapsuleerFailureTests|FullyQualifiedName~CapsuleerSchedulingTests|FullyQualifiedName~CapsuleerTrafficPerformanceTests|FullyQualifiedName~OccupiedCapsuleerSimulationTests|FullyQualifiedName~ResidentPopulationTests|FullyQualifiedName~CapsuleerDeadlineTests|FullyQualifiedName~CapsuleerGateWatchTests
```

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** None. Server scheduling and saved-state behavior are covered by automated tests.

## Combat anomaly scouting on station departure — November 17, 2026

A saved traveler repeatedly failed its Trade job while preparing to undock. An automated replay of a copy of the
incident checkpoint reproduced the nested transaction in combat anomaly discovery. Loading a system's public site
list can create sites or advance their waves. Travelers now scout a candidate before the station transaction opens.
The ship's post-resupply fit, health and patrol priorities still decide whether it launches for that site.

The same job advanced and undocked on replay after the fix. The scheduler regression starts with an uncached
site list and verifies the ship's destination, a single job revision and zero scheduler failures. No live
gameplay acceptance was performed. [Combat anomalies](https://wiki.eveuniversity.org/Guide_to_combat_sites)

Focused validation filter

```text
FullyQualifiedName~PilotSpaceActivitiesTests|FullyQualifiedName~CombatAnomalyTests|FullyQualifiedName~RattingRulesTests|FullyQualifiedName~PatrolTests|FullyQualifiedName~RoamingGangTests|FullyQualifiedName~CapsuleerFailureTests|FullyQualifiedName~CapsuleerHaulerTests
```

The focused run passed 52 of 53 tests. The overpriced-ammunition shopping case also failed on the unchanged
main revision (5ade3cd4).

## Traffic returning to occupied systems — November 17, 2026

A player's presence in a traveler's home system keeps that pilot's route running through empty systems.
A player watching a stargate can also admit the configured gate visitors. Both ends share each saved visitor
identity. The unseen approach uses the reciprocal gate, preserves the ship and enters Local through the regular
system transfer. If a player is already in the departure system, new visitors begin at their source station.
Miners still work the belts of their home system. All routines pause when the last human leaves the world.

The receiving-side session checks follow distinct ships for one simulated hour, including after restart with a
visitor waiting at a station. The home hauler check also follows the same ship back after the only human leaves
and re-enters the home system away from the gate. Both one-hour fixture runs observed five distinct pilots and
12 arrivals. Live acceptance was not performed.

The combined combat, flight and traffic validation used this .NET test filter.

```text
FullyQualifiedName~PilotDroneTests|FullyQualifiedName~CombatDroneTests|FullyQualifiedName~GateActivityProtocolTests|FullyQualifiedName~FlightBoundaryTests|FullyQualifiedName~ShipCollisionTests|FullyQualifiedName~CapsuleerHaulerTests|FullyQualifiedName~CapsuleerInbound|FullyQualifiedName~CapsuleerBackgroundPreparationTests|FullyQualifiedName~OccupiedCapsuleerSimulationTests|FullyQualifiedName~CapsuleerGateWatchTests|FullyQualifiedName~CapsuleerPopulationRetryTests|FullyQualifiedName~CapsuleerTrafficPerformanceTests|FullyQualifiedName~CapsuleerSchedulingTests|FullyQualifiedName~CapsuleerPopulationSettingsTests|FullyQualifiedName~DronePerformanceReplayTests
```

## Occupied-system simulation - November 12, 2026

Each solar system runs its capsuleer jobs while at least one connected player has selected a character there.
Founders are prepared in bounded batches for occupied systems. Gate visitor preparation requires both ends of the gate to
be occupied and the visitor's starting station to be in the departure system. A pilot leaving an occupied system
completes its gate transfer and pauses at the destination if that system is empty.

The last player leaving removes that system's jobs from the active queue. Re-entry uses the pilot's physical location,
including arrivals from other communities, and shifts pending deadlines by dormant time without replaying missed work.
Saved identities, ships, cargo, paid mining cycles and gate transfer state carry forward. Older saves retain their last
settled pilot ticks and their existing cursor rows for database compatibility. A stopped server accrues no simulation time.

This supersedes the November 10 coarse scheduling and regional population preparation records below. Native client acceptance
has not been performed. The [NPC mining operations reference](https://wiki.eveuniversity.org/NPC_Mining_Operations) describes
in-system NPC activity. Eden's occupancy rule is a server scheduling policy.

## Coarse distant populations — November 10, 2026

Regional preparation still fills the existing saved rosters. It does not enroll untouched home communities simply because
their saved travelers already have journey descriptions. Visited communities and explicitly admitted gate visitors remain
eligible for remote work; schema 138 retains visitor admission separately from population preparation and restores existing
saved gate lanes. Identities and possessions are not replaced. This extends the earlier tiered simulation record and
supersedes the old per-pilot remote queue and startup-only activation descriptions below.

Unobserved eligible pilots share one due entry per physical system and a durable pilot cursor. Each transaction settles one
pilot and advances that cursor with its canonical job revision. Cursor conflicts reload committed state; failed settlements
retain their cursor and yield to other systems. Systems rotate between settlements, without accumulating historical jobs.
Regional preparation and coarse work share a soft 5 ms allowance per 50 ms, checked between complete settlements.
The saved detailed cutoff and per-pilot remote settlement time preserve a partially completed paid cycle across re-entry.

Remote mining collects existing paid work once and batches affordable subsequent cycles against the current rock and hold.
Crystal cycles remain individually accounted; no batch can exceed available capacitor, resources or cargo space. Travel
samples analytic boundaries, with authoritative route, gate and docking checks in the same transaction. Economic boundaries
use current stock, prices and payments. No remote bounty or combat result is invented. Existing ratting trips, combat,
missiles and drones retain the required detail until their interaction can finish; new autonomous ratting does not start
remotely. Optional legacy traffic template/cosmetic correction waits for detailed execution.

Re-entry prioritizes bounded current-time resumption and forgoes unprocessed remote time. Already paid cycles and committed
transfers survive. A second observer does not demote a system while another remains. Human-online time still excludes an
empty or stopped server. The Population page separates saved, detailed/urgent, coarse, pending and pinned work, including
remote age, budget overruns and system-specific return details with the longest skipped interval for that visit.
See the [performance record](performance.md#coarse-distant-populations-november-10-2026)
for measurements and acceptance limitations.

## Batched population startup — November 10, 2026

Normal startup no longer loads NPC jobs or creates missing rosters throughout the galaxy. Regions activate on player entry:
the entered system restores or creates its population first, then the remaining systems in that region prepare in bounded
background batches. Untouched regions stay dormant. Activated regions remain available for the rest of the server session;
after restart they wait for entry again, retaining all saved identities and possessions. Existing on-visit preparation remains
available. The Population page and console distinguish saved coverage from activated regions.

The scheduler restores only explicitly activated communities and individually required gate visitors. It does not hydrate
saved hauler routes and jobs for untouched regions. Activation shifts saved hauler deadlines using typed pause rules without
hydrating their fits or replaying dormant time. Saved-limit restoration uses shared geography counts rather than performing a
route search for every saved community. The background worker waits on human membership changes when its entered-region queue
is empty, and wakes for subsequent region entries instead of exiting permanently or polling SQL.

The background worker snapshots roster/identity inputs briefly under the live world owner, validates hauler blueprints in its
separate memory workspace outside that owner, then refreshes reservations and commits one community under ownership.
If a concurrent reservation changes the required fit, it rolls back and revalidates next turn. Newly committed jobs register
incrementally with the running scheduler. Cancellation before commit publishes no pilot. Failed communities stay pending for
visit/restart recovery while other communities continue. Live checkpoints persist completed work through the usual save path.
Turns check at most 16 systems or a 25 ms scheduling budget, yielding for 100 ms between turns; an individual community can
exceed that budget, so it is not a guaranteed foreground latency ceiling. Completed queues stop without repeated SQL polling.

The reported 2× startup and Rider Dynamic Program Analysis screenshot exposed repeated catalog lookup,
traffic selection, fitting finalization, ammunition loading and supply setup during global expansion.
The screenshot identifies repeated operations; its displayed issue values are not treated as CPU timings.
The attached DPA stream was preserved outside version control. The measurements below come from automated
copied-save replays, rather than an exported timing report from that stream.

Offline reconciliation now pins one catalog generation and read connection. Region/security/route-compatible
traffic pools retain their newest compatible observations and hull groups across pilot selections, with lazy
fallback tiers and an exhausted-pool marker valid only while startup reservations grow. Selection keeps the
existing world-seed ordering, light-hull allowance, unique imported characters and name checks.

New haulers use a separate in-memory SQLite workspace to construct typed ship blueprints through the normal
skill, fitting, ammunition and supply rules. A bounded cache retains up to 128 race/complete-fit blueprints;
ammunition quantities are part of the identity. Skills and inventory are materialized with parameterized bulk
inserts, remapping every ship/module/container identity. Health, capacitor, reload delays, starter cargo,
drone supplies and supply markers are preserved. Scratch creation rolls back; no scratch identity is saved.
Saved pilots retain their possessions and jobs. The existing per-community transaction bounds each durable
batch, so interrupted expansion reuses completed communities and rolls back the incomplete community.

Complete fitting initialization on an empty hull now shares its initial resource checkpoint with final online
validation instead of finalizing an intermediate offline fit. Full magazines of the same ammunition type load
together through the normal ammunition owner; partial magazines keep separate loads and exact recorded counts.
Slot, hull, hardpoint, rig, CPU/power, online-limit, charge and cargo checks remain authoritative.

Gameplay reference: [EVE University NPC mining operations](https://wiki.eveuniversity.org/NPC_Mining_Operations).
This changes preparation and persistence, without changing flight simulation or wire formats. The existing
pinned Carbon simulation evidence remains applicable; no Carbon behavior is reimplemented here.

**Automated validation.** Focused regressions compare ordinary and batched ship grants, full and partial
magazines, skills, supplies, health, capacitor, reload deadlines and reopened persistence. A warmed blueprint
performs zero fitting calculations; the prepared 10,000-profile selection pool does not repeat compatibility
work while selecting 100 system/slot variants. Global tests cover retained/lost ships, lowered and raised
limits, imported identities, interrupted transactions, cancellation and cached restart.

The opt-in replay is
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerStartupPerformanceTests.CopiedSaveMeasuresBoundedTwofoldExpansion`.
Set `EDEN_STARTUP_PERFORMANCE_SOURCE` to a stopped or frozen copied save and optionally
`EDEN_STARTUP_PERFORMANCE_SYSTEMS` (1–128, default 12). The test takes another SQLite backup and copies its
catalog/snapshot, activates 2× only in that disposable world and selects incomplete rosters deterministically.
The source world and running server are not changed by these replays. Whole-galaxy startup, live rendering and gameplay feel
are not established by this bounded replay. EF counters include work inside the memory validation workspace.

**Live acceptance.** Not performed; agent client testing is disabled.

Final validation: tool and solution restore, solution build (zero warnings/errors, 5.29 s), both Python policy suites
(8 and 9 cases), formatting (1,580 files) and line-width verification passed. The final targeted run passed **50 tests**
in **22.89 s** including runner startup (feature build 3.98 s). It covers regional creation/restoration, cancellation,
restart, ownership/checkpoint behavior, schema migration, indexed ammunition reads and navigation.
The broader affected-feature run was interrupted after two navigation fixtures assumed startup-loaded jobs; both were
corrected to activate their fixture system explicitly and passed in the final targeted run. No full .NET suite was run.
The final filter was:

`FullyQualifiedName~CapsuleerBackgroundPreparationTests|FullyQualifiedName~CapsuleerRuntimeTests|FullyQualifiedName~CapsuleerHandoffTests|FullyQualifiedName~CapsuleerGlobalPopulationTests|FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerTrafficNavigationTests.WitnessedMultiHopAndStationlessTurnaroundKeepTheSameShipAndPersistCursor|FullyQualifiedName~AmmunitionLookupTests|FullyQualifiedName~WorldLookupTests|FullyQualifiedName~WorldMigrationTests`

**Optional maintainer checks.** Restart a rebuilt server and compare the preparation progress and Population
page at 2×; enter another region and confirm its entered system becomes available first.

## Population startup progress — November 10, 2026

Normal server startup now reports NPC population preparation with a 20-cell ASCII loading bar,
percentage, checked-system count and deferred-system count. Startup first identifies saved-world/settings
loading and saved-pilot reuse. Preparation progress is reported synchronously after each community transaction
commits, through a typed optional callback from World to Server. Cached systems count immediately; a fully
cached or empty catalog reports 100% immediately. Bounded preparation stops below 100% when work remains.
An exception or cancellation does not report an unprocessed system as completed.

The host emits an ordinary buffered information log line at each 5% bar boundary and at most once per second
within a segment when counts change. This preserves readable redirected logs without terminal escape codes
or extra database queries. 100% means every catalog system was checked, including deferred rosters;
the existing final summary retains expanded/deferred counts. Offline maintenance commands do not show the bar.
Population policy, provisioning transactions, saved grants and retry behavior are unchanged.

**Automated validation:** The initial exact-method filter
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerGlobalPopulationTests.StartupProgressCountsCommittedSystemsAndResumesFromCachedSystems`
passed 1/1 in 16.12 s including its build. The first combined global-population/progress-reporter run passed
14/14 in 22.13 s including its build. Coverage includes separately readable committed counts, bounded preparation,
cached restart, cancellation, interrupted provisioning, deferred rosters, empty catalogs and throttled log output.
Final tool/solution restore, solution build (zero warnings/errors), 45/45 focused .NET cases, both Python policy
suites (8 and 9 cases) and formatting verification passed in 44.10 s total. The full .NET suite was not run.
The final filter was:

```text
FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerGlobalPopulationTests|FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerProvisioningTests|FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerPopulationSettingsTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting.PopulationStartupProgressTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting.BufferedServerLogTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting.ServerLogTests
```

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** On the next normal server start, inspect the startup bar in the preferred console.

## Simulated-pilot target lock retention, November 9, 2026

Peaceful simulated pilots no longer clear other pilots' completed target locks during defense maintenance.
`CapsuleerDefense.Stop` previously called `RemoveShip`, which removes every incoming and outgoing lock edge.
An unthreatened pilot runs this cleanup repeatedly, so a player's scan could complete and then immediately lose
its shared lock before a weapon activation. Stopping defense now removes only the outgoing locks recorded in
that defense state. Changing the simulated pilot's hull retains the existing full old-ship cleanup.

An aligning pilot remains targetable while its ship is in range. Actual warp entry still invalidates the target,
sends the normal `OnTarget("lost")` notification and stops weapon repetition. Docking, gates, cloak, range,
targeting limits and weapon legality continue through their existing owners. There is no save migration.

The automated regression publishes a simulated hauler through the real shared scene, completes the native
`dogmaIM.AddTarget` request, runs three peaceful defense/publication cycles with a controlled clock, activates
a real fitted weapon during alignment, and verifies lock loss and weapon cleanup on warp entry. Both resident
and database-backed world variants reproduced the premature lock loss before the fix and passed afterward.
A defense-lifecycle regression also asserts that outgoing cleanup preserves the player's incoming lock.

Gameplay reference: [EVE University targeting](https://wiki.eveuniversity.org/Targeting). The unchanged Destiny
movement boundary was checked against [Carbon Ball.cpp at 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp).
Build-3662168 targeting RPC and `OnTarget` forms reuse the existing named protocol models and pinned fixture
catalog; this correction changes shared lock ownership, without new wire fields or movement rules.

**Automated validation:** The initial exact-method filter
`FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerDefenseTests.NativeLockSurvivesPeacefulDefenseAndAlignmentUntilTheTargetEntersWarp`
failed both variants at the retained-lock assertion before the correction (11.38 s build, 6.69 s tests).
The first corrected run passed both variants (5.71 s build, 6.95 s tests).
Final tool/solution restore and solution build passed with zero warnings/errors in 6.44 s. The affected test
run passed 104/104 cases in 40.09 s with this filter:

```text
FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerDefense|FullyQualifiedName~Eden.Protocol.Tests.Targeting.TargetingTests|FullyQualifiedName~Eden.Protocol.Tests.Combat.PilotCombatTests|FullyQualifiedName~Eden.Protocol.Tests.Combat.PilotTurretTests
```

Both Python policy suites passed (8 catalog and 9 formatting-policy cases, 1.02 s). Changed documentation
encoding, whitespace, links, the new section anchor and `git diff --check` passed. Formatting/style and the
140-character source-width verification passed in 22.27 s. Full .NET regression was not requested or run.

**Live acceptance:** Not performed; agent client testing is disabled. The user's report that simulated pilots
could be locked and then immediately unlocked, including while leaving a station, prompted this correction.

**Optional maintainer checks:** Lock a nearby simulated pilot during station departure and activate a weapon
before actual warp entry, subject to the ordinary safety and aggression rules; confirm the lock clears on warp.

## Deadline-driven maintenance, November 9, 2026

The resident host now waits for the next queued job or an input-change signal instead of polling traffic every 50 ms.
`ICapsuleerScheduler.NextWake` returns a relative wait and a captured change task under the same owner as the queue
and human-active clock. No pending job, or a paused clock with future work, leaves the worker asleep without a timer.
The default interface implementation retains polling for alternate schedulers. The existing two-argument hauler
schedule method remains available; the new overload adds the background-mode input.

Positive waits below 50 ms are batched at the existing maintenance resolution, preventing staggered populations
from requiring one timer wake per pilot. Persisted deadlines and the authoritative clock retain their exact values.
Already-due backlog still drains in bounded, yielding turns. Player selection, committed world changes and queue-head
changes interrupt a later wait. Read-only synchronization leaves an unchanged schedule asleep. A signal arriving
between bounded jobs receives another turn, and no owner is held across a wait. Failed actions retry after 50 ms.
Shutdown cancels the wait and disposes its one-shot timer; repeated disposal is supported. Very long saved deadlines
use bounded timers and are re-evaluated without executing future work early.

Committed aggression and entry into a previously unobserved system can move an already-running pilot's pending job
to the current human-active clock. This increments its revision without consuming its action sequence or paid work.
Entry only requests sampling when the pilot's progress is behind that clock; restarting and entering at the same
saved time does not consume another action.
The revised job participates in ordinary detached checkpoints and transaction revision checks. Dormant local encounters
retain their existing pause/resume behavior. Background haulers without active defense now sleep through an in-space
retry until its saved deadline. Observed or defending pilots retain their existing polling and finer defense deadlines.
Warp, gate, movement, danger and payment rules, population policy, schema and native replies are unchanged.

Reference checks used [EVE University warp mechanics](https://wiki.eveuniversity.org/Warp_Mechanics),
[NPC mining operations](https://wiki.eveuniversity.org/NPC_Mining_Operations), and
[Carbon Destiny Ballpark.cpp at 114e89fa584f0e1a81eb0a679aabf41af8870072](https://github.com/carbonengine/destiny/blob/114e89fa584f0e1a81eb0a679aabf41af8870072/src/Ballpark.cpp#L6216).
Carbon's inactive-bubble optimization motivates avoiding idle work; this increment changes host scheduling and safe
retry waits rather than skipping authoritative movement or threat simulation.

### Matched scheduling replay

The deterministic Debug replay compares fixed polling with deadline eligibility on the same retained role code.
Each arm runs 200 fifty-millisecond slots over ten human-active seconds, plus normal one-Hz human session frames
and the native encoding sink. Both arms perform the same deadline probes, isolating omitted maintenance turns.
One arm calls complete maintenance/presentation each slot; the other calls it for due work or a changed input.
The actual asynchronous host's timer, cancellation, batching and lost-wakeup behavior are tested separately with
a controlled `TimeProvider`. Each populated fixture has two local simulated pilots and one or two human observers.
The empty-system observer leaves the saved local encounters dormant.

| Scenario | Polling / deadline turns | Polling / deadline processing, ms | Polling / deadline managed allocation, MiB |
| --- | ---: | ---: | ---: |
| Empty system, one observer | 200 / 0 | 13.12 / 4.05 | 4.78 / 1.35 |
| Populated system, one observer | 200 / 11 | 24.17 / 13.56 | 7.61 / 3.55 |
| Populated system, two observers | 200 / 11 | 44.05 / 17.05 | 10.56 / 4.99 |

Final jobs, action sequences, due times, pilot positions, role progress, cargo and encoded byte counts match.
Each populated arm issues 25 SQL commands, opens 13 connections and performs zero fitting calculations; mutations
remain at their existing boundaries. Empty arms issue zero commands, opens and fitting calculations. Warm wake-plan
reads also assert zero SQL, connection opens and fitting calculations. The quiet session frames emit no changed
packets after warm-up; encryption, network backpressure and native rendering are outside this comparison.
The samples establish less idle maintenance work, not a new simulated-pilot capacity or dense-battle guarantee.
Existing bounded-queue tests cover 1,804 and 10,000 logical jobs, separately from these small real-world fixtures.

In the two-observer replay, checkpoint capture/projection/write are 0.41 / 2.23 / 12.61 ms with polling and
0.42 / 1.81 / 12.60 ms with deadline eligibility. Projection and writing remain outside the owner. No save-speed
improvement is claimed. Raw matched reports are retained under ignored `artifacts/deadline-work`.
Set `EDEN_DEADLINE_OUTPUT` to an absolute directory and run the exact filter
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerDeadlinePerformanceTests.MatchedMaintenanceFramesPreserveJobsAndAssetsWithFewerIdleTurns`
to reproduce all three cases.

**Automated validation.** The first exact filter was
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerMaintenanceTests.OneWakeDrainsMultipleBoundedTurnsWithoutWaitingForAnotherTimerTick`.
New checks cover paused worlds, early timer callbacks, subsecond and long deadlines, signal cancellation, changes
during execution, bounded failure retries, duplicate disposal, early aggression, saved retry waits and reopened
checkpoints. Development shutdown regressions exposed repeated-disposal and timer-cleanup failures; the retained
worker owns its timer explicitly and the corrected cases pass. The first wider run passed 454 cases and failed two
entry/restart cases: the entry expectation now distinguishes a runnable traveler from a paused miner, and same-time
restart entry no longer schedules a redundant sample. Both corrected cases passed individually. Final validation on
main `6ab9f423` plus these changes passed all 456 affected .NET cases in 234.32 seconds including test host startup
and shutdown. Tool/solution restore, solution build with zero warnings/errors, eight catalog policy cases, nine
formatting policy cases and formatting verification passed. The sequence took 272.69 seconds, excluding the earlier
dedicated scheduling replay. Documentation links, retained headings, UTF-8 and whitespace checks passed. The filter was:

```text
FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~Persistence.LiveWorldTests|FullyQualifiedName~Persistence.DetachedCheckpointTests|FullyQualifiedName~Combat.LiveCombatObjectTests|FullyQualifiedName~Combat.NpcCombatTests|FullyQualifiedName~Combat.NpcEwarTests|FullyQualifiedName~Combat.PlayerFrameWorkTests|FullyQualifiedName~Combat.PlayerEwarLifecycleTests|FullyQualifiedName~Combat.ResidentPollingTests|FullyQualifiedName~Hosting
```

This includes the concurrently integrated ratting, paid resupply and activity-lifecycle checks. A final integration
build also passed after main `4e88e757` added unrelated agent and courier fixes. The full .NET suite was not run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** None for this scheduling change.

## Persistent local miner — November 8, 2026

Eden provisions one world-owned miner by default on new and upgraded saves. Its home is selected once from the earliest human pilot's saved home, searching by gate distance for a highsec system with a station and generated mineable ore. Systems and stations use catalog ID ordering, with preference for the pilot's home station. The founder is deferred when there is no human pilot or eligible catalog location. Normal character creation retries provisioning inside its transaction.

The one-time founder grant is a Venture, Miner I, required fitting skills and 10,000 ISK. The pilot inherits the founding human's racial background and uses the school NPC corporation, falling back to the home station owner. Its negative account ID has no launcher credentials. The community record prevents repeat grants. Existing development supply settings continue to apply to their world.

The miner fits through ordinary fitting operations, undocks, aligns and warps using shared NavigationState, approaches and locks ordinary ore, pays each laser cycle through PilotMiningStore, fills its mining hold, returns to its permanent home and unloads into its personal hangar. Ore remains stockpiled. Depleted targets are replaced. Mining payments, collection and saved progress share transactions. A failed collection preserves the ore and is retryable. Checkpoints capture navigation and active-time progress with the resident world generation, then project them outside the world owner.

Server scheduling advances one active second per admitted tick while a selected human pilot is online, including docked pilots and pilots elsewhere. There is no offline trip or mining catch-up. Local membership, station guests, Show Info, ship contacts, warp and laser effects use the existing native presentation. Player damage signals and visible NPC aggression initiate retreat through ordinary warp and docking. NPC fire and missile impacts use the existing loss settlement. NPC pursuit is released when the pilot enters warp or docks. Damage events for other ships preserve an already observed threat to the miner. A pilot whose Venture is lost returns in its capsule and waits docked for replacement equipment.

The miner currently retains its starting skills without autonomous training decisions. Replacement purchasing and autonomous pirate hunting are subsequent milestones. The hauler milestone below extends this first miner record with ordinary ore sales and delivery.

Gameplay reference checked against [EVE University mining](https://wiki.eveuniversity.org/Mining). Native flight presentation reuses the existing Destiny implementation and [Carbon Ball.cpp revision 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp). Build-3662168 Venture and Miner I attributes and required skills are exercised with the committed starter-production catalog extract.

**Automated validation** — Tool and solution restore passed. Solution build passed with zero warnings/errors in 1.13 seconds. The affected filter was `FullyQualifiedName~CapsuleerMinerTests|FullyQualifiedName~MiningTests|FullyQualifiedName~MiningTierTests|FullyQualifiedName~CharacterCreationTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~ChatWorldTests|FullyQualifiedName~TypedFlightTests|FullyQualifiedName~LiveWorldTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~PilotCombatTests`. This selected 202 cases, with 196 passing and six existing NPC-mining restart cases failing because their resident world was still open. Those two test methods now stop/checkpoint the resident owner before reconciliation. Their focused rerun passed all 12 variants in 17 seconds, 21.59 seconds including runner startup. Both Python policy suites passed, eight catalog and nine formatting cases. Formatting verification passed.

The measured warmed miner frame took 1.17 ms with zero SQL commands, connections or fitting calculations. The empty-server frame took less than 0.01 ms with the same zero-work counts. In that fixture, checkpoint capture took 1.95 ms, projection 23.65 ms and writing 14.81 ms. These are diagnostic fixture measurements, not capacity or native-client claims. Tests cover two observers, paid-cycle pause/restart, collection rollback, founder grants across concurrent character creation, retreat/restart, depletion and ordinary ship loss. Warm paid-cycle and empty-server frames assert zero SQL commands, connections and fitting calculations. Checkpoint capture, projection and writing are reported separately. The full .NET suite was not requested. A subsequent pursuit-release correction passed the 28-case miner/NPC-combat filter in 14 seconds, followed by the six miner regressions after adding unrelated-damage threat coverage.

**Live acceptance** — Not performed; agent client testing is disabled.

**Optional maintainer checks** — Observe the same pilot in Local, station guests and Overview. Follow one mining round trip and verify the Venture's warp and laser rendering.

## Local ore hauling — November 8, 2026

The default community adds Kira Auvinen, a persistent Badger pilot with a one-time 100,000 ISK wallet and the skills needed to fly the ship. Names receive a numeric suffix if already taken. World schema 115 adds a hauler founder ledger and saved work. Existing miner identities, home stations and possessions are retained. Startup or first-character provisioning selects the lowest-ID adjacent highsec system with reciprocal gates and a market station, then the lowest eligible station ID. Both source and destination must provide the catalog market service. A community without an eligible route defers the hauler grant. The route is saved permanently and repeated provisioning preserves the founder and starting assets.

After unloading, the miner lists a batch of actual ore through personal market orders. The initial quote policy uses the catalog type's reference price divided by its portion size. It caps each source offer at 100,000 ISK gross and checks ordinary brokerage funds and order limits. The hauler purchases an affordable quantity through the normal matching engine, retaining funds for destination brokerage. Cheaper competing orders and human buyers participate in the same matching rules. If another buyer takes the ore, the hauler waits. It does not receive replacement goods or money.

The Badger loads its own purchased stack, undocks, aligns and warps to the gate, approaches activation range, waits the native ten-second departure interval, transfers through the reciprocal gate and flies to the destination station. The hauler then unloads and lists the delivered stock at a target 25% markup over the source reference price. It returns for another trip. Unsold orders retain their goods in ordinary station escrow. Limited capital, fees, order limits and losses can stop further buying. Price targets are bot decision defaults and do not guarantee a profitable sale. Expiration, cancellation, fills, wallets and inventory retain the existing personal-market behavior.

Human and autonomous gate transfers share typed activation-range and arrival rules plus the World transfer operation, including active-ship ownership, reciprocal gate identity and Crimewatch validation. The existing position operation retains travel task tracking and heat cancellation. Protocol scene state is now held per capsuleer, allowing the miner's beam and the hauler's movement to coexist. Local membership changes only when the hauler crosses systems. Station guests, Show Info, gate departure/arrival and late observer snapshots use existing native forms.

Work advances through the same selected-human active clock as mining, including docked or remote human pilots. Empty worlds pause travel and buying. Checkpoints capture the hauler's navigation, current task and remaining jump time with the resident world generation. A committed purchase can be recovered from owned hangar inventory after a loading failure, preserving one payment. Normal NPC combat and player damage observations can prompt retreat to the station on the current side of the route. Tackle affects speed and warp eligibility. Ship and cargo destruction use normal settlement. A surviving capsule returns to the home station and waits for a replacement instead of receiving another founder grant.

Gameplay was checked against [EVE University's hauling reference](https://wiki.eveuniversity.org/Hauling). Navigation reuses Eden's existing implementation, checked against [Carbon Destiny Ball.cpp revision 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp). Gate presentation uses the existing build-3662168 JumpOut/JumpIn effects and peer departure/arrival flags. The pinned starter-production extract supplies Badger skills and cargo and the ore type metadata used in automated tests.

**Automated validation** — Tool and solution restores passed. Solution build passed with zero warnings/errors in 4.99 seconds. Filter `FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~PersonalMarketTests|FullyQualifiedName~PersonalMarketRulesTests|FullyQualifiedName~FlightSessionTests|FullyQualifiedName~FlightBoundaryTests|FullyQualifiedName~TypedFlightTests|FullyQualifiedName~MobilityHeatTests|FullyQualifiedName~LiveWorldTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~CharacterCreationTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~ChatWorldTests|FullyQualifiedName~Eden.Protocol.Tests.Combat.NpcCombatTests` passed 213 cases in 58 seconds, 60.70 seconds including runner startup. Both Python policy suites passed, eight catalog and nine formatting tests. Formatting verification passed.

A warmed combined community frame took 0.76 ms with zero SQL commands, connections or fitting calculations. The empty-server frame took less than 0.01 ms with the same zero-work counts. The shipment fixture checkpoint measured capture at 1.20 ms, projection at 29.71 ms and writing at 35.60 ms. These are fixture diagnostics rather than a capacity claim. The revised shipment regression, including warmed in-flight observer counts and waiting for an unsold order, passed separately in seven seconds after the 213-case affected run. The warmed in-flight observer frame took 0.77 ms with zero SQL commands, connections or fitting calculations. The warm-up includes the observer refresh after the warp transition. Tests trace a full mining trip through market purchase, cargo loading, a restart during gate departure, destination escrow and an empty return trip. They also cover a human buying first, migration from schema 114, a failed load after a committed purchase, lost cargo, gate rejection with unchanged state, Local/space observer transfer, active-time pause and warmed SQL/fitting counts. Checkpoint capture, projection and writing are measured separately. No full .NET suite was requested.

**Live acceptance** — Not performed; agent client testing is disabled.

**Optional maintainer checks** — Watch Kira's Badger depart and arrive at the paired gates, then inspect the delivered ore order at the destination market. Native rendering and flight feel remain unverified by the agent.

## Population and scheduling foundation — November 8, 2026

The first increment of the [global population plan](../capsuleer-population.md) introduces schema 116. Source-system community records and unique community/career founder records register the existing miner and hauler. Migration preserves their original identities, ships, grant metadata, wallets and authoritative paid/travel progress. Registry reads return metadata without fits or native scene loading. New-character provisioning registers founders in the same transaction. No additional global communities are provisioned in this increment.

A saved human-active clock now runs from selected flight-session leases. Docked pilots count, transfers change the active-system set, and selection disposal releases presence even when chat transport is unavailable. Chat continues to present Local membership but no longer owns the activity clock. The clock counts overlapping humans once, preserves subsecond samples, pauses when the last selection leaves and resumes from a checkpoint without offline catch-up. Clock capture shares the resident generation with the relational image; detached projection and failed-save retry preserve later resident time. Maintenance uses this clock to admit the existing miner/hauler ticks.

Typed job records, stable due ordering, stale-revision rejection, a 64-action limit and a 10 ms soft execution budget establish the scheduling rules. One ordered queue entry per agent bounds storage during repeated rescheduling. An atomic settlement operation compares the complete expected job and commits feature writes with the next deadline/revision/sequence. Failure rolls both back; duplicate callbacks do not invoke the feature mutation. Jobs and the defined 50 ms wake interval are prepared for the role-execution increment. Current founders still execute through their existing state machines, and no scheduler jobs are created for them yet.

An interrupted-upgrade regression exposed an older migration step that wrote schema 68 even when opening a newer save. That step now writes its version only for saves older than 68, preserving the newer version when the later transaction fails so startup can retry safely.

Remaining work includes per-agent role execution, conversion of paid deadlines, synchronization barriers, active/background scene handoff and NPC encounter equivalence, global eligibility/provisioning, and administration/capacity reporting. The synthetic scheduling measurements cover queue mechanics only. They do not establish full-population simulation performance.

Gameplay references were checked against [EVE University mining](https://wiki.eveuniversity.org/Mining) and [hauling](https://wiki.eveuniversity.org/Hauling). This increment uses the existing navigation, native presentation and gameplay rules documented above; it introduces no new wire format or Carbon-owned simulation behavior.

**Automated validation** — Tool and solution restores passed. Solution build passed with zero warnings/errors in 4.45 seconds. Filter `FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~CharacterCreationTests|FullyQualifiedName~ServerServicesTests|FullyQualifiedName~DataModelOwnershipTests|FullyQualifiedName~GameplayDependenciesTests|FullyQualifiedName~TypedFlightTests|FullyQualifiedName~FlightSessionTests|FullyQualifiedName~XmppChatTests|FullyQualifiedName~LiveWorldTests` passed all 134 cases in 25 seconds, 27.20 seconds including runner startup. Both Python policy suites passed: eight catalog tests in 0.71 seconds and nine formatting tests in 0.02 seconds. Formatting verification passed. The full .NET suite was not requested.

New coverage exercises schema-115 migration and retry, unchanged founder assets and paid mining progress, atomic job rollback and duplicate callbacks, stable ordering, stale rescheduling, action/time budgets, overlapping selections, transfers, chat-independent docked presence, fractional clock precision, restart without offline progress, failed checkpoints and detached clock projection. Warm presence reads assert zero SQL commands, connections and fitting calculations; the existing warmed miner/hauler and observer checks remain in the affected filter. Synthetic queue restoration and the first bounded turn measured 14.80 ms for 1,804 jobs and 30.79 ms for 10,000 jobs in this run, including fixture assertions and initialization. These diagnostics measure queue preparation rather than a warmed frame or global simulation capacity.

**Live acceptance** — Not performed; agent client testing is disabled.

**Optional maintainer checks** — None for this foundation increment. Global population and native handoff acceptance remain pending implementation.

## Shared miner and hauler jobs — November 8, 2026

Both founders now run through `ICapsuleerScheduler` and its shared due queue. Server maintenance wakes every 50 ms; each turn retains the 64-action limit and 10 ms soft budget. The world owner serializes the queue, per-pilot workers and canonical job versions. Repeated/concurrent wakes cannot advance one deadline twice. Ship identity indexes route aggression without retaining destroyed hulls indefinitely. The dedicated role advancement methods are removed.

World schema 117 creates missing founder jobs once. Migration maps saved lock, mining, waiting and gate deadlines onto the existing human-active clock, retaining each phase's remaining duration and navigation origin. Navigation stamps derive from the saved clock offset. Paid mining retains its original capacitor payment and rebases the native cooldown when human activity resumes or the server reopens. Empty worlds pause, and restart reconstructs jobs without offline catch-up. Existing identities, ships, routes, cargo, wallets and one-time founder grants are preserved.

Economic and travel steps now share explicit transaction-aware feature operations with ordinary human commands. Fitting, inventory moves, market fills/fees, docking, gate transfers, funded mining and NPC disengagement commit with role progress and next-job version. A failed load rolls back the attempted purchase. A failed final job write likewise rolls back fitting, undocking, goods and money; retry commits one action. Resident positions, targets, object lists and navigation restore after rollback. Ordinary motion and nonfatal combat use resident state, while immutable job/progress arrays project with the captured checkpoint generation.

Docked retries sleep until their deadline. In-space navigation, locking, mining danger and jump departure retain one-second samples, with earlier subsecond completion deadlines honored. Native beam, travel and scene presentation use the existing adapters. This increment does not provide background event simulation, system activation barriers or global founder provisioning. Those remain the next increments of the [population plan](../capsuleer-population.md).

### Login freeze correction

During development, the maintainer reported the client freezing after character selection. A read-only managed stack capture of the running server showed a pilot update in `CombatAnomalyStore.List` waiting for `LiveWorld.Enter` while holding `WorldDatabase.MotionLock`. The scheduler simultaneously held the resident owner and waited for that movement lock in `NpcEncounterStore.List`; checkpoint and renewal threads also waited for the owner. NPC, anomaly, electronic-warfare, movement and translocation entry points now acquire the resident owner before the movement lock. The automated regression blocks the owner, starts each competing read, verifies the movement gate remains available, and releases all threads before asserting. A process already stuck in the old lock inversion requires a rebuilt-server restart. This diagnostic observation is not native gameplay acceptance.

Gameplay references were rechecked against [EVE University mining](https://wiki.eveuniversity.org/Mining) and the existing hauling record. The shared navigation sampler retains the behavior checked against [Carbon Destiny Ball.cpp revision 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp). Build 3662168 Miner I duration, capacitor/yield inputs, Venture/Badger capabilities, gates and market forms continue to use the pinned catalog and existing native models.

**Automated validation** — Tool and solution restores passed. The solution build passed with zero warnings/errors in 3.03 seconds. Final filter `FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~PersonalMarketTests|FullyQualifiedName~PersonalMarketRulesTests|FullyQualifiedName~InventoryTests|FullyQualifiedName~FittingTests|FullyQualifiedName~MiningTests|FullyQualifiedName~MiningTierTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~FlightBoundaryTests|FullyQualifiedName~TypedFlightTests|FullyQualifiedName~FlightSessionTests|FullyQualifiedName~LiveWorldTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~CharacterCreationTests|FullyQualifiedName~ServerServicesTests|FullyQualifiedName~DataModelOwnershipTests|FullyQualifiedName~GameplayDependenciesTests|FullyQualifiedName~InventoryStackSplitTests|FullyQualifiedName~InventoryPersistenceTests|FullyQualifiedName~FittingConstraintTests|FullyQualifiedName~FittingSnapshotTests|FullyQualifiedName~MobilityHeatTests|FullyQualifiedName~TranslocationTests|FullyQualifiedName~NpcEwarTests|FullyQualifiedName~Eden.Protocol.Tests.Exploration|FullyQualifiedName~PilotLoginTests|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~TargetLockSessionTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~ChatWorldTests` executed 646 cases: 645 passed and one existing anomaly-scenery assertion failed, in 4 minutes 50 seconds (292.57 seconds including runner startup). Every capsuleer and lock-order case passed. Both Python policy suites passed: eight catalog tests in 2.53 seconds and nine formatting tests in 0.04 seconds. Formatting verification and local documentation links passed. No full .NET suite was requested.

The known baseline failure is `IdleAnomalyFrameTests.IdlePodAndRemoteObserverHaveNoRecurringSqlOrSceneReloads(populated: True, nearScenery: True)`: capsule drift is 3,636.9244384765625 metres against its 0.01-metre assertion. The exact method filter was also run in an isolated unchanged `b772c424246a7a28267b490a2031a9607ced147c` checkout. It produced the identical drift failure, with two variants passing and one failing in 29 seconds (35.06 seconds including runner startup). This pre-existing scenery limitation is retained; the focused release run is not described as an entirely passing run. An earlier capsuleer-only run was interrupted on nested transactions; the transaction fixes and scheduler-fixture correction are covered by the final run.

New tests cover overlapping wakes, one settlement per revision, unchanged goods/funds/fitting/location after failed final job writes, purchase/loading rollback and restart, schema-116 paid-work migration, native cooldown rebasing, pause, and owner/movement-lock contention. Existing tests exercise complete mining and hauling trips, saved warp/gate progress, aggression retreat, ship/cargo loss, concurrent creation, notifications, and zero warmed SQL/connection/fitting counts. A not-due scheduler frame also asserts zero SQL, connections and fitting work. In the final concurrent test run, the miner checkpoint measured capture 1.30 ms, detached projection 51.88 ms and writing 21.35 ms; the hauler checkpoint measured 6.11 ms, 18.41 ms and 24.17 ms respectively. These fixture diagnostics do not establish global population capacity.

**Live acceptance** — Not performed; agent client testing is disabled. The existing blocked server was inspected through a managed stack capture only.

**Optional maintainer checks** — Restart the rebuilt server and retry character selection. Observe one miner and hauler round trip for native movement and beam presentation.

## Active and background handoff — November 8, 2026

The existing miner and hauler now share one canonical simulation across observed and unobserved systems. `SystemSimulationRegistry` records Background, Activating, Active and Deactivating modes, activation generations and the settled clock boundary. A selected human activates their system while docked or undocked; overlapping selections retain the same activation until the last observer leaves. Modes are connection lifetime state and rebuild after restart. Durable job revisions and the existing checkpoint generation continue to guard saved work, so schema 117 and existing founder grants remain unchanged.

Human dispatch, initial snapshots and system entry settle preceding due jobs through `ICapsuleerHandoff`. Asynchronous requests yield between bounded queue turns, then execute their synchronous mutation under the same owner as the final due check. Network output occurs outside that owner. Selection begins before the factory loads native objects; construction failure releases selection and presentation. Native flight motion now shares the authoritative synchronization owner, retaining owner-before-database-motion-lock ordering from the login correction. Failed entry preserves the old membership. Disconnect removes its lease even if a pending action fails, pausing the clock and retaining the due job for retry. Work already due at the frozen clock boundary can settle without advancing time.

Background jobs read cached collision/route geometry and resources instead of calling the native system-scene loader. Compact NPC simulation contacts carry identity, position and warp status without creating native NPC observer records. Shared acquisition, weapons, missiles, repairs and electronic warfare retain their authoritative records. Both careers advance NPC effects and honor shared tackle attributes; a disrupted miner cannot bypass its escape restriction by changing simulation mode.

Unobserved warps schedule the first strict exit sample of the existing analytic `WarpCourse`; gate departure schedules its saved transfer deadline. Entry partway through a scheduled warp samples its original origin, destination, start, position and velocity without executing arrival or revising the paid job. That resident sample participates in the ordinary immutable role checkpoint. Alignment, subwarp collisions, locking, mining danger and threatened work retain conservative one-second samples. Fully sparse combat scheduling, broader combat equivalence and global capacity are not established by this increment.

Committed presentation carries increasing batch revisions plus system generations. Background systems retain chat and station membership while native autonomous scene objects, movement orders and NPC observer caches are released. Entry rebuilds current native warp, mining beam and health state. An older captured batch cannot rematerialize a departed system after a newer batch is applied. Failed presentation retains committed observations for retry, merging newer observations by pilot; it does not execute the job or pay again. Canonical encounters, missiles and effect deadlines are never replaced with a presentation snapshot, and checkpoint capture/projection retains their existing generation rules.

Gameplay references were checked against [EVE University mining](https://wiki.eveuniversity.org/Mining), [hauling](https://wiki.eveuniversity.org/Hauling) and [NPC mining operations](https://wiki.eveuniversity.org/NPC_Mining_Operations). The shared navigation integration and analytic warp course retain the behavior documented against [Carbon Destiny Ball.cpp revision 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp). These are implementation references; no new native-client observations were collected.

**Automated validation** — Tool and solution restores passed. The solution built with zero warnings/errors; the final correction build took 3.57 seconds. Expanded filter `FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~NpcEncounterTests|FullyQualifiedName~NpcSceneUpdateTests|FullyQualifiedName~NpcPursuitTests|FullyQualifiedName~NpcOrbitTests|FullyQualifiedName~NpcEwarTests|FullyQualifiedName~NpcMissileTests|FullyQualifiedName~NpcCatalogSharingTests|FullyQualifiedName~NpcPopulationTests|FullyQualifiedName~NpcMiningTests|FullyQualifiedName~NpcMiningSessionTests|FullyQualifiedName~RegionalNpcTests|FullyQualifiedName~NpcConvoyTests|FullyQualifiedName~NpcGuardTests|FullyQualifiedName~NpcRepairTests|FullyQualifiedName~NpcAggressionTests|FullyQualifiedName~FlightSessionTests|FullyQualifiedName~FlightBoundaryTests|FullyQualifiedName~TypedFlightTests|FullyQualifiedName~TranslocationTests|FullyQualifiedName~LiveWorldTests|FullyQualifiedName~PilotLoginTests|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~ChatWorldTests|FullyQualifiedName~ServerServicesTests|FullyQualifiedName~GameplayDependenciesTests|FullyQualifiedName~DataModelOwnershipTests` passed all 566 cases in 3 minutes 42 seconds (223.72 seconds including runner startup). After tightening active warp sampling and adding metadata-only activation, filter `FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~FlightSessionTests|FullyQualifiedName~FlightBoundaryTests|FullyQualifiedName~TypedFlightTests|FullyQualifiedName~TranslocationTests|FullyQualifiedName~LiveWorldTests|FullyQualifiedName~PilotLoginTests|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~ServerServicesTests|FullyQualifiedName~GameplayDependenciesTests|FullyQualifiedName~DataModelOwnershipTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~NpcSceneUpdateTests|FullyQualifiedName~NpcEncounterTests|FullyQualifiedName~NpcEwarTests` passed all 313 cases in 2 minutes 3 seconds (124.76 seconds including startup). The catalog policy caught a direct station lookup; routing it through the shared typed catalog boundary was followed by filter `FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~ServerServicesTests|FullyQualifiedName~GameplayDependenciesTests|FullyQualifiedName~DataModelOwnershipTests`, which passed all 72 cases in 22 seconds (24.07 seconds including startup). The final named warp-limit cleanup passed exact filter `FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerHandoffTests.EnteringBackgroundWarpSamplesTheOriginalCourseWithoutExecutingItsArrival|FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerHandoffTests.ActiveAndBackgroundMiningHaveEqualOutcomesAtTheSameClockBoundary`: two cases in four seconds (6.74 seconds including startup), with the feature build passing in 4.92 seconds. Both Python suites passed after correction: eight catalog tests in 0.772 seconds and nine formatting-policy tests in 0.030 seconds. Formatting and local documentation targets were verified. No full .NET suite was requested.

New handoff coverage includes explicit generations, repeated entry/departure during alignment, approach, locking, mining and return, mid-warp sampling and continuing active observer refresh without executing arrival, exact gate transfer with unchanged cargo, overlapping humans, stale captured batches, paid-work restart, failed entry and initial scene construction, failed disconnect and presentation retry, retained NPC aggression/projectiles/effect deadlines, actual background warp disruption, and active/background mining equivalence at the same clock boundary. Metadata-only remote activation and ten warmed 50 ms maintenance wakes assert zero SQL commands, connections and fitting calculations, with no background native pilot scenes. Those counters cover warmed non-transactional frames; they do not claim that market fills, inventory transitions, combat settlement or initial hydration avoid SQL. Broader combat equivalence and full-population capacity remain unverified.

**Live acceptance** — Not performed; agent client testing is disabled.

**Optional maintainer checks** — Retry character selection with the rebuilt server. Enter and leave the miner's system during warp/mining, and the hauler's destination during a gate transfer, to inspect native movement, arrival and beam presentation.

## Global provisioning and login latency — November 8, 2026

Schema 118 removes the singleton role-table restriction and adds a saved provisioning seed plus catalog-wide, versioned eligibility. Startup creates missing communities in separate atomic transactions, with one Venture miner and one Badger hauler, actual supported deposits, catalog-valid origins and starting skills, owned equipment, ordinary wallets, fitted miner lasers and saved jobs staggered across 30 human-active seconds. New communities use deterministic unique names derived from the saved world seed, source system and career; case-insensitive collisions receive a suffix. Existing founders, owned assets, source routes, paid work and current jobs remain authoritative. Interrupted grants roll back the pair and resume once; provisioning neither replenishes saved depleted deposits nor replaces lost founder assets.

Every catalog system receives a policy/fingerprint record and an eligibility or deferral reason. The restart-only global setting controls additional grants while retaining established work. Provisioning is prohibited against a running resident world. Local administration now serves authenticated population coverage, per-system eligibility, waiting reasons and performance counters, including backlog age, failures and materialized pilot scenes. Routine diagnostics use immutable published snapshots; system transitions visit observed systems rather than cloning the entire catalog registry on every wake. Resident allowances include the autonomous population so a global pass does not continually evict fits and health views at the historical visitor-cache limit.

The reported login delay was investigated with read-only managed stacks and saved-state counts. The server was executing NPC danger/population work while character selection waited for synchronization; it was not the earlier owner/motion lock inversion. The selected-human clock had advanced about 156 seconds beyond the oldest jobs. Synchronization recomputed its target after every bounded turn, so an overloaded world could keep extending a login's catch-up window. It now freezes that request's entry deadline, releases ownership between turns and invokes the command after settling that boundary. Later advancement by maintenance cannot rewind settlement. Maintenance yields into further bounded turns while overdue instead of imposing another timer wait per turn.

NPC row changes previously cleared all systems' population schedules and membership caches. They now invalidate the affected old/current system, with a scoped refresh during seeding. Calm, stationary NPC miners with funded beams cache their next collection/pickup boundary. Changed ore and standings invalidate that deadline; hostile standings, navigation, alarms, hauling and unfinished mining retain conservative polling. Shared NPC mining, acquisition, weapons, missiles, repairs and effects continue to own their outcomes. No new native packet shape or Destiny motion rule is introduced.

Gameplay references: [EVE University NPC mining operations](https://wiki.eveuniversity.org/NPC_Mining_Operations) and [hauling](https://wiki.eveuniversity.org/Hauling). Existing navigation remains based on [Carbon Destiny Ball.cpp revision 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp). The installed build-3662168 catalog supplies the station, route, origin, ship, skill and ore metadata. The agent did not open the native client.

**Automated validation** — Tool and solution restores passed. The final solution build passed with zero warnings/errors in 4.16 seconds. Affected filter `FullyQualifiedName~Eden.Protocol.Tests.Mining.NpcMiningTests|FullyQualifiedName~Eden.Protocol.Tests.Administration.AdministrationTests|FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~Eden.Protocol.Tests.Combat.LiveSimulationTests` passed 141 cases in 69 seconds (71.79 seconds with runner startup). Separate exact checks passed the moving-clock login barrier, cross-system NPC cache retention, calm funded-mining/ore invalidation, both live/nonresident hostile-standing cases (two cases, 8.34 seconds including startup), and maintenance draining multiple turns from one timer wake (one case, 6.80 seconds including startup). Both Python policy suites passed: eight catalog tests in 0.724 seconds and nine formatting tests in 0.015 seconds. The final registry/entry-horizon exact filter passed two cases in 4 seconds (6.47 seconds including startup), following the final registry invariant correction. Formatting verification passed. No full .NET regression suite was requested.

The isolated installed-catalog diagnostic validated 8,490 registered systems, 902 eligible communities and 1,804 pilots with 959,077 prepared asteroid rows. Before the final calm-NPC scheduling correction, startup preparation took 104.98 seconds, host creation 602 ms, all-agent health warming 4.85 seconds and the cached all-agent pass 9.54 ms. Ten warmed full docked observer frames took 2.26 ms total with zero SQL commands, connections or fitting calculations; background native pilot scenes remained zero. The first count-bounded 64-action turn took 829 ms. Checkpoint capture/projection/write measured 50.60/30.27/393.34 ms separately. Those queue timings use TestClock and the action-count cap rather than a real elapsed 10 ms budget. A 35-second simulated run passed, but required 150.86 real seconds for 12,158 actions, motivating the subsequent calm-fleet deadline and maintenance catch-up fixes. These numbers are diagnostic evidence, not a final capacity claim.

At the user's request to push and continue in a new chat, the extended 60-second installed-catalog rerun was stopped. The larger synthetic population run was not performed. Repeat both after the final scheduler/NPC changes before claiming sustained global capacity. The default eight-system synthetic regression passed within the affected run. An earlier attempted concurrent feature build was blocked by the running diagnostic's DLL locks; the final build above succeeded after the diagnostic was stopped.

**Live acceptance** — Not performed; agent client testing is disabled.

**Optional maintainer checks** — Rebuild/restart the running server and retry character selection. Inspect population/backlog in local administration. Continue the installed-catalog and larger synthetic automated diagnostics in the next chat; use `EDEN_PROVISION_CATALOG`, `EDEN_PROVISION_SYSTEMS`, `EDEN_PROVISION_SECONDS` and `EDEN_PROVISION_OUTPUT` with `CapsuleerProvisioningScaleTests`.

## Startup and local login recovery — November 8, 2026

A disposable copy of the installed save contained 902 communities, 1,804 capsuleer jobs and 960,399 asteroid rows. All jobs were overdue, with an oldest delay of approximately 128 seconds. Before these changes, stopped-world preparation took 41.80 seconds with 20,050 commands and 19,952 connection opens. A global entry barrier remained pending after six seconds and issued 32,049 commands.

Eligibility reuse, batched catalog reads and indexed station/hull validation reduced measured preparation to 3.17 seconds on the first reconciliation and 2.88 seconds in a new process on the reconciled copy, with 181 commands and 43 connection opens. Resident host creation measured 1.23 seconds. A scoped entry into a system with no related capsuleer work dispatched in 13.22 milliseconds with one command and no fitting calculations. These are initialization and handoff measurements, not native end-to-end login timings.

The saved route index includes source and destination systems for each hauler. A local barrier runs the current residents and incoming route jobs through its frozen deadline. Account-level dispatch bypasses job advancement. Global maintenance can retain unrelated overdue work after a local command. Individual system settlement remains monotonic while the global watermark catches up. Gate transfer coverage asserts arrival, unchanged cargo and exactly one transfer even when unrelated jobs remain due.

Two 32-action maintenance samples on the copied save issued 3,932 and 4,627 commands, compared with 10,256 and 10,731 before the changes. The samples visited cold agents and systems and measured 6.99 and 2.38 seconds, respectively. They do not establish sustainable global throughput. NPC hydration now batches the four system-dependent row sets, position-only changes avoid full combat ship reloads, and background miner observations defer health hydration. Mining cargo volume is reused within each operation.

Checkpoint comparisons use the resident SQL versions. Detached saves retain differences across successive backups and failed projection retries. Role updates and job upserts use bounded parameterized batches. No wire packet or Destiny movement calculation changed. The existing gameplay references remain [EVE University NPC mining operations](https://wiki.eveuniversity.org/NPC_Mining_Operations) and [Carbon Destiny Ball.cpp revision 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp).

**Live acceptance** — Not performed; agent client testing is disabled.

The installed-catalog diagnostic completed with 8,490 registered systems, 902 communities and 1,804 agents. Fresh provisioning measured 66.26 seconds, resident host creation 530 milliseconds, and ten warmed docked frames 2.20 milliseconds with zero SQL commands, connection opens and fitting calculations. Background native scenes remained zero. The 59 subsequent one-second simulation windows completed 25,191 actions in 128.14 real seconds, with no simulation failures or due jobs remaining at the checked boundaries. Other automated tests ran concurrently with this diagnostic, so these timings do not establish isolated real-time capacity. Checkpoint capture, projection and write measured 84.59, 44.57 and 562.06 milliseconds, respectively.

**Automated validation** — The combined affected run passed 192 cases in 65 seconds. A subsequent funded-beam correction retained the existing early return before cargo reads while a beam is pending; its exact calm-mining, depletion/reopen and hold-capacity filter passed all ten cases in 17 seconds (18.81 seconds including runner startup). Its filter selected `Eden.Protocol.Tests.Capsuleers`, `Combat.LiveSimulationTests`, `Persistence.DetachedCheckpointTests`, `Persistence.LiveWorldTests`, `Mining.NpcMiningTests`, `PilotLoginTests`, `PilotBindingsTests`, `TypedSessionTests`, `CharacterLogoffTests` and `Hosting`. Exact regressions separately covered unchanged eligibility, renewed deposits, unrelated-system entry with a 128-second backlog, incoming gate transfer, bounded NPC hydration, moving-clock entry, detached projection retry and successive saves. The installed-catalog 60-second diagnostic passed in 3 minutes 39 seconds including provisioning and teardown. Tool/solution restores and builds passed, including the ordinary Debug output with zero warnings/errors. Both Python policy suites passed, eight catalog cases and nine formatting cases. Formatting verification passed.

An earlier broader PilotSessions selection was interrupted after the pre-existing `CharacterSheetServiceTests.NativeRemapUpdatesAttributesDogmaTrainingDeadlineAndClientCaches` assertion failed. The same exact failure was reproduced from unchanged commit `919bb37` in an isolated checkout. A subsequent broad selection was interrupted when the legacy nullable selection-update regression surfaced, then that regression was corrected and passed both its exact rerun and the final affected run. Build attempts overlapping diagnostic/IDE assembly ownership were blocked by file locks; later isolated and ordinary builds succeeded. No full .NET regression suite was requested.

**Optional maintainer checks** — Restart the rebuilt server and retry character selection, then enter a populated system to inspect native scene and movement presentation. Sustained global real-time capacity remains unverified.

## Observed capsuleer encounters — November 8, 2026

System entry now creates local supported encounters on demand. New miners begin a funded cycle at a real asteroid; haulers receive finite cargo once and fly their existing reciprocal station route. Existing founders, owned assets, orders and losses are retained. Empty systems execute no career work, startup grants no catalog-wide population, and command dispatch no longer drains jobs. Role checkpoints preserve remaining paid durations across absence and restart. One witnessed gate transition may complete after observers leave. Familiar hauling identities can reappear at the other reachable endpoint after an absence, preserving ship and cargo.

Native movement and serialization reuse the existing adapters and Carbon Destiny `27ee40b38a563d29054b32f64c3ebc87fabaec1f`, [Ball.cpp](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp). Gameplay references remain [EVE University mining](https://wiki.eveuniversity.org/Mining) and [hauling](https://wiki.eveuniversity.org/Hauling). These sources inform observed gameplay. Regional economic policy is independent of ambient career work.

**Automated validation** — Encounter coverage exercises shared first entry, already-funded mining, two-gate pursuit, unchanged owned cargo, inactive jobs, restart and zero warmed inactive SQL/connection/fitting/native-scene counts. The isolated installed-save copy retained 1,804 jobs: 100 inactive maintenance checks measured 0.96 ms total and zero SQL commands, connections and fitting calculations; shared dispatch measured 0.19 ms. These are isolated automation measurements, with existing active gameplay coverage checked separately. The affected capsuleer, pilot-login, administration and architecture filter passed 88 cases in 26 seconds (28.31 seconds including startup). The final presence-rollback correction passed the ten encounter/population/failed-entry cases in six seconds (8.18 seconds including startup). Tool/solution restores, solution build, both Python policy suites (eight catalog and nine formatting cases), formatting verification and diff checks passed. No full .NET suite was requested. The previous global-population capacity tests and economic bot-purchase tests were replaced because those operations were retired.

**Live acceptance** — Not performed; agent client testing is disabled.

**Optional maintainer checks** — Observe a miner's beam and follow a hauler through gates to inspect native presentation.

### Encounter profile and frame measurements

The completed profiles admit hauling through connected high-security markets without belts. Three-gate corridors receive traffic consistently; half of quieter systems receive a hauler, chosen from the saved seed. Miner opening phases vary among docked, returning and funded mining. A route re-encounter test retains the exact ship and cargo, with ordinary warp navigation toward the destination station. Revisits retry previously deferred local eligibility.

The copied 1,804-pilot save and a small synthetic world ran the same ten-docked-frame workflow. The copy measured 2.27 ms total and the fixture 2.05 ms, both with zero SQL commands, connections and fitting calculations. Cold entry measured 269.75 ms and 1,004.00 ms respectively, including initial preparation/JIT differences. These are separate catalog/save fixtures and do not isolate cold-entry scaling. The copy's checkpoint capture/projection/file-write phases measured 58.43/5.02/436.60 ms. No native rendering, socket backpressure or combat-heavy capacity claim is established by these docked automated samples.

Final local preparation reads the selected system and its immediate route neighbors. The earlier implementation still loaded global security payloads and asteroid counts during a local visit. A regression now places an invalid payload in an unrelated system and verifies that preparing the selected system succeeds. Population metadata invalidates on its actual saved row dependencies and is published before warmed observer frames. A late witnessed transfer is removed from the current career turn once it reaches an unobserved destination.

The final 99-case capsuleer/resident-world/simulation filter passed in 40 seconds (42.31 seconds including startup). The final copied-save sample measured 4.65 ms for 100 inactive wakes, 0.13 ms shared dispatch, 640.10 ms cold docked entry and 0.73 ms for ten warmed docked frames. The small fixture measured 764.82 ms cold entry and 4.27 ms for ten warmed frames. Both warm samples retained zero SQL commands, connections and fitting calculations. Copy checkpoint capture/projection/write measured 68.74/2.93/564.74 ms; the small fixture measured 1.23/17.74/11.01 ms. Earlier samples above document timing variation and successive corrections. Cold measurements include preparation and JIT, and different catalog/save fixtures limit direct scaling conclusions.

## Galaxy-wide station and gate traffic

World schema 120 adds durable career slots and a saved system sequence for new hauling routes. Existing agents occupy slot zero. Old station-pair routes remain readable. Migration recognizes generated pilot names and renames a matching founder ship. New roster grants commit ship, pilot, wallet, skills, cargo, job and route together on first visit. Revisits retain the slot after a ship loss. A failed expansion rolls back the additional roster members together.

The build-3662168 catalog supplies static station objects and reciprocal usable stargates. A stationless system may lie on a multi-hop route, including a return trip through a dead end. Isolated station pairs support local traffic. Initial placement is a one-time encounter choice. Saved navigation includes the route index and direction, allowing an observer to follow the same ship through repeated systems in either direction. The [population guide](../capsuleer-population.md#galaxy-wide-station-and-gate-traffic) gives the density rules.

Gameplay references include [EVE University stargates](https://wiki.eveuniversity.org/Stargates) and [system security](https://wiki.eveuniversity.org/System_security). Movement continues to use the implementation checked against [Carbon Destiny Ball.cpp at 27ee40b](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp).

A read-only census of all 8,490 installed systems with the development save's seed found 5,297 systems with routes and 3,193 with none. Of the routable systems, 321 planned zero home haulers, 1,166 planned one, 1,642 planned two, 1,352 planned three, 640 planned four, 91 planned five and 85 planned six. That amounts to 12,031 potential home haulers if every system is visited. The census created no pilots. It validated 42,220 route candidates and 746,998 consecutive reciprocal gate legs. Jita, Amarr and Rens planned six, Dodixie five, Hek three, Thera four and Kisogo one. The five empire trade hub approach slots selected the corresponding [main stations](https://wiki.eveuniversity.org/Trade_Hubs).

A gate observer previously saw departures but could miss arrivals indefinitely. The far-side career paused when its system emptied, and the earlier reappearance rule considered occasional route-endpoint visits. A gate watch now uses selected human pilots' actual ship positions. The nearest valid reciprocal gate within 250 km of its surface qualifies. Each gate pair can grant one persistent hauler when one or both endpoint traffic profiles support hauling. Multiple observers share that pilot. A pending inbound crossing has a saved deadline and uses the ordinary reciprocal gate transfer and Local membership. The far-side pilot takes ordinary flight actions when observed.

Two automated flight sessions 10 km from the Uoyonen gate in Vattuolen (50005333, reciprocal gate 50003987) observed the same individual haul from an empty Uoyonen. On an isolated upgrade of the installed version-118 world, the ship entered at simulated second 43, left at 179 and returned at 222. On a separate copy of the version-120 world it entered at second 99, left at 235 and returned at 278. Both observers received the ship contact, arrival indication and matching Local updates. These initial runs established the crossing. The maintainer reported a missing gate flash on arrival and departure.

The busy-station fixture produced five home haulers and one miner. A fixed 120-second observation at its station and then a gate saw three distinct traveling pilots, with its first station contact at second 0 and first gate contact at second 117. The quiet dead-end control had zero home agents and zero native scenes. The version-118 snapshot retained 1,804 pilot identities, 1,812 ships, 1,150 item stacks and 1,810 wallets across migration. The fixed Jita 4-4 observation saw a ship on the station grid at second 0. These are server-side samples.

With the [gate controller correction](navigation.md#observed-stargate-activation-in-build-3662168), the version-118 upgrade repeated the crossings at seconds 43 and 222 with a departure at 179. A later read-only copy of the current version-120 world produced arrivals at seconds 133 and 312 and a departure at 269. Each of the two independently selected observers received exactly two arrival and three departure gate commands on the Vattuolen gate across each complete copied-save run, including other departing traffic. The checks inspected the actual serialized gate ID, direction, value and optional arguments in each flight update queue, separately from the ship effects. The pending-wait restart case preserved the actor across 600 simulated offline seconds.

**Automated validation.** Tool and solution restore, build, 436 focused .NET cases (77 seconds), both Python policy suites (eight catalog and nine formatting cases), formatting verification and diff checks passed. The complete sequence took 101 seconds. The final .NET filter was `FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~Eden.Protocol.Tests.Flight|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~LiveWorldTests|FullyQualifiedName~DetachedCheckpointTests|FullyQualifiedName~PilotLoginTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~ChatWorldTests|FullyQualifiedName~AdministrationTests|FullyQualifiedName~CharacterCreationTests|FullyQualifiedName~DirectionalScanningTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting` with four test threads. Copied-world checks ran separately using the opt-in fixture. A preliminary affected run found two fixture mismatches: the human travel test supplied an incorrectly typed gate argument, and the inactive-system performance control had become a valid traffic route. The corrected tests retain the actual jump and zero-work assertions. Full .NET regression was not requested.

**Live acceptance.** Not performed; agent client testing is disabled. The maintainer's report of missing flashes predates the gate controller correction.

**Optional maintainer check.** Run the updated server and watch the Uoyonen gate from Vattuolen. Confirm the gate animates on incoming and outgoing crossings.

### Traffic improvement stage 1 — stationless lane routes

Gate encounters now use the saved route cursor and ordinary route targets after their inbound crossing. Previously the lane-specific target override always selected the watched gate when there was no local station, trapping a hauler between two stationless endpoints even when its saved home station was reachable farther along the route. Initial placement, arming and hidden transfer resolve the exact directed edge in the saved route. Subsequent flight follows the ordinary station/turnaround rules and retains the same owned hull.

References remain the [EVE University stargate reference](https://wiki.eveuniversity.org/Stargates), the pinned catalog and [Carbon Destiny Ball.cpp at 27ee40b](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp); movement and wire formats are unchanged.

**Automated validation.** The exact `CapsuleerInboundRuntimeTests.StationlessLaneContinuesToItsSavedRemoteStationAndPersistsCursor` method passed both gate orientations (6.56 seconds including runner startup). The focused filter `FullyQualifiedName~CapsuleerInboundRuntimeTests|FullyQualifiedName~CapsuleerInboundProvisioningTests|FullyQualifiedName~CapsuleerInboundGateAcceptanceTests|FullyQualifiedName~CapsuleerTrafficNavigationTests|FullyQualifiedName~CapsuleerHaulerTests` passed 48 cases in 13.47 seconds including runner startup. Coverage includes the real remote-station dock, saved route cursor, shared gate observations, rejection, rollback and lost ships. The copied-save gate case remains opt-in and was not exercised. No full .NET regression was run.

Tool/solution restore, solution build (zero warnings/errors), both Python policy suites (eight catalog and nine formatting cases), formatting verification and diff checks also passed.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** None required for this route correction beyond the historical presentation check above.

### Traffic improvement stage 2 — saved trip timing

`HaulerProgress.DwellSequence` defaults to zero for old JSON progress. New inbound waits and station layovers use a deterministic SplitMix64 choice keyed by pilot identity and this saved sequence, then commit the increment with the chosen deadline. The sequence does not advance on inbound pause/resume, and failed transactions retain the original sequence. A replacement hull retains the sequence. Existing waits retain their saved deadlines; there is no world-schema migration. Busy/medium/quiet inbound bounds remain 35–75, 65–105 and 95–135 seconds; station layovers remain 15–45 seconds. Hull selection is unchanged.

**Automated validation.** The exact method `CapsuleerInboundRuntimeTests.SavedWaitSequenceVariesTripsWithinBoundsAndRoundTripsThroughLegacyAndCurrentProgress` passed in 5.19 seconds including runner startup. The focused filter `FullyQualifiedName~CapsuleerInboundRuntimeTests|FullyQualifiedName~CapsuleerInboundGateAcceptanceTests|FullyQualifiedName~CapsuleerTrafficNavigationTests|FullyQualifiedName~CapsuleerHaulerTests` passed 40 cases in 11.77 seconds including runner startup. It covers variation/bounds, old progress, reopened sequence/deadline, paused remaining time, real station docking, rollback, destruction and repeated observed arrivals. The copied-save gate check remains opt-in and was not exercised. No full .NET regression was run.

Solution build (zero warnings/errors), both Python policy suites (eight catalog and nine formatting cases), formatting verification and diff checks also passed. Dependencies and tools are unchanged from the passing stage-1 restores.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** None; timing variation is established by the deterministic checks.

### Traffic improvement stage 3 — incremental scheduling

Ordinary reconciliation now removes withdrawn jobs and publishes only changed revisions into the existing sorted queue. Full restoration is retained for initial/full reload and exception recovery. Incremental population loading does not rebuild the queue. Active membership, turn selection and inbound-lane scratch collections are reused under the world owner. A not-due turn returns before constructing scoped sorted candidates; due jobs retain their stable deadline/pilot/sequence ordering. Same-tick membership and exact saved deadlines remain authoritative.

The same warmed workload before and after the change measured 200 complete maintenance wakes, including capture, shared native presentation and Local publication, with the test clock held below the next deadline. Every sample asserted zero SQL commands, connections and fitting calculations and unchanged canonical jobs.

| Fixture | Baseline allocation | Updated allocation | Baseline / updated elapsed |
| --- | ---: | ---: | ---: |
| Empty system, one observer | 5,143,272 B | 4,348,328 B | 22.03 / 22.09 ms |
| Occupied system, one observer | 7,245,704 B | 5,960,904 B | 30.54 / 14.59 ms |
| Occupied system, two observers | 10,079,040 B | 8,787,584 B | 19.20 / 16.31 ms |

These small-fixture samples include forced diagnostic instrumentation and JIT effects; allocation fell about 13–18%, while elapsed samples do not establish a production throughput claim. Separate updated checkpoint capture/projection/write samples measured 1.26/6.62/10.50 ms for the empty fixture, 0.43/1.67/15.77 ms with one occupied-system observer and 0.36/1.64/10.50 ms with two. A direct 10,000-selected-job check bounds 100 idle turns to 16 KiB of allocation and verifies withdrawn/re-enrolled work executes once in stable order.

**Automated validation.** The exact `CapsuleerTrafficPerformanceTests.WarmedMaintenanceIncludesSharedPresentationWithoutSqlOrFittingWork` method passed three cases before and after the optimization (6.97/6.64 seconds including runner startup). The final focused filter `FullyQualifiedName~CapsuleerSchedulingTests|FullyQualifiedName~CapsuleerRuntimeTests|FullyQualifiedName~CapsuleerHandoffTests|FullyQualifiedName~CapsuleerEncounterTests|FullyQualifiedName~CapsuleerInboundRuntimeTests|FullyQualifiedName~CapsuleerInboundGateAcceptanceTests|FullyQualifiedName~CapsuleerMaintenanceTests|FullyQualifiedName~CapsuleerTrafficPerformanceTests` passed 54 cases in 18.10 seconds including runner startup. Coverage includes concurrency, committed-generation recovery, rollback, observer release/re-entry, deferred inbound work and the late witnessed transfer. Copied-save evidence remains opt-in and was not exercised. No full .NET regression was run.

Solution build (zero warnings/errors), both Python policy suites (eight catalog and nine formatting cases), formatting verification and diff checks also passed. Dependencies and tools remain unchanged from the passing stage-1 restores.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** None required for the scheduling correction.

### Traffic improvement stage 4 — complete update diagnostics

`CapsuleerMaintenance.Tick` now records the whole maintenance update, from before acquiring its operation gate through scheduler execution, presentation capture, shared scene application and Local publication. A common monotonic completion timestamp defines total and presentation durations, including failed presentation attempts. The scheduler's existing `LastTurn` now starts before initial loading/reconciliation and covers settlement and result construction. Initial preparation failures are included in its failure diagnostics. All elapsed calculations use the injected monotonic `TimeProvider`; gameplay clocks and deadlines are unchanged.

The scheduler publishes immutable `LastUpdate` and `LastPresentation` values through the existing diagnostics snapshot, preserving them when a later simulation-only turn updates its counters. A narrow Gameplay diagnostics interface lets the Server maintenance owner supply these measurements. The performance page labels the complete traffic update, simulation/preparation and scene/Local costs separately. Checkpoint timings remain separate. Administration reads still consume the published snapshot without entering the world owner or opening SQLite.

**Automated validation.** The exact `CapsuleerMaintenanceTests.CompleteUpdateIncludesInitialReconciliationCaptureAndPresentationEvenOnFailure` method passed success and failure cases in 5.66 seconds including runner startup. Its simulated preparation/capture/scene costs of 11/3/7 ms produce an 11 ms scheduler turn, 10 ms presentation and 21 ms complete update, including when scene publication throws. The final focused filter `FullyQualifiedName~CapsuleerMaintenanceTests|FullyQualifiedName~CapsuleerHandoffTests|FullyQualifiedName~CapsuleerRuntimeTests|FullyQualifiedName~CapsuleerTrafficPerformanceTests|FullyQualifiedName~Eden.Protocol.Tests.Administration.ServerPerformanceTests|FullyQualifiedName=Eden.Protocol.Tests.Administration.AdministrationTests.PerformanceRequiresAdministratorAndServesLiveProcessDiagnosticsInDarkEditor|FullyQualifiedName~Eden.Protocol.Tests.Hosting.ServerServicesTests` passed 28 cases in 25.46 seconds including runner startup. It includes registered dependencies, failed-presentation recovery, concurrent job execution, warmed zero-SQL/fitting frames and the authenticated HTTP performance page with all three labels. No full .NET regression or copied-save run was performed.

Solution build (zero warnings/errors), both Python policy suites (eight catalog and nine formatting cases), formatting verification and diff checks also passed. Dependencies and tools remain unchanged from the passing stage-1 restores.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** View the performance page while traffic is active to inspect the new readings; native gameplay presentation remains unverified.

## zKill-backed traffic — November 8, 2026

Traffic can draw real pilot names, published supported hulls and complete victim fits from a local snapshot. The [population guide](../capsuleer-population.md#zkill-backed-travelers) documents the import command, date selection, cache, snapshot adoption and retained behavior. Density, saved wait sequences, station/gate routing, observer sharing and retreat remain the existing encounter policy. Imported travelers receive no starter ore or recorded cargo. A populated snapshot does not depend on the legacy freight-ore grant being available.

Source checks used [zKill's current API documentation](https://zkillboard.com/api/docs/), its daily full raw archives at `r2z2.zkillboard.com/history/raw/YYYYMMDD.json`, and [CCP's ESI API explorer](https://developers.eveonline.com/api-explorer#/operations/PostUniverseNames) for public character-name resolution. [EVE University killmails](https://wiki.eveuniversity.org/Killmail), [travel fits](https://wiki.eveuniversity.org/Travel_fits) and [capital ships](https://wiki.eveuniversity.org/Capital_ships) provide the gameplay references. Death records supply observations of pilots and equipment; they are not treated as ordinary travel counts. The pinned catalog and existing hull/module capability checks decide compatibility.

The importer combines destroyed and dropped quantities and retains explicit fitted flags and loaded ammunition, including nested magazine records. Cargo, drone bays and implants are excluded. Every remaining item must be legal: an unknown type, unsupported subsystem, invalid slot, hardpoint/calibration conflict, CPU/power excess, online-group limit or incompatible/oversized magazine rejects the complete observation. An isolated in-memory world runs the ordinary fitting, resource and ammunition operations. Complete modules come online together, avoiding ordering-dependent rejection of valid resource-provider fits. Rigs retain their ordinary fitted phase. No active combat effects are enabled.

Per-character/hull/region/security observations are processed newest first; a newer accepted fit supersedes only older observations of that same combination. Different hulls remain available for route and cargo compatibility. Fit decisions share a catalog/assembly/configuration cache, while quantity bounds remain per-record checks. Each validator uses the existing catalog operation scope to capture a generation instead of performing a file-generation check on every attribute read. Atomic output replacement follows SQLite integrity, manifest count and content-digest verification. A failed import retains the previous installed snapshot and completed download/name caches.

Schema 121 adds a unique source-character reservation keyed to the local pilot, with the source kill and snapshot identity. Reservations deliberately survive pilot deletion. Ships, modules, loaded charges and skills use ordinary saved entities and checkpoint capture. Existing imported pilots never reroll after a snapshot change. Only recognizable, uncustomized generated Badgers can convert while docked and undamaged; the original pilot/ship IDs, wallet, cargo and progress remain. The owning transaction includes equipment, name, hull and reservation changes, and rollback preserves the original state. Production startup shares the initialized world descriptor and captures the snapshot before serving sessions; login-ticket preparation does not load it.

Native packet formats and Destiny calculations are unchanged. The existing scene, Local, station guest, directory, fitting and gate adapters publish the resulting saved pilot and ship. Prior native observations in this page remain historical; no new live-client acceptance was conducted.

**Imported snapshot.** The requested range was reduced from thirty days to one week during implementation. The completed snapshot covers **November 1–7, 2026**; November 8's complete archive had not yet been published. It processed **114,244** kill records with zero duplicate kill IDs and retained **27,947** compatible observations for **17,722** unique pilots, **190** hull types and **18,895** distinct complete fits, including recorded magazine quantities. The **86,297** discarded records include **11,809** superseded observations; the remaining reasons are preserved in the manifest. The seven uncompressed source archives total **288,899,164 bytes**; the installed snapshot is **26,435,584 bytes** at `%LOCALAPPDATA%\Eden\traffic.sqlite`. Its content identity is `CEAF1C1C4520AC3E7E956772FC38DB5FC16123957424DF3AF4B2DE288096E1F0`. The completed weekly rebuild took **188.43 seconds** using already downloaded archives and cached fit decisions. Older archives downloaded during the interrupted monthly run, and the duplicate preflight archive, were removed. The default command now selects seven published complete days.


**Automated validation.** Tool and solution restore, the solution build (zero warnings/errors), eight catalog-policy cases, nine formatting-policy cases, formatting verification and diff checks passed. The final affected filter `FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~Eden.Protocol.Tests.Ammunition|FullyQualifiedName~PilotFittingTests|FullyQualifiedName~ModuleFittingTests|FullyQualifiedName~FittingConstraintTests|FullyQualifiedName~PassiveFittingTests|FullyQualifiedName~FittingSnapshotTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~LiveWorldTests|FullyQualifiedName~DetachedCheckpointTests|FullyQualifiedName~PilotLoginTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~ChatWorldTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting` passed **341 cases in 56 seconds**, using `-- xUnit.MaxParallelThreads=4`. An earlier broader affected run including the Flight namespace passed **545 cases in 3 minutes 49 seconds**. After final typed-parameter/status-constant cleanup, the exact deterministic provisioning and archive-import methods passed another two cases in six seconds (8.56 seconds including runner startup). The opt-in installed-snapshot check was exercised against the actual weekly artifact and verified its digest, counts, names and all 190 admitted hull types. The full .NET suite was not requested and was not run.

Two preliminary twelve-worker validation runs each finished 340/341: one login fixture exhausted its five-second timer during cold database preparation, and one separate ticket-preparation process exceeded its ten-second process budget under parallel load. The login fixture now constructs its world before starting the unchanged five-second exchange timer. Ticket preparation passed in isolation, and the final four-worker affected run passed without widening its process timeout. The original exact regression was `TrafficSnapshotTests.DeterministicCompleteFitProvisioningKeepsRealIdentityEquipmentAndNoStarterCargo`. Coverage includes complete equipment and loaded ammunition, passive statistics, record rejection, collisions, snapshot corruption/mismatch, cached/offline imports, atomic failures, safe docked conversion and cargo/customization preservation, observer sharing, repeated gate travel, losses and reopened checkpoints. Both original and imported warmed traffic controls assert zero SQL commands, connections and fitting calculations; complete update and checkpoint costs remain separate measurements.

**Live acceptance.** Not performed; agent client testing is disabled. Native rendering, input and gameplay feel remain unverified.

**Optional maintainer checks.** Restart the server to adopt the snapshot, then observe mixed ships at a station and gate and inspect a traveler's fitting.

## Destination journeys — November 9, 2026

Gate encounters now prepare a bounded group of four station travelers with staggered departures. One initial visitor can finish a moving gate approach on the unobserved source side. Remaining visitors wait docked at real source stations and fly to the gate when due, removing the previous far-side parking queue. Reciprocal watchers share the group. Observation preserves direction and due time. Each journey saves its kind, home, source, destination, ordered system path and sequence in the existing hauler progress payload. Subsequent station departures choose a compatible destination from the actual docking location. Local return trips retain their home. Visitors avoid their previous origin when another eligible destination exists.

Local journeys cover zero to two jumps, regional journeys prefer three to seven, and long-distance journeys prefer eight to twenty. Unsupported distance bands fall back to shorter real paths. New routes have distinct station endpoints and unique systems. Stationless dead ends receive no new shuttle routes. Hull admission checks every system on the path. Destination candidates are memoized and bounded per distance, with additional selection weight for hubs and station clusters.

The scheduler retains a system-to-route membership index as destinations change. Established travelers continue through empty systems on shared playtime, using alignment, analytic warp, approach and transfer deadlines. Selected-player time pauses in empty worlds. Existing movement, gate effects, transaction/version checks and detached checkpoints provide the travel boundaries. Existing pilots and assets persist, with a new journey chosen at a safe dock. Migration of a paused inbound wait retains its remaining delay. Loading materializes saved rows before committing those transitions so a streaming SQLite reader cannot lock the writer.

Gameplay reference was checked against [EVE University route planning](https://wiki.eveuniversity.org/Star_Map). Movement retains the existing implementation checked against [Carbon Destiny Ball.cpp revision 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp). Routing uses the installed build-3662168 catalog. No new live-client observations were collected.

### Local roster correction

A reported empty Dodixie Local exposed a role-allocation error. Its five home pilots had global IDs that selected regional/long-distance work, while its nearby legacy traveler remained paused. Roles now derive from stable community roster slots. A busy five-pilot roster reserves two locals, two regional travelers and one long-distance traveler. Locals use their community's station as home and prefer same-system station trips. A previously misassigned local returns over real gates after reaching a safe dock. Activation enrolls paused legacy routes into continuous travel without replaying elapsed absence.

Visitors choose an onward destination before routing back to their actual home station. The three-station circuit regression verifies the onward stop and subsequent homeward leg using one persistent ship.

### Automated comparison

The same seeded receiving-gate fixture was observed for 3,600 simulated seconds on baseline revision a7078e8d and the destination-journey implementation. The fresh opening produced the following results. The pending-wait restart retained an already delivered visitor and observed twelve subsequent arrivals from five pilots.

| Receiving-gate measurement | Baseline | Destination journeys |
| --- | ---: | ---: |
| Total inbound crossings | 15 | 13 |
| Distinct arriving pilots | 1 | 5 |
| Repeat arrivals | 14 | 8 |
| Reverse crossings without an intervening dock | 0 | 0 |
| First arrival, fresh fixture | 109 seconds | 4 seconds |

This fixture has station endpoints on both sides, so its baseline reversals include a dock. Separate stationless-corridor tests establish onward travel and quiet dead ends. The baseline comparison intentionally fails the new distinct-pilot requirement.

The same one-hour residential and quiet fixtures were also compared. The residential baseline had one pilot, one home visit and one crossing before becoming dormant. The destination implementation has five home visits and nine crossings by that same pilot. The quiet fixture stays empty in both versions.

An eight-jump fixture covers ordinary flight, background deadlines, a player waiting downstream, and reopening a checkpoint halfway through the trip. Route-generation cases exercise all distance bands and real reciprocal gates. Existing rejection, rollback, tackle, loss, shared observer, native notification and warmed observer tests remain in the affected validation set. An installed-catalog check verifies the Vattuolen/Uoyonen crossing has an onward station destination.

The final copied installed save retained 1,861 roster identities, 1,871 ships, 2,141 item stacks and 1,868 wallets across initialization and reopening. Initialization took 831 ms. Before warming another system, the reported Dodixie case reserved two local pilots and observed its first local in space after one simulated second. Jita preparation then took 972 ms. Its one-hour sweep saw six distinct gate pilots and six distinct station pilots. The two-observer path saw four distinct pilots. These are copied-world automated observations.

Warmed complete maintenance measured 11.27–37.33 ms across 200 unchanged wakes, including one or two observers and original/imported traffic. Each case asserted zero SQL commands, connections and fitting calculations. Checkpoint capture, projection and write measured 0.61–1.45 ms, 1.46–7.58 ms and 10.72–21.69 ms respectively. These are automated fixture measurements.

**Automated validation.** The affected validation filter includes Capsuleers, Flight, WorldMigrationTests, LiveWorldTests, DetachedCheckpointTests, PilotLoginTests, StationGuestTests, ChatWorldTests and Hosting. The final selection passed 494 cases in 135 seconds with four workers. The separate login-preparation process check passed in isolation in three seconds. A preliminary parallel run timed out that check while a copied-world run was competing for resources, so the final selection excluded that method and exercised it separately with its unchanged ten-second timeout. After moving the background timing calculation into Gameplay, the 14 scheduling, eight-jump, gate-boundary and station-wait cases passed in seven seconds. Both Python policy suites passed, with eight catalog and nine formatting cases, and formatting verification passed. The final opt-in copied-save and installed-route checks passed two cases in 85 seconds. Full .NET regression was not requested.

The affected filter was the following expression. The isolated method filter was FullyQualifiedName=Eden.Protocol.Tests.Persistence.LiveWorldTests.DevelopmentTicketPreparationDoesNotAcquireTheRunningWorld.

~~~text
(FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~Eden.Protocol.Tests.Flight|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~LiveWorldTests|FullyQualifiedName~DetachedCheckpointTests|FullyQualifiedName~PilotLoginTests|FullyQualifiedName~StationGuestTests|FullyQualifiedName~ChatWorldTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting)&FullyQualifiedName!~DevelopmentTicketPreparationDoesNotAcquireTheRunningWorld
~~~

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** After restarting with the new build, watch a connected gate for varied arrivals and follow one traveler across successive systems. Existing short trips can finish before adopting their next destination.

## Persistent traveler routines — November 9, 2026

Travelers now retain a deterministic pace, station affinities and three recent stops in their existing hauler progress payload.
Geographic destination weights combine with stable personal affinities and a separate per-trip mixer. Recent stops receive a
quarter of their ordinary weight. Existing journeys finish before routine assignment at a dock. New station dwell times use
half/normal/double pace with 20% variation, retaining population counts and journey distance bands. Security status is not a
travel preference; real hull restrictions remain enforced. All stations are considered before retaining bounded candidates.
Capital destination searches now apply hull restrictions during traversal, preserving detours hidden by a shorter highsec path.

Attack memory retains four systems for thirty human-active minutes. A retreat shelters at the next dock for five to fifteen
human-active minutes. Alternate paths and destinations avoid remembered systems while available, including postponing a
preferred homeward leg. Otherwise ordinary compatible routes remain available. Memory and shelter share the saved progress
and checkpoint generation. Rejected navigation retains the threat instead of silently consuming the attack notification.
Legacy saves retain their assets, identities, current navigation, deadlines and nullable progress compatibility.

References: [EVE University hauling](https://wiki.eveuniversity.org/Hauling), with Eden's explicit security-neutral routing policy;
unchanged navigation uses [Carbon Ball.cpp at 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp).
No client observations were collected.

**Automated validation.** Tool/solution restore and build passed with zero warnings/errors (final build 3.89 seconds).
Final affected filter `FullyQualifiedName~TravelerRoutineTests|FullyQualifiedName~CapsuleerTrafficRoutesTests|FullyQualifiedName~CapsuleerTrafficNavigationTests|FullyQualifiedName~CapsuleerInboundRuntimeTests|FullyQualifiedName~CapsuleerTrafficObservationTests|FullyQualifiedName~CapsuleerTrafficPerformanceTests|FullyQualifiedName~LiveSimulationTests`
passed **84 tests** in **20.57 seconds** (feature build 1.48 seconds). Catalog setup (**8**) and formatting policy (**9**)
Python tests passed. Formatting and diff verification passed. Tests cover station affinity and recent-stop weighting,
pace variation, compatible capital detours, threat avoidance/expiry/fallback, all station endpoints, retreat/shelter and
checkpoint persistence. Warmed complete traffic wakes retain zero SQL commands, connections and fitting calculations for
empty and occupied systems with multiple observers; capture/projection/write are measured separately.
Two hundred warmed complete wakes took **9.58–24.45 ms** across those cases. Checkpoint capture was **0.44–2.85 ms**,
projection **1.87–6.36 ms**, and writing **11.41–18.50 ms** on this run; these measurements are workload evidence, not latency guarantees.
Development first used exact filter `FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.TravelerRoutineTests.HabitsThreatsAndShelterSurviveCheckpointWithoutChangingLegacyDeadlines`.
The first fixture serialization omitted the production read-only-property setting and was corrected. The expanded retreat
regression exposed stale outbound alignment and dirty-position eviction on docking; both were corrected before final validation.
The dirty-position correction is exercised by a real resident checkpoint after retreat. No full .NET suite was requested or run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Observe recurring travelers and the sheltered return after an attack to assess visual pacing.

## Simulated-pilot retaliation — November 9, 2026

Travelers and miners now share a defense controller independently of their career routines. Typed Gameplay rules select
threats, activation thresholds, capacitor reserve and retreat. World executes normal authoritative operations; Protocol
publishes the existing targeting and module contacts. The committed hostility ledger and incoming combat triggers replace
the victim-only damage callback. Paid misses, launches, tackle and electronic warfare can provoke defense; target locks alone
cannot. Defense selects an active tackler, then the most recent attacker, then stable ship identity. It stays on the current
grid and forgets ships that depart or are lost, without following gates or selecting replacement capsules.

Actual hull cohorts, online usable equipment and remaining damage sources distinguish combat ships from escape-first
miners and transports. Locks retain normal scan delay, range and jamming restrictions and share the human target-lock
registry. Each offensive action reuses normal legality, payment, cooldown, damage and loss operations. Automated module
coverage includes turrets, launchers, drones, tackle/core stabilizers, ECM, dampeners, turret/guidance disruption, painters,
neutralizers/nosferatu, propulsion, local/ancillary shield and armor repairs, hull repair, hardeners, capacitor boosters,
assault damage controls, signature suppression and targeting/tracking/guidance/drone support.

Local repairs start below 80% of their layer, boosters below 30% capacitor, and emergency defenses below 40% of the fitted
tank layer. Defensive modules precede optional offensive support with a capacitor reserve. Ordinary navigation maintains
weapon range and advances at most once per movement stamp despite subsecond module deadlines. Combat ships retreat below
35% effective health, below 75% structure, or when usable damage sources run out. Retreat cancels offensive repeats and
recalls drones, allowing at most ten seconds before available escape takes priority. Normal weapons timers still apply.
An emergency core's strength ends at its paid duration while retaining its longer reactivation deadline; successful core
activation immediately updates the career's warp restriction.

One durable `WorldCapsuleerSupplies` marker per pilot shares the owning grant/departure transaction. New ships, and surviving
original ships at their next dock, receive at most five magazines of loaded consumable combat charges, bounded by free cargo.
Scripts and nonconsumable crystals are excluded. A legal homogeneous flight of supported T1 mobile combat drones prefers the
largest class permitting a full flight, with stable type ordering. Bay, bandwidth and control limits apply; existing cargo,
equipment and drone stacks are preserved. Required drone skills use the existing simulated-pilot grant mechanism. Consumption,
abandonment and loss remain finite. There is no hunting, fleet assistance, automatic overheating, market restocking or
replacement-ship grant. The schema reconciliation creates the marker table idempotently on existing version-131 saves.

Nullable defense progress preserves legacy payloads. Locks, cycle deadlines, recall and movement stamps checkpoint with
career progress. Standard local repair cycles and sustained-module renewal keep paid capacitor and cooldowns resident.
Renewed repair deadlines replace older projected completions, and checkpoint/restart tests complete the last paid repair once.
Existing initialization clears sustained bonuses; defense preserves paid deadlines without advertising a removed bonus or
paying again before another cycle is due. Ordinary turret combat against pilots now uses the existing resident combat math;
criminal/fatal actions retain transactional settlement. Failed and duplicate callbacks do not repeat payments or losses.

Performance keeps unchanged and absent reads cached, shares observer contacts, indexes pending hostility by victim and caches
defense fitting profiles by the actual pilot revision. Peaceful updates, movement, nonfatal turret/drone combat, standard
local repairs and sustained local renewals use resident state. Initial activation, charge loading, missile and remote-module
operations retain their established SQL boundaries. Their cost remains relevant when scaling mixed-module fights; the fixture
measurements below are not a large heterogeneous-fleet capacity claim. Checkpoint capture, projection and file writing are
measured separately from complete maintenance and two-observer session frames, including packet encoding.

Gameplay references: [EVE University timers](https://wiki.eveuniversity.org/Timers),
[turrets](https://wiki.eveuniversity.org/Turret_mechanics), [drones](https://wiki.eveuniversity.org/Drones), and
[tackling](https://wiki.eveuniversity.org/Tackling). Movement retains the established
[Carbon Ball.cpp revision 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp).
Catalog variants use committed build-3662168 extracts. No new live-client observations were collected.

**Automated validation.** Development began with exact method filters through `tools/Test-Feature.ps1`, including
`PilotTurretTests.WarmPilotVolleysKeepPaymentsAndHostilityResidentWithoutSql`,
`CapsuleerDefenseTests.CombatPilotLocksAndRetaliatesAgainstAMissingShotWithNormalCostsAndLegality`,
`CapsuleerDefenseModuleTests.DefenseUsesTheRealSupportedModuleAndCannotReplayItsPaidCycle`,
`CapsuleerDefenseLifecycleTests.EmptyMagazineReloadsOwnedCargoThenRetreatsWithoutCreatingCharges`,
`CapsuleerDefenseLifecycleTests.DroneRecallReturnsIndividualsOrYieldsToEscapeAfterTenSeconds`,
`CapsuleerDefenseLifecycleTests.LockingAloneDoesNotProvokeButTackleMakesAMiningFitFightWhileTrapped`,
`CapsuleerDefenseTests.ACommittedJamPreventsLocksOnOtherAttackersUntilItEnds`,
`CapsuleerDefensePerformanceTests.WarmLocalRepairCyclesUseNoSqlAndCheckpointOnePaidCompletionAcrossRestart`,
`CapsuleerDefenseFrameTests.WarmCompleteMaintenanceAndTwoObserverFramesKeepCombatResident`, and the three
`CapsuleerSupplyTests` methods. An intermediate affected selection passed 146 cases in 55.43 seconds including runner startup
(feature build 6.41 seconds). The wider affected selection checked 1,112 cases in 318.46 seconds: 1,111 passed and one old
snapshot assertion expected only a fitted gun at the ship location, excluding the newly required reserve cargo. The assertion
now checks the fitted slot, unchanged loaded ammunition, reserve stack and durable marker separately. Its exact method and the
updated capacitor-reserve rule passed two cases in 6.09 seconds (feature build 4.25 seconds). A subsequent exact NPC-miss filter
passed both turret and failed-electronic-warfare cases in 6.36 seconds (build 5.69 seconds).

The final affected follow-up passed **319 cases in 93.38 seconds**, with four workers, on the corrected code. Its filter was
`FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerDefense|FullyQualifiedName~CapsuleerSupplyTests|FullyQualifiedName~TrafficSnapshotTests|FullyQualifiedName~CapsuleerMinerTests|FullyQualifiedName~CapsuleerHaulerTests|FullyQualifiedName~CapsuleerInboundRuntimeTests|FullyQualifiedName~CapsuleerTrafficPerformanceTests|FullyQualifiedName~PilotTurretTests|FullyQualifiedName~PilotDroneTests|FullyQualifiedName~NpcEwar|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~NpcMissile|FullyQualifiedName~DroneCombatTrigger|FullyQualifiedName~LiveSimulationTests`.
The wider filter additionally selected Capsuleers, Propulsion, Repair and Defenses namespaces and MissileTests, PlayerEwar,
CapacitorWarfare, SupportModule, DetachedCheckpointTests, WorldMigrationTests and TargetingTests. Those unaffected checks were
not blindly repeated after the focused correction. No full .NET regression suite was requested or run.

Tool/solution restore and the final solution build passed with zero warnings/errors (4.31 seconds). Both Python policy suites
passed: eight catalog cases in 0.837 seconds and nine formatting cases in 0.026 seconds. Formatting verification checked 1,467
files and passed the 140-character policy; diff verification passed. Coverage includes human/NPC attackers, all security bands,
misses and failed electronic warfare, lock-only inactivity, tackle-only aggression, jamming, attacker selection, retreat,
finite ammunition/reloading, individual drone recovery and timeout, supported module families, duplicate paid cycles,
rollback, unchanged-state rejection, exactly-once losses, absent NPC rewards for capsuleer kills, two-observer effects,
bounded grants and restart continuity. Early focused failures corrected fixture setup and exposed paid-cycle retention,
renewed repair projection and emergency-core expiry issues before final validation.

The final deterministic workload uses 12 warmed, time-advancing complete maintenance/session frames and two observers,
with packet encoding. A quiet population fixture avoids unrelated unsupported NPC spawn retries while retaining ordinary
career jobs, commands, damage and session updates. Counts include periodic transaction work, rather than reporting only
the controller's inner loop. Timings are fixture evidence under a four-worker test run, not server-capacity guarantees.

| Complete frame or checkpoint | Peaceful | Retaliating |
| --- | ---: | ---: |
| Median frame | 0.53 ms | 0.76 ms |
| 95th-percentile frame | 1.03 ms | 3.14 ms |
| SQL commands per frame | 0–2 | 0–8 |
| Connections per frame | 0–2 | 0–8 |
| Fitting calculations per frame | 0 | 0 |
| Checkpoint capture | 0.50 ms | 0.74 ms |
| Detached projection | 1.69 ms | 9.86 ms |
| Checkpoint file writing | 23.45 ms | 20.01 ms |

Ten warmed paid local repair cycles took 0.88 ms and allocated 25,816 bytes, with zero SQL commands, connections or fitting
calculations. Their checkpoint capture/projection/write measured 0.52/5.29/11.02 ms. Separate warmed peaceful-controller,
pilot-volley, drone-combat and traffic-presentation checks retain zero SQL/connection/fitting budgets where no transaction
boundary is due. Captured locks/cycle deadlines, damage, charge consumption and the final paid repair are checked across
projection/restart; same-time callbacks cannot replay them.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Observe a retaliation encounter and a retreat to assess native effects and gameplay feel.

### Main integration — November 9, 2026

Integrated both pilot milestones with main revision `39fb0fc2`, retaining its gas/ice mining, map and mod-package changes.
The only conflict was the protocol index; both modding and simulated-pilot links are retained. Shared resident read-model
changes preserve the mining-site cache alongside defense-profile invalidation. Uncommitted work in the main checkout
was excluded from this merge and preserved.

**Automated validation.** Tool/solution restore and the merged solution build passed with zero warnings/errors
(6.04 seconds). Exact filter
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerDefenseFrameTests.WarmCompleteMaintenanceAndTwoObserverFramesKeepCombatResident`
passed both peaceful/combat observer cases in 8.14 seconds (feature build 1.39 seconds). The affected filter
`FullyQualifiedName~CapsuleerDefense|FullyQualifiedName~CapsuleerSupplyTests|FullyQualifiedName~TravelerRoutineTests|FullyQualifiedName~CapsuleerTrafficRoutesTests|FullyQualifiedName~CapsuleerTrafficNavigationTests|FullyQualifiedName~CapsuleerInboundRuntimeTests|FullyQualifiedName~CapsuleerMinerTests|FullyQualifiedName~LiveSimulationTests|FullyQualifiedName~GasMiningTests|FullyQualifiedName~IceMiningTests`
passed **151 tests in 49.24 seconds** (feature build 1.47 seconds). Both Python policy suites passed: eight catalog cases
in 0.854 seconds and nine formatting cases in 0.023 seconds. Formatting checked 1,491 files; line-width and diff checks passed.
No full .NET suite was requested or run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** The retaliation/retreat observations above remain optional.

## Belt ratting, resupply and ship variety — November 9, 2026

Combat-fit travelers now select bounded observed belt-ratting sorties, proactively engage only suitable ordinary belt
pirates, collect owned NPC loot and unload it at their actual return station. Pure typed Gameplay assessment evaluates
the complete visible group using fitted damage/application, ammunition, capacitor, layered resistances, enemy repairs
and conservative survival bounds. Unsupported NPC electronic-warfare estimates are rejected. The shared defense owner
accepts an explicit proactive NPC target without manufacturing incoming aggression; real attackers retain priority.
Normal module, targeting, damage, bounty/skill-point, drone, retreat and loss owners remain authoritative.

Optional hauler checkpoint fields retain activity choice, belts, elapsed budget, targets, loot identity, supply preferences,
shopping destination and visit spending. Ratting is removed from continuous unobserved traveler scheduling and resumes
remaining activity/controller deadlines, including when the global human clock was stopped. Committed effects retain
normal settlement; no unobserved farming loop or global NPC freeze is introduced.

Resupply uses owned hangar stock, then actual market purchases in the same owning transaction as progress advancement.
Arrival freezes a quarter-wallet budget. Unit prices are bounded by twice the bundled sell reference; missing prices
are skipped. Candidate shopping paths are limited to three jumps in the current region and avoid remembered threats.
Empty magazines reload through the ordinary paid ammunition operation. No new money, recurring supplies or lost ships
are granted. The population guide records the selection and capacity policies.

Traffic selection now draws hull groups before characters and bounds combined rookie/shuttle admission by the community
roster, including visitors. Idempotent `WorldTrafficCorrection` reconciliation records one-time untouched rookie hull/fit
corrections while retaining the original imported identity. Supply inventory changes invalidate the actual imported pilot's
fitting/assessment dependencies. Warm movement, direct combat and shared observer reads retain resident ownership;
market, loot, reload, missile and destruction operations retain their normal SQL boundaries.

Gameplay reference: [EVE University belt ratting](https://wiki.eveuniversity.org/Ratting).
Navigation continues to use the established
[Carbon Ball.cpp revision 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp)
and pinned build-3662168 catalog definitions. No new native-client observations were collected.

**Automated validation.** Development used exact method filters through `tools/Test-Feature.ps1`, starting with
`RattingRulesTests.ActualFitMustClearTheWholeGroupAndSurviveTwiceAsLong` and then real-world purchase, navigation,
proactive combat and reload cases. An intermediate affected run passed 103 cases in 37.29 seconds including runner
startup (build 5.35 seconds). Later additions cover real pirate destruction and exactly-once bounty settlement,
owned versus foreign wrecks, hangar unloading/restart, paused human-clock deadlines, three-jump boundaries,
price ceilings, threat memory, finite owned reloads, and correction rollback/idempotency.
Final integration with main `e31693d1` passed **471 affected tests** in **108.15 seconds** including runner startup,
using four workers. Tool/solution restore and the solution build passed with zero warnings/errors (build 5.59 seconds).
Both Python policy suites passed: eight catalog cases in 0.613 seconds and nine formatting cases in 0.015 seconds.
Formatting verification checked 1,554 files and passed the 140-character policy; diff verification passed.
No full .NET regression suite was requested or run.

Final affected filter:
`FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~WreckTests|FullyQualifiedName~PersonalMarketTests|FullyQualifiedName~RegionalMarketTests|FullyQualifiedName~NpcScene|FullyQualifiedName~LiveSimulationTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~PilotTurretTests|FullyQualifiedName~PilotDroneTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~DetachedCheckpointTests`.

Complete maintenance/session-frame measurements include two observers and packet encoding after twelve warmup frames.
The ratting sample measures an observed returning sortie; the combat sample exercises the shared retaliation controller.
These small deterministic fixtures establish their database/fitting budgets, not heterogeneous-fleet server capacity.
Checkpoint capture, detached projection and file writing are measured separately.

| Workload | Median / p95 frame | SQL / connections per frame | Fitting calculations | Capture / projection / write |
| --- | --- | --- | --- | --- |
| Peaceful traveler | 0.88 / 5.36 ms | 0-2 / 0-2 | 0 | 0.82 / 6.13 / 11.53 ms |
| Shared combat controller | 2.12 / 2.75 ms | 0-8 / 0-8 | 0 | 0.53 / 3.88 / 12.56 ms |
| Returning ratter | 0.57 / 2.46 ms | 0-2 / 0-2 | 0 | 0.50 / 2.16 / 12.75 ms |

Development failures exposed a nested departure read transaction and a schema-order dependency in supply invalidation;
both were corrected. Other early failures corrected fixture serialization, ownership queries, initial reload cooldowns
and remote ask prices. Focused checks were rerun after those corrections; the final affected result above includes them.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Observe a belt fight, wreck approach and subsequent resupply trip to assess native effects
and gameplay feel.

## Configurable simulated-pilot population — November 9, 2026

The restart-only config editor now exposes an overall enable switch, a simulated-miner switch, station traffic density
(0–10×, default 1×) and watched-gate visitors (0–16, default four). The hidden legacy `GlobalProvisioning` preference
round-trips independently. Existing configuration documents receive unchanged defaults for missing fields. Normal immutable
revisions, validation, changed-values comparisons and export/import apply to all four controls.

Startup reconciles saved roster slots against the activated policy before accepting sessions. `WorldCapsuleerSuspension`
records a durable pause anchor for excess agents. Pilots, ships, inventory, identities, wallet balances, losses and grant
ledgers remain intact. Returning slots reactivate their saved jobs/progress in the same transaction that removes the pause;
missing slots are provisioned only when their community or gate is next visited. Reciprocal watched gates retain one visitor
identity per ordinal, including ordinals above the former four-visitor default. Home roster slots remain disjoint from gates.

Suspended agents are excluded from scheduling and presentation. Saved positions are retained while directional scanning,
cloak proximity, NPC target acquisition and deployed-drone contacts omit dormant ownership. Startup already cancels live
projectiles and releases NPC/drone control; suspension introduces no unobserved attacks. Resume adjusts active-clock deadlines
and ratting controller timing without replaying work or resetting any loss or paid visit budget. Membership is loaded on
population changes rather than queried every frame. The config editor applies these changes at restart; it does not stop or
restart the server itself.

Gameplay reference: [EVE University NPC mining operations](https://wiki.eveuniversity.org/NPC_Mining_Operations), used to keep
simulated capsuleers separate from NPC mining-fleet controls. Population policy introduces no new movement rules;
the existing [Carbon Ball.cpp revision 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp)
and pinned build-3662168 catalog remain the movement reference.

**Automated validation.** Development began with the exact method filter
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerPopulationSettingsTests.RestartSuspendsAndReusesSavedPilotsWithoutChangingTheirPossessionsOrReplayingJobs`.
Coverage includes pending-versus-active configuration, repeated restart and reactivation, exact saved identities/possessions,
partial limits, maximum reciprocal visitors, transaction rejection/rollback, ratting deadlines/loot/budget retention,
legacy documents, deterministic fractional density, invalid ranges, authenticated editor save/export, and rendered labels.

Tool/solution restore and the solution build passed with zero warnings/errors (build 7.45 seconds). The final affected run
passed **476 tests**, with no skips, in **262.89 seconds** including runner startup (feature build 1.43 seconds).
An added maximum-density provisioning/reuse case then passed independently in 5.92 seconds (build 3.69 seconds).
Both Python policy suites passed: eight catalog checks in 0.741 seconds and nine formatting checks in 0.015 seconds.
Formatting and the 140-character policy passed. An earlier wider development run passed 541 cases with four unrelated
client-package-dependent cases skipped, in 250.75 seconds; development test API/exception expectations were corrected
before these results. No full .NET regression suite was requested or run.

Final affected filter:
`FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~AdministrationTests|FullyQualifiedName~WorldSettingsPageTests|FullyQualifiedName~AdministrationComponentTests|FullyQualifiedName~WorldConfigurationTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~InformationTests|FullyQualifiedName~SpatialModuleTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~PilotDroneTests|FullyQualifiedName~DetachedCheckpointTests|FullyQualifiedName~LiveSimulationTests`.

One hundred warmed disabled-population maintenance frames took 1.47 ms total with zero SQL commands, connections or fitting
calculations. Existing complete maintenance/two-observer frame and checkpoint assertions also passed. Those fixture timings
were collected alongside other work and are diagnostic evidence rather than a server capacity claim.

Integration onto main `1a036454` retained the deadline scheduler and the agent-header/courier fixes. The merged solution
built with zero warnings/errors in 8.05 seconds; the exact suspension/reuse regression passed again. The combined affected
filter passed **386 tests**, with no skips, in **204.24 seconds** including runner startup (feature build 1.22 seconds).
Both Python policy suites passed again (eight catalog cases in 0.977 seconds and nine formatting cases in 0.022 seconds),
followed by formatting and diff verification.

Integration filter:
`FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~Eden.Protocol.Tests.Administration.AdministrationTests|FullyQualifiedName~WorldSettingsPageTests|FullyQualifiedName~WorldConfigurationTests|FullyQualifiedName~AgentDirectoryTests|FullyQualifiedName~AgentDirectoryServiceTests|FullyQualifiedName~AgentInformationRulesTests|FullyQualifiedName~CourierServiceTests`.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** After saving and restarting, verify that lowering the controls removes pilots from Local,
space and station guests; raise them and restart to observe the returning identities. Browser interaction and native rendering
remain unverified.

## Population diagnostics and Local membership — November 9, 2026

The Population page's old one-miner/one-hauler/restart-provisioning description no longer matched encounter-driven spawning.
Its retained system/pilot totals also failed to distinguish historical records from prepared systems and paused pilots.
The page now shows the active population policy, systems prepared under that exact catalog/policy fingerprint, their eligibility,
saved communities/pilots, enabled versus paused pilots, scheduled work and pilots represented in watched systems (including docked
guests). Legacy system records remain visible with a refresh-on-visit label. Saved totals retain all pilots; they are not Local
population counts. A top-level refresh updates the snapshot while pilots travel.

Population metadata reuses the provisioning fingerprint and remains cached against its existing membership revision. Scheduled
counts update when enrollment changes even if no job has executed. Diagnostic reads never advance jobs. Local publication is
batched as documented in the [social record](social.md#simulated-pilot-local-roster-batches--november-9-2026).

**Automated validation.** The combined chat/population filter passed 150 tests in 60.89 seconds, with tool/solution restore,
solution build, both Python policy suites and formatting verification passing. See the [social validation record](social.md#simulated-pilot-local-roster-batches--november-9-2026)
for exact filters and corrected development expectations. Regressions cover authenticated HTML/API fields, unchanged jobs,
current-versus-old policy preparation records, retained paused totals, real high-density guest/Local agreement, and zero SQL,
connections, fitting calculations or new chat deliveries across twenty warmed unchanged maintenance/diagnostic reads.
Existing complete maintenance/two-observer and detached-checkpoint assertions also passed. No full .NET suite was run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Refresh Population after visiting a new system or applying a lower limit and restarting; compare
saved/paused counts with the pilots visible in the current system.

## Global population preparation on restart — November 9, 2026

The station-traffic multiplier now prepares all catalog systems on normal server restart, rather than waiting for human visits.
`Program` invokes `WorldInitialization.PrepareCapsuleers` after schema/configuration/renewal reconciliation and before opening the
resident world or listeners. Offline migration, configuration authoring, password changes and redemption administration retain
their existing storage-only initialization. Tests exercise the same preparation phase used by normal host startup.

The stopped-world operation creates/preserves the world seed, reactivates eligible saved pilots before new grants, and walks
systems in stable identity order. Existing agent slots, identities, ships, wallets, cargo, funded work and permanent losses
remain authoritative. Lower limits retain excess agents in `WorldCapsuleerSuspension`; restored limits reuse those agents.
Only missing eligible slots receive a founder grant. New global traffic starts with a saved initial journey, allowing ordinary
background travel while humans are in unrelated systems; it does not require a first visit to enroll. Existing journeys and
funded progress are preserved. Miners retain their separate switch and one-per-system limit. Saved gate
visitor ordinals obey the restart policy; new watched-gate encounters retain their existing preparation path.

Each community expansion commits independently and atomically with its saved metadata. An interruption preserves completed
communities and rolls back the failed community's assets, payments and identities. Retry fills the missing slots. Cancellation
and bounded preparation also retain committed work. The runtime rejects global reconciliation while a resident world owns the
save. There is no new absence reward, background ratting, ship replacement or native movement rule.

Provisioning policy version 6 and the shared fingerprint include catalog revision, seed, population policy and traffic snapshot
identity. Unchanged complete rosters are skipped on subsequent restarts. Missing supported fits or depleted mining resources
retain a deferred fingerprint and can retry on restart or system preparation. Startup shares catalog/world contexts across
communities, clears tracked assets after each commit and loads name/imported-character reservations once. Failed attempts discard
that reservation view; retries rebuild it from committed rows. This avoids repeatedly loading the entire saved roster for every
new imported pilot and preserves unique imported identities. The editor now states the global restart scope explicitly.

Gameplay reference: [EVE University hauling](https://wiki.eveuniversity.org/Hauling). Existing travel, hull restrictions and
retaliation remain unchanged; movement retains the pinned [Carbon Ball.cpp revision 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ball.cpp).

**Automated validation.** Development began with
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerGlobalPopulationTests.GlobalRestartFillsUnvisitedSystemRostersWithoutObservers`,
which failed against the old no-op global reconciler. Tests cover unvisited systems at 10×, all-catalog metadata, lowered/raised
limits, exact possessions/identity reuse, an actual permanent ship loss, partial rollback/reopen, cancellation, bounded retries,
imported identity uniqueness, snapshot changes, and cached restarts without fitting work. Development corrected a fixture that
selected a legitimately zero-traffic system and duplicate imported kill IDs. A broader run passed 358 cases and exposed three
older chat-test assertions that still expected per-pilot join events. Those now consume the established complete roster format;
the exact gate-arrival/miner-restart follow-up and new unobserved-travel case passed together.

After integration onto main `9735f197`, tool/solution restore and solution build passed with zero warnings/errors (5.52 seconds).
The final affected run passed **375 tests**, with no skips, in **223.56 seconds** including runner startup (feature build 1.10 seconds).
Both Python policy suites passed: eight catalog cases in 0.599 seconds and nine formatting cases in 0.014 seconds. Formatting
checked 1,568 files; the 140-character and diff checks passed. No full .NET suite was requested or run.

Final filter:
`FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~Eden.Protocol.Tests.Administration.AdministrationTests|FullyQualifiedName~WorldSettingsPageTests|FullyQualifiedName~WorldConfigurationTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~ServerServicesTests|FullyQualifiedName~MotionProjectionTests`.

Cached global preparation asserts zero fitting calculations, at most 16 SQL commands and at most four connections, independently
of catalog-size loops. Existing complete maintenance/observer-frame and detached-checkpoint assertions also passed. These are
automated synthetic-fixture results, not a full-galaxy capacity measurement; the running world was not changed by the agent.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Restart with a changed traffic multiplier and inspect the global preparation log and Population
page before visiting another system; lower and restore the multiplier across restarts to observe retained pilots returning.

## Resident ship blueprints (November 14, 2026)

Cold founder and inbound-visitor ship templates use a private typed fitting preview with the same resident inventory, skill,
fitting and supply operations as gameplay. Preparation takes place outside world ownership and publishes only immutable blueprint
values. The live provisioning action reserves identities and applies equipment, skills, timers and ship resources together.
Catalog reads are permitted during preparation. The founder regression checks that blueprint creation issues zero saved-world
SQL commands and runs outside world ownership. The offline traffic-import validator remains a separate tool.

## Traffic through unobserved systems

The first selected player admits saved, enabled travelers from resident membership across the map. A traveler can launch,
jump, dock and start the return leg while the player stays in an unrelated system. Inbound visitors use their saved
reciprocal gates and routes. Existing patrols and corporate supply trips continue on the same queue. Local mining and
solo NPC ratting use local observation, and the last player leaving pauses the shared activity clock.

Gate transfers commit the same ship, location and next job together. A checkpoint taken after arrival restores the ship to
that system and resumes the next leg on shared playtime. Peaceful offscreen travel uses static route geometry and the
existing travel deadlines. A player's entry enables detailed presentation from the current state. The reference evejs
snapshot (package 261108.1) tracks scene occupancy and always-on systems separately. Eden uses the bounded traveler queue
for offscreen progress. The gameplay reference describes jumps through gates to adjacent systems.
[EVE University Stargates](https://wiki.eveuniversity.org/Stargates)

**Automated validation.** The regression reproduced missing scheduler admission before the fix. It now checks a round
trip through unobserved systems with a restart after the outbound jump, the same ship and no native scenes in either
system. Existing coverage checks departure during a pending transfer, newly admitted traffic, the local mining and
ratting pause, and idle world frames with zero world SQL, connections or fitting calculations.

Tool and solution restore and the zero-warning solution build took 10.33 seconds. The final focused selection passed
180 cases in 107.99 seconds including startup. Both Python suites passed (eight catalog and nine formatting-policy cases),
and formatting and diff verification passed. Policy and format checks took 47.69 seconds. The full .NET suite was outside
the validation scope.

```text
(FullyQualifiedName~CapsuleerGlobalPopulationTests|FullyQualifiedName~CapsuleerEncounterTests|FullyQualifiedName~CapsuleerBackgroundPreparationTests|FullyQualifiedName~CapsuleerHandoffTests|FullyQualifiedName~CapsuleerProvisioningTests|FullyQualifiedName~OccupiedCapsuleerSimulationTests|FullyQualifiedName~ResidentPopulationTests|FullyQualifiedName~CapsuleerTrafficPerformanceTests|FullyQualifiedName~CapsuleerTrafficAcceptanceTests|FullyQualifiedName~CapsuleerInbound|FullyQualifiedName~RoamingGangTests|FullyQualifiedName~GateCampTests|FullyQualifiedName~PilotSpaceActivitiesTests|FullyQualifiedName~CapsuleerSchedulingTests|FullyQualifiedName~CapsuleerDeadline|FullyQualifiedName~CapsuleerMaintenanceTests|FullyQualifiedName~TenfoldTrafficCompletesGateTransfersAndStationVisits)&FullyQualifiedName!~ShoppingChoosesRealAffordableStockAndHonorsThreatMemory
```

The tenfold fixture completed gate transfers and station visits on both the patch and baseline `763a3118`. The separate
targeted runs both recorded 11 SQL commands, four connections and 22 fitting calculations over 180 simulated seconds for
one roster of 11 pilots. The final combined run of the same fixture recorded 87 commands, six connections and 22 fitting
calculations. Dedicated-server capacity remains unmeasured.

A wider development run was stopped after 358 reported cases. Separate checks on `763a3118` reproduced four of that
run's failures in overpriced shopping, rival-raid targeting and two disconnect-time founder projection variants.

**Live acceptance.** Not performed. Agent client testing is disabled.

**Optional maintainer checks.** Keep one player online outside a busy route and enter that route later to observe its
traffic after the server is updated.

## Paced offscreen starts

The previous login path made the entire offscreen roster's resume jobs due together when the playtime clock was already
running or restored from a save. Resuming a pilot does not increment the completed gameplay action count.

On the first login, the scheduler reads saved traveler membership from the resident world. Eligible pilots in occupied
systems enter the job queue immediately. Eligible offscreen pilots wait in order of saved deadline and pilot ID. The
scheduler starts up to four per existing 50 ms maintenance interval when the job queue has caught up. Missed intervals
give no additional start credit. Entering a system brings its waiting pilots into the queue immediately.

When a waiting pilot starts, the existing pause logic resumes from its saved job and checkpoint. The shared playtime
clock freezes with no players online. The performance page counts pilots waiting to start separately from scheduled
pilots and due jobs.

**Automated validation.** The regression `LoginDrainsLocalJobsBeforeStartingBoundedBatchesOfOffscreenTravelers` starts
from a save with five minutes of playtime. It checks local progress, the offscreen batch limit, repeated turns at the same
time, missed intervals and direct entry into a system with waiting pilots. An initial affected run exposed starvation
between coarse simulated wakes. The scheduler now stages the next batch as soon as due work drains, and the existing
unvisited-region progress test passes with that change.

The solution built with zero warnings. The affected selection passed 82 cases in 59.24 seconds including startup.
The checks include checkpoint/restart and warmed maintenance with zero SQL commands, connections or fitting calculations.
Both Python policy suites passed with eight catalog and nine formatting cases. Formatting verification passed in
52.96 seconds, and the diff check passed. Dedicated-server throughput remains unmeasured.

```text
FullyQualifiedName~CapsuleerGlobalPopulationTests|FullyQualifiedName~CapsuleerBackgroundPreparationTests|FullyQualifiedName~OccupiedCapsuleerSimulationTests|FullyQualifiedName~CapsuleerHandoffTests|FullyQualifiedName~ResidentPopulationTests|FullyQualifiedName~CapsuleerTrafficPerformanceTests|FullyQualifiedName~CapsuleerSchedulingTests|FullyQualifiedName~CapsuleerRuntimeTests|FullyQualifiedName~CapsuleerMaintenanceTests
```

**Live acceptance.** Not performed. Agent client testing is disabled.

**Optional maintainer checks.** On the first login after deployment, watch the offscreen waiting count and oldest queue
delay on the performance page as the roster starts.

## Compact navigation checkpoints

Saved miner and hauler navigation retains the follow target's identity and approach data. The checkpoint writer
omits collision meshes. Navigation resolves targets by identity and gets collision obstacles from the system view.
The schema 190 migration strips existing embedded meshes and reclaims free pages on disk before the host copies
the world into memory. The migration commits the payload changes before reclamation and retries the latter if
startup is interrupted. SQLite can require temporary disk space for this one-time compaction.
[SQLite VACUUM documentation](https://www.sqlite.org/lang_vacuum.html)

On a copied save containing 23,323 bots, a pre-fix headless replay saved 729 embedded meshes. Removing their
collision fields from a disposable copy reduced the compacted file from 529.34 MiB to 324.50 MiB.
On the dedicated Linux host, the saved hauler progress alone contained 991.2 MiB of text. Kernel logs confirmed
multiple out-of-memory kills near startup. The host recovered on the previous build before this change was deployed.

### Automated validation

The navigation regression writes a legacy follow target with a compound collider, restarts through the normal
world initialization path, checks the file shrinks, and compares the next movement step using the system's
collision obstacles. A subsequent checkpoint write also omits the mesh.

The solution build passed with zero warnings. This affected filter passed 74 tests in 65 seconds.
Both Python policy suites and formatting verification passed.
The Release startup probe used the copied 23,323-bot save, a 0.5 traffic multiplier and 729 seeded legacy
meshes. The production migration reduced the image from 618.05 MiB to 321.68 MiB. The headless host
then hydrated resident state and warmed the startup catalogs at a 2,158 MiB post-collection working set.

```text
FullyQualifiedName~CapsuleerTrafficNavigationTests|FullyQualifiedName~CapsuleerPopulationSettingsTests|FullyQualifiedName~CapsuleerHandoffTests|FullyQualifiedName~CapsuleerResidentRoutineTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~WorldInitializationTests|FullyQualifiedName~DetachedCheckpointTests
```

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer checks

Record startup RAM on the dedicated host with its current population setting after the normal update.

## Shared resident skill profiles

The resident skill table loads skills by pilot and type. Pilots with identical ordered skill IDs and 64-bit
point balances reference one immutable profile. A committed edit updates a private copy of the pilot's profile.
The store releases a profile after the last pilot stops using it. Skill lookups, checkpoint rows and the
SQLite update-hook identity map retain the same pilot and type keys.

### Automated validation

Matched Release startup probes used the same 23,323-bot save and 0.5 traffic multiplier. After catalog warmup,
the managed heap after collection fell from 1,227.2 MiB to 1,133.3 MiB. Process working set varied between the
two runs. Host hydration was 6.33 seconds for the control and 6.78 seconds with shared profiles. These are single
headless samples on the local workstation. The regression exercises equal starting profiles, rollback,
independent point edits, a SQL notification, a detached checkpoint, and reopening.

The solution build passed with zero warnings. The affected filter passed 220 tests in 88 seconds.
Both Python policy suites and formatting verification passed.

```text
FullyQualifiedName~ResidentEconomyTests|FullyQualifiedName~ResidentFitTests|FullyQualifiedName~PilotSkillBookTests|FullyQualifiedName~FreeSkillPoint|FullyQualifiedName~SkillExtraction|FullyQualifiedName~SkillInjector|FullyQualifiedName~CapsuleerProvisioningTests|FullyQualifiedName~CapsuleerGlobalPopulationTests
```

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer checks

None.

## Offscreen bot residency

Registration keeps a pilot's role, home community, ship, location and job. Waiting pilots allocate their role worker
when the scheduler prepares or runs a job or when an observation needs it. Pausing releases the worker and retains
the committed checkpoint and pause clock. A failed job likewise releases the worker and retries from resident state.

The resident owner tracks bot membership by system for transaction projection. Registration can update that index
without creating the system's flight, combat and observer state. A gate transfer moves membership to the next system
when its location commits.

### Automated validation

A matched Release login replay used the same copied save with a 0.5 traffic multiplier. The first scheduler turn
retained 1,174.0 MiB on the control and 1,109.3 MiB with lazy role workers and compact membership, after collection.
It created flight or combat state for 2,714 systems on the control and 25 with the change. Both runs left 7 scheduled
pilots and 3,697 waiting starts with the same completed-action count. Working set varied between the two runs.
The paced-start regression bounds retained workers while offscreen jobs are waiting and observes their promotion
on system entry.

The initial affected run passed 163 of 175 cases. The unchanged `f254c044` baseline reproduced all twelve failures.
Nine transaction participant cases counted four SQL commands against a budget of two. Two one-observer presentation
cases exceeded their 6 MiB allocation ceiling with zero observation reads. The retry test had kept a presence lease
alive past host disposal. Releasing the lease before the final flush and restart corrected the fixture, and the
exact method passed. The final filter excludes the two methods with pre-existing budget failures. The third
presentation case passed in the original run.

The solution build passed with zero warnings. The final filter passed 163 cases in 105 seconds. Both Python
policy suites and formatting verification passed.

```text
(FullyQualifiedName~CapsuleerGlobalPopulationTests|FullyQualifiedName~CapsuleerBackgroundPreparationTests|FullyQualifiedName~OccupiedCapsuleerSimulationTests|FullyQualifiedName~CapsuleerHandoffTests|FullyQualifiedName~ResidentPopulationTests|FullyQualifiedName~CapsuleerTrafficPerformanceTests|FullyQualifiedName~CapsuleerSchedulingTests|FullyQualifiedName~CapsuleerRuntimeTests|FullyQualifiedName~CapsuleerMaintenanceTests|FullyQualifiedName~CapsuleerPresentationTests|FullyQualifiedName~CapsuleerFailureTests|FullyQualifiedName~CapsuleerInboundRuntimeTests|FullyQualifiedName~CapsuleerLockOrderTests|FullyQualifiedName~RoamingGangTests|FullyQualifiedName~RegionalConflictWorldTests|FullyQualifiedName~ResidentBotEconomyTests|FullyQualifiedName~TransactionParticipantTests|FullyQualifiedName~DetachedCheckpointTests)&FullyQualifiedName!~ClassifiedProjectionDoesNotGrowWithRemotePopulationOrObserverCount&FullyQualifiedName!~UnchangedPresentationSharesObservationsAcrossAdvancingFrames
```

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer checks

None.

## Scheduler admission and idle release

The performance page reads saved pilot and community totals from the resident world. On login the scheduler reads IDs
and deadlines for eligible offscreen pilots from the resident indexes. It loads a pilot's job, ship membership and role
registration when that pilot enters the scheduled queue. An occupied system admits its eligible local pilots immediately.
Offscreen admission uses the existing four-pilot limit per 50 ms interval after due jobs drain. Population suspensions
exclude pilots from both paths.

The last logout clears the waiting queue and releases scheduler registrations, role workers and clean checkpoint copies.
The resident world keeps authoritative pilot and asset records, including changes that still differ from the checkpoint
source. The shared playtime clock, paid-work deadlines and checkpoint recovery use the existing pause rules.
The loaded job count measures registrations in the scheduler. Saved totals include suspended pilots.

The local EVE.js reference marks a solar-system scene empty after its last session leaves in
`server/src/space/runtime/spaceRuntime/sceneRegistry.js` (`noteSolarSystemSceneEmpty`, SHA-256
`2E0ABF2AB95F86D48F04BBFA805C758FA97888B5D7824680E6AFDCDD5B3C9B8C`). Eden applies its own scheduling policy to
resident bots. [EVE University capsuleer terminology](https://wiki.eveuniversity.org/NPC)

### Automated validation

The exact filter
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerGlobalPopulationTests.LoginDrainsLocalJobsBeforeStartingBoundedBatchesOfOffscreenTravelers`
passed in 10.01 seconds after a 3.98-second build. It checks the saved count at idle, zero loaded remote jobs at first login,
bounded starts, immediate admission on system entry and release after the last logout. Job inspection before and after
release checks that the committed deadlines are still readable.

The affected run matched 185 cases across population preparation, handoff, resident admission and routines, scheduling,
maintenance, inbound traffic, combat operations and checkpoint recovery. It passed 183 cases in 178.88 seconds after a
10.49-second build. The two `OccupiedCapsuleerSimulationTests` cases that expect transfers to pause offscreen work failed
on both revisions. Three resident admission failures also reproduced on `31abd819`. The final run excluded the two test
methods that produced those three failures.

A matched headless Release replay used copies of one save with 23,271 pilots and the same login and simulation clock.
The baseline `31abd819` loaded 23,271 scheduler jobs on the first turn and kept them after logout. The updated run loaded
24 jobs on the first turn and zero after logout. Both runs had 20,202 waiting starts on the first turn, 104 scheduled pilots
after 20 more turns, and 10 completed actions with zero failures. Managed heap samples after collection were 955.2 MiB
and 939.6 MiB on the first turn, and 993.8 MiB and 978.6 MiB after logout. Idle heap and working-set samples varied between
the runs, limiting the comparison to scheduler residency.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

Compare the saved and loaded counts on the performance page through first login and last logout.

## Capsule escape after ship loss

The November 18 server checkpoint showed three stranded hauler capsules in KFIE-Z. Their jobs retried with
the same saved-route error. After losing a ship on a regional sortie, the capsule could be outside the original
hauling route, leaving its normal travel step without a valid destination. The live catalog has a local NPC
station in KFIE-Z.

An in-space hauler capsule now takes a route from its current system to the nearest reachable NPC station by
gate count. It uses a local station when one is available. In a stationless system it uses the existing graph of
reciprocal, usable gates. The retreat follows ordinary alignment, warp, gate transfer, and docking checks,
including tackle and Crimewatch restrictions. The escape route is saved with the job's flight progress so it
survives a checkpoint and restart. The pilot waits for a replacement after docking. Existing capsules with
stranded hauling jobs can start the same escape on their next eligible action.

The behavior follows the warp-out and docking guidance in [Pod saving](https://wiki.eveuniversity.org/Pod_saving).

### Automated validation

The exact filter `FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerHaulerTests.CapsuleEscapesOutsideItsHaulingRouteAndDocksAfterRestart`
reproduced the saved-route error on `6d5c2c05` in 14.15 seconds. It passed in 13.22 seconds with the fix. The test
settles a resident ship loss outside the original route, checkpoints the escaping pod, restarts, and checks
that the original capsule takes the available gate and docks. The destroyed hull stays lost.

The affected selection passed all 89 cases in 105 seconds. It included `CapsuleerHaulerTests`,
`CapsuleerInboundRuntimeTests`, `RoamingGangTests`, `RegionalConflictWorldTests`, `CapsuleerTrafficRoutesTests`,
and `CapsuleerMinerTests.LostVentureUsesNormalLossSettlementAndWaitsDockedForReplacement`. The solution built
in 7.94 seconds with zero warnings or errors. Both Python policy suites and formatting verification passed.

### Live acceptance

Not performed. Agent client testing is disabled. Read-only checkpoint and catalog queries confirmed the
stranded jobs and the local station before deployment.

### Optional maintainer check

After deployment, observe a previously stranded capsule in KFIE-Z and confirm it docks at the local station.

[Earlier capsuleer implementation records](https://github.com/CJ-SPT/Eden/blob/c31ac05af42c133567e203bb6859799c19af3852/docs/protocol/capsuleers.md)

## Earned bot career training

Resident miners, haulers and combat pilots spend their unallocated skill points when they dock. They rotate
between work qualifications, a saved ship goal, and the Magic 14 at level IV. Work qualifications cover mining,
hauling and combat skills, Industry and Reprocessing for producers, and Corporation Management for prospective
founders and CEOs. Existing prerequisites determine what a bot can train next. A blocked skill leaves room for
another actionable goal. [EVE University skills and learning](https://wiki.eveuniversity.org/Skills_and_learning) ·
[The Magic 14](https://wiki.eveuniversity.org/The_Magic_14)

Ship goals draw from published build-3662168 hulls that Eden can assemble in the pilot's career. The catalog supplies the
hull requirements and the transitive requirements of its skills. A miner can draw a Venture, a barge or an
exhumer, among other eligible hulls. Haulers and combat pilots draw from their own hull classes. The ship goal and
next training focus follow the pilot through checkpoint reopen and ship loss. A goal establishes skill priorities.
The existing fitting and procurement systems govern ownership and boarding.

Bots apply points through the same allocation, prerequisite and history rules as human pilots. They inject owned
books or pay for missing skills using the normal direct purchase or station market. Skillbook purchases use up to
one quarter of the pilot's current wallet. The simulated capsuleer account is permanently Omega. Startup repairs older Alpha
records for that account, and the resident write boundary rejects a downgrade. Human accounts keep their saved clone
grades. The local evejs reference `skillMgrService.js` was checked for separate free-pool and skill-application
operations. Its SHA-256 is `64D7E60FC25C835CBB87C535BC10758AFADE4D17B23CE163BB8DED3C37451B5F`.
Existing protocol calls serve both kinds of pilot.

### Automated validation

Final tool restore, solution restore, and build passed with zero warnings or errors. All 161 affected
.NET cases passed in 1m 06s. Both Python suites passed, with eight catalog and nine formatting-policy
cases. Formatting and documentation link checks passed. The build and verification sequence took
102.02 seconds. The final filter combined these names through
`FullyQualifiedName~`.

```text
CloneGradeTests | FreeSkillPointTests | FreeSkillPointServiceTests | SkillPurchaseTests
PilotSkillBookTests | PilotTrainingTests | MiningSkillPointRewardTests | NpcSkillPointRewardTests
CapsuleerMiningFleetTests | CapsuleerInboundRuntimeTests
ActualCombatFitCanClearAnOrdinaryPirateAndCannotCollectItsBountyTwice
```
### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer checks

None required for the earned-SP allocation and Omega checks.

## First bot career ships

New pilots work toward a standard Tech I hull first. The pinned catalog supplies the Venture for mining, the
Badger for hauling, and the Merlin for combat. Later targets require the pilot to have boarded an earlier
career hull, and the saved plan retains the highest stage reached across checkpoints and ship losses. A saved
goal from an earlier release is checked against the pilot's current stage. Earned skill points fund both the
hull prerequisites and the skills required by its working fit. Career and Magic 14 training continue.

At the station, a qualified pilot checks personal inventory and shops for the missing hull, modules and charges.
The bot pays with its own ISK through the resident market. Where a supported career part is missing, it can
place a funded station buy order. The fleet and fitting stores assemble the packaged hull, online the required modules,
load combat magazines, and board the owned ship. The docked worker keeps its old hull and uses the same
undock and activity code for the new one.

Mining goals cover belt ships with a laser fit. Hauling goals use a travel fit where the hull supports it,
and combat goals use a catalog weapon and matching charges. Specialized hulls without a supported bot fit,
such as drone-only mining command ships and strategic cruisers that need subsystems, are outside the goal pool.

The Venture's opening role follows [EVE University's ORE hull guide](https://wiki.eveuniversity.org/ORE_Basic_Ship_and_Skill_Overview).
The local evejs reference checks ship ownership and skills in `_activateShipById` before activation
(`shipService.js` SHA-256 `C34C3573C11CBAECA041D2CDC4791E19DB7DB6759A94F73125AD71E6B62848F1`).

### Automated validation

The original acquisition replay and its measured timings are in the earlier implementation record linked above.

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer checks

None.

## Offscreen mining, belt ratting, and industry

The scheduler registers established mining and hauling pilots on the first human selection and admits distant jobs in its
existing four-pilot batches. Prioritization still gives due jobs a chance to drain before starting more pilots. Unobserved
industrial settlements use the existing corporate wallets, reserved inputs, blueprint slots and public market listings at
NPC stations. Eligible combat-fit travelers can start ordinary belt sorties and settle ammunition, wrecks and bounties
through the same combat path they use in observed systems. Jobs load resources and native combat state without publishing
an observer scene. Offscreen anomaly scouting and player-structure staging are outside this slice.

When the last human deselects or disconnects, new bot actions stop. The same persisted human-active clock measures the
remaining duration of jobs installed by the scheduled bot factory. The industry service checks that deadline when reading
readiness or delivering a job after an offline gap. Older installed jobs retain their original deadlines. Committed effects
retain their normal settlement rules. Ordinary player industry retains its wall-clock behavior.

The mining and industry paths follow the existing catalog, fitted equipment and paid production model.
[EVE University careers](https://wiki.eveuniversity.org/Careers) · [Manufacturing](https://wiki.eveuniversity.org/Production)

The local evejs scene reference marks empty solar-system scenes after their final session leaves in
`server/src/space/runtime/spaceRuntime/sceneRegistry.js` (`noteSolarSystemSceneEmpty`, local file SHA-256
`2E0ABF2AB95F86D48F04BBFA805C758FA97888B5D7824680E6AFDCDD5B3C9B8C`). Eden continues to separate observer
scene admission from resident ownership. This change uses Eden's existing gameplay owners and client messages.

### Automated validation

The exact filter `FullyQualifiedName~TravelerRatsUnobservedAndPausesWhenTheLastHumanLeaves` passed both cases in 10.32 seconds.
It covers a pause during an ordinary pirate fight, the native bounty across reopen, and cargo recovered from owned NPC wrecks.
`UnobservedMinerAndPaidFactoryPauseAndResumeAcrossRestart` verifies ore collection and paid ammunition delivery after
a zero-player pause and restart, with the only player in another system. A 19-case rerun including both scenarios,
the miner observer handoff, Miner II production through copying and invention, and world migrations passed in 14.18 seconds.

The broader filter selected `PilotSpaceActivitiesTests`, `CapsuleerAmmunitionSupplyTests`, `CapsuleerMinerTests`,
`CapsuleerHaulerTests`, `CapsuleerGlobalPopulationTests`, `CapsuleerBackgroundPreparationTests`,
`CapsuleerPopulationSettingsTests`, `CapsuleerDeadlineTests`, `CapsuleerInboundRuntimeTests`, `IndustryTests`,
and `IndustryContainerTests`. It passed 138 of 141 cases in 133.11 seconds before updating the miner handoff and Miner II
fixtures to exercise the human-active clock. Both updated cases passed the subsequent rerun. The remaining failure,
`ShoppingChoosesRealAffordableStockAndHonorsThreatMemory` with an overpriced offer and no avoided system, reproduced
on milestone 4's `main` at `9e2e42bc`. The solution build passed with zero warnings and errors. Both Python policy
suites and formatting verification passed.

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer checks

None.

## Player-structure bot factories

Bot-led corporations can stage an operator at a same-system Upwell facility with docking permission for the hull and an
online manufacturing service for the product. Facility choice prefers corporate ownership and then stable structure ID.
The operator uses an owned blueprint and available material stacks from the facility or the departure station. A move uses
the normal cargo hold, applies volume and quantity limits, and commits cargo and the chosen destination in the same
checkpoint generation. The approach and docking use normal flight, range, Crimewatch and access checks. If access is
lost en route, the pilot returns to the departure station with the surviving cargo.

The facility can manufacture ammunition, the supported starter Merlin kit parts, or Miner II modules when the applicable
blueprint, materials, skills and service are available. Native manufacturing owns material consumption, blueprint
reservation, structure bonuses and the corporate debit. Finished stock goes to the operator's Item Hangar in the structure.
Regional orders and their fulfilment use NPC markets. A miner returning to deliver a finished job first settles its current
paid mining cycle.

The human-active deadline and the service suspension point both survive checkpoints. If the service goes offline,
reopening it extends the bot deadline by the human-active time spent suspended. Time when the last player has logged off
does not count toward the job or extend it twice during an overlapping service outage.

Eden uses pinned build 3662168 profiles for docking envelopes, manufacturing modifiers and fitted service requirements.
The local evejs reference checks docking service, character access and hull eligibility in `canCharacterDockAtStructure` in
`server/src/services/structure/structureState.js` (SHA-256
`E12D487E9E2E0E5486CE03A60B1178EAADC5A74C0BE39813B026F16206376C1C`).
[Fitting Upwell structures](https://wiki.eveuniversity.org/Fitting_upwell_structures) ·
[Structure permissions](https://wiki.eveuniversity.org/Structure_permissions)

### Automated validation

The exact filter `FullyQualifiedName~BotStagesAtAPlayerFactoryAndFinishesItsPaidJobAfterServicePauseAndRestart` passed
in 10.93 seconds. It checks denied access and a fitted-but-offline plant, physical cargo and native docking across reopen,
one paid corporate installation, logout while the service is online, a further service pause across restart, and single
delivery to the structure's Item Hangar after the active deadline.

The 172-case focused selection passed in 173.23 seconds. It covered `CapsuleerAmmunitionSupplyTests`,
`CapsuleerMinerTests`, `CapsuleerHaulerTests`, `CapsuleerBackgroundPreparationTests`,
`TravelerRatsUnobservedAndPausesWhenTheLastHumanLeaves`, `StructureTests`, `StructureControlTests`,
`IndustryTests`, `IndustryContainerTests`, `IndustryFacilityLookupTests`, `ReactionJobTests`, `ScienceJobTests`,
and `WorldMigrationTests`. The solution built in 7.84 seconds with zero warnings or errors. Both Python policy
suites, formatting verification and the whitespace check passed.

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer checks

None.

## Fractured moon ore and paid reactions

The subsequent [Athanor-to-Miner II chain](#athanor-to-miner-ii-chain) adds funded demand, composite reaction dependencies,
factory freight, and replacement fuel. This section records the original one-reaction milestone.

A miner based at a same-system NPC reprocessing station can work an unexpired fracture from its corporation's Athanor.
The saved extraction ID and native rock IDs limit the trip to that field. The miner uses a fitted laser and the regular
capacitor, ore hold, and collection rules. It returns with a whole reprocessing portion or a full hold, and the native
mining ledger credits the Athanor. The NPC station quotes yield and tax and reprocesses the owned ore into usable feed.

A bot with the required formula, skills, and a complete batch of ingredients can stage at its corporation's low- or
null-security Athanor with an online composite reactor. The pilot carries the refined feed and any other needed stacks
and formula in its own cargo hold. After normal docking, the resident industry operation reserves the formula, consumes
the inputs and recipe fuel, and debits the corporation once. The reaction uses the human-active deadline and structure
service pause rules. Output is delivered to the operator's Item Hangar at the Athanor.

This slice uses already-commissioned refineries, same-system logistics, and existing stocks of complementary inputs
and fuel. The remaining milestone includes fuel and ice sourcing, broader facility logistics, and the Miner II chain.

The local evejs reference uses ordinary persistent asteroid rows for fractured moon rocks in
`server/src/services/structure/moonMiningSpaceRuntime.js` (SHA-256
`F880203C91160399B1333D6F5BBA1C8A7D691E5FF9E727B8852C497BCD8FB17D`). Eden uses its own pinned catalog and
native mining, reprocessing, and industry operations.
[Moon mining](https://wiki.eveuniversity.org/Moon_mining) · [Reprocessing](https://wiki.eveuniversity.org/Reprocessing) ·
[Reactions](https://wiki.eveuniversity.org/Reactions)

### Automated validation

`BotMinesAnOwnedFractureAndHaulsReprocessedFeedForOnePaidReaction` exercises native auto-fracture, the moon mining ledger,
ore reprocessing, cobalt cargo across restart, native reaction inputs and fees, and delivery after a second restart.
The isolated catalog credits the selected ore grades with cobalt. The scenario stages cadmium, a formula, and fuel
as complementary stock and obtains all of the cobalt from mining and reprocessing.

The exact filter `FullyQualifiedName~BotMinesAnOwnedFractureAndHaulsReprocessedFeedForOnePaidReaction`
passed in 13.95 seconds. The final focused run passed 90 cases in 109.45 seconds, covering
`CapsuleerAmmunitionSupplyTests`, `CapsuleerMinerTests`, `CapsuleerHaulerTests`, `MoonExtractionTests`,
`MoonMiningRulesTests`, `MoonMiningWarpTests`, `ReactionJobTests`, `ReprocessingTests`, and
`WorldMigrationTests`. The solution built in 10.84 seconds with zero warnings or errors. Both Python policy
suites, formatting verification, and the whitespace check passed.

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer checks

None.

## Athanor-to-Miner II chain

A funded Miner II order can assign a corporate miner to an owned Athanor and a reachable NPC station with
market, reprocessing, and manufacturing services. The factory plans advanced component shortages from catalog
recipes. The operator works backward from those needs through the composite reactions, accounting for stock at
the station, in the reactor hangar, and in corporate freight. The miner reprocesses moon ore with corporate funds,
sources complementary inputs and formulas through ordinary supply, and stages batches in loads that fit the ship.

The operator returns required reaction output to the NPC station. A corpmate physically hauls owned stacks
to the requesting factory, where jobs make Photon Microprocessors and Laser Focusing Crystals. The existing
copying, invention, and Miner II jobs deliver stock for the funded order. Jobs, cargo and escrow retain their
normal resident ownership, checkpoint, and human-active clock rules.

Service fuel and the fuel each reaction consumes are budgeted separately. The operator manufactures blocks from
owned and paid inputs. A fitted ice harvester can work eligible local public ice sites. The NPC station reprocesses
ice. A courier deposits service fuel, brings services online, and starts the next extraction when the old field
is gone. The published recipe graph comes from build 3662168. The EVE.js moon-fracture reference remains the
snapshot recorded above. [Tech II manufacturing](industry.md#tech-ii-manufacturing-integration) ·
[Supply audit](../tech-two-supply-audit.json) · [CCP reactions](https://support.eveonline.com/hc/en-us/articles/115005405785-Reactions)

### Automated validation

The scheduler replay starts with a commissioned Athanor, complementary stock, one owned ice block, and bootstrap
fuel. It uses an isolated catalog with controlled material counts, job durations, and invention chance. The only
human is in another system. The replay reopens during a paid reaction and service outage, with courier cargo on
board, and during final manufacturing. After the first sale, the service fuel runs out and the old field expires.
The bots reprocess the ice, manufacture replacement fuel, fill the Athanor, and start a second extraction.
After another reopen, they mine the new field and sell the second module. The test checks job outputs, corporate
charges, cargo, and settled escrow. It does not establish supply availability with the unmodified catalog, live
ice acquisition, or dedicated-server pacing.

The exact filter was
`FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerAmmunitionSupplyTests.BotsDeliverTwoMinerTwoBatchesAcrossRestartsAndReplaceBootstrapReactionFuel`.
The replay passed in 45.48 seconds including runner startup. An initial affected run found that corporate
reprocessing charges had also reached the original single-reaction flow. Restricting those charges to assigned
Miner II supply preserved that flow. Its exact filter passed in 13.15 seconds.

The affected selection passed 105 cases in 1 minute 51 seconds. The filter matched
`CapsuleerAmmunitionSupplyTests`, `CapsuleerMinerTests`, `CapsuleerHaulerTests`, `MoonExtractionTests`,
`MoonMiningRulesTests`, `MoonMiningWarpTests`, `ReactionJobTests`, `ReprocessingTests`, `IceMiningTests`,
and `WorldMigrationTests` by fully qualified name. The build, both Python policy suites, formatting,
and whitespace checks passed. This sequence took 156.35 seconds. After requiring a core and all three fitted
services before assigning a refinery, the final build and five focused cases passed. Those cases cover the
two-batch replay, the original single reaction, and the three refinery commissioning scenarios
(`BotsBuildAndCommissionTheirMoonRefineryAcrossRestarts`). Tests took 58 seconds and the final run took 88.84 seconds.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

Observe the refinery and destination factory during a funded Miner II order.
