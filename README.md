# Eden

Eden is a C# server emulator for EVE Online 24.01, Windows build **3662168**. Two local pilots can fly together in Jita using separate Windows clients.

## Current status

On the local server at `127.0.0.1:26000`, **Eden Pilot** can undock the Ibis from Jita IV â€“ Moon 4, fly on a heading, stop, and dock at the Caldari Navy Assembly Plant. **Eden Wing** has a separate account and Ibis at the same station. Both pilots can warp to catalog objects within Jita, see each other leave and arrive in Overview, and coast to a stop under the Ibis’s inertia. Jita contains 18 stations, seven stargates, eight planets, 33 moons, and one star. A restart restores each ship in its hangar or at its last saved position in space.

## Local game data

Eden uses Entity Framework Core 10 with two SQLite files under `%LOCALAPPDATA%\Eden`. The read-only `catalog.sqlite` holds build 3662168 game definitions. The writable `world.sqlite` holds accounts, pilots, owned ships, and their positions. Both files are generated locally.

On Windows, install 64-bit Python 3.11 or later and the pinned .NET SDK. Stage the matching client and resource cache, then run the setup command from the repository root.

```powershell
.\tools\DevelopmentClient.ps1 -PrepareFrom 'E:\Eve' -DevelopmentRoot 'E:\EdenDev\3662168'
.\tools\Setup-Catalog.ps1 -DevelopmentRoot 'E:\EdenDev\3662168'
```

Setup downloads and verifies CCP's exact-build export on the first run, decodes the staged client, and checks the replacement catalog before installing it. Normal play uses the installed catalog and save offline. See the [Windows client guide](docs/development-client.md#creating-and-recreating-the-databases) for offline setup, save migration, and verification.

## Build and run

Install the .NET SDK version in `global.json`. Run these commands from the repository root.

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes --no-restore
dotnet run --no-build --project src/Eden.Server
```

The tests use fixtures and loopback sockets. To try the real client, follow the [Windows client setup](docs/development-client.md). The [protocol notes](docs/protocol.md) describe the verified exchange and flight and two-pilot acceptance results.

## Projects and conventions

| Project | Responsibility |
| --- | --- |
| `src/Eden.Server` | Host the loopback session and own the SQLite world. |
| `src/Eden.Protocol` | Frame and decode messages, negotiate encrypted sessions, and serve character selection, inventory, and first flight. |
| `tests/Eden.Protocol.Tests` | Verify wire formats, login, ship movement, docking, and SQLite persistence. |

The server uses Microsoft DI to discover services marked with `[Injectable]` and creates one scope per client connection. The [contributor guide](AGENTS.md#services-and-protocol-calls) explains service lifetimes and how to add a protocol handler.

Use the root EditorConfig and shared build properties for new projects. Follow [Microsoft's C# conventions](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions) and keep production and test projects separate as in [Microsoft's project layout guide](https://learn.microsoft.com/en-us/dotnet/core/tutorials/libraries).

Eden's original source and documentation are available under the [MIT License](LICENSE). Supply the EVE client and its resource cache separately.
