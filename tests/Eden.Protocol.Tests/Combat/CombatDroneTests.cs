using System.Reflection;
using System.Text.Json;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.Drones;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using BaseFixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class CombatDroneTests
{
    private const int Brutix = 16229;
    private const int WarriorTwo = 2488;
    private const int HammerheadTwo = 2185;
    private const int SystemId = 30000144;

    public static IEnumerable<object[]> TypeCases()
    {
        using var stream = Assembly
            .GetExecutingAssembly()
            .GetManifestResourceStream("Eden.Protocol.Tests.Fixtures.CombatDroneCatalog.json")!;
        using var document = JsonDocument.Parse(stream);
        foreach (var type in document.RootElement.GetProperty("types").EnumerateArray())
        {
            if (
                type.GetProperty("groupId").GetInt32() == DroneRules.CombatDroneGroup
                && type.GetProperty("attributes")
                    .EnumerateArray()
                    .Any(a =>
                        a.GetProperty("id").GetInt32() == (int)DogmaAttributeId.DroneBandwidthUsed
                        && a.GetProperty("value").GetDouble() is 5 or 10
                    )
            )
            {
                yield return [type.GetProperty("id").GetInt32()];
            }
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal BaseFixture Base { get; } = new();
        internal IPilotDrones Drones => Base.Db.Services.GetRequiredService<IPilotDrones>();
        internal long Pilot => Base.PilotId;
        internal long Ship => Base.ShipId;
        internal long Now => Base.Clock.Now;
        internal NpcContact Npc { get; }

        internal Fixture()
        {
            PassiveCatalogFixture.LoadCombatDrones(Base.CatalogPath);
            NpcEncounterTests.Prepare(Base.Db);
            using (CatalogDbContext catalog = new(Base.CatalogPath, writable: true))
            {
                catalog.Database.ExecuteSqlRaw(
                    """
                    INSERT OR IGNORE INTO DogmaAttribute VALUES (481,'entityKillBounty');
                    INSERT OR REPLACE INTO TypeAttribute VALUES (30380,481,3000);
                    INSERT INTO SourceRecord VALUES ('client/asteroidcountbytypeidbeltid','40009232','{{}}'),
                        ('client/asteroidcountbytypeidbeltid','40009245','{{}}');
                    """
                );
            }
            foreach (var skill in Enum.GetValues<DroneSkill>())
            {
                Base.Train((int)skill, 5);
            }
            using (WorldDbContext world = new(Base.Db.Path))
            {
                world.Ships.Find(Ship)!.TypeId = Brutix;
                world.SaveChanges();
            }
            Npc = Base.Db.Services.GetRequiredService<INpcEncounters>().List(SystemId, Now)[0];
        }

        internal long Bay(int type, int quantity)
        {
            long id = Base.Grant(type, quantity);
            Base.Inventory.Move(Pilot, [id], Base.StationId, Ship, (int)InventoryFlag.DroneBay, null);
            return id;
        }

        internal void Space(double distance = 1000) =>
            Base.Db.Store.SetPosition(Ship, Npc.Position + new SpacePosition(distance, 0, 0), SystemId);

        internal long[] Launch(int type = WarriorTwo, int quantity = 5)
        {
            long id = Bay(type, quantity);
            Space();
            return Drones.Launch(Pilot, [new(id, quantity)], Now).Launches.Single().DroneIds.ToArray();
        }

        internal DroneUpdate Tick(int seconds = 1)
        {
            Base.Clock.Seconds += seconds;
            return Drones.Advance(Pilot, Now, (int)Base.Clock.Seconds);
        }

        public void Dispose() => Base.Dispose();
    }

    [Fact]
    public void EveryPublishedStaticLightAndMediumProfileUsesItsOwnCatalogInputs()
    {
        using Fixture f = new();
        using CatalogDbContext catalog = new(f.Base.CatalogPath);
        var types = catalog.Set<ItemType>().Where(t => t.GroupId == DroneRules.CombatDroneGroup).ToArray();
        var admitted = types
            .Where(t =>
                catalog.FittingAttributes(t.Id).GetValueOrDefault(DogmaAttributeId.DroneBandwidthUsed)
                    is DroneRules.LightBandwidth
                        or DroneRules.MediumBandwidth
            )
            .ToArray();
        Assert.Equal(41, admitted.Length);
        foreach (var type in admitted)
        {
            var profile = DroneCatalog.Profile(catalog, type.Id);
            Assert.Equal(type.Mass, profile.Mass);
            Assert.Equal(catalog.FittingAttributes(type.Id)[DogmaAttributeId.WeaponCycleTime], profile.CycleMilliseconds);
            Assert.True(profile.Damage.Total > 0);
        }
        Assert.Throws<InvalidDataException>(() => DroneCatalog.Profile(catalog, 2476));
    }

    [Theory]
    [MemberData(nameof(TypeCases))]
    public void EveryAdmittedDroneVariantLaunchesAndReturnsTheSameIndividual(int typeId)
    {
        using Fixture f = new();
        long id = f.Launch(typeId, 1)[0];
        var contact = Assert.Single(f.Drones.List(SystemId, f.Now));
        Assert.Equal(typeId, contact.Profile.TypeId);
        Assert.Equal(contact.Profile.Defense.Undamaged, contact.Health.Remaining);
        f.Drones.Order(f.Pilot, [id], DroneOrder.ReturnToBay, null, new HashSet<long>(), f.Now);
        var returned = Assert.Single(f.Tick().Changes);
        Assert.Equal(id, returned.Item.Id);
        Assert.Equal(typeId, returned.Item.TypeId);
        Assert.Equal((int)InventoryFlag.DroneBay, returned.Item.FlagId);
        Assert.Equal(1, returned.Item.Quantity);
        Assert.True(returned.Item.Singleton);
        Assert.Empty(f.Drones.List(SystemId, f.Now));
    }

    [Theory]
    [InlineData(WarriorTwo, 25)]
    [InlineData(HammerheadTwo, 50)]
    public void BrutixLaunchesFiveIndividualOwnedDronesWithRealBandwidth(int type, double bandwidth)
    {
        using Fixture f = new();
        var ids = f.Launch(type);
        Assert.Equal(5, ids.Distinct().Count());
        var contacts = f.Drones.List(SystemId, f.Now);
        Assert.Equal(bandwidth, contacts.Sum(d => d.Profile.Bandwidth));
        Assert.All(
            contacts,
            d =>
            {
                Assert.Equal(f.Pilot, d.PilotId);
                Assert.Equal(f.Ship, d.ShipId);
                Assert.Equal(DroneActivity.Idle, d.Activity);
                Assert.False(d.Health.Remaining.Destroyed);
            }
        );
        Assert.DoesNotContain(f.Base.Inventory.List(f.Pilot, f.Ship), i => i.FlagId == (int)InventoryFlag.DroneBay);
        using WorldDbContext world = new(f.Base.Db.Path);
        Assert.All(
            ids,
            id =>
            {
                var item = world.Set<OwnedItem>().Find(id)!;
                Assert.Equal(1, item.Quantity);
                Assert.True(item.Singleton);
                Assert.Equal(SystemId, item.LocationId);
            }
        );
    }

    [Theory]
    [InlineData("docked")]
    [InlineData("skill")]
    [InlineData("count")]
    [InlineData("bandwidth")]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    [InlineData("wrong-hold")]
    [InlineData("heavy")]
    [InlineData("hull")]
    public void RejectedLaunchLeavesInventoryAndControlUnchanged(string failure)
    {
        using Fixture f = new();
        long stack = f.Bay(
            failure == "heavy" ? 2476 : HammerheadTwo,
            failure == "heavy" ? 1
                : failure == "count" ? 6
                : 5
        );
        if (failure != "docked")
        {
            f.Space();
        }
        if (failure == "skill")
        {
            f.Base.Train((int)DroneSkill.MediumDroneOperation, 0);
        }
        if (failure == "bandwidth")
        {
            using CatalogDbContext catalog = new(f.Base.CatalogPath, writable: true);
            catalog
                .Set<TypeAttribute>()
                .Where(a => a.ItemTypeId == Brutix && a.AttributeId == (int)DogmaAttributeId.DroneBandwidth)
                .ExecuteUpdate(set => set.SetProperty(a => a.Value, 40));
        }
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            var item = world.Set<OwnedItem>().Find(stack)!;
            if (failure == "hull")
            {
                world.Ships.Find(f.Ship)!.TypeId = 601;
            }
            if (failure == "foreign")
            {
                item.OwnerId++;
            }
            if (failure == "wrong-hold")
            {
                item.FlagId = (int)InventoryFlag.Cargo;
            }
            world.SaveChanges();
        }
        IReadOnlyList<DroneStack> request =
            failure == "duplicate"
                ? [new(stack, 1), new(stack, 1)]
                :
                [
                    new(
                        stack,
                        failure == "heavy" ? 1
                            : failure == "count" ? 6
                            : 5
                    ),
                ];
        Assert.Throws<InvalidDataException>(() => f.Drones.Launch(f.Pilot, request, f.Now));
        using WorldDbContext after = new(f.Base.Db.Path);
        Assert.Equal(
            failure == "heavy" ? 1
                : failure == "count" ? 6
                : 5,
            after.Set<OwnedItem>().Find(stack)!.Quantity
        );
        Assert.Empty(after.Set<WorldDrone>());
    }

    [Fact]
    public void PartialLaunchConservesStackAndFiveDroneLimitAcrossRepeatedRequests()
    {
        using Fixture f = new();
        long stack = f.Bay(WarriorTwo, 10);
        f.Space();
        var first = f.Drones.Launch(f.Pilot, [new(stack, 3)], f.Now);
        Assert.Equal(3, first.Launches.Single().DroneIds.Count);
        Assert.Equal(7, f.Base.Inventory.List(f.Pilot, f.Ship).Single(i => i.Id == stack).Quantity);
        f.Drones.Launch(f.Pilot, [new(stack, 2)], f.Now);
        Assert.Throws<InvalidDataException>(() => f.Drones.Launch(f.Pilot, [new(stack, 1)], f.Now));
        Assert.Equal(5, f.Base.Inventory.List(f.Pilot, f.Ship).Single(i => i.Id == stack).Quantity);
        Assert.Equal(5, f.Drones.List(SystemId, f.Now).Count);
    }

    [Fact]
    public void SkillsAffectDamageSpecializationOptimalAndTravelWithoutInventingBonuses()
    {
        using Fixture f = new();
        using CatalogDbContext catalog = new(f.Base.CatalogPath);
        var basis = DroneCatalog.Profile(catalog, HammerheadTwo);
        using WorldDbContext world = new(f.Base.Db.Path);
        var skilled = DroneCatalog.Skilled(world, catalog, f.Pilot, HammerheadTwo);
        Assert.Equal(basis.Damage.Total * 1.25 * 1.5 * 1.1, skilled.Damage.Total, 8);
        Assert.Equal(basis.Optimal * 1.25, skilled.Optimal);
        Assert.Equal(basis.MaximumSpeed * 1.25f, skilled.MaximumSpeed);
        var ordinary = DroneCatalog.Profile(catalog, 2183);
        var skills = Enum.GetValues<DroneSkill>().ToDictionary(s => s, _ => 5);
        Assert.Equal(ordinary.Damage.Total * 1.25 * 1.5, DroneRules.ApplySkills(ordinary, skills, new HashSet<int>()).Damage.Total, 8);
        Assert.Equal(60_000, DroneRules.ControlRange(skills));
    }

    [Fact]
    public void NativeDronePrimeUsesSkilledHpAndDamageForTooltipsAndBayDamageGauges()
    {
        using Fixture f = new();
        long id = f.Bay(WarriorTwo, 1);
        using var scope = f.Base.Db.Services.CreateScope();
        var pilot = f.Base.Db.Store.FindPilot(1000001)!;
        var item = f.Base.Inventory.List(f.Pilot, f.Ship).Single(i => i.Id == id);
        var prime = scope.ServiceProvider.GetRequiredService<ShipDogmaBuilder>().PrimeModule(pilot, item);
        using CatalogDbContext catalog = new(f.Base.CatalogPath);
        var basis = catalog.FittingAttributes(WarriorTwo);
        Assert.Equal(basis[DogmaAttributeId.StructureHitPoints] * 1.25, prime.Attributes[DogmaAttributeId.StructureHitPoints]);
        Assert.Equal(basis[DogmaAttributeId.ArmorHitPoints] * 1.25, prime.Attributes[DogmaAttributeId.ArmorHitPoints]);
        Assert.Equal(basis[DogmaAttributeId.ShieldCapacity] * 1.25, prime.Attributes[DogmaAttributeId.ShieldCapacity]);
        Assert.Equal(basis[DogmaAttributeId.DamageMultiplier] * 1.25 * 1.5 * 1.1, prime.Attributes[DogmaAttributeId.DamageMultiplier], 8);
    }

    [Fact]
    public void AttackRequiresOwnershipLockRangeAndLiveNpcBeforeChangingOrders()
    {
        using Fixture f = new();
        long drone = f.Launch(quantity: 1)[0];
        Assert.Throws<InvalidDataException>(() =>
            f.Drones.Order(f.Pilot, [drone], DroneOrder.Engage, f.Npc.Id, new HashSet<long>(), f.Now)
        );
        Assert.Throws<InvalidDataException>(() =>
            f.Drones.Order(f.Pilot, [drone, drone], DroneOrder.Orbit, null, new HashSet<long>(), f.Now)
        );
        Assert.Throws<InvalidDataException>(() =>
            f.Drones.Order(f.Pilot + 1, [drone], DroneOrder.Engage, f.Npc.Id, new HashSet<long> { f.Npc.Id }, f.Now)
        );
        Assert.Equal(DroneActivity.Idle, f.Drones.List(SystemId, f.Now).Single().Activity);
        f.Drones.Order(f.Pilot, [drone], DroneOrder.Engage, f.Npc.Id, new HashSet<long> { f.Npc.Id }, f.Now);
        Assert.Equal(DroneActivity.Fighting, f.Drones.List(SystemId, f.Now).Single().Activity);
        f.Space(100_000);
        Assert.Throws<InvalidDataException>(() =>
            f.Drones.Order(f.Pilot, [drone], DroneOrder.Engage, f.Npc.Id, new HashSet<long> { f.Npc.Id }, f.Now)
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarpingNpcsRejectNewOrdersAndStopExistingDroneAttacks(bool alreadyEngaged)
    {
        using Fixture f = new();
        var ids = f.Launch(quantity: 1);
        if (alreadyEngaged)
        {
            f.Drones.Order(f.Pilot, ids, DroneOrder.Engage, f.Npc.Id, new HashSet<long> { f.Npc.Id }, f.Now);
        }
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            world.Set<WorldNpc>().Find(f.Npc.Id)!.Navigation = NpcNavigationState.Warping;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() =>
            f.Drones.Order(f.Pilot, ids, DroneOrder.Engage, f.Npc.Id, new HashSet<long> { f.Npc.Id }, f.Now)
        );
        Assert.Empty(f.Tick().Hits);
        Assert.Equal(DroneActivity.Idle, f.Drones.List(SystemId, f.Now).Single().Activity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DroneFlightAndCombatAdvanceOncePerIntervalAndSettleOneKill(bool resident)
    {
        using Fixture f = new();
        using LiveWorld? live = resident ? new(new(f.Base.Db.Path), f.Base.Clock, _ => { }) : null;
        var ids = f.Launch();
        f.Drones.Order(f.Pilot, ids, DroneOrder.Engage, f.Npc.Id, new HashSet<long> { f.Npc.Id }, f.Now);
        var start = f.Drones.List(SystemId, f.Now).First().Position;
        var hits = Enumerable.Range(0, 40).SelectMany(_ => f.Tick().Hits).ToArray();
        Assert.Contains(hits, hit => hit.Applied > 0);
        Assert.Contains(hits, hit => hit.Health.Remaining.Destroyed);
        Assert.Empty(f.Drones.Advance(f.Pilot, f.Now, 40).Hits);
        using WorldDbContext world = new(f.Base.Db.Path);
        Assert.Single(world.Set<NpcBounty>().Where(b => b.NpcId == f.Npc.Id));
        Assert.Single(world.Set<WorldWreck>().Where(w => w.NpcId == f.Npc.Id));
        Assert.NotEqual(start, f.Drones.List(SystemId, f.Now).First().Position);
        Assert.All(f.Drones.List(SystemId, f.Now), d => Assert.Equal(DroneActivity.Idle, d.Activity));
    }

    [Fact]
    public void RecallReturnsSameDamagedIndividualsAndCannotRepeatOrRepairThem()
    {
        using Fixture f = new();
        var ids = f.Launch(quantity: 2);
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            var saved = world.Set<WorldDrone>().Find(ids[0])!;
            saved.ArmorDamage = 20;
            saved.StructureDamage = 10;
            world.SaveChanges();
        }
        f.Drones.Order(f.Pilot, ids, DroneOrder.ReturnToBay, null, new HashSet<long>(), f.Now);
        var changes = f.Tick().Changes;
        Assert.Equal(2, changes.Count);
        Assert.Empty(f.Drones.List(SystemId, f.Now));
        var stored = f.Base.Inventory.List(f.Pilot, f.Ship).Where(i => i.FlagId == (int)InventoryFlag.DroneBay).ToArray();
        Assert.Equal(ids.Order(), stored.Select(i => i.Id).Order());
        Assert.Empty(f.Tick().Changes);
        var health = f.Drones.BayHealth(f.Pilot, f.Ship, ids.ToHashSet())[ids[0]];
        Assert.Equal(20, health.Defense.Armor - health.Remaining.Armor);
        f.Drones.Launch(f.Pilot, [new(ids[0], 1)], f.Now);
        Assert.Equal(
            10,
            f.Drones.List(SystemId, f.Now).Single().Health.Defense.Structure
                - f.Drones.List(SystemId, f.Now).Single().Health.Remaining.Structure
        );
    }

    [Fact]
    public void ReleaseAndRestartLeaveDronesInSpaceAndReconnectPreservesIdentityAndCooldown()
    {
        using Fixture f = new();
        var ids = f.Launch(quantity: 1);
        var before = f.Drones.List(SystemId, f.Now).Single();
        f.Drones.Release(f.Pilot, f.Ship);
        Assert.Equal(0, f.Drones.List(SystemId, f.Now).Single().ShipId);
        WorldInitialization.Initialize(new(f.Base.Db.Path), f.Base.Clock);
        Assert.Empty(f.Tick(100).Hits);
        var saved = f.Drones.List(SystemId, f.Now).Single();
        Assert.Equal(before.Position, saved.Position);
        Assert.Equal(ids[0], saved.Id);
        f.Drones.Order(f.Pilot, ids, DroneOrder.Reconnect, null, new HashSet<long>(), f.Now);
        Assert.Equal(f.Ship, f.Drones.List(SystemId, f.Now).Single().ShipId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AggressiveModeDefendsAgainstNewAttackersWhilePassiveModeWaitsForOrders(bool aggressive)
    {
        using Fixture f = new();
        var ids = f.Launch(quantity: 2);
        f.Drones.ChangeSettings(f.Pilot, aggressive, true);
        using (CatalogDbContext catalog = new(f.Base.CatalogPath, writable: true))
        {
            catalog
                .Set<TypeAttribute>()
                .Where(a => a.ItemTypeId == f.Npc.TypeId && a.AttributeId == (int)DogmaAttributeId.ShieldCapacity)
                .ExecuteUpdate(set => set.SetProperty(a => a.Value, 10_000));
        }
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            world.Set<WorldNpc>().Find(f.Npc.Id)!.ShieldCharge = 10_000;
            world
                .Set<NpcAggression>()
                .Add(
                    new()
                    {
                        NpcId = f.Npc.Id,
                        TargetShipId = f.Ship,
                        LastShotAt = f.Now + 1,
                    }
                );
            world.SaveChanges();
        }
        f.Tick();
        Assert.All(
            f.Drones.List(SystemId, f.Now),
            d =>
            {
                Assert.Equal(aggressive ? DroneActivity.Fighting : DroneActivity.Idle, d.Activity);
                Assert.Equal(aggressive ? f.Npc.Id : f.Ship, d.TargetId);
            }
        );
        f.Drones.Order(f.Pilot, ids, DroneOrder.Orbit, null, new HashSet<long>(), f.Now);
        f.Tick();
        Assert.All(f.Drones.List(SystemId, f.Now), d => Assert.Equal(DroneActivity.Idle, d.Activity));
        WorldInitialization.Initialize(new(f.Base.Db.Path), f.Base.Clock);
        Assert.Equal(new DroneSettings(aggressive, true), f.Drones.Control(f.Pilot).Settings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NpcTurretsCanDamageAndDestroyDronesWithoutDamagingTheirOwnerOrPayingBounties(bool resident)
    {
        using Fixture f = new();
        using LiveWorld? live = resident ? new(new(f.Base.Db.Path), f.Base.Clock, _ => { }) : null;
        var ids = f.Launch(quantity: 1);
        f.Drones.Order(f.Pilot, ids, DroneOrder.Engage, f.Npc.Id, new HashSet<long> { f.Npc.Id }, f.Now);
        using (WorldDbContext world = new(f.Base.Db.Path))
        using (CatalogDbContext catalog = new(f.Base.CatalogPath))
        {
            var defense = DroneCatalog.Skilled(world, catalog, f.Pilot, WarriorTwo).Defense;
            var drone = world.Set<WorldDrone>().Find(ids[0])!;
            drone.Shield = 0;
            drone.ArmorDamage = defense.Armor;
            drone.StructureDamage = defense.Structure - 1;
            world.Set<NpcAggression>().Add(new() { NpcId = f.Npc.Id, TargetShipId = f.Ship });
            world.SaveChanges();
        }
        new WorldDatabase(f.Base.Db.Path).MotionStamps[SystemId] = 0;
        var combat = f.Base.Db.Services.GetRequiredService<INpcCombat>();
        var shot = combat.Fire(f.Pilot, f.Ship, f.Npc.Id, SpacePosition.Zero, f.Now, 0, 0)!;
        Assert.NotNull(shot);
        Assert.Equal(ids[0], shot.Health.ShipId);
        Assert.True(shot.Health.Remaining.Destroyed);
        Assert.Null(shot.Capsule);
        Assert.Equal(
            ids[0],
            f.Base.Db.Services.GetRequiredService<INpcEncounters>().List(SystemId, f.Now).Single(n => n.Id == f.Npc.Id).Weapon!.TargetId
        );
        Assert.Null(combat.Fire(f.Pilot, f.Ship, f.Npc.Id, SpacePosition.Zero, f.Now, 0, 0));
        Assert.Empty(f.Drones.List(SystemId, f.Now));
        Assert.True(f.Drones.WasDestroyed(ids[0]));
        Assert.Empty(f.Tick().Hits);
        using WorldDbContext saved = new(f.Base.Db.Path);
        Assert.Equal((int)InventoryLocation.Graveyard, saved.Set<OwnedItem>().Find(ids[0])!.LocationId);
        Assert.Empty(saved.Set<NpcBounty>());
    }

    [Theory]
    [InlineData("skill")]
    [InlineData("bandwidth")]
    public void LosingControlCapacityReleasesExcessDronesWithoutMovingOrDeletingThem(string change)
    {
        using Fixture f = new();
        var ids = f.Launch(type: 2486);
        if (change == "skill")
        {
            f.Base.Train((int)DroneSkill.Drones, 2);
        }
        else
        {
            using CatalogDbContext catalog = new(f.Base.CatalogPath, writable: true);
            catalog
                .Set<TypeAttribute>()
                .Where(a => a.ItemTypeId == Brutix && a.AttributeId == (int)DogmaAttributeId.DroneBandwidth)
                .ExecuteUpdate(set => set.SetProperty(a => a.Value, 10));
        }
        Assert.True(f.Tick().ControlChanged);
        var contacts = f.Drones.List(SystemId, f.Now);
        Assert.Equal(5, contacts.Count);
        Assert.Equal(2, contacts.Count(d => d.ShipId == f.Ship));
        Assert.Equal(3, contacts.Count(d => d.ShipId == 0));
        Assert.Equal(ids.Order(), contacts.Select(d => d.Id).Order());
        Assert.Throws<InvalidDataException>(() =>
            f.Drones.Order(
                f.Pilot,
                contacts.Where(d => d.ShipId == 0).Select(d => d.Id).ToArray(),
                DroneOrder.Reconnect,
                null,
                new HashSet<long>(),
                f.Now
            )
        );
    }

    [Theory]
    [InlineData("disconnect")]
    [InlineData("warp")]
    [InlineData("loss")]
    public void FlightTransitionsReleaseDronesAtTheirCurrentPosition(string transition)
    {
        using Fixture f = new();
        var ids = f.Launch(quantity: 1);
        var before = f.Drones.List(SystemId, f.Now).Single();
        var pilot = f.Base.Db.Store.FindPilot(1000001)!;
        using FlightSession flight = new(f.Base.Db.Store, pilot, f.Base.Clock, new SharedSpace(), [], drones: f.Drones);
        flight.Snapshot();
        if (transition == "warp")
        {
            Assert.Equal(
                PilotCallResult.None,
                flight.Execute(new FlightCommand.WarpToPoint(1, pilot.Position! + new SpacePosition(1e11, 0, 0), 0))
            );
        }
        else if (transition == "loss")
        {
            flight.BeginShipLoss();
        }
        else
        {
            flight.Dispose();
        }
        var after = f.Drones.List(SystemId, f.Now).Single();
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.Position, after.Position);
        Assert.Equal(0, after.ShipId);
        Assert.Equal(0, after.TargetId);
        Assert.Throws<InvalidDataException>(() => f.Drones.Order(f.Pilot, ids, DroneOrder.ReturnToBay, null, new HashSet<long>(), f.Now));
    }

    [Fact]
    public void DistantDronesTravelHomeBeforeRecallAndAFullBayDoesNotDuplicateOrLoseThem()
    {
        using Fixture f = new();
        var ids = f.Launch(quantity: 1);
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            var drone = world.Set<WorldDrone>().Find(ids[0])!;
            drone.X += 15_000;
            world.SaveChanges();
        }
        f.Drones.Order(f.Pilot, ids, DroneOrder.ReturnToBay, null, new HashSet<long>(), f.Now);
        Assert.Empty(f.Tick().Changes);
        Assert.Single(f.Drones.List(SystemId, f.Now));
        Assert.Contains(
            Enumerable.Range(0, 30).SelectMany(_ => f.Tick().Changes),
            c => c.Item.Id == ids[0] && c.Item.LocationId == f.Ship && c.Item.FlagId == (int)InventoryFlag.DroneBay
        );
        f.Drones.Launch(f.Pilot, [new(ids[0], 1)], f.Now);
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            world
                .Set<OwnedItem>()
                .Add(
                    new()
                    {
                        Id = InventoryPersistence.NextId(world),
                        OwnerId = f.Pilot,
                        TypeId = WarriorTwo,
                        Quantity = 15,
                        LocationId = f.Ship,
                        FlagId = (int)InventoryFlag.DroneBay,
                    }
                );
            world.SaveChanges();
        }
        // Fill the bay, then verify a returning drone stays in space.
        f.Drones.Order(f.Pilot, ids, DroneOrder.ReturnToBay, null, new HashSet<long>(), f.Now);
        Assert.Empty(f.Tick().Changes);
        Assert.Single(f.Drones.List(SystemId, f.Now));
        Assert.Equal(DroneActivity.Idle, f.Drones.List(SystemId, f.Now).Single().Activity);
    }

    [Fact]
    public void MigrationFromPreviousVersionCreatesDroneStateWithoutGrantingOrChangingInventory()
    {
        using Fixture f = new();
        long stack = f.Bay(WarriorTwo, 5);
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "DROP TABLE WorldDrone; DROP TABLE WorldDroneSettings; ALTER TABLE WorldNpcAggression DROP COLUMN LastTargetId; "
                    + "PRAGMA user_version=84;"
            );
        }
        WorldInitialization.Initialize(new(f.Base.Db.Path), f.Base.Clock);
        using WorldDbContext saved = new(f.Base.Db.Path);
        Assert.Equal(5, saved.Set<OwnedItem>().Find(stack)!.Quantity);
        Assert.Empty(saved.Set<WorldDrone>());
        Assert.Empty(saved.Set<WorldDroneSettings>());
        Assert.Equal(WorldMigration.CurrentVersion, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Fact]
    public async Task ConcurrentLaunchRequestsCannotExceedTheControlLimitOrDuplicateItems()
    {
        using Fixture f = new();
        long stack = f.Bay(WarriorTwo, 10);
        f.Space();
        var outcomes = await Task.WhenAll(
            Enumerable
                .Range(0, 2)
                .Select(_ =>
                    Task.Run(() =>
                    {
                        try
                        {
                            f.Drones.Launch(f.Pilot, [new(stack, 5)], f.Now);
                            return true;
                        }
                        catch (InvalidDataException)
                        {
                            return false;
                        }
                    })
                )
        );
        Assert.Single(outcomes, success => success);
        Assert.Equal(5, f.Drones.List(SystemId, f.Now).Count);
        Assert.Equal(5, f.Base.Inventory.List(f.Pilot, f.Ship).Single(i => i.Id == stack).Quantity);
    }

    [Fact]
    public void NativeLaunchBindingDroneStatesAndObserverSnapshotsCarryActualDrones()
    {
        using Fixture f = new();
        long stack = f.Bay(WarriorTwo, 5);
        f.Space();
        using var scope = f.Base.Db.Services.CreateScope();
        var pilot = f.Base.Db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create,
            77,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        Assert.IsType<DogmaItemInfo<double>>(
            Assert.IsType<PilotCallResult.Success>(router.Call(context, "dogmaIM", "ItemGetInfo", new object?[] { stack }, _ => { })).Reply
        );
        var result = router.Call(
            context,
            "ship",
            "LaunchDrones",
            new object?[]
            {
                new List<object?> { new object?[] { stack, 5L } },
                null,
                false,
            },
            _ => { }
        );
        var launch = Assert.IsType<DroneLaunchReply>(Assert.IsType<PilotCallResult.Success>(result).Reply);
        Assert.Equal(5, launch.Launches.Single().DroneIds.Count);
        foreach (long id in launch.Launches.Single().DroneIds)
        {
            var info = Assert.IsType<DogmaItemInfo<double>>(
                Assert.IsType<PilotCallResult.Success>(router.Call(context, "dogmaIM", "ItemGetInfo", new object?[] { id }, _ => { })).Reply
            );
            Assert.True(info.Attributes[DogmaAttributeId.StructureHitPoints] > 0);
        }
        Assert.Contains(context.Flight.Updates, u => u.Command is DestinyCommand.DroneStateChanged);
        Assert.Equal(5, context.Flight.VisibleDrones.Count);
        var bound = router.Call(
            context,
            "entity",
            "MachoBindObject",
            new object?[]
            {
                (long)SystemId,
                new object?[] { "CmdReturnHome", new object?[] { launch.Launches.Single().DroneIds.Select(i => (object?)i).ToList() } },
            },
            _ => { }
        );
        Assert.IsType<PilotCallResult.Success>(bound);
        context.Flight.Updates.Clear();
        context.Flight.Snapshot();
        var snapshot = Assert.IsType<DestinyCommand.SetState>(
            context.Flight.Updates.Single(u => u.Command is DestinyCommand.SetState).Command
        );
        var wire = Assert.IsType<BlueInstance>(snapshot.Snapshot.ToBlue());
        Assert.NotNull(wire);
        Assert.Equal(5, snapshot.Snapshot.Drones!.Count);
        Assert.Equal(5, snapshot.Snapshot.Slims.OfType<DroneSlimItem>().Count());
        Assert.All(
            snapshot.Snapshot.Drones,
            state =>
            {
                Assert.IsType<BlueInstance>(state.ToBlue());
                Assert.Equal(7, state.Arguments.Length);
            }
        );
        Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(snapshot.Snapshot)));
        Assert.IsType<object?[]>(BlueMarshal.Decode(BlueMarshal.Encode(context.Flight.Updates.First())));
        var controls = f.Drones.Control(f.Pilot);
        Assert.Equal(5, controls.MaximumDrones);
        Assert.Equal(25, controls.UsedBandwidth);
        var targeting = scope.ServiceProvider.GetRequiredService<TargetingHandler>();
        targeting.CallWire(context, "dogmaIM", "AddTarget", [f.Npc.Id]);
        f.Base.Clock.Seconds += 30;
        targeting.Advance(context);
        Assert.True(targeting.IsLocked(f.Npc.Id));
        var engage = router.Call(
            context,
            "entity",
            "CmdEngage",
            new object?[] { launch.Launches.Single().DroneIds.Select(i => (object?)i).ToList(), f.Npc.Id },
            _ => { }
        );
        Assert.IsType<DroneCommandReply>(Assert.IsType<PilotCallResult.Success>(engage).Reply);
        var wing = f.Base.Db.Store.FindPilot(1000002)!;
        f.Base.Db.Store.SetPosition(wing.ShipId, pilot.Position! + new SpacePosition(100, 0, 0), SystemId);
        wing = f.Base.Db.Store.FindPilot(1000002)!;
        using FlightSession observer = new(f.Base.Db.Store, wing, f.Base.Clock, new SharedSpace(), [], drones: f.Drones);
        observer.Snapshot();
        Assert.Equal(5, observer.VisibleDrones.Count);
        context.NotificationsBeforeFlight.Clear();
        var handler = scope.ServiceProvider.GetRequiredService<DroneHandler>();
        for (int tick = 0; tick < 6; tick++)
        {
            f.Base.Clock.Seconds++;
            handler.Advance(context);
            observer.RefreshDrones();
        }
        Assert.Contains(context.NotificationsBeforeFlight, n => n.Name == "OnDamageMessage");
        Assert.Contains(observer.Updates, u => u.Command is DestinyCommand.TimedSpecialEffect { Graphic: "effects.Laser" });
    }
}
