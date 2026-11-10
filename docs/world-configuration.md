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

NPC suppliers follow the pinned corporation offers. Generated sell stock requires a delivered manufacturing claim.
Resources and non-faction Tech I items have NPC buyers immediately; supported advanced products require a production
claim for demand. Hubs start with approximately 150 million ISK of demand per type, growing through eight-hour stages
to 600 million after 24 active hours. Specialist stations have one fifth of the budget. Finite quantities recover at
ten-minute boundaries, retain partial intervals and move quotes by at most fifteen percent as they deplete. Blueprint,
skillbook, faction and supercapital exclusions are described in the [industrial demand record](protocol/industry.md#industrial-npc-demand--november-9-2026).

Regional quotes are prepared lazily for the current station, its regional hub and previously prepared stations in that
region. Direct trades and NPC purchases from competitively priced personal sell listings consume the same finite demand.
Quotes use the embedded real-market snapshot, independent of adjusted prices used for service fees. Station roles use
catalog market/manufacturing facilities, gate connectivity and station counts. The superseded time-unlock/material-price
policy remains in the [historical protocol record](protocol/industry.md#progressive-regional-markets--november-8-2026).

## Overrides and compatibility

Supported overrides change the server-supplied **adjusted price** for an existing catalog item or the **manufacturing index** for an existing system. The client obtains these values through `config.GetAverageMarketPrices` and the industry facility response. IDs must exist in the installed catalog, prices must be positive, indices must be within zero and one, and entries cannot repeat. Enabling service costs requires adjusted prices for catalog materials with positive source base prices.

The same native price feed exposes a separate `averagePrice` for inventory and other estimated values, even with service
costs disabled. It uses the existing snapshot midpoint or sole observed quote side. Simulated reference history
supports the native ticker and charts. Adjusted-price overrides
change industrial fee inputs and do not change inventory estimates or NPC trade prices.

These overrides are economic projections belonging to the saved world. They do not modify the original item payload, base price, recipe or shared catalog cache. Two worlds using the same catalog retain separate active and pending definitions. Skills use the separate [skill editor and signed client package workflow](#skill-editor). Other native-client physics, volumes, recipes, identities and schema fields remain read-only. Adding another writable field requires an explicit compatible server/client synchronization contract.

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

## Skill editor

**Current workflow — November 9, 2026:** Skill authoring has moved to the offline Avalonia
[Eden Modding SDK](modding.md). Use local administration's **Mods** page to install/select/build packages;
the web skill-authoring page and its API endpoints have been removed. Export existing drafts/history with
`--export-skill-project ABSOLUTE_FOLDER` and open that folder in the SDK. The records below preserve the
historical web-editor implementation and its evidence; its old navigation instructions are historical.


Open **Skills** in local administration. Search original and custom skills by name, description or ID; several words can appear in any order. The form edits names, descriptions, existing skill groups, skillbook base prices, publication, icons, market groups, training multipliers, primary/secondary attributes, up to six prerequisites and each race's Alpha limit. Zero Alpha levels require Omega; the normal five-level progression remains unchanged. New skills receive server-assigned IDs from a reserved range, checked against installed types and retained custom identities.

The editor has three sections: **Identity & training**, **Bonuses**, and **Requirements & access**. Switching sections retains draft values. The save/discard controls and draft/publication status stay visible while scrolling the form. The status distinguishes unsaved edits, a saved draft ready to publish, a published package awaiting restart, and changes active on the server. **Skillbook appearance and market** holds the optional icon and market-group fields. **Publish saved changes to game clients** expands the packaging controls above the editor.

Primary and secondary learning attributes select which pilot stats determine training speed: skill points per minute are primary plus half the secondary value, before account modifiers. Training multiplier (rank) changes the skill points required, not bonus strength. Bonus attributes are different: they store numbers used by effects. For example, a bonus attribute can store `25`, while an effect applies that value as a percentage to selected equipment. Adding an attribute alone does not create a gameplay bonus.

**Bonuses** opens directly on **Build a bonus**. Search for a stat such as velocity, damage, cargo or mining, select **What changes**, then select **Applies to**. Targets come from compatible existing rules; a stat with one target selects it automatically. Choose **Increase** or **Decrease**, enter a positive **Amount**, and choose **Per skill level** or **Once trained**. The summary explains the requested result before **Add bonus** changes the draft. Search and composer selections do not mark the saved definition dirty; adding or editing a bonus does.

The builder supports single-stat percentage changes, flat additions and fixed multipliers. It connects a compatible level-scaling rule automatically where one exists; fixed or inline scaling is reflected in the Apply control. Multipliers are edited as percentage changes rather than raw factors: a 25% increase stores 1.25 for a multiplier rule. Existing recognized bonuses use the same Increase/Decrease, Amount and Apply controls, with their targets and level I–V previews visible. Removing a card removes its application and separate scaling rule together, retaining the stored value for reuse.

**Technical rules** retains the complete original rule/attribute editor. Unknown effects, multiple-output rules, intermediate bonus attributes, duplicate values/rules and inputs shared by unrelated bonuses remain there. They are preserved without silently changing their meaning. The builder rejects overlapping bonuses and changes that would alter another rule's shared strength. Original skills with dedicated calculations remain read-only in both views.

Skill groups, prerequisites, technical attributes/effects, icons and market groups retain searchable named choices. Expand **Find …** when needed; each technical list shows at most 30 matches and keeps its current selection visible. The bonus builder instead searches and groups choices by the resulting stat and target, and does not require effect IDs or source attributes. Prerequisite and technical rows align their inputs and removal buttons on wide screens, and stack vertically on narrow screens. The builder's direction, amount and scaling fields also align and stack together.

### Example: 25% ore mining yield per level

1. Choose **New skill**, or **Create a copy** of a suitable skill, and enter its name and description.
2. Open **Bonuses**, search for `mining`, and select **Ore mining yield (%)** in **What changes**. Choose fitted items requiring Mining in **Applies to**.
3. Choose **Increase**, enter **Amount** `25`, select **Per skill level**, then **Add bonus**. Its card shows +25%, +50%, +75%, +100% and +125% extra yield for levels I–V. Levels add: level V grants +125%, not 25% compounded five times. Other bonuses can multiply the result.
4. Set prerequisites and Alpha limits in **Requirements & access**, then save. Build/stage a client package and restart the server to activate the saved definition as described below.

The recipe reuses pinned build-3662168 Mining/Astrogeology rules: effect `389` multiplies attribute `434` (miningAmountBonus) by trained skill level, and effect `391` applies the resulting percentage to mining amount on fitted items requiring Mining. The per-level option is offered only when the installed catalog has a compatible scaling rule. It covers ore mining lasers and strip miners requiring Mining; it does not boost ice harvesters, gas harvesters or mining drones. [EVE University's mining guide](https://wiki.eveuniversity.org/Mining) describes the ordinary Mining/Astrogeology yield bonuses; the editable recipe uses the pinned catalog and Eden's existing modifier graph. The original Mining/Astrogeology skills retain their dedicated gameplay owner and remain read-only for bonus editing.

For a recognized existing flat bonus, change **Apply** to **Per skill level** to retain its percentage and add the compatible scaling rule. For legacy mining rules such as online effect `1882`, **Make it scale with skill level** retains its percentage, replaces the equivalent application rule, and installs the passive `389`/`391` pair. It does not stack a second mining application on top of the old one. A stored `20` becomes +20% at level I, +60% at level III and +100% at level V. The form warns when a mining application has no level-scaling rule. Writing “per skill level” in the description cannot change this math. Save and publish the repaired definition, restart the server, and let the launcher install the matching package. Existing published definitions are not silently rewritten by this update.

New skill bonuses require supported category-0 passive effects. Online (category 4), active and targeted module rules are excluded from choices and rejected by authoring writes. Existing unsupported values remain visible for review; replace them before saving a custom definition. In Advanced, **Add effect rule** combines supported existing rules and their input values. Per-level scaling and target application can require separate effects. Targets and operations belong to the selected catalog rule; arbitrary targets, new rule implementations, new skill groups/icons and gameplay scripting cannot be authored here. Original skills with dedicated calculations remain read-only, with forged writes rejected by the server.

Search ignores case and punctuation and combines words across names, descriptions and gameplay terms. Exact IDs and readable names rank before description-only matches. For example, `mining yield` finds both ore-yield application and skill-level scaling rules despite their internal miningAmount names. Matching choices appear as clickable results below the search box, with at most 30 per picker. The sidebar retains every match across pages. Search indexes share the immutable choice collections and do no catalog reads.

Searching/paging the skill list updates the browser separately from the draft form. Catalog definitions, lookup labels and typed effect metadata are loaded in batches and shared across editor visits; searching does not read the catalog. Saving and reloading preserve the list's search and page (clamped if its results shrink). The editor rechecks saved generations and administrator access for every operation.

**New skill** creates a definition; **Create a copy** starts from the selected skill. **Save skill** durably saves that draft without changing the running game. Resetting a draft discards unsaved edits. **Restore original values** saves a fresh revision containing the original catalog definition. Revision history can restore earlier saved versions, including removed custom skills. Restored originals leave the edited-only list. Stale saves are rejected while retaining the draft.

Only user-created skills have a removal button. The same restriction is enforced by the server and immutable stored provenance. A skill required by another current definition cannot be removed until that prerequisite is changed. Removal retains its ID, history, existing skill points and personal assets; the published retired definition becomes unpublished, loses its passive effects and is no longer supplied by NPCs or injectable. Existing character records can therefore still display a retired skill. IDs are never reused.

### General skill bonus builder — November 9, 2026

The mining-only preset flow has been replaced by the stat/target/strength builder described above. Typed choice metadata now carries the target stat, target scope and unit directly from the pinned catalog. The UI discovers compatible single-output passive rules and their scaling partners, instead of requiring a user to choose effect pairs. It recognizes safe existing pairs, keeps direction/strength edits attached to the same stored input, and preserves technical combinations that cannot be edited independently. Supported targets and rule implementations remain catalog-defined; this does not add arbitrary gameplay scripting.

The ordinary navigation and mining references are [EVE University's navigation skills](https://wiki.eveuniversity.org/Skills:Navigation) and [mining guide](https://wiki.eveuniversity.org/Mining). Their percentage-per-level descriptions are gameplay references, checked November 9; actual authored values and targets use build 3662168 and the existing authoritative modifier graph. No Blue, transport or Destiny behavior changed.

**Automated validation:** After integrating main `563f0766`, tool restore, solution restore/build (zero warnings/errors; 6.05 seconds), all 53 focused cases (8.74 seconds of runner time, no skips), both Python policy suites (8 catalog-setup cases in 0.730 seconds; 9 formatting-policy cases in 0.025 seconds) and formatting verification passed. The affected filter combines `SkillBonusBuilderTests`, `SkillBonusEditorTests`, `SkillBonusPresentationTests`, `SkillEditorUsabilityTests` and `SkillEditorTests` with `FullyQualifiedName~Eden.Protocol.Tests.Administration.<class>` alternatives. The package integration case uses an isolated copy of the pinned original client archive/manifest/runtime, leaving installed clients unchanged. Development began with exact filter `FullyQualifiedName=Eden.Protocol.Tests.Administration.SkillBonusBuilderTests.EmptyEditorShowsStatTargetStrengthAndBasisBeforeAnyClickAndComposesSelectedSpeed` through `tools/Test-Feature.ps1` (one pass before direction variants were added; build 9.71 seconds, runner 3.12 seconds). Coverage includes the empty authoring form before any click, a non-mining choice, increase/decrease and scaling modes, inline scaling, independent bonus removal, rejected overlaps/shared inputs and unchanged-state checks, saved/reopened custom definitions, actual ship speed at levels 0/I/III/V, and existing mining module/protocol behavior. No full .NET suite was requested.

The headless layout script passed 150 row/control checks across five viewport widths (1480, 1024, 900, 768 and 375 pixels), collapsed/expanded searches, populated and technical rows, the empty composer, a composed decrease, and a ready-to-add speed bonus. It checks native displayed direction/scaling values and checks that the empty screen already exposes the stat, target, direction, amount and Apply fields. Development checks caught and corrected a selector showing Increase for a stored decrease, an initially mismatched default Apply label, a duplicate synthetic fixture ID, and an assertion depending on which of two valid rejection messages appeared first. No full .NET regression suite or manual server/gameplay session was run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Restart the server and inspect the empty Bonuses editor; create a custom bonus for a non-mining stat and review its summary and level preview.

### Guided bonus cards and row alignment — November 9, 2026

The mining editor now treats its level-scaling and application effects as one bonus. Adding, repairing and removing a guided bonus updates the linked rules together; editing its percentage uses the existing shared attribute. Opening either view does not normalize a saved definition. Unrecognized, duplicated or unrelated shared-input combinations require technical editing. No gameplay formulas, protocol fields or client archive adapters changed. The gameplay reference remains [EVE University's mining guide](https://wiki.eveuniversity.org/Mining), checked November 9; exact targets and effects remain pinned to build 3662168 as documented above.

**Automated validation:** Tool restore, solution restore/build (zero warnings/errors; final build 0.88 seconds), both Python policy suites (8 catalog-setup tests in 0.731 seconds and 9 formatting-policy tests in 0.024 seconds), and formatting verification passed. Development started with exact filter `FullyQualifiedName=Eden.Protocol.Tests.Administration.SkillBonusPresentationTests.LayoutFixtureRendersOneBonusInputAndThreeDistinctPrerequisiteRows` through `tools/Test-Feature.ps1` (one pass; build 7.29 seconds, test runner 7.11 seconds). The final affected filter was:

```text
FullyQualifiedName~Eden.Protocol.Tests.Administration.SkillBonusEditorTests|FullyQualifiedName~Eden.Protocol.Tests.Administration.SkillBonusPresentationTests|FullyQualifiedName~Eden.Protocol.Tests.Administration.SkillEditorUsabilityTests|FullyQualifiedName~Eden.Protocol.Tests.Administration.SkillEditorTests
```

All 40 affected cases ultimately passed. The final group produced 39 passes and one source-fixture failure in 9.39 seconds: the selected installation had a changed archive and no original backup. Retrying only the package case against the prepared installation reached a modified-manifest rejection. A separate test source copied the verified original archive, original manifest and runtime DLLs; the exact `PublishedPackageActivatesOnRestartAndCustomBooksTrainPersistAndRetireSafely` filter then passed in 6.93 seconds. Installed clients were read only. This integration covers archive generation, authenticated download/tickets, restart activation and persistent training/retirement. No full .NET regression suite was requested or run.

The automated browser check passed 90 alignment checks across guided and technical views at widths 1480, 1024, 900, 768 and 375 pixels, with searches collapsed and expanded. It verifies input/button bounds, stacked narrow rows, a single guided percentage input and no page/control overflow. These checks use HTML rendered from production components and the actual administration stylesheet; they do not establish live browser navigation or interactive client acceptance. Reproduce with Python Playwright and its Chromium installed:

```powershell
$env:EDEN_SKILL_LAYOUT_DIRECTORY = "$PWD/artifacts/skill-editor-layout"
.\tools\Test-Feature.ps1 -Filter 'FullyQualifiedName=Eden.Protocol.Tests.Administration.SkillBonusPresentationTests.LayoutFixtureRendersOneBonusInputAndThreeDistinctPrerequisiteRows'
python tools/Test-SkillEditorLayout.py artifacts/skill-editor-layout
```

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** After restarting the host, review an existing custom mining skill in Bonuses, switch to Advanced and back with a draft, and inspect an expanded prerequisite search in the live administration page.

### Earlier skill-editor performance validation — November 9, 2026

**Automated performance check (2026-11-09):** A fresh-process comparison against the installed 589-skill catalog loaded the same normalized definitions (matching SHA-256) in 8.877 seconds with the previous per-skill loader versus 2.033 seconds with batched loading. Catalog commands fell from 2,947 to 14 and connections from 2,944 to two. Constructing the richer lookup labels took a further 0.535 seconds; three repeated reads plus lookups took 0.053 seconds with no catalog commands or connections. These are one local data-loading comparison, not browser timings or a general latency guarantee. A deterministic 200-added-skill regression bounds cold query/connection counts, and warm reads, history and choices assert zero catalog work. Component checks cover bounded lookup markup, retained unsupported values, escaped text, nullable clearing and draft change tracking. The affected administration/catalog tests and required build/policy/format checks are used; the full .NET suite is not requested.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Open Skills after restarting the server, search and switch several skills, then try named bonus choices and save/discard a draft. Check that opening and closing appearance/bonuses keeps your values and that searching the list retains your draft.

**Stable milestone validation:** Tool restore, solution restore and build passed with zero warnings/errors. The focused filter below passed 39 tests; one optional pinned-client packaging test was skipped because `EDEN_TEST_CLIENT_ROOT` was not supplied for this UI change. Both Python policy suites passed (8 catalog tests and 9 formatting tests), and `tools/Format.ps1 -Check` passed. The final sequence took 39.20 seconds: build 6.57 seconds, focused tests 7.93 seconds, policies 0.93 seconds and formatting 22.91 seconds, plus restore time. No full .NET suite or interactive acceptance was performed.

```text
FullyQualifiedName~Eden.Protocol.Tests.Administration|FullyQualifiedName~Eden.Protocol.Tests.Persistence.CatalogFittingReadTests
```

### Layout and configurable bonus validation — November 9, 2026

**Automated validation:** Tool restore, solution restore and build passed with zero warnings/errors. The final focused filter below passed 65 tests, with one optional pinned-client packaging test skipped because `EDEN_TEST_CLIENT_ROOT` was not configured. Both Python policy suites passed (8 catalog tests and 9 formatting tests), and `tools/Format.ps1 -Check` passed. The stable validation sequence took 37.04 seconds. No full .NET suite was requested or run.

```text
FullyQualifiedName~Eden.Protocol.Tests.Administration|FullyQualifiedName~Eden.Protocol.Tests.Persistence.CatalogFittingReadTests|FullyQualifiedName~Eden.Protocol.Tests.Mining.MiningTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.PassiveFittingTests
```

Development began through `tools/Test-Feature.ps1 -Filter` with the exact method `FullyQualifiedName=Eden.Protocol.Tests.Administration.SkillBonusEditorTests.MiningRecipePersistsAndAppliesTwentyFivePercentPerLevelToMatchingModulesOnly`. After correcting test fixture setup, that case passed and was expanded into `MiningRecipePersistsAndAppliesConfiguredPercentPerLevelToMatchingModulesOnly`, covering 25%, 12.5% and zero. The final checks cover saved/reopened definitions, projected catalogs, real mining-module yields at levels 0/I/III/V, nonmatching equipment remaining unchanged, shared editable values, unavailable/mismatched recipe rejection, shortened escaped picker captions, read-only original controls, and rendered level previews. Warm editor reads still perform zero catalog commands/connections or fitting calculations. The fixture reproduces package catalog metadata without building or installing a native client package.

**Live acceptance:** Not performed; agent client testing is disabled. Browser layout, keyboard input and native presentation remain unverified.

**Optional maintainer checks:** After restarting the host, open Skills and switch sections with an unsaved draft; inspect long effect choices at a narrow width; create a custom mining bonus and compare its percentage/level preview before saving.

## Publishing and launcher installation

1. Save the skill definitions you want to use. Unsaved form changes block publication.
2. Enter a build-3662168 client folder on the **server computer**, then choose **Build and stage package**. The server requires 64-bit Python 3.11 or newer; `EDEN_PYTHON` can select its executable. `EDEN_CLIENT_ROOT` can supply the initial source folder, and the last successfully used folder is remembered. A prepared source copy with the launcher's verified original-archive backup is also supported.
3. Restart the server normally. The pending catalog and matching client archive activate together before world reconciliation. Saving or packaging alone does not change the active game.
4. Use the updated Eden Launcher. Individual Play and launch groups refresh server information, obtain a ticket for the active package, download changed archive bytes from the authenticated server endpoint and verify size/SHA-256 and the matching signed native manifest before installation. An already-current archive is reused. Close any games using the same installation before changing its package or server trust. Switching to a server without a skill package restores the pinned original archive.

The source installation and base `catalog.sqlite` remain unchanged. The package contains a separate projected catalog plus a client archive with an authored adapter for type/dogma/prerequisite records, localized names/descriptions and Alpha limits. The adapter preserves unchanged native rows, caches its shared views and uses original native functions as fallbacks. Compilation never executes game module bodies. Both the source archive and its Python runtime are pinned by SHA-256. The native manifest signer accepts only the canonical original prefix and substitutes the active server package's exact archive hash; clients cannot request arbitrary code hashes.

Published custom skillbooks with a market group and positive base price become NPC institutional offers. Development markets retain their saved testing prices and stock. Edited base prices affect ordinary institutional skillbook offers; changing only a name does not alter prices. Activation refreshes development market seed markers and retires removed NPC offers while preserving personal orders and execution history. Existing training commitments retain their saved deadlines; changing rank can change the level corresponding to existing skill points.

Back up the stopped world together with `<world>.skills.sqlite`, `<world>.skills-active` and `<world>.skill-packages/`, plus its original catalog. Definitions/history live in the independent authoring database and do not enter resident checkpoint replacement. Packages retain their base catalog fingerprint for configuration compatibility and have their own content identity. A changed original catalog, stale generation, incomplete package or damaged archive is rejected. No game restart or installed-client update is performed by the editor itself.

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
