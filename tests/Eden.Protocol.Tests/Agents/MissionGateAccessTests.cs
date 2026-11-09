using System.Text.Json.Nodes;
using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Inventory;
using Eden.Protocol.PilotSessions;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed class MissionGateAccessTests
{
    private const int ScarletMission = 15237;
    private const int GateKeyType = 2076;

    [Fact]
    public void ScarletBossCompletesMissionAndEarlierKeyCarriersStillDropOnlyOneKey()
    {
        using MissionFixture f = new(4, MissionRules.SecurityDivision);
        var mission = Scarlet(f);
        var rooms = f.Missions.Sites(mission.SystemId);
        foreach (var room in rooms)
        {
            f.Missions.Arrive(mission.SystemId, room.Position);
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            long boss = world.Set<SavedMissionMember>().Single(m => m.MissionId == mission.Id && m.CompletesMission).NpcId;
            f.Kill(boss);
        }
        Assert.True(f.Missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!.ObjectiveComplete);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var carriers = world
                .Set<SavedMissionMember>()
                .Where(m => m.MissionId == mission.Id && m.LootTypeId == GateKeyType)
                .Select(m => m.NpcId)
                .ToArray();
            foreach (long npc in carriers)
            {
                f.Kill(npc);
            }
        }
        Assert.Equal(1, Quantity(f, location: null));
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath))
        {
            var profile = MissionCatalog.Find(catalog, ScarletMission);
            Assert.True(profile.AttacksFaction(500010));
            Assert.True(profile.AttacksFaction(500020));
            Assert.False(profile.AttacksFaction(500001));
        }
        f.Dock(MissionFixture.StationId);
        Assert.NotNull(f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, mission.Id).BalanceCents);
        Assert.Throws<InvalidDataException>(() =>
            f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, mission.Id)
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScarletDropsOneReusableKeyAndNativeGateRejectsMissingCargoWithoutStartingWarp(bool resident)
    {
        using MissionFixture f = new(4, MissionRules.SecurityDivision, live: resident);
        var mission = Scarlet(f);
        var lobby = f.Missions.Sites(mission.SystemId).Single(s => s.Position == mission.Position);
        var gate = Assert.Single(lobby.Gates);
        Assert.Equal(new MissionGateKey(GateKeyType), gate.Access.Key);
        Assert.False(lobby.Deadspace);
        Move(f, mission.SystemId, gate.Position);
        Assert.IsType<PilotCallResult.Failure>(f.Call("keeper", "ActivateAccelerationGate", [gate.Id]));
        Assert.DoesNotContain(f.Context.Flight.Updates, u => u.Command is DestinyCommand.WarpTo);
        f.Missions.Arrive(mission.SystemId, mission.Position);
        long[] carriers;
        using (WorldDbContext world = new(f.Db.Path))
        {
            carriers = world
                .Set<SavedMissionMember>()
                .Where(m => m.MissionId == mission.Id && m.LootTypeId == GateKeyType)
                .OrderBy(m => m.NpcId)
                .Select(m => m.NpcId)
                .ToArray();
        }
        Assert.Equal(4, carriers.Length);
        foreach (long id in carriers[..^1])
        {
            f.Kill(id);
            Assert.Equal(0, Quantity(f, location: null));
        }
        f.Kill(carriers[^1], commit: false);
        Assert.Equal(0, Quantity(f, location: null));
        f.Kill(carriers[^1]);
        Assert.Equal(1, Quantity(f, location: null));
        using (WorldDbContext world = new(f.Db.Path))
        {
            var key = world.Set<OwnedItem>().Single(i => i.TypeId == GateKeyType);
            var wreck = world.Set<WorldWreck>().Find(key.LocationId)!;
            Assert.Equal(carriers[^1], wreck.NpcId);
            Move(f, mission.SystemId, new(wreck.X, wreck.Y, wreck.Z));
            var changes = f
                .Services.GetRequiredService<IWrecks>()
                .Take(MissionFixture.PilotId, f.Context.Pilot.ShipId, wreck.Id, [key.Id], null, f.Clock.Now);
            Assert.Single(changes);
        }
        Move(f, mission.SystemId, gate.Position);
        Assert.Null(MissionFixture.Reply(f.Call("keeper", "ActivateAccelerationGate", [gate.Id])));
        Assert.Null(MissionFixture.Reply(f.Call("keeper", "ActivateAccelerationGate", [gate.Id])));
        Assert.Single(f.Context.Flight.Updates.Select(u => u.Command).OfType<DestinyCommand.WarpTo>());
        Assert.Equal(1, Quantity(f, f.Context.Pilot.ShipId));
        f.Clock.Seconds += 120;
        f.Context.Flight.Checkpoint();
        if (resident)
        {
            await LiveWorld.Find(f.Db.Path)!.FlushAsync();
            f.StopResident();
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.Equal(1, Quantity(f, f.Context.Pilot.ShipId));
        Move(f, mission.SystemId, gate.Position);
        Assert.Null(MissionFixture.Reply(f.Call("keeper", "ActivateAccelerationGate", [gate.Id])));
        Assert.Equal(1, Quantity(f, f.Context.Pilot.ShipId));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<SavedMissionGateUnlock>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsumedKeyPaymentIsAtomicAcrossStacksPersistsUnlockAndResetsAtExactDowntime(bool resident)
    {
        using MissionFixture f = new(4, MissionRules.SecurityDivision, live: resident);
        var mission = Scarlet(f);
        var gate = f.Missions.Sites(mission.SystemId).SelectMany(s => s.Gates).Single(g => g.Access.Key is not null);
        gate = gate with { Access = gate.Access with { Key = new(GateKeyType, 3, Consume: true) } };
        long ship = f.Context.Pilot.ShipId;
        AddKey(f, ship, 2);
        AddKey(f, ship, 2);
        var rolledBack = Pay(f, mission, gate, commit: false);
        Assert.Equal(2, rolledBack.Items.Count);
        Assert.Equal(4, Quantity(f, ship));
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Empty(world.Set<SavedMissionGateUnlock>());
        }
        var paid = Pay(f, mission, gate);
        Assert.Equal(2, paid.Items.Count);
        Assert.Equal(1, Quantity(f, ship));
        foreach (var item in paid.Items)
        {
            InventoryChangesWire.NotifyItemChange(f.Context, item, beforeFlight: true);
        }
        Assert.Equal(2, f.Context.NotificationsBeforeFlight.OfType<ItemChanged>().Count());
        Assert.Empty(f.Context.Notifications);
        if (resident)
        {
            await LiveWorld.Find(f.Db.Path)!.FlushAsync();
            f.StopResident();
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.Empty(Pay(f, mission, gate).Items);
        Assert.Equal(1, Quantity(f, ship));
        Assert.Throws<InvalidDataException>(() => Pay(f, mission, gate, pilotId: 90000002));
        Assert.Throws<InvalidDataException>(() => Pay(f, mission, gate with { DungeonObjectId = gate.DungeonObjectId + 1 }));
        f.Clock.UtcNow = MissionGateAccessRules.NextDowntime(f.Clock.UtcNow);
        Assert.Throws<InvalidDataException>(() => Pay(f, mission, gate));
        Assert.Equal(1, Quantity(f, ship));
        AddKey(f, ship, 2);
        Assert.Equal(2, Pay(f, mission, gate).Items.Count);
        Assert.Equal(0, Quantity(f, ship));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Single(saved.Set<SavedMissionGateUnlock>());
        Assert.True(saved.Set<SavedMissionGateUnlock>().Single().ResetAt > f.Clock.Now);
    }

    [Fact]
    public void KeyChecksIgnoreOtherOwnersStationsContainersAndSpecializedHolds()
    {
        using MissionFixture f = new(4, MissionRules.SecurityDivision);
        var mission = Scarlet(f);
        var gate = f.Missions.Sites(mission.SystemId).SelectMany(s => s.Gates).Single(g => g.Access.Key is not null);
        long ship = f.Context.Pilot.ShipId;
        AddKey(f, MissionFixture.StationId, 1);
        AddKey(f, ship, 1, flag: InventoryFlag.FleetHangar);
        AddKey(f, ship, 1, flag: InventoryFlag.GeneralMiningHold);
        AddKey(f, ship, 1, owner: 90000002);
        Assert.Throws<InvalidDataException>(() => Pay(f, mission, gate));
        Assert.Equal(4, Quantity(f, location: null));
        AddKey(f, ship, 1);
        Assert.Empty(Pay(f, mission, gate).Items);
        Assert.Empty(Pay(f, mission, gate).Items);
        Assert.Equal(5, Quantity(f, location: null));
    }

    [Fact]
    public void Version124UpgradeAddsEmptyKeyStateAndPreservesExistingMissionEnemiesAndQuote()
    {
        using MissionFixture f = new(division: MissionRules.SecurityDivision);
        var mission = f.Accept(f.Request());
        f.Missions.Arrive(mission.SystemId, mission.Position);
        using (WorldDbContext world = new(f.Db.Path))
        {
            f.Kill(world.Set<SavedMissionMember>().First().NpcId);
            world.Database.ExecuteSqlRaw(
                """
                DROP TABLE WorldMissionGateUnlock;
                ALTER TABLE WorldMissionMember DROP COLUMN LootTypeId;
                ALTER TABLE WorldMissionMember DROP COLUMN LootQuantity;
                PRAGMA user_version=124;
                """
            );
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        f.Missions.Arrive(mission.SystemId, mission.Position);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<SavedMissionGateUnlock>());
        Assert.Equal(9, saved.Set<SavedMissionMember>().Count());
        Assert.All(
            saved.Set<SavedMissionMember>(),
            m =>
            {
                Assert.Equal(0, m.LootTypeId);
                Assert.Equal(0, m.LootQuantity);
            }
        );
        Assert.Equal(mission.Rewards.IskCents, saved.Set<SavedMission>().Find(mission.Id)!.IskCents);
        Assert.Equal(WorldMigration.CurrentVersion, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CatalogGateListsAndRacesRejectProhibitedHullsAndOfflineFittedEquipment(bool offline)
    {
        using MissionFixture f = new(division: MissionRules.SecurityDivision, configure: Configure);
        var mission = Gated(f);
        var gate = Assert.Single(f.Missions.Sites(mission.SystemId).SelectMany(s => s.Gates));
        Move(f, mission.SystemId, gate.Position);
        Assert.Equal(gate, f.Missions.Gate(MissionFixture.PilotId, gate.Id));
        int originalType = f.Context.Pilot.ShipTypeId;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Ships.Find(f.Context.Pilot.ShipId)!.TypeId = 670;
            world.SaveChanges();
        }
        Assert.IsType<PilotCallResult.Failure>(f.Call("keeper", "ActivateAccelerationGate", [gate.Id]));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Ships.Find(f.Context.Pilot.ShipId)!.TypeId = originalType;
            world.Add(
                new OwnedItem
                {
                    Id = InventoryPersistence.NextId(world),
                    OwnerId = MissionFixture.PilotId,
                    LocationId = f.Context.Pilot.ShipId,
                    FlagId = (int)InventoryFlag.LowSlot0,
                    TypeId = 520,
                    Quantity = 1,
                    Online = !offline,
                }
            );
            world.SaveChanges();
        }
        Assert.IsType<PilotCallResult.Failure>(f.Call("keeper", "ActivateAccelerationGate", [gate.Id]));
        Assert.DoesNotContain(f.Context.Flight.Updates, u => u.Command is DestinyCommand.WarpTo);

        static void Configure(CatalogDbContext catalog)
        {
            string raw = catalog
                .Database.SqlQueryRaw<string>("SELECT Data AS Value FROM SourceRecord WHERE Dataset='client/dungeons' AND Key='213'")
                .Single();
            var dungeon = JsonNode.Parse(raw)!;
            dungeon["connections"]![0]!["allowedShipsList"] = 459;
            dungeon["connections"]![0]!["allowedShipRaces"] = new JsonArray(1);
            dungeon["connections"]![0]!["disallowedFittedTypesList"] = 900001;
            string updated = dungeon.ToJsonString();
            catalog.Database.ExecuteSql($"UPDATE SourceRecord SET Data={updated} WHERE Dataset='client/dungeons' AND Key='213'");
            string list = "{\"includedTypeIDs\":[520]}";
            catalog.Database.ExecuteSql($"INSERT INTO SourceRecord VALUES ('client/typelist','900001',{list})");
        }
    }

    [Theory]
    [InlineData(25, 1, true)]
    [InlineData(25, 2, false)]
    [InlineData(25, 0, true)]
    [InlineData(29, 1, false)]
    [InlineData(5120, 1, false)]
    public void TypedAccessUsesCatalogListRaceAndDefaultCommandCarrierRestriction(int group, int race, bool allowed)
    {
        InventoryItem ship = new(1, 601, 2, 3, 0, 1, group, (int)InventoryCategory.Ship, 1);
        var access = new MissionGateAccess(null, new HashSet<long> { 1 }, null, null);
        if (group is 29 or 5120)
        {
            access = MissionGateAccess.Default;
        }
        if (allowed)
        {
            MissionGateAccessRules.ValidateShip(access, ship, race, []);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => MissionGateAccessRules.ValidateShip(access, ship, race, []));
        }
    }

    [Fact]
    public void ExplicitTypeInclusionOverridesGroupExclusionAndCanAdmitDefaultRestrictedHull()
    {
        InventoryItem ship = new(1, 670, 2, 3, 0, 1, 29, (int)InventoryCategory.Ship, 1);
        InventoryTypeList list = new(
            new HashSet<long> { 6 },
            new HashSet<long>(),
            new HashSet<long> { 670 },
            new HashSet<long>(),
            new HashSet<long> { 29 },
            new HashSet<long>()
        );
        var access = new MissionGateAccess(list, new HashSet<long>(), null, null);
        MissionGateAccessRules.ValidateShip(access, ship, 0, []);
        Assert.Throws<InvalidDataException>(() => MissionGateAccessRules.ValidateShip(access, ship with { TypeId = 671 }, 0, []));
        Assert.False(MissionGateAccessRules.RequiresKey(new(GateKeyType, Consume: true), true));
        Assert.True(MissionGateAccessRules.RequiresKey(new(GateKeyType), true));
    }

    private static MissionOffer Scarlet(MissionFixture f)
    {
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new SavedNpcStanding
                {
                    PilotId = MissionFixture.PilotId,
                    SourceId = 1000044,
                    Value = 8,
                }
            );
            world.SaveChanges();
        }
        return Find(f, ScarletMission);
    }

    private static MissionOffer Gated(MissionFixture f) => Find(f, 875);

    private static MissionOffer Find(MissionFixture f, int id)
    {
        for (int i = 0; i < 10; i++)
        {
            var offer = f.Accept(f.Request());
            if (offer.MissionId == id)
            {
                return offer;
            }
            f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Quit, offer.Id);
        }
        throw new InvalidOperationException("Missing mission.");
    }

    private static MissionGateActivation Pay(
        MissionFixture f,
        MissionOffer mission,
        MissionGate gate,
        bool commit = true,
        long pilotId = MissionFixture.PilotId
    )
    {
        long shipId = f.Context.Pilot.ShipId;
        using WorldDbContext world = new(f.Db.Path);
        if (pilotId != MissionFixture.PilotId)
        {
            shipId = world.Pilots.Where(p => p.Id == pilotId).Select(p => p.ActiveShipId).Single();
        }
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        using var transaction = world.Database.BeginTransaction();
        var result = MissionGatePayment.Apply(world, catalog, pilotId, shipId, mission.Id, gate, f.Clock.Now);
        world.SaveChanges();
        if (commit)
        {
            transaction.Commit();
        }
        else
        {
            transaction.Rollback();
        }
        return result;
    }

    private static long AddKey(
        MissionFixture f,
        long location,
        int quantity,
        InventoryFlag flag = InventoryFlag.Cargo,
        long owner = MissionFixture.PilotId
    )
    {
        using WorldDbContext world = new(f.Db.Path);
        var item = world
            .Add(
                new OwnedItem
                {
                    Id = InventoryPersistence.NextId(world),
                    OwnerId = owner,
                    LocationId = location,
                    FlagId = (int)flag,
                    TypeId = GateKeyType,
                    Quantity = quantity,
                }
            )
            .Entity;
        world.SaveChanges();
        return item.Id;
    }

    private static int Quantity(MissionFixture f, long? location)
    {
        using WorldDbContext world = new(f.Db.Path);
        return world
            .Set<OwnedItem>()
            .Where(i => i.TypeId == GateKeyType && (location == null || i.LocationId == location))
            .Sum(i => i.Quantity);
    }

    private static void Move(MissionFixture f, int system, SpacePosition position)
    {
        f.Db.Store.SetPosition(f.Context.Pilot.ShipId, position, system);
        f.Context.ChangeShip(f.Db.Store.FindPilot(1000001)!);
        f.Context.Flight.Snapshot();
    }
}
