using Eden.Data.Catalog;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Mining;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Fixtures;

// Explicit isolated encounter fixture. Production uses regional population rules.
internal sealed class TestNpcPopulationSeeder : INpcPopulationSeeder
{
    public long NextUpdate(WorldDbContext world, int systemId, long now) => NpcPopulationSchedule.Next(world, systemId, now);

    public void Seed(WorldDbContext world, CatalogDbContext catalog, int systemId, long now)
    {
        if (systemId != NpcEncounterDefinitions.PerimeterSystem)
        {
            return;
        }
        var belts = catalog
            .Set<Celestial>()
            .Where(c => c.SolarSystemId == systemId && c.GroupId == AsteroidDefinitions.BeltGroup)
            .OrderBy(c => c.Id)
            .ToArray();
        if (belts.Length == 0)
        {
            return;
        }
        var type = catalog.Set<ItemType>().Include(t => t.Group).Single(t => t.Id == NpcEncounterDefinitions.GuristasRookieType);
        var attributes = catalog
            .Set<TypeAttribute>()
            .Where(a => a.ItemTypeId == type.Id)
            .ToDictionary(a => (DogmaAttributeId)a.AttributeId, a => a.Value);
        var defense = ShipHealth.DefenseFrom(attributes);
        foreach (var belt in belts)
        {
            // Keep defeated rows for bounty identity. A replacement gets a new
            // AUTOINCREMENT ID only after the local ten-minute respawn delay.
            var previous = world.Set<WorldNpc>().Where(n => n.BeltId == belt.Id).OrderByDescending(n => n.Id).FirstOrDefault();
            if (
                previous is not null
                && (
                    !previous.DestroyedAt.HasValue
                    || now - previous.DestroyedAt.Value < TimeSpan.TicksPerSecond * NpcEncounterDefinitions.RespawnSeconds
                )
            )
            {
                continue;
            }
            world
                .Set<WorldNpc>()
                .Add(
                    new()
                    {
                        Id = previous is null ? checked(NpcEncounterDefinitions.FirstNpcId + belt.Id) : 0,
                        BeltId = belt.Id,
                        SystemId = systemId,
                        TypeId = type.Id,
                        CorporationId = NpcEncounterDefinitions.GuristasCorporation,
                        FactionId = NpcEncounterDefinitions.GuristasFaction,
                        EncounterId = belt.Id,
                        HomeX = belt.X + NpcEncounterDefinitions.AnchorOffset,
                        HomeY = belt.Y,
                        HomeZ = belt.Z,
                        X = belt.X + NpcEncounterDefinitions.AnchorOffset,
                        Y = belt.Y,
                        Z = belt.Z,
                        ShieldCharge = defense.Shield,
                        UpdatedAt = now,
                    }
                );
        }
    }
}
