# Saved-world gameplay tuning

[Documentation](../README.md) / [Protocol index](../protocol.md) / [Administrator guide](../world-configuration.md)

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
