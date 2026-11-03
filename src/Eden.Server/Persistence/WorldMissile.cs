using System.Text.Json;
using System.Text.Json.Serialization;
using Eden.Gameplay.Combat;

namespace Eden.Server.Persistence;

public sealed class WorldMissile
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long SourceShipId { get; set; }
    public long ModuleId { get; set; }
    public int LauncherTypeId { get; set; }
    public int ChargeTypeId { get; set; }
    public int GroupId { get; set; }
    public int SystemId { get; set; }
    public long TargetId { get; set; }
    public float TargetRadius { get; set; }
    public int CorporationId { get; set; }
    public int LaunchStamp { get; set; }
    public long LaunchedAt { get; set; }
    public long UpdatedAt { get; set; }
    public long ExpiresAt { get; set; }
    public long? EndedAt { get; set; }
    public MissileFlightStatus Status { get; set; }
    public bool Reported { get; set; }
    public required string Snapshot { get; set; }
    public string? Outcome { get; set; }

    internal MissilePayload Payload() =>
        JsonSerializer.Deserialize(Snapshot, MissileJson.Default.MissilePayload)
        ?? throw new InvalidDataException("Missing saved missile flight.");

    internal void Save(MissilePayload payload) => Snapshot = JsonSerializer.Serialize(payload, MissileJson.Default.MissilePayload);

    internal MissileContact Contact(MissilePayload payload) =>
        new(
            Id,
            ChargeTypeId,
            GroupId,
            SystemId,
            PilotId,
            SourceShipId,
            [ModuleId],
            TargetId,
            TargetRadius,
            CorporationId,
            LaunchStamp,
            payload.Flight
        );
}

internal sealed record MissilePayload(MissileFlight Flight, MissileWarhead Warhead);

[JsonSerializable(typeof(MissilePayload))]
[JsonSerializable(typeof(MissileImpact))]
[JsonSourceGenerationOptions(IgnoreReadOnlyProperties = true)]
internal sealed partial class MissileJson : JsonSerializerContext;
