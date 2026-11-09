# Agent missions

[Protocol index](../protocol.md) / [Documentation index](../README.md)

## Opportunities mission details and three-dot menu — November 9, 2026

The Opportunities mission details page reported **Unexpected mission keyword arguments** because its objective read uses a named argument that the legacy conversation does not send. Inert inspection of the staged build-3662168 `code.ccp` (current staged archive SHA-256 **051a2f027309aa9e806273200c9f5e969cad8675f49ef97313ff57a6b4549bb9**) confirmed `jobboard/client/features/agent_missions/job.pyj`, `AgentMissionJob._get_objective_info`, calls the bound agent's `GetMissionObjectiveInfo(ignoreLocateCheck=True)`. `FleetMembersAgentMissionJob._get_objective_info` also supplies `charID`. Earlier records retain their historical archive hashes.

The bound objective read now accepts a boolean `ignoreLocateCheck` and optional `charID` equal to the selected pilot or None. Eden already reads its own saved objectives without an agent-location check, so the flag does not change gameplay authorization. Foreign pilot IDs, malformed values, unknown keywords, positional arguments and those keywords on other methods still reject. Reading fleet members' missions remains unsupported. Existing no-keyword conversations, empty objectives before an offer, combat/delivery objectives and committed mission state retain their behavior. No save, reward, Blue codec or Destiny changes are involved.

The tracked mission's **three-dot menu** separately reported **Invalid or unsupported agent mission request**. The same archive's `AgentMissionJob.get_menu` calls `evemissions/client/qa_tools.pyj`, `get_agent_mission_context_menu` / `_get_agent_context_menu`. For developer roles, building this menu immediately calls `Agents.IsCheatingWithAgent(agentID)` and `Agents.ShouldAlwaysAllowReplay(agentID)`; the local service forwards each as a zero-argument bound-agent RPC. Both now return native boolean **False**, matching the absence of Eden mission cheat/replay overrides. Known-agent validation still applies; arguments and service keywords still reject. No cheat mutation or administrative slash command is added.

The [EVE University mission guide](https://wiki.eveuniversity.org/Missions) describes Opportunities mission details and completion tracking. Consulted [Carbon Blue Marshal.cpp at af0d65b29f951260ea09140ca11adf5d40566997](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/Marshal.cpp) for native boolean/None/dictionary preservation; mission-specific arguments are established by the pinned client above. The supplied screenshot is evidence of the rejection dialog, not post-fix live acceptance.

### Automated validation

The exact regression filter `FullyQualifiedName=Eden.Protocol.Tests.Agents.MissionProtocolTests.OpportunitiesObjectiveReadsAcceptNativeKeywordsAndPreserveMissionState` reproduced the rejection in **4/4 cases** before the fix (build **8.48 seconds**, tests **8.88 seconds**), then passed **4/4** after the fix (build **5.02 seconds**, tests **13.92 seconds**). It covers offered/accepted combat and delivery missions, empty pre-offer reads, native keywords, byte-identical legacy replies and unchanged mission/notification state. Additional rejection coverage checks foreign pilots, malformed values, unknown keywords, invalid agents, positional arguments and other methods.

Before the three-dot-menu follow-up, tool restore, solution restore and solution build passed, with zero warnings/errors and a **4.98-second** build. That focused selection passed **70/70 in 46.76 seconds**:

`FullyQualifiedName~Eden.Protocol.Tests.Agents.MissionProtocolTests|FullyQualifiedName~Eden.Protocol.Tests.Agents.MissionPersistenceTests|FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.AgentDirectoryServiceTests|FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.TypedCallResultTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting`

The exact menu regression filter `FullyQualifiedName=Eden.Protocol.Tests.Agents.MissionProtocolTests.OpportunitiesThreeDotMenuReadsDisabledCheatsWithoutChangingMissions` reproduced both missing status calls in **4/4 cases** before the menu fix (build **7.41 seconds**, tests **16.49 seconds**), then passed **4/4** after the fix (build **4.14 seconds**, tests **22.86 seconds**). It checks no-offer, offered and accepted missions, repeated reads, native False replies, malformed arguments, keywords, invalid agents and unchanged mission/notification state.

Final validation of both fixes passed tool restore, solution restore and solution build (**1.78 seconds**, zero warnings/errors). The focused selection above passed **74/74 in 66.89 seconds**. Both Python policy suites passed (**8 catalog and 9 formatting tests**, **1.56 seconds** together). Formatting, line width, documentation links/anchors and `git diff --check` passed. No full .NET suite was requested or run.

### Live acceptance

Not performed; agent client testing is disabled. Native rendering and input remain unverified.

### Optional maintainer checks

- After updating and restarting the server, reopen the tracked mission in Opportunities and confirm its details load without the dialog.
- Open the tracked mission's three-dot menu and confirm it appears without the unsupported-request dialog.

## Supported mission content expansion — November 9, 2026

Expanded the ordinary mission pool from **20 to 311 definitions**: **50 combat encounters, 177 ordinary deliveries and 84 delivery storylines**. The [content guide](../missions.md) lists combat families, reconstruction choices and delivery policy. The reproducible [catalog audit](../data/mission-support-3662168.csv) accounts for every one of the **2,892** pinned mission records. Its **2,581** blocked records distinguish specialized lifecycles, chains, missing roster/trigger research, singleton cargo, differing deadlines and known scripting gaps. Missing content remains a blocker even when the engine could represent the eventual verified encounter; this is not a claim that all historical mechanically feasible encounters have been reconstructed.

Added all remaining Hidden Stash level-one/two faction variants, all ten Duo of Death level-three/four variants, the remaining Pirate Nuisance factions, Serpentis Spies level two, four verified Pirate Campers variants, eight Seek and Destroy variants plus two supplier-specific catalog twins, and the second Scarlet template sharing its reviewed dungeon. NPC names resolve against pinned catalog types; optional/required groups, exact objective cargo and explicit room/gate identities retain the existing gameplay systems. Two old Pirate Nuisance rosters were swapped relative to their dungeon factions; **916 now uses Guristas and 920 Angel**. Scarlet retains the previously documented final-boss reconstruction and early escape/implant exclusions.

Admitted every catalog-complete standalone stack delivery in the ordinary/basic and generic-storyline lifecycles. Cargo volume supplies explicit Eden level bands because the client does not publish level assignments: **≤320 m³ level 1, ≤1,000 level 2, ≤5,000 level 3, larger level 4**. Original eight delivery assignments remain unchanged. Native names and English briefings, exact quantities and supplier faction/corporation metadata are retained. The existing route/reward/deadline policies remain explicit Eden choices. Legacy offers retain priority so expansion does not reorder the original cohort. Faction/corporation restrictions now filter offer generation and saved-offer acceptance, with rejection before cargo/state changes.

The [EVE University distribution reports](https://wiki.eveuniversity.org/Distribution_mission_reports) and [agent guide](https://wiki.eveuniversity.org/Agent) describe the delivery, division and storyline boundaries. Each added encounter records its specific University URL and revision in the embedded manifest; full export inputs and reconciliation notes are linked from the content guide. Corpus generation uses read-only build-3662168 catalog records and has no runtime network dependency. No protocol packets or schema changed: the save version remains **127**. The native client and Carbon source evidence in the preceding implementation records still apply to these reused mechanisms; no new Destiny/Blue behavior is inferred.

### Automated validation

Initial exact filter `FullyQualifiedName=Eden.Protocol.Tests.Agents.MissionEncounterTests.EveryAuthoredNpcAlternativeHasACompletePinnedCombatProfile` passed **1/1 in 9.04 seconds**. The new manifest/faction exact filter passed **1/1 in 6.88 seconds** after correcting test imports and API names. The first combat matrix passed 36/38: its test expected every Scarlet wave to spawn immediately, and the offline fixture lacked the Blood battleship crystal loot dependency. The assertion now counts initial waves per room, and the exporter derives all ordinary loot enum dependencies. The corrected exact combat filter passed **38/38 in 37.01 seconds**.

The exact delivery filter `FullyQualifiedName=Eden.Protocol.Tests.Agents.MissionContentTests.EveryAdmittedDeliveryCanBeOfferedTransportedReopenedAndPaidExactlyOnce` passed **261/261 in 212.86 seconds**, covering every admitted ordinary delivery and generic storyline, saved cargo hand-in, reopen and duplicate-payment rejection. Storyline cases seed fifteen previous completions, then complete a real courier to cross the sixteen-completion threshold. The exact supplier-restriction filter passed **5/5 in 16.97 seconds**, including unchanged saved-offer state and cargo after rejection. These preliminary results precede integration of traffic change **df0aecc6** and the final validation below.

Integrated **df0aecc6** into the clean task checkout while preserving its traffic changes and these mission edits. Final tool restore, solution restore and solution build passed; the build took **10.53 seconds**, with zero warnings/errors. The affected selection passed **510/510 in 298.54 seconds**, including all **305 new content cases**, the existing mission/gate/container/structure/protocol coverage, NPC combat, wreck settlement and service registration:

`FullyQualifiedName~Eden.Protocol.Tests.Agents|FullyQualifiedName~NpcEncounterTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~Eden.Protocol.Tests.Loot.WreckTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~TypedCallResultTests`

The Python catalog policy, formatting policy and new mission admission suites passed **8/8, 9/9 and 5/5** in **3.11 seconds** for the commands. Formatting verification checked **1,387 files**, and the 140-column check and `git diff --check` passed. Both exporters reproduced the manifest, audit and fixture byte-for-byte: **311 definitions, 2,892 audited records, 553 types and 3,472 fixture source records**. Documentation links, final newlines and historical protocol records were verified. No full .NET suite was requested or run. Automated validation establishes this content milestone; live acceptance remains separate.

### Live acceptance

Not performed; agent client testing is disabled. Native presentation and gameplay feel remain unverified.

### Optional maintainer checks

- Accept an expanded Hidden Stash or Duo of Death offer, recover its objective cargo and turn it in.
- Cycle a distribution agent's offers and inspect an expanded delivery's name, briefing, cargo quantity and destination.

## Destructible mission structures and objectives — November 9, 2026

Authored mission structures are now real combat targets with catalog shield, armor, hull, resistances and signature radius. Their destruction can be required alongside enemy clearance, complete a designated objective directly, and release mission-owned recovery cargo. Added **The Hidden Stash (Angel Cartel), level 1**, mission **1226**, dungeon **314**. The ordinary pool now contains **twelve combat definitions**, four couriers and four delivery storylines. This supersedes earlier exclusions for authored destructible structures and destruction-released objective containers.

### Content and objective rules

The [EVE University report, revision 192015](https://wiki.eveuniversity.org/index.php?title=The_Hidden_Stash_(Angel_Cartel)_(Level_1)&oldid=192015), edited August 27, 2022 at 15:59, identifies a warehouse destruction/retrieval blitz, two groups of eight and four Gistii Hijackers, and **150 m³** of required cargo. The [EVE-Survival report](https://eve-survival.org/wikka.php?wakka=HiddenStash1an) is retained as the content source pointer. Pinned mission **1226** supplies **15 Small Sealed Cargo Containers**, type **16044**, **10 m³** each. Catalog geometry retains an empty gate lobby omitted from the report's single-pocket description: room **3399**, entry **66426**, gate **66425**, then room **3400**, entry **66471**.

Warehouse object **66472**, type **17787 (Starbase Storage Facility)**, is the explicit completion target and releases the required cargo. Objects **66431/66432/66433/66434** are optional destructible Cargo Rig, Pressure Silo, Power Generator and habitation-module structures. Their health comes from the pinned catalog. The warehouse's 600 shield, 500 armor and 700 hull are not substituted with ship health. The gate stays unlocked and defenders are optional for the documented warehouse blitz. NPC placement/alternative selection and shared acquisition behavior retain Eden's existing authored mission policy; exact report-specific group hold/aggro scripting and unspecified trade/ammunition loot are not reconstructed.

Typed objective rules combine enemy clearance with every required structure. Explicit completion targets can finish the combat requirement while optional structures and escorts remain alive. Clearing defenders alone cannot finish an outstanding structure requirement. Retrieval possession remains a separate requirement, recomputed from active Cargo Hold and the agent-station hangar, and turn-in consumes the exact cargo in the existing reward transaction. Killing an optional structure never invents objective cargo.

### Saved state and combat integration

World version **127** adds `WorldMissionStructure` membership, source-object identity, required/completion flags, destruction time and recovery-container emptiness, plus nullable NPC display names. The concurrently merged CONCORD feature keeps saved role **8** and schema version 126; structures use saved role **9** and upgrade saves to 127. Existing missions, inventory, quotes and NPCs retain their state; earlier NPC names remain catalog-derived. Structure targets use ordinary allocated global NPC identities recorded in their membership, avoiding a separate manually assigned NPC identity range. Acceptance creates their actor state and membership in one transaction; a failed acceptance leaves the offer and actor population unchanged.

The shared combat owner handles targeting, turret shots, physical missile impacts, manual drone attacks, health recharge, damage projection and checkpoints. A distinct `MissionStructure` role has no acquisition, retaliation, offensive EWAR, outgoing missiles, pursuit, regional respawn, pirate bounty, skill-point award or NPC-kill task credit. Catalog destruction-related standing/history handling remains with its existing owner. Structure bodies stay fixed in both resident and database-backed motion. The collision resolver preserves fixed input bodies while retaining the historical all-pairs response for moving actors; randomized swept-index comparisons now assert both contracts separately.

Fatal damage, saved destruction, objective progress and cargo creation commit together. Fatal retries, rollback, duplicate impacts, arrivals and reconnects cannot release another copy. Recovery cargo belongs to the mission pilot even when another pilot delivers the fatal shot. Structure actors never create ship wrecks or salvage. The dropped **Cargo Container type 23** uses the existing mission-container identity range and access/hold/capacity checks. It appears only after committed destruction, at the structure's location, and persists with the mission. Partial looting leaves the remainder; full looting saves an empty state and queues a native slim update. Completion, abandonment and exact expiry remove unclaimed cargo and retire surviving mission actors while preserving items already recovered.

Mission-site snapshots invalidate on mission structure membership/destruction/empty changes rather than every NPC coordinate or damage update. Authored mission losses are excluded from the regional population deadline, as anomalies already were, preventing a dead warehouse from forcing repeated population reloads after the ordinary respawn interval. Warm resident shots, observer reads and unchanged site reads have SQL, connection and fitting-calculation assertions.

### Native protocol and references

Inert inspection of build-3662168 `code.ccp`, SHA-256 **37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368**, covered `spaceObject/LargeCollidableStructure.pyj` (`Assemble`, `LoadModel`), `spaceObject/cargo.pyj`, `invControllers.BaseCelestialContainer.GetName` (ballpark slim names), and the previously recorded agent fetch/targeting routes. The structure renderer sets static rotation; its native entity slim still supplies catalog group/type, localized name, standings and damage state. Existing target-lock, weapon and drone calls own interaction; no new client RPC is introduced. A structure serializes as a local **rigid, massive ball**, without free-ball motion fields. Its health remains in the ballpark damage map, and committed destruction uses the existing exploding `RemoveBall` path. Recovery cans include personal loot rights and saved `isEmpty`; retained static-scene changes now publish `OnSlimItemChange`.

Consulted [Carbon Destiny `Thunkers.cpp`, revision 114e89fa584f0e1a81eb0a679aabf41af8870072](https://github.com/carbonengine/destiny/blob/114e89fa584f0e1a81eb0a679aabf41af8870072/src/Thunkers.cpp) for rigid-mode field omissions and [the same revision's `Ballpark.cpp`](https://github.com/carbonengine/destiny/blob/114e89fa584f0e1a81eb0a679aabf41af8870072/src/Ballpark.cpp) for `SetBallRigid`. Existing Carbon-backed collision response math is retained for moving participants. Source inspection and automated wire assertions do not establish native rendering or input acceptance.

Only explicitly bound large-collidable structures (group **319**) are admitted as damageable mission actors. Other scenery remains inert. This milestone does not add proximity/attack/threshold-triggered reinforcements, structure explosion area damage, repair-station support networks, optional randomized trade loot or every historical mission variant. Damsel/repair-station missions with those extra scripts are not silently admitted.

### Automated validation

Exact starting filter: `FullyQualifiedName=Eden.Protocol.Tests.Agents.MissionStructureTests.HiddenStashWarehouseDestructionReleasesPersistentCargoAndCompletesOnlyAfterRetrieval` passed both file-backed and resident cases in **11.41 seconds**. The new turret fixture initially lacked targeting attributes; after supplying its range, scan resolution and lock slots, the exact turret/rollback filter passed **2/2 in 8.71 seconds**. The structure class then passed **19/19 in 30.67 seconds**. After adding acceptance rollback and regional-deadline coverage, the combined structure/collision selection passed **44/44 in 23.73 seconds**. The initial formatting pass identified one long SQL definition; it was split without an exception.

Stable-code tool restore, solution restore and solution build passed; the build took **5.92 seconds**, with zero warnings/errors. The affected selection passed **876/876 in 339.67 seconds**, including **21 new structure cases**, and covered the shared turret, missile, drone, collision, scene, NPC population/AI, migration, wreck, anomaly and registration boundaries:

`FullyQualifiedName~Eden.Protocol.Tests.Agents|FullyQualifiedName~Eden.Protocol.Tests.Flight|FullyQualifiedName~LiveCombatObjectTests|FullyQualifiedName~LiveSimulationTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~NpcEncounterTests|FullyQualifiedName~NpcPopulationTests|FullyQualifiedName~NpcEwarTests|FullyQualifiedName~NpcMissileTests|FullyQualifiedName~NpcGuardTests|FullyQualifiedName~NpcSceneUpdateTests|FullyQualifiedName~NpcCatalogSharingTests|FullyQualifiedName~NpcSkillPointRewardTests|FullyQualifiedName~MissileCombatTests|FullyQualifiedName~CombatDroneTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~Eden.Protocol.Tests.Loot.WreckTests|FullyQualifiedName~CombatAnomaly|FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~TypedCallResultTests`

Both Python policy suites passed **8/8** and **9/9** in **1.68 seconds** for the policy commands. Formatting verification, the 140-column check and `git diff --check` passed. Both exporters reproduced checked-in data: **20 definitions, 243 types and 961 source records**. Documentation links, UTF-8/final newlines and preservation of historical mission records were verified. No full .NET suite was requested or run. The primary checkout contained unrelated in-progress changes; integration uses the clean task checkout so those files remain intact.

The feature branch result above covers **509af58**, before CONCORD **fb283f1e** reached `main`. Clean-checkout integration resolved the shared role enum and migration owner: published CONCORD role **8** remains unchanged, mission structures use **9**, both schema upgraders remain active, and the combined save version is **127**. The migration test now reopens both version-125 and version-126 saves and asserts the saved role values. Its initial integration build stopped at EF1003 on concatenated test PRAGMA text; a fixed-literal switch removed that issue. The exact migration filter passed **2/2 in 6.85 seconds**.

Post-integration tool/solution restores, solution build (**3.23 seconds**, zero warnings/errors), both Python policies and formatting verification passed. The combined integration selection passed **428/428 in 155.14 seconds**, including **22 structure cases**:

`FullyQualifiedName~Eden.Protocol.Tests.Agents|FullyQualifiedName~ConcordTests|FullyQualifiedName~CrimewatchTests|FullyQualifiedName~NpcCombatTests|FullyQualifiedName~NpcMissileTests|FullyQualifiedName~NpcEwarTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~TypedCallResultTests|FullyQualifiedName~PilotTurretTests|FullyQualifiedName~PilotMissileTests|FullyQualifiedName~PilotDroneTests`

### Live acceptance

Not performed; agent client testing is disabled. Native structure graphics, destruction effects, health bars and cargo interaction remain unverified.

### Optional maintainer checks

- Accept The Hidden Stash, use the entrance gate, lock and destroy the warehouse, recover the 150 m³ objective cargo (or make multiple trips), and turn it in at the agent's station.
- Partially damage a structure and reconnect; confirm its health and location, then destroy it and check the recovery can appears once and changes to empty after looting.

## Combat item retrieval and mission containers — November 9, 2026

Combat retrieval now combines saved combat progress with personally owned objective cargo. Added **The Spy Stash (Guristas), level 3**, mission **1770**, dungeon **558**: an empty entrance, two combat pockets, unlocked acceleration gates and a protected Officer's Quarters containing **five Classified Report - Station Defenses**, type **22222**, totaling **0.5 m³**. The ordinary pool now contains **eleven combat definitions**, four couriers and four delivery storylines. This supersedes earlier blanket exclusions for combat retrieval and mission containers; other bespoke mission scripts remain separate work.

### Content and persistence

The [EVE University report, revision 218641](https://wiki.eveuniversity.org/index.php?title=The_Spy_Stash_(Guristas_Pirates)&oldid=218641), edited September 7, 2024, supplies the selected roster and final-pocket blitz. The [EVE-Survival report](https://eve-survival.org/wikka.php?wakka=SpyStash3gu), edited February 22, 2017 at 13:34 UTC, independently identifies the reports and their quantity. The newer University count of three initial elite frigates takes precedence over the older count of four. Initial-pocket enemies and final sentries are optional; the five final ships are required. Random battleships and unspecified drug loot are excluded because their exact selection/probability is unavailable. Fixed placement and alternative selection retain Eden's existing reconstruction policy.

Pinned `client/missions/1770.killMission` supplies objective type **22222**, quantity **5**, and dungeon **558**. The catalog binds rooms **18410/18411/18412**, gates **810928/810930**, and container object **810942**, type **22223**, group **306 (Spawn Container)**, category **11 (Entity)**. The container's localized name is **Guristas Officer's Quarters**. Its entity category is admitted explicitly; other entity anchors are still excluded from scenery.

Acceptance creates container contents atomically in ordinary saved inventory. Container IDs use a dedicated mission-instance range and stable authored container slots, distinct from scenery, NPCs and wrecks. Reads, repeated arrivals and reconnects never seed or refill contents. Partial pickup preserves the source stack and creates a real cargo split; full pickup moves its existing identity. No world-schema migration is needed: saved mission membership and saved inventory already own all required state. Containers persist for the accepted mission rather than receiving wreck expiry or salvage behavior.

Eden explicitly protects the Officer's Quarters until the required combat objective is settled, following the report's final-ship blitz. This is an authored guard requirement, not a recovered general CCP container-lock script. Opening and taking revalidate acceptance, exact expiry, pilot ownership, solar system, saved active-ship position and the **2,500 m surface-distance** boundary. Native requests reject warp and session transitions. Transfers reuse the existing wreck transfer operation for quantity, item identity, destination-hold eligibility, capacity and transactional inventory changes; wreck authorization/lifetime/salvage remain with their existing owner. Empty containers remain openable without regenerating loot. Finishing, quitting or expiring removes unclaimed contents and invalidates old bindings; already recovered cargo remains ordinary owned inventory until turn-in or disposal.

Clearing the guards alone does not complete retrieval. Journal reads recompute possession from the pilot's current active-ship Cargo Hold or personal agent-station hangar. Cargo in another station, another ship, a nested container, specialized hold or another owner's inventory does not count. Losing or moving the reports removes completion eligibility. Turn-in requires docking at the agent's station and consumes exactly five reports across stable item-ID-ordered stacks in the same transaction as ISK, LP, standings and mission closure. Missing cargo, failed payment and repeated completion cannot pay or consume twice; extra reports survive. Shared fleet cargo/reward hand-in and destructible-container objectives remain unsupported.

### Native protocol and references

Inert inspection of build-3662168 `code.ccp`, SHA-256 **37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368**, confirmed `agentDialogueUtil._ProcessObjectiveEntry` unpacks **fetch** as `(dropoffOwnerID, dropoffLocation, cargo)`, with cargo `typeID`, `quantity`, total `volume` and `hasCargo`. Fetch objectives accompany a separate dungeon entry; courier transport objectives and acceptance gifts remain distinct. Briefing keywords include the objective type, quantity and agent-station destination. Completed combat and current cargo possession are represented separately, while completed mission history retains completion after consumption.

Native `invbroker.GetInventoryFromId` obtains a location-scoped container binding. `GetSelfInvItem`, `List`, and existing ship `Add`/`MultiAdd` paths expose and move the saved items, with normal `OnItemChange` notifications. The container slim carries its localized name and catalog entity-standing thresholds (**-11/11**): pinned `bracketVarious.GetIconColor` classifies this entity as an NPC object and consults its entity-standing component. Supplying these fields avoids the historical missing-threshold overview error path. These are automated protocol assertions, not native presentation acceptance.

Consulted [Carbon Destiny Ball.cpp, revision 114e89fa584f0e1a81eb0a679aabf41af8870072](https://github.com/carbonengine/destiny/blob/114e89fa584f0e1a81eb0a679aabf41af8870072/src/Ball.cpp) for shared ball-state/motion ownership. This work uses the existing local rigid-ball scene writer and inventory wire adapters; Blue and Destiny layouts are unchanged. The mission-specific loot and turn-in rules come from typed Eden operations, the pinned catalog/client and the gameplay reports above.

### Automated validation

Exact starting filter: `FullyQualifiedName=Eden.Protocol.Tests.Agents.MissionRetrievalTests.SpyStashRetrievalUsesNativeContainerInventoryPersistsAndConsumesReportsOnce`. Both file-backed and resident cases passed in **11.17 seconds** after correcting explicit entity admission and test setup for native inventory-row materialization and reconnect scene entry. The initial combined retrieval/protocol/encounter/wreck selection passed 50 cases and exposed a warp test that was still aligning; the test now waits for actual warp with the existing simulated clock. The corrected retrieval class passed **9/9 in 27.71 seconds** before final metadata assertions were added.

The first final combined run passed **530/531**; the new slim-metadata assertion incorrectly treated a native `SlimItem` instance as a bare dictionary. Correcting that test adapter passed the exact `FullyQualifiedName=Eden.Protocol.Tests.Agents.MissionRetrievalTests.ContainerRejectsWrongOwnerShipSystemRangeAndInvalidTransfersWithoutChangingContents` filter **1/1 in 6.46 seconds**. The subsequent final selection passed **531/531 in 147.24 seconds**:

`FullyQualifiedName~Eden.Protocol.Tests.Agents|FullyQualifiedName~Eden.Protocol.Tests.Flight|FullyQualifiedName~Eden.Protocol.Tests.Loot.WreckTests|FullyQualifiedName~Eden.Protocol.Tests.Docking.TypedInventoryTests|FullyQualifiedName~Eden.Protocol.Tests.Docking.DockedServicesTests|FullyQualifiedName~Eden.Protocol.Tests.Information|FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~TypedCallResultTests|FullyQualifiedName~CombatAnomaly|FullyQualifiedName~Eden.Protocol.Tests.Inventory.ItemTrashTests`

This includes **12 retrieval cases**, typed guard/range rules, native fetch and container records, ordinary cargo notifications, partial/full pickup and `Add`/`MultiAdd` routing, ownership/ship/system/quantity/hold/capacity/warp rejections, guard-only completion rejection, moving cargo away, wrong-station hand-in, exact consumption with excess remaining, payout rollback, stale/empty/expired containers, abandonment and file-backed/resident checkpoint reopen. Existing agent, wreck, inventory, flight, information, anomaly and registration boundaries remain covered by the affected selection.

Tool restore, solution restore and final solution build passed; the solution build took **3.73 seconds** with zero warnings/errors. The Python catalog and formatting policy suites passed **8/8** and **9/9**, with **1.39 seconds** for the policy commands. Formatting verification, the 140-column check and `git diff --check` passed. Both exporters reproduced the checked-in output: **19 definitions, 239 types and 943 source records**. Relative documentation links, UTF-8/final newlines and preservation of historical mission records were verified. No full .NET suite was requested or run.

The first push encountered a concurrent portrait update on `main`. The mission commit rebased cleanly onto **a6ee5ec3** with no conflicts. Post-integration tool/solution restores, solution build (**5.95 seconds**, zero warnings/errors), both Python policy suites and formatting verification passed. The post-integration filter `FullyQualifiedName~Agents.MissionRetrievalTests|FullyQualifiedName~Agents.MissionProtocolTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~TypedCallResultTests|FullyQualifiedName~CharacterPortraitTests` passed **65 tests in 15.16 seconds**, with **one unrelated optional portrait-adapter check skipped** because `EDEN_TEST_CLIENT_ROOT` was unset. The 531-case result above covers the unchanged mission implementation before that rebase; all retrieval and native mission protocol cases were exercised again after integration.

### Live acceptance

Not performed; agent client testing is disabled. Native container presentation, opening interaction and gameplay feel remain unverified.

### Optional maintainer checks

- Accept The Spy Stash, bypass the first combat pocket, defeat the final ships, open the Officer's Quarters and recover the reports; confirm the native fetch display and agent-station turn-in.
- Partially loot the container, reconnect, and confirm only the remaining quantity is available before finishing the mission.

## Keyed acceleration gates and ship restrictions — November 9, 2026

Mission gates now enforce catalog ship lists, race restrictions and prohibited fitted-equipment lists, with authored reusable or consuming cargo keys. Added **Dread Pirate Scarlet**, mission **15237**, dungeon **4227**, as playable reusable-key content. The ordinary pool now contains **ten combat definitions**, four courier definitions and four delivery storylines. This record supersedes the key/ship-list exclusions in the earlier gate milestone while preserving that historical record.

### Gate eligibility and keys

`allowedShipsList` and `disallowedFittedTypesList` resolve through typed `client/typelist` records. Category, group and type inclusions/exclusions use the shared `InventoryTypeList` rule, also used by item trashing: exclusions apply at their own level before the next, more specific inclusion. A specific type inclusion can therefore override a group exclusion; a specific type exclusion still wins. Hull lists and allowed races intersect. Race zero remains admissible under the native client's race check. Explicit hull/race restrictions replace the default blacklist, rather than silently banning explicitly admitted hulls. The default blacklist now also includes build-3662168 **Command Carrier group 5120**. Prohibited equipment checks cover fitted modules, rigs and subsystems, including offline equipment; cargo items do not count as fitted equipment. Rejections produce readable gate errors. This milestone does not add a server-driven native ship-restriction-window notification.

Gate keys are explicit typed bindings in the mission content file because the pinned dungeon connections do not supply a key requirement. The server requires the exact item type and quantity in the activating pilot's active ship Cargo Hold. Other owners, stations, nested containers and specialized holds do not supply keys. Reusable keys remain in cargo and must be present on each activation. Temporary shared gate-open windows for fleet followers are not reconstructed; reusable-key eligibility is per pilot. Consuming keys pay across stable item-ID-ordered stacks in the activation transaction and publish native inventory changes before flight updates.

The [EVE University gate guide](https://wiki.eveuniversity.org/Acceleration_gate) describes key and ship restrictions. Its older canceled-warp key wording is reconciled with [CCP's January 16, 2025 PVE patch](https://www.eveonline.com/news/view/patch-notes-version-22-02): a consumed key unlocks that gate for that character for the site, surviving departure and logout until downtime. Eden saves that unlock by mission instance, catalog gate object and pilot. Repeated activations, canceled alignment and reconnect/restart do not consume a second key before the reset deadline. Eden applies the next **11:00 UTC** boundary from the [daily downtime reference](https://wiki.eveuniversity.org/Server_downtime), without shutting down the local server. A new mission, another gate or another character needs its own unlock. The included Scarlet gate is reusable; consuming-key behavior is validated with real saved-world transactions and checkpoint reopen, rather than claiming additional admitted consuming-key encounters.

The flight owner validates navigation and disruption against a copied course before committing gate access. The world operation rechecks mission membership/expiry, active hull, fit, range and room locks inside the key transaction. Only a successful transaction publishes the prepared movement and inventory changes. Missing keys, rejected ships/equipment, failed transactions and invalid navigation do not charge keys or start warp. The same active gate course acknowledges duplicate requests without paying again. Existing deadspace bypass protections remain in force for deadspace missions.

### Dread Pirate Scarlet content

The [EVE-Survival report](https://eve-survival.org/wikka.php?wakka=DreadPirateScarlet4), edited **October 20, 2020 15:52 UTC**, provides the key and roster evidence. Gate **969329** requires reusable **Gate Key type 2076**; its catalog destination beacon is **969330**. Gates **969331** and **969333** lead to beacons **969332** and **969339** without another key requirement. The ordered rooms are **22286, 22287, 22288, 22289**.

The last initial-pocket battleship drops one key into its own wreck. Earlier battleships do not drop it, including after the final objective is already complete. Key loot, NPC destruction, wreck ownership and ordinary loot commit or roll back together. Existing wreck transfer rules deliver the key to cargo. A brought key can bypass the initial guards. Final-pocket **Anire Scarlet type 2072** completes the objective; remaining escorts are optional and completion cannot pay twice. Mixed-faction groups receive their respective catalog faction corporations, and offer selection checks every authored enemy faction against the issuing agent's faction.

Eden chooses one explicit report alternative per room: Serpentis initial guards, Guristas second-pocket escorts, the report's Serpentis/Guristas third-pocket group and mixed final-pocket defenses. This is a documented reconstruction, not recovered random selection. Earlier Scarlet escape/early-kill/implant scripts, optional station loot, exact group aggro and historical respawn variants are excluded. Only the final Scarlet target is spawned. The report explicitly permits direct fleet warps into these **normal-space** pockets, so this mission does not receive the deadspace redirection used by Guristas Spies and The Assault. Local geometry remains catalog-derived with Eden's existing room placement.

World version **125** adds `WorldMissionGateUnlock` and saved member key-loot fields with zero defaults for existing members. The migration is idempotent and preserves existing NPC deaths, waves and reward quotes. It does not retrofit new loot onto previously saved members or change earlier accepted mission identities.

### Native and reference evidence

Inert disassembly of build-3662168 `code.ccp`, SHA-256 **37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368**, covered `evedungeons/client/util.pyj` (`GetDungeonShipRestrictions`, nested `IsTypeValid`, `GetDungeonShipRestrictionsTypeListID`), `evedungeons/common/constants.pyj`, the ship restrictions window and the unchanged `movementFunctions.RealActivateAccelerationGate` route. It confirms type-list/race intersection, the race-zero exception, replacement of the default blacklist for explicit restrictions, and one-ID `keeper.ActivateAccelerationGate` calls. No game or client code was executed.

Consulted [Carbon Destiny Ball.cpp at 114e89fa584f0e1a81eb0a679aabf41af8870072](https://github.com/carbonengine/destiny/blob/114e89fa584f0e1a81eb0a679aabf41af8870072/src/Ball.cpp), alignment/warp-state methods. Key authorization now precedes publishing the existing aligned course; no Blue or Destiny packet layout changed.

### Automated validation

Exact starting filter: `FullyQualifiedName=Eden.Protocol.Tests.Agents.MissionGateAccessTests.ScarletDropsOneReusableKeyAndNativeGateRejectsMissingCargoWithoutStartingWarp` passed both file-backed and resident cases in **10.86 seconds**. The initial combined gate/encounter/trash checks passed **86/86**. A later combined run passed 132 cases but exposed a nested transaction in the checkpoint test helper's lazy pilot-context setup; the fixture now resolves its context before opening its write transaction. Both corrected consuming-key cases passed, followed by the exact consuming-key/migration selection passing **3/3 in 7.28 seconds**.

Coverage includes key drop rollback and exactly one final-battleship key, drops after objective completion, missing/foreign-owner/remote/specialized-hold keys, reusable retries, consuming payment across multiple stacks, payment rollback, per-character/per-gate isolation, native inventory changes before flight, checkpoint reopen, exact downtime expiry, old-version migration, type-list inclusion precedence, default Command Carrier rejection, race-zero behavior, and online/offline prohibited fits. Final Scarlet death and exactly-once reward completion have real-world coverage. Consuming payment uses real saved-world transactions; the admitted Scarlet gate exercises reusable activation through native routing and the actual flight owner.

Final tool restore, solution restore and solution build passed with zero warnings/errors; incremental solution build took **0.65 seconds**. The affected filter `FullyQualifiedName~Eden.Protocol.Tests.Agents|FullyQualifiedName~Eden.Protocol.Tests.Flight|FullyQualifiedName~Eden.Protocol.Tests.Fleets|FullyQualifiedName~Eden.Protocol.Tests.Bookmarks|FullyQualifiedName~TackleTests|FullyQualifiedName~NpcEwarTests|FullyQualifiedName~CombatAnomaly|FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~TypedCallResultTests|FullyQualifiedName~Eden.Protocol.Tests.Inventory.ItemTrashTests|FullyQualifiedName~Eden.Protocol.Tests.Loot.WreckTests` passed **544/544 tests in 171.62 seconds**. Both exporters reproduced the checked-in files apart from platform newlines: **18 mission definitions, 221 types and 891 source records**. The Python catalog and formatting suites passed **8/8** and **9/9** in **0.779 seconds** combined. Formatting verification, the 140-column check and `git diff --check` passed; policy plus formatting checks took **19.20 seconds**. Documentation links, UTF-8/final newlines and preservation of all earlier protocol records were checked. No full .NET suite was requested or run.

### Live acceptance

Not performed; agent client testing is disabled. Native presentation, gate input and gameplay feel remain unverified.

### Optional maintainer checks

- Accept Dread Pirate Scarlet, verify the initial gate rejects missing cargo, loot the last battleship's Gate Key, and activate the gate; confirm the key remains in cargo after returning.
- With a catalog-restricted gate, compare an allowed hull with a prohibited hull and fitted-equipment variant; verify the rejection message and unchanged ship position.

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

## Mission layout corrections — November 9, 2026

All 50 admitted combat mission definitions have matching templates in the local evejs Security mission
packs. Their separation of native room geometry, entity anchors, fixed batteries and reconstructed
populations identified unused fields in Eden's own build-3662168 catalog. This milestone corrects the
existing mission pool; it does not admit additional mission definitions or import evejs authored
rosters, rewards, gate inference or JavaScript.

The reference packs were compiled from dungeon build 3579973 against server SDE 3396210. Local source
fingerprints, with no Git metadata available in the supplied release:

| Pack | SHA-256 |
| --- | --- |
| `evejs-security-missions-level-1-1.1.10.json` | `aac8b94379e9315aa08d98cc70c3e2c119fd43403c488899a39fd91f0f55ae6f` |
| `evejs-security-missions-level-2-1.1.9.json` | `04765eddeb4a5825e32d98347babccee1463b5cd5b73a56c6e7af874c4d05df5` |
| `evejs-security-missions-level-3-1.1.9.json` | `f59bcb999336ddd52045af6ab9e82701431fdefaaf2508871aa8f51d0cb42fd1` |
| `evejs-security-missions-level-4-1.1.9.json` | `351fe341993c14c2dc039ce9e4f7549175ad07e9950d8d423425dd16dda56376` |

Previously a group's reported distance became its positive-X position, and the global wave slot
became its Y coordinate. Every wave therefore formed a vertical line, ignoring native directions and
battery locations. `MissionCatalog` now retains room-local anchors, initial/reinforcement flags and
per-anchor aggression ranges. Positions are measured from that room's explicitly bound entry object,
then translated by its existing room offset and the saved mission location. This avoids applying a
lobby origin to destination-room coordinates.

Fixed batteries with a matching native type and spawn stage use their individual positions without
formation offsets. Mobile groups select eligible native anchors by proximity to their reported range;
anchors within 1,500 metres of the best range match share groups in stable object-ID order. Members
are spaced in 1,500-metre rings, with shared-anchor counts preventing two groups from occupying the
same starting points. Each room's first authored wave is treated as initial even when its global wave
index is nonzero. Missing stage metadata falls back to the room's mobile anchors; missing or exhausted
battery matches retain the report-distance fallback. These association and spacing rules are explicit
Eden policies, since native `npcGroupingID` records do not disclose their rosters.

Native scenery is no longer dropped merely because an NPC group is attached to it. Existing category
and invisible-beacon filters remain in effect. Mission objects also use the shared
[dungeon collision geometry correction](combat.md#anomaly-warp-in-collision-correction--november-9-2026),
so ore quantities and authored visual bounds do not become giant solid spheres, and non-interactable
decorations remain non-solid. Objective structures, containers and gate ownership/lifecycle retain
their existing explicit bindings.

NPC type selection, member identities, wave/room progress, required/optional groups, triggers, cargo
carriers and reward settlement are unchanged. Saved NPC positions are not relocated or rerolled;
newly started rooms and future waves use the corrected placement. No schema migration or reset is
required. Courier and storyline delivery placement is unaffected.

Independent gameplay references are [Duo of Death (Guristas, level 3)](https://wiki.eveuniversity.org/Duo_of_Death_%28Guristas_Pirates%29_%28Level_3%29)
and [Dread Pirate Scarlet](https://wiki.eveuniversity.org/Dread_Pirate_Scarlet). Catalog positions take
precedence over approximate report distances. Existing wire serialization and movement remain based
on [Carbon Destiny Thunkers.cpp at 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Thunkers.cpp);
this change does not alter Blue, transport or Destiny formats.

**Automated validation.** The Duo of Death battery-position regression failed before implementation
and passed after correction. Focused coverage checks all 50 existing combat definitions, native
scenery and room transforms, fixed batteries in five mission variants, separated Guristas Spies
groups, initial/reinforcement selection, invalid range rejection, transaction rollback, repeated
arrival and resident checkpoint/reopen without relocation. Every combat-room entry is also checked
against solid geometry with a 500-metre-radius hull. The development filter
`FullyQualifiedName~Eden.Protocol.Tests.Agents` passed **457 cases** (267-second test duration;
269.62-second invocation). It completed before a requested stop took effect, so it was not interrupted.

After adding the entry-clearance and later-room regressions, final root validation passed **154
focused .NET cases** (81-second duration) with this filter:
`(FullyQualifiedName~Eden.Protocol.Tests.Agents&FullyQualifiedName!~Eden.Protocol.Tests.Agents.MissionContentTests)|FullyQualifiedName=Eden.Protocol.Tests.Agents.MissionContentTests.ExpandedContentLoadsEveryDefinitionAndCorrectsNuisanceFactionRosters`.
The delivery/expansion matrix already covered by the broad run was not repeated. Tool restore,
solution restore and build (zero warnings/errors), the catalog policy suite (8 cases, 0.729 seconds),
formatting policy suite (9 cases, 0.015 seconds), mission content suite (5 cases, 0.007 seconds),
formatting verification (1,429 files, maximum 140 characters), source fingerprints, documentation
links/encoding/whitespace and diff checks passed. Ordered final validation took **108.69 seconds**.
No full .NET suite was requested or run. Final TRX:
`artifacts/feature-tests/results/mission-layout-final/mission-layouts.trx`; broad development TRX:
`artifacts/feature-tests/results/6deca9f2204e48e4a7bb107b05540c85/tests.trx`.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Inspect Guristas Spies and a Duo of Death destination pocket for
separated groups, native battery positions, scenery and stable arrival.
