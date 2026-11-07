using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Mining;

namespace Eden.Data.World.Mining;

public sealed class WorldAsteroid
{
    public long Id { get; set; }
    public int BeltId { get; set; }
    public int SystemId { get; set; }
    public int TypeId { get; set; }
    public long Quantity { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public float Radius { get; set; }
}

public sealed class WorldNpcMiningCargo
{
    public long NpcId { get; set; }
    public int TypeId { get; set; }
    public long Quantity { get; set; }
}

public sealed class WorldNpcMiningMember
{
    public long NpcId { get; set; }
    public long EncounterId { get; set; }
    public NpcMiningTravel Travel { get; set; }
    public long? AsteroidId { get; set; }
    public long? MiningStartedAt { get; set; }
    public long? MiningDueAt { get; set; }
    public double Capacitor { get; set; }
    public long CapacitorAt { get; set; }
    public long? WarpStartedAt { get; set; }
    public long? WarpArrivesAt { get; set; }
    public double WarpOriginX { get; set; }
    public double WarpOriginY { get; set; }
    public double WarpOriginZ { get; set; }
    public double WarpSpeed { get; set; }
    public long? SupportTargetId { get; set; }
    public long SupportStartedAt { get; set; }
    public long SupportReadyAt { get; set; }
    public NpcActivityKind SupportKind { get; set; }

    internal NpcWarpTransit? Transit(WorldNpc npc) =>
        WarpStartedAt is long start && WarpArrivesAt is long end
            ? new(
                new(WarpOriginX, WarpOriginY, WarpOriginZ),
                new(npc.HomeX!.Value, npc.HomeY!.Value, npc.HomeZ!.Value),
                start,
                end,
                WarpSpeed
            )
            : null;

    internal NpcActivityContact? Activity(WorldNpc npc, double duration, long now, int moduleType = 0) =>
        npc.Navigation == NpcNavigationState.Idle && AsteroidId is long rock && MiningStartedAt is long mining && MiningDueAt > now
            ? new(npc.Id, moduleType > 0 ? moduleType : npc.TypeId, rock, NpcActivityKind.Mining, mining, duration)
        : SupportTargetId is long target && SupportReadyAt > now
            ? new(
                npc.Id,
                npc.TypeId,
                target,
                SupportKind,
                SupportStartedAt,
                (SupportReadyAt - SupportStartedAt) / (double)TimeSpan.TicksPerMillisecond
            )
        : null;
}

public sealed class WorldNpcMiningOperation
{
    public long EncounterId { get; set; }
    public int SystemId { get; set; }
    public int BeltId { get; set; }
    public int CorporationId { get; set; }
    public int FactionId { get; set; }
    public int? StationId { get; set; }
    public NpcMiningStage Stage { get; set; }
    public long PickupAt { get; set; }
    public int HaulGeneration { get; set; }
    public long? ThreatShipId { get; set; }
    public long? ResponseAt { get; set; }
    public long? ResetAt { get; set; }
    public double AlertX { get; set; }
    public double AlertY { get; set; }
    public double AlertZ { get; set; }
}
