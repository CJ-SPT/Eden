# Interdiction

## Launched bubbles — November 16, 2026

Milestone 1 adds the Interdiction Sphere Launcher I and both published warp-disrupt probes from build **3662168**.
Ordinary probes have a 20 km radius and 120 second lifetime; surgical probes have a 10 km radius and 180 second lifetime.
A paid launch consumes one loaded probe, preserves the launcher magazine/reload rules and starts the weapons timer.
Only null-security and wormhole systems permit bubbles; Zarzakh is rejected. Corruption-based lowsec exceptions require
the separate faction-warfare simulation and are not enabled by this milestone.

The resident world owns independent probe identities, positions, health and exact expiry deadlines. Launch, charges,
capacitor and cooldowns commit together. Probes survive loss, offlining or departure of their launcher; smartbomb damage
can destroy them. Checkpoint projection writes the saved rows; the operation has no offline SQL implementation.
Expired/dead rows are pruned on the next launch in their system. Views exclude them at the exact expiry deadline.

Warp requests reject departure from inside a bubble regardless of warp core strength. A spherical intersection near the
requested destination catches or drags a warp to just inside the first bubble along its line. The 500 km destination
window and deterministic identity ordering are explicit Eden policies informed by the reference engine. Unrelated fields
and mid-route fields do not intercept the warp. Shared resident movement rechecks departure during alignment, including
ships advanced by another observer; a warp already in flight keeps its destination. Shuttles use catalog bubble immunity.

### References and pinned client

Gameplay references: [Interdictors](https://wiki.eveuniversity.org/Interdictors),
[Tackling](https://wiki.eveuniversity.org/Tackling),
[Heavy Interdiction Cruisers](https://wiki.eveuniversity.org/Heavy_Interdiction_Cruisers) and
[Interdiction Nullifier](https://wiki.eveuniversity.org/Interdiction_Nullifier).
The pinned catalog controls sizes, durations, restrictions and skills where contemporary descriptions differ.

The original client archive SHA-256 is
`37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`.
Inert bytecode inspection confirms `MobileWarpDisruptor.Assemble` reads the type's `warpScrambleRange` and builds the
forcefield; the probe uses the existing native static-ball/slim path. `WarpDisruptFieldGenerating` accepts a range override
in `graphicInfo` for the later HIC milestone. `BaseWarpDisruptSphere` supplies signature and propulsion modifier consumers.
`interdiction_client_test.py` executes eight pinned forcefield setup cases against inert objects.
No client or renderer was started. Carbon's
[Destiny Thunkers.cpp](https://github.com/carbonengine/destiny/blob/114e89fa584f0e1a81eb0a679aabf41af8870072/src/Thunkers.cpp) was consulted for the existing
rigid-ball layout; this milestone adds no new binary ball format.

At the user's request, the local evejs snapshot at `F:\Git Repos\evejs` was consulted for behavior. It has no `.git`
metadata, so the consulted source snapshot is identified by SHA-256 rather than an invented revision. Its server package
identifies AGPL-3.0-only licensing; Eden's implementation is independently authored C# and includes no copied evejs code.

| Reference source under `server/src/services/ship/` | SHA-256 |
| --- | --- |
| `interdictionProbeRuntime.js` | `9e7b90ec2285d51b4a91a491b3d30b86e8c6b4969ac2742d2273b91c48b64b84` |
| `interdictionVolumes.js` | `5aabd7a0f983a5713f18f33cd2f57b9219fc099b39abac0ca1e7baff6c57732b` |
| `warpDisruptFieldGeneratorRuntime.js` | `1cf570256208432cf9ea2baed9edfa9bd3497da32016e04794fa1c0dbe077de5` |

### Milestones and boundaries

1. Launched bubbles: resident probes, warp interception, lifecycle, checkpoints and native launch route.
2. HIC generator: moving fields, focused disruption/scrambling scripts, assistance restrictions and native cycle controls.
3. Nullifiers: eligible fitting, online penalties, paid immunity windows, cooldowns and bubble-only protection.

Anchored mobile deployables, stasis-web probes, capital jump/cyno systems and unrelated specialist modules remain separate
work. Native rendering and interaction are unverified until maintainer acceptance.

**Automated validation:** Tool/solution restore, solution build, 438 affected .NET tests, both Python policy suites
(8 catalog and 9 formatting cases) and formatting verification passed in **268.11 seconds**. The affected filter was
`FullyQualifiedName~Eden.Protocol.Tests.Propulsion|FullyQualifiedName~Eden.Protocol.Tests.Flight|FullyQualifiedName~SmartbombTests|FullyQualifiedName~AmmunitionTests`.
Eight additional pinned-client forcefield cases passed headlessly. Development began with exact geometry, probe-lifecycle
and native-launch method filters through `tools/Test-Feature.ps1`. The tests cover both probe profiles, geometry, security
rejection, launch notifications, smartbomb destruction, exact expiry, checkpoint reopen and warmed SQL-free reads.
An earlier affected run was stopped after 314 passes and nine failures from the newly injected service reading absent
resident fixtures. The correction returns an empty read view without adding SQL gameplay and passed the exact failing
factory method and the complete affected filter above. No full .NET regression suite was requested or run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Inspect a launched bubble and its expiry; verify catch/drag landing and alignment interruption.
