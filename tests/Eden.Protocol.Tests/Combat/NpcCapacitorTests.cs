using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Drones;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Protocol.Combat;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class CapacitorWarfareTests
{
    private const long EnergyNpcId = 9_800_000_001;
    private const int EnergyNpcType = 11901;

    private static void NpcSpace(Fixture f, double source = 100)
    {
        PassiveCatalogFixture.LoadNpcPopulation(f.CatalogPath);
        Space(f, source);
        var position = f.Db.Store.FindPilot(1000001)!.Position!;
        using WorldDbContext world = new(f.Db.Path);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var values = catalog.FittingAttributes(EnergyNpcType);
        world.Add(
            new WorldNpc
            {
                Id = EnergyNpcId,
                TypeId = EnergyNpcType,
                SystemId = 30000142,
                X = position.X + 1000,
                Y = position.Y,
                Z = position.Z,
                HomeX = position.X + 1000,
                HomeY = position.Y,
                HomeZ = position.Z,
                ShieldCharge = values.GetValueOrDefault(DogmaAttributeId.ShieldCapacity),
                UpdatedAt = f.Clock.Now,
                Role = NpcRole.Pirate,
            }
        );
        world.SaveChanges();
    }

    private static CapacitorActivation ActivateNpc(Fixture f, long module, DogmaEffect effect) =>
        Store(f).Activate(f.PilotId, module, effect, EnergyNpcId, new HashSet<long> { EnergyNpcId }, f.Clock.Now);

    [Theory]
    [InlineData(530, DogmaEffect.EnergyNosferatu, 108, 692)]
    [InlineData(533, DogmaEffect.EnergyNeutralizer, 66.25, 655)]
    public void NpcDrainsCommitBothPoolsAndPublishHostilityWithoutPlayerTimersOrDamageContribution(
        int type,
        DogmaEffect effect,
        double source,
        double target
    )
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        NpcSpace(f);
        var result = ActivateNpc(f, module, effect);
        Assert.Equal(TackleTargetKind.Npc, result.Cycle.TargetKind);
        Assert.Equal(source, result.Capacitor, 7);
        Assert.Equal(target, result.TargetCapacitor, 7);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(source, world.Set<ShipEnergy>().Find(f.ShipId)!.Charge, 7);
        Assert.Equal(target, world.Set<WorldNpcEnergy>().Find(EnergyNpcId)!.Charge, 7);
        Assert.Equal(f.Clock.Now, world.Set<WorldNpcEnergy>().Find(EnergyNpcId)!.UpdatedAt);
        Assert.NotNull(world.Set<NpcAggression>().Find(EnergyNpcId));
        var timers = world.Set<SavedCrimewatch>().Find(f.PilotId)!;
        Assert.True(timers.NpcUntil > f.Clock.Now);
        Assert.Equal(0, timers.PvpUntil);
        Assert.Equal(0, timers.WeaponsUntil);
        Assert.Empty(world.Set<SavedPilotHostility>());
        Assert.Empty(world.Set<SavedCombatDamage>());
        var trigger = Assert.Single(world.Set<WorldDroneCombatTriggers>());
        Assert.Equal(f.ShipId, trigger.ShipId);
        Assert.Throws<InvalidDataException>(() => ActivateNpc(f, module, effect));
    }

    [Theory]
    [InlineData("missing-lock")]
    [InlineData("destroyed")]
    [InlineData("retired")]
    [InlineData("warping")]
    [InlineData("different-system")]
    [InlineData("range")]
    [InlineData("capacitor")]
    [InlineData("unverified-recharge")]
    [InlineData("offline")]
    [InlineData("transfer")]
    public void InvalidNpcTargetsAndUnpaidPulsesPreserveBothResourcesAndEveryCombatSideEffect(string rejection)
    {
        using Fixture f = Prepare();
        long module = f.Fit(rejection == "transfer" ? 529 : 533);
        NpcSpace(f);
        if (rejection == "unverified-recharge")
        {
            using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
            catalog
                .Set<Eden.Data.Catalog.TypeAttribute>()
                .Where(a => a.ItemTypeId == EnergyNpcType && a.AttributeId == (int)DogmaAttributeId.CapacitorRechargeTime)
                .ExecuteUpdate(set => set.SetProperty(a => a.Value, 0));
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            var npc = world.Set<WorldNpc>().Find(EnergyNpcId)!;
            switch (rejection)
            {
                case "destroyed":
                    npc.DestroyedAt = f.Clock.Now;
                    break;
                case "retired":
                    npc.RetiredAt = f.Clock.Now;
                    break;
                case "warping":
                    npc.Navigation = NpcNavigationState.Warping;
                    break;
                case "different-system":
                    npc.SystemId = 30000144;
                    break;
                case "range":
                    npc.X += 100000;
                    break;
                case "capacitor":
                    world.Set<ShipEnergy>().Find(f.ShipId)!.Charge = 0;
                    break;
                case "offline":
                    world.Set<OwnedItem>().Find(module)!.Online = false;
                    break;
            }
            world.SaveChanges();
        }
        var effect = rejection == "transfer" ? DogmaEffect.RemoteCapacitorTransfer : DogmaEffect.EnergyNeutralizer;
        List<CapacitorChanged> published = [];
        f.Db.Services.GetRequiredService<CapacitorSignal>().Changed += published.Add;
        Assert.Throws<InvalidDataException>(() =>
            Store(f)
                .Activate(
                    f.PilotId,
                    module,
                    effect,
                    EnergyNpcId,
                    rejection == "missing-lock" ? [] : new HashSet<long> { EnergyNpcId },
                    f.Clock.Now
                )
        );
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Equal(rejection == "capacitor" ? 0 : 100, verify.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
        Assert.Empty(verify.Set<WorldNpcEnergy>());
        Assert.Null(verify.Set<ModuleCooldown>().Find(module));
        Assert.Empty(verify.Set<SavedCrimewatch>());
        Assert.Empty(verify.Set<NpcAggression>());
        Assert.Empty(verify.Set<WorldDroneCombatTriggers>());
        Assert.Empty(published);
    }

    [Fact]
    public void NpcNosferatuObeysAbsoluteGJAndCannotCreateEnergyFromAnEmptyPool()
    {
        using Fixture f = Prepare();
        long module = f.Fit(530);
        NpcSpace(f, source: 1000);
        var result = ActivateNpc(f, module, DogmaEffect.EnergyNosferatu);
        Assert.Equal(0, result.Applied);
        Assert.Equal(1000, result.Capacitor);
        Assert.Equal(700, result.TargetCapacitor);
        f.Clock.Milliseconds += (long)result.Cycle.Module.DurationMilliseconds;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<WorldNpcEnergy>().Find(EnergyNpcId)!.Charge = 0;
            world.Set<WorldNpcEnergy>().Find(EnergyNpcId)!.UpdatedAt = f.Clock.Now;
            world.Set<ShipEnergy>().Find(f.ShipId)!.Charge = 0;
            world.Set<ShipEnergy>().Find(f.ShipId)!.UpdatedAt = f.Clock.Now;
            world.SaveChanges();
        }
        // Both pools were depleted at this cycle boundary; no elapsed regeneration can supply a drain.
        var empty = ActivateNpc(f, module, DogmaEffect.EnergyNosferatu);
        Assert.Equal(0, empty.Applied);
    }

    [Fact]
    public async Task NpcCapacitorRechargesPersistsThroughCheckpointAndCannotReplayAfterRestart()
    {
        using Fixture f = Prepare();
        long module = f.Fit(533);
        NpcSpace(f);
        CapacitorActivation result;
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            _ = f.Db.Services.GetRequiredService<INpcEncounters>().List(30000142, f.Clock.Now);
            result = ActivateNpc(f, module, DogmaEffect.EnergyNeutralizer);
            f.Clock.Seconds += 6;
            result = ActivateNpc(f, module, DogmaEffect.EnergyNeutralizer);
            using CatalogDbContext catalog = new(f.CatalogPath);
            var profile = NpcCapacitorRules.Profile(catalog.FittingAttributes(EnergyNpcType));
            double expected =
                NpcCapacitorRules.Read(profile, new(655, result.Cycle.StartedAt - 6 * TimeSpan.TicksPerSecond), f.Clock.Now).Charge - 45;
            Assert.Equal(expected, result.TargetCapacitor, 7);
            await live.FlushAsync();
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using WorldDbContext reopened = new(f.Db.Path);
        Assert.Equal(result.TargetCapacitor, reopened.Set<WorldNpcEnergy>().Find(EnergyNpcId)!.Charge, 7);
        Assert.Equal(result.Capacitor, reopened.Set<ShipEnergy>().Find(f.ShipId)!.Charge, 7);
        Assert.Equal(result.Cycle.ExpiresAt, reopened.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        Assert.Throws<InvalidDataException>(() => ActivateNpc(f, module, DogmaEffect.EnergyNeutralizer));
    }

    [Fact]
    public void PreviousSaveUpgradeIsIdempotentAndPreservesNpcIdentityAndSourceInventory()
    {
        using Fixture f = Prepare();
        long module = f.Fit(533);
        NpcSpace(f);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TABLE WorldNpcEnergy; PRAGMA user_version=102;");
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Equal(EnergyNpcType, verify.Set<WorldNpc>().Find(EnergyNpcId)!.TypeId);
        Assert.Equal(f.PilotId, verify.Set<OwnedItem>().Find(module)!.OwnerId);
        Assert.Empty(verify.Set<WorldNpcEnergy>());
        Assert.Equal(WorldMigration.CurrentVersion, verify.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Theory]
    [InlineData(40665, DogmaEffect.EnergyNosferatu, 100, 16.1875)]
    [InlineData(40659, DogmaEffect.EnergyNeutralizer, 4000, 83.25)]
    public void CapitalDrainSignatureResolutionUsesTheActualNpcSignature(int type, DogmaEffect effect, double source, double amount)
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        NpcSpace(f, source);
        var result = ActivateNpc(f, module, effect);
        Assert.Equal(amount, result.Applied, 7);
        Assert.Equal(700 - amount, result.TargetCapacitor, 7);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(.5, 22.5)]
    [InlineData(1, 45)]
    public void NpcResistanceChangesTheRealPoolDrain(double resistance, double amount)
    {
        using Fixture f = Prepare();
        long module = f.Fit(533);
        NpcSpace(f);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            int attribute = (int)DogmaAttributeId.EnergyWarfareResistance;
            catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO DogmaAttribute VALUES ({attribute},{attribute.ToString()})");
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES ({EnergyNpcType},{attribute},{resistance})");
        }
        var result = ActivateNpc(f, module, DogmaEffect.EnergyNeutralizer);
        Assert.Equal(amount, result.Applied, 7);
        Assert.Equal(700 - amount, result.TargetCapacitor, 7);
    }

    [Fact]
    public void NosferatuChangesNeitherTheNpcRepairProfileNorItsExistingRepairDeadlines()
    {
        using Fixture f = Prepare();
        long module = f.Fit(530);
        NpcSpace(f);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var before = NpcRepairRules.Profile(catalog.FittingAttributes(EnergyNpcType));
        ActivateNpc(f, module, DogmaEffect.EnergyNosferatu);
        var after = NpcRepairRules.Profile(catalog.FittingAttributes(EnergyNpcType));
        Assert.Equal(before, after);
        using WorldDbContext world = new(f.Db.Path);
        var npc = world.Set<WorldNpc>().Find(EnergyNpcId)!;
        Assert.Equal(0, npc.ShieldRepairAt);
        Assert.Equal(0, npc.ArmorRepairAt);
        Assert.Equal(0, npc.RepairRevision);
    }

    [Fact]
    public void OrdinarySpawnProfilesHaveVerifiedNominalCapacitorsAndPoolRowsCascadeWithNpcDeletion()
    {
        using TestWorld db = new();
        RegionalNpcTests.Prepare(db);
        var profiles = db.Services.GetRequiredService<NpcSpawnCatalog>().Profiles;
        Assert.NotEmpty(profiles);
        Assert.All(profiles, profile => _ = NpcCapacitorRules.Profile(profile.Attributes));
        using Fixture f = Prepare();
        long module = f.Fit(533);
        NpcSpace(f);
        ActivateNpc(f, module, DogmaEffect.EnergyNeutralizer);
        using WorldDbContext world = new(f.Db.Path);
        world.Set<NpcAggression>().Where(a => a.NpcId == EnergyNpcId).ExecuteDelete();
        world.Set<WorldNpc>().Where(n => n.Id == EnergyNpcId).ExecuteDelete();
        Assert.Empty(world.Set<WorldNpcEnergy>());
    }

    [Fact]
    public void SameTickModulesConsumeTheSameNpcPoolAndFreshNpcIdentitiesStartWithTheirOwnCharge()
    {
        using Fixture f = Prepare();
        long first = f.Fit(530);
        long second = f.Fit(530);
        long third = f.Fit(530);
        NpcSpace(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var a = ActivateNpc(f, first, DogmaEffect.EnergyNosferatu);
        var b = ActivateNpc(f, second, DogmaEffect.EnergyNosferatu);
        Assert.Equal(a.TargetCapacitor - 8, b.TargetCapacitor, 7);
        Assert.Equal(a.Capacitor + 8, b.Capacitor, 7);
        using WorldDbContext world = new(f.Db.Path);
        var old = world.Set<WorldNpc>().Find(EnergyNpcId)!;
        old.DestroyedAt = f.Clock.Now;
        world.Add(
            new WorldNpc
            {
                Id = EnergyNpcId + 1,
                TypeId = EnergyNpcType,
                SystemId = old.SystemId,
                X = old.X,
                Y = old.Y,
                Z = old.Z,
                HomeX = old.HomeX,
                HomeY = old.HomeY,
                HomeZ = old.HomeZ,
                ShieldCharge = old.ShieldCharge,
                UpdatedAt = f.Clock.Now,
            }
        );
        world.SaveChanges();
        Assert.Null(world.Set<WorldNpcEnergy>().Find(EnergyNpcId + 1));
        Assert.Equal(684, world.Set<WorldNpcEnergy>().Find(EnergyNpcId)!.Charge, 7);
        var fresh = Store(f)
            .Activate(f.PilotId, third, DogmaEffect.EnergyNosferatu, EnergyNpcId + 1, new HashSet<long> { EnergyNpcId + 1 }, f.Clock.Now);
        Assert.Equal(692, fresh.TargetCapacitor, 7);
    }

    [Theory]
    [InlineData(0, 45)]
    [InlineData(1, 22.5)]
    [InlineData(2, 2.8125)]
    public void NpcDrainFalloffUsesCurrentEdgeDistanceRatherThanCenterDistance(int falloffs, double amount)
    {
        using Fixture f = Prepare();
        long module = f.Fit(533);
        NpcSpace(f);
        var source = f.Db.Store.FindPilot(1000001)!;
        using CatalogDbContext catalog = new(f.CatalogPath);
        var raw = catalog.FittingAttributes(533);
        double radii = (catalog.FittingType(source.ShipTypeId)!.Radius ?? 0) + (catalog.FittingType(EnergyNpcType)!.Radius ?? 0);
        double edge = raw[DogmaAttributeId.MaxRange] + falloffs * raw[DogmaAttributeId.FalloffEffectiveness];
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<WorldNpc>().Find(EnergyNpcId)!.X = source.Position!.X + edge + radii;
            world.SaveChanges();
        }
        Assert.Equal(amount, ActivateNpc(f, module, DogmaEffect.EnergyNeutralizer).Applied, 7);
    }
}
