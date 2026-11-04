using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;

namespace Eden.Gameplay.Characters;

public enum InfomorphSkill
{
    Psychology = 24242,
    Synchronizing = 33399,
    AdvancedPsychology = 33407,
    ElitePsychology = 73910,
}

public sealed record JumpCloneSkills(int Psychology, int AdvancedPsychology, int ElitePsychology, int Synchronizing)
{
    public int Capacity => Psychology + AdvancedPsychology + ElitePsychology;
    public int CooldownHours => JumpCloneRules.BaseCooldownHours - Synchronizing;
}

public sealed record JumpClone(long Id, int StationId, string Name);

public sealed record JumpCloneSnapshot(IReadOnlyList<JumpClone> Clones, long? LastJump)
{
    public IReadOnlyList<InstalledImplant> Implants { get; init; } = [];
}

public sealed record JumpCloneChange(long BalanceCents, int StationId);

public sealed record JumpCloneTransfer(DockedPilot Pilot, IReadOnlyList<InventoryChange> Changes, long BalanceCents, int SourceStationId);

public enum JumpClonePaymentKind
{
    Installation,
    Activation,
}

public sealed record JumpClonePayment(
    long Id,
    long PilotId,
    int StationId,
    long OperatorId,
    JumpClonePaymentKind Kind,
    long AmountCents,
    long BalanceCents,
    long Time
);

public interface IPilotJumpCloneSkills
{
    JumpCloneSkills ReadCloneSkills(long pilotId);
}

public interface IPilotJumpClones : IPilotJumpCloneSkills
{
    JumpCloneSnapshot Read(long pilotId);
    int? InstallLimit(long pilotId);
    long Price(long pilotId);
    JumpCloneChange Install(long pilotId);
    void Rename(long pilotId, long cloneId, string name);
    int Destroy(long pilotId, long cloneId);
    JumpCloneTransfer Jump(long pilotId, long expectedShipId, int stationId, long cloneId, long quotedCents);
}

public static class JumpCloneRules
{
    // Build 3662168 appConst.costJumpClone; jumpClonesPanel's rename limit.
    public const long FeeCents = 900_000 * MarketDefinitions.CentsPerIsk;
    public const int MaximumNameLength = 100;
    public const int BaseCooldownHours = 24;
    public const long FirstJournalId = 11_000_000_000_000;
    public const int TheraSystem = 31000005;

    public static long Pay(long balance, long quotedCents)
    {
        if (quotedCents != FeeCents)
        {
            throw new InvalidDataException("The jump clone fee changed. Request a new quote.");
        }
        if (balance < FeeCents)
        {
            throw new InvalidDataException("You need 900,000 ISK to use this jump clone facility.");
        }
        return balance - FeeCents;
    }

    public static string Name(string name)
    {
        string value = name.Trim();
        if (value.Length is 0 or > MaximumNameLength || value.Any(char.IsControl))
        {
            throw new InvalidDataException("Choose a jump clone name of 1 to 100 characters without control characters.");
        }
        return value;
    }

    public static void CheckLocation(int systemId, int regionId)
    {
        if (
            regionId == HomeStationRules.PochvenRegion
            || (systemId is >= HomeStationRules.FirstWormholeSystem and <= HomeStationRules.LastWormholeSystem && systemId != TheraSystem)
        )
        {
            throw new InvalidDataException("Remote clone jumps are unavailable in this system.");
        }
    }

    public static void CheckTimer(bool sameStation, long? lastJump, JumpCloneSkills skills, long now)
    {
        // CCP's 20.07 change exempts same-station swaps without clearing or
        // restarting the existing cross-station cooldown.
        if (!sameStation && lastJump is long last && now < checked(last + TimeSpan.FromHours(skills.CooldownHours).Ticks))
        {
            throw new InvalidDataException("You must wait for your jump clone cooldown before changing stations.");
        }
    }
}
