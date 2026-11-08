# Information modules and salvage

[Documentation index](../README.md) / [Protocol index](../protocol.md)

## Scanners and salvage operations, November 8, 2026

This checkpoint admits all **32 published static module profiles** in the pinned catalog: four cargo scanners, three ship scanners, five mining survey chipsets, six passive targeters, six automatic targeters, six tractor beams and two salvagers. Their native effects, fitting requirements, skills, costs and modifiers are validated. Three capital tractors retain their industrial-capital hull dependency. No hull cohort or supply policy is expanded.

### Native scans and targeting

Cargo and ship scanners use `dogmaIM.Activate(moduleID, effectName, targetID, repeat)` with `cargoScan` or `shipScan`. They require a lock, pay once at activation and produce one result at the saved deadline. They never repeat automatically. Manual shutdown finishes the paid scan; lock loss, invalid travel, offlining and disconnect cancel disclosure without refund. Restart clears pending results while retaining expenditure and cooldowns. The saved paid module definition prevents later skill/fitting changes from altering an earlier cycle.

| Notification | Arguments |
| --- | --- |
| `OnCargoScanComplete` | target ID, Python list of `(typeID, quantity)` tuples |
| `OnShipScanCompleted` | ship ID, capacitor charge, capacitor capacity, Python list of hardware tuples |

Results belong only to the scanning session. Cargo reads expose supported ship holds, wreck/container contents and saved NPC mining cargo without transferring ownership. Ship reads expose fitted player hardware and current capacitor; NPCs use their existing nominal capacitor profile and have no synthetic inventory modules. These are truthful optimal-range snapshots. Exact server ship-scan falloff and hardware sampling are not established by client presentation code; the three ship-scanner profiles remain **partial** for those details. No guessed falloff distribution is enabled.

`Survey` is skill **3551**, distinct from CPU Management **3426**. Its pinned modifiers enter the existing fitting graph once. Duration, native skill brain and paid deadlines share that calculation. Five online survey chipsets apply their mining critical/residue modifiers and `miningScannerUpgrade`, with real group limits. On an eligible hull with `integratedMiningScanner`, `miningScanMgr.perform_scan()` returns Python-list triples `(asteroidID, typeID, remainingQuantity)` for live deposits within the pinned **250 km** radius. Unupgraded tier-one presentation remains client-local.

Directional scanning uses the location-scoped `@systemScan` binding and `ConeScan(angleRadians, rangeMetres, x, y, z)`. Finite cone/range inputs, hull range and d-scan immunity are enforced. Native results contain `id`, `typeID` and `groupID`. Supported off-grid pilot ships, drones, wrecks/containers, mining NPCs and catalog celestials are cone-filtered; ordinary combat NPCs are omitted. Results contain no cargo, hardware or health. Cloaks and probes remain separate milestones.

Passive targeters use `targetPassively` for an ordinary timed lock without publishing the victim's targeted marker. Normal source target limits remain enforced. Successful offensive activation reveals the shared lock. Automatic targeters use `targetHostiles`, add their catalog online target allowance and acquire actual saved attackers within range at a paid cycle boundary. Merely locking an innocent ship does not make it a candidate.

### Tractor and salvage operations

Tractors use `tractorBeamCan` and their catalog range, speed, cost and duration. They require a locked container belonging to the pilot or a current fleet member. Membership changes revoke access before another resident movement step, including observer-first reads. Tractor access grants no loot rights. Two beams cannot claim one target simultaneously. Movement stops at a **1,500 m surface separation**, respects paid deadlines and never overshoots. Earned Noctis range, velocity and cycle bonuses feed actual operations.

Tractor movement lives under the existing world owner. Shared contacts expose moved positions immediately and reuse unchanged views. SQL projection occurs at transaction boundaries and checkpoints; detached captures retain positions from the same relational generation. Failed mutations retain the last committed position and can reload a still-paid tractor. Warmed movement/observer tests assert zero SQL commands, connections and fitting calculations.

Salvagers use `salvaging`. Each attempt pays at the start and resolves once at the deadline. Chance is `(catalog accessDifficulty + effective accessDifficultyBonus) / 100`, clamped to `[0, 1]`. Salvaging skill, hull cycle reductions and additive, unpenalized Salvage Tackle bonuses use the fitting graph. Impossible wrecks and unknown material families are rejected. Failure can begin another paid attempt; success ends the chain. Salvaging another pilot's wreck creates no aggression and transfers no existing loot rights.

World version **113** adds saved information cycles and wreck salvage manifests/completion timestamps. New wrecks receive their material outcome in the destruction transaction. Existing recognized wrecks are backfilled once from stable identities. Success atomically checks cargo capacity, deposits saved materials and marks the wreck consumed. Duplicate/concurrent completion cannot pay twice. Full cargo leaves the wreck, manifest and inventory intact. Empty wrecks disappear; unlooted wrecks become native Cargo Containers at the same identity/position, retaining contents, ownership and expiry.

### Authorized Eden salvage distribution

The maintainer authorized defaults for weights and quantities absent from public server data. EVE University's faction tables determine material eligibility. Basic Amarr/Blood/Sansha, Minmatar/Angel, Caldari/Guristas, Gallente/Serpentis and Rogue Drone wrecks use the listed T1 pools. Advanced recognized empire/pirate wrecks use T2 pools, with Blood and Sansha separated; advanced Amarr player wrecks use the combined Amarr pools. Unknown/special families receive no invented pool.

The distribution is **25% empty**. Otherwise choose one eligible material uniformly, then choose **1–3 units** for small basic wrecks, **1–6** for medium basic wrecks or **1–10** for large/advanced recognized wrecks. Size cohorts use catalog access difficulty. SHA-256 of wreck identity, type and creation time supplies a stable initial draw. This is Eden policy, not a claim about CCP drop rates. Skills and retries never reroll or improve manifest contents.

### Evidence and validation

Gameplay references: [EVE University Salvaging](https://wiki.eveuniversity.org/Salvaging), including distribution, ownership, success chance and loot preservation; [cargo scanning](https://wiki.eveuniversity.org/Cargo_scanning); [directional scanning](https://wiki.eveuniversity.org/Directional_scanning); and [scanning skills](https://wiki.eveuniversity.org/Skills:Scanning). The linked Chruker distribution table was also inspected. Only material eligibility is taken from those tables; quantities/weights are the authorized policy above.

Native evidence: staged build **3662168**, archive SHA-256 **37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**. Inert inspection covers `shipHud/shipUI.pyj` callbacks, `shipscan.pyj` tuple consumers, `asteroid_scanning_service.pyj._perform_tier2_scan`, `mining/client/mining_const.pyj`, `directionalScanSvc.pyj`, directional result consumers and `targetMgr.pyj.LockTargetPassively`. An initial inspection used the ordinary installation archive; all used shapes/constants were subsequently rechecked against the hash-verified staged archive. No client bytecode was executed.

Catalog fingerprint: **45BBDCA5C79ECA0353BF05854CDE25042DA02CA3D4607C34BAFE10693C33B2EA**. `tools/Export-InformationCatalog.py` reproduces the 32-profile fixture with hull/skill/wreck, material and salvage-rig closure. Carbon Destiny [revision 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ballpark.cpp) was consulted for authoritative movement/update ownership. Existing stamped position, special-effect and ball replacement adapters are reused; no reference source was copied and no Blue codec changed.

Automated validation: **245 unique affected .NET cases passed** across the final runs. The 210-case information/targeting/wreck/fitting/rig/coverage/checkpoint/startup/small-hull filter took **216.73 s**, the 36-case targeting correction plus utility hull bonus filter took **76.23 s**, and the 11 pure information-rule cases were rerun after the final finite-vector guard in **3.19 s**. The utility filter overlaps one targeting case; the rule rerun overlaps the main filter. Profiles, paid notifications, unchanged-state rejection, actual-budget Noctis fits, Survey identity, mining scans, targeting, cargo-full rollback, foreign wrecks, fleet revocation, resident movement and reopened checkpoints are covered. The initial combined run was interrupted after exposing a catalog edit-scope fixture error and missing tractor/salvage hull probes; both fixtures were corrected before these passes. Full .NET regression testing was not requested.

Filters: `FullyQualifiedName~Eden.Protocol.Tests.Information` combined with the affected `TargetingTests`, `WreckTests`, `ModuleFittingTests`, `ShipContentCoverageTests`, `ShipRigTests`, `DetachedCheckpointTests`, `LiveWorldTests`, `WorldInitializationTests` and `SmallHullBonusTests` classes; separately, `TargetingTests.LockUsesScanTimeAndNotifiesBothPilotsUntilUnlocked|UtilityHullTests.EverySupportedBonusUsesItsCatalogSelectorAndEarnedAmount`; finally, `FullyQualifiedName~Eden.Protocol.Tests.Information.InformationRuleTests`. Tool and solution restore, solution/affected builds, both Python policy suites (**8 + 9 tests**, **0.716 + 0.018 s**), formatting and local documentation-link verification passed. No game or interactive server acceptance session was used.

Live acceptance: **Not performed; agent client testing is disabled.** Native rendering, input, result-window layout and beams remain unverified.

Optional maintainer checks:

1. Compare cargo/ship scan windows and upgraded mining survey results with known contents.
2. Tractor a wreck on a Noctis and compare another pilot's observer view.
3. Salvage an unlooted wreck, recover its preserved container contents and verify restart cannot pay salvage again.
