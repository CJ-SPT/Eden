using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.Gameplay.Sovereignty;
using Eden.Gameplay.Targeting;
using Eden.Protocol.Tests.Alliances;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Sovereignty;

internal sealed class SovereigntyCaptureFixture : IDisposable
{
    internal const long PilotId = 90000001;
    internal const long OtherPilotId = 90000002;
    internal const int SystemId = 30003995;
    internal const int Strontium = 16275;
    internal const int LinkOne = 34593;
    internal const int LinkTwo = 34595;
    internal TestClock Clock { get; } = new(new DateTimeOffset(2026, 11, 16, 18, 0, 0, TimeSpan.Zero));
    internal TestWorld World { get; }
    internal LiveWorld Live => World.Services.GetRequiredService<LiveWorld>();
    internal IPilotEntosis Entosis => World.Services.GetRequiredService<IPilotEntosis>();
    internal ISovereignty Claims => World.Services.GetRequiredService<ISovereignty>();
    internal IPilotTargetLocks Locks => World.Services.GetRequiredService<IPilotTargetLocks>();
    internal int CorporationId { get; }
    internal int AllianceId { get; }
    internal int OtherAllianceId { get; }

    internal SovereigntyCaptureFixture(int hull = 643)
    {
        World = new(clock: Clock, liveWorld: true);
        PassiveCatalogFixture.LoadSovereignty(new WorldDatabase(World.Path).CatalogPath);
        CorporationId = AllianceCreationTests.Corporation(World);
        AllianceId = World.Services.GetRequiredService<IAlliances>().Create(PilotId, new("Sovereignty Alpha", "SVA", "", "")).Alliance.Id;
        _ = AllianceCreationTests.Corporation(World, OtherPilotId, "Sovereignty Beta Corp", "SVB");
        OtherAllianceId = World
            .Services.GetRequiredService<IAlliances>()
            .Create(OtherPilotId, new("Sovereignty Beta", "SVBX", "", ""))
            .Alliance.Id;
        using var action = Live.Economy.Begin(TransactionParticipants.None);
        foreach (long id in new[] { PilotId, OtherPilotId })
        {
            var pilot = action.Pilots.Find(id)!;
            action.Set<Account>().Find(pilot.AccountId)!.CloneGrade = AccountCloneGrade.Omega;
            pilot.ActiveShip.TypeId = hull;
            var skill =
                action.Set<PilotSkill>().Find(id, EntosisRules.InfomorphPsychologySkill)
                ?? action.Add(new PilotSkill { PilotId = id, TypeId = EntosisRules.InfomorphPsychologySkill }).Entity;
            skill.Points = TrainedSkill.PointsForLevel(1, 5);
        }
        action.Commit();
    }

    internal long Ship(long pilot = PilotId) => Live.Economy.Inspect(world => world.Pilots.Find(pilot)!.ActiveShipId);

    internal long Grant(int type, int quantity = 1, bool cargo = true, long pilot = PilotId)
    {
        using var action = Live.Economy.Begin(new(pilots: [pilot]));
        var ship = action.Pilots.Find(pilot)!.ActiveShip;
        long id = InventoryPersistence.NextId(action);
        action.Add(
            new OwnedItem
            {
                Id = id,
                TypeId = type,
                OwnerId = pilot,
                Quantity = quantity,
                LocationId = cargo ? ship.Id : ship.DockedLocationId,
                FlagId = (int)(cargo ? InventoryFlag.Cargo : InventoryFlag.Hangar),
            }
        );
        action.Commit();
        return id;
    }

    internal long Fit(int type = LinkTwo, long pilot = PilotId)
    {
        var fitting = World.Services.GetRequiredService<PilotFittingStore>();
        long id = Grant(type, cargo: false, pilot: pilot);
        long station = Live.Economy.Inspect(world => world.Pilots.Find(pilot)!.ActiveShip.DockedLocationId);
        fitting.Fit(pilot, [id], station, (int)InventoryFlag.AutoFit);
        fitting.SetOnline(pilot, Ship(pilot), id, true);
        return id;
    }

    internal void Position(SpacePosition point, long pilot = PilotId, int system = SystemId) =>
        World.Store.SetPosition(Ship(pilot), point, system);

    internal long Deploy()
    {
        Position(new(2_000_000_000, 0, 0));
        return Entosis.Deploy(PilotId, Grant(EntosisRules.HubTypeId)).Structure.Id;
    }

    internal void Lock(long hub, long pilot = PilotId) => Locks.Add(Ship(pilot), hub);

    internal double Capacitor(long pilot = PilotId) =>
        Live.Economy.Inspect(world =>
        {
            using CatalogDbContext catalog = new(new WorldDatabase(World.Path).CatalogPath);
            return ShipResources.Capacitor(world, Ship(pilot), PilotFittingStore.ShipAttributes(world, catalog, pilot), Clock.Now);
        });

    internal int Quantity(long item) => Live.Economy.Inspect(world => world.Set<OwnedItem>().Find(item)?.Quantity ?? 0);

    public void Dispose() => World.Dispose();
}
