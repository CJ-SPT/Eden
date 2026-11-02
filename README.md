# Eden

Eden is a C# server emulator for EVE Online 24.01, Windows build **3662168**. The first playable milestone is one local pilot who can undock, fly a starter ship, dock, and return to the same ship after a restart.

## Current status

Two independent launches of the unmodified client completed the local version and queue-status exchange. Eden listens on `127.0.0.1:26000`, sends its greeting, checks the client build, answers the queue check, and closes the probe connection. The next milestone is a local authenticated session and character selection.

## Build and run

Install the .NET SDK version in `global.json`. Run these commands from the repository root.

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes --no-restore
dotnet run --no-build --project src/Eden.Server
```

The tests use fixtures and loopback sockets. To try the real client, follow the [Windows client setup](docs/development-client.md). The [protocol notes](docs/protocol.md) describe the verified exchange and the next milestones.

## Projects and conventions

| Project | Responsibility |
| --- | --- |
| `src/Eden.Server` | Listen on loopback, apply connection timeouts, and report handshake progress. |
| `src/Eden.Protocol` | Frame startup packets and handle the pinned client compatibility exchange. |
| `tests/Eden.Protocol.Tests` | Verify wire bytes, malformed input, and a complete status exchange. |

Use the root EditorConfig and shared build properties for new projects. Follow [Microsoft's C# conventions](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions) and keep production and test projects separate as in [Microsoft's project layout guide](https://learn.microsoft.com/en-us/dotnet/core/tutorials/libraries).

Eden's original source and documentation are available under the [MIT License](LICENSE). Supply the EVE client and its resource cache separately.
