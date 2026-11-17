using Eden.Data.World.Capsuleers;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Inventory;
using Eden.Server;
using Eden.Server.Administration;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Configuration;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class PatrolTests
{
    [Fact]
    public void GeneratedNullsecPilotsOwnTheirFitWithoutAnImportedIdentity()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        using (CatalogDbContext edit = new(f.Database.CatalogPath, writable: true))
        {
            edit.Database.ExecuteSqlRaw(
                "UPDATE SourceRecord SET Data=json_set(Data,'$.security',-0.2) "
                    + "WHERE Dataset='client/solarsystemcontent' AND Key='30000141'"
            );
        }
        var config = new WorldConfigurationStore(f.Database, f.Clock);
        var saved = config.Read();
        var form = SettingsForm.From(saved.Active.Draft.Settings);
        form.GeneratedCombatPercent = 100;
        form.RoamPercent = 100;
        var settings = form.Build();
        config.Save(saved.Generation, saved.Active.Draft with { Settings = settings }, "combat fits");
        WorldInitialization.Initialize(f.Database, f.Clock);
        using var provisioner = new CapsuleerProvisioner(f.Database, f.Clock);
        provisioner.EnsureSystem(30000141);
        using WorldDbContext world = new(f.Database.Path);
        var pilot = Assert.Single(world.Set<SavedCapsuleerHauler>());
        Assert.Equal(603, world.Ships.Find(pilot.FounderShipId)!.TypeId);
        Assert.Equal(2, world.Set<OwnedItem>().Count(i => i.LocationId == pilot.FounderShipId && i.Online && i.TypeId == 561));
        Assert.Empty(world.Set<SavedTrafficIdentity>());
        using CatalogDbContext catalog = new(f.Database.CatalogPath);
        Assert.Null(CapsuleerPatrolFit.Select(catalog, settings.Capsuleers, 1, 30000142, 0));
        Assert.Null(CapsuleerPatrolFit.Select(catalog, settings.Capsuleers with { GeneratedCombatPercent = 0 }, 1, 30000141, 0));
        Assert.Throws<InvalidDataException>(() => CapsuleerPopulationRules.Validate(settings.Capsuleers with { RoamPercent = 101 }));
    }

    [Fact]
    public void FittedPilotUndocksPatrolsSeveralGridsAndReturns()
    {
        using CapsuleerHaulerTests.Fixture f = new(isolateRoute: true);
        var founder = f.Founder();
        using (CatalogDbContext catalog = new(f.Database.CatalogPath, writable: true))
        {
            string data = "{\"securityStatus\":-0.2}";
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000141',{data})");
            catalog.Database.ExecuteSqlRaw(
                "UPDATE SourceRecord SET Data=json_set(Data,'$.security',-0.2) "
                    + "WHERE Dataset='client/solarsystemcontent' AND Key='30000141'"
            );
        }
        using (CatalogDbContext catalog = new(f.Database.CatalogPath))
        {
            using WorldDbContext world = new(f.Database.Path);
            using var transaction = world.Database.BeginTransaction();
            world.Database.ExecuteSqlRaw("INSERT OR REPLACE INTO WorldCapsuleerProvisioning(Id,WorldSeed) VALUES(1,1)");
            TrafficFitting.Apply(
                world,
                catalog,
                founder.PilotId,
                founder.FounderShipId,
                TrafficSnapshotTests.Profile() with
                {
                    HullTypeId = 603,
                    Equipment = [new(561, (int)InventoryFlag.HighSlot0, 1), new(215, (int)InventoryFlag.HighSlot0, 50)],
                },
                f.Clock.Now
            );
            transaction.Commit();
        }
        var config = new WorldConfigurationStore(f.Database, f.Clock);
        var saved = config.Read();
        var form = SettingsForm.From(saved.Active.Draft.Settings);
        form.RoamPercent = 100;
        form.GeneratedCombatPercent = 100;
        form.BeltRatting = form.AnomalyRatting = false;
        config.Save(saved.Generation, saved.Active.Draft with { Settings = form.Build() }, "always roam");
        WorldInitialization.Initialize(f.Database, f.Clock);
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        HashSet<long> visited = [];
        using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, 30000141);
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        scheduler.PopulationPrepared(30000141);
        var worker = host.GetRequiredService<CapsuleerMaintenance>();
        var observation = scheduler.Hauler(founder.PilotId)!;
        for (int i = 0; i < 1600; i++)
        {
            if (observation.Work.Ratting is { Phase: RattingPhase.Searching, Patrol.Kind: PatrolKind.Roam } activity)
            {
                visited.Add(activity.Belts[activity.BeltIndex]);
            }
            if (visited.Count > 0 && observation.Pilot.Position is null)
            {
                break;
            }
            f.Clock.Seconds++;
            worker.Tick();
            observation = scheduler.Hauler(founder.PilotId)!;
        }
        var job = scheduler.Jobs().Single(j => j.PilotId == founder.PilotId);
        Assert.True(
            visited.Count >= 2,
            $"Visited {visited.Count} grids, job {job}, last activity {observation.Work.Activity}, {observation.Work.WaitingReason}"
        );
        Assert.Null(observation.Pilot.Position);
        Assert.Null(observation.Work.Ratting);
    }
}
