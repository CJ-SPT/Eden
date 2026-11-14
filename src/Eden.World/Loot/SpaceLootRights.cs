using Eden.Data.World.Characters;
using Eden.Data.World.Loot;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Loot;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Loot;

[Injectable]
internal sealed class SpaceLootRights(WorldDatabase database) : ISpaceLootRights
{
    internal static bool Entitled(WorldAction world, Pilot pilot, int system, LootRights rights) =>
        LootRightsRules.CanTake(
            pilot.Id,
            pilot.CorporationId,
            rights,
            LiveWorld.Find(world.WorldPath)?.Fleets?.CanShareLoot(pilot.Id, rights.OwnerId, system) == true
        );

    internal static bool CheckTake(WorldAction world, CatalogDbContext catalog, Pilot pilot, int system, LootRights rights) =>
        !Entitled(world, pilot, system, rights)
        && LootRightsRules.CheckTake(
            false,
            CrimewatchPersistence.Security(catalog, system),
            pilot.SafetyLevel,
            world.Set<Account>().Find(pilot.AccountId)!.CloneGrade == AccountCloneGrade.Alpha
        );

    internal static void RecordTheft(WorldAction world, Pilot pilot, int system, long now)
    {
        var state = CrimewatchPersistence.State(world, pilot.Id);
        state.SystemId = system;
        state.Apply(LootRightsRules.Theft(state.Timers(), now));
    }

    public void Abandon(long pilotId, IReadOnlyList<long> containerIds, long now)
    {
        if (
            containerIds.Count > LootRightsRules.MaximumAbandonSelection
            || containerIds.Any(id => id <= 0)
            || containerIds.Distinct().Count() != containerIds.Count
        )
        {
            throw new InvalidDataException("Choose distinct wrecks or cargo containers to abandon.");
        }
        using WorldActionScope action = new(
            database,
            new(
                pilots: [pilotId],
                resources: containerIds.Select(id => new SimulationResource(SharedSimulationResource.Wreck, id)).ToArray()
            )
        );
        var world = action.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Find(pilotId) ?? throw new InvalidDataException("Unknown pilot.");
        if (pilot.ActiveShip.Position is not { } position)
        {
            throw new InvalidDataException("Abandon loot while your ship is in space.");
        }
        world.RequireUncloaked(pilot.ActiveShipId);
        int system = position.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId;
        SpacePosition source = new(position.X, position.Y, position.Z);
        foreach (long id in containerIds)
        {
            if (JetcanRules.IsJetcan(id))
            {
                if (
                    world.Set<SavedJetcan>().Find(id) is { } can
                    && Eligible(can.OwnerId, can.SystemId, can.ExpiresAt, new(can.X, can.Y, can.Z))
                )
                {
                    can.Abandoned = true;
                }
            }
            else
            {
                if (
                    world.Set<WorldWreck>().Find(id) is { } wreck
                    && Eligible(
                        wreck.OwnerId,
                        wreck.SystemId,
                        wreck.ExpiresAt,
                        LiveWorld.Find(database.Path)?.Tractors.Position(id) ?? new(wreck.X, wreck.Y, wreck.Z)
                    )
                )
                {
                    wreck.Abandoned = true;
                }
            }
        }
        action.Commit();

        // Build 3662168's AbandonAllLoot selects HaveLootRight, which includes fleet/corporation wrecks.
        // Skip foreign or stale entries without granting permission to abandon somebody else's property.
        bool Eligible(long owner, int targetSystem, long expires, SpacePosition target) =>
            owner == pilotId && targetSystem == system && expires > now && source.DistanceTo(target) <= WreckDefinitions.VisibilityRange;
    }

    internal static void Upgrade(WorldDbContext world)
    {
        if (!world.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldWreck')").Contains("Abandoned"))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldWreck ADD COLUMN Abandoned INTEGER NOT NULL DEFAULT 0");
        }
        if (!world.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldJetcan')").Contains("Abandoned"))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldJetcan ADD COLUMN Abandoned INTEGER NOT NULL DEFAULT 0");
        }
    }
}
