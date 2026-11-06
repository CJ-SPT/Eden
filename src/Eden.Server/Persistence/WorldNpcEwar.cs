using System.Text.Json;
using Eden.Gameplay.Combat;

namespace Eden.Server.Persistence;

public sealed class WorldNpcEwar
{
    public long NpcId { get; set; }
    public NpcEwarKind Kind { get; set; }
    public int TypeId { get; set; }
    public long TargetShipId { get; set; }
    public long StartedAt { get; set; }
    public long ReadyAt { get; set; }
    public string AbilityJson { get; set; } = "";
    public string? AppliedJson { get; set; }

    internal NpcEwarContact? Contact() =>
        AppliedJson is null
            ? null
            : new(
                NpcId,
                TypeId,
                TargetShipId,
                JsonSerializer.Deserialize<NpcEwarAbility>(AbilityJson)
                    ?? throw new InvalidDataException("Invalid saved NPC EWAR ability."),
                JsonSerializer.Deserialize<NpcEwarPower>(AppliedJson) ?? throw new InvalidDataException("Invalid saved NPC EWAR power."),
                StartedAt,
                ReadyAt
            );
}
