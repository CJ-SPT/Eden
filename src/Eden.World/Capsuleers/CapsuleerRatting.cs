using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Runtime.Combat;
using Eden.World.Storage;

namespace Eden.World.Capsuleers;

[Injectable]
internal sealed class CapsuleerRatting(
    WorldDatabase database,
    INpcEncounters encounters,
    IWrecks wrecks,
    CapsuleerDefense defense,
    IPilotDrones drones
)
{
    private const int MaximumCachedAssessments = 512;

    private readonly Dictionary<int, DamageVector> _missileDamage = [];
    private readonly Dictionary<long, AssessmentEntry> _assessments = [];

    private sealed record AssessmentEntry(
        ShipHealth Health,
        double Capacitor,
        RattingWeapon[] Weapons,
        RattingOpponent[] Enemies,
        double SupportCapacitor,
        RattingAssessment Result
    );

    internal NpcContact[] Pirates(DockedPilot pilot, long belt, long now) =>
        encounters
            .ReadScene(pilot.SystemId, pilot.Position!, now)
            .Visible.Where(n =>
                n.Role == NpcRole.Pirate
                && n.AnchorKind == NpcAnchorKind.Belt
                && !n.Health.Remaining.Destroyed
                && n.Warp is null
                && pilot.Position!.DistanceTo(n.Position) <= NpcEncounterDefinitions.VisibilityRange
            )
            .OrderBy(n => n.Id)
            .ToArray();

    internal IReadOnlyList<WreckContact> Wrecks(DockedPilot pilot, long now) =>
        wrecks
            .List(pilot.SystemId, now)
            .Where(w =>
                w.OwnerId == pilot.PilotId
                && !w.Empty
                && w.Id < WreckDefinitions.FirstPlayerWreckId
                && pilot.Position!.DistanceTo(w.Position) <= WreckDefinitions.VisibilityRange
            )
            .ToArray();

    internal RattingAssessment Assess(DockedPilot pilot, NpcContact[] contacts, long now)
    {
        var profile = defense.Profile(pilot.PilotId);
        var runtime = LiveCombatRuntime.Find(database.Path);
        var ship = runtime?.DefenseShip(pilot.PilotId, now);
        if (ship is null || !profile.Combat || TrafficRules.LightHull((Eden.Gameplay.Ships.HullClass)pilot.ShipGroupId))
        {
            return new(false, double.PositiveInfinity, 0, "No supported resident combat fit.");
        }
        var weapons = profile
            .RattingWeapons.Select(source =>
            {
                if (ship.Turrets.TryGetValue(source.Weapon.ItemId, out var turret))
                {
                    return source with
                    {
                        Weapon = turret.Definition.Weapon,
                        AvailableShots =
                            turret.Definition.Error is null && (!turret.Definition.AmmoFed || turret.Quantity > 0)
                                ? Math.Min(
                                    source.AvailableShots,
                                    turret.Definition.Energy || !turret.Definition.AmmoFed
                                        ? int.MaxValue
                                        : source.AvailableShots
                                            - Math.Max(0, source.LoadedShots - turret.Quantity / Math.Max(1, turret.Definition.Rounds))
                                )
                                : 0,
                    };
                }
                return source;
            })
            .Where(w => w.AvailableShots > 0)
            .ToArray();
        var activeDrones = drones
            .List(pilot.SystemId, now)
            .Where(d =>
                d.PilotId == pilot.PilotId
                && d.ShipId == pilot.ShipId
                && !d.Health.Remaining.Destroyed
                && d.Profile.Kind != CombatDroneKind.Sentry
            )
            .ToArray();
        if (activeDrones.Length > 0)
        {
            weapons = weapons
                .Where(w => w.Weapon.ItemId > 0)
                .Concat(
                    activeDrones.Select(d => new RattingWeapon(
                        new(
                            -d.Id,
                            d.Profile.TypeId,
                            0,
                            d.Profile.Damage,
                            d.Profile.CycleMilliseconds,
                            d.Profile.Optimal,
                            d.Profile.Falloff,
                            d.Profile.Tracking,
                            d.Profile.SignatureReference,
                            0
                        ),
                        int.MaxValue
                    ))
                )
                .ToArray();
        }
        List<RattingOpponent> enemies = [];
        foreach (var contact in contacts)
        {
            var npc = runtime!.RattingNpc(contact.Id, now);
            if (npc is null || npc.ElectronicWarfare.Count > 0)
            {
                return new(false, double.PositiveInfinity, 0, "Unsupported or electronic-warfare encounter.");
            }
            var attributes = npc.Attributes;
            double cycle = attributes.GetValueOrDefault(DogmaAttributeId.WeaponCycleTime);
            DamageVector incoming =
                cycle > 0
                    ? new DamageVector(
                        attributes.GetValueOrDefault(DogmaAttributeId.EmDamage),
                        attributes.GetValueOrDefault(DogmaAttributeId.ExplosiveDamage),
                        attributes.GetValueOrDefault(DogmaAttributeId.KineticDamage),
                        attributes.GetValueOrDefault(DogmaAttributeId.ThermalDamage)
                    ).Scale(attributes.GetValueOrDefault(DogmaAttributeId.DamageMultiplier) * 1500 / cycle)
                    : DamageVector.None;
            if (attributes.GetValueOrDefault(DogmaAttributeId.EntityMissileType) is > 0 and var missile)
            {
                double duration = attributes.GetValueOrDefault(DogmaAttributeId.EntityMissileCycle);
                if (duration <= 0)
                {
                    return new(false, double.PositiveInfinity, 0, "Unknown incoming missile cycle.");
                }
                if (!_missileDamage.TryGetValue(contact.TypeId, out var missileDamage))
                {
                    using CatalogDbContext catalog = new(database.CatalogPath);
                    missileDamage = NpcMissileRules.Warhead(PilotFittingStore.ReadAttributes(catalog, (int)missile), attributes).Damage;
                    _missileDamage[contact.TypeId] = missileDamage;
                }
                var dps = missileDamage.Scale(1000 / duration);
                incoming = new(
                    incoming.Em + dps.Em,
                    incoming.Explosive + dps.Explosive,
                    incoming.Kinetic + dps.Kinetic,
                    incoming.Thermal + dps.Thermal
                );
            }
            double Repair(NpcRepairCycle? repair) => repair is null ? 0 : repair.Amount * TimeSpan.TicksPerSecond / repair.DurationTicks;
            double distance = Math.Max(1, pilot.Position!.DistanceTo(npc.Position) - pilot.Flight.ShipRadius - npc.Radius);
            if (distance > profile.TargetRange)
            {
                return new(false, double.PositiveInfinity, 0, "Outside targeting range.");
            }
            enemies.Add(
                new(
                    npc.Id,
                    npc.Health.Defense,
                    npc.Health.At(now),
                    npc.SignatureRadius,
                    Math.Max(npc.Velocity.Length, attributes.GetValueOrDefault(DogmaAttributeId.MaxVelocity)),
                    distance,
                    incoming,
                    Repair(npc.RepairProfile.Shield),
                    Repair(npc.RepairProfile.Armor)
                )
            );
        }
        double capacitor = Eden.Gameplay.Ships.ChargedResource.Recharged(
            ship.Capacitor,
            ship.CapacitorCapacity,
            ship.CapacitorRechargeMilliseconds,
            Math.Max(0, (now - ship.CapacitorAt) / (double)TimeSpan.TicksPerMillisecond)
        );
        double support = profile
            .Modules.Where(m => m.Effect is not (DogmaEffect.LaserFired or DogmaEffect.ProjectileFired or DogmaEffect.UseMissiles))
            .Sum(m =>
                m.Attributes.GetValueOrDefault(DogmaAttributeId.CapacitorNeed)
                * 1000
                / Math.Max(
                    1,
                    m.Attributes.GetValueOrDefault(
                        DogmaAttributeId.Duration,
                        m.Attributes.GetValueOrDefault(DogmaAttributeId.WeaponCycleTime, 1000)
                    )
                )
            );
        capacitor = Math.Max(0, capacitor - ship.CapacitorCapacity * CapsuleerDefenseRules.OffensiveCapacitorReserve);
        ShipHealth health = new(ship.ShipId, ship.Health.Defense, ship.Health.At(now));
        var opponents = enemies.ToArray();
        if (
            _assessments.TryGetValue(pilot.PilotId, out var cached)
            && cached.Health == health
            && cached.Capacitor == capacitor
            && cached.SupportCapacitor == support
            && cached.Weapons.SequenceEqual(weapons)
            && cached.Enemies.SequenceEqual(opponents)
        )
        {
            return cached.Result;
        }
        var result = RattingRules.Assess(health, capacitor, weapons, opponents, support);
        if (_assessments.Count >= MaximumCachedAssessments)
        {
            var live = LiveWorld.Find(database.Path);
            var cold = _assessments
                .Where(p =>
                    p.Key != pilot.PilotId
                    && (live is null || live.KnownShipSystem(p.Value.Health.ShipId) is not int system || !live.IsDetailedSystem(system))
                )
                .Select(p => p.Key)
                .Take(1)
                .ToArray();
            if (cold.Length != 0)
            {
                _assessments.Remove(cold[0]);
            }
        }
        _assessments[pilot.PilotId] = new(health, capacitor, weapons, opponents, support, result);
        return result;
    }
}
