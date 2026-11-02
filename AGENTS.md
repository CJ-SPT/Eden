# Eden contributor guide

## Project organization

- Put wire formats and client protocol behavior in [Eden.Protocol](src/Eden.Protocol/). Put server hosting and SQLite world persistence in [Eden.Server](src/Eden.Server/).
- Place automated tests in [Eden.Protocol.Tests](tests/Eden.Protocol.Tests/) and development scripts in [tools](tools/). Keep the EVE installation, resource cache, and runtime data such as the local world database outside version control.
- Keep project status and build instructions in the [README](README.md), protocol findings and acceptance results in the [protocol notes](docs/protocol.md), and staging and launch instructions in the [Windows client guide](docs/development-client.md).

## Code style

- Follow the root [.editorconfig](.editorconfig). In C#, use four spaces, file-scoped namespaces, braces around control-statement bodies, and opening braces on their own lines. Put `System` imports first.
- Start each namespace with the owning project name (`Eden.Protocol`, `Eden.Server`, or `Eden.Protocol.Tests`). Use that namespace for types at the project root. When grouping related types in a feature folder, append the folder name to the namespace. For example, types in an `Inventory` folder under `Eden.Protocol` would use `Eden.Protocol.Inventory`.
- Use two spaces in project (`.csproj`), solution (`.slnx`), `.props`, and JSON files. Save files as UTF-8 with a final newline and trim trailing whitespace.
- Choose names that describe a value's role. Comment on non-obvious wire constants and client quirks so the next contributor can tell why the code handles them that way.
- Use the SDK pinned in [global.json](global.json). Follow the [shared build settings](Directory.Build.props), which enable nullable reference types, enforce code style during builds, and treat warnings as errors.

## Testing

1. For code changes, run the restore, build, test, and formatting checks from the repository root in the order given in the [README build instructions](README.md#build-and-run).
2. For changes that affect what the pilot sees or can do, **use the Computer Use skill**. Follow the Windows client guide to launch the staged client against the local Eden server. Perform the affected action in the game window and verify the visible result. For an inventory change, select **Eden Pilot**, open **Inventory**, and verify that it shows the expected active ship.
3. Report automated check results separately from live client observations. For live acceptance, record the action taken and what appeared in the client. If the check is blocked, identify the blocker and the specific behavior that still needs verification.

## Milestones

After verifying a milestone, commit its code, tests, and documentation together and push the commit to GitHub. Confirm the commit is on the remote branch before reporting the milestone complete or starting the next one.
