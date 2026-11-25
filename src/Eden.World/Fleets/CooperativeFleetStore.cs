using System.Security.Cryptography;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;

namespace Eden.World.Fleets;

[Injectable(InjectionType.Singleton)]
internal sealed class CooperativeFleetStore(TimeProvider clock, IPilotSecurityStatus security, WorldDatabase? database = null)
    : ICooperativeFleets
{
    private readonly object _gate = new();
    private readonly FleetWorld _world = new(BitConverter.ToInt64(RandomNumberGenerator.GetBytes(sizeof(long))) & (long.MaxValue / 2));
    private OwnerDirectorySnapshot? _directory;

    private void RefreshAffiliations()
    {
        var current = database is null ? null : LiveWorld.Find(database.Path)?.OwnerDirectory.Snapshot;
        if (current is null || ReferenceEquals(current, _directory))
        {
            return;
        }
        if (
            _directory is null
            || current.Pilots != _directory.Pilots
            || current.AllianceMemberships != _directory.AllianceMemberships
            || current.DirectMilitias != _directory.DirectMilitias
            || current.OrganizationMilitias != _directory.OrganizationMilitias
        )
        {
            _world.RefreshAffiliations(id =>
                current.Pilots.TryGetValue(id, out var pilot)
                    ? new(
                        pilot.CorporationId,
                        current.AllianceMemberships.GetValueOrDefault(pilot.CorporationId) is > 0 and var alliance ? alliance : null,
                        current.PilotMilitia(id)
                    )
                    : null
            );
        }
        _directory = current;
    }

    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    private T Read<T>(Func<T> action)
    {
        lock (_gate)
        {
            RefreshAffiliations();
            return action();
        }
    }

    private void Write(Action action)
    {
        lock (_gate)
        {
            RefreshAffiliations();
            action();
        }
    }

    public FleetAdvert? MyAdvert(long actor) => Read(() => _world.MyAdvert(actor));

    public FleetAdvert SetAdvert(long actor, FleetAdvertSettings settings) => Read(() => _world.SetAdvert(actor, settings, Now));

    public void RemoveAdvert(long actor) => Write(() => _world.RemoveAdvert(actor));

    public IReadOnlyList<FleetAdvert> AvailableAdverts(long actor, double security) => Read(() => _world.AvailableAdverts(actor, security));

    public bool ApplyToFleet(long actor, long fleet, double security) => Read(() => _world.ApplyToFleet(actor, fleet, security, Now));

    public IReadOnlyList<FleetPilot> JoinRequests(long actor, long fleet) => Read(() => _world.JoinRequests(actor, fleet));

    public void RejectApplication(long actor, long fleet, long target) => Write(() => _world.RejectApplication(actor, fleet, target));

    public void Update(Guid session, FleetPilot? pilot) =>
        Write(() =>
        {
            var directory = database is null ? null : LiveWorld.Find(database.Path)?.OwnerDirectory.Snapshot;
            if (pilot is not null && directory?.Pilots.TryGetValue(pilot.Id, out var current) == true)
            {
                pilot = pilot with
                {
                    CorporationId = current.CorporationId,
                    AllianceId = directory.AllianceMemberships.GetValueOrDefault(current.CorporationId) is > 0 and var id ? id : null,
                    WarFaction = directory.PilotMilitia(pilot.Id),
                };
            }
            _world.Update(session, pilot);
        });

    public FleetIdentity? Identity(long actor) => Read(() => _world.Identity(actor));

    public IReadOnlySet<long> MembersInFleet(long actor, IReadOnlyList<long> candidates) =>
        Read<IReadOnlySet<long>>(() =>
            _world.Identity(actor) is { FleetId: var fleet }
                ? candidates.Where(candidate => _world.Identity(candidate)?.FleetId == fleet).ToHashSet()
                : new HashSet<long>()
        );

    public FleetMembership? JoinMembership(long actor) => Read(() => _world.JoinMembership(actor));

    public IReadOnlyList<FleetPilot> Fleetmates(long actor) => Read(() => _world.Fleetmates(actor));

    public bool CanShareLoot(long actor, long owner, int system) => Read(() => _world.CanShareLoot(actor, owner, system));

    public FleetMembership? Membership(long actor) => Read(() => _world.Membership(actor));

    public IReadOnlyList<FleetDelivery> Drain(long actor) => Read(() => _world.Drain(actor));

    public void NotifyStandingChanged(long pilotId, long sourceId, double value) =>
        Write(() => _world.NotifyStandingChanged(pilotId, sourceId, value));

    public CooperativeFleet Create(long actor) => Read(() => _world.Create(actor));

    public bool CanBind(long actor, long fleet) => Read(() => _world.CanBind(actor, fleet, Now));

    public CooperativeFleet Read(long actor, long fleet) => Read(() => _world.Read(actor, fleet));

    public IReadOnlyList<FleetCompositionMember> Composition(long actor, long fleet)
    {
        if (database is null || LiveWorld.Find(database.Path) is not { } resident)
        {
            throw new InvalidDataException("Fleet composition requires a running world.");
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        return resident.Economy.Inspect(world =>
            Read(() => _world.Composition(actor, fleet))
                .Select(member =>
                {
                    var (pilot, detailed) = member;
                    return new FleetCompositionMember(
                        pilot.Id,
                        pilot.SystemId,
                        detailed ? pilot.DockedLocationId : null,
                        detailed ? pilot.ShipTypeId : null,
                        detailed
                            ? new(
                                PilotSkillReader.Level(world, catalog, pilot.Id, (int)FleetLeadershipSkill.Leadership),
                                PilotSkillReader.Level(world, catalog, pilot.Id, (int)FleetLeadershipSkill.WingCommand),
                                PilotSkillReader.Level(world, catalog, pilot.Id, (int)FleetLeadershipSkill.FleetCommand)
                            )
                            : null
                    );
                })
                .ToArray()
        );
    }

    public void Invite(long actor, long fleet, long target, FleetPlacement placement) =>
        Write(() => _world.Invite(actor, fleet, target, placement, Now));

    public void Accept(long actor, long fleet)
    {
        double status = security.Read(actor).Value;
        Write(() => _world.Accept(actor, fleet, Now, status));
    }

    public void Reject(long actor, long fleet) => Write(() => _world.Reject(actor, fleet));

    public void Leave(long actor, long fleet) => Write(() => _world.Leave(actor, fleet));

    public void Kick(long actor, long fleet, long target) => Write(() => _world.Kick(actor, fleet, target));

    public void Disband(long actor, long fleet) => Write(() => _world.Disband(actor, fleet));

    public void Move(long actor, long fleet, long target, FleetPlacement placement) =>
        Write(() => _world.Move(actor, fleet, target, placement));

    public IReadOnlyList<long> MoveMany(long actor, long fleet, IReadOnlyList<long> targets, FleetPlacement placement) =>
        Read(() => _world.MoveMany(actor, fleet, targets, placement));

    public void MakeBoss(long actor, long fleet, long target) => Write(() => _world.MakeBoss(actor, fleet, target));

    public long CreateWing(long actor, long fleet) => Read(() => _world.CreateWing(actor, fleet));

    public long CreateSquad(long actor, long fleet, long wing) => Read(() => _world.CreateSquad(actor, fleet, wing));

    public void ChangeGroup(long actor, long fleet, long group, bool squad, string? name) =>
        Write(() => _world.ChangeGroup(actor, fleet, group, squad, name));

    public void SetFreeMove(long actor, long fleet, bool enabled) => Write(() => _world.SetFreeMove(actor, fleet, enabled));

    public int SetMaximumSize(long actor, long fleet, int size) => Read(() => _world.SetMaximumSize(actor, fleet, size));

    public void SetAutoJoinSquad(long actor, long fleet, long? squad) => Write(() => _world.SetAutoJoinSquad(actor, fleet, squad));

    public void SetMotd(long actor, long fleet, string text) => Write(() => _world.SetMotd(actor, fleet, text));

    public void SetTakesWarp(long actor, long fleet, bool enabled) => Write(() => _world.SetTakesWarp(actor, fleet, enabled));

    public void SetAcceptsConduitJumps(long actor, long fleet, bool enabled) =>
        Write(() => _world.SetAcceptsConduitJumps(actor, fleet, enabled));

    public void SetAcceptsRegroup(long actor, long fleet, bool enabled) => Write(() => _world.SetAcceptsRegroup(actor, fleet, enabled));

    public void Broadcast(
        long actor,
        long fleet,
        FleetBroadcastKind kind,
        FleetBroadcastScope scope,
        FleetBroadcastRange range,
        long? item,
        int? type
    ) => Write(() => _world.Broadcast(actor, fleet, kind, scope, range, item, type, Now));

    public IReadOnlyList<FleetTargetTag> TargetTags(long actor) => Read(() => _world.TargetTags(actor));

    public void TagTarget(long actor, long fleet, FleetTagTarget target, string? tag) =>
        Write(() => _world.TagTarget(actor, fleet, target, tag));

    public void Warp(long actor, long fleet, long target, double range, SpacePosition? point) =>
        Write(() => _world.Warp(actor, fleet, target, range, point, Now));

    public void CancelMovementOrders(long actor) => Write(() => _world.CancelMovementOrders(actor));

    public bool CanExecuteWarp(long actor, FleetWarpOrder order) => Read(() => _world.CanExecuteWarp(actor, order, Now));

    public void Regroup(long actor, long fleet) => Write(() => _world.Regroup(actor, fleet, Now));

    public bool CanExecuteRegroup(long actor, FleetRegroupOrder order) => Read(() => _world.CanExecuteRegroup(actor, order, Now));
}
