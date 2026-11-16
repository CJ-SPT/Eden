namespace Eden.Gameplay.Agents;

public sealed record MissionItemReward(int TypeId, int Quantity);

public static class MissionItemRewardRules
{
    public static MissionItemReward Quote(IReadOnlyList<int> types, int quantity, int selection)
    {
        if (
            types.Count == 0
            || types.Any(type => type <= 0)
            || types.Distinct().Count() != types.Count
            || quantity <= 0
            || selection < 0
            || selection >= types.Count
        )
        {
            throw new InvalidDataException("Invalid mission item reward alternatives, quantity or selection.");
        }
        return new(types[selection], quantity);
    }
}
