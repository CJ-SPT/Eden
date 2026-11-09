# Eden modding

The modding implementation is delivered in milestones. The foundation provides version-1 `.edenmod` archives,
exact dependency resolution, explicit skill overrides, permanent per-world custom skill identities, offline
reference export and immutable composed catalog/client packages. SDK authoring, plugin execution and launcher
cutover follow this foundation; this page records each completed boundary.

## Host commands

Run from the repository root; append `--world PATH` when using a nondefault world. Commands exit without starting listeners.

```powershell
dotnet run --project src/Eden.Server -- --export-modding-reference reference.json
dotnet run --project src/Eden.Server -- --mod-install example.edenmod
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
