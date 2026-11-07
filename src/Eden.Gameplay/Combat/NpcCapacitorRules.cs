using Eden.Gameplay.Dogma;
using Eden.Gameplay.Ships;

namespace Eden.Gameplay.Combat;

public sealed record NpcCapacitorProfile(double Capacity, double RechargeMilliseconds);

public sealed record NpcCapacitorState(double Charge, long UpdatedAt);

public static class NpcCapacitorRules
{
    public static NpcCapacitorProfile Profile(IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        double capacity = attributes.GetValueOrDefault(DogmaAttributeId.CapacitorCapacity);
        double recharge = attributes.GetValueOrDefault(DogmaAttributeId.CapacitorRechargeTime);
        NpcCapacitorProfile profile = new(capacity, recharge);
        Validate(profile);
        return profile;
    }

    private static void Validate(NpcCapacitorProfile profile)
    {
        if (
            !double.IsFinite(profile.Capacity)
            || profile.Capacity < 0
            || !double.IsFinite(profile.RechargeMilliseconds)
            || profile.RechargeMilliseconds < 0
            || profile.Capacity > 0 && profile.RechargeMilliseconds == 0
        )
        {
            throw new InvalidDataException("The NPC has no verified capacitor capacity/recharge profile.");
        }
    }

    public static CapacitorPool Read(NpcCapacitorProfile profile, NpcCapacitorState? state, long now)
    {
        Validate(profile);
        if (state is null)
        {
            return new(profile.Capacity, profile.Capacity);
        }
        if (!double.IsFinite(state.Charge) || state.Charge < 0)
        {
            throw new InvalidDataException("Invalid saved NPC capacitor charge.");
        }
        return new(
            ChargedResource.Recharged(
                state.Charge,
                profile.Capacity,
                profile.RechargeMilliseconds,
                Math.Max(0, (now - state.UpdatedAt) / (double)TimeSpan.TicksPerMillisecond)
            ),
            profile.Capacity
        );
    }
}
