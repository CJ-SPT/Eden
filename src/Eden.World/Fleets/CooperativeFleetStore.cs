using System.Security.Cryptography;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Registration;

namespace Eden.World.Fleets;

[Injectable(InjectionType.Singleton)]
internal sealed class CooperativeFleetStore(TimeProvider clock) : ICooperativeFleets
{
    private readonly object _gate = new();
    private readonly FleetWorld _world = new(BitConverter.ToInt64(RandomNumberGenerator.GetBytes(sizeof(long))) & (long.MaxValue / 2));
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    private T Read<T>(Func<T> action)
    {
        lock (_gate)
        {
            return action();
        }
    }

    private void Write(Action action)
    {
        lock (_gate)
        {
            action();
        }
    }

    public void Update(Guid session, FleetPilot? pilot) => Write(() => _world.Update(session, pilot));

    public FleetIdentity? Identity(long actor) => Read(() => _world.Identity(actor));

    public FleetMembership? Membership(long actor) => Read(() => _world.Membership(actor));

    public IReadOnlyList<FleetDelivery> Drain(long actor) => Read(() => _world.Drain(actor));

    public CooperativeFleet Create(long actor) => Read(() => _world.Create(actor));

    public bool CanBind(long actor, long fleet) => Read(() => _world.CanBind(actor, fleet, Now));

    public CooperativeFleet Read(long actor, long fleet) => Read(() => _world.Read(actor, fleet));

    public void Invite(long actor, long fleet, long target, FleetPlacement placement) =>
        Write(() => _world.Invite(actor, fleet, target, placement, Now));

    public void Accept(long actor, long fleet) => Write(() => _world.Accept(actor, fleet, Now));

    public void Reject(long actor, long fleet) => Write(() => _world.Reject(actor, fleet));

    public void Leave(long actor, long fleet) => Write(() => _world.Leave(actor, fleet));

    public void Kick(long actor, long fleet, long target) => Write(() => _world.Kick(actor, fleet, target));

    public void Disband(long actor, long fleet) => Write(() => _world.Disband(actor, fleet));

    public void Move(long actor, long fleet, long target, FleetPlacement placement) =>
        Write(() => _world.Move(actor, fleet, target, placement));

    public void MakeBoss(long actor, long fleet, long target) => Write(() => _world.MakeBoss(actor, fleet, target));

    public long CreateWing(long actor, long fleet) => Read(() => _world.CreateWing(actor, fleet));

    public long CreateSquad(long actor, long fleet, long wing) => Read(() => _world.CreateSquad(actor, fleet, wing));

    public void ChangeGroup(long actor, long fleet, long group, bool squad, string? name) =>
        Write(() => _world.ChangeGroup(actor, fleet, group, squad, name));

    public void SetFreeMove(long actor, long fleet, bool enabled) => Write(() => _world.SetFreeMove(actor, fleet, enabled));

    public void SetMotd(long actor, long fleet, string text) => Write(() => _world.SetMotd(actor, fleet, text));

    public void SetTakesWarp(long actor, long fleet, bool enabled) => Write(() => _world.SetTakesWarp(actor, fleet, enabled));

    public void Broadcast(
        long actor,
        long fleet,
        FleetBroadcastKind kind,
        FleetBroadcastScope scope,
        FleetBroadcastRange range,
        long? item,
        int? type
    ) => Write(() => _world.Broadcast(actor, fleet, kind, scope, range, item, type, Now));

    public void Warp(long actor, long fleet, long target, double range, SpacePosition? point) =>
        Write(() => _world.Warp(actor, fleet, target, range, point, Now));

    public void CancelWarpOrders(long actor) => Write(() => _world.CancelWarpOrders(actor));

    public bool CanExecuteWarp(long actor, FleetWarpOrder order) => Read(() => _world.CanExecuteWarp(actor, order, Now));
}
