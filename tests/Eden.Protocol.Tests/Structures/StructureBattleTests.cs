using Eden.Data.World.Structures;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Flight;
using Eden.Protocol.Serialization;
using Eden.Protocol.Structures;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Eden.World.Structures;
using Fixture = Eden.Protocol.Tests.Structures.StructureTests.Fixture;

namespace Eden.Protocol.Tests.Structures;

public sealed class StructureBattleTests
{
    [Fact]
    public async Task ResidentDamageRollsBackAndCheckpointReopenRetainsTheSharedCapAndRepairDeadline()
    {
        using Fixture f = new(liveWorld: true);
        var structure = f.Online();
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        var profile = StructureBattlePersistence.Profile(catalog, structure.Profile.TypeId);
        double original = profile.Defense.Shield;
        using (WorldActionScope rollback = new(new(f.Db.Path), new(systems: [Fixture.SystemId])))
        {
            StructureBattlePersistence.Apply(
                rollback.World,
                catalog,
                rollback.World.Set<SavedStructure>().Find(structure.Id)!,
                new(100, 0, 0, 0),
                f.Clock.Now
            );
        }
        Assert.Equal(original, f.Structures.Read(f.PilotId, structure.Id)!.Battle!.Health.Shield);
        var hit = Damage(f, structure.Id, new(100, 0, 0, 0));
        Assert.True(hit.Applied > 0);
        var snapshot = f.Structures.Read(f.PilotId, structure.Id)!;
        Assert.Equal(hit.Battle, snapshot.Battle);
        Assert.NotNull(snapshot.TimerEnd);
        var native = Assert.IsType<BlueNewObject>(new StructureHangarReply(snapshot).ToBlue());
        var state = Assert.IsType<Dictionary<string, object?>>(native.State);
        Assert.Equal(snapshot.Battle!.RepairAt, state["_timerEndAt"]);
        var fractions = Assert.IsType<object?[]>(state["_damageState"]);
        Assert.Equal(snapshot.DamageFractions!.Shield, fractions[0]);
        var addition = Ballpark.AddObjects(Contacts(f), 0);
        Assert.Equal(snapshot.DamageFractions.Shield, addition.Damage[structure.Id].ShieldFraction);
        _ = Contacts(f);
        using (var diagnostics = SessionPerformance.Begin("warm damaged structures", _ => { }, force: true)!)
        {
            Assert.Same(Contacts(f), Contacts(f));
            Assert.Equal(0, diagnostics.Commands);
            Assert.Equal(0, diagnostics.Connections);
            Assert.Equal(0, diagnostics.FittingCalculations);
        }
        await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        string copy = Path.Combine(Path.GetDirectoryName(f.Db.Path)!, "structure-battle.sqlite");
        File.Copy(f.Db.Path, copy);
        using LiveWorld reopened = new(new(copy), f.Clock);
        using (WorldActionScope read = new(new(copy), new(systems: [Fixture.SystemId])))
        {
            var row = read.World.Set<SavedStructure>().Find(structure.Id)!;
            Assert.Equal(hit.Battle, StructureBattlePersistence.Read(read.World, catalog, row));
            var next = StructureBattlePersistence.Apply(read.World, catalog, row, new(100, 0, 0, 0), f.Clock.Now);
            Assert.True(next.Battle.WindowDamage.Shield > hit.Battle.WindowDamage.Shield);
            read.Commit();
        }
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(hit.Battle.RepairAt!.Value - f.Clock.Now));
        var repaired = f.Structures.Read(f.PilotId, structure.Id)!;
        Assert.Equal(original, repaired.Battle!.Health.Shield);
        Assert.Null(repaired.Battle.RepairAt);
        Assert.Equal(StructureState.ShieldVulnerable, repaired.State);
    }

    [Fact]
    public void ResidentReinforcementExitsAtTheSavedDeadline()
    {
        using Fixture f = new(liveWorld: true);
        var structure = f.Online();
        f.Structures.DepositFuel(f.PilotId, structure.Id, f.Item(4051, 5000));
        f.Structures.FitService(f.PilotId, structure.Id, f.Item(35899), StructureRules.FirstServiceSlot);
        long service = Assert.Single(f.Structures.Read(f.PilotId, structure.Id)!.Modules).ItemId;
        f.Structures.SetServiceOnline(f.PilotId, structure.Id, service, true);
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        var profile = StructureBattlePersistence.Profile(catalog, structure.Profile.TypeId);
        using (WorldActionScope prepare = new(new(f.Db.Path), new(systems: [Fixture.SystemId])))
        {
            prepare.World.Add(
                new SavedStructureBattle
                {
                    StructureId = structure.Id,
                    Shield = 1,
                    Armor = profile.Defense.Armor,
                    Hull = profile.Defense.Structure,
                }
            );
            prepare.Commit();
        }
        var reinforced = Damage(f, structure.Id, new(100, 0, 0, 0));
        Assert.Equal(StructureState.ArmorReinforced, reinforced.Battle.State);
        long exit = reinforced.Battle.EndsAt!.Value;
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(exit - f.Clock.Now - 1));
        Assert.Equal(StructureState.ArmorReinforced, f.Structures.Read(f.PilotId, structure.Id)!.State);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        var vulnerable = f.Structures.Read(f.PilotId, structure.Id)!;
        Assert.Equal(StructureState.ArmorVulnerable, vulnerable.State);
        Assert.Equal(exit + StructureRules.RepairDuration, vulnerable.Battle!.RepairAt);
        Assert.True(Assert.Single(vulnerable.Modules).Online);
        using (WorldActionScope prepareHull = new(new(f.Db.Path), new(systems: [Fixture.SystemId])))
        {
            prepareHull.World.Set<SavedStructureBattle>().Find(structure.Id)!.Armor = 1;
            prepareHull.Commit();
        }
        var hull = Damage(f, structure.Id, new(100, 0, 0, 0));
        Assert.Equal(StructureState.HullVulnerable, hull.Battle.State);
        var damaged = f.Structures.Read(f.PilotId, structure.Id)!;
        Assert.False(Assert.Single(damaged.Modules).Online);
        Assert.DoesNotContain(StructureService.Reprocessing, damaged.Services);
        f.Clock.AdvanceWallTime(TimeSpan.FromMinutes(31));
        var repaired = f.Structures.Read(f.PilotId, structure.Id)!;
        Assert.Equal(StructureState.ShieldVulnerable, repaired.State);
        Assert.False(Assert.Single(repaired.Modules).Online);
    }

    [Fact]
    public void AbandonmentBecomesVisibleAtItsExactDeadlineAndClearsWhenAServiceIsFueled()
    {
        using Fixture f = new(liveWorld: true);
        var structure = f.Online();
        long low = structure.LowPowerAt!.Value;
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(low + StructureRules.AbandonmentDuration - f.Clock.Now - 1));
        Assert.Equal(StructureUpkeepState.LowPower, Assert.Single(Contacts(f)).Structure!.UpkeepState);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.Equal(StructureUpkeepState.Abandoned, Assert.Single(Contacts(f)).Structure!.UpkeepState);
        Assert.Equal(StructureUpkeepState.Abandoned, f.Structures.Read(f.PilotId, structure.Id)!.Upkeep);
        f.Structures.DepositFuel(f.PilotId, structure.Id, f.Item(4051, 1000));
        f.Structures.FitService(f.PilotId, structure.Id, f.Item(35899), StructureRules.FirstServiceSlot);
        structure = f.Structures.Read(f.PilotId, structure.Id)!;
        f.Structures.SetServiceOnline(f.PilotId, structure.Id, Assert.Single(structure.Modules).ItemId, true);
        Assert.Equal(StructureUpkeepState.FullPower, Assert.Single(Contacts(f)).Structure!.UpkeepState);
    }

    private static IReadOnlyList<Eden.Gameplay.Flight.StaticSpaceObject> Contacts(Fixture f) =>
        ((IStructureContacts)f.Structures).Contacts(Fixture.SystemId);

    private static StructureHit Damage(Fixture f, long structureId, DamageVector damage)
    {
        using WorldActionScope action = new(new(f.Db.Path), new(systems: [Fixture.SystemId]));
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        var result = StructureBattlePersistence.Apply(
            action.World,
            catalog,
            action.World.Set<SavedStructure>().Find(structureId)!,
            damage,
            f.Clock.Now
        );
        action.Commit();
        return result;
    }
}
