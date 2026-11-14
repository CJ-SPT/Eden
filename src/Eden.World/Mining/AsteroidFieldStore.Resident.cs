using System.Globalization;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Economy;
using Eden.Data.World.Mining;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Economy;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Mining;

internal sealed partial class AsteroidFieldStore
{
    internal static void Renew(WorldAction world, CatalogDbContext catalog, IReadOnlyList<SavedBeltRenewal> belts)
    {
        foreach (var deposit in RenewalDeposits(catalog, belts))
        {
            if (world.Set<WorldAsteroid>().Find(deposit.Id) is { } saved)
            {
                if (saved.Quantity < deposit.Quantity)
                {
                    saved.Quantity = deposit.Quantity;
                }
            }
            else
            {
                world.Add(deposit);
            }
        }
    }

    internal static void Populate(WorldAction world, CatalogDbContext catalog, int systemId, long now)
    {
        long? nextRenewal = null;
        var belts = catalog
            .CelestialsInSystem(systemId)
            .Where(b => b.GroupId == AsteroidDefinitions.BeltGroup)
            .OrderBy(b => b.Id)
            .ToArray();
        if (belts.Length == 0)
        {
            return;
        }
        var beltIds = belts.Select(b => b.Id).ToArray();
        var renewed = world.Set<SavedBeltRenewal>().Where(b => Enumerable.Contains(beltIds, b.BeltId)).Select(b => b.BeltId).ToHashSet();
        renewed.UnionWith(world.Set<SavedBeltRenewal>().Local.Select(b => b.BeltId));
        var populated = world
            .Set<WorldAsteroid>()
            .Where(a => Enumerable.Contains(beltIds, a.BeltId))
            .Select(a => a.BeltId)
            .Distinct()
            .ToHashSet();
        foreach (var belt in belts)
        {
            if (!Enumerable.Contains(renewed, belt.Id))
            {
                world.Add(
                    new SavedBeltRenewal
                    {
                        BeltId = belt.Id,
                        SystemId = systemId,
                        NextRenewal = nextRenewal ??=
                            ResourceRenewalRules.Next(now, world.Set<SavedEconomicPolicy>().Single().RenewalHourUtc),
                    }
                );
            }
            // Visiting/reconnecting never replenishes a saved deposit.
            if (Enumerable.Contains(populated, belt.Id))
            {
                continue;
            }
            var rocks = Generate(catalog, belt);
            foreach (var rock in rocks)
            {
                world.Add(rock);
            }
        }
    }

    internal static IReadOnlyList<StaticSpaceObject> List(WorldAction world, CatalogDbContext catalog, int systemId)
    {
        var saved = world
            .Set<WorldAsteroid>()
            .AsNoTracking()
            .Where(a => a.SystemId == systemId && a.Quantity > 0)
            .Where(a => a.Id < MissionMiningRules.FirstDepositId || a.Id >= MissionMiningRules.EndDepositId)
            .OrderBy(a => a.Id)
            .ToArray();
        var types = saved.Select(a => a.TypeId).Distinct().ToArray();
        var definitions = types.Select(catalog.FittingType).OfType<CatalogItemDefinition>().ToDictionary(type => type.Id);
        return saved
            .Select(a => new StaticSpaceObject(
                a.Id,
                a.TypeId,
                definitions[a.TypeId].GroupId,
                definitions[a.TypeId].CategoryId,
                1,
                new SpacePosition(a.X, a.Y, a.Z),
                a.Radius
            )
            {
                Solid = definitions[a.TypeId].GroupId != MiningDefinitions.GasCloudGroup,
                IsGlobal = false,
            })
            .ToArray();
    }
}
