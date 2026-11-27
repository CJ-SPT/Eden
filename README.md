# Eden

<img src="assets/icons/eden-server.png" width="80" align="right" alt="">

Eden is a self-hosted C# server emulator for EVE Online. Play solo or invite friends to a persistent world where simulated
pilots mine, trade, and form corporations.

**Client required** · EVE Online 24.01 for Windows, build 3662168. Supply the matching client and resource cache separately.

**[Set up the server](#start-here)** · [Connect and play](#connect-and-play) · [Host with Docker](docs/docker.md) ·
[Gameplay coverage](docs/feature-notes.md#gameplay-overview) · [Documentation](docs/README.md) ·
[Discord](https://discord.gg/ZTxJrZKAXe)

## Start here

### Set up a local server on Windows or Linux

Use Eden's built-in setup command from a self-contained server download or build it from source with the
[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). On Windows, you can generate the catalog directly from
the build 3662168 client folder containing `tq` and `ResFiles`. On Linux, supply the `catalog.sqlite` generated on Windows.

| Installation | Setup command |
| --- | --- |
| Windows | `.\Eden.Server.exe --setup --build-catalog "C:/Eve" --import-market --administrator admin` |
| Linux | `./Eden.Server --setup --catalog "/home/you/eden-inputs/catalog.sqlite" --import-market --administrator admin` |
| From source | `dotnet run --project src/Eden.Server -- --setup --catalog "/absolute/path/to/catalog.sqlite" --import-market --administrator admin` |

Each example prepares the catalog, imports the Standard market snapshot, and prompts for the first administrator's password.
Catalog generation uses 64-bit Python 3.11 or newer. Market import uses Python 3.8 or newer. Both can download their inputs.
If you have a catalog file, use `--catalog` on either platform. If the data directory already contains the catalog,
omit both catalog arguments. [Offline setup and data paths](docs/development-client.md#creating-and-recreating-the-databases)

Once setup exits successfully, start Eden with its normal command (`.\Eden.Server.exe`, `./Eden.Server`, or
`dotnet run --project src/Eden.Server`). Open the [World setup](docs/world-configuration.md#first-start) link printed in
the server console, sign in, and approve the world. Game account creation and sign-in open once the world is ready.

### Connect and play

1. Open Eden Launcher. In **Settings**, connect to `http://127.0.0.1:26002` for a local server, or use the API address from
   your server host.
2. Select your prepared Windows client and choose **Use this client**, or use **Prepare a new Eden client** in the launcher.
3. Choose **Add account** to create an account or sign in. Select the account and click **Play**.

[Get Eden Launcher](https://github.com/CJ-SPT/Eden-Launcher#run) ·
[Build the launcher](https://github.com/CJ-SPT/Eden-Launcher#build-and-verify) ·
[Account setup and remote hosting](docs/launcher.md)

## Customize your world

Use the admin tools to adjust progression, travel, rewards, and production. Eden also supports skill mods, C# server plugins,
and Python client extensions. [World settings](docs/world-configuration.md) · [Modding guide](docs/modding.md)

## Local game data

- `catalog.sqlite` contains game definitions for build 3662168. Eden setup verifies and installs it, and can generate it on Windows.
- `world.sqlite` saves accounts, pilots, assets, and simulation state. Eden creates it when an administrator approves first-start settings.

The default data directory is `src/Eden.Server/data` when running from source and `data` beside a published executable.
Stop the server before copying or moving an existing world. [Save migration guide](docs/development-client.md#moving-an-existing-server)

## Build and run

Use the commands under [Start here](#start-here) to run a local server. For code milestones, run the checks below from the
repository root in order. Replace the sample hosting filter with the affected tests and their immediate dependencies.

```powershell
dotnet tool restore
dotnet restore --artifacts-path artifacts/feature-tests
dotnet build --no-restore --artifacts-path artifacts/feature-tests
dotnet test --no-build --artifacts-path artifacts/feature-tests --filter 'FullyQualifiedName~Eden.Protocol.Tests.Hosting'
python tests/Eden.Protocol.Tests/Persistence/catalog_setup_test.py
python tests/Eden.Protocol.Tests/Formatting/formatting_policy_test.py
.\tools\Format.ps1 -Check
```

Stop at the first failure. Record the test filter, results, elapsed time, and limitations. Run the full .NET suite only for an
explicitly requested regression check. Documentation changes use documentation checks.

[Contributor guide](AGENTS.md) · [Testing guide](docs/testing.md) · [Architecture and runtime](docs/architecture.md)

## Formatting

After restoring tools and the solution, use `.\tools\Format.ps1` to format files or `.\tools\Format.ps1 -Check` to verify them.
The script runs .NET style rules, CSharpier 1.3.0, and line-width checks. For style-only fixes, use
`dotnet format style --no-restore --severity warn`.

Use four spaces and Allman braces in C#. Use two spaces in project and configuration files. The 140-character limit covers
authored C#, project and solution files, `global.json`, JSON under `.config` and `.vscode`, and `appsettings*.json` and
`launchSettings.json`. Generated code, build outputs, and runtime data are excluded.

Wrap long lines where possible. For unavoidable exceptions, record the repository-relative path with forward slashes, the
exact line, and a reason. For format on save in Visual Studio, VS Code, or Rider, select the repository-local CSharpier tool
and keep EditorConfig enabled. [EditorConfig](.editorconfig) · [Formatting exceptions](tools/formatting-exceptions.json)

## License

Eden’s original source and documentation are available under the MIT License. The Carbon notes cover reference-source
attribution. [MIT License](LICENSE) · [Carbon licensing notes](docs/carbon-license.md)
