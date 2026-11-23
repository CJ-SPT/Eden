using System.Text.Json;
using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Loot;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Information;
using Eden.Gameplay.Loot;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    private static void SeedCareerSalvage(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        SavedMission mission,
        MissionProfile profile,
        long now
    )
    {
        if (profile.Definition.SalvageObjectId is not long objectId)
        {
            return;
        }
        var contact = profile.Rooms.SelectMany(room => room.Objects).Single(obj => obj.Id == objectId);
        if (
            world is not EconomyTransaction
            || contact.GroupId != WreckDefinitions.Group
            || profile.CargoType <= 0
            || profile.CargoQuantity <= 0
        )
        {
            throw new InvalidDataException("Invalid career salvage objective.");
        }
        var position = new SpacePosition(mission.X, mission.Y, mission.Z) + contact.Position;
        // The wreck is already destroyed when the player arrives. Keep its source
        // out of the active encounter while preserving ordinary wreck custody.
        long source = world.AvailableNpcId(MissionSalvageRules.SourceId(mission.Id));
        world.Add(
            new WorldNpc
            {
                Id = source,
                TypeId = contact.TypeId,
                SystemId = mission.SystemId,
                CorporationId = pilot.CorporationId,
                AnchorKind = NpcAnchorKind.Mission,
                Role = NpcRole.Convoy,
                EncounterId = MissionRules.FirstSiteId + mission.Id,
                X = position.X,
                Y = position.Y,
                Z = position.Z,
                UpdatedAt = now,
                MotionAt = now,
                DestroyedAt = now,
                RetiredAt = now,
            }
        );
        world.Add(
            new WorldWreck
            {
                Id = MissionSalvageRules.WreckId(mission.Id),
                NpcId = source,
                OwnerId = mission.PilotId,
                CorporationId = pilot.CorporationId,
                TypeId = contact.TypeId,
                SystemId = mission.SystemId,
                Name = catalog.RequireItem(contact.TypeId).Name,
                X = position.X,
                Y = position.Y,
                Z = position.Z,
                CreatedAt = now,
                ExpiresAt = mission.ExpiresAt,
                SalvageJson = JsonSerializer.Serialize(new ScanItem[] { new(profile.CargoType, profile.CargoQuantity) }),
            }
        );
    }

    internal static SavedMission RequireCareerSalvage(WorldAction world, long pilotId, long wreckId, long now)
    {
        if (
            world is not EconomyTransaction
            || world.Set<SavedMission>().Find(MissionSalvageRules.MissionId(wreckId)) is not { State: MissionState.Accepted } mission
            || mission.PilotId != pilotId
            || mission.ExpiresAt <= now
            || !MissionCatalog.IsCareerSalvage(mission.MissionId)
        )
        {
            throw new InvalidDataException("This wreck is only available to the pilot assigned to its career mission.");
        }
        var guards = world.Filter<SavedMissionMember>(member => member.MissionId == mission.Id && member.Required).ToArray();
        if (guards.Length == 0 || guards.Any(guard => world.Set<WorldNpc>().Find(guard.NpcId)?.DestroyedAt is null))
        {
            throw new InvalidDataException("Defeat the guards before salvaging the transport wreck.");
        }
        return mission;
    }

    internal static void CloseCareerSalvage(WorldAction world, SavedMission mission)
    {
        if (
            !MissionCatalog.IsCareerSalvage(mission.MissionId)
            || world.Set<WorldWreck>().Find(MissionSalvageRules.WreckId(mission.Id)) is not { } wreck
        )
        {
            return;
        }
        world.Remove(wreck);
        if (wreck.NpcId is long npcId && world.Set<WorldNpc>().Find(npcId) is { } source)
        {
            world.Remove(source);
        }
    }
}
