namespace Eden.Gameplay.Mining;

public interface IResourceRenewal
{
    int Renew(int limit = ResourceRenewalRules.BatchSize);
}

public static class ResourceRenewalRules
{
    public const int BatchSize = 32;

    public static long Next(long now, int hourUtc)
    {
        if (hourUtc is < 0 or > 23)
        {
            throw new InvalidDataException("Belt renewal hour must be between zero and 23 UTC.");
        }
        DateTime instant = DateTime.FromFileTimeUtc(now);
        DateTime next = instant.Date.AddHours(hourUtc);
        return (next > instant ? next : next.AddDays(1)).ToFileTimeUtc();
    }
}
