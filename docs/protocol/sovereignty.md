# Sovereignty

## Scope and implementation plan

Target the Sovereignty Hub system supported by Windows build **3662168**, using resident authority and checkpoint
persistence. A deployed neutral hub and a completed alliance claim are separate states. Deployment alone must not grant
territory. Current gameplay references are [UniWiki sovereignty](https://wiki.eveuniversity.org/Sovereignty) and
[Entosis Links](https://wiki.eveuniversity.org/Entosis_Link), checked against the pinned client and catalog at each milestone.

| Milestone | Implementation and completion criteria |
| --- | --- |
| 1. Foundation | Typed claims and system eligibility, strategic-age and ADM rules, resident indexed reads, migration and coherent checkpoint/reopen coverage. Implemented below. |
| 2. Deployment and initial capture | Inventory-backed deployment around the star; corporation/alliance authority; one neutral hub per eligible system; Entosis warm-up, paid cycles, range and ship restrictions; contested progress; atomic completed claims and native sovereignty/map views. |
| 3. Territorial conflict | Vulnerability windows and delayed changes, reinforcement deadlines, constellation command-node campaigns, defense/regeneration and deterministic ownership-loss settlement; restart-safe progress and native notifications. |
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
