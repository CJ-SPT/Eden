using Eden.Protocol;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Market;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

[Injectable]
internal sealed class PilotCombatStore(WorldDatabase database, IPilotFitting fitting) : IPilotCombat
{
    public IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(long pilotId, InventoryItem module)
    {
        var attributes = fitting.Attributes(module.TypeId).ToDictionary();
        if (module.TypeId != (int)CombatWeaponType.CivilianGatlingRailgun) { return attributes; }
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        int Level(CombatSkill skill) => PilotSkillReader.Level(world, catalog, pilotId, (int)skill);
        // Pinned effects 414/582, 290, 2847, 587, 287 and 298 apply to this
        // Energy Weapon's Gunnery requirement, not Small Hybrid Turret skills.
        attributes[DogmaAttributeId.WeaponCycleTime] *= (1 - CombatDefinitions.GunneryCycleReduction * Level(CombatSkill.Gunnery)) *
            (1 - CombatDefinitions.RapidFiringCycleReduction * Level(CombatSkill.RapidFiring));
        attributes[DogmaAttributeId.MaxRange] *= 1 + CombatDefinitions.WeaponSkillBonus * Level(CombatSkill.Sharpshooter);
        attributes[DogmaAttributeId.AccuracyFalloff] *= 1 + CombatDefinitions.WeaponSkillBonus * Level(CombatSkill.TrajectoryAnalysis);
        attributes[DogmaAttributeId.TrackingSpeed] *= 1 + CombatDefinitions.WeaponSkillBonus * Level(CombatSkill.MotionPrediction);
        attributes[DogmaAttributeId.DamageMultiplier] *= 1 + CombatDefinitions.SurgicalStrikeDamageBonus * Level(CombatSkill.SurgicalStrike);
        attributes[DogmaAttributeId.CapacitorNeed] *= 1 - CombatDefinitions.WeaponSkillBonus * Level(CombatSkill.ControlledBursts);
        return attributes;
    }

    public CombatShot Fire(long pilotId, long moduleId, long targetId, SpacePosition velocity, long now, double roll)
    {
        if (!double.IsFinite(roll) || roll is < 0 or >= 1) { throw new InvalidDataException("Invalid weapon accuracy draw."); }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var module = world.Set<OwnedItem>().Find(moduleId);
        var npc = world.Set<WorldNpc>().Find(targetId);
        if (pilot.ActiveShip.Position is not { } position || module is null || module.OwnerId != pilotId || module.LocationId != pilot.ActiveShipId ||
            module.TypeId != (int)CombatWeaponType.CivilianGatlingRailgun || !module.Singleton || !module.Online || !InventoryDefinitions.IsHighSlot(module.FlagId) ||
            npc is null || npc.DestroyedAt.HasValue)
        {
            throw new InvalidDataException("Use an online fitted civilian railgun on a locked NPC while in space.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, module.TypeId);
        int system = position.SystemId ?? catalog.Set<Station>().Single(s => s.Id == pilot.ActiveShip.StationId).SolarSystemId;
        var npcType = catalog.Set<ItemType>().Single(t => t.Id == npc.TypeId);
        var hull = catalog.Set<ItemType>().Single(t => t.Id == pilot.ActiveShip.TypeId);
        var separation = new SpacePosition(npc.X - position.X, npc.Y - position.Y, npc.Z - position.Z);
        double distance = Math.Max(0, separation.DistanceTo(SpacePosition.Zero) - (hull.Radius ?? 0) - (npcType.Radius ?? 0));
        var ship = fitting.ShipAttributes(pilotId);
        if (npc.SystemId != system || distance > ship.GetValueOrDefault(DogmaAttributeId.MaxTargetRange))
        {
            throw new InvalidDataException("The NPC is outside targeting range.");
        }
        var attributes = ModuleAttributes(pilotId, PilotInventoryStore.Read(catalog, module));
        if (attributes.Values.Any(v => !double.IsFinite(v)) || attributes.GetValueOrDefault(DogmaAttributeId.WeaponCycleTime) <= 0)
        {
            throw new InvalidDataException("Invalid weapon cycle attributes.");
        }
        var cooldown = world.Set<WeaponCooldown>().Find(moduleId);
        if (cooldown is not null && now < cooldown.ReadyAt) { throw new InvalidDataException("The weapon is still cycling."); }
        DamageVector damage = new(attributes.GetValueOrDefault(DogmaAttributeId.EmDamage), attributes.GetValueOrDefault(DogmaAttributeId.ExplosiveDamage),
            attributes.GetValueOrDefault(DogmaAttributeId.KineticDamage), attributes.GetValueOrDefault(DogmaAttributeId.ThermalDamage));
        CombatWeapon weapon = new(moduleId, module.TypeId, checked((int)attributes.GetValueOrDefault(DogmaAttributeId.BuiltInCharge)),
            damage.Scale(attributes.GetValueOrDefault(DogmaAttributeId.DamageMultiplier)), attributes[DogmaAttributeId.WeaponCycleTime],
            attributes.GetValueOrDefault(DogmaAttributeId.MaxRange), attributes.GetValueOrDefault(DogmaAttributeId.AccuracyFalloff),
            attributes.GetValueOrDefault(DogmaAttributeId.TrackingSpeed), attributes.GetValueOrDefault(DogmaAttributeId.WeaponSignatureResolution),
            attributes.GetValueOrDefault(DogmaAttributeId.CapacitorNeed));
        var target = fitting.Attributes(npc.TypeId);
        var defense = ShipHealth.DefenseFrom(target);
        HullDamage health = new(ChargedResource.Recharged(npc.ShieldCharge, defense.Shield, defense.ShieldRechargeMilliseconds,
            Math.Max(0, (now - npc.UpdatedAt) / (double)TimeSpan.TicksPerMillisecond)),
            Math.Max(0, defense.Armor - npc.ArmorDamage), Math.Max(0, defense.Structure - npc.StructureDamage));
        double chance = CombatDamage.HitChance(distance, CombatDamage.AngularVelocity(separation, velocity), target.GetValueOrDefault(DogmaAttributeId.SignatureRadius),
            weapon.Optimal, weapon.Falloff, weapon.Tracking, weapon.SignatureResolution);
        double quality = CombatDamage.ShotQuality(chance, roll);
        var result = CombatDamage.Apply(health, defense, weapon.Damage.Scale(quality));
        double capacitor = ShipResources.SpendCapacitor(world, pilot.ActiveShipId, ship, weapon.CapacitorNeed, now);
        cooldown ??= world.Set<WeaponCooldown>().Add(new() { ModuleId = moduleId }).Entity;
        cooldown.ReadyAt = checked(now + (long)Math.Ceiling(weapon.CycleMilliseconds * TimeSpan.TicksPerMillisecond));
        npc.ShieldCharge = result.Remaining.Shield;
        npc.ArmorDamage = defense.Armor - result.Remaining.Armor;
        npc.StructureDamage = defense.Structure - result.Remaining.Structure;
        npc.UpdatedAt = Math.Max(now, npc.UpdatedAt);
        NpcBounty? reward = null;
        if (result.Remaining.Destroyed)
        {
            npc.DestroyedAt = now;
            double bounty = target.GetValueOrDefault(DogmaAttributeId.EntityKillBounty);
            if (!double.IsFinite(bounty) || bounty < 0) { throw new InvalidDataException("Invalid NPC bounty."); }
            long cents = checked((long)Math.Round(bounty * MarketDefinitions.CentsPerIsk, MidpointRounding.ToEven));
            if (cents > 0)
            {
                var wallet = world.Set<PilotWallet>().Find(pilotId) ?? world.Set<PilotWallet>().Add(new() { PilotId = pilotId }).Entity;
                wallet.Cents = checked(wallet.Cents + cents);
                reward = world.Set<NpcBounty>().Add(new()
                {
                    PilotId = pilotId,
                    NpcId = npc.Id,
                    TypeId = npc.TypeId,
                    SystemId = npc.SystemId,
                    Time = now,
                    AmountCents = cents,
                    BalanceCents = wallet.Cents,
                }).Entity;
            }
        }
        world.SaveChanges();
        transaction.Commit();
        return new(weapon, result.Applied, quality, new(npc.Id, defense, result.Remaining), capacitor, result.Remaining.Destroyed,
            reward is null ? null : Read(reward));
    }

    private static CombatReward Read(NpcBounty b) => new(b.Id, b.PilotId, b.NpcId, b.TypeId, b.SystemId, b.Time, b.AmountCents, b.BalanceCents);

    public IReadOnlyList<CombatReward> Rewards(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        return world.Set<NpcBounty>().AsNoTracking().Where(b => b.PilotId == pilotId).OrderByDescending(b => b.Time).ThenByDescending(b => b.Id)
            .AsEnumerable().Select(Read).ToArray();
    }
}
