# Agent missions

[Protocol index](../protocol.md) / [Documentation index](../README.md)

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
