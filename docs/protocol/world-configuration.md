# Saved-world gameplay tuning

[Documentation](../README.md) / [Protocol index](../protocol.md) / [Administrator guide](../world-configuration.md)

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
