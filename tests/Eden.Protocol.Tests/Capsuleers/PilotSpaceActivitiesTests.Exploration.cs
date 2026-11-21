using Eden.Data.World.Capsuleers;
using Eden.Data.World.Exploration;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Exploration;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed partial class PilotSpaceActivitiesTests
{
    [Theory]
    [InlineData(ExplorationKind.Data, false)]
    [InlineData(ExplorationKind.Relic, false)]
    [InlineData(ExplorationKind.Data, true)]
    public async Task ExplorerBuysFitsScansHacksAndSellsSiteLoot(ExplorationKind kind, bool disableOnRestart)
    {
        using CapsuleerHaulerTests.Fixture f = new(additionalCatalogData: path =>
        {
            PassiveCatalogFixture.LoadProbes(path);
            PassiveCatalogFixture.LoadHacking(path);
            PassiveCatalogFixture.LoadStarterMarket(path, new HashSet<int> { (int)MarketSkill.Trade });
        });
        long pilot = PrepareDockedRatter(f);
        using (CatalogDbContext writable = new(f.Database.CatalogPath, writable: true))
        {
            writable.Database.ExecuteSqlRaw(
                """
                INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000141','{{"securityStatus":1.0}}');
                """
            );
        }
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var live = host.GetRequiredService<LiveWorld>();
        using CatalogDbContext catalog = new(f.Database.CatalogPath);
        Assert.True(
            catalog.SystemPolitics(SystemId).Security is >= CapsuleerExplorationRules.SafeSecurity,
            $"The fixture needs high-security space. Security: {catalog.SystemPolitics(SystemId).Security}."
        );
        var kit = CapsuleerIndustry.ExplorerKit(catalog);
        Assert.NotNull(kit);
        var sites = host.GetRequiredService<IPirateExploration>().Signatures(SystemId, f.Clock.Now);
        var site = Assert.Single(sites, value => value.Exploration == kind);
        var profile = HackingCatalog.Sites.Single(definition => definition.DungeonId == site.DungeonId);
        long buyer;
        long originalShip;
        int rewardType;
        List<MarketSale> offers = [];
        using (var turn = live.Economy.Begin())
        {
            originalShip = turn.Pilots.Find(pilot)!.ActiveShipId;
            Assert.Equal(0, turn.Set<SavedCapsuleerAgent>().Find(pilot)!.Slot);
            // Keep the legacy founder eligible when startup reapplies the saved traffic density.
            var population = turn.Set<SavedCapsuleerProvisioning>().Find(1) ?? turn.Add(new SavedCapsuleerProvisioning { Id = 1 }).Entity;
            population.WorldSeed = 1;
            buyer = turn.Set<SavedCapsuleerAgent>().Single(agent => agent.Career == CapsuleerCareer.Mining).PilotId;
            turn.Set<PilotWallet>().Find(pilot)!.Cents = 4_000_000;
            turn.Set<PilotWallet>().Find(buyer)!.Cents = 4_000_000;
            int trade = (int)MarketSkill.Trade;
            var tradeSkill =
                turn.Set<PilotSkill>().Find(buyer, trade) ?? turn.Add(new PilotSkill { PilotId = buyer, TypeId = trade }).Entity;
            tradeSkill.Points = TrainedSkill.PointsForLevel(catalog.FittingAttributes(trade)[DogmaAttributeId.SkillTimeConstant], 1);
            HashSet<int> skills = [];
            foreach (var part in kit.Parts())
            {
                Qualify(part.TypeId);
                long id = InventoryPersistence.NextId(turn);
                turn.Add(
                    new OwnedItem
                    {
                        Id = id,
                        OwnerId = buyer,
                        LocationId = Station,
                        FlagId = (int)InventoryFlag.Hangar,
                        TypeId = part.TypeId,
                        Quantity = part.Quantity,
                    }
                );
                offers.Add(new(id, Station, part.TypeId, 10_000, part.Quantity));
            }
            int[] equipment = kit.Parts().Select(part => part.TypeId).ToArray();
            Assert.Empty(equipment.Except(PilotSkillReader.UsableTypes(turn, catalog, pilot, equipment)));
            var first = turn.Set<SavedHackingContainer>().Find(site.Objects.First().Id)!;
            int difficulty = profile.Containers[first.Index].Difficulty;
            first.LootSeed = Enumerable.Range(1, 2000).First(seed => CanWin(seed, difficulty, 90, 20) && CanWin(seed, difficulty, 110, 30));
            foreach (var extra in turn.Filter<SavedHackingContainer>(container => container.SiteId == site.Id).Where(c => c.Id != first.Id))
            {
                extra.Destroyed = true;
            }
            foreach (var other in sites.Where(other => other.Id != site.Id))
            {
                turn.Set<SavedExplorationSite>().Find(other.Id)!.ReplaceAt = f.Clock.Now + TimeSpan.TicksPerHour;
            }
            rewardType = ExplorationLootRules
                .Roll(
                    first.LootSeed,
                    kind,
                    (NpcPirateFaction)profile.FactionId,
                    profile.Band,
                    HackingCatalog.Loot(catalog, HackingCatalog.DatacoreGroup),
                    HackingCatalog.Loot(catalog, HackingCatalog.DecryptorGroup)
                )
                .First()
                .TypeId;
            turn.Commit();

            void Qualify(int type)
            {
                foreach (var (skillAttribute, _) in InventoryDefinitions.RequiredSkills)
                {
                    int skill = (int)catalog.FittingAttributes(type).GetValueOrDefault(skillAttribute);
                    if (skill == 0 || !skills.Add(skill))
                    {
                        continue;
                    }
                    Qualify(skill);
                    var row =
                        turn.Set<PilotSkill>().Find(pilot, skill) ?? turn.Add(new PilotSkill { PilotId = pilot, TypeId = skill }).Entity;
                    row.Points = TrainedSkill.PointsForLevel(catalog.FittingAttributes(skill)[DogmaAttributeId.SkillTimeConstant], 5);
                }
            }
        }
        var market = host.GetRequiredService<IPersonalMarket>();
        market.PlaceSell(buyer, offers, 7);
        market.PlaceBuy(buyer, new(Station, rewardType, 150_000, 1, 7));
        var presence = host.GetRequiredService<IHumanPresence>();
        Guid observer = Guid.NewGuid();
        presence.Update(observer, SystemId);
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
        bool seenScan = false;
        bool paused = false;
        int deepestTurn = 0;
        HaulerObservation? landed = null;
        List<string> trace = [];
        for (int tick = 0; tick < 900; tick++)
        {
            f.Clock.Seconds++;
            maintenance.Tick();
            var bot = scheduler.Hauler(pilot)!;
            var current = bot.Work.Exploration;
            string entry =
                $"hull {bot.Pilot.ShipTypeId}, {bot.Work.Activity}/{current?.Phase}, scan {current?.Scans}/{current?.RangeStep}, "
                + $"signal {current?.SignalId}, container {current?.ContainerId}, retreat {bot.Work.Retreating}, {bot.Work.WaitingReason}";
            if (trace.LastOrDefault() != entry && trace.Count < 50)
            {
                trace.Add(entry);
            }
            if (bot.Work.Exploration is { } progress)
            {
                seenScan |= progress is { ScanInFlight: true, Scans: >= 2 };
                if (seenScan && !paused)
                {
                    paused = true;
                    presence.Update(observer, null);
                    f.Clock.Seconds += 90;
                    maintenance.Tick();
                    Assert.Equal(progress, scheduler.Hauler(pilot)!.Work.Exploration);
                    if (disableOnRestart)
                    {
                        await ReturnWithExplorationDisabled(progress, bot.Pilot.ShipId);
                        return;
                    }
                    presence.Update(observer, 30000142);
                }
                var contact = live.Economy.Read(world => host.GetRequiredService<PirateExplorationStore>().Pending(world, pilot));
                if (contact is not null)
                {
                    deepestTurn = Math.Max(deepestTurn, contact.Board.Turn);
                    Assert.Equal(kind, contact.Kind);
                    Assert.Equal(
                        kind == ExplorationKind.Data ? CapsuleerIndustry.DataAnalyzer : CapsuleerIndustry.RelicAnalyzer,
                        contact.Module.TypeId
                    );
                }
            }
            else if (seenScan && bot.Pilot.Position is null)
            {
                landed = bot;
                break;
            }
        }
        Assert.True(landed is not null, $"Exploration did not return to station. {string.Join("\n", trace)}");
        Assert.NotEqual(originalShip, landed.Pilot.ShipId);
        Assert.Equal(CapsuleerIndustry.ExplorerHull, landed.Pilot.ShipTypeId);
        Assert.True(deepestTurn > 3, $"Expected a native hacking board. {string.Join("\n", trace)}");
        await live.FlushAsync();
        live.Economy.Read(world =>
        {
            Assert.NotNull(world.Set<SavedResolvedSignature>().Find(pilot, site.Id));
            Assert.Null(world.Set<SavedProbeScan>().Find(pilot));
            Assert.Empty(world.Filter<SavedScanProbe>(probe => probe.PilotId == pilot));
            Assert.Empty(world.Filter<SavedHackingAttempt>(attempt => attempt.PilotId == pilot));
            var container = world.Set<SavedHackingContainer>().Find(site.Objects.First().Id)!;
            Assert.True(container.Hacked && container.Empty);
            Assert.Equal(pilot, container.OwnerId);
            var bid = Assert.Single(world.Filter<SavedPersonalOrder>(order => order.PilotId == buyer), order => order.Bid);
            Assert.Equal(0, bid.Remaining);
            Assert.Equal(0, bid.EscrowCents);
            Assert.Single(
                world.Filter<OwnedItem>(item => item.OwnerId == buyer),
                item => item.TypeId == rewardType && item.LocationId == Station && item.Quantity == 1
            );
            Assert.Equal(3, world.Filter<OwnedItem>(item => item.LocationId == landed.Pilot.ShipId).Count(item => item.Online));
            Assert.Equal(
                ProbeScanRules.MaximumProbes,
                world
                    .Filter<OwnedItem>(item => item.LocationId == landed.Pilot.ShipId)
                    .Where(item => item.TypeId == CapsuleerIndustry.ExplorerProbe)
                    .Sum(item => item.Quantity)
            );
            return true;
        });

        async Task ReturnWithExplorationDisabled(ExplorationProgress scanning, long ship)
        {
            var config = host.GetRequiredService<IWorldConfiguration>();
            var state = config.Read();
            var settings = state.Active.Draft.Settings;
            config.Save(
                state.Generation,
                state.Active.Draft with
                {
                    Settings = settings with { Capsuleers = settings.Capsuleers with { Exploration = false } },
                },
                "admin"
            );
            Assert.True(f.Database.Settings.Capsuleers.Exploration);
            await live.FlushAsync();
            host.Dispose();

            WorldInitialization.Initialize(f.Database, f.Clock);
            Assert.False(f.Database.Settings.Capsuleers.Exploration);
            using var reopened = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
            var restored = reopened.GetRequiredService<LiveWorld>();
            using var observerAfterReopen = reopened.GetRequiredService<IHumanPresence>().Select(90000001, SystemId);
            var resumed = reopened.GetRequiredService<ICapsuleerScheduler>();
            var jobs = reopened.GetRequiredService<CapsuleerMaintenance>();
            Assert.False(restored.Economy.Read(world => world.Set<SavedCapsuleerSuspension>().Find(pilot) is not null));
            var checkpoint = Assert.IsType<ExplorationProgress>(resumed.Hauler(pilot)?.Work.Exploration);
            Assert.Equal(ExplorationPhase.Scanning, checkpoint.Phase);
            Assert.True(checkpoint.ScanInFlight);
            Assert.Equal(scanning.Scans, checkpoint.Scans);
            Assert.Equal(scanning.ReadyTicks, checkpoint.ReadyTicks);
            for (int tick = 0; tick < 500 && resumed.Hauler(pilot)!.Work.Exploration is not null; tick++)
            {
                f.Clock.Seconds++;
                jobs.Tick();
            }
            var returned = resumed.Hauler(pilot)!;
            Assert.Null(returned.Work.Exploration);
            Assert.Equal(Station, returned.Pilot.StationId);
            Assert.Equal(ship, returned.Pilot.ShipId);
            restored.Economy.Read(world =>
            {
                Assert.Null(world.Set<SavedProbeScan>().Find(pilot));
                Assert.Empty(world.Filter<SavedScanProbe>(probe => probe.PilotId == pilot));
                Assert.Null(world.Set<SavedResolvedSignature>().Find(pilot, site.Id));
                Assert.False(world.Set<SavedHackingContainer>().Find(site.Objects.First().Id)!.Hacked);
                Assert.Equal(
                    ProbeScanRules.MaximumProbes,
                    world
                        .Filter<OwnedItem>(item => item.LocationId == ship)
                        .Where(item => item.TypeId == CapsuleerIndustry.ExplorerProbe)
                        .Sum(item => item.Quantity)
                );
                return true;
            });
            for (int tick = 0; tick < 180; tick++)
            {
                f.Clock.Seconds++;
                jobs.Tick();
                Assert.Null(resumed.Hauler(pilot)?.Work.Exploration);
            }
        }
    }

    private static bool CanWin(int seed, int difficulty, int coherence, int strength)
    {
        var board = HackingRules.Create(seed, difficulty, coherence, strength, 3);
        while (
            board.Outcome == HackingOutcome.Running
            && board.Turn < CapsuleerExplorationRules.MaximumHackTurns
            && CapsuleerExplorationRules.Hack(board) is { } move
        )
        {
            board = HackingRules.Step(board, move);
        }
        return board.Outcome == HackingOutcome.Won;
    }
}
