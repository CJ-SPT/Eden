# Sovereignty

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
| 4. Activity and capitals | NPC-kill and mined-volume contributions, activity decay, strategic age in published views, capital selection/delays and single-system behavior; ADM connected to capture timers. |
| 5. Resources and upgrades | Catalog-backed upgrades, stellar power, Skyhook resources, workforce routing, reagents/fuel, priorities, offline/low-power transitions and site-generation effects. |
| 6. Infrastructure and cleanup | Ansiblex, beacons and jammers gated by active upgrades; access and map consumers; destruction, self-destruct, alliance departure/dissolution and Skyhook ownership transitions. |

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
