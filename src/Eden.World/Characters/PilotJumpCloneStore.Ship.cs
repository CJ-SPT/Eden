using Eden.Data.World.Characters;
using Eden.Data.World.Docking;
using Eden.Data.World.Economy;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Corporations;
using Eden.World.Fittings;
using Eden.World.Flight;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Characters;

internal sealed partial class PilotJumpCloneStore
{
    private const int ShipCloneOfferSeconds = 120;

    private LiveWorld Resident =>
        LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Ship clone service requires the resident world.");

    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public IReadOnlyList<ShipClone> ShipClones(long hostId, long shipId) =>
        Resident.Economy.Inspect(
            world =>
            {
                if (Pilot(world, hostId).ActiveShipId != shipId)
                {
                    throw new InvalidDataException("Open the clone vat on your active ship.");
                }
                return BodiesInShip(world, shipId)
                    .OrderBy(clone => clone.Id)
                    .Select(clone => new ShipClone(clone.Id, clone.PilotId, shipId))
                    .ToArray();
            },
            new(pilots: [hostId], ships: [shipId])
        );

    public ShipCloneOffer Offer(long hostId, long shipId, long pilotId)
    {
        if (hostId == pilotId)
        {
            throw new InvalidDataException("Offer clone installation to another pilot.");
        }
        long now = Now;
        var offer = new ShipCloneOffer(hostId, pilotId, shipId, checked(now + ShipCloneOfferSeconds * TimeSpan.TicksPerSecond));
        using CatalogDbContext catalog = new(database.CatalogPath);
        Resident.Economy.Inspect(
            world => RequireInstallation(world, catalog, offer, Pilot(world, pilotId).ActiveShipId, now),
            new(pilots: [hostId, pilotId], ships: [shipId])
        );
        offers.Add(offer, now);
        return offer;
    }

    public long OfferPrice(long pilotId) =>
        offers.Pending(pilotId, Now) ? JumpCloneRules.FeeCents : throw new InvalidDataException("A ship clone offer is required.");

    public ShipCloneInstallation Accept(long pilotId, long shipId)
    {
        long now = Now;
        return offers.Accept(
            pilotId,
            now,
            offer =>
            {
                using var world = Resident.Economy.Begin(new(pilots: [pilotId, offer.HostId], ships: [shipId, offer.ShipId]));
                using CatalogDbContext catalog = new(database.CatalogPath);
                _ = RequireInstallation(world, catalog, offer, shipId, now);
                long balance = SettleVatFee(world, pilotId, offer.HostId, offer.ShipId, JumpCloneRules.FeeCents, now, installation: true);
                world.Add(
                    new SavedJumpClone
                    {
                        PilotId = pilotId,
                        LocationId = offer.ShipId,
                        InShip = true,
                    }
                );
                world.Commit();
                return new ShipCloneInstallation(offer, balance);
            }
        );
    }

    public ShipCloneOffer? Cancel(long pilotId) => offers.Cancel(pilotId);

    public ShipCloneJump JumpToShip(long pilotId, long expectedShipId, long shipId, long cloneId, long quotedCents)
    {
        using var world = Resident.Economy.Begin(new(pilots: [pilotId], ships: [expectedShipId, shipId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        long now = Now;
        var pilot = Pilot(world, pilotId);
        var old = pilot.ActiveShip;
        var source = CurrentStation(catalog, pilot, facility: false);
        if (old.Id != expectedShipId || catalog.FittingType(old.TypeId)?.GroupId != FleetDefinitions.CapsuleGroup)
        {
            throw new InvalidDataException("Leave your ship and jump from your current capsule.");
        }
        var incoming = Owned(world, pilotId, cloneId);
        if (!incoming.InShip || incoming.LocationId != shipId || world.Ships.Find(shipId) is not { } hull)
        {
            throw new InvalidDataException("The jump clone is not at that ship.");
        }
        var host = Pilot(world, hull.OwnerId);
        var (system, position) = RequireVat(world, catalog, host, shipId, now);
        JumpCloneRules.CheckLocation(system, catalog.RequireSystem(system).RegionId);
        CrimewatchPersistence.CheckTravel(world, catalog, pilot, CrimewatchTravel.ChangeShip, now);
        var timer = world.Set<SavedJumpCloneTimer>().Find(pilotId);
        JumpCloneRules.CheckTimer(false, timer?.Time, Skills(world, catalog, pilot), now);
        long balance = SettleVatFee(world, pilotId, host.Id, shipId, quotedCents, now, installation: false);
        var type = catalog.RequireItem(FleetDefinitions.CapsuleType);
        var arrival =
            position
            + new SpacePosition((catalog.RequireItem(hull.TypeId).Radius ?? 0) + (type.Radius ?? 0) + VatRange(catalog, hull), 0, 0);
        var previous = PilotFleetStore.Read(catalog, old);
        var capsule = world
            .Add(
                new Ship
                {
                    Id = world.NextItemId(),
                    OwnerId = pilotId,
                    TypeId = type.Id,
                    StationId = source.Id,
                    Name = type.Name,
                    Position = new FlightPosition
                    {
                        SystemId = system,
                        X = arrival.X,
                        Y = arrival.Y,
                        Z = arrival.Z,
                    },
                }
            )
            .Entity;
        SwapBodies(world, pilotId, incoming, source.Id);
        timer ??= world.Add(new SavedJumpCloneTimer { PilotId = pilotId }).Entity;
        timer.Time = now;
        pilot.ActiveShip = capsule;
        pilot.ActiveShipId = capsule.Id;
        world.SaveChanges();
        ShipStoragePersistence.Destroy(world, old);
        world.SaveChanges();
        var item = PilotFleetStore.Read(catalog, capsule);
        var changed = lookup.FindPilot(world, pilot.AccountId, pilotId)!;
        world.Commit();
        return new(new(changed, [new(previous with { Quantity = 0 }, previous), new(item, null)], balance, source.Id), host.Id);
    }

    private static IQueryable<SavedJumpClone> BodiesInShip(WorldAction world, long shipId) =>
        world.Filter<SavedJumpClone>(clone => clone.LocationId == shipId && clone.InShip);

    private bool RequireInstallation(WorldAction world, CatalogDbContext catalog, ShipCloneOffer offer, long shipId, long now)
    {
        var host = Pilot(world, offer.HostId);
        var guest = Pilot(world, offer.PilotId);
        var (system, position) = RequireVat(world, catalog, host, offer.ShipId, now);
        if (
            guest.ActiveShipId != shipId
            || guest.ActiveShip.Position is null
            || CrimewatchPersistence.System(catalog, guest.ActiveShip) != system
            || !TryPosition(world, system, shipId, out var guestPosition)
        )
        {
            throw new InvalidDataException("Approach the clone vat in space.");
        }
        var hull = host.ActiveShip;
        double radii = (catalog.RequireItem(hull.TypeId).Radius ?? 0) + (catalog.RequireItem(guest.ActiveShip.TypeId).Radius ?? 0);
        if (position.DistanceTo(guestPosition) - radii > VatRange(catalog, hull))
        {
            throw new InvalidDataException("Approach within range of the ship clone vat.");
        }
        var bodies = BodiesInShip(world, hull.Id);
        if (bodies.Any(clone => clone.PilotId == guest.Id))
        {
            throw new InvalidDataException("You already have a jump clone aboard this ship.");
        }
        double capacity = PilotFittingStore.ShipAttributes(world, catalog, host).GetValueOrDefault(DogmaAttributeId.MaxJumpClones);
        if (bodies.Count() >= (int)capacity)
        {
            throw new InvalidDataException("The ship clone vat is full.");
        }
        if (
            world.Filter<SavedJumpClone>(clone => clone.PilotId == guest.Id && !clone.Active).Count()
            >= Skills(world, catalog, guest).Capacity
        )
        {
            throw new InvalidDataException("Your trained Infomorph skills and account status do not allow another jump clone.");
        }
        return true;
    }

    private (int System, SpacePosition Position) RequireVat(WorldAction world, CatalogDbContext catalog, Pilot host, long shipId, long now)
    {
        var hull = host.ActiveShip;
        if (
            hull.Id != shipId
            || hull.Position is null
            || hull.OwnerId != host.Id
            || PilotCapitalTravelStore.ActiveMode(world, shipId, now)?.Effect != DogmaEffect.CloneJumpAccepting
        )
        {
            throw new InvalidDataException("The destination ship's clone vat is unavailable or inactive.");
        }
        int system = CrimewatchPersistence.System(catalog, hull);
        if (!TryPosition(world, system, shipId, out var position))
        {
            throw new InvalidDataException("The destination ship's clone vat is unavailable.");
        }
        return (system, position);
    }

    private bool TryPosition(WorldAction world, int system, long shipId, out SpacePosition position)
    {
        if (
            !presence.IsTravelProtected(system, shipId)
            && world.Set<SpatialNavigationState>().Find(shipId) is not { CloakModuleId: not 0 }
            && presence.FindArea(system, shipId) is { Position: var current }
        )
        {
            position = current;
            return true;
        }
        position = SpacePosition.Zero;
        return false;
    }

    private static double VatRange(CatalogDbContext catalog, Ship hull) =>
        catalog.FittingAttributes(hull.TypeId).GetValueOrDefault(DogmaAttributeId.MaxOperationalDistance);

    private static long SettleVatFee(WorldAction world, long pilotId, long hostId, long shipId, long quoted, long now, bool installation)
    {
        var payer = world.Set<PilotWallet>().Find(pilotId);
        long balance = JumpCloneRules.Pay(payer?.Cents ?? 0, quoted);
        payer!.Cents = balance;
        var kind = installation ? WalletEntryType.JumpCloneInstallation : WalletEntryType.JumpCloneActivation;
        world.Add(
            new SavedServicePayment
            {
                PilotId = pilotId,
                CounterpartyId = hostId,
                ContextId = shipId,
                Kind = kind,
                AmountCents = -quoted,
                BalanceCents = balance,
                Time = now,
            }
        );
        if (installation)
        {
            var receiver = world.Set<PilotWallet>().Find(hostId) ?? world.Add(new PilotWallet { PilotId = hostId }).Entity;
            receiver.Cents = checked(receiver.Cents + quoted);
            world.Add(
                new SavedServicePayment
                {
                    PilotId = hostId,
                    CounterpartyId = pilotId,
                    ContextId = shipId,
                    Kind = kind,
                    AmountCents = quoted,
                    BalanceCents = receiver.Cents,
                    Time = now,
                }
            );
        }
        return balance;
    }
}
