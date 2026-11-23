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
| `mobileWarpDisruptorRuntime.js` | `9266f4bf44474ab54d36b270c4881cf100ee26fa9417b9d8a2376e6f705820b5` |

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

## HIC fields and focused scripts — November 16, 2026

Milestone 2 admits all eight published static Warp Disruption Field Generators and both focused scripts. Unscripted
fields follow the current committed HIC position and share probe interception rules. Source loss, docking, travel,
offlining, disconnection and exact paid-cycle expiry remove their gameplay effect. Manual deactivation finishes the paid
cycle; repeats buy a fresh cycle and stop on failed payment, invalid membership, lost locks or targets.

The native script group 908 uses ordinary single-charge loading with no script consumption. Catalog fitted modifiers
change range, capacitor cost, duration, signature bonus and space restrictions. The ordinary generator has a 30-second
cycle; either script reduces it to six seconds. Focused disruption uses 150% of the unscripted range; focused scrambling
uses 80%. Hull skills still modify the actual module graph. Both targeted modes supply the catalog 100-point tackle,
use locks/range and existing Crimewatch legality, and permit empire activation. Scrambling also suppresses MWD;
both focused modes suppress micro jumps. Focused tackle prevents docking at player structures. Capital gate/cyno/jump
consumers remain with capital admission and are not supplied by this milestone.

Active generators reject targeted remote assistance through the existing fitting attribute. Unscripted generators apply
their catalog signature-radius penalty; loaded scripts remove it. Existing paid heat handles generator and launcher overload.
The common resident timed-module owner removes expired focused rows and moving fields, so late callbacks cannot keep an
unpaid tackle or assistance restriction alive. Checkpoints capture paid deadlines and fields; startup clears connection-owned
HIC effects while preserving independent launched probes and spent capacitor/cooldowns.

The pinned default effect is 3380 (`warpDisruptSphere`); script defaults are 6849/6848
(`shipModuleFocusedWarpDisruptionScript` / `shipModuleFocusedWarpScramblingScript`). Native module notifications use
those identities. `effects.WarpDisruptFieldGenerating` receives the earned range in `graphicInfo.range`; observer replay
uses the same typed contact. Scripted beams and incoming EWAR use the existing pinned tackle wire adapter.
The wiki, local evejs snapshot hashes and Carbon revision recorded above remain the references. Contemporary prose and
the pinned focused-disruption effect differ on micro-jump stopping; Eden applies the documented HIC gameplay restriction
explicitly without giving the disruption script MWD suppression.

**Automated validation:** Tool/solution restore, solution build, **559 affected .NET tests**, both Python policy suites
(8 catalog and 9 formatting cases) and formatting verification passed in **321.77 seconds**. Twelve pinned-client probe/HIC
setup cases also passed headlessly. Fourteen combined new-feature cases passed during development, including exact
moving-field, focused-profile and native-focused-route filters through `tools/Test-Feature.ps1`. They cover catalog
admission, typed rejection, movement, penalties, locks, native visuals/EWAR, checkpoint capture and exact expiry.

An initial affected run was stopped at 322 passes and seven failures. Two micro-jump cases exposed a query-adapter
translation issue in the new focused check; explicit enum comparisons corrected it. A complete-session structure-service
fixture could tick into Abyssal monitoring without a resident world; that fixture now starts a resident owner. All three
corrected cases passed together. A development rebuild attempted while the initial test host still held its assembly
produced a file-lock build failure; it was rerun after stopping that host, not counted as a test result.

Four other cases reproduce on unmodified milestone-1 commit `52559fab` and are excluded from the final affected filter:
`CapacitorWarfareTests.AdmittedEnergyHullBonusesConsumeTheirEarnedModifiersOnce` for hulls **49710** and **47269**
(the shared probe helper lacks skill 47870), and both
`RemoteAssistanceTests.FutureLogisticsHullSelectorsHaveKnownConsumersWithoutEnablingTheirHullCohort` cases
(11989/11978 are already admitted T2 hulls). The isolated baseline worktree was removed after saving its TRX evidence.
The structure-service case passed in isolation on that baseline, so it is treated as a timing-sensitive fixture correction,
not a baseline exclusion.

The next broad filter reached 575 passes before being stopped with three additional stale audit failures. Baseline
`52559fab` also reproduces the energy-hull helper failures for hulls **49711** (missing skill probe 3348) and **47270**
(missing skill probe 47871), and the old `MutadaptiveRemoteRepairTests.RestrictionsAndDuplicateFittingPreserveInventory`
assertion that an already-admitted T2 logistics hull cannot assemble. The final filter selects the actual owning classes
by their full namespace/class prefix, rather than matching other class names that contain `RemoteRepairTests`, and omits
the unrelated generic energy-hull audit method. All these broader-run limitations remain recorded; no full suite was run.

Final affected filter:

```text
(FullyQualifiedName~Eden.Protocol.Tests.Propulsion.|FullyQualifiedName~Eden.Protocol.Tests.Persistence.SupportModuleTests.|FullyQualifiedName~Eden.Protocol.Tests.Combat.RemoteAssistanceTests.|FullyQualifiedName~Eden.Protocol.Tests.Combat.RemoteRepairTests.|FullyQualifiedName~Eden.Protocol.Tests.Combat.CapacitorWarfareTests.|FullyQualifiedName~Eden.Protocol.Tests.Structures.StructureTests.|FullyQualifiedName~Eden.Protocol.Tests.Docking.StructureServiceTests.|FullyQualifiedName~Eden.Protocol.Tests.Flight.FlightBoundaryTests.|FullyQualifiedName~Eden.Protocol.Tests.Thermal.ElectronicSupportHeatTests.|FullyQualifiedName~Eden.Protocol.Tests.Ammunition.AmmunitionTests.)&FullyQualifiedName!~FutureLogisticsHullSelectorsHaveKnownConsumersWithoutEnablingTheirHullCohort&FullyQualifiedName!~AdmittedEnergyHullBonusesConsumeTheirEarnedModifiersOnce
```


**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Inspect the moving field's earned radius; verify scripted beams and recipient EWAR indicators.

## Interdiction nullifiers — November 16, 2026

Milestone 3 admits all four published high-slot Interdiction Nullifiers from the pinned catalog. Existing hull fitting
restrictions and admission still apply; this does not enable strategic cruisers or other deferred hulls. One nullifier
can be fitted per ship. Online modules apply the catalog targeting-range, scan-resolution and drone-bandwidth penalties.
Earned hull modifiers operate on the same graph: the Ares role reduces the penalties and reactivation delay and doubles
the immunity duration. Fitting previews and resident calculations share these consumers.

| Nullifier | Base immunity | Reactivation after immunity | Capacitor |
| --- | --- | --- | --- |
| I (58919) | 10 s | 100 s | 50 GJ |
| Compact (58966) | 12 s | 130 s | 50 GJ |
| Enduring (58971) | 12 s | 130 s | 35 GJ |
| II (58972) | 15 s | 150 s | 50 GJ |

A paid activation commits capacitor, the exact active deadline and the reactivation deadline atomically in resident state.
The pinned effect 8117 (`interdictionNullifierBonus`) uses attribute 3115 (`durationHighisGood`) and is warp-safe.
It can activate through an existing cloak without breaking that cloak. Native notifications expose the active effect,
capacitor and next activation time. Nullifiers run one paid cycle without automatic repeat; manual deactivation ends
at the paid deadline. Offlining, source loss and disconnection remove immunity while preserving the cooldown and payment.
Checkpoint projection and reopened checkpoints preserve those costs and deadlines.

Immunity bypasses probe and unscripted HIC departure/catch/drag checks. Focused HIC scripts and ordinary targeted tackle
retain their effects. A nullifier that expires while a ship aligns inside a bubble stops that unfinished warp; an already
in-flight warp retains its selected destination. Fitted Higgs Anchor rigs and burst jammers suppress immunity, including
offline imported burst jammers; their fitted catalog effect also forces the reported ship attribute to zero. Burst-jammer
activation remains a separate feature. Innate shuttle immunity still works without a module.

The wiki and local evejs references above informed immunity eligibility and bubble-only behavior. The pinned catalog
controls profiles and role bonuses. Warmed bubble checks inspect resident membership and exact paid deadlines without
SQL or rebuilding a fit; source mutations remain serialized through the existing world owner.

**Automated validation:** Tool/solution restore, solution build, **484 affected .NET tests**, both Python policy suites
(8 catalog and 9 formatting cases), twelve pinned-client probe/HIC setup cases and formatting verification passed in
**257.96 seconds**. The content-coverage inventory was regenerated from the pinned catalog. Development exact filters
through `tools/Test-Feature.ps1` covered the four paid profiles, native cloaked activation/alignment expiry, incompatible
fits and checkpoint reopen.
The complete nullifier class passed 16 cases before final integration checks. Development exposed repeated fitting
calculations in observer immunity reads; the final lookup uses resident membership/deadlines and asserts zero SQL
commands, connections and fitting calculations. Test setup corrections positioned the cloaked ship away from scenery,
supplied the resident spatial service, and accounted for capacitor recharge after the saved cooldown. A missing namespace
import in the fitted-jammer attribute change caused a development build failure and was corrected before validation.
The first affected validation sequence was interrupted after the native translocation menu's in-space case reached Abyssal
monitoring with no resident world. Its complete-session fixture now starts a resident owner; the failing method is
included in the final affected filter. The interrupted run produced no completed TRX and is not counted as a pass.
No full .NET suite was requested or run.

Final affected filter:

```text
FullyQualifiedName~Eden.Protocol.Tests.Propulsion.|FullyQualifiedName~Eden.Protocol.Tests.Flight.|FullyQualifiedName~Eden.Protocol.Tests.Persistence.PassiveFittingTests.|FullyQualifiedName~Eden.Protocol.Tests.Persistence.FittingConstraintTests.|FullyQualifiedName~Eden.Protocol.Tests.Persistence.ModuleFittingTests.|FullyQualifiedName~Eden.Protocol.Tests.Persistence.ShipRigTests.|FullyQualifiedName~Eden.Protocol.Tests.Persistence.RigRulesTests.|FullyQualifiedName~Eden.Protocol.Tests.Persistence.ShipContentCoverageTests.|FullyQualifiedName=Eden.Protocol.Tests.Persistence.TechTwoHullTests.EveryPublishedTechTwoSubcapitalHasAnExplicitOperationalOrDeferredOwner
```

Merge verification combined feature commit `2b48f0ee` with main's planetary fixes through `fb511962`.
The protocol-index conflict retained both topic entries. Tool/solution restore, solution build, **57 focused .NET tests**,
both Python policy suites and formatting verification passed in **80.56 seconds**. The merge filter was:

```text
FullyQualifiedName~Eden.Protocol.Tests.Propulsion.InterdictionTests.|FullyQualifiedName~Eden.Protocol.Tests.Propulsion.HicInterdictionTests.|FullyQualifiedName~Eden.Protocol.Tests.Propulsion.NullifierTests.|FullyQualifiedName~Eden.Protocol.Tests.Planetary.PlanetaryClientTests.|FullyQualifiedName~Eden.Protocol.Tests.Planetary.PlanetaryProtocolTests.
```

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Inspect nullifier cycle/cooldown indicators while cloaked; verify the native alignment stop at expiry.

## Interdiction cleanup during session ticks

On November 23, 2026, HostCram logged `PersistenceRequiredException` while ending an interdiction effect during a player
tick on revision `2fefe2156`. The tick supplied the pilot's system context. Cleanup took that system's owner before asking
for the shared resident transaction, which rejected the acquisition order and disconnected the session. The container
reported zero restarts and continued writing checkpoints after the incident.

Cleanup now acquires shared ownership before reading the focused tackle edge. The resident transaction then resolves the
source and any ship target to their owning systems and settles the active module, tackle, field and heat together. The
session clears the effect after resident cleanup succeeds. Paid deadlines and cooldowns retain the behavior covered by the
pinned catalog and the [focused HIC](#hic-fields-and-focused-scripts--november-16-2026) and
[nullifier](#interdiction-nullifiers--november-16-2026) records above.

**Automated validation.** Both native expiry flows reproduced the exception with the pilot's system context before the
fix. The focused `HicInterdictionTests`, `NullifierTests` and `InterdictionTests` selection passed all 37 cases after the
fix. The isolated build took 12.15 seconds and reported no warnings. The test runner reported 20 seconds.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** None.
