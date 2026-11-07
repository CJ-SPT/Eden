using System.Globalization;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Docking;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Characters;

public sealed class SavedHomeStation
{
    public long PilotId { get; set; }
    public int StationId { get; set; }
}

public sealed class SavedHomeStationPayment
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public int StationId { get; set; }
    public long OperatorId { get; set; }
    public long AmountCents { get; set; }
    public long BalanceCents { get; set; }
    public long Time { get; set; }

    public HomeStationPayment Snapshot() => new(Id, PilotId, StationId, OperatorId, AmountCents, BalanceCents, Time);
}

public sealed class SavedHomeCloneTransfer
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long OldShipId { get; set; }
    public long NewShipId { get; set; }
    public int SourceStationId { get; set; }
    public int DestinationStationId { get; set; }
    public long Time { get; set; }
}

[Injectable]
internal sealed class PilotHomeStationStore(WorldDatabase database, SqliteWorld lookup, TimeProvider clock, PilotTrainingStore training)
    : IPilotHomeStation
{
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    private static Pilot Pilot(WorldDbContext world, long id) =>
        world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).SingleOrDefault(p => p.Id == id)
        ?? throw new InvalidDataException("Unknown pilot.");

    private static Station? Station(CatalogDbContext catalog, int id) =>
        catalog.Set<Station>().Include(s => s.SolarSystem).ThenInclude(s => s.Constellation).SingleOrDefault(s => s.Id == id);

    private static HomeStation Snapshot(Station station, bool fallback = false) =>
        new(station.Id, station.TypeId, station.SolarSystemId, fallback);

    private static int? SchoolHeadquarters(CatalogDbContext catalog, Pilot pilot)
    {
        long? corporation = catalog
            .Set<SourceRecord>()
            .Find("client/schools", pilot.SchoolId.ToString(CultureInfo.InvariantCulture))
            ?.Read<ClientSchools>()
            .CorporationID;
        long? station = corporation is long id
            ? catalog
                .Set<SourceRecord>()
                .Find("client/npccorporations", id.ToString(CultureInfo.InvariantCulture))
                ?.Read<ClientNpccorporations>()
                .StationID
            : null;
        return station is > 0 and <= int.MaxValue ? (int)station : null;
    }

    internal static HomeStation Read(WorldDbContext world, CatalogDbContext catalog, Pilot pilot)
    {
        int id = world.Set<SavedHomeStation>().Find(pilot.Id)?.StationId ?? pilot.ActiveShip.StationId;
        if (Station(catalog, id) is { } saved)
        {
            return Snapshot(saved);
        }
        int fallback = SchoolHeadquarters(catalog, pilot) ?? pilot.ActiveShip.StationId;
        return Snapshot(Station(catalog, fallback) ?? throw new InvalidDataException("No valid home station is available."), true);
    }

    public HomeStation Read(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        return Read(world, catalog, Pilot(world, pilotId));
    }

    public IReadOnlyList<HomeStationCandidate> Candidates(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        return Candidates(world, catalog, Pilot(world, pilotId));
    }

    private static IReadOnlyList<HomeStationCandidate> Candidates(WorldDbContext world, CatalogDbContext catalog, Pilot pilot)
    {
        var home = Read(world, catalog, pilot);
        int? current = pilot.ActiveShip.Position is null ? pilot.ActiveShip.StationId : null;
        int? school = SchoolHeadquarters(catalog, pilot);
        // Eden's pilots currently belong to NPC corporations; there are no
        // player-corporation offices that grant additional remote candidates.
        return new[] { current, school }
            .OfType<int>()
            .Distinct()
            .Select(id => Station(catalog, id))
            .OfType<Station>()
            .OrderBy(station => station.Id)
            .Select(station => new HomeStationCandidate(
                Snapshot(station),
                station.Id == current,
                station.Id == school,
                HomeStationRules.Errors(station.Id, home.Id, station.SolarSystemId, station.SolarSystem.Constellation.RegionId)
            ))
            .ToArray();
    }

    public long? NextRemoteChange(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        _ = Pilot(world, pilotId);
        // Current station and school HQ changes never consume the remote-office
        // cooldown. NPC-corporation pilots have no remote-office entitlement.
        return null;
    }

    public HomeStationChange Set(long pilotId, int stationId, bool allowRemote)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        var candidate =
            Candidates(world, catalog, pilot).SingleOrDefault(candidate => candidate.Station.Id == stationId)
            ?? throw new InvalidDataException("Choose your current station or your school's headquarters.");
        if (candidate.Errors.Count > 0)
        {
            throw new InvalidDataException(
                candidate.Errors.Contains(HomeStationError.AlreadyHome)
                    ? "That station is already your home."
                    : "That station cannot be selected as your home."
            );
        }
        // allowRemote is confirmation, never permission to select an arbitrary
        // station. All currently eligible candidates are exempt from cooldown.
        _ = allowRemote;
        var wallet = world.Set<PilotWallet>().Find(pilotId);
        long balance = HomeStationRules.Pay(wallet?.Cents ?? 0);
        wallet!.Cents = balance;
        var saved = world.Set<SavedHomeStation>().Find(pilotId) ?? world.Add(new SavedHomeStation { PilotId = pilotId }).Entity;
        saved.StationId = stationId;
        world.Add(
            new SavedHomeStationPayment
            {
                PilotId = pilotId,
                StationId = stationId,
                OperatorId = Station(catalog, stationId)!.OwnerId,
                AmountCents = HomeStationRules.RelocationFeeCents,
                BalanceCents = balance,
                Time = Now,
            }
        );
        world.SaveChanges();
        transaction.Commit();
        return new(candidate.Station, balance);
    }

    public HomeCloneTransfer SelfDestruct(long pilotId, long expectedShipId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        var old = pilot.ActiveShip;
        var home = Read(world, catalog, pilot);
        if (old.Id != expectedShipId || old.OwnerId != pilotId)
        {
            throw new InvalidDataException("Your active capsule changed before the transfer.");
        }
        var type = catalog.Set<ItemType>().Find(old.TypeId) ?? throw new InvalidDataException("Unknown active capsule.");
        HomeStationRules.CheckSelfDestruct(old.Position is null, type.GroupId == FleetDefinitions.CapsuleGroup, old.StationId == home.Id);
        var capsuleType =
            catalog.Set<ItemType>().SingleOrDefault(t => t.Id == FleetDefinitions.CapsuleType && t.GroupId == FleetDefinitions.CapsuleGroup)
            ?? throw new InvalidDataException("The catalog has no replacement capsule.");
        var previous = PilotFleetStore.Read(catalog, old);
        var capsule = world
            .Add(
                new Ship
                {
                    Id = InventoryPersistence.NextId(world),
                    OwnerId = pilotId,
                    TypeId = capsuleType.Id,
                    Name = capsuleType.Name,
                    StationId = home.Id,
                }
            )
            .Entity;
        long now = Now;
        world.Add(
            new SavedHomeCloneTransfer
            {
                PilotId = pilotId,
                OldShipId = old.Id,
                NewShipId = capsule.Id,
                SourceStationId = old.StationId,
                DestinationStationId = home.Id,
                Time = now,
            }
        );
        PilotTaskTrackingStore.Record(world, catalog, pilotId, now, TaskEvents.PodLost);
        training.ChangeAttributes(world, pilotId, () => PilotJumpCloneStore.ResetActive(world, pilotId));
        pilot.ActiveShip = capsule;
        pilot.ActiveShipId = capsule.Id;
        world.SaveChanges();
        ShipStoragePersistence.Destroy(world, old);
        world.SaveChanges();
        var incoming = PilotFleetStore.Read(catalog, capsule);
        transaction.Commit();
        return new(lookup.FindPilot(pilot.AccountId, pilotId)!, [new(previous with { Quantity = 0 }, previous), new(incoming, null)]);
    }
}

[Injectable]
internal sealed class NpcCorporationDirectory(WorldDatabase database) : INpcCorporationDirectory
{
    public NpcCorporationInfo Read(int id)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        var corporation = catalog.Set<NpcCorporation>().Find(id) ?? throw new InvalidDataException("Unknown NPC corporation.");
        return Read(catalog, corporation);
    }

    public IReadOnlyList<NpcCorporationInfo> Read(IReadOnlyList<int> ids)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return catalog
            .Set<NpcCorporation>()
            .Where(c => ids.Contains(c.Id))
            .OrderBy(c => c.Id)
            .ToArray()
            .Select(c => Read(catalog, c))
            .ToArray();
    }

    private static NpcCorporationInfo Read(CatalogDbContext catalog, NpcCorporation corporation)
    {
        int id = corporation.Id;
        var data =
            catalog
                .Set<SourceRecord>()
                .Find("client/npccorporations", id.ToString(CultureInfo.InvariantCulture))
                ?.Read<ClientNpccorporations>()
            ?? throw new InvalidDataException("Missing NPC corporation metadata.");
        return new(
            id,
            corporation.Name,
            data.TickerName ?? "",
            checked((int?)data.StationID),
            checked((int?)data.SolarSystemID),
            data.FactionID,
            data.CeoID,
            data.TaxRate ?? 0,
            data.Shares ?? 0,
            data.MemberLimit ?? 0,
            data.Url ?? ""
        );
    }
}
