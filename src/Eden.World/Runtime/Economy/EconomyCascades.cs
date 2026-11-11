using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class EconomyTransaction
{
    internal void RemoveDependents<TRow>(TRow row)
        where TRow : class
    {
        switch (row)
        {
            case Eden.Data.World.Capsuleers.SavedCapsuleerPopulation removed:
                RemoveRange(
                    Set<Eden.Data.World.Capsuleers.SavedCapsuleerAgent>().Where(child => child.CommunitySystemId == removed.SystemId)
                );
                break;
            case Eden.Data.World.Characters.Pilot removed:
                RemoveRange(Set<Eden.Data.World.Capsuleers.SavedCapsuleerAgent>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Characters.SavedCharacterDeletion>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Characters.SavedCompletedTask>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Characters.SavedNpcStanding>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Characters.SavedTaskCounter>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Combat.SavedSecurityStatus>().Where(child => child.PilotId == removed.Id));
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
                RemoveRange(Set<Eden.Data.World.Loot.WorldWreck>().Where(child => child.OwnerId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Market.MarketExecution>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Market.PilotWallet>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Market.SavedPersonalOrder>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Mining.SavedPersonalMiningLog>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Propulsion.WorldTackle>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Repair.WorldRemoteAssistance>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Skills.PilotSkill>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Skills.SavedFreeSkillPoints>().Where(child => child.PilotId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Thermal.SavedNaniteRepair>().Where(child => child.PilotId == removed.Id));
                break;
            case Eden.Data.World.Characters.Account removed:
                break;
            case Eden.Data.World.Inventory.Ship removed:
                RemoveRange(Set<Eden.Data.World.Combat.NpcAggression>().Where(child => child.TargetShipId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Combat.Npcs.WorldNpcEwar>().Where(child => child.TargetShipId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Combat.SavedWeaponLink>().Where(child => child.ShipId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Combat.ShipHealthState>().Where(child => child.ShipId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Combat.WorldPlayerEwar>().Where(child => child.SourceShipId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Docking.FlightPosition>().Where(child => child.ShipId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Fittings.ActiveFittedModule>().Where(child => child.ShipId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Fittings.ShipEnergy>().Where(child => child.ShipId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Propulsion.WorldTackle>().Where(child => child.SourceShipId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Repair.PendingLocalRepair>().Where(child => child.ShipId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Repair.WorldRemoteAssistance>().Where(child => child.SourceShipId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Repair.WorldRemoteAssistance>().Where(child => child.TargetId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Thermal.SavedNaniteRepair>().Where(child => child.ShipId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Thermal.SavedShipHeat>().Where(child => child.ShipId == removed.Id));
                break;
            case Eden.Data.World.Inventory.OwnedItem removed:
                RemoveRange(Set<Eden.Data.World.Combat.ModuleCooldown>().Where(child => child.ModuleId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Combat.SavedWeaponLink>().Where(child => child.MasterId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Combat.SavedWeaponLink>().Where(child => child.SlaveId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Combat.WorldPlayerEwar>().Where(child => child.ModuleId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Fittings.ActiveFittedModule>().Where(child => child.ModuleId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Propulsion.WorldTackle>().Where(child => child.ModuleId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Repair.PendingLocalRepair>().Where(child => child.ModuleId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Repair.WorldRemoteAssistance>().Where(child => child.ModuleId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Thermal.SavedModuleCondition>().Where(child => child.ModuleId == removed.Id));
                RemoveRange(Set<Eden.Data.World.Thermal.SavedNaniteRepair>().Where(child => child.ModuleId == removed.Id));
                break;
            case Eden.Data.World.Combat.Npcs.WorldNpc removed:
                RemoveRange(Set<Eden.Data.World.Combat.NpcAggression>().Where(child => child.NpcId == removed.Id));
                if (Set<Eden.Data.World.Combat.ShipLoss>().Any(child => child.NpcId == removed.Id))
                {
                    throw new InvalidDataException("The resident WorldNpc still has ShipLoss references.");
                }
                if (Set<Eden.Data.World.Loot.WorldWreck>().Any(child => child.NpcId == removed.Id))
                {
                    throw new InvalidDataException("The resident WorldNpc still has WorldWreck references.");
                }
                break;
            case Eden.Data.World.Combat.NpcAggression removed:
                RemoveRange(Set<Eden.Data.World.Combat.Npcs.WorldNpcEwar>().Where(child => child.NpcId == removed.NpcId));
                break;
            case Eden.Data.World.Corporations.SavedCorporation removed:
                RemoveRange(Set<Eden.Data.World.Corporations.SavedCorporationTitle>().Where(child => child.CorporationId == removed.Id));
                break;
            case Eden.Data.World.Combat.ShipLoss removed:
                if (Set<Eden.Data.World.Loot.WorldWreck>().Any(child => child.ShipId == removed.ShipId))
                {
                    throw new InvalidDataException("The resident ShipLoss still has WorldWreck references.");
                }
                break;
            case Eden.Data.World.Market.NpcMarketOrder removed:
                RemoveRange(Set<Eden.Data.World.Market.SavedRegionalMarketStock>().Where(child => child.OrderId == removed.Id));
                break;
            case Eden.Data.World.Mining.WorldAsteroid removed:
                RemoveRange(Set<Eden.Data.World.Mining.SavedMiningDeposit>().Where(child => child.Id == removed.Id));
                break;
            case Eden.Data.World.Mining.SavedMiningSite removed:
                RemoveRange(Set<Eden.Data.World.Mining.SavedMiningDeposit>().Where(child => child.SiteId == removed.Id));
                break;
            case Eden.Data.World.Structures.SavedStructure removed:
                RemoveRange(Set<Eden.Data.World.Structures.SavedStructureAccess>().Where(child => child.StructureId == removed.Id));
                break;
        }
    }
}
