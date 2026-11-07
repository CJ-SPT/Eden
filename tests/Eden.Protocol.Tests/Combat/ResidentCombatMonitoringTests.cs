using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Tests.PilotSessions;
using Eden.World.Corporations;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed class ResidentCombatMonitoringTests
{
    [Fact]
    public async Task WarmIdentityAndCrimewatchReadsRetainDirtyCombatUntilCheckpoint()
    {
        using NpcCombatTests.Fixture f = new(liveWorld: true, useTestClock: true);
        var corporations = f.Db.Services.GetRequiredService<IPlayerCorporations>();
        var loyalty = f.Db.Services.GetRequiredService<ICorporationLoyaltyWallet>();
        var crimewatch = f.Db.Services.GetRequiredService<IPilotCrimewatch>();
        var pilot = f.Pilot;
        var identity = corporations.Identity(pilot.PilotId);
        Assert.Null(corporations.Read(pilot.CorpId));
        crimewatch.Read(pilot.PilotId, pilot.SystemId);
        Assert.Empty(loyalty.Read(pilot.PilotId));
        f.Aggress();
        using (var metrics = SessionPerformance.Begin("warm combat monitoring", _ => { }, force: true)!)
        {
            for (int repeat = 0; repeat < 20; repeat++)
            {
                Assert.Empty(loyalty.Read(pilot.PilotId));
                Assert.Same(identity, corporations.Identity(pilot.PilotId));
                Assert.Null(corporations.Read(pilot.CorpId));
                Assert.True(crimewatch.Read(pilot.PilotId, pilot.SystemId).Timers.Npc.ActiveUntil > f.Clock.Now);
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        Assert.Same(identity, corporations.Identity(pilot.PilotId));
        Assert.True(crimewatch.Read(pilot.PilotId, pilot.SystemId).Timers.Npc.ActiveUntil > f.Clock.Now);
    }

    [Fact]
    public void CorporationIdentityInvalidatesOnMembershipAndRolesWhileCoordinateChangesStayWarm()
    {
        using TestWorld f = new(liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(f);
        const long member = 90000002;
        const long ship = 1000000000002;
        const long ceoShip = 1000000000001;
        long headquarters = (long)CorporationRole.HangarCanQuery1;
        using (WorldDbContext world = new(f.Path))
        {
            var row =
                world.Set<SavedCorporationMemberAccess>().Find(member)
                ?? world.Add(new SavedCorporationMemberAccess { PilotId = member, CorporationId = corporation }).Entity;
            row.DataJson = JsonSerializer.Serialize(new CorporationMemberAccess(new(RolesAtHQ: headquarters)));
            world.SaveChanges();
        }
        var corporations = f.Services.GetRequiredService<IPlayerCorporations>();
        Assert.Equal(headquarters, corporations.Identity(member).Roles & headquarters);
        f.Store.SetPosition(ship, new(1000, 0, 0), 30000142);
        var flying = corporations.Identity(member);
        Assert.Equal(0, flying.Roles & headquarters);
        using (WorldDbContext world = new(f.Path))
        {
            world.Database.ExecuteSql($"update worldposition set x=x+1 where shipid={ship}");
        }
        using (var metrics = SessionPerformance.Begin("identity after coordinates", _ => { }, force: true)!)
        {
            Assert.Same(flying, corporations.Identity(member));
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
        }
        using (WorldDbContext world = new(f.Path))
        using (var transaction = world.Database.BeginTransaction())
        {
            var row =
                world.Set<SavedCorporationMemberAccess>().Find(member)
                ?? world.Add(new SavedCorporationMemberAccess { PilotId = member, CorporationId = corporation }).Entity;
            row.DataJson = JsonSerializer.Serialize(new CorporationMemberAccess(new((long)CorporationRole.Accountant)));
            world.SaveChanges();
            transaction.Rollback();
        }
        Assert.Equal(flying, corporations.Identity(member));
        using (WorldDbContext world = new(f.Path))
        {
            world.Database.ExecuteSql($"update worldposition set shipid={ceoShip} where shipid={ship}");
        }
        Assert.Equal(headquarters, corporations.Identity(member).Roles & headquarters);
        using (WorldDbContext world = new(f.Path))
        {
            var row =
                world.Set<SavedCorporationMemberAccess>().Find(member)
                ?? world.Add(new SavedCorporationMemberAccess { PilotId = member, CorporationId = corporation }).Entity;
            row.DataJson = JsonSerializer.Serialize(new CorporationMemberAccess(new((long)CorporationRole.Accountant)));
            world.SaveChanges();
        }
        Assert.Equal((long)CorporationRole.Accountant, corporations.Identity(member).Roles);
        Assert.Equal(2, corporations.Read(corporation)!.MemberCount);
    }

    [Fact]
    public void CorporationLoyaltyInvalidatesBalancesTitlesRolesAndMembershipIncludingRollback()
    {
        using TestWorld f = new(liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(f);
        const long member = 90000002;
        const int issuer = 1000044;
        var loyalty = f.Services.GetRequiredService<ICorporationLoyaltyWallet>();
        Assert.Empty(loyalty.Read(member));
        using (WorldDbContext world = new(f.Path))
        {
            world.Add(
                new SavedCorporationLoyaltyBalance
                {
                    CorporationId = corporation,
                    IssuerCorporationId = issuer,
                    Points = 100,
                }
            );
            world.Add(
                new SavedCorporationMemberAccess
                {
                    PilotId = member,
                    CorporationId = corporation,
                    DataJson = JsonSerializer.Serialize(new CorporationMemberAccess(new(), TitleMask: 1)),
                }
            );
            world.SaveChanges();
        }
        Assert.Empty(loyalty.Read(member));
        using (WorldDbContext world = new(f.Path))
        {
            world.Add(
                new SavedCorporationTitle
                {
                    CorporationId = corporation,
                    TitleId = 1,
                    DataJson = JsonSerializer.Serialize(new CorporationTitle(1, "Accounts", new((long)CorporationRole.JuniorAccountant))),
                }
            );
            world.SaveChanges();
        }
        var initial = loyalty.Read(member);
        Assert.Equal(100, Assert.Single(initial).Points);
        using (WorldDbContext world = new(f.Path))
        using (var transaction = world.Database.BeginTransaction())
        {
            world.Set<SavedCorporationLoyaltyBalance>().Find(corporation, issuer)!.Points = 200;
            world.SaveChanges();
            transaction.Rollback();
        }
        Assert.Equal(100, Assert.Single(loyalty.Read(member)).Points);
        using (WorldDbContext world = new(f.Path))
        {
            world.Set<SavedCorporationLoyaltyBalance>().Find(corporation, issuer)!.Points = 300;
            world.SaveChanges();
        }
        Assert.Equal(300, Assert.Single(loyalty.Read(member)).Points);
        Assert.Equal(100, Assert.Single(initial).Points);
        using (WorldDbContext world = new(f.Path))
        {
            var access = world.Set<SavedCorporationMemberAccess>().Find(member)!;
            access.DataJson = JsonSerializer.Serialize(new CorporationMemberAccess(new(), TitleMask: 1, BlockRoles: true));
            world.SaveChanges();
        }
        Assert.Empty(loyalty.Read(member));
        Assert.Equal(300, Assert.Single(loyalty.Read(90000001)).Points);
        using (WorldDbContext world = new(f.Path))
        {
            world.Pilots.Find(90000001L)!.CorporationId = issuer;
            world.SaveChanges();
        }
        Assert.Empty(loyalty.Read(90000001));
        Assert.Equal(1, f.Services.GetRequiredService<IPlayerCorporations>().Read(corporation)!.MemberCount);
    }
}
