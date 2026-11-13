using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;

namespace Eden.World.Combat;

internal sealed partial class PilotMissileStore
{
    private static (
        IReadOnlyDictionary<DogmaAttributeId, double> Launcher,
        IReadOnlyDictionary<DogmaAttributeId, double> Charge
    ) BreacherAttributes(
        WorldAction world,
        CatalogDbContext catalog,
        long pilot,
        IReadOnlyDictionary<DogmaAttributeId, double> launcher,
        IReadOnlyDictionary<DogmaAttributeId, double> charge
    )
    {
        var levels = PilotSkillReader.Levels(world, catalog, pilot, Enum.GetValues<BreacherSkill>().Select(skill => (int)skill).ToArray());
        var fitted = launcher.ToDictionary();
        var loaded = charge.ToDictionary();
        Scale(fitted, DogmaAttributeId.WeaponCycleTime, BreacherSkill.RapidFiring, DogmaAttributeId.MissileRateBonus);
        Scale(loaded, DogmaAttributeId.MaxVelocity, BreacherSkill.Projection, DogmaAttributeId.SpeedFactor);
        Scale(loaded, DogmaAttributeId.BreacherDuration, BreacherSkill.CloneLongevity, DogmaAttributeId.DurationBonus);
        Scale(loaded, DogmaAttributeId.BreacherMaxDamage, BreacherSkill.CloneEfficacy, DogmaAttributeId.BreacherDamageBonus);
        Scale(loaded, DogmaAttributeId.BreacherMaxPercent, BreacherSkill.LauncherOperation, DogmaAttributeId.BreacherPercentBonus);
        return (fitted, loaded);

        void Scale(Dictionary<DogmaAttributeId, double> values, DogmaAttributeId attribute, BreacherSkill skill, DogmaAttributeId bonus)
        {
            int level = levels.GetValueOrDefault((int)skill);
            if (level > 0)
            {
                values[attribute] *= 1 + level * catalog.FittingAttributes((int)skill).GetValueOrDefault(bonus) / 100;
            }
        }
    }

    private static void AttachBreacher(WorldMissile row, MissilePayload payload, long time, IReadOnlyList<WorldMissile> rows)
    {
        var pod = payload.Breacher!;
        var previous = rows.FirstOrDefault(other =>
            other.Status == MissileFlightStatus.Attached
            && other.TargetId == row.TargetId
            && other.SourceShipId == row.SourceShipId
            && other.ModuleId == row.ModuleId
            && other.ExpiresAt >= time
        );
        long? attachedAt = previous?.Payload().Breacher?.AttachedAt;
        if (previous is not null)
        {
            previous.Status = MissileFlightStatus.Impact;
            previous.EndedAt = time;
        }
        row.Status = MissileFlightStatus.Attached;
        row.UpdatedAt = previous?.UpdatedAt ?? time;
        row.ExpiresAt = checked(time + (long)Math.Ceiling(pod.DurationMilliseconds * TimeSpan.TicksPerMillisecond));
        row.EndedAt = null;
        row.Save(payload with { Breacher = pod with { AttachedAt = attachedAt ?? time } });
    }

    private bool AdvanceBreacher(
        WorldAction world,
        CatalogDbContext catalog,
        WorldMissile[] rows,
        int system,
        long now,
        List<PilotDamage> pilotImpacts
    )
    {
        bool destroyed = false;
        foreach (var group in rows.Where(row => row.Status == MissileFlightStatus.Attached).GroupBy(row => row.TargetId))
        {
            var effects = group.Select(row => (Row: row, Payload: row.Payload())).ToArray();
            long targetId = group.Key;
            while (true)
            {
                var eligible = effects.Where(effect => effect.Row.Status == MissileFlightStatus.Attached).ToArray();
                if (eligible.Length == 0)
                {
                    break;
                }
                long at = eligible.Min(effect => checked(effect.Row.UpdatedAt + BreacherRules.Tick));
                var active = eligible
                    .Where(effect => effect.Payload.Breacher!.AttachedAt < at && effect.Row.ExpiresAt >= at && effect.Row.UpdatedAt < at)
                    .ToArray();
                foreach (var effect in eligible.Where(effect => effect.Row.ExpiresAt < at && effect.Row.ExpiresAt <= now))
                {
                    End(effect.Row, effect.Row.ExpiresAt);
                }
                if (at > now)
                {
                    break;
                }
                if (active.Length == 0)
                {
                    continue;
                }
                var first = active[0].Row;
                var victim = first.TargetKind == MissileTargetKind.Pilot ? FindVictim(world, catalog, system, targetId, at) : null;
                var npc = first.TargetKind == MissileTargetKind.Npc ? world.Set<WorldNpc>().Find(targetId) : null;
                if (victim is null && (npc is not { DestroyedAt: null, RetiredAt: null } || npc.SystemId != system))
                {
                    foreach (var effect in eligible)
                    {
                        End(effect.Row, at);
                    }
                    break;
                }
                var attributes = victim is not null
                    ? PilotFittingStore.ShipAttributes(world, catalog, victim)
                    : catalog.FittingAttributes(npc!.TypeId);
                var defense = ShipHealth.DefenseFrom(attributes);
                double resistance = Math.Clamp(attributes.GetValueOrDefault(DogmaAttributeId.BreacherResistance, 1), 0, 1);
                if (
                    victim is not null
                    && world
                        .Set<ActiveFittedModule>()
                        .Where(module => module.ShipId == targetId && module.ExpiresAt > at)
                        .AsEnumerable()
                        .Any(module => world.Set<OwnedItem>().Find(module.ModuleId) is { Online: true, TypeId: BreacherRules.CounterType })
                )
                {
                    resistance *= 1 + catalog.FittingAttributes(BreacherRules.CounterType)[DogmaAttributeId.BreacherCounterPercent] / 100;
                }
                var best = active
                    .OrderByDescending(effect => effect.Payload.Breacher!.Damage(defense, resistance))
                    .ThenByDescending(effect => effect.Row.ExpiresAt)
                    .ThenBy(effect => effect.Row.Id)
                    .First();
                double amount = best.Payload.Breacher!.Damage(defense, resistance);
                DamageVector damage = new(0, 0, amount, 0);
                var source = world.Pilots.Find(best.Row.PilotId)!;
                var row = best.Row;
                foreach (var effect in eligible.Where(effect => effect.Row.UpdatedAt < at && effect.Payload.Breacher!.AttachedAt < at))
                {
                    effect.Row.UpdatedAt = at;
                }
                if (victim is not null)
                {
                    var hit = ApplyPilotImpact(
                        world,
                        catalog,
                        row,
                        best.Payload,
                        victim,
                        new(row.TargetId, SpacePosition.Zero, SpacePosition.Zero),
                        at,
                        damage,
                        bypassResistances: true
                    );
                    pilotImpacts.Add(hit);
                    if (hit.Health.Remaining.Destroyed)
                    {
                        foreach (var effect in eligible)
                        {
                            End(effect.Row, at);
                        }
                    }
                }
                else
                {
                    var result = NpcDamagePersistence.Apply(
                        world,
                        catalog,
                        npc!,
                        source,
                        row.SourceShipId,
                        attributes,
                        damage,
                        at,
                        row.LauncherTypeId,
                        row.SourceTypeId,
                        paidProjectile: true,
                        bypassResistances: true
                    );
                    destroyed |= result.Destroyed;
                    world.SaveChanges();
                    row.Outcome = JsonSerializer.Serialize(
                        new MissileImpact(
                            row.Id,
                            row.PilotId,
                            row.SourceShipId,
                            row.TargetId,
                            row.LauncherTypeId,
                            row.ChargeTypeId,
                            result.Health,
                            damage,
                            result.Applied,
                            result.Destroyed,
                            result.Reward is null ? null : NpcDamagePersistence.Read(result.Reward)
                        ),
                        MissileJson.Default.MissileImpact
                    );
                    row.Reported = false;
                    if (result.Destroyed)
                    {
                        foreach (var effect in eligible)
                        {
                            End(effect.Row, at);
                        }
                    }
                }
            }
        }
        return destroyed;

        static void End(WorldMissile row, long time)
        {
            row.Status = MissileFlightStatus.Impact;
            row.EndedAt = time;
        }
    }
}
