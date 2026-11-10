using Eden.Gameplay.Combat;

namespace Eden.Gameplay.Capsuleers;

public enum RattingPhase
{
    TravellingToBelt,
    Searching,
    Fighting,
    Looting,
    Returning,
}

public sealed record RattingProgress(int StationId, long[] Belts, int BeltIndex, long RemainingTicks)
{
    public long LastObservedAt { get; init; }
    public RattingPhase Phase { get; init; }
    public long TargetId { get; init; }
    public long[] LootIds { get; init; } = [];
    public long[] VisitedWrecks { get; init; } = [];
}

public sealed record SupplyRequirement(int TypeId, int Quantity, bool Drone, double Volume);

public sealed record ResupplyProgress(int StationId, long BudgetCents, long SpentCents)
{
    public int? DestinationStationId { get; init; }
    public SupplyRequirement[] Requirements { get; init; } = [];
    public bool Checked { get; init; }
}

public sealed record RattingWeapon(CombatWeapon Weapon, int AvailableShots, MissileWarhead? Missile = null, int LoadedShots = 0);

public sealed record RattingOpponent(
    long Id,
    HullDefense Defense,
    HullDamage Health,
    double Signature,
    double Speed,
    double Distance,
    DamageVector DamagePerSecond,
    double ShieldRepairPerSecond,
    double ArmorRepairPerSecond
);

public sealed record RattingAssessment(bool Capable, double ClearSeconds, double SurvivalSeconds, string? Reason = null);

public static class RattingRules
{
    public const int MaximumBelts = 3;
    public const int MaximumMinutes = 10;
    public const int MaximumClearSeconds = 120;
    public const int MaximumShoppingJumps = 3;
    public const int DepartureDivisor = 3;
    public const int BudgetDivisor = 4;
    public const int ReferencePriceMultiplier = 2;
    private const double SurvivalMargin = 2;
    private const double DamageMargin = .75;

    public static bool Choose(long pilot, long sequence) =>
        TravelerRoutineRules.Draw(TravelerRoutineRules.Create(pilot), sequence) % DepartureDivisor == 0;

    // Deliberately pessimistic: no credit for future capacitor recharge or player repairs, maximum
    // NPC repairs/recharge, transverse target motion, and every pirate shooting for the entire clear.
    public static RattingAssessment Assess(
        ShipHealth pilot,
        double capacitor,
        IReadOnlyList<RattingWeapon> weapons,
        IReadOnlyList<RattingOpponent> opponents,
        double supportCapacitorPerSecond = 0
    )
    {
        RattingAssessment Reject(string reason) => new(false, double.PositiveInfinity, 0, reason);
        if (CapsuleerDefenseRules.Retreat(pilot, weapons.Count > 0) || opponents.Count == 0)
        {
            return Reject("No safe combat fit or visible pirates.");
        }
        if (
            !double.IsFinite(capacitor)
            || capacitor < 0
            || !double.IsFinite(supportCapacitorPerSecond)
            || supportCapacitorPerSecond < 0
            || weapons.Any(w => w.Weapon.CycleMilliseconds <= 0)
        )
        {
            return Reject("Unsupported combat estimate.");
        }
        double clear = 0;
        foreach (var enemy in opponents.OrderBy(e => e.Id))
        {
            DamageVector damage = DamageVector.None;
            foreach (var source in weapons.Where(w => w.AvailableShots > 0))
            {
                var weapon = source.Weapon;
                double application;
                if (source.Missile is { } missile)
                {
                    application = enemy.Distance <= weapon.Optimal ? MissileRules.Application(missile, enemy.Signature, enemy.Speed) : 0;
                }
                else
                {
                    double chance = CombatDamage.HitChance(
                        source.Weapon.ItemId < 0 ? Math.Min(enemy.Distance, weapon.Optimal) : enemy.Distance,
                        enemy.Speed / Math.Max(1, enemy.Distance),
                        enemy.Signature,
                        weapon.Optimal,
                        weapon.Falloff,
                        weapon.Tracking,
                        weapon.SignatureResolution
                    );
                    // The median successful quality is a conservative bound on expected turret damage.
                    application = chance * CombatDamage.ShotQuality(chance, chance * .5);
                }
                var part = weapon.Damage.Scale(application * DamageMargin * 1000 / weapon.CycleMilliseconds);
                damage = new(
                    damage.Em + part.Em,
                    damage.Explosive + part.Explosive,
                    damage.Kinetic + part.Kinetic,
                    damage.Thermal + part.Thermal
                );
            }
            double Layer(double hp, DamageVector resonance, double repairs)
            {
                double net = damage.Against(resonance) - repairs;
                return hp <= 0 ? 0
                    : net > 0 ? hp / net
                    : double.PositiveInfinity;
            }
            double recharge =
                enemy.Defense.ShieldRechargeMilliseconds > 0 ? 2500 * enemy.Defense.Shield / enemy.Defense.ShieldRechargeMilliseconds : 0;
            clear +=
                Layer(enemy.Health.Shield, enemy.Defense.ShieldResonance, enemy.ShieldRepairPerSecond + recharge)
                + Layer(enemy.Health.Armor, enemy.Defense.ArmorResonance, enemy.ArmorRepairPerSecond)
                + Layer(enemy.Health.Structure, enemy.Defense.StructureResonance, 0);
        }
        if (!double.IsFinite(clear) || clear > MaximumClearSeconds)
        {
            return Reject("Insufficient applied damage against the pirate group.");
        }
        double cap = clear * supportCapacitorPerSecond;
        foreach (var source in weapons)
        {
            double shots = Math.Ceiling(clear * 1000 / source.Weapon.CycleMilliseconds) + 1;
            if (shots > source.AvailableShots)
            {
                return Reject("Insufficient ammunition for the whole group.");
            }
            cap += shots * source.Weapon.CapacitorNeed;
        }
        if (cap > capacitor)
        {
            return Reject("Insufficient capacitor for the whole group.");
        }
        DamageVector incoming = new(
            opponents.Sum(e => e.DamagePerSecond.Em),
            opponents.Sum(e => e.DamagePerSecond.Explosive),
            opponents.Sum(e => e.DamagePerSecond.Kinetic),
            opponents.Sum(e => e.DamagePerSecond.Thermal)
        );
        double LayerTime(double hp, DamageVector resonance) =>
            hp <= 0 ? 0
            : incoming.Against(resonance) is > 0 and var dps ? hp / dps
            : double.PositiveInfinity;
        double survival =
            LayerTime(pilot.Remaining.Shield, pilot.Defense.ShieldResonance)
            + LayerTime(pilot.Remaining.Armor, pilot.Defense.ArmorResonance)
            + LayerTime(
                Math.Max(0, pilot.Remaining.Structure - pilot.Defense.Structure * CapsuleerDefenseRules.RetreatStructureFraction),
                pilot.Defense.StructureResonance
            );
        return new(
            survival >= clear * SurvivalMargin,
            clear,
            survival,
            survival >= clear * SurvivalMargin ? null : "Insufficient tank against the pirate group."
        );
    }
}
