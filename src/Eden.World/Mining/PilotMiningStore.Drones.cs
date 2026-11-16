using Eden.Data.World.Characters;
using Eden.Data.World.Mining;
using Eden.Data.World.Structures;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Structures;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;

namespace Eden.World.Mining;

internal sealed partial class PilotMiningStore
{
    internal static WorldAsteroid DroneResource(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long target,
        UtilityDroneProfile profile,
        int system,
        long now
    )
    {
        var rock = world.Set<WorldAsteroid>().Find(target);
        if (rock is null || rock.Quantity <= 0 || rock.SystemId != system)
        {
            throw new InvalidDataException("Choose an available resource in this system.");
        }
        var type = catalog.RequireItem(rock.TypeId);
        var family = profile.Kind == UtilityDroneKind.IceMining ? MiningFamily.Ice : MiningFamily.Ore;
        if (!MiningDefinitions.MatchesFamily(family, type.CategoryId, type.GroupId) || type.GroupId == MiningDefinitions.MercoxitGroup)
        {
            throw new InvalidDataException("The resource does not match this drone's ore or ice harvesting capability.");
        }
        if (MissionMiningRules.IsDeposit(target))
        {
            PilotMissionStore.RequireMiningAccess(world, target, pilotId, now);
        }
        if (
            MoonMiningRules.IsMoonRock(target)
            && (world.Set<SavedMoonExtraction>().Find(MoonMiningRules.ExtractionId(target))?.FieldExpiresAt ?? 0) <= now
        )
        {
            throw new InvalidDataException("The moon field is no longer available.");
        }
        if (MiningSiteRules.IsDeposit(target) && world.Set<SavedMiningDeposit>().Find(target) is { } deposit)
        {
            var site = world.Set<SavedMiningSite>().Find(deposit.SiteId);
            if (site is null || site.ReplaceAt is not null || site.ExpiresAt <= now)
            {
                throw new InvalidDataException("The resource site is no longer available.");
            }
        }
        return rock;
    }

    internal static MiningYield CollectDrone(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        long droneId,
        WorldAsteroid rock,
        UtilityDroneProfile profile,
        MiningCollection collection,
        long now
    )
    {
        var family = profile.Kind == UtilityDroneKind.IceMining ? MiningFamily.Ice : MiningFamily.Ore;
        var laser = new MiningLaser(
            droneId,
            0,
            profile.Range,
            profile.DurationMilliseconds,
            profile.Amount,
            0,
            0,
            0,
            MissionMiningRules.IsDeposit(rock.Id) ? 0 : profile.ResidueChance,
            profile.ResidueMultiplier,
            family
        );
        var validated = new ValidatedMining(pilot, rock, laser, PilotFittingStore.ShipAttributes(world, catalog, pilot));
        return CollectPaid(world, catalog, pilot.Id, droneId, rock.Id, collection, now, 1, validated, droneCargo: true);
    }
}
