namespace Eden.Gameplay.Capsuleers;

public static class CapsuleerRecruitmentRules
{
    public const int MaximumJumps = 7;
    public const int MaximumSearchSystems = 64;
    public const int MinimumOfferScore = 12;
    public const int AdDays = 1;

    public static TimeSpan ReviewDelay(long pilotId) => TimeSpan.FromMinutes(18 + (int)(unchecked((ulong)pilotId) % 23));

    public static TimeSpan OfferDelay(long pilotId) => TimeSpan.FromMinutes(4 + (int)(unchecked((ulong)pilotId) % 7));

    public static int DesiredMembers(CapsuleerProfession profession) =>
        profession switch
        {
            CapsuleerProfession.Mining => 4,
            CapsuleerProfession.Hauling => 5,
            CapsuleerProfession.Combat => 3,
            _ => 0,
        };

    public static int OfferScore(long pilotId, int corporationId, int vacancies, bool services, double tax, int jumps, int unsafeJumps)
    {
        ulong preference = CapsuleerProvisioningRules.Seed(pilotId, corporationId, CapsuleerCareer.Hauling);
        return 15 + vacancies * 3 + (services ? 5 : 0) + (int)(preference % 8) - (int)(tax * 50) - jumps * 2 - unsafeJumps * 3;
    }
}
