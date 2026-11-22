# Saved-world gameplay tuning

[Documentation](../README.md) / [Protocol index](../protocol.md) / [Administrator guide](../world-configuration.md)

## First-start world setup

New setup approvals use Established Universe. The default draft uses the Standard market, catalog-derived
industry reference prices and activity-funded PvP ships. Setup validates an installed compatible capture before
approving the Standard draft. Existing active settings remain available when reconfiguring
an established save. The setup endpoint rejects a Fresh Universe profile or draft. Existing approved setup operations
can still resume, and active legacy saves keep their rules until wiped. A wiped legacy save records that setup is
required and the replacement starts with the established market defaults.
[Operator flow](../world-configuration.md#first-start) · [Wipe choices](../world-wipe.md)

The Standard default setup case passed in 7.70 seconds of test-run time. It detects a missing snapshot, then approves the
draft and reads a finite captured order through the resident market. The combined filter covered the default draft,
rejection of Fresh Universe creation, reuse and reconfiguration after wipe, and saved settings.
[Validation](capsuleers.md#activity-funded-pvp-ships)

### Original setup rollout

The launcher API binds before world reconciliation. Anonymous `GET /api/v1/server` adds
`worldStatus` with `setup_required`, `starting` or `ready` and an optional `worldSetupUrl`.
Setup or startup returns HTTP 503 with `world_setup_required` or `world_starting` for account and launch
operations. Ready servers keep the existing v1/v2 response shape with these additive fields. The launcher
treats older metadata with the field omitted as ready and validates remote setup links as HTTPS.

The server descriptor also includes optional `administrationUrl` once the administration listener is running.
The launcher uses this address for **Server admin** on ready worlds. Remote launchers receive the configured
HTTPS administration origin. Loopback-only links are available for local connections. The admin-link change passed
7 server cases in 14.56 seconds (launcher API, remote administration and first-start setup) and 40 launcher
cases in 3.83 seconds (API and view models).

The setup-only administration host uses the existing administrator and antiforgery protection. It persists
the selected Fresh or Established configuration in the stopped world before initialization. Startup activates
the approved revision and removes the setup record in one transaction. A wiped checkpoint records when the
next boot requires setup. Reuse keeps the staged revision, and the confirmation checks its configuration
generation against the final checkpoint. The backup captures the old state before the change.

See the [operator flow](../world-configuration.md#first-start) and [wipe choices](../world-wipe.md).

**Automated validation.** The server filter covered administration, wipe, launcher API/accounts, startup,
configuration persistence, character creation and the Fresh Universe restart case. It passed 90 cases
with two opt-in skips in 57.30 seconds. An earlier run found an outdated population-page label assertion,
which reproduced on baseline `721abebc` and was excluded from that filter. The installed build 3662168
fresh-population case passed separately in 21.81 seconds. The final setup, wipe and readiness filter passed
all 11 cases in 14.66 seconds. The launcher filter covered view models, API and launch, groups, CA downloads
and TLS trust. It passed 56 cases in 19.93 seconds.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Approve a new world from the launcher's admin link and exercise both wipe choices.

## Administration settings with decimal-comma locales, November 20, 2026

On a French-configured server, changing **Warp speed** on `localhost` caused the administration reload banner before
the draft was submitted. Field validation tried to read the fixed minimum `0.1` using the server's regional format and
threw, terminating the Blazor connection. The settings form now parses its fixed decimal limits using the invariant
culture. Valid fractional edits remain interactive, and out-of-range values show field validation messages.
[Microsoft .NET RangeAttribute documentation](https://learn.microsoft.com/dotnet/api/system.componentmodel.dataannotations.rangeattribute.parselimitsininvariantculture)

**Automated validation.** Before the fix, `WorldSettingsPageTests.DecimalRangesAcceptFractionalLimitsInFrenchServerCulture`
threw while parsing the minimum, and `AdministrationTests.LiveSettingsStayInteractiveInFirefoxAndChromium` showed the
reload banner on the first decimal edit in Firefox. Both passed after the change. The browser check edits and saves
against isolated resident worlds using Firefox on `localhost` with `fr-FR` and Chromium on `127.0.0.1` with `en-US`.
The final filter joined `AdministrationTests`, `WorldSettingsPageTests`, `AdministrationComponentTests`,
`WorldConfigurationTests` and `WorldTuningTests` with `FullyQualifiedName~` and `|`. It passed all 115 cases in
38.46 seconds. Tool and solution restore, the build (5.08 seconds, zero warnings or errors), both Python policy suites
(8 catalog and 9 formatting tests), and formatting verification passed.

**Live acceptance.** Not performed. Agent game client testing is disabled.

**Optional maintainer checks.** Update the reporter's server and edit the same setting in their browser.

### English (South Africa) follow-up

A later report saw the same banner on a build with the decimal validation fix. Firefox and Chromium both passed edits,
fractional range validation, and repeated saves on `localhost` with `en-ZA` in both the server and the browser.
The cause on the reported installation is still unknown. Eden now writes Blazor circuit warnings and errors to the server console so
the exception behind a future reload banner can be identified. The browser still shows the generic banner.
[ASP.NET Core circuit error handling](https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Components/Server/src/Circuits/CircuitHost.cs#L636-L664)

**Automated validation.** The exact browser method passed in 39.31 seconds and the exact
`AdministrationTests.AdministrationLogsCircuitFailuresWithoutEnablingRoutineFrameworkMessages` check passed in 5.99 seconds.
The final `FullyQualifiedName~` filter joined `AdministrationTests`, `WorldSettingsPageTests` and
`AdministrationComponentTests`. All 50 tests passed in 44.61 seconds with the opt-in browser checks enabled.
Tool and solution restore, the build, both Python policy suites, and formatting passed in 111.39 seconds including the tests.

## Minimum corporation age for anchoring, November 16, 2026

`settings.structures.minimumCorporationAgeDays` now controls the Upwell anchoring age gate. The default is **7** days,
including historical documents that omit the property; **0** removes the wait. Negative values are rejected by both
the typed policy validator and the administrator form. The setting appears under **Structures**, in revision
comparisons and in export/import, and activates on restart with the other saved-world policies.

Deployment reads the active resident world's settings and checks age through a typed Gameplay rule. The zero setting
disables only the age check; player-corporation membership and structure-management authority remain enforced.
Saved ticks preserve the exact seven-day boundary. Decimal tick arithmetic avoids overflow for large
configured day counts. Error messages now distinguish missing authority from insufficient corporation age.

The default matches the [EVE University Upwell corporation requirement](https://wiki.eveuniversity.org/Upwell_structure#Deploying_an_Upwell_Structure).
The override is an Eden policy. Local EVE.js **261108.1** `sovPlayerDeployment.js` was consulted for the existing
corporation deployment authority boundary; no client RPC or Destiny simulation changes are needed.

**Automated validation:** The exact-method filter joined
`WorldSettingsPageTests.FormRoundTripsAllRatesAndReportsErrorsForHiddenActivityFields` and
`WorldTuningTests.RestartExportImportRestoreAndInvalidDraftsPreserveWorldIsolationAndHistory` using
`FullyQualifiedName~` and `|`; both cases passed in 5.41 seconds. The final filter joined `StructureTests`,
`StructureRulesTests`, `StructureDeploymentDefaultsTests`, `WorldSettingsPageTests`, `WorldConfigurationTests` and these
four `WorldTuningTests` methods: `ImportAndCommandsShareBoundsForEveryRate`,
`InvalidPendingRateRetainsActivePoliciesAndItsHistoricalDocument`,
`LegacyDocumentsDefaultToOneAndEveryRateRejectsInvalidValues` and
`RestartExportImportRestoreAndInvalidDraftsPreserveWorldIsolationAndHistory`, with the same filter operators.
All **137 existing cases passed in 37.11 seconds**. Existing rule, form and configuration checks were extended for zero,
negative values, the seven-day boundary, large day counts, omitted defaults and configuration persistence; no test cases
or classes were added. Tool and solution restore, solution build (zero warnings/errors, 4.20 seconds), both Python policy
suites (8 catalog and 9 formatting cases), formatting and whitespace checks passed. The full .NET suite was not run.
An integration build on the updated main branch also passed with zero warnings/errors in 8.40 seconds.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** After deploying the update, set the value to 0, save for restart, restart and retry
anchoring with the new corporation.

## Custom gameplay switches, November 15, 2026

Saved configuration now owns five independent switches for ordinary empire LP-store datacores, simulated reference
market history, added regional-hub blueprint/command-center supply, beginner market supply and civilian starting
equipment. All default to true, including omitted properties in historical revision documents. The existing
`capsuleers.enabled` master switch is exposed as **Enable player bots** and covers the complete simulated population.
The [administrator guide](../world-configuration.md#custom-gameplay-options) lists JSON paths and UI locations.

The administrator form, original/active/pending comparison and authenticated save/export/import paths carry every
switch. Saving stages a pending revision. Startup activates it before reconciliation. The hub-supply flag participates
in the shared catalog offer-cache key and institutional preparation identity, so cached extra offers retire under a
disabled policy. Disabled beginner supply retires generated unclaimed products on preparation. Original NPC suppliers,
production-unlocked products and purchased assets retain their ordinary eligibility. Disabled LP extensions reject
purchases while militia offers remain available. History keeps actual local executions, with generated rows omitted.
Starting equipment applies during character creation. Bot pause/resume uses existing saved suspension and ownership.

Gameplay references reviewed during the audit include [EVE University invention](https://wiki.eveuniversity.org/Datacores),
[blueprints](https://wiki.eveuniversity.org/Blueprint_Original) and [skillbooks](https://wiki.eveuniversity.org/Skillbooks).
The options select existing Eden policies against the pinned build-3662168 catalog and service contracts.

**Automated validation.** Tool/solution restore and the solution build passed with zero warnings/errors
(9.84-second build). The final affected .NET run passed **350 tests**, with no skips, in 92 seconds.
It includes automated Firefox and Chromium editing, tab changes, save/reload and retained checkbox values, plus
resident rejection, restart, checkpoint/reopen and bot suspension coverage. Both Python policy suites passed
(8 catalog checks and 9 formatting checks). Formatting and line-width verification passed. The final test/policy/format
segment took 122.99 seconds. The preceding restore/build and broader affected run took 143.88 seconds, stopping
before policy/format checks when three existing cases failed. That run passed 450 cases and skipped eight optional
package tests. No full .NET suite or interactive gameplay session was run.

Each excluded case reproduced independently on unchanged baseline `1c50db66444adc74ba7c485ea240dbf158185073`:

- `Market.NpcRestingOrderTests.MaintenanceUsesPricePriorityAndRejectsDuplicateConsumptionAfterRollback` leaves its
  injected settlement-rejection trigger active when resident checkpoint disposal occurs.
- `Market.RegionalMarketTests.FailedPaymentRollsBackStockGoodsAndHistoryAndRetrySettlesOnce` fails cleanup with
  `world.sqlite.owner` still held.
- `Capsuleers.CapsuleerPopulationSettingsTests.RestartSuspendsAndReusesSavedPilotsWithoutChangingTheirPossessionsOrReplayingJobs`
  fails its final immediate resumed-hauler visibility assertion. Its preceding asset, suspension, disabled scheduling,
  restart and deadline checks pass. Other population-policy cases pass in the final run. Immediate resumed visibility
  remains outside the passing validation result.

The first exact development filter was
`FullyQualifiedName=Eden.Protocol.Tests.Administration.WorldSettingsPageTests.CustomPolicyControlsRoundTripAndReportEveryDisabledOption`.
It passed one case. A separate exact authenticated-page save/export/compare regression passed one case, and the
focused resident development run passed eight cases. The final affected filter ran with `EDEN_TEST_ADMIN_BROWSER=1`:

```text
(FullyQualifiedName~Eden.Protocol.Tests.Market|FullyQualifiedName~Eden.Protocol.Tests.Administration.AdministrationTests|FullyQualifiedName~WorldSettingsPageTests|FullyQualifiedName~WorldConfigurationTests|FullyQualifiedName~CharacterCreationTests|FullyQualifiedName~WorldInitializationTests|FullyQualifiedName~CapsuleerPopulationSettingsTests|FullyQualifiedName~GameplayDependenciesTests)&FullyQualifiedName!~MaintenanceUsesPricePriorityAndRejectsDuplicateConsumptionAfterRollback&FullyQualifiedName!~FailedPaymentRollsBackStockGoodsAndHistoryAndRetrySettlesOnce&FullyQualifiedName!~RestartSuspendsAndReusesSavedPilotsWithoutChangingTheirPossessionsOrReplayingJobs
```

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** None.

## Published administration framework assets, November 15, 2026

The remote administration page rendered its header links and settings categories, but category changes and form
buttons were inert even after a hard refresh. Read-only inspection of the deployed main revision `05a66e75` found
`/_framework/blazor.web.js` returning HTTP 404. The container's `wwwroot` contained icons but no framework scripts.

Docker restores the copied project files before copying Razor sources. The pinned .NET 10 SDK infers
`RequiresAspNetWebAssets` from the presence of `.razor` content at restore time; the project-only restore therefore
omitted `Microsoft.AspNetCore.App.Internal.Assets`. Publishing with `--no-restore` retained that incomplete graph.
The server project now sets `RequiresAspNetWebAssets` explicitly, and the Docker publish step rejects a missing
framework script. No gameplay, configuration revision, authentication or transport policy changed.

References: [Microsoft's Blazor project structure](https://learn.microsoft.com/en-us/aspnet/core/blazor/project-structure?view=aspnetcore-10.0)
and the [upstream project-only Docker restore issue](https://github.com/dotnet/aspnetcore/issues/69341).
The owning SDK rules were also inspected in `Microsoft.NET.Sdk.Web.ProjectSystem.targets` and
`Microsoft.NET.Sdk.FrameworkReferenceResolution.targets` for SDK 10.0.403.

`python tools/Test-AdministrationPublish.py` reproduces Docker's restore-before-sources sequence in an isolated
temporary tree, checks the restored framework-assets dependency, publishes without another restore, and verifies
both Blazor script payloads and endpoint mappings. It failed before the property change and passed afterward
(8.24 seconds for the passing invocation). Automated Firefox and Chromium checks now exercise every category
before and after saves, preserving Travel draft edits across tab changes and verifying their saved values.

Automated validation: project-only restore/publish regression passed after first reproducing the missing-assets
failure. Tool/solution restores and the solution build passed with zero warnings/errors (1.10-second build).
All 40 affected .NET tests passed without skips in 18 seconds, including the expanded automated Firefox and
Chromium scenarios. Both Python policy suites (8 catalog and 9 formatting tests) and formatting verification passed.
The full .NET suite was not requested or run. Final filter with `EDEN_TEST_ADMIN_BROWSER=1`:

```text
FullyQualifiedName~Eden.Protocol.Tests.Administration.AdministrationTests|FullyQualifiedName~Eden.Protocol.Tests.Administration.WorldSettingsPageTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting.GameplayDependenciesTests
```

Remote verification: deployed `71923416` through the existing Docker Compose configuration. The replacement
container contains the framework script, reports all listeners ready, and serves both `/login` and
`/_framework/blazor.web.js` with HTTP 200; the JavaScript payload is 200,645 bytes. These public asset checks
do not establish authenticated browser interaction on the deployed world. Local automated browser tests use
disposable credentials and worlds.

Live acceptance: Not performed; agent client testing is disabled.

Optional maintainer checks: refresh the administration page and select Travel after updating the server.

## Mining hold capacity, November 14, 2026

The Gathering page stages independent capacity multipliers for dogma attributes 1556 (general mining), 3136 (ice)
and 1557 (gas). Each accepts 0.1–100× and defaults to 1×, including on older configuration documents. The active revision
scales the fitted attributes used by mining, inventory transfers and ship primes. The operation preserves the hull’s
existing hold selection and skill bonuses. The multiplier also applies when staging a cached simulated-pilot ship fit.

Inert inspection of the original build 3662168 archive (SHA-256 `37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`)
confirmed that `invCacheContainer.GetCapacity` resolves the hold attribute through `inventoryFlagsCommon` and asks
`clientDogmaIM.GetCapacityForItem` for the value on the loaded dogma item. `infosvc.GetAttributeDictForItem` can also read
the fitted dogma item. `dogmaIM.GetAllInfo` and `ItemGetInfo` send the effective values. The existing ship brain transmits
item-specific multiplication for the client’s fitting graph. `baseDogmaLocation._IsShipEffectValid` filters item modifiers
to attributes present on the hull’s catalog type. The client can still show base catalog values for type-only views or when
an item has no loaded local fitting graph. The Evejs 261108.1 specialized hold registry was checked against the pinned
attribute mapping. [EVE University distinguishes base and skill-adjusted capacity on the Retriever](https://wiki.eveuniversity.org/Retriever).

**Automated validation.** The focused .NET run passed 514 cases in 106.76 seconds. It covered settings saves, legacy
imports, restart activation, both fitted dogma replies and the client’s ship brain, capacity-limited harvesting and
inventory moves, and simulated-pilot fit caching. The final filter selected the following classes and method under
`Eden.Protocol.Tests` and excluded `RoutineProjectionPersistsNewResidentFounderBeforeItsDependents`. Two disconnect
cases in that method failed at the same founder lookup both with this change and on baseline `af6d625a`. Its third
case passed on both. Tool and solution restore and the solution build passed with zero build warnings and errors.
Catalog policy passed 8 checks and formatting policy passed 9. Formatting verification passed in 38.61 seconds.

```text
Persistence.MiningHoldConfigurationTests
Persistence.PilotBrainTests
Persistence.PilotFittingTests
Persistence.WorldConfigurationTests
Persistence.WorldTuningTests
Persistence.ShipInventoryRulesTests
Persistence.UtilityHullOperationTests
Administration.WorldSettingsPageTests
Administration.AdministrationTests.SettingsSaveUpdatesStatusAndKeepsControlsInteractiveAcrossRepeatedSaves
Mining.MiningTests
Mining.IceMiningTests
Mining.GasMiningTests
Mining.MiningSkillPointRewardTests
Capsuleers.CapsuleerShipBlueprintTests
Capsuleers.ResidentAdmissionTests
```

**Live acceptance.** Not performed. Agent client testing is disabled.

**Optional maintainer check.** Set the general mining hold to 2×, save and restart, then open an active Venture’s
inventory and ship attributes to compare its 10,000 m³ capacity.

## NPC bounty display, November 13, 2026

The original-bounty SP behavior below is historical and is superseded by
[multiplied bounty SP](combat.md#multiplied-bounty-sp--november-15-2026).

The in-space Selected Item panel and Show Info for a visible NPC display the gross ISK bounty from the active world revision.
The NPC scene snapshot and additions carry the same catalog base, role eligibility and cents rounding used at settlement.
The signed login adapter makes the native bounty helper read that amount from the NPC's slim item. It accepts an explicit
zero and falls back to the native helper for type-only lookups or items without an Eden bounty. Save the setting, restart the
updated server and reconnect to receive the adapter. The NPC kill skill-point rate remains independent and uses the original
catalog bounty. A 3,000 ISK pirate with 2× bounty and 1× kill SP shows and pays 6,000 ISK before tax and grants 7 unallocated SP.

The original build 3662168 archive (SHA-256 `37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`)
shows that `ActiveItem.CheckBounty`, the alternate Selected Item panel and the Show Info header import `get_entity_bounty`.
That helper reads catalog dogma attribute 481 and can apply a catalog owner multiplier. The build's native system bounty
percentage uses a separate service. Evejs 261108.1 was consulted for its NPC slim payload and separate dynamic bounty reply.
The [Carbon Destiny AddBall binding](https://github.com/carbonengine/destiny/blob/114e89fa584f0e1a81eb0a679aabf41af8870072/src/Ballpark_Blue.cpp)
was consulted for the scene boundary. [EVE University ratting reference](https://wiki.eveuniversity.org/Ratting)

**Automated validation.** Five exact cases passed in 14.44 seconds. They exercise pending versus activated rates, the wire
amount in initial and added NPCs, gross settlement and independent silent SP in both file-backed and resident worlds, and the
pinned client's two Selected Item methods in the headless Python 2 runtime. The encrypted login test was updated to use the
session packet reader after the signed challenge exceeded the test's 16 KiB plaintext-handshake limit.

The broader focused filter passed 471 tests in 300.72 seconds on the fitting update `c9a42c3b`. After integrating contract
search update `db52fbb0`, 18 bounty, login and contract-search tests passed in 16.63 seconds. The final solution build reported
zero warnings or errors. Both Python policy suites (8 catalog and 9 formatting checks) and formatting verification passed.

```text
FullyQualifiedName~WorldTuningTests|FullyQualifiedName~WorldConfigurationTests|FullyQualifiedName~WorldSettingsPageTests|FullyQualifiedName~ConcordOverviewTests|FullyQualifiedName~PilotLoginTests|FullyQualifiedName~TypedFlightTests|FullyQualifiedName~NpcEncounterTests|FullyQualifiedName~NpcSceneReadTests|FullyQualifiedName~NpcCatalogSharingTests|FullyQualifiedName~LiveSimulationTests|FullyQualifiedName~LiveCombatObjectTests|FullyQualifiedName~PilotCombatTests|FullyQualifiedName~CorporationWalletTests|FullyQualifiedName~MissileCombatTests|FullyQualifiedName~CombatDroneTests|FullyQualifiedName~ApplyFittingTests|FullyQualifiedName~BlueMarshalTests
FullyQualifiedName~WorldTuningTests.NpcBountyDisplayAndSettlementUseActivatedIskRateAndIndependentKillSkillPoints|FullyQualifiedName~ConcordOverviewTests|FullyQualifiedName~PilotLoginTests|FullyQualifiedName~ContractSearchPerformanceTests|FullyQualifiedName~ContractSearchSessionTests
```

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** After restarting and reconnecting, select a pirate in the overview and compare the displayed
amount with its gross Wallet bounty. Reopen the skill queue to see the updated unallocated SP balance.

## Remote administration in Docker, November 13, 2026

The Compose admin override publishes TCP 26003 on an explicit host address. Eden serves HTTPS using the deployment PFX and
checks the configured browser scheme, hostname and port, including the Origin header when supplied. The admin login uses
secure cookies and retains the separate credentials, rate limit, antiforgery checks and credential-stamp validation.
The default listener still requires a loopback peer and local Host. [Docker setup](../docker.md#remote-administration)

**Automated validation.** The exact remote HTTPS test passed in 6.10 seconds including runner startup. The final focused run
passed 102 tests in 26.73 seconds with four opt-in mod or packaging scenarios skipped. It covered HTTPS login and cookies,
Host and Origin rejection, Blazor negotiation and the existing Firefox and Chromium interactivity checks. A stale test
expectation for the removed population panel was corrected after the first focused run. Tool and solution restore, the
warning-free build, both Python policy suites (8 catalog and 9 formatting checks) and formatting verification passed.
Docker Compose 5.7.1 resolved the base and opt-in configurations with the expected port mappings. The full .NET suite was
outside the focused selection.

```text
FullyQualifiedName~Eden.Protocol.Tests.Administration|FullyQualifiedName~Eden.Protocol.Tests.Launcher.LauncherApiTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting.ServerLifetimeTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting.ServerDataPathsTests
```

**Live acceptance.** Not performed; agent client testing is disabled. This workstation has no Docker Engine for a container
deployment check.

**Optional maintainer checks.** Open the configured HTTPS address from another machine and sign in to verify the deployed
certificate, port mapping and interactive connection.

## Administration save interactivity — November 11, 2026

Saving settings in an interactive administration connection could commit a pending revision and its checkpoint while leaving
the page unresponsive. Refreshing opened another connection and displayed the saved revision. The synchronous durable-close
boundary waited on `LiveWorld.FlushAsync`, whose checkpoint continuation captured the blocked Blazor synchronization context.
The checkpoint wait now resumes independently of its caller's synchronization context, so save completion can return to the
page and update its status. Revision transactions, durable-save failure propagation and restart activation retain their terms.

A deterministic checkpoint test forces an incomplete save under a recording synchronization context and asserts that no
continuation is posted back to it. It failed before the correction and passed afterward. Interactive renderer tests exercise
repeated saves, status changes, tab switching, reset, discard and validation against both file-backed and resident worlds.
An optional automated Playwright test exercises Firefox and Chromium against an isolated resident-world administration host,
including checkbox and numeric edits, invalid-input reset and reload of saved values. Enable it with
`EDEN_TEST_ADMIN_BROWSER=1` after installing Python Playwright and its Firefox/Chromium browsers; the test owns its disposable
world and never uses an installed administrator account.

**Automated validation:** The exact development renderer filter was
`FullyQualifiedName=Eden.Protocol.Tests.Administration.AdministrationTests.SettingsSaveUpdatesStatusAndKeepsControlsInteractiveAcrossRepeatedSaves`.
The regression filter
`FullyQualifiedName=Eden.Protocol.Tests.Persistence.LiveWorldTests.DurableFlushDoesNotCaptureTheCallingSynchronizationContext`
failed before the fix (one unwanted synchronization-context post) and passed after it (zero posts and the expected disk state).
The final affected filter below passed **61 tests, zero failures/skips**, including the opt-in Firefox and Chromium scenarios,
in **43.66 seconds including runner startup**. Tool restore, solution restore/build (zero warnings/errors), those focused tests,
both Python policy suites (8 catalog and 9 formatting checks), and formatting verification passed in **101.80 seconds combined**.
No full .NET suite was requested or run.

```text
FullyQualifiedName~Eden.Protocol.Tests.Administration.AdministrationTests|FullyQualifiedName~AdministrationComponentTests|FullyQualifiedName~WorldSettingsPageTests|FullyQualifiedName~WorldConfigurationTests|FullyQualifiedName~LiveWorldTests|FullyQualifiedName~CheckpointOwnershipTests|FullyQualifiedName~DetachedCheckpointTests
```

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** After restarting the updated server, save a settings change in the existing Firefox session
and confirm the pending-restart status appears without refreshing.

## World tuning — November 9, 2026

The original-bounty SP policy recorded here is superseded by
[multiplied bounty SP and configurable base rates](combat.md#multiplied-bounty-sp--november-15-2026).

The existing saved-world revision system now carries decimal player gathering, reward and industry multipliers. Historical
format-version-1 documents omit these fields and receive neutral 1× defaults. Revision documents remain immutable; save, import,
restore, generation checks and restart activation retain their existing ownership. Invalid pending settings retain the previous
active configuration and record an activation failure. Live operations read only the world's active in-memory snapshot.

Gathering applies once after ordinary module/hull/skill/crystal calculations and before collection. Native dogma primes receive
the same effective mining amount. The client brain receives independent Mining and Ice Harvesting required-skill multipliers;
these selectors are mutually exclusive for admitted tools in the pinned catalog. Gas groups have independent multipliers.
Modulated ore tools also receive
the matching mining-specialty amount. Source catalog records and cached catalog profiles are unchanged.

Industry time policies use the same typed character modifiers for authoritative job planning and the native character brain.
A speed multiplier `s` becomes a duration percentage `100 × (1/s − 1)`. These world modifiers have no trained-skill source;
their native `skills` lists are empty. Both research activities use the research setting. Existing installed jobs retain saved
durations and deadlines, and invention probability/materials/output runs remain unchanged. Native `Job._get_time` rounds
subsecond previews to zero; installation normalizes an exact zero quote to the one-second floor before authoritative validation.
Negative, nonfinite, stale and otherwise incorrect time quotes remain rejected.

NPC destruction settles scaled gross bounty, corporation tax and independently scaled original-bounty SP in the same existing
transaction. Zero ISK bounty can still award silent SP, and repeated kills cannot replay rewards. Mission policies are captured
at offer creation; saved offers/accepted missions are never repriced. Reprocessing quotes and mutations share the capped
efficiency calculation. Autonomous NPC mining and production do not consume the new player rates.

The local administrator page uses six persistent activity panels, a shared restart-save bar, per-control resets, changed-value
summaries and an expandable original/active/pending comparison. Server validation remains authoritative for API and command
imports. Training-speed tuning is deferred; this milestone does not require a signed client package update.

### Sources

Inert inspection of staged build 3662168 `eve/common/script/dogma/effect.pyj` (`BrainEffect.GetLiteralKey`) and
`industry/job.pyj` (`Job._get_time`). The inspected staged archive, which includes existing Eden client adapters, has SHA-256
`76E969D2CDE0AC25359F0F9194725F741C5DB5D6C24F552B72B2184457DA6DC4`.
This milestone does not edit that archive.
Literal source keys retain `(tuple(skills), value)`; the native modifier forms and Blue serialization are unchanged.
Consulted [Carbon Blue Marshal.cpp at af0d65b29f951260ea09140ca11adf5d40566997](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/Marshal.cpp).

Gameplay references: EVE University's [mining yield](https://wiki.eveuniversity.org/Mining_Yield),
[missions](https://wiki.eveuniversity.org/Mission), [manufacturing](https://wiki.eveuniversity.org/Manufacturing) and
[reprocessing](https://wiki.eveuniversity.org/Reprocessing). The exposed multipliers are Eden world policies layered onto
the pinned catalog; they are not claims about live EVE server configuration.

### Acceptance

**Automated validation.** The first exact development filter was
`FullyQualifiedName=Eden.Protocol.Tests.Persistence.WorldTuningTests.LegacyDocumentsDefaultToOneAndEveryRateRejectsInvalidValues`.
After correction of a missing test import it passed (one case; 5.16-second build and 2.34-second runner).
Development checks also corrected a stopped-world fixture setup and the activity buttons' rendered ARIA boolean values.

The broader affected filter below passed **696 cases, zero failures/skips**, in 292.15 seconds including runner startup
(4 minutes 49 seconds reported test duration). Pinned tool restore, solution restore/build (zero warnings/errors),
both Python policy suites (8 catalog and 9 formatting checks), formatting and patch checks passed in **324.29 seconds combined**.

```text
FullyQualifiedName~WorldConfigurationTests|FullyQualifiedName~WorldTuningTests|FullyQualifiedName~WorldSettingsPageTests|FullyQualifiedName~AdministrationComponentTests|FullyQualifiedName~AdministrationTests|FullyQualifiedName~MiningTests|FullyQualifiedName~MiningRulesTests|FullyQualifiedName~MiningTierTests|FullyQualifiedName~MiningCrystalTests|FullyQualifiedName~IndustryTests|FullyQualifiedName~ManufacturingRulesTests|FullyQualifiedName~ScienceRulesTests|FullyQualifiedName~ScienceJobTests|FullyQualifiedName~ReprocessingTests|FullyQualifiedName~PilotBrainTests|FullyQualifiedName~PilotCombatTests|FullyQualifiedName~MissileCombatTests|FullyQualifiedName~PilotMissileTests|FullyQualifiedName~CombatDroneTests|FullyQualifiedName~CorporationWalletTests|FullyQualifiedName~MissionRulesTests|FullyQualifiedName~MissionPersistenceTests|FullyQualifiedName~MissionProtocolTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~ServerServicesTests|FullyQualifiedName~GameplayDependenciesTests
```

A subsequent native-selector refinement uses independent Mining and Ice Harvesting selectors instead of an ore-group/ice-ratio
combination. The final focused filter below passed **54 cases, zero failures/skips**, in 10.45 seconds plus a 4.90-second build.
It includes both pinned mining catalog cohorts, native brain serialization and world-tuning/configuration/UI scenarios.
The successful 696-case run preceded this refinement; the affected native/default paths were then covered by the focused run.
The final solution build, both Python suites and formatting verification passed in **23.25 seconds combined**.

```text
FullyQualifiedName=Eden.Protocol.Tests.Mining.MiningTierTests.EveryAdmittedToolHasAnIndependentNativeWorldYieldSelector|FullyQualifiedName~PilotBrainTests|FullyQualifiedName~WorldTuningTests|FullyQualifiedName~WorldSettingsPageTests|FullyQualifiedName~WorldYield|FullyQualifiedName~WorldGasYield|FullyQualifiedName~WorldIceYield|FullyQualifiedName~WorldSettingsPageRenders
```

Tests cover legacy/default compatibility and every multiplier bound, history/restart/failed activation/world isolation,
player ore/ice/gas collection and unchanged NPC mining, independent silent ISK/SP settlement and tax/rollback,
saved mission offer terms, industry quote normalization/deadlines and capped reprocessing outputs. The full .NET suite
was neither requested nor run. Native rendering, input and gameplay feel remain unverified.

After integrating concurrent market commit `809c9ea5`, the overlap filter below passed **178 cases, zero failures/skips**,
in **32.99 seconds including runner startup**. Solution restore/build (zero warnings/errors), both Python suites
(8 and 9 checks), and formatting verification across 1,540 files passed with those tests in **63.54 seconds combined**.
This integration run covers the final code with the market update; the documentation-only acceptance entry was added afterward.

```text
FullyQualifiedName~WorldConfigurationTests|FullyQualifiedName~WorldTuningTests|FullyQualifiedName~WorldSettingsPageTests|FullyQualifiedName~AdministrationComponentTests|FullyQualifiedName~AdministrationTests|FullyQualifiedName~IndustryTests|FullyQualifiedName~ManufacturingRulesTests|FullyQualifiedName~ScienceJobTests|FullyQualifiedName~ScienceRulesTests|FullyQualifiedName~ReprocessingTests|FullyQualifiedName~PilotBrainTests|FullyQualifiedName~MarketValuationTests|FullyQualifiedName~MarketReferenceHistoryTests|FullyQualifiedName~EconomicLifecycleTests
```

Live acceptance: Not performed; agent client testing is disabled.

Optional maintainer checks:

1. Review tab switching, invalid-input reset and pending-restart status in the administrator page.
2. Compare native ore/ice/gas module information with actual completed-cycle extraction under custom rates.
3. Compare native manufacturing/science previews and installed deadlines, including the one-second minimum.

## Optional data imports in server administration

The authenticated **Optional data** page stages the existing [EVE Ref market snapshot](industry.md#standard-live-order-market-and-unified-economy-settings-november-16-2026)
and [zKill traffic snapshot](capsuleers.md#zkill-backed-traffic--november-8-2026) beside the selected world using its active
catalog. The provider format and catalog compatibility rules are shared with the command-line importers.
The [EVE Ref dataset format](https://docs.everef.net/datasets/market-orders.html) and
[zKillboard archive documentation](https://zkillboard.com/api/docs/) describe the external inputs. Reference checks also
covered EVE.js `averageMarketPrices.js` and `killmailItemPayload.js` in the local checkout, which has no Git revision
metadata. This administration change uses Eden's existing providers and introduces no native client calls or saved gameplay changes.

The page shows the installed market capture and traffic archive range. Administrator-authenticated, antiforgery-protected
forms start background imports. The host runs one web import at a time, and jobs outlive the browser session. The traffic
command shares the same download/cache and publication path with the page. The market import uses the packaged Python
script in a staging directory and publishes the validated manifest after moving its generation into place. Previous snapshots
stay installed on failure. The server adopts the files on restart.

**Automated validation.** The focused filter joined `AdministrationTests`, `AdministrationComponentTests`,
`TrafficImportTests`, `TrafficSnapshotTests` and `StandardMarketTests` using `FullyQualifiedName~` and `|`.
In 26.56 seconds, 101 passed and the opt-in browser test was skipped. The exact optional-data page test passed
again in 11.25 seconds after the final copy update. It checks missing-data explanations, administrator and
antiforgery gates, and an import failure that preserves the installed traffic snapshot. The Standard market
refresh test checks staging, preservation of the old capture until restart, and use of the new stock after reopen.
The solution built with zero warnings and errors. Eight catalog policy, nine formatting policy and four offline
market importer checks passed. The Docker publish helper verified the packaged market importer in 125.74 seconds.
Formatting passed in 41.04 seconds. Documentation links and whitespace checks passed.
After rebasing onto `9e2a7cdd`, the solution build and the same focused filter passed in 37.91 seconds with 101
passing tests and one opt-in skip. All three Python checks passed again.

**Live acceptance.** Not performed. Agent client testing is disabled. Provider downloads were not exercised.

**Optional maintainer checks.** Import a snapshot on the server and verify that Eden adopts it on restart.
