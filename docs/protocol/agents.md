# Agent missions

[Protocol index](../protocol.md) / [Documentation index](../README.md)

## Multi-room missions and acceleration gates — November 9, 2026

Added saved, independently advancing mission rooms and native acceleration-gate travel. This supersedes the single-room admission boundary in the original mission record below. The supported pool now contains nine combat definitions, four courier definitions and four delivery storylines. Existing single-room missions retain their saved identities, NPCs, progress and reward quotes.

### Content and gameplay

| Added content | Level | Mission / dungeon | Rooms |
| --- | --- | --- | --- |
| The Guristas Spies | 1 | 875 / 213 | Empty entrance 2711, then combat room 2710 |
| The Assault, Guristas | 4 | 2537 / 888 | Empty lobby 17283, combat rooms 17284 and 17285 |

Geometry, gate types/radii, room entry beacons and NPC combat profiles come from build 3662168. The catalog connections identify only their source objects; `missions-3662168.json` therefore binds each gate explicitly to a destination room and its catalog entry beacon. Room ordering/destinations are authored bindings, not a recovered destination field. Eden places consecutive rooms 50,000 km apart on separate grids, preserving local geometry relative to each entry beacon. This spacing and axis are Eden placement policy; catalog editor coordinates and gate orientation are not presented as exact live topology. Gate objects with NPC anchor metadata remain visible; their encounter roster is spawned separately.

The [Guristas Spies report](https://eve-survival.org/?wakka=GuristaSpies1), edited July 14, 2012, supplies eight frigates in its combat pocket. [The Assault report](https://eve-survival.org/wikka.php?wakka=Assault4gu), edited October 16, 2017, supplies its two combat rosters and reinforcements. The March 1, 2018 04:27 UTC completion clarification on that report supersedes its older group-three blitz advice: Eden requires the final room's initial enemies and its two reinforcement waves. The first combat room is optional. Variable reinforcement quantities use the report's upper bounds; a highest-bounty candidate is fixed as the trigger and the remaining alternatives exclude it. These deterministic choices are Eden reconstruction policy. The first room has 13 initial enemies and eight reinforcements; the final room has 24 initial enemies, eight reinforcements and two final cruisers. Trigger deaths advance only their own room, including when a later room has already started.

The [EVE University acceleration-gate reference](https://wiki.eveuniversity.org/Acceleration_gate) supports the 2,500 m activation range, disruption rejection and ship/kill locks. Eden measures the range between surfaces, using the catalog gate radius and current ship radius. The admitted gates have no key or narrower ship-list requirement; Eden's default admission policy rejects capsules and capital ship groups. A typed room-clear lock exists for future authored bindings. Catalog gates with additional ship/race/fitting restrictions reject admission until implemented. Key consumption, keyed missions, restricted ship lists, branching content and structure-controlled reinforcement suppression remain unsupported. The Assault's optional structure/loot interactions, exact group aggro scripts and native gate orientation are not reconstructed.

The [deadspace reference](https://wiki.eveuniversity.org/Deadspace) supports entry redirection and blocked internal warps. All ordinary point, object, bookmark and fleet warp commands share the flight-owner check: an external target inside an active mission pocket redirects to its entrance, and intra-complex warp attempts reject. Warp-out remains available. Pocket boundaries use the existing 250 km contact radius; this is Eden's finite-grid approximation. Gates use the existing aligned warp course and native warp effects rather than a teleport. Positive disruption also rejects a stabilized ship's gate activation and cancels a gate's alignment. Normal warp-core behavior remains owned by the existing tackle rules.

World version 124 adds `WorldMissionRoom`, keyed by mission and catalog room ID. Arrival starts only that room once; waves and killed members survive reconnects, checkpoints and restart. Legacy single-room progress falls back to its existing `WorldMission.Wave` without recreating NPCs. Completed objectives retain gates, scenery and deadspace access until mission turn-in, quit or expiry; closing the mission removes its scenery and retires remaining mission NPCs through the existing owner. Wrecks keep their normal lifecycle. Mission-site caches depend on both mission and room rows, preserving warm reads without SQL or fitting work.

### Native protocol evidence

Inert disassembly of build-3662168 `code.ccp`, SHA-256 **37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368**, confirmed `eve/client/script/ui/services/menuSvcExtras/movementFunctions.pyj`, `RealActivateAccelerationGate`, calls `keeper.ActivateAccelerationGate(itemID)`. `violateSafetyTimer=1` belongs to the enclosing local `sessionMgr.PerformSessionChange` call, not the remote method. Valid activation returns Python None and queues existing Destiny `WarpTo` and warp-effect commands. Repeated activation of the same active course acknowledges without restarting it; malformed arguments, keywords, missing/wrong-system/distant gates and unavailable ships reject.

Consulted [Carbon Destiny Ball.cpp at 114e89fa584f0e1a81eb0a679aabf41af8870072](https://github.com/carbonengine/destiny/blob/114e89fa584f0e1a81eb0a679aabf41af8870072/src/Ball.cpp), `IsWarpish`, `IsAligningForWarp`, `IsWarping` and `IsAlignedForWarp`, for alignment/effect-stamp semantics. Gate travel reuses Eden's existing implementation; no Destiny binary layout or Blue codec changed. No client was launched.

### Automated validation

Exact starting filter: `FullyQualifiedName=Eden.Protocol.Tests.Agents.MissionGateTests.GatedMissionKeepsEntranceEmptyAndStartsCombatOnlyInDestinationRoom`. It exposed the NPC-anchored gate exclusion; the corrected exact test passed. Further focused cases cover native scene publication and None/warp replies, repeated activation, wrong-system/distant/closed gates, capsule rejection, disruption before and during alignment with and without stabilization, independently advancing rooms, delayed earlier-room triggers, final-room completion, exactly-once rewards, checkpoint reopen, legacy world upgrade and warm read counters. Boundary tests verify that a stand-off landing inside a pocket redirects even when its requested anchor is outside. Development fixture/system/clock expectations were corrected. One overlapping development rebuild hit the already-running testhost's file lock and ran no tests; the final sequence below ran sequentially.

Final tool restore, solution restore and solution build passed with zero warnings/errors; solution build took **5.10 seconds**. The combined filter `FullyQualifiedName~Eden.Protocol.Tests.Agents|FullyQualifiedName~Eden.Protocol.Tests.Flight|FullyQualifiedName~Eden.Protocol.Tests.Fleets|FullyQualifiedName~Eden.Protocol.Tests.Bookmarks|FullyQualifiedName~TackleTests|FullyQualifiedName~NpcEwarTests|FullyQualifiedName~CombatAnomaly|FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~TypedCallResultTests` passed **475/475 tests in 152.79 seconds**, covering the final code. Both mission exporters reproduced their checked-in outputs exactly apart from platform newlines: **17 definitions, 185 types and 767 source records**. The Python catalog and formatting policy suites passed **8/8** and **9/9** cases in **0.757 seconds** combined. Formatting verification, the 140-column check, changed-file UTF-8/final-newline validation, documentation links and `git diff --check` passed. Policy plus formatting verification took **20.39 seconds**. No full .NET suite was requested or run.

### Live acceptance

Not performed; agent client testing is disabled. Native gate presentation, input and camera feel remain unverified.

### Optional maintainer checks

- Restart the updated server, accept The Guristas Spies (level 1), and activate the entrance gate; verify the gate model, warp camera and destination overview.
- In The Assault (level 4), traverse both gates and return after docking/reconnecting; verify completed rooms retain their progress and final-room completion allows one reward payment.

## Mission-tracker refresh compatibility — November 9, 2026

The maintainer supplied an accepted-mission conversation screenshot showing **"Invalid mission tracker request."** The earlier handler recognized only `GetMissionInfoItems(agentID)` while claiming the entire `missionTrackerMgr` service. Inert inspection of the pinned build-3662168 archive (SHA-256 **37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368**) established four native calls in `missions/client/missionObjectivesTracker.pyj`: `UpdateAllMissions(agentList)` takes one Python list, while `GetMissionInfoItems`, `GetAllMissionObjectives` and `IsInActiveDungeonID` each take one agent ID. The info-panel consumer enumerates all-objective replies, so None is not valid for that collection read.

A dedicated selected-pilot handler now owns those four calls. Valid batch refreshes return None; current legacy info returns None; all legacy objectives return an empty native list; active legacy dungeon membership returns native False. Every supplied ID must identify a catalog agent, and malformed argument shapes, unknown agents and unexpected keywords reject. Empty batch lists and empty keyword maps are accepted. Unknown methods remain unsupported. These calls do not create offers, mutate objectives, issue rewards or emit notifications.

Eden's ordinary mission sites do not register the legacy tracker's event-driven objective sequence or dungeon membership. This correction removes the missing-call errors and supplies the correct empty shapes; it does **not** implement that legacy progression language. Actual mission state, objective progress and combat bookmarks continue through the existing journal, `GetMissionObjectiveInfo` and native mission notifications. The [EVE University mission guide](https://wiki.eveuniversity.org/Missions) distinguishes the tracked completion indicator from the mission details. Consulted [Carbon Blue Marshal.cpp at af0d65b29f951260ea09140ca11adf5d40566997](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/Marshal.cpp) for tuple/list/None/boolean preservation; no codec changed.

### Automated validation

Exact development filter: `FullyQualifiedName=Eden.Protocol.Tests.Agents.MissionProtocolTests.LegacyTrackerRefreshAndReadsUseNativeShapesWithoutChangingMissionState`. Both combat and courier cases first failed against the previous handler on the native batch refresh. After correction, both passed in **8.29 seconds**. Coverage exercises refresh before an offer, repeated refresh/read cycles after acceptance, the empty batch, special-agent IDs without offers, None/list/boolean response types, malformed tuples/lists/agent IDs, unexpected keywords, unknown methods and unchanged saved mission state/notifications. Actual native objective replies remain available after tracker calls.

Final tool restore, solution restore and solution build passed with zero warnings/errors. The affected filter `FullyQualifiedName~Eden.Protocol.Tests.Agents|FullyQualifiedName~AgentDirectory|FullyQualifiedName~TypedCallResultTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting` passed **114/114 tests in 15.41 seconds**. Both Python policy suites passed (**8/8** catalog and **9/9** formatting cases in **1.15 seconds** combined). Formatting verification, the 140-column check and `git diff --check` passed; policy plus formatting verification took **25.87 seconds**. No full .NET suite was requested or run.

### Live acceptance

Not performed; agent client testing is disabled. The screenshot records the original tracker dialog, not acceptance of this correction.

### Optional maintainer checks

Restart the server with the updated build, reopen the accepted mission and toggle tracking; verify the tracker-request dialog no longer appears and mission details remain usable. Legacy tracked objective progression remains outside this compatibility correction.

## Blank combat conversation correction — November 9, 2026

The maintainer's follow-up screenshot shows an empty Ogyura Unao conversation with an offer already recorded. The earlier header correction removed a missing remote call but did not establish that the objective HTML could be built. Static inspection of the same pinned build-3662168 archive and an isolated automated invocation of `agentDialogueUtil.GetMissionObjectiveHTML` reproduced **`TypeError: list indices must be integers, not NoneType`** with Eden's combat reply. `_ProcessDungeonData` tests whether `shipRestrictions` is present, then uses its value as an integer list index. A present None is invalid.

Combat dungeon replies now omit unavailable optional `shipRestrictions`, `briefingMessage` and `ownerID` fields. Omitting the briefing override retains the native default objective text. They also omit dungeon `completionStatus` until completion: the native renderer interprets a present zero as a failed site and strikes out its text, whereas an unfinished site requires the field to be absent. The required `objectiveCompleted` remains None while unfinished and becomes one at completion; the top-level mission completion status is unchanged. No mission selection, rewards or persistence rules changed.

References remain the [EVE University mission guide](https://wiki.eveuniversity.org/Missions), the pinned archive SHA-256 **37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368**, and [Carbon Blue Marshal.cpp at af0d65b29f951260ea09140ca11adf5d40566997](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/Marshal.cpp). This correction changes the wire adapter, not the Blue codec.

### Automated validation

Exact regression filter: `FullyQualifiedName=Eden.Protocol.Tests.Agents.MissionProtocolTests.CombatObjectiveOmitsUnavailableNativeOptionalFieldsAndOnlyReportsSettledCompletion`. Both incomplete and complete cases failed against the previous reply because `shipRestrictions` was present; both pass after the correction. An isolated renderer probe evaluated actual round-tripped Eden objective replies for levels one through four, combat and courier, unfinished and finished: **16/16 produced HTML**, and unfinished views contained no failed/struck-out site marker. This used extracted renderer functions with stand-in catalog/localization/service lookups; no client module initialization, game startup, UI or interactive acceptance occurred. An initial diagnostic export lacked the standing needed for higher-level offers; adding fixture standing resolved that setup failure. Initial probe failures from incomplete stand-ins and an over-specific text assertion were corrected before the final result.

Final tool restore, solution restore and solution build passed with zero warnings/errors. The combined filter `FullyQualifiedName~Eden.Protocol.Tests.Agents|FullyQualifiedName~AgentDirectory|FullyQualifiedName~TypedCallResultTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting` passed **112/112 tests in 16.68 seconds**. The Python catalog and formatting policy suites passed **8/8** and **9/9** cases in **0.96 seconds** combined. Formatting verification, the 140-column check and `git diff --check` passed; policy plus formatting verification took **26.38 seconds**. No full .NET suite was requested or run.

### Live acceptance

Not performed; agent client testing is disabled. The screenshot establishes the remaining failure after the header correction; native rendering remains unverified in a running client.

### Optional maintainer checks

Restart the server with the updated build and reopen Ogyura Unao's offered mission. Verify the briefing, objective and action buttons appear, with the unfinished combat site shown without failed/struck-out text.

## Agent conversation header correction — November 9, 2026

The maintainer supplied an **Ogyura Unao Level 1** conversation screenshot showing "Invalid or unsupported agent mission request." Static inspection of the pinned build-3662168 archive (SHA-256 **37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368**) confirmed that ordinary agents use `AgentDialogueWindow`; its `GetBriefingHTML` calls `GetAgentHeader`, which calls the bound agent's previously missing zero-argument `GetAgentLocationWrap`. This call occurs before the window renders its briefing. `agentDialogueUtil.LocationWrapper` consumes a dictionary containing `typeID`, `locationID` and `solarsystemID` for a station location. Eden now returns those fields from the existing catalog station directory, independently of any mission offer. Extra arguments and agents without a supported station location reject.

The same window's `GetObjectiveHTML` directly indexes `missionCompleted`, `missionDeclined`, `missionQuit` and `missionCantReplay`. Conversation replies now include all four false defaults and set the applicable terminal event flag. This preserves the objective visibility contract after the header read succeeds. The new regression follows the native embedded binding, greeting, automatic request, location, and objective reads; verifies catalog station identity and no offer created by the header read; and rejects extraneous location arguments. Direct wire cases cover greeting, offer, acceptance, completion, decline and quit flags.

References: [EVE University missions](https://wiki.eveuniversity.org/Missions), reconciled with the pinned client's legacy conversation route; [Carbon Blue Marshal.cpp at af0d65b29f951260ea09140ca11adf5d40566997](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/Marshal.cpp), consulted for native dictionary/value preservation. No codec or gameplay selection rules changed.

### Automated validation

The exact regression filter is `FullyQualifiedName=Eden.Protocol.Tests.Agents.MissionProtocolTests.OrdinaryAgentConversationProvidesNativeLocationHeaderAndEveryObjectiveStatusFlag`. It first reproduced the missing handler failure. A subsequent run exposed an incorrect expected station type in the test; the expectation was corrected to fixture catalog type **52678**.

Final tool restore, solution restore and solution build passed with zero warnings/errors. The corrected exact regression passed in **14.49 seconds**; the combined filter `FullyQualifiedName~Eden.Protocol.Tests.Agents|FullyQualifiedName~AgentDirectory|FullyQualifiedName~TypedCallResultTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting` passed **110/110 tests in 14.56 seconds**. Both Python policy suites passed (**8/8** catalog and **9/9** formatting cases in **1.16 seconds** combined). Formatting verification, the 140-column check and `git diff --check` passed; policy plus formatting verification took **22.19 seconds**. No full .NET suite was requested or run.

### Live acceptance

Not performed; agent client testing is disabled. The supplied screenshot establishes the original failure, not acceptance of the correction.

### Optional maintainer checks

Restart the server with the updated build and reopen Ogyura Unao's conversation; verify the station header, briefing and objectives render.

## Ordinary agent missions — November 9, 2026

Implemented a first playable ordinary-agent cohort: saved offers, acceptance, objectives, completion, expiry, decline/defer/quit, ISK and LP payments, agent/corporation standings, and delivery storylines. This record supersedes the earlier empty mission-journal boundary; the historical [agent-directory record](characters.md#catalog-agent-directory-and-saved-agents) remains evidence for that earlier revision.

### Admitted content and provenance

The build-3662168 catalog contains mission text, cargo definitions and dungeon geometry, but not complete NPC group rosters or the general ordinary-agent offer-selection table. Eden therefore uses a small explicit offer pool. Templates match the agent's level and supported division; an agent does not offer combat against its own faction. Repeated requests retain the same saved offer. Closed offers advance a saved sequence through the pool. This is authored Eden selection policy, not recovered CCP offer selection.

| Content | Levels | Catalog mission IDs |
| --- | --- | --- |
| The Angel Cartel Spies | 1–4 | 896, 897, 890, 892 |
| Eliminate a Pirate Nuisance, Angel and Guristas variants | 1 | 916, 920 |
| The Blockade, Guristas variant | 1 | 2924 |
| Move The Goods | 1–4 | 2912–2915 |
| A Special Delivery, storyline | 1–4 | 2919–2922 |

Combat quantities, alternative NPC identities, reinforcement triggers and completion targets come from the dated [EVE-Survival mission reports](https://eve-survival.org/?wakka=MissionReports). `src/Eden.World/Agents/missions-3662168.json` records each report URL and edit date. `tools/Export-MissionCatalog.py` reproduces those numeric bindings against the pinned catalog; `tools/Export-MissionFixture.py` exports offline test inputs. The courier and storyline level assignments and reward weights are explicit Eden policy. Layouts, cargo types/quantities, names, briefings and NPC statistics remain catalog-derived.

The Angel level-four report's "Tribunis" spelling resolves to pinned **Gistatis Tribunus**. Its stasis tower resolves to the catalog Angel Stasis Tower. The Blockade report's generic Starbase Stasis Tower resolves to the Minmatar Stasis Tower placed in catalog dungeon 1938. Legacy web probabilities of 50/100 on destructible/overseer sentries convert to fractions; ordinary ship probabilities retain their existing representation. This is a catalog compatibility interpretation, covered by direct rule and real-profile tests.

Only single-room combat layouts are admitted. Staging rooms and acceleration gates are not silently skipped. Blood Raider/Guristas Spies and other gated encounters, level-five encounters, mining missions, career chains, research, COSMOS, epic arcs, faction warfare and other special-agent services remain unsupported. Level-five reward/access rules exist for future content; an empty supported pool rejects the request. Optional structure/container loot, report-specific proximity messages, exact spawn distributions and unspecified scripting are not reconstructed. Existing NPC wrecks, salvage, bounties and kill-related standing/security changes retain their normal shared owners.

### Agent access

Ordinary basic agents use pinned `eveCfg.CanUseAgent`: level one is available regardless of negative standings. Levels 2/3/4/5 require **1/3/5/7** effective standing with the agent, corporation or faction, and **each** effective standing must be strictly greater than **−2**. Comparisons use unrounded values. Connections applies to neutral/positive ordinary standings, Criminal Connections to neutral/positive pirate standings, and Diplomacy to negative standings. These bonuses are mutually exclusive and apply to the remaining distance to +10 at 4% per skill level. Pinned factions excluded from social skill bonuses remain excluded. Alpha skill limits come from the pilot race's pinned clone-grade record; level-four and level-five missions require Omega.

The server checks access when requesting and accepting an ordinary offer, including direct or forged native calls. Losing access after acceptance does not prevent returning an already completed mission. Special agents do not receive ordinary-agent access merely because they appear in the directory. Storyline agents require their actual saved invitation; their catalog level one does not replace the invitation's earned mission level.

### Reward policy

These are the user-agreed base completion rewards. A saved template difficulty weight selects a value inside the level band; repeated reads, reconnects and restarts cannot reroll the quote.

| Level | Base ISK | Base LP | Corporation gain | Storyline faction gain |
| --- | --- | --- | --- | --- |
| 1 | 100,000–300,000 | 500–1,500 | 0.05–0.1% | 1.5–2.0% |
| 2 | 400,000–600,000 | 2,000–2,500 | 0.1–0.3% | 3.0–4.0% |
| 3 | 1,200,000–1,800,000 | 3,000–4,000 | 0.2–0.6% | 5.0–6.5% |
| 4 | 4,000,000–6,000,000 | 6,000–11,000 | 0.8–1.5% | 7.5–9.3% |
| 5 | 10,000,000–20,000,000 | 12,000–25,000 | 2.0–3.5% | None |

Negotiation adds 5% ISK per effective skill level; the relevant division Connections skill adds 10% LP per level; Social adds 5% per level to positive standing gains. Quotes include these skills at offer creation and are retained on acceptance. Agent standing uses the same percentage as corporation standing for this initial cohort. A gain of fraction `g` changes raw standing `s` to `s + (10-s)*g`, capped at +10. Native standing reads remain raw; the client applies effective-standing display bonuses. Rewards commit with mission completion, cargo consumption and standing history. Duplicate/stale completion cannot pay again. LP belongs to the offering agent's corporation and can be spent through the existing LP store.

Regular missions do not grant faction standing. After 16 completions of the same level for the same faction, a reachable, unoccupied storyline agent receives a saved delivery offer at that level. Selection uses shortest gate distance, then stable agent ID; a high-security issuing agent selects a high-security storyline destination. Busy/no reachable candidates leave the earned counter pending. Storyline completion grants agent/corporation and direct faction standing, with no LP, following the [mission](https://wiki.eveuniversity.org/Missions) and [standing](https://wiki.eveuniversity.org/NPC_standings) reference distinction. Derived faction changes await a sourced faction-relationship matrix.

Bounties, loot and salvage are additional to mission completion ISK. This cohort does not add time bonuses or corporation mission tax. Offers and accepted missions each have a saved seven-day deadline. One free decline per agent is available every four hours; a second decline inside that interval is explicitly rejected instead of inventing an unsourced penalty. Quitting and expiry close the mission without rewards. Negative mission-standing penalties remain unimplemented; the access checks and positive reward rules above are implemented.

### Native protocol and persistence

Inert inspection of build **3662168**, `code.ccp` SHA-256 **37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368**, covered `eveMoniker.GetAgent`, `Agents`, `OldMissionController`, native objective/reward builders, `agentDialogueUtil`, `ActiveMissionController`, the journal, mission job provider, movement bookmark functions, `standingUtil` and `eveCfg.CanUseAgent`. No client code was executed and no client was launched. Consulted unchanged [Carbon Blue Marshal.cpp at af0d65b29f951260ea09140ca11adf5d40566997](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/Marshal.cpp) for native tuple/list/instance preservation; no codec or Destiny format changed.

Agent monikers bind `agentMgr` with the agent character ID and retain a distinct pilot-lifetime reference per agent. Conversations return `((message, actions), lastActionInfo)`; message pairs use catalog content IDs. Action IDs include the saved offer identity, rejecting stale accept/complete calls. Native objective, briefing, mission journal, standing-preview and keyword replies use named adapters. The journal's first list contains ten-field mission tuples; the second research list remains empty. Combat bookmarks use `WarpToLocation` through ordinary personal/fleet flight operations. Docked, foreign, unaccepted and wrong-system mission warps reject. Native objective cards receive actual progress; the separate legacy tracker event language is not fabricated.

The maintainer supplied a screenshot of an empty station Agents tab during implementation. Inert inspection of `dockedUI/agents.pyj` and `Agents.GetAgentTimers` established that the panel calls each bound agent's `GetReplayTimestamp(characterID)` before rendering any rows. That missing read is now handled with None for the initial cohort, which has no replay lock; foreign-character requests reject. A focused regression exercises the exact embedded bind/timer call for ordinary, career and storyline directory entries without creating offers. The screenshot establishes the original presentation failure, not acceptance of the correction.

World schema **123** adds saved missions, mission NPC membership, per-faction/level completion counters and per-agent decline timestamps. Migration preserves existing pilots, inventory and balances and is idempotent. Acceptance issues real courier cargo at the agent station. Completion requires docking at the delivery station and consuming personally owned cargo from the active ship's cargo hold or personal destination hangar; unrelated owners, containers and contract escrow do not satisfy the objective. Combat arrival spawns once for every observer, and fatal shared NPC settlement records wave/trigger progress atomically. Finished/abandoned missions retire surviving mission NPCs. Typed mission-scene snapshots invalidate on committed mission changes and expire at the exact saved deadline; warmed empty and populated reads perform no SQL or fitting calculation. Checkpoints preserve mission/NPC identity, losses, deadlines and quotes.

### Automated validation

Focused mission development tests cover agreed reward boundaries and skills, ordinary access thresholds and each negative cutoff, Alpha skill admission, rejection/unchanged-state, concurrent request/accept, cargo delivery and exactly-once rewards, forced rollback, expiry/decline boundaries, storylines, native bindings/replies/notifications/warp, all admitted combat levels and NPC alternatives, sentry web compatibility, trigger overlap/final-target completion, resident cache counts, rollback and checkpoint/reopen.

Required root validation passed tool restore, solution restore, solution build with zero warnings/errors and **317 affected .NET tests in 107.18 seconds**, followed by the catalog policy suite **8/8** and formatting policy suite **9/9**. After the station-panel timer correction and NPC-alternative audit, the final restore/build and **103 mission/directory/binding/hosting tests passed in 25.02 seconds**, including **47 mission-specific cases**. The final Python suites passed again in **2.32 seconds**; formatting and 140-column checks and `git diff --check` passed. Both numeric content and offline fixtures reproduced against the installed read-only catalog. No full .NET suite was requested or run.

Broad filter: `FullyQualifiedName~Eden.Protocol.Tests.Agents|FullyQualifiedName~AgentDirectory|FullyQualifiedName~NpcEwar|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~NpcEncounterTests|FullyQualifiedName~NpcSceneUpdateTests|FullyQualifiedName~CombatAnomaly|FullyQualifiedName~IdleAnomalyFrameTests|FullyQualifiedName~TypedCallResultTests|FullyQualifiedName~LoyaltyStoreTests|FullyQualifiedName~LoyaltyWalletServiceTests|FullyQualifiedName~CharacterInteractionHistoryTests|FullyQualifiedName~InteractionHistoryPersistenceTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~WorldInitializationTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting`.

Final follow-up filter: `FullyQualifiedName~Eden.Protocol.Tests.Agents|FullyQualifiedName~AgentDirectory|FullyQualifiedName~TypedCallResultTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting`.

### Live acceptance

Not performed; agent client testing is disabled. Native window rendering, objective refresh and flight presentation remain unverified.

### Optional maintainer checks

1. Restart the server with the updated build. On a developer pilot use `/tr me 60003754` to dock at **Perimeter II - Moon 1 - Caldari Navy Assembly Plant**; open level-one Security agent **Ogyura Unao (3012018)** from the station Agents list. Level-one access has no standing prerequisite.
2. Accept and complete a combat mission; verify the objective location, NPCs, completion, ISK/LP and standing history in native windows.
3. Complete a courier mission through real cargo movement and verify the offer/progress survives reconnecting.
