# Modding protocol and activation

## Express map lines and system information — November 15, 2026

The user reported missing express-route map lines and a blank system Show Info window. Both native map
renderers read `res:/staticdata/starMapCache.pickle` through their own `GetStarMapCache`; updating only
`cfg.mapJumpCache` did not update their visual edges. Client composition version 7 overlays both readers,
preserves native system metadata, updates system neighbours and emits one visual edge per reciprocal route.
The source cache remains unchanged and warmed callers reuse the composed view.

Added location rows now match ordinary build-3662168 `mapObjects.db` stargates: `connector=False`, with
null orbital parent and celestial/orbit indices. Headless checks execute native system-item, neighbour,
orbital-body and system-info construction calls, including the new neighbour tab. These checks have not
reproduced the user's complete blank-window failure. Header and celestial-info exceptions now also write
the bounded local trace described below, while preserving and rethrowing the original exception. Qualified
code-object matching patches only the monolithic header method, preserving the base class's same-named method.

Native evidence is from the unchanged build-3662168 archive SHA-256
`37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`, its map pickle and local static map database.
This change introduces no wire format, gameplay calculation or saved-world persistence changes.

Automated validation: tool/solution restore, zero-warning solution build, 55 focused .NET cases (zero failures/skips),
44 headless native client assertions, both Python policy suites (8 catalog and 9 formatting cases), and formatting
verification passed in 51.66 seconds. The full .NET suite was not requested or run. Focused .NET filter:

```text
FullyQualifiedName~TravelRouteModTests|FullyQualifiedName~ModFoundationTests|FullyQualifiedName~ModRuntimeTests|FullyQualifiedName~ModLauncherApiTests|FullyQualifiedName~AutoMiningPackageTests|FullyQualifiedName~SkillPackageRetentionTests|FullyQualifiedName~GameplayDependenciesTests
```

A separate headless run applied the installed Regional Express overlay to the real native map pickle:
all 138 directed gate entries resolve to 69 new visual links, every edited system's neighbours agree with the
overlay, warmed reads share identity and the original cache remains unchanged.

Live acceptance: Not performed; agent client testing is disabled. The user confirmed the info window stays blank.

Optional maintainer checks: refresh the launcher package, reopen system Show Info, and verify express map lines.
If the window remains blank, inspect the new `info-header` / `celestial-info` trace in the local client error log.

## Preserve native catalog indices — November 15, 2026

The captured user traceback identified `EveConfig instance has no attribute 'stations'` in
`CharacterSelectionDataForCharacter.GetCurrentStationAndStationLocation`, at its station-orbit lookup.
Inert execution of native `EveConfig.LoadNPCStations` reproduced the earlier failure: its input requires the
root catalog's named `npcStations` index, which the Regional Express dictionary copy had discarded.
Per-system row checks alone did not cover this native loader interface.

Client composition version 6 retains the original catalog behind each map overlay and forwards its native
attributes and indices. The aggregate stargate index also includes added gates with their containing system IDs.
Added vectors support both named XYZ fields and the sequence access used by native geometry consumers.
The regression reads real pinned solar-system FSD records, builds their named-index surface, executes the exact
native NPC station loader, verifies the Kihtaled station/orbit from the user's trace, and executes the previously
failing native character-selector station-detail method. It checks that existing station index identity survives
and that express gates appear in both per-system and aggregate views.

Automated validation: tool/solution restore, zero-warning solution build, 38 affected .NET cases (no failures/skips),
28 headless native adapter assertions, both Python policy suites (8 catalog and 9 formatting cases), and formatting
verification passed in 55.67 seconds. The affected .NET filter is the diagnostic-capture filter below. Native
checks execute the reported Kihtaled station-detail failure path. The full .NET suite was not requested or run.

Live acceptance: Not performed; agent client testing is disabled. The failure trace was produced by the user.

Optional maintainer checks: update the launcher package and confirm a docked character's selector fully loads.

## Character selector exception capture — November 15, 2026

The user reported a partially filled character selector after the pre-selection location RPC correction.
Server logs showed authentication but no character selection. Public gate/system overlays were also exercised
against the real pinned FSD loaders, and the local Kihtaled station/orbit records were present. These checks did
not establish the failing native UI call; no further root cause is assumed from the screenshot alone.

Client composition version 5 wraps the uniquely matched build-3662168 `SmallCharacterSlot.LoadSlot` method.
Its original body runs unchanged. On failure, the client saves the Python traceback under
`%LOCALAPPDATA%/Eden/ClientErrors/character-selection-PID.log` and rethrows the original exception.
Each process owns its log; writing is best-effort and stops after 2 MiB. Captures contain the stage, character ID,
UTC time and stack trace, without serializing game objects or session tokens. This is diagnostic support, not
acceptance of a character-selector fix.

Automated validation: the pinned interpreter test verifies the original exception survives and a durable native
stack trace is written; 22 adapter assertions pass without starting the game.
Tool/solution restore, zero-warning solution build, 38 affected .NET cases (no failures/skips), both Python
policy suites (8 catalog and 9 formatting cases), the native adapter check and formatting verification passed
in 63.84 seconds. The full .NET suite was not requested or run. Filter:

```text
FullyQualifiedName~ModFoundationTests|FullyQualifiedName~ModRuntimeTests|FullyQualifiedName~ModLauncherApiTests|FullyQualifiedName~AutoMiningPackageTests|FullyQualifiedName~SkillPackageRetentionTests|FullyQualifiedName~GameplayDependenciesTests
```

Live acceptance: Not performed; agent client testing is disabled.

Optional maintainer checks: refresh the launcher package and reproduce the incomplete selector once so its
traceback can identify the precise failing call.

## Regional Express — November 15, 2026

Optional package `eden.regional-express` 1.0.0 exports 69 reciprocal route pairs across 64 ordinary known-space
regions in build 3662168. One regional interchange joins the nearest of five fully connected empire hubs.
The [modding guide](../modding.md#regional-express) records installation, selection policy and exclusions.

`travel-routes.json` uses API 1.1.0; hosts retain API-1.0.0 compatibility. Old hosts reject the new API instead of
silently ignoring its geography. Catalog composition adds reciprocal Stargate/Celestial records and updates
`client/systems`, `client/jumps` and `client/solarsystemcontent` in the immutable package catalog. A failed edit
rolls back as a unit. The original catalog and live world are not edited during publication. Runtime travel uses
the existing resident operation and checkpoints; this adds no saved-world SQL or alternate travel implementation.

Gate types use the source region's most common ordinary gate family, with Region variants for the same family
and Border variants between families. Build-3662168 ItemType group 10 supplies exact type IDs and radii; each end
is independent. This excludes damaged and special gates from family selection. The full-catalog check verifies
the chosen family exists in each endpoint system and checks all 138 projected types and positions.

Pinned archive `37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368` was inspected inertly.
EveConfig's `mapJumpCache`, `mapSystemCache`, `mapSolarSystemContentCache`, `GetLocationsLocal` and
`GetLocationsLocalBySystem` each receive a uniquely matched code-object wrapper at composition. Original bodies
are preserved. Shared overlays retain unchanged row identity, native attribute access and integer keys; original
gate-ID mapping derives both reciprocal IDs from the overlaid system content. Name and map replies preserve
their original CRowset column ordering. The loader does not depend on opening Eden Mods.

No Blue encoding, Destiny simulation or transport rules change. Existing Carbon Blue revision
`af0d65b29f951260ea09140ca11adf5d40566997` and Destiny revision
`27ee40b38a563d29054b32f64c3ebc87fabaec1f` references remain applicable. Gameplay reference:
[EVE University stargates](https://wiki.eveuniversity.org/Stargates), particularly region/border architecture and
ordinary gate use. This network and its hub policy are Eden additions, not official EVE routes.

Shortest-path comparisons against the original pinned catalog (all security levels): Jita–Amarr 11 → 1 jumps,
Jita–Dodixie 12 → 1, Jita–Rens 15 → 1, Jita–Hek 9 → 1, and Jita–1DQ1-A 42 → 12. These are catalog graph
measurements, not native-client timing or safer-route acceptance.

### Automated validation

The initial exact development filter was
`FullyQualifiedName=Eden.Protocol.Tests.Administration.TravelRouteModTests.ComposedGateTravelUsesResidentStateAndSurvivesCheckpointReopen`.
Development corrected position serialization and the package metadata required by the checkpoint fixture.
Focused coverage includes native gate contacts, rejected range/destination calls with unchanged state, reciprocal
resident transfers, checkpoint reopening, invalid composition rollback, package activation/removal and model families.
The complete real-catalog test composes all 138 gates, checks deterministic IDs and models, excluded regions and
the three-jump interchange bound. Headless Python checks execute actual patched build-3662168 functions and
verify route caches, reciprocal gate mapping, names and map-row shape without starting the client.

Final tool restore, solution restore/build (zero warnings/errors), **106 focused .NET cases, zero skips/failures**,
both Python policy suites (8 catalog, 9 formatting cases), 20 headless native-adapter assertions, five client-mod
adapter cases and formatting verification passed in **163.80 seconds combined**. The .NET runner reported
1 minute 48 seconds (111.52 seconds including runner startup). The full .NET suite was not requested or run.
`EDEN_TEST_CLIENT_ROOT=E:/EdenDev/3662168` and `EDEN_TEST_TRAVEL_CATALOG` pointing to the original pinned
`catalog.sqlite` enabled the archive and complete-catalog checks. An earlier policy failure was corrected by
moving region-style reads into the shared catalog provider and declaring the isolated travel catalog writer
alongside the existing skill catalog writer in the catalog policy.

Final focused filter:

```text
FullyQualifiedName~TravelRouteModTests|FullyQualifiedName~RegionalExpressCatalogTests|FullyQualifiedName~ModFoundationTests|FullyQualifiedName~ModRuntimeTests|FullyQualifiedName~ModLauncherApiTests|FullyQualifiedName~AutoMiningPackageTests|FullyQualifiedName~SkillBonusBuilderTests|FullyQualifiedName~SkillPackageRetentionTests|FullyQualifiedName~FlightSessionTests|FullyQualifiedName~GateTravelRulesTests|FullyQualifiedName~GateActivityProtocolTests|FullyQualifiedName~GameplayDependenciesTests
```

Live acceptance: Not performed; agent client testing is disabled. Native gate rendering, map drawing and
interactive autopilot remain unverified.

Optional maintainer checks: after activation, inspect gate art at both ends of an empire–nullsec link; plot and
fly a route through that link using native autopilot.

## Client package retention — November 15, 2026

Generated packages each contain a complete projected catalog. Retaining every generation and directories left
by terminated compilers caused approximately 26 GiB of deployed data; pruning obsolete generations and two
abandoned builds reclaimed 18.6 GiB while preserving active and rollback sets.

World-owned `SkillPackageRetention` now prunes before and after both publisher paths and after successful host
activation. It reads the active marker, mod Active/Previous/Pending identities and legacy PendingPackage from
read-only sidecars before selecting any deletions. Legacy activation also writes `.skills-previous` atomically
before replacing the active marker. Once mods own the catalog, their durable Previous supersedes that legacy marker.
Interrupted mod activation continues to protect both previous and pending identities.

A shared per-world `.skill-packages.owner` file lease covers compilation, atomic directory publication, staging
and cleanup across publishers and CLI processes. The owner file persists after closing to avoid unlink/reopen races.
Cleanup can remove abandoned recognized build directories whenever it acquires the lease; it cannot acquire
ownership while another updated publisher is building. Concurrent publication fails before creating output.
Mod compiler cancellation now waits for process exit before releasing ownership and removing temporary output.

Cleanup checks saved references first, rejects filesystem links, preserves unrecognized folders, and logs/defer
errors without changing successful publication or startup results. It does not prune installed mod archives,
world saves, original catalogs, edit history or operator backups. No gameplay, Blue or native client wire behavior changes.

### Automated validation

The initial exact development filter was
`FullyQualifiedName=Eden.Protocol.Tests.Administration.SkillPackageRetentionTests.PruningPreservesAllSavedReferencesAndRemovesOnlyOwnedObsoleteDirectories`:
one pass, 43.71-second build and 4.47-second runner. Subsequent focused tests exposed an omitted
`InvalidDataException` cleanup catch and Windows fixture symlink/junction cleanup requirements; these were corrected.

Final pinned tool restore, solution restore/build (zero warnings/errors), the focused filter below, both Python
policy suites (8 catalog and 9 formatting cases) and formatting verification passed in **71.50 seconds combined**.
The focused .NET run passed **53 cases, zero failures/skips**, with a reported test duration of 13 seconds.
`EDEN_TEST_CLIENT_ROOT=E:/EdenDev/3662168` enabled real headless pinned-client publication checks.

```text
FullyQualifiedName~SkillPackageRetentionTests|FullyQualifiedName~SkillEditorTests|FullyQualifiedName~ModFoundationTests|FullyQualifiedName~ModLauncherApiTests|FullyQualifiedName~ModRuntimeTests|FullyQualifiedName~AutoMiningPackageTests|FullyQualifiedName~ServerServicesTests|FullyQualifiedName~GameplayDependenciesTests
```

Coverage includes bounded repeated generations, reopened sidecars, legacy rollback, damaged/missing references,
abandoned directories, shared ownership, filesystem links, actual publication failures and subsequent publisher reuse.
The full .NET suite was not requested or run.

Live acceptance: Not performed; agent client testing is disabled.

Optional maintainer checks: none required for retention; operator data backup retention remains separate.

## Package foundation — November 9, 2026

Version-1 portable packages and host composition retain the existing build-3662168 skill overlay and immutable
client archive boundary. No new native RPC or notification is introduced in this milestone. Shared skill validation
now resides in Gameplay and consumes typed catalog metadata, retaining protected original skill calculations.

Permanent logical/numeric skill mappings and namespaced mod state reside in World-owned SQLite persistence.
Restart activation verifies composed files and retains an interrupted-activation recovery marker. See the
[modding guide](../modding.md#foundation-validation--november-9-2026) for commands, source references and automated evidence.

Live acceptance: Not performed; agent client testing is disabled.

## Offline authoring — November 9, 2026

Skill validation, bonus composition and search are shared Gameplay APIs. Offline projects preserve original-skill
protection and export portable logical references. Migrated projects retain numeric identities through a world-bound
map; another world cannot admit it. No new native wire calls are introduced in this authoring milestone.
See the [SDK and migration record](../modding.md#offline-sdk-and-skill-migration--november-9-2026) for validation.

## Native mod command bridge — November 9, 2026

`edenMods.Execute` is selected-pilot only and accepts exactly three text arguments: mod identity, command and argument
text. Replies use an explicit Blue dictionary adapter with `success` boolean, `json` text and nullable `error` text.
UTF-8 command/reply data is bounded at 64 KiB; command-role requirements are authoritative. Unknown or malformed calls
return structured errors, and other service/method pairs remain unsupported by this handler.

Neocom.Run/GetMenu adapters are inertly compiled against uniquely matched build-3662168 functions. Session notifications
use the inspected native service manager's RegisterNotify path. This milestone changes no Blue codecs or Destiny rules.
See the [runtime record](../modding.md#working-server-and-client-extensions--november-9-2026) for pinned source identity,
Carbon references, validation filters and limitations.

Live acceptance: Not performed; agent client testing is disabled.

## Launcher version 2 — November 9, 2026

Server metadata adds optional `ModSet` (identity, generation, client archive hash/length and ordered mod versions).
Ticket requests add nullable `ModSetId`; the server requires an exact active-set match. The authenticated
`/api/v2/mod-sets/{id}/code` route supplies only the active immutable archive. Authentication and legacy package
routes remain under `/api/v1`. Updated launchers accept metadata versions 1 and 2 and retain their verified archive,
manifest, backup and recovery mechanisms. These HTTP additions do not alter the native Blue/Destiny formats.

The current administration surface is `/mods`. Web skill authoring and its API routes are removed; historical
component/protocol evidence is retained. See the [cutover guide](../modding.md#launcher-and-administration-cutover--november-9-2026).

Live acceptance: Not performed; agent client testing is disabled.

Final automated evidence and selected test classes are in the [cutover validation](../modding.md#final-cutover-validation).
