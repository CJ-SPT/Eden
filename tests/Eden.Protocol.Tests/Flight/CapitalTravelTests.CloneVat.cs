using Eden.Data.World.Characters;
using Eden.Data.World.Market;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstalledRorqualCloneSurvivesRestartUntilUsedOrHostDestroyed(bool destroyHost)
    {
        using Fixture fixture = new(UtilityDroneRules.Rorqual, Oxygen);
        long vat = fixture.FitCapitalModule(CapitalModeRules.CloneVat);
        fixture.PutInCapitalCargo(LiquidOzone, 2);
        var owner = fixture.Db.Store.FindPilot(1000001)!;
        var guest = fixture.Db.Store.FindPilot(1000002)!;
        using (var world = fixture.Live.Economy.Begin(new(pilots: [guest.PilotId])))
        {
            world.Set<Account>().Find(1000002)!.CloneGrade = AccountCloneGrade.Omega;
            world.Commit();
        }
        FundGuest(0);
        fixture.Train(guest.PilotId, ((int)InfomorphSkill.Psychology, 2));
        var origin = new SpacePosition(10_000_000, 0, 0);
        fixture.Db.Store.SetPosition(owner.ShipId, origin, Jan);
        fixture.Db.Store.SetPosition(guest.ShipId, origin + new SpacePosition(1000, 0, 0), Jan);
        using Client host = fixture.Select(1000001);
        using Client miner = fixture.Select(1000002);
        Assert.IsType<PilotCallResult.Failure>(Offer());
        Assert.Same(PilotCallResult.None, host.Call("dogmaIM", "Activate", vat, "cloneJumpAccepting", null, 0L));
        var offer = Offer();
        Assert.True(offer is PilotCallResult.Success, offer.ToString());
        await miner.TickAsync();
        Assert.Contains(miner.Packets, packet => ContainsText(packet, "OnShipJumpCloneInstallationOffered"));
        Assert.Equal(900_000d, Assert.IsType<PilotCallResult.Success>(miner.Call("jumpCloneSvc", "GetPriceForClone")).Reply.ToBlue());
        Assert.Same(PilotCallResult.None, host.Call("jumpCloneSvc", "CancelShipCloneInstallation"));
        Assert.IsType<PilotCallResult.Failure>(Accept());
        Assert.Same(PilotCallResult.None, Offer());
        Assert.IsType<PilotCallResult.Failure>(Accept());
        Assert.Empty(Clones());
        long fee = JumpCloneRules.FeeCents;
        FundGuest(3 * fee);
        long ownerBalance = Balance(owner.PilotId);
        Assert.Same(PilotCallResult.None, Accept());
        Assert.IsType<PilotCallResult.Failure>(Accept());
        Assert.Equal(2 * fee, Balance(guest.PilotId));
        Assert.Equal(ownerBalance + fee, Balance(owner.PilotId));
        var body = Assert.Single(Clones());
        Assert.Equal(owner.ShipId, body.ShipId);
        var rows = Assert.IsType<CompatibilityRowset>(
            Assert.IsType<PilotCallResult.Success>(host.Call("jumpCloneSvc", "GetShipCloneState")).Reply
        );
        Assert.Equal(["jumpCloneID", "ownerID", "locationID"], rows.Header);
        Assert.Equal(new object?[] { body.Id, guest.PilotId, owner.ShipId }, Assert.IsType<object?[]>(Assert.Single(rows.Rows).ToBlue()));
        await host.TickAsync();
        Assert.Contains(host.Packets, packet => ContainsText(packet, "OnShipJumpCloneInstallationDone"));
        host.Dispose();
        miner.Dispose();

        fixture.Db.Store.SetPosition(guest.ShipId, null);
        var pod = fixture.Db.Services.GetRequiredService<IPilotFleet>().Leave(guest.PilotId, guest.ShipId).Pilot;
        Assert.Equal(Jita, pod.SystemId);
        await fixture.Live.FlushAsync();
        fixture.Reopen(restart: true);
        Assert.Equal(body, Assert.Single(Clones()));
        using Client hostAfterRestart = fixture.Select(1000001);
        using Client traveler = fixture.Select(1000002);
        if (destroyHost)
        {
            hostAfterRestart.Dispose();
            using (var world = fixture.Live.Economy.Begin(new(pilots: [owner.PilotId], ships: [owner.ShipId])))
            using (CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath))
            {
                var pilot = world.Pilots.Find(owner.PilotId)!;
                ShipLossPersistence.Apply(
                    world,
                    catalog,
                    pilot,
                    null,
                    owner.ShipId,
                    Jan,
                    pilot.ActiveShip.Position!,
                    fixture.Clock.Now,
                    new FixedCapitalLootRoll(false),
                    new CombatParty(90000003, null, 1000035, null, 601, null, 0, 1000)
                );
                world.Commit();
            }
            Assert.IsType<PilotCallResult.Failure>(Jump(true));
            Assert.Equal(2 * fee, Balance(guest.PilotId));
            traveler.Dispose();
            await fixture.Live.FlushAsync();
            fixture.Reopen(restart: true);
            Assert.Empty(Clones());
            return;
        }
        Assert.Equal("JumpCheckIntoShip", Assert.IsType<PilotCallResult.NamedFailure>(Jump(false)).Name);
        Assert.Equal(2 * fee, Balance(guest.PilotId));
        var active = Assert.IsType<CapitalModeCycle>(fixture.Travel.Mode(owner.ShipId));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(active.EndsAt - fixture.Clock.Now));
        Assert.IsType<PilotCallResult.Failure>(Jump(true));
        Assert.Equal(2 * fee, Balance(guest.PilotId));
        Assert.Same(PilotCallResult.None, hostAfterRestart.Call("dogmaIM", "Activate", vat, "cloneJumpAccepting", null, 0L));
        var jump = Jump(true);
        Assert.True(jump is PilotCallResult.Success, jump.ToString());
        Assert.Equal(Jan, traveler.Pilot.SystemId);
        Assert.NotEqual(pod.ShipId, traveler.Pilot.ShipId);
        Assert.InRange(traveler.Pilot.Position!.DistanceTo(origin), 5000, 10_000);
        Assert.Equal(fee, Balance(guest.PilotId));
        Assert.Equal(pod.StationId, Assert.Single(Clones()).StationId);
        Assert.IsType<PilotCallResult.Failure>(Jump(true));

        PilotCallResult Offer() => host.Call("jumpCloneSvc", "OfferShipCloneInstallation", guest.PilotId);
        PilotCallResult Accept() => miner.Call("jumpCloneSvc", "AcceptShipCloneInstallation");
        PilotCallResult Jump(bool confirm) => traveler.Call("jumpCloneSvc", "CloneJump", owner.ShipId, body.Id, 900_000d, confirm);
        IReadOnlyList<JumpClone> Clones() => fixture.Db.Services.GetRequiredService<IPilotJumpClones>().Read(guest.PilotId).Clones;
        long Balance(long pilot) => fixture.Live.Economy.Inspect(world => world.Set<PilotWallet>().Find(pilot)?.Cents ?? 0);
        void FundGuest(long cents)
        {
            using var world = fixture.Live.Economy.Begin(new(pilots: [guest.PilotId]));
            var wallet = world.Set<PilotWallet>().Find(guest.PilotId) ?? world.Add(new PilotWallet { PilotId = guest.PilotId }).Entity;
            wallet.Cents = cents;
            world.Commit();
        }
    }

    [Fact]
    public async Task RorqualCloneVatPaysOzoneAndCompletesItsImmobilizingCycleAfterRestart()
    {
        using Fixture fixture = new(UtilityDroneRules.Rorqual, Oxygen);
        long vat = fixture.FitCapitalModule(CapitalModeRules.CloneVat);
        var pilot = fixture.Db.Store.FindPilot(1000001)!;
        fixture.Db.Store.SetPosition(pilot.ShipId, new(10_000_000, 0, 0), Jan);
        using Client rorqual = fixture.Select(1000001);
        var fitting = fixture.Db.Services.GetRequiredService<IPilotFitting>();
        fixture.Train(pilot.PilotId, (CapitalModeRules.CloningFacilityOperation, 5));
        Assert.Equal(10.5, fitting.ShipAttributes(pilot.PilotId)[DogmaAttributeId.MaxJumpClones], 6);
        double velocity = fitting.ShipAttributes(pilot.PilotId)[DogmaAttributeId.MaxVelocity];
        Assert.IsType<PilotCallResult.Failure>(Activate());
        Assert.Null(fixture.Travel.Mode(pilot.ShipId));
        fixture.PutInCapitalCargo(LiquidOzone, 2);
        var paid = Activate();
        Assert.True(paid is PilotCallResult.Success, paid.ToString());
        var active = Assert.IsType<CapitalModeCycle>(fixture.Travel.Mode(pilot.ShipId));
        Assert.Equal(DogmaEffect.CloneJumpAccepting, active.Effect);
        Assert.Equal(TimeSpan.FromMinutes(5).Ticks, active.EndsAt - active.StartedAt);
        Assert.Equal(1, Quantity(fixture, rorqual, LiquidOzone));
        Assert.Equal(0, fitting.ShipAttributes(pilot.PilotId)[DogmaAttributeId.MaxVelocity]);
        Assert.IsType<PilotCallResult.Failure>(Activate());
        Assert.Same(PilotCallResult.None, rorqual.Call("dogmaIM", "Deactivate", vat, "cloneJumpAccepting"));
        var docking = fixture.Db.Services.GetRequiredService<IStationDocking>();
        Assert.Throws<InvalidDataException>(() => docking.Dock(pilot.PilotId, pilot.ShipId, JanStation));
        await rorqual.TickAsync();
        Assert.Contains(rorqual.Packets, packet => ContainsText(packet, "OnGodmaShipEffect"));
        rorqual.Dispose();
        await fixture.Live.FlushAsync();
        fixture.Reopen(restart: true);
        Assert.Equal(active, fixture.Travel.Mode(pilot.ShipId));
        using Client returned = fixture.Select(1000001);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(active.EndsAt - fixture.Clock.Now));
        await returned.TickAsync();
        Assert.Null(fixture.Travel.Mode(pilot.ShipId));
        Assert.Equal(1, Quantity(fixture, returned, LiquidOzone));
        Assert.Equal(velocity, fitting.ShipAttributes(pilot.PilotId)[DogmaAttributeId.MaxVelocity]);

        PilotCallResult Activate() => rorqual.Call("dogmaIM", "Activate", vat, "cloneJumpAccepting", null, 0L);
    }
}
