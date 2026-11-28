# Compatibility and route audits

[Documentation](../README.md) / [Protocol index](../protocol.md)

RPC inventory, placeholder routes, localization settings and native rejection dialogs.

These records preserve milestone evidence and historical observations. Dates, schema versions, test results and live acceptance apply to the recorded change; they do not establish acceptance of later changes.

## Contents

- [Third emulator bug audit](#third-emulator-bug-audit)
- [Second emulator bug audit](#second-emulator-bug-audit)
- [Emulator bug audit](#emulator-bug-audit)
- [EVE.js comparison for account and station clone reads](#evejs-comparison-for-account-and-station-clone-reads)
- [EVE.js comparison for map, character and dungeon routes](#evejs-comparison-for-map-character-and-dungeon-routes)
- [In-game text for routine rejections](#in-game-text-for-routine-rejections)
- [Client RPC backlog inventory (November 5, 2026)](#client-rpc-backlog-inventory-november-5-2026)
- [Localization QA wrap compatibility (November 5, 2026)](#localization-qa-wrap-compatibility-november-5-2026)
- [Console logging cleanup (November 5, 2026)](#console-logging-cleanup-november-5-2026)
- [Placeholder route implementation (November 4, 2026)](#placeholder-route-implementation-november-4-2026)
- [Gameplay rejection dialogs, November 2, 2026](#gameplay-rejection-dialogs-november-2-2026)

## Third emulator bug audit

The third audit started at Eden `0ae8c93f` and fixed 13 defects. The topic records describe the behavioral changes and the
related pinned-client, EVE.js and CCP evidence.

- [Militia membership statistics and navy pursuit recovery](faction-warfare.md#militia-membership-statistics-and-navy-pursuit-recovery)
- [Local Skyhook and resource system resolution](sovereignty.md#local-skyhook-and-resource-system-resolution)
- [Salvage targets and repair drone safety](drones.md#salvage-targets-and-repair-drone-safety)
- [Standup fighter tubes across operators](capital-logistics.md#standup-fighter-tubes-across-operators)
- [Docked progression items and skill queue refresh](characters.md#docked-progression-items-and-skill-queue-refresh)
- [CONCORD exchange location](industry.md#concord-exchange-location)
- [Station repair for carried hulls and fittings](fitting.md#station-repair-for-carried-hulls-and-fittings)
- [Fleet boosts during same-system translocation](navigation.md#fleet-boosts-during-same-system-translocation)

### Automated validation

The 13 exact-method selectors matched 17 cases. On baseline `0ae8c93f`, the 13 regressions failed and the four controls
passed in 15.45 seconds. All 17 cases passed with the fixes in 20.05 seconds. A pre-existing loyalty store fixture still expected
an overflow when an existing reward stack was full. The baseline reproduced that failure. The updated fixture verifies a separate
reward stack, the original stack, both balances, the required items and a single payment receipt. Together with the
`Market.LoyaltyStoreTests` class, the exact-method selection passed all 40 cases in 19.62 seconds.

The broader affected-feature selection ran 671 cases in 408.35 seconds. Its only failure was the old loyalty fixture.
The broader run passed all 644 cases outside that class, and the rerun passed all 27 cases in the updated loyalty class.

The solution built with zero warnings or errors. The catalog setup policy (8 tests) and formatting policy (9 tests) passed.
The full formatting check passed in an isolated checkout in 39.11 seconds, checking 3,342 files and the 140-column limit.

<details>
<summary>Test filters</summary>

Prefix each suffix with `Eden.Protocol.Tests.` and join the `FullyQualifiedName` selectors with `|`.
The exact-method selection uses `=`.

```text
Repair.RepairTests.StationRepairsUseTheLocationOfTheCarrierHoldingTheHull
Flight.TranslocationTests.SameSystemTranslocationPreservesCommandBurstsAfterNpcUndock
Sovereignty.SovereigntyResourceBoundaryTests.LocalSkyhooksUseTheDockedOrUndockSystemInsteadOfTheSavedNpcStation
FactionWarfare.MilitiaProgressionTests.BiomassDropsMilitiaHeadcountsAndKeepsHistoricalActivityAcrossCheckpoint
FactionWarfare.MilitiaNavyTests.PursuitWaitsAcrossReopenAndDeploysOnlyOneLiveWingPerGrid
Persistence.ImplantTests.ProgressionItemsUseTheCurrentUpwellHangarInsteadOfThePreviousNpcStation
PilotSessions.FreeSkillPointServiceTests.QueueSnapshotPublishesExternallyRemovedSkillsBeforeAcknowledgingTheRevision
Market.LoyaltyStoreTests.RejectedConcordExchangesPreserveBothIssuerBalances
FactionWarfare.MilitiaProgressionTests.DirectEnlistmentCountsThePilotWithoutEnlistingTheirCorporationInStatistics
Combat.UtilityDroneIntegrationTests.AutomaticSalvageKeepsItsCycleOnTheAssignedWreckUntilItDisappears
Combat.UtilityDroneIntegrationTests.SalvageDroneStartsWorkingAtTheResidentTractorPositionBeforeItIsCheckpointed
Combat.UtilityDroneIntegrationTests.RepairDroneCancelsCleanlyWhenTheTargetDroneOwnerBecomesCriminalBeforeFirstCycle
Structures.StandupFighterTests.ASecondStructureManagerCannotLoadAnOccupiedTubeUntilItsSquadronLands
```

The broader affected-feature selection uses `~`.

```text
Repair.RepairTests
Flight.TranslocationTests
Flight.TranslocationTargetTests
Flight.GateCloakTests
Sovereignty.SovereigntyResource
FactionWarfare.MilitiaProgressionTests
FactionWarfare.MilitiaNavyTests
FactionWarfare.MilitiaEnlistmentTests
FactionWarfare.MilitiaOrganizationTests
Combat.UtilityDrone
Combat.DroneEquipmentTests
Structures.StandupFighterTests
Flight.CapitalTravelTests.Archon
Persistence.ImplantTests
Persistence.PilotSkillBookTests
Persistence.BoosterTests
Persistence.BoosterTimingTests
PilotSessions.BoosterServiceTests
PilotSessions.FreeSkillPointServiceTests
Market.LoyaltyStoreTests
Market.LoyaltyStoreSkinLicenseTests
PilotSessions.LoyaltyWalletServiceTests
```

</details>

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

None.

## Second emulator bug audit

The second audit began at Eden `7efbc04d` and fixed 15 defects. The topic records preserve the behavioral changes,
local EVE.js references and relevant pinned-client evidence for build 3662168.

- [Abyssal cooperative admission and trace expiry](abyssal.md#cooperative-admission-and-exact-trace-expiry)
- [Wormhole travel after NPC undock](wormholes.md#wormhole-travel-after-npc-undock)
- [Lost probe reconnection](information.md#lost-probe-reconnection)
- [NPC station contract hull location](contracts.md#npc-station-contract-hull-location)
- [Customs access, tax quotes and transit routes](planetary.md#customs-access-tax-quotes-and-transit-routes)
- [Calendar invite retention and retired mail participants](social.md#calendar-invite-retention-and-retired-mail-participants)
- [Clone jump station cache updates](characters.md#clone-jump-station-cache-updates)
- [Missile contact after undocking and disconnecting](combat.md#missile-contact-after-undocking-and-disconnecting)
- [Ship insurance cancellation deadline](inventory.md#ship-insurance-cancellation-deadline)
- [Interrupted checkpoint file recovery](performance.md#interrupted-checkpoint-file-recovery)

### Automated validation

The 16 exact-method selectors matched 18 cases. On baseline `7efbc04d`, 17 failed and the explicit-system
missile control passed in 15.67 seconds. On the corrected revision, all 18 passed in 17.93 seconds.
The solution built with zero warnings or errors. The 25 affected-feature selectors passed all 407 cases in 192.25 seconds.
The Python catalog and formatting policy suites passed all 17 cases. The complete formatting and line-width checks passed in
an isolated checkout in 37.66 seconds, including restore.

<details>
<summary>Test filters</summary>

Prefix each suffix with `Eden.Protocol.Tests.` and join the `FullyQualifiedName` selectors with `|`.
The exact-method selection uses `=`.

```text
Combat.PilotMissileTests.OfflineVictimRemainsHittableAndItsPvpDeadlineExtendsAtImpact
Persistence.IncrementalCheckpointTests.StartupReclaimsAbandonedCheckpointFilesRegardlessOfCaptureMode
Persistence.InsuranceTests.PolicyExpiresAtItsDeadlineEvenIfTheClientSendsACancelRequest
PilotSessions.CalendarContactPolicyTests.ExistingBlockedInviteeDoesNotPreventRemovingAnotherGuest
PilotSessions.JumpCloneServiceTests.NativeUnboardAndCloneJumpReplaceTheCapsuleAndReleaseOldStationReferencesBeforeTheSessionDelta
PilotSessions.MailContactPolicyTests.BiomassedRecipientCannotReceivePaidRepliesToHistoricalMail
PilotSessions.MailingListServiceTests.OwnerBiomassRetiresTheListAndPreservesHistoricalMessagesAcrossReopen
Persistence.MailingListLifecycleTests.StartupRetiresLegacyMailingListsOwnedByBiomassedPilots
Exploration.WormholeTravelTests.WormholeJumpAcceptsTheSystemInferredFromAnNpcStationUndock
Exploration.AbyssalCooperativeTests.TraceOwnerCanStillEnterAfterAMateStartsTheRunAndAnotherDeploymentIsRejected
Exploration.AbyssalTests.ExpiredAbyssalRunStopsAdvertisingItsTraceAndActiveStateBeforeSettlement
Exploration.ProbeScanningTests.ReconnectReplaysAlreadyPublishedProbesWithoutConsumingMoreCharges
PilotSessions.ContractServiceTests.NpcStationContractsExcludeHullsDockedAtUpwellStructures
Planetary.PlanetaryProtocolTests.CustomsOfficeWindowAndLaunchpadTransfersWorkAwayFromShipCargoRange
Planetary.PlanetaryProtocolTests.CustomsTransferWithAnOutdatedQuoteRequiresConfirmationAtTheNewRate
Planetary.PlanetaryIndustryTests.SchematicChangesPreservePlanetaryTransitRoutes
```

The affected class selection uses `~`.

```text
Combat.PilotMissileTests
Combat.CrimewatchTests
Persistence.IncrementalCheckpointTests
Persistence.LiveWorldTests
Persistence.InsuranceTests
PilotSessions.InsuranceServiceTests
PilotSessions.CalendarContactPolicyTests
PilotSessions.CalendarServiceTests
Persistence.CalendarTests
PilotSessions.JumpCloneServiceTests
PilotSessions.MailContactPolicyTests
PilotSessions.MailingListServiceTests
Persistence.MailingListLifecycleTests
Persistence.CharacterDeletionTests
Exploration.Abyssal
Exploration.WormholeTravelTests
Exploration.WormholeLifecycleTests
Exploration.ProbeScanningTests
PilotSessions.ContractServiceTests
PilotSessions.ContractAssetServiceTests
Persistence.ContractAssetTests
Planetary.PlanetaryProtocolTests
Planetary.PlanetaryIndustryTests
Planetary.CustomsConfigurationTests
Planetary.PlanetaryConfigurationTests
```

</details>

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

None.

## Emulator bug audit

The audit fixed 16 player-facing and host defects in Eden. The topic records explain the corrected behavior and preserve the
relevant build 3662168 and EVE.js evidence.

- [Launcher credential deadlines](startup.md#launcher-credential-deadlines)
- [Maintenance and checkpoint failure isolation](performance.md#maintenance-and-checkpoint-failure-isolation)
- [Market delivery and industry job corrections](industry.md#market-delivery-and-industry-job-corrections)
- [Live organization mail and mailbox selection](social.md#live-organization-mail-and-mailbox-selection)
- [Fleet departure and bridge notifications](fleets.md#fleet-departure-and-bridge-notifications)
- [Cynosural cycles and travel restrictions](navigation.md#cynosural-cycles-and-travel-restrictions)

### Automated validation

Against the original `63ec0c4f` server, 23 of the 25 focused cases failed in 16.73 seconds. The two industry resume
controls passed. All 25 cases passed against the corrected server in 22.48 seconds using the same exact-method selection.

The solution built with zero warnings or errors. The 22-class follow-up filter passed 406 cases in 385.42 seconds,
with one optional Rhea acquisition case skipped because `EDEN_CAPITAL_MARKET_CATALOG` was unset. This selection covered
launcher authentication, checkpointing, industry and markets, corporation wallets, mail, fleets, flight and structures.
The Python catalog and formatting policy suites passed all 17 cases.
The full formatting and line-width checks passed in a clean checkout in 38.81 seconds, including restore. The working checkout
contains a local `.artifacts` archive that CSharpier also scans.

<details>
<summary>Test filters</summary>

Prefix each suffix with `Eden.Protocol.Tests.` and join the `FullyQualifiedName` selectors with `|`.
The exact-method selection uses `=`.

```text
Launcher.LauncherAccountTests.RefreshedAccessExpiresAtTheRememberedSessionDeadline
Launcher.LauncherApiTests.LaunchReplyReportsTheExactTicketDeadlineNearSessionExpiry
Economy.EconomicLifecycleTests.FailedMarketDemandDoesNotBlockBeltRenewal
Persistence.LiveWorldTests.SlowCheckpointStaysSuccessfulWhenTheLogSinkThrows
Market.StandardMarketTests.StationAsksClearAfterUndockingWhileSystemAndRegionAsksRemain
Market.CorporationMarketSaleTests.NativeCorporationSaleCreditsTheDivisionAndPreservesTheUnsoldStockAcrossCheckpoint
Market.PersonalMarketTests.FullHangarStackDoesNotPreventDeliveryOfPurchasedItems
Structures.StructureTests.ManufacturingUsesEngineeringBonusesAndPausesAtTheExactFuelDeadlineAcrossReopen
PilotSessions.MailServiceTests.MissingReplacementMaskLeavesExistingMailLabelsIntact
Alliances.AllianceSocialTests.AllianceMailSnapshotsRecipientsAndCalendarChecksLiveMembershipAcrossReopen
PilotSessions.CooperativeFleetTests.FleetDepartureStillNotifiesClientAfterPendingEventsOverflow
Flight.CapitalTravelTests.CovertMobileBeaconSupportsFleetPortalWithHostFuel
Flight.CapitalTravelTests.JumpPortalPublishesAndRejectsRepeatActivationWithAnImplicitOriginSystem
Flight.CapitalTravelTests.CynosuralFieldRemainsActiveWhenItsEmitterUndocksFromTheLocalStation
Flight.CapitalTravelTests.MicroJumpSettlementHonorsPanicAppliedDuringSpool
Flight.GateCloakTests.DeveloperTranslocationEndsGateCloakInSpaceAndWhenDocking
```

The class selection uses `~`.

```text
Launcher.LauncherAccountTests
Launcher.LauncherApiTests
Launcher.LoginTicketCheckpointTests
Launcher.LauncherCleanupTests
Launcher.PasswordConcurrencyTests
Hosting.ServerServicesTests
Persistence.LiveWorldTests
Persistence.IncrementalCheckpointTests
Economy.EconomicLifecycleTests
Market.StandardMarketTests
Market.PersonalMarketTests
Market.CorporationMarketSaleTests
Structures.StructureTests
PilotSessions.CorporationWalletTests
Industry.StarterProductionTests
Alliances.AllianceSocialTests
PilotSessions.MailServiceTests
PilotSessions.MailArrivalContractTests
PilotSessions.CooperativeFleetTests
Flight.CapitalTravelTests
Flight.GateCloakTests
Flight.TypedFlightTests
```

</details>

### Live acceptance

Not performed; agent client testing is disabled.

## EVE.js comparison for account and station clone reads

On November 21, 2026, five routes were verified against Eden `83f8629a`, local EVE.js **261108.1** (client build **3396210**),
and the original build **3662168** client archive with SHA-256
`37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`. The client call sites were disassembled as data.

| Route | Pinned client call and Eden reply |
| --- | --- |
| `charUnboundMgr.GetNumCharacters()` | `CharacterCreationContainer.CheckSkipTutorial` compares the result to one. The resident roster returns only the authenticated account's character count. Prepared characters count until biomass completes. |
| `userSvc.GetCreateDate()` | `newFeatureNotify.UserIsTooNew` passes the result to `gametime.GetDaysSinceWallclockTime`. Eden returns the authenticated account's saved creation FILETIME. EVE.js derives this value from the current character when available. |
| `loginRewardFacilities.get_my_clone_grade()` | `SeasonalLoginCampaignService._get_my_clone_grade_from_sever` reads the same saved Alpha/Omega scalar as Eden's subscription service. |
| `cache.GetServerVersionAndBuild()` | `BugReportingService.CollectData` unpacks version and build. Eden returns the tuple `(24.01, 3662168)` from the boot metadata shared with the compatibility handshake and login challenge. |
| `jumpCloneSvc.GetStationCloneState()` | `CloneJump.GetStationClones` calls a location-bound moniker. The reply uses the existing jump-clone rowset (`jumpCloneID`, `locationID`, `cloneName`) filtered to the selected pilot's inactive clones at their current NPC station. Renaming a station clone invalidates the native station cache. In space or docked at an Upwell structure the rowset is empty. |

Reference files under `server/src/services` are `character/charService.js` (`5128a5ebd2f2`),
`user/userService.js` (`ef2e6cded872`), `campaign/loginRewardFacilitiesService.js` (`ecf0c54ea2fd`),
`cache/cacheService.js` (`3345cd30287f`), `station/jumpCloneService.js` (`340aba96bd46`), and
`station/jumpCloneRuntime.js` (`9a1082e0c456`). These are SHA-256 prefixes because the reference lacks Git metadata.
[EVE University jump clones](https://wiki.eveuniversity.org/Jump_clones) ·
[CCP daily login campaigns](https://support.eveonline.com/hc/en-us/articles/360007432240-Daily-Login-Campaigns)

The personal map visit filter requires checkpointed per-character travel history. The PLEX vault balance requires
authoritative account PLEX storage. These remain follow-up candidates.

**Automated validation.** The four exact method filters passed all seven cases in 9.37 seconds. The solution build passed
with zero warnings or errors. The adjacent filter covered `PilotServicesTests`, `SubscriptionServiceTests`,
`JumpCloneServiceTests`, `JumpCloneTests`, `CharacterCreationTests`, `CharacterDeletionTests`, `CloneGradeTests`,
`CompatibilityHandshakeTests` and `InstalledRorqualCloneSurvivesRestartUntilUsedOrHostDestroyed`. It passed 113 of 119
cases in 39.00 seconds. All six failures reproduced on baseline `83f8629a` in 14.70 seconds. Four were rejection-message
assertions in `GameplayRejectionShowsTextAndKeepsTheModuleAndSessionIntact`, and two were missing resident world fixtures
in `NativePilotServicesPacketsReturnSubscriptionTimeWithoutDestinationErrors`. With those two methods excluded, all 113
cases passed in 36.43 seconds. Both Python policy suites passed (8 catalog and 9 formatting cases), along with the
formatting and protocol link checks. The full .NET suite was outside this focused run.
After rebasing onto `df6aa8d1`, the solution build passed in 18.35 seconds with zero warnings or errors and all seven exact
cases passed again in 10.14 seconds.

**Live acceptance.** Not performed. Agent client testing is disabled.

**Optional maintainer checks.** Open the clone bay at an NPC station and verify the visible clone name after renaming it.

## EVE.js comparison for map, character and dungeon routes

The November 21, 2026 pass compared Eden `157c5190` with local EVE.js **261108.1** (client build **3396210**).
Eden's client audit used build **3662168** and archive SHA-256
`37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`.
The scan decoded 12,789 modules and 171,678 code objects with zero failures after correcting xdis 6.3.0's read of the
signed Python 2 long digit count in the audit process. Client bytecode was inspected as data.

| Route | Pinned client and EVE.js behavior | Eden implementation |
| --- | --- | --- |
| `map.GetStationCount()` | `MapFilterStationCount` constructs a Python dictionary from the returned sequence. EVE.js returns pairs for all catalog systems. | Returns a list of `(solarSystemID, count)` tuples from the pinned catalog, including zero-count systems. Player structures are outside the NPC station count. |
| `charMgr.GetCharacterCreationDate()` | `CCSvc._GetCharacterCreationDate` calls with zero arguments. EVE.js returns a FILETIME. | Returns the selected character's saved creation FILETIME. |
| `charMgr.GetOrganizationInfoForCharacters(characterIDs)` | `LocalChatChannelController._update_member_list` requests a list of missing pilot IDs and reads `corporationID`, `allianceID` and `warFactionID` from each returned entry. EVE.js uses a dictionary keyed by character ID. | Batches one committed resident owner-directory snapshot with catalog NPC identities. Duplicate IDs coalesce and unknown identities carry zeroes. |
| `keeper.GetCurrentDungeonForCharacter()` | `DungeonTracking.OnCharacterSessionChanged` unpacks `(dungeonID, roomID, instanceID, values)` before ballpark binding. EVE.js answers with current room state or Python None. | Reads the active participant's Abyssal room or the saved Ghost Site at the ship's position. Ghost Site values include the active visible countdown, and the arrival notification can refresh it if the timer changes during reconnect. Other locations return Python None. |

The EVE.js source was available without Git metadata. Compared `map/mapService.js` (SHA-256 prefix `6c06a8e47c57`),
`character/charMgrService.js` (`82731fcebb67`), `dungeon/keeperService.js` (`46fe5bc9604e`) and
`dungeon/dungeonTrackingRuntime.js` (`c632edb39169`) under `server/src/services`. Eden's implementations use its own
catalog, resident snapshots and existing Blue adapters.
[Carbon Blue at fcbf0c2e](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp) ·
[EVE University Star Map](https://wiki.eveuniversity.org/Star_Map) ·
[Abyssal Deadspace](https://wiki.eveuniversity.org/Abyssal_Deadspace) · [Ghost Sites](https://wiki.eveuniversity.org/Ghost_Sites)

The literal service-name and handler comparison found 411 pinned client calls with an EVE.js method and no Eden source
candidate after this pass. It also found 34 with only Eden compatibility candidates. These counts include possible aliases,
disabled features and developer calls. EVE.js matches also include placeholder bodies. For example EVE.js's agent map
filter returns an empty rowset, while the system-visit filter reads persisted per-character travel history. Ship killmarks
require per-hull awards and updates to visible ship state. These are separate gaps from the four verified implementations.
The [previous inventory](../client-routes/README.md) preserves its November 5 source snapshot.

**Automated validation.** The four exact regression methods passed in 11.01 seconds through `Test-Feature.ps1`.
Tool and solution restores passed, and the solution built in 2.79 seconds with zero warnings or errors.
The following focused filter passed 118 tests (44.46 seconds including the test runner).

```text
FullyQualifiedName~StationDirectoryTests|FullyQualifiedName~CharacterSheetServiceTests|FullyQualifiedName~OwnerDirectoryTests|FullyQualifiedName~AbyssalTests|FullyQualifiedName~DedAndGhostSiteTests|FullyQualifiedName~HackingWorldTests|FullyQualifiedName~MapStartupTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting
```

The cases cover native serialization, argument rejection, selection gating, zero-count systems, committed affiliation
changes, Abyssal room changes, and reading a Ghost Site's timer without advancing its simulation. The full .NET suite
was out of scope for this focused run. The catalog and formatting policy suites passed 8 and 9 tests respectively.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Open the star map's station-count filter and compare a system with NPC stations to one without.

### Follow-up candidates

The [Upwell repair shop](structures.md#native-upwell-repair-shop) was implemented from the next pass. The comparison also
confirmed these gaps against the pinned client and the same EVE.js version.

| Route | Client use and implementation dependency |
| --- | --- |
| `map.GetSolarSystemVisits` | The Personal map filter reads `lastDateTime`, `solarSystemID` and `visits` from each row. EVE.js increments on a return from another system, filters private Abyssal locations, and preserves history across reconnects. See [Eden's checkpointed visit history](navigation.md#personal-map-visit-history). |
| `allianceRegistry.GetDaysInAlliance`, `GetAllianceMembersOlderThan` | The corporation wallet calls these to check EverMark donation eligibility. EVE.js computes whole elapsed days from current membership. Eden already saves membership start times. The donation flow still needs alliance-aware EverMark settlement. |

## In-game text for routine rejections

Routine service rejections after pilot selection use the native `CustomNotify` message with a Unicode `notify` value.
This covers commonplace refusals in flight, module operation, targeting, inventory and station services, including failures
returned by the first call inside `MachoBindObject`. Both direct and bound callers still receive the Macho code-2
`UserError`, so a rejection follows the client's normal error-handling path. The reply precedes queued updates.

Build **3662168** gives `CustomNotify` the `notify` dialog type and body ID **259599** (`{notify}`). It gives `CustomInfo`
the `info` dialog type and body ID **259687** (`{info}`). The pinned client's
`player_messaging.client.dialog_handler._handle_player_fsd_dialog` routes `notify` through its text-message handler.
The original client archive has SHA-256 **37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**.
See also [EVE University's Log and Messages reference](https://wiki.eveuniversity.org/Neocom#Log_and_Messages).

EveJs **261108.1** (client build **3396210**) was checked for module operation, station access and warp refusals. Its
`dogmaService.js`, `corpStationMgrService.js` and `warpFailureUserError.js` use `CustomNotify` for those routine cases.
The explicit Information paths cover character selection, market and contract errors that need a dialog, crimewatch warp
restrictions, the pending agent-dialog limit and oversized replies. Named client errors such as the cargo quantity retry
and targeting cleanup keep their message keys and parameters. Agent questions keep their client callbacks and redemption
keeps its per-token reports. The wire models use existing Carbon serialization support.
[Carbon Blue reference at fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp)

**Automated validation** covers direct and nested module refusals, unavailable warp and station-market checks, Unicode
message text, notification order, the retained Information and named-error replies, and successful calls after a rejection.
All **334 focused .NET tests** passed in **3m 34s**. Tool restore, solution restore, the build, both Python policy suites
and formatting verification passed. The first exact-method run stopped during Windows test-seed publication with an
access-denied error. Using `EDEN_TEST_TEMP_DIRECTORY` on the repository drive resolved it, and all four cases passed.

**Live acceptance** was not performed. Agent client testing is disabled.

**Optional maintainer checks.** Try to take a module offline in space and confirm that the refusal appears as on-screen text.

## Client RPC backlog inventory (November 5, 2026)

The [client route inventory](../client-routes/README.md) compares pinned build **3662168** with Eden production source **01dea7b6a045afed1727a6eec842929ffbee044a**. Inert inspection decoded **12,789 modules** and visited **171,678 code objects**, finding **1,204 unique service/method pairs**: **835 no-handler-found**, **45 compatibility-review**, and **324 handler-candidate**. **39 dynamic selectors** remain unresolved; decoding and analysis reported **zero failures**. The CSV is sortable, the JSON retains all detected client module/function/bytecode-offset evidence and server source candidates, and the separate unresolved CSV identifies dynamic selectors.

These are source candidates, not an exhaustive implementation checklist. The scanner follows literal remote/proxy selectors, moniker factories, aliases, self fields, local-service GUIDs, bounded remote-return summaries, CSPA string dispatch and remote method references passed to callbacks. Explicit bridges cover Michelle's remote ballpark and invCache's dynamic container wrappers; local inventory cache methods are excluded from automatic forwarding. Server matching conservatively combines dispatch-file literals and prefers exact tuple matches in its evidence. A handler candidate does not establish argument, role, location, persistence or gameplay support. Compatibility replies need semantic review: some intentionally represent valid empty state. Developer-only, deprecated, feature-flagged and unreachable client references remain in the backlog; dynamic dispatch, unknown wrappers, native binaries and HTTP/gateway APIs can escape it.

Useful player-feature entries include drone commands (`entity.CmdEngage`, `CmdReturnBay`), overheating (`dogmaIM.Overload`, `StopOverload`), fleet creation (`fleetObjectHandler.CreateFleet`), scan-result bookmarking (`beyonce.BookmarkScanResult`), planetary interaction, alliance operations and corporation voting. These are examples from the candidate backlog, not newly implemented features or priority commitments.

The tool refuses a client archive whose SHA-256 differs from **37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**. It uses **xdis 6.3.0** to decode Python 2 bytecode as data; no client module is imported or executed. No game, client UI or normal runtime save is used. The existing [Carbon Blue reference at fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp) was consulted; this audit changes no Blue codec or Destiny behavior.

To refresh the snapshot from the repository root, supply an existing xdis installation with `--python-libs`, or install an isolated copy first:

```powershell
python -m pip install --target artifacts/client-route-libs xdis==6.3.0
python tools/Audit-ClientRoutes.py --archive 'E:\EdenDev\3662168\tq\code.ccp' --python-libs artifacts/client-route-libs --output docs/client-routes
```

**Automated validation.** The complete static scan passed in **49.92 seconds** with zero decode/analysis failures. Eleven focused Python audit regressions passed (**0.005 seconds**), including direct and callback RPCs, dynamic-name retention, local-service exclusion, bound references, cross-module wrapper summaries, inventory cache forwarding, private/data attributes, CSPA string dispatch and server guard matching. Tool/solution restores and an isolated solution build passed (**5.76 seconds**, zero warnings/errors); focused .NET filter `FullyQualifiedName~Eden.Protocol.Tests.Hosting` passed **9 tests** (**3 seconds**). Catalog policy passed **3 tests** (**0.233 seconds**) and formatting policy passed **9 tests** (**0.017 seconds**); formatting and snapshot integrity checks passed. The initial regular build was stopped after the running server locked its outputs; isolated outputs under `artifacts/client-route-validation` then passed without stopping that server. The earlier build preceded the concurrent corporation-share milestone; the final build and hosting results cover production revision `01dea7b`. No full .NET suite was requested or run. Static reachability and full feature semantics remain unverified.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** None; this milestone delivers an offline audit and backlog.

## Localization QA wrap compatibility (November 5, 2026)

The reported **Invalid Packet Destination** dialog identified the missing `localizationServer.UpdateLocalizationQAWrap` route. The account-phase localization handler now accepts exactly one native boolean before or after pilot selection, records it on the connection context and returns Python **None**. Repeated updates replace the flag; other connections and saved pilot settings are unaffected. Character logoff retains the connection flag, and a new connection defaults to false. Invalid argument types/counts and unexpected service keywords fail without changing the flag; the router still removes transport-owned `machoVersion`. Unknown localization methods remain unsupported.

In build **3662168**, `localization.settings.qaSettings.UpdateAndSaveSettings` saves preferences locally, invalidates its cached QA-wrap decision and sends `LocWrapSettingsActive()` to this method. `carbon.client.script.localization.localizationClient.OnCharacterSessionChanged` also sends it on initial selection for `ROLE_QA`. Both callers discard the result. The server branch of `LocWrapSettingsActive` reads the connection session variable `localizationQAWrap`, default false. Eden retains the equivalent flag; it does not yet generate localized server text or QA markup from that flag. The client continues to own its localization display preferences. Live text-cache updates (`GetAllTextChanges`) remain outside this correction.

Sources: inert bytecode inspection of the pinned staged build **3662168** `code.ccp`, SHA-256 **37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368**. The same call sites and argument behavior were confirmed in `E:\Eve\tq\code.ccp`, SHA-256 **9EC838341FCDAAC99080BC849FAB47DBFABBA9EBF97A1AA0F2CBAE474265C6D3**. Consulted the public Carbon repository inventory and [Carbon Blue `Marshal.cpp` revision fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp), including the existing boolean and `TY_NONE` adapters. No codec, Destiny, game launch, client-script execution or normal runtime database modification occurred. The screenshot establishes the original failure only.

**Automated validation:** tool/solution restore, build (zero warnings/errors), **40 focused .NET tests** (14 seconds), both Python policy suites (**3 catalog / 9 formatting tests**) and formatting verification (**729 files**, 140-column check) passed in **33.34 seconds** total. The recorded .NET filter was `FullyQualifiedName~LocalizationServiceTests|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~PilotBindingsTests|FullyQualifiedName~CharacterLogoffTests|FullyQualifiedName~TypedCallResultTests`. Focused regressions cover boolean updates before/after selection, repeated changes, connection isolation/reset, rejected requests preserving state, no notifications and successful native session replies without invalid-destination errors. No full .NET suite was requested or run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** after deploying the rebuilt server, repeat the action that produced the dialog and confirm it no longer appears.

## Console logging cleanup (November 5, 2026)

Routine handshake, RPC, notification, gateway, movement and successful checkpoint reports now use a quiet-by-default debug sink. Startup/shutdown and authenticated account/character lifecycle summaries remain visible. Invalid service calls, slow calls/updates, TLS negotiation problems, NPC seed warnings and failed checkpoints remain visible as warnings/errors. Ordinary socket closures are debug diagnostics; unexpected connection failures retain error reports. Console entries share a timestamp and severity label, with warnings/errors on standard error. Chat diagnostics continue to exclude stanzas and authentication tokens.

Set `EDEN_VERBOSE_LOGGING=1` before host startup for routine diagnostics. The existing opt-in performance and motion trace switches retain independent output. This change only routes diagnostics; it does not change Blue, transport, Destiny, gameplay or checkpoint/retry behavior, so no new Carbon behavior/revision comparison is required. Automated validation covers quiet/verbose output, explicit tracing, session warning/lifecycle routing and existing hosting/session/checkpoint behavior. Live acceptance: **Not performed; agent client testing is disabled.**

**Automated validation:** tool/solution restore and build passed (zero warnings/errors; final build 2.83 seconds). Filter `FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~PilotServicesTests|FullyQualifiedName~PilotLoginTests|FullyQualifiedName~SessionPerformanceTests|FullyQualifiedName~LiveWorldTests|FullyQualifiedName~Eden.Protocol.Tests.Chat|FullyQualifiedName~GatewayRequestsTests`, with `xUnit.MaxParallelThreads=4`, passed **89 cases in 11 seconds**. The new quiet/verbose, explicit trace and session-routing regressions also passed an initial four-case development run. Both Python policy suites passed (3 catalog and 9 formatting cases). Formatting verification initially caught the checkpoint trace sink's layout; it was formatted before the final verification, which passed (710 files and the 140-column check). No full .NET suite was requested or run. Optional maintainer checks: none.

## Placeholder route implementation (November 4, 2026)

The route audit starts with `DockedCompatibilityHandler` and `AccountCompatibilityHandler`. Empty replies are valid for features with no earned or configured state, but these handlers also conceal missing persistence and feature operations. Work proceeds by client feature, with a single handler owning each implemented service/method pair.

| Feature | Audited routes | State |
| --- | --- | --- |
| Character settings | `charMgr.GetCharacterSettings`, `SaveCharacterSetting`, `DeleteCharacterSetting` | Implemented with private persistent values |
| Station directory | `map.GetStationInfo`, `stationSvc.GetStation` | Implemented for every catalog station, including service masks |
| Personal contacts | `charMgr.GetContactList`, add/edit/delete, block/unblock, labels; `onlineStatus.GetInitialState`, `GetOnlineStatus` | Implemented for local pilots and saved NPC agents, including optional notification messages; organization contacts pending |
| Personal locations | `accessGroupBookmarkMgr.GetMyActiveBookmarks` and private folder/bookmark operations; `beyonce.BookmarkLocation` and bookmark warp | Implemented for personal folders; shared ACLs and scan-result bookmarks pending |
| Ship insurance | `insuranceSvc` quotes, contracts, purchase/replacement and cancellation | Implemented for personal station-owned hulls and NPC-loss payouts; corporation policies pending |
| Task tracking | `monolithTaskTrackerMgr.HasEverWarped`, `GetCompletedAchievementsAndClientEventCount`, `UpdateClientAchievmentsAndCounters` | Implemented with persistent client snapshots and supported server activities; pinned legacy UI is disabled |
| Home station | `home_station` read, candidates, selection, remote-change time and docked clone destruction | Implemented for current NPC station and school HQ; player-corporation remote offices pending |
| Agent directory | `agentMgr.GetAgents`, NPC owner/profile lookups and scalar `charMgr.AddContact` for agents | Implemented from pinned catalog with persistent private agent bookmarks; mission gameplay pending |
| Jump clones | `jumpCloneSvc` state, quote, preflight, install, rename, destroy and jump; `ship.LeaveShip` | Implemented for personal bodies and learning implants in NPC stations; structure/capital bays and Pochven installation pending |
| Personal calendar | `calendarProxy.GetEventList`, details; `calendarMgr` create, edit, invitations, responses and cancellation | Implemented for personal events and local Eden pilot invitees; corporation/alliance and official events pending |
| Notification inbox | `notificationMgr` history, unread/group reads, processed state and deletion | Implemented with durable contact-add/edit messages and native delivery; other gameplay producers pending |
| Personal EVE Mail | `mailMgr` send, synchronization, headers/bodies, read/unread, trash/restore/delete and label operations | Persistent local-pilot mail, private mailbox state, reply/forward flags, block/rate checks and native events implemented; mailing lists implemented below; organization mail and paid CSPA pending |
| Mailing lists | `mailingListsMgr` membership, role/access management and welcome mail; `mailMgr` list delivery and selectors | Persistent zero-fee local lists, private broadcasts, owner/operator/muted roles and welcome messages implemented; alliance access and paid CSPA pending |
| Contract preparation | `contractProxy.GetItemsInDockableLocation`, `GetItemsInContainer`, `GetNumItemsInContainers`, `SplitStack` | Implemented for personal station assets; escrowed assets hidden; corporation assets pending |
| Personal contracts | `contractProxy` create, details, login, escrow, lists, search, acceptance, rejection, cancellation, collection, courier delivery/failure, package inventory, auction bidding/buyouts, claims and bulk fitted-ship creation | Personal item exchange, couriers, auctions and bulk fitted-ship creation implemented at NPC stations; corporation/structure contracts and package subcontracting pending |
| Ship appearance | `ship.GetDirtTimestamp`, `ResetDirtTimestamp` and player-ship `dirtTime` metadata | Implemented with per-hull persistence, ownership/location checks and owner/observer updates; hull rendering unverified |
| Personal security status | `crimewatch.GetMySecurityStatus`, `GetCharacterSecurityStatus`, `GetSecurityStatusTransactions` | Implemented for credited pirate NPC kills, persistent five-minute windows, private history and owner updates; PvP penalties and tags pending |
| Account redemption | `userSvc.GetRedeemTokens`, `ClaimRedeemTokens`, `TrashRedeemTokens`, `ReverseRedeem` | Persistent ordinary inventory, fixed SP and ISK/EverMarks rewards, administrator issuance, expiry, home/fixed station delivery and account updates implemented; other special activation and blueprint rewards pending |
| Unallocated skill points | Bound `@skills` reads, queue/free-point snapshot, direct/selected-level/queue previews and applications | Persistent pool, all 59 fixed redeem rewards, transactional spending, training/Alpha rules and native notifications implemented; injector activation/conversion and extraction implemented below |
| Loyalty wallets and stores | `LPSvc.GetAllMyCharacterWalletLPBalances`, `LPStoreMgr` | Saved personal/corporate issuer balances, donations, supported physical LP-store purchases and personal CONCORD conversion; blueprint copies, cosmetic/Analysis Kredit offers and mission LP payouts remain pending. See [store scope](industry.md#loyalty-point-stores-november-8-2026). |
| Learning implants | Bound `@skills.GetImplants`, `dogmaIM.InjectImplant`, `DestroyImplant` and effective character attributes | All 25 published pure learning implants, physical clone ownership, destructive unplugging, training bonuses and native cache updates implemented; hardwiring/set implants and combat boosters pending |
| Cerebral accelerators | Bound `@skills.GetBoosters`, `dogmaIM.UseBooster(itemID, locationID)` and effective character attributes | All 186 published pure learning profiles, timed/age expiry, Biology, clone persistence and expiry-aware skill queues implemented; combat/side-effect boosters pending |
| Skill injectors | Bound `@skills` allocated-SP read, injector preview/preflight/activation and split/combine; Alpha cooldown and non-diminishing allowance reads | All five published injector profiles, persistent daily timers, capacity-preserving conversion and historical Absolute augmentor implemented; extraction implemented below |
| Skill extraction | Bound `@skills.ExtractSkills` with points-to-remove selections | Atomic removal/output, allocated minimum, capsule/Alpha/queue/prerequisite/clone/implant restrictions, receipts, history and session notifications implemented |
| Larger feature systems | Agent missions/journal, standings, drones/fighters and player corporations | Compatibility states; gameplay pending |

## Gameplay rejection dialogs, November 2, 2026

The missing-skill fitting rejection above exposed a response classification issue: a handled gameplay validation failure used Macho error code **0 / UnMachoDestination**, giving it the native title **Invalid Packet Destination**. Checked the same pinned client build/hash. `fittingSvc.CheckBusyFittingAndRaiseIfNeeded` raises `UserError('CustomInfo', {'info': text})` for its readable fitting rejection; `searchUtil._ValidateSearchInput` uses the same message shape. The session now sends handled `PilotCallRouter.Error` reasons through this native **Information** dialog using the already supported code-2 nested `exceptions.UserError` encoding. Existing named errors, including cargo-capacity quantity retry and targeting failures, keep their names and values. Unsupported calls and invalid bindings retain their destination-error classification. There is no new codec, Carbon revision, world migration or gameplay permission change.

The first native online-state check also exposed an unhandled call shape: Godma's `EffectWrapper.Deactivate` sends `dogmaIM.Deactivate(moduleID, 'online')`, sometimes as the first call in a new binding, rather than `TakeModuleOffline(shipID, moduleID)`. `StateManager.Activate` sends the corresponding four-argument activation with target/repeat. The single module-activation dispatcher now routes effect **16 / online** to the existing fitting operation, validating the self target and repeat shape. The same ownership, active hull, docking, skill and resource checks apply; the existing explicit online/offline methods remain supported. It emits the same online-effect and ship-resource notifications as those methods.

**Automated checks.** Restore, build with zero warnings/errors, all **270 tests**, and formatting verification passed. Wire regression cases reject taking a module offline in space through both a direct call and a nested first bound call, using both native effect and explicit method shapes. They verify the exact CustomInfo reason, retain the fitted online module, and read the wallet successfully on the same connection. Docked fitting coverage cycles native and explicit online/offline calls and verifies online-state primes and notifications. The unavailable station-market bound call now checks its exact CustomInfo payload. Existing capacity-error coverage checks the original NotEnoughCargoSpace payload, and unsupported methods still check code 0.

**Live Windows client.** Used the Computer Use skill on the same normal-damage `shield-boost-20261102` save. Selected **Eden Pilot**, verified the recovered Ibis and its **100 Tritanium / Civilian Miner / 6 of 125 m³** cargo outside Perimeter II - Moon 1, and right-clicked the online booster's HUD button. Chose **Put Offline**, accepted the local module prompt, and observed **Information — Change module online state on your docked active ship.** Dismissed it and successfully docked on the same connection. The captured client log records the expected CustomInfo UserError from `_ChangeOnline`; optional gateway and unsupported ping-service errors still occur. This native run accepted the validation dialog and continued gameplay; successful docked native-effect toggles were automated coverage.

Independent comparison with the prior shield-cycle audit retained every item/slot/online row, skill, queue, wallet, loss, wreck, health, capacitor and cooldown row after rejection. Booster **2000000000027** remained singleton, online and fitted at flag **19**. The normal-damage host/client are left running docked at the same station with the recovered Ibis active.
