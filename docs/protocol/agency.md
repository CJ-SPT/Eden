# The Agency

[Documentation index](../README.md) / [Protocol index](../protocol.md)

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
