using System.Globalization;
using System.Text.Json;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Ships;
using Microsoft.EntityFrameworkCore;

namespace Eden.Data.World.Combat.Npcs;

public sealed class WorldNpc
{
    public long Id { get; set; }
    public int BeltId { get; set; }
    public int SystemId { get; set; }
    public int TypeId { get; set; }
    public string? Name { get; set; }
    public int CorporationId { get; set; } = NpcEncounterDefinitions.GuristasCorporation;
    public int FactionId { get; set; }
    public NpcRole Role { get; set; }
    public NpcAnchorKind AnchorKind { get; set; }
    public long EncounterId { get; set; }
    public int SpawnSlot { get; set; }
    public double? AcquisitionRange { get; set; }
    public double? HomeX { get; set; }
    public double? HomeY { get; set; }
    public double? HomeZ { get; set; }
    public long? RetiredAt { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double ShieldCharge { get; set; }
    public double ArmorDamage { get; set; }
    public double StructureDamage { get; set; }
    public long UpdatedAt { get; set; }
    public long ShieldRepairAt { get; set; }
    public long ArmorRepairAt { get; set; }
    public long RepairRevision { get; set; }
    public long? DestroyedAt { get; set; }
    public double VelocityX { get; set; }
    public double VelocityY { get; set; }
    public double VelocityZ { get; set; }
    public long MotionAt { get; set; }
    public NpcNavigationState Navigation { get; set; }
}

public sealed class WorldNpcEncounter
{
    public long Id { get; set; }
    public int SystemId { get; set; }
    public int AnchorId { get; set; }
    public NpcAnchorKind AnchorKind { get; set; }
    public NpcRole Role { get; set; }
    public int FactionId { get; set; }
    public double HomeX { get; set; }
    public double HomeY { get; set; }
    public double HomeZ { get; set; }
    public bool Occupied { get; set; }
    public int Generation { get; set; }
    public int Version { get; set; }
    public long? NextSpawnAt { get; set; }
}

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
