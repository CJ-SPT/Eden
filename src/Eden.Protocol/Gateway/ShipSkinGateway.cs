using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using Eden.Gameplay.Docking;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Gateway.ShipSkins;
using Eden.Protocol.Gateway.ShipSkins.Api;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Eden.Protocol.Gateway;

public interface IShipSkinVisibility
{
    IReadOnlySet<long> VisibleShipIds(ShipSkinViewer viewer);
}

public sealed class ShipSkinNoticeCursor
{
    internal (long Account, long Pilot) Viewer { get; set; }
    internal Dictionary<long, AppliedShipSkin> States { get; set; } = [];
}

[Injectable(InjectionType.Singleton)]
public sealed class ShipSkinGateway(IPilotShipSkins skins, IShipSkinVisibility visibility, TimeProvider clock)
{
    private const string Api = "eve_public.cosmetic.ship.api.";

    private sealed record WatchedShip(long Id, bool Owned, AppliedShipSkin? Baseline);

    private sealed record Observer(long PilotId, long? FittingShipId, ImmutableDictionary<long, WatchedShip> Ships);

    private readonly ConcurrentDictionary<long, Observer> _observations = new();

    internal GatewayReply? Read(string type, ByteString payload, AuthoritativeContext? context, long? authenticatedAccount)
    {
        if (type is not (Api + "GetRequest" or Api + "GetAllInBubbleRequest"))
        {
            return null;
        }

        long pilotId = context?.Identity?.Character?.Sequential ?? 0;
        if (authenticatedAccount is not long account || account <= 0 || pilotId <= 0 || skins.Viewer(account, pilotId) is not { } viewer)
        {
            return new(HttpStatusCode.Unauthorized);
        }

        try
        {
            if (type == Api + "GetRequest")
            {
                ulong id = GetRequest.Parser.ParseFrom(payload).Ship?.Sequential ?? 0;
                if (id is 0 or > long.MaxValue)
                {
                    return new(HttpStatusCode.BadRequest);
                }

                long shipId = (long)id;
                var owned = skins.ReadOwned(pilotId, shipId);
                var visible = visibility.VisibleShipIds(viewer);
                var state = owned ?? (visible.Contains(shipId) ? skins.ReadVisible([shipId]).SingleOrDefault() : null);
                if (state is null)
                {
                    return new(HttpStatusCode.NotFound);
                }

                Remember(account, pilotId, viewer.ShipId, owned?.ShipId, [new(shipId, owned is not null, state)], visible);
                return new(HttpStatusCode.OK, new GetResponse { State = Wire(state) });
            }

            _ = GetAllInBubbleRequest.Parser.ParseFrom(payload);
            var inBubble = visibility.VisibleShipIds(viewer);
            IReadOnlyList<AppliedShipSkin> states = inBubble.Count == 0 ? [] : skins.ReadVisible(inBubble);
            Remember(
                account,
                pilotId,
                viewer.ShipId,
                fittingShipId: null,
                states.Select(state => new WatchedShip(state.ShipId, state.PilotId == pilotId, state)),
                inBubble
            );
            var response = new GetAllInBubbleResponse();
            response.States.Add(states.OrderBy(state => state.ShipId).Select(Wire));
            return new(HttpStatusCode.OK, response);
        }
        catch (InvalidProtocolBufferException)
        {
            return new(HttpStatusCode.BadRequest);
        }
    }

    internal void Applied(long accountId, long pilotId, long activeShipId, long shipId) =>
        Remember(accountId, pilotId, activeShipId, shipId, [new(shipId, true, null)], visible: null);

    internal IReadOnlyList<Notice> Notices(long accountId, ShipSkinNoticeCursor cursor)
    {
        if (
            !_observations.TryGetValue(accountId, out var observer)
            || observer.Ships.Count == 0
            || skins.Viewer(accountId, observer.PilotId) is not { } viewer
        )
        {
            cursor.Viewer = default;
            cursor.States.Clear();
            return [];
        }
        if (cursor.Viewer != (accountId, observer.PilotId))
        {
            cursor.Viewer = (accountId, observer.PilotId);
            cursor.States.Clear();
        }

        var visible = visibility.VisibleShipIds(viewer);
        var visibleIds = visible.Where(observer.Ships.ContainsKey).ToArray();
        var current =
            visibleIds.Length == 0 ? new Dictionary<long, AppliedShipSkin>() : skins.ReadVisible(visibleIds).ToDictionary(s => s.ShipId);
        ReadOwned(viewer.ShipId);
        if (observer.FittingShipId is long fitting && fitting != viewer.ShipId)
        {
            ReadOwned(fitting);
        }

        void ReadOwned(long shipId)
        {
            if (
                observer.Ships.GetValueOrDefault(shipId) is { Owned: true }
                && !current.ContainsKey(shipId)
                && skins.ReadOwned(observer.PilotId, shipId) is { } owned
            )
            {
                current.Add(shipId, owned);
            }
        }

        foreach (var id in cursor.States.Keys.Where(id => !observer.Ships.ContainsKey(id)).ToArray())
        {
            cursor.States.Remove(id);
        }

        List<Notice> changes = [];
        foreach (var state in current.Values.OrderBy(state => state.ShipId))
        {
            var before = cursor.States.GetValueOrDefault(state.ShipId) ?? observer.Ships[state.ShipId].Baseline;
            if (before is null || before.PilotId != state.PilotId || before.SkinId != state.SkinId)
            {
                changes.Add(
                    new()
                    {
                        Dispatched = Timestamp.FromDateTimeOffset(clock.GetUtcNow()),
                        Uuid = ByteString.CopyFrom(Guid.NewGuid().ToByteArray(bigEndian: true)),
                        InternalOrigin = "eden",
                        TargetGroup = new() { Character = checked((uint)viewer.PilotId) },
                        Payload = Any.Pack(new SetNotice { State = Wire(state) }),
                    }
                );
            }
            // Remember the delivered state while a watched ship is away from this grid,
            // so a brief departure does not replay the same appearance change.
            cursor.States[state.ShipId] = state;
        }
        return changes;
    }

    private void Remember(
        long accountId,
        long pilotId,
        long activeShipId,
        long? fittingShipId,
        IEnumerable<WatchedShip> observations,
        IReadOnlySet<long>? visible
    )
    {
        var requested = observations.ToArray();
        var empty = new Observer(pilotId, null, ImmutableDictionary<long, WatchedShip>.Empty);
        _observations.AddOrUpdate(accountId, _ => Merge(empty), (_, before) => Merge(before.PilotId == pilotId ? before : empty));

        Observer Merge(Observer before)
        {
            long? fitting = fittingShipId ?? before.FittingShipId;
            var next = before.Ships.ToBuilder();
            foreach (var (id, watch) in before.Ships)
            {
                if (watch.Owned && id != activeShipId && id != fitting || !watch.Owned && visible is not null && !visible.Contains(id))
                {
                    next.Remove(id);
                }
            }

            foreach (var watch in requested)
            {
                // Keep the last solicited result as a new notice stream's baseline. A ship first
                // seen on Apply has no baseline, so even a docked pilot receives the new state.
                next[watch.Id] =
                    watch.Baseline is null && next.TryGetValue(watch.Id, out var previous) ? previous with { Owned = true } : watch;
            }
            return new(pilotId, fitting, next.ToImmutable());
        }
    }

    private static State Wire(AppliedShipSkin state) =>
        new()
        {
            Character = new() { Sequential = checked((uint)state.PilotId) },
            Ship = new() { Sequential = checked((ulong)state.ShipId) },
            Skin = state.SkinId is int skinId
                ? new() { Firstparty = new() { Identifier = new() { Sequential = checked((ulong)skinId) } } }
                : new() { NoSkin = true },
        };
}
