# Information modules and salvage

[Documentation index](../README.md) / [Protocol index](../protocol.md)

## Celestial Show Info resource reads — November 9, 2026

The continued evejs comparison found a public-gateway dependency missing from Sun and Planet Show Info. In pinned
build **3662168**, `Info.GetCelestialStatisticsForSun` asks `sovereigntyResourceSvc` for stellar power before finishing
the Attributes tab. `ConfigurationDataSource._prime_data_from_external_source` sends
`eve_public.sovereignty.resource.star.api.GetAllConfigurationsRequest` and turns the gateway's previous **501**
reply into `DataUnavailableError`; this sun path does not catch that error. The corresponding planet path requests
planetary resource definitions and catches unavailable data, so it omitted the resource attributes instead.

The gateway now serves these three read-only native messages:

| Request | Reply and limits |
| --- | --- |
| `star.api.GetAllConfigurationsRequest` | A successful empty `configurations` collection lets the sun window retain its physical attributes. No authoritative stellar-power configurations exist in Eden's pinned catalog, so the optional power attribute remains absent. No spectral-class estimate is invented. |
| `planet.api.GetAllDefinitionsRequest` | Sorted definitions from the existing typed `sde/planetResources` catalog: planet ID, power, workforce and optional reagent type, amount per cycle, cycle duration and capacities. A content-derived semantic version identifies the immutable projection; an equal known version returns native **304**, otherwise **200** includes the current definitions/version. |
| `planet.api.GetDefinitionsVersionRequest` | The current exact version returns **200** and its definitions. Absent/stale versions return native **404**; historical resource snapshots are not stored. |

The full type prefix for the planet requests is `eve_public.sovereignty.resource.planet.api`; the star request uses
`eve_public.sovereignty.resource.star.api`. Malformed payloads return **400** without affecting other gateway
routes. Replies preserve request correlation/application IDs. Public catalog data requires no selected pilot and
does not expose or change saved-world state. The World directory shares an immutable catalog definition; repeated
warmed reads issue no SQL or connections. Its revision covers the projected values, so catalog changes invalidate
the client's resource cache even within one pinned build.

The installed pinned dataset contains **25,798** planetary resource definitions. Its reagent cycle periods are
**3,600 seconds**, preserved as protobuf durations. The native Info window derives the displayed amount per minute
from the full cycle amount/duration. The SDE does not publish a security-split percentage; the wire field retains
its zero default, and this correction enables no skyhook, harvesting, sovereignty-hub or resource-transfer gameplay.
Planets absent from the resource catalog retain their physical attributes without fabricated resource values.

Other inspected paths and boundaries:

| Path | Audit result |
| --- | --- |
| NPC corporation CEO → character header | Existing public NPC profiles and `corpmgr.GetCorporationIDForCharacter` already resolve the catalog corporation. No session-corporation fallback is needed. |
| Militia rank → Hierarchy | The local `facwar.GetCharacterRankOverview(session.charid)` calls the existing zero-argument `GetMyCharacterRankOverview`; the apparent missing same-named remote method is not used. |
| Stargate → destination | Existing slim-item `jumps` rows provide `toCelestialID` and `locationID`; the native info callback uses them directly. |
| Station → Services/Location | Existing station replies supply geography, ownership and service masks. Standing restrictions and security-office eligibility are local static checks. |
| Ship overview and item attributes | Existing native killmark-read shape and local/static or primed attribute paths are retained. This audit adds no per-hull killmark persistence or mutable-item gameplay. |
| Player alliances, Upwell/customs structures and anchored secure-container expiry | These require authoritative gameplay/state Eden does not currently implement. The audit does not fabricate owners, memberships, structures or expiry dates. Inventory-only secure-container info returns before the anchored-container expiry read. |

Evidence: inert inspection of preserved `E:/EdenDev/3662168/.eden-original-code.ccp`, SHA-256
**37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**. Reviewed `infosvc`, the resource service,
`sovereignty/resource/client/data_sources`, `messenger`, the resource cache and the generated public protobuf
descriptors. Binary descriptors were extracted as raw marshal string data, preserving non-ASCII bytes; no client
Python was imported or executed. A fixture independently encoded using those descriptors checks Eden's outgoing
planet/reagent bytes. Only enclosing API types use `Any.TypeUrl`; the named value projections preserve the native
field numbers, unsigned widths, duration representation and deprecated/current reagent-ID fields.

Secondary reference: local **evejs 261108.1** `server/src/_secondary/express/gatewayServices/sovereigntyGatewayService.js`,
SHA-256 **44B9E0C466CE42368EC8DBC489C7D35E4F92D9C4D9C15A91685C42E1DD953124**, and
`server/src/services/sovereignty/sovStaticData.js`, SHA-256
**37AF5EF18049E9AB12A986F6C68CC7E709D994AB814C3D6FE8AB2774A5170A47**. Eden uses its pinned typed SDE values;
evejs's older imported authority, synthetic resource fallbacks and active sovereignty implementation are not imported.
Gameplay context follows [EVE University's sovereignty resources](https://wiki.eveuniversity.org/Sovereignty) and
[Orbital Skyhook resource overview](https://wiki.eveuniversity.org/Orbital_Skyhook). The existing Blue/Destiny transport
is unchanged; the [previously checked Carbon reference](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp)
continues to own that encoding evidence.

**Automated validation:** Development began with exact filter
`FullyQualifiedName=Eden.Protocol.Tests.Gateway.CelestialInfoGatewayTests.SunInfoCanPrimeTheStarResourceConfigurationInsteadOfFailingTheWindow`.
It reproduced **501 instead of 200** (build **8.97s**, tests **3.42s**). An initial implementation build caught a
misplaced import; after correcting it, the exact regression passed (build **7.66s**, tests **2.75s**).
The expanded gateway filter passed **16 tests** (build **7.23s**, tests **8.28s**) before adding the independent
native-descriptor byte assertion. Cases cover real World/host composition, power/workforce/reagent values, empty
catalogs, native cache statuses, changing catalog versions, old immutable snapshots, reopen, malformed requests,
invalid unsigned inputs and warmed read counters. The first integrated validation built successfully and passed
**76/77** cases, then stopped on the pre-existing `GeneratedCatalogModelsHaveNoUntypedJsonProperties` guard. It
scanned the handwritten `CatalogStoredRecord.Payload`, whose raw JSON is the documented catalog browser boundary.
The guard now exempts only that display record; generated payloads remain fully checked. Its exact correction
filter passed (build **5.89s**, tests **2.63s**). Final validation on integrated main **c63535c6** passed tool/solution
restores, solution build with zero warnings/errors, **77 focused .NET cases in 13s**, both Python policy suites
(**8 catalog + 9 formatting**), formatting (**1,564 files**), width, documentation-link and diff checks.
The ordered restore/build/test/policy/format sequence took **57.95s**. A separate read-only catalog audit validated
the projected numeric ranges for all **25,798** installed records. The catalog-access inventory was regenerated.
Native semantic-version inspection also confirmed that equality/hash identity preserves build tags used by the
resource revision. No full .NET suite was requested or run. Final filter:
`FullyQualifiedName~CelestialInfoGatewayTests|FullyQualifiedName~GatewayRequestsTests|FullyQualifiedName~MapStartupTests|FullyQualifiedName~MapNavigationTests|FullyQualifiedName~Sovereignty|FullyQualifiedName~CatalogProviderTests|FullyQualifiedName~CatalogSchemaTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting`.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Restart and open Sun Show Info; open Planet Show Info for a null-security planet
with catalog resource data. Check that Attributes finish loading and known resource values appear. Native rendering
and client-side construction remain unverified.

## Probe launcher undocking crash correction, November 9, 2026

The maintainer reported `Nullable object must have a value` at `PilotProbeScanningStore.Launch` when activating a probe launcher. Ordinary undocking saves a flight position without an explicit system ID; navigation resolves that position through the ship's last station. Probe launch instead dereferenced the nullable ID, aborting the connection before its transaction committed. Existing tests always supplied an explicit system and missed this state.

Probe launch, deployed-probe limits, recovery, destruction, scan startup and scan completion now resolve the same effective system: the explicit flight system takes precedence, otherwise the last station's catalog system applies. A missing flight position still means docked. No saved-world migration, protocol shape, probe profile or scan rule changes are required.

Automated coverage exercises native module activation, notifications, perfect scan resolution and warp authorization with both explicit and station-inferred systems. Additional cases cover split launches and unchanged inventory at the eight-probe limit, recovery/destruction, pending-scan restart, and docked rejection without inventory/probe changes or result disclosure. The initial native regression reproduced the reported exception exactly. An initial docked test compared positions from before scanning; its assertion now captures the deployed state after scan placement.

Gameplay reference: [EVE University probe scanning](https://wiki.eveuniversity.org/Probe_scanning). Rechecked Carbon [Destiny Ballpark.cpp at 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ballpark.cpp) for existing movement ownership. This correction is confined to Eden's saved-position interpretation; Blue and Destiny behavior are unchanged. Pinned build-3662168 module activation and probe reply/notification adapters are reused.

Automated validation: the final ordered tool restore, solution restore/build, focused .NET tests, both Python policy suites and formatting verification passed in **160.66 seconds**. The solution built with zero warnings/errors; **169 .NET cases passed with zero failures/skips** (75.97 seconds including runner startup), along with **8 catalog + 9 formatting policy cases** (3.227 + 0.056 seconds). Formatting verified **1,441 files**. The new documentation anchor, UTF-8, final newline, trailing whitespace and `git diff --check` checks passed. No full .NET regression run was requested.

Final affected filter: `FullyQualifiedName~ProbeScanningTests|FullyQualifiedName~ProbeScanningRuleTests|FullyQualifiedName~HackingWorldTests|FullyQualifiedName~AmmunitionTests|FullyQualifiedName~StationUndockTests|FullyQualifiedName~FlightSessionTests|FullyQualifiedName~WorldInitializationTests`. Development started with exact method filter `FullyQualifiedName=Eden.Protocol.Tests.Exploration.ProbeScanningTests.NativeLaunchScanNotificationsAndWarpRequireAPerPilotPerfectResult`; it initially passed the explicit-system case and failed the station-inferred case at the reported launch line. After correction, both native cases and the restart lifecycle passed; the docked assertion correction passed its exact method filter before final validation.

Live acceptance: **Not performed; agent client testing is disabled.** The reported failure is historical evidence, not acceptance of this fix.

Optional maintainer checks: after restarting the updated server, undock normally, launch probes from the fitted launcher, scan, and recover them.

## Hacking and archaeology, November 9, 2026

Native data and relic analyzers now open the client's hacking minigame against ordinary pirate exploration containers. The supported cohort contains all **ten published static analyzer profiles** in build 3662168, including integrated analyzers and their actual virus coherence, strength, utility slots, range, CPU/power, skill and hull restrictions. Hacking and Archaeology skills, operational hull bonuses and supported analyzer rigs feed the existing fitting graph. Integrated analyzers retain both required-skill contributions. Purloined Sansha equipment retains its Echelon restriction; this milestone does not admit unrelated hull capabilities.

### Discoverable sites and earned contents

The pinned catalog export admits **209 ordinary pirate definitions: 105 data and 104 relic sites, containing 1,001 hackable container placements**. Selection requires a known pirate faction, the ordinary data/relic archetype, the appropriate name/security tier, one room, real hackable container types and no NPC entity anchors. Container coordinates retain their offsets from the catalog entry object. Scenery, decorative objects and exact retail site scripts are not reconstructed. No combat layout, evejs content pack or older-build geometry is imported.

Eligible known-space pirate systems receive one data and one relic signature appropriate to their local faction and security band. They use the existing progressive probe solver, per-pilot resolutions, scanner IDs, native warp validation and shared space scene. The data and relic scan categories use pinned radar/magnetometric attributes **208/210** and archetypes **45/44**. Discovery reveals no exact position until the normal perfect scan. Combat signatures keep their existing content and progression. Exploration scanner identifiers occupy the reserved sequence range starting at 8,000,000, separate from ordinary combat-site sequences.

Population and timing are explicit Eden policy: site generation uses the existing pirate-population setting, a site expires after 24 hours, and clearing or expiry schedules replacement after ten minutes. A site clears when every container is destroyed or successfully hacked and emptied. Partial progress and container identities survive restart. Replacement gets new site/container identities and cannot reuse the previous pilot's resolved warp target. Existing ordinary probe/Heron production and catalog manufacturing provide the equipment foundation; no finished analyzer or exploration input is granted by this feature.

Winning creates real owned container inventory in the same transaction that marks the container hacked. Data containers award published datacores or generic decryptors; relic containers use the existing faction salvage pools (normal salvage in high/low security, with one third of null-security containers using advanced salvage). Quantities and weighting are an explicit initial Eden distribution: two seeded rolls of 1–3 units, merged normally when they select the same type; one third of data containers use the decryptor pool. Data reward pools are broad catalog pools; exact retail drop weights are not reconstructed. The saved container seed fixes its contents before the attempt and retries cannot reroll or grant them twice. There are no blueprint-copy, filament or special-site rewards in this cohort. Only the successful hacker can retrieve these contents under Eden's current individual loot-rights policy.

Opening and transferring contents independently recheck the current ship, system, outcome, owner and 2,500 m surface range. Transfers reuse normal hold rules, volume checks, stack identity/splitting and item notifications. Failed capacity or ownership checks leave the earned contents unchanged. Collecting the final contents updates the shared slim state and can retire the site. This adds actual exploration material sources, without adding substitute NPC offers for other missing industrial materials.

### Minigame, authority and native calls

One fitted analyzer activation spends capacitor and saves its module cooldown, immutable fitted profile and exclusive container reservation atomically. The analyzer must be online on the active ship, pass normal skill/hull and nanite-repair guards, and have the container locked and in fitted range. The native effect is `dogmaIM.Activate(moduleID, 'doHacking', targetID, repeat)`; repetition never starts automatic attempts. Direct `hackingMgr.StartNewGameInstance` requests cannot supply a target, difficulty or virus stats to bypass this admission.

The native `ClickedOnTile`, `UsedUtilityElement`, `UsedUtilityElementOnVirus` and `QuitHackingAttempt` methods operate the server-owned board. `OnHackingTurnComplete` carries explicit event dictionaries in a Python list, coordinates as tuples, and native tile/virus/utility forms. Hidden tiles disclose neither subtype nor combat stats. Invalid actions leave the board unchanged and acknowledge input. Native turn results, analyzer dogma state, `effects.TargetScan` and the container's `hackingSecurityState` update through the existing notification/Destiny queues. Normal `invbroker.GetInventoryFromId` and inventory transfers retrieve earned contents; no custom loot RPC is introduced.

The pure Gameplay engine implements offset-hex adjacency, reveal/block rules, core and defense combat, antivirus/firewall/restoration/suppression, caches, distance hints, self repair, kernel rot, secondary vector and polymorphic shields. Restoration never heals the core; defenses retaliate only when they survive, and lethal retaliation cannot be undone by a later repair tick. Earlier board snapshots remain unchanged. The **8×8 full-grid generator, defense distributions, cache generation, difficulty tuning and deterministic restoration ordering are Eden reconstructions**, not a recovered CCP server algorithm. Rare/special defenses and exact board-distribution fidelity remain unverified.

World version **131** adds exploration sites, container outcomes and saved active boards. Each accepted turn commits before its events are published. Quit, deactivation, lock loss, range loss, warp/travel, changing ships and disconnect abandon the active attempt; each abandonment counts once. Two failures destroy an ordinary container. Startup abandons any interrupted board once while retaining paid capacitor/cooldowns, existing failures and completed loot. No offline hack succeeds or pays rewards. Saved tables participate in the ordinary detached checkpoint image. Warm signature reads and active/idle hacking monitoring do not issue SQL or rebuild fittings; mutation commands retain their transaction boundary.

### Evidence and validation

Inert inspection used the original build-3662168 `code.ccp`, SHA-256 **37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**: `hackingcommon/hackingConstants`, native hacking UI service/window/tile/utility consumers, offset geometry, and the `hackingSecurityState` slim consumers. No client Python was executed. `tools/Export-HackingCatalog.py` reproduces both the typed content payload in `Eden.Data` and the automated-test fixture from the read-only pinned catalog.

Gameplay references: [EVE University Hacking](https://wiki.eveuniversity.org/Hacking) and [Relic and data sites](https://wiki.eveuniversity.org/Relic_and_data_sites). Consulted Carbon [Blue Marshal.cpp at af0d65b29f951260ea09140ca11adf5d40566997](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/Marshal.cpp) for the existing list/dictionary/tuple encoding and [Destiny Ballpark.cpp at 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ballpark.cpp) for unchanged scene/update ownership. The earlier evejs 261108.1 audit informed the feature comparison; the implementation and pinned-build export are authored for Eden, with no evejs source or content-pack import.

Automated validation: **835 affected .NET tests passed, zero failures/skips**, including **50 hacking/archaeology cases**. The test step took **523.79 seconds** wall time (feature build 1.38 seconds); the complete ordered restore/build/test/policy/format/export/documentation sequence took **569.88 seconds**. The solution built with zero warnings/errors. Both Python policy suites passed (**8 catalog cases in 1.101 seconds; 9 formatting cases in 0.024 seconds**), and formatting/line-width verification checked **1,441 files**. The content and fixture reproduced byte-for-byte, the catalog-access audit reported zero violations, and documentation links, UTF-8/newlines and diff checks passed. A separate content audit confirmed all 1,001 placements match their site's analyzer skill and lie within 250 km of the entry anchor. Final validation covers the integrated `1394b8e0` base plus this milestone; a pre-integration affected run was intentionally interrupted to include the concurrent combat layout and warp-in corrections and is not counted as a pass. Development failures in reward-pool selection, fixtures and warp-alignment cancellation were corrected and covered by the final run. No full .NET suite was requested or run. Development began with exact filter `FullyQualifiedName=Eden.Protocol.Tests.Exploration.HackingRuleTests.CoreRetaliatesOnlyIfItSurvivesAndEarlierSnapshotsRemainUnchanged`. Automated coverage includes native generated-board wins for both analyzers, normal inventory delivery, probe resolution, module variants, skills/rigs, immutable calculations, rejection and unchanged-state checks, failures/restart, replacement, checkpoint capture and warm-read counters. Ghost sites, Sleeper caches/sites, wormholes, career/mission hacking objectives and multicore event boards remain separate work.

Final affected filter: `FullyQualifiedName~Eden.Protocol.Tests.Exploration|FullyQualifiedName~Eden.Protocol.Tests.Targeting|FullyQualifiedName~Eden.Protocol.Tests.Information|FullyQualifiedName~Eden.Protocol.Tests.Flight|FullyQualifiedName~ModuleFittingTests|FullyQualifiedName~EquipmentBonusTests|FullyQualifiedName~SmallHullBonusTests|FullyQualifiedName~ShipRigTests|FullyQualifiedName~ShipContentCoverageTests|FullyQualifiedName~Eden.Protocol.Tests.Inventory|FullyQualifiedName~TypedInventoryTests|FullyQualifiedName~InventoryPersistenceTests|FullyQualifiedName~InventoryStackMergeTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~WorldInitializationTests|FullyQualifiedName~DetachedCheckpointTests|FullyQualifiedName~LiveWorldTests|FullyQualifiedName~TypedCallResultTests|FullyQualifiedName~PilotBindingsTests|FullyQualifiedName~ProductionCatalogTests|FullyQualifiedName~ProductionInventoryTests|FullyQualifiedName~StarterProductionTests|FullyQualifiedName~MissionRetrievalTests`.

Live acceptance: **Not performed; agent client testing is disabled.** Native animations, interaction and gameplay feel remain unverified.

Optional maintainer checks:

1. Fit a data or relic analyzer, resolve the matching signature and approach/lock a container to open the native board.
2. Use utilities, complete or abandon a hack, then inspect container/bracket changes and collect earned contents.
3. Reopen after restart and check preserved container progress, normal loot delivery and replacement scanner entries.

## Probe scanning and combat signatures, November 9, 2026

The native probe scanner now supports fitted probe launchers, real owned scanner-probe ammunition, up to eight deployed probes per pilot/system, formation destinations and range steps, timed scans, progressive results, recovery, destruction, expiry and reconnecting to saved probes. Core launchers reject combat-sized probes. Expanded launchers accept core and combat scanner probes; both scan **signatures only** in this milestone. Ship/drone hunting, cloaking, hacking, wormholes, DED complexes and expeditions remain separate work.

Seven published static launcher profiles and six scanner-probe profiles are admitted from build 3662168, along with the ten published rangefinding, pinpointing and acquisition arrays. The four ordinary exploration frigates consume their existing probe bonuses. Astrometrics, Astrometric Rangefinding, Astrometric Pinpointing and Astrometric Acquisition feed the fitting graph; scan strength, deviation, duration, supported rigs and equipment update on the next scan after skills or fittings change. The native launched-probe bonus fallback receives actual ship/equipment contributions.

### Content and scan policy

Eligible pirate regions receive up to **two additional hidden combat sites**, separately from their ordinary anomalies. This initial cohort uses existing low-difficulty, report-backed anomaly layouts and waves as **Eden signature variants**, not reconstructions of CCP's gated signature complexes. Population settings still govern new generation. Hidden sites reuse the shared arrival, NPC combat, bounty/wreck, completion and ten-minute replacement lifecycle. They are omitted from anomaly lists and Agency anomaly counts. Both pilots observe the same site and its committed progress.

The pinned client exports probe ranges, strength, deviation, lifetime and result thresholds, but no authoritative server-side scanning solver or signature population distribution. Eden uses a documented deterministic approximation: coverage falls with squared center distance within each probe sphere; effective strength falls with the catalog range-step factor. Average covered strength is divided by `20 * max(1, site difficulty)`, multiplied by `min(1, covered probes / 4)` and a geometry factor between 0.5 and 1. The geometry term uses the largest scalar triple product of normalized probe-to-site vectors. Fewer than four covered probes or nearly coplanar/coincident geometry cannot produce a perfect result. Unchanged formations never improve or reroll by repeated analysis.

Unresolved error follows a stable site-specific direction with magnitude `minimum covered maximum deviation * (1 - certainty)`. The initial overlay exposes only a position rounded to whole AU and a one-AU uncertainty. The combat group appears at **25%**, the name at **75%**, and exact coordinates/direct warp at **100%**. Partial results carry an uncertain sphere rather than a warpable point. A reported-position/range-reduction test proves the initial coarse result can be refined to a perfect fix.

Probe repositioning and recovery are currently immediate authoritative transitions; the paid scan itself observes its fitted duration. Physical probe balls, probe travel/return animation timing and reconnect throttling are not simulated. Combat probes do not disclose other ships in this release.

### Protocol, persistence and equipment supply

`dogmaIM.LaunchProbes(moduleID, count)` and ordinary `Activate(moduleID, 'useMissiles', targetID, repeat)` share the launch owner. Native `@systemScan.RequestScans(probeDict)` accepts named probe records, validates owned IDs, finite destinations and legal range steps, and derives profiles from the server's fitted catalog inputs. Client-supplied type, scan range, strength and lifetime cannot override them. `RecoverProbes`, `DestroyProbe` and `ReconnectToLostProbes` share ownership/system checks. Notifications use `OnNewProbe`, `OnRemoveProbe`, `OnSystemScanStarted` and `OnSystemScanStopped`. Tracker full state retains the four-part tuple and adds the signature map; membership changes use `OnSignalTrackerSignatureUpdate`.

World version **128** adds probe rows, pending scan snapshots and per-pilot resolved-signature records, plus a signature discriminator on encounter rows. Launching consumes the real magazine and commits probe creation/cooldown together. Recovery checks cargo capacity and deposits the original probe types atomically; duplicate recovery cannot deliver again. A scan's authoritative snapshot and deadline survive restart. Completion checks current ship/system, live probes and site availability, then records earned resolutions once. Completion requires the scan's ship to be in space in the same system. Completion/replacement invalidates stale warp targets; one pilot's resolution does not authorize another pilot's direct signature warp. Ordinary point bookmarks and fleet travel keep their existing rules.

The basic **Heron / Core Probe Launcher I / Core Scanner Probe I** production cohort uses pinned recipes and ordinary industry delivery/market unlocks. Its blueprint types are **952 / 17939 / 30014**. The catalog lists corporation **1000035** for the Heron original and **1000125, 1000132, 1000137, 1000143** for the launcher/probe originals; existing institutional supply selects their actual stations. This introduces no new finished-goods grant. The Heron joins material-valued insurance. Advanced launchers/probes retain the existing supply boundaries.

The reported scanner-ID problem showed multiple rows as `EDN-00`. The earlier `EDN-001` sequence varied only its clipped final digit. IDs now use deterministic letter-first `AAA-000`, `BAA-000`, etc. identifiers derived from persistent site identities; replacement gets a new ID and restart preserves it. The suffix also varies visible digits before its final digit as the letter space cycles. Existing site identities, waves and rewards are preserved; clients should reopen their scanner after the updated server starts.

### Evidence and validation

Inert inspection used the original build-3662168 archive, SHA-256 **37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**: `scanSvc.pyj`, `probescanning/{const,probeTracker,scanHandler,results,util,explorationSites,scanBonusController}.pyj` and the signature/anomaly overlay consumers. No client bytecode was executed. `tools/Export-ProbeCatalog.py` reproduces the fixture from the read-only pinned catalog, including prerequisite/bonus and basic recipe closure. Gameplay references: [EVE University probe scanning](https://wiki.eveuniversity.org/Probe_scanning) and [exploration](https://wiki.eveuniversity.org/Exploration). Their guidance supplies the career loop; the pinned client/catalog supply build-specific constants, and the solver/population choices above are explicit Eden policy.

Consulted Carbon [Blue Marshal.cpp at fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp) for native instance/dictionary/list/tuple forms, and [Destiny Ballpark.cpp at 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ballpark.cpp) for existing warp/state ownership. Existing codecs and ship flight are reused; no reference implementation was copied and no new physical probe simulation is claimed.

Automated validation: **532 unique focused .NET cases passed, zero failures/skips**. The 507-case affected run took **371.86 seconds** wall time; the separate 25-case insurance run took **10.41 seconds**, with no overlapping cases. The final ordered restore/build/affected-test/Python/format sequence took **399.13 seconds**. Solution build completed with zero warnings/errors; Python policy suites passed **8 catalog + 9 formatting cases** (0.781 + 0.015 seconds); formatting checked **1,406 files**. All nine added/changed local documentation links/anchors and deterministic fixture regeneration passed. Warm probe monitoring asserts zero SQL commands, connections and fitting calculations.

The first broad run was intentionally interrupted after **377 passes and two failures** in the shared hull-bonus test helper. Newly operational probe bonuses were falling through to its missile probe or missing its Astrometrics mapping. The helper now loads actual probe inputs and selects the appropriate launcher/charge; both exact failing cases passed before the final affected run. The earlier ordinary-output solution build was blocked by the maintainer's running server; final validation used the separate artifact output and left that server running.

Final affected filter: `FullyQualifiedName~Eden.Protocol.Tests.Exploration|FullyQualifiedName~Eden.Protocol.Tests.Information|FullyQualifiedName~AmmunitionTests|FullyQualifiedName~ModuleFittingTests|FullyQualifiedName~PassiveFittingTests|FullyQualifiedName~EquipmentBonusTests|FullyQualifiedName~SmallHullBonusTests|FullyQualifiedName~UtilityHullTests|FullyQualifiedName~ShipRigTests|FullyQualifiedName~ShipContentCoverageTests|FullyQualifiedName~Eden.Protocol.Tests.Industry|FullyQualifiedName~RegionalMarket|FullyQualifiedName~MarketProgression|FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~WorldInitializationTests|FullyQualifiedName~DetachedCheckpointTests|FullyQualifiedName~LiveWorldTests|FullyQualifiedName~TypedCallResultTests|FullyQualifiedName~PilotBindingsTests`. Additional insurance filter: `FullyQualifiedName~Eden.Protocol.Tests.Persistence.InsuranceTests|FullyQualifiedName~InsuranceServiceTests|FullyQualifiedName~InsuranceLossTests`.

 Development began with exact filter `FullyQualifiedName=Eden.Protocol.Tests.Exploration.ProbeScanningRuleTests.ScannerIdentifiersRemainDistinctInNativeVisiblePrefix`, then the exact lifecycle regression `FullyQualifiedName=Eden.Protocol.Tests.Exploration.ProbeScanningTests.LaunchScanResolveCombatRewardAndReplacementSurviveRestart`. The native module activation and production admission paths are tested separately before widening coverage. Tests cover progressive geometry, invalid/forged inputs, paid deadlines, inventory conservation, recovery rejection, per-pilot warp rights, skill/equipment changes, completion/restart/replacement, detached snapshots, and warm monitoring with zero SQL commands, connections and fitting calculations. No full .NET regression run was requested.

Live acceptance: **Not performed; agent client testing is disabled.** The maintainer's ID screenshot is evidence of the prior display problem, not acceptance of this correction. Native probe-map interaction, animations and gameplay feel remain unverified.

Optional maintainer checks:

1. Fit a Heron, load eight core probes and refine a hidden combat signature from its coarse overlay result to a warpable point.
2. Clear the site, collect its ordinary rewards, and compare scanner removal/replacement and recovery after restarting.
3. Confirm distinct anomaly IDs and ship/equipment scan bonuses in the native scanner.


## Directional scanner during undocking, November 9, 2026

The maintainer reported **Use the directional scanner while in space.** during undocking. Build **3662168** `DirectionalScannerPalette.ApplyAttributes` defaults `scanOnOpen` to true, so restoring the palette can call `ConeScan` before the server has initialized the ballpark. `OnBallparkSetState` requests another scan once the scene is ready. The former server guard reused the self-module readiness check and turned that expected initial refresh into a modal information dialog.

`ConeScan` now returns the ordinary native empty result list while the flight cannot use the scanner, including docked and session-transition states. It clears cached scan names and checks readiness before the cooldown, so a restored palette stays quiet even after a recent scan. It neither queries world contacts nor starts a cooldown while unavailable. Once the ballpark is ready, existing scan limits, contact/name disclosure, warp support and the **1500 ms** cooldown apply. The World operation continues to reject actual docked scans; the transition reply discloses no contacts and does not enable scanning inside stations. Module readiness and flight transitions are unchanged.

Evidence: inert inspection of the original build archive, SHA-256 **37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**. The scanner palette and service modules match the staged skill-overlay client byte-for-byte. [EVE University directional scanning](https://wiki.eveuniversity.org/Directional_scanning) remains the gameplay reference. The existing native list codec follows [Carbon Blue Marshal.cpp at fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp); no codec or Destiny changes were needed.

**Automated validation.** Tool/solution restore and the solution build passed with zero warnings/errors. The combined filter `FullyQualifiedName~Eden.Protocol.Tests.Fittings|FullyQualifiedName~Eden.Protocol.Tests.Information|FullyQualifiedName~Eden.Protocol.Tests.Exploration.CombatAnomalyTests|FullyQualifiedName~Eden.Protocol.Tests.Flight.StationUndockTests|FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.PilotServicesTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting` passed **234 cases**, zero failed/skipped, in **69 seconds** (**72.85 seconds** test-command wall time). Both Python policy suites passed (**8 catalog + 9 formatting cases**), as did formatting verification (**1394 files**), line-width checks and `git diff --check`. The required sequence took **101.77 seconds**. Edited-document encoding/whitespace and **502 local documentation links/anchors** passed; unrelated linked pages were read tolerantly for pre-existing non-UTF-8 punctuation when resolving their headings. No full .NET suite or client acceptance was requested or run. Exact development filter `FullyQualifiedName=Eden.Protocol.Tests.Information.InformationTests.DirectionalUndockRestorationReturnsEmptyUntilBallparkAndDoesNotConsumeCooldown` passed. The regression uses the production router and Blue round trips through two actual undock/snapshot/dock cycles with `TestClock`. It asserts empty docked and pre-ballpark replies, immediate post-snapshot contacts, normal duplicate-scan rejection, quiet docking before cooldown expiry, cleared names, saved docked positions and empty notification queues. Direct World scanning still rejects docked pilots.

**Live acceptance.** Not performed; agent client testing is disabled. The attached screenshot is evidence of the previous failure. Native window restoration remains unverified.

**Optional maintainer checks.** After updating/restarting the server, leave the directional scanner open, dock and undock, then confirm no information dialog appears and scanning returns normal results in space.

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

## Directional scanning completion, November 8, 2026

This milestone completes the earlier cone-scan slice for Eden's existing space entities. `scanMgr.GetSystemScanMgr()` retains its location-scoped `@systemScan` reference. A dedicated scoped handler owns `ConeScan(angleRadians, rangeMetres, x, y, z)`. The client filters result groups through its selected overview preset; there is no server-side overview argument.

The full cone opening is applied about the requested direction, with inclusive center-distance and angular boundaries. Inputs must be finite, the direction must be nonzero, and the requested radius must respect both the ship's `maxDirectionalScanRange` and the legacy maximum **2,147,483,648,000 metres**. Zero radius produces only eligible coincident contacts. All seven native angle presets are covered. Scans work during warp after advancing the existing flight owner. Docking, destroyed-ship transitions and system travel prevent scans until the ship is usable in space.

The pinned client `directionalScanSvc.pyj` sets `THROTTLE_TIME = 1500` milliseconds. Successful scans, including empty results, start the same **1.5-second** session cooldown on the server. Failed requests do not consume a new cooldown. EVE University's current article describes two seconds; this build-specific implementation follows the inspected client constant.

Results retain the native Python list of `utillib.KeyVal` objects with exactly `id`, `typeID` and `groupID`. Inert inspection of `DirectionalScannerPalette.UpdateScroll` shows that off-grid IDs are primed through `cfg.evelocations.Prime`, and `GetScrollEntry` reads each location name. `config.GetMultiLocationsEx` now resolves names from the scanning session's latest successful result, with zero placeholders for unavailable coordinates. Visible-object rows keep priority. Arbitrary requested IDs gain no access, and releasing the scan binding on location change revokes these supplemental names. No distance, coordinates, cargo, fitting, health or pilot identity is added to scan replies.

Eligible contacts include piloted and unpiloted ships in the same system, deployed drones, unexpired wrecks/containers, supported mining-operation NPCs, catalog celestials including stargates, and stations. Hull `directionalScanImmune`, expired logoff presence, ordinary NPCs and beacons are excluded. Wreck and logoff deadlines are evaluated at every read, including exact expiry and resident PvP logoff extensions. Anomalies and their scenery are not added to the scanner. Probes, cloaks and player structures without an existing world representation remain outside this milestone; future entities must supply their actual visibility state.

World reads share immutable system membership/name baselines and per-pilot range/origin inputs under the existing owner. Dependency changes invalidate those baselines. Current ship, drone, NPC and tractor positions are overlaid from resident state before filtering. This explicit read path does not open a transaction or project mutable combat resources just to discover names and positions. The existing command/checkpoint persistence boundaries remain authoritative.

Evidence: hash-verified staged build **3662168**, `code.ccp` SHA-256 **37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**. Relevant inert bytecode: `directionalScanSvc.pyj` (`DirectionalScan`, `ReportCooldownAfterThrottle`), `directionalScanUtil.pyj` (range/angle helpers), `directionalScannerPalette.pyj` (`UpdateScroll`, `GetScrollEntry`) and `directionalScanResultEntry.pyj`. No client bytecode was executed. Gameplay reference: [EVE University directional scanning](https://wiki.eveuniversity.org/Directional_scanning). Carbon Destiny [revision 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ballpark.cpp) was consulted for movement ownership and current position state; this change reads Eden's existing resident motion and changes no Destiny or Blue encoding.

Automated validation: **350 unique affected .NET cases passed** across the final runs. The broader information/anomaly/fleet/storage/drone/resident/checkpoint filter ran 349 cases in **127.78 s**, with 348 passing and one pre-existing migration assertion expecting schema 113 instead of current 118. That assertion now uses `WorldMigration.CurrentVersion`; the final **68 information cases passed in 29.49 s**, covering the corrected migration, the new complete-frame comparison and all scanner changes. The remaining 282 immediate-dependency cases from the broader run are unchanged. Early development runs also exposed incomplete drone catalog metadata and a docked fixture incorrectly requesting a space snapshot; both fixtures were corrected before final validation. Full .NET regression testing was not requested.

Final filter: `FullyQualifiedName~Eden.Protocol.Tests.Information`. The broader filter combined that namespace with `Exploration.CombatAnomalyTests`, `Persistence.PilotFleetTests`, `Persistence.ShipStorageTests`, `Persistence.DetachedCheckpointTests`, `Persistence.LiveWorldTests`, `Combat.CombatDroneTests` and `Combat.ResidentPollingTests`. Tool/solution restore, solution build, both Python policy suites (**8 + 9 cases**, **0.608 + 0.031 s**), formatting and documentation links passed. The final ordered validation sequence took **58.26 s**. The focused development runs cover native binding/reply/name lookup, exact cooldown, warp use, eligibility and immunity, hull range invalidation, same-tick committed membership changes, rollback, expiry and detached-checkpoint reopening. A deterministic 30-read workload compares resident reads enabled/disabled with the same two moving pilots. The enabled path asserts zero SQL commands, zero connections and zero fitting calculations; the disabled baseline asserts nonzero counts. The counter assertions cover world reads. A separate complete-session test runs eight ticks with a scan and encoded output per tick, measuring the final five after warmup. With all resident reads disabled/enabled, the final run measured **17.44/0.16 ms mean** and **19.95/0.30 ms maximum** frame latency. Detached checkpoint capture was **0.35/0.29 ms**, with projection **1.39/1.33 ms**, measured separately from frames. The toggle affects all existing resident readers, so the full-frame difference cannot be attributed solely to the scanner. This small deterministic fixture is not a production-scale latency guarantee. Native rendering remains unverified.

Live acceptance: **Not performed; agent client testing is disabled.**

Optional maintainer checks: open Alt+D, compare narrow and full scans against a named off-grid ship, then repeat during warp and after changing systems. Confirm the name appears and off-grid distance remains unavailable.

## Directional scanner first-open range correction, November 8, 2026

The maintainer reported that the native scanner still displayed **Invalid directional scan cone.**, with a screenshot after the previous completion checkpoint. That observation supersedes any implication that the earlier automated checks established a usable first-open client flow.

Hash-verified build **3662168** (`code.ccp` SHA-256 **37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**) explains the failure. In `directionalScanUtil.pyj`, `GetScanRange` reads `dir_scanrange` with default `const.AU * GetMaxRangeAU()`, already a metre value. `GetScanRangeInMeters` multiplies it by **1000** again, because the saved setting otherwise uses kilometres. With an unset setting, the resulting first-open request is therefore **1000 times the allowed range**. The stock `DirectionalScannerPalette.ApplyAttributes` can scan immediately on opening. The prior synthetic tests supplied valid ranges and missed this native fallback. Inert inspection also reconfirmed `GetMaxRangeM`, `GetMaxRangeKM`, `ConvertKmToAu` and `ConvertAuToKm`; no client code was executed and no settings or client installation was changed.

Typed Gameplay range rules now cap every finite, nonnegative request at the lesser of the ship limit and Eden's global directional limit, before cone geometry is evaluated. The world operation applies that rule before validation and again with its authoritative fitted ship limit. A huge client value can no longer cause this rejection or reveal a contact beyond legal range. Shorter requested ranges are retained. Negative and nonfinite requests, invalid angles and invalid directions remain rejected. This corrects first-open compatibility without interpreting oversized requests as a request for privileged visibility.

The regression sends the pinned first-open range through Blue encode/decode and the native handler, checks an off-grid target exactly at the ship maximum, then checks one metre beyond it after the next cooldown boundary. It runs against both database and resident reads. It failed against the original implementation and passed after the correction. Pure rules cover short/equal/oversized requests, the global ceiling, huge finite values and invalid request/ship limits. The existing catalog-change test now verifies that oversized scans are capped when a hull limit changes.

Gameplay reference: [EVE University directional scanning](https://wiki.eveuniversity.org/Directional_scanning), including finite scan radius and cone selection. This correction changes no Blue codec, transport or Destiny simulation behavior; the previously recorded Carbon revision remains the reference for the unchanged movement owner.

Automated validation: **96 affected .NET cases passed in 30.69 s** using `FullyQualifiedName~Eden.Protocol.Tests.Information|FullyQualifiedName~Eden.Protocol.Tests.Exploration.CombatAnomalyTests`. Tool and solution restore, solution build, both Python policy suites (**8 + 9 cases**, **0.860 + 0.027 s**), formatting and **358 local documentation links** passed. The exact development filter was `FullyQualifiedName=Eden.Protocol.Tests.Information.InformationTests.DirectionalFirstOpenClientRangeIsCappedToTheShipLimit`; both database/resident cases passed in **6.52 s** after reproducing the pre-fix rejection. Full .NET regression testing was not requested.

Live acceptance: **Not performed; agent client testing is disabled.** The maintainer's attached rejection screenshot is failure evidence for the previous revision, not acceptance of this fix.

Optional maintainer check: after updating/restarting the server, reopen Alt+D using the same client profile and verify that first-open scanning and subsequent range changes show results without the rejection dialog.
