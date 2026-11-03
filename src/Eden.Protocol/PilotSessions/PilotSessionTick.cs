using Eden.Gameplay.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Mining;
using Eden.Protocol.Skills;
using static Eden.Protocol.PilotSessions.PilotSessionWire;

namespace Eden.Protocol.PilotSessions;

internal sealed class PilotSessionTick(TimeProvider clock, PilotUpdateCoordinator updates, SkillHandler skills, IAsteroidField asteroids)
{
    public async Task AdvanceAsync(
        PilotCallContext context,
        PilotSessionOutput output,
        Action<string> report,
        long accountId,
        long clientId
    )
    {
        var flight = context.Flight;
        if (context.NextSkillCompletion <= clock.GetUtcNow().UtcDateTime.ToFileTimeUtc())
        {
            skills.Advance(context);
            await output.FlushNotificationsAsync();
        }
        flight.Checkpoint();
        var departure = flight.Pilot;
        if (flight.CompleteJump())
        {
            context.ReleaseLocationReferences();
            await output.WriteAsync(new PilotSessionChange(accountId, clientId, flight.Pilot, departure).ToBlue());
            report($"Ship {flight.Pilot.ShipId} jumped from {departure.SystemId} to {flight.Pilot.SystemId}.");
        }
        if (flight.InBallpark)
        {
            flight.RefreshAsteroids(asteroids.List(flight.Pilot.SystemId));
            flight.RefreshWrecks();
            flight.RefreshNpcs();
            flight.RefreshPeers();
            await output.FlushFlightUpdatesAsync();
        }
        var beforeAttack = updates.Advance(context);
        if (beforeAttack is not null && beforeAttack.ShipId != context.Pilot.ShipId)
        {
            await output.AnnounceTransitionAsync(beforeAttack);
        }
        await output.FlushFlightUpdatesAsync();
        await output.FlushNotificationsAsync();
    }
}
