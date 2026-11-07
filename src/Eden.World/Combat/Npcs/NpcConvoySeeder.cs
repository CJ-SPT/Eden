using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat.Npcs;

[Injectable(InjectionType.Singleton)]
internal sealed class NpcConvoySeeder(NpcConvoyCatalog profiles)
{
    internal void Seed(WorldDbContext world, CatalogDbContext catalog, int systemId, double security, long now)
    {
        if (!NpcConvoyRules.Eligible(security))
        {
            return;
        }
        var stations = catalog
            .Set<Station>()
            .Where(s => s.SolarSystemId == systemId)
            .OrderBy(s => s.Id)
            .ToArray()
            .Where(station => catalog.FittingType(station.TypeId) is not null)
            .ToArray();
        var obstacles = NpcGuardSeeder.CatalogObstacles(catalog, systemId);
        obstacles.AddRange(stations.Select(s => new NpcGuardObstacle(new(s.X, s.Y, s.Z), catalog.FittingType(s.TypeId)?.Radius ?? 0)));
        // Include newly seeded, tracked guard encounters as well as saved ones.
        _ = world.Set<WorldNpcEncounter>().Where(e => e.SystemId == systemId && e.Role == NpcRole.Navy).ToArray();
        obstacles.AddRange(
            world
                .Set<WorldNpcEncounter>()
                .Local.Where(e => e.SystemId == systemId && e.Role == NpcRole.Navy)
                .Select(e => new NpcGuardObstacle(
                    new(e.HomeX, e.HomeY, e.HomeZ),
                    NpcGuardRules.PatrolRadius + NpcGuardRules.FormationMargin
                ))
        );
        foreach (var station in stations)
        {
            var owner = catalog.Read(CatalogData.Client.Npccorporations, (long)station.OwnerId);
            if (
                owner?.FactionID is not long faction
                || NpcGuardRules.Roster(checked((int)faction)) is null
                || owner.RaceID is not long race
                || catalog.NpcCorporationName(station.OwnerId) is null
            )
            {
                continue;
            }
            var pool = profiles.Profiles.Where(p => p.RaceId == race).ToArray();
            if (pool.Length == 0)
            {
                continue;
            }
            long id = NpcConvoyRules.EncounterId(station.Id);
            var encounter = world.Set<WorldNpcEncounter>().Find(id);
            if (encounter is null)
            {
                encounter = new()
                {
                    Id = id,
                    SystemId = systemId,
                    AnchorId = station.Id,
                    AnchorKind = NpcAnchorKind.Station,
                    Role = NpcRole.Convoy,
                    FactionId = checked((int)faction),
                    Occupied = true,
                    Version = NpcSpawnRules.PopulationVersion,
                    NextSpawnAt = now,
                };
                world.Add(encounter);
            }
            if (encounter.NextSpawnAt is not long due || now < due)
            {
                continue;
            }
            var active = world.Set<WorldNpc>().Where(n => n.EncounterId == id && n.DestroyedAt == null && n.RetiredAt == null).ToArray();
            if (active.Length > 0)
            {
                foreach (var member in active)
                {
                    member.RetiredAt = now;
                }
                encounter.Generation = checked(encounter.Generation + 1);
                encounter.NextSpawnAt = checked(now + NpcConvoyRules.CooldownSeconds * TimeSpan.TicksPerSecond);
                continue;
            }
            var route = NpcConvoyRules.Route(
                new(station.X, station.Y, station.Z),
                catalog.FittingType(station.TypeId)?.Radius ?? 0,
                pool.Max(p => p.Radius),
                StationUndockCatalog.Read(catalog, station.TypeId),
                obstacles
            );
            if (route is null)
            {
                continue;
            }
            var start = NpcConvoyRules.Start(route, encounter.Generation);
            var destination = NpcConvoyRules.Destination(route, encounter.Generation);
            (encounter.HomeX, encounter.HomeY, encounter.HomeZ) = (route.Near.X, route.Near.Y, route.Near.Z);
            double duration = 0;
            for (int slot = 0; slot < NpcConvoyRules.Members; slot++)
            {
                uint seed = NpcSpawnRules.Seed(systemId, id + slot, encounter.Generation);
                var member = pool[(int)(seed % (uint)pool.Length)];
                var offset = NpcConvoyRules.FormationOffset(slot);
                var position = start + offset;
                var home = destination + offset;
                duration = Math.Max(duration, NpcConvoyRules.DurationSeconds(member.Mass, member.Attributes));
                world.Add(
                    new WorldNpc
                    {
                        EncounterId = id,
                        SpawnSlot = slot,
                        AnchorKind = NpcAnchorKind.Station,
                        SystemId = systemId,
                        TypeId = member.TypeId,
                        Role = NpcRole.Convoy,
                        FactionId = checked((int)faction),
                        CorporationId = station.OwnerId,
                        HomeX = home.X,
                        HomeY = home.Y,
                        HomeZ = home.Z,
                        X = position.X,
                        Y = position.Y,
                        Z = position.Z,
                        ShieldCharge = member.Attributes.GetValueOrDefault(DogmaAttributeId.ShieldCapacity),
                        UpdatedAt = now,
                        MotionAt = now,
                        Navigation = NpcNavigationState.Travelling,
                    }
                );
            }
            // While traffic is active this deadline ends its local route. Once
            // retired/killed, the same persisted field schedules the next group.
            encounter.NextSpawnAt = checked(now + (long)duration * TimeSpan.TicksPerSecond);
        }
    }
}
