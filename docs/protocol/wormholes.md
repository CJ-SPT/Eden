# Wormholes

## Resident connections â€” November 16, 2026

World version **171** replaces the three permanent reservoir links with resident connection slots. The first
generation preserves their source/destination choices when the pinned catalog contains them. Ordinary known-space
systems receive a replenished wandering connection. C1/C3 systems receive a known-space static; C2 receives a
known-space and a J-space static; C4 receives two distinct J-space static types; C5/C6 receive a J-space static.
Slot choices and density are Eden population policies, not Tranquility's per-system assignments. The ordinary-region
allowlist excludes Jove and other special known-space regions from both source and destination pools. Only classes 1â€“9
participate; Thera, small-ship shattered and Drifter connections are outside this population policy.

Type target classes, lifetime in minutes, total mass, daily mass regeneration and individual jump mass come from
build **3662168** dogma attributes **1381â€“1385**. Initial total mass varies deterministically within 90â€“110% of
the catalog limit. Destinations are selected from sorted, class-matching catalog systems and exclude the origin.
Source slots are populated on system observation. Incoming K162 signatures become visible after the first
successful transit, or deterministically after activation plus five minutes once no more than fifteen hours remain.
This passive reveal schedule is an Eden policy rather than a reproduction of the retail probability curve.

Warp requests activate the lifetime. Unactivated holes do not expire. A successful transfer deducts fitted ship mass,
records directional polarization and moves the ship in the same resident transaction. Rejection changes none of those
values. The final ship may cross with less remaining connection mass than its own mass; collapse then removes both
ends. Time expiry rejects at the exact saved deadline. Collapsed slots replenish after sixty seconds with a fresh
endpoint identity and position. Old item bookmarks, resolved signatures and pending transfers cannot acquire the new
connection. Coordinate bookmarks continue to identify their original location. The six legacy endpoint IDs are never
allocated to new connections. Regeneration caps at initial mass and cannot revive a collapsed connection.

`SavedWormholeConnection` stores slot identity, generation, endpoints, activation/expiry/replacement deadlines,
visibility and mass. `ResidentEconomy` owns these rows. Checkpoint projection is the sole durable write path;
there is no new offline SQL gameplay implementation. Saved polarization remains pilot/direction-specific. The
existing coherent resident owner serializes competing transits. Unchanged signature views retain their identity;
warmed observation and transfer tests assert zero SQL commands/connections and zero fitting recalculations.

### Pinned client and references

The original archive at `E:\EdenDev\3662168\.eden-original-code.ccp` was verified as SHA-256
`37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368`.
`WormholeSetup` in `evegraphics/controllers/clientControllerRegistry.pyj` accepts **1.0, 0.7, 0.4** size bands
and mass classes **1â€“4**, rather than kilograms. `infoContentBase.update_description_caption_and_sub_caption`
uses these fields for Show Info. The headless `wormhole_client_test.py` executes 48 combinations of the actual
native controller with inert model objects. No renderer, client session or game is started.

Gameplay references: [UniWiki wormholes](https://wiki.eveuniversity.org/Wormholes) and
[wormhole attributes](https://wiki.eveuniversity.org/Wormhole_attributes). The pinned catalog takes precedence
where modern reference values differ. Consulted local evejs build-3396210 `wormholeRuntime.js` (SHA-256
`3D20EB4D8041CB2E6C2FDB4F8EF7B3814067DE88951AFB40E9750531902D68C9`) and `wormholeAuthority.js`
(`D25683DF070E80FC8F2AFB2ADD87C39DE5541E7FD1F5DDF5FE0A8483504F9A7E`) for behavioral comparison only;
no reference implementation code was copied. Consulted Carbon Blue
[`Marshal.cpp` at fcbf0c2](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp);
the Blue codec and Destiny packet formats are unchanged. The existing wormhole jump effects and cancellation RPC
remain in use. There are no new live observations.

### Validation

**Automated validation:** Development began with exact filter
`FullyQualifiedName=Eden.Protocol.Tests.Exploration.WormholeTravelTests.WarmWormholeTravelKeepsPolarizationResidentUntilCheckpoint`.
The native controller check passed 48 cases. Lifecycle tests cover last-transit collapse, exact expiry, hidden exit
reveal, unchanged rejection, replenishment, stale identities and checkpoint/restart recovery. The combined affected
filter passed **241 cases** in **132.48 seconds** (feature build **2.69 seconds**). Solution restore/build passed
with zero warnings/errors. Both Python policy suites passed (8 catalog and 9 formatting cases). The final
resident-only scan-availability guard and a further post-restart ID-allocation assertion are covered by a subsequent
focused run. That assertion exposed a route-key/endpoint-sequence name collision after restart; the endpoint
allocator now has a separate checkpointed sequence, and the exact restart regression passed in 10.14 seconds.
The final wormhole/probing/initialization/migration filter passed **72 cases in 71.76 seconds** (feature build
**2.23 seconds**) after that correction. Formatting verification checked **2,341 files**; line-width,
documentation links and diff checks passed. This last filter was
`FullyQualifiedName~Eden.Protocol.Tests.Exploration.Wormhole|FullyQualifiedName~Eden.Protocol.Tests.Exploration.ProbeScanning|FullyQualifiedName~WorldInitializationTests|FullyQualifiedName~WorldMigrationTests`. No full .NET regression suite was requested or run.

Final affected filter:

```text
FullyQualifiedName~Eden.Protocol.Tests.Exploration.Wormhole|FullyQualifiedName~Eden.Protocol.Tests.Exploration.ProbeScanning|FullyQualifiedName~Eden.Protocol.Tests.Flight.FlightSessionTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.PilotGateTravel|FullyQualifiedName~Bookmark|FullyQualifiedName~WorldInitializationTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~Crimewatch|FullyQualifiedName~CombatAnomalyWorldTests|FullyQualifiedName~AnomalyWarpInTests|FullyQualifiedName~TypedFlightTests
```

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Inspect native stability/age labels while rolling a hole, and scan the replacement.

## Sleeper combat and system effects — November 16, 2026

Ordinary C1–C6 systems now populate class-appropriate Sleeper combat anomalies. The 24 standard combat reports
bind to native dungeon geometry and NPC types from build 3662168. Arrival starts the encounter; trigger kills and
saved wave deadlines use the existing resident anomaly machinery. Completion, replacement, NPC destruction,
wreck contents and salvaging share the resident transaction and checkpoint path. New Sleeper operations require
the resident world. They do not introduce an offline SQL gameplay implementation.

Sleepers use native Unknown faction 500021 and have no NPC bounty. Wrecks contain the NPC's reported blue-loot
quantities. Salvaging selects from the fifteen classic Ancient Salvage materials using Eden's existing deterministic
salvage selection and empty-result policy; retail salvage probabilities are not asserted. Catalog-authorized NPC
buyers pay the four documented blue-loot prices: 200,000, 500,000, 1,500,000 and 5,000,000 ISK. This corrects the
buyer's unit price without adding suppliers, asks or unauthorized buyers. Sales retain the ordinary tax, inventory,
wallet and rejection rules.

The six wormhole phenomena use all 36 native class-specific beacon programs from `mapSecondarySuns`,
`typedogma` and `dogmaeffects`. The resident fitting graph applies their native selectors and source values to
ships, modules, ammunition and drones. System modifiers are exempt from fitted-module stacking penalties.
Native maximum-attribute clamps still apply. NPC attributes are unaffected. Moving between different environments
invalidates the resident fit; coordinate changes inside the same system do not. Initial `GetAllInfo` carries the
native `systemWideEffectsOnShip` dictionary, keyed by `(sourceItemID, sourceTypeID)` with sets of effect IDs.
Arrival and exit emit deduplicated `OnSystemWideEffectStart` and `OnSystemWideEffectStop` triples. An empty
initial dictionary does not discard the handler's pending exit notifications, matching the pinned client's behavior.

### Sources and reproducibility

[UniWiki wormhole sites](https://wiki.eveuniversity.org/Wormhole_sites) supplies the class/site index; each embedded
report records its own article URL and revision. [Perimeter Ambush Point](https://wiki.eveuniversity.org/Perimeter_Ambush_Point)
provides a checked example: the full encounter yields 8.6 million ISK in blue loot.
[Sleepers](https://wiki.eveuniversity.org/Sleepers), [wormhole space](https://wiki.eveuniversity.org/Wormhole_space)
and [Tech 3 production](https://wiki.eveuniversity.org/Tech_3_Production) describe the rewards and system effects.
NPC drop quantities come from the [WHDBX Sleeper index](https://evewh.ru/sleepers/); each embedded entry records
its detail URL. Index names are reconciled with native catalog identities because some detail-page headings are
incorrect. No reference implementation code was copied.

`Import-WormholeReports.py`, `Import-SleeperLoot.py` and `Export-WormholeCatalog.py` reproduce the reports,
loot and bounded native test catalog. The report importer also retains guarded data/relic research and rejects
conditional reports it cannot represent; these records are not admitted as combat anomalies. Capital/Drifter
escalations and special wormhole classes are outside this combat population.

Consulted local evejs `wormholeEnvironmentRuntime.js` (SHA-256
`9B9BF08F13BB786E2FE31EA8093F327A53592FDF6DC9707E86267CEA0F3B7435`) and `systemWideEffectsSync.js`
(`6428C1400F14BAE6ADAC2C6497BA785A151A07F5C549D4FDB1FC10E2AE5F9310`) for behavioral comparison.
The original native archive hash is recorded above. The headless native test now also executes the actual
`systemWideEffects.pyj` start/stop handlers with inert services, including duplicate and final-stop behavior.
No client process, rendering or live gameplay is involved.

### Validation

**Automated validation:** Exact-method development checks preceded affected-class runs. Data-driven resident
tests exercise all six classes through real fatal-damage settlement, every wave, blue loot, Ancient Salvage,
completion, replacement and checkpoint/restart. Separate tests cover fixed-price redemption, unchanged rejected
sales and reopened balances. All 36 environment programs are checked against native values and attribute clamps,
including effect removal and checkpoint/restart. Warm site and coordinate reads assert zero SQL commands,
connections and fitting recalculations. The native catalog audit found exactly 36 effect types across 1,038 systems,
all using supported selector and operation forms. The catalog fixture reproduced byte for byte.

The affected filter passed **792 cases in 8 minutes 53 seconds** after fixing an extra catalog read during absent-system
scans. The exact scanner regression passed in **13.43 seconds** (feature build **7.01 seconds**). Three unrelated
checks failed identically on the preceding pushed commit `3e9de794` and were excluded from the final affected run:
`ModuleAndBrainReadsShareTheCommittedGraphAndReturnIndependentAttributes` expects at most nine SQL commands
but observes ten; `AllFiveStaticProfilesHaveImplementedHeatAndRetainExplicitHullDependencies` expects type 49713
to be rejected although it is admitted; `MaintenanceUsesPricePriorityAndRejectsDuplicateConsumptionAfterRollback`
leaves its deliberate failing SQL trigger installed and fails during teardown. The two earlier affected sequences were
interrupted after detecting these failures; they are not reported as successful runs. Baseline checks ran in a separate
detached worktree, with no changes to the baseline source.

Affected filter (excluding the three exact method names above):

```text
FullyQualifiedName~Wormhole|FullyQualifiedName~SleeperSiteTests|FullyQualifiedName~CombatAnomaly|FullyQualifiedName~AnomalySpawnRule|FullyQualifiedName~AnomalyWarpIn|FullyQualifiedName~DogmaGraphTests|FullyQualifiedName~PassiveFittingTests|FullyQualifiedName~FittingSnapshotTests|FullyQualifiedName~CombatDroneTests|FullyQualifiedName~DroneHullTests|FullyQualifiedName~Heat|FullyQualifiedName~OverloadFitting|FullyQualifiedName~NpcSupplyTests|FullyQualifiedName~NpcRestingOrderTests|FullyQualifiedName~Eden.Protocol.Tests.Information
```

Tool restore, solution restore/build, both Python policy suites (8 catalog cases in 1.347 seconds and 9 formatting cases
in 0.021 seconds), native controller/system-effect checks, formatting (2,350 files), line-width, documentation links and diff checks
passed. After placing payloads and catalog models in `Eden.Data`, the final solution build passed with zero
warnings/errors in **88.82 seconds**. The focused `SleeperSiteTests|WormholeEnvironmentTests` filter passed
**11 cases in 41.00 seconds** (feature build **18.30 seconds**). These cases cover the final source and resource layout.
No full .NET regression suite was requested or run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Inspect a Sleeper site and native system-effect icons, and redeem blue loot at an
authorized NPC buyer.
