# Planetary industry

[Documentation index](../README.md) / [Protocol index](../protocol.md)

## Saved Omega license and generated colony repair — November 16, 2026

Mythic's generated Athanor supply colonies exposed two separate failures. The native planet view divided by zero
in `planetUILinks.LinkBase.__init__` when an extractor head occupied exactly the same coordinates as its control
unit. The operator repair moved only the 15 ECU anchor latitudes by 0.003 radians, through the resident world owner
and checkpoint. Resource-head positions, yields, installed programs, deadlines, routes, links, goods and wallet
balances were preserved. The repaired checkpoint was reopened and validated before installation, with a fresh
backup and an audit proving that only this pilot's five colony rows changed. This corrects the generated layout;
it does not establish native rendering acceptance or alter ordinary player placement rules.

The yellow **All Exporting Disabled / Upgrade** prompt had another cause. The saved account was already Omega,
but build 3662168 now checks `PublicUserLicenseRequestMessenger.get_user_license_request` through its public
license API, which Eden's gateway does not implement. Client diagnostics recorded HTTP 501 on that request.
The host-signed login adapter now handles only `UserLicenseType.OMEGA` by reading the existing authenticated
`subscriptionMgr.GetCloneGrade` operation. Saved grade 1 becomes a native permanent `UserLicense` with no expiry;
Alpha and unknown grades return no license. Other license types retain their original handlers. Installation is
idempotent and performs no RPC. No account entitlement, public gateway authorization, database schema or gameplay
permission changes were added. The public envelope's client-supplied identity is not treated as a credential.

The red **One or more facility requires attention** indicator is separate again. Native
`ColonyData.IsSomeProductUnrouted` compares routed quantities with the pin's complete output. All 15 generated
ECUs were active and produced base batches of 727,700–900,800 raw units at 100×, while deliberately routing only
1,500 units per 15-minute cycle to feed one basic processor. The unused output therefore triggers the native
warning on every colony. These routes are sufficient for the planned chain; the compatibility adapter does not
suppress that warning. Cross-planet P1 hauling and storage capacity still constrain production.

### Reference evidence

[UniWiki Planetary Industry](https://wiki.eveuniversity.org/Planetary_Industry) and
[colony setup](https://wiki.eveuniversity.org/Setting_up_a_planetary_colony) were checked for Omega restrictions,
routing and factory inputs. The local EveJs license lifecycle also keeps entitlement authority in saved account
state, with a different expiry policy; it was consulted without copying implementation. Its snapshot has no Git
metadata; `newEdenStore/licenseLifecycle.js` SHA-256 is
`533160b8868f35b91811afec1a46a945c4463382fc49c95947ec3ba44c4c5c8e`.
No Carbon-owned codec, transport or Destiny behavior changed.

Inert inspection used the original build-3662168 archive SHA-256
`37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`. Relevant decompressed module hashes:

| Module | SHA-256 |
| --- | --- |
| `eve/client/script/ui/shared/planet/planetUILinks.pyj` | `867a5a83333df3e4ea900073f9d9f5910c4231f9afa1337ac899a597c224f6bf` |
| `user_licenses/client/messenger/request_messenger.pyj` | `df08b00c4937d1e242b20febf374aa71866d2cb69d8a127e2a70408caf9737f7` |
| `user_licenses/client/user_license_svc.pyj` | `aa5d43068a34e74ebe21ad1d660b0c49777f5c7d9631f2856ccce0160b1935ec` |
| `omega/client/clone_grade_svc.pyj` | `e82fa0f2a7205cadeff3a6498c12887e8759ae2aa2e03c699e1fa44a6c249c2b` |

### Automated validation

The initial exact filter through `tools/Test-Feature.ps1 -Filter` was
`FullyQualifiedName=Eden.Protocol.Tests.PilotSessions.CloneLicenseClientTests.NativeOmegaLicenseUsesAuthenticatedCloneGradeWithoutGrantingOtherLicenses`.
The isolated adapter checks permanent Omega, Alpha/unknown rejection, repeated installation and unchanged non-Omega
delegation. The pinned Python 2 fixture also executes the actual native license constructor, `has_license` and
`is_omega_without_grace_period` with inert services, exercising Alpha/Omega transitions without a game session.
Affected planetary, saved clone-grade, pilot-login, license-adapter and overview tests passed **86 checks**, no
failures/skips, in **33.75 seconds**; the strengthened native fixture then passed separately in **2.68 seconds**.
Tool/solution restore and solution build passed with zero warnings/errors. Both Python policy suites passed
**8 + 9** checks. The full .NET suite was not requested or run.
Final solution build, formatting verification, `git diff --check` and local documentation links passed.

### Live acceptance

Not performed; agent client testing is disabled. The repaired save restarted successfully and the launcher API
returned HTTP 200. Native rendering and the removal of the Upgrade prompt require maintainer acceptance.

### Optional maintainer checks

After deploying the adapter, fully reconnect Mythic and open a repaired colony to check the planet surface and
Omega export controls. Inspect an ECU's product routes to distinguish unused extraction output from a stopped
program. Haul the source planets' P1 products to the Kihtaled II factory as described in the operator receipt.

## Resource scan intensity and extraction previews — November 16, 2026

User screenshots showed effectively empty resource bars, a missing selected-resource heat map, and an extraction
preview totaling **37 units** with **100×** extraction settings. A further screenshot showed ordinary level-V
capacity of **25,415 CPU / 19,000 MW** with 100× fitting settings. These are reports of the preceding behavior,
not live acceptance of this correction.

`GetPlanetResourceInfo` previously returned the integer **1** for every available resource. The pinned
`ResourceController.CreateLayout` divides quality by `appConst.MAX_DISPLAY_QUALITY`, which is
`1.21 * 255 * 0.5 = 154.275`; every bar consequently represented about **0.65%** abundance. Replies now derive
quality from the existing deterministic field's surface mean, in the native half-byte-intensity scale. Its
constant spherical-harmonic term supplies this mean; the directional terms integrate to zero.

`GetResourceData` now converts normalized authoritative coefficients to **255×** float32 intensities at the wire
boundary. Native `GetResourceAndRender` first scales the harmonic by `1 / 1.21`, then `ChartResourceLayer` derives
texture alpha from the maximum sample divided by **255**. Sending normalized coefficients into that path made
the heat map nearly transparent. Resource geography and authoritative extraction rules retain their existing
meanings. This correction adds no saved-world data or SQL path.

The signed adapter now makes `ClientColony.CreateProgram` reuse the existing `GetProgramResultInfo` quote for
tuned worlds, matching the authoritative path already used by native installation. Edited heads, resource type,
head radius and temporary ECU IDs reach that quote unchanged. Quantity, cycle time and cycle count come from
the server; native overlap hints remain. Empty-head previews retain native behavior. Extraction-only tuning now
also activates the adapter, and reconnecting to ordinary policy restores the original method. Untuned first
logins remain lazy. Each preview calculation with heads adds one quote RPC; interactive latency is unverified.
Existing programs retain their saved yield; installing a new program uses the active extraction multiplier.

The [adapter import correction](#planetary-adapter-import-correction--november-16-2026) at `476621e7` separately
fixes initialization of native CPU/power and factory output. With the supplied **100×** policy, expected level-V
capacity is **2,541,500 CPU / 1,900,000 MW**; ordinary P1/P2/P3/P4 factory batches produce **2,000 / 500 / 300 / 100**
units. Factory ingredients/cycles, storage capacity and existing route quantities retain their ordinary meanings.

### Reference evidence

Consulted [UniWiki Planetary Industry](https://wiki.eveuniversity.org/Planetary_Industry) on November 16, 2026 for
abundance bars, scan selection and contrast controls. Eden's resource geography remains simulation policy.
The local EveJs `planetMgrService.js` and `planetRuntimeStore.js` were checked for resource qualities and float32
buffer serialization; their SHA-256 identities remain those in the historical reference table below. No reference
implementation was copied.

Inert inspection used the unchanged build-3662168 archive at SHA-256
`37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`:

| Module | Decompressed SHA-256 |
| --- | --- |
| `eve/common/lib/appConst.pyj` | `91fcb869cb6c9ef98ff32d807b947ccb6348b154f1a127239f273806cd3ae60e` |
| `eve/client/script/ui/shared/planet/resourceController.pyj` | `726b3ca0e60f6719c150ae09e0fa6eab97dfa0d48000bbaaf7894bdff5254702` |
| `eve/client/script/ui/shared/planet/planetUISvc.pyj` | `d3c2e8a2a6029391727dcfa5ebfecf93e4cf177620e7c0846737fad925e81171` |
| `eve/common/script/planet/entities/commandPin.pyj` | `c4fc616d4bbc4e45949aa429856b4603ddd2c1b36c879d7bc517de7ec0817ccb` |

The actual native command-pin output functions and `ChartResourceLayer` were also executed under the pinned
Python 2.7 runtime with inert service/buffer stubs. No native module body, renderer, network connection or game
session was started. These checks establish configured fitting values and the bitmap-intensity correction,
not live rendering. Native `SurveyWindow.GetProgramParameters` and `BaseColony.CreateProgram` were inspected as
bytecode to verify the preview boundary and preserved four-value return shape.

### Automated validation

The initial exact filter through `tools/Test-Feature.ps1 -Filter` was
`FullyQualifiedName=Eden.Protocol.Tests.Planetary.PlanetaryProtocolTests.ResourceAbundanceUsesNativeQualityScaleAndMatchesTheSurveyedField`.
It reproduced the **0.65%** abundance failure for all eight planet types. Additional exact-method checks cover
the float32 intensity boundary, signed-adapter previews and equality between temporary quotes, saved ECU quotes
and newly installed 100× programs.

- Tool/solution restore and final solution build passed with zero warnings/errors (**1.68 seconds** combined).
- Final affected tests below passed **79 checks**, none failed/skipped (**34.00 seconds** wall time).
- Both Python policy suites passed: **8 catalog/setup + 9 formatting-policy checks**, **1.35 seconds** combined.
- Inert pinned Python 2.7 command-pin/bitmap checks passed, including repeated tuning and default restoration.
- Formatting verification passed (**30.87 seconds**), along with `git diff --check` and local documentation links.
- The full .NET suite was not run.

Final affected filter, joined with `|`:

```text
FullyQualifiedName~Eden.Protocol.Tests.Planetary
FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.PilotLoginTests
FullyQualifiedName=Eden.Protocol.Tests.PilotSessions.ConcordOverviewTests.SignedLoginExpressionIncludesPortraitsAndConcordColorWithAnEmptyChallengeResult
```

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

- Update/restart the server and reconnect; reopen planet mode and inspect abundance bars and the selected heat map.
- Confirm the level-V 100× command-center capacities above and a newly selected factory's scaled output route.
- Compare the preview and installed yield for a new extraction program; observe response time while moving a head.

## Planetary adapter import correction — November 16, 2026

The next client report still showed six empty colony slots. Read-only verification found the running server at
`b342d418`, with active configuration revision **51**, no pending revision, and `maximumPlanets: 100`.
The clients had authenticated after that server started, ruling out an older deployment or pending setting.

The adapter imported top-level `planetCommon`, which is absent from the pinned stock archive. Build **3662168**
instead provides `eve.common.script.util.planetCommon`; the native `CommandPin` imports this exact module.
The headless scaffolding had supplied a synthetic top-level module, concealing the import mismatch.
The adapter and its default-restoration lookup now use the real fully qualified module name.

Pinned `GPS.__Execute` catches signed-expression exceptions and returns their traceback in the login proof's output
buffer; Eden's existing proof parser reads only the response hash. This explains why an adapter import failure
could leave login successful and the native directory unchanged without appearing in the ordinary client exception
log. Adapter failures now also use the existing bounded local error logger with stage `planetary-tuning`, retaining
the configured maximum and original exception. A separate helper frame preserves the original exception under
Python 2.7 even if the diagnostic logger is unavailable.

### Automated validation

No regression tests or new cases/assertions were added. Existing adapter scaffolding was corrected to expose only
the real namespaced module; its existing lazy-login assertion now checks that same module name.

An isolated headless evaluation used the stock **3662168** `ColoniesPanel.ConstructEmptyEntries` code object under
the pinned Python 2.7 runtime, with inert UI/service stubs. The corrected adapter produced **100 slots**, each with
`slotsTotal = 100`; seven owned colonies left **93 placeholders**. Shared Interplanetary Consolidation stayed at
level V. Reinstalling the default policy restored **six placeholders**. These are native method results, not rendered
UI or deployment acceptance. No game session or client module body was executed.

Existing-check results:

- Initial exact method filter through `tools/Test-Feature.ps1 -Filter`:
  `FullyQualifiedName=Eden.Protocol.Tests.Planetary.PlanetaryClientTests.SignedAdapterScalesFittingAndProductsWithoutCompoundingAndRestoresDefaults`;
  **1 passed**, **18.30 seconds** build and **4.67 seconds** test-command wall time.
- Final tool/solution restore and build passed with zero warnings/errors (**5.76 seconds** combined).
- The [four-part existing affected filter](#planet-limit-directory-and-build-button-correction--november-16-2026)
  passed **5 checks**, none skipped (**9.62 seconds** wall time).
- Both Python policy suites passed: **8 catalog/setup + 9 formatting-policy checks**, **1.54 seconds** combined.
- Formatting verification passed (**31.34 seconds**), along with `git diff --check`.
- The full .NET suite was not run.

### Live acceptance

Not performed; agent client testing is disabled. The running server and saved active configuration were inspected
read-only. This correction was not deployed during that inspection. The user screenshots show the preceding failure;
rendering and deployment beyond six colonies remain unverified until the updated server is installed and reconnected.

### Optional maintainer checks

- Update/restart the server, reconnect, reopen the directory and verify 100 slots and placement beyond six colonies.
- If initialization fails, inspect `%LOCALAPPDATA%/Eden/ClientErrors/character-selection-<pid>.log` for `planetary-tuning`.

## Planet-limit directory and build-button correction — November 16, 2026

A client screenshot after the configurable-limit change showed the native Planetary Industry directory retaining
six **Unestablished Colony** entries. Inert inspection of the pinned build **3662168** identified two additional
client checks that the original Submit-only adapter did not cover:

- `ColoniesPanel.ConstructEmptyEntries` uses the module constant `MAX_NUM_COLONIES = 6` for placeholder rows and
  `skills.GetMyLevel(typeInterplanetaryConsolidation)` for their unlocked count.
- `PlanetEditModeContainer.GetBuildEntryDisabledHint` separately uses effective Interplanetary Consolidation to
  disable command-center construction before Submit, even when the server and Submit validator allow more colonies.

The signed adapter now supplies the active count to all three native functions through private function namespaces.
The directory's placeholder bound and unlocked count follow the explicit setting, including **100 total colonies**.
The build button uses the same count while preserving location, required-building-skill and command-center cargo
checks. Existing owned-colony rows remain unchanged. Repeated logins unwrap earlier adapters; reconnecting to a
skill-based world restores the original functions and six-slot directory behavior.

The reference source is the verified stock archive at SHA-256
`37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`.
Decompressed module SHA-256 values:

| Module under `eve/client/script/ui/shared/planet/` | SHA-256 |
| --- | --- |
| `planetEditModeContainer.pyj` | `1649d89d432233d9be2f6bdc56c1040eb93bd813c2ae3833628e17f194c6f102` |
| `planetsWindow/coloniesPanel.pyj` | `307ada918ede2326c3da56bf32ef7b0e599675544c2d61837ea44fb9deb5f89f` |

The [UniWiki Planetary Industry reference](https://wiki.eveuniversity.org/Planetary_Industry) was consulted again;
its ordinary skill-based limit remains the default. EveJs's count validation remains reference evidence as described
below; this correction changes Eden's pinned native UI adapter, with no copied reference implementation.

### Automated validation

No regression tests were added, at the user's request. The existing signed-adapter test's import scaffolding was
updated for the additional native UI classes; its test cases and assertions remain unchanged. Native directory and
build-button behavior was investigated through inert bytecode inspection, without executing game modules.

Validation on the correction:

- Initial exact existing filter through `tools/Test-Feature.ps1 -Filter`:
  `FullyQualifiedName=Eden.Protocol.Tests.Planetary.PlanetaryClientTests.SignedAdapterScalesFittingAndProductsWithoutCompoundingAndRestoresDefaults`;
  **1 passed**, **4.50 seconds** test-command wall time, **74.44 seconds** initial build.
- Tool/solution restore and final solution build passed with zero warnings/errors (**7.06 seconds** combined).
- Existing affected checks below: **5 passed**, none skipped (**9.35 seconds** wall time).
- Python policy suites: **8 catalog/setup + 9 formatting-policy checks passed**, **2.47 seconds** combined.
- Formatting verification passed (**36.49 seconds**); `git diff --check` passed.
- The full .NET suite was not run.

Affected filter, joined with `|`:

```text
FullyQualifiedName~Eden.Protocol.Tests.Planetary.PlanetaryClientTests
FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.PilotLoginTests
FullyQualifiedName=Eden.Protocol.Tests.PilotSessions.ConcordOverviewTests.SignedLoginExpressionIncludesPortraitsAndConcordColorWithAnEmptyChallengeResult
FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.CorporationClientDiagnosticsTests
```

### Live acceptance

Not performed; agent client testing is disabled. The supplied screenshot is user evidence of the previous behavior,
not verification of the correction. Native rendering, input and placement beyond six colonies remain unverified.

### Optional maintainer checks

- With the setting at 100, restart/reconnect, reopen the directory and verify more than six available colony slots
  and command-center deployment beyond the sixth colony.

## Configurable planet limit — November 16, 2026

**Settings → Planetary industry → Maximum planets (0 = skill-based)** saves
`settings.planetaryIndustry.maximumPlanets`. The default **0** retains one planet plus Interplanetary Consolidation
level. An explicit **1–100** replaces that count with a total per pilot. Missing fields in older exports default to 0;
negative values and values over 100 fail form and imported-policy validation. Changes activate on server restart,
with the signed login adapter supplying the active limit on reconnect.

Admission counts authoritative resident colonies under the existing world owner, before consuming command-center
cargo, charging the wallet or allocating pin identities. Abandoning a colony frees a slot. Lowering the setting or
restoring skill-based limits preserves existing colonies, including production and edits. Omega status, planet
eligibility, cargo, wallet and building/upgrade requirements remain enforced. Checkpoints retain colonies and active
configuration. An explicit count removes the Interplanetary Consolidation extraction floor; Command Center Upgrades
protection remains. Returning to skill-based mode caps the consolidation floor at its real maximum of level V even
when more than six grandfathered colonies exist.

### Reference evidence

Consulted [UniWiki Planetary Industry](https://wiki.eveuniversity.org/Planetary_Industry) and
[Planet Management skills](https://wiki.eveuniversity.org/Skills:Planet_Management) on November 16, 2026:
ordinary gameplay permits one initial planet plus one per Interplanetary Consolidation level, up to six.
The optional count override and 100-planet configuration bound are Eden policies.
The local EveJs reference `planetRuntimeStore.js`, identified by the SHA-256 in the historical record below, also
checks one plus this skill's level; it is reference evidence only, with no implementation copied.

Inert inspection of build **3662168** confirmed that
`eve/client/script/environment/planet/clientColony.pyj` / `ClientColony.PostValidateCreatePin` checks `GetMyPlanets`
and effective Interplanetary Consolidation before Submit. Its decompressed bytecode SHA-256 is
`80198cc84f980a7ee1b866f99515650cc5f793677e3d66a5cd8b21b83e9b7758`.
The initial signed adapter gave only that native function a private skills-service view with the configured
count; the directory/build-button correction above extends this coverage. It preserves native wallet and
required-building-skill validation, leaves shared skill data untouched,
unwraps previous tuning on reconnect, and restores the original function for default worlds. Default first logins
still leave planetary modules unloaded. No separate client package or transport/Blue change is required.

### Automated validation

Focused tests cover typed count rules, form/import validation, old-export defaults, pending activation, resident
admission/rejection, unchanged rejected cargo/wallet/identities, abandonment, lower-limit edits, reopened checkpoints,
skill-extraction floors, and signed-adapter repeated tuning/reset with preserved wallet and building requirements.
An additional headless check ran the actual pinned native validation code object under the client Python 2.7 runtime
against inert service stubs for limits 1, 8, 100 and skill-based defaults, including wallet/skill rejection and resets.
No client module body, rendering, network connection or game session was started by that check.

The initial exact filter was
`FullyQualifiedName=Eden.Protocol.Tests.Planetary.PlanetaryConfigurationTests.ResidentPlanetLimitEnforcesAdmissionWithoutChangingRejectedState`
through `tools/Test-Feature.ps1 -Filter`: **4 passed**. The exact signed-adapter and resident-extraction methods
then passed **4 tests**. Final validation incorporated main's NPC-supply update at `e043aa92`:

- Tool/solution restore and solution build passed, with zero warnings/errors; build **12.50 seconds**.
- The final affected filter below passed **166 tests**, none skipped, in **83.20 seconds** wall time.
- Both Python policy suites passed (**8 catalog/setup + 9 formatting-policy tests**, **1.75 seconds** combined).
- Formatting verification passed (**64.03 seconds**), as did `git diff --check` and added local documentation links.
  The full .NET suite was not run.

Final test filter, joined with `|`:

```text
FullyQualifiedName~Eden.Protocol.Tests.Planetary
FullyQualifiedName~Eden.Protocol.Tests.Administration.WorldSettingsPageTests
FullyQualifiedName~Eden.Protocol.Tests.Persistence.WorldConfigurationTests
FullyQualifiedName~Eden.Protocol.Tests.Persistence.SkillExtraction
FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.SkillExtractionServiceTests
FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.PilotLoginTests
FullyQualifiedName=Eden.Protocol.Tests.PilotSessions.ConcordOverviewTests.SignedLoginExpressionIncludesPortraitsAndConcordColorWithAnEmptyChallengeResult
FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.CorporationClientDiagnosticsTests
```

### Live acceptance

Not performed; agent client testing is disabled. Native rendering, input and gameplay feel remain unverified.

### Optional maintainer checks

- After saving an override and restarting/reconnecting, verify native placement and Submit at the chosen limit.

## Persistent colonies and production — November 11, 2026

Planetary industry now supplies a personal, persistent production loop across the eight ordinary planet types.
It uses all **68 pinned planetary schematics**, producing P1 through P4 from the **15 raw resources**. Actual catalog
inputs, quantities, cycles, building restrictions, volumes and dogma defaults determine production. The fixture
export contains 144 types and 359 source records and is reproducible with
`python tools/Export-PlanetaryCatalog.py --catalog <catalog.sqlite>`.

### Player behavior

- An Omega pilot carries the matching published command center in ordinary cargo or the specialized command-center
  hold and deploys it while undocked in that planet's system. A basic colony needs no scanning skill. The saved
  colony count is limited to one plus Interplanetary Consolidation. The pinned ordinary planet types and UniWiki's
  restricted system list govern colonization; shattered/scorched planets have no admitted building/resource profile.
- Native Submit supports all 13 ordinary command-stream operations: build/remove pins, create/remove/upgrade links,
  create/remove routes, assign schematics, upgrade the command center, edit extractor heads and install/stop programs.
  CPU/power include buildings, heads, spherical link distance and link upgrades. Building and command upgrades charge
  the wallet. Demolition destroys the building's contents; abandoning removes the complete surface colony.
- Extractors support up to ten heads within the catalog area of influence, overlap penalties, native one-hour to
  fourteen-day programs, cycle intervals and the client's decay/noise output formula. Quotes also accept temporary
  extractor identities before Submit; actual construction and program yield are validated against the final layout.
- Connected, non-repeating routes reserve link bandwidth and feed only valid products/ingredients. Processors consume
  complete catalog batches and retain at most one additional input batch. Producers discard unrouted/overflow output.
  Storage and launchpad capacity use actual commodity volumes. Expedited transfers validate the entire path and
  impose a source cooldown of at least five minutes, increased by volume and bottleneck bandwidth.
- Colonies advance by saved deadlines on access. Offline progress uses the same chronological events as stepped
  simulation, stops at extraction expiry and preserves pending processor cycles. Repeated reads at one timestamp
  cannot start additional production or pull another batch. Ordinary flight frames do not run colony simulation.
- Command-center launches remove real goods, charge the 15% export tariff and create owned orbital containers with
  five-day expiry. The journal lists their coordinates and supports the native `launch` warp selector. Containers
  use ordinary nearby cargo collection, including specialized hold restrictions. Launch deletion removes its cargo.
- The first colony creates a shared Interbus customs office around that planet. Each pilot has a private 35,000 m³
  compartment. Ship transfers require an uncloaked ship within 2,500 m in the same system. Launchpad import/export
  consumes actual compartment/surface inventory and charges the 17% Interbus tariff; imports cost half exports.
  Customs Code Expertise does not reduce Interbus tariffs. Client-supplied tax quotes cannot change charges.
- Survey scans require Remote Sensing. Colonized planets and the current system get local proximity; other scans
  enforce the 1/3/5/7/9-light-year ranges and known-space restrictions. Planetology and Advanced Planetology bound
  the returned harmonic bands using the pinned proximity table. Client skill/proximity claims are not authoritative.
- Skill extraction protects Interplanetary Consolidation levels needed by current colonies and Command Center
  Upgrades levels needed by existing command centers. Surface edits, wallet charges, cargo consumption, identity
  allocation and colony saves share one transaction. Recent duplicate Submit requests reuse the committed result.

### Protocol and persistence

Gameplay owns typed colony state, commands, geometry, routing, capacities, taxes and event advancement. World loads
the immutable catalog definition and persists named colony state, global pin allocation, launch records and offices.
Data owns saved entities. Protocol parses inert Blue values and emits explicit models; no client code is executed.

`planetMgr` binds by planet ID, with pilot-lifetime references retained across docking. The router validates binding
shape, gates selection and preserves nested first-call failures. Native replies include KeyVals, CRowset colony and
launch directories, integer commodity dictionary keys, list/tuple distinctions, float coordinates/radius, raw
float32 harmonic coefficients and FILETIME cycle/deadline values. Public colony overlays expose positions and links
without inventory, program or production information. GM mutation routes remain unsupported.

Orbital contacts reuse the existing stationary contact and inventory boundaries, retaining the actual orbital
category, planet identity and idle state. Customs offices remain visible throughout their system for the native
launchpad's `planetInfo.GetFunctionalCustomOfficeOrbitalsForPlanet` lookup. Warmed observer reads use the existing
resident snapshot; office/launch commits, inventory membership and exact launch expiry invalidate that snapshot.
Checkpoint capture includes all planetary tables. Schema 139 preserves the preceding coarse-population upgrade;
planetary schema creation is idempotent for existing and newly created saves.

### Reference evidence and intentional approximations

Gameplay references consulted on November 11, 2026:
[Planetary Industry](https://wiki.eveuniversity.org/Planetary_Industry),
[Planetary buildings](https://wiki.eveuniversity.org/Planetary_buildings),
[Colony management](https://wiki.eveuniversity.org/Colony_management),
[Planet Management skills](https://wiki.eveuniversity.org/Skills:Planet_Management), and
[Customs Code Expertise](https://wiki.eveuniversity.org/Skills:Trade#Customs_Code_Expertise).
The pinned catalog and client resolve build-specific wire behavior and values.

The local EveJs reference at `F:\Git Repos\evejs` identifies client build **3396210**, whereas Eden uses **3662168**.
The supplied source snapshot has no Git metadata, so its reference identity is recorded by SHA-256:

| Reference file under `server/src/services/` | SHA-256 |
| --- | --- |
| `planet/planetMgrService.js` | `726ee5e88e5b7009ace10b535bef39057ec761846984bd3c37df456c47912151` |
| `planet/planetRuntimeStore.js` | `cb74256d55a9add0fa5fedd022362118a1fe0d67b27caaff302f30a7a8ce45da` |
| `planet/planetStaticData.js` | `3cca293b7a98bfe4c4a9559ec3b109d0a661652ab441d8b005cef3751b3ebbc9` |
| `inventory/invBroker/planetaryImportExport.js` | `b267d900cdb14b90fb4e8d677820079c013dd67e782559c7b49597765c44ff97` |

The comparison informed service coverage, explicit reply fields, temporary identities, construction settlement,
orbital cargo and lazy production. Eden's implementation is independently authored against typed gameplay rules
and the pinned client/catalog, without copying EveJs implementation code.

Inert inspection of build 3662168's `code.ccp` covered `clientPlanet`, `planetSvc`, `colonyData`, `commandStream`,
`ecuPin`, `processPin`, `planetCommon`, `planetInfoSvc`, launchpad/import-export UI and the planetary journal.
The stock archive SHA-256 is `37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`.
The staged client includes Eden's skill overlay and has archive SHA-256
`051a2f027309aa9e806273200c9f5e969cad8675f49ef97313ff57a6b4549bb9`; all **27** selected planetary/client modules
were compared against the stock backup and are byte-identical. The stock archive identity therefore covers the
inspected native behavior despite the staged archive's additional overlay.
Evidence includes the temporary-pin survey call, FILETIME program parameters, `launch` warp selector, actual
orbital idle state, 0.3-radian default head range, commodity tiers and native band-resolution tables.
The customs contact also supplies the `level` attribute required by `CustomsOffice.LoadModel`; None selects the
type's ordinary catalog graphic rather than a special upgrade graphic.
Existing Blue and Destiny codecs are reused; no serialization or movement algorithm is replaced.

Resource geography and extraction base yield are explicit **Eden simulation policy**: a reproducible real spherical
harmonic field shared by scan bytes and authoritative yield, footprint scaling and symmetric head-overlap penalties.
They are not Tranquility's proprietary distributions. Dynamic geological depletion/regeneration, security-dependent
abundance, player-owned customs gantries, orbital combat/ownership/tax configuration, skyhooks and PI templates are
outside this implementation. Offices are supplied on first colonization and remain NPC infrastructure. Native
presentation, rendering and input remain unverified.

### Automated validation

Development began with the exact filter
`FullyQualifiedName=Eden.Protocol.Tests.Planetary.PlanetaryIndustryTests.ExtractsProcessesAndPersistsWithoutReplayingCycles`.
Coverage includes all 68 recipes, all eight planet profiles, expiry boundaries, stepped versus offline production,
same-timestamp idempotence, atomic rejection, Submit retry, real customs cargo/taxes, command launches, collection,
restart/checkpoint persistence, skill extraction floors, inert wire shapes, temporary ECU quotes and warmed empty
and populated observer reads with zero SQL commands, connections and fitting calculations.

Validation incorporates main's `36f4e2e611316c414efd043ba3e3c45389cbf773` coarse-population changes, the
subsequent `aeb1fc357bd5070663a1b1b82889ccce7d8b3045` mining-reward changes, and the idempotent migration
from schema 138 to 139:

| Automated check | Result | Elapsed |
| --- | --- | ---: |
| Pinned tool restore, solution restore and merged solution build | Passed; zero warnings/errors | 10.51 s |
| Affected .NET tests and immediate dependencies | 190 passed; none failed/skipped | 83.95 s |
| Final incremental solution build after the customs model-level correction | Passed; zero warnings/errors | 1.16 s |
| Final planetary/orbital tests after that correction | 50 passed, including all 37 planetary cases and 13 wreck cases | 42.29 s |
| Latest main integration restore/build | Passed; zero warnings/errors | 23.82 s |
| Latest main integration: planetary, mining rewards and world tuning | 114 passed; none failed/skipped | 54.77 s |
| Catalog policy and formatting policy Python suites | 8 + 9 passed | 5.89 s |
| `tools/Format.ps1 -Check` | Passed, including 140-column verification | 21.59 s |
| Stock/staged module comparison and `git diff --check` | Passed | — |

The 190-test filter combined these exact prefixes with `|`:

```text
FullyQualifiedName~Eden.Protocol.Tests.Planetary
FullyQualifiedName~Eden.Protocol.Tests.Loot.WreckTests
FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.PilotBindingsTests
FullyQualifiedName~Eden.Protocol.Tests.Persistence.SkillExtraction
FullyQualifiedName~Eden.Protocol.Tests.Persistence.WorldMigrationTests
FullyQualifiedName~Eden.Protocol.Tests.Hosting
FullyQualifiedName~Eden.Protocol.Tests.Capsuleers.CapsuleerCoarseTests
```

The final 50-test filter combined the first two prefixes. An exact method run first established the new
`PlanetaryProtocolTests.CustomsContactSuppliesTheRequiredNativeModelLevelAndOrbitalCategory` regression.
After the mining-reward merge, the 114-test filter combined `FullyQualifiedName~Eden.Protocol.Tests.Planetary`,
`FullyQualifiedName~Eden.Protocol.Tests.Mining.MiningSkillPointRewardTests`, and
`FullyQualifiedName~Eden.Protocol.Tests.Persistence.WorldTuningTests` with `|`.
The catalog policy's initial direct-read rejection was corrected through `FindCelestial`, `RequireSystem` and
the shared type-group index, retaining the existing policy. The final planetary tests exercised that correction.
The full .NET suite was not requested and was not run. Automated checks establish protocol values and saved
outcomes; they do not establish native rendering, input or gameplay feel.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

1. Deploy a command center, build an extractor/basic-processing layout and verify scan, Submit and production visuals.
2. Import/export through a launchpad and collect a command-center launch through its journal warp destination.
3. Reconnect after an extraction cycle and inspect retained layout, goods, timers and the Planets directory.

## Resident planetary industry

Colony edits and advancement, skill checks, taxes, customs storage and launch cargo use resident state.
Colony advancement, commodity changes, wallet fees, launch IDs and pin IDs commit in the same action.
Office and launch publication includes the affected system, so a previously empty orbital view refreshes
as soon as the action commits. The orbital view uses the next saved launch expiry and includes resident
cargo changes. Existing saves retain the same tables and identity sequences.

Reference checks covered saved colony state in the local EveJS `261108.1` `planetRuntimeStore.js`
(SHA-256 prefix `CB74256D55A9ADD0`) and its separate customs settlement operation.
[EVE University planetary industry](https://wiki.eveuniversity.org/Planetary_Industry)

### Automated validation

The focused cases cover resident and file-backed customs import/export and taxation, launch collection
and expiry, same-tick orbital publication, and colony production after reopening a checkpoint. Warmed
empty and populated orbital reads assert zero SQL commands, connection opens and fitting calculations.
[Combined validation](social.md#resident-mail-inbox-and-calendar)

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer checks

None.

## Command-center market supply — November 14, 2026

Regional hubs now include the pinned catalog's NPC command-center sell assortment alongside blueprint originals.
Previously, hub expansion included only blueprints; command centers appeared only when their original supplier's
station was prepared, leaving ordinary regional market browsing without the items needed to begin PI. The existing
resident institutional seeder adds the missing offers on market preparation, including for existing saves.
No manufacturing unlock, database reset or catalog rebuild is required.

Build 3662168 has eight published market types in group 1027: Temperate 2254, Barren 2524, Oceanic 2525,
Ice 2533, Gas 2534, Lava 2549, Storm 2550 and Plasma 2551. Their catalog NPC sellers are corporations
1000010, 1000054, 1000065 and 1000109, with positive fractional `corporationTrades` values of 0.333.
Each already has a sell quote in Eden's embedded market snapshot. The fix preserves those prices, configured
institutional stock, replenishment and original supplier stations. Buy-only, unlisted, unpublished and
non-market types remain ineligible for hub sell supply; other planetary buildings are not added.

[EVE University: setting up a planetary colony](https://wiki.eveuniversity.org/Setting_up_a_planetary_colony)
confirms that the matching command center is purchased from NPC merchants before deployment. The local EveJS
`261108.1` reference's `tools/market-seederv3/src/overlay.rs` identifies all eight command centers as NPC overlay
goods (SHA-256 prefix `BD03B18F186AECC6`). This source snapshot has no Git metadata; no code was copied.
The hub distribution is Eden policy, rather than a claim about retail station distribution.

### Automated validation

The exact initial filter was
`FullyQualifiedName=Eden.Protocol.Tests.Market.PlanetaryMarketTests.HubCommandCentersCanBePurchasedAndSurviveCheckpointReopen`.
All eight cases passed after correcting the test's stock expectation to match existing immediate institutional
replenishment (build 5.52 s, tests 7.98 s). The first run failed only that incorrect stock assertion.
The resident tests exercise regional discovery from a non-supplier station, snapshot prices, rejected underpriced
purchases with unchanged balances/history/orders, hangar delivery, wallet charges, stable order identity and
inventory/history after checkpoint reopen. Additional cases cover original suppliers and eligibility rejection.

Final validation used the following combined filter:

```text
FullyQualifiedName~Eden.Protocol.Tests.Market.PlanetaryMarketTests|FullyQualifiedName~Eden.Protocol.Tests.Market.BlueprintMarketTests|FullyQualifiedName~Eden.Protocol.Tests.Market.NpcSupplyTests|FullyQualifiedName~Eden.Protocol.Tests.Planetary
```

All 70 tests passed in 30.23 s. The earlier combined run passed 69 cases and exposed a missing NPC corporation row
in the new supplier-station fixture; after supplying that catalog row, the exact supplier test passed (build 5.08 s,
tests 6.44 s) before the final combined run. Tool/solution restore and solution build passed in 1.84 s with zero
warnings/errors; both Python policy suites passed (8 + 9 tests, 1.30 s), and `tools/Format.ps1 -Check` passed in
27.65 s. `git diff --check` passed. The full .NET suite was not requested or run.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

After deploying and restarting the updated server, browse Command Centers with regional market range and buy the
type matching the intended planet. Native market presentation and deployment remain unverified by this change.

## World configuration — November 14, 2026

The administration page now has a **Planetary industry** category with independently validated 0.1–100× extraction,
factory product, command-center CPU and command-center powergrid multipliers. All default to 1×; older configuration
revisions without the group deserialize to those defaults. Draft activation remains restart-only.
See [operator settings and route limitations](../world-configuration.md#planetary-industry).

Typed Gameplay rules scale new extraction base quantities before saving the program and scale factory products when
advancing deadlines. World passes the active saved policy through every colony advancement, edit and quote boundary.
Program base quantities already serialize as `qtyPerCycle`; program deadlines and previously installed yields remain
unchanged. Factory multipliers affect all subsequently simulated completions, including offline catch-up. Configured
CPU and power affect final-layout admission. Colony state remains under the resident owner and existing checkpoint;
no second persistence implementation or SQL fallback was introduced.

Factory routes may carry up to the greater of original and configured product output, retaining ordinary input limits
and bandwidth accounting. Existing routes are never silently increased; users resize them and upgrade links to carry
additional output. Overflow and storage limits still apply. Reducing capacity preserves existing buildings but requires
a fitting final layout on the next edit.

The existing host-signed login expression supplies the active capacity and factory policies. Its planetary adapter
wraps `planetCommon.GetCPUOutput`, `GetPowerOutput` and `ProcessPin.SetSchematic`; the latter scales each pin's own
products so native simulation, output previews and route creation use the same quantity. Catalog recipes, ingredients
and cycle times are untouched. Reinstallation retains the original functions and reconnecting to default settings
restores 1× behavior. Default logins avoid eagerly loading planetary modules.

References rechecked: [UniWiki colony setup](https://wiki.eveuniversity.org/Setting_up_a_planetary_colony) and
[planetary buildings](https://wiki.eveuniversity.org/Planetary_buildings) explain command-center supply and building,
head and link constraints. These multipliers are Eden operator policy. The local EveJs files and SHA-256 identities
listed above remain the reference snapshot; its factory-output routing and capacity behavior were consulted without
copying implementation. No Carbon-owned codec, transport or Destiny behavior changed.

Inert inspection of the installed build-3662168 archive (SHA-256
`2dbc29987d01515f4f40c319f89f0276f05ac3d175d79776a03f1f8e2bab5c6a`) confirmed the command-pin calls into
`planetCommon` and `ProcessPin.SetSchematic`'s per-pin product dictionary, consumed by `Run` and `GetProducts`.
This archive is a locally prepared client, not the stock archive recorded earlier.

**Automated validation:** Tool/solution restore and isolated solution build passed (23.85 seconds, zero warnings).
Focused .NET validation passed 84 tests in 18 seconds; three optional native-client/browser checks were skipped because
their opt-in environments were unset. The catalog and formatting Python policy suites passed 8 and 9 tests respectively
(0.993 and 0.017 seconds). Formatting verification passed. Full .NET regression was not requested.

Coverage includes all 68 schematics at 0.1×, 1×, 2.5× and 100×, independent capacity rejection, legacy imports,
draft activation, resident rollback/checkpoint/reopen, and isolated signed adapter scaling, repeated installation and
default restoration. The initial exact-method filter through `tools/Test-Feature.ps1` was
`FullyQualifiedName=Eden.Protocol.Tests.Planetary.PlanetaryConfigurationTests.CapacityMultipliersIndependentlyAdmitAndRejectColonyLayouts`.
The final focused filter was:

```text
FullyQualifiedName~Eden.Protocol.Tests.Planetary|FullyQualifiedName~Eden.Protocol.Tests.Persistence.WorldConfigurationTests|FullyQualifiedName~Eden.Protocol.Tests.Administration.WorldSettingsPageTests|FullyQualifiedName~Eden.Protocol.Tests.Administration.AdministrationTests|FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.PilotLoginTests|FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.ConcordOverviewTests|FullyQualifiedName~Eden.Protocol.Tests.PilotSessions.FreePointClientTests
```

A preceding shared-output run failed before executing tests with a test-assembly version/loading mismatch. Validation
then used `--artifacts-path artifacts/planetary-configuration` for an isolated restore/build and the successful run above.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** After restart and reconnect, compare a tuned colony's CPU/power display and factory
output preview, then resize a factory route and confirm Submit accepts a layout within the configured capacity.
