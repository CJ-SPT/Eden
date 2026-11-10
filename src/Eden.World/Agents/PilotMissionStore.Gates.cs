using Eden.Data.World.Agents;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    public MissionGateActivation ActivateGate(long pilotId, long gateId, SpacePosition destination)
    {
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            // Resolve/expire the shared scene before opening the mutation transaction.
            // Validation below re-reads authoritative membership, hull, fit and position in that transaction.
            var gate = Gate(pilotId, gateId);
            using WorldDbContext world = new(database.Path);
            using var transaction = world.Database.BeginTransaction();
            using CatalogDbContext catalog = new(database.CatalogPath);
            if (gate.Destination != destination)
            {
                throw new InvalidDataException("The acceleration gate destination changed; activate it again.");
            }
            var pilot = ReadPilot(world, pilotId);
            ValidateGate(world, catalog, pilot, gate);
            long missionId = MissionId(gate);
            var result = MissionGatePayment.Apply(world, catalog, pilotId, pilot.ActiveShipId, missionId, gate, Now);
            world.SaveChanges();
            transaction.Commit();
            return result;
        }
    }

    public MissionGate Gate(long pilotId, long gateId)
    {
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            using WorldDbContext world = new(database.Path);
            using CatalogDbContext catalog = new(database.CatalogPath);
            var pilot = ReadPilot(world, pilotId);
            var position = pilot.ActiveShip.Position ?? throw new InvalidDataException("Undock before activating an acceleration gate.");
            int system = position.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId;
            var gate =
                Sites(system).SelectMany(s => s.Gates).SingleOrDefault(g => g.Id == gateId)
                ?? throw new InvalidDataException("This acceleration gate is unavailable in your solar system.");
            ValidateGate(world, catalog, pilot, gate);
            return gate;
        }
    }

    private static Eden.Data.World.Characters.Pilot ReadPilot(WorldDbContext world, long pilotId) =>
        world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);

    private static long MissionId(MissionGate gate) => (gate.Id - MissionRules.FirstObjectId) / MissionRules.NpcsPerMission;

    private void ValidateGate(WorldDbContext world, CatalogDbContext catalog, Eden.Data.World.Characters.Pilot pilot, MissionGate gate)
    {
        var position = pilot.ActiveShip.Position ?? throw new InvalidDataException("Undock before activating an acceleration gate.");
        int system = position.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId;
        var mission = world.Set<SavedMission>().Find(MissionId(gate));
        if (mission is null || mission.State != MissionState.Accepted || mission.SystemId != system || mission.ExpiresAt <= Now)
        {
            throw new InvalidDataException("This acceleration gate is no longer available.");
        }
        var type = catalog.RequireItem(pilot.ActiveShip.TypeId);
        bool cleared = true;
        if (gate.RequiresClear)
        {
            var profile = MissionCatalog.Find(catalog, mission.MissionId);
            var waves = Enumerable.Range(0, profile.Definition.Waves.Length).Where(w => profile.WaveRoom(w) == gate.SourceRoomId).ToArray();
            int last =
                world.Set<SavedMissionRoom>().Find(mission.Id, gate.SourceRoomId)?.Wave ?? (profile.Rooms.Length == 1 ? mission.Wave : -1);
            var required = world
                .Set<SavedMissionMember>()
                .Where(m => m.MissionId == mission.Id && m.Required)
                .ToArray()
                .Where(m => waves.Contains(m.Wave))
                .Select(m => m.NpcId)
                .ToArray();
            cleared =
                waves.All(w => w <= last)
                && !world.Set<WorldNpc>().Any(n => required.Contains(n.Id) && n.DestroyedAt == null && n.RetiredAt == null);
        }
        MissionGateRules.Validate(new(position.X, position.Y, position.Z), type.Radius ?? 0, type.GroupId, gate, cleared);
        var ship = new InventoryItem(
            pilot.ActiveShipId,
            type.Id,
            pilot.Id,
            pilot.ActiveShip.StationId,
            (int)InventoryFlag.Hangar,
            1,
            type.GroupId,
            type.CategoryId,
            0,
            true
        );
        var fitted = gate.Access.ForbiddenEquipment is null
            ? []
            : world
                .Set<Eden.Data.World.Inventory.OwnedItem>()
                .Where(i => i.LocationId == pilot.ActiveShipId && i.OwnerId == pilot.Id)
                .ToArray()
                .Where(i =>
                    InventoryDefinitions.IsFittedSlot(i.FlagId)
                    || i.FlagId is >= (int)InventoryFlag.SubsystemSlot0 and <= (int)InventoryFlag.SubsystemSlot3
                )
                .Select(i => PilotInventoryStore.Read(catalog, i))
                .ToArray();
        int race = checked((int)(catalog.Read(CatalogData.Client.Types, (long)type.Id)?.RaceID ?? 0));
        MissionGateAccessRules.ValidateShip(gate.Access, ship, race, fitted);
    }

    public SpacePosition WarpDestination(int systemId, SpacePosition origin, SpacePosition destination)
    {
        var sites = Sites(systemId).Where(s => s.Deadspace).ToArray();
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
