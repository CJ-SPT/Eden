# Eden

[Capsuleer encounters](docs/capsuleer-population.md) bring persistent ships to stations and gates across New Eden. A busy hub can support five or six home travelers, while a quiet branch can have none. An optional local zKill snapshot supplies real pilot names, varied supported hulls and complete victim fits. Travelers use real station and gate routes, including stationless transit systems. High-security miners work real deposits. Observers share ship identities, movement, damage and losses. Career jobs sleep when the system empties. Regional markets develop independently.

[Production-driven regional markets](docs/protocol/industry.md#production-driven-market-progression--november-8-2026) start with catalog NPC offers. A player's claimed manufacturing output permanently unlocks that product for the whole world. Real market snapshots price generated buy/sell orders; each eligible region has one high-security hub, with smaller selections at secondary factories. New server worlds use this market by default; existing development saves can select it in local administration.



Eden is a C# server emulator for EVE Online 24.01, Windows build **3662168**. It supports persistent local worlds and multiple pilots using separate Windows clients. Supply the matching EVE installation and resource cache separately.

## Start here

| I want to… | Read |
| --- | --- |
| Build and start the server | [Build and run](#build-and-run) |
| Prepare a Windows client and local data | [Windows client setup](docs/development-client.md) |
| Connect accounts or host a remote server | [Launcher hosting](docs/launcher.md) |
| Browse all documentation | [Documentation index](docs/README.md) |
| Contribute or validate a change | [Contributor guide](AGENTS.md) and [automated validation](docs/testing.md) |
| Find the code for a feature | [Project boundaries and feature layout](docs/architecture.md#projects-and-conventions) |
| Investigate native behavior or prior milestones | [Protocol records by topic](docs/protocol.md) |

## Current status

[Cloaking and standard micro-jump drives](docs/protocol/navigation.md#cloaking-and-standard-micro-jump-drives-november-9-2026) support 14 static cloaks and four standard drives, with hidden contacts, covert warp, persisted delays and scrambler-interrupted spools. Native presentation remains unverified.

[Map navigation support](docs/protocol/navigation.md#native-map-navigation-support-november-9-2026) now supplies the route planner’s cached history baseline and roaming-weather reads. Activity history remains empty; native map interaction is unverified.

[Native search](docs/protocol/characters.md#universal-native-search-november-9-2026) now finds catalog locations, agents, NPCs, corporations, factions and published items alongside local pilots and player corporations. Mixed-category map searches are supported; native presentation remains unverified.

[Ice harvesting](docs/protocol/industry.md#ice-harvesting-and-public-ice-fields-november-9-2026) supports six static tools and four public
ice-field layouts across 117 documented high-security systems, with finite saved stock and six-hour replacement after depletion.

[Gas harvesting](docs/protocol/industry.md#gas-harvesting-and-mykoserocin-nebulae-november-9-2026) supports eight static tools and 16
Mykoserocin nebula templates, with finite saved clouds, probe discovery and gas hull bonuses. Hazardous gas and reactions remain pending.

[Modulated ore mining](docs/protocol/industry.md#modulated-ore-mining-and-crystals-november-9-2026) supports the three static modulated miners
and 66 ordinary mining crystals, with matching ore lists, fitted bonuses, persistent wear and native shattering updates.

| Area | Implemented scope and limits |
| --- | --- |
| World configuration | Six activity tabs with saved-world gathering, reward, kill-SP and industry multipliers, revision history and restart activation. See the [administrator guide](docs/world-configuration.md#world-tuning). |
| Accounts and characters | Launcher registration/login, three character slots, persistent biomass preparation/cancellation and final deletion, profiles, skill training, remapping, learning implants/boosters, skill injectors/extraction, redemption and personal clones. |
| Combat anomalies | Shared regional pirate and rogue-drone encounters across known space, covering 199 catalog layouts with report-backed waves and four labeled reconstructions. Scanner discovery, warp, scenery, trigger/timed waves, bounties, wrecks and replacement persist. Major Threat tiers require an administrator override. Escalations and exact script fidelity remain pending. See [coverage and limitations](docs/protocol/combat.md#known-space-combat-anomalies-november-8-2026). |
| Agent missions | 311 authored definitions across levels 1–4: 50 combat encounters, 177 ordinary deliveries and 84 delivery storylines. Includes multi-room encounters, keyed gates and ship restrictions, combat retrieval, mission containers, destructible warehouses, all ten Hidden Stash and ten Duo of Death variants, standing-based access and exactly-once rewards. Delivery levels/routes/rewards use explicit Eden policy. Level-five combat, special-agent chains, unverified encounter rosters and additional objective scripts remain pending. See the [content inventory and complete catalog audit](docs/missions.md) and [implementation record](docs/protocol/agents.md). |
| Shared world and travel | Multi-pilot flight, docking, warp, gates, collision motion, bookmarks, maps and developer translocation to catalog destinations, pilots, ships, NPCs, wrecks, asteroids and deployed drones. See [TR destination support](docs/protocol/navigation.md#runtime-tr-destinations-november-8-2026). |
| Directional scanning | Native cone/range scans, scans during warp, off-grid names, hull immunity and a pinned 1.5-second cooldown. Shared resident contacts avoid SQL on warmed world reads. Active module cloaks are excluded. See [scanner behavior and validation](docs/protocol/information.md#directional-scanning-completion-november-8-2026). |
| Probe scanning | Owned probes, native formations/range steps, fitted bonuses, timed progressive results, recovery and persistent combat signatures. First sites are explicit Eden variants of existing combat layouts. A Heron/core-launcher/core-probe production chain is supported. Combat-probe ship hunting and wormholes remain pending. See [scope and validation](docs/protocol/information.md#probe-scanning-and-combat-signatures-november-9-2026). |
| Hacking and archaeology | Native analyzer minigames, fitted skill/hull/rig bonuses, 209 catalog-backed pirate data/relic definitions, persistent shared containers and earned datacore/decryptor/salvage loot. Board generation and loot distributions use documented Eden approximations. Special sites and mission hacking remain pending. See [scope and validation](docs/protocol/information.md#hacking-and-archaeology-november-9-2026). |
| Personal assets | Native station directory, remote item lists and client-side search over personal station assets and supported container contents. Available while docked or in space. Asset Safety reports an empty list; Upwell storage and safety transfers remain unsupported. |
| Ships and fitting | Persistent hulls/holds, personal saved fitting plans, supported Tech I hulls, catalog-backed modules, T1/T2 ship rigs with calibration and skill-reduced drawbacks, saved weapon groups and 76 static light/medium/heavy/sentry combat drones across 149 admitted hulls with drone bays, including earned hull bonuses, effective control limits, static drone equipment, scripted tracking links, combat-drone rig effects and fleet assist/guard with owner safety and range checks. Installed rigs are destroyed on removal or ship loss. Lost drones can be reconnected or scooped into a drone bay or cargo. Assault damage control mechanics are implemented; their eligible T2 hull admission remains pending. Market availability does not imply gameplay support. |
| Combat and NPCs | Pilot turret, missile and combat-drone damage, player ECM/dampeners/weapon disruptors/target painters and ECM amplifiers, player remote shield/armor/hull repairs, local and remote ancillary armor/shield repairs with real fuel and reloads, ramping mutadaptive remote armor repairs, battleship signature suppression with saved cooldowns, player capacitor transfers, NOS/neutralizers against pilots and NPCs, remote tracking/sensor assistance, shared ship-loss settlement, capsule destruction with home-station respawn, enforced safety, saved aggression timers and timed logout presence; regional belt pirates, defensive empire guards, passive high-security station convoys, high-security NPC mining/hauling and response fleets, NPC turrets/missiles, declared electronic warfare and local repairs, bounties, security/standing history, ship losses and wrecks. |
| Scanners and salvage | Cargo/ship scans, upgraded mining surveys, directional scans, passive/automatic targeting, resident tractors and persistent salvage with authorized Eden drop distributions. Noctis and Salvage Tackle bonuses apply. Exact ship-scan sampling/falloff and capital tractor hull consumers remain pending; see [scope and evidence](docs/protocol/information.md). |
| Science and industry | Personal station-local manufacturing across 4,049 valid published recipes, material/time research, blueprint copying, Tech II invention and ancient-relic Tech III invention. Exact components, skills, blueprint efficiency/runs, fees and station restrictions are enforced. Missing material sources stay unobtainable. Corporate/remote jobs, reaction facilities and supercapital construction remain unavailable. Manufacturing does not add flight/fitting support. See [production scope and validation](docs/protocol/industry.md#catalog-wide-station-production-november-9-2026). |
| Economy | Renewable belts, mining, priced economy-world reprocessing and Merlin/Venture/Badger starter production, persistent station-local personal market orders, the optional testing market, personal wallets, insurance and personal item-exchange/courier/auction contracts. NPC loyalty stores offer supported physical rewards using personal or authorized corporate LP/ISK, plus personal CONCORD LP conversion. LP-store blueprint-copy rewards, cosmetics and Analysis Kredit offers remain unsupported; see [LP-store scope](docs/protocol/industry.md#loyalty-point-stores-november-8-2026). |
| Social features | Cooperative fleets with invitations, command hierarchy, broadcasts, fleet chat and point fleet warps; TLS Local/corporation/private chat, automatic destination Local membership through developer translocation, personal mail and mailing lists, contacts, calendar, notifications, station guests and public pilot information. |
| Player corporations | Creation/membership, administration, roles/titles, shared wallets, bounty tax, LP/EverMarks and share transfers. Rented offices, seven permission-checked shared hangars and office asset reports are implemented; recurring rental bills, impound recovery and governance remain pending; see the [corporation roadmap](docs/corporations.md). |

See [feature implementation notes](docs/feature-notes.md) for catalog coverage, restrictions and examples, and [protocol records](docs/protocol.md) for milestone evidence. Agent validation is automated; native rendering, input and gameplay feel remain unverified unless a record explicitly documents live acceptance for that change. Historical observations do not establish acceptance of later changes.

[Character portraits](docs/protocol/characters.md#character-portraits-across-the-client--november-9-2026) use the native scene renderer and cache across character selection, the character sheet, chat, station guests and Neocom. Created pilots retain their submitted appearance; seeded pilots and simulated capsuleers without creator data use catalog defaults matched to their bloodline and gender. Restart the server and client to install the signed login adapter.

The [ship and module completion program](docs/feature-notes.md#ship-and-module-completion-program) tracks remaining ordinary static content by operational role, with a reproducible inventory separating fitting admission, missing capabilities and external dependencies. [Milestone 6 heating](docs/protocol/fitting.md#complete-overheating-and-native-controls-november-8-2026) implements native module/rack controls, paid overload cycles for all 1,826 admitted heatable profiles, adjacent heat damage, burnout cancellation, saved module condition, station repairs and cargo-funded nanite repairs with partial material settlement. Burnout preserves shield and capacitor charge fractions. Automated validation establishes this scope; native presentation remains unverified.

[NPC bounty skill points](docs/protocol/combat.md#npc-bounty-skill-point-rewards-november-9-2026) award the killer an additional **0.25% of the gross ISK bounty as unallocated SP**, rounded down per kill. A **150,000 ISK** bounty grants **375 SP** alongside its normal ISK payment.

Bounty SP gains are silent; refresh the skill window to read the updated total.

[CONCORD criminal response](docs/protocol/combat.md#concord-criminal-response--november-9-2026) now enforces supported high-security criminal offenses with persistent response deadlines, catalog Captains/Commanders, capsule escape and no insurance payout. Baseline response times approximate a fresh live response; existing-squad timing and pulling remain unsupported. CONCORD overview icons remain blue during attacks through the signed login adapter; restart the server and fully reopen the client after updating.

## Local game data

[Combat-site layout corrections](docs/protocol/combat.md#combat-site-layout-corrections--november-9-2026) use native initial/reinforcement
anchors and fixed battery positions across the existing anomaly pool, correct 19 physical variant bindings, and preserve started encounters.

[Mission layout corrections](docs/protocol/agents.md#mission-layout-corrections--november-9-2026) apply room-local NPC anchors,
individual battery positions, restored scenery and corrected collision sizes to the 50 existing combat mission definitions.

Eden uses Entity Framework Core 10 with two SQLite files under `%LOCALAPPDATA%\Eden`. The read-only `catalog.sqlite` holds build 3662168 game definitions. The writable `world.sqlite` holds accounts, pilot profiles and trained skill points, owned ships, their positions and health, item stacks in cargo or station hangars, asteroid fields, wallets, market orders, trade records and manufacturing jobs. Both files are generated locally.

On Windows, install 64-bit Python 3.11 or later and the pinned .NET SDK. Stage the matching client and resource cache, then run the setup command from the repository root.

```powershell
.\tools\DevelopmentClient.ps1 -PrepareFrom 'E:\Eve' -DevelopmentRoot 'E:\EdenDev\3662168'
.\tools\Setup-Catalog.ps1 -DevelopmentRoot 'E:\EdenDev\3662168'
```

Setup downloads and verifies CCP's exact-build export on the first run, decodes the staged client, and checks the replacement catalog before installing it. Normal play uses the installed catalog and save offline. See the [Windows client guide](docs/development-client.md#creating-and-recreating-the-databases) for offline setup, save migration, and verification.

All 442 catalog datasets have generated typed keys and payloads in `Eden.Data`; see the [schema and provenance report](src/Eden.Data/Catalog/CatalogSchema.g.json). The [shared catalog provider](docs/protocol/catalog.md#shared-catalog-provider-november-7-2026) preloads core definitions and caches other records within a bounded budget. [Industry and economic inputs](docs/protocol/catalog.md#industry-and-economic-catalog-adoption-november-7-2026) use the shared definitions. [Fitting, inventory and skill inputs](docs/protocol/catalog.md#fitting-inventory-and-skill-catalog-adoption-november-7-2026) also share typed definitions. [Combat and simulation readers](docs/protocol/catalog.md#combat-and-simulation-catalog-adoption-november-7-2026) share immutable profiles and support decisions. [Geography access](docs/protocol/catalog.md#typed-geography-access-november-7-2026) shares indexed locations and static scene definitions. [Reference services](docs/protocol/catalog.md#shared-item-facts-in-reference-services-november-7-2026) reuse shared item facts and typed records. [Location and directory consumers](docs/protocol/catalog.md#shared-location-and-directory-consumers-november-7-2026) also use shared definitions. [Saved configuration](docs/world-configuration.md) supports typed policies, economic overrides and immutable revisions pending restart. The [local Blazor editor](docs/world-configuration.md#local-graphical-editor) browses every dataset, stages settings and displays live process/checkpoint diagnostics in a dark theme through a separate loopback administrator login. [Adoption checks and remaining constants](docs/catalog-adoption.md) describe the final access boundary and build policy.

The native Insider [Type Browser](docs/development-client.md#developer-type-browser) can create persistent items, original blueprints and packaged ships in a developer pilot's station hangar or load cargo within its capacity. Existing assembly/fitting support remains authoritative.

Insider's [Skill Tool](docs/development-client.md#developer-skill-tool) grants published skill profiles. Type Browser creation includes
skill injectors and more physical categories; fixed SP/currency rewards go to Redeem Items. Server restart recovers misplaced personal
reward stacks. Command discovery, native redemption arguments and the batch-SP training-queue crash are
[corrected](docs/protocol/navigation.md#insider-tools-and-reward-destinations-november-9-2026).

The native [Trash Item action](docs/protocol/inventory.md#trashing-personal-items-and-ships-november-8-2026) permanently removes personal station items, containers and inactive ships with their contents, including remote personal assets. Active ships, space inventory and assets committed to contracts or industry are protected.

The [mod loader and Avalonia SDK](docs/modding.md) support portable skill mods, generated C# server plugins, Python client extensions and verified launcher installation.

## Build and run

Author skills in the separate [Eden Modding SDK](docs/modding.md), including fixed or per-level bonuses, prerequisites,
original restoration and revision history. Install and stage packages through local administration's **Mods** page
or host commands. The updated launcher installs the required signed client archive before login. Only custom skills
can be retired; existing skill points and assets are retained.

Use the SDK pinned in [global.json](global.json), 64-bit Python 3.11 or later, and PowerShell 7 on Windows. Run from the repository root. After a code milestone is stable, validate in this order; select a .NET filter for the affected behavior and immediate dependencies, and stop to investigate a failed step.

```powershell
dotnet tool restore
dotnet restore --artifacts-path artifacts/feature-tests
dotnet build --no-restore --artifacts-path artifacts/feature-tests
dotnet test --no-build --artifacts-path artifacts/feature-tests --filter 'FullyQualifiedName~Eden.Protocol.Tests.Hosting'
python tests/Eden.Protocol.Tests/Persistence/catalog_setup_test.py
python tests/Eden.Protocol.Tests/Formatting/formatting_policy_test.py
.\tools\Format.ps1 -Check
```

The hosting filter is an example. Record the filter, results, elapsed time and limitations; see [automated validation](docs/testing.md) for focused test selection and infrastructure. Run the full .NET suite **only when the user explicitly requests a regression check**. Documentation-only changes use documentation checks. Agents do not launch the game or conduct interactive acceptance unless explicitly requested. See the [testing policy](AGENTS.md#testing).

To run the local server for play, use the following command separately from validation:

```powershell
dotnet run --no-build --project src/Eden.Server
```

[Offline startup reconciliation](docs/protocol/performance.md#offline-startup-belt-renewal-november-9-2026) renews overdue belts in bounded SQL batches while preserving saved deposits and deadlines.

Normal server startup prints a bordered ASCII EDEN banner with a four-space left margin, the compiled version and full Git revision before world reconciliation. The border expands to fit the build identity. Build identity comes from the assembly's informational version, so published servers do not need Git installed. Builds without source revision metadata show `Revision: unknown`. Maintenance commands keep their existing output.

The console shows startup reconciliation and host timings, timestamped startup/shutdown and account/character session summaries, warnings and failures by default. Routine service calls, notifications, gateway requests, handshakes, movement and successful world checkpoints are quiet. Warnings and errors go to standard error. Set `$env:EDEN_VERBOSE_LOGGING = '1'` before starting the server to include routine diagnostics; remove it with `Remove-Item Env:EDEN_VERBOSE_LOGGING` to return to quiet output. The existing `EDEN_TRACE_PERFORMANCE=1` and `EDEN_TRACE_MOTION=1` switches still enable their targeted traces independently of verbose logging. Checkpoint failures always remain visible and pending changes retain the existing save/retry behavior.

The [Windows client setup](docs/development-client.md) contains optional maintainer procedures. The [protocol notes](docs/protocol.md) retain historical exchange, flight, and two-pilot observations; they do not establish acceptance of later changes.

## Formatting

Run `.\tools\Format.ps1` to apply formatting after restoring tools and the solution. Run `.\tools\Format.ps1 -Check` to verify without editing files. Both commands run .NET style rules, CSharpier, then strict line-width validation.

The root `.editorconfig` is the source of truth. CSharpier 1.3.0 is pinned in the local tool manifest and owns layout; .NET analyzers enforce required braces, file-scoped namespaces and import placement. Do not run the full `dotnet format` command, whose whitespace formatter can disagree with CSharpier. Use `dotnet format style --no-restore --severity warn` for style-only cleanup.

Follow Microsoft's C# conventions with four spaces, Allman braces and a maximum line width of **140 characters**, including indentation. Expand nonempty executable brace bodies. Auto-properties, empty bodies, initializers, patterns and expression-bodied members may remain compact. Prefer expression-bodied trivial accessors because CSharpier otherwise places their statements beside their braces. Project/configuration files use two spaces. Preserve existing line endings.

The strict width check covers authored C#, `.csproj`, `.props`, `.targets`, `.slnx`, root `global.json`, JSON under `.config` and `.vscode`, and `appsettings*.json`/`launchSettings.json`. Generated C# (including the catalog models), SDK outputs, build artifacts and runtime/data JSON are excluded. Documentation and development scripts retain the basic UTF-8, final-newline and trailing-whitespace rules without a width limit.

Wrap long code first. Split literals only when their runtime value remains identical. For content that cannot safely be split, add an entry to `tools/formatting-exceptions.json` with its repository-relative `path` (forward slashes), exact full `line` (including indentation), and a specific `reason`. The checker rejects duplicate or unused exceptions, including exceptions whose content changes or no longer exceeds the limit. The allowlist itself is data and is excluded from width checking.

For format-on-save in Visual Studio, VS Code or Rider, install the CSharpier editor integration and select the repository-local tool. Keep EditorConfig support enabled and avoid running a second whitespace formatter on save. Restore with `dotnet tool restore` after cloning or changing the pinned formatter version.

## Projects and conventions

| Project | Responsibility |
| --- | --- |
| [Eden.Gameplay](src/Eden.Gameplay) | Typed gameplay rules, state, commands and persistence interfaces. |
| [Eden.Data](src/Eden.Data) | Generated catalog models and saved database entities; contexts and operations remain in World. |
| [Eden.Protocol](src/Eden.Protocol) | Native wire formats, service dispatch, replies and notifications. |
| [Eden.World](src/Eden.World) | Authoritative state, shared catalog access and SQLite world persistence. |
| [Eden.Infrastructure](src/Eden.Infrastructure) | Shared registration, operation scopes, logging and diagnostics. |
| [Eden.Server](src/Eden.Server) | Startup, listeners and background scheduling. |
| [Eden.Protocol.Tests](tests/Eden.Protocol.Tests) | Automated protocol, gameplay and persistence coverage. |
| [tools](tools) | Development, catalog, auditing and formatting tools. |

Follow the [contributor guide](AGENTS.md) for code style, dependency direction, service ownership and milestones. See [architecture and runtime](docs/architecture.md) for the implementation boundaries and examples.

## Architecture

Gameplay builds independently of Protocol, Server, EF Core and the host container. Server loads and commits typed inputs; Protocol adapts requests and results to the native client. See [architecture and runtime](docs/architecture.md#architecture).

### Session performance diagnostics

The host uses pooled protocol buffers and buffered console logging. See the
[library evaluation and measurements](docs/protocol/performance.md#pooled-protocol-buffers-and-selective-libraries-november-9-2026)
for the dependency choices, compatibility coverage and limits of the measurements.

Run isolated encoding or scene-filter comparisons from the repository root:

```powershell
dotnet run --project tools/Benchmarks/Eden.Benchmarks.csproj -c Release -- --filter '*SerializationBenchmarks*' --job short
dotnet run --project tools/Benchmarks/Eden.Benchmarks.csproj -c Release -- --filter '*SceneFilteringBenchmarks*' --job short
```

BenchmarkDotNet writes reports under ignored `artifacts/library-benchmarks` by default. Use `--artifacts <directory>` to retain
separate comparisons. These small benchmarks complement the complete-session replays below; they do not establish player capacity.

Bound-call warnings now identify their embedded method. Fitting and brain primes share committed fitting snapshots and operation-local catalog reads; see the [dogma and ship-call latency record](docs/protocol/performance.md#bound-dogma-and-ship-call-latency-november-7-2026).

Unchanged player-frame polling and stable NPC observer records now share resident inputs; see the [multi-player frame comparison](docs/protocol/performance.md#shared-work-across-player-frames-november-7-2026).

Compare NPC and connected-player populations with `.\tools\Measure-NpcCapacity.ps1`; see the [capacity measurements](docs/protocol/performance.md#npc-and-player-capacity-november-6-2026) and [implemented NPC optimizations](docs/protocol/performance.md#indexed-npc-scene-updates-november-6-2026).

Use `EDEN_TRACE_PERFORMANCE=1` for targeted timings and database counters. See [diagnostic settings](docs/architecture.md#session-performance-diagnostics) and [performance records](docs/protocol/performance.md) for replay evidence.

The live host loads the immutable NPC/agent directory before admitting clients. Its name lookup uses one collection parameter so catalog initialization does not bind thousands of individual SQLite parameters; see the [agent directory latency record](docs/protocol/performance.md#agent-directory-first-request-latency-november-6-2026).

### Resident world and background saves

The server runs an authoritative world in memory and checkpoints committed state to disk every 15 seconds. Drone flight, shared observer contacts and nonfatal direct combat use resident objects; see the [drone performance record](docs/protocol/performance.md#resident-drone-flight-and-combat-november-6-2026). Checkpoint SQL projection now runs on a detached image outside the world owner; see the [checkpoint pause comparison](docs/protocol/performance.md#detached-checkpoint-projection-november-7-2026). Graceful shutdown flushes pending changes. Stop the host before editing, backing up or restoring its save; a crash can lose progress since the last completed checkpoint. See [resident state and checkpoint behavior](docs/architecture.md#resident-world-and-background-saves).

## License

Eden's original source and documentation are available under the [MIT License](LICENSE). Supply the EVE client and its resource cache separately. See the [Carbon licensing notes](docs/carbon-license.md) for reference-source attribution.

[Persistent traveler routines](docs/protocol/capsuleers.md#persistent-traveler-routines--november-9-2026) give simulated pilots saved travel habits, shelter and temporary attack memory.
[Simulated-pilot retaliation](docs/protocol/capsuleers.md#simulated-pilot-retaliation--november-9-2026) adds actual fitted defenses, shared locks, retreat and finite starter supplies.
[Belt ratting and paid resupply](docs/protocol/capsuleers.md#belt-ratting-resupply-and-ship-variety--november-9-2026) give suitable combat travelers pirate encounters, owned loot, shopping trips and more varied hull selection.

Progression markets have [industrial NPC buyers](docs/protocol/industry.md#industrial-npc-demand--november-9-2026)
for resources and non-faction Tech I goods before supply unlocks. Advanced products require a production claim for demand. Finite demand grows with active play;
manufacturing a product remains necessary before generated NPC sell orders appear.
NPC buyers also consume competitively priced personal sell listings as finite demand recovers during active play;
see [resting-order purchases](docs/protocol/industry.md#npc-purchases-from-resting-sell-orders--november-9-2026).

[Inventory estimated values and market history](docs/protocol/industry.md#inventory-estimates-and-reference-market-history--november-9-2026)
use existing snapshot prices and simulated reference history for fresh-world ticker data, without a history download.
Industrial fee overrides remain separate.
