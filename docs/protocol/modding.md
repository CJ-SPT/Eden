# Modding protocol and activation

## Client package retention — November 15, 2026

Generated packages each contain a complete projected catalog. Retaining every generation and directories left
by terminated compilers caused approximately 26 GiB of deployed data; pruning obsolete generations and two
abandoned builds reclaimed 18.6 GiB while preserving active and rollback sets.

World-owned `SkillPackageRetention` now prunes before and after both publisher paths and after successful host
activation. It reads the active marker, mod Active/Previous/Pending identities and legacy PendingPackage from
read-only sidecars before selecting any deletions. Legacy activation also writes `.skills-previous` atomically
before replacing the active marker. Once mods own the catalog, their durable Previous supersedes that legacy marker.
Interrupted mod activation continues to protect both previous and pending identities.

A shared per-world `.skill-packages.owner` file lease covers compilation, atomic directory publication, staging
and cleanup across publishers and CLI processes. The owner file persists after closing to avoid unlink/reopen races.
Cleanup can remove abandoned recognized build directories whenever it acquires the lease; it cannot acquire
ownership while another updated publisher is building. Concurrent publication fails before creating output.
Mod compiler cancellation now waits for process exit before releasing ownership and removing temporary output.

Cleanup checks saved references first, rejects filesystem links, preserves unrecognized folders, and logs/defer
errors without changing successful publication or startup results. It does not prune installed mod archives,
world saves, original catalogs, edit history or operator backups. No gameplay, Blue or native client wire behavior changes.

### Automated validation

The initial exact development filter was
`FullyQualifiedName=Eden.Protocol.Tests.Administration.SkillPackageRetentionTests.PruningPreservesAllSavedReferencesAndRemovesOnlyOwnedObsoleteDirectories`:
one pass, 43.71-second build and 4.47-second runner. Subsequent focused tests exposed an omitted
`InvalidDataException` cleanup catch and Windows fixture symlink/junction cleanup requirements; these were corrected.

Final pinned tool restore, solution restore/build (zero warnings/errors), the focused filter below, both Python
policy suites (8 catalog and 9 formatting cases) and formatting verification passed in **71.50 seconds combined**.
The focused .NET run passed **53 cases, zero failures/skips**, with a reported test duration of 13 seconds.
`EDEN_TEST_CLIENT_ROOT=E:/EdenDev/3662168` enabled real headless pinned-client publication checks.

```text
FullyQualifiedName~SkillPackageRetentionTests|FullyQualifiedName~SkillEditorTests|FullyQualifiedName~ModFoundationTests|FullyQualifiedName~ModLauncherApiTests|FullyQualifiedName~ModRuntimeTests|FullyQualifiedName~AutoMiningPackageTests|FullyQualifiedName~ServerServicesTests|FullyQualifiedName~GameplayDependenciesTests
```

Coverage includes bounded repeated generations, reopened sidecars, legacy rollback, damaged/missing references,
abandoned directories, shared ownership, filesystem links, actual publication failures and subsequent publisher reuse.
The full .NET suite was not requested or run.

Live acceptance: Not performed; agent client testing is disabled.

Optional maintainer checks: none required for retention; operator data backup retention remains separate.

## Package foundation — November 9, 2026

Version-1 portable packages and host composition retain the existing build-3662168 skill overlay and immutable
client archive boundary. No new native RPC or notification is introduced in this milestone. Shared skill validation
now resides in Gameplay and consumes typed catalog metadata, retaining protected original skill calculations.

Permanent logical/numeric skill mappings and namespaced mod state reside in World-owned SQLite persistence.
Restart activation verifies composed files and retains an interrupted-activation recovery marker. See the
[modding guide](../modding.md#foundation-validation--november-9-2026) for commands, source references and automated evidence.

Live acceptance: Not performed; agent client testing is disabled.

## Offline authoring — November 9, 2026

Skill validation, bonus composition and search are shared Gameplay APIs. Offline projects preserve original-skill
protection and export portable logical references. Migrated projects retain numeric identities through a world-bound
map; another world cannot admit it. No new native wire calls are introduced in this authoring milestone.
See the [SDK and migration record](../modding.md#offline-sdk-and-skill-migration--november-9-2026) for validation.

## Native mod command bridge — November 9, 2026

`edenMods.Execute` is selected-pilot only and accepts exactly three text arguments: mod identity, command and argument
text. Replies use an explicit Blue dictionary adapter with `success` boolean, `json` text and nullable `error` text.
UTF-8 command/reply data is bounded at 64 KiB; command-role requirements are authoritative. Unknown or malformed calls
return structured errors, and other service/method pairs remain unsupported by this handler.

Neocom.Run/GetMenu adapters are inertly compiled against uniquely matched build-3662168 functions. Session notifications
use the inspected native service manager's RegisterNotify path. This milestone changes no Blue codecs or Destiny rules.
See the [runtime record](../modding.md#working-server-and-client-extensions--november-9-2026) for pinned source identity,
Carbon references, validation filters and limitations.

Live acceptance: Not performed; agent client testing is disabled.

## Launcher version 2 — November 9, 2026

Server metadata adds optional `ModSet` (identity, generation, client archive hash/length and ordered mod versions).
Ticket requests add nullable `ModSetId`; the server requires an exact active-set match. The authenticated
`/api/v2/mod-sets/{id}/code` route supplies only the active immutable archive. Authentication and legacy package
routes remain under `/api/v1`. Updated launchers accept metadata versions 1 and 2 and retain their verified archive,
manifest, backup and recovery mechanisms. These HTTP additions do not alter the native Blue/Destiny formats.

The current administration surface is `/mods`. Web skill authoring and its API routes are removed; historical
component/protocol evidence is retained. See the [cutover guide](../modding.md#launcher-and-administration-cutover--november-9-2026).

Live acceptance: Not performed; agent client testing is disabled.

Final automated evidence and selected test classes are in the [cutover validation](../modding.md#final-cutover-validation).
