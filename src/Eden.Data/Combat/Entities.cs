using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Ships;
using Microsoft.EntityFrameworkCore;

namespace Eden.Data.Combat;

// Shared by paid module cycles and ammunition reloads. Version 25 preserves
// existing WorldWeaponCooldown deadlines and classifies them as ordinary cycles.
public sealed class ModuleCooldown
{
    public long ModuleId { get; set; }
    public long ReadyAt { get; set; }
    public ModuleCooldownKind Kind { get; set; }
}

public sealed class NpcAggression
{
    public long NpcId { get; set; }
    public long TargetShipId { get; set; }
    public long ReadyAt { get; set; }
    public long? LastShotAt { get; set; }
    public long? LastTargetId { get; set; }
    public long MissileReadyAt { get; set; }

    internal static NpcAggression Start(long npcId, long targetShipId, double delayMilliseconds, long now)
    {
        if (!double.IsFinite(delayMilliseconds) || delayMilliseconds < 0)
        {
            throw new InvalidDataException("Invalid NPC reaction delay.");
        }
        return new()
        {
            NpcId = npcId,
            TargetShipId = targetShipId,
            ReadyAt = checked(now + (long)Math.Ceiling(delayMilliseconds * TimeSpan.TicksPerMillisecond)),
            MissileReadyAt = checked(now + (long)Math.Ceiling(delayMilliseconds * TimeSpan.TicksPerMillisecond)),
        };
    }
}

public sealed class NpcBounty
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long NpcId { get; set; }
    public int TypeId { get; set; }
    public int SystemId { get; set; }
    public long Time { get; set; }
    public long AmountCents { get; set; }
    public long BalanceCents { get; set; }
    public long TaxCents { get; set; }
}

public sealed class PendingSecurityReward
{
    public long PilotId { get; set; }
    public long Due { get; set; }
    public long NpcId { get; set; }
    public int TypeId { get; set; }
    public int SystemId { get; set; }
    public int CorporationId { get; set; }
    public double Fraction { get; set; }
    public double Maximum { get; set; }
}

public sealed class SavedCrimewatch
{
    public long PilotId { get; set; }
    public int SystemId { get; set; }
    public long WeaponsActive { get; set; }
    public long WeaponsUntil { get; set; }
    public long PvpActive { get; set; }
    public long PvpUntil { get; set; }
    public long NpcActive { get; set; }
    public long NpcUntil { get; set; }
    public long SuspectActive { get; set; }
    public long SuspectUntil { get; set; }
    public long CriminalActive { get; set; }
    public long CriminalUntil { get; set; }

    public CrimewatchTimers Timers() =>
        new(
            new(WeaponsActive, WeaponsUntil),
            new(PvpActive, PvpUntil),
            new(NpcActive, NpcUntil),
            new(SuspectActive, SuspectUntil),
            new(CriminalActive, CriminalUntil)
        );

    internal void Apply(CrimewatchTimers timers)
    {
        (WeaponsActive, WeaponsUntil) = (timers.Weapons.ActiveUntil, timers.Weapons.ExpiresAt);
        (PvpActive, PvpUntil) = (timers.Pvp.ActiveUntil, timers.Pvp.ExpiresAt);
        (NpcActive, NpcUntil) = (timers.Npc.ActiveUntil, timers.Npc.ExpiresAt);
        (SuspectActive, SuspectUntil) = (timers.Suspect.ActiveUntil, timers.Suspect.ExpiresAt);
        (CriminalActive, CriminalUntil) = (timers.Criminal.ActiveUntil, timers.Criminal.ExpiresAt);
    }
}

[PrimaryKey(nameof(LowerPilotId), nameof(HigherPilotId))]
public sealed class SavedEngagement
{
    public long LowerPilotId { get; set; }
    public long HigherPilotId { get; set; }
    public long ActiveUntil { get; set; }
    public long ExpiresAt { get; set; }
}

public sealed class SavedLogoff
{
    public long PilotId { get; set; }
    public long ShipId { get; set; }
    public int SystemId { get; set; }
    public long ExpiresAt { get; set; }
}

[PrimaryKey(nameof(SourceShipId), nameof(TargetShipId))]
public sealed class SavedPilotHostility
{
    public long SourceShipId { get; set; }
    public long TargetShipId { get; set; }
    public long ExpiresAt { get; set; }
}

public sealed class SavedSecurityStatus
{
    public long PilotId { get; set; }
    public double Value { get; set; }
}

public sealed class SavedSecurityTransaction
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long Time { get; set; }
    public long NpcId { get; set; }
    public int TypeId { get; set; }
    public int SystemId { get; set; }
    public int CorporationId { get; set; }
    public double Modification { get; set; }
    public double NewValue { get; set; }

    public SecurityStatusTransaction Snapshot() =>
        new(Id, Time, SecurityEvent.PirateNpcKilled, CorporationId, SystemId, Modification, NewValue, NpcId, TypeId);
}

public sealed class SavedWeaponLink
{
    public long SlaveId { get; set; }
    public long MasterId { get; set; }
    public long ShipId { get; set; }
}

public sealed class ShipHealthState
{
    public long ShipId { get; set; }
    public double ShieldCharge { get; set; }
    public double ArmorDamage { get; set; }
    public double StructureDamage { get; set; }
    public long UpdatedAt { get; set; }
}

public sealed class ShipLoss
{
    public long ShipId { get; set; }
    public long PilotId { get; set; }
    public long? NpcId { get; set; }
    public long? AttackerPilotId { get; set; }
    public long CapsuleId { get; set; }
    public int TypeId { get; set; }
    public int SystemId { get; set; }
    public long Time { get; set; }
    public string Name { get; set; } = "";
    public string ItemsJson { get; set; } = "[]";
    public string StoredShipsJson { get; set; } = "[]";
    public string DroppedItemIdsJson { get; set; } = "[]";
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}

public sealed class WorldMissile
{
    public long Id { get; set; }
    public MissileSourceKind SourceKind { get; set; }
    public int SourceTypeId { get; set; }
    public long PilotId { get; set; }
    public long SourceShipId { get; set; }
    public long ModuleId { get; set; }
    public int LauncherTypeId { get; set; }
    public int ChargeTypeId { get; set; }
    public int GroupId { get; set; }
    public int SystemId { get; set; }
    public long TargetId { get; set; }
    public MissileTargetKind TargetKind { get; set; }
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
            SourceKind == MissileSourceKind.Npc ? CorporationId : PilotId,
            SourceShipId,
            [ModuleId],
            TargetId,
            TargetRadius,
            CorporationId,
            LaunchStamp,
            payload.Flight
        );
}

public sealed class WorldPlayerEwar
{
    public long ModuleId { get; set; }
    public long TargetId { get; set; }
    public long PilotId { get; set; }
    public long SourceShipId { get; set; }
    public int SystemId { get; set; }
    public TackleTargetKind TargetKind { get; set; }
    public string ContactJson { get; set; } = "";

    internal ElectronicWarfareContact Contact() =>
        JsonSerializer.Deserialize<ElectronicWarfareContact>(ContactJson)
        ?? throw new InvalidDataException("Invalid saved electronic warfare contact.");
}
