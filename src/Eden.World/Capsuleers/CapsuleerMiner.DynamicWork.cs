using Eden.Gameplay.Capsuleers;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerMiner
{
    private bool TryTakeHuntingWork()
    {
        var policy = database.Settings.Capsuleers;
        var catalog = _operation.Catalog;
        if (
            policy.RoamPercent <= 0
            || !PatrolRules.Allowed(
                new(PatrolKind.Roam, Pilot.SystemId),
                catalog.Definition(CapsuleerProvisioningCatalog.Security, Pilot.SystemId),
                policy
            )
            || Work.FactoryStage is not null
            || Work.FactorySupply is not null
            || Work.RefineryIceType is not null
            || Work.Mercoxit is not null
            || _operation.World is not EconomyTransaction world
            || CapsuleerIndustry.Employer(world, pilotId, Pilot.CorpId) is null
            || CapsuleerIndustry.HasStationFactoryJob(world, pilotId, Pilot.StationId)
            || CapsuleerIndustry.HasWorkDemand(world, catalog, Pilot, Now)
        )
        {
            return false;
        }

        long now = Now;
        var combat = CapsuleerWorkHulls.Find(world, catalog, Pilot, CapsuleerProfession.Combat, now);
        if (combat is null)
        {
            return false;
        }
        if (CapsuleerIndustry.ReadyHuntReplacements(world, catalog, Pilot, combat) < CapsuleerWorkRules.MinimumHuntingReplacements)
        {
            _operation.MarketChanged |= industry.TryReplenishHuntReserve(world, catalog, Pilot, now, combat);
            return false;
        }
        if (Live?.CapsuleerActivity.Read().ActiveSystems.Contains(Pilot.SystemId) != true)
        {
            return false;
        }
        CapsuleerWorkHulls.Board(world, catalog, Pilot, combat, now);
        CapsuleerWorkHandoff.ToFlight(_operation, world, Pilot, combat, Work.ActiveTicks);
        _pilot = ships.FindPilot(world, CapsuleerCommunityStore.MinerAccountId, pilotId)!;
        _operation.TravelChanged = true;
        return true;
    }
}
