using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Thermal;

public sealed record HeatRackProfile(ModuleRack Rack, double Capacity, double Dissipation, double Generation, double Attenuation);

public sealed record HeatRackState(double Heat, long UpdatedAt);

public static class HeatRules
{
    private sealed record RackAttributes(DogmaAttributeId Capacity, DogmaAttributeId Dissipation, DogmaAttributeId Attenuation);

    // Build 3662168 dogma/attributes/heatAttribute.pyj.CalculateHeat changes
    // branches at this incoming rate, independently of the current temperature.
    public const double MinimumIncomingHeat = 5e-8;
    private const double MillisecondsPerSecond = 1000;
    private const double CoolingCutoff = .5;

    public static HeatRackProfile Define(ModuleRack rack, IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        RackAttributes binding = rack switch
        {
            ModuleRack.High => new(
                DogmaAttributeId.HeatCapacityHigh,
                DogmaAttributeId.HeatDissipationHigh,
                DogmaAttributeId.HeatAttenuationHigh
            ),
            ModuleRack.Medium => new(
                DogmaAttributeId.HeatCapacityMedium,
                DogmaAttributeId.HeatDissipationMedium,
                DogmaAttributeId.HeatAttenuationMedium
            ),
            ModuleRack.Low => new(
                DogmaAttributeId.HeatCapacityLow,
                DogmaAttributeId.HeatDissipationLow,
                DogmaAttributeId.HeatAttenuationLow
            ),
            _ => throw new InvalidDataException("Ship rigs do not form an overload rack."),
        };
        HeatRackProfile profile = new(
            rack,
            attributes.GetValueOrDefault(binding.Capacity),
            attributes.GetValueOrDefault(binding.Dissipation),
            attributes.GetValueOrDefault(DogmaAttributeId.HeatGenerationMultiplier, 1),
            attributes.GetValueOrDefault(binding.Attenuation, 1)
        );
        Validate(profile);
        return profile;
    }

    private static void Validate(HeatRackProfile profile)
    {
        if (
            profile.Rack is not (ModuleRack.Low or ModuleRack.Medium or ModuleRack.High)
            || !double.IsFinite(profile.Capacity)
            || profile.Capacity <= 0
            || !double.IsFinite(profile.Dissipation)
            || profile.Dissipation < 0
            || !double.IsFinite(profile.Generation)
            || profile.Generation < 0
            || !double.IsFinite(profile.Attenuation)
            || profile.Attenuation is < 0 or > 1
        )
        {
            throw new InvalidDataException("Invalid ship heat rack profile.");
        }
    }

    public static double Calculate(HeatRackProfile profile, double heat, double incomingHeat, double elapsedMilliseconds)
    {
        Validate(profile);
        if (new[] { heat, incomingHeat, elapsedMilliseconds }.Any(value => !double.IsFinite(value) || value < 0))
        {
            throw new InvalidDataException("Invalid heat calculation inputs.");
        }
        double seconds = elapsedMilliseconds / MillisecondsPerSecond;
        if (incomingHeat < MinimumIncomingHeat)
        {
            double cooled = heat * Math.Exp(-seconds * profile.Dissipation);
            // Native Python 2 round(x, 0) rounds an exact positive .5 upward.
            // Only values strictly below .5 reset to zero; do not round the rack.
            return cooled < CoolingCutoff ? 0 : cooled;
        }
        double retention = Math.Exp(-seconds * incomingHeat * profile.Generation);
        double result = profile.Capacity - profile.Capacity * retention + heat * retention;
        if (!double.IsFinite(result))
        {
            throw new InvalidDataException("Heat calculation overflowed.");
        }
        return result;
    }

    public static HeatRackState Advance(HeatRackProfile profile, HeatRackState state, double incomingHeat, long now)
    {
        if (state.UpdatedAt < 0 || now < 0)
        {
            throw new InvalidDataException("Invalid heat timestamp.");
        }
        double elapsed = Math.Max(0, now - state.UpdatedAt) / (double)TimeSpan.TicksPerMillisecond;
        double value = Calculate(profile, state.Heat, incomingHeat, elapsed);
        return new(value, Math.Max(now, state.UpdatedAt));
    }
}
