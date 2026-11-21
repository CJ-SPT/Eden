# Faction warfare

## Theaters of War shipcaster reads — November 21, 2026

The empire warzone panel calls `shipCasterLandingPadMgr.GetFactionLandingPads(factionID)` and sorts the result before
building the Interstellar Shipcaster section. It then checks faction membership in the result of
`shipCasterLauncherMgr.GetFactionsWithShipcaster()`. The warzone map also iterates
`shipCasterLandingPadMgr.GetAllLandingPads()` to place landing-pad markers.

Eden returns empty Python lists for these selected-pilot reads. The client uses its shipcaster-not-built view and
skips shipcaster and landing-pad markers. Building and using shipcasters requires a separate gameplay implementation.
The warzone map continues to use the existing occupation, headquarters, battlefield and visible-insurgency reads.

Build 3662168's `shipcasterSvc.pyj`, `shipCasterVisualization.pyj` and `warzoneMapPanel.pyj` establish these call sites
and collection uses. The inspected archive SHA-256 is `37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`.
The older evejs shipcaster runtime also sends empty lists in its default state
(`shipcasterRuntime.js` SHA-256 `4fc40271b4e5e2b590abdcceb424ee1a3e80be333a1e23a8e1e06e50d55209c7`).
CCP documents the landing-pad objects used by the live feature. [Havoc expansion notes](https://www.eveonline.com/news/view/havoc-expansion-notes)

**Automated validation.** `WarzoneShipcasterReadsReturnEmptyListsForSelectedPilots` reproduced the unsupported call
before the fix. It passed in 6.97 seconds after the fix, checking list encoding, selection gating and malformed
arguments. Tool and solution restores and a warning-free build passed in 1.80 seconds. The focused run below
passed all 34 tests in 19.89 seconds. Both Python policy suites passed all 17 checks in 1.70 seconds.
Formatting and width verification passed in 51.13 seconds. The full .NET suite was not run.

```text
FullyQualifiedName~FactionWarzoneProtocolTests|FullyQualifiedName~FactionWarzoneFoundationTests|FullyQualifiedName~Exploration.AgencyTests|FullyQualifiedName~MilitiaComplexTests
```

**Live acceptance.** Not performed. Agent client testing is disabled.

**Optional maintainer checks.** Open both empire warzone maps and switch factions in the Interstellar Shipcaster panel.

## Militia missions, ranks, and combat statistics — November 20, 2026

### Mission content and rewards

Militia agents offer a build 3662168 assignment at each level for each empire. The initial rotation uses
Decapitation (1), Knife in the Throat (2), A Left Hook (3), and Lethal Strike (4). The
[mission bindings](../../src/Eden.Data/Catalog/FactionWarfare/MilitiaMissionBindings.cs) identify all 16 missions,
objectives and escorts. Layouts, mission text and gate type lists 512, 513, 514 and 517 come from the pinned catalog.
The exported dungeons lack server spawn-table rows. Eden selects escorts and commander hulls from the opposing
militia's catalog and uses the existing agent reward schedule. Other militia mission scripts remain outside this
rotation.

Only members of the issuing militia with sufficient agent access can take an offer. Destinations rotate among the
five nearest reachable opposing systems without a pending transfer at offer time. Accepted assignments create a
public, warpable entrance beacon and use the catalog gate's hull restrictions. Levels 1 and 4 require destroying a
commander. Levels 2 and 3 require destroying a structure. Completing an objective makes the mission eligible for
remote turn-in while docked and still enlisted. Mission NPC kills grant no bounty or enemy faction standing loss.
Completion gives militia LP and agent and militia corporation standings. Missions give no VP or storyline progress.
Quitting, expiry of an accepted mission, and declining again within four hours lower militia corporation standing.

### Progression, PvP and the dashboard

The ten client ranks use raw militia corporation standing. Promotions through ranks 1–9 award a one-time 2%
standing gain toward the issuing empire per newly reached rank. Eden keeps a durable high-water mark per pilot and
faction so demotion, retirement and re-enlistment cannot repeat a promotion award. Promotion earnings do not
propagate to other empires. Successful complexes and battlefields give winners a 0.5% militia corporation standing
gain. These percentages and the 2% militia mission penalty are Eden policies.

An eligible opposing-militia ship loss records one kill for the final pilot. LP uses the catalog market
reference or resident adjusted hull value, subtracts maximum insurance when available, then adds items actually
destroyed in the loss. Eden awards one LP per 10,000 ISK, rounded down and capped at 50,000 per loss. Pods,
corvettes, friendly, neutral and same-account kills do not count. The final pilot and same-side pilots who dealt
damage and are present on grid split the award once per account, with integer remainders assigned in pilot-ID
order. There is no multiplier for dropped items. The historical CCP formula establishes the valuation inputs.
Current server-only multipliers and payout coefficients were unavailable. [CCP PvP LP announcement](https://www.eveonline.com/news/view/fw-dev-blog-1-1-1#lps-for-kills)

Mission, PvP, complex, battlefield, hub and Advantage LP credits use the militia issuer and existing corporation
LP tax. An award receipt records time, source, issuer, recipient, gross LP and tax in the same resident transaction
as the balance change. The existing LP store spends these balances on its supported catalog offers.

The dashboard reports actual pilot, corporation, alliance and militia kills and VP for yesterday, the previous
seven complete UTC days, and the lifetime of the recorded activity. The kill and capture events supply historical
organization membership. Complexes and battlefields divide the actual applied VP change among winners, including
defensive reductions. The pinned dashboard's top and all tables return category maxima and sums across recorded
pilots, corporations and alliances. Faction summaries use current occupancy and enlistment. The migration does
not infer historical kills or VP from older saves.

Build 3662168 static inspection covered the statistics calls in `facWarSvc.pyj`, rank reads, dashboard group
IDs 1, 2 and 32, and standing events in `appLogConst.pyj`. The older evejs
`server/src/services/faction/facWarMgrService.js` confirmed rank and faction-info fields at SHA-256
`0deb401e43bea36d4f01e7125135e1e46b840b4f15488c3168874d6c54ac9422`. Gameplay references include the
[CCP 23.02 patch notes](https://www.eveonline.com/news/view/patch-notes-version-23-02),
[CCP standings update](https://www.eveonline.com/news/view/standings-and-awoxing-update),
[EVE University militia missions](https://wiki.eveuniversity.org/Faction_warfare_missions),
[EVE University standings](https://wiki.eveuniversity.org/Faction_warfare_standings), and
[EVE University ranks](https://wiki.eveuniversity.org/Ranks).

### Milestone 7 validation

**Automated validation.** All 16 mission, gate, beacon, NPC and structure profiles loaded from the installed
build 3662168 catalog. Tool restore, solution restore, and the warning-free build passed in 4.49 seconds
combined. The exact PvP ship-loss test passed in 6.35 seconds. The six directly affected mission, rank, PvP and
capture checks passed in 8.11 seconds. The broader focused run passed 958 of 961 tests in 10 minutes 31 seconds.
The two Blockade warp cases and the fatal-shot combat-text session case failed in the same way on the
pre-milestone commit `ed522cf6` when run in isolation. Both Python policy suites passed all 17 checks.
Formatting and width verification passed in 31.68 seconds, and documentation links and whitespace passed.
The full .NET suite was not run.

The broader run used the following filter.

```text
FullyQualifiedName~FactionWarfare|FullyQualifiedName~Agents.Mission|FullyQualifiedName~CharacterSheet|FullyQualifiedName~CharacterInteractionHistoryTests|FullyQualifiedName~PilotCombatTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~LoyaltyStoreTests|FullyQualifiedName~CorporationLoyaltyTests|FullyQualifiedName~LoyaltyWalletServiceTests|FullyQualifiedName~IncrementalCheckpointTests|FullyQualifiedName~DetachedCheckpointTests|FullyQualifiedName~CombatAnomaly|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~CooperativeFleetTests
```

The test logs are in `artifacts/faction-warfare/milestone-7/`.

**Live acceptance.** Not performed. Agent client testing is disabled.

**Optional maintainer checks.** Warp to another militia pilot's mission beacon. Check the native character-sheet
rank and faction warfare dashboard after earning a promotion and an opposing-militia kill.

## Advantage and battlefields — November 20, 2026

Faction scores now come from resident warzone state. Frontline systems get 10,000 terrain points per
same-warzone neighboring system owned by each faction. Contribution and terrain sum to at most 100,000 per
faction. Positive net Advantage adds a proportional VP bonus with a minimum of one extra VP. Defense applies
the bonus in the direction of reducing contestation. The underlying formula, minimum, and score scale were
verified in build 3662168's advantageState.pyj. Terrain updates immediately when occupation changes. Eden
decays earned contribution by 1,000 per elapsed day since a faction's last effective score change in that
system and clears earned scores on occupation transfer. This decay schedule is an Eden policy.
[CCP Uprising notes](https://www.eveonline.com/news/view/patch-notes-version-20-10) ·
[UniWiki terrain and objectives](https://wiki.eveuniversity.org/Faction_Warfare#Advantage)

On Frontlines, each system has a shared NVY-30 battlefield drawn from the four pinned templates (9898–9901).
The existing acceleration gate reads hull list 514 and forbidden equipment list 984. Three 30 km points
each contribute one signed point per 20 seconds of continuous control. Opposing pilots and local militia
defenders block scoring. The scoreboard ends the site at ±150. The scanner lists it with a separate Agency
battlefield directory. Completion settles 2,000 base VP, 15,000 Advantage, and 150,000 LP per eligible
winner up to 30 full shares. Eden splits excess shares and uses eligible ships in the capture areas at
completion. The respawn is three hours from completion and survives restarts. Eden uses one modeled
occupier defender per active contested point, with the normal complex NPC replacement interval.
[CCP battlefield and reward changes](https://www.eveonline.com/news/view/patch-notes-version-20-10-2) ·
[CCP V24.01 battlefield notes](https://www.eveonline.com/news/view/patch-notes-version-24-01)

Frontline and Command Operations systems share empire supply cache and depot targets plus a probe-scannable
occupier Rendezvous Point. A supply loss removes 2,000 Advantage and credits participating enemies with
15,000 LP each up to five full shares. Rendezvous completion awards 2,000 Advantage and an Eden-authored
15,000 LP to the eligible final attacker. The layouts use stable Eden grids for each instance. The
supply ship roster and the basic three-hull Rendezvous wing use catalog militia hulls. Rare officer
waves and authored NPC spawn-table rows remain unmodeled.

Rearguard Operation Centers use the five containers in each of the four native layouts (9904–9907).
Opposing militia pilots can run the normal data hacking and loot flow. Eden awards one enemy splice
or decoder per solved container. The first success schedules a militia response after eight seconds
and limits the site to another 20 minutes. The existing militia LP offers exchange the components for
Propaganda Broadcast Structures or Listening Outposts.

Build 3662168's eveMisc.pyj sends ship.Drop with a list of (itemID, quantity) tuples, an owner and an
ignore-warning flag. The reply maps input IDs to lists of launched IDs. Eden validates cargo ownership,
faction, Anchoring III, system security, placement near gates, stations and the same deployable type, and
the system cap before consuming a unit. Deployables activate after ten seconds. The ten-minute reward
timer advances while the sponsor is present and pauses for enemy reinforcements. Eden uses three modeled
reinforcement waves, a one-hour expiry, and 10,000 LP at successful completion. Propaganda adds 2,000
Advantage and listening removes 2,000 from the opponent.
[CCP deployable balance](https://www.eveonline.com/news/view/patch-notes-version-22-02) ·
[UniWiki deployment and hacking](https://wiki.eveuniversity.org/Faction_Warfare#Propaganda_Broadcast_Structures)

The native battlefield slim uses component_fwCapturePoint_factionInfo, component_fwCapturePoint_nextPointTime
and component_fwScoreboard_score. The gateway uses GetSolarSystemScoresResponse and SolarSystemScoresNotice
for public values and routes ScoreContributionNotice only to the activating or settling pilot's account.
The evejs comparison used fwAdvantageGatewayService.js SHA-256
2b432a5f5b239fb169bc4475d77e846ce17341ba849cf410daaa7bb40c5f769e for the public reply shape and fixture scores.
The reference gateway serves fixture scores. Rigid objective balls reuse the Carbon Destiny
layout already used by infrastructure hubs. World schema 199 saves Advantage, objective identities and
deadlines through resident checkpoints. Short timing samples are discarded at restart.

**Automated validation.** The merge filter passed all 34 scheduler, complex, and infrastructure hub tests
in 9.21 seconds. The milestone solution built with zero warnings in 9.99 seconds. The final focused run
passed all 551 tests in 323.52 seconds. Tool and solution restores and the two Python policy suites
(8 catalog setup and 9 formatting policy tests) passed. The installed catalog audit covered four
battlefield layouts and 12 capture points, four Operation Centers and 20 containers, all four Rendezvous
layouts, and 16 supply and deployable types. Formatting verification passed in 31.53 seconds.
Warmed Advantage reads issued zero SQL commands or connections
and performed zero fitting calculations. Validation used focused suites.

The filter joined the following FullyQualifiedName substring selectors with the test runner's OR operator.

- FactionWarfare, Exploration.Hacking, Exploration.AgencyTests, Exploration.ProbeScanning,
  Exploration.CombatAnomaly, IdleAnomalyFrameTests, MissionGate, Combat.NpcPopulation, Combat.NpcCombatTests
- Combat.AreaWeaponTests, Combat.SmartbombTests, Combat.PilotMissileTests, Combat.ResidentMissileLaunchTests,
  Combat.PilotDroneTests, Combat.ResidentDroneTests, Flight.FlightSessionTests, Gateway
- LoyaltyStoreTests, WorldMigrationTests, IncrementalCheckpointTests, DetachedCheckpointTests

The follow-up merge brought in main at 09787a13. Its build passed with zero warnings in 9.97 seconds.
The integration filter passed 158 tests with 9 skipped in 14.61 seconds. It covered MilitiaAdvantageTests,
MilitiaObjectiveTests, BattlefieldThreePointScore, OperationCenterRequiresOpposingMilitia,
NpcPopulationFrameTests, and Eden.Protocol.Tests.Administration. The Operation Center case passed again
in 6.92 seconds after enforcing resident-only access.

**Live acceptance.** Not performed. Agent client testing is disabled.

**Optional maintainer checks.** Open the Advantage panel and complete an objective. Verify the native
scoreboard and gate on a Battlefield NVY-30. Hack an Operation Center and launch its redeemed deployable.

## Organization enlistment and militia communications — November 20, 2026

Player corporation CEOs and directors can apply for their independent corporation. The executor corporation’s
CEO and directors act for an alliance and all its member corporations. Join and leave applications take effect
at the next 11:00 UTC downtime and can be withdrawn before that boundary. Leaving organizations retain their
faction until the scheduled withdrawal. Active affiliation is inherited immediately by new corporation and alliance
members. Departure or dissolution removes inheritance. Compatible direct enlistment remains on record and resumes
when inheritance ends. An independent corporation must finish or withdraw its own militia enrollment before joining
or forming an alliance.

Enrollment requires a nonnegative organization standing and each member’s unmodified faction standing at or
above −5.0. The organization average uses recorded base standings of members with at least seven days in their
corporation. Members without a standing record contribute no sample, and an empty average is zero. These are
Eden’s aggregation rules. They apply across all member corporations for alliances. Existing direct enlistment with
another militia blocks the application. Eden checks all members at filing and activation, including offline members.
It checks the affected recruit or corporation for corporation admission and alliance application, acceptance and
effective admission. An enrollment request that becomes ineligible during its wait returns to the neutral state.
[UniWiki faction warfare](https://wiki.eveuniversity.org/Factional_Warfare) ·
[CCP Cradle of War release notes](https://www.eveonline.com/news/view/cradle-of-war-expansion-notes) ·
[CCP downtime schedule](https://support.eveonline.com/hc/en-us/articles/26205909113244-Downtime)

The daily review gives members who fall below −5.0 a persisted 24-hour warning. A member who has recovered
at the next review keeps their membership. An unresolved ordinary member returns to their original NPC
corporation. An unresolved CEO causes their independent corporation or entire alliance to lose militia
affiliation. Eden uses a separate 24-hour grace period if the organization standing drops below zero.
The individual standing notifications use build 3662168’s types 298–303 and its formatter dictionaries.
The warning duration uses FILETIME ticks. Aggregate warnings and removals use the legacy corporation and
alliance faction-warfare types. World schema 198 saves the organization phase, deadlines and member warnings.
The host runs scheduled reviews without connected pilots, and the first review after reopening resumes saved
transitions and warnings. A missed review cannot remove a member who has never received a warning.

Build 3662168’s `facWarSvc.pyj` sends a single faction ID to each of the eight corporation and alliance
join, leave and withdraw methods addressed to `facWarMgr`. The `GetCorpFactionalWarStatus` reply is a
`utillib.KeyVal` with `status` (joining 0, active 1, leaving 2), `factionID` and `startDate`. The date is
the scheduled boundary for a pending change and the activation date while active.
`fwEnlistmentSvc.pyj` consumes effective, direct and inherited faction in `OnEnlistmentStateUpdated`.
Eden publishes these independently of changes to the effective `warfactionid`, so a direct enlistee still
receives changes to the inherited component. The committed identity also supplies public profiles and existing
combat, objectives and docking rules.

Build 3662168’s fleet scope `INVITE_MILITIA` uses bit 4. Militia adverts add same-faction access with the existing
member-group restrictions. The fleet leader and applicant wire records carry `warFactionID`. Militia chat uses
`faction_<factionID>` and the `warfactionid` XMPP presence attribute. Join, member reads and message delivery use
the committed affiliation, including a change that reaches the server before the client refreshes its session.
The evejs comparison covered `facWarMgrService.js` (SHA-256
`0deb401e43bea36d4f01e7125135e1e46b840b4f15488c3168874d6c54ac9422`) for corporation status and
`sessionChatSync.js` (SHA-256 `c220168813e71b0ba1980877a32146b688af841332280b6cc6558830dffb1ce9`) and
`xmppStubServer.js` for militia channel and presence conventions. The current client and released CCP
rules supply the organization transitions.

**Automated validation.** The exact downtime and checkpoint test passed in 7.31 seconds. The focused
chat, advert and aggregate standing tests passed in 7.52 seconds. The existing corporation role/title reopen
check passed in both configurations after normalizing neutral militia metadata. Final solution build took
5.12 seconds with zero warnings. All 327 focused .NET cases passed in 131.05 seconds. Tool and solution
restores and both Python policy suites passed (8 catalog setup and 9 formatting policy cases).
Formatting verification passed in 32.61 seconds.

The final filter joined these `FullyQualifiedName~` selectors with `|`:

`Eden.Protocol.Tests.FactionWarfare`, `Eden.Protocol.Tests.Alliances`, `Eden.Protocol.Tests.Chat`,
`Eden.Protocol.Tests.Fleets`, `CorporationMembershipTests`, `CorporationRecruitmentTests`,
`CorporationClosureTests`, `CorporationRoleTests`, `CorporationPublicInfoTests`, `CharacterSheetServiceTests`,
`CooperativeFleetTests`, `InboxTests`, `InboxServiceTests`, `CapsuleerAllianceTests`, `CapsuleerCorporationTests`,
`CrimewatchTests`, `WorldMigrationTests`, `IncrementalCheckpointTests`, `DetachedCheckpointTests`

**Live acceptance.** Agent client testing is disabled. The native client was used for static protocol inspection.

**Optional maintainer checks.** Inspect pending and active enlistment in the native panel. After downtime,
verify that the militia channel and Fleet Finder reflect the new affiliation and that the inbox renders standing warnings.

## Infrastructure hubs and territorial conquest — November 20, 2026

Each capturable warzone system exposes a warpable empire infrastructure hub when its resident space scene loads.
The four build 3662168 types are 29286 (Amarr), 29302 (Caldari), 29303 (Gallente), and 29304 (Minmatar).
Their catalog group is 925, radius is 10,290 m, and base shield, armor, and structure are 7.5M, 7.5M, and 10M HP.
Catalog shield recharge time is 10,800 seconds. The exported celestial catalog contains no hub coordinates.
Eden uses a stable grid at `(20,000,000,000, 0, -10,000,000,000)` meters in each warzone system. The closest
catalog celestial or station to this grid across the 160 systems is more than 9 billion meters away.

At 75,000 VP the hub becomes vulnerable to the opposing militia. Turrets, missiles, drones, and area damage share
that check. A defense that leaves the score in the 500 VP buffer preserves the current hub's damage and
contributors. A defense that drops the score below 75,000 replaces the hub at full health on the same grid and
clears the failed attack's contributions. The new item ID invalidates paid impacts addressed to the previous hub.
At 1% structure, the hub is destroyed and the system is captured with its old occupier until the next 11:00 UTC.
Eden suspends the local complexes during that interval. [UniWiki infrastructure hubs and victory points](https://wiki.eveuniversity.org/Faction_warfare#Infrastructure_Hub) ·
[CCP downtime schedule](https://support.eveonline.com/hc/en-us/articles/26205909113244-Downtime)

The terminal hit records a durable attacker and transfer deadline and settles a fixed 40,000 LP pool among
distinct contributors who still belong to the attacking militia. The pool is split equally, with any remainder
assigned in pilot ID order. Each share applies normal player corporation LP tax. Eden adopts the published
historical base payout and preserves contributions from eligible pilots who have left the grid or disconnected.
[CCP hub capture rewards](https://www.eveonline.com/news/view/fw-dev-blog-1-1-1#rewards-paying-for-a-job-well-done)

World schema 197 stores the current hub, pending transfer, and damage contributions. Durability uses the ordinary
resident checkpoint generation. Startup and empire maintenance process due transfers without observers. Transfer
creates the attacker's hub, resets VP, retires old complex defenders, and regenerates the system's complex
identities in one resident action. The occupation snapshot recalculates adjacency and supplies docking and service
policy as well as the map. The effective capture date is the scheduled downtime even when the server reopens later.

Stock `fwWarzoneSvc.pyj` consumes `OnWarzoneOccupationStateUpdated(systemID, new, old)`.
`fwVictoryPointSvc.pyj` consumes `OnVictoryPointStateUpdated(systemID, state)` and the two
`OnFacWarBunkerVulnerable` and `OnFacWarBunkerNonVulnerable` prompts with the attacker faction ID. The host
publishes these from committed warzone revisions. A destroyed hub is removed through Destiny. The native
two-field `VictoryPointState` still represents the pending system by its VP score. The retired hub LP donation
window and upgrade tiers are outside this milestone.
[Carbon Destiny ball insertion flags at 27ee40b](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Thunkers.cpp#L101)

The evejs `factionWarfareAuthority.js` comparison (SHA-256
`4437cc03679a9a2ee1cd127da6c293e815b05b10faeb564cda6e8b284b1fc5a2`) exposes occupation and VP for two
authored systems. Hub combat and transfer timing here come from the pinned catalog and the gameplay references.

**Automated validation.** Exact-method checks passed for threshold and downtime boundaries (2.63 seconds),
defensive recovery and checkpoint reopen (7.60 seconds), and the defense-versus-final-hit race
(6.88 seconds). The catalog audit covered all four hub types and checked the placement against stations
and celestials in all 160 warzone systems. Warmed warzone reads and deadline checks recorded zero SQL
commands, connections, and fitting calculations.

The solution built in 12.37 seconds with zero warnings. The focused dependency run passed all 267 tests
in 126.66 seconds. Tool and solution restores, both Python policy suites (8 and 9 tests), and formatting
verification (28.83 seconds) passed.

```text
FullyQualifiedName~FactionWarfare|FullyQualifiedName~Combat.NpcCombatTests|FullyQualifiedName~Combat.NpcScene|FullyQualifiedName~Combat.PilotMissileTests|FullyQualifiedName~Combat.ResidentMissileLaunchTests|FullyQualifiedName~Combat.PilotDroneTests|FullyQualifiedName~Combat.ResidentDroneTests|FullyQualifiedName~Combat.AreaWeaponTests|FullyQualifiedName~Combat.SmartbombTests|FullyQualifiedName~Flight.FlightSessionTests|FullyQualifiedName~Flight.DungeonObjectGeometryTests|FullyQualifiedName~Docking.StructureServiceTests|FullyQualifiedName~PilotSessions.CorporationLoyaltyTests|FullyQualifiedName~Persistence.DetachedCheckpointTests|FullyQualifiedName~Persistence.IncrementalCheckpointTests|FullyQualifiedName~Persistence.WorldMigrationTests
```

**Live acceptance.** Not performed. Agent client testing is disabled.

**Optional maintainer checks.** Warp to a warzone hub from the overview. Compare the native damage and
vulnerability UI on either side of the VP threshold. Verify the new hub and map after a scheduled transfer.

## Shared complexes and rewards — November 20, 2026

The 160 warzone systems each have one shared site slot for Scout, Small, Medium, Large, and Open complexes.
The site's template follows the system's occupier. Completing a site removes it from the scanner, Agency,
and space. The replacement uses a new instance ID and a new grid after 20, 40, or 60 minutes in Frontline,
Command Operations, or Rearguard systems. Eden uses a deterministic rotation within each family.
[CCP complex distribution and replacement intervals](https://www.eveonline.com/news/view/patch-notes-version-21-05)

The [build 3662168 template manifest](../../src/Eden.Data/Catalog/FactionWarfare/MilitiaComplexVariants.cs)
identifies the four empire dungeon IDs, beacon, occupier spawner, and payout capacity for each variant.
The installed catalog audit covered 72 templates, of which 68 have gates. Four retired Small Interceptor
templates remain in the manifest for provenance. The other 68 templates are available for rotation.

| Family | Variants in rotation | Timer | Gate lists | Base LP | VP |
| --- | --- | ---: | --- | ---: | --- |
| Scout | BSC-1, NVY-1/2, ADV-1 | 5 minutes BSC, 10 minutes otherwise | 238, 511, 854 | 10,000 | 50 BSC, 25 NVY, 100 ADV |
| Small | NVY-1/2, ADV-1/2, AF-1 | 10 minutes | 512, 515, 1015 | 15,000, 25,000 AF | 50 NVY, 75 ADV, 150 AF |
| Medium | NVY-1/3, ADV-1/3 | 15 minutes | 513, 516 | 20,000 | 150 NVY, 175 ADV |
| Large | NVY-1/4, ADV-1 | 15 minutes | 514, 517 | 25,000 | 250 NVY, 300 ADV |
| Open | Open Complex, up to 5 full shares | 15 minutes | Open warp-in | 30,000 | 350 |

Gate range is 90 km plus hull and gate radii. All installed gates check their native hull list and reject
fitted warp core stabilizers via type list 984. Neutral pilots use Partial or Disabled safety and receive
the ordinary lowsec suspect timer after a successful gate command. Direct warp to the gated pocket routes
to its entrance. Open sites have no gated suspect check. Both sides can reverse a signed timer from inside
the 30 km radius. Eligible opposing pilots pause it. Neutral pilots, capsules, Ventures, cloaked ships, ships
aligning for warp, and ships in warp do not contribute. A capture point blocks cloak activation inside its
radius. [CCP gate behavior](https://www.eveonline.com/news/view/patch-notes-for-march-2020-release) ·
[UniWiki capture mechanics](https://wiki.eveuniversity.org/Faction_warfare#Capturing_complexes)

Inert client inspection of `anomalyTracker.pyj` confirmed the Agency directory's system-to-list shape and
occupier filtering. The normal complex bracket reads `slimItem.capturePoint` via `bracketMgr.pyj` and
`inSpaceBracket.pyj`. Its fields are `points` as a signed percentage of the current side's progress,
`captureTime` in minutes, `captureID` as the active faction, the frozen `"contested"` marker, or None for an idle point, and
`lastCapturing` as the faction that holds the signed progress. The client records `lastIncident` in simulation
time when it applies the slim. Both sides reuse the ordinary shared anomaly scanner notifications.

The staged occupier spawners give a minimum replacement interval of 85 seconds for BSC and 210 seconds for
the other audited variants. Eden uses one catalog militia hull per offensive wave and targets attackers
inside the radius. The exact server-side spawn table rows, opposing NPC waves, special AF NPCs and their
loot remain outside this runtime. Eden uses family base LP for the ordinary sites and an authored 100 VP for
Scout ADV. The AF reward and removal of Interceptor-1 follow the September 22 V24.01 patch.
[CCP site rewards and AF changes](https://www.eveonline.com/news/view/patch-notes-version-24-01) ·
[CCP territorial VP and LP multipliers](https://www.eveonline.com/news/view/patch-notes-version-21-05)

Settlement applies the 1.5×, 1×, and 0.01× operational multipliers, scales defense LP by pre-completion
contestation, splits excess shares equally, truncates to whole LP, and credits the winning militia's issuer.
Player corporation LP tax comes out of each pilot's share. Stable defense pays zero LP. Offensive capture
at or above 75,000 VP pays zero LP and can still fill the 500 VP buffer. A single resident transaction
commits the site deadline, VP change, and personal and corporation balances under world schema 196.
The checkpoint preserves progress, defender IDs, completion, and the replacement deadline. The next active
observation starts a fresh timing sample when a pilot reconnects or a world reopens. Dedicated completion notices, standings,
rank progression, and hub conquest are later work.

The evejs comparison used `fwOpenRuntime.js` (SHA-256
`6ff64488da2110606f46bd989c372e6b9fdd65f0d75d3ee1aa947e39a0d8cd6c`) and
`fwOpenRewards.js` (SHA-256 `87843f86fef1fd3daa8750fae887ccb675555b6712e082a050f13bb898684dbd`).
It informed contact eligibility, signed reversal, continuous observation, and once-only settlement.

**Automated validation.** The initial exact reward-rule test passed in 2.85 seconds. The exact offensive wave
and shared settlement test passed in 6.12 seconds. The installed-catalog audit covered 72 dungeon records,
the corresponding capture points and occupier spawners, 68 gate layouts, and 16 empire NPC hull profiles.

The dependency filter passed 328 tests in 217.55 seconds. After the idle bracket correction and the additional
post-settlement reopen assertions, the solution built in 9.87 seconds with zero warnings and the final
complex and Agency filter passed 28 tests in 15.18 seconds. Warmed site reads recorded zero SQL commands,
connections, and fitting calculations. Tool and solution restores, both Python policy suites (8 and 9 tests),
and formatting verification (33.01 seconds) passed. Validation used focused suites.

Dependency filter

```text
FullyQualifiedName~FactionWarfare|FullyQualifiedName~Exploration.AgencyTests|FullyQualifiedName~Exploration.CombatAnomaly|FullyQualifiedName~AnomalyWarpInTests|FullyQualifiedName~IdleAnomalyFrameTests|FullyQualifiedName~MissionGate|FullyQualifiedName~Combat.CrimewatchTests|FullyQualifiedName~Combat.NpcCombatTests|FullyQualifiedName~Propulsion.SpatialModule|FullyQualifiedName~Flight.GateCloakTests|FullyQualifiedName~Flight.SiteWarpInRuleTests|FullyQualifiedName~CorporationLoyaltyTests|FullyQualifiedName~LoyaltyWalletServiceTests|FullyQualifiedName~Market.LoyaltyStoreTests|FullyQualifiedName~Persistence.DetachedCheckpointTests|FullyQualifiedName~Persistence.IncrementalCheckpointTests|FullyQualifiedName~Persistence.WorldMigrationTests
```

Final filter

```text
FullyQualifiedName~FactionWarfare.MilitiaComplexTests|FullyQualifiedName~Exploration.AgencyTests
```

**Live acceptance.** Not performed. Agent client testing is disabled.

**Optional maintainer checks.** Warp to a Scout from the scanner and verify the native capture timer.
Try its gate at green and Partial safety on a neutral pilot. Capture an Open complex and check the militia LP
wallet and the system's VP in the warzone map.

## Faction navy pursuit and service access — November 20, 2026

Enemy empire highsec warns an enlisted pilot in a ship, then dispatches a faction frigate and cruiser.
System faction and security come from build 3662168. The four wings use the catalog navy profiles already
admitted for empire guards and run through ordinary NPC motion, weapons, electronic warfare and ship loss.
Capsules, neutral pilots and non-opposing militias are exempt. The patrol is assigned to its original pilot
and the pursuit profiles omit warp disruption.

Eden uses 6-, 9- or 12-second initial response delays by security band (0.85 and 0.65 boundaries)
and a 10-second redeployment delay after the pilot leaves a grid or defeats the wing. Gate cloak and warp
defer dispatch. Leaving the system or militia, docking, changing hull or closing the pilot session ends the
current pursuit. World schema 195 preserves warning deadlines and live responder IDs across checkpoint
reopen. An active wing uses the same NPC pursuit distance bound as other encounters. The selected pilot session
advances dispatch, and the existing empire enforcement tick sweeps saved pursuits whose pilots have departed.

Highsec command bursts skip an active militia combatant when the booster belongs to a different militia or
has no militia affiliation. The recipient's capsuleer-logoff timer controls the restriction. Same-militia
boosts and lowsec rules retain their normal behavior. A filtered recipient leaves the paid burst available to
eligible fleet members.

NPC station docking availability now reflects the current occupation and capsule exception. Upwell fitting,
repair, insurance, industry, refining, compression and refinery service checks apply the same occupation rule
alongside ACLs and service availability. The tether runtime is on `codex/structure-completion`. Its integration
still needs the occupation check when the two branches are combined.

The `OnRemoteMessage` warning was verified by inert inspection of build 3662168 `gameui.pyj`. The evejs
`space/runtime/commandBurst.js` comparison (SHA-256
`b6172225f6ed90d077ebb083aa48b7d497920164ebec7878e0a06a042cd931fb`) also checks the recipient's PvP timer.
Eden uses the pinned empire pairings from the enlistment milestone. The reference's `fwOpenRuntime.js`
illustrates the `CustomNotify` payload. Navy roster source is `NpcGuardRules` and the build-3662168 NPC catalog.
[UniWiki faction navy behavior](https://wiki.eveuniversity.org/Faction_warfare_strategy_and_tactics) ·
[CCP neutral burst rules](https://www.eveonline.com/news/view/patch-notes-for-april-2019-release) ·
[CCP Upwell and station rules](https://www.eveonline.com/news/view/patch-notes-version-20-10)

**Automated validation.** The exact
`FullyQualifiedName=Eden.Protocol.Tests.FactionWarfare.MilitiaNavyTests.PursuitWaitsAcrossReopenAndDeploysOnlyOneLiveWingPerGrid`
filter passed in 9.93 seconds after a 21.93-second test build. It covers the saved response deadline, delayed
cloaked deployment, ordinary NPC damage, cleared aggressors, and redeployment on the next grid. Warmed
pursuit reads recorded zero SQL commands, connections and fitting calculations.

The milestone build passed in 28.13 seconds with zero warnings. The focused filter below passed all 227 cases
in 90.25 seconds, including command burst recipient affiliation and timer expiry, occupation-dependent
service access, ordinary NPC guards, CONCORD, industry and schema migration. Tool and solution restores,
both Python policy suites (8 and 9 cases), and formatting verification (39.39 seconds) passed. Validation used
focused suites.

```text
FullyQualifiedName~FactionWarfare|FullyQualifiedName~Fleets.CommandBurstTests|FullyQualifiedName~Docking.StructureServiceTests|FullyQualifiedName~Structures.StructureCompressionTests|FullyQualifiedName~Fittings.MultifitTests|FullyQualifiedName~Combat.NpcGuardTests|FullyQualifiedName~Combat.ConcordTests|FullyQualifiedName~Combat.CrimewatchTests|FullyQualifiedName~Persistence.WorldMigrationTests|FullyQualifiedName~InsuranceTests|FullyQualifiedName~Eden.Protocol.Tests.Reprocessing.ReprocessingTests|FullyQualifiedName~Eden.Protocol.Tests.Repair.RepairTests|FullyQualifiedName~Eden.Protocol.Tests.Industry.IndustryTests
```

**Live acceptance.** Not performed. Agent client testing is disabled.

**Optional maintainer check.** Enter enemy highsec in a militia ship and verify the warning, navy appearance,
and ability to warp away.

## Individual enlistment and militia combat — November 20, 2026

Pilots can join any of the four NPC empire militias at a station owned by that empire. An eligible player corporation
member can instead enlist directly and keep their corporation. The CEO or a director sets which militias members may
join. Both entry paths require nonnegative raw faction standings, a matching NPC station and no current enlistment.
Joining an NPC militia from a player corporation also checks that corporation's existing departure rules.
Self-retirement from an NPC militia uses the native corporation departure call and restores the pilot's original NPC
corporation. Direct retirement starts the pinned 24-hour direct-enlistment cooldown. The deadline survives reopening
and permits enrollment at equality. The corporation policy governs admission to direct enlistment.

World schema 194 stores direct affiliation and its cooldown in the resident economy. NPC militia membership and
history use ordinary corporation employment. Native session changes publish `warfactionid`. The enlistment notification
and read both carry effective, direct and corporation faction IDs. Public character rows and ship slim items use the
same authoritative affiliation. The corporation status and employment reads support the native Militia window.

Only Amarr–Minmatar and Caldari–Gallente are hostile empire pairs. Opposing enlistees can fight at green safety,
including in the same corporation. Paid attacks retain weapons and capsuleer-logoff timers and use the shared ship
loss path. Militia legality by itself applies neither a suspect/criminal flag nor a limited engagement. Assistance
from outside the recipient's militia while their capsuleer-logoff timer is active follows the shared highsec criminal
and lowsec suspect rules. Ordinary Crimewatch checks still apply to neutral targets. An enemy enlistee can dock or
undock only in a capsule at NPC stations and Upwell structures in Command Operations and Rearguard systems.
Frontlines and friendly occupation retain the existing docking permissions.

Inert build-3662168 inspection used `fwEnlistmentSvc.pyj`, `enlistmentChecks.pyj`, `facWarSvc.pyj`,
`facwarCommon.pyj`, `dockableChecks.pyj` and `enlistmentUtil.pyj` from the staged archive identified below.
The retirement button calls `corp.KickOut`, which dispatches `corpRegistry.KickOutMember`. The reference
`fwCharacterEnlistmentMgrService.js` supplies the independent evejs comparison. Its hash is recorded below.
The older `faction/facWarMgrService.js` hash is `0deb401e43bea36d4f01e7125135e1e46b840b4f15488c3168874d6c54ac9422`.
The pinned client confirms the empire enemy pairs and the scope of the retirement cooldown.
[EVE University](https://wiki.eveuniversity.org/Faction_warfare) ·
[CCP docking rules](https://www.eveonline.com/news/view/patch-notes-version-20-10) ·
[CCP neutral assistance clarification](https://forums.eveonline.com/t/devblog-war-war-sometimes-changes/142879/192)

The navy pursuit, command burst and service changes are recorded above. Complexes, LP and capture actions remain
later milestones.

**Automated validation.** The direct-enlistment flow passed its initial exact-method rerun in 6.83 seconds.
The 420-case dependency run took 211.82 seconds and produced 416 passes and four failures. One failure caught
the corporation faction lookup accepting an unknown corporation ID. The lookup now retains the original
corporation validation. The other three assertions reproduced at the same lines on `bd8adb62`: two cases of
`FutureLogisticsHullSelectorsHaveKnownConsumersWithoutEnablingTheirHullCohort` and one of
`RestrictionsAndDuplicateFittingPreserveInventory`. They assert older hull admission boundaries.

The corrected solution build passed in 23.31 seconds. The correction filter passed all 41 cases in 22.57
seconds, including native join, retirement, occupation-based docking, corporation permissions and checkpoint
reopen. Both Python policy suites passed (8 and 9 cases), as did tool and solution restores and formatting
verification (40.75 seconds). The full .NET suite was outside these runs.

Dependency filter

```text
FullyQualifiedName~FactionWarzone|FullyQualifiedName~MilitiaEnlistmentTests|FullyQualifiedName~CrimewatchTests|FullyQualifiedName~ConcordTests|FullyQualifiedName~RemoteAssistanceTests|FullyQualifiedName~RemoteRepairTests|FullyQualifiedName~CorporationAdministrationTests|FullyQualifiedName~CorporationScreenTests|FullyQualifiedName~CorporationShowInfoTests|FullyQualifiedName~CharacterSheetServiceTests|FullyQualifiedName~MapStartupTests|FullyQualifiedName~FlightSessionTests|FullyQualifiedName~StationUndockTests|FullyQualifiedName~StructureServiceTests|FullyQualifiedName~DockedServicesTests|FullyQualifiedName~WorldMigrationTests
```

Correction filter

```text
FullyQualifiedName~MilitiaEnlistmentTests|FullyQualifiedName~CorporationShowInfoTests|FullyQualifiedName~CorporationScreenTests|FullyQualifiedName~CharacterSheetServiceTests
```

**Live acceptance.** Not performed. Agent client testing is disabled.

**Optional maintainer checks.** Check enlistment and retirement in the native Militia window. Observe the war-target
icon on an opposing militia pilot.

## Resident warzone foundation — November 20, 2026

Eden serves both empire warzones, their four militia headquarters, local and global occupation and victory-point
reads, and the initial Advantage score query. A local read outside the warzones returns the pilot's system ID and
Python None. HQ systems appear in the HQ directory. The Agency has no Faction Warfare complexes or battlefields
to list yet.

The [embedded roster](../../src/Eden.Data/Catalog/FactionWarfare/warzone-roster-3662168.json) contains 70 systems
in Amarr–Minmatar and 90 in Caldari–Gallente. The November 20
[CCP ESI roster](https://esi.evetech.net/latest/fw/systems/?datasource=tranquility) supplied system IDs and original
owners. All 160 have territory sprites in their corresponding build-3662168 map record and are lowsec in the
pinned SDE. The Caldari–Gallente map contains another 11 entries with territory sprites outside the ESI roster,
including Intaki HQ. Map art alone cannot establish capturability. All four HQ IDs were matched to pinned SDE
names and [CCP's Havoc notes](https://www.eveonline.com/news/view/havoc-expansion-notes).

Eden initializes each system with its original owner, zero VP and an unset capture date. It derives Frontline,
Command Operations and Rearguard from the 220 reciprocal same-warzone gates in the pinned catalog. This seed
produces 15 Frontlines, 34 Command Operations systems and 111 Rearguards. The server uses the embedded roster
at runtime. Version 193 checkpoints occupation changes as rows keyed by system. Saved occupations take
precedence over the seed. Local reads use the resident index for the current and nearby systems. Global reads take
one coherent resident snapshot, which also supplies the VP directory.

The two Blue constructors use the pinned client's argument order. The local methods take no positional
arguments and return two-element tuples. The Advantage gateway reads the pinned protobuf field numbers and
returns zero contribution and floor for both empire participants in a warzone system. It returns an empty
score list outside a warzone. Gameplay contributions and notices remain in the Advantage milestone.

The reference engine was used for the nested map shape, local tuple and constructor conventions.
The evejs `map/fwWarzoneSolarsystemService.js` and `faction/fwVictoryPointMgrService.js` snapshot SHA-256 values
are `e4e0e128a255351cb24fbb288ae75c49da684bcaa6a34d9d05cb01cfde124253` and
`a8a143f6eb4324f822477c7f919ba2a1f8622d18653875281c33bcadcb73f513` respectively. The pinned client evidence
below takes precedence over the older reference's fixed adjacency and two authored locations. The ESI response
SHA-256 is `2abbe93c0e2cc28d4591086b33d93a6394b12292747c76da035b10ac3697b8fc`. Pinned
`sde/mapStargates` SHA-256 is `2e1185669643d7a4454cd0a88be394dd67b9d8a3d95d028638350a8d78d16d5b`.

The 18 capture point records all specify a 30 km radius and one point per tick. Type `73439` uses a 10-second
tick and capture/block type lists `94/94`. Twelve types use 20 seconds and lists `514/521`, four use 20 seconds
and lists `958/959`, and type `93305` uses one second and lists `514/521`. All five lists resolve in both
`sde/typeLists` and `client/typelist`. Dungeon objectives, acceleration-gate admission and payouts still need per-site
bindings before complexes can spawn.

**Automated validation.** The initial exact filter
`FullyQualifiedName=Eden.Protocol.Tests.FactionWarfare.FactionWarzoneProtocolTests.MapReadsReturnNativeWarzonesHeadquartersOccupationAndVictoryPoints`
passed in 9.85 seconds after a 22.24-second test build. The milestone solution build passed in 17.40 seconds.
The focused filter below passed all 122 .NET cases in 17.00 seconds. It covers the map and gateway packet
shapes, selected-pilot and argument validation, empty Agency activities, schema migration, rollback, detached
checkpoint projection, and restored occupations and adjacency. Warmed reads recorded zero SQL commands,
connections or fitting calculations. Tool and solution restores, both Python policy suites (8 and 9 cases),
formatting verification (29.65 seconds), and documentation link checks passed.

```text
FullyQualifiedName~FactionWarzone|FullyQualifiedName~Maps.MapStartupTests|FullyQualifiedName~Exploration.AgencyTests|FullyQualifiedName~Gateway.GatewayRequestsTests|FullyQualifiedName~Gateway.GatewayTransportTests|FullyQualifiedName~Persistence.WorldMigrationTests|FullyQualifiedName~Serialization.BlueMarshalTests
```

**Live acceptance.** Not performed. Agent client testing is disabled.

**Optional maintainer checks.** Open Warzone Control and verify the four HQ markers and a Frontline system's panel.

## Research and implementation plan — November 16, 2026

This historical plan was drafted from `origin/main` at `92776fc2`. Implementation records above describe current
support. The milestone order below starts with the read-only warzone foundation.

Implement empire faction warfare for all four militias and both warzones using resident authority and checkpoints.
The first playable release should support joining, legal militia combat, capturing complexes, earning militia LP,
making systems vulnerable and completing conquest. Later milestones complete organization enlistment, Advantage,
battlefields, missions and progression. Enlistment or a single authored capture site does not complete this feature.

Pirate insurgencies, corruption/suppression, FOB campaigns, shipcasters, military seasonal campaigns and Vanguard
integration are separate expansions. Preserve their native empty states until authoritative systems exist. Retain
pirate-faction metadata required by empire occupation models without granting pirate enrollment or rewards.

## Sources and limitations

References checked on November 16, 2026:

- [UniWiki faction warfare](https://wiki.eveuniversity.org/Faction_warfare): enrollment, complexes and conquest.
  Neutral pilots cannot capture; adding friendly pilots does not accelerate capture. Its system-upgrade section
  explicitly warns that it is outdated, and old tier tables are not Eden's specification. Treat its downtime-flip
  description as a build-specific question rather than silently implementing it.
- [UniWiki faction warfare standings](https://wiki.eveuniversity.org/Faction_warfare_standings): raw enrollment
  standings and progression; the page explicitly warns of 2026 changes. Verify removal and promotion rules separately.
- [UniWiki ranks](https://wiki.eveuniversity.org/Ranks): ranks follow militia-corporation standings and appear in
  Decorations. Retain per-faction progression through retirement and re-enlistment.
- [UniWiki faction warfare missions](https://wiki.eveuniversity.org/Faction_warfare_missions): mission completion
  does not award territorial progress, and recent ship restrictions need checking. Reconcile older allied-militia
  access wording with the client and 2023 ceasefires.
- [CCP Uprising notes](https://www.eveonline.com/news/view/patch-notes-version-20-10): Frontline adjacency, Command
  Operations, Rearguard, the 75,000 VP threshold, size-dependent objectives and removal of reward tiers. Its launch-era
  Rearguard multiplier is superseded by the pinned constant below.
- [CCP Allegiance notes](https://www.eveonline.com/news/view/patch-notes-version-21-03): Direct Enlistment, its 24-hour
  retirement cooldown and the end of old cross-warzone alliances. Use Amarr versus Minmatar and Caldari versus Gallente;
  do not restore the former four-way war or allow capture for a former ally.
- [CCP Standings & Awoxing Update](https://www.eveonline.com/news/view/standings-and-awoxing-update): removal of derived
  promotion losses, different friendly-fire penalties inside/outside FW sites, and proposed negative-standing
  warning/removal behavior. The article distinguishes deployed changes from plans: confirm actual release state before
  implementing removal deadlines.
- [CCP Fractured Skies notes](https://www.eveonline.com/news/view/fractured-skies-expansion-notes), published November 6
  for November 10, change FW NPC waves and loot. They postdate the staged V24.01 client. Do not silently import the
  five-wave cap or new campaign drops into the build-3662168 specification.

### evejs comparison

The local `F:\Git Repos\evejs` tree has no Git metadata. Its README targets **24.01 build 3396210**; root
`package.json` identifies release **261108.1**, licensed **AGPL-3.0-only**. Eden remains MIT. Consult behavior and
independently implement typed C# rules from verified contracts and public mechanics. Do not copy JavaScript or data
payloads into Eden. These hashes identify inspected local sources, not an upstream commit.

| Source under `server/src/services/` | Evidence and limits |
| --- | --- |
| `corporation/fwCharacterEnlistmentMgrService.js` | Direct/corporation membership, session changes and cooldown. Creation checks positive IDs but omits the required faction allowlist, standings, location, corporation policy and cooldown enforcement. Do not reproduce those omissions. |
| `faction/facWarMgrService.js` | Rank and faction-statistics reply examples. Rank aliases/defaults are compatibility projections, not a verified promotion implementation. |
| `faction/factionWarfareAuthority.js` | Two warzones, militia mapping and occupation projection. Only two authored systems are authoritative; adjacency is fixed at Command Operations. System LP/upgrades stay zero. |
| `faction/fwOpenModel.js`, `fwOpenRuntime.js` | Authored Fliet/Heydieles Gallente/Caldari Open sites, 15-minute progress, range checks, NPC contesting, pause and replacement. Not a complete site catalog or territorial campaign. |
| `faction/fwOpenRewards.js` | Completion identity, participant snapshot and settlement/outbox concepts. Fixed 350 VP and campaign-election progression are authored policies. Do not import election coupling or file-flush persistence. |
| `faction/fwQueryPayloads.js`, `fwVictoryPointMgrService.js`, `map/fwWarzoneSolarsystemService.js` | Occupation/VP models, nested dictionaries, local tuples, HQ reads and updates. The authored VP owner clamps at the threshold, omitting the pinned 500-point buffer. |

Snapshot SHA-256:

| File | SHA-256 |
| --- | --- |
| `factionWarfareAuthority.js` | `4437cc03679a9a2ee1cd127da6c293e815b05b10faeb564cda6e8b284b1fc5a2` |
| `fwCharacterEnlistmentMgrService.js` | `9fb0cb6b0730035848c35b1c3e8f24a372fe4e82e0ca021d314c8fb937d9eaba` |
| `fwOpenModel.js` | `d5704b0888b45c25b8fe45daa5e19309a1bc71743c7ed7f12126b8fc7f2c083a` |
| `fwOpenRuntime.js` | `6ff64488da2110606f46bd989c372e6b9fdd65f0d75d3ee1aa947e39a0d8cd6c` |
| `fwOpenRewards.js` | `87843f86fef1fd3daa8750fae887ccb675555b6712e082a050f13bb898684dbd` |
| `fwQueryPayloads.js` | `fb504cd7b1639f48a997f6d0e657513347d8e738d85e37176704241b4c4ee1ea` |

### Pinned client and catalog

Inert Python 2 bytecode inspection used `E:\EdenDev\3662168\tq\code.ccp`, SHA-256
`238ef5811fcecd77db8ecdb8f143f0e74bbd337d225a2582ed25132def900d8c`, with **xdis 6.3.0**.
No client code was imported or executed. This is the prepared archive; the historical route audit records an earlier
archive hash and stale handler candidates. Its CSV is a discovery aid, not current support evidence. Disassembly is
retained locally in ignored `artifacts/faction-warfare-research/client-fw-bytecode.txt`.

| Client source | Confirmed contract or constant |
| --- | --- |
| `ui/services/fwEnlistmentSvc.pyj` | `GetMyEnlistment()` unpacks `(effectiveFactionID, directFactionID, corpFactionID)`. `OnEnlistmentStateUpdated` takes those three values. Create/remove, corporation allowlist and cooldown use separate RPCs. |
| `factionwarfare/client/enlistmentChecks.pyj` | UI checks standings, allowed factions, station eligibility, existing membership, pending corporation enlistment and cooldown. Enforce independently on the server. |
| `factionwarfare/const.pyj` | `FL`, `SL`, `BL`; LP multipliers **1.5, 1.0, 0.01** respectively; VP threshold **75,000**, maximum buffer **500**, enrollment cooldown **86,400 seconds**. This does not establish server rounding or hub-flip timing. |
| `factionwarfare/occupationState.pyj` | Constructor order: occupier, adjacency, two potential empire occupiers, pirate faction, date taken. Non-null occupier/pirate IDs and exactly two distinct potential occupiers are required. |
| `factionwarfare/victoryPointState.pyj` | Constructor `(vpScore, vpThreshold)`; score nonnegative, threshold positive; vulnerable when score is **greater than or equal to** threshold. |
| `ui/services/fwWarzoneSvc.pyj`, `fwVictoryPointSvc.pyj` | Local state includes system identity: late updates must not replace another current system. Occupation reads have cached/uncached global variants. |
| `spacecomponents/client/components/fwCapturePoint.pyj` | Native bracket consumes `component_fwCapturePoint_factionInfo` as `(attackerID, occupierID)` and `component_fwCapturePoint_nextPointTime`. It evaluates catalog capture/blocking type lists and ship `warFactionID`; chat messages alone cannot supply native capture UI. |
| `factionwarfare/dockableChecks.pyj` | Dockable-structure usage check permits Frontline state, restricts hostile occupation outside it, and has a capsule exemption helper. Trace station, structure, tether and service callers separately. |
| `ui/services/facWarSvc.pyj` | Separate character/corporation/alliance join, leave and withdrawal flows; NPC-militia joining can change corporation and employment history. |
| `factionwarfare/activity/client/requestMessenger.pyj`, `noticeMessenger.pyj` | Advantage uses public-gateway `eve_public.faction.activity.GetSolarSystemScoresRequest/Response`, `SolarSystemScoresNotice` and `ScoreContributionNotice`, not only Blue RPCs. |

Read-only catalog inspection found `client/warzonespositiondata` with two records containing **103 and 149 map entries**.
They include presentation/visibility data, not verified capturable-system counts or initial occupation seeds.
`client/spacecomponentsbytype` contains **18** entries with `fwCapturePoint`, including 30 km radii, tick durations,
type-list IDs and point values. Use actual component profiles instead of one 15-minute timer for every site. Dungeon
layouts, objective totals, NPC waves and gate restrictions still need auditing.

Catalog dataset hashes: `client/warzonespositiondata`
`25dafbbc0158f4e7df4bac990e531f3780731c6d55f5e6adf4f72c38a08c3aa8`;
`client/spacecomponentsbytype` `c69848594e6846f98d2345f449a78d1d0ab654f2bf69cc48a5ea07c76755e5e9`.

Consulted [Carbon Blue Marshal.cpp](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp)
at revision `fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae` for explicit primitive/container serialization boundaries.
This plan changes no codec, transport or Destiny implementation. Reuse named Blue adapters; inspect the relevant
Carbon Destiny revision if an implementation milestone changes movement or Destiny behavior.

## Eden gap map

| Area | Existing owner to extend | Missing work |
| --- | --- | --- |
| Membership/identity | `CorporationMembershipHandler`, `CorporationInformationHandler`, `CharacterSheetHandler`, `PilotSessionWire`, `CharacterWire` | Enlistment/rank/affiliation replies are empty; session fields omit `warfactionid`, public profiles publish no militia. Add authoritative affiliation to login, changes, public profiles and space contacts. |
| Territory/maps | `MapStartupHandler`, `DockedCompatibilityHandler`, Maps/Exploration handlers | Empty occupation maps and Agency FW dictionaries. Add roster, occupation, VP, adjacency, HQ and shared sites; move existing stubs to one feature owner. |
| Combat/security | `CrimewatchRules`, `CrimewatchPersistence.Resident`, `ConcordPersistence.Resident`, NPC combat and interaction history | Aggression inputs lack militia hostility. Legal enemy aggression must reach safety, CONCORD/sentries, security loss, assistance and ship/pod handling consistently. Faction navy is a separate high-sec response. |
| Sites/gates | `CombatAnomalyStore`, `PilotProbeScanningStore`, mission/DED gates, flight contacts | Discovery/combat/gate pieces exist; shared FW lifecycle, objective timers, capture components and admission do not. Ordinary FW capture must not use private mission ownership. |
| Rewards | `SavedLoyaltyBalance`, `PilotLoyaltyStore`, corporation LP wallets/tax, mission settlement | Militia stores exist. Add transactional earning, rounding/tax policy, standings/ranks, kill eligibility and once-only receipts. Wallet readers are not reward commands. |
| Organizations/social | resident corporations/alliances, fleets, chat | Inherited faction, delayed/cancellable transitions and membership hooks. Militia fleet scope is rejected; chat presence hardcodes faction zero. |
| Durability/scheduling | `WorldActionScope`, `LiveWorld`, `Runtime/Economy`, detached checkpoints, `Server/Maintenance` | Saved rows, hydration/projection/cascades, indexes, coherent captures and deadline reconciliation through existing owners. |
| Gateway | `GatewayRequests`, `Server/Gateway` | Advantage request/notice handlers are absent. Audit dashboard statistics/leaderboards too; legacy RPC inventory cannot establish complete protocol coverage. |

Native dispatch inventory to complete during M1, preserving selected-pilot gating and argument validation:

- `fwCharacterEnlistmentMgr`: `GetMyEnlistment`, `GetMyDirectEnlistmentCooldownTimestamp`,
  `CreateMyDirectEnlistment`, `RemoveMyDirectEnlistment`, `GetCorpAllowedEnlistmentFactions`,
  `SetMyCorpAllowedEnlistmentFactions`.
- `fwWarzoneSolarsystem`: `GetAllWarzones`, `GetHQSystemIDs`, `GetAllWarzonesOccupationStates`,
  `GetAllWarzonesOccupationStatesUncached`, `GetLocalOccupationState`; `fwVictoryPointMgr`:
  `GetAllVictoryPointStates`, `GetLocalVictoryPointState`.
- `facWarMgr`: membership/status/rank reads, character/corporation/alliance join/leave/withdraw flows,
  `GetStats_FactionInfo`, `GetStats_Militia`, `GetStats_Personal`, `GetStats_Corp`, `GetStats_Alliance` and current
  zone-information consumers. Trace local facwar wrappers to actual remote calls before registering names.
- `dungeonInstanceCacheMgr.GetFactionWarfareInstances` and `GetFactionWarfareBattlefieldInstances`, map consumers,
  native affiliation/capture slim items and public-gateway Advantage requests/notices. Preserve tuple/list forms,
  None versus zero, numeric widths, timestamp representation and cache invalidation in packet-level assertions.

## State and ownership design

Use feature folders `Eden.Gameplay.FactionWarfare`, `Eden.Data.World.FactionWarfare`, `Eden.World.FactionWarfare` and
`Eden.Protocol.FactionWarfare`. Catalog payloads remain in `Eden.Data.Catalog`.

- Gameplay: immutable IDs, affiliation, enrollment inputs/decisions, hostility, occupation, adjacency, participants,
  capture state and reward breakdown. Narrow `IFactionWarfare` command/read contracts; no Blue, protobuf, EF Core or DI.
- Saved state: direct membership/cooldown, organization membership/pending transitions, per-faction rank history,
  system occupation/VP/Advantage, hub state, site/objective progress/NPC wave state, replacement deadlines and completion
  identities. VP alone cannot represent a destroyed hub awaiting transfer. Reuse existing LP/standing entities.
- Resident commands: serialize with existing world/system owners. Coordinate global affiliation and adjacency changes
  across affected systems in stable order. Shared immutable directories need explicit versions, not full-world scans
  on each observer read or shot.
- Completion: advance progress and snapshot eligible participants at one authoritative time. Commit terminal site
  state, VP, LP/tax, standings/ranks and unique completion identity in one resident transaction where possible.
  Notifications follow commit; deferred effects need durable idempotent pending records. Death callbacks, reconnects
  and retries cannot award twice. Keep bounded aggregates rather than scanning complete receipt/history tables per tick.
- Persistence: integrate checkpoint generation capture and detached projection. Preserve rollback, failed-save retry
  and prior immutable snapshots. All ordinary commands, reads, combat and timers require the resident world. Add no
  SQLite gameplay implementation or offline SQL fallback. Migrations, hydration and checkpoint projection remain
  persistence plumbing, with reopen coverage using resident worlds.
- Time: use the injected clock and saved deadlines. Do not earn capture progress during downtime, absent scenes,
  disconnection, warp or the interval before arrival/unblocking. Eligibility requires a real piloted hull, current
  membership and range; session claims cannot grant it. Reconcile expired deadlines once on startup and before relevant
  commands without creating another simulation loop in Protocol.

## Ordered milestones

Validate, document, commit and push each milestone, then verify its remote commit before starting the next.
M1 precedes all gameplay; M2 precedes M3; M3 precedes M4; M5 extends M2; M6 builds on M3/M4; M7 consumes completed
membership, rewards and site boundaries.

| Milestone | Deliverable and completion gate |
| --- | --- |
| **1. Contracts, catalog and resident foundation** | Verify roster/seed policy, dungeon/gate/component profiles and native read shapes. Add typed state, migrations and checkpoints. Implement real read-only warzone/HQ, occupation/VP and Agency views, plus neutral Advantage reads needed for dashboard startup. Preserve saved worlds and non-warzone None forms. Gate: seed audit, malformed-call rejection, packet shapes, rollback/capture/reopen and warmed zero-SQL/zero-fitting reads. No enrollment enabled yet. |
| **2. Individual enrollment and combat consequences** | Direct enlist/retire, NPC-militia corporation join/leave and employment history, corporation allowlists and saved cooldown. Enforce valid empire, raw standings, location, pending membership and authority. Publish session/profile/space affiliation and enrollment events. Add legal militia targets, safety/security/CONCORD/sentry consistency, navy response and build-specific docking/service checks. Gate: four-faction matrix, opposing direct enlistees in one corporation, pods/assistance, reconnect/logout, exact cooldown boundary, rejected unchanged state, navy persistence and occupation-dependent docking. Enrollment must not imply combat protection Eden does not enforce. |
| **3. Shared complexes and rewards** | Verified Scout/Small/Medium/Large/Open families and allowed variants for all four militias, with catalog type lists and audited templates. Scanner/Agency/beacons, gates, neutral suspect/safety checks, NPC waves, capture reversal/contesting and native bracket state. Settle offensive/defensive completions, VP and militia LP/tax atomically with reward-capacity and stable/vulnerable rules. Gate: data-driven admission, range/timer boundaries, neutrals/cloak/warp/pods, multiple sessions, late arrivals, blockers, duplicate settlement, replacement and checkpoint/reopen. Track every supported variant in a coverage manifest. |
| **4. Hubs and territorial conquest** | Empire FW hubs, vulnerability/attacker-gated damage, contributions/rewards, defensive recovery and the 500 VP buffer. Resolve immediate versus scheduled occupation transfer, replacement and restart. Recompute affected adjacency; update maps, access and versions coherently. Gate: stable → contested → vulnerable → captured → new occupation, defense across threshold, simultaneous final blows/captures, stale callbacks and restart at each stage. Do not use nullsec Sovereignty Hub/Entosis ownership. This completes the first individual-play territorial loop. |
| **5. Corporation/alliance enrollment and social integration** | Authorized join/leave/withdraw/cancel, persisted delayed transitions, inherited membership and changed-member validation. Hook recruitment, organization joins/departures, executor changes and dissolution. Verify released negative-standing warning/removal rules, then implement deadlines/CEO consequences. Add militia fleet adverts, chat access/presence and organization affiliation. Gate: unauthorized callers, pending restart, conflicting direct membership, offline members, alliance changes and immediate identity invalidation. |
| **6. Advantage and battlefields** | Supply caches/depots, rendezvous, Operation Center hacking/loot, propaganda/listening deployables and battlefield objectives. Add evidenced Advantage floors/caps/contributions, VP effects, topology dependencies and decay. Extend protobuf requests/notices and objective components. Gate: inventory/skill rejection, hacking eligibility, NPC interference, concurrent objectives, competing contributions, caps, expired timers/reopen and shared gateway views without personal-data leakage. |
| **7. Missions, progression and economy completion** | Verified militia missions, destinations/ship restrictions, eligibility, NPC reward/standing exceptions and beacons. Ranks from militia standings, once-only promotion history and verified derived-standing policy. PvP LP from actual destruction/value and valid participation, rejecting duplicate/self/friendly abuse. Complete actual personal/corp/alliance/militia statistics, required leaderboards, LP tax/journals and supported store rewards. Gate: no mission VP, lifecycle/reopen, demotion/rejoin, once-only promotion bonuses, kill reward conservation/rounding and no fabricated global statistics or unsupported blueprint/SKIN awards. |

### Evidence gates and recommended policies

1. **Roster/occupation (M1):** map-position data is insufficient. Build a source-backed manifest of capturable systems,
   original faction, warzone and HQ exclusions. Recommended new-world policy: deterministic original empire ownership,
   zero contested progress, no live-TQ dependency. Label this as Eden initialization, preserve saved occupations on
   catalog updates and version/review roster changes.
2. **Sites/rewards (M1/M3):** record capture/blocking lists, tick duration, objective total, hull/equipment restrictions,
   NPC types/waves, VP, base LP, participant scaling and respawn for each family. Missing server-authored details require
   documented Eden policies, not invented CCP precision. LP multipliers/VP constants above are resolved; base payout
   tables, rounding, current defensive rewards and vulnerable-system rewards still require reconciliation.
3. **Hub/access (M2/M4):** corroborate hub transfer timing and trace dockable-check callers. Establish station versus
   Upwell behavior, Frontline exceptions, capsules, services and tether separately. Document an explicit timing policy
   if public/client evidence cannot establish the server-only transition.
4. **Membership/standings (M2/M5/M7):** distinguish solo entry, inherited-member thresholds, corporation averages,
   application delays, warning/removal and actual 2026 releases. Resolve conflicting precedence; direct enrollment
   cannot silently override a hostile corporation/alliance. Plan inherited membership in M1 even though M5 enables it.
5. **Advanced formulas (M6/M7):** establish Advantage arithmetic and PvP LP value inputs before enabling awards.
   Unavailable server-only formulas require explicit policy and deterministic tests. Keep removed warzone tiers and
   LP-donation upgrades disabled unless new pinned evidence establishes a current consumer.

## Automated validation strategy

New coverage belongs in `tests/Eden.Protocol.Tests/FactionWarfare/`. Begin with an exact method filter through
`tools/Test-Feature.ps1 -Filter`, then combine affected feature classes and immediate dependencies. Planned classes:
`FactionWarfareRulesTests`, `FactionWarfareCatalogTests`, `EnlistmentTests`, `FactionWarfareProtocolTests`,
`FactionWarfareCaptureTests`, `FactionWarfareConquestTests`, `FactionWarfareCheckpointTests`,
`FactionWarfareOrganizationTests` and `FactionWarfareGatewayTests`. These tests do not exist yet.

Use resident worlds, `TestClock` and simulated sessions. Assert exact replies, notification ordering, failure state,
reward conservation and reopened state. Existing dependencies include `CrimewatchTests`, `ConcordTests`,
`MissionGateAccessTests`, `AgencyTests`, `MapStartupTests`, `LoyaltyStoreTests`, `CorporationLoyaltyTests`,
`DetachedCheckpointTests` and `IncrementalCheckpointTests`; add corporation/alliance, standings and flight filters as
their owners change. New FW behavior must not require SQLite-only fixtures.

Measure warmed full session frames for owners/observers, empty/populated systems, simultaneous captures, dense contacts
and topology changes. Assert SQL command/connection and fitting counts alongside outcomes, allocation growth and
snapshot reuse. Measure detached checkpoint capture/projection/writes separately. No history-sized work per shot or
full-warzone rebuild per observer tick.

After stable code changes, follow the [root build order](../../README.md#build-and-run): tool restore, solution
restore/build, affected .NET filters, both Python policy suites and formatting verification. Stop and investigate the
first failure. Full .NET tests require an explicitly requested regression check. Record filters, counts, elapsed time
and revision limitations here at every implementation milestone.

## Planning milestone validation

**Automated validation.** Documentation checks passed: UTF-8/final newlines/trailing whitespace for all three changed
documents, new local links and heading anchors, both index links, all six recorded evejs source hashes and Git
whitespace verification. The initial combined check took **0.28 seconds**; final verification is recorded with the
planning commit. Research used static bytecode inspection, read-only catalog queries and source inspection.
No implementation, gameplay test run or full .NET regression run is claimed; .NET build/format checks are not
applicable to this documentation-only change.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** None for this planning change. Later playable milestones should reserve at most three
short checks for native dashboard rendering, capture-bracket behavior and combat/docking presentation.
