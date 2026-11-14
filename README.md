# Eden

Eden is a self-hosted C# server emulator for **EVE Online 24.01, Windows build 3662168**. Play solo or connect multiple
pilots to the same persistent world. You’ll need to supply the matching Windows client and resource cache separately.

## Start here

For local setup on Windows, install PowerShell 7, 64-bit Python 3.11 or later, and the .NET SDK pinned in
[global.json](global.json). Run these commands from the repository root, replacing the example paths with your EVE
installation and a separate staging directory. Catalog setup builds the server and downloads the matching CCP data export
on its first run.

```powershell
.\tools\DevelopmentClient.ps1 -PrepareFrom 'E:\Eve' -DevelopmentRoot 'E:\EdenDev\3662168'
.\tools\Setup-Catalog.ps1 -DevelopmentRoot 'E:\EdenDev\3662168'
dotnet run --no-build --project src/Eden.Server
```

- [Windows client setup](docs/development-client.md) covers staging, offline catalog rebuilds and development login.
- [Launcher and remote hosting](docs/launcher.md) covers account setup, connecting with Eden Launcher and configuring TLS.
- [Docker deployment](docs/docker.md) covers hosting in Linux containers with persistent data.
- [Documentation index](docs/README.md) lists setup, gameplay, modding and developer guides.

## Current status

| Area | What you can do |
| --- | --- |
| Flight and fitting | Fly, dock, use gates and autopilot, save bookmarks, and fit supported ships and modules, including all published Tech II subcapital hulls. Specialist module support varies by hull. [Feature notes](docs/feature-notes.md#fitting-weapons-and-modules) |
| Combat and exploration | Fight other pilots or NPCs, [abandon or steal wreck loot](docs/protocol/inventory.md#loot-abandonment-and-theft-november-14-2026), run anomalies and level 1–4 combat, delivery and mining missions, or probe down DED complexes, Ghost Sites and ordinary data and relic sites. [Combat](docs/protocol/combat.md), [mission inventory](docs/missions.md), [scanning and hacking](docs/protocol/information.md) |
| Industry and trade | Mine ore, ice and gas, [jettison cargo](docs/protocol/inventory.md#cargo-jettison-november-13-2026), run planetary colonies, research blueprints, manufacture, place market orders, and [trade items and ISK with docked players](docs/protocol/inventory.md#direct-player-trading). In progressive regional markets, claiming manufactured output unlocks generated NPC sell orders for eligible products. [Industry and markets](docs/protocol/industry.md), [planetary industry](docs/protocol/planetary.md), [market settings](docs/world-configuration.md#progressive-regional-markets) |
| Persistent capsuleers | Encounter simulated travelers, miners, haulers and ratters. Their routines advance while a connected player has a character in the system and resume from saved state when players return. [Population guide](docs/capsuleer-population.md), [simulation rules](docs/protocol/capsuleers.md#occupied-system-simulation---november-12-2026) |
| Fleets, corporations and structures | Fly in fleets, use chat and mail, manage player corporations and shared hangars, and deploy Upwell structures with supported docking and industry services. Specialized structure systems such as moon extraction and structure combat remain pending. [Fleets](docs/protocol/fleets.md), [corporations](docs/corporations.md), [structures](docs/protocol/structures.md) |
| World settings and mods | Tune gathering, rewards, progression, industry and structure timers through administration. Install skill mods, C# server plugins and Python client extensions. [World settings](docs/world-configuration.md), [remote administration in Docker](docs/docker.md#remote-administration), [modding guide](docs/modding.md) |

The [feature notes](docs/feature-notes.md) detail supported content and limits. The [protocol records](docs/protocol.md)
document automated coverage and any live client observations.

Use the [personal notepad and shared settings](docs/protocol/social.md#notepad-and-shared-settings--november-14-2026)
to save private notes and folders, and share overview profiles, inventory filters and fleet broadcast settings with other pilots.

## Local game data

- `catalog.sqlite` contains game definitions for build 3662168. The setup script generates it locally.
- `world.sqlite` contains accounts, characters, assets and simulation state. The server creates it on first startup.

The default data directory is `src/Eden.Server/data` when running from source and `data` beside a published executable.
If you’re keeping an existing world, stop the old server and copy its data before starting the updated server.
[Save migration guide](docs/development-client.md#moving-an-existing-server)

[AutoMining](docs/modding.md#automining) adds opt-in harvesting, player fleet following, multi-system ore routes and station hauling through Eden Mods.

## Build and run

Use the Windows commands in [Start here](#start-here) to prepare data and run a local server. For code milestones, run
the following checks from the repository root in order. Replace the example hosting filter with the affected behavior and
its immediate dependencies.

```powershell
dotnet tool restore
dotnet restore --artifacts-path artifacts/feature-tests
dotnet build --no-restore --artifacts-path artifacts/feature-tests
dotnet test --no-build --artifacts-path artifacts/feature-tests --filter 'FullyQualifiedName~Eden.Protocol.Tests.Hosting'
python tests/Eden.Protocol.Tests/Persistence/catalog_setup_test.py
python tests/Eden.Protocol.Tests/Formatting/formatting_policy_test.py
.\tools\Format.ps1 -Check
```

Stop at the first failure and record the test filter, results, elapsed time and limitations. Run the full .NET suite only
for an explicitly requested regression check. Documentation-only changes use documentation checks.
[Contributor guide](AGENTS.md) · [Automated validation](docs/testing.md)

For project boundaries and server diagnostics, see the [architecture and runtime guide](docs/architecture.md).

## Formatting

After restoring tools and the solution, run `.\tools\Format.ps1` to apply formatting or
`.\tools\Format.ps1 -Check` to verify it. The script runs .NET style rules, CSharpier 1.3.0 and strict line-width
validation. Use `dotnet format style --no-restore --severity warn` for style-only fixes and CSharpier for whitespace.

Follow [EditorConfig](.editorconfig) and the [contributor guide](AGENTS.md#code-style). Authored C# uses four-space
indentation and Allman braces. Project and configuration files use two spaces. The 140-character limit covers authored
C#, project and solution files, `global.json`, JSON under `.config` and `.vscode`, and
`appsettings*.json` and `launchSettings.json`. Generated code, build outputs and runtime data are excluded.

Wrap long lines first. For unavoidable exceptions, record the repository-relative path with forward slashes, the exact
line and a specific reason in [formatting-exceptions.json](tools/formatting-exceptions.json). For format-on-save in
Visual Studio, VS Code or Rider, select the repository-local CSharpier tool and keep EditorConfig enabled.

## License

Eden’s original source and documentation are available under the [MIT License](LICENSE).
See the [Carbon licensing notes](docs/carbon-license.md) for reference-source attribution.
