# Eden contributor guide

## Project organization

- Put typed gameplay rules, state, commands, and persistence interfaces in [Eden.Gameplay](src/Eden.Gameplay/). Gameplay must build without Protocol, Server, EF Core, protobuf, or a DI container.
- Put wire formats and client protocol behavior in [Eden.Protocol](src/Eden.Protocol/). Put server hosting and SQLite world persistence in [Eden.Server](src/Eden.Server/).
- Place automated tests in [Eden.Protocol.Tests](tests/Eden.Protocol.Tests/) and development scripts in [tools](tools/). Keep the EVE installation, resource cache, and runtime data such as the local world database outside version control.
- Keep project status and build instructions in the [README](README.md), protocol findings and acceptance results in the [protocol notes](docs/protocol.md), and staging and launch instructions in the [Windows client guide](docs/development-client.md).

## Code style

- Follow the root [.editorconfig](.editorconfig) and [Microsoft's C# conventions](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions). In C#, use four spaces, file-scoped namespaces, braces around control-statement bodies, and opening braces on their own lines. Put `System` imports first.
- Keep authored C# and project/configuration lines within 140 characters, including indentation. Expand nonempty executable brace bodies in methods, constructors, control flow, catches, accessors and block lambdas. Auto-properties, empty bodies, initializers, patterns and expression-bodied members may remain compact. Use expression-bodied trivial accessors to avoid CSharpier's inline block-accessor layout.
- Run `.\tools\Format.ps1` to apply formatting and `.\tools\Format.ps1 -Check` to verify it. CSharpier 1.3.0 owns layout; `dotnet format style` owns style fixes. Do not run the full `dotnet format` command or re-enable the conflicting `IDE0055` whitespace diagnostic. Restore the pinned formatter with `dotnet tool restore`.
- Wrap long code or safely split literals before adding a width exception. Unavoidable exceptions belong in `tools/formatting-exceptions.json` with an exact repository-relative path, full line and specific reason. Remove stale entries; duplicate and unused exceptions fail verification. See [formatting coverage and editor setup](README.md#formatting) for generated/data exclusions and integration instructions.
- Start each namespace with the owning project name (`Eden.Gameplay`, `Eden.Protocol`, `Eden.Server`, or `Eden.Protocol.Tests`). Use that namespace for types at the project root. When grouping related types in a feature folder, append the folder name to the namespace. For example, types in an `Inventory` folder under `Eden.Protocol` would use `Eden.Protocol.Inventory`.
- Use two spaces in project (`.csproj`), solution (`.slnx`), `.props`, `.targets`, JSON and YAML files. Save files as UTF-8 with a final newline and trim trailing whitespace.
- Choose names that describe a value's role. Comment on non-obvious wire constants and client quirks so the next contributor can tell why the code handles them that way.
- Avoid magic numbers in code. Use enums for related protocol or gameplay values, such as inventory flags, dogma attributes and service identifiers, where appropriate. Use descriptive named constants for other fixed IDs, limits and wire values. Keep numeric conversions at serialization or persistence boundaries, and document build-specific values and their source instead of scattering unexplained integers through behavior.
- Use the SDK pinned in [global.json](global.json). Follow the [shared build settings](Directory.Build.props), which enable nullable reference types, enforce code style during builds, and treat warnings as errors.

## Services and protocol calls

- Consult the [open-source Carbon engine](https://github.com/carbonengine) when implementing or investigating behavior it owns, such as Blue serialization, transport, or Destiny simulation. Check gameplay service methods and response fields against the pinned client and catalog. Record the relevant source revision and live observations in the protocol notes.
- Mark service classes with `[Injectable]` and declare dependencies in their public constructor. The server scans the Eden.Server and Eden.Protocol assemblies at startup. Each class is available by its concrete type and its Eden interfaces, including interfaces owned by Eden.Gameplay.
- Use the default transient lifetime for stateless handlers. Use `[Injectable(InjectionType.Scoped)]` for one instance per client connection and `[Injectable(InjectionType.Singleton)]` for shared infrastructure. Keep service resolution in the host. Pass connection data, such as the authenticated session ID, to the operation that uses it.
- Add gameplay methods to a feature handler implementing `ISelectedPilotCallHandler`. Use `IPilotCallHandler` for account-level calls available before character selection. Give each service/method pair one owner within its phase. Return `PilotCallResult.Unsupported` for an unhandled call and `PilotCallResult.None` for successful Python None. Return named `BlueModel` replies or explicit primitive values for success, and `PilotCallResult.Failure` or `NamedFailure` for errors. The router owns bound references and selection gating. The session loop sends replies and notifications.
- Keep pure codecs and packet builders independent of the DI container. Protocol services depend on narrow persistence interfaces implemented by the server.
- Use named immutable models for known requests, replies, events, and intermediate data in new or changed code. Use typed collections for homogeneous values and named properties instead of positional arrays or string-keyed dictionaries in gameplay logic. Reuse existing feature models where their meaning matches.
- Keep flexible Python values (`object`, heterogeneous arrays, and dictionaries) inside explicit wire adapters and the low-level Blue codec, or at genuinely dynamic boundaries such as appearance data. Preserve tuple/list distinctions, None, field order, native class forms, and numeric wire representations with explicit conversion; do not use reflection-based serialization or hide untyped data inside a generic result wrapper.
- Implement outbound model conversion through `BlueModel.ToBlue()` (`IBlueWritable`). Parse variable service arguments at the dispatch boundary before calling typed operations. Queue named `PilotNotification` and `DestinyCommand` records; preserve the before-flight notification queue. Use `BlueRead` for inert wire validation and `BlueRows` for shared native row descriptors. The [remaining-object audit](docs/protocol.md#named-protocol-models-final-audit) explains the intentional dynamic boundaries.

## Testing

1. For code changes, run the restore, build, test, and formatting checks from the repository root in the order given in the [README build instructions](README.md#build-and-run).
2. For changes that affect what the pilot sees or can do, **use the Computer Use skill**. Follow the Windows client guide to launch the staged client against the local Eden server. Perform the affected action in the game window and verify the visible result. For an inventory change, select **Eden Pilot**, open **Inventory**, and verify that it shows the expected active ship.
3. Report automated check results separately from live client observations. For live acceptance, record the action taken and what appeared in the client. If the check is blocked, identify the blocker and the specific behavior that still needs verification.

## Milestones

After verifying a milestone, commit its code, tests, and documentation together and push the commit to GitHub. Confirm the commit is on the remote branch before reporting the milestone complete or starting the next one.
