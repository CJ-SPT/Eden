using Eden.Data.World.Agents;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    public MissionGate Gate(long pilotId, long gateId)
    {
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        lock (database.MotionLock)
        {
            using WorldDbContext world = new(database.Path);
            using CatalogDbContext catalog = new(database.CatalogPath);
            var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
            var position = pilot.ActiveShip.Position ?? throw new InvalidDataException("Undock before activating an acceleration gate.");
            int system = position.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId;
            var site =
                Sites(system).SingleOrDefault(s => s.Gates.Any(g => g.Id == gateId))
                ?? throw new InvalidDataException("This acceleration gate is unavailable in your solar system.");
            var gate = site.Gates.Single(g => g.Id == gateId);
            var type =
                catalog.FittingType(pilot.ActiveShip.TypeId)
                ?? throw new InvalidDataException("This ship's catalog profile is unavailable.");
            bool cleared = true;
            if (gate.RequiresClear)
            {
                var mission = world.Set<SavedMission>().Find(site.Id)!;
                var profile = MissionCatalog.Find(catalog, mission.MissionId);
                var waves = Enumerable.Range(0, profile.Definition.Waves.Length).Where(w => profile.WaveRoom(w) == site.RoomId).ToArray();
                int last =
                    world.Set<SavedMissionRoom>().Find(site.Id, site.RoomId)?.Wave ?? (profile.Rooms.Length == 1 ? mission.Wave : -1);
                var required = world
                    .Set<SavedMissionMember>()
                    .Where(m => m.MissionId == site.Id && m.Required)
                    .ToArray()
                    .Where(m => waves.Contains(m.Wave))
                    .Select(m => m.NpcId)
                    .ToArray();
                cleared =
                    waves.All(w => w <= last)
                    && !world.Set<WorldNpc>().Any(n => required.Contains(n.Id) && n.DestroyedAt == null && n.RetiredAt == null);
            }
            MissionGateRules.Validate(new(position.X, position.Y, position.Z), type.Radius ?? 0, type.GroupId, gate, cleared);
            return gate;
        }
    }

    public SpacePosition WarpDestination(int systemId, SpacePosition origin, SpacePosition destination)
    {
        var sites = Sites(systemId);
        var target = sites.FirstOrDefault(s =>
            (s.Gates.Count > 0 || s.Position != s.Entrance) && s.Position.DistanceTo(destination) <= NpcEncounterDefinitions.VisibilityRange
        );
        if (target is null)
        {
            return destination;
        }
        if (sites.Any(s => s.Id == target.Id && s.Position.DistanceTo(origin) <= NpcEncounterDefinitions.VisibilityRange))
        {
            throw new InvalidDataException("Use an acceleration gate to travel inside this deadspace complex.");
        }
        return target.Entrance;
    }
}
