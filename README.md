# Eden

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

| Area | Implemented scope and limits |
| --- | --- |
| Accounts and characters | Launcher registration/login, three character slots, persistent biomass preparation/cancellation and final deletion, profiles, skill training, remapping, learning implants/boosters, skill injectors/extraction, redemption and personal clones. |
| Shared world and travel | Multi-pilot flight, docking, warp, gates, collision motion, bookmarks, maps and developer translocation. |
| Ships and fitting | Persistent hulls/holds, personal saved fitting plans, supported Tech I hulls, catalog-backed modules, T1/T2 ship rigs with calibration and skill-reduced drawbacks, saved weapon groups and 76 static light/medium/heavy/sentry combat drones across 149 admitted hulls with drone bays, including earned hull bonuses, effective control limits, static drone equipment, scripted tracking links, combat-drone rig effects and fleet assist/guard with owner safety and range checks. Installed rigs are destroyed on removal or ship loss. Lost drones can be reconnected or scooped into a drone bay or cargo. Assault damage control mechanics are implemented; their eligible T2 hull admission remains pending. Market availability does not imply gameplay support. |
| Combat and NPCs | Pilot turret, missile and combat-drone damage, player ECM/dampeners/weapon disruptors/target painters and ECM amplifiers, player remote shield/armor/hull repairs, local and remote ancillary armor/shield repairs with real fuel and reloads, ramping mutadaptive remote armor repairs, battleship signature suppression with saved cooldowns, player capacitor transfers, NOS/neutralizers against pilots and NPCs, remote tracking/sensor assistance, shared ship-loss settlement, capsule destruction with home-station respawn, enforced safety, saved aggression timers and timed logout presence; regional belt pirates, defensive empire guards, passive high-security station convoys, high-security NPC mining/hauling and response fleets, NPC turrets/missiles, declared electronic warfare and local repairs, bounties, security/standing history, ship losses and wrecks. |
| Economy | Renewable belts, mining, priced economy-world reprocessing and Merlin/Venture/Badger starter production, persistent station-local personal market orders, the optional testing market, personal wallets, insurance and personal item-exchange/courier/auction contracts. |
| Social features | Cooperative fleets with invitations, command hierarchy, broadcasts, fleet chat and point fleet warps; TLS Local/corporation/private chat, automatic destination Local membership through developer translocation, personal mail and mailing lists, contacts, calendar, notifications, station guests and public pilot information. |
| Player corporations | Creation/membership, administration, roles/titles, shared wallets, bounty tax, LP/EverMarks and share transfers. Rented offices, seven permission-checked shared hangars and office asset reports are implemented; recurring rental bills, impound recovery and governance remain pending; see the [corporation roadmap](docs/corporations.md). |

See [feature implementation notes](docs/feature-notes.md) for catalog coverage, restrictions and examples, and [protocol records](docs/protocol.md) for milestone evidence. Agent validation is automated; native rendering, input and gameplay feel remain unverified unless a record explicitly documents live acceptance for that change. Historical observations do not establish acceptance of later changes.

The [ship and module completion program](docs/feature-notes.md#ship-and-module-completion-program) tracks remaining ordinary static content by operational role, with a reproducible inventory separating fitting admission, missing capabilities and external dependencies.

## Local game data

Eden uses Entity Framework Core 10 with two SQLite files under `%LOCALAPPDATA%\Eden`. The read-only `catalog.sqlite` holds build 3662168 game definitions. The writable `world.sqlite` holds accounts, pilot profiles and trained skill points, owned ships, their positions and health, item stacks in cargo or station hangars, asteroid fields, wallets, market orders, trade records and manufacturing jobs. Both files are generated locally.

On Windows, install 64-bit Python 3.11 or later and the pinned .NET SDK. Stage the matching client and resource cache, then run the setup command from the repository root.

```powershell
.\tools\DevelopmentClient.ps1 -PrepareFrom 'E:\Eve' -DevelopmentRoot 'E:\EdenDev\3662168'
.\tools\Setup-Catalog.ps1 -DevelopmentRoot 'E:\EdenDev\3662168'
```

Setup downloads and verifies CCP's exact-build export on the first run, decodes the staged client, and checks the replacement catalog before installing it. Normal play uses the installed catalog and save offline. See the [Windows client guide](docs/development-client.md#creating-and-recreating-the-databases) for offline setup, save migration, and verification.

All 442 catalog datasets have generated typed keys and payloads in `Eden.Data`; see the [schema and provenance report](src/Eden.Data/Catalog/CatalogSchema.g.json). The [shared catalog provider](docs/protocol/catalog.md#shared-catalog-provider-november-7-2026) preloads core definitions and caches other records within a bounded budget. [Industry and economic inputs](docs/protocol/catalog.md#industry-and-economic-catalog-adoption-november-7-2026) use the shared definitions. [Fitting, inventory and skill inputs](docs/protocol/catalog.md#fitting-inventory-and-skill-catalog-adoption-november-7-2026) also share typed definitions. [Combat and simulation readers](docs/protocol/catalog.md#combat-and-simulation-catalog-adoption-november-7-2026) share immutable profiles and support decisions. [Geography access](docs/protocol/catalog.md#typed-geography-access-november-7-2026) shares indexed locations and static scene definitions. [Reference services](docs/protocol/catalog.md#shared-item-facts-in-reference-services-november-7-2026) reuse shared item facts and typed records. [Location and directory consumers](docs/protocol/catalog.md#shared-location-and-directory-consumers-november-7-2026) also use shared definitions. [Saved configuration](docs/world-configuration.md) supports typed policies, economic overrides and immutable revisions pending restart. The [local Blazor editor](docs/world-configuration.md#local-graphical-editor) browses every dataset, stages settings and displays live process/checkpoint diagnostics in a dark theme through a separate loopback administrator login. [Adoption checks and remaining constants](docs/catalog-adoption.md) describe the final access boundary and build policy.

## Build and run

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

The console shows timestamped startup/shutdown and account/character session summaries, warnings and failures by default. Routine service calls, notifications, gateway requests, handshakes, movement and successful world checkpoints are quiet. Warnings and errors go to standard error. Set `$env:EDEN_VERBOSE_LOGGING = '1'` before starting the server to include routine diagnostics; remove it with `Remove-Item Env:EDEN_VERBOSE_LOGGING` to return to quiet output. The existing `EDEN_TRACE_PERFORMANCE=1` and `EDEN_TRACE_MOTION=1` switches still enable their targeted traces independently of verbose logging. Checkpoint failures always remain visible and pending changes retain the existing save/retry behavior.

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

Bound-call warnings now identify their embedded method. Fitting and brain primes share committed fitting snapshots and operation-local catalog reads; see the [dogma and ship-call latency record](docs/protocol/performance.md#bound-dogma-and-ship-call-latency-november-7-2026).

Unchanged player-frame polling and stable NPC observer records now share resident inputs; see the [multi-player frame comparison](docs/protocol/performance.md#shared-work-across-player-frames-november-7-2026).

Compare NPC and connected-player populations with `.\tools\Measure-NpcCapacity.ps1`; see the [capacity measurements](docs/protocol/performance.md#npc-and-player-capacity-november-6-2026) and [implemented NPC optimizations](docs/protocol/performance.md#indexed-npc-scene-updates-november-6-2026).

Use `EDEN_TRACE_PERFORMANCE=1` for targeted timings and database counters. See [diagnostic settings](docs/architecture.md#session-performance-diagnostics) and [performance records](docs/protocol/performance.md) for replay evidence.

The live host loads the immutable NPC/agent directory before admitting clients. Its name lookup uses one collection parameter so catalog initialization does not bind thousands of individual SQLite parameters; see the [agent directory latency record](docs/protocol/performance.md#agent-directory-first-request-latency-november-6-2026).

### Resident world and background saves

The server runs an authoritative world in memory and checkpoints committed state to disk every 15 seconds. Drone flight, shared observer contacts and nonfatal direct combat use resident objects; see the [drone performance record](docs/protocol/performance.md#resident-drone-flight-and-combat-november-6-2026). Checkpoint SQL projection now runs on a detached image outside the world owner; see the [checkpoint pause comparison](docs/protocol/performance.md#detached-checkpoint-projection-november-7-2026). Graceful shutdown flushes pending changes. Stop the host before editing, backing up or restoring its save; a crash can lose progress since the last completed checkpoint. See [resident state and checkpoint behavior](docs/architecture.md#resident-world-and-background-saves).

## License

Eden's original source and documentation are available under the [MIT License](LICENSE). Supply the EVE client and its resource cache separately. See the [Carbon licensing notes](docs/carbon-license.md) for reference-source attribution.
