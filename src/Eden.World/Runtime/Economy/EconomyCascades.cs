namespace Eden.World.Runtime.Economy;

internal sealed partial class EconomyTransaction
{
    internal void RemoveDependents<TRow>(TRow row)
        where TRow : class
    {
        switch (row)
        {
            case Eden.Data.World.Characters.Account removed:
                RemoveRange(Set<Eden.Data.World.Characters.Pilot>().Where(child => child.AccountId == removed.Id));
                break;
            case Eden.Data.World.Inventory.InventoryOwner removed:
                if (Set<Eden.Data.World.Inventory.OwnedItem>().Any(child => child.OwnerId == removed.Id))
                {
                    throw new InvalidDataException("The resident InventoryOwner still has OwnedItem references.");
                }
                if (Set<Eden.Data.World.Inventory.Ship>().Any(child => child.OwnerId == removed.Id))
                {
                    throw new InvalidDataException("The resident InventoryOwner still has Ship references.");
                }
                break;
            case Eden.Data.World.Combat.NpcAggression removed:
                RemoveRange(Set<Eden.Data.World.Combat.Npcs.WorldNpcEwar>().Where(child => child.NpcId == removed.NpcId));
                break;
            case Eden.Data.World.Market.NpcMarketOrder removed:
                RemoveRange(Set<Eden.Data.World.Market.SavedRegionalMarketStock>().Where(child => child.OrderId == removed.Id));
                break;
            case Eden.Data.World.Inventory.OwnedItem removed:
                RemoveRange(Filter<Eden.Data.World.Structures.SavedStructureService>(child => child.ItemId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Combat.ModuleCooldown>().Where(child => child.ModuleId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Combat.SavedWeaponLink>().Where(child => child.MasterId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Combat.SavedWeaponLink>().Where(child => child.SlaveId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Combat.WorldPlayerEwar>().Where(child => child.ModuleId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Drones.WorldDrone>().Where(child => child.ItemId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Fittings.ActiveFittedModule>().Where(child => child.ModuleId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Propulsion.WorldTackle>().Where(child => child.ModuleId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Repair.PendingLocalRepair>().Where(child => child.ModuleId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Repair.WorldRemoteAssistance>().Where(child => child.ModuleId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Repair.WorldRemoteRepair>().Where(child => child.ModuleId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Thermal.SavedModuleCondition>().Where(child => child.ModuleId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Thermal.SavedNaniteRepair>().Where(child => child.ModuleId == removed.Id));
                break;
            case Eden.Data.World.Characters.SavedJumpClone removed:
                RemoveRange(Set<Eden.Data.World.Characters.SavedPilotImplant>().Where(child => child.CloneId == removed.Id));
                break;
            case Eden.Data.World.Characters.Pilot removed:
                RemoveRange(Filter<Eden.Data.World.Agents.SavedMissionGateUnlock>(child => child.PilotId == removed.Id));
                RemoveRange(Filter<Eden.Data.World.Agents.SavedResearchProject>(child => child.PilotId == removed.Id));
                RemoveRange(Filter<Eden.Data.World.Exploration.SavedDedGateUnlock>(child => child.PilotId == removed.Id));
                RemoveRange(Filter<Eden.Data.World.Exploration.SavedScanProbe>(child => child.PilotId == removed.Id));
                RemoveRange(Filter<Eden.Data.World.Exploration.SavedResolvedSignature>(child => child.PilotId == removed.Id));
                if (Set<Eden.Data.World.Exploration.SavedHackingAttempt>().Find(removed.Id) is { } attempt)
                {
                    Remove(attempt);
                }
                if (Set<Eden.Data.World.Exploration.SavedProbeScan>().Find(removed.Id) is { } scan)
                {
                    Remove(scan);
                }
                RemoveRange(Filter<Eden.Data.World.Calendar.SavedCalendarEvent>(c => c.OwnerId == removed.Id));
                RemoveRange(Filter<Eden.Data.World.Calendar.SavedCalendarParticipant>(c => c.PilotId == removed.Id));
                RemoveRange(Filter<Eden.Data.World.Calendar.SavedCalendarChange>(c => c.PilotId == removed.Id));
                RemoveRange(Filter<Eden.Data.World.Mail.SavedMailEntry>(c => c.PilotId == removed.Id));
                RemoveRange(Filter<Eden.Data.World.Mail.SavedMailLabel>(c => c.PilotId == removed.Id));
                RemoveRange(Filter<Eden.Data.World.Mail.SavedMailChange>(c => c.PilotId == removed.Id));
                RemoveRange(Filter<Eden.Data.World.Mail.SavedMailingListMember>(c => c.PilotId == removed.Id));
                RemoveRange(Filter<Eden.Data.World.Mail.SavedMailingListChange>(c => c.PilotId == removed.Id));
                RemoveRange(Filter<Eden.Data.World.Mail.SavedInboxNotification>(c => c.ReceiverId == removed.Id));
                RemoveRange(Filter<Eden.Data.World.Characters.SavedContact>(c => c.PilotId == removed.Id));
                RemoveRange(Filter<Eden.Data.World.Characters.SavedContact>(c => c.ContactId == removed.Id));
                RemoveRange(Filter<Eden.Data.World.Characters.SavedAgentContact>(c => c.PilotId == removed.Id));
                RemoveRange(Filter<Eden.Data.World.Characters.SavedContactLabel>(c => c.PilotId == removed.Id));
                RemoveRange(Filter<Eden.Data.World.Characters.SavedBlockedContact>(c => c.PilotId == removed.Id));
                RemoveRange(Filter<Eden.Data.World.Characters.SavedBlockedContact>(c => c.ContactId == removed.Id));
                RemoveRange(Filter<Eden.Data.World.Bookmarks.SavedBookmark>(c => c.CreatorId == removed.Id));
                RemoveRange(Filter<Eden.Data.World.Bookmarks.SavedBookmarkFolder>(c => c.PilotId == removed.Id));
                RemoveRange(Filter<Eden.Data.World.Bookmarks.SavedBookmarkConnection>(c => c.PilotId == removed.Id));
                RemoveRange(Filter<Eden.Data.World.Planetary.SavedPlanetaryColony>(c => c.PilotId == removed.Id));
                RemoveRange(Filter<Eden.Data.World.Planetary.SavedPlanetaryLaunch>(c => c.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Agents.SavedMission>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Capsuleers.SavedCapsuleerAgent>().Where(child => child.PilotId == removed.Id));
                if (Set<Eden.Data.World.Capsuleers.SavedCapsuleerCommunity>().Any(child => child.PilotId == removed.Id))
                {
                    throw new InvalidDataException("The resident Pilot still has SavedCapsuleerCommunity references.");
                }
                RemoveRange(Set<Eden.Data.World.Capsuleers.SavedCapsuleerHauler>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Characters.SavedCharacterDeletion>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Characters.SavedCharacterSetting>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Filter<Eden.Data.World.Characters.SavedCharacterSheet>(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Loot.SavedJetcan>().Where(child => child.OwnerId == removed.Id));
                if (Set<Eden.Data.World.Loot.SavedJettisonCooldown>().Find(removed.Id) is { } cooldown)
                {
                    Remove(cooldown);
                }
                RemoveRange(Set<Eden.Data.World.Characters.SavedCombatReport>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Characters.SavedCompletedTask>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Characters.SavedHomeCloneTransfer>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Characters.SavedImplantState>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Characters.SavedJumpClone>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Characters.SavedHomeStation>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Agents.SavedMissionProgress>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Agents.SavedAgentDecline>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Market.SavedLoyaltyBalance>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Characters.SavedNpcStanding>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Characters.SavedStandingTransaction>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Characters.SavedTaskCounter>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Combat.NpcBounty>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Combat.PendingSecurityReward>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Combat.SavedSecurityStatus>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Combat.SavedSecurityTransaction>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Combat.ShipLoss>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Combat.WorldMissile>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Combat.WorldPlayerEwar>().Where(child => child.PilotId == removed.Id));
                if (Set<Eden.Data.World.Corporations.SavedCorporation>().Any(child => child.CeoId == removed.Id))
                {
                    throw new InvalidDataException("The resident Pilot still has SavedCorporation references.");
                }
                if (Set<Eden.Data.World.Corporations.SavedCorporation>().Any(child => child.CreatorId == removed.Id))
                {
                    throw new InvalidDataException("The resident Pilot still has SavedCorporation references.");
                }
                RemoveRange(Set<Eden.Data.World.Corporations.SavedCorporationMemberAccess>().Where(child => child.PilotId == removed.Id));
                RemoveRange(
                    Set<Eden.Data.World.Corporations.SavedCorporationWalletSelection>().Where(child => child.PilotId == removed.Id)
                );
                RemoveRange(Set<Eden.Data.World.Industry.IndustryJob>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Exploration.SavedWormholeJump>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Insurance.SavedInsuranceLedger>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Insurance.SavedInsurancePolicy>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Loot.WorldWreck>().Where(child => child.OwnerId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Market.MarketExecution>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Market.PilotWallet>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Market.SavedPersonalOrder>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Mining.SavedPersonalMiningLog>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Propulsion.WorldTackle>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Repair.StationRepairPayment>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Repair.WorldRemoteAssistance>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Repair.WorldRemoteRepair>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Skills.QueuedLevel>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Skills.TrainingLog>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Skills.PilotSkill>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Skills.SavedFreeSkillPoints>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Thermal.SavedNaniteRepair>().Where(child => child.PilotId == removed.Id));
                break;
            case Eden.Data.World.Capsuleers.SavedCapsuleerAgent removed:
                RemoveRange(Set<Eden.Data.World.Capsuleers.SavedCapsuleerJob>().Where(child => child.PilotId == removed.PilotId));
                RemoveRange(Set<Eden.Data.World.Capsuleers.SavedCapsuleerSuspension>().Where(child => child.PilotId == removed.PilotId));
                break;
            case Eden.Data.World.Capsuleers.SavedCapsuleerPopulation removed:
                RemoveRange(
                    Set<Eden.Data.World.Capsuleers.SavedCapsuleerAgent>().Where(child => child.CommunitySystemId == removed.SystemId)
                );
                break;
            case Eden.Data.World.Corporations.SavedCorporation removed:
                RemoveRange(Set<Eden.Data.World.Corporations.SavedCorporationTitle>().Where(child => child.CorporationId == removed.Id));
                break;
            case Eden.Data.World.Insurance.SavedInsurancePolicy removed:
                if (Set<Eden.Data.World.Insurance.SavedInsuranceLedger>().Any(child => child.PolicyId == removed.Id))
                {
                    throw new InvalidDataException("The resident SavedInsurancePolicy still has SavedInsuranceLedger references.");
                }
                break;
            case Eden.Data.World.Mining.SavedMiningSite removed:
                RemoveRange(Set<Eden.Data.World.Mining.SavedMiningDeposit>().Where(child => child.SiteId == removed.Id));
                break;
            case Eden.Data.World.Calendar.SavedCalendarEvent removed:
                RemoveRange(Filter<Eden.Data.World.Calendar.SavedCalendarParticipant>(c => c.EventId == removed.Id));
                RemoveRange(Filter<Eden.Data.World.Calendar.SavedCalendarChange>(c => c.EventId == removed.Id));
                break;
            case Eden.Data.World.Bookmarks.SavedBookmarkFolder removed:
                RemoveRange(Filter<Eden.Data.World.Bookmarks.SavedBookmark>(b => b.FolderId == removed.Id));
                RemoveRange(Filter<Eden.Data.World.Bookmarks.SavedBookmarkSubfolder>(s => s.FolderId == removed.Id));
                RemoveRange(Filter<Eden.Data.World.Bookmarks.SavedBookmarkConnection>(c => c.FolderId == removed.Id));
                break;
            case Eden.Data.World.Planetary.SavedPlanetaryLaunch removed:
                RemoveRange(Filter<Eden.Data.World.Inventory.OwnedItem>(i => i.LocationId == removed.Id));
                break;
            case Eden.Data.World.Agents.SavedMission removed:
                RemoveRange(Filter<Eden.Data.World.Agents.SavedMissionGateUnlock>(child => child.MissionId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Agents.SavedMissionMember>().Where(child => child.MissionId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Agents.SavedMissionRoom>().Where(child => child.MissionId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Agents.SavedMissionStructure>().Where(child => child.MissionId == removed.Id));
                break;
            case Eden.Data.World.Structures.SavedStructure removed:
                RemoveRange(Filter<Eden.Data.World.Structures.SavedStructureService>(child => child.StructureId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Structures.SavedStructureAccess>().Where(child => child.StructureId == removed.Id));
                break;
            case Eden.Data.World.Docking.FlightPosition removed:
                Eden.World.Fleets.CommandBurstStore.Clear(this, removed.ShipId);
                Eden.World.Repair.PilotRemoteAssistanceStore.ClearForTravel(this, removed.ShipId);
                break;
            case Eden.Data.World.Inventory.Ship removed:
                Eden.World.Fleets.CommandBurstStore.Clear(this, removed.Id);
                if (Set<Eden.Data.World.Characters.Pilot>().Any(child => child.ActiveShipId == removed.Id && child.Id == removed.OwnerId))
                {
                    throw new InvalidDataException("The resident Ship still has Pilot references.");
                }
                RemoveRange(Set<Eden.Data.World.Combat.NpcAggression>().Where(child => child.TargetShipId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Combat.Npcs.WorldNpcEwar>().Where(child => child.TargetShipId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Combat.SavedWeaponLink>().Where(child => child.ShipId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Combat.ShipHealthState>().Where(child => child.ShipId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Combat.WorldPlayerEwar>().Where(child => child.SourceShipId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Docking.FlightPosition>().Where(child => child.ShipId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Drones.WorldDroneCombatTriggers>().Where(child => child.ShipId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Fittings.ActiveFittedModule>().Where(child => child.ShipId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Fittings.ShipEnergy>().Where(child => child.ShipId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Propulsion.SpatialNavigationState>().Where(child => child.ShipId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Propulsion.WorldTackle>().Where(child => child.SourceShipId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Repair.PendingLocalRepair>().Where(child => child.ShipId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Repair.WorldRemoteAssistance>().Where(child => child.SourceShipId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Repair.WorldRemoteAssistance>().Where(child => child.TargetId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Repair.WorldRemoteRepair>().Where(child => child.SourceShipId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Repair.WorldRemoteRepair>().Where(child => child.TargetId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Thermal.SavedNaniteRepair>().Where(child => child.ShipId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Thermal.SavedShipHeat>().Where(child => child.ShipId == removed.Id));
                break;
            case Eden.Data.World.Combat.ShipLoss removed:
                if (Set<Eden.Data.World.Loot.WorldWreck>().Any(child => child.ShipId == removed.ShipId))
                {
                    throw new InvalidDataException("The resident ShipLoss still has WorldWreck references.");
                }
                break;
            case Eden.Data.World.Loot.SavedJetcan removed:
                RemoveRange(Filter<Eden.Data.World.Inventory.OwnedItem>(child => child.LocationId == removed.Id));
                break;
            case Eden.Data.World.Mining.WorldAsteroid removed:
                RemoveRange(Set<Eden.Data.World.Mining.SavedMiningDeposit>().Where(child => child.Id == removed.Id));
                break;
            case Eden.Data.World.Combat.Npcs.WorldNpc removed:
                RemoveRange(Set<Eden.Data.World.Combat.NpcAggression>().Where(child => child.NpcId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Combat.NpcBounty>().Where(child => child.NpcId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Combat.Npcs.WorldNpcEnergy>().Where(child => child.NpcId == removed.Id));
                if (Set<Eden.Data.World.Combat.ShipLoss>().Any(child => child.NpcId == removed.Id))
                {
                    throw new InvalidDataException("The resident WorldNpc still has ShipLoss references.");
                }
                if (Set<Eden.Data.World.Loot.WorldWreck>().Any(child => child.NpcId == removed.Id))
                {
                    throw new InvalidDataException("The resident WorldNpc still has WorldWreck references.");
                }
                RemoveRange(Set<Eden.Data.World.Mining.WorldNpcMiningCargo>().Where(child => child.NpcId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Mining.WorldNpcMiningMember>().Where(child => child.NpcId == removed.Id));
                break;
            case Eden.Data.World.Combat.Npcs.WorldNpcEncounter removed:
                RemoveRange(Set<Eden.Data.World.Mining.WorldNpcMiningOperation>().Where(child => child.EncounterId == removed.Id));
                break;
            case Eden.Data.World.Mining.WorldNpcMiningOperation removed:
                RemoveRange(Set<Eden.Data.World.Mining.WorldNpcMiningMember>().Where(child => child.EncounterId == removed.EncounterId));
                break;
        }
    }
}
