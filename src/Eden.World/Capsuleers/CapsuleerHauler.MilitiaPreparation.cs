using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.FactionWarfare;
using Eden.World.Combat;
using Eden.World.FactionWarfare;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private bool TryPrepareMilitiaPatrol()
    {
        if (_operation.World is not EconomyTransaction world || MilitiaPatrolRules.StagingFaction(Pilot.StationId) is not { } faction)
        {
            return false;
        }
        var catalog = _operation.Catalog;
        if (
            !MilitiaEnlistmentStore.CanJoinBotMilitia(world, catalog, pilotId, faction)
            || militia.MilitiaPatrolFitPolicy(world, catalog, MilitiaPatrolRules.Aldranette, Now) is not { } allowedFit
        )
        {
            return false;
        }

        var profile = CapsuleerDefenseProfile.Read(world, catalog, pilotId);
        if (
            !allowedFit(Pilot.ShipTypeId, world.FittedItems(pilotId, Pilot.ShipId).Select(item => item.TypeId))
            || !profile.Combat
            || profile.RattingWeapons.Length == 0
        )
        {
            var acquisition = ammunitionSupply.TryBuyEarnedPvpShip(
                world,
                catalog,
                database.Traffic,
                Pilot,
                Now,
                stagingSystem: MilitiaPatrolRules.Aldranette,
                allowedMilitiaFit: allowedFit
            );
            _operation.MarketChanged |= acquisition.MarketChanged;
            if (!acquisition.Boarded)
            {
                return false;
            }
            _pilot = ReadPilot(world, CapsuleerCommunityStore.MinerAccountId);
            ResetTradeHull();
        }

        var supplies = resupply.Dock(database, world, catalog, pilotId, Work.Resupply, Now, out var fit);
        _operation.MarketChanged |= supplies.SpentCents != (Work.Resupply?.SpentCents ?? 0);
        _work = Work with { Resupply = supplies };
        profile = CapsuleerDefenseProfile.Read(world, catalog, pilotId, fit);
        if (
            !profile.Combat
            || !profile.RattingWeapons.Any(weapon => weapon.AvailableShots > 0)
            || CapsuleerDefenseRules.Retreat(
                PilotHealthStore.Read(world, Pilot.ShipId, ShipHealth.DefenseFrom(profile.ShipAttributes), Now),
                true
            )
        )
        {
            return false;
        }
        bool alreadyEnlisted = MilitiaEnlistmentStore.Effective(world, world.Pilots.Find(pilotId)!) is not null;
        if (!MilitiaEnlistmentStore.TryJoinBotMilitia(world, catalog, pilotId, faction, Now))
        {
            return false;
        }
        if (!alreadyEnlisted)
        {
            _pilot = ReadPilot(world, CapsuleerCommunityStore.MinerAccountId);
            world.AfterCommit(database.NotifyAccessGroupsChanged);
        }
        return true;
    }
}
