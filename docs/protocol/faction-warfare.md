# Faction warfare

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

Hostile highsec navy pursuit, war-specific command burst filtering, and service or tether restrictions remain
separate pieces of the enrollment and combat milestone. Complexes, LP and capture actions remain later milestones.

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
