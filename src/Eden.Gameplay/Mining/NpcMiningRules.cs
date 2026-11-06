using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Mining;

public enum NpcMiningStage
{
    Working,
    Alarm,
    Cooldown,
}

public enum NpcMiningTravel
{
    Working,
    Inbound,
    Collecting,
    Outbound,
    Fleeing,
    Safe,
}

public enum NpcActivityKind
{
    Mining,
    ArmorRepair,
    ShieldRepair,
}

public sealed record NpcActivityContact(long SourceId, int TypeId, long TargetId, NpcActivityKind Kind, long StartedAt, double Duration);

public sealed record NpcMiningLaser(double Amount, double Duration, double Range, double Capacitor);

public sealed record NpcMiningRoster(int Hauler, int Transport, int Damage, int Tackle, int Logistics, int ElectronicWarfare);

public sealed record NpcWarpTransit(SpacePosition Origin, SpacePosition Destination, long StartedAt, long ArrivesAt, double Speed)
{
    public WarpCourse Course(int stamp, long now, FlightProperties hull) =>
        new(
            Origin,
            Destination,
            stamp - checked((int)Math.Max(0, (now - StartedAt) / TimeSpan.TicksPerSecond)),
            hull with
            {
                WarpSpeed = Speed,
            },
            0
        );
}

public static class NpcMiningRules
{
    public const int Venture = 42533;
    public const int Retriever = 42534;
    public const int Skiff = 42535;
    public const int MinerCount = 3;
    public const int DefenseCount = 8;
    public const int ResponseSeconds = 5;
    public const int CooldownSeconds = 600;
    public const int DefenseLifetimeSeconds = 300;
    public const double HostileStanding = -5;
    public const double FriendlyStanding = 5;
    public const double CorporationLossFraction = 0.000667;
    public const double TransferRange = 2500;
    public const double MinerStandOff = 400;
    public const double FormationSpacing = 1000;
    public const double HaulerStandOff = 2000;
    public const double PickupFillFraction = 0.5;
    public const double DefaultWarpSpeed = 3;
    public const int LogisticsSlot = 3;
    public const double SafeDistance = 1_000_000_000;
    private const long EncounterOffset = 1_000_000_000;

    public static long EncounterId(int belt) => -EncounterOffset - belt;

    public static bool IsMinerType(int type) => type is Venture or Retriever or Skiff;

    public static NpcMiningRoster? Roster(int faction) =>
        (NpcEmpireFaction)faction switch
        {
            NpcEmpireFaction.Amarr => new(42654, 42655, 42841, 42843, 42842, 42845),
            NpcEmpireFaction.Caldari => new(42656, 42657, 43563, 43565, 43564, 43567),
            NpcEmpireFaction.Gallente => new(42658, 42659, 43574, 43576, 43575, 43577),
            NpcEmpireFaction.Minmatar => new(42660, 42661, 43585, 43587, 43586, 43588),
            _ => null,
        };

    public static IReadOnlyList<int> Types =>
        new[] { Venture, Retriever, Skiff }
            .Concat(
                Enum.GetValues<NpcEmpireFaction>()
                    .SelectMany(f =>
                    {
                        var r = Roster((int)f)!;
                        return new[] { r.Hauler, r.Transport, r.Damage, r.Tackle, r.Logistics, r.ElectronicWarfare };
                    })
            )
            .ToArray();

    public static NpcMiningLaser Laser(IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        NpcMiningLaser laser = new(
            attributes.GetValueOrDefault(DogmaAttributeId.BehaviorMiningAmount),
            attributes.GetValueOrDefault(DogmaAttributeId.BehaviorMiningDuration),
            attributes.GetValueOrDefault(DogmaAttributeId.BehaviorMiningRange),
            attributes.GetValueOrDefault(DogmaAttributeId.BehaviorMiningCapacitor)
        );
        if (
            !double.IsFinite(laser.Amount + laser.Duration + laser.Range + laser.Capacitor)
            || laser.Amount <= 0
            || laser.Duration <= 0
            || laser.Range <= 0
            || laser.Capacitor < 0
        )
        {
            throw new InvalidDataException("Invalid catalog NPC mining laser.");
        }
        return laser;
    }

    public static long Extract(double amount, double freeVolume, double oreVolume, long available)
    {
        if (!double.IsFinite(amount + freeVolume + oreVolume) || amount < 0 || oreVolume <= 0 || available < 0)
        {
            throw new InvalidDataException("Invalid NPC mining volume or deposit.");
        }
        return Math.Min(
            available,
            checked((long)Math.Floor((Math.Max(0, Math.Min(amount, freeVolume)) + InventoryDefinitions.ResourceTolerance) / oreVolume))
        );
    }

    public static double PickupMilliseconds(NpcMiningLaser laser, double minerCapacity, double haulerCapacity) =>
        Math.Max(
            laser.Duration,
            Math.Min(minerCapacity * MinerCount, haulerCapacity) * PickupFillFraction / (MinerCount * laser.Amount / laser.Duration)
        );

    public static SpacePosition SafePoint(SpacePosition belt) => belt + new SpacePosition(SafeDistance, SafeDistance, SafeDistance);

    public static bool CanWarp(double baseStatus, IEnumerable<double> disruption) => baseStatus + disruption.Sum() <= 0;

    public static double RepairAmount(double amount, double optimal, double falloff, double distance) =>
        distance <= optimal ? amount
        : falloff > 0 ? amount * Math.Pow(0.5, Math.Pow((distance - optimal) / falloff, 2))
        : 0;

    public static IReadOnlyDictionary<DogmaAttributeId, double> CombatAttributes(IReadOnlyDictionary<DogmaAttributeId, double> source)
    {
        var result = source.ToDictionary();
        // Behavior ships omit the old AI's attack/fly range. Derive its pursuit
        // inputs from their actual catalog weapon/target ranges, never a faction buff.
        result[DogmaAttributeId.EntityAttackRange] = Math.Max(
            source.GetValueOrDefault(DogmaAttributeId.MaxTargetRange),
            source.GetValueOrDefault(DogmaAttributeId.MaxRange) + source.GetValueOrDefault(DogmaAttributeId.AccuracyFalloff)
        );
        result[DogmaAttributeId.EntityFlyRange] = source.GetValueOrDefault(DogmaAttributeId.MaxRange) * 0.5;
        return result;
    }
}
