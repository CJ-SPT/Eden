using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Ships;

namespace Eden.Gameplay.Combat;

public sealed record LiveHealth(
    HullDefense Defense,
    HullDamage Remaining,
    long UpdatedAt,
    long Revision = 0,
    double? ArmorDamage = null,
    double? StructureDamage = null
)
{
    public HullDamage At(long now) =>
        Remaining with
        {
            Shield = ChargedResource.Recharged(
                Remaining.Shield,
                Defense.Shield,
                Defense.ShieldRechargeMilliseconds,
                Math.Max(0, (now - UpdatedAt) / (double)TimeSpan.TicksPerMillisecond)
            ),
        };
}

public sealed record LiveRepair(long ModuleId, long CompletesAt, double Amount, RepairLayer Layer);

public sealed record LiveTurretDefinition(
    CombatWeapon Weapon,
    int FlagId,
    bool AmmoFed,
    bool Energy,
    int Rounds,
    IReadOnlyDictionary<DogmaAttributeId, double> ChargeAttributes,
    string? Error
);

public sealed record LiveTurret(LiveTurretDefinition Definition, long? ChargeItemId, int Quantity, double CrystalDamage, long ReadyAt);

public sealed record LiveCombatShip(
    long PilotId,
    long ShipId,
    int CorporationId,
    int TypeId,
    int SystemId,
    SpacePosition? Position,
    double Radius,
    double TargetRange,
    double CapacitorCapacity,
    double CapacitorRechargeMilliseconds,
    double Capacitor,
    long CapacitorAt,
    LiveHealth Health,
    IReadOnlyList<LiveRepair> Repairs,
    IReadOnlyDictionary<long, LiveTurret> Turrets,
    IReadOnlyDictionary<long, IReadOnlyList<long>> Banks,
    int GroupId,
    double SignatureRadius,
    IReadOnlyDictionary<long, CombatParty> Contributions,
    CombatTimer? NpcAggression = null
)
{
    public LiveCombatShip SettleRepairs(long now)
    {
        var due = Repairs.Where(r => r.CompletesAt <= now).ToArray();
        if (due.Length == 0)
        {
            return this;
        }
        ShipHealth current = new(ShipId, Health.Defense, Health.At(now));
        foreach (var repair in due)
        {
            current = LocalRepairRules.Apply(current, repair.Amount, repair.Layer);
        }
        return this with
        {
            Health = current.Remaining.Destroyed
                ? Health
                : Health with
                {
                    Remaining = current.Remaining,
                    UpdatedAt = Math.Max(now, Health.UpdatedAt),
                    Revision = Health.Revision + 1,
                    // A local repair must preserve the saved damage of unaffected layers,
                    // including damage above a reduced fitting's current capacity.
                    ArmorDamage = due.Any(r => r.Layer == RepairLayer.Armor)
                        ? Health.Defense.Armor - current.Remaining.Armor
                        : Health.ArmorDamage,
                    StructureDamage = due.Any(r => r.Layer == RepairLayer.Hull)
                        ? Health.Defense.Structure - current.Remaining.Structure
                        : Health.StructureDamage,
                },
            Repairs = Repairs.Where(r => r.CompletesAt > now).ToArray(),
        };
    }
}

public sealed record LiveCombatNpc(
    long Id,
    int TypeId,
    int SystemId,
    SpacePosition Position,
    SpacePosition Velocity,
    double Radius,
    double SignatureRadius,
    LiveHealth Health,
    bool Available,
    IReadOnlyDictionary<long, CombatParty> Contributions,
    NpcRole Role,
    long EncounterId,
    double ReactionMilliseconds,
    int CorporationId,
    int FactionId,
    SpacePosition Home,
    IReadOnlyDictionary<DogmaAttributeId, double> Attributes
)
{
    public IReadOnlyList<ElectronicWarfareContact> IncomingElectronicWarfare { get; init; } = [];

    public NpcNavigationState Navigation { get; init; }
    public long MotionAt { get; init; }
    public double Mass { get; init; }
    public double MissileRange { get; init; }
    public int GroupId { get; init; }
    public int CategoryId { get; init; }
    public string Name { get; init; } = string.Empty;
    public NpcRepairProfile RepairProfile { get; init; } = new(null, null);
    public NpcRepairState RepairState { get; init; } = new();
    public IReadOnlyList<NpcEwarAbility> ElectronicWarfare { get; init; } = [];
    public NpcWarpTransit? Transit { get; init; }
    public NpcActivityContact? Activity { get; init; }
}

public sealed record LiveAggression(
    long NpcId,
    long ShipId,
    long ReadyAt,
    long MissileReadyAt,
    long? LastShotAt,
    long? LastTargetId = null
);

public sealed record TurretDraw(double Accuracy, double Crystal);

public sealed record LiveVolley(LiveCombatShip Ship, LiveCombatNpc Target, CombatVolley Volley, DamageVector Damage);

public static class LiveCombatRules
{
    public static LiveVolley Fire(
        LiveCombatShip ship,
        LiveCombatNpc target,
        long moduleId,
        bool banked,
        SpacePosition velocity,
        long now,
        IReadOnlyList<TurretDraw> draws,
        WeaponActivation? activation
    )
    {
        ship = ship.SettleRepairs(now);
        if (ship.Position is null || !target.Available || ship.SystemId != target.SystemId)
        {
            throw new InvalidDataException("Use an online fitted supported turret on a locked NPC while in space.");
        }
        var separation = target.Position - ship.Position;
        double distance = Math.Max(0, separation.Length - ship.Radius - target.Radius);
        if (distance > ship.TargetRange)
        {
            throw new InvalidDataException("The NPC is outside targeting range.");
        }
        if (ship.Health.At(now).Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot fire its weapons.");
        }
        IReadOnlyList<long> members = banked ? ship.Banks.Values.FirstOrDefault(b => b.Contains(moduleId)) ?? [moduleId] : [moduleId];
        if (draws.Count != members.Count)
        {
            throw new InvalidDataException("A volley needs one independent draw per weapon.");
        }
        var turrets = ship.Turrets.ToDictionary();
        List<CombatWeapon> weapons = [];
        List<Magazine> magazines = [];
        List<double> qualities = [];
        DamageVector combined = DamageVector.None;
        double capacitor = ChargedResource.Recharged(
            ship.Capacitor,
            ship.CapacitorCapacity,
            ship.CapacitorRechargeMilliseconds,
            Math.Max(0, (now - ship.CapacitorAt) / (double)TimeSpan.TicksPerMillisecond)
        );
        for (int index = 0; index < members.Count; index++)
        {
            if (!turrets.TryGetValue(members[index], out var turret) || turret.Definition.Error is { })
            {
                throw new InvalidDataException(turret?.Definition.Error ?? "Use an online fitted supported turret.");
            }
            var definition = turret.Definition;
            var weapon = definition.Weapon;
            if (
                activation is not null
                && activation != (definition.AmmoFed && !definition.Energy ? WeaponActivation.AmmoFed : WeaponActivation.Laser)
            )
            {
                throw new InvalidDataException("Use the weapon's default firing effect.");
            }
            if (now < turret.ReadyAt)
            {
                throw new InvalidDataException("The weapon is still cycling.");
            }
            if (definition.AmmoFed && (turret.ChargeItemId is null || turret.Quantity < definition.Rounds || definition.Rounds <= 0))
            {
                throw new InvalidDataException("Load enough compatible ammunition before firing the weapon.");
            }
            if (capacitor + InventoryDefinitions.ResourceTolerance < weapon.CapacitorNeed)
            {
                throw new InvalidDataException("Insufficient capacitor to activate the module.");
            }
            capacitor = Math.Max(0, capacitor - weapon.CapacitorNeed);
            double chance = CombatDamage.HitChance(
                distance,
                CombatDamage.AngularVelocity(separation, velocity - target.Velocity),
                target.SignatureRadius,
                weapon.Optimal,
                weapon.Falloff,
                weapon.Tracking,
                weapon.SignatureResolution
            );
            double quality = CombatDamage.ShotQuality(chance, draws[index].Accuracy);
            qualities.Add(quality);
            var component = weapon.Damage.Scale(quality);
            combined = new(
                combined.Em + component.Em,
                combined.Explosive + component.Explosive,
                combined.Kinetic + component.Kinetic,
                combined.Thermal + component.Thermal
            );
            long ready = checked(now + (long)Math.Ceiling(weapon.CycleMilliseconds * TimeSpan.TicksPerMillisecond));
            if (definition.AmmoFed)
            {
                if (definition.Energy)
                {
                    var wear = CrystalRules.Use(definition.ChargeAttributes, turret.CrystalDamage, draws[index].Crystal);
                    turret = turret with { CrystalDamage = wear.Damage, Quantity = wear.Destroyed ? 0 : turret.Quantity };
                }
                else
                {
                    turret = turret with { Quantity = turret.Quantity - definition.Rounds };
                }
                magazines.Add(
                    new(weapon.ItemId, ship.ShipId, definition.FlagId, weapon.ChargeTypeId, turret.Quantity, 0, turret.CrystalDamage)
                );
            }
            turrets[members[index]] = turret with { ReadyAt = ready };
            weapons.Add(weapon);
        }
        if (weapons.Select(w => w.ChargeTypeId).Distinct().Count() > 1)
        {
            throw new InvalidDataException("Load the same ammunition in every grouped weapon.");
        }
        var damage = CombatDamage.Apply(target.Health.At(now), target.Health.Defense, combined);
        double average = qualities.Average();
        var first = weapons[0];
        var aggregate =
            weapons.Count == 1
                ? first
                : first with
                {
                    Damage = average > 0 ? combined.Scale(1 / average) : combined,
                    CycleMilliseconds = weapons.Max(w => w.CycleMilliseconds),
                };
        var contributors = target.Contributions.ToDictionary();
        if (damage.Applied > 0)
        {
            contributors[ship.ShipId] = new CombatParty(
                ship.ShipId,
                ship.PilotId,
                ship.CorporationId,
                null,
                ship.TypeId,
                first.TypeId,
                null,
                contributors.GetValueOrDefault(ship.ShipId)?.Damage + damage.Applied ?? damage.Applied
            );
        }
        var nextShip = ship with { Turrets = turrets, Capacitor = capacitor, CapacitorAt = Math.Max(now, ship.CapacitorAt) };
        var nextTarget = target with
        {
            Health = target.Health with
            {
                Remaining = damage.Remaining,
                UpdatedAt = Math.Max(now, target.Health.UpdatedAt),
                Revision = target.Health.Revision + 1,
                ArmorDamage = target.Health.Defense.Armor - damage.Remaining.Armor,
                StructureDamage = target.Health.Defense.Structure - damage.Remaining.Structure,
            },
            Contributions = contributors,
            Available = !damage.Remaining.Destroyed,
        };
        CombatShot shot = new(
            aggregate,
            damage.Applied,
            average,
            new(target.Id, target.Health.Defense, damage.Remaining),
            capacitor,
            damage.Remaining.Destroyed,
            null,
            magazines.FirstOrDefault()
        );
        return new(nextShip, nextTarget, new(shot, weapons, magazines), combined);
    }
}
