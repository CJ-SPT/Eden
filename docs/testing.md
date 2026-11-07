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

## Continuous integration

GitHub validation runs hosting/fixture smoke tests, build, policy and formatting checks on pushes and pull requests. Its full .NET suite is opt-in through the manual workflow's `regression_check` input; normal pushes do not start it. Contributors still run the relevant feature filters locally.

## Test infrastructure

The tests use fixtures and loopback sockets. The test runner allows twelve concurrent collections with the conservative scheduler; see [xUnit runner configuration](https://xunit.net/docs/config-xunit-runner-json). This limit was selected for the development machine's 14 cores / 20 hardware threads. Scratch databases default to the ignored `artifacts/test-data` directory on the repository drive; set `EDEN_TEST_TEMP_DIRECTORY` to select another location. Tests copy an immutable base catalog into independent writable files and reuse parsed catalog fixture definitions. Fitting helpers read identity fields directly instead of rebuilding pilot and fitting projections for each lookup. World initialization, migrations and gameplay assertions still exercise the real implementation. The previous four-worker full suite took approximately 36 minutes; this is historical context, not a runtime guarantee.

## Reporting and publishing

Agents perform **automated testing only**: no game launch, Computer Use, interactive gameplay acceptance, or extended manual server-testing substitute unless the user explicitly requests live testing. Report automated checks, results, elapsed time, and limitations separately from live acceptance: **"Not performed; agent client testing is disabled."** Provide at most three optional maintainer actions when visual or gameplay checks remain. Native rendering, input, and gameplay feel remain unverified until tested. Automated validation and confirmation that the milestone commit is on the remote branch establish completion; live acceptance is not required. See the [contributor testing policy](../AGENTS.md#testing).

## Documentation changes

Check local links and section anchors, verify relocated records and historical acceptance evidence were retained, and check UTF-8 encoding, final newlines, trailing whitespace and `git diff --check`. Documentation-only work does not require gameplay tests or live client acceptance.
