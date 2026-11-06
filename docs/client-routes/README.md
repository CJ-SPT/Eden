# Client RPC route inventory

Client build: **3662168**. Eden source: `01dea7b6a045afed1727a6eec842929ffbee044a`.

Archive SHA-256: `37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`.

Scanned **12,789 modules** / **171,678 code objects** without executing client code.

| Classification | Unique service/method pairs |
| --- | ---: |
| compatibility-review | 45 |
| handler-candidate | 324 |
| no-handler-found | 835 |

Unresolved dynamic selectors: **39**. Decode/analysis failures: **0**.

## Interpretation

- **no-handler-found**: a statically traced client RPC has no matching Eden source candidate. Prioritize for review.
- **compatibility-review**: only compatibility dispatch candidates were found. Some replies are valid; others conceal missing features.
- **handler-candidate**: a dispatch source candidate exists. This is not proof of supported arguments, persistence or gameplay.
- `@skills` and `@inventory` identify returned bound objects; inventory candidates include Eden's container aliases.
- `routes.json` retains every detected call site; `routes.csv` is the sortable inventory; `unresolved.csv` preserves dynamic selectors.

This is a static lower bound, not an exhaustive list of everything the client or server can do. Dynamic service/method names,
cross-module wrappers, HTTP/gateway APIs, native binary calls and data-driven dispatch can escape this scan. Branches are inspected
linearly; dead, developer-only, deprecated and feature-flagged calls are included and need triage. The scanner follows literal
remote selectors, moniker factories, simple local aliases, self attributes and four rounds of remote-return wrapper summaries.
Local service GUIDs connect return summaries across modules. Explicit pinned-client bridges cover Michelle's remote park and
invCache's dynamic manager/container wrappers. Inventory methods can be local cache operations and require per-call review.
Remote method references are included, covering callbacks passed to thread/session helpers; reachability is not established.
The pinned CSPAChargedAction string dispatcher is traced explicitly, including mail sending.
Server candidates conservatively match dispatch-file literals; exact tuple matches are preferred in the evidence column.
A source candidate may represent a different guard branch. Argument/location/role restrictions and partial implementations need review.

## No handler found, grouped by service

| Service | Count | Methods |
| --- | ---: | --- |
| `tourneyMgr` | 38 | `AbortCountdown`, `AddPlayer`, `BanShip`, `BanShipType`, `CleanupGrid`, `CompletePregameChecks`, `CreateMatch`, `ExecuteMemberChange`, `FinalizeBans`, `GetActiveMatchForCamera`, `GetFancyDetails`, `GetMatchSystem`, `GetPotentialMatches`, `GetTeamMembers`, `GetTourneySetup`, `IsOtherMatchInProgress`, `LockMemberList`, `MatchCompleted`, `OverrideDroneRestrict`, `OverrideError`, `OverrideFleetBossID`, `OverrideLockRestrict`, `OverrideMoveRestrict`, `OverrideWarpRestrict`, `QueryShipList`, `RemovePlayer`, `RemoveSelfFromReferees`, `ResetMatch`, `ReturnAllPlayers`, `ReturnTeamPlayers`, `StartAutobanning`, `StartCountdown`, `TeleportPlayers`, `TeleportRefHome`, `TeleportToArena`, `TeleportToStation`, `UnlockMemberList`, `UpdateFleetDetails` |
| `corpRegistry` | 35 | `AddBulletin`, `AddCorporateContact`, `ApplyToJoinAlliance`, `AssignLabels`, `CharGetAllyBaseCost`, `CreateLabel`, `DeclareWarAgainst`, `DeleteBulletin`, `DeleteLabel`, `EditContactsRelationshipID`, `EditCorporateContact`, `EditLabel`, `GetBulletins`, `GetLabels`, `GetMemberIDsByQuery`, `GetMemberIDsWithMoreThanAvgShares`, `GetMemberTrackingInfo`, `GetMemberTrackingInfoSimple`, `GetPendingAutoKicks`, `GetRecentKills`, `GetRecentLosses`, `GetStructureReinforceDefault`, `InsertInvitation`, `KickOutMembers`, `PayoutDividend`, `RegisterNewAcceptStructureSettings`, `RegisterNewAggressionSettings`, `RegisterNewCorpMailRestrictionSettings`, `RemoveCorporateContacts`, `RemoveLabels`, `ResignFromCEO`, `SetCorpWelcomeMail`, `SetStructureReinforceDefault`, `UnBind`, `UpdateBulletinOrder` |
| `allianceRegistry` | 34 | `AddAllianceContact`, `AddBulletin`, `AssignLabels`, `CancelCapitalSystemTransition`, `CreateLabel`, `DeclareWarAgainst`, `DeleteBulletin`, `DeleteLabel`, `EditAllianceContact`, `EditContactsRelationshipID`, `EditLabel`, `GMForceAcceptApplication`, `GetAlliance`, `GetAllianceContacts`, `GetAllianceMembers`, `GetAllianceMembersOlderThan`, `GetAlliancePublicInfo`, `GetAllianceSovereigntyStructuresInfo`, `GetBillBalance`, `GetBills`, `GetBillsReceivable`, `GetBulletins`, `GetCapitalSystemInfo`, `GetDaysInAlliance`, `GetEmploymentRecord`, `GetLabels`, `GetPrimeTimeInfo`, `GetRankedAlliances`, `PayBill`, `RemoveAllianceContacts`, `RemoveLabels`, `SetCapitalSystem`, `SetPrimeHour`, `UpdateBulletinOrder` |
| `petitioner` | 31 | `AddPetitionRating`, `CancelPetition`, `ClaimPetition`, `ClosePetition`, `CreatePetition`, `DeletePetition`, `EscalatePetition`, `GetCategories`, `GetCategoryHierarchicalInfo`, `GetCategoryProperties`, `GetClaimedPetitions`, `GetClientPickerInfo`, `GetLog`, `GetMyPetitionsEx`, `GetPetitionMessages`, `GetPetitionQueue`, `GetQueues`, `GetUnreadMessages`, `GetUserCatalogCountry`, `GetZendeskJwtLink`, `HasOpenTickets`, `IsSerenity`, `IsZendeskEnabled`, `IsZendeskSwapEnabled`, `MarkAsRead`, `MayPetition`, `PetitioneeChat`, `PetitionerChat`, `PropertyPopulationInfo`, `UnClaimPetition`, `UpdatePetitionRating` |
| `beyonce` | 28 | `AdminGetBubbleID`, `BookmarkScanResult`, `CallComponentFromClient`, `CmdAbandonLoot`, `CmdAbortSelfDestructStructure`, `CmdAlignTo`, `CmdBridgeToFleetDeployableBeacon`, `CmdBridgeToFleetModuleBeacon`, `CmdFleetRegroup`, `CmdFleetTagTarget`, `CmdGotoBookmark`, `CmdGroupJumpToFleetDeployableBeacon`, `CmdGroupJumpToFleetModuleBeacon`, `CmdJumpThroughFleet`, `CmdJumpToFleetDeployableBeacon`, `CmdJumpToFleetModuleBeacon`, `CmdSelfDestructStructure`, `CmdSteerDirection`, `CmdTransferCorpAssetOwnership`, `CmdTurboDock`, `CmdWarpToStuffAutopilot`, `GetCapacityOfSiphon`, `GetContainerExpiryDate`, `GetCynoJammerState`, `GetFleetLocationAndInfo`, `GetFleetState`, `GetOperationSpawnpointPosition`, `GetRelativity` |
| `posMgr` | 21 | `AnchorOrbital`, `AnchorStructure`, `AssumeStructureControl`, `ChangeStructureProvisionType`, `GetConsumptionFactor`, `GetControlTowers`, `GetMoonForTower`, `GetMoonProcessInfoForTower`, `GetSiloCapacityByItemID`, `GetStarbasePermissions`, `GetTowerNotificationSettings`, `GetTowerSentrySettings`, `LinkResourceForTower`, `OnlineOrbital`, `RelinquishStructureControl`, `RunMoonProcessCycleforTower`, `SetStarbasePermissions`, `SetTowerNotifications`, `SetTowerPassword`, `SetTowerSentrySettings`, `UnanchorOrbital` |
| `planetMgr` | 20 | `DeleteLaunch`, `GMAddCommodity`, `GMConvertCommandCenter`, `GMForceInstallProgram`, `GMGetLocalDistributionReport`, `GMGetSynchedServerState`, `GMRunDepletionSim`, `GetCommandPinsForPlanet`, `GetExtractorsForPlanet`, `GetFullNetworkForOwner`, `GetMyLaunchesDetails`, `GetPlanetInfo`, `GetPlanetResourceInfo`, `GetPlanetsForChar`, `GetProgramResultInfo`, `GetResourceData`, `UserAbandonPlanet`, `UserLaunchCommodities`, `UserTransferCommodities`, `UserUpdateNetwork` |
| `ProjectDiscovery` | 19 | `fix_task_id`, `get_is_player_entitled_to_tutorial_reward`, `get_maximum_bonus_samples`, `get_new_task`, `get_player_analysis_kredits`, `get_player_state`, `get_player_statistics`, `get_project_id`, `get_remaining_bonus_samples`, `get_total_needed_xp`, `get_tutorial_completion_status`, `get_tutorial_level`, `get_tutorial_xp_reward`, `give_tutorial_rewards`, `increase_tutorial_level`, `new_training_task`, `post_classification`, `reset_tutorial`, `skip_tutorial` |
| `warRegistry` | 18 | `AcceptAllyNegotiation`, `AcceptSurrender`, `CreateSurrenderNegotiation`, `CreateWarAllyOffer`, `DeclineAllyOffer`, `DeclineSurrender`, `GMActivateDefender`, `GMDeactivateDefender`, `GMExtendAllyContract`, `GMJoinDefender`, `GMSetDeclareTime`, `GMSetWarFinishTime`, `GetNegotiations`, `GetWarNegotiation`, `RetractMutualWar`, `RetractWarAllyOffer`, `SetOpenForAllies`, `UnBind` |
| `map` | 17 | `GetAllRoamingWeatherSystems`, `GetBeaconCount`, `GetConstellationLPData`, `GetCurrentSovData`, `GetDeadspaceAgentsMap`, `GetDeadspaceComplexMap`, `GetFacWarZoneInfo`, `GetHistory`, `GetIncursionGlobalReport`, `GetIndustryCostModifier`, `GetIndustryJobsOverLast24Hours`, `GetMyExtraMapInfo`, `GetMyExtraMapInfoAgents`, `GetRecentSovActivity`, `GetSolarSystemVisits`, `GetStationCount`, `GetSystemsInIncursions` |
| `bountyProxy` | 16 | `AddToBounty`, `CancelSellKillRight`, `GMClearBountyCache`, `GMReimburseBounties`, `GetKillRightsOnCharacters`, `GetMyBounties`, `GetTopAllianceBounties`, `GetTopAllianceBountyHunters`, `GetTopCorpBounties`, `GetTopCorporationBountyHunters`, `GetTopPilotBounties`, `GetTopPilotBountyHunters`, `SearchAllianceBounties`, `SearchCharBounties`, `SearchCorpBounties`, `SellKillRight` |
| `operationsManager` | 16 | `activate_operation_in_solar_system`, `block_task`, `complete_category_for_character`, `get_active_category_id`, `get_category_state`, `get_character_progress`, `get_mission_avoidance_systems`, `get_operations_completed_at_least_once`, `get_tutorial_state`, `has_skipped_tutorial`, `is_category_complete`, `is_main_tutorial_finished`, `process_client_event`, `skip_task`, `start_site`, `unblock_task` |
| `dungeonInstanceCacheMgr` | 15 | `GetBulwarkMiningSiteCounts`, `GetCombatAnomaliesCount`, `GetCombatAnomalyInstances`, `GetFactionWarfareBattlefieldInstances`, `GetFactionWarfareInstances`, `GetHomefrontSiteInstances`, `GetIceBeltInstances`, `GetIceBeltsCount`, `GetOreAnomaliesCount`, `GetOreAnomaliesCountInRange`, `GetOreAnomalyInstances`, `GetPirateInsurgencyIceHeistInstances`, `GetPirateInsurgencyInstances`, `GetSignatureInstances`, `GetTriglavianSiteInstances` |
| `userSvc` | 14 | `ActivateCharacterReSculpt`, `ActivateMultiTraining`, `ActivateSoulboundMultiTraining`, `CastDownvote`, `CastUpvote`, `CreateRedeemTokenQA`, `GetCreateDate`, `GetSessionPlaytimeMinutes`, `GetUpdatedMultiCharacterTraining`, `GetUserName`, `GetUserToken`, `InvalidateMultiCharacterTrainingCache`, `ReportBot`, `ReportISKSpammer` |
| `@inventory` | 13 | `ALSCConfigGet`, `ClearCharges`, `DestroyFitting`, `FitFitting`, `GetPriceForItems`, `ImportExportWithPlanet`, `ListFighterBay`, `MultiMerge`, `QALockItem`, `QAUnlockItem`, `Reprocess`, `StackAll`, `StripFitting` |
| `dogmaIM` | 13 | `AddTargetOBO`, `FullyDescribeAttribute`, `GetRequiredSkillLevels`, `InitiateModuleRepair`, `InitiateModuleRepairMany`, `Overload`, `OverloadRack`, `RemoveTargetOBO`, `RemoveTargets`, `ShipOnlineModules`, `StopModuleRepair`, `StopOverload`, `StopOverloadRack` |
| `seasonManager` | 13 | `challenge_is_dormant`, `claim_challenge_rewards`, `claim_goal_reward`, `fill_up_and_return_challenges_for_character`, `get_challenge`, `get_challenge_expiration_date`, `get_character_progress_by_character_id`, `get_loot_table_by_typeID`, `get_max_active_challenges`, `get_season_end_time`, `process_client_event`, `reload_fsd_data`, `select_season` |
| `charMgr` | 11 | `AddOwnerNote`, `EditOwnerNote`, `GetCharacterCreationDate`, `GetCohortsForCharacter`, `GetOrganizationInfoForCharacters`, `GetOwnerNote`, `GetOwnerNoteLabels`, `GetPaperdollState`, `GetPrivateInfo`, `LogSettings`, `RemoveOwnerNote` |
| `entity` | 11 | `CmdAbandonDrone`, `CmdAssist`, `CmdEngage`, `CmdGuard`, `CmdMineRepeatedly`, `CmdReconnectToDrones`, `CmdReturnBay`, `CmdReturnHome`, `CmdSalvage`, `CmdUnanchor`, `GetDebugSpawnPoolFleetEnemyClusters` |
| `invbroker` | 10 | `AssembleCargoContainer`, `DepositPlexToVault`, `GetContainerContents`, `GetDamageForCrystals`, `IsMyItemAtDockableLocation`, `SetLabel`, `TrashItems`, `ValidateItemCanBeOpened`, `ValidateItemListCanBeOpened`, `WithdrawPlexFromVault` |
| `fighterMgr` | 9 | `CmdAbandonFighter`, `CmdActivateAbilitySlots`, `CmdDeactivateAbilitySlots`, `CmdScoopAbandonedFighterFromSpace`, `ExecuteMovementCommandOnFighters`, `LaunchFightersFromTubes`, `LoadFightersToTube`, `RecallFightersToTubes`, `UnloadTubeToFighterBay` |
| `kiringMgr` | 9 | `ActivateRedeemingCode`, `GetAntiAddictionCode`, `GetEveGuardServerAddress`, `GetKiringConfiguration`, `GetSAuthAid`, `IsMonochromeStyleEnabled`, `PerformKiringServerSideAuthenticationFromCode`, `PlaceKiringOrder`, `ShouldKickCheater` |
| `lookupSvc` | 9 | `LookupCharacters`, `LookupCorporations`, `LookupEvePlayerCharacters`, `LookupFactions`, `LookupKnownLocationsByGroup`, `LookupLocationsByGroup`, `LookupNoneNPCAccountOwners`, `LookupOwners`, `LookupWarableCorporationsOrAlliances` |
| `pvpFilamentMgr` | 9 | `AbyssalPVPEndGateActivation`, `GetActiveEvents`, `GetAllEvents`, `GetCharacterStatistics`, `GetLeaderboard`, `GetMostRecentEvent`, `GetNextEventDate`, `JoinPVPQueue`, `LeavePVPQueue` |
| `sovMgr` | 9 | `AcquireSkyhooks`, `DestroySkyhooks`, `GetInfrastructureHubInfo`, `GetSovHubFuelAccessGroup`, `GetSovStructuresInfoForLocalSolarSystem`, `GetSovStructuresInfoForSolarSystem`, `GetSystemSovereigntyInfo`, `IsOnLocalSovHubFuelAccessGroup`, `SetSovHubFuelAccessGroup` |
| `voteManager` | 9 | `ActivateSanctionedAction`, `CanRunForCEO`, `GetCorpSanctionedActions`, `GetVoteCase`, `GetVoteCaseOptions`, `GetVoteCasesByCorporation`, `GetVotes`, `InsertVote`, `InsertVoteCase` |
| `charUnboundMgr` | 8 | `CancelCharacterDeletePrepare`, `DeleteCharacter`, `GetCharacterInfo`, `GetNumCharacters`, `PrepareCharacterForDelete`, `ToggleValidation`, `UpdateCharacterBloodline`, `UpdateCharacterGender` |
| `corpRecProxy` | 8 | `CreateRecruitmentAd`, `DeleteRecruitmentAd`, `GetRecruiters`, `GetRecruitmentAd`, `GetRecruitmentAdsByCorpID`, `GetRecruitmentAdsForCorporation`, `SearchCorpAds`, `UpdateRecruitmentAd` |
| `corporationSvc` | 8 | `CreateMedal`, `GetAllCorpMedals`, `GetCorpInfo`, `GetEmploymentRecord`, `GetMedalDetails`, `GetRecipientsOfMedal`, `GiveMedalToCharacters`, `SetMedalStatus` |
| `essMgr` | 8 | `AttemptLinkToMainBank`, `AttemptLinkToReserveBank`, `GetMainBankTheftsForClientSolarSystem`, `GetReserveBankTheftsForClientSolarSystem`, `IsClientLinkedToReserveBank`, `RequestMainBankUnlink`, `RequestReserveBankUnlink`, `RequestUnlockReserveBank` |
| `fleetProxy` | 8 | `AddFleetFinderAdvert`, `ApplyToJoinFleet`, `GetAvailableFleetAds`, `GetMyFleetFinderAdvert`, `RemoveFleetFinderAdvert`, `UpdateAdvertAllowedEntities`, `UpdateAdvertInfo`, `UpdateFleetAdvertWithNewLeader` |
| `ingamereport` | 8 | `report_exploit_abuse`, `report_gambling`, `report_inappropriate_character_name`, `report_inappropriate_language`, `report_macro_use`, `report_offensive_mail`, `report_other`, `report_questionable_transaction` |
| `jumpCloneSvc` | 8 | `AcceptShipCloneInstallation`, `CancelShipCloneInstallation`, `GetNumClonesInPilotsStructure`, `GetShipCloneState`, `GetStationCloneState`, `InstallCloneInStructure`, `OfferShipCloneInstallation`, `ResetLastCloneJumpTime` |
| `node_graph` | 8 | `process_client_blackboard_request`, `process_client_graph_message`, `qa_start_node`, `qa_stop_active_nodes`, `qa_stop_node`, `qa_stop_node_graph`, `qa_subscribe`, `qa_unsubscribe` |
| `shipCosmeticsMgr` | 8 | `ActivateSkinLicense`, `ApplySkinToShip`, `GMExpireSkinLicense`, `GetFirstPartySkinData`, `GetLicencedSkins`, `GetLicencedSkinsForShipType`, `GiveSkin`, `RemoveSkin` |
| `structureAssetSafety` | 8 | `GetItemsInSafetyForCharacter`, `GetItemsInSafetyForCorp`, `GetStructuresICanDeliverTo`, `GetWrapNames`, `MoveCorpAssetsToSafety`, `MoveEjectTimeGM`, `MovePersonalAssetsToSafety`, `MoveSafetyWrapToStructure` |
| `crateService` | 7 | `ClaimFitting`, `ClaimLoot`, `ClaimLootFromCrateStack`, `ClaimSkillBundle`, `GetCrateLocation`, `OpenCrate`, `PeekInsideFixedCrate` |
| `agentMgr` | 6 | `GetCompletedCareerAgentIDs`, `GetDisabledMissions`, `GetMessagesForEpicArcMissions`, `GetMyEpicArcStatus`, `GetPlausibleMissionIDs`, `GetSolarSystemOfAgent` |
| `contextualOfferMgr` | 6 | `TrackOfferBuyPress`, `TrackOfferClosed`, `TrackOfferDelivered`, `TrackOfferPurchased`, `TrackOfferPurchasedSeen`, `TrackOfferSeen` |
| `corpmgr` | 6 | `AuditMember`, `GetAggressionSettingsForCorps`, `GetAssetInventory`, `GetAssetInventoryForLocation`, `GetPublicInfo`, `SearchAssets` |
| `fleetMgr` | 6 | `AddToWatchlist`, `BroadcastToBubble`, `BroadcastToSystem`, `ForceLeaveFleet`, `RegisterForDamageUpdates`, `RemoveFromWatchlist` |
| `keeper` | 6 | `ActivateAccelerationGate`, `GetLevelEditor`, `GetOperationSpawnpointSolarSystem`, `GotoRoom`, `IsOperationSite`, `PlayDungeon` |
| `marketProxy` | 6 | `CancelCharOrder`, `GetPlexNewPriceHistory`, `GetPlexOldPriceHistory`, `ModifyCharOrder`, `ModifyPlexCharOrder`, `PlacePlexSellOrder` |
| `moonExtractions` | 6 | `CancelExtraction`, `FractureChunkWithStructure`, `GetExtractionAndEventIDFromStructureID`, `GetExtractionsForCorp`, `GetMoonResourcesForStructure`, `StartNewExtraction` |
| `mutualWarInviteMgr` | 6 | `GetPendingInvitesForSession`, `IsCorpInvitesBlockedPlayer`, `RespondToInviteByPlayer`, `SendInviteByPlayer`, `SetInvitesBlockedByPlayer`, `WithdrawInviteByPlayer` |
| `warsInfoMgr` | 6 | `GetPublicWarInfo`, `GetTop50`, `GetWarsByOwnerID`, `GetWarsByOwners`, `GetWarsForStructure`, `GetWarsRequiringAssistance` |
| `FastCheckoutService` | 5 | `BuyOffer`, `ClearFastCheckoutCacheForUser`, `GetOffersForUser`, `GetTestingConfiguration`, `IsFastCheckoutEnabledForUser` |
| `abyssalMgr` | 5 | `AbyssalEndGateActivation`, `AbyssalEntranceDeployment`, `AbyssalEntranceGateActivation`, `AbyssalGateActivation`, `ClientIsReady` |
| `authentication` | 5 | `AccruedTime`, `AmUnderage`, `Login`, `Logout`, `SetLanguageID` |
| `dungeon` | 5 | `DEGetArchetypes`, `DEGetDungeons`, `DEGetFactions`, `DEGetRoomObjectPaletteData`, `DEGetRooms` |
| `dynamicResourceCacheMgr` | 5 | `GetDBSMapData`, `GetDynamicResourceSettings`, `GetESSAgencyData`, `GetESSReserveBankKeys`, `GetESSSystemDetails` |
| `fwCharacterEnlistmentMgr` | 5 | `CreateMyDirectEnlistment`, `GetMyDirectEnlistmentCooldownTimestamp`, `GetMyEnlistment`, `RemoveMyDirectEnlistment`, `SetMyCorpAllowedEnlistmentFactions` |
| `hackingMgr` | 5 | `ClickedOnTile`, `QuitHackingAttempt`, `StartNewGameInstance`, `UsedUtilityElement`, `UsedUtilityElementOnVirus` |
| `machoNet` | 5 | `ForwardCharacterNotification`, `GetClusterGameStatisticsForClient`, `NoBlock`, `ReloadClientCodeHash`, `SetGlobalConfigValue` |
| `storeManager` | 5 | `buy_offer`, `can_purchase_offer`, `get_categories`, `get_offers`, `get_products` |
| `structureControl` | 5 | `BoardStructure`, `EjectFromStructure`, `GetStructurePilot`, `ReleaseControl`, `TakeControl` |
| `@globalInventory` | 4 | `GetAssetWorth`, `List`, `ListIncludingContainers`, `ListStationItems` |
| `XmppChatMgr` | 4 | `EnsureResourceWarsChannelExists`, `GMMute`, `GMUnmute`, `GrantChannelOwnership` |
| `abyssal_content_manager` | 4 | `GenerateAbyssEncounterContent`, `GenerateContentFromExistingPlan`, `GetContentDetailsForShipLocation`, `generate_abyss_npc_spawn_report` |
| `activityMgr` | 4 | `ActivitySeen`, `GetActivitiesPopState`, `GetActivityBadgeStatuses`, `SetActivitiesPopState` |
| `alert` | 4 | `BeanDelivery`, `GetLogModeForError`, `GroupBeanDelivery`, `SendClientStackTraceAlert` |
| `missionTrackerMgr` | 4 | `GetAllMissionObjectives`, `GetMissionInfoItems`, `IsInActiveDungeonID`, `UpdateAllMissions` |
| `paperDollServer` | 4 | `GetMyPaperDollData`, `GetPaperDollDataBatch`, `UpdateExistingCharacterFull`, `UpdateExistingCharacterLimited` |
| `planetOrbitalRegistryBroker` | 4 | `GetSettingsInfo`, `GetTaxRate`, `RevertOrbitalsToInterBus`, `UpdateSettings` |
| `structureDeployment` | 4 | `Anchor`, `CancelUnanchor`, `RenameStructure`, `Unanchor` |
| `structureSettings` | 4 | `CharacterCheckService`, `CharacterGetService`, `CharacterGetServices`, `CharacterHasSetting` |
| `warStatisticMgr` | 4 | `GetBaseInfo`, `GetKillMail`, `GetKills`, `GetKillsByGroup` |
| `@skills` | 3 | `CharStartTrainingSkill`, `GetSkillBundleInfo`, `GetSkillChangesForISIS` |
| `LPStoreMgr` | 3 | `GetAvailableOffersFromCorp`, `TakeOfferForCharacter`, `TakeOfferForCorporation` |
| `bannedwords` | 3 | `check_search_words_allowed`, `check_words_allowed`, `fetch_regexes` |
| `crimewatch` | 3 | `ApplyTagsForSecStatus`, `RespondToDuelChallenge`, `StartDuelChallenge` |
| `facilityManager` | 3 | `GetFacilitiesByID`, `GetFacilityTaxes`, `SetFacilityTaxes` |
| `publicQaToolsServer` | 3 | `CanGiveItemForMultifit`, `MoveMeTo`, `SlashCmd` |
| `serviceGatewayMgr` | 3 | `qa_disable_hijack_mode`, `qa_enable_hijack_mode`, `qa_is_hijack_mode_enabled` |
| `stationSvc` | 3 | `GetSolarSystem`, `GetStationsForOwner`, `GetSystemsForAlliance` |
| `structureCompressionMgr` | 3 | `CompressItemInStructure`, `DecompressGasInStructure`, `GetMyGasDecompressionEfficiency` |
| `structureCynoBeaconMgr` | 3 | `CmdBridgeToStructureBeacon`, `CmdGroupJumpToStructureBeacon`, `CmdJumpToStructureBeacon` |
| `structureDirectory` | 3 | `GetNearbyJumpBridges`, `GetStructureDescription`, `SetStructureDescription` |
| `structureJumpBridgeMgr` | 3 | `CmdJumpThroughStructureStargate`, `GetJbStructureDestination`, `GetLinkedStructure` |
| `survey` | 3 | `ClaimSurveyRewards`, `GetActiveSurveyID`, `PerformSurveyChecks` |
| `voidSpaceMgr` | 3 | `ClientIsReady`, `VoidSpaceEndGateActivation`, `VoidSpacePlayerJump` |
| `RWManager` | 2 | `get_closest_rw_stations`, `solarsystem_contains_rw_instances` |
| `account` | 2 | `GetDefaultContactCost`, `SetContactCost` |
| `billMgr` | 2 | `CharPayBill`, `PayCorporationBill` |
| `browserLockdownSvc` | 2 | `GetDefaultHomePage`, `IsBrowserInLockdown` |
| `communityTrackMgr` | 2 | `change_qa_mode`, `get_instance_attributes_public` |
| `contractProxy` | 2 | `DeleteMultipleContracts`, `GM_ExpireContract` |
| `corpMiningLedger` | 2 | `GetObserverLedger`, `GetObserversWithMiningEvents` |
| `devIndexManager` | 2 | `GetAllDevelopmentIndices`, `GetDevelopmentIndicesForSystem` |
| `dynamicItemService` | 2 | `CreateDynamicItem`, `GetDynamicItemInfo` |
| `eveguard_report` | 2 | `heartbeat_started`, `heartbeat_stopped` |
| `fwVictoryPointMgr` | 2 | `GetAllVictoryPointStates`, `GetLocalVictoryPointState` |
| `fwWarzoneSolarsystem` | 2 | `GetAllWarzones`, `GetHQSystemIDs` |
| `i2` | 2 | `GetItem`, `IsPrimed` |
| `infoGatheringMgr` | 2 | `GetStateAndConfig`, `LogInfoEventsFromClient` |
| `killRightMgr` | 2 | `ActivateKillRight`, `BuyKillRight` |
| `loadService` | 2 | `GetTotalStats`, `SetConfig` |
| `localizationServer` | 2 | `GetAllTextChanges`, `ReloadFSDPickle` |
| `nes_intro` | 2 | `get_nes_intro_state`, `skip_nes_intro` |
| `overviewPresetMgr` | 2 | `GetStoredPreset`, `StoreLinkAndGetID` |
| `rewardMgr` | 2 | `GetDelayedRewardsByGroupIDs`, `GetRewardLPLogs` |
| `seasonalLoginCampaignManager` | 2 | `claim_reward`, `get_claim_history` |
| `sharedSettingsMgr` | 2 | `GetStoredSharedSetting`, `StoreSettingLinkAndGetID` |
| `ship` | 2 | `GetShipFittingInfo`, `SetShipPassword` |
| `shipCasterLandingPadMgr` | 2 | `GetAllLandingPads`, `GetFactionLandingPads` |
| `solarsystemInterferenceMgr` | 2 | `GetAllInterferenceBands`, `GetLocalInterferenceState` |
| `starterShipcasterTravelMgr` | 2 | `CanCharacterJump`, `CmdJumpThroughStarterShipcaster` |
| `structureDocking` | 2 | `Dock`, `Undock` |
| `superWeaponMgr` | 2 | `ActivateSinglePointTargetedModule`, `ActivateSlashModule` |
| `taleMgr` | 2 | `GetGlobalWorldEventTales`, `get_active_tales_by_template` |
| `wormholeMgr` | 2 | `GetWormholePolarization`, `WormholeJump` |
| `LPSvc` | 1 | `ExchangeConcordLP` |
| `air_npe` | 1 | `skip_air_npe` |
| `cache` | 1 | `GetServerVersionAndBuild` |
| `characterEnergyMgr` | 1 | `GetMyEnergyState` |
| `characterMiningLedger` | 1 | `GetCharacterLogs` |
| `colonyResourcesMgr` | 1 | `GetMyCorporationSkyhooks` |
| `config` | 1 | `GetStationSolarSystemsByOwner` |
| `corpStationMgr` | 1 | `MoveCorpHQHere` |
| `custom_agency_provider` | 1 | `get_content_data` |
| `debug` | 1 | `Exec` |
| `dungeonExplorationMgr` | 1 | `GetMyEscalatingPathDetails` |
| `eject` | 1 | `LaunchShipOrContainerFromWreckOrContainer` |
| `encodedItems` | 1 | `DecodeItem` |
| `epicArcStatus` | 1 | `AgentHasEpicMissionsForCharacter` |
| `eventLog` | 1 | `LogClientEvent` |
| `externalQueueMgr` | 1 | `PublishProxyEvent` |
| `fleetObjectHandler` | 1 | `CreateFleet` |
| `inSpaceCompressionMgr` | 1 | `CompressItemInSpace` |
| `industryManager` | 1 | `CompleteManyJobs` |
| `itemLocking` | 1 | `GetLockedItemLocations` |
| `loginCampaignManager` | 1 | `claim_reward` |
| `loginRewardFacilities` | 1 | `get_my_clone_grade` |
| `mailMgr` | 1 | `PokePlayerAboutChatMsgGm` |
| `milestoneMgr` | 1 | `ClaimRewards` |
| `miningScanMgr` | 1 | `perform_scan` |
| `notificationMgr` | 1 | `LogNotificationInteraction` |
| `peaceTreatyMgr` | 1 | `GetPeaceTreatiesForSession` |
| `performanceMetricsService` | 1 | `get_performance_metrics` |
| `pingService` | 1 | `Ping` |
| `populationCap` | 1 | `MoveCharacterToNewSystem` |
| `raffleMgr` | 1 | `QA_SeedRaffles` |
| `randomJumpMgr` | 1 | `ActivateRandomJumpFilament` |
| `repackagingSvc` | 1 | `RepackageItems` |
| `repairSvc` | 1 | `RepairItemsInStructure` |
| `scanMgr` | 1 | `GetSystemScanMgr` |
| `sessionMgr` | 1 | `RemoveSessionsFromServer` |
| `shipCasterLauncherMgr` | 1 | `GetFactionsWithShipcaster` |
| `shipStanceMgr` | 1 | `SetShipStance` |
| `shipcasterTravelMgr` | 1 | `CmdJumpThroughShipcaster` |
| `standingMgr` | 1 | `GetStandingCompositions` |
| `structure` | 1 | `JettisonStructureFuel` |
| `structureCargoDelivery` | 1 | `DropOffItems` |
| `structureDeliveries` | 1 | `Deliver` |
| `structureGuests` | 1 | `GetGuests` |
| `structureHangarViewMgr` | 1 | `GetMyHangarViewState` |
| `structureVulnerability` | 1 | `UpdateReinforceTimesFromClient` |
| `subscriptionMgr` | 1 | `GetSubscriptionTime` |
| `tcpRawProxyService` | 1 | `GetESPTunnelingAddressByNodeID` |
| `trademgr` | 1 | `InitiateTrade` |
| `vaultManager` | 1 | `get_account_balance` |
| `warpVectorMgr` | 1 | `UseVector` |

## Compatibility candidates

| Route | Eden source |
| --- | --- |
| `agentMgr.GetMyJournalDetails` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `air_npe.get_air_npe_state` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `air_npe.is_air_npe_enabled` | `src/Eden.Protocol/PilotSessions/AccountCompatibilityHandler.cs` |
| `alert.BeanCount` | `src/Eden.Protocol/PilotSessions/AccountCompatibilityHandler.cs` |
| `beyonce.GetFormations` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `bountyProxy.GetBounties` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `bountyProxy.GetBountiesAndKillRights` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `bountyProxy.GetMyKillRights` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `charMgr.GetSettingsInfo` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `charMgr.SetActivityStatus` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `clientStatLogger.LogString` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `corpRegistry.GetCorporateContacts` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `crimewatch.GetClientStates` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `crimewatch.SetSafetyLevel` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `dogmaIM.ChangeDroneSettings` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `dynamicBountyMgr.GetOutputForClientSolarSystem` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `essMgr.GetDataForClientSolarSystem` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `eventLog.LogClientStats` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `eventLog.LogPlayerRequestedDisconnect` | `src/Eden.Protocol/PilotSessions/AccountCompatibilityHandler.cs` |
| `fighterMgr.GetFightersForShip` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `fwWarzoneSolarsystem.GetLocalOccupationState` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `insurgencySolarsystem.GetAllVisibleCampaigns` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `insurgencySolarsystem.GetLocalCampaignClientSnapshot` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `jumpTimers.GetTimers` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `keeper.GetCurrentDungeonForCharacter` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `loginCampaignManager.get_client_campaign_state` | `src/Eden.Protocol/PilotSessions/AccountCompatibilityHandler.cs` |
| `machoNet.GetTime` | `src/Eden.Protocol/PilotSessions/AccountCompatibilityHandler.cs` |
| `map.GetEdencomFortressSystems` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `map.GetEdencomMinorVictorySystems` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `map.GetSecurityModifiedSystems` | `src/Eden.Protocol/PilotSessions/AccountCompatibilityHandler.cs` |
| `map.GetTriglavianMinorVictorySystems` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `marketProxy.GetCharEscrow` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `milestoneMgr.ProcessCharacterLogon` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `multiLoginBlocker.Login` | `src/Eden.Protocol/PilotSessions/AccountCompatibilityHandler.cs` |
| `scanMgr.SignalTrackerRegister` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `seasonManager.get_season_data_for_character` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `seasonalLoginCampaignManager.get_active_campaign` | `src/Eden.Protocol/PilotSessions/AccountCompatibilityHandler.cs` |
| `securityMgr.get_modified_systems` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `shipCosmeticsMgr.GetEnabledCosmetics` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `shipKillCounter.GetItemKillCountPlayer` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `standingMgr.GetNPCNPCStandings` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `stationSvc.GetStationItemBits` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `subscriptionMgr.GetCloneGrade` | `src/Eden.Protocol/PilotSessions/AccountCompatibilityHandler.cs` |
| `userSvc.GetMultiCharactersTrainingSlots` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
| `warRegistry.GetWars` | `src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs` |
