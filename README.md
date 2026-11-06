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
| Investigate native behavior or prior milestones | [Protocol records by topic](docs/protocol.md) |

## Current status

| Area | Implemented scope and limits |
| --- | --- |
| Accounts and characters | Launcher registration/login, three character slots, profiles, skill training, remapping, learning implants/boosters, skill injectors/extraction, redemption and personal clones. |
| Shared world and travel | Multi-pilot flight, docking, warp, gates, collision motion, bookmarks, maps and developer translocation. |
| Ships and fitting | Persistent hulls/holds, personal saved fitting plans, supported Tech I hulls, catalog-backed modules, saved weapon groups and light/medium combat drones on Brutix and Brutix Navy Issue. Market availability does not imply gameplay support. |
| Combat and NPCs | Regional belt pirates, defensive empire guards, passive high-security station convoys, high-security NPC mining/hauling and response fleets, NPC turrets/missiles, declared electronic warfare and local repairs, bounties, security/standing history, ship losses and wrecks. |
| Economy | Mining, reprocessing, supported manufacturing, the testing market, personal wallets, insurance and personal item-exchange/courier/auction contracts. |
| Social features | TLS Local/corporation/private chat, automatic destination Local membership through developer translocation, personal mail and mailing lists, contacts, calendar, notifications, station guests and public pilot information. |
| Player corporations | Creation/membership, administration, roles/titles, shared wallets, bounty tax, LP/EverMarks and share transfers. Offices/asset access and governance remain pending; see the [corporation roadmap](docs/corporations.md). |

See [feature implementation notes](docs/feature-notes.md) for catalog coverage, restrictions and examples, and [protocol records](docs/protocol.md) for milestone evidence. Agent validation is automated; native rendering, input and gameplay feel remain unverified unless a record explicitly documents live acceptance for that change. Historical observations do not establish acceptance of later changes.

## Local game data

Eden uses Entity Framework Core 10 with two SQLite files under `%LOCALAPPDATA%\Eden`. The read-only `catalog.sqlite` holds build 3662168 game definitions. The writable `world.sqlite` holds accounts, pilot profiles and trained skill points, owned ships, their positions and health, item stacks in cargo or station hangars, asteroid fields, wallets, market orders, trade records and manufacturing jobs. Both files are generated locally.

On Windows, install 64-bit Python 3.11 or later and the pinned .NET SDK. Stage the matching client and resource cache, then run the setup command from the repository root.

```powershell
.\tools\DevelopmentClient.ps1 -PrepareFrom 'E:\Eve' -DevelopmentRoot 'E:\EdenDev\3662168'
.\tools\Setup-Catalog.ps1 -DevelopmentRoot 'E:\EdenDev\3662168'
```

Setup downloads and verifies CCP's exact-build export on the first run, decodes the staged client, and checks the replacement catalog before installing it. Normal play uses the installed catalog and save offline. See the [Windows client guide](docs/development-client.md#creating-and-recreating-the-databases) for offline setup, save migration, and verification.

## Build and run

Use the SDK pinned in [global.json](global.json), 64-bit Python 3.11 or later, and PowerShell 7 on Windows. Run from the repository root. After a code milestone is stable, validate in this order; select a .NET filter for the affected behavior and immediate dependencies, and stop to investigate a failed step.

```powershell
dotnet tool restore
dotnet restore
dotnet build --no-restore
dotnet test --no-build --filter 'FullyQualifiedName~Eden.Protocol.Tests.Hosting'
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

Run `.\tools\Format.ps1` to apply formatting after restoring tools and the solution. Run `.\tools\Format.ps1 -Check` to verify without editing files. Both commands run .NET style rules, CSharpier, then strict line-width validation. Verification also runs in the Windows GitHub Actions workflow on pull requests and pushes to `main`.

The root `.editorconfig` is the source of truth. CSharpier 1.3.0 is pinned in the local tool manifest and owns layout; .NET analyzers enforce required braces, file-scoped namespaces and import placement. Do not run the full `dotnet format` command, whose whitespace formatter can disagree with CSharpier. Use `dotnet format style --no-restore --severity warn` for style-only cleanup.

Follow Microsoft's C# conventions with four spaces, Allman braces and a maximum line width of **140 characters**, including indentation. Expand nonempty executable brace bodies. Auto-properties, empty bodies, initializers, patterns and expression-bodied members may remain compact. Prefer expression-bodied trivial accessors because CSharpier otherwise places their statements beside their braces. Project/configuration files use two spaces. Preserve existing line endings.

The strict width check covers authored C#, `.csproj`, `.props`, `.targets`, `.slnx`, root `global.json`, JSON under `.config` and `.vscode`, and `appsettings*.json`/`launchSettings.json`. Generated C# (including the catalog models), SDK outputs, build artifacts and runtime/data JSON are excluded. Documentation and development scripts retain the basic UTF-8, final-newline and trailing-whitespace rules without a width limit.

Wrap long code first. Split literals only when their runtime value remains identical. For content that cannot safely be split, add an entry to `tools/formatting-exceptions.json` with its repository-relative `path` (forward slashes), exact full `line` (including indentation), and a specific `reason`. The checker rejects duplicate or unused exceptions, including exceptions whose content changes or no longer exceeds the limit. The allowlist itself is data and is excluded from width checking.

For format-on-save in Visual Studio, VS Code or Rider, install the CSharpier editor integration and select the repository-local tool. Keep EditorConfig support enabled and avoid running a second whitespace formatter on save. Restore with `dotnet tool restore` after cloning or changing the pinned formatter version.

## Projects and conventions

| Project | Responsibility |
| --- | --- |
| [Eden.Gameplay](src/Eden.Gameplay) | Typed gameplay rules, state, commands and persistence interfaces. |
| [Eden.Protocol](src/Eden.Protocol) | Native wire formats, service dispatch, replies and notifications. |
| [Eden.Server](src/Eden.Server) | Hosting, catalog access and SQLite world persistence. |
| [Eden.Protocol.Tests](tests/Eden.Protocol.Tests) | Automated protocol, gameplay and persistence coverage. |
| [tools](tools) | Development, catalog, auditing and formatting tools. |

Follow the [contributor guide](AGENTS.md) for code style, dependency direction, service ownership and milestones. See [architecture and runtime](docs/architecture.md) for the implementation boundaries and examples.

## Architecture

Gameplay builds independently of Protocol, Server, EF Core and the host container. Server loads and commits typed inputs; Protocol adapts requests and results to the native client. See [architecture and runtime](docs/architecture.md#architecture).

### Session performance diagnostics

Use `EDEN_TRACE_PERFORMANCE=1` for targeted timings and database counters. See [diagnostic settings](docs/architecture.md#session-performance-diagnostics) and [performance records](docs/protocol/performance.md) for replay evidence.

### Resident world and background saves

The server runs an authoritative world in memory and checkpoints committed state to disk every 15 seconds. Graceful shutdown flushes pending changes. Stop the host before editing, backing up or restoring its save; a crash can lose progress since the last completed checkpoint. See [resident state and checkpoint behavior](docs/architecture.md#resident-world-and-background-saves).

## License

Eden's original source and documentation are available under the [MIT License](LICENSE). Supply the EVE client and its resource cache separately. See the [Carbon licensing notes](docs/carbon-license.md) for reference-source attribution.
