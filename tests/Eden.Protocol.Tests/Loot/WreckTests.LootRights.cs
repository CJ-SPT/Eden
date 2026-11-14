using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Loot;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Loot;

public sealed partial class WreckTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignLootIsReadableButTakingRequiresSafetyAndCommitsOnlyASuspectTimer(bool liveWorld)
    {
        using Fixture f = new(liveWorld: liveWorld);
        var thief = f.Db.Store.FindPilot(1000002)!;
        f.Db.Store.SetPosition(thief.ShipId, f.Wreck.Position, f.Wreck.SystemId);
        long id = f.Item;
        Assert.Equal(id, Assert.Single(f.Store.Read(thief.PilotId, f.Wreck.Id, Now).Items).Id);
        SetSafety(f, thief.PilotId, ShipSafetyLevel.Full);
        Assert.Throws<InvalidDataException>(() => f.Store.Take(thief.PilotId, thief.ShipId, f.Wreck.Id, [id], null, Now));
        SetSafety(f, thief.PilotId, ShipSafetyLevel.Partial);
        Assert.Throws<InvalidDataException>(() => f.Store.Take(thief.PilotId, thief.ShipId, f.Wreck.Id, [id], 101, Now));
        using (WorldDbContext before = new(f.Db.Path))
        {
            Assert.False(before.Set<SavedCrimewatch>().Find(thief.PilotId)?.Timers().Suspect.Running(Now) == true);
            Assert.Equal(f.Pilot.PilotId, before.Set<OwnedItem>().Find(id)!.OwnerId);
        }
        f.Store.Take(thief.PilotId, thief.ShipId, f.Wreck.Id, [id], 25, Now);
        var timers = f.Db.Services.GetRequiredService<IPilotCrimewatch>().Read(thief.PilotId, f.Wreck.SystemId).Timers;
        Assert.Equal(Now + CrimewatchRules.FlagDuration.Ticks, timers.Suspect.ExpiresAt);
        Assert.False(timers.Weapons.Running(Now));
        Assert.False(timers.Pvp.Running(Now));
        Assert.False(timers.Criminal.Running(Now));
        Assert.Equal(75, Assert.Single(f.Store.Read(f.Pilot.PilotId, f.Wreck.Id, Now).Items).Quantity);
        long later = Now + TimeSpan.TicksPerMinute;
        f.Store.Take(thief.PilotId, thief.ShipId, f.Wreck.Id, [id], null, later);
        Assert.Throws<InvalidDataException>(() => f.Store.Take(thief.PilotId, thief.ShipId, f.Wreck.Id, [id], null, later + 1));
        if (liveWorld)
        {
            await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        }
        else
        {
            WorldInitialization.Open(f.Db.Path);
        }
        using WorldDbContext saved = new(WorldConnections.FileConnection(f.Db.Path));
        Assert.Equal(later + CrimewatchRules.FlagDuration.Ticks, saved.Set<SavedCrimewatch>().Find(thief.PilotId)!.SuspectUntil);
        Assert.Equal(
            100,
            saved
                .Set<OwnedItem>()
                .Where(i => i.TypeId == WreckDefinitions.StarterLootType && i.OwnerId == thief.PilotId)
                .Sum(i => i.Quantity)
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbandonmentChangesOnlyOwnedLootAndIsPublicAndPersistent(bool liveWorld)
    {
        using Fixture f = new(liveWorld: liveWorld);
        var rights = f.Db.Services.GetRequiredService<ISpaceLootRights>();
        var other = f.Db.Store.FindPilot(1000002)!;
        f.Db.Store.SetPosition(other.ShipId, f.Wreck.Position, f.Wreck.SystemId);
        long id = f.Item;
        rights.Abandon(other.PilotId, [f.Wreck.Id], Now);
        Assert.False(Assert.Single(f.Store.List(f.Wreck.SystemId, Now)).Abandoned);
        rights.Abandon(f.Pilot.PilotId, [f.Wreck.Id, long.MaxValue], Now);
        rights.Abandon(f.Pilot.PilotId, [f.Wreck.Id], Now);
        Assert.True(Assert.Single(f.Store.List(f.Wreck.SystemId, Now)).Abandoned);
        if (liveWorld)
        {
            await LiveWorld.Find(f.Db.Path)!.FlushAsync();
            using WorldDbContext disk = new(WorldConnections.FileConnection(f.Db.Path));
            Assert.True(disk.Set<WorldWreck>().Find(f.Wreck.Id)!.Abandoned);
        }
        Assert.Single(f.Store.Take(other.PilotId, other.ShipId, f.Wreck.Id, [id], null, Now));
        Assert.False(
            f.Db.Services.GetRequiredService<IPilotCrimewatch>().Read(other.PilotId, f.Wreck.SystemId).Timers.Suspect.Running(Now)
        );
        using WorldDbContext saved = new(f.Db.Path);
        Assert.True(saved.Set<WorldWreck>().Find(f.Wreck.Id)!.Abandoned);
        Assert.Equal(other.PilotId, saved.Set<OwnedItem>().Find(id)!.OwnerId);
        if (!liveWorld)
        {
            WorldInitialization.Open(f.Db.Path);
            Assert.True(Assert.Single(f.Store.List(f.Wreck.SystemId, Now)).Abandoned);
        }
    }

    [Fact]
    public void RejectedLateInventoryWriteRollsBackTheftAndOwnership()
    {
        using Fixture f = new(liveWorld: false);
        var thief = f.Db.Store.FindPilot(1000002)!;
        f.Db.Store.SetPosition(thief.ShipId, f.Wreck.Position, f.Wreck.SystemId);
        SetSafety(f, thief.PilotId, ShipSafetyLevel.Partial);
        long id = f.Item;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectTheft BEFORE UPDATE OF OwnerId ON WorldItem BEGIN SELECT RAISE(ABORT,'loot failed'); END;"
            );
        }
        Assert.Throws<DbUpdateException>(() => f.Store.Take(thief.PilotId, thief.ShipId, f.Wreck.Id, [id], null, Now));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(f.Pilot.PilotId, saved.Set<OwnedItem>().Find(id)!.OwnerId);
        Assert.False(saved.Set<SavedCrimewatch>().Find(thief.PilotId)?.Timers().Suspect.Running(Now) == true);
    }

    [Fact]
    public void AbandonmentSkipsRemoteAndExpiredEntriesAndRejectsMalformedSelections()
    {
        using Fixture f = new();
        var rights = f.Db.Services.GetRequiredService<ISpaceLootRights>();
        long owner = f.Pilot.PilotId;
        rights.Abandon(owner, [], Now);
        Assert.Throws<InvalidDataException>(() => rights.Abandon(owner, [f.Wreck.Id, f.Wreck.Id], Now));
        rights.Abandon(owner, [f.Wreck.Id], Now + TimeSpan.TicksPerHour * 2);
        f.Db.Store.SetPosition(
            f.Pilot.ShipId,
            f.Wreck.Position + new SpacePosition(WreckDefinitions.VisibilityRange + 1, 0, 0),
            f.Wreck.SystemId
        );
        rights.Abandon(owner, [f.Wreck.Id], Now);
        f.Db.Store.SetPosition(f.Pilot.ShipId, f.Wreck.Position, 30000142);
        rights.Abandon(owner, [f.Wreck.Id], Now);
        Assert.False(Assert.Single(f.Store.List(f.Wreck.SystemId, Now)).Abandoned);
    }

    [Fact]
    public void Version156UpgradePreservesPrivateWrecksAndContainers()
    {
        using Fixture f = new(liveWorld: false);
        const long canId = JetcanRules.FirstId;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new SavedJetcan
                {
                    Id = canId,
                    OwnerId = f.Pilot.PilotId,
                    ShipId = f.Pilot.ShipId,
                    CorporationId = f.Pilot.CorpId,
                    SystemId = f.Wreck.SystemId,
                    ExpiresAt = Now + JetcanRules.Lifetime,
                }
            );
            world.SaveChanges();
            world.Database.ExecuteSqlRaw(
                """
                ALTER TABLE WorldWreck DROP COLUMN Abandoned;
                ALTER TABLE WorldJetcan DROP COLUMN Abandoned;
                PRAGMA user_version=156;
                """
            );
        }
        WorldInitialization.Open(f.Db.Path);
        WorldInitialization.Open(f.Db.Path);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.False(saved.Set<WorldWreck>().Find(f.Wreck.Id)!.Abandoned);
        Assert.False(saved.Set<SavedJetcan>().Find(canId)!.Abandoned);
        Assert.Equal(f.Pilot.PilotId, saved.Set<OwnedItem>().Find(f.Item)!.OwnerId);
        Assert.Equal(WorldMigration.CurrentVersion, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Fact]
    public void AlphaHighSecurityTheftCannotUseALoweredSavedSafetySetting()
    {
        using Fixture f = new();
        var thief = f.Db.Store.FindPilot(1000002)!;
        f.Db.Store.SetPosition(thief.ShipId, f.Wreck.Position, f.Wreck.SystemId);
        SetSafety(f, thief.PilotId, ShipSafetyLevel.Partial, alpha: true);
        Assert.Throws<InvalidDataException>(() => f.Store.Take(thief.PilotId, thief.ShipId, f.Wreck.Id, [f.Item], null, Now));
        Assert.Equal(100, Assert.Single(f.Store.Read(thief.PilotId, f.Wreck.Id, Now).Items).Quantity);
        Assert.False(
            f.Db.Services.GetRequiredService<IPilotCrimewatch>().Read(thief.PilotId, f.Wreck.SystemId).Timers.Suspect.Running(Now)
        );
    }

    private static void SetSafety(Fixture f, long pilotId, ShipSafetyLevel safety, bool alpha = false)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true);
        string json = System.Text.Json.JsonSerializer.Serialize(new { securityStatus = 0.8 });
        string system = f.Wreck.SystemId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems',{system},{json})");
        using WorldDbContext world = new(f.Db.Path);
        var pilot = world.Pilots.Find(pilotId)!;
        pilot.SafetyLevel = safety;
        world.Set<Account>().Find(pilot.AccountId)!.CloneGrade = alpha ? AccountCloneGrade.Alpha : AccountCloneGrade.Omega;
        world.SaveChanges();
    }
}
