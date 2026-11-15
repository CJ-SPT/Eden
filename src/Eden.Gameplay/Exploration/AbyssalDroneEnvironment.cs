using Eden.Gameplay.Combat;
using Eden.Gameplay.Drones;

namespace Eden.Gameplay.Exploration;

public sealed record AbyssalDroneEnvironment(
    DroneProfile Base,
    AbyssalWeather Weather,
    int Penalty,
    int Room,
    int Seed,
    AbyssalFieldMask DisabledFields = AbyssalFieldMask.None,
    AbyssalFieldMask? AppliedFields = null
)
{
    public static DroneContact Refresh(DroneContact contact)
    {
        if (contact.Abyssal is not { } environment)
        {
            return contact;
        }
        var mask = AbyssalEnvironmentRules.Fields(environment.Room, environment.Seed, contact.Position) & ~environment.DisabledFields;
        if (mask == environment.AppliedFields)
        {
            return contact;
        }
        var source = environment.Base;
        double penalty = environment.Penalty / 100.0;
        var defense = source.Defense with
        {
            Shield = source.Defense.Shield * (environment.Weather == AbyssalWeather.Gamma ? 1.5 : 1),
            Armor = source.Defense.Armor * (environment.Weather == AbyssalWeather.Firestorm ? 1.5 : 1),
            ShieldResonance = Resistance(source.Defense.ShieldResonance),
            ArmorResonance = Resistance(source.Defense.ArmorResonance),
            StructureResonance = Resistance(source.Defense.StructureResonance),
        };
        var profile = source with
        {
            MaximumSpeed =
                source.MaximumSpeed
                * (environment.Weather == AbyssalWeather.Dark ? 1.5f : 1)
                * (mask.HasFlag(AbyssalFieldMask.Tachyon) ? 4 : 1),
            Agility = source.Agility * (mask.HasFlag(AbyssalFieldMask.Tachyon) ? 0.5f : 1),
            Optimal = source.Optimal * (environment.Weather == AbyssalWeather.Dark ? 1 - penalty : 1),
            Falloff = source.Falloff * (environment.Weather == AbyssalWeather.Dark ? 1 - penalty : 1),
            Tracking =
                source.Tracking
                * (
                    mask.HasFlag(AbyssalFieldMask.ShortTracking) ? 1.8
                    : mask.HasFlag(AbyssalFieldMask.MediumTracking) ? 1.6
                    : 1
                ),
            Defense = defense,
        };
        return contact with
        {
            Profile = profile,
            Abyssal = environment with { AppliedFields = mask },
            Health = contact.Health with
            {
                Defense = defense,
                Remaining = new(
                    Math.Min(defense.Shield, contact.Health.Remaining.Shield),
                    Math.Max(0, defense.Armor - (contact.Profile.Defense.Armor - contact.Health.Remaining.Armor)),
                    contact.Health.Remaining.Structure
                ),
            },
        };

        DamageVector Resistance(DamageVector value) =>
            environment.Weather switch
            {
                AbyssalWeather.Electrical => value with { Em = value.Em * (1 + penalty) },
                AbyssalWeather.Exotic => value with { Kinetic = value.Kinetic * (1 + penalty) },
                AbyssalWeather.Firestorm => value with { Thermal = value.Thermal * (1 + penalty) },
                AbyssalWeather.Gamma => value with { Explosive = value.Explosive * (1 + penalty) },
                _ => value,
            };
    }

    public static double SignatureMultiplier(DroneContact contact) =>
        contact.Abyssal?.AppliedFields?.HasFlag(AbyssalFieldMask.Bioluminescence) == true ? 4 : 1;
}
