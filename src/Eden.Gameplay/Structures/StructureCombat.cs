using Eden.Gameplay.Combat;

namespace Eden.Gameplay.Structures;

public sealed record StructureCombatProfile(HullDefense Defense, HullDamage DamageLimits, HullDamage PauseThresholds, bool AutoRepair);

public sealed record StructureBattle(
    StructureState State,
    long StartedAt,
    long? EndsAt,
    HullDamage Health,
    long? RepairAt,
    long DamageWindowAt,
    HullDamage WindowDamage,
    long PausedUntil
);

public sealed record StructureBattleContext(
    StructureProfile Structure,
    StructureCombatProfile Combat,
    bool FullPower,
    bool Abandoned,
    bool Wormhole,
    double Security,
    int ReinforceHour,
    long ReinforcementJitter
)
{
    public bool HasCore { get; init; } = true;
}

public sealed record StructureHit(StructureBattle Battle, double Applied);

public static class StructureCombatRules
{
    public const long DamageWindow = TimeSpan.TicksPerSecond;
    public const long HullRepairDuration = 30 * TimeSpan.TicksPerMinute;
    public const long MediumWormholeDelay = 60 * TimeSpan.TicksPerHour;
    public const long MediumLowNullDelay = 84 * TimeSpan.TicksPerHour;
    public const long MediumHighDelay = 108 * TimeSpan.TicksPerHour;
    public const long LargeArmorDelay = TimeSpan.TicksPerDay;
    public const long LargeWormholeHullDelay = 36 * TimeSpan.TicksPerHour;
    public const long LargeLowNullHullDelay = 60 * TimeSpan.TicksPerHour;
    public const long LargeHighHullDelay = 108 * TimeSpan.TicksPerHour;
    public const long FlexHullDelay = TimeSpan.TicksPerHour;
    public const long MediumExitJitter = 90 * TimeSpan.TicksPerMinute;
    public const long LargeExitJitter = 3 * TimeSpan.TicksPerHour;
    public const long FlexExitJitter = 30 * TimeSpan.TicksPerMinute;

    public static bool Vulnerable(StructureState state) =>
        state
            is StructureState.Onlining
                or StructureState.ShieldVulnerable
                or StructureState.ArmorVulnerable
                or StructureState.HullVulnerable
                or StructureState.AnchorVulnerable
                or StructureState.DeployVulnerable;

    // Build 3662168 structures.const.STATE_SHIELD_ARMOR_HULL.
    public static HullDamage StateHealth(StructureState state, HullDefense defense) =>
        state switch
        {
            StructureState.ArmorReinforced or StructureState.ArmorVulnerable => new(0, defense.Armor, defense.Structure),
            StructureState.Anchoring
            or StructureState.Fitting
            or StructureState.Onlining
            or StructureState.HullReinforced
            or StructureState.HullVulnerable
            or StructureState.AnchorVulnerable
            or StructureState.DeployVulnerable => new(0, 0, defense.Structure),
            _ => defense.Undamaged,
        };

    public static void CheckAttack(StructureProfile structure, double security, StructureBattle battle, bool retaliation = false)
    {
        if (structure.Family == StructureFamily.SovereigntyHub)
        {
            throw new InvalidDataException("Sovereignty hubs require Entosis warfare.");
        }
        if (!double.IsFinite(security) || security >= StructureRules.HighSecurityBoundary && !retaliation)
        {
            // High-security attacks require war authority or retaliation against this exact attacking structure.
            throw new InvalidDataException("A declared war is required to attack a high-security structure.");
        }
        if (!Vulnerable(battle.State))
        {
            throw new InvalidDataException("The structure is invulnerable.");
        }
    }

    public static StructureBattle Advance(StructureBattleContext context, StructureBattle battle, long now)
    {
        if (battle.PausedUntil != 0 && battle.PausedUntil <= now)
        {
            battle = battle with { PausedUntil = 0 };
        }
        if (battle.EndsAt is long end && end <= now)
        {
            StructureState? next = battle.State switch
            {
                StructureState.ArmorReinforced => StructureState.ArmorVulnerable,
                StructureState.HullReinforced => StructureState.HullVulnerable,
                _ => null,
            };
            if (next is { } state)
            {
                battle = battle with
                {
                    State = state,
                    StartedAt = end,
                    EndsAt = null,
                    RepairAt = context.Combat.AutoRepair ? checked(end + RepairDuration(state)) : null,
                };
            }
        }
        if (
            battle.RepairAt <= now
            && Vulnerable(battle.State)
            && battle.State != StructureState.AnchorVulnerable
            && (battle.State != StructureState.Onlining || context.HasCore)
        )
        {
            if (battle.State == StructureState.DeployVulnerable)
            {
                return battle with
                {
                    StartedAt = battle.RepairAt!.Value,
                    Health = StateHealth(battle.State, context.Combat.Defense),
                    RepairAt = null,
                    DamageWindowAt = 0,
                    WindowDamage = new(0, 0, 0),
                    PausedUntil = 0,
                };
            }
            return new(
                StructureState.ShieldVulnerable,
                battle.RepairAt!.Value,
                null,
                context.Combat.Defense.Undamaged,
                null,
                0,
                new(0, 0, 0),
                0
            );
        }
        return battle;
    }

    public static StructureHit Apply(StructureBattleContext context, StructureBattle battle, DamageVector damage, long now)
    {
        if (!double.IsFinite(damage.Total) || new[] { damage.Em, damage.Explosive, damage.Kinetic, damage.Thermal }.Any(v => v < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(damage));
        }
        if (now < battle.StartedAt || now < battle.DamageWindowAt)
        {
            throw new InvalidDataException("Structure damage cannot move its saved clock backwards.");
        }
        battle = Advance(context, battle, now);
        if (!Vulnerable(battle.State))
        {
            throw new InvalidDataException("The structure is invulnerable.");
        }
        if (damage.Total == 0)
        {
            return new(battle, 0);
        }
        long window = now - now % DamageWindow;
        battle = battle.DamageWindowAt == window ? battle : battle with { DamageWindowAt = window, WindowDamage = new(0, 0, 0) };
        double rawRemaining = 1;
        double applied = 0;
        for (int layer = 0; layer < 3 && rawRemaining > 0; layer++)
        {
            double hp = Layer(battle.Health, layer);
            if (hp <= 0)
            {
                continue;
            }
            DamageVector resistance = layer switch
            {
                0 => context.Combat.Defense.ShieldResonance,
                1 => context.Combat.Defense.ArmorResonance,
                _ => context.Combat.Defense.StructureResonance,
            };
            double effective = damage.Against(resistance) * rawRemaining;
            double limit = Layer(context.Combat.DamageLimits, layer);
            double budget = limit > 0 ? Math.Max(0, limit - Layer(battle.WindowDamage, layer)) : double.MaxValue;
            double dealt = Math.Min(hp, Math.Min(effective, budget));
            battle = battle with
            {
                Health = WithLayer(battle.Health, layer, hp - dealt),
                WindowDamage = WithLayer(battle.WindowDamage, layer, Layer(battle.WindowDamage, layer) + dealt),
            };
            applied += dealt;
            if (dealt > 0 && context.Combat.AutoRepair && (battle.State != StructureState.Onlining || context.HasCore))
            {
                battle = battle with
                {
                    StartedAt = battle.RepairAt is null ? now : battle.StartedAt,
                    RepairAt = battle.RepairAt ?? checked(now + RepairDuration(battle.State)),
                };
                if (Layer(battle.WindowDamage, layer) >= Layer(context.Combat.PauseThresholds, layer))
                {
                    long pauseEnd = checked(now + DamageWindow);
                    long extension = Math.Max(0, pauseEnd - Math.Max(now, battle.PausedUntil));
                    battle = battle with
                    {
                        RepairAt = checked(battle.RepairAt + extension),
                        PausedUntil = Math.Max(battle.PausedUntil, pauseEnd),
                    };
                }
            }
            if (hp > dealt)
            {
                break;
            }
            var next = AfterLayer(context, battle, layer, now);
            if (next.State != battle.State)
            {
                battle = next;
                if (!Vulnerable(battle.State))
                {
                    break;
                }
            }
            rawRemaining = effective > 0 ? rawRemaining * Math.Max(0, 1 - dealt / effective) : 0;
        }
        return new(battle, applied);
    }

    private static StructureBattle AfterLayer(StructureBattleContext context, StructureBattle battle, int layer, long now)
    {
        if (layer == 2)
        {
            return battle with { State = StructureState.Destroyed, StartedAt = now, EndsAt = null, RepairAt = null };
        }
        StructureState state;
        if (layer == 0)
        {
            state =
                context.Abandoned || context.Structure.Size == StructureSize.Flex ? StructureState.ArmorVulnerable
                : context.FullPower || context.Structure.Size == StructureSize.Medium ? StructureState.ArmorReinforced
                : StructureState.ArmorVulnerable;
        }
        else
        {
            state =
                context.Abandoned
                || context.Structure.Size == StructureSize.Medium
                || context.Structure.Size == StructureSize.Flex && !context.FullPower
                    ? StructureState.HullVulnerable
                    : StructureState.HullReinforced;
        }
        bool reinforced = state is StructureState.ArmorReinforced or StructureState.HullReinforced;
        return battle with
        {
            State = state,
            StartedAt = now,
            EndsAt = reinforced ? ReinforcementExit(context, state, now) : null,
            RepairAt = !reinforced && context.Combat.AutoRepair ? checked(now + RepairDuration(state)) : null,
            PausedUntil = 0,
        };
    }

    public static long ReinforcementExit(StructureBattleContext context, StructureState state, long now)
    {
        if (context.ReinforceHour is < 0 or > 23)
        {
            throw new ArgumentOutOfRangeException(nameof(context));
        }
        long delay =
            state == StructureState.ArmorReinforced
                ? context.Structure.Size != StructureSize.Medium
                    ? LargeArmorDelay
                    : context.Wormhole
                        ? MediumWormholeDelay
                        : context.Security >= StructureRules.HighSecurityBoundary
                            ? MediumHighDelay
                            : MediumLowNullDelay
                : context.Structure.Size == StructureSize.Flex
                    ? FlexHullDelay
                    : context.Wormhole
                        ? LargeWormholeHullDelay
                        : context.Security >= StructureRules.HighSecurityBoundary
                            ? LargeHighHullDelay
                            : LargeLowNullHullDelay;
        long jitterLimit = context.Structure.Size switch
        {
            StructureSize.Medium => MediumExitJitter,
            StructureSize.Flex => FlexExitJitter,
            _ => LargeExitJitter,
        };
        if (Math.Abs((decimal)context.ReinforcementJitter) > jitterLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(context));
        }
        long earliest = checked(now + delay);
        DateTime candidate = DateTime.FromFileTimeUtc(earliest).Date.AddHours(context.ReinforceHour);
        if (candidate.ToFileTimeUtc() < earliest)
        {
            candidate = candidate.AddDays(1);
        }
        return checked(candidate.ToFileTimeUtc() + context.ReinforcementJitter);
    }

    private static long RepairDuration(StructureState state) =>
        state == StructureState.HullVulnerable ? HullRepairDuration : StructureRules.RepairDuration;

    private static double Layer(HullDamage damage, int layer) =>
        layer switch
        {
            0 => damage.Shield,
            1 => damage.Armor,
            _ => damage.Structure,
        };

    private static HullDamage WithLayer(HullDamage damage, int layer, double value) =>
        layer switch
        {
            0 => damage with { Shield = value },
            1 => damage with { Armor = value },
            _ => damage with { Structure = value },
        };
}
