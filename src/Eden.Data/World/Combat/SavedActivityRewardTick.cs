using System.Collections.Immutable;

namespace Eden.Data.World.Combat;

public sealed record SavedActivityBounty(long FirstNpcId, int FirstNpcTypeId, long AmountCents);

public sealed class SavedActivityRewardTick
{
    public long PilotId { get; init; }
    public long DueAt { get; init; }
    public long SkillPoints { get; set; }
    public ImmutableSortedDictionary<int, SavedActivityBounty> Bounties { get; set; } =
        ImmutableSortedDictionary<int, SavedActivityBounty>.Empty;
}
