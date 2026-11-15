# The Agency

[Documentation index](../README.md) / [Protocol index](../protocol.md)

## Agents and Missions browsing and actions — November 15, 2026

The Agents & Missions milestone covers the native Agency and Agent Finder using Eden's existing mission systems.
Security, distribution, mining and R&D agents use the catalog directory and their existing conversations; earned
storylines use the selected pilot's journal. The client owns faction, corporation, division, level, distance,
security and standings filters, sorting, destination setting, docking and conversation buttons. No custom Agency
search service or replacement client interface is introduced.

The missing contracts were identified by inert inspection of build 3662168:

| Call or field | Behavior |
| --- | --- |
| `agentMgr.GetAgents().isLocatorAgent` | Corrected the prior `isLocator` column to the field used by `ContentProviderLocatorAgents`, the Agent Finder checkbox and agent cards. Direct static-info replies expose `isLocatorAgent` alongside the existing `isLocator` alias. |
| `epicArcStatus.AgentHasEpicMissionsForCharacter(agentID)` | Native `False` for a known active catalog agent. The Finder invokes this availability check even when epic-arc progression is absent. It no longer fails with an unsupported service call. |
| Bound `GetEntryPoint()` | Native three-float tuple for the selected pilot's accepted combat/mining entrance; None before acceptance, after closure, at expiry, or without a dungeon. It does not allocate a site or expire a mission. |
| Bound `GetDungeonShipRestrictions(dungeonID[, gateID])` | Native KeyVal with Python lists `allowedShipTypes` and `restrictedShipTypes`, plus boolean `nonDefaultShipRestrictions`. The dungeon must belong to the selected pilot's current unexpired offer/accepted mission; an optional gate is its catalog `fromObjectID`. |
| Mission objective dungeon `ownerID` / `shipRestrictions` | Known enemy faction and integer `1` for explicit hull/race restrictions, enabling the existing faction badge and restricted-ship preview. Unavailable optional metadata is omitted. |

The new preview operations read resident state under its existing owner. Catalog-generation caches share ship/race
inputs and immutable restriction results; catalog edits invalidate them. Ship previews reuse the same typed hull,
race and type-list checks as gate activation. A dungeon-wide list intersects every authored gate's admission,
including default restrictions on an otherwise unrestricted gate. This deliberately follows Eden's enforcement
when explicit and default gates are mixed. A dungeon with no authored gates has no hull exclusions. Fitted
equipment, keys, room-clear locks and current ship state remain separate activation checks.

Selection and known-agent validation apply to the added calls. Wrong dungeons/gates, other pilot identities,
invalid numeric representations, extra arguments and service keywords reject before publishing anything.
The router's `machoVersion` transport keyword remains accepted. Previews do not accept/complete missions, issue
cargo, pay rewards, close expired offers, start rooms, or send mission notifications. Existing conversation
actions retain the journal and `OnAgentMissionChange` flow, including earned storyline refreshes and private
offers. No schema, checkpoint format, Blue codec, Destiny format or client package changes are needed.

Career, epic-arc, Paragon, faction-warfare and locator-search gameplay is outside this browsing milestone.
Their catalog identities remain available; existing career-completion and epic-arc status/message placeholders
remain unchanged. The false epic-arc availability response does not add playable arcs. Stationless special-agent
spawning and travel to unmaterialized agent sites are not implemented here.

### References and evidence

[EVE University: The Agency](https://wiki.eveuniversity.org/The_Agency),
[Agent Finder](https://wiki.eveuniversity.org/Agent_Finder) and [agents](https://wiki.eveuniversity.org/Agent)
describe the category and filter boundaries. Wire contracts were checked against the pinned client rather than
inferred from current wiki descriptions. Nineteen inspected Agency providers/pieces, agents/agentUtil,
activeMissionController, epicArcSvc and dungeon-util modules match the original build archive byte-for-byte
(archive SHA-256 `37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`).
Evidence and source-comparison output: `artifacts/agency-agents-missions/`.

The local evejs snapshot has no Git metadata. Comparison fingerprints:

| Source | SHA-256 |
| --- | --- |
| `server/src/services/agent/agentMgrService.js` | `34544351a7ad85567ba55a4626aa5db4304cbc10546466b547dbd271b3772c66` |
| `server/src/services/agent/epicArcStatusService.js` | `d9acab8ddfa7f940af34e4a44386614d3eb873b216d7836eb57ed340881c7cdb` |
| `server/src/services/dungeon/dungeonShipRestrictionResolver.js` | `f1219591f6f36e122fe47fef1e0fc610a6854b251cd248ef7ac1863a8e5421e6` |

Only contracts and behavior were compared; Eden's implementation uses its own typed rules, resident missions
and checkpoint lifecycle. No Carbon transport, serialization or simulation behavior was changed.

### Validation

**Automated validation.** The initial exact filter
`FullyQualifiedName=Eden.Protocol.Tests.PilotSessions.AgentDirectoryServiceTests.DirectoryUsesNativeRowsetFieldsAndRequiresSelectedPilotWithoutAcceptingForgedFilters`
reproduced the incorrect locator column (31.36 s build, 7.37 s invocation), then passed after correction
(14.02 s build, 7.73 s invocation). The new security/distribution/mining card theory passed three cases
(11.56 s build, 20.39 s invocation), including native conversations, journal shapes, checkpoint reopening and
three warmed preview cycles with zero SQL commands/connections. Other new cases exercise availability rejection,
deadline boundaries without mutation, native session packets, earned storylines, ship-list/race exclusions,
mixed gate restrictions, immutable snapshots and catalog invalidation.

A broader development run was interrupted after existing failures. A clean `ad50bde4` worktree reproduced all
five selected baseline cases (11.13 s build, 11.67 s invocation): the two agent-info and two map-session cases
lacked a resident world, and the contact rollback case expected `SqliteException` instead of its EF wrapper.
The agent-info/map session fixtures now start the resident world; those four cases passed in the targeted
follow-up. `AgentDirectoryTests.FailedAgentRemovalRollsBackEarlierPlayerContactDeletionInTheSameBatch` remains
unchanged and is explicitly excluded from final focused validation. No full .NET suite was requested or run.

Final ordered root validation passed in **93.57 seconds**: tool restore, solution restore/build (zero
warnings/errors), **126 focused .NET cases** with no skips (57.10 s invocation), both Python policy suites
(8 catalog and 9 formatting cases), and formatting verification across 2,224 files with the 140-character limit.
Documentation encoding, newline, whitespace and three new index-anchor references passed separately, as did
`git diff --check`. The final filter used `FullyQualifiedName~` clauses for these classes, joined with `|`,
then excluded the single baseline contact failure above using `&FullyQualifiedName!=`:

```text
Eden.Protocol.Tests.Agents.AgencyAgentTests
Eden.Protocol.Tests.PilotSessions.AgentDirectoryServiceTests
Eden.Protocol.Tests.Agents.MissionProtocolTests
Eden.Protocol.Tests.Agents.AgentShowInfoTests
Eden.Protocol.Tests.Agents.MissionGateAccessTests
Eden.Protocol.Tests.Agents.AgentInformationRulesTests
Eden.Protocol.Tests.Agents.ResearchAgentTests
Eden.Protocol.Tests.Persistence.AgentDirectoryTests
Eden.Protocol.Tests.Maps.MapNavigationTests
```

The exact expression and per-step times are in `artifacts/agency-agents-missions/validation.json`;
final TRX: `artifacts/agency-agents-missions/final/agency.trx`. The clean-baseline reproduction is retained
as `artifacts/agency-agents-missions/baseline-failures.trx`.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Inspect the Locator checkbox and faction/ship previews in Agent Finder; open a
supported agent's conversation and earned storyline card. Native rendering and input remain unverified.

## Native Agency directories — November 10, 2026

The Agency now connects the pinned client's cosmic-signature and ice-field providers to Eden's saved sites.
The existing agent directory, combat-anomaly directory, static asteroid belts and planetary catalog remain their
respective sources. The client owns category layout, filters, distance ordering and route/warp buttons.

| Request | Native reply and behavior |
| --- | --- |
| `dungeonInstanceCacheMgr.GetSignatureInstances()` | System-to-integer-count dictionary for current saved combat, data/relic and gas signatures. Retired, expired and superseded generations are excluded. Pirate population settings suppress combat and hacking signatures, independently of gas. No signature identity or coordinates are revealed. |
| `GetIceBeltInstances()` | System-to-list dictionary of active saved ice fields, using native `util.KeyVal` records. Each record includes `siteID`, `instanceID`, scanner `targetID`, dungeon/name IDs, ice archetype, faction and tuple position. |
| `GetIceBeltsCount()` | One available field per admitted pinned ice system, including fields not yet materialized. A depleted field reports zero until its saved six-hour replacement deadline, then becomes available as an unknown field until the normal lifecycle materializes it. |
| `GetOreAnomalyInstances()`, `GetOreAnomaliesCount()`, `GetOreAnomaliesCountInRange()`, `GetBulwarkMiningSiteCounts()` | Empty dictionaries: Eden's ordinary asteroid belts are static celestials; no corresponding ore-anomaly lifecycle exists. |
| `GetFactionWarfareInstances()`, `GetFactionWarfareBattlefieldInstances()`, `GetHomefrontSiteInstances()`, `GetPirateInsurgencyInstances()`, `GetPirateInsurgencyIceHeistInstances()`, `GetTriglavianSiteInstances()` | Empty dictionaries for activities without authoritative shared sites in Eden. |
| `custom_agency_provider.get_content_data()` | Empty Python list; no custom campaigns or pirate strongholds are advertised. |
| `map.GetIncursionGlobalReport()` | Empty Python list; no active incursion simulation. |
| `map.GetSystemsInIncursions()` | Empty native rowset with `locationID`, `sceneType`, `templateNameID`. |
| `dynamicResourceCacheMgr.GetESSAgencyData()` | Empty dictionary; no ESS banks or dynamic bounty systems are advertised. |
| `GetDynamicResourceSettings()` | Named dormant display defaults from evejs for the ESS filter bounds and tooltips, which load even without any banks. They do not change Eden's bounty rewards. |
| `GetDBSMapData()` | Empty dictionary for the accompanying dynamic-bounty map filter. |

All requests above require a selected pilot and zero service arguments. Forged positional filters and service
keywords fail before reading the directory; the router continues to strip its native `machoVersion` transport
keyword. Unknown methods remain unsupported. Combat-directory requests now apply the same validation, and unrelated
dungeon requests no longer accidentally run the current-system combat-population path.

The new World reader holds the existing world owner and movement gate while taking its snapshot. It reads saved
site generations and never generates sites, advances encounters, writes deadlines or loads the full universe.
Ice eligibility is cached by catalog generation and uses the same admitted system, anchor and deposit criteria as
the ice lifecycle. Saved depletion and signature expiry are checked against the supplied clock on every read.
Remote signature counts cover materialized systems; they do not estimate unexplored systems. Existing combat
directory materialization semantics are unchanged. No database schema or client package changes are needed.

### References and pinned-client evidence

[EVE University: The Agency](https://wiki.eveuniversity.org/The_Agency) describes the category, discovery and filter
behavior; its gameplay descriptions are reconciled with build 3662168 rather than copied as wire contracts.

The local `F:/Git Repos/evejs` snapshot has no Git metadata, so source SHA-256 fingerprints identify the inspected
revision. The reference supplied collection shapes and known-versus-unknown discovery behavior; Eden's existing
gameplay and saved sites remain authoritative. No Blue codec or Destiny engine behavior changed.

| evejs source | SHA-256 |
| --- | --- |
| `server/src/services/dungeon/dungeonInstanceCacheMgrService.js` | `2936d208872223b0c0dc5ad5a8437937c24f89953a5c8e67cfa013fab0bf887f` |
| `server/src/services/agency/customAgencyProviderService.js` | `b5bfc5f58a12312ade215bb61d0ce4efd8b793fdfb71f8695b94b0f9e52c2eea` |
| `server/src/services/incursion/incursionPayloads.js` | `00cc56e094cb86f98d2a9d2f76b337b848ac9c2696d068d332368d2174b2aaac` |
| `server/src/services/dynamic/dynamicResourceState.js` | `b0a343317499b1a32cc30d7a722afe836965951d2165e720b759dc2202caefb6` |

Static bytecode inspection used the staged build's Agency modules, all **359** of which were compared byte-for-byte
with the original pinned archive (`37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`).
`ContentProviderSignatures._GetAllDungeonInstances` expands remote integer counts into unknown entries, then uses
the current sensor-suite signatures locally. `evedungeons/client/anomalyTracker.pyj` calls separate instance and
unknown-count RPCs for ice and ore. `IceBeltSystemContentPiece.ConstructIceBeltContentPiece` requires `siteID`, and
`IceBeltContentPiece._ExecutePrimaryFunction` forwards `targetID` to the existing scanner warp route. This is static
inspection, with no live-client observations in this milestone.

### Validation

**Automated validation.** The exact development filter
`FullyQualifiedName~Eden.Protocol.Tests.Exploration.AgencyTests.IceDiscoveryKeepsUnknownFieldsSeparateAndTracksDepletionReplacementAndReopen`
passed one case (19.36-second build, 25.28-second test invocation). The expanded development filter
`FullyQualifiedName~Eden.Protocol.Tests.Exploration.AgencyTests` passed all 21 cases (4.11-second build,
38.29-second test invocation). Coverage includes simulated-router selection and argument rejection, native
collection and ice-entry fields, undiscovered availability, depletion boundary, replacement and database reopen,
signature families, expiry, retired generations, population settings and unchanged saved row counts.
The ESS filter-settings correction then passed the exact
`FullyQualifiedName~Eden.Protocol.Tests.Exploration.AgencyTests.ProvidersRequireSelectionRejectForgedFiltersAndPreserveNativeCollections`
filter (21 cases; 9.09-second build and 14.78-second test invocation).

Final root validation passed tool restore, solution restore, solution build with zero warnings/errors, **95 focused
.NET cases** (48.97-second test invocation), the catalog policy suite (8 cases; 0.643 seconds), formatting policy
suite (9 cases; 0.015 seconds), and formatting verification (1,585 files, maximum 140 characters). The ordered
sequence took **79.25 seconds**. The separate documentation check passed four UTF-8 files and five local
links/anchors; diff checks passed. The full .NET suite was not requested or run.

The final filter combined
`FullyQualifiedName~Eden.Protocol.Tests.Exploration.AgencyTests|FullyQualifiedName~Eden.Protocol.Tests.Exploration.CombatAnomalyWorldTests|FullyQualifiedName~Eden.Protocol.Tests.Exploration.ProbeScanningTests|FullyQualifiedName~Eden.Protocol.Tests.Mining.IceMiningTests|FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.AgentDirectoryServiceTests`.
Final TRX: `artifacts/feature-tests/results/agency-final/agency.trx`.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Open Agency categories while docked and in space; inspect known/unknown ice cards
and the existing scanner warp action; compare current-system signatures with the probe scanner. Native rendering,
input and filter/warp interactions remain unverified.
