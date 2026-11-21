using System.Collections.Immutable;
using Eden.Gameplay.FactionWarfare;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.FactionWarfare;

internal sealed class FactionWarzoneUpdates(IFactionWarzones warzones)
{
    private long _revision = -1;
    private ImmutableArray<WarzoneSystem> _previous;

    internal void Advance(PilotCallContext context)
    {
        long revision = warzones.Revision;
        if (revision < 0 || revision == _revision)
        {
            return;
        }
        var current = warzones.Systems();
        if (!_previous.IsDefault)
        {
            var prior = _previous.ToDictionary(system => system.SystemId);
            foreach (var state in current)
            {
                if (!prior.TryGetValue(state.SystemId, out var before) || before == state)
                {
                    continue;
                }
                if (before.Occupier != state.Occupier || before.Adjacency != state.Adjacency || before.CapturedAt != state.CapturedAt)
                {
                    context.Notifications.Add(new WarzoneOccupationUpdated(state, before));
                }
                if (before.VictoryPoints != state.VictoryPoints || before.PendingTransferAt != state.PendingTransferAt)
                {
                    context.Notifications.Add(new WarzoneVictoryPointsUpdated(state));
                }
                if (state.SystemId == context.Pilot.SystemId && before.HubPhase != state.HubPhase)
                {
                    bool vulnerable = state.HubPhase == MilitiaHubPhase.Vulnerable;
                    if (vulnerable || before.HubPhase == MilitiaHubPhase.Vulnerable && state.HubPhase == MilitiaHubPhase.Contested)
                    {
                        var attacker =
                            state.Occupier == state.Warzone.FirstFaction ? state.Warzone.SecondFaction : state.Warzone.FirstFaction;
                        context.Notifications.Add(new MilitiaBunkerVulnerability(attacker, vulnerable));
                    }
                }
            }
        }
        _previous = current;
        _revision = revision;
    }
}

internal sealed record WarzoneOccupationUpdated(WarzoneSystem Current, WarzoneSystem Previous)
    : PilotNotification("OnWarzoneOccupationStateUpdated")
{
    public override object?[] Arguments => [Current.SystemId, new WarzoneOccupation(Current), new WarzoneOccupation(Previous)];
}

internal sealed record WarzoneVictoryPointsUpdated(WarzoneSystem Current) : PilotNotification("OnVictoryPointStateUpdated")
{
    public override object?[] Arguments => [Current.SystemId, new WarzoneVictoryPoints(Current)];
}

internal sealed record MilitiaBunkerVulnerability(MilitiaFaction Attacker, bool Vulnerable)
    : PilotNotification(Vulnerable ? "OnFacWarBunkerVulnerable" : "OnFacWarBunkerNonVulnerable")
{
    public override object?[] Arguments => [(int)Attacker];
}
