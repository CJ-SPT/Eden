using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private void RegisterBotTables()
    {
        Register(
            new EconomyTable<Eden.Data.World.Agents.SavedMission, EconomySavedMission>(
                "WorldMission",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.AcceptedAt,
                        row.AgentId,
                        row.ClosedAt,
                        row.CorporationFraction,
                        row.DestinationStation,
                        row.ExpiresAt,
                        row.FactionFraction,
                        row.IskCents,
                        row.Level,
                        row.LoyaltyPoints,
                        row.MissionId,
                        row.ObjectiveComplete,
                        row.OfferedAt,
                        row.PilotId,
                        row.State,
                        row.Storyline,
                        row.SystemId,
                        row.Wave,
                        row.X,
                        row.Y,
                        row.Z
                    ),
                state => new Eden.Data.World.Agents.SavedMission
                {
                    Id = state.Id,
                    AcceptedAt = state.AcceptedAt,
                    AgentId = state.AgentId,
                    ClosedAt = state.ClosedAt,
                    CorporationFraction = state.CorporationFraction,
                    DestinationStation = state.DestinationStation,
                    ExpiresAt = state.ExpiresAt,
                    FactionFraction = state.FactionFraction,
                    IskCents = state.IskCents,
                    Level = state.Level,
                    LoyaltyPoints = state.LoyaltyPoints,
                    MissionId = state.MissionId,
                    ObjectiveComplete = state.ObjectiveComplete,
                    OfferedAt = state.OfferedAt,
                    PilotId = state.PilotId,
                    State = state.State,
                    Storyline = state.Storyline,
                    SystemId = state.SystemId,
                    Wave = state.Wave,
                    X = state.X,
                    Y = state.Y,
                    Z = state.Z,
                },
                ProjectSavedMission,
                "Id,AcceptedAt,AgentId,ClosedAt,CorporationFraction,DestinationStation,ExpiresAt,FactionFraction,"
                    + "IskCents,Level,LoyaltyPoints,MissionId,ObjectiveComplete,OfferedAt,PilotId,State,Storyline,SystemId,"
                    + "Wave,X,Y,Z",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.IsDBNull(2) ? null : reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.IsDBNull(4) ? null : reader.GetInt64(4),
                        reader.GetDouble(5),
                        reader.GetInt32(6),
                        reader.GetInt64(7),
                        reader.GetDouble(8),
                        reader.GetInt64(9),
                        reader.GetInt32(10),
                        reader.GetInt64(11),
                        reader.GetInt32(12),
                        reader.GetBoolean(13),
                        reader.GetInt64(14),
                        reader.GetInt64(15),
                        (Eden.Gameplay.Agents.MissionState)reader.GetInt32(16),
                        reader.GetBoolean(17),
                        reader.GetInt32(18),
                        reader.GetInt32(19),
                        reader.GetDouble(20),
                        reader.GetDouble(21),
                        reader.GetDouble(22)
                    ),
                state => new(state.Id),
                indexes:
                [
                    new("Id", row => row.Id),
                    new("AgentId", row => row.AgentId),
                    new("MissionId", row => row.MissionId),
                    new("PilotId", row => row.PilotId),
                    new("SystemId", row => row.SystemId),
                ],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Agents.SavedMissionMember, EconomySavedMissionMember>(
                "WorldMissionMember",
                row => new(row.NpcId),
                row =>
                    new(
                        row.NpcId,
                        row.CompletesMission,
                        row.LootQuantity,
                        row.LootTypeId,
                        row.MissionId,
                        row.Required,
                        row.Trigger,
                        row.Wave
                    ),
                state => new Eden.Data.World.Agents.SavedMissionMember
                {
                    NpcId = state.NpcId,
                    CompletesMission = state.CompletesMission,
                    LootQuantity = state.LootQuantity,
                    LootTypeId = state.LootTypeId,
                    MissionId = state.MissionId,
                    Required = state.Required,
                    Trigger = state.Trigger,
                    Wave = state.Wave,
                },
                ProjectSavedMissionMember,
                "NpcId,CompletesMission,LootQuantity,LootTypeId,MissionId,Required,Trigger,Wave",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetBoolean(2),
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        reader.GetInt64(5),
                        reader.GetBoolean(6),
                        reader.GetBoolean(7),
                        reader.GetInt32(8)
                    ),
                state => new(state.NpcId),
                indexes: [new("NpcId", row => row.NpcId), new("LootTypeId", row => row.LootTypeId), new("MissionId", row => row.MissionId)],
                primaryColumn: "NpcId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Agents.SavedMissionRoom, EconomySavedMissionRoom>(
                "WorldMissionRoom",
                row => new(row.MissionId, row.RoomId),
                row => new(row.MissionId, row.RoomId, row.Wave),
                state => new Eden.Data.World.Agents.SavedMissionRoom
                {
                    MissionId = state.MissionId,
                    RoomId = state.RoomId,
                    Wave = state.Wave,
                },
                ProjectSavedMissionRoom,
                "MissionId,RoomId,Wave",
                reader => new(reader.GetInt64(1), reader.GetInt32(2), reader.GetInt32(3)),
                state => new(state.MissionId, state.RoomId),
                indexes: [new("MissionId", row => row.MissionId), new("RoomId", row => row.RoomId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Agents.SavedMissionStructure, EconomySavedMissionStructure>(
                "WorldMissionStructure",
                row => new(row.NpcId),
                row => new(row.NpcId, row.CompletesMission, row.DestroyedAt, row.LootEmpty, row.MissionId, row.ObjectId, row.Required),
                state => new Eden.Data.World.Agents.SavedMissionStructure
                {
                    NpcId = state.NpcId,
                    CompletesMission = state.CompletesMission,
                    DestroyedAt = state.DestroyedAt,
                    LootEmpty = state.LootEmpty,
                    MissionId = state.MissionId,
                    ObjectId = state.ObjectId,
                    Required = state.Required,
                },
                ProjectSavedMissionStructure,
                "NpcId,CompletesMission,DestroyedAt,LootEmpty,MissionId,ObjectId,Required",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetBoolean(2),
                        reader.IsDBNull(3) ? null : reader.GetInt64(3),
                        reader.GetBoolean(4),
                        reader.GetInt64(5),
                        reader.GetInt64(6),
                        reader.GetBoolean(7)
                    ),
                state => new(state.NpcId),
                indexes: [new("NpcId", row => row.NpcId), new("MissionId", row => row.MissionId), new("ObjectId", row => row.ObjectId)],
                primaryColumn: "NpcId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Capsuleers.SavedCapsuleerAdmission, EconomySavedCapsuleerAdmission>(
                "WorldCapsuleerAdmission",
                row => new(row.PilotId),
                row => new(row.PilotId),
                state => new Eden.Data.World.Capsuleers.SavedCapsuleerAdmission { PilotId = state.PilotId },
                ProjectSavedCapsuleerAdmission,
                "PilotId",
                reader => new(reader.GetInt64(1)),
                state => new(state.PilotId),
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: "PilotId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Capsuleers.SavedCapsuleerAgent, EconomySavedCapsuleerAgent>(
                "WorldCapsuleerAgent",
                row => new(row.PilotId),
                row =>
                    new(
                        row.PilotId,
                        row.Career,
                        row.CommunitySystemId,
                        row.FounderModuleId,
                        row.FounderShipId,
                        row.Slot,
                        row.StartingCents
                    ),
                state => new Eden.Data.World.Capsuleers.SavedCapsuleerAgent
                {
                    PilotId = state.PilotId,
                    Career = state.Career,
                    CommunitySystemId = state.CommunitySystemId,
                    FounderModuleId = state.FounderModuleId,
                    FounderShipId = state.FounderShipId,
                    Slot = state.Slot,
                    StartingCents = state.StartingCents,
                },
                ProjectSavedCapsuleerAgent,
                "PilotId,Career,CommunitySystemId,FounderModuleId,FounderShipId,Slot,StartingCents",
                reader =>
                    new(
                        reader.GetInt64(1),
                        (Eden.Gameplay.Capsuleers.CapsuleerCareer)reader.GetInt32(2),
                        reader.GetInt32(3),
                        reader.GetInt64(4),
                        reader.GetInt64(5),
                        reader.GetInt32(6),
                        reader.GetInt64(7)
                    ),
                state => new(state.PilotId),
                indexes:
                [
                    new("PilotId", row => row.PilotId),
                    new("Career", row => row.Career),
                    new("CommunitySystemId", row => row.CommunitySystemId),
                    new("FounderModuleId", row => row.FounderModuleId),
                    new("FounderShipId", row => row.FounderShipId),
                    new("Slot", row => row.Slot),
                ],
                primaryColumn: "PilotId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Capsuleers.SavedCapsuleerClock, EconomySavedCapsuleerClock>(
                "WorldCapsuleerClock",
                row => new(row.Id),
                row => new(row.Id, row.ElapsedTicks),
                state => new Eden.Data.World.Capsuleers.SavedCapsuleerClock { Id = state.Id, ElapsedTicks = state.ElapsedTicks },
                ProjectSavedCapsuleerClock,
                "Id,ElapsedTicks",
                reader => new(reader.GetInt32(1), reader.GetInt64(2)),
                state => new(state.Id),
                indexes: [new("Id", row => row.Id)],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Capsuleers.SavedCapsuleerCoarsePilot, EconomySavedCapsuleerCoarsePilot>(
                "WorldCapsuleerCoarsePilot",
                row => new(row.PilotId),
                row => new(row.PilotId, row.SettledTicks),
                state => new Eden.Data.World.Capsuleers.SavedCapsuleerCoarsePilot
                {
                    PilotId = state.PilotId,
                    SettledTicks = state.SettledTicks,
                },
                ProjectSavedCapsuleerCoarsePilot,
                "PilotId,SettledTicks",
                reader => new(reader.GetInt64(1), reader.GetInt64(2)),
                state => new(state.PilotId),
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: "PilotId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Capsuleers.SavedCapsuleerCoarseWork, EconomySavedCapsuleerCoarseWork>(
                "WorldCapsuleerCoarseWork",
                row => new(row.SystemId),
                row => new(row.SystemId, row.Cursor, row.Generation, row.NextTicks, row.SettledTicks),
                state => new Eden.Data.World.Capsuleers.SavedCapsuleerCoarseWork
                {
                    SystemId = state.SystemId,
                    Cursor = state.Cursor,
                    Generation = state.Generation,
                    NextTicks = state.NextTicks,
                    SettledTicks = state.SettledTicks,
                },
                ProjectSavedCapsuleerCoarseWork,
                "SystemId,Cursor,Generation,NextTicks,SettledTicks",
                reader => new(reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5)),
                state => new(state.SystemId),
                indexes: [new("SystemId", row => row.SystemId)],
                primaryColumn: "SystemId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Capsuleers.SavedCapsuleerCommunity, EconomySavedCapsuleerCommunity>(
                "WorldCapsuleerCommunity",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.Career,
                        row.FounderModuleId,
                        row.FounderShipId,
                        row.HomeStationId,
                        row.PilotId,
                        row.Progress,
                        row.SchoolId,
                        row.Seed,
                        row.StartingCents
                    ),
                state => new Eden.Data.World.Capsuleers.SavedCapsuleerCommunity
                {
                    Id = state.Id,
                    Career = state.Career,
                    FounderModuleId = state.FounderModuleId,
                    FounderShipId = state.FounderShipId,
                    HomeStationId = state.HomeStationId,
                    PilotId = state.PilotId,
                    Progress = state.Progress,
                    SchoolId = state.SchoolId,
                    Seed = state.Seed,
                    StartingCents = state.StartingCents,
                },
                ProjectSavedCapsuleerCommunity,
                "Id,Career,FounderModuleId,FounderShipId,HomeStationId,PilotId,Progress,SchoolId,Seed,StartingCents",
                reader =>
                    new(
                        reader.GetInt32(1),
                        (Eden.Gameplay.Capsuleers.CapsuleerCareer)reader.GetInt32(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetInt32(5),
                        reader.IsDBNull(6) ? null : reader.GetInt64(6),
                        reader.IsDBNull(7) ? null : reader.GetString(7),
                        reader.GetInt32(8),
                        reader.GetInt32(9),
                        reader.GetInt64(10)
                    ),
                state => new(state.Id),
                indexes:
                [
                    new("Id", row => row.Id),
                    new("FounderModuleId", row => row.FounderModuleId),
                    new("FounderShipId", row => row.FounderShipId),
                    new("HomeStationId", row => row.HomeStationId),
                    new("PilotId", row => row.PilotId),
                    new("SchoolId", row => row.SchoolId),
                ],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Capsuleers.SavedCapsuleerHauler, EconomySavedCapsuleerHauler>(
                "WorldCapsuleerHauler",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.DestinationStationId,
                        row.FounderShipId,
                        row.PilotId,
                        row.Progress,
                        row.RouteSystems?.ToArray(),
                        row.SourceStationId,
                        row.StartingCents
                    ),
                state => new Eden.Data.World.Capsuleers.SavedCapsuleerHauler
                {
                    Id = state.Id,
                    DestinationStationId = state.DestinationStationId,
                    FounderShipId = state.FounderShipId,
                    PilotId = state.PilotId,
                    Progress = state.Progress,
                    RouteSystems = state.RouteSystems?.ToArray(),
                    SourceStationId = state.SourceStationId,
                    StartingCents = state.StartingCents,
                },
                ProjectSavedCapsuleerHauler,
                "Id,DestinationStationId,FounderShipId,PilotId,Progress,RouteSystems,SourceStationId,StartingCents",
                reader =>
                    new(
                        reader.GetInt32(1),
                        reader.GetInt32(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.IsDBNull(6) ? null : JsonSerializer.Deserialize<System.Int32[]>(reader.GetString(6))!,
                        reader.GetInt32(7),
                        reader.GetInt64(8)
                    ),
                state => new(state.Id),
                equal: (left, right) =>
                    left with { RouteSystems = null } == right with { RouteSystems = null }
                    && (
                        left.RouteSystems is null
                            ? right.RouteSystems is null
                            : right.RouteSystems is not null && left.RouteSystems.SequenceEqual(right.RouteSystems)
                    ),
                indexes:
                [
                    new("Id", row => row.Id),
                    new("DestinationStationId", row => row.DestinationStationId),
                    new("FounderShipId", row => row.FounderShipId),
                    new("PilotId", row => row.PilotId),
                    new("SourceStationId", row => row.SourceStationId),
                ],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Capsuleers.SavedCapsuleerJob, EconomySavedCapsuleerJob>(
                "WorldCapsuleerJob",
                row => new(row.PilotId),
                row => new(row.PilotId, row.Action, row.DueTicks, row.Revision, row.Sequence),
                state => new Eden.Data.World.Capsuleers.SavedCapsuleerJob
                {
                    PilotId = state.PilotId,
                    Action = state.Action,
                    DueTicks = state.DueTicks,
                    Revision = state.Revision,
                    Sequence = state.Sequence,
                },
                ProjectSavedCapsuleerJob,
                "PilotId,Action,DueTicks,Revision,Sequence",
                reader =>
                    new(
                        reader.GetInt64(1),
                        (Eden.Gameplay.Capsuleers.CapsuleerAction)reader.GetInt32(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetInt64(5)
                    ),
                state => new(state.PilotId),
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: "PilotId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Capsuleers.SavedCapsuleerPopulation, EconomySavedCapsuleerPopulation>(
                "WorldCapsuleerPopulation",
                row => new(row.SystemId),
                row => new(row.SystemId, row.HomeStationId, row.Seed, row.Version),
                state => new Eden.Data.World.Capsuleers.SavedCapsuleerPopulation
                {
                    SystemId = state.SystemId,
                    HomeStationId = state.HomeStationId,
                    Seed = state.Seed,
                    Version = state.Version,
                },
                ProjectSavedCapsuleerPopulation,
                "SystemId,HomeStationId,Seed,Version",
                reader => new(reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt32(4)),
                state => new(state.SystemId),
                indexes: [new("SystemId", row => row.SystemId), new("HomeStationId", row => row.HomeStationId)],
                primaryColumn: "SystemId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Capsuleers.SavedCapsuleerProvisioning, EconomySavedCapsuleerProvisioning>(
                "WorldCapsuleerProvisioning",
                row => new(row.Id),
                row => new(row.Id, row.WorldSeed),
                state => new Eden.Data.World.Capsuleers.SavedCapsuleerProvisioning { Id = state.Id, WorldSeed = state.WorldSeed },
                ProjectSavedCapsuleerProvisioning,
                "Id,WorldSeed",
                reader => new(reader.GetInt32(1), reader.GetInt64(2)),
                state => new(state.Id),
                indexes: [new("Id", row => row.Id)],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Capsuleers.SavedCapsuleerSimulation, EconomySavedCapsuleerSimulation>(
                "WorldCapsuleerSimulation",
                row => new(row.SystemId),
                row => new(row.SystemId, row.StartedTicks),
                state => new Eden.Data.World.Capsuleers.SavedCapsuleerSimulation
                {
                    SystemId = state.SystemId,
                    StartedTicks = state.StartedTicks,
                },
                ProjectSavedCapsuleerSimulation,
                "SystemId,StartedTicks",
                reader => new(reader.GetInt32(1), reader.GetInt64(2)),
                state => new(state.SystemId),
                indexes: [new("SystemId", row => row.SystemId)],
                primaryColumn: "SystemId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Capsuleers.SavedCapsuleerSupplies, EconomySavedCapsuleerSupplies>(
                "WorldCapsuleerSupplies",
                row => new(row.PilotId),
                row => new(row.PilotId, row.ShipId),
                state => new Eden.Data.World.Capsuleers.SavedCapsuleerSupplies { PilotId = state.PilotId, ShipId = state.ShipId },
                ProjectSavedCapsuleerSupplies,
                "PilotId,ShipId",
                reader => new(reader.GetInt64(1), reader.GetInt64(2)),
                state => new(state.PilotId),
                indexes: [new("PilotId", row => row.PilotId), new("ShipId", row => row.ShipId)],
                primaryColumn: "PilotId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Capsuleers.SavedCapsuleerSuspension, EconomySavedCapsuleerSuspension>(
                "WorldCapsuleerSuspension",
                row => new(row.PilotId),
                row => new(row.PilotId, row.StartedTicks),
                state => new Eden.Data.World.Capsuleers.SavedCapsuleerSuspension
                {
                    PilotId = state.PilotId,
                    StartedTicks = state.StartedTicks,
                },
                ProjectSavedCapsuleerSuspension,
                "PilotId,StartedTicks",
                reader => new(reader.GetInt64(1), reader.GetInt64(2)),
                state => new(state.PilotId),
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: "PilotId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Capsuleers.SavedCapsuleerSystem, EconomySavedCapsuleerSystem>(
                "WorldCapsuleerSystem",
                row => new(row.SystemId),
                row =>
                    new(
                        row.SystemId,
                        row.CatalogFingerprint,
                        row.DestinationStationId,
                        row.Eligibility,
                        row.HomeStationId,
                        row.PolicyVersion
                    ),
                state => new Eden.Data.World.Capsuleers.SavedCapsuleerSystem
                {
                    SystemId = state.SystemId,
                    CatalogFingerprint = state.CatalogFingerprint,
                    DestinationStationId = state.DestinationStationId,
                    Eligibility = state.Eligibility,
                    HomeStationId = state.HomeStationId,
                    PolicyVersion = state.PolicyVersion,
                },
                ProjectSavedCapsuleerSystem,
                "SystemId,CatalogFingerprint,DestinationStationId,Eligibility,HomeStationId,PolicyVersion",
                reader =>
                    new(
                        reader.GetInt32(1),
                        reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetInt32(3),
                        (Eden.Gameplay.Capsuleers.CapsuleerEligibility)reader.GetInt32(4),
                        reader.IsDBNull(5) ? null : reader.GetInt32(5),
                        reader.GetInt32(6)
                    ),
                state => new(state.SystemId),
                indexes:
                [
                    new("SystemId", row => row.SystemId),
                    new("DestinationStationId", row => row.DestinationStationId),
                    new("HomeStationId", row => row.HomeStationId),
                ],
                primaryColumn: "SystemId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Capsuleers.SavedTrafficCorrection, EconomySavedTrafficCorrection>(
                "WorldTrafficCorrection",
                row => new(row.PilotId),
                row => new(row.PilotId, row.OriginalHullTypeId, row.ShipId, row.TemplateKillId),
                state => new Eden.Data.World.Capsuleers.SavedTrafficCorrection
                {
                    PilotId = state.PilotId,
                    OriginalHullTypeId = state.OriginalHullTypeId,
                    ShipId = state.ShipId,
                    TemplateKillId = state.TemplateKillId,
                },
                ProjectSavedTrafficCorrection,
                "PilotId,OriginalHullTypeId,ShipId,TemplateKillId",
                reader => new(reader.GetInt64(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4)),
                state => new(state.PilotId),
                indexes:
                [
                    new("PilotId", row => row.PilotId),
                    new("OriginalHullTypeId", row => row.OriginalHullTypeId),
                    new("ShipId", row => row.ShipId),
                    new("TemplateKillId", row => row.TemplateKillId),
                ],
                primaryColumn: "PilotId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Capsuleers.SavedTrafficIdentity, EconomySavedTrafficIdentity>(
                "WorldTrafficIdentity",
                row => new(row.PilotId),
                row => new(row.PilotId, row.CharacterId, row.KillId, row.SnapshotId),
                state => new Eden.Data.World.Capsuleers.SavedTrafficIdentity
                {
                    PilotId = state.PilotId,
                    CharacterId = state.CharacterId,
                    KillId = state.KillId,
                    SnapshotId = state.SnapshotId,
                },
                ProjectSavedTrafficIdentity,
                "PilotId,CharacterId,KillId,SnapshotId",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4)),
                state => new(state.PilotId),
                indexes:
                [
                    new("PilotId", row => row.PilotId),
                    new("CharacterId", row => row.CharacterId),
                    new("KillId", row => row.KillId),
                    new("SnapshotId", row => row.SnapshotId),
                ],
                primaryColumn: "PilotId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Characters.SavedCharacterDeletion, EconomySavedCharacterDeletion>(
                "WorldCharacterDeletion",
                row => new(row.PilotId),
                row => new(row.PilotId, row.DeletedAt, row.ReadyAt),
                state => new Eden.Data.World.Characters.SavedCharacterDeletion
                {
                    PilotId = state.PilotId,
                    DeletedAt = state.DeletedAt,
                    ReadyAt = state.ReadyAt,
                },
                ProjectSavedCharacterDeletion,
                "PilotId,DeletedAt,ReadyAt",
                reader => new(reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.GetInt64(3)),
                state => new(state.PilotId),
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: "PilotId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Characters.SavedCombatDamage, EconomySavedCombatDamage>(
                "WorldCombatDamage",
                row => new(row.VictimId, row.AttackerId),
                row => new(row.VictimId, row.AttackerId, row.Damage, row.PartyJson),
                state => new Eden.Data.World.Characters.SavedCombatDamage
                {
                    VictimId = state.VictimId,
                    AttackerId = state.AttackerId,
                    Damage = state.Damage,
                    PartyJson = state.PartyJson,
                },
                ProjectSavedCombatDamage,
                "VictimId,AttackerId,Damage,PartyJson",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetDouble(3), reader.GetString(4)),
                state => new(state.VictimId, state.AttackerId),
                indexes: [new("VictimId", row => row.VictimId), new("AttackerId", row => row.AttackerId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Characters.SavedCombatReport, EconomySavedCombatReport>(
                "WorldCombatReport",
                row => new(row.Id),
                row => new(row.Id, row.Data, row.IsLoss, row.PilotId, row.SourceKey, row.Time),
                state => new Eden.Data.World.Characters.SavedCombatReport
                {
                    Id = state.Id,
                    Data = state.Data,
                    IsLoss = state.IsLoss,
                    PilotId = state.PilotId,
                    SourceKey = state.SourceKey,
                    Time = state.Time,
                },
                ProjectSavedCombatReport,
                "Id,Data,IsLoss,PilotId,SourceKey,Time",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetString(2),
                        reader.GetBoolean(3),
                        reader.GetInt64(4),
                        reader.GetString(5),
                        reader.GetInt64(6)
                    ),
                state => new(state.Id),
                indexes: [new("Id", row => row.Id), new("PilotId", row => row.PilotId), new("SourceKey", row => row.SourceKey)],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Characters.SavedHomeStation, EconomySavedHomeStation>(
                "WorldHomeStation",
                row => new(row.PilotId),
                row => new(row.PilotId, row.StationId),
                state => new Eden.Data.World.Characters.SavedHomeStation { PilotId = state.PilotId, StationId = state.StationId },
                ProjectSavedHomeStation,
                "PilotId,StationId",
                reader => new(reader.GetInt64(1), reader.GetInt32(2)),
                state => new(state.PilotId),
                indexes: [new("PilotId", row => row.PilotId), new("StationId", row => row.StationId)],
                primaryColumn: "PilotId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Characters.SavedStandingTransaction, EconomySavedStandingTransaction>(
                "WorldStandingTransaction",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.After,
                        row.Before,
                        row.Event,
                        row.PilotId,
                        row.SourceId,
                        row.SystemId,
                        row.Time,
                        row.VictimCorporationId,
                        row.VictimTypeId
                    ),
                state => new Eden.Data.World.Characters.SavedStandingTransaction
                {
                    Id = state.Id,
                    After = state.After,
                    Before = state.Before,
                    Event = state.Event,
                    PilotId = state.PilotId,
                    SourceId = state.SourceId,
                    SystemId = state.SystemId,
                    Time = state.Time,
                    VictimCorporationId = state.VictimCorporationId,
                    VictimTypeId = state.VictimTypeId,
                },
                ProjectSavedStandingTransaction,
                "Id,After,Before,Event,PilotId,SourceId,SystemId,Time,VictimCorporationId,VictimTypeId",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetDouble(2),
                        reader.GetDouble(3),
                        (Eden.Gameplay.Characters.StandingEvent)reader.GetInt32(4),
                        reader.GetInt64(5),
                        reader.GetInt64(6),
                        reader.GetInt32(7),
                        reader.GetInt64(8),
                        reader.GetInt32(9),
                        reader.GetInt32(10)
                    ),
                state => new(state.Id),
                indexes:
                [
                    new("Id", row => row.Id),
                    new("PilotId", row => row.PilotId),
                    new("SourceId", row => row.SourceId),
                    new("SystemId", row => row.SystemId),
                    new("VictimCorporationId", row => row.VictimCorporationId),
                    new("VictimTypeId", row => row.VictimTypeId),
                ],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Combat.ModuleCooldown, EconomyModuleCooldown>(
                "WorldWeaponCooldown",
                row => new(row.ModuleId),
                row => new(row.ModuleId, row.Kind, row.ReadyAt),
                state => new Eden.Data.World.Combat.ModuleCooldown
                {
                    ModuleId = state.ModuleId,
                    Kind = state.Kind,
                    ReadyAt = state.ReadyAt,
                },
                ProjectModuleCooldown,
                "ModuleId,Kind,ReadyAt",
                reader => new(reader.GetInt64(1), (Eden.Data.World.Combat.ModuleCooldownKind)reader.GetInt32(2), reader.GetInt64(3)),
                state => new(state.ModuleId),
                indexes: [new("ModuleId", row => row.ModuleId), new("Kind", row => row.Kind)],
                primaryColumn: "ModuleId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Combat.NpcAggression, EconomyNpcAggression>(
                "WorldNpcAggression",
                row => new(row.NpcId),
                row => new(row.NpcId, row.LastShotAt, row.LastTargetId, row.MissileReadyAt, row.ReadyAt, row.TargetShipId),
                state => new Eden.Data.World.Combat.NpcAggression
                {
                    NpcId = state.NpcId,
                    LastShotAt = state.LastShotAt,
                    LastTargetId = state.LastTargetId,
                    MissileReadyAt = state.MissileReadyAt,
                    ReadyAt = state.ReadyAt,
                    TargetShipId = state.TargetShipId,
                },
                ProjectNpcAggression,
                "NpcId,LastShotAt,LastTargetId,MissileReadyAt,ReadyAt,TargetShipId",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.IsDBNull(2) ? null : reader.GetInt64(2),
                        reader.IsDBNull(3) ? null : reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetInt64(5),
                        reader.GetInt64(6)
                    ),
                state => new(state.NpcId),
                indexes:
                [
                    new("NpcId", row => row.NpcId),
                    new("LastTargetId", row => row.LastTargetId),
                    new("TargetShipId", row => row.TargetShipId),
                ],
                primaryColumn: "NpcId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Combat.NpcBounty, EconomyNpcBounty>(
                "WorldNpcBounty",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.AmountCents,
                        row.BalanceCents,
                        row.NpcId,
                        row.PilotId,
                        row.SystemId,
                        row.TaxCents,
                        row.Time,
                        row.TypeId
                    ),
                state => new Eden.Data.World.Combat.NpcBounty
                {
                    Id = state.Id,
                    AmountCents = state.AmountCents,
                    BalanceCents = state.BalanceCents,
                    NpcId = state.NpcId,
                    PilotId = state.PilotId,
                    SystemId = state.SystemId,
                    TaxCents = state.TaxCents,
                    Time = state.Time,
                    TypeId = state.TypeId,
                },
                ProjectNpcBounty,
                "Id,AmountCents,BalanceCents,NpcId,PilotId,SystemId,TaxCents,Time,TypeId",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetInt64(5),
                        reader.GetInt32(6),
                        reader.GetInt64(7),
                        reader.GetInt64(8),
                        reader.GetInt32(9)
                    ),
                state => new(state.Id),
                indexes:
                [
                    new("Id", row => row.Id),
                    new("NpcId", row => row.NpcId),
                    new("PilotId", row => row.PilotId),
                    new("SystemId", row => row.SystemId),
                    new("TypeId", row => row.TypeId),
                ],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Combat.Npcs.WorldNpc, EconomyWorldNpc>(
                "WorldNpc",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.AcquisitionRange,
                        row.AnchorKind,
                        row.ArmorDamage,
                        row.ArmorRepairAt,
                        row.BeltId,
                        row.CorporationId,
                        row.DestroyedAt,
                        row.EncounterId,
                        row.FactionId,
                        row.HomeX,
                        row.HomeY,
                        row.HomeZ,
                        row.MotionAt,
                        row.Name,
                        row.Navigation,
                        row.RepairRevision,
                        row.RetiredAt,
                        row.Role,
                        row.ShieldCharge,
                        row.ShieldRepairAt,
                        row.SpawnSlot,
                        row.StructureDamage,
                        row.SystemId,
                        row.TypeId,
                        row.UpdatedAt,
                        row.VelocityX,
                        row.VelocityY,
                        row.VelocityZ,
                        row.X,
                        row.Y,
                        row.Z
                    ),
                state => new Eden.Data.World.Combat.Npcs.WorldNpc
                {
                    Id = state.Id,
                    AcquisitionRange = state.AcquisitionRange,
                    AnchorKind = state.AnchorKind,
                    ArmorDamage = state.ArmorDamage,
                    ArmorRepairAt = state.ArmorRepairAt,
                    BeltId = state.BeltId,
                    CorporationId = state.CorporationId,
                    DestroyedAt = state.DestroyedAt,
                    EncounterId = state.EncounterId,
                    FactionId = state.FactionId,
                    HomeX = state.HomeX,
                    HomeY = state.HomeY,
                    HomeZ = state.HomeZ,
                    MotionAt = state.MotionAt,
                    Name = state.Name,
                    Navigation = state.Navigation,
                    RepairRevision = state.RepairRevision,
                    RetiredAt = state.RetiredAt,
                    Role = state.Role,
                    ShieldCharge = state.ShieldCharge,
                    ShieldRepairAt = state.ShieldRepairAt,
                    SpawnSlot = state.SpawnSlot,
                    StructureDamage = state.StructureDamage,
                    SystemId = state.SystemId,
                    TypeId = state.TypeId,
                    UpdatedAt = state.UpdatedAt,
                    VelocityX = state.VelocityX,
                    VelocityY = state.VelocityY,
                    VelocityZ = state.VelocityZ,
                    X = state.X,
                    Y = state.Y,
                    Z = state.Z,
                },
                ProjectWorldNpc,
                "Id,AcquisitionRange,AnchorKind,ArmorDamage,ArmorRepairAt,BeltId,CorporationId,DestroyedAt,"
                    + "EncounterId,FactionId,HomeX,HomeY,HomeZ,MotionAt,Name,Navigation,RepairRevision,RetiredAt,Role,"
                    + "ShieldCharge,ShieldRepairAt,SpawnSlot,StructureDamage,SystemId,TypeId,UpdatedAt,VelocityX,VelocityY,"
                    + "VelocityZ,X,Y,Z",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.IsDBNull(2) ? null : reader.GetDouble(2),
                        (Eden.Gameplay.Combat.NpcAnchorKind)reader.GetInt32(3),
                        reader.GetDouble(4),
                        reader.GetInt64(5),
                        reader.GetInt32(6),
                        reader.GetInt32(7),
                        reader.IsDBNull(8) ? null : reader.GetInt64(8),
                        reader.GetInt64(9),
                        reader.GetInt32(10),
                        reader.IsDBNull(11) ? null : reader.GetDouble(11),
                        reader.IsDBNull(12) ? null : reader.GetDouble(12),
                        reader.IsDBNull(13) ? null : reader.GetDouble(13),
                        reader.GetInt64(14),
                        reader.IsDBNull(15) ? null : reader.GetString(15),
                        (Eden.Gameplay.Combat.NpcNavigationState)reader.GetInt32(16),
                        reader.GetInt64(17),
                        reader.IsDBNull(18) ? null : reader.GetInt64(18),
                        (Eden.Gameplay.Combat.NpcRole)reader.GetInt32(19),
                        reader.GetDouble(20),
                        reader.GetInt64(21),
                        reader.GetInt32(22),
                        reader.GetDouble(23),
                        reader.GetInt32(24),
                        reader.GetInt32(25),
                        reader.GetInt64(26),
                        reader.GetDouble(27),
                        reader.GetDouble(28),
                        reader.GetDouble(29),
                        reader.GetDouble(30),
                        reader.GetDouble(31),
                        reader.GetDouble(32)
                    ),
                state => new(state.Id),
                indexes:
                [
                    new("Id", row => row.Id),
                    new("AnchorKind", row => row.AnchorKind),
                    new("BeltId", row => row.BeltId),
                    new("CorporationId", row => row.CorporationId),
                    new("EncounterId", row => row.EncounterId),
                    new("FactionId", row => row.FactionId),
                    new("SpawnSlot", row => row.SpawnSlot),
                    new("SystemId", row => row.SystemId),
                    new("TypeId", row => row.TypeId),
                ],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Combat.Npcs.WorldNpcEncounter, EconomyWorldNpcEncounter>(
                "WorldNpcEncounter",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.AnchorId,
                        row.AnchorKind,
                        row.FactionId,
                        row.Generation,
                        row.HomeX,
                        row.HomeY,
                        row.HomeZ,
                        row.NextSpawnAt,
                        row.Occupied,
                        row.Role,
                        row.SystemId,
                        row.Version
                    ),
                state => new Eden.Data.World.Combat.Npcs.WorldNpcEncounter
                {
                    Id = state.Id,
                    AnchorId = state.AnchorId,
                    AnchorKind = state.AnchorKind,
                    FactionId = state.FactionId,
                    Generation = state.Generation,
                    HomeX = state.HomeX,
                    HomeY = state.HomeY,
                    HomeZ = state.HomeZ,
                    NextSpawnAt = state.NextSpawnAt,
                    Occupied = state.Occupied,
                    Role = state.Role,
                    SystemId = state.SystemId,
                    Version = state.Version,
                },
                ProjectWorldNpcEncounter,
                "Id,AnchorId,AnchorKind,FactionId,Generation,HomeX,HomeY,HomeZ,NextSpawnAt,Occupied,Role,SystemId," + "Version",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        (Eden.Gameplay.Combat.NpcAnchorKind)reader.GetInt32(3),
                        reader.GetInt32(4),
                        reader.GetInt32(5),
                        reader.GetDouble(6),
                        reader.GetDouble(7),
                        reader.GetDouble(8),
                        reader.IsDBNull(9) ? null : reader.GetInt64(9),
                        reader.GetBoolean(10),
                        (Eden.Gameplay.Combat.NpcRole)reader.GetInt32(11),
                        reader.GetInt32(12),
                        reader.GetInt32(13)
                    ),
                state => new(state.Id),
                indexes:
                [
                    new("Id", row => row.Id),
                    new("AnchorId", row => row.AnchorId),
                    new("AnchorKind", row => row.AnchorKind),
                    new("FactionId", row => row.FactionId),
                    new("SystemId", row => row.SystemId),
                ],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Combat.Npcs.WorldNpcEnergy, EconomyWorldNpcEnergy>(
                "WorldNpcEnergy",
                row => new(row.NpcId),
                row => new(row.NpcId, row.Charge, row.UpdatedAt),
                state => new Eden.Data.World.Combat.Npcs.WorldNpcEnergy
                {
                    NpcId = state.NpcId,
                    Charge = state.Charge,
                    UpdatedAt = state.UpdatedAt,
                },
                ProjectWorldNpcEnergy,
                "NpcId,Charge,UpdatedAt",
                reader => new(reader.GetInt64(1), reader.GetDouble(2), reader.GetInt64(3)),
                state => new(state.NpcId),
                indexes: [new("NpcId", row => row.NpcId)],
                primaryColumn: "NpcId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Combat.Npcs.WorldNpcEwar, EconomyWorldNpcEwar>(
                "WorldNpcEwar",
                row => new(row.NpcId, (long)row.Kind),
                row => new(row.NpcId, row.Kind, row.AbilityJson, row.AppliedJson, row.ReadyAt, row.StartedAt, row.TargetShipId, row.TypeId),
                state => new Eden.Data.World.Combat.Npcs.WorldNpcEwar
                {
                    NpcId = state.NpcId,
                    Kind = state.Kind,
                    AbilityJson = state.AbilityJson,
                    AppliedJson = state.AppliedJson,
                    ReadyAt = state.ReadyAt,
                    StartedAt = state.StartedAt,
                    TargetShipId = state.TargetShipId,
                    TypeId = state.TypeId,
                },
                ProjectWorldNpcEwar,
                "NpcId,Kind,AbilityJson,AppliedJson,ReadyAt,StartedAt,TargetShipId,TypeId",
                reader =>
                    new(
                        reader.GetInt64(1),
                        (Eden.Gameplay.Combat.NpcEwarKind)reader.GetInt32(2),
                        reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.GetInt64(5),
                        reader.GetInt64(6),
                        reader.GetInt64(7),
                        reader.GetInt32(8)
                    ),
                state => new(state.NpcId, (long)state.Kind),
                changed: NpcEwarChanged,
                indexes:
                [
                    new("NpcId", row => row.NpcId),
                    new("Kind", row => row.Kind),
                    new("TargetShipId", row => row.TargetShipId),
                    new("TypeId", row => row.TypeId),
                ],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Combat.PendingSecurityReward, EconomyPendingSecurityReward>(
                "WorldSecurityPending",
                row => new(row.PilotId),
                row => new(row.PilotId, row.CorporationId, row.Due, row.Fraction, row.Maximum, row.NpcId, row.SystemId, row.TypeId),
                state => new Eden.Data.World.Combat.PendingSecurityReward
                {
                    PilotId = state.PilotId,
                    CorporationId = state.CorporationId,
                    Due = state.Due,
                    Fraction = state.Fraction,
                    Maximum = state.Maximum,
                    NpcId = state.NpcId,
                    SystemId = state.SystemId,
                    TypeId = state.TypeId,
                },
                ProjectPendingSecurityReward,
                "PilotId,CorporationId,Due,Fraction,Maximum,NpcId,SystemId,TypeId",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        reader.GetInt64(3),
                        reader.GetDouble(4),
                        reader.GetDouble(5),
                        reader.GetInt64(6),
                        reader.GetInt32(7),
                        reader.GetInt32(8)
                    ),
                state => new(state.PilotId),
                indexes:
                [
                    new("PilotId", row => row.PilotId),
                    new("CorporationId", row => row.CorporationId),
                    new("NpcId", row => row.NpcId),
                    new("SystemId", row => row.SystemId),
                    new("TypeId", row => row.TypeId),
                ],
                primaryColumn: "PilotId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Combat.SavedConcordResponse, EconomySavedConcordResponse>(
                "WorldConcordResponse",
                row => new(row.ShipId),
                row =>
                    new(
                        row.ShipId,
                        row.CommanderId,
                        row.CompletedAt,
                        row.DestroyAt,
                        row.FirstCaptainId,
                        row.PilotId,
                        row.RespondAt,
                        row.SecondCaptainId,
                        row.SystemId
                    ),
                state => new Eden.Data.World.Combat.SavedConcordResponse
                {
                    ShipId = state.ShipId,
                    CommanderId = state.CommanderId,
                    CompletedAt = state.CompletedAt,
                    DestroyAt = state.DestroyAt,
                    FirstCaptainId = state.FirstCaptainId,
                    PilotId = state.PilotId,
                    RespondAt = state.RespondAt,
                    SecondCaptainId = state.SecondCaptainId,
                    SystemId = state.SystemId,
                },
                ProjectSavedConcordResponse,
                "ShipId,CommanderId,CompletedAt,DestroyAt,FirstCaptainId,PilotId,RespondAt,SecondCaptainId,SystemId",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.IsDBNull(2) ? null : reader.GetInt64(2),
                        reader.IsDBNull(3) ? null : reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.IsDBNull(5) ? null : reader.GetInt64(5),
                        reader.GetInt64(6),
                        reader.GetInt64(7),
                        reader.IsDBNull(8) ? null : reader.GetInt64(8),
                        reader.GetInt32(9)
                    ),
                state => new(state.ShipId),
                indexes:
                [
                    new("ShipId", row => row.ShipId),
                    new("CommanderId", row => row.CommanderId),
                    new("FirstCaptainId", row => row.FirstCaptainId),
                    new("PilotId", row => row.PilotId),
                    new("SecondCaptainId", row => row.SecondCaptainId),
                    new("SystemId", row => row.SystemId),
                ],
                primaryColumn: "ShipId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Combat.SavedCrimewatch, EconomySavedCrimewatch>(
                "WorldCrimewatch",
                row => new(row.PilotId),
                row =>
                    new(
                        row.PilotId,
                        row.CriminalActive,
                        row.CriminalUntil,
                        row.NpcActive,
                        row.NpcUntil,
                        row.PvpActive,
                        row.PvpUntil,
                        row.SuspectActive,
                        row.SuspectUntil,
                        row.SystemId,
                        row.WeaponsActive,
                        row.WeaponsUntil
                    ),
                state => new Eden.Data.World.Combat.SavedCrimewatch
                {
                    PilotId = state.PilotId,
                    CriminalActive = state.CriminalActive,
                    CriminalUntil = state.CriminalUntil,
                    NpcActive = state.NpcActive,
                    NpcUntil = state.NpcUntil,
                    PvpActive = state.PvpActive,
                    PvpUntil = state.PvpUntil,
                    SuspectActive = state.SuspectActive,
                    SuspectUntil = state.SuspectUntil,
                    SystemId = state.SystemId,
                    WeaponsActive = state.WeaponsActive,
                    WeaponsUntil = state.WeaponsUntil,
                },
                ProjectSavedCrimewatch,
                "PilotId,CriminalActive,CriminalUntil,NpcActive,NpcUntil,PvpActive,PvpUntil,SuspectActive,"
                    + "SuspectUntil,SystemId,WeaponsActive,WeaponsUntil",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetInt64(5),
                        reader.GetInt64(6),
                        reader.GetInt64(7),
                        reader.GetInt64(8),
                        reader.GetInt64(9),
                        reader.GetInt32(10),
                        reader.GetInt64(11),
                        reader.GetInt64(12)
                    ),
                state => new(state.PilotId),
                indexes: [new("PilotId", row => row.PilotId), new("SystemId", row => row.SystemId)],
                primaryColumn: "PilotId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Combat.SavedEngagement, EconomySavedEngagement>(
                "WorldEngagement",
                row => new(row.LowerPilotId, row.HigherPilotId),
                row => new(row.LowerPilotId, row.HigherPilotId, row.ActiveUntil, row.ExpiresAt),
                state => new Eden.Data.World.Combat.SavedEngagement
                {
                    LowerPilotId = state.LowerPilotId,
                    HigherPilotId = state.HigherPilotId,
                    ActiveUntil = state.ActiveUntil,
                    ExpiresAt = state.ExpiresAt,
                },
                ProjectSavedEngagement,
                "LowerPilotId,HigherPilotId,ActiveUntil,ExpiresAt",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)),
                state => new(state.LowerPilotId, state.HigherPilotId),
                indexes: [new("LowerPilotId", row => row.LowerPilotId), new("HigherPilotId", row => row.HigherPilotId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Combat.SavedLogoff, EconomySavedLogoff>(
                "WorldLogoff",
                row => new(row.PilotId),
                row => new(row.PilotId, row.ExpiresAt, row.ShipId, row.SystemId),
                state => new Eden.Data.World.Combat.SavedLogoff
                {
                    PilotId = state.PilotId,
                    ExpiresAt = state.ExpiresAt,
                    ShipId = state.ShipId,
                    SystemId = state.SystemId,
                },
                ProjectSavedLogoff,
                "PilotId,ExpiresAt,ShipId,SystemId",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt32(4)),
                state => new(state.PilotId),
                indexes: [new("PilotId", row => row.PilotId), new("ShipId", row => row.ShipId), new("SystemId", row => row.SystemId)],
                primaryColumn: "PilotId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Combat.SavedPilotHostility, EconomySavedPilotHostility>(
                "WorldPilotHostility",
                row => new(row.SourceShipId, row.TargetShipId),
                row => new(row.SourceShipId, row.TargetShipId, row.ExpiresAt, row.LastAttackAt),
                state => new Eden.Data.World.Combat.SavedPilotHostility
                {
                    SourceShipId = state.SourceShipId,
                    TargetShipId = state.TargetShipId,
                    ExpiresAt = state.ExpiresAt,
                    LastAttackAt = state.LastAttackAt,
                },
                ProjectSavedPilotHostility,
                "SourceShipId,TargetShipId,ExpiresAt,LastAttackAt",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)),
                state => new(state.SourceShipId, state.TargetShipId),
                indexes: [new("SourceShipId", row => row.SourceShipId), new("TargetShipId", row => row.TargetShipId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Combat.SavedSecurityStatus, EconomySavedSecurityStatus>(
                "WorldPilotSecurity",
                row => new(row.PilotId),
                row => new(row.PilotId, row.Value),
                state => new Eden.Data.World.Combat.SavedSecurityStatus { PilotId = state.PilotId, Value = state.Value },
                ProjectSavedSecurityStatus,
                "PilotId,Value",
                reader => new(reader.GetInt64(1), reader.GetDouble(2)),
                state => new(state.PilotId),
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: "PilotId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Combat.SavedSecurityTransaction, EconomySavedSecurityTransaction>(
                "WorldSecurityTransaction",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.CorporationId,
                        row.Modification,
                        row.NewValue,
                        row.NpcId,
                        row.PilotId,
                        row.SystemId,
                        row.Time,
                        row.TypeId
                    ),
                state => new Eden.Data.World.Combat.SavedSecurityTransaction
                {
                    Id = state.Id,
                    CorporationId = state.CorporationId,
                    Modification = state.Modification,
                    NewValue = state.NewValue,
                    NpcId = state.NpcId,
                    PilotId = state.PilotId,
                    SystemId = state.SystemId,
                    Time = state.Time,
                    TypeId = state.TypeId,
                },
                ProjectSavedSecurityTransaction,
                "Id,CorporationId,Modification,NewValue,NpcId,PilotId,SystemId,Time,TypeId",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        reader.GetDouble(3),
                        reader.GetDouble(4),
                        reader.GetInt64(5),
                        reader.GetInt64(6),
                        reader.GetInt32(7),
                        reader.GetInt64(8),
                        reader.GetInt32(9)
                    ),
                state => new(state.Id),
                indexes:
                [
                    new("Id", row => row.Id),
                    new("CorporationId", row => row.CorporationId),
                    new("NpcId", row => row.NpcId),
                    new("PilotId", row => row.PilotId),
                    new("SystemId", row => row.SystemId),
                    new("TypeId", row => row.TypeId),
                ],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Combat.SavedWeaponLink, EconomySavedWeaponLink>(
                "WorldWeaponLink",
                row => new(row.SlaveId),
                row => new(row.SlaveId, row.MasterId, row.ShipId),
                state => new Eden.Data.World.Combat.SavedWeaponLink
                {
                    SlaveId = state.SlaveId,
                    MasterId = state.MasterId,
                    ShipId = state.ShipId,
                },
                ProjectSavedWeaponLink,
                "SlaveId,MasterId,ShipId",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)),
                state => new(state.SlaveId),
                indexes: [new("SlaveId", row => row.SlaveId), new("MasterId", row => row.MasterId), new("ShipId", row => row.ShipId)],
                primaryColumn: "SlaveId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Combat.ShipHealthState, EconomyShipHealthState>(
                "WorldShipHealth",
                row => new(row.ShipId),
                row => new(row.ShipId, row.ArmorDamage, row.ShieldCharge, row.StructureDamage, row.UpdatedAt),
                state => new Eden.Data.World.Combat.ShipHealthState
                {
                    ShipId = state.ShipId,
                    ArmorDamage = state.ArmorDamage,
                    ShieldCharge = state.ShieldCharge,
                    StructureDamage = state.StructureDamage,
                    UpdatedAt = state.UpdatedAt,
                },
                ProjectShipHealthState,
                "ShipId,ArmorDamage,ShieldCharge,StructureDamage,UpdatedAt",
                reader => new(reader.GetInt64(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4), reader.GetInt64(5)),
                state => new(state.ShipId),
                indexes: [new("ShipId", row => row.ShipId)],
                primaryColumn: "ShipId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Combat.ShipLoss, EconomyShipLoss>(
                "WorldShipLoss",
                row => new(row.ShipId),
                row =>
                    new(
                        row.ShipId,
                        row.AttackerPilotId,
                        row.CapsuleId,
                        row.DroppedItemIdsJson,
                        row.ItemsJson,
                        row.Name,
                        row.NpcId,
                        row.PilotId,
                        row.StoredShipsJson,
                        row.SystemId,
                        row.Time,
                        row.TypeId,
                        row.X,
                        row.Y,
                        row.Z
                    ),
                state => new Eden.Data.World.Combat.ShipLoss
                {
                    ShipId = state.ShipId,
                    AttackerPilotId = state.AttackerPilotId,
                    CapsuleId = state.CapsuleId,
                    DroppedItemIdsJson = state.DroppedItemIdsJson,
                    ItemsJson = state.ItemsJson,
                    Name = state.Name,
                    NpcId = state.NpcId,
                    PilotId = state.PilotId,
                    StoredShipsJson = state.StoredShipsJson,
                    SystemId = state.SystemId,
                    Time = state.Time,
                    TypeId = state.TypeId,
                    X = state.X,
                    Y = state.Y,
                    Z = state.Z,
                },
                ProjectShipLoss,
                "ShipId,AttackerPilotId,CapsuleId,DroppedItemIdsJson,ItemsJson,Name,NpcId,PilotId,StoredShipsJson,"
                    + "SystemId,Time,TypeId,X,Y,Z",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.IsDBNull(2) ? null : reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.GetString(6),
                        reader.IsDBNull(7) ? null : reader.GetInt64(7),
                        reader.GetInt64(8),
                        reader.GetString(9),
                        reader.GetInt32(10),
                        reader.GetInt64(11),
                        reader.GetInt32(12),
                        reader.GetDouble(13),
                        reader.GetDouble(14),
                        reader.GetDouble(15)
                    ),
                state => new(state.ShipId),
                indexes:
                [
                    new("ShipId", row => row.ShipId),
                    new("AttackerPilotId", row => row.AttackerPilotId),
                    new("CapsuleId", row => row.CapsuleId),
                    new("NpcId", row => row.NpcId),
                    new("PilotId", row => row.PilotId),
                    new("SystemId", row => row.SystemId),
                    new("TypeId", row => row.TypeId),
                ],
                primaryColumn: "ShipId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Combat.WorldMissile, EconomyWorldMissile>(
                "WorldMissile",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.ChargeTypeId,
                        row.CorporationId,
                        row.EndedAt,
                        row.ExpiresAt,
                        row.GroupId,
                        row.LaunchStamp,
                        row.LaunchedAt,
                        row.LauncherTypeId,
                        row.ModuleId,
                        row.Outcome,
                        row.PilotId,
                        row.Reported,
                        row.Snapshot,
                        row.SourceKind,
                        row.SourceShipId,
                        row.SourceTypeId,
                        row.Status,
                        row.SystemId,
                        row.TargetId,
                        row.TargetKind,
                        row.TargetRadius,
                        row.UpdatedAt
                    ),
                state => new Eden.Data.World.Combat.WorldMissile
                {
                    Id = state.Id,
                    ChargeTypeId = state.ChargeTypeId,
                    CorporationId = state.CorporationId,
                    EndedAt = state.EndedAt,
                    ExpiresAt = state.ExpiresAt,
                    GroupId = state.GroupId,
                    LaunchStamp = state.LaunchStamp,
                    LaunchedAt = state.LaunchedAt,
                    LauncherTypeId = state.LauncherTypeId,
                    ModuleId = state.ModuleId,
                    Outcome = state.Outcome,
                    PilotId = state.PilotId,
                    Reported = state.Reported,
                    Snapshot = state.Snapshot,
                    SourceKind = state.SourceKind,
                    SourceShipId = state.SourceShipId,
                    SourceTypeId = state.SourceTypeId,
                    Status = state.Status,
                    SystemId = state.SystemId,
                    TargetId = state.TargetId,
                    TargetKind = state.TargetKind,
                    TargetRadius = state.TargetRadius,
                    UpdatedAt = state.UpdatedAt,
                },
                ProjectWorldMissile,
                "Id,ChargeTypeId,CorporationId,EndedAt,ExpiresAt,GroupId,LaunchStamp,LaunchedAt,LauncherTypeId,"
                    + "ModuleId,Outcome,PilotId,Reported,Snapshot,SourceKind,SourceShipId,SourceTypeId,Status,SystemId,"
                    + "TargetId,TargetKind,TargetRadius,UpdatedAt",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        reader.GetInt32(3),
                        reader.IsDBNull(4) ? null : reader.GetInt64(4),
                        reader.GetInt64(5),
                        reader.GetInt32(6),
                        reader.GetInt32(7),
                        reader.GetInt64(8),
                        reader.GetInt32(9),
                        reader.GetInt64(10),
                        reader.IsDBNull(11) ? null : reader.GetString(11),
                        reader.GetInt64(12),
                        reader.GetBoolean(13),
                        reader.GetString(14),
                        (Eden.Gameplay.Combat.MissileSourceKind)reader.GetInt32(15),
                        reader.GetInt64(16),
                        reader.GetInt32(17),
                        (Eden.Gameplay.Combat.MissileFlightStatus)reader.GetInt32(18),
                        reader.GetInt32(19),
                        reader.GetInt64(20),
                        (Eden.Gameplay.Combat.MissileTargetKind)reader.GetInt32(21),
                        reader.GetFloat(22),
                        reader.GetInt64(23)
                    ),
                state => new(state.Id),
                changed: MissileChanged,
                indexes:
                [
                    new("Id", row => row.Id),
                    new("ChargeTypeId", row => row.ChargeTypeId),
                    new("CorporationId", row => row.CorporationId),
                    new("GroupId", row => row.GroupId),
                    new("LauncherTypeId", row => row.LauncherTypeId),
                    new("ModuleId", row => row.ModuleId),
                    new("PilotId", row => row.PilotId),
                    new("SourceKind", row => row.SourceKind),
                    new("SourceShipId", row => row.SourceShipId),
                    new("SourceTypeId", row => row.SourceTypeId),
                    new("Status", row => row.Status),
                    new("SystemId", row => row.SystemId),
                    new("TargetId", row => row.TargetId),
                    new("TargetKind", row => row.TargetKind),
                ],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Combat.WorldPlayerEwar, EconomyWorldPlayerEwar>(
                "WorldPlayerEwar",
                row => new(row.ModuleId),
                row => new(row.ModuleId, row.ContactJson, row.PilotId, row.SourceShipId, row.SystemId, row.TargetId, row.TargetKind),
                state => new Eden.Data.World.Combat.WorldPlayerEwar
                {
                    ModuleId = state.ModuleId,
                    ContactJson = state.ContactJson,
                    PilotId = state.PilotId,
                    SourceShipId = state.SourceShipId,
                    SystemId = state.SystemId,
                    TargetId = state.TargetId,
                    TargetKind = state.TargetKind,
                },
                ProjectWorldPlayerEwar,
                "ModuleId,ContactJson,PilotId,SourceShipId,SystemId,TargetId,TargetKind",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetString(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetInt32(5),
                        reader.GetInt64(6),
                        (Eden.Gameplay.Propulsion.TackleTargetKind)reader.GetInt32(7)
                    ),
                state => new(state.ModuleId),
                changed: PlayerEwarChanged,
                indexes:
                [
                    new("ModuleId", row => row.ModuleId),
                    new("PilotId", row => row.PilotId),
                    new("SourceShipId", row => row.SourceShipId),
                    new("SystemId", row => row.SystemId),
                    new("TargetId", row => row.TargetId),
                    new("TargetKind", row => row.TargetKind),
                ],
                primaryColumn: "ModuleId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Corporations.SavedCorporation, EconomySavedCorporation>(
                "WorldCorporation",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.BalanceCents,
                        row.CeoId,
                        row.CreatedAt,
                        row.CreatorId,
                        row.DetailsJson,
                        row.MemberLimit,
                        row.Name,
                        row.StationId,
                        row.SystemId,
                        row.Ticker
                    ),
                state => new Eden.Data.World.Corporations.SavedCorporation
                {
                    Id = state.Id,
                    BalanceCents = state.BalanceCents,
                    CeoId = state.CeoId,
                    CreatedAt = state.CreatedAt,
                    CreatorId = state.CreatorId,
                    DetailsJson = state.DetailsJson,
                    MemberLimit = state.MemberLimit,
                    Name = state.Name,
                    StationId = state.StationId,
                    SystemId = state.SystemId,
                    Ticker = state.Ticker,
                },
                ProjectSavedCorporation,
                "Id,BalanceCents,CeoId,CreatedAt,CreatorId,DetailsJson,MemberLimit,Name,StationId,SystemId,Ticker",
                reader =>
                    new(
                        reader.GetInt32(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetInt64(5),
                        reader.GetString(6),
                        reader.GetInt32(7),
                        reader.GetString(8),
                        reader.GetInt32(9),
                        reader.GetInt32(10),
                        reader.GetString(11)
                    ),
                state => new(state.Id),
                indexes:
                [
                    new("Id", row => row.Id),
                    new("CeoId", row => row.CeoId),
                    new("CreatorId", row => row.CreatorId),
                    new("Name", row => row.Name),
                    new("StationId", row => row.StationId),
                    new("SystemId", row => row.SystemId),
                    new("Ticker", row => row.Ticker),
                ],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Corporations.SavedCorporationMemberAccess, EconomySavedCorporationMemberAccess>(
                "WorldCorporationMemberAccess",
                row => new(row.PilotId),
                row => new(row.PilotId, row.CorporationId, row.DataJson),
                state => new Eden.Data.World.Corporations.SavedCorporationMemberAccess
                {
                    PilotId = state.PilotId,
                    CorporationId = state.CorporationId,
                    DataJson = state.DataJson,
                },
                ProjectSavedCorporationMemberAccess,
                "PilotId,CorporationId,DataJson",
                reader => new(reader.GetInt64(1), reader.GetInt32(2), reader.GetString(3)),
                state => new(state.PilotId),
                indexes: [new("PilotId", row => row.PilotId), new("CorporationId", row => row.CorporationId)],
                primaryColumn: "PilotId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Corporations.SavedCorporationTitle, EconomySavedCorporationTitle>(
                "WorldCorporationTitle",
                row => new(row.CorporationId, row.TitleId),
                row => new(row.CorporationId, row.TitleId, row.DataJson),
                state => new Eden.Data.World.Corporations.SavedCorporationTitle
                {
                    CorporationId = state.CorporationId,
                    TitleId = state.TitleId,
                    DataJson = state.DataJson,
                },
                ProjectSavedCorporationTitle,
                "CorporationId,TitleId,DataJson",
                reader => new(reader.GetInt32(1), reader.GetInt64(2), reader.GetString(3)),
                state => new(state.CorporationId, state.TitleId),
                indexes: [new("CorporationId", row => row.CorporationId), new("TitleId", row => row.TitleId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Corporations.SavedCorporationWalletSelection, EconomySavedCorporationWalletSelection>(
                "WorldCorporationWalletSelection",
                row => new(row.PilotId),
                row => new(row.PilotId, row.CorporationId, row.Key),
                state => new Eden.Data.World.Corporations.SavedCorporationWalletSelection
                {
                    PilotId = state.PilotId,
                    CorporationId = state.CorporationId,
                    Key = state.Key,
                },
                ProjectSavedCorporationWalletSelection,
                "PilotId,CorporationId,Key",
                reader => new(reader.GetInt64(1), reader.GetInt32(2), reader.GetInt32(3)),
                state => new(state.PilotId),
                indexes: [new("PilotId", row => row.PilotId), new("CorporationId", row => row.CorporationId)],
                primaryColumn: "PilotId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Corporations.SavedWalletLedger, EconomySavedWalletLedger>(
                "WorldWalletLedger",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.AmountCents,
                        row.BalanceCents,
                        row.CounterpartyId,
                        row.EntryType,
                        row.Key,
                        row.OwnerId,
                        row.Reason,
                        row.ReferenceId,
                        row.Time
                    ),
                state => new Eden.Data.World.Corporations.SavedWalletLedger
                {
                    Id = state.Id,
                    AmountCents = state.AmountCents,
                    BalanceCents = state.BalanceCents,
                    CounterpartyId = state.CounterpartyId,
                    EntryType = state.EntryType,
                    Key = state.Key,
                    OwnerId = state.OwnerId,
                    Reason = state.Reason,
                    ReferenceId = state.ReferenceId,
                    Time = state.Time,
                },
                ProjectSavedWalletLedger,
                "Id,AmountCents,BalanceCents,CounterpartyId,EntryType,Key,OwnerId,Reason,ReferenceId,Time",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        (Eden.Gameplay.Market.WalletEntryType)reader.GetInt32(5),
                        reader.GetInt32(6),
                        reader.GetInt64(7),
                        reader.GetString(8),
                        reader.GetInt64(9),
                        reader.GetInt64(10)
                    ),
                state => new(state.Id),
                indexes:
                [
                    new("Id", row => row.Id),
                    new("CounterpartyId", row => row.CounterpartyId),
                    new("OwnerId", row => row.OwnerId),
                    new("ReferenceId", row => row.ReferenceId),
                ],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Drones.WorldDrone, EconomyWorldDrone>(
                "WorldDrone",
                row => new(row.ItemId),
                row =>
                    new(
                        row.ItemId,
                        row.Activity,
                        row.ArmorDamage,
                        row.CommandedAt,
                        row.ControllerShipId,
                        row.CorporationId,
                        row.DelegateFleetId,
                        row.DelegateMembership,
                        row.DelegatePilotId,
                        row.DelegateShipId,
                        row.DelegationMode,
                        row.Orbiting,
                        row.OwnerMembership,
                        row.PilotId,
                        row.ReactionRevision,
                        row.ReadyAt,
                        row.Shield,
                        row.ShotAt,
                        row.ShotTargetId,
                        row.SpeedLimit,
                        row.StructureDamage,
                        row.SystemId,
                        row.TargetId,
                        row.UpdatedAt,
                        row.VelocityX,
                        row.VelocityY,
                        row.VelocityZ,
                        row.X,
                        row.Y,
                        row.Z
                    ),
                state => new Eden.Data.World.Drones.WorldDrone
                {
                    ItemId = state.ItemId,
                    Activity = state.Activity,
                    ArmorDamage = state.ArmorDamage,
                    CommandedAt = state.CommandedAt,
                    ControllerShipId = state.ControllerShipId,
                    CorporationId = state.CorporationId,
                    DelegateFleetId = state.DelegateFleetId,
                    DelegateMembership = state.DelegateMembership,
                    DelegatePilotId = state.DelegatePilotId,
                    DelegateShipId = state.DelegateShipId,
                    DelegationMode = state.DelegationMode,
                    Orbiting = state.Orbiting,
                    OwnerMembership = state.OwnerMembership,
                    PilotId = state.PilotId,
                    ReactionRevision = state.ReactionRevision,
                    ReadyAt = state.ReadyAt,
                    Shield = state.Shield,
                    ShotAt = state.ShotAt,
                    ShotTargetId = state.ShotTargetId,
                    SpeedLimit = state.SpeedLimit,
                    StructureDamage = state.StructureDamage,
                    SystemId = state.SystemId,
                    TargetId = state.TargetId,
                    UpdatedAt = state.UpdatedAt,
                    VelocityX = state.VelocityX,
                    VelocityY = state.VelocityY,
                    VelocityZ = state.VelocityZ,
                    X = state.X,
                    Y = state.Y,
                    Z = state.Z,
                },
                ProjectWorldDrone,
                "ItemId,Activity,ArmorDamage,CommandedAt,ControllerShipId,CorporationId,DelegateFleetId,"
                    + "DelegateMembership,DelegatePilotId,DelegateShipId,DelegationMode,Orbiting,OwnerMembership,PilotId,"
                    + "ReactionRevision,ReadyAt,Shield,ShotAt,ShotTargetId,SpeedLimit,StructureDamage,SystemId,TargetId,"
                    + "UpdatedAt,VelocityX,VelocityY,VelocityZ,X,Y,Z",
                reader =>
                    new(
                        reader.GetInt64(1),
                        (Eden.Gameplay.Drones.DroneActivity)reader.GetInt32(2),
                        reader.GetDouble(3),
                        reader.GetInt64(4),
                        reader.GetInt64(5),
                        reader.GetInt32(6),
                        reader.GetInt64(7),
                        reader.GetInt64(8),
                        reader.GetInt64(9),
                        reader.GetInt64(10),
                        (Eden.Gameplay.Drones.DroneDelegationMode)reader.GetInt32(11),
                        reader.GetBoolean(12),
                        reader.GetInt64(13),
                        reader.GetInt64(14),
                        reader.GetInt64(15),
                        reader.GetInt64(16),
                        reader.GetDouble(17),
                        reader.GetInt64(18),
                        reader.GetInt64(19),
                        reader.GetFloat(20),
                        reader.GetDouble(21),
                        reader.IsDBNull(22) ? null : reader.GetInt32(22),
                        reader.GetInt64(23),
                        reader.GetInt64(24),
                        reader.GetDouble(25),
                        reader.GetDouble(26),
                        reader.GetDouble(27),
                        reader.GetDouble(28),
                        reader.GetDouble(29),
                        reader.GetDouble(30)
                    ),
                state => new(state.ItemId),
                indexes:
                [
                    new("ItemId", row => row.ItemId),
                    new("ControllerShipId", row => row.ControllerShipId),
                    new("CorporationId", row => row.CorporationId),
                    new("DelegateFleetId", row => row.DelegateFleetId),
                    new("DelegatePilotId", row => row.DelegatePilotId),
                    new("DelegateShipId", row => row.DelegateShipId),
                    new("PilotId", row => row.PilotId),
                    new("ShotTargetId", row => row.ShotTargetId),
                    new("SystemId", row => row.SystemId),
                    new("TargetId", row => row.TargetId),
                ],
                primaryColumn: "ItemId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Drones.WorldDroneCombatTriggers, EconomyWorldDroneCombatTriggers>(
                "WorldDroneCombatTriggers",
                row => new(row.ShipId),
                row =>
                    new(
                        row.ShipId,
                        row.IncomingAt,
                        row.IncomingRevision,
                        row.IncomingSourceId,
                        row.OffensiveAt,
                        row.OffensiveRevision,
                        row.OffensiveTargetId
                    ),
                state => new Eden.Data.World.Drones.WorldDroneCombatTriggers
                {
                    ShipId = state.ShipId,
                    IncomingAt = state.IncomingAt,
                    IncomingRevision = state.IncomingRevision,
                    IncomingSourceId = state.IncomingSourceId,
                    OffensiveAt = state.OffensiveAt,
                    OffensiveRevision = state.OffensiveRevision,
                    OffensiveTargetId = state.OffensiveTargetId,
                },
                ProjectWorldDroneCombatTriggers,
                "ShipId,IncomingAt,IncomingRevision,IncomingSourceId,OffensiveAt,OffensiveRevision,OffensiveTargetId",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetInt64(5),
                        reader.GetInt64(6),
                        reader.GetInt64(7)
                    ),
                state => new(state.ShipId),
                indexes:
                [
                    new("ShipId", row => row.ShipId),
                    new("IncomingSourceId", row => row.IncomingSourceId),
                    new("OffensiveTargetId", row => row.OffensiveTargetId),
                ],
                primaryColumn: "ShipId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Drones.WorldDroneSettings, EconomyWorldDroneSettings>(
                "WorldDroneSettings",
                row => new(row.PilotId),
                row => new(row.PilotId, row.Aggressive, row.FocusFire),
                state => new Eden.Data.World.Drones.WorldDroneSettings
                {
                    PilotId = state.PilotId,
                    Aggressive = state.Aggressive,
                    FocusFire = state.FocusFire,
                },
                ProjectWorldDroneSettings,
                "PilotId,Aggressive,FocusFire",
                reader => new(reader.GetInt64(1), reader.GetBoolean(2), reader.GetBoolean(3)),
                state => new(state.PilotId),
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: "PilotId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Economy.SavedAdjustedPrice, EconomySavedAdjustedPrice>(
                "WorldAdjustedPrice",
                row => new(row.TypeId),
                row => new(row.TypeId, row.PriceIsk),
                state => new Eden.Data.World.Economy.SavedAdjustedPrice { TypeId = state.TypeId, PriceIsk = state.PriceIsk },
                ProjectSavedAdjustedPrice,
                "TypeId,PriceIsk",
                reader => new(reader.GetInt32(1), reader.GetDecimal(2)),
                state => new(state.TypeId),
                indexes: [new("TypeId", row => row.TypeId)],
                primaryColumn: "TypeId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Economy.SavedBeltRenewal, EconomySavedBeltRenewal>(
                "WorldBeltRenewal",
                row => new(row.BeltId),
                row => new(row.BeltId, row.NextRenewal, row.SystemId),
                state => new Eden.Data.World.Economy.SavedBeltRenewal
                {
                    BeltId = state.BeltId,
                    NextRenewal = state.NextRenewal,
                    SystemId = state.SystemId,
                },
                ProjectSavedBeltRenewal,
                "BeltId,NextRenewal,SystemId",
                reader => new(reader.GetInt32(1), reader.GetInt64(2), reader.GetInt32(3)),
                state => new(state.BeltId),
                indexes: [new("BeltId", row => row.BeltId), new("SystemId", row => row.SystemId)],
                primaryColumn: "BeltId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Economy.SavedEconomicPolicy, EconomySavedEconomicPolicy>(
                "WorldEconomicPolicy",
                row => new(row.Id),
                row => new(row.Id, row.ChargeServiceCosts, row.DefaultManufacturingIndex, row.RenewBelts, row.RenewalHourUtc),
                state => new Eden.Data.World.Economy.SavedEconomicPolicy
                {
                    Id = state.Id,
                    ChargeServiceCosts = state.ChargeServiceCosts,
                    DefaultManufacturingIndex = state.DefaultManufacturingIndex,
                    RenewBelts = state.RenewBelts,
                    RenewalHourUtc = state.RenewalHourUtc,
                },
                ProjectSavedEconomicPolicy,
                "Id,ChargeServiceCosts,DefaultManufacturingIndex,RenewBelts,RenewalHourUtc",
                reader => new(reader.GetInt32(1), reader.GetBoolean(2), reader.GetInt32(3), reader.GetBoolean(4), reader.GetInt32(5)),
                state => new(state.Id),
                indexes: [new("Id", row => row.Id)],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Exploration.WorldAnomalyMember, EconomyWorldAnomalyMember>(
                "WorldAnomalyMember",
                row => new(row.NpcId),
                row => new(row.NpcId, row.Sentry, row.SiteId, row.Trigger, row.Wave),
                state => new Eden.Data.World.Exploration.WorldAnomalyMember
                {
                    NpcId = state.NpcId,
                    Sentry = state.Sentry,
                    SiteId = state.SiteId,
                    Trigger = state.Trigger,
                    Wave = state.Wave,
                },
                ProjectWorldAnomalyMember,
                "NpcId,Sentry,SiteId,Trigger,Wave",
                reader => new(reader.GetInt64(1), reader.GetBoolean(2), reader.GetInt64(3), reader.GetBoolean(4), reader.GetInt32(5)),
                state => new(state.NpcId),
                indexes: [new("NpcId", row => row.NpcId), new("SiteId", row => row.SiteId)],
                primaryColumn: "NpcId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Exploration.WorldCombatAnomaly, EconomyWorldCombatAnomaly>(
                "WorldCombatAnomaly",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.CompletedAt,
                        row.DefinitionVersion,
                        row.DungeonId,
                        row.IsSignature,
                        row.NextWaveAt,
                        row.ReplaceAt,
                        row.Slot,
                        row.SystemId,
                        row.Wave,
                        row.X,
                        row.Y,
                        row.Z
                    ),
                state => new Eden.Data.World.Exploration.WorldCombatAnomaly
                {
                    Id = state.Id,
                    CompletedAt = state.CompletedAt,
                    DefinitionVersion = state.DefinitionVersion,
                    DungeonId = state.DungeonId,
                    IsSignature = state.IsSignature,
                    NextWaveAt = state.NextWaveAt,
                    ReplaceAt = state.ReplaceAt,
                    Slot = state.Slot,
                    SystemId = state.SystemId,
                    Wave = state.Wave,
                    X = state.X,
                    Y = state.Y,
                    Z = state.Z,
                },
                ProjectWorldCombatAnomaly,
                "Id,CompletedAt,DefinitionVersion,DungeonId,IsSignature,NextWaveAt,ReplaceAt,Slot,SystemId,Wave,X,Y,Z",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.IsDBNull(2) ? null : reader.GetInt64(2),
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        reader.GetBoolean(5),
                        reader.IsDBNull(6) ? null : reader.GetInt64(6),
                        reader.IsDBNull(7) ? null : reader.GetInt64(7),
                        reader.GetInt32(8),
                        reader.GetInt32(9),
                        reader.GetInt32(10),
                        reader.GetDouble(11),
                        reader.GetDouble(12),
                        reader.GetDouble(13)
                    ),
                state => new(state.Id),
                indexes: [new("Id", row => row.Id), new("DungeonId", row => row.DungeonId), new("SystemId", row => row.SystemId)],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Fittings.ActiveFittedModule, EconomyActiveFittedModule>(
                "WorldActiveModule",
                row => new(row.ModuleId),
                row => new(row.ModuleId, row.ExpiresAt, row.ShipId),
                state => new Eden.Data.World.Fittings.ActiveFittedModule
                {
                    ModuleId = state.ModuleId,
                    ExpiresAt = state.ExpiresAt,
                    ShipId = state.ShipId,
                },
                ProjectActiveFittedModule,
                "ModuleId,ExpiresAt,ShipId",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)),
                state => new(state.ModuleId),
                changed: TimedModuleChanged,
                indexes: [new("ModuleId", row => row.ModuleId), new("ShipId", row => row.ShipId)],
                primaryColumn: "ModuleId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Fittings.ShipEnergy, EconomyShipEnergy>(
                "WorldShipEnergy",
                row => new(row.ShipId),
                row => new(row.ShipId, row.Charge, row.UpdatedAt),
                state => new Eden.Data.World.Fittings.ShipEnergy
                {
                    ShipId = state.ShipId,
                    Charge = state.Charge,
                    UpdatedAt = state.UpdatedAt,
                },
                ProjectShipEnergy,
                "ShipId,Charge,UpdatedAt",
                reader => new(reader.GetInt64(1), reader.GetDouble(2), reader.GetInt64(3)),
                state => new(state.ShipId),
                indexes: [new("ShipId", row => row.ShipId)],
                primaryColumn: "ShipId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Insurance.SavedInsuranceLedger, EconomySavedInsuranceLedger>(
                "WorldInsuranceLedger",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.AmountCents,
                        row.BalanceCents,
                        row.IsPayout,
                        row.PilotId,
                        row.PolicyId,
                        row.ShipId,
                        row.Time,
                        row.TypeId
                    ),
                state => new Eden.Data.World.Insurance.SavedInsuranceLedger
                {
                    Id = state.Id,
                    AmountCents = state.AmountCents,
                    BalanceCents = state.BalanceCents,
                    IsPayout = state.IsPayout,
                    PilotId = state.PilotId,
                    PolicyId = state.PolicyId,
                    ShipId = state.ShipId,
                    Time = state.Time,
                    TypeId = state.TypeId,
                },
                ProjectSavedInsuranceLedger,
                "Id,AmountCents,BalanceCents,IsPayout,PilotId,PolicyId,ShipId,Time,TypeId",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetBoolean(4),
                        reader.GetInt64(5),
                        reader.IsDBNull(6) ? null : reader.GetInt64(6),
                        reader.GetInt64(7),
                        reader.GetInt64(8),
                        reader.GetInt32(9)
                    ),
                state => new(state.Id),
                indexes:
                [
                    new("Id", row => row.Id),
                    new("PilotId", row => row.PilotId),
                    new("PolicyId", row => row.PolicyId),
                    new("ShipId", row => row.ShipId),
                    new("TypeId", row => row.TypeId),
                ],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Insurance.SavedInsurancePolicy, EconomySavedInsurancePolicy>(
                "WorldInsurancePolicy",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.EndTime,
                        row.Level,
                        row.PilotId,
                        row.PremiumCents,
                        row.ShipId,
                        row.StartTime,
                        row.Status,
                        row.TypeId,
                        row.ValueCents
                    ),
                state => new Eden.Data.World.Insurance.SavedInsurancePolicy
                {
                    Id = state.Id,
                    EndTime = state.EndTime,
                    Level = state.Level,
                    PilotId = state.PilotId,
                    PremiumCents = state.PremiumCents,
                    ShipId = state.ShipId,
                    StartTime = state.StartTime,
                    Status = state.Status,
                    TypeId = state.TypeId,
                    ValueCents = state.ValueCents,
                },
                ProjectSavedInsurancePolicy,
                "Id,EndTime,Level,PilotId,PremiumCents,ShipId,StartTime,Status,TypeId,ValueCents",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        (Eden.Gameplay.Insurance.InsuranceLevel)reader.GetInt32(3),
                        reader.GetInt64(4),
                        reader.GetInt64(5),
                        reader.GetInt64(6),
                        reader.GetInt64(7),
                        (Eden.Gameplay.Insurance.InsurancePolicyStatus)reader.GetInt32(8),
                        reader.GetInt32(9),
                        reader.GetInt64(10)
                    ),
                state => new(state.Id),
                indexes:
                [
                    new("Id", row => row.Id),
                    new("PilotId", row => row.PilotId),
                    new("ShipId", row => row.ShipId),
                    new("Status", row => row.Status),
                    new("TypeId", row => row.TypeId),
                ],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Loot.WorldWreck, EconomyWorldWreck>(
                "WorldWreck",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.CorporationId,
                        row.CreatedAt,
                        row.ExpiresAt,
                        row.Name,
                        row.NpcId,
                        row.OwnerId,
                        row.SalvageJson,
                        row.SalvagedAt,
                        row.ShipId,
                        row.SystemId,
                        row.TypeId,
                        row.X,
                        row.Y,
                        row.Z
                    ),
                state => new Eden.Data.World.Loot.WorldWreck
                {
                    Id = state.Id,
                    CorporationId = state.CorporationId,
                    CreatedAt = state.CreatedAt,
                    ExpiresAt = state.ExpiresAt,
                    Name = state.Name,
                    NpcId = state.NpcId,
                    OwnerId = state.OwnerId,
                    SalvageJson = state.SalvageJson,
                    SalvagedAt = state.SalvagedAt,
                    ShipId = state.ShipId,
                    SystemId = state.SystemId,
                    TypeId = state.TypeId,
                    X = state.X,
                    Y = state.Y,
                    Z = state.Z,
                },
                ProjectWorldWreck,
                "Id,CorporationId,CreatedAt,ExpiresAt,Name,NpcId,OwnerId,SalvageJson,SalvagedAt,ShipId,SystemId," + "TypeId,X,Y,Z",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetInt64(6),
                        reader.GetInt64(7),
                        reader.IsDBNull(8) ? null : reader.GetString(8),
                        reader.IsDBNull(9) ? null : reader.GetInt64(9),
                        reader.IsDBNull(10) ? null : reader.GetInt64(10),
                        reader.GetInt32(11),
                        reader.GetInt32(12),
                        reader.GetDouble(13),
                        reader.GetDouble(14),
                        reader.GetDouble(15)
                    ),
                state => new(state.Id),
                indexes:
                [
                    new("Id", row => row.Id),
                    new("CorporationId", row => row.CorporationId),
                    new("NpcId", row => row.NpcId),
                    new("OwnerId", row => row.OwnerId),
                    new("ShipId", row => row.ShipId),
                    new("SystemId", row => row.SystemId),
                    new("TypeId", row => row.TypeId),
                ],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Mining.SavedMiningDeposit, EconomySavedMiningDeposit>(
                "WorldMiningDeposit",
                row => new(row.Id),
                row => new(row.Id, row.SiteId),
                state => new Eden.Data.World.Mining.SavedMiningDeposit { Id = state.Id, SiteId = state.SiteId },
                ProjectSavedMiningDeposit,
                "Id,SiteId",
                reader => new(reader.GetInt64(1), reader.GetInt64(2)),
                state => new(state.Id),
                indexes: [new("Id", row => row.Id), new("SiteId", row => row.SiteId)],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Mining.SavedMiningSite, EconomySavedMiningSite>(
                "WorldMiningSite",
                row => new(row.Id),
                row => new(row.Id, row.DungeonId, row.ExpiresAt, row.Family, row.ReplaceAt, row.SystemId, row.X, row.Y, row.Z),
                state => new Eden.Data.World.Mining.SavedMiningSite
                {
                    Id = state.Id,
                    DungeonId = state.DungeonId,
                    ExpiresAt = state.ExpiresAt,
                    Family = state.Family,
                    ReplaceAt = state.ReplaceAt,
                    SystemId = state.SystemId,
                    X = state.X,
                    Y = state.Y,
                    Z = state.Z,
                },
                ProjectSavedMiningSite,
                "Id,DungeonId,ExpiresAt,Family,ReplaceAt,SystemId,X,Y,Z",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        reader.GetInt64(3),
                        (Eden.Gameplay.Mining.MiningFamily)reader.GetInt32(4),
                        reader.IsDBNull(5) ? null : reader.GetInt64(5),
                        reader.GetInt32(6),
                        reader.GetDouble(7),
                        reader.GetDouble(8),
                        reader.GetDouble(9)
                    ),
                state => new(state.Id),
                indexes: [new("Id", row => row.Id), new("DungeonId", row => row.DungeonId), new("SystemId", row => row.SystemId)],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Mining.SavedPersonalMiningLog, EconomySavedPersonalMiningLog>(
                "WorldPersonalMiningLog",
                row => new(row.Id),
                row =>
                    new(row.Id, row.CorporationId, row.Critical, row.Day, row.PilotId, row.Quantity, row.SystemId, row.TypeId, row.Wasted),
                state => new Eden.Data.World.Mining.SavedPersonalMiningLog
                {
                    Id = state.Id,
                    CorporationId = state.CorporationId,
                    Critical = state.Critical,
                    Day = state.Day,
                    PilotId = state.PilotId,
                    Quantity = state.Quantity,
                    SystemId = state.SystemId,
                    TypeId = state.TypeId,
                    Wasted = state.Wasted,
                },
                ProjectSavedPersonalMiningLog,
                "Id,CorporationId,Critical,Day,PilotId,Quantity,SystemId,TypeId,Wasted",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetInt64(5),
                        reader.GetInt64(6),
                        reader.GetInt32(7),
                        reader.GetInt32(8),
                        reader.GetInt64(9)
                    ),
                state => new(state.Id),
                indexes:
                [
                    new("Id", row => row.Id),
                    new("CorporationId", row => row.CorporationId),
                    new("Day", row => row.Day),
                    new("PilotId", row => row.PilotId),
                    new("SystemId", row => row.SystemId),
                    new("TypeId", row => row.TypeId),
                ],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Mining.WorldAsteroid, EconomyWorldAsteroid>(
                "WorldAsteroid",
                row => new(row.Id),
                row => new(row.Id, row.BeltId, row.Quantity, row.Radius, row.SystemId, row.TypeId, row.X, row.Y, row.Z),
                state => new Eden.Data.World.Mining.WorldAsteroid
                {
                    Id = state.Id,
                    BeltId = state.BeltId,
                    Quantity = state.Quantity,
                    Radius = state.Radius,
                    SystemId = state.SystemId,
                    TypeId = state.TypeId,
                    X = state.X,
                    Y = state.Y,
                    Z = state.Z,
                },
                ProjectWorldAsteroid,
                "Id,BeltId,Quantity,Radius,SystemId,TypeId,X,Y,Z",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        reader.GetInt64(3),
                        reader.GetFloat(4),
                        reader.GetInt32(5),
                        reader.GetInt32(6),
                        reader.GetDouble(7),
                        reader.GetDouble(8),
                        reader.GetDouble(9)
                    ),
                state => new(state.Id),
                indexes:
                [
                    new("Id", row => row.Id),
                    new("BeltId", row => row.BeltId),
                    new("SystemId", row => row.SystemId),
                    new("TypeId", row => row.TypeId),
                ],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Mining.WorldNpcMiningCargo, EconomyWorldNpcMiningCargo>(
                "WorldNpcMiningCargo",
                row => new(row.NpcId, row.TypeId),
                row => new(row.NpcId, row.TypeId, row.Quantity),
                state => new Eden.Data.World.Mining.WorldNpcMiningCargo
                {
                    NpcId = state.NpcId,
                    TypeId = state.TypeId,
                    Quantity = state.Quantity,
                },
                ProjectWorldNpcMiningCargo,
                "NpcId,TypeId,Quantity",
                reader => new(reader.GetInt64(1), reader.GetInt32(2), reader.GetInt64(3)),
                state => new(state.NpcId, state.TypeId),
                indexes: [new("NpcId", row => row.NpcId), new("TypeId", row => row.TypeId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Mining.WorldNpcMiningMember, EconomyWorldNpcMiningMember>(
                "WorldNpcMiningMember",
                row => new(row.NpcId),
                row =>
                    new(
                        row.NpcId,
                        row.AsteroidId,
                        row.Capacitor,
                        row.CapacitorAt,
                        row.EncounterId,
                        row.MiningDueAt,
                        row.MiningStartedAt,
                        row.SupportKind,
                        row.SupportReadyAt,
                        row.SupportStartedAt,
                        row.SupportTargetId,
                        row.Travel,
                        row.WarpArrivesAt,
                        row.WarpOriginX,
                        row.WarpOriginY,
                        row.WarpOriginZ,
                        row.WarpSpeed,
                        row.WarpStartedAt
                    ),
                state => new Eden.Data.World.Mining.WorldNpcMiningMember
                {
                    NpcId = state.NpcId,
                    AsteroidId = state.AsteroidId,
                    Capacitor = state.Capacitor,
                    CapacitorAt = state.CapacitorAt,
                    EncounterId = state.EncounterId,
                    MiningDueAt = state.MiningDueAt,
                    MiningStartedAt = state.MiningStartedAt,
                    SupportKind = state.SupportKind,
                    SupportReadyAt = state.SupportReadyAt,
                    SupportStartedAt = state.SupportStartedAt,
                    SupportTargetId = state.SupportTargetId,
                    Travel = state.Travel,
                    WarpArrivesAt = state.WarpArrivesAt,
                    WarpOriginX = state.WarpOriginX,
                    WarpOriginY = state.WarpOriginY,
                    WarpOriginZ = state.WarpOriginZ,
                    WarpSpeed = state.WarpSpeed,
                    WarpStartedAt = state.WarpStartedAt,
                },
                ProjectWorldNpcMiningMember,
                "NpcId,AsteroidId,Capacitor,CapacitorAt,EncounterId,MiningDueAt,MiningStartedAt,SupportKind,"
                    + "SupportReadyAt,SupportStartedAt,SupportTargetId,Travel,WarpArrivesAt,WarpOriginX,WarpOriginY,"
                    + "WarpOriginZ,WarpSpeed,WarpStartedAt",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.IsDBNull(2) ? null : reader.GetInt64(2),
                        reader.GetDouble(3),
                        reader.GetInt64(4),
                        reader.GetInt64(5),
                        reader.IsDBNull(6) ? null : reader.GetInt64(6),
                        reader.IsDBNull(7) ? null : reader.GetInt64(7),
                        (Eden.Gameplay.Mining.NpcActivityKind)reader.GetInt32(8),
                        reader.GetInt64(9),
                        reader.GetInt64(10),
                        reader.IsDBNull(11) ? null : reader.GetInt64(11),
                        (Eden.Gameplay.Mining.NpcMiningTravel)reader.GetInt32(12),
                        reader.IsDBNull(13) ? null : reader.GetInt64(13),
                        reader.GetDouble(14),
                        reader.GetDouble(15),
                        reader.GetDouble(16),
                        reader.GetDouble(17),
                        reader.IsDBNull(18) ? null : reader.GetInt64(18)
                    ),
                state => new(state.NpcId),
                indexes:
                [
                    new("NpcId", row => row.NpcId),
                    new("AsteroidId", row => row.AsteroidId),
                    new("EncounterId", row => row.EncounterId),
                    new("SupportKind", row => row.SupportKind),
                    new("SupportTargetId", row => row.SupportTargetId),
                ],
                primaryColumn: "NpcId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Mining.WorldNpcMiningOperation, EconomyWorldNpcMiningOperation>(
                "WorldNpcMiningOperation",
                row => new(row.EncounterId),
                row =>
                    new(
                        row.EncounterId,
                        row.AlertX,
                        row.AlertY,
                        row.AlertZ,
                        row.BeltId,
                        row.CorporationId,
                        row.FactionId,
                        row.HaulGeneration,
                        row.PickupAt,
                        row.ResetAt,
                        row.ResponseAt,
                        row.Stage,
                        row.StationId,
                        row.SystemId,
                        row.ThreatShipId
                    ),
                state => new Eden.Data.World.Mining.WorldNpcMiningOperation
                {
                    EncounterId = state.EncounterId,
                    AlertX = state.AlertX,
                    AlertY = state.AlertY,
                    AlertZ = state.AlertZ,
                    BeltId = state.BeltId,
                    CorporationId = state.CorporationId,
                    FactionId = state.FactionId,
                    HaulGeneration = state.HaulGeneration,
                    PickupAt = state.PickupAt,
                    ResetAt = state.ResetAt,
                    ResponseAt = state.ResponseAt,
                    Stage = state.Stage,
                    StationId = state.StationId,
                    SystemId = state.SystemId,
                    ThreatShipId = state.ThreatShipId,
                },
                ProjectWorldNpcMiningOperation,
                "EncounterId,AlertX,AlertY,AlertZ,BeltId,CorporationId,FactionId,HaulGeneration,PickupAt,ResetAt,"
                    + "ResponseAt,Stage,StationId,SystemId,ThreatShipId",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetDouble(2),
                        reader.GetDouble(3),
                        reader.GetDouble(4),
                        reader.GetInt32(5),
                        reader.GetInt32(6),
                        reader.GetInt32(7),
                        reader.GetInt32(8),
                        reader.GetInt64(9),
                        reader.IsDBNull(10) ? null : reader.GetInt64(10),
                        reader.IsDBNull(11) ? null : reader.GetInt64(11),
                        (Eden.Gameplay.Mining.NpcMiningStage)reader.GetInt32(12),
                        reader.IsDBNull(13) ? null : reader.GetInt32(13),
                        reader.GetInt32(14),
                        reader.IsDBNull(15) ? null : reader.GetInt64(15)
                    ),
                state => new(state.EncounterId),
                indexes:
                [
                    new("EncounterId", row => row.EncounterId),
                    new("BeltId", row => row.BeltId),
                    new("CorporationId", row => row.CorporationId),
                    new("FactionId", row => row.FactionId),
                    new("StationId", row => row.StationId),
                    new("SystemId", row => row.SystemId),
                    new("ThreatShipId", row => row.ThreatShipId),
                ],
                primaryColumn: "EncounterId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Propulsion.SpatialNavigationState, EconomySpatialNavigationState>(
                "WorldSpatialNavigation",
                row => new(row.ShipId),
                row =>
                    new(
                        row.ShipId,
                        row.CloakModuleId,
                        row.CloakReactivationMilliseconds,
                        row.CloakTargetingMilliseconds,
                        row.HeadingX,
                        row.HeadingY,
                        row.HeadingZ,
                        row.JumpEndsAt,
                        row.JumpModuleId,
                        row.JumpStartedAt,
                        row.TargetingReadyAt,
                        row.VelocityX,
                        row.VelocityY,
                        row.VelocityZ
                    ),
                state => new Eden.Data.World.Propulsion.SpatialNavigationState
                {
                    ShipId = state.ShipId,
                    CloakModuleId = state.CloakModuleId,
                    CloakReactivationMilliseconds = state.CloakReactivationMilliseconds,
                    CloakTargetingMilliseconds = state.CloakTargetingMilliseconds,
                    HeadingX = state.HeadingX,
                    HeadingY = state.HeadingY,
                    HeadingZ = state.HeadingZ,
                    JumpEndsAt = state.JumpEndsAt,
                    JumpModuleId = state.JumpModuleId,
                    JumpStartedAt = state.JumpStartedAt,
                    TargetingReadyAt = state.TargetingReadyAt,
                    VelocityX = state.VelocityX,
                    VelocityY = state.VelocityY,
                    VelocityZ = state.VelocityZ,
                },
                ProjectSpatialNavigationState,
                "ShipId,CloakModuleId,CloakReactivationMilliseconds,CloakTargetingMilliseconds,HeadingX,HeadingY,"
                    + "HeadingZ,JumpEndsAt,JumpModuleId,JumpStartedAt,TargetingReadyAt,VelocityX,VelocityY,VelocityZ",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetDouble(3),
                        reader.GetDouble(4),
                        reader.GetDouble(5),
                        reader.GetDouble(6),
                        reader.GetDouble(7),
                        reader.GetInt64(8),
                        reader.GetInt64(9),
                        reader.GetInt64(10),
                        reader.GetInt64(11),
                        reader.GetDouble(12),
                        reader.GetDouble(13),
                        reader.GetDouble(14)
                    ),
                state => new(state.ShipId),
                indexes:
                [
                    new("ShipId", row => row.ShipId),
                    new("CloakModuleId", row => row.CloakModuleId),
                    new("JumpModuleId", row => row.JumpModuleId),
                ],
                primaryColumn: "ShipId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Propulsion.WorldTackle, EconomyWorldTackle>(
                "WorldTackle",
                row => new(row.ModuleId),
                row =>
                    new(
                        row.ModuleId,
                        row.CapacitorNeed,
                        row.DurationMilliseconds,
                        row.Effect,
                        row.ExpiresAt,
                        row.MwdBlock,
                        row.PilotId,
                        row.Range,
                        row.SourceShipId,
                        row.StartedAt,
                        row.Strength,
                        row.SystemId,
                        row.TargetId,
                        row.TargetKind,
                        row.TypeId,
                        row.WebPercent
                    ),
                state => new Eden.Data.World.Propulsion.WorldTackle
                {
                    ModuleId = state.ModuleId,
                    CapacitorNeed = state.CapacitorNeed,
                    DurationMilliseconds = state.DurationMilliseconds,
                    Effect = state.Effect,
                    ExpiresAt = state.ExpiresAt,
                    MwdBlock = state.MwdBlock,
                    PilotId = state.PilotId,
                    Range = state.Range,
                    SourceShipId = state.SourceShipId,
                    StartedAt = state.StartedAt,
                    Strength = state.Strength,
                    SystemId = state.SystemId,
                    TargetId = state.TargetId,
                    TargetKind = state.TargetKind,
                    TypeId = state.TypeId,
                    WebPercent = state.WebPercent,
                },
                ProjectWorldTackle,
                "ModuleId,CapacitorNeed,DurationMilliseconds,Effect,ExpiresAt,MwdBlock,PilotId,Range,SourceShipId,"
                    + "StartedAt,Strength,SystemId,TargetId,TargetKind,TypeId,WebPercent",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetDouble(2),
                        reader.GetDouble(3),
                        (Eden.Gameplay.Inventory.DogmaEffect)reader.GetInt32(4),
                        reader.GetInt64(5),
                        reader.GetDouble(6),
                        reader.GetInt64(7),
                        reader.GetDouble(8),
                        reader.GetInt64(9),
                        reader.GetInt64(10),
                        reader.GetDouble(11),
                        reader.GetInt32(12),
                        reader.GetInt64(13),
                        (Eden.Gameplay.Propulsion.TackleTargetKind)reader.GetInt32(14),
                        reader.GetInt32(15),
                        reader.GetDouble(16)
                    ),
                state => new(state.ModuleId),
                changed: TackleChanged,
                indexes:
                [
                    new("ModuleId", row => row.ModuleId),
                    new("PilotId", row => row.PilotId),
                    new("SourceShipId", row => row.SourceShipId),
                    new("SystemId", row => row.SystemId),
                    new("TargetId", row => row.TargetId),
                    new("TargetKind", row => row.TargetKind),
                    new("TypeId", row => row.TypeId),
                ],
                primaryColumn: "ModuleId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Repair.PendingLocalRepair, EconomyPendingLocalRepair>(
                "WorldArmorRepair",
                row => new(row.ModuleId),
                row => new(row.ModuleId, row.Amount, row.CompletesAt, row.Layer, row.ShipId, row.StartedAt),
                state => new Eden.Data.World.Repair.PendingLocalRepair
                {
                    ModuleId = state.ModuleId,
                    Amount = state.Amount,
                    CompletesAt = state.CompletesAt,
                    Layer = state.Layer,
                    ShipId = state.ShipId,
                    StartedAt = state.StartedAt,
                },
                ProjectPendingLocalRepair,
                "ModuleId,Amount,CompletesAt,Layer,ShipId,StartedAt",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetDouble(2),
                        reader.GetInt64(3),
                        (Eden.Gameplay.Repair.RepairLayer)reader.GetInt32(4),
                        reader.GetInt64(5),
                        reader.GetInt64(6)
                    ),
                state => new(state.ModuleId),
                indexes: [new("ModuleId", row => row.ModuleId), new("ShipId", row => row.ShipId)],
                primaryColumn: "ModuleId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Repair.StationRepairPayment, EconomyStationRepairPayment>(
                "WorldRepairPayment",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.AmountCents,
                        row.BalanceCents,
                        row.PilotId,
                        row.RepairsJson,
                        row.StationId,
                        row.StationOwnerId,
                        row.Time
                    ),
                state => new Eden.Data.World.Repair.StationRepairPayment
                {
                    Id = state.Id,
                    AmountCents = state.AmountCents,
                    BalanceCents = state.BalanceCents,
                    PilotId = state.PilotId,
                    RepairsJson = state.RepairsJson,
                    StationId = state.StationId,
                    StationOwnerId = state.StationOwnerId,
                    Time = state.Time,
                },
                ProjectStationRepairPayment,
                "Id,AmountCents,BalanceCents,PilotId,RepairsJson,StationId,StationOwnerId,Time",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetString(5),
                        reader.GetInt64(6),
                        reader.GetInt64(7),
                        reader.GetInt64(8)
                    ),
                state => new(state.Id),
                indexes:
                [
                    new("Id", row => row.Id),
                    new("PilotId", row => row.PilotId),
                    new("StationId", row => row.StationId),
                    new("StationOwnerId", row => row.StationOwnerId),
                ],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Repair.WorldRemoteAssistance, EconomyWorldRemoteAssistance>(
                "WorldRemoteAssistance",
                row => new(row.ModuleId),
                row => new(row.ModuleId, row.ContactJson, row.PilotId, row.SourceShipId, row.SystemId, row.TargetId),
                state => new Eden.Data.World.Repair.WorldRemoteAssistance
                {
                    ModuleId = state.ModuleId,
                    ContactJson = state.ContactJson,
                    PilotId = state.PilotId,
                    SourceShipId = state.SourceShipId,
                    SystemId = state.SystemId,
                    TargetId = state.TargetId,
                },
                ProjectWorldRemoteAssistance,
                "ModuleId,ContactJson,PilotId,SourceShipId,SystemId,TargetId",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetString(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetInt32(5),
                        reader.GetInt64(6)
                    ),
                state => new(state.ModuleId),
                indexes:
                [
                    new("ModuleId", row => row.ModuleId),
                    new("PilotId", row => row.PilotId),
                    new("SourceShipId", row => row.SourceShipId),
                    new("SystemId", row => row.SystemId),
                    new("TargetId", row => row.TargetId),
                ],
                primaryColumn: "ModuleId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Repair.WorldRemoteRepair, EconomyWorldRemoteRepair>(
                "WorldRemoteRepair",
                row => new(row.ModuleId),
                row => new(row.ModuleId, row.CycleJson, row.PilotId, row.SourceShipId, row.SystemId, row.TargetId),
                state => new Eden.Data.World.Repair.WorldRemoteRepair
                {
                    ModuleId = state.ModuleId,
                    CycleJson = state.CycleJson,
                    PilotId = state.PilotId,
                    SourceShipId = state.SourceShipId,
                    SystemId = state.SystemId,
                    TargetId = state.TargetId,
                },
                ProjectWorldRemoteRepair,
                "ModuleId,CycleJson,PilotId,SourceShipId,SystemId,TargetId",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetString(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetInt32(5),
                        reader.GetInt64(6)
                    ),
                state => new(state.ModuleId),
                indexes:
                [
                    new("ModuleId", row => row.ModuleId),
                    new("PilotId", row => row.PilotId),
                    new("SourceShipId", row => row.SourceShipId),
                    new("SystemId", row => row.SystemId),
                    new("TargetId", row => row.TargetId),
                ],
                primaryColumn: "ModuleId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Skills.SavedFreeSkillPoints, EconomySavedFreeSkillPoints>(
                "WorldFreeSkillPoints",
                row => new(row.PilotId),
                row => new(row.PilotId, row.Points, row.Revision),
                state => new Eden.Data.World.Skills.SavedFreeSkillPoints
                {
                    PilotId = state.PilotId,
                    Points = state.Points,
                    Revision = state.Revision,
                },
                ProjectSavedFreeSkillPoints,
                "PilotId,Points,Revision",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)),
                state => new(state.PilotId),
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: "PilotId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Structures.SavedStructure, EconomySavedStructure>(
                "WorldStructure",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.CorporationId,
                        row.DeployedAt,
                        row.Description,
                        row.HasCore,
                        row.LowPowerAt,
                        row.MoonId,
                        row.Name,
                        row.PublicAccess,
                        row.ReinforceHour,
                        row.ReinforceWeekday,
                        row.State,
                        row.StateEndsAt,
                        row.StateStartedAt,
                        row.SystemId,
                        row.TypeId,
                        row.UnanchorAt,
                        row.X,
                        row.Y,
                        row.Yaw,
                        row.Z
                    ),
                state => new Eden.Data.World.Structures.SavedStructure
                {
                    Id = state.Id,
                    CorporationId = state.CorporationId,
                    DeployedAt = state.DeployedAt,
                    Description = state.Description,
                    HasCore = state.HasCore,
                    LowPowerAt = state.LowPowerAt,
                    MoonId = state.MoonId,
                    Name = state.Name,
                    PublicAccess = state.PublicAccess,
                    ReinforceHour = state.ReinforceHour,
                    ReinforceWeekday = state.ReinforceWeekday,
                    State = state.State,
                    StateEndsAt = state.StateEndsAt,
                    StateStartedAt = state.StateStartedAt,
                    SystemId = state.SystemId,
                    TypeId = state.TypeId,
                    UnanchorAt = state.UnanchorAt,
                    X = state.X,
                    Y = state.Y,
                    Yaw = state.Yaw,
                    Z = state.Z,
                },
                ProjectSavedStructure,
                "Id,CorporationId,DeployedAt,Description,HasCore,LowPowerAt,MoonId,Name,PublicAccess,ReinforceHour,"
                    + "ReinforceWeekday,State,StateEndsAt,StateStartedAt,SystemId,TypeId,UnanchorAt,X,Y,Yaw,Z",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        reader.GetInt64(3),
                        reader.GetString(4),
                        reader.GetBoolean(5),
                        reader.IsDBNull(6) ? null : reader.GetInt64(6),
                        reader.IsDBNull(7) ? null : reader.GetInt32(7),
                        reader.GetString(8),
                        reader.GetBoolean(9),
                        reader.GetInt32(10),
                        reader.GetInt32(11),
                        (Eden.Gameplay.Structures.StructureState)reader.GetInt32(12),
                        reader.IsDBNull(13) ? null : reader.GetInt64(13),
                        reader.GetInt64(14),
                        reader.GetInt32(15),
                        reader.GetInt32(16),
                        reader.IsDBNull(17) ? null : reader.GetInt64(17),
                        reader.GetDouble(18),
                        reader.GetDouble(19),
                        reader.GetDouble(20),
                        reader.GetDouble(21)
                    ),
                state => new(state.Id),
                indexes:
                [
                    new("Id", row => row.Id),
                    new("CorporationId", row => row.CorporationId),
                    new("MoonId", row => row.MoonId),
                    new("SystemId", row => row.SystemId),
                    new("TypeId", row => row.TypeId),
                ],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Structures.SavedStructureAccess, EconomySavedStructureAccess>(
                "WorldStructureAccess",
                row => new(row.StructureId, row.MemberId),
                row => new(row.StructureId, row.MemberId, row.Allowed),
                state => new Eden.Data.World.Structures.SavedStructureAccess
                {
                    StructureId = state.StructureId,
                    MemberId = state.MemberId,
                    Allowed = state.Allowed,
                },
                ProjectSavedStructureAccess,
                "StructureId,MemberId,Allowed",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetBoolean(3)),
                state => new(state.StructureId, state.MemberId),
                indexes: [new("StructureId", row => row.StructureId), new("MemberId", row => row.MemberId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Thermal.SavedModuleCondition, EconomySavedModuleCondition>(
                "WorldModuleCondition",
                row => new(row.ModuleId),
                row => new(row.ModuleId, row.Damage),
                state => new Eden.Data.World.Thermal.SavedModuleCondition { ModuleId = state.ModuleId, Damage = state.Damage },
                ProjectSavedModuleCondition,
                "ModuleId,Damage",
                reader => new(reader.GetInt64(1), reader.GetDouble(2)),
                state => new(state.ModuleId),
                indexes: [new("ModuleId", row => row.ModuleId)],
                primaryColumn: "ModuleId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Thermal.SavedNaniteRepair, EconomySavedNaniteRepair>(
                "WorldNaniteRepair",
                row => new(row.ModuleId),
                row =>
                    new(
                        row.ModuleId,
                        row.CompletesAt,
                        row.Generation,
                        row.InitialDamage,
                        row.InitialHitPoints,
                        row.PasteUnits,
                        row.PasteUnitsPerDamage,
                        row.PilotId,
                        row.ShipId,
                        row.StartedAt
                    ),
                state => new Eden.Data.World.Thermal.SavedNaniteRepair
                {
                    ModuleId = state.ModuleId,
                    CompletesAt = state.CompletesAt,
                    Generation = state.Generation,
                    InitialDamage = state.InitialDamage,
                    InitialHitPoints = state.InitialHitPoints,
                    PasteUnits = state.PasteUnits,
                    PasteUnitsPerDamage = state.PasteUnitsPerDamage,
                    PilotId = state.PilotId,
                    ShipId = state.ShipId,
                    StartedAt = state.StartedAt,
                },
                ProjectSavedNaniteRepair,
                "ModuleId,CompletesAt,Generation,InitialDamage,InitialHitPoints,PasteUnits,PasteUnitsPerDamage,"
                    + "PilotId,ShipId,StartedAt",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetGuid(3),
                        reader.GetDouble(4),
                        reader.GetDouble(5),
                        reader.GetInt64(6),
                        reader.GetDouble(7),
                        reader.GetInt64(8),
                        reader.GetInt64(9),
                        reader.GetInt64(10)
                    ),
                state => new(state.ModuleId),
                indexes:
                [
                    new("ModuleId", row => row.ModuleId),
                    new("Generation", row => row.Generation),
                    new("PilotId", row => row.PilotId),
                    new("ShipId", row => row.ShipId),
                ],
                primaryColumn: "ModuleId"
            )
        );
        Register(
            new EconomyTable<Eden.Data.World.Thermal.SavedShipHeat, EconomySavedShipHeat>(
                "WorldShipHeat",
                row => new(row.ShipId),
                row => new(row.ShipId, row.StateJson),
                state => new Eden.Data.World.Thermal.SavedShipHeat { ShipId = state.ShipId, StateJson = state.StateJson },
                ProjectSavedShipHeat,
                "ShipId,StateJson",
                reader => new(reader.GetInt64(1), reader.GetString(2)),
                state => new(state.ShipId),
                indexes: [new("ShipId", row => row.ShipId)],
                primaryColumn: "ShipId"
            )
        );
    }
}

internal sealed partial class EconomyTransaction
{
    private void AssignBotIdentity<TRow>(TRow row)
        where TRow : class
    {
        switch (row)
        {
            case Eden.Data.World.Agents.SavedMission value:
                value.Id = value.Id == 0 ? Next("WorldMission") : value.Id;
                Reserve("WorldMission", value.Id);
                break;
            case Eden.Data.World.Capsuleers.SavedCapsuleerClock value:
                value.Id = value.Id == 0 ? checked((int)Next("WorldCapsuleerClock")) : value.Id;
                Reserve("WorldCapsuleerClock", value.Id);
                break;
            case Eden.Data.World.Capsuleers.SavedCapsuleerCommunity value:
                value.Id = value.Id == 0 ? checked((int)Next("WorldCapsuleerCommunity")) : value.Id;
                Reserve("WorldCapsuleerCommunity", value.Id);
                break;
            case Eden.Data.World.Capsuleers.SavedCapsuleerHauler value:
                value.Id = value.Id == 0 ? checked((int)Next("WorldCapsuleerHauler")) : value.Id;
                Reserve("WorldCapsuleerHauler", value.Id);
                break;
            case Eden.Data.World.Capsuleers.SavedCapsuleerProvisioning value:
                value.Id = value.Id == 0 ? checked((int)Next("WorldCapsuleerProvisioning")) : value.Id;
                Reserve("WorldCapsuleerProvisioning", value.Id);
                break;
            case Eden.Data.World.Characters.SavedCombatReport value:
                value.Id = value.Id == 0 ? Next("WorldCombatReport") : value.Id;
                Reserve("WorldCombatReport", value.Id);
                break;
            case Eden.Data.World.Characters.SavedStandingTransaction value:
                value.Id = value.Id == 0 ? Next("WorldStandingTransaction") : value.Id;
                Reserve("WorldStandingTransaction", value.Id);
                break;
            case Eden.Data.World.Combat.NpcBounty value:
                value.Id = value.Id == 0 ? Next("WorldNpcBounty") : value.Id;
                Reserve("WorldNpcBounty", value.Id);
                break;
            case Eden.Data.World.Combat.Npcs.WorldNpc value:
                value.Id = value.Id == 0 ? Next("WorldNpc") : value.Id;
                Reserve("WorldNpc", value.Id);
                break;
            case Eden.Data.World.Combat.SavedSecurityTransaction value:
                value.Id = value.Id == 0 ? Next("WorldSecurityTransaction") : value.Id;
                Reserve("WorldSecurityTransaction", value.Id);
                break;
            case Eden.Data.World.Characters.SavedHomeCloneTransfer value:
                value.Id = value.Id == 0 ? Next("WorldHomeCloneTransfer") : value.Id;
                Reserve("WorldHomeCloneTransfer", value.Id);
                break;
            case Eden.Data.World.Skills.TrainingLog value:
                value.Id = value.Id == 0 ? Next("WorldSkillHistory") : value.Id;
                Reserve("WorldSkillHistory", value.Id);
                break;
            case Eden.Data.World.Combat.WorldMissile value:
                value.Id = value.Id == 0 ? Next("WorldMissile") : value.Id;
                Reserve("WorldMissile", value.Id);
                break;
            case Eden.Data.World.Corporations.SavedWalletLedger value:
                value.Id = value.Id == 0 ? Next("WorldWalletLedger") : value.Id;
                Reserve("WorldWalletLedger", value.Id);
                break;
            case Eden.Data.World.Economy.SavedEconomicPolicy value:
                value.Id = value.Id == 0 ? checked((int)Next("WorldEconomicPolicy")) : value.Id;
                Reserve("WorldEconomicPolicy", value.Id);
                break;
            case Eden.Data.World.Exploration.WorldCombatAnomaly value:
                value.Id = value.Id == 0 ? Next("WorldCombatAnomaly") : value.Id;
                Reserve("WorldCombatAnomaly", value.Id);
                break;
            case Eden.Data.World.Insurance.SavedInsuranceLedger value:
                value.Id = value.Id == 0 ? Next("WorldInsuranceLedger") : value.Id;
                Reserve("WorldInsuranceLedger", value.Id);
                break;
            case Eden.Data.World.Insurance.SavedInsurancePolicy value:
                value.Id = value.Id == 0 ? Next("WorldInsurancePolicy") : value.Id;
                Reserve("WorldInsurancePolicy", value.Id);
                break;
            case Eden.Data.World.Mining.SavedMiningSite value:
                value.Id = value.Id == 0 ? Next("WorldMiningSite") : value.Id;
                Reserve("WorldMiningSite", value.Id);
                break;
            case Eden.Data.World.Mining.SavedPersonalMiningLog value:
                value.Id = value.Id == 0 ? Next("WorldPersonalMiningLog") : value.Id;
                Reserve("WorldPersonalMiningLog", value.Id);
                break;
            case Eden.Data.World.Repair.StationRepairPayment value:
                value.Id = value.Id == 0 ? Next("WorldRepairPayment") : value.Id;
                Reserve("WorldRepairPayment", value.Id);
                break;
            case Eden.Data.World.Structures.SavedStructure value:
                value.Id = value.Id == 0 ? Next("WorldStructure") : value.Id;
                Reserve("WorldStructure", value.Id);
                break;
        }
    }

    private void ValidateBotRows()
    {
        foreach (var row in Retained<Eden.Data.World.Agents.SavedMission>())
        {
            if (
                !double.IsFinite(row.CorporationFraction)
                || !double.IsFinite(row.FactionFraction)
                || !double.IsFinite(row.X)
                || !double.IsFinite(row.Y)
                || !double.IsFinite(row.Z)
            )
            {
                throw new InvalidDataException("Invalid resident SavedMission values.");
            }
            if (Set<Eden.Data.World.Characters.Pilot>().Find(row.PilotId) is null)
            {
                throw new InvalidDataException("Missing resident Pilot reference.");
            }
            if (
                row.ClosedAt == null
                && Set<Eden.Data.World.Agents.SavedMission>()
                    .Any(other =>
                        other.ClosedAt == null && other.PilotId == row.PilotId && other.AgentId == row.AgentId && (other.Id != row.Id)
                    )
            )
            {
                throw new InvalidDataException("Duplicate resident SavedMission unique value.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Agents.SavedMissionMember>())
        {
            if (Set<Eden.Data.World.Agents.SavedMission>().Find(row.MissionId) is null)
            {
                throw new InvalidDataException("Missing resident SavedMission reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Agents.SavedMissionRoom>())
        {
            if (Set<Eden.Data.World.Agents.SavedMission>().Find(row.MissionId) is null)
            {
                throw new InvalidDataException("Missing resident SavedMission reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Agents.SavedMissionStructure>())
        {
            if (Set<Eden.Data.World.Agents.SavedMission>().Find(row.MissionId) is null)
            {
                throw new InvalidDataException("Missing resident SavedMission reference.");
            }
            if (
                true
                && Set<Eden.Data.World.Agents.SavedMissionStructure>()
                    .Any(other => true && other.MissionId == row.MissionId && other.ObjectId == row.ObjectId && (other.NpcId != row.NpcId))
            )
            {
                throw new InvalidDataException("Duplicate resident SavedMissionStructure unique value.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Capsuleers.SavedCapsuleerAdmission>()) { }
        foreach (var row in Retained<Eden.Data.World.Capsuleers.SavedCapsuleerAgent>())
        {
            if (Set<Eden.Data.World.Capsuleers.SavedCapsuleerPopulation>().Find(row.CommunitySystemId) is null)
            {
                throw new InvalidDataException("Missing resident SavedCapsuleerPopulation reference.");
            }
            if (Set<Eden.Data.World.Characters.Pilot>().Find(row.PilotId) is null)
            {
                throw new InvalidDataException("Missing resident Pilot reference.");
            }
            if (
                true
                && Set<Eden.Data.World.Capsuleers.SavedCapsuleerAgent>()
                    .Any(other =>
                        true
                        && other.CommunitySystemId == row.CommunitySystemId
                        && other.Career == row.Career
                        && other.Slot == row.Slot
                        && (other.PilotId != row.PilotId)
                    )
            )
            {
                throw new InvalidDataException("Duplicate resident SavedCapsuleerAgent unique value.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Capsuleers.SavedCapsuleerClock>()) { }
        foreach (var row in Retained<Eden.Data.World.Capsuleers.SavedCapsuleerCoarsePilot>()) { }
        foreach (var row in Retained<Eden.Data.World.Capsuleers.SavedCapsuleerCoarseWork>()) { }
        foreach (var row in Retained<Eden.Data.World.Capsuleers.SavedCapsuleerCommunity>())
        {
            if (row.PilotId is not null && Set<Eden.Data.World.Characters.Pilot>().Find(row.PilotId.Value) is null)
            {
                throw new InvalidDataException("Missing resident Pilot reference.");
            }
            if (
                true
                && row.PilotId is not null
                && Set<Eden.Data.World.Capsuleers.SavedCapsuleerCommunity>()
                    .Any(other => true && other.PilotId == row.PilotId && (other.Id != row.Id))
            )
            {
                throw new InvalidDataException("Duplicate resident SavedCapsuleerCommunity unique value.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Capsuleers.SavedCapsuleerHauler>())
        {
            if (Set<Eden.Data.World.Characters.Pilot>().Find(row.PilotId) is null)
            {
                throw new InvalidDataException("Missing resident Pilot reference.");
            }
            if (
                true
                && Set<Eden.Data.World.Capsuleers.SavedCapsuleerHauler>()
                    .Any(other => true && other.PilotId == row.PilotId && (other.Id != row.Id))
            )
            {
                throw new InvalidDataException("Duplicate resident SavedCapsuleerHauler unique value.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Capsuleers.SavedCapsuleerJob>())
        {
            if (Set<Eden.Data.World.Capsuleers.SavedCapsuleerAgent>().Find(row.PilotId) is null)
            {
                throw new InvalidDataException("Missing resident SavedCapsuleerAgent reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Capsuleers.SavedCapsuleerPopulation>()) { }
        foreach (var row in Retained<Eden.Data.World.Capsuleers.SavedCapsuleerProvisioning>()) { }
        foreach (var row in Retained<Eden.Data.World.Capsuleers.SavedCapsuleerSimulation>()) { }
        foreach (var row in Retained<Eden.Data.World.Capsuleers.SavedCapsuleerSupplies>()) { }
        foreach (var row in Retained<Eden.Data.World.Capsuleers.SavedCapsuleerSuspension>())
        {
            if (Set<Eden.Data.World.Capsuleers.SavedCapsuleerAgent>().Find(row.PilotId) is null)
            {
                throw new InvalidDataException("Missing resident SavedCapsuleerAgent reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Capsuleers.SavedCapsuleerSystem>()) { }
        foreach (var row in Retained<Eden.Data.World.Capsuleers.SavedTrafficCorrection>()) { }
        foreach (var row in Retained<Eden.Data.World.Capsuleers.SavedTrafficIdentity>())
        {
            if (
                true
                && Set<Eden.Data.World.Capsuleers.SavedTrafficIdentity>()
                    .Any(other => true && other.CharacterId == row.CharacterId && (other.PilotId != row.PilotId))
            )
            {
                throw new InvalidDataException("Duplicate resident SavedTrafficIdentity unique value.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Characters.SavedCharacterDeletion>())
        {
            if (Set<Eden.Data.World.Characters.Pilot>().Find(row.PilotId) is null)
            {
                throw new InvalidDataException("Missing resident Pilot reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Characters.SavedCombatDamage>())
        {
            if (!double.IsFinite(row.Damage))
            {
                throw new InvalidDataException("Invalid resident SavedCombatDamage values.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Characters.SavedCombatReport>())
        {
            if (Set<Eden.Data.World.Characters.Pilot>().Find(row.PilotId) is null)
            {
                throw new InvalidDataException("Missing resident Pilot reference.");
            }
            if (
                true
                && Set<Eden.Data.World.Characters.SavedCombatReport>()
                    .Any(other => true && other.SourceKey == row.SourceKey && (other.Id != row.Id))
            )
            {
                throw new InvalidDataException("Duplicate resident SavedCombatReport unique value.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Characters.SavedHomeStation>())
        {
            if (Set<Eden.Data.World.Characters.Pilot>().Find(row.PilotId) is null)
            {
                throw new InvalidDataException("Missing resident Pilot reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Characters.SavedStandingTransaction>())
        {
            if (!double.IsFinite(row.After) || !double.IsFinite(row.Before))
            {
                throw new InvalidDataException("Invalid resident SavedStandingTransaction values.");
            }
            if (Set<Eden.Data.World.Characters.Pilot>().Find(row.PilotId) is null)
            {
                throw new InvalidDataException("Missing resident Pilot reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Combat.ModuleCooldown>())
        {
            if (Set<Eden.Data.World.Inventory.OwnedItem>().Find(row.ModuleId) is null)
            {
                throw new InvalidDataException("Missing resident OwnedItem reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Combat.NpcAggression>())
        {
            if (Set<Eden.Data.World.Combat.Npcs.WorldNpc>().Find(row.NpcId) is null)
            {
                throw new InvalidDataException("Missing resident WorldNpc reference.");
            }
            if (Set<Eden.Data.World.Inventory.Ship>().Find(row.TargetShipId) is null)
            {
                throw new InvalidDataException("Missing resident Ship reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Combat.NpcBounty>())
        {
            if (!(row.AmountCents > 0 && row.BalanceCents >= 0))
            {
                throw new InvalidDataException("Invalid resident NpcBounty values.");
            }
            if (Set<Eden.Data.World.Combat.Npcs.WorldNpc>().Find(row.NpcId) is null)
            {
                throw new InvalidDataException("Missing resident WorldNpc reference.");
            }
            if (Set<Eden.Data.World.Characters.Pilot>().Find(row.PilotId) is null)
            {
                throw new InvalidDataException("Missing resident Pilot reference.");
            }
            if (true && Set<Eden.Data.World.Combat.NpcBounty>().Any(other => true && other.NpcId == row.NpcId && (other.Id != row.Id)))
            {
                throw new InvalidDataException("Duplicate resident NpcBounty unique value.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Combat.Npcs.WorldNpc>())
        {
            if (
                row.AcquisitionRange is double finiteAcquisitionRange && !double.IsFinite(finiteAcquisitionRange)
                || !double.IsFinite(row.ArmorDamage)
                || row.HomeX is double finiteHomeX && !double.IsFinite(finiteHomeX)
                || row.HomeY is double finiteHomeY && !double.IsFinite(finiteHomeY)
                || row.HomeZ is double finiteHomeZ && !double.IsFinite(finiteHomeZ)
                || !double.IsFinite(row.ShieldCharge)
                || !double.IsFinite(row.StructureDamage)
                || !double.IsFinite(row.VelocityX)
                || !double.IsFinite(row.VelocityY)
                || !double.IsFinite(row.VelocityZ)
                || !double.IsFinite(row.X)
                || !double.IsFinite(row.Y)
                || !double.IsFinite(row.Z)
                || !(row.ShieldCharge >= 0 && row.ArmorDamage >= 0 && row.StructureDamage >= 0)
            )
            {
                throw new InvalidDataException("Invalid resident WorldNpc values.");
            }
            if (
                row.Role != 0
                && row.DestroyedAt == null
                && row.RetiredAt == null
                && Set<Eden.Data.World.Combat.Npcs.WorldNpc>()
                    .Any(other =>
                        other.Role != 0
                        && other.DestroyedAt == null
                        && other.RetiredAt == null
                        && other.EncounterId == row.EncounterId
                        && other.SpawnSlot == row.SpawnSlot
                        && (other.Id != row.Id)
                    )
            )
            {
                throw new InvalidDataException("Duplicate resident WorldNpc unique value.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Combat.Npcs.WorldNpcEncounter>())
        {
            if (!double.IsFinite(row.HomeX) || !double.IsFinite(row.HomeY) || !double.IsFinite(row.HomeZ))
            {
                throw new InvalidDataException("Invalid resident WorldNpcEncounter values.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Combat.Npcs.WorldNpcEnergy>())
        {
            if (!double.IsFinite(row.Charge) || !(row.Charge >= 0))
            {
                throw new InvalidDataException("Invalid resident WorldNpcEnergy values.");
            }
            if (Set<Eden.Data.World.Combat.Npcs.WorldNpc>().Find(row.NpcId) is null)
            {
                throw new InvalidDataException("Missing resident WorldNpc reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Combat.Npcs.WorldNpcEwar>())
        {
            if (Set<Eden.Data.World.Combat.NpcAggression>().Find(row.NpcId) is null)
            {
                throw new InvalidDataException("Missing resident NpcAggression reference.");
            }
            if (Set<Eden.Data.World.Inventory.Ship>().Find(row.TargetShipId) is null)
            {
                throw new InvalidDataException("Missing resident Ship reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Combat.PendingSecurityReward>())
        {
            if (!double.IsFinite(row.Fraction) || !double.IsFinite(row.Maximum))
            {
                throw new InvalidDataException("Invalid resident PendingSecurityReward values.");
            }
            if (Set<Eden.Data.World.Characters.Pilot>().Find(row.PilotId) is null)
            {
                throw new InvalidDataException("Missing resident Pilot reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Combat.SavedConcordResponse>()) { }
        foreach (var row in Retained<Eden.Data.World.Combat.SavedCrimewatch>()) { }
        foreach (var row in Retained<Eden.Data.World.Combat.SavedEngagement>()) { }
        foreach (var row in Retained<Eden.Data.World.Combat.SavedLogoff>()) { }
        foreach (var row in Retained<Eden.Data.World.Combat.SavedPilotHostility>()) { }
        foreach (var row in Retained<Eden.Data.World.Combat.SavedSecurityStatus>())
        {
            if (!double.IsFinite(row.Value))
            {
                throw new InvalidDataException("Invalid resident SavedSecurityStatus values.");
            }
            if (Set<Eden.Data.World.Characters.Pilot>().Find(row.PilotId) is null)
            {
                throw new InvalidDataException("Missing resident Pilot reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Combat.SavedSecurityTransaction>())
        {
            if (!double.IsFinite(row.Modification) || !double.IsFinite(row.NewValue))
            {
                throw new InvalidDataException("Invalid resident SavedSecurityTransaction values.");
            }
            if (Set<Eden.Data.World.Characters.Pilot>().Find(row.PilotId) is null)
            {
                throw new InvalidDataException("Missing resident Pilot reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Combat.SavedWeaponLink>())
        {
            if (Set<Eden.Data.World.Inventory.OwnedItem>().Find(row.MasterId) is null)
            {
                throw new InvalidDataException("Missing resident OwnedItem reference.");
            }
            if (Set<Eden.Data.World.Inventory.Ship>().Find(row.ShipId) is null)
            {
                throw new InvalidDataException("Missing resident Ship reference.");
            }
            if (Set<Eden.Data.World.Inventory.OwnedItem>().Find(row.SlaveId) is null)
            {
                throw new InvalidDataException("Missing resident OwnedItem reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Combat.ShipHealthState>())
        {
            if (
                !double.IsFinite(row.ArmorDamage)
                || !double.IsFinite(row.ShieldCharge)
                || !double.IsFinite(row.StructureDamage)
                || !(row.ShieldCharge >= 0 && row.ArmorDamage >= 0 && row.StructureDamage >= 0)
            )
            {
                throw new InvalidDataException("Invalid resident ShipHealthState values.");
            }
            if (Set<Eden.Data.World.Inventory.Ship>().Find(row.ShipId) is null)
            {
                throw new InvalidDataException("Missing resident Ship reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Combat.ShipLoss>())
        {
            if (!double.IsFinite(row.X) || !double.IsFinite(row.Y) || !double.IsFinite(row.Z))
            {
                throw new InvalidDataException("Invalid resident ShipLoss values.");
            }
            if (row.NpcId is not null && Set<Eden.Data.World.Combat.Npcs.WorldNpc>().Find(row.NpcId.Value) is null)
            {
                throw new InvalidDataException("Missing resident WorldNpc reference.");
            }
            if (Set<Eden.Data.World.Characters.Pilot>().Find(row.PilotId) is null)
            {
                throw new InvalidDataException("Missing resident Pilot reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Combat.WorldMissile>())
        {
            if (!float.IsFinite(row.TargetRadius))
            {
                throw new InvalidDataException("Invalid resident WorldMissile values.");
            }
            if (Set<Eden.Data.World.Characters.Pilot>().Find(row.PilotId) is null)
            {
                throw new InvalidDataException("Missing resident Pilot reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Combat.WorldPlayerEwar>())
        {
            if (Set<Eden.Data.World.Inventory.OwnedItem>().Find(row.ModuleId) is null)
            {
                throw new InvalidDataException("Missing resident OwnedItem reference.");
            }
            if (Set<Eden.Data.World.Characters.Pilot>().Find(row.PilotId) is null)
            {
                throw new InvalidDataException("Missing resident Pilot reference.");
            }
            if (Set<Eden.Data.World.Inventory.Ship>().Find(row.SourceShipId) is null)
            {
                throw new InvalidDataException("Missing resident Ship reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Corporations.SavedCorporation>())
        {
            if (Set<Eden.Data.World.Characters.Pilot>().Find(row.CeoId) is null)
            {
                throw new InvalidDataException("Missing resident Pilot reference.");
            }
            if (Set<Eden.Data.World.Characters.Pilot>().Find(row.CreatorId) is null)
            {
                throw new InvalidDataException("Missing resident Pilot reference.");
            }
            if (
                true
                && Set<Eden.Data.World.Corporations.SavedCorporation>()
                    .Any(other => true && other.CeoId == row.CeoId && (other.Id != row.Id))
            )
            {
                throw new InvalidDataException("Duplicate resident SavedCorporation unique value.");
            }
            if (
                true
                && Set<Eden.Data.World.Corporations.SavedCorporation>().Any(other => true && other.Name == row.Name && (other.Id != row.Id))
            )
            {
                throw new InvalidDataException("Duplicate resident SavedCorporation unique value.");
            }
            if (
                true
                && Set<Eden.Data.World.Corporations.SavedCorporation>()
                    .Any(other => true && other.Ticker == row.Ticker && (other.Id != row.Id))
            )
            {
                throw new InvalidDataException("Duplicate resident SavedCorporation unique value.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Corporations.SavedCorporationMemberAccess>())
        {
            if (Set<Eden.Data.World.Characters.Pilot>().Find(row.PilotId) is null)
            {
                throw new InvalidDataException("Missing resident Pilot reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Corporations.SavedCorporationTitle>())
        {
            if (Set<Eden.Data.World.Corporations.SavedCorporation>().Find(row.CorporationId) is null)
            {
                throw new InvalidDataException("Missing resident SavedCorporation reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Corporations.SavedCorporationWalletSelection>())
        {
            if (Set<Eden.Data.World.Characters.Pilot>().Find(row.PilotId) is null)
            {
                throw new InvalidDataException("Missing resident Pilot reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Corporations.SavedWalletLedger>()) { }
        foreach (var row in Retained<Eden.Data.World.Drones.WorldDrone>())
        {
            if (
                !double.IsFinite(row.ArmorDamage)
                || !double.IsFinite(row.Shield)
                || !float.IsFinite(row.SpeedLimit)
                || !double.IsFinite(row.StructureDamage)
                || !double.IsFinite(row.VelocityX)
                || !double.IsFinite(row.VelocityY)
                || !double.IsFinite(row.VelocityZ)
                || !double.IsFinite(row.X)
                || !double.IsFinite(row.Y)
                || !double.IsFinite(row.Z)
            )
            {
                throw new InvalidDataException("Invalid resident WorldDrone values.");
            }
            if (Set<Eden.Data.World.Inventory.OwnedItem>().Find(row.ItemId) is null)
            {
                throw new InvalidDataException("Missing resident OwnedItem reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Drones.WorldDroneCombatTriggers>())
        {
            if (Set<Eden.Data.World.Inventory.Ship>().Find(row.ShipId) is null)
            {
                throw new InvalidDataException("Missing resident Ship reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Drones.WorldDroneSettings>()) { }
        foreach (var row in Retained<Eden.Data.World.Economy.SavedAdjustedPrice>()) { }
        foreach (var row in Retained<Eden.Data.World.Economy.SavedBeltRenewal>()) { }
        foreach (var row in Retained<Eden.Data.World.Economy.SavedEconomicPolicy>()) { }
        foreach (var row in Retained<Eden.Data.World.Exploration.WorldAnomalyMember>()) { }
        foreach (var row in Retained<Eden.Data.World.Exploration.WorldCombatAnomaly>())
        {
            if (!double.IsFinite(row.X) || !double.IsFinite(row.Y) || !double.IsFinite(row.Z))
            {
                throw new InvalidDataException("Invalid resident WorldCombatAnomaly values.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Fittings.ActiveFittedModule>())
        {
            if (Set<Eden.Data.World.Inventory.OwnedItem>().Find(row.ModuleId) is null)
            {
                throw new InvalidDataException("Missing resident OwnedItem reference.");
            }
            if (Set<Eden.Data.World.Inventory.Ship>().Find(row.ShipId) is null)
            {
                throw new InvalidDataException("Missing resident Ship reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Fittings.ShipEnergy>())
        {
            if (!double.IsFinite(row.Charge) || !(row.Charge >= 0))
            {
                throw new InvalidDataException("Invalid resident ShipEnergy values.");
            }
            if (Set<Eden.Data.World.Inventory.Ship>().Find(row.ShipId) is null)
            {
                throw new InvalidDataException("Missing resident Ship reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Insurance.SavedInsuranceLedger>())
        {
            if (Set<Eden.Data.World.Characters.Pilot>().Find(row.PilotId) is null)
            {
                throw new InvalidDataException("Missing resident Pilot reference.");
            }
            if (row.PolicyId is not null && Set<Eden.Data.World.Insurance.SavedInsurancePolicy>().Find(row.PolicyId.Value) is null)
            {
                throw new InvalidDataException("Missing resident SavedInsurancePolicy reference.");
            }
            if (
                row.IsPayout
                && Set<Eden.Data.World.Insurance.SavedInsuranceLedger>()
                    .Any(other => other.IsPayout && other.ShipId == row.ShipId && (other.Id != row.Id))
            )
            {
                throw new InvalidDataException("Duplicate resident SavedInsuranceLedger unique value.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Insurance.SavedInsurancePolicy>())
        {
            if (Set<Eden.Data.World.Characters.Pilot>().Find(row.PilotId) is null)
            {
                throw new InvalidDataException("Missing resident Pilot reference.");
            }
            if (
                row.Status == 0
                && Set<Eden.Data.World.Insurance.SavedInsurancePolicy>()
                    .Any(other => other.Status == 0 && other.ShipId == row.ShipId && (other.Id != row.Id))
            )
            {
                throw new InvalidDataException("Duplicate resident SavedInsurancePolicy unique value.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Loot.WorldWreck>())
        {
            if (
                !double.IsFinite(row.X)
                || !double.IsFinite(row.Y)
                || !double.IsFinite(row.Z)
                || !((row.NpcId is not null && row.ShipId is null) || (row.NpcId is null && row.ShipId is not null))
            )
            {
                throw new InvalidDataException("Invalid resident WorldWreck values.");
            }
            if (row.NpcId is not null && Set<Eden.Data.World.Combat.Npcs.WorldNpc>().Find(row.NpcId.Value) is null)
            {
                throw new InvalidDataException("Missing resident WorldNpc reference.");
            }
            if (Set<Eden.Data.World.Characters.Pilot>().Find(row.OwnerId) is null)
            {
                throw new InvalidDataException("Missing resident Pilot reference.");
            }
            if (row.ShipId is not null && Set<Eden.Data.World.Combat.ShipLoss>().Find(row.ShipId.Value) is null)
            {
                throw new InvalidDataException("Missing resident ShipLoss reference.");
            }
            if (
                true
                && row.NpcId is not null
                && Set<Eden.Data.World.Loot.WorldWreck>().Any(other => true && other.NpcId == row.NpcId && (other.Id != row.Id))
            )
            {
                throw new InvalidDataException("Duplicate resident WorldWreck unique value.");
            }
            if (
                true
                && row.ShipId is not null
                && Set<Eden.Data.World.Loot.WorldWreck>().Any(other => true && other.ShipId == row.ShipId && (other.Id != row.Id))
            )
            {
                throw new InvalidDataException("Duplicate resident WorldWreck unique value.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Mining.SavedMiningDeposit>())
        {
            if (Set<Eden.Data.World.Mining.WorldAsteroid>().Find(row.Id) is null)
            {
                throw new InvalidDataException("Missing resident WorldAsteroid reference.");
            }
            if (Set<Eden.Data.World.Mining.SavedMiningSite>().Find(row.SiteId) is null)
            {
                throw new InvalidDataException("Missing resident SavedMiningSite reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Mining.SavedMiningSite>())
        {
            if (!double.IsFinite(row.X) || !double.IsFinite(row.Y) || !double.IsFinite(row.Z))
            {
                throw new InvalidDataException("Invalid resident SavedMiningSite values.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Mining.SavedPersonalMiningLog>())
        {
            if (Set<Eden.Data.World.Characters.Pilot>().Find(row.PilotId) is null)
            {
                throw new InvalidDataException("Missing resident Pilot reference.");
            }
            if (
                true
                && Set<Eden.Data.World.Mining.SavedPersonalMiningLog>()
                    .Any(other =>
                        true
                        && other.PilotId == row.PilotId
                        && other.Day == row.Day
                        && other.CorporationId == row.CorporationId
                        && other.SystemId == row.SystemId
                        && other.TypeId == row.TypeId
                        && (other.Id != row.Id)
                    )
            )
            {
                throw new InvalidDataException("Duplicate resident SavedPersonalMiningLog unique value.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Mining.WorldAsteroid>())
        {
            if (
                !float.IsFinite(row.Radius)
                || !double.IsFinite(row.X)
                || !double.IsFinite(row.Y)
                || !double.IsFinite(row.Z)
                || !(row.Quantity >= 0)
            )
            {
                throw new InvalidDataException("Invalid resident WorldAsteroid values.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Mining.WorldNpcMiningCargo>())
        {
            if (!(row.Quantity >= 0))
            {
                throw new InvalidDataException("Invalid resident WorldNpcMiningCargo values.");
            }
            if (Set<Eden.Data.World.Combat.Npcs.WorldNpc>().Find(row.NpcId) is null)
            {
                throw new InvalidDataException("Missing resident WorldNpc reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Mining.WorldNpcMiningMember>())
        {
            if (
                !double.IsFinite(row.Capacitor)
                || !double.IsFinite(row.WarpOriginX)
                || !double.IsFinite(row.WarpOriginY)
                || !double.IsFinite(row.WarpOriginZ)
                || !double.IsFinite(row.WarpSpeed)
            )
            {
                throw new InvalidDataException("Invalid resident WorldNpcMiningMember values.");
            }
            if (Set<Eden.Data.World.Mining.WorldNpcMiningOperation>().Find(row.EncounterId) is null)
            {
                throw new InvalidDataException("Missing resident WorldNpcMiningOperation reference.");
            }
            if (Set<Eden.Data.World.Combat.Npcs.WorldNpc>().Find(row.NpcId) is null)
            {
                throw new InvalidDataException("Missing resident WorldNpc reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Mining.WorldNpcMiningOperation>())
        {
            if (!double.IsFinite(row.AlertX) || !double.IsFinite(row.AlertY) || !double.IsFinite(row.AlertZ))
            {
                throw new InvalidDataException("Invalid resident WorldNpcMiningOperation values.");
            }
            if (Set<Eden.Data.World.Combat.Npcs.WorldNpcEncounter>().Find(row.EncounterId) is null)
            {
                throw new InvalidDataException("Missing resident WorldNpcEncounter reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Propulsion.SpatialNavigationState>())
        {
            if (
                !double.IsFinite(row.CloakReactivationMilliseconds)
                || !double.IsFinite(row.CloakTargetingMilliseconds)
                || !double.IsFinite(row.HeadingX)
                || !double.IsFinite(row.HeadingY)
                || !double.IsFinite(row.HeadingZ)
                || !double.IsFinite(row.VelocityX)
                || !double.IsFinite(row.VelocityY)
                || !double.IsFinite(row.VelocityZ)
            )
            {
                throw new InvalidDataException("Invalid resident SpatialNavigationState values.");
            }
            if (Set<Eden.Data.World.Inventory.Ship>().Find(row.ShipId) is null)
            {
                throw new InvalidDataException("Missing resident Ship reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Propulsion.WorldTackle>())
        {
            if (
                !double.IsFinite(row.CapacitorNeed)
                || !double.IsFinite(row.DurationMilliseconds)
                || !double.IsFinite(row.MwdBlock)
                || !double.IsFinite(row.Range)
                || !double.IsFinite(row.Strength)
                || !double.IsFinite(row.WebPercent)
            )
            {
                throw new InvalidDataException("Invalid resident WorldTackle values.");
            }
            if (Set<Eden.Data.World.Inventory.OwnedItem>().Find(row.ModuleId) is null)
            {
                throw new InvalidDataException("Missing resident OwnedItem reference.");
            }
            if (Set<Eden.Data.World.Characters.Pilot>().Find(row.PilotId) is null)
            {
                throw new InvalidDataException("Missing resident Pilot reference.");
            }
            if (Set<Eden.Data.World.Inventory.Ship>().Find(row.SourceShipId) is null)
            {
                throw new InvalidDataException("Missing resident Ship reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Repair.PendingLocalRepair>())
        {
            if (!double.IsFinite(row.Amount) || !(row.Amount > 0))
            {
                throw new InvalidDataException("Invalid resident PendingLocalRepair values.");
            }
            if (Set<Eden.Data.World.Inventory.OwnedItem>().Find(row.ModuleId) is null)
            {
                throw new InvalidDataException("Missing resident OwnedItem reference.");
            }
            if (Set<Eden.Data.World.Inventory.Ship>().Find(row.ShipId) is null)
            {
                throw new InvalidDataException("Missing resident Ship reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Repair.StationRepairPayment>())
        {
            if (Set<Eden.Data.World.Characters.Pilot>().Find(row.PilotId) is null)
            {
                throw new InvalidDataException("Missing resident Pilot reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Repair.WorldRemoteAssistance>())
        {
            if (Set<Eden.Data.World.Inventory.OwnedItem>().Find(row.ModuleId) is null)
            {
                throw new InvalidDataException("Missing resident OwnedItem reference.");
            }
            if (Set<Eden.Data.World.Characters.Pilot>().Find(row.PilotId) is null)
            {
                throw new InvalidDataException("Missing resident Pilot reference.");
            }
            if (Set<Eden.Data.World.Inventory.Ship>().Find(row.SourceShipId) is null)
            {
                throw new InvalidDataException("Missing resident Ship reference.");
            }
            if (Set<Eden.Data.World.Inventory.Ship>().Find(row.TargetId) is null)
            {
                throw new InvalidDataException("Missing resident Ship reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Repair.WorldRemoteRepair>())
        {
            if (Set<Eden.Data.World.Inventory.OwnedItem>().Find(row.ModuleId) is null)
            {
                throw new InvalidDataException("Missing resident OwnedItem reference.");
            }
            if (Set<Eden.Data.World.Characters.Pilot>().Find(row.PilotId) is null)
            {
                throw new InvalidDataException("Missing resident Pilot reference.");
            }
            if (Set<Eden.Data.World.Inventory.Ship>().Find(row.SourceShipId) is null)
            {
                throw new InvalidDataException("Missing resident Ship reference.");
            }
            if (Set<Eden.Data.World.Inventory.Ship>().Find(row.TargetId) is null)
            {
                throw new InvalidDataException("Missing resident Ship reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Skills.SavedFreeSkillPoints>())
        {
            if (Set<Eden.Data.World.Characters.Pilot>().Find(row.PilotId) is null)
            {
                throw new InvalidDataException("Missing resident Pilot reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Structures.SavedStructure>())
        {
            if (!double.IsFinite(row.X) || !double.IsFinite(row.Y) || !double.IsFinite(row.Yaw) || !double.IsFinite(row.Z))
            {
                throw new InvalidDataException("Invalid resident SavedStructure values.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Structures.SavedStructureAccess>())
        {
            if (Set<Eden.Data.World.Structures.SavedStructure>().Find(row.StructureId) is null)
            {
                throw new InvalidDataException("Missing resident SavedStructure reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Thermal.SavedModuleCondition>())
        {
            if (!double.IsFinite(row.Damage) || !(row.Damage >= 0 && row.Damage <= 1.7976931348623157e308))
            {
                throw new InvalidDataException("Invalid resident SavedModuleCondition values.");
            }
            if (Set<Eden.Data.World.Inventory.OwnedItem>().Find(row.ModuleId) is null)
            {
                throw new InvalidDataException("Missing resident OwnedItem reference.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Thermal.SavedNaniteRepair>())
        {
            if (
                !double.IsFinite(row.InitialDamage)
                || !double.IsFinite(row.InitialHitPoints)
                || !double.IsFinite(row.PasteUnitsPerDamage)
                || !(row.StartedAt >= 0 && row.CompletesAt > row.StartedAt)
                || !(row.InitialDamage > 0 && row.InitialDamage < row.InitialHitPoints)
                || !(row.InitialHitPoints <= 1.7976931348623157e308)
                || !(row.PasteUnits >= 0)
                || !(row.PasteUnitsPerDamage >= 0 && row.PasteUnitsPerDamage <= 1.7976931348623157e308)
            )
            {
                throw new InvalidDataException("Invalid resident SavedNaniteRepair values.");
            }
            if (Set<Eden.Data.World.Inventory.OwnedItem>().Find(row.ModuleId) is null)
            {
                throw new InvalidDataException("Missing resident OwnedItem reference.");
            }
            if (Set<Eden.Data.World.Characters.Pilot>().Find(row.PilotId) is null)
            {
                throw new InvalidDataException("Missing resident Pilot reference.");
            }
            if (Set<Eden.Data.World.Inventory.Ship>().Find(row.ShipId) is null)
            {
                throw new InvalidDataException("Missing resident Ship reference.");
            }
            if (
                true
                && Set<Eden.Data.World.Thermal.SavedNaniteRepair>()
                    .Any(other => true && other.Generation == row.Generation && (other.ModuleId != row.ModuleId))
            )
            {
                throw new InvalidDataException("Duplicate resident SavedNaniteRepair unique value.");
            }
        }
        foreach (var row in Retained<Eden.Data.World.Thermal.SavedShipHeat>())
        {
            if (Set<Eden.Data.World.Inventory.Ship>().Find(row.ShipId) is null)
            {
                throw new InvalidDataException("Missing resident Ship reference.");
            }
        }
    }
}
