# Automated validation

[Documentation index](README.md) / [Contributor policy](../AGENTS.md#testing)

The [README build order](../README.md#build-and-run) is the quick reference. This guide retains filter selection, test infrastructure and reporting details.

## Focused tests

During an edit, run the exact rule method through the feature runner:

```powershell
.\tools\Test-Feature.ps1 -Filter 'FullyQualifiedName=Eden.Protocol.Tests.Flight.ShipCollisionTests.FixedBodyReflectsRadialVelocityUsingCarbonsDampedImpulse'
```

The runner incrementally builds the test project and its dependencies in ignored `artifacts/feature-tests`, then reports the matched count and build/test times. Empty filters and zero matches fail. Each run retains a TRX report under that output directory. The separate output allows a server using the default build directory to keep running.

Once the method passes, combine the affected classes and immediate dependencies in one filter. Run tool/solution restore, the solution build, focused tests, both Python policy suites and formatting verification when the milestone is stable, following the README order. Use `--artifacts-path artifacts/feature-tests` consistently for restore, build and test.

For new features, test calculation and rejection variants directly against typed `Eden.Gameplay` inputs. Use real worlds and simulated sessions for persistence, ownership, routing and notification assertions. This keeps data-driven rule coverage inexpensive while integration tests exercise the boundaries.

Record the filter and results. Add focused bug regressions when practical; cover expected outcomes across gameplay rules, protocol replies/notifications, and persistence as applicable. Include rejection and unchanged-state checks for inventory/ownership, reopen/restart checks for persistence, and repeated-cycle/order checks using `TestClock` and simulated sessions for timing or synchronization. Exercise catalog variants with data-driven tests.

## Regression checks

Run the full .NET suite (`dotnet test --no-build` without a filter) **only when the user explicitly requests a regression check**. Normal milestone work, merges, commits, pushes, and shared serialization, initialization or fixture changes use focused validation and do not authorize a full run. When requested, run the regression check once; do not repeat a successful full suite for unchanged code or reporting. After a correction, rerun affected tests and required build/format checks. Investigate failures instead of retrying blindly, and identify the revision or subsequent corrections covered by each result. Interrupted or incomplete runs are not passes. Documentation-only changes require relevant documentation checks rather than gameplay tests.

## Local validation

Run milestone validation locally using the README build order and the affected feature filters. The GitHub Actions validation workflow was removed to avoid consuming hosted runner minutes.

## Test infrastructure

The tests use fixtures and loopback sockets. The test runner allows twelve concurrent collections with the conservative scheduler; see [xUnit runner configuration](https://xunit.net/docs/config-xunit-runner-json). This limit was selected for the development machine's 14 cores / 20 hardware threads. Scratch databases default to the ignored `artifacts/test-data` directory on the repository drive; set `EDEN_TEST_TEMP_DIRECTORY` to select another location. Ordinary feature tests copy immutable prepared catalog and world snapshots into private writable files. The on-disk seed cache lives under `artifacts/test-data/seeds` (or `EDEN_TEST_TEMP_DIRECTORY/seeds`) and is fingerprinted from all Eden assemblies, embedded test data and the test dependency manifest. SQLite backup produces standalone snapshots, a process lock serializes publication, and checksums detect damaged or incomplete seeds. A missing or damaged seed is rebuilt with the real initializer. Each process retains immutable seed bytes after loading. Deleting the seed directory while tests are stopped forces rebuilding. Tests also reuse parsed catalog fixture definitions. Fitting helpers read identity fields directly instead of rebuilding pilot and fitting projections for each lookup. Fixtures with explicit clocks or starting funds use fresh initialization. Pass `freshInitialization: true` when a test specifically covers creation or migration. Migration fixtures use this path, and restart assertions explicitly reopen the modified private database through the real initializer. Gameplay assertions exercise the real implementation. The previous four-worker full suite took approximately 36 minutes; this is historical context, not a runtime guarantee.

## Measured feature-loop timings

The seed-cache comparison used the preserved pre-change binaries from `fc33779` and the cache implementation, with three fresh test processes per filter on the development machine. These are wall-clock medians for `dotnet test --no-build`, excluding builds. Each run selected the same assertions and test count.

| Filter suffix | Cases | Before | Prepared seed |
| --- | --- | --- | --- |
| `Flight.ShipCollisionTests.FixedBodyReflectsRadialVelocityUsingCarbonsDampedImpulse` | 1 | 1.26 s | 1.28 s |
| `Fixtures.TestCatalogTests` | 1 | 4.15 s | 3.63 s |
| `Flight.ShipCollisionTests` | 15 | 9.25 s | 8.29 s |

A separate first run against an empty cache took 3.96 seconds for `Fixtures.TestCatalogTests`. The comparison table uses the prepared cache. Changes to Eden binaries or dependency versions invalidate the seed, so the next feature run rebuilds it. EF model/query initialization and test-process startup still contribute to every fresh process. Two simultaneous processes also passed the isolation filter against one empty shared cache.

## Populated player scaling replay

`ServerPerformanceReplayTests.ReplayPopulatedWorldAtPlayerScale` is an opt-in automated replay for 1, 10, 100 and
1,000 simulated sessions. Supply a frozen directory containing `world.sqlite`, its matching `catalog.sqlite`, and
any traffic database and skill-package sidecars. The test copies these files into its private scratch directory;
it never runs against the source save directly. The template pilot must own a fitted mining ship with trained skills.

```powershell
$env:EDEN_SCALE_SOURCE = 'F:\frozen-eden-save'
$env:EDEN_SCALE_OUTPUT = 'F:\scale-results'
$env:EDEN_SCALE_TEMPLATE_PILOT = '90001835'
$env:EDEN_SCALE_SAMPLES = '24'
.\tools\Test-Feature.ps1 -Filter 'FullyQualifiedName~ReplayPopulatedWorldAtPlayerScale'
```

The fixture retains the populated world, adds independent accounts and fitted ships, and uses up to ten systems.
Ten percent of sessions mine, forty percent move, and fifty percent stay docked; the single-player case mines.
Measured mining grids are moved away from hostile belts and given sufficient ore to preserve the workload.
Three cohorts warm the sessions. Each measured cohort advances five simulation seconds and executes complete
session frames, with skill-point, movement or station calls every fourth cohort, background work every cohort,
and durable checkpoints every eighth cohort. Every miner must remain active and deliver ore; protocol errors,
background failures and failed saves fail the test.

Each `players-N.json` includes admission time, completion and execution latency, throughput, allocation,
ownership timing, checkpoint stages, background backlog and individual samples. Completion latency includes
the wait from scheduling the cohort through the frame and reply encoding. SQL, connection and fitting counts
come from a separate warmed cohort, with verbose tracing disabled during measured throughput. The clock combines
explicit advances with wall time so background CPU budgets work; executed background actions can therefore differ
between builds. This is a closed-cohort in-process replay, not a TCP load test or a guarantee of sustained live-player
capacity. An invocation without `EDEN_SCALE_SOURCE` does not run a load test.

For comparisons, preserve the complete baseline runtime output and run the same test assembly with each runtime,
sequentially on the same host and source save. Keep builds, unrelated tests and other substantial work out of the
measurement window. Record runtime revisions, build configuration and machine details with the results.

## Saved space-session frame replay

For a moving space session, `ServerPerformanceReplayTests.ProfileSavedSpaceSessionFrames`
copies the same frozen world/catalog and active skill sidecars into a private directory.
It places the pilot two million metres along X from the home-station coordinates in
the pilot's current system, starts ordinary
forward movement, warms 20 frames and measures 100 full frames, including any outgoing
packet encoding. The last ten warmup frames collect phase timings and assert zero SQL,
connection opens and fitting calculations. Measured frames disable performance tracing;
checkpoint capture/projection/copy/write costs are recorded separately afterward.

```powershell
$env:EDEN_TRACE_PERFORMANCE = '0'
$env:EDEN_FRAME_SOURCE = 'F:\frozen-eden-save'
$env:EDEN_FRAME_OUTPUT = 'F:\frame-results'
$env:EDEN_FRAME_PILOT = '90001835'
.\tools\Test-Feature.ps1 -Filter 'FullyQualifiedName=Eden.Protocol.Tests.Combat.ServerPerformanceReplayTests.ProfileSavedSpaceSessionFrames'
```

The output contains `frames.json` (individual timings, allocation, counted warmup frames,
phase summaries, final position/velocity and checkpoints) and `frames.log`. Compare the
same test assembly against preserved baseline and updated runtime dependencies, running
one replay at a time without builds or other tests in the measurement window. An unset
`EDEN_FRAME_SOURCE` only prints instructions. This replay has one moving session and no
concurrent maintenance, socket writes or active weapon/miner cycle; it does not establish
fleet capacity or live-client rendering performance.

`EDEN_FRAME_WARMUP` and `EDEN_FRAME_SAMPLES` override the default 20/100 frames; warmup
must be at least ten. For longer comparisons, use 100/1,000, disable tiered compilation
with `DOTNET_TieredCompilation=0` in both test processes, and use the same CPU affinity
for both. Keep normal scheduler results separate from these controlled measurements.
Only `CountedFrames` and `Phases` have active SQL/connection/fitting counters; the zero
counter fields in uninstrumented `Frames` are placeholders, not additional assertions.

## Saved character-selection replay

`ServerPerformanceReplayTests.ReplaySavedCharacterSelectionAndFirstTicks` copies a frozen world and catalog,
the optional traffic/skill sidecars and active skill package, then selects three pilots concurrently and
executes five first/warmed ticks per session, including normal anomaly handling and reply encoding.
It does not run background traffic maintenance or network transport. Supply a matching catalog and the
active package's catalog/metadata; never point the source at an actively written SQLite database.

```powershell
$env:EDEN_SELECTION_SOURCE = 'F:\frozen-eden-save'
$env:EDEN_SELECTION_OUTPUT = 'F:\selection-results'
$env:EDEN_SELECTION_PILOTS = '90023327,90001835,90023328'
.\tools\Test-Feature.ps1 -Filter 'FullyQualifiedName=Eden.Protocol.Tests.Combat.ServerPerformanceReplayTests.ReplaySavedCharacterSelectionAndFirstTicks'
```

Without `EDEN_SELECTION_SOURCE`, this optional test only reports setup instructions. The replay uses a fixed
November 14, 2026 clock and preserves the copied pilot locations. Its logs separate catalog/SQL commands,
fitting calculations, waits and full call/frame times. Compare the same save and pilot list sequentially;
the Windows Debug replay does not establish production latency or sustained capacity.

## Saved average-price output replay

`ServerPerformanceReplayTests.ReplaySavedAveragePriceOutput` copies the same frozen world,
catalog and active skill sidecars as the selection replay, warms the public price cache,
and sends ten complete Macho replies through production compression, encryption and
framing to a memory stream. Each decrypted/inflated frame must match the expected envelope.
It reports compression-level sizes and output-stage timings; it does not measure TCP or WAN latency.

```powershell
$env:EDEN_PERFORMANCE_SOURCE = 'F:\frozen-eden-save'
$env:EDEN_PERFORMANCE_OUTPUT = 'F:\price-output-results'
.\tools\Test-Feature.ps1 -Filter 'FullyQualifiedName=Eden.Protocol.Tests.Combat.ServerPerformanceReplayTests.ReplaySavedAveragePriceOutput'
```

Without `EDEN_PERFORMANCE_SOURCE`, this optional test only reports setup instructions.
Run baseline and updated assemblies sequentially with identical inputs and keep other
substantial work out of the measurement window. Read `price-output.log` for byte counts,
compression costs, and warm output timings; the first output also includes cold code paths.

## Reporting and publishing

Agents perform **automated testing only**: no game launch, Computer Use, interactive gameplay acceptance, or extended manual server-testing substitute unless the user explicitly requests live testing. Report automated checks, results, elapsed time, and limitations separately from live acceptance: **"Not performed; agent client testing is disabled."** Provide at most three optional maintainer actions when visual or gameplay checks remain. Native rendering, input, and gameplay feel remain unverified until tested. Automated validation and confirmation that the milestone commit is on the remote branch establish completion; live acceptance is not required. See the [contributor testing policy](../AGENTS.md#testing).

## Documentation changes

Check local links and section anchors, verify relocated records and historical acceptance evidence were retained, and check UTF-8 encoding, final newlines, trailing whitespace and `git diff --check`. Documentation-only work does not require gameplay tests or live client acceptance.
