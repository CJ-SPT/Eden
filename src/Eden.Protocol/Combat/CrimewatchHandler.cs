using Eden.Gameplay.Combat;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Combat;

internal enum CrimewatchTimerState
{
    WeaponsIdle = 100,
    WeaponsActive = 101,
    WeaponsCounting = 102,
    PvpIdle = 200,
    PvpActive = 201,
    PvpCounting = 202,
    CriminalIdle = 300,
    CriminalActive = 301,
    SuspectActive = 302,
    CriminalCounting = 303,
    SuspectCounting = 304,
    NpcIdle = 400,
    NpcActive = 401,
    NpcCounting = 402,
    DisapprovalIdle = 500,
}

internal sealed record CrimewatchTimerReply(CrimewatchTimerState State, long? ExpiresAt) : BlueModel
{
    public override object ToBlue() => new object?[] { (int)State, ExpiresAt };
}

internal sealed record CrimewatchEngagement(long PilotId, long Timeout);

internal sealed record CrimewatchClientView(
    ShipSafetyLevel Safety,
    CrimewatchTimerReply Weapons,
    CrimewatchTimerReply Pvp,
    CrimewatchTimerReply Npc,
    CrimewatchTimerReply Criminal,
    IReadOnlyList<CrimewatchEngagement> Engagements,
    IReadOnlyList<long> Criminals,
    IReadOnlyList<long> Suspects
) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            new object?[] { Weapons, Pvp, Npc, Criminal, new CrimewatchTimerReply(CrimewatchTimerState.DisapprovalIdle, null) },
            new BlueMap<long, long>(Engagements.ToDictionary(e => e.PilotId, e => e.Timeout)),
            new object?[] { new BlueList<long>(Criminals), new BlueList<long>(Suspects) },
            (int)Safety,
        };
}

internal sealed record CombatTimerChanged(string Event, CrimewatchTimerReply Timer) : PilotNotification(Event)
{
    public override object?[] Arguments => [(int)Timer.State, Timer.ExpiresAt];
}

internal sealed record CrimewatchFlagsChanged(IReadOnlyList<long> Idle, IReadOnlyList<long> Suspects, IReadOnlyList<long> Criminals)
    : PilotNotification("OnSystemCriminalFlagUpdates")
{
    public override object?[] Arguments => [new BlueList<long>(Idle), new BlueList<long>(Suspects), new BlueList<long>(Criminals)];
}

internal sealed record EngagementChanged(string Event, long OtherPilotId, long? Timeout = null) : PilotNotification(Event)
{
    public override object?[] Arguments => Timeout is long timeout ? [OtherPilotId, timeout] : [OtherPilotId];
}

[Injectable(InjectionType.Scoped)]
internal sealed class CrimewatchHandler(IPilotCrimewatch crimewatch, IPilotSafety safety, TimeProvider clock) : ISelectedPilotCallHandler
{
    // crimewatch.const.crimewatchEngagementTimeoutOngoing in build 3662168.
    private const long OngoingEngagement = -1;
    private CrimewatchClientView? _last;
    private int _system;

    public bool IsMonitoring =>
        _last is { } state
        && (
            state.Weapons.State != CrimewatchTimerState.WeaponsIdle
            || state.Pvp.State != CrimewatchTimerState.PvpIdle
            || state.Npc.State != CrimewatchTimerState.NpcIdle
            || state.Criminal.State != CrimewatchTimerState.CriminalIdle
            || state.Engagements.Count > 0
            || state.Criminals.Count > 0
            || state.Suspects.Count > 0
        );

    private static CrimewatchTimerReply Timer(
        CombatTimer timer,
        long now,
        CrimewatchTimerState idle,
        CrimewatchTimerState active,
        CrimewatchTimerState counting
    ) =>
        !timer.Running(now) ? new(idle, null)
        : timer.Active(now) ? new(active, null)
        : new(counting, timer.ExpiresAt);

    private CrimewatchClientView View(PilotCallContext context)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var state = crimewatch.Read(context.Pilot.PilotId, context.Pilot.SystemId);
        var timers = state.Timers;
        var flag = timers.Flag(now);
        var criminal =
            flag == CrimewatchFlag.Criminal
                ? Timer(
                    timers.Criminal,
                    now,
                    CrimewatchTimerState.CriminalIdle,
                    CrimewatchTimerState.CriminalActive,
                    CrimewatchTimerState.CriminalCounting
                )
                : Timer(
                    timers.Suspect,
                    now,
                    CrimewatchTimerState.CriminalIdle,
                    CrimewatchTimerState.SuspectActive,
                    CrimewatchTimerState.SuspectCounting
                );
        return new(
            state.Safety,
            Timer(
                timers.Weapons,
                now,
                CrimewatchTimerState.WeaponsIdle,
                CrimewatchTimerState.WeaponsActive,
                CrimewatchTimerState.WeaponsCounting
            ),
            Timer(timers.Pvp, now, CrimewatchTimerState.PvpIdle, CrimewatchTimerState.PvpActive, CrimewatchTimerState.PvpCounting),
            Timer(timers.Npc, now, CrimewatchTimerState.NpcIdle, CrimewatchTimerState.NpcActive, CrimewatchTimerState.NpcCounting),
            criminal,
            state
                .Engagements.Where(e => e.ExpiresAt > now)
                .OrderBy(e => e.OtherPilotId)
                .Select(e => new CrimewatchEngagement(e.OtherPilotId, e.ActiveUntil > now ? OngoingEngagement : e.ExpiresAt))
                .ToArray(),
            state.Flags.Where(p => p.Timers.Flag(now) == CrimewatchFlag.Criminal).Select(p => p.PilotId).Order().ToArray(),
            state.Flags.Where(p => p.Timers.Flag(now) == CrimewatchFlag.Suspect).Select(p => p.PilotId).Order().ToArray()
        );
    }

    public void Enter(PilotCallContext context)
    {
        if (context.Selected)
        {
            _last = View(context);
            _system = context.Pilot.SystemId;
        }
    }

    public void Reset() => _last = null;

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            Reset();
            return;
        }
        var view = View(context);
        if (_last is not { } previous || _system != context.Pilot.SystemId)
        {
            _last = view;
            _system = context.Pilot.SystemId;
            return;
        }
        NotifyTimer("OnWeaponsTimerUpdate", view.Weapons, previous.Weapons);
        NotifyTimer("OnPvpTimerUpdate", view.Pvp, previous.Pvp);
        NotifyTimer("OnNpcTimerUpdate", view.Npc, previous.Npc);
        NotifyTimer("OnCriminalTimerUpdate", view.Criminal, previous.Criminal);
        if (!view.Criminals.SequenceEqual(previous.Criminals) || !view.Suspects.SequenceEqual(previous.Suspects))
        {
            var idle = previous.Criminals.Concat(previous.Suspects).Except(view.Criminals.Concat(view.Suspects)).Order().ToArray();
            context.Notifications.Add(new CrimewatchFlagsChanged(idle, view.Suspects, view.Criminals));
        }
        foreach (var old in previous.Engagements.Where(e => view.Engagements.All(n => n.PilotId != e.PilotId)))
        {
            context.Notifications.Add(new EngagementChanged("OnCrimewatchEngagementEnded", old.PilotId));
        }
        foreach (var current in view.Engagements)
        {
            var old = previous.Engagements.SingleOrDefault(e => e.PilotId == current.PilotId);
            if (old is null)
            {
                context.Notifications.Add(new EngagementChanged("OnCrimewatchEngagementCreated", current.PilotId, current.Timeout));
            }
            else if (old.Timeout != current.Timeout)
            {
                string name =
                    current.Timeout == OngoingEngagement ? "OnCrimewatchEngagementStopTimeout" : "OnCrimewatchEngagementStartTimeout";
                context.Notifications.Add(
                    new EngagementChanged(name, current.PilotId, current.Timeout == OngoingEngagement ? null : current.Timeout)
                );
            }
        }
        _last = view;

        void NotifyTimer(string name, CrimewatchTimerReply current, CrimewatchTimerReply old)
        {
            if (current != old)
            {
                context.Notifications.Add(new CombatTimerChanged(name, current));
            }
        }
    }

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "crimewatch")
        {
            return PilotCallResult.Unsupported;
        }
        if (method == "GetClientStates" && args.Length == 0 && keywords is null or { Count: 0 })
        {
            _last = View(context);
            _system = context.Pilot.SystemId;
            return _last;
        }
        if (
            method != "SetSafetyLevel"
            || args is not [long level]
            || keywords is { Count: > 0 }
            || level is < (long)ShipSafetyLevel.Disabled or > (long)ShipSafetyLevel.Full
        )
        {
            return method == "SetSafetyLevel"
                ? new PilotCallResult.Failure("Choose a valid ship safety level.")
                : PilotCallResult.Unsupported;
        }
        try
        {
            safety.Set(context.Pilot.PilotId, (ShipSafetyLevel)level);
            return PilotCallResult.None;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
