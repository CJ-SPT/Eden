# Saved world configuration

[Documentation index](README.md)

Schema 99 adds immutable configuration revisions to each saved world. Upgrading captures its existing development supply, economic policies, adjusted prices, manufacturing indices and renewal schedule. Existing worlds retain their values; normal new-world defaults remain unchanged.

A draft saves as **pending restart**. Saving, restoring and importing use the same validation and optimistic generation check. Startup validates the pending revision before resource, market, combat and drone reconciliation. A rejected revision leaves the previous active revision in use and records its failure. Its history entry remains available; restoring a valid revision creates another pending entry. Revisions cannot be updated or deleted.

| Settings group | Editable server policies |
| --- | --- |
| Economy | Charge service costs, default manufacturing index and progressive regional markets. |
| Development supply | Testing market and initial wallet grants; institutional stock; testing stock/price; grant amount for future pilots. |
| Resources | Enable belt renewal and set its UTC hour. |
| NPC population | Enable new pirates/anomalies, guards, convoys and mining populations independently. Existing living residents remain. An optional Major Threat anomaly override enables the six advanced nullsec families without sovereignty upgrades. |
| Progression | Grant catalog starting skills to future pilots. Native training formulas remain unchanged. |
| Simulation | Checkpoint interval, market maintenance interval and resource check interval. |

Changes affect future operations. Balances, installed jobs and paid costs, quoted obligations, historical transactions and saved deadlines are not rewritten. Existing grants are not replayed. Testing NPC market orders refresh under the selected policy on subsequent market operations; personal orders and escrow keep their saved terms. Disabling a population prevents new spawns without deleting residents or changing their mechanics.

## Progressive regional markets

The Economy card exposes **Progressive regional markets**. Save the setting and restart the host to activate it. The saved `economy.progressiveMarkets` value accepts true or false. An omitted or null value follows the world's supply mode, enabling progression for economy saves and retaining configured development prices for development saves. This switch can be staged on an existing save and preserves personal orders, escrow, balances, inventories and history. Progression now means [claimed-production unlocks](protocol/industry.md#production-driven-market-progression--november-8-2026), rather than timed product unlocks. New server worlds start with testing market supply disabled; `--testing-market` explicitly creates a development market. Existing saves retain their saved supply mode.

Markets use one shared selected-human clock. Economic age and replenishment advance while at least one human pilot is selected, including while docked. Additional humans share the same elapsed interval. Training and installed industry retain their established deadline policies.

The initial policy supplies materials immediately, equipment after four active hours, and the Venture, Merlin and Badger at hubs after eight, sixteen and twenty-four hours. Stock targets grow every eight active hours through three growth stages. Finite stock and demand recover in ten-minute intervals, by one sixth of capacity per interval. Repeated reads retain partial intervals. Local shortages move quotes up to fifteen percent from their station-specific anchors. Hull availability uses the audited starter cohort; other products require supported recipes and positively priced mineral inputs. These are Eden progression defaults.

Regional quotes are prepared lazily for the current station, its regional hub and previously prepared stations in that region. Direct station trades consume the relevant finite order. Personal listings continue to trade between humans. Mining-area mineral supply and hub mineral demand provide an initial transport opportunity. Prices use saved adjusted mineral values, with pinned catalog reference values for remaining inputs, recipe quantities and output portions. Station roles use catalog market/manufacturing facilities, gate connectivity and station counts.

## Overrides and compatibility

Supported overrides change the server-supplied **adjusted price** for an existing catalog item or the **manufacturing index** for an existing system. The client obtains these values through `config.GetAverageMarketPrices` and the industry facility response. IDs must exist in the installed catalog, prices must be positive, indices must be within zero and one, and entries cannot repeat. Enabling service costs requires adjusted prices for catalog materials with positive source base prices.

These overrides are economic projections belonging to the saved world. They do not modify the original item payload, base price, recipe or shared catalog cache. Two worlds using the same catalog retain separate active and pending definitions. Native-client physics, volumes, skills, recipes, identities and schema fields remain read-only. Adding another writable field requires an explicit compatible server/client synchronization contract.

Each document carries format version 1 and the installed catalog source fingerprint. Unknown properties, incompatible catalogs and invalid settings are rejected. Decimal economic inputs retain their precision in saved documents and existing SQLite projections. Original catalog files are never written.

## Local graphical editor

Provision a separate administrator from a local terminal. This command prompts for a password and confirmation; it does not change game accounts or activate pending configuration:

```powershell
dotnet run --project src/Eden.Server -- --world 'C:\Eden\world.sqlite' --set-admin-password Corey
```

Start the server normally, then open **http://127.0.0.1:26003**. The listener starts only after an administrator has been provisioned. Use `--admin-port <port>` for a different dedicated port or `--disable-admin` to disable it. It always binds IPv4 loopback, independently of game listeners and inherited web-host configuration. Nonlocal Host/Origin values are rejected. Game accounts receive no administration access.

Settings contains typed forms for all six policy groups and supported economic overrides. It displays original, active and pending values, sources, compatibility and validation errors. Reset changes only the draft. **Save pending revision** validates and durably stages it; activation requires restart. Revisions provides comparison, restore, JSON export and validated file import. Restoring also creates a pending revision.

Catalog provides searchable pages across all 442 generated datasets, typed fields, explicit missing/null distinctions, exact source numbers and relationship navigation. Nested collections have their own display pages. Native fields remain read-only in original, active and pending views. Generated types validate records before the browser displays their original payloads; dynamic JSON stays inside this administration/storage boundary.

The editor uses a dark theme throughout settings, catalog browsing, revision history and sign-in, including native form controls, validation messages and keyboard focus indicators.

**Performance** shows live diagnostics for the Eden server process: uptime, CPU usage, working set and private memory, managed memory and allocation rate, authenticated game connections, process/thread-pool threads, queued work and cumulative garbage collections. Connected accounts include character selection; they exclude pending logins and web administrator sessions. CPU usage is normalized across the processors available to the process. CPU and allocation rates need two samples and cover the actual elapsed interval between samples. These are server-process measurements rather than total machine usage or simulation-frame latency.

The page refreshes every two seconds while open. A shared, demand-driven cache limits resource sampling to once per two seconds across all administrator viewers. Navigating away disposes that page's refresh loop. The first sample displays collecting indicators for rates. Each refresh checks the current administrator credentials; password rotation stops existing interactive access. The same read-only snapshot is available at authenticated `GET /api/admin/performance` under the existing loopback and administrator policy.

Resident hosts also report the last successful checkpoint's generation, completion time, file size and separate owner wait, capture, projection and file-write durations. Failed saves display an alert while retaining the previous successful report. Before the first save, the page says no checkpoint has completed; hosts without a resident world show diagnostics as unavailable. Reading these reports does not acquire world ownership, open SQLite connections, project simulation state, trigger saves or force garbage collection. No historical samples are stored.

Authentication uses a separate ASP.NET Core cookie scheme and administrator role. HTTP and interactive operations enforce authorization and the current credential stamp. Forms and writes require antiforgery validation. Password rotation revokes existing HTTP sessions and interactive operation access. Passwords are hashed in a separate `<world>.administrators.json` credential file. Authentication keys live in `<world>.admin-keys` and use the current Windows user's key protection on Windows. These runtime files are excluded from version control. The local password command can rotate credentials while the server runs.

The editor uses Blazor server interaction, following [ASP.NET Core Blazor security](https://learn.microsoft.com/en-us/aspnet/core/blazor/security/?view=aspnetcore-10.0) and [cookie authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/cookie?view=aspnetcore-10.0). It delegates to the same configuration validation and persistence operations as local commands. Installed catalog files and gameplay rules remain outside its ownership.

### Performance panel and dark theme validation — November 7, 2026

**Automated validation:** The initial development run used `tools/Test-Feature.ps1 -Filter 'FullyQualifiedName=Eden.Protocol.Tests.Administration.ServerPerformanceTests.SharedSamplesNormalizeCpuCacheReadersAndTrackAccountRelease'` (one test passed). Stable milestone validation restored tools and the solution, built with zero warnings/errors, passed 23 focused tests, passed both Python policy suites (8 catalog and 9 formatting tests), and passed `tools/Format.ps1 -Check`. The final sequence took 27.40 seconds: build 5.59 seconds, focused tests 5.64 seconds, Python checks 0.97 seconds, formatting verification 14.43 seconds, plus restore time. The full .NET suite was not requested or run.

The final test filter was:

```text
FullyQualifiedName~Eden.Protocol.Tests.Administration|FullyQualifiedName~Eden.Protocol.Tests.Hosting.ConnectionLimitTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting.ConnectionOwnershipTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting.ServerServicesTests
```

Coverage includes shared/monotonic rate sampling, account admission/release, no SQL or fitting work from diagnostic reads, checkpoint failure/recovery, HTTP authorization and credential revocation, and rendered component content. Automated HTML checks establish dark-theme declarations across editor routes; browser appearance and interactive timer behavior remain unverified.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** After restarting the server, open Performance and confirm periodic updates; inspect settings, catalog, revisions and sign-in for dark-theme appearance and keyboard focus.

## Local commands

Stop the server before these commands. They acquire the saved world's ownership file; a running server uses the authenticated local editor. Commands can upgrade an older world but do not activate pending revisions. Run from the repository root, supplying the saved world explicitly:

```powershell
dotnet run --project src/Eden.Server -- --world 'C:\Eden\world.sqlite' --configuration-history
dotnet run --project src/Eden.Server -- --world 'C:\Eden\world.sqlite' --configuration-export 1 --output 'C:\Eden\revision.json'
dotnet run --project src/Eden.Server -- --world 'C:\Eden\world.sqlite' --configuration-import 'C:\Eden\revision.json'
dotnet run --project src/Eden.Server -- --world 'C:\Eden\world.sqlite' --configuration-restore 1
```

History shows the active/pending IDs and recent revisions. Import and restore always create a new pending revision; start the server normally to activate it. A configuration save runs under the existing world owner and transactions, then requires the existing checkpoint mechanism to complete before reporting a durable save. A failed checkpoint retains committed pending state for retry and reports the failure; it does not activate the draft.

## Constant classification

Catalog facts stay in generated models/shared definitions. Native service IDs, effect operations, tax formulas, eligibility rules and mathematical invariants remain named code values. The table above lists server policy knobs. Settings never enable unsupported items or alter the build-specific support policy. Existing economic tables remain the transaction-owned projections used by features, while immutable revision documents own administration history.

## Autonomous population

The restart-only `Capsuleers.GlobalProvisioning` setting defaults to true. Startup records eligibility for every catalog system and provisions missing miner/hauler pairs in eligible locations. Disabling additional provisioning retains established pilots, grants and saved jobs. Re-enabling it resumes missing communities without replacing existing assets. See the [population guide](capsuleer-population.md) and local administration population/performance pages for coverage, deferral reasons and queue diagnostics.
