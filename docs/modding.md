# Eden modding

Eden Modding SDK authors offline skill, C# server-plugin and Python client-extension projects. Versioned `.edenmod`
packages declare exact dependencies and explicit overrides. Hosts compose a selected set into one matching catalog
and client archive; the launcher verifies and installs that required set before play. Activation requires restart.
The SDK solution is directly at `F:/Git Repos/Eden Modding Tools/Eden.Modding.Tools.slnx`.

## Host commands

Run from the repository root; append `--world PATH` when using a nondefault world. Commands exit without starting listeners.

```powershell
dotnet run --project src/Eden.Server -- --export-modding-reference C:\Mods\reference.json
dotnet run --project src/Eden.Server -- --mod-install C:\Mods\example.edenmod
dotnet run --project src/Eden.Server -- --mod-enable PACKAGE_SHA256
dotnet run --project src/Eden.Server -- --mod-status
dotnet run --project src/Eden.Server -- --mod-build E:\EdenDev\3662168
dotnet run --project src/Eden.Server -- --mod-disable PACKAGE_SHA256
```

Install copies a verified package into the world's `.mods` directory and prints its hash. Enable/disable changes
the selected set; build resolves the entire selection and stages a combined package pending restart. Dependencies
must be installed and enabled at their declared exact versions. Disable dependent mods together. Accidental
overlapping skill definitions are rejected; replacements declare both the target and its owning dependency.

Build reads an unchanged build-3662168 client, or a prepared client's verified original archive, and its pinned
Python runtime. It compiles inert code without starting the game. Neither the source client nor the base catalog
is changed. Built sets reuse the existing immutable `.skill-packages` catalog/archive boundary.

## AutoMining

AutoMining is an optional Eden-supplied client package with server-side mining controls. Export it for the target
world and activate it through the normal mod workflow.

```powershell
dotnet run --project src/Eden.Server -- --mod-export-automining AutoMining.edenmod
dotnet run --project src/Eden.Server -- --mod-install AutoMining.edenmod
dotnet run --project src/Eden.Server -- --mod-enable PACKAGE_SHA256
dotnet run --project src/Eden.Server -- --mod-build E:\EdenDev\3662168
```

Use the hash printed by install. Append the same `--world PATH` to all four commands for a nondefault world.
Restart Eden, then launch through Eden Launcher to receive the active mod set. In space, open **Eden Mods →
AutoMining** from the Neocom context menu. Enter comma-separated ore group names such as `Veldspar, Scordite`,
or leave the field empty for all supported ore. Select **Save Filter**, then **Start**. The filter is saved for the
selected character. **Stop** ends the automated run, and the panel displays changes in run status.

See the [AutoMining protocol record](protocol/industry.md#automining-the-current-grid) for cycle behavior.

## Identity and persistence

Custom skill references use `author.mod:skill`; original skills use `catalog:TYPE_ID`. World allocates permanent
numeric IDs above the pinned catalog range. Removed custom definitions are retained as retired skills, including
IDs, character skill points and existing assets. Current definitions cannot require retired skills.

Mod selection, identity allocations and namespaced plugin state live in a `.mods.sqlite` sidecar. Do not delete it
when retaining a world. Include it and the `.mods` and `.skill-packages` directories in backups.

Startup verifies composed files before switching catalogs. Interrupted activation restores the previous marker
and refuses that startup so the host can inspect the pending set before retrying. Previous immutable builds remain
available. Code mods are trusted host-selected software; dependency contexts are not a security boundary.

## References

Skill rules retain the existing [EVE University skills reference](https://wiki.eveuniversity.org/Skills_and_learning)
and pinned catalog behavior. This foundation changes no Blue or Destiny behavior; existing Carbon Blue
`af0d65b29f951260ea09140ca11adf5d40566997` and Destiny `27ee40b38a563d29054b32f64c3ebc87fabaec1f` references remain applicable.
See [native .NET plugin loading](https://learn.microsoft.com/en-us/dotnet/core/tutorials/creating-app-with-plugin-support)
for the dependency-loading model.

## Foundation validation — November 9, 2026

Automated validation: tool/solution restore and build passed with zero warnings/errors; 55 focused .NET tests passed
with no skips, including pinned-client inert compilation and activation recovery. Filter: `FullyQualifiedName~Eden.Protocol.Tests.Administration.ModFoundationTests|FullyQualifiedName~Eden.Protocol.Tests.Administration.SkillEditorTests|FullyQualifiedName~Eden.Protocol.Tests.Administration.SkillBonusEditorTests|FullyQualifiedName~Eden.Protocol.Tests.Administration.SkillBonusBuilderTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting.GameplayDependenciesTests`.
The .NET test run took 14 seconds; the preceding build took 4.88 seconds. Catalog policy (8 tests), formatting policy
(9 tests), and formatting verification passed. The full .NET suite was not requested or run. An initial inert compilation
attempt rejected the nonmatching `E:/Eve` archive; the final run used `E:/EdenDev/3662168`. The prepared-client API fixture
now restores the permitted code hash before validating its pinned original manifest prefix.

Live acceptance: Not performed; agent client testing is disabled.

Optional maintainer checks: none for the foundation's noninteractive interfaces.

## Offline SDK and skill migration — November 9, 2026

The Avalonia solution lives directly at `F:/Git Repos/Eden Modding Tools/Eden.Modding.Tools.slnx`.
It consumes versioned local-feed Eden.Modding and Eden.Gameplay preview packages, with no sibling project references.
Generate Skills, Server, Client or Combined projects; server templates include a compilable C# solution and API reference.
Build and export from the desktop app or CLI. Shared skill forms, bonus discovery, scaling and search now live in Gameplay.

The modes are explicitly **Fixed bonus once learned** and **Bonus per skill level**. A fixed +25% remains +25%
through levels I–V; a per-level +25% reaches +125% at V. These are persistent skill bonuses, not consumables.

Export existing skills with `--export-skill-project ABSOLUTE_FOLDER`, then open that folder in the SDK. The export
retains revisions, draft values and a world-bound logical/numeric identity map. The host rejects replacing legacy
edits until the migrated package is included, and rejects using its map in a different world. Keep legacy artifacts
until the imported mod set activates successfully.

Automated validation: 75 focused Eden tests passed without skips (8 seconds in the runner; 12.04 seconds including
runner setup, preceded by an 8.14-second feature build), covering the foundation, skill editing/bonus presentation,
search/usability and dependency boundaries. Both policy suites (8 catalog, 9 formatting) and formatting verification
passed. SDK build and four focused project/headless tests passed; a generated Combined project also built against
an actual exported catalog reference. SDK tests cover C# compilation/API loading, reproducibility, debug symbols,
project history/reopen, stale saves, prerequisites, draft retention and explicit bonus previews.

Live acceptance: Not performed; agent client testing is disabled.

Optional maintainer checks: inspect the SDK forms at preferred desktop scaling and confirm the two bonus modes read clearly.

## Working server and client extensions — November 9, 2026

Trusted server plugins implement `IServerMod`. Registration supplies namespaced commands and lifecycle/pilot events.
Plugin assemblies and their managed dependencies are loaded into individual collectible dependency contexts,
sharing Eden.Modding and Eden.Gameplay contract identity. Managed assemblies load from streams so immutable package
files are not retained as Windows mappings. Registration closes after startup; plugin assemblies cannot replace
internal DI services through the supported registration API.

Mod commands run outside the simulation owner; catalog reads use the startup snapshot, and skill reads/redemption
operations enter the existing operation scope. State keys and reward references are scoped to the calling mod.
Command exceptions and invalid/oversized JSON replies return structured errors. Plugin event exceptions request
clean server shutdown, including cancellation of active native connections. Events observe committed session
membership after the operation scope completes. Plugins must synchronize their own mutable state across callbacks.

The pinned Python compiler adds namespaced modules and one Eden bootstrap. It wraps NeocomSvc.Run/GetMenu at
uniquely matched code objects, preserving original function bodies, and adds **Eden Mods** to the Neocom context
menu. Client `register(api)` exposes menu/window/session/command helpers. Window rendering uses the pinned native
Window, Button and EveLabelMedium APIs. The bridge calls `edenMods.Execute(modId, command, arguments)` and receives
`success`, JSON text and optional error fields. Selection and command roles are enforced by the server.

Client module composition follows resolved mod order and never imports game or mod module bodies while building.
Static inspection used build-3662168 code archive SHA-256
`37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368`. Blue serialization remains unchanged;
Carbon Blue `af0d65b29f951260ea09140ca11adf5d40566997` remains the reference.

Automated validation: 95 focused .NET tests passed without skips (23 seconds in the runner; 26.26 seconds including
setup, preceded by a 5.21-second feature build). Filter covered ModRuntimeTests, ModFoundationTests,
GameplayDependenciesTests, ServerServicesTests, ConnectionOwnershipTests, PilotServicesTests, TypedSessionTests and
RedemptionTests. Two inert Python client-adapter tests passed. The pinned skill adapter test passed its 11 assertions
in 1.963 seconds without starting the game. Tool/solution restore, zero-warning build, both policy suites (8 and 9)
and formatting verification passed. Initial development checks exposed worker-token substitution and Windows DLL
mapping issues; both were corrected before this final validation. The full .NET suite was not run.

Live acceptance: Not performed; agent client testing is disabled. Native window/menu rendering and input remain unverified.

Optional maintainer checks: after launcher cutover, inspect the example mod's Neocom menu/window and invoke its hello command.

## Launcher and administration cutover — November 9, 2026

Local administration now provides **Mods** at `/mods`, with authenticated/antiforgery-protected install, selection,
build and status controls. The `/skills` authoring page and `/api/admin/skills` endpoints have been removed.
Create and edit skills in the SDK. Historical component fixtures remain to protect shared bonus behavior and the
prior protocol record; they expose no routed authoring page. Existing skill sidecars and packages remain available
for export and migration.

Current server metadata advertises launcher API version 2 and its active `ModSet`. Authentication endpoints retain
`/api/v1` routes. `LaunchRequest.ModSetId` must match the active set before a ticket is issued. The authenticated
archive download is `/api/v2/mod-sets/{id}/code`; stale IDs reject. Signed manifest generation binds the same archive
hash as the advertised set. Pending selections do not affect active clients.

The updated launcher supports API versions 1 and 2, including legacy skill-only packages. Play and launch groups
use the same client-archive verification, pinned-original backup, installation lock, interrupted-update recovery,
server switching and restoration. Close clients sharing an installation before changing its active package.
Update server and launcher together; older launchers reject version-2 servers.

The SDK's final API feed is Eden.Modding `1.0.0-preview.3` with Eden.Gameplay `1.0.0-preview.2`. Existing generated
preview-2 plugins remain compatible with the version-1 supported plugin API. The SDK ships its feed and generates
projects without sibling-checkout references. Its catalog picker explains that the exported file supplies original
skills and supported bonus rules for offline editing.

### Final cutover validation

Automated validation: final worktree builds passed with zero warnings/errors. Eden's focused filter covered
AdministrationTests, ModLauncherApiTests, ModRuntimeTests, ModFoundationTests, SkillEditorTests, SkillBonusEditorTests,
SkillBonusBuilderTests, SkillBonusPresentationTests, SkillEditorUsabilityTests, the Launcher namespace and
GameplayDependenciesTests: **122 passed, no skips**, 11 seconds in the runner (14.11 seconds including setup;
9.41-second feature build). Launcher filters covered SkillPackageTests, ViewModelTests, GroupLaunchTests,
ApiAndLaunchTests, ChatManifestTests and InstallationTests: **62 passed, no skips**, one-second runner duration,
4.83-second build. SDK project and Avalonia headless filters passed **five tests, no skips**, in three seconds,
including independent client-only templates. Both Eden policy suites (8 catalog, 9 formatting) and all repository
formatting checks passed. Archive reads enforce expansion bounds while streaming and reject explicit-null metadata.
Unchanged mod-session observations allocate no new records. The full .NET regression suite was not requested or run.

Live acceptance: Not performed; agent client testing is disabled. Native menu/window rendering, input and gameplay
feel remain unverified. Desktop checks used Avalonia headless controls only.

Optional maintainer checks: inspect the SDK's fixed/per-level previews at preferred scaling; open the example's
Neocom context-menu window; use its hello command after activating the composed set.
