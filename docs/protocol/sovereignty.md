# Sovereignty

[Earlier sovereignty implementation record](https://github.com/CJ-SPT/Eden/blob/c31ac05af42c133567e203bb6859799c19af3852/docs/protocol/sovereignty.md)

## Local skyhook and resource system resolution

The pinned `eve_public.sovereignty.skyhook.api.GetAllLocal` reply now uses the active ship's resolved solar system for both
its `solar_system` field and the local Skyhook rows. Docking at an Upwell structure uses that structure's system. NPC undock
positions with an implicit system use the departure station's system. Local deployment and resource transfers use the
same resolution while enforcing the existing in-space and range checks. Local EVE.js `sovModernState.js` (SHA-256 prefix
`dd128ff4a953`) also reads the local Skyhook index using the active identity's solar system.
[CCP Orbital Skyhook](https://support.eveonline.com/hc/en-us/articles/14339732733724-Orbital-Skyhook)

## Sovereignty hub custody after corporation departure

A sovereignty claim belongs to the alliance. Hub management requires roles in the owning corporation. If that
corporation leaves, its hub and campaign command nodes pass to the active executor corporation in the same resident
transaction. The new owner inherits the existing hub, claim timestamp, campaign and upgrades. When an alliance has
no executor, it keeps the claim and grants management when executor support resolves. An empty alliance remains
subject to its existing downtime closure. First sovereignty maintenance also repairs old saves in which a surviving
alliance still assigns a hub to a former member despite having an executor.
[CCP Sovereignty Hub](https://support.eveonline.com/hc/en-us/articles/14339751569436-Sovereignty-Hub) ·
[UniWiki Sovereignty](https://wiki.eveuniversity.org/Sovereignty)

EVE.js **261108.1** transfers hub custody to the active executor or waits for one in
`allianceDepartureState.js::settleDepartureSovereignty`
(`58206ff6dedfa7fca3b3769ba20df4856ff267aee67174cd4f23a2862406e4ff`) and
`allianceSovereigntyLifecycle.js::transferAllianceSovereigntyOwnership`
(`1ad250dfd2b68dedb5efea76a0a80071089818433875aa1e729baf20c3c89a7`). Eden refreshes claim and structure owners
using the existing `ProcessSovStatusChanged` and `OnSolarSystemSovStructuresUpdated` notifications from the pinned
build **3662168** `sovSvc.pyj` (`37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`).

**Automated validation.** `SovereigntyClaimCustodyTests` covers immediate transfer,
executor election after restart, and repair of a checkpoint with an outdated custodian.
It verifies management permissions, remote status updates, and preservation of the
existing hub, campaign nodes and upgrades. The custody, dissolution, membership,
corporation closure, upgrade and warfare filters passed 46 cases.

**Live acceptance.** Not performed; agent client testing is disabled.

## Sovereignty cleanup on alliance dissolution

At the saved downtime deadline, an alliance that closes for nonpayment or after its last corporation leaves loses each
remaining claim. Eden removes the claim and capture hub, marks the structure destroyed, drops installed upgrades, fuel
and hub workforce configuration, and shuts down services that depended on the claim. It also removes campaign command
nodes by source system, including nodes in neighboring systems, and clears the current and pending capital. A paid
Entosis cycle on a removed target stops scoring immediately and keeps its ship reservation until the paid interval ends.
A later claim requires the normal new-hub deployment and capture. [CCP closure guidance](https://support.eveonline.com/hc/en-us/articles/203209062-Corporation-Alliance-Closure) ·
[CCP Sovereignty Hub](https://support.eveonline.com/hc/en-us/articles/14339751569436-Sovereignty-Hub) ·
[UniWiki Sovereignty](https://wiki.eveuniversity.org/Sovereignty)

Closure and claim loss commit under the same resident owner and project through the existing checkpoint. The first
sovereignty maintenance pass repairs older checkpoints that still assign claims to an already-closed alliance, using
its saved closure time. Campaign defeat and dissolution share the same hub cleanup for resource and service state.

EVE.js **261108.1** drops core sovereignty structures, hub and campaign data, and affected capital selections in
`allianceSovereigntyLifecycle.js::dropAllianceSovereigntyClaims`
(`1ad250dfd2b68dedb5efea76a0a80071089818433875aa1e729baf20c3c89a7`).
`allianceDepartureState.js` (`58206ff6dedfa7fca3b3769ba20df4856ff267aee67174cd4f23a2862406e4ff`) calls it during
durable alliance closure. The pinned build **3662168** archive SHA-256 is
`37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`. In `sovSvc.pyj`,
`ProcessSovStatusChanged` line 74 clears a system's claim on a None status, and
`OnSolarSystemSovStructuresUpdated` line 127 replaces its structure list. The client retains system caches on
`allianceid` changes, so the monitor refreshes a founder's previously observed remote territory when dissolution
removes it, as well as publishing changes in the local system.

**Automated validation.** The exact-method run passed six cases in 9.81 seconds after a 1.26-second build.
It covers unpaid and empty-alliance deadlines, claim and hub teardown, campaign nodes in another system,
unrelated claims and Skyhooks, paid Entosis expiry, legacy checkpoint repair, and observation of the claim
and structure updates. The dissolution and legacy cases checkpoint the removal and reopen the saved world
to verify that the claims remain absent.

The solution built with zero warnings and errors. All 260 focused cases passed, with 88.33 seconds for the build
and tests together. The `FullyQualifiedName~` selectors were `Eden.Protocol.Tests.Sovereignty`,
`Eden.Protocol.Tests.Alliances`, `Eden.Protocol.Tests.Maps.SovereigntyTests`, `MapStartupTests`,
`CapsuleerAllianceTests`, `CorporationClosureTests`, and `DetachedCheckpointTests`. The run also included the exact
`PharoluxJumpRechecksAccessAndJammerDeadline` and `SotiyoPausesAndCompletesPinnedAeonRecipeWithSovereigntyUpgrade`
cases. All eight catalog checks and nine formatting policy checks passed, along with `Format.ps1 -Check` and
whitespace verification.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Observe a claimed system through alliance dissolution and refresh the sovereignty map after reconnecting.

## Alliance Systems window

Build **3662168** `FormAlliancesSystems.Load` calls `stationSvc.GetSystemsForAlliance(session.allianceid)` before building
the Systems dashboard. The original archive has SHA-256 prefix `37fb2af4fa0e`. EVE.js **261108.1**, build **3396210**,
answers with a list of `utillib.KeyVal` records containing `solarSystemID` and `allianceID`. The reference is in
`stationService.js` (`34c675bfafdf`), `sovPayloads.js` (`998c373de30e`), and `sovState.js` (`674bf18c6288`).
[Sovereignty](https://wiki.eveuniversity.org/Sovereignty)

Eden reads the committed sovereignty claims through the resident alliance index and returns matching systems ordered by
system ID. An alliance without claims returns an empty list. A call without arguments uses the selected pilot's alliance.

**Automated validation.** `ClaimedHubRepliesMapRowsSlimScoreAndNotificationsUseNativeShapes` passed through `Test-Feature.ps1`
in 20.86 seconds after a 10.77-second build. It covers selection gating, native row fields, the session-alliance fallback,
empty and malformed inputs, separation between alliances, and zero SQL commands and connections on the warm read.
The solution build finished in 1.12 seconds with zero warnings or errors. The following filter passed 19 tests in 9.94
seconds. The catalog setup and formatting policy suites passed 8 and 9 tests. `Format.ps1 -Check` and the whitespace check passed.

```text
FullyQualifiedName~Eden.Protocol.Tests.Sovereignty.SovereigntyProtocolTests|FullyQualifiedName~Eden.Protocol.Tests.Maps.MapStartupTests|FullyQualifiedName~Eden.Protocol.Tests.Maps.SovereigntyTests|FullyQualifiedName~Eden.Protocol.Tests.Docking.StationDirectoryTests
```

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Open the corporation window's **Alliances → Systems** tab for an alliance that holds systems.

## Autonomous first claims

After a Sovereignty Hub reaches the executor's headquarters, a free docked corporate hauler can use its existing
qualified ship, fitted Entosis Link and fuel. If it needs equipment, it can buy the shortfall for a Squall,
Entosis Link I and Strontium Clathrates from local sell orders, preserving a month of alliance dues. The normal fitting
and boarding operations fit the link and leave the old hull in the station. The standard Squall plan qualifies the
pilot in Upwell Hauler V under Eden's existing bot skill policy. Its infrastructure hold can carry the 60,000 m³ package.
The equipment and minimum outbound fuel are prerequisites for leaving the station. This first expedition uses one ship
for hauling and capture.

The executor assigns Station Manager through the corporation role store. Cargo loading, the gate route and the
expedition assignment settle together. The pilot takes ordinary gates to the first available site within three jumps,
including stationless systems. Arrival rechecks current claim state, then uses ordinary neutral deployment. The pilot
warps to the actual deployed hub, waits for a target lock, and pays capacitor and fuel for normal Entosis cycles. A
rival can still contest the neutral hub through the existing capture rules. Completion publishes the normal claim,
and the courier returns to headquarters after any paid travel restriction ends.

The route, cargo and deployed hub ID are checkpointed. A lost ship, unavailable hub or lost expedition authority clears
or ends the assignment. Normal paid cycle reservations and capture progress survive a checkpoint, and an active scoring
lease has to be acquired again. A pause invalidates the scoring lease. When unattended capsuleer activity is enabled,
the [alliance review](alliances.md#autonomous-bot-alliances) and first expedition can advance with zero selected players.
Local equipment availability and funds determine admission.

Pinned build 3662168 supplies the Squall hull (81008), Entosis Link I (34593), hold rules and the hub's catalog capture
duration. The existing EVE.js snapshot's neutral-versus-owned capture contract is recorded in
[first-claim procurement](alliances.md#first-claim-hub-procurement). [CCP Entosis Link](https://support.eveonline.com/hc/en-us/articles/203729281-Entosis-Link) ·
[UniWiki Sovereignty](https://wiki.eveuniversity.org/Sovereignty)

### Automated validation

The exact `CapsuleerAllianceTests.FundedAllianceStagesOneHubAcrossRestartForAnUnobservedClaimableSystem` filter passed two
cases in 11.86 seconds including runner startup. It covers a real sell order for the hub, paid acquisition and fitting of
the Squall kit, infrastructure cargo across checkpoint reopen, reciprocal gate travel, neutral deployment, fuel payment,
an empty-server interruption, ordinary claim settlement and the trip back to headquarters. Development corrected an
artificial gate placed inside the fixture star and gave the return an operational dock stop for immediate settlement.

The [November 28 unattended replay](alliances.md#autonomous-bot-alliances) confirms the first claim and return with zero
selected players, followed by a paid response to a home disruption and command node, reagent delivery and invoice settlement.

Tool and solution restore and the solution build passed with zero warnings and errors. The affected .NET selection
passed 339 cases in 2 minutes 10 seconds. The `FullyQualifiedName~` filter combined `Eden.Protocol.Tests.Sovereignty`,
`Eden.Protocol.Tests.Alliances`, `CapsuleerAllianceTests`, `CapsuleerInboundRuntimeTests`, `CapsuleerTrafficRoutesTests`,
`CapsuleerTrafficNavigationTests`, `CapsuleerDeadlineTests`, `CapsuleerAmmunitionSupplyTests`, `CapsuleerFailureTests`,
`PersonalMarketTests` and `Eden.Protocol.Tests.Market.ResidentEconomyTests`. Both Python policy suites passed (8 and 9 cases).
Formatting and diff checks passed. The complete sequence took 167.60 seconds and used isolated worlds.
Formatting and diff checks passed. The complete validation sequence took 165.24 seconds and used isolated worlds.

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer checks

Follow a funded bot expedition through deployment and confirm the owner changes on the in-game sovereignty map.

## Bot home defense and upkeep

A bot alliance uses its earliest current claim as home for border strategy. Docked pilots review hubs and campaign nodes
across all of the alliance's holdings. They prioritize live hostile links, then claims nearer to defeat and targets nearer
to attacker control. A reachable objective reserves one outbound defender. Other available pilots can respond to different
objectives while defense, claim, attack or supply expeditions are underway. Saved assignments retain reservations across
restart. One fitted pilot can stand by at the executor's headquarters between assignments.

Defenders use ordinary travel, target locks, fuel, capacitor and Entosis. A node requires a route to its physical system
and continued ownership of the claim it represents. Departure and each on-grid action revalidate the objective. Paid
travel restrictions apply on retreat. Defenders update the native hub or campaign score through the existing contention
and reinforcement rules. [CCP command-node events](https://support.eveonline.com/hc/en-us/articles/203728981-Command-Nodes-Event)

The executor uses the existing recurring alliance invoice and designated wallet division. The bot review enables the
native maintenance default for existing bot alliances with a claim and respects any explicit billing preference.
Defensive outfitting and local reagent purchases retain one monthly alliance reserve. When a configured home upgrade's
reagents run below its remaining startup cost plus two hours of consumption, a courier can purchase and carry stock toward
startup plus four hours. A reachable hub with configured upgrades can receive deliveries if the executor corporation
owns it. Its Station Manager deposits the cargo through the native fuel ledger, and power, workforce, startup and hourly
rules govern runtime. Upgrade installation and Skyhook deployment remain ordinary corporation actions.

Build 3662168's command node (34832) has a catalog radius of 243 m and omits a dogma signature. The resident signature
read falls back to the physical radius for this type, so both player and bot targeting can use the ordinary lock timer.
The EVE.js 261108.1 snapshot also uses physical radius as a targeting fallback in `server/src/space/runtime/targeting.js`
(SHA-256 `C8FB927B47CDE92FB582ADC75FB8A34A6ABF697DE890ABA25E3EC9500BF37C14`). Its neutral, owned and command node
Entosis contracts are recorded in [first-claim procurement](alliances.md#first-claim-hub-procurement).
[CCP sovereignty resources](https://www.eveonline.com/news/view/sovereignty-updates-transition-and-upgrades) ·
[UniWiki Sovereignty](https://wiki.eveuniversity.org/Sovereignty)

### Automated validation

The exact `CapsuleerAllianceTests.FundedAllianceStagesOneHubAcrossRestartForAnUnobservedClaimableSystem` selection passed
two cases in 14.11 seconds including runner startup. The extended case creates and captures the first hub, stages a
disrupted hub and an active campaign node, and verifies that the actual guard's paid cycles restore each defense score.
A supplier fills the reagent order and the courier deposits the cargo, paying startup and leaving the projected reserve.
The native invoice settles once from the executor wallet. Reopening preserves the claim, fuel ledger and paid wallet state.
Development surfaced the need for a docked standby between operations and the absent command-node signature attribute.

Tool and solution restore and the solution build passed with zero warnings and errors. The affected .NET selection
passed 331 cases in 2 minutes 12 seconds. The `FullyQualifiedName~` filter combined `Eden.Protocol.Tests.Sovereignty`,
`Eden.Protocol.Tests.Alliances`, `CapsuleerAllianceTests`, `CapsuleerInboundRuntimeTests`, `CapsuleerTrafficNavigationTests`,
`CapsuleerAmmunitionSupplyTests`, `TargetingTests`, `CorporationFinancialPanelTests`, `CorporationWalletTests`,
`PersonalMarketTests` and `Eden.Protocol.Tests.Market.ResidentEconomyTests`. Both Python policy suites passed (8 and 9 cases).

The exact `FullyQualifiedName=` selection for the three methods below passed seven cases in 29.49 seconds after
a 10.19-second build. All three methods belong to `Eden.Protocol.Tests.Capsuleers.CapsuleerAllianceTests`.

- `UnattendedDefendersProtectLaterClaimAndDivideCampaignNodesAcrossRestart`
- `FundedRivalsContestAnOwnedHubUnattendedAcrossRestart`
- `FundedAllianceStagesOneHubAcrossRestartForAnUnobservedClaimableSystem`

The new unattended fixture stages disruption on the later of two claims and observes a paid defender restore the hub.
It then stages a campaign for that claim with one node in each system. The first outbound assignment survives a
checkpoint. A second pilot takes the other node, and both pay for overlapping cycles. The campaign reaches native
defender victory and both holdings survive. Using pre-owned headquarters stock, a courier fuels the later hub's
configured upgrade and settles startup with four hours of reagent remaining.

The broader `FullyQualifiedName~` selection passed 408 cases in 178.13 seconds. It combined `CapsuleerAllianceTests`,
`CapsuleerCorporationTests`, `Eden.Protocol.Tests.Alliances`, `Eden.Protocol.Tests.Sovereignty`, `CapsuleerMaintenanceTests`,
`CapsuleerPopulationSettingsTests`, `RegionalConflictTests`, `RegionalConflictWorldTests`, `CapsuleerTrafficNavigationTests`,
`TargetingTests` and `PersonalMarketTests`. The solution built in 0.72 seconds with zero warnings or errors. All 17 Python
policy cases passed in 1.75 seconds. Formatting and whitespace checks passed with the local recovery archive excluded
from CSharpier.

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer checks

Observe a funded guard dispatch during a home-system disruption and check the defense score in the sovereignty UI.

## Entosis replacement and fuel stock

The headquarters fleet reserve includes Strontium Clathrates for an online Entosis fit or a verified recent loss of
one. A defender short of the required fuel can take ordinary supply or other docked work. Regional freight checks
reachable suppliers and carries paid stock to the corporation's Market Deliveries. An Entosis fuel trip buys the
quantity available at its approved limit. The executor's purchases retain its monthly alliance dues reserve, with
affordability checked again at the supplier. Purchased cargo and the route use the existing freight checkpoint.

Expedition outfitting combines the pilot's hangar, owned corporate stock and local market purchases. A fitted defense
Squall can refuel with its existing link. Outfitting prefers a spare cycle and can use the exact minimum when stock or
cargo space is tight. A lost ship's assignment is released by the normal ship-loss path, so a fueled pilot can take a current
objective. Replacement and return continue through ordinary docking, fitting and paid Entosis.

Build 3662168 supplies the fuel type and quantity through Entosis dogma. The EVE.js 261108.1
`entosisLinkRuntime.js` reference delegates locking, capacitor and fuel payment to its module runtime. The inspected
file has SHA-256 `afe03a1e9e0fa1525aa56a4d3782ef4e91ba06e509a49ce15803e3d1732e1d1d`.
[CCP Entosis Link](https://support.eveonline.com/hc/en-us/articles/203729281-Entosis-Link) ·
[UniWiki Entosis Link](https://wiki.eveuniversity.org/Entosis_Link)

### Automated validation

`LostDefenderReplenishesAllianceFromRegionalFuelAcrossRestart` destroys a defender during its first paid cycle on
the newer of two held claims. The regional planner declines the trip with only dues in the treasury. After funding,
the backup requests four units, buys the three units a player offers, and pays the native broker fee. The world
reopens with the paid cargo in flight. The backup delivers it, withdraws the required fuel, restores the contested hub
with its original ship, and returns to headquarters. Both claims survive, the treasury retains dues, and the seller
fills a single order with zero human pilots selected during the replay.

The exact `CapsuleerAllianceTests` methods were `LostDefenderReplenishesAllianceFromRegionalFuelAcrossRestart`,
`UnattendedDefendersProtectLaterClaimAndDivideCampaignNodesAcrossRestart`, `FundedRivalsContestAnOwnedHubUnattendedAcrossRestart`,
and `FundedAllianceStagesOneHubAcrossRestartForAnUnobservedClaimableSystem`. All eight cases passed on the final
revision in 40.76 seconds. The isolated solution build took 15.54 seconds with zero warnings or errors.

The final focused filter passed 389 cases in 246.08 seconds. It selected the `Sovereignty` and `Alliances` test
namespaces plus `CapsuleerAllianceTests`, `CapsuleerAmmunitionSupplyTests`, `PersonalMarketTests`, `PilotCombatTests`
and `CapsuleCombatTests`. The two Python policy suites passed 17 cases. Formatting and whitespace checks passed
with the local recovery archive excluded from CSharpier.

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer checks

None.

## Bot attacks on rival sovereignty

With **Regional conflicts** and **Start new sovereignty claims** enabled, a bot executor that already holds territory
can send an available Entosis pilot against a reachable rival claim within three jumps of headquarters. An active war
or negative alliance standing qualifies a rival. With neutral or unset standing, ordinary gate adjacency between the
home claim and a foreign bot alliance's claim creates a border rivalry. Positive standing prevents that fallback.
The executor retains a monthly dues reserve and avoids routes through systems the pilot has recently marked dangerous.
The pilot uses its fitted link and fuel or buys missing supplies through normal market orders.

The planner prefers reachable command nodes in an active rival campaign. The executor sends one pilot to an owned hub
and can send available pilots to distinct command nodes once the campaign starts. Attacking an undamaged hub requires
an open vulnerability window with time for travel and warmup. Attackers carry enough fuel for the current score and
activity defense multiplier. The home claim and the expected rival alliance, hub and destination survive checkpoint
reopen. The alliance counts saved expeditions before its pilots resume. The pilot revalidates the objective before
engaging. Paid cycles and scoring leases follow the same rules as first claims and defense. Home response can dispatch
another available pilot while an attacker is away.

Paid attacker and defender links can block each other's progress. Native reinforcement sets the campaign deadline,
and native command-node completion updates the campaign score. An attacker victory destroys the rival hub and opens
the system for a new claim. The rules follow the existing build 3662168 warfare implementation and the EVE.js
261108.1 `entosisLinkRuntime.js` reference recorded in [first-claim procurement](alliances.md#first-claim-hub-procurement).
[CCP Entosis Link](https://support.eveonline.com/hc/en-us/articles/203729281-Entosis-Link) ·
[UniWiki sovereignty warfare](https://wiki.eveuniversity.org/Sovereignty_Hub#Capturing_existing_structures)

### Automated validation

The exact `FullyQualifiedName=Eden.Protocol.Tests.Capsuleers.CapsuleerAllianceTests.FundedRivalsContestAnOwnedHubUnattendedAcrossRestart`
filter passed both cases in 18.98 seconds after a 6.74-second build. With zero selected players, a two-corporation alliance
attacks a three-corporation defender. The fixture checks the dues reserve and friendly standing before departure, then
reopens an outbound assignment. One replay uses hostile standing and observes both paid links block the hub score.
The neutral-border replay follows native reinforcement and reopens the command-node campaign at its saved deadline.
At a defense multiplier of three, the available pilots pay for overlapping cycles on different nodes, destroy the rival
hub and remove its claim. The original home claim stays held throughout the campaign.

The broader `FullyQualifiedName~` selection passed 379 cases in 169.42 seconds. It combined `CapsuleerAllianceTests`,
`CapsuleerCorporationTests`, `Eden.Protocol.Tests.Alliances`, `Eden.Protocol.Tests.Sovereignty`, `CapsuleerMaintenanceTests`,
`CapsuleerPopulationSettingsTests`, `RegionalConflictTests`, `RegionalConflictWorldTests`,
`CapsuleerTrafficNavigationTests` and `TargetingTests`. The final solution build took 0.87 seconds with zero warnings or
errors. Both Python policy suites passed all 17 cases in 2.05 seconds. Formatting and whitespace checks passed with the
local recovery archive excluded from CSharpier.

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer checks

None.

## Bot claims after a campaign victory

When attackers capture a command node, an alliance earns credit if a valid bot link contributes at the completion
boundary. Native attacker victory settles that credit alongside the destruction of the rival hub. A successful defense
or the loss of the hub outside the campaign discards its pending credits. Any subsequent claim clears the system's
credits. Saved-world schema 242 checkpoints these records by destroyed hub and contributing alliance.

With **Start new sovereignty claims** enabled, a bot alliance that already has a home can pursue a credited, unoccupied
system within three jumps of its headquarters. It uses the same corporate bid, brokerage and monthly dues reserve as
a first claim. The executor can load an owned hub and the normal Squall route and fuel checks apply. The assignment
revalidates the home claim, credit and destination before deploying the new hub.

The replacement starts neutral and can be contested through ordinary paid Entosis. Victory credit guides bot planning.
The first alliance to finish the neutral capture receives the claim through the existing build 3662168 settlement.
The matching EVE.js 261108.1 neutral hub behavior is recorded in the [procurement reference](alliances.md#first-claim-hub-procurement).
[CCP command-node events](https://support.eveonline.com/hc/en-us/articles/203728981-Command-Nodes-Event) ·
[UniWiki sovereignty warfare](https://wiki.eveuniversity.org/Sovereignty_Hub#After_the_event)

### Automated validation

Four exact `FullyQualifiedName=` filters passed six cases in 41.33 seconds. The build took 1.78 seconds.

- `Eden.Protocol.Tests.Capsuleers.CapsuleerAllianceTests.FundedRivalsContestAnOwnedHubUnattendedAcrossRestart`
- `Eden.Protocol.Tests.Persistence.WorldMigrationTests.Version241UpgradeAddsCampaignCreditBeforeResidentStartup`
- `Eden.Protocol.Tests.Sovereignty.SovereigntyCampaignTests.PaidAttackReinforcesAndNineAttackerNodesDestroyWithoutTransferringClaim`
- `Eden.Protocol.Tests.Sovereignty.SovereigntyCampaignTests.UnattendedCampaignDefendsAndKeepsOriginalClaimUntilNextDaysWindow`

The victorious bot replay uses an offline market seller and keeps funds available for a duplicate bid. It verifies a
single corporate order, escrow and fill, infrastructure cargo bound for the defeated system, neutral deployment,
three paid cycles, return to headquarters, and credit removal. The first empty system in the generic route search
remains unclaimed. Both holdings survive checkpoint reopen and the next native dues payment. The returning raiders'
cycles expire after their target nodes disappear. The other cases check an older save, human-paid victory and pending
credit cleanup on defense.

The broader `FullyQualifiedName~` selection passed 366 cases in 200.13 seconds. It combined `CapsuleerAllianceTests`,
`CapsuleerCorporationTests`, `CapsuleerMaintenanceTests`, `CapsuleerTrafficNavigationTests`, `Eden.Protocol.Tests.Alliances`,
`Eden.Protocol.Tests.Sovereignty`, `PersonalMarketTests`, `WorldInitializationTests` and `WorldMigrationTests`.
The solution built in 11.78 seconds with zero warnings and errors. All 17 Python cases passed in 1.98 seconds.
Formatting and whitespace checks passed with the local recovery archive excluded from CSharpier.

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer checks

None.

## Scope and implementation plan

Target the Sovereignty Hub system supported by Windows build **3662168**, using resident authority and checkpoint
persistence. A deployed neutral hub and a completed alliance claim are separate states. Deployment alone must not grant
territory. Current gameplay references are [UniWiki sovereignty](https://wiki.eveuniversity.org/Sovereignty) and
[Entosis Links](https://wiki.eveuniversity.org/Entosis_Link), checked against the pinned client and catalog at each milestone.

| Milestone | Implementation and completion criteria |
| --- | --- |
| 1. Foundation | Typed claims and system eligibility, strategic-age and ADM rules, resident indexed reads, migration and coherent checkpoint/reopen coverage. Implemented below. |
| 2. Deployment and initial capture | Inventory-backed deployment around the star; corporation/alliance authority; one neutral hub per eligible system; Entosis warm-up, paid cycles, range and ship restrictions; contested progress; atomic completed claims and native sovereignty/map views. Implemented below. |
| 3. Territorial conflict | Vulnerability windows and delayed changes, reinforcement deadlines, constellation command-node campaigns, defense/regeneration and deterministic ownership-loss settlement; restart-safe progress and native notifications. Implemented below. |
| 4. Activity and capitals | NPC-kill and mined-volume contributions, activity decay, strategic age in published views, capital selection/delays and single-system behavior; ADM connected to capture timers. Implemented below. |
| 5. Resources and upgrades | Catalog-backed upgrades, stellar power, Skyhook resources, workforce routing, reagents/fuel, priorities, offline/low-power transitions and site-generation effects. Implemented below. |
| 6. Infrastructure and cleanup | Ansiblex, beacons, jammers and stability-generator ship bonuses gated by active upgrades; access and map consumers; destruction, self-destruct, alliance departure/dissolution and Skyhook ownership transitions. |

Each milestone needs focused automated rules, resident transactions, checkpoint/reopen and native-boundary tests where
applicable, followed by the repository's root validation and a remotely verified commit before the next milestone starts.
The full .NET suite is reserved for an explicitly requested regression check. Client testing is disabled for agents.

Design constraints and unresolved evidence:

- Existing `PilotStructureStore` accepts the hub hull through the generic Upwell lifecycle. That historical foundation
  does not establish a sovereignty claim. Milestone 2 must replace its hub-specific lifecycle and define recovery of any
  previously saved generic hubs without automatically granting sovereignty or duplicating inventory.
- Initial capture must settle inventory, ownership, claim time and notifications under the existing resident owner.
  No administrator shortcut or instant-claim service is part of this plan. Failed/duplicate callbacks must not grant a claim.
- Entosis module restrictions, cycle timing, capture targets and notification fields need fresh pinned-client/catalog
  inspection before implementation. Existing `sovMgr`, `devIndexManager` and `stationSvc` read stubs remain unchanged in
  milestone 1; no player-facing sovereignty action is enabled by the foundation.
- Public sources disagree with the older reference engine on vulnerability-change delays (UniWiki says 96 hours;
  evejs currently defines 48). Resolve this against build-specific evidence in milestone 3.
- Military/industry accumulation and decay need documented inputs and calibration. Do not present an invented formula
  as CCP behavior. Strategic time and ADM arithmetic below have explicit source evidence.
- Upgrades and resource requirements must come from the pinned catalog. Current public upgrade documentation contains
  historical restrictions; later sources must not silently override the build's behavior.

## References and pinned evidence

[CCP Sovereignty Hub](https://support.eveonline.com/hc/en-us/articles/14339751569436-Sovereignty-Hub) describes neutral SCC
ownership followed by Entosis capture. [CCP ADM](https://support.eveonline.com/hc/en-us/articles/203354271-Activity-Defense-Multiplier)
defines additive military, industrial and strategic bonuses, base 1, maximum 6 and the +2 capital bonus.
[CCP's strategic thresholds](https://www.eveonline.com/news/view/upgrading-and-upkeep-of-sovereign-solar-systems-in-dominion)
are historical evidence independently confirmed in the pinned client's `eve/common/script/sys/devIndexUtil.pyj`:
levels 1–5 require 7, 21, 35, 65 and 100 completed days. `sovSvc.GetIndexLevel` uses inclusive completed thresholds.
ADM bonuses for levels 0–5 are military/industrial `0, .6, 1.2, 1.7, 2.1, 2.5` and strategic `0, .4, .6, .8, .9, 1`.

Static bytecode inspection on November 16, 2026 used `E:\EdenDev\3662168\tq\code.ccp`, SHA-256
`238ef5811fcecd77db8ecdb8f143f0e74bbd337d225a2582ed25132def900d8c`. This is the prepared build-3662168 archive;
`E:\Eve` is a newer build and was not used as contract authority. No client was launched.
The foundation changes no Blue codec, transport or Destiny behavior, so it requires no new Carbon implementation.

The local `F:\Git Repos\evejs` reference has no Git metadata and declares AGPL-3.0-only. It was consulted for behavioral
comparison, without copying implementation. Source snapshot SHA-256 values:

| Reference | SHA-256 |
| --- | --- |
| `server/src/services/sovereignty/sovSystemRules.js` | `4d365adbf3800d57c4a0425ffe45dbe1aaa380e033a9b6b6f91be660290e97bd` |
| `server/src/services/sovereignty/sovConstants.js` | `d28392969e362444ebdc4b44e2ba8e8a13baba6784063ffd3f801df9ceb39215` |

The actual build-3662168 catalog exposed an important difference from a system-only faction check: C-7SBM has no faction
at system, constellation or region; Venal and Syndicate inherit NPC faction ownership; Serpentis Prime inherits it from
its constellation even though its region has no faction. Resolve system → constellation → region before classifying
claimability. The catalog reader requires complete, consistent ancestry and security data; absent records cannot grant
eligibility. It also preserves catalog anchoring prohibitions for sovereignty category 40 and hub group 1012.

## Foundation — November 16, 2026

Schema **179** adds `WorldSovereigntyClaim`, keyed by system, with a unique hub identity, owning corporation/alliance and
the original claim timestamp. Foreign keys describe saved identity dependencies. Immutable resident snapshots and an
alliance index support shared reads; private transaction copies provide rollback and immediate committed visibility.
Checkpoint capture retains its generation while the resident world advances. Projection, hydration and deletion use
the existing detached checkpoint machinery. Gameplay reads require the resident world and have no offline SQL fallback.
Only schema migration, initial hydration and checkpoint projection use SQL for this feature.

`ISovereignty` exposes typed claim reads and catalog system inputs for later operations. It intentionally has no public
claim mutation before deployment and Entosis exist. Tests seed records through the internal resident transaction API.
No existing hub is converted to a claim. Native sovereignty replies continue to describe the existing unclaimed world.

Typed eligibility excludes empire/NPC-owned space, positive-security space, wormholes and special space, the three
Jovian regions, Pochven, Zarzakh and catalog anchor restrictions. Strategic age never advances from a backwards clock;
it uses the persisted original timestamp and exact day boundaries. ADM combines validated index levels using decimal
arithmetic and caps the capital-adjusted result at 6. Activity collection, capital selection and capture consumers are
later milestones; these pure rules do not create activity or grant capital status.

**Automated validation.** Development exact filter
`FullyQualifiedName=Eden.Protocol.Tests.Sovereignty.SovereigntyFoundationTests.ResidentClaimsRollbackCaptureCoherentlyAndSurviveReopen`
passed **1 test**, with build **33.73 s** and tests **9.48 s**. The initial feature filter
`FullyQualifiedName~Eden.Protocol.Tests.Sovereignty` passed **49 tests** before the inherited-NPC-faction correction.
The corrected catalog filter passed **7 tests** (build **12.52 s**, tests **9.11 s**).

Final root checks passed in **59.81 s**: tool/solution restore, solution build with zero warnings/errors, **77 focused
tests** (including **52 new sovereignty cases**), both Python policy suites (**8 + 9 tests**) and formatting/width
verification (**2,435 files**). The final filter was
`FullyQualifiedName~Eden.Protocol.Tests.Sovereignty|FullyQualifiedName~Eden.Protocol.Tests.Maps|FullyQualifiedName~AllianceFoundationTests|FullyQualifiedName~DetachedCheckpointTests|FullyQualifiedName~IncrementalCheckpointTests`.
Coverage includes claim rollback, immediate shared reads, retained immutable snapshots, detached capture during later
resident changes, update/deletion across reopen, migration from version 178, inherited NPC factions, catalog anchor
restrictions, special-space rejection, exact age thresholds and invalid/capped ADM inputs. Warm claim, directory and
catalog reads assert zero SQL commands/connections; claim reads also assert zero fitting calculations.

The first dependency run passed **75/77** and stopped before policies/formatting: the existing native map-information and
sovereignty-panel packet fixtures created offline worlds, and a session tick reached resident-only Abyssal reads.
Those two fixtures now create resident worlds; their exact-method pair passed **2 tests** (build **6.51 s**, tests
**9.74 s**) before the successful final run above. No production Abyssal/session behavior was changed. UTF-8, final
newlines, added local documentation links and Git whitespace checks passed. The full .NET suite was not requested or run.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** None; this milestone enables no new client actions.

## Neutral hub deployment and Entosis capture — November 16, 2026

Schema **180** adds resident hub capture state and paid Entosis cycles. The native Deploy action for sovereignty hub
type **32458** uses `ship.LaunchDrones([(itemID, 1)], characterID, False)`, through the existing launch RPC owner.
It accepts one packaged hub from the active ship's cargo, fleet hangar or infrastructure hold. A player corporation
in an active alliance and effective Station Manager/Director/CEO authority are required. The transaction rejects
empire, NPC and special space, prohibited anchoring, occupied systems and conflicting geometry before consuming a hull.
The catalog's deployment component places the hub **2,000,000,000 m from the star**, along the deploying ship's radial
direction. The new object belongs to the **SCC (1000132)**; it grants no claim at deployment.

`structureDeployment.Anchor` no longer gives sovereignty hubs the generic Upwell timer. Previously saved generic hubs
without completed claims become **Unanchored**, retaining their identities, owning corporations, positions and contents.
Their managers may approach and recover the packaged hull through the existing capacity/access-checked recovery path.
This includes duplicate or ineligible legacy deployments. Migration grants neither a claim nor an inventory refund;
recovering an object removes that identity and cannot grant a second hull. Nearby legacy objects can obstruct new
placement until recovered. Existing historical structure notes describe the old foundation behavior.

### Entosis cycles and ownership settlement

`dogmaIM.Activate(moduleID, 'entosisLink', hubID, repeat)` requires an online fitted link, required skills, an alliance,
a locked neutral hub in the same system and catalog edge-to-edge range. The first cycle is warm-up. Each admitted
cycle atomically spends fitted capacitor and the catalog fuel requirement from ordinary cargo, normally one Strontium
Clathrate. Missing fuel/capacitor, bad ownership, missing locks, range and other rejection paths leave the transaction
unchanged. Modified links use their own catalog ranges, costs and limits; the unpublished QA link is unsupported.
The capital duration multiplier comes from the hull's fitted attributes and applies to warm-up and later cycles.
Interceptors cannot activate Entosis.

Deactivate requests stop repetition at the paid deadline. Warp, docking, gates/wormholes, translocation, cloak and
micro-jump activation cannot bypass the outstanding reservation. The restriction remains after disconnect/restart
until the saved end time. Existing boarding/ejection paths require docking. Capital ships additionally reject remote
repair, capacitor transfer and electronic assistance; starting Entosis removes existing remote electronic assistance.
The speed cap is a separate **online passive effect**, read from catalog attribute 2033: ordinary links impose
4,000 m/s and the Noir. variant 4,200 m/s. Finishing a cycle releases its travel lock while the online speed cap remains.

The hub's catalog capture component supplies **595 seconds** of uncontested scoring time. Coalition identity is the
alliance: multiple links from one alliance do not increase capture speed, and multiple warmed alliances pause it.
A lone opposing alliance first removes existing progress before building its own. Warm-up, paid-end and completion
boundaries use exact saved timestamps; delayed callbacks receive no unpaid time. Late repeats start and pay at the
actual callback time, without retroactive cycles. Completion chooses a still-valid contributing corporation from the
winning alliance and commits its claim, original completion time and structure ownership together. Duplicate callbacks
cannot create another claim or resume repetition against the owned hub. The last paid reservation still runs to its end.

Locks, fitting, range, current ship, destruction and current corporation/alliance eligibility are rechecked against
resident state. Losing eligibility or disconnecting stops scoring, preserving the earned progress and paid reservation.
Scoring leases and target locks are process-local; checkpoints retain progress and paid deadlines, so reopening cannot
silently capture territory while nobody is connected. A restarted link needs another paid warm-up. Coherent detached
capture includes hub state, cycles and fuel from the same generation while later resident cycles continue.

### Native replies and source evidence

The existing selected-pilot `sovMgr` methods now return the deployed hub list and, after capture, native
`sovereignty.data_types.SovClaimInfo` / `SovHubInfo` data-object constructors. Absent claims still return Python None;
military/industrial indices remain zero. `stationSvc.GetAllianceSystems` publishes current claim map rows.
Local ownership changes emit `OnSolarSystemSovStructuresUpdated` and `ProcessSovStatusChanged` cache notifications.
Slim items publish corporation/alliance ownership and the four-part `entosis_score` value, whose scoring attributes
are `(score, updateTime, rate, blocked)`. The neutral defender is build-3662168 `inventorycommon.const.ownerSystem`
(**1**), matching `entosisController.NEUTRAL`. The native `effects.EntosisBeam`, module activation/deactivation timers,
capacitor and item changes use existing named notification and Destiny models. Hub contacts are targetable and no
longer get duplicated by the generic structure refresh's former category-65-only filtering.

The inert Blue reader allowlists just the two known sovereignty constructors with validated arities and scalar
fields. It executes no incoming class constructors. The existing REDUCE writer and slim adapters were checked against
[Carbon Blue Marshal.cpp, af0d65b29f951260ea09140ca11adf5d40566997](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/Marshal.cpp).
No new Blue tags, transport behavior or Destiny physics engine are introduced.

Gameplay references: [UniWiki Entosis Link](https://wiki.eveuniversity.org/Entosis_Link),
[CCP Entosis Link](https://support.eveonline.com/hc/en-us/articles/203729281-Entosis-Link) and the hub references above.
Static inspection used the same prepared build-3662168 archive recorded above, including `entosis/spacecomponents/client/`,
`entosis/ui/entosisController.pyj`, `spacecomponents/client/components/deploy.pyj`, `sovereignty/data_types.pyj` contracts
consumed by `sovSvc`, and the native `client/spacecomponentsbytype`, type/dogma and map catalog data.
The older reference engine's `sovHubPlacement.js` uses a local sun-warp-grid policy; the pinned deployment component's
fixed star distance is authoritative here. No AGPL implementation was copied. Additional source snapshot SHA-256 values:

| Reference under `server/src/services/sovereignty/` | SHA-256 |
| --- | --- |
| `sovPlayerDeployment.js` | `3cb388d724c2bac03e5151298a325c5f494ba136fa0e7ac1cf4894c1e57cc2c1` |
| `sovCaptureState.js` | `d3740d18dfce64001f257f6aded200f4b1f9ac46da92efa8c400716b9d4f64a3` |
| `sovPayloads.js` | `998c373de30eed294d6e1e8f31581d0af563c2930fc82184ff91cf049dd34c4b` |
| `sovHubPlacement.js` | `433bea4e3fff2d6dd21183abbd29135a6744cc3f08c584a803a035124917f511` |

`tools/Export-SovereigntyCatalog.py` reproduces the inert test definitions from the local read-only catalog. The fixture
contains all six published link variants, the unpublished QA variant, hub/fuel, relevant hulls and prerequisites, plus
the C-7SBM star and ancestry. No game installation, resource cache or saved player world is embedded.

Owned-hub attacks, vulnerability/reinforcement, command nodes, passive defensive regeneration, alliance capitals,
activity accumulation, upgrades/resources and ownership-loss cleanup remain the subsequent milestones. In particular,
owned hubs cannot yet be attacked through Entosis; native replies have no campaign or vulnerability interval.
The existing zero activity-index baseline and base defensive multiplier remain until those consumers are implemented.

Capture contributions use a time-ordered event sweep with alliance counts; fleet size does not stack capture speed.
Expired paid rows are retired after accounting so settled hubs do not accumulate repeated-cycle history. Capital
assistance rejection also runs at the shared admission boundary used by repair drones.

**Automated validation.** In development, exact capture/repeat completion passed **1 test** (build **7.28 s**, test command
**8.30 s**). A subsequent feature run passed **74 tests** before the final native, legacy-recovery and passive-limit
changes. The native deployment/targeting/effect exact method also passed after moving the fixture outside the hub's
collision sphere. Root validation passed in **425.72 s**: tool/solution restore, solution build with zero warnings/errors,
**752 focused tests** (including **85 sovereignty cases**, **33 new** in this milestone), both Python policy suites
(**8 + 9 tests**) and formatting/width verification (**2,454 files**). Coverage includes native deployment/targeting,
inventory rejection and concurrent deployment, six published link variants, pause/reversal, exact capture deadlines,
paid travel/assistance restrictions, disconnect/restart, coherent detached checkpoints and legacy-hub recovery.
Warmed resident capture/observer reads assert zero SQL commands/connections and fitting calculations.

The root test binary preceded the final neutral-defender scalar correction. Its exact regression filter
`FullyQualifiedName=Eden.Protocol.Tests.Sovereignty.SovereigntyProtocolTests.ClaimedHubRepliesMapRowsSlimScoreAndNotificationsUseNativeShapes`
then passed **1 test** (build **6.51 s**, tests **8.42 s**); the final
`FullyQualifiedName~SovereigntyProtocolTests|FullyQualifiedName~BlueMarshalTests` filter passed **61 tests**. These
post-correction checks completed in **25.74 s** including compilation. Root formatting/policy checks covered the final
code; subsequent changes were validation documentation only. UTF-8, final newlines, local documentation links and Git
whitespace checks passed. No live client was used.

The exact development filter
`FullyQualifiedName=Eden.Protocol.Tests.Sovereignty.EntosisRulesTests.DenseFleetIntervalsDoNotStackAndAllocationGrowthIsBounded`
passed **1 test** (build **13.44 s**, tests **5.16 s**), covering 4,096 links, a contested interval, no allied speed stacking
and bounded allocation growth when doubling the contribution count.

The initial dependency run passed **764/771** in **470.32 s** of test execution; all seven failures reproduced at the
unchanged starting commit `3f1d4968505ab2fe73044ed5cff23a5966fae0c6` in a separate detached worktree:
`RemoteAssistanceTests.FutureLogisticsHullSelectorsHaveKnownConsumersWithoutEnablingTheirHullCohort` failed both
11978/11989 cases because those hull cohorts are already enabled, and
`CapacitorWarfareTests.AdmittedEnergyHullBonusesConsumeTheirEarnedModifiersOnce` failed five of eighteen cases
(47269, 47270, 47271, 49710, 49711) because its bonus probes omit required skills. The final dependency filter excludes
both unrelated methods (**20 cases**); their production behavior and fixtures are unchanged. The temporary worktree was
removed after retaining its reports. A mistakenly overlapping development build hit Windows test-host file locks and
produced no test results; subsequent builds were serialized. The full .NET suite was not requested or run.

The final focused dependency filter was:

```text
(FullyQualifiedName~Eden.Protocol.Tests.Sovereignty|FullyQualifiedName~Eden.Protocol.Tests.Maps|FullyQualifiedName~Eden.Protocol.Tests.Structures.StructureTests|FullyQualifiedName~TargetingTests|FullyQualifiedName~BlueMarshalTests|FullyQualifiedName~ModuleFittingTests|FullyQualifiedName~MovementPersistenceTests|FullyQualifiedName~WarpCapacitorTests|FullyQualifiedName~GateTravelRulesTests|FullyQualifiedName~GateActivityProtocolTests|FullyQualifiedName~SpatialModule|FullyQualifiedName~Eden.Protocol.Tests.Combat.RemoteRepairTests|FullyQualifiedName~Eden.Protocol.Tests.Combat.RemoteAssistanceTests|FullyQualifiedName~Eden.Protocol.Tests.Combat.CapacitorWarfareTests|FullyQualifiedName~ResidentTimedModuleTests|FullyQualifiedName~DetachedCheckpointTests|FullyQualifiedName~IncrementalCheckpointTests|FullyQualifiedName~CombatDroneTests|FullyQualifiedName~DroneSynchronizationTests|FullyQualifiedName~HicInterdictionTests|FullyQualifiedName~Eden.Protocol.Tests.Propulsion.InterdictionTests)&FullyQualifiedName!~FutureLogisticsHullSelectorsHaveKnownConsumersWithoutEnablingTheirHullCohort&FullyQualifiedName!~AdmittedEnergyHullBonusesConsumeTheirEarnedModifiersOnce
```

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Deploy and lock a neutral hub using native menus; observe Entosis warm-up/contested
progress and the beam; inspect the claimed system in the sovereignty panel and map. Rendering and input remain unverified.


## Sovereignty conflict and command nodes — November 16, 2026

Schema **181** adds resident alliance prime-time schedules, per-hub conflict state and command nodes. Existing version-180
claims, neutral hubs, inventory and paid cycles are retained. Initial capture still grants a corporation/alliance claim;
attacking an owned hub never transfers that claim directly. The claim and original claim time remain throughout
reinforcement and the node contest. Attacker victory destroys the physical hub and atomically removes its claim,
capture state, conflict and remaining campaign nodes. A replacement requires another packaged hub and neutral capture.
Defender victory retains the original claim and secures the hub until the following day's vulnerability window.

### Windows, paid attacks and durable deadlines

Alliances initially use **18:00 UTC**, an Eden default which executor-corporation directors/CEOs can change through
native `allianceRegistry.SetPrimeHour(hour)`. `GetPrimeTimeInfo` publishes `currentPrimeHour`, `newPrimeHour` and
`newPrimeHourValidAfter`. A requested change waits **48 hours**, with the old and new windows available during a
24-hour transition. Replacing a pending request restarts its delay; another request during the overlap is rejected.
Current conflict progress is settled before a schedule mutation. Existing reinforcement deadlines never reroll.

The pinned `entosis.entosisConst.CHANGE_PRIMETIME_DELAY` is **48 × HOUR**, also imported by the alliance sovereignty UI;
this takes precedence over the current UniWiki's 96-hour description. The historical
[CCP Aegis Sovereignty patch notes](https://www.eveonline.com/news/view/patch-notes-for-aegis-sovereignty) also specify 48 hours.
Daily windows are centered on prime time, including windows crossing UTC midnight. This milestone uses the existing
**base ADM of 1** and an **18-hour** window. Activity/capital collection and ADM-driven live timers remain milestone 4;
the pure window rule already accepts a validated multiplier from 1 to 6.

An owned hub accepts a paid Entosis warm-up only while vulnerable or already partly disrupted. Warm-up alone cannot
create overtime after a window closes. Partial disruption keeps the hub vulnerable until it is defended or reinforced.
All pilots outside the owning alliance, including unallied pilots, share the attacker side; links from separate attacker
alliances do not cancel one another. One side's additional links never accelerate capture. An active defender and an
active attacker pause progress together. Defender-only links restore control; otherwise the catalog regeneration rate
restores an unattended hub. Range, locks, membership, skills, hull eligibility, fuel, capacitor, travel reservations and
capital-assistance restrictions use the same resident admission and revalidation path as neutral capture.

The pinned hub component supplies **595 seconds** and regeneration **0.20000000298023224**. Full attacker disruption
saves a deterministic, once-selected timestamp within the vulnerability window two days later. At that timestamp the
hub enters its node campaign. Saved phase/deadlines prevent duplicate callbacks, restart rerolls and unpaid catch-up.
`SovereigntyMaintenance` advances unattended ownership deadlines through the resident owner; no gameplay SQL path was added.

### Constellation campaigns and settlement

A campaign begins at **60 defender points**. Initially **five** nodes are placed across the source constellation,
independent of sovereignty in the destination systems. A captured node changes the campaign by **seven points** and
is immediately replaced while the event continues. Thus six uncontested defender captures or nine uncontested attacker
captures win an otherwise unchanged event. Every node carries its own immutable source-system/hub/campaign identity;
simultaneous campaigns keep separate scores. Captures, starts and extra spawns advance across the constellation in
chronological order, with stable source-system/node identity ordering for equal timestamps. Shared capacity cannot be
reused at an earlier timestamp by advancing one campaign ahead of another.

The pinned unpublished server object **34832**, group **1316**, has a **235-second** capture component and regeneration
**0.03999999910593033**. New nodes start neutral, with signed progress toward the defender or attacker; unattended
nodes move toward the defender. Paid link expiries and warm-ups split progress at exact boundaries. Node identities,
positions, progress, campaign scores and upcoming spawn times persist through checkpoints. Reopening invalidates
process-local attacking leases and locks, retaining earned progress and paid travel deadlines. Passive defense and
scheduled events can advance across elapsed downtime; a disconnected attacker cannot win from an offline lease.
Detached checkpoint tests verify the node/conflict generation while later resident progress continues.

Eden uses reproducible placement, a fixed **30-minute** extra-spawn interval and saved deterministic selection seeds.
These are explicit server policies, rather than claims about CCP's random distribution. Positions are separated within
each system and remain stable across restart. Caps are **five nodes per system**, **twenty per constellation** and
**ten per campaign**; the campaign cap follows the
[Galatea patch notes](https://www.eveonline.com/news/view/patch-notes-for-galatea), while constellation/system caps follow
[CCP's Command Nodes Event](https://support.eveonline.com/hc/en-us/articles/203728981-Command-Nodes-Event).
The latter historical help page still describes five-point captures. Seven-point scoring follows the current
[UniWiki sovereignty reference](https://wiki.eveuniversity.org/Sovereignty#Command_node_control); the pinned client consumes
server-provided scores, and its catalog does not define the score delta. This choice is recorded as Eden gameplay policy.

### Native views and source evidence

The existing `sovMgr` structure rows now carry native `vulnerabilityState` pairs and four-part `campaignState` tuples.
`allianceRegistry.GetAllianceSovereigntyStructuresInfo` returns the three native collections (empty historical TCU rows,
hub rows and per-source/per-team scores), including campaign and vulnerability occupancy fields. Command nodes appear
as named, global, targetable space contacts, with `campaign_sourceInfo = (sourceSystemID, 2, hubID, defenderAllianceID)`
and the native four-part `entosis_score`. Owned-hub slim scores use the defender alliance instead of the neutral-owner
marker; reinforcement hides the active capture timer. The nominal attacked window remains published during overtime. Native Entosis activation publishes the same beam and paid cycle
notifications for nodes as for hubs.

`OnSolarSystemSovStructuresUpdated` and `ProcessSovStatusChanged` publish local changes and changes to the selected
pilot's alliance holdings, including when that pilot is outside the source system. A committed revision changes only
for relevant public state (phase, window, campaign score, claim identity or entering/leaving disruption); ordinary
progress ticks do not rebuild or resend the alliance panel. Unchanged polling emits no duplicate notifications.

Static inspection used the prepared build-3662168 archive and hash recorded above, including `entosis/entosisConst.pyj`,
`entosis/spacecomponents/client/entosisCommandNode.pyj`, `entosis/ui/entosisController.pyj`, `sovSvc.pyj`, alliance
`all_cso.pyj` and `all_ui_sovereignty.pyj`, plus the pinned hub/node space components and constellation geography.
One unrelated prepared UI module (`sovHubEntry.pyj`) could not be decoded by xdis; no conclusion depends on it.
The existing explicit Blue adapters and Carbon revision cited in milestone 2 remain the serialization boundary; no Blue
codec tags, transport behavior or Destiny simulation engine changed. No client was launched.

The local AGPL reference was consulted for behavioral comparison only; no implementation was copied. Relevant source
snapshot SHA-256 values:

| Reference | SHA-256 |
| --- | --- |
| `server/src/services/sovereignty/sovCampaignState.js` | `c04e2a5af2699b1a10454ea5b57306d60538830e9a5914740ad0b01ed500a634` |
| `server/src/services/corporation/allianceRegistryRuntime.js` | `073f759c9ea8d219d6d72f89829a6c30737aba63dd1be31b1cf87d5b6ee7a5e1` |
| `server/src/services/sovereignty/sovPayloads.js` | `998c373de30eed294d6e1e8f31581d0af563c2930fc82184ff91cf049dd34c4b` |

The reproducible fixture now includes node type 34832 and all six source-constellation systems. Live activity indices,
capital selection/bonuses, upgrades/resources, infrastructure and alliance departure/dissolution cleanup remain the
following milestones. The native module's personal bar, bracket graphics, input and travel feel remain unverified.

**Automated validation.** Final root checks passed in **129.68 s**: tool/solution restore, solution build with zero
warnings/errors, **298 focused tests** (including **108 sovereignty cases**, **23 new** in this milestone), both Python
policy suites (**8 + 9 tests**) and formatting/width verification (**2,470 files**). No cases were excluded. Coverage
includes prime authority/midnight/delay boundaries, paid warm-up and overtime, coalition pause, exact reinforcement,
nine attacker-node captures, passive defense, simultaneous-campaign isolation and caps, native rows/notifications/effects,
version-180 migration, detached checkpoint generations, attacker-victory/replacement reopen and restart scoring leases.
Warmed node/contact/prime reads assert zero SQL commands/connections and fitting calculations. A clean re-export matches
all **43 fixture types** and **190 inert source records**. UTF-8, final newlines, local documentation links and Git
whitespace checks passed. Validation uses simulated sessions and deterministic clocks; the full .NET suite was not
requested or run.

Development started with exact filter
`FullyQualifiedName=Eden.Protocol.Tests.Sovereignty.SovereigntyWarfareRulesTests.OwnedHubAttackersShareOneSideAndDefendersPauseWithoutStacking`,
which passed **1 test** (build **8.17 s**, tests **2.72 s**). The exact paid attacker-campaign filter
`FullyQualifiedName=Eden.Protocol.Tests.Sovereignty.SovereigntyCampaignTests.PaidAttackReinforcesAndNineAttackerNodesDestroyWithoutTransferringClaim`
passed **1 test** (build **4.71 s**, tests **6.50 s**) before the final reopen assertions were added. A **107-case** feature
run passed before the final callback/empty-system integration corrections.

The first dependency run passed **269/297** (test-run timestamps span **68.77 s**) and stopped before the Python/format
checks. Twenty-seven cascading failures exposed an eager node-profile lookup in empty systems; one assertion exposed a
nominal window being moved forward by a delayed overtime callback. Empty systems now return before reading node catalog
profiles, and overtime retains its original window. The exact correction filter covering the overtime method, native
alliance creation and attacker-win/replacement reopen passed **3 tests** (build **8.83 s**, tests **8.60 s**).
A subsequent **297-case** dependency run passed, then stopped at the catalog policy: the constellation definition still
used a direct catalog table read. It now uses the shared provider's cached geography without a policy exception.

The final exact late-callback/observer filter
`FullyQualifiedName=Eden.Protocol.Tests.Sovereignty.SovereigntyCampaignTests.UnattendedCampaignDefendsAndKeepsOriginalClaimUntilNextDaysWindow|FullyQualifiedName=Eden.Protocol.Tests.Sovereignty.SovereigntyWarfareBoundaryTests.NativeNodeSlimCarriesCampaignSourceAndWarmReadsUseNoSqlOrFits`
passed **3 tests** (build **11.26 s**, tests **9.77 s**), including 4-hour and 72-hour callbacks. The latter checks the
immediate resident window before a second read can refresh it. Earlier development checks also caught an unsupported raw
numeric dictionary in the native campaign reply (replaced by `BlueMap`) and a capacity-fixture assumption (corrected to
assert an actually spawned node's source). Both regressions passed before final root validation.

The focused dependency filter was:

```text
FullyQualifiedName~Eden.Protocol.Tests.Sovereignty|FullyQualifiedName~Eden.Protocol.Tests.Maps|FullyQualifiedName~Eden.Protocol.Tests.Structures.StructureTests|FullyQualifiedName~TargetingTests|FullyQualifiedName~BlueMarshalTests|FullyQualifiedName~DetachedCheckpointTests|FullyQualifiedName~IncrementalCheckpointTests|FullyQualifiedName~Eden.Protocol.Tests.Market.ResidentEconomyTests|FullyQualifiedName~InventoryPersistenceTests|FullyQualifiedName~AllianceCreationTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting
```

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Change alliance prime time in the native sovereignty UI; observe an owned hub's
reinforcement and campaign panel; warp to and capture a command node, checking its source name, bracket and beam.

## Sovereignty activity and capitals — November 16, 2026

### Activity inputs and decay

Military and industrial indices now grow from committed resident gameplay. A qualifying pirate ship destruction queues
one military input in the same transaction as fatal NPC settlement. Abyssal kills, pirate sentries/overseer sentries,
navy/CONCORD, convoys and NPC miners do not qualify. The existing destroyed guard prevents repeat rewards. Player and
mining-drone collection queue the actual ore/ice volume, including critical bonus, while excluding residue, gas and
mission deposits. NPC mining, PI, manufacturing and market transactions are not industrial inputs. Credit depends on the
system having a claim, irrespective of the contributing pilot's alliance. Reward/bounty multipliers do not multiply
military input; increased gathering contributes the volume actually collected.

[UniWiki sovereignty](https://wiki.eveuniversity.org/Sovereignty) does not establish an exact server earning formula.
[CCP's legacy upgrades reference](https://support.eveonline.com/hc/en-us/articles/210777969-Sovereignty-System-Upgrades-Legacy-mode)
describes NPC ships and mining volume as inputs, and
[CCP's 119.5 notes](https://www.eveonline.com/news/view/patch-notes-for-119.5-release) explicitly include mining drones.
Eden therefore uses an explicit calibration: **10,000 raw military points per qualifying ship** and **one raw industrial
point per cubic metre collected**. These raw earning amounts are Eden policy, not a reconstruction of CCP's hidden formula.

Static inspection of build-3662168 `eve/common/script/sys/devIndexUtil.pyj` supplies six activity bands of
**9,600,000 points**, maximum **57,600,000**, and **100,000 points/hour** decay. Levels 1–5 start at 9.6, 19.2, 28.8,
38.4 and 48 million points. Client military input modifiers at levels 0–5 are `5, 3, 2, 1.5, 1, .8`; industrial modifiers
are `7.2, 2.36, 1.649, 1.2, .7, .35`. Large contributions split at each band using that band's modifier and saturate at
the maximum. Both point balances decay continuously at exact clock resolution toward zero, including elapsed downtime.
The 96-hour-per-band client decay agrees with the historical direction of
[CCP's Dominion upkeep explanation](https://www.eveonline.com/news/view/dominion-upkeep-and-upgrades-the-expanded-edition).
Continuous settlement is Eden scheduling policy; it does not claim CCP's downtime implementation. Native `increasing`
means a contribution occurred within the preceding hour, another explicit Eden display policy. Strategic age uses the
original claim timestamp and the existing exact 7/21/35/65/100-day thresholds.

Schema **182** adds immutable resident activity, capital, capital-history and pending-input records plus conflict ADM/schedule fields.
The input queue checkpoints with its originating inventory/destruction changes. The sovereignty owner consumes inputs
by timestamp and identity after settling earlier capture progress; rollback discards the input, and consumed inputs
cannot replay after restart. Scalar decay, indices, trends, ownership and saved deadlines share the resident owner.
Detached checkpoint images retain their own generation as later resident inputs settle. New gameplay/service paths
require the resident world; SQL remains limited to migration, hydration and checkpoint projection.

### Capitals and capture scaling

The executor corporation's CEO/directors can select an alliance-owned system as capital or cancel a pending transition.
A change takes **seven days**, retains the current capital until its deadline and is invalidated if the destination is
lost. An alliance holding exactly one system automatically receives that system's capital bonus immediately. Losing
current ownership removes that capital; a sole remaining holding becomes capital. Pending/current identities and the
transition deadline persist through restart. These choices follow
[CCP's summer sovereignty report](https://www.eveonline.com/news/view/summer-2015-nullsec-and-sov-status-report) and the
[single-system deployment clarification](https://www.eveonline.com/es/news/view/summer-of-sov-transition-and-deployment).
[Alliance dissolution cleanup](#sovereignty-cleanup-on-alliance-dissolution) clears both capitals as it removes the closing alliance's claims.
Effective capital changes retain their timestamps so an inactive constellation settling through multiple transitions
uses the bonus that applied during each interval, including across checkpoint/reopen.

The live multiplier is **base 1 + military + industrial + strategic + 2 for capital**, capped at **6** using the bonus
tables already recorded above. Owned-hub attackers now capture at `1 / ADM` of the base rate. Defender restoration,
passive regeneration, opposing-side pause, neutral hub capture and module warm-up remain at their existing rates.
Each index/capital boundary settles the previous multiplier before installing the next; a new input cannot rescale
already-earned progress. Paid-cycle boundaries and offline scoring-lease invalidation remain authoritative.

Daily vulnerability lengths are `18 hours / ADM`. The next day's length freezes at the current window's midpoint;
changes after that cutoff wait for the following lock. Window centers and both frozen multipliers persist, and skipped
constant-ADM days advance arithmetically. Prime-time changes preserve the frozen length through their handover and the
existing overlap rule. A started disruption retains its nominal window for overtime. These scheduling choices implement
the lock described by [CCP's ADM reference](https://support.eveonline.com/hc/en-us/articles/203354271-Activity-Defense-Multiplier).

Reinforcement snapshots the **source system's ADM** into its campaign. Both its saved campaign deadline and all command
nodes use this frozen multiplier, irrespective of later source activity/capital changes or the destination system's
indices. Attacker node capture is slowed by the frozen ADM; defender links and unattended regeneration keep base speed.
Existing version-181 campaigns retain their original multiplier of 1, deadline and earned progress when migrating.

### Native views and source evidence

`GetDevelopmentIndicesForSystem` returns current points and trend flags; `GetAllDevelopmentIndices` returns named native
rows with military/industrial points and completed claim days. Structure, alliance-panel and local/alliance monitor
views carry live ADM, frozen window/campaign occupancy and capital status. `GetCapitalSystemInfo`, `SetCapitalSystem`
and `CancelCapitalSystemTransition` use the pinned allianceRegistry contracts. Capital commands queue
`OnUpdateCapitalInfo`; completed capital changes emit `OnCapitalSystemChanged` with one three-element change tuple.
`OnSolarSystemDevIndexChanged` invalidates native local index caches. Monitors publish index-band/trend changes even
when the ADM cap hides their arithmetic effect, and unchanged reads emit no repeat notifications.

Static xdis 6.3.0 inspection used the prepared archive/hash recorded above, including `devIndexUtil.pyj`, `sovSvc.pyj`,
`all_cso.pyj` and `infoPanelLocationInfo.pyj`. The latter explicitly unpacks a single capital-change tuple; sovSvc's
index-change callback accepts one system ID. The existing explicit Blue boundary/Carbon revision from milestone 2
continues to apply; no codec, transport or Destiny engine behavior changed. No client was launched.

The local AGPL reference was consulted only for behavior, with no implementation copied. It has no Git metadata;
relevant source snapshot SHA-256 values are:

| Reference | SHA-256 |
| --- | --- |
| `server/src/services/sovereignty/sovState.js` | `674bf18c62889b09d71b75f2add440feefa9620c39d665a52aa0f679d628df1f` |
| `server/src/services/sovereignty/sovNotifications.js` | `ed7df442bb6ffbfb75348f11337b4b00b2a35db7b6de61eb75a3290a12a445a` |
| `server/src/services/corporation/allianceRegistryRuntime.js` | `073f759c9ea8d219d6d72f89829a6c30737aba63dd1be31b1cf87d5b6ee7a5e1` |

The deterministic catalog fixture adds a supported pirate/wreck and ore/mining dependencies; it contains **49 types**
and **208 inert records**. Resources, upgrades and infrastructure remain later milestones. Native rendering and input
remain unverified.

**Automated validation.** Root checks passed in **207.36 s**: tool/solution restore, solution build with zero
warnings/errors, **572 focused tests** (**170.89 s**, including **127 sovereignty cases**, **19 new**), both Python
policy suites (**8 + 9 tests**) and formatting/width verification (**2,487 files**). No cases were excluded. The
catalog re-export matches all **49 types / 208 records**; unchanged pre-existing types/records retain identical content.
UTF-8, final newlines, changed guide file links and Git whitespace checks passed. The full .NET suite was not requested
or run. Validation uses deterministic clocks and simulated sessions; native rendering, input and gameplay feel remain
unverified.

Coverage includes band-split earning, saturation/rejections, subsecond decay, attacker-only ADM scaling, qualifying
fatal NPC settlement and rollback/deduplication, actual laser/drone ore yield and residue exclusion, capital authority,
ownership, cancellation and exact seven-day transition, locked daily windows, nonretroactive capture-rate changes,
frozen source campaign ADM across capital transition/destination/restart, version-181 migration, capital history and
detached checkpoint generations. Warm development/hub/capital reads assert zero SQL commands/connections and fitting
calculations. Immediate dependencies include mining/crystals/rewards/ledger, mining drones, NPC rewards, pilot combat,
missiles/drones, maps, structures, inventory, alliance creation, Blue serialization, hosting and checkpoint machinery.

Development began with exact filter
`FullyQualifiedName=Eden.Protocol.Tests.Sovereignty.SovereigntyDevelopmentRulesTests.CreditsSplitAtClientBandsAndDecayPreservesSubsecondBoundaries`,
which passed **1 test** (build **8.16 s**, tests **2.75 s**). The initial 121-case feature run passed **110/121**:
the campaign fixtures reserved only ten paid cycles, too short for a capital's new ADM 3. Reservations now cover the
scaled duration. The nine-node scoring-isolation test uses a noncapital source so unattended nodes cannot introduce
additional defender points during the longer campaign. The corrected 121-case run passed, followed by **127/127**
after integration and capital-history coverage. Other development failures caught incomplete pirate fixture wreck/loot
dependencies, a nested committed test transaction, and a native assertion expecting one changed holding after several
days; those fixture assumptions were corrected before root validation.

After root validation, the pending-input restart test was strengthened to retain a nonzero military balance after
one day's decay and to assert that the input is still pending immediately after reopening. Its exact filter
`FullyQualifiedName=Eden.Protocol.Tests.Sovereignty.SovereigntyDevelopmentTests.PendingCreditsCheckpointTogetherAndReopenDecaysWithoutReplayingInputs`
passed **1 test** (build **4.76 s**, tests **6.36 s**); formatting verification was repeated. Production code remained
unchanged after the 572-case run.

The root focused dependency filter was:

```text
FullyQualifiedName~Eden.Protocol.Tests.Sovereignty|FullyQualifiedName~Eden.Protocol.Tests.Maps|FullyQualifiedName~Eden.Protocol.Tests.Structures.StructureTests|FullyQualifiedName~TargetingTests|FullyQualifiedName~BlueMarshalTests|FullyQualifiedName~DetachedCheckpointTests|FullyQualifiedName~IncrementalCheckpointTests|FullyQualifiedName~Eden.Protocol.Tests.Market.ResidentEconomyTests|FullyQualifiedName~InventoryPersistenceTests|FullyQualifiedName~AllianceCreationTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~Eden.Protocol.Tests.Mining.MiningTests|FullyQualifiedName~MiningCrystalTests|FullyQualifiedName~MiningSkillPointRewardTests|FullyQualifiedName~PersonalMiningLedgerTests|FullyQualifiedName~MiningDroneLifecycleTests|FullyQualifiedName~PilotCombatTests|FullyQualifiedName~NpcSkillPointRewardTests|FullyQualifiedName~PilotMissileTests|FullyQualifiedName~PilotDroneTests
```

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Select/cancel an alliance capital in the native panel; observe activity/trends and ADM
in the sovereignty UI; compare an owned-hub timer with a command node after its source capital changes.

## Sovereignty Hub fuel access group lookup

The native Hub management controller reads the current fuel access group using
`sovMgr.GetSovHubFuelAccessGroup(solarSystemID)`. Eden resolves the system's current claimed hub and applies the
existing alliance read check before returning native None for the unset access group. Quasar fuel operations
continue to identify a hub by item ID. Access-list assignment and delegated fuel deposits remain gaps.

Build **3662168** `hubController.pyj` passes `_solarSystemID` during initialization. The same argument is
forwarded unchanged by `sovSvc.pyj`. REA extraction evidence
`ev_e37bbbaf074e032842a629ab1ac0b7213e4630d05f883fbf5c32eb1ff5ec28c0` contains both modules.
Local EVE.js 261108.1 `sovMgrService.js` confirms the system ID lookup boundary (SHA-256
`f1e68e6b7af1d5151c30c093e4f3e97e584ad18136023baba5803c79d9fc5daa`).

`SovereigntyProtocolTests.NativeHubFuelAccessGroupReadsBySolarSystemAndRetainsAllianceReadBoundary` passed
in the shared exact feature run (21.52 seconds to build, 22.90 seconds to test). It covers the native system
ID, outsiders, hub item IDs and unclaimed systems.

## Sovereignty resources and upgrades — November 16, 2026

Schema **183** adds seven saved resource tables with explicit immutable resident mappings: per-hub budgets, installed
upgrades, shared fuel ledgers, workforce configuration/sources, Skyhooks and ore-site provenance. Exploration sites gain
a saved sovereignty slot (existing sites remain slot zero). Initial hydration, migration and detached checkpoint
projection use SQL; resource commands, reads, timers and site effects require the resident world. Inventory debits,
configuration, startup fuel and resulting power states commit together. Failed commands preserve inventory and resources.

### Catalog resources and resource operations

Definitions use **44 published** upgrades from **49** pinned `sde/sovereigntyUpgrades` records. The three unpublished drone
arrays and both QA definitions are excluded. Requirements, production, exclusivity and startup/hourly reagent costs come
from this export, rather than historical prices in public articles. For example, Cynosural Navigation consumes power
250, workforce 1500, 62000 Magmatic Gas at startup and 205/hour. The native public definition reply retains all published
upgrade definitions; resource allocation alone does not implement each upgrade's downstream consumer (see below).

Despite its name, `sde/planetResources` contains stellar entries: **2712** of its **25798** records identify stars in
`sde/mapStars`. The shared directory now separates stars from planets and serves actual native star power configurations.
C-7SBM's star **40252918** supplies **900** power. Skyhooks harvest only their planet's exported power/workforce/reagent
profile; unavailable resources never acquire a fabricated fallback. Local power cannot be transferred.

An owning corporation's effective Station Manager/Director/CEO launches one packaged Orbital Skyhook **81080** through
the native Deploy action, including corporation-owned cargo. Deployment requires claimable nullsec and proximity to a
planet, rejects a second live hook and atomically consumes one hull. A claim or alliance is not required to deploy.
Previously saved generic Skyhooks remain decommissionable through their original lifecycle; migration does not turn
them into resource harvesters. Newly deployed resource Skyhooks reject generic Upwell management.
The component's catalog activation delay is **1800 seconds**. Only active, completed hooks belonging to the claim's
alliance contribute power/workforce to its hub. Hooks may harvest reagents without a system claim.

Placement is an explicit **Eden policy**: within 50 million metres of the planet's surface, the hook is placed five
million metres above it in the ship's radial direction. The component export names an orbital placement function but
provides no numeric altitude. Harvesting uses complete exported cycles, separate secured/surplus capacity limits and a
**50% secured** split, rounded down independently for each cycle; the surplus retains the other units. This integer
policy makes one late callback equivalent to repeated complete cycles. Deactivation pauses production and resets a
partial cycle. Capped stocks, cycle cursors, activation deadlines and partial withdrawals survive checkpoints.

Managers can extract positive quantities from secured stock to fitted ship cargo, subject to available capacity,
cloaking and edge-to-edge range (10 km). Surplus theft, theft vulnerability, Skyhook damage/ownership transfer and access
lists belong to the next milestone. The current slim advertises the pinned shield-vulnerable state **210** and native
activation fields; it does not imply that the theft/combat lifecycle is implemented.

Workforce supports inactive, import, export and transit configurations. A destination explicitly accepts at most three
source systems. Exports traverse alliance-owned stargates through transit-mode hubs and are limited to the source's
local harvested workforce. Disconnected exports remain available locally; generated workforce cannot be re-exported.
Topology changes settle old allocations before publishing both systems' new budgets. No network configuration creates
resources or consumes an imported quantity twice.

### Upgrade lifecycle and site effects

Installation consumes one real packaged upgrade from cargo, fleet hangar or infrastructure hold within hub range.
The request must configure every installed/new type exactly once. Duplicate identities/types, foreign managers,
unsupported items and installations exceeding potential system power fail atomically. Uninstall destroys the upgrade
without a refund. Reagent deposits debit real inventory into a bounded shared hub ledger.

Priority order determines allocation. Mutually exclusive groups admit one running type, and resource-producing upgrades
only help lower-priority entries. Native power states are Offline **1**, Online **2**, Low Power **3** and startup Pending
**4**. Resource/fuel shortages retain the configured intent; restoring inputs resumes allocation. Manual offlining clears
the startup-paid state. Fractional hourly consumption persists as an integer numerator in ticks/hour; shared-fuel
thresholds reallocate at exact deadlines, so delayed callbacks and repeated ticks produce identical ledgers. Late Skyhook
activation splits settlement at its original deadline. Claim-loss campaigns settle and destroy the old hub's resource
state at ownership loss; already earned finite sites remain until completion/depletion.

Active minor/major detection arrays add one to three supported combat slots by tier. Exploration detectors add one to
three data slots and relic slots; ghost sites and wormholes are unchanged. Prospecting arrays create one finite site
of their catalog ore family, eight rocks per tier with 10000 units per tier per rock, with ten-minute replacement after
depletion. Existing generated sites survive offlining; offline upgrades stop replacement. Slot identities and ore
provenance persist across reopening. State changes invalidate shared resident views immediately. Warm resource reads
assert zero SQL commands, connections and fitting calculations.

These rosters, quantities and placements are **Eden calibration**, not a reconstruction of CCP's hidden distribution
service. Exported distribution dogma identifiers are not dungeon identifiers and are never interpreted as such. Combat
slots select supported regional minor/major profiles; ore mappings are Veldspar, Mordunium, Kylixium, Griemeer, Nocxite,
Hezorime and Ueganite. The pinned catalog determines resource requirements and ore identities.

[Ansiblex, Pharolux and Tenebrex](capital-logistics.md) consume their corresponding active upgrades. Supercapital
construction upgrades and the four stability generators' ship bonuses remain downstream work. Skyhook theft,
access lists, alliance departure/dissolution and ownership transitions
also remain explicit milestone 6 boundaries. Native UI input, rendering and live relay authentication remain unverified.

### Native contracts and evidence

Static xdis **6.3.0** inspection used the prepared build archive/hash recorded above; protobuf descriptor bytes were read
inertly and projected into named schemas. Relevant packages are `eve_public.sovereignty.hub.upgrade.api`, `hub.fuel.api`,
`hub.workforce.api`, `hub.api` and `skyhook.api`. The implementation covers definition/upgrade/fuel/resource reads,
configuration, installation/uninstall, fuel deposit, workforce networks and Skyhook reads/activation. Native updates use
`HubUpgradesSimulatedNotice`, uninstall and resource-simulation notices, plus Skyhook activation, workforce and reagent
simulation notices. The notice envelope preserves native field tags.
The native component extraction and authorization methods use `beyonce.CallComponentFromClient`; corporation Skyhook
listings return named KeyVal rows. Existing explicit Blue conversion and the milestone-2 Carbon boundary remain in use;
no Blue codec or Destiny simulation implementation was changed. The pinned Skyhook notice listener reads
`ReagentSimulation.simulation` through `HarvestSplitSimulationState.create_from_proto`; this is the populated secured/
surplus snapshot, rather than the newer harvest/maturation fields.

Private gateway operations require an authenticated account whose selected character belongs to it, then apply resident
corporation/alliance authority. Client-supplied character context is not authentication. After game-login proof, the host
holds only a hash of the login ticket for a Bearer gateway lease lasting that connection; public streams retain no private
access. The real transport forwards Authorization metadata. A notice subscription rechecks the lease and publishes only
changed observed hubs. Committed gateway inventory debits enter the existing Macho item-notification queue. Automated
contract tests establish these boundaries; they do not establish that the native launcher/client forwards the expected
credential in a live session.

Gameplay references consulted: [UniWiki sovereignty](https://wiki.eveuniversity.org/Sovereignty),
[UniWiki Skyhook](https://wiki.eveuniversity.org/Skyhook),
[CCP Sovereignty Hub](https://support.eveonline.com/hc/en-us/articles/14339751569436-Sovereignty-Hub),
[CCP transition and upgrades](https://www.eveonline.com/news/view/sovereignty-updates-transition-and-upgrades) and the
[official system-resource workbook](https://web.ccpgamescdn.com/aws/eveonline/files/EquinoxSystemResourceData4.xlsx).
Historical public costs are superseded by the pinned export where they differ. The inert focused fixture contains
**119 types / 516 records**, including all published/unpublished upgrade records and constellation resource/stargate inputs.

The local AGPL reference was consulted for behavior only; no implementation was copied. Snapshot SHA-256 values:

| Reference under `server/src/services/sovereignty/` | SHA-256 |
| --- | --- |
| `sovHubFuelBurn.js` | `38ab746d5b50f61b1d9c31bc6aefc56d673c377ae0b66903b01ca68737ac77c7` |
| `sovHubUpgradeInstall.js` | `ab180fb5fd10f306f1bdc0929a8adc8909322981ca7aff8f70104d9d8ec1a8d0` |
| `sovUpgradeSupport.js` | `da97b6c2a110b36e8c22eac0eb1db9a31816b23bc4131b57150ac2827a20abdc` |
| `colonyResourcesMgrService.js` | `c4b253530480ca49126c6c7cada12ff933f42ebef4e7db4b18f637da96b917a3` |
| `sovStaticAuthority.js` | `9e3f1d7b801455f5bb5357289752359602a5be8a8045432056334c7b4a3e323d` |
| `sovStaticData.js` | `37af5ef18049e9ab12a986f6c68cc7e709d994ab814c3d6fe8ab2774a5170a47` |

**Automated validation.** Root checks passed in **400.93 s**: tool/solution restore, solution build with zero
warnings/errors, **721 focused tests** (**344.97 s**, including **148 sovereignty cases / 21 new**), both Python policy
suites (**8 + 9 tests**) and formatting/width verification (**2520 files**). No cases were excluded. The full .NET suite
was not requested or run. The final legacy-management compatibility adjustment and its affected classes are validated
separately below. UTF-8, final newlines, changed guide links and Git whitespace checks passed.

The last compatibility guard preserves decommissioning for generic Skyhooks saved before this milestone, while new
resource Skyhooks reject generic Upwell management. After that adjustment, root tool/solution restore, a zero-warning
solution build, **49 sovereignty-protocol and structure tests** (**44.12 s**), both Python policy suites (**8 + 9**) and
formatting verification (**2520 files**) passed in **108.33 s**. Its focused filter was
`FullyQualifiedName~SovereigntyResourceProtocolTests|FullyQualifiedName~Eden.Protocol.Tests.Structures.StructureTests`.
The 721-case dependency run preceded only this narrow management guard and the corresponding assertions; no other
production code changed afterward.

Development started with exact filter
`FullyQualifiedName=Eden.Protocol.Tests.Sovereignty.SovereigntyResourceRulesTests.PriorityProductionAndSharedFuelExhaustionSettleAtExactBoundaries`:
**1 passed**, build **12.92 s**, tests **4.13 s**. Coverage includes exact shared-fuel thresholds and repeated subsecond
callbacks; priority/production/exclusivity and disconnected workforce routes; actual inventory installation/deposit and
unchanged-state rejection; timed Skyhook harvests/caps, repeated activation and partial withdrawals; migration from
version 182, fuel/cycle/network/site checkpoints and reopening; physical native deployment, authenticated HTTP/2,
credential replacement, private notice deduplication and committed inventory notifications. The deterministic catalog
re-export matches **119 types / 516 records** and preserves every pre-existing fixture identity/value.

The first 160-case dependency run passed **159**, with one packet fixture expecting the former zero secured percentage;
the fixture now independently encodes the 50% field. The first root attempt was deliberately stopped after **188**
completed cases (**158 passed / 30 failed**) exposed EF translation of the new Skyhook deadline and exploration-slot
queries in existing nonresident fixtures. New projections now run only against resident state; no resource SQL fallback
was added. The targeted native/Agency/site rerun passed **25/25** (build **7.73 s**, tests **17.06 s**).

A second root attempt was stopped after **568** completed cases (**566 passed / 2 failed**). The generic hull theory
still expected Skyhook deployment through Upwell operations; it now verifies rejection and unchanged inventory, while
the native launch test covers successful dedicated deployment. The existing hacking history test also caught increased
allocations. A first predicate-only correction reduced allocations but still failed that budget (**29/30** passed).
Positive sovereignty slots now have a resident index, and systems without extra slots retain the original base query.
The unchanged history budgets and upgrade-generated site tests then passed **5/5** (build **9.53 s**, tests **18.25 s**).
No tests were disabled or excluded to accommodate these corrections. Final native Skyhook notices were added before
the final root run. Native rendering/input and live credential forwarding remain unverified.

The root focused dependency filter is:

```text
FullyQualifiedName~Eden.Protocol.Tests.Sovereignty|FullyQualifiedName~Eden.Protocol.Tests.Gateway|FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~Eden.Protocol.Tests.Exploration|FullyQualifiedName~Eden.Protocol.Tests.Maps|FullyQualifiedName~Eden.Protocol.Tests.Structures.StructureTests|FullyQualifiedName~Eden.Protocol.Tests.Mining.MiningTests|FullyQualifiedName~MiningDroneLifecycleTests|FullyQualifiedName~InventoryPersistenceTests|FullyQualifiedName~Eden.Protocol.Tests.Market.ResidentEconomyTests|FullyQualifiedName~DetachedCheckpointTests|FullyQualifiedName~IncrementalCheckpointTests|FullyQualifiedName~PilotDroneTests|FullyQualifiedName~PilotLoginTests
```

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Deploy/activate a Skyhook and withdraw secured reagent; configure/deposit fuel and change
upgrade priority in the hub UI; observe workforce connectivity and generated site markers across a power outage.

## Main integration — November 16, 2026

Combined sovereignty milestones 1–5 (`e6be0c5a`) with main `d8dc05db` in an isolated integration worktree. Conflict
resolution retains main's area-weapon handlers, structure controls, native service power state, moon mining points,
hull geometry and service-dependent contact invalidation alongside sovereignty capture/resource state and notices.
Schema version **183** retains both branches' idempotent schema reconciliation steps. Existing milestone evidence and
unimplemented infrastructure/cleanup boundaries above remain applicable.

**Automated validation.** The exact native corporation Skyhook deployment test passed **1/1** (build **9.98 s**, tests
**6.98 s**). Root tool/solution restore, a zero-warning/error solution build, **494 focused integration tests**
(**151.47 s**), both Python policy suites (**8 + 9 tests**) and formatting/width verification (**2564 files**)
passed in **189.44 s**. No cases were excluded; the full .NET suite was not requested or run.
Git whitespace, UTF-8/final newlines and local guide links were checked. The root filter was:

```text
FullyQualifiedName~Eden.Protocol.Tests.Sovereignty|FullyQualifiedName~Eden.Protocol.Tests.Structures|FullyQualifiedName~Eden.Protocol.Tests.Maps|FullyQualifiedName~Eden.Protocol.Tests.Gateway|FullyQualifiedName~AreaWeapon|FullyQualifiedName~StaticCollisionGeometryTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~DetachedCheckpointTests|FullyQualifiedName~IncrementalCheckpointTests|FullyQualifiedName~InventoryPersistenceTests|FullyQualifiedName~FittingTests
```

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** None additional for this merge; milestone-specific optional checks remain above.
