using Eden.Gameplay.Docking;

namespace Eden.Gameplay.Combat;

public enum CrimewatchFlag
{
    None,
    Suspect,
    Criminal,
}

public enum CrimewatchTravel
{
    Dock,
    Jump,
    Warp,
    ChangeShip,
    SafeLogoff,
}

public sealed record CombatTimer(long ActiveUntil = 0, long ExpiresAt = 0)
{
    public bool Running(long now) => ExpiresAt > now;

    public bool Active(long now) => ActiveUntil > now;
}

public sealed record CrimewatchTimers(CombatTimer Weapons, CombatTimer Pvp, CombatTimer Npc, CombatTimer Suspect, CombatTimer Criminal)
{
    public static CrimewatchTimers Empty { get; } = new(new(), new(), new(), new(), new());

    public CrimewatchFlag Flag(long now) =>
        Criminal.Running(now) ? CrimewatchFlag.Criminal
        : Suspect.Running(now) ? CrimewatchFlag.Suspect
        : CrimewatchFlag.None;
}

public sealed record LimitedEngagement(long OtherPilotId, long ActiveUntil, long ExpiresAt);

public sealed record FlaggedPilot(long PilotId, CrimewatchTimers Timers);

public sealed record CrimewatchSnapshot(
    ShipSafetyLevel Safety,
    CrimewatchTimers Timers,
    IReadOnlyList<LimitedEngagement> Engagements,
    IReadOnlyList<FlaggedPilot> Flags
);

public sealed record LoggedOffPilot(DockedPilot Pilot, long ExpiresAt);

public sealed record AggressionInputs(
    double Security,
    ShipSafetyLevel Safety,
    bool Alpha,
    bool TargetCapsule,
    CrimewatchFlag TargetFlag,
    bool TargetOutlaw,
    bool Engagement
);

public sealed record AggressionDecision(CrimewatchFlag Flag, bool Engagement);

public interface IPilotCrimewatch
{
    CrimewatchSnapshot Read(long pilotId, int systemId);
    void CheckTravel(long pilotId, CrimewatchTravel action, int? destinationSystemId = null);
    void Connect(long pilotId);
    void Disconnect(long pilotId, long shipId);
    IReadOnlyList<LoggedOffPilot> LoggedOff(int systemId);
}

public static class CrimewatchRules
{
    // Pinned crimewatch.const and eveuniverse.security.SecurityClassFromLevel.
    public const double HighSecurity = 0.45;
    public const double OutlawSecurity = -5;
    public static readonly TimeSpan WeaponsDuration = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PvpDuration = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan NpcDuration = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan FlagDuration = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan EngagementDuration = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan NormalLogoffDuration = TimeSpan.FromSeconds(60);

    public static AggressionDecision Decide(AggressionInputs input)
    {
        if (!double.IsFinite(input.Security) || !Enum.IsDefined(input.Safety) || !Enum.IsDefined(input.TargetFlag))
        {
            throw new InvalidDataException("Invalid aggression inputs.");
        }
        if (input.Security <= 0)
        {
            return new(CrimewatchFlag.None, input.Engagement);
        }
        bool free = input.TargetFlag != CrimewatchFlag.None || input.TargetOutlaw;
        if (free || input.Engagement)
        {
            return new(CrimewatchFlag.None, true);
        }
        var flag = input.Security >= HighSecurity || input.TargetCapsule ? CrimewatchFlag.Criminal : CrimewatchFlag.Suspect;
        var safety = input.Alpha && input.Security >= HighSecurity ? ShipSafetyLevel.Full : input.Safety;
        if (safety == ShipSafetyLevel.Full || safety == ShipSafetyLevel.Partial && flag == CrimewatchFlag.Criminal)
        {
            throw new InvalidDataException("Your ship safety setting prevents this hostile action.");
        }
        // High-sec criminal aggression needs CONCORD enforcement, which belongs
        // to the next combat milestone. Do not silently permit an unpoliced attack.
        if (input.Security >= HighSecurity)
        {
            throw new InvalidDataException("Criminal high-security aggression is not supported until security enforcement is available.");
        }
        return new(flag, false);
    }

    public static CombatTimer Extend(CombatTimer timer, long end, TimeSpan duration) =>
        new(Math.Max(timer.ActiveUntil, end), Math.Max(timer.ExpiresAt, checked(end + duration.Ticks)));

    public static CrimewatchTimers Player(CrimewatchTimers timers, long end, CrimewatchFlag flag, bool attacker, long now)
    {
        var result = timers with { Pvp = Extend(timers.Pvp, end, PvpDuration) };
        if (attacker)
        {
            result = result with { Weapons = Extend(timers.Weapons, end, WeaponsDuration) };
        }
        if (flag == CrimewatchFlag.Criminal || flag == CrimewatchFlag.Suspect && timers.Criminal.Running(now))
        {
            result = result with { Criminal = Extend(timers.Criminal, end, FlagDuration) };
        }
        else if (flag == CrimewatchFlag.Suspect)
        {
            result = result with { Suspect = Extend(timers.Suspect, end, FlagDuration) };
        }
        return result;
    }

    public static void CheckTravel(CrimewatchTimers timers, CrimewatchTravel action, double security, long now)
    {
        if (timers.Weapons.Running(now) && action != CrimewatchTravel.Warp)
        {
            throw new InvalidDataException("The weapons timer prevents docking, jumping, changing ships and safe logoff.");
        }
        if (timers.Criminal.Running(now) && security >= HighSecurity)
        {
            throw new InvalidDataException("The criminal timer prevents this travel in high-security space.");
        }
        if (action == CrimewatchTravel.SafeLogoff && (timers.Pvp.Running(now) || timers.Npc.Running(now)))
        {
            throw new InvalidDataException("Combat aggression prevents safe logoff.");
        }
    }

    public static long LogoffDeadline(CrimewatchTimers timers, long now) =>
        checked(
            now
            + (
                timers.Pvp.Running(now) ? PvpDuration
                : timers.Npc.Running(now) ? NpcDuration
                : NormalLogoffDuration
            ).Ticks
        );
}
