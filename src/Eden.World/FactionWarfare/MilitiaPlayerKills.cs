using Eden.Data.Catalog;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Insurance;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Economy;
using Eden.World.Insurance;
using Eden.World.Market;
using Eden.World.Runtime.Economy;

namespace Eden.World.FactionWarfare;

internal static class MilitiaPlayerKills
{
    internal static void Settle(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot victim,
        Ship hull,
        CombatParty final,
        int system,
        SpacePosition position,
        IReadOnlyList<CombatLostItem> items,
        long now
    )
    {
        if (
            world is not EconomyTransaction
            || final.CharacterId is not long killerId
            || world.Pilots.Find(killerId) is not { } killer
            || killer.Id == victim.Id
            || MilitiaEnlistmentStore.Effective(world, killer) is not { } faction
            || !MilitiaRules.AreEnemies(faction, MilitiaEnlistmentStore.Effective(world, victim))
            || catalog.RequireItem(hull.TypeId).GroupId is FleetDefinitions.CapsuleGroup or FleetDefinitions.CorvetteGroup
        )
        {
            return;
        }

        var agents = world.Set<SavedCapsuleerAgent>();
        bool killerIsBot = agents.Find(killer.Id) is not null;
        bool victimIsBot = agents.Find(victim.Id) is not null;
        // Distinct autonomous pilots share a storage account. The account check still excludes same-account human kills.
        if (killer.AccountId == victim.AccountId && !(killerIsBot && victimIsBot))
        {
            return;
        }
        MilitiaProgressionStore.Record(world, killer, faction, new(1, 0), now);
        if (killerIsBot || victimIsBot)
        {
            return;
        }
        long insuredValue = PilotInsuranceStore.Value(world, catalog, hull.TypeId);
        decimal insurance =
            insuredValue > 0 ? InsuranceRules.Payout(insuredValue, InsuranceLevel.Platinum) / (decimal)MarketDefinitions.CentsPerIsk : 0;
        var reference = MarketValuations.ReferencePrices(catalog);
        decimal value = items.Sum(LostValue);
        long total = MilitiaProgressionRules.PvpLoyalty(Price(hull.TypeId), insurance, value);
        if (total == 0)
        {
            return;
        }

        var contributors = PilotInteractionHistoryStore
            .DamagePilots(world, hull.Id)
            .Where(pilot =>
                pilot.AccountId != killer.AccountId
                && pilot.AccountId != victim.AccountId
                && agents.Find(pilot.Id) is null
                && MilitiaEnlistmentStore.Effective(world, pilot) == faction
                && pilot.ActiveShip.Position is { } at
                && at.SystemId == system
                && position.DistanceTo(new(at.X, at.Y, at.Z)) <= NpcEncounterDefinitions.VisibilityRange
            )
            .OrderBy(pilot => pilot.Id)
            .DistinctBy(pilot => pilot.AccountId)
            .Append(killer)
            .OrderBy(pilot => pilot.Id)
            .ToArray();
        for (int i = 0; i < contributors.Length; i++)
        {
            long share = MilitiaProgressionRules.Share(total, i, contributors.Length);
            MilitiaLoyalty.Credit(world, faction, contributors[i], share, MilitiaLoyaltySource.PlayerShipLoss, hull.Id, now);
        }

        decimal LostValue(CombatLostItem item) => Price(item.TypeId) * item.Destroyed + item.Contents.Sum(LostValue);

        decimal Price(int type) =>
            EconomicStore.AdjustedPrice(world, type) is > 0 and var adjusted ? adjusted
            : reference.TryGetValue(type, out decimal quoted) ? quoted
            : InsuranceRules.CatalogValue(catalog.Read(CatalogData.Client.Types, (long)type)?.BasePrice)
                / (decimal)MarketDefinitions.CentsPerIsk;
    }
}
