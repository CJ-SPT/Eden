using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Protocol.Corporations;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationRecruitmentTests
{
    private const long Activity = 1L << 25;
    private const long Area = 1L << 12;
    private const long Language = 1L << 7;
    private const long NewPilots = 1L << 34;

    private static CorporationRecruitmentDraft Draft(string title = "Explorers") =>
        new(title, "星間 exploration", Activity | Area, Language, 7, NewPilots, 1000, [90000001, 90000002]);

    private static CorporationRecruitmentSearch Query() => new(Activity | Area, Language, true, true, 1000, 1, 100, 1, 1, 1, NewPilots);

    private static int Prepare(TestWorld world, long funds = 800_000_000)
    {
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        world
            .Services.GetRequiredService<ICorporationWallet>()
            .Transfer(
                90000001,
                new(false, CorporationWalletRules.MasterKey, corporation, CorporationWalletRules.MasterKey, funds, "Recruitment budget")
            );
        return corporation;
    }

    [Fact]
    public void NativeRecruitmentCallsChargeMasterWalletAndNotifyMembers()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = Prepare(world);
        using CorporationTestClient ceo = new(world, 1000001);
        using CorporationTestClient member = new(world, 1000002);
        var wallet = world.Services.GetRequiredService<ICorporationWallet>();
        var store = world.Services.GetRequiredService<ICorporationRecruitment>();
        long before = wallet.Balance(90000001, CorporationWalletRules.MasterKey);
        object?[] create =
        [
            2L,
            Activity | Area,
            Language,
            "Description",
            new List<object?> { 90000001L, 90000002L },
            "Recruiting 星",
            7L,
            1000L,
            NewPilots,
        ];
        Assert.IsType<PilotCallResult.Failure>(member.CallService("corpRecProxy", "CreateRecruitmentAd", create));
        var reply = Assert.IsType<PilotCallResult.Success>(ceo.CallService("corpRecProxy", "CreateRecruitmentAd", create));
        long id = Assert.IsType<long>(reply.Reply.ToBlue());
        Assert.Equal(before - CorporationRecruitmentRules.Fee(2, true), wallet.Balance(90000001, CorporationWalletRules.MasterKey));
        var entry = Assert.Single(
            wallet.Journal(90000001, true, CorporationWalletRules.MasterKey),
            entry => entry.EntryType == WalletEntryType.CorporationAdvertisementFee
        );
        Assert.Equal(id, entry.ReferenceId);
        Assert.Equal(-100_000_000, entry.AmountCents);
        var listing = Assert.IsType<CorporationRecruitmentRows>(
            Assert.IsType<PilotCallResult.Success>(member.CallService("corpRecProxy", "GetRecruitmentAdsForCorporation", [])).Reply
        );
        Assert.Equal(id, Assert.Single(listing.Ads).Id);
        var row = Assert.IsType<BlueConstructor>(Assert.Single(Assert.IsType<List<object?>>(listing.ToBlue())));
        Assert.Equal("blue.DBRow", row.Name);
        Assert.NotEmpty(BlueMarshal.Encode(listing));
        Assert.Equal(corporation, Assert.IsType<object?[]>(row.Arguments[1])[1]);
        var publicReply = Assert.IsType<CorporationRecruitmentMatches>(
            Assert
                .IsType<PilotCallResult.Success>(member.CallService("corpRecProxy", "GetRecruitmentAdsByCorpID", [(long)corporation]))
                .Reply
        );
        Assert.Equal(id, Assert.Single(publicReply.Matches).Ad.Id);
        var query = Assert.IsType<CorporationRecruitmentMatches>(
            Assert
                .IsType<PilotCallResult.Success>(
                    member.CallService(
                        "corpRecProxy",
                        "SearchCorpAds",
                        [Activity | Area, Language, true, true, 1000L, 1L, 10L, 0.5, 0.5, 1L, NewPilots]
                    )
                )
                .Reply
        );
        Assert.True(Assert.Single(query.Matches).Grade > 0);
        Assert.IsType<CorporationRecruitmentRow>(
            Assert.IsType<PilotCallResult.Success>(member.CallService("corpRecProxy", "GetRecruitmentAd", [(long)corporation, id])).Reply
        );
        Assert.Equal(new long[] { 90000001, 90000002 }, store.Recruiters(id));
        member.Advance();
        Assert.Empty(Assert.Single(member.Context.Notifications.OfType<CorporationRecruitmentChanged>()).Arguments);
        Assert.IsType<PilotCallResult.Success>(
            ceo.CallService(
                "corpRecProxy",
                "UpdateRecruitmentAd",
                [id, Activity | Area, Language, "Edited", new List<object?> { 90000002L }, "Edited title", 1L, 3L],
                new() { ["minSP"] = 2000L, ["otherMask"] = 0L }
            )
        );
        Assert.Equal(
            before - CorporationRecruitmentRules.Fee(2, true) - CorporationRecruitmentRules.DailyFeeCents,
            wallet.Balance(90000001, CorporationWalletRules.MasterKey)
        );
        Assert.Equal(2000, Assert.Single(store.ForCorporation(corporation)).Details.MinimumSkillPoints);
        Assert.IsType<PilotCallResult.Success>(ceo.CallService("corpRecProxy", "DeleteRecruitmentAd", [id]));
        Assert.Empty(store.ForCorporation(corporation));
    }

    [Fact]
    public void FailedPaymentForgedRecruitersAndAdLimitsLeaveStateUnchanged()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = Prepare(world, 300_000_000);
        var store = world.Services.GetRequiredService<ICorporationRecruitment>();
        var wallet = world.Services.GetRequiredService<ICorporationWallet>();
        Assert.Throws<InvalidDataException>(() => store.Create(90000001, Draft(), 28));
        Assert.Empty(store.ForCorporation(corporation));
        Assert.Equal(300_000_000, wallet.Balance(90000001, CorporationWalletRules.MasterKey));
        Assert.Throws<InvalidDataException>(() => store.Create(90000001, Draft() with { Recruiters = [90000003] }, 1));
        Assert.Throws<InvalidDataException>(() => store.Create(90000002, Draft(), 1));
        for (int index = 0; index < CorporationRecruitmentRules.MaximumAds; index++)
        {
            store.Create(90000001, Draft(), 1);
        }
        Assert.Throws<InvalidDataException>(() => store.Create(90000001, Draft(), 1));
        long id = store.ForCorporation(corporation)[0].Id;
        Assert.Throws<InvalidDataException>(() => store.Update(90000001, id, Draft("No funds"), 4));
        Assert.Equal("Explorers", store.ForCorporation(corporation)[0].Details.Title);
        Assert.Throws<InvalidDataException>(() => store.Delete(90000002, id));
        Assert.Throws<InvalidDataException>(() => store.Delete(90000001, long.MaxValue));
        Assert.Equal(75_000_000, wallet.Balance(90000001, CorporationWalletRules.MasterKey));
        Assert.Equal(3, store.ForCorporation(corporation).Count);
    }

    [Fact]
    public void ExpiryExtensionDeletionAndSequenceSurviveCheckpointRestart()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = Prepare(world);
        var store = world.Services.GetRequiredService<ICorporationRecruitment>();
        long id = store.Create(90000001, Draft(), 1);
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        Assert.Empty(store.Search(Query()));
        Assert.Empty(store.ForCorporation(corporation));
        Assert.Single(store.ForCorporation(corporation, true));
        Assert.Throws<InvalidDataException>(() => store.Update(90000001, id, Draft(), 0));
        store.Update(90000001, id, Draft("Renewed"), 2);
        Assert.Equal(clock.Now + 2 * TimeSpan.TicksPerDay, Assert.Single(store.Search(Query())).Ad.ExpiresAt);
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock: clock, liveWorld: true);
        store = reopened.GetRequiredService<ICorporationRecruitment>();
        Assert.Equal("Renewed", Assert.Single(store.ForCorporation(corporation)).Details.Title);
        store.Delete(90000001, id);
        long next = store.Create(90000001, Draft(), 1);
        Assert.True(next > id);
        reopened.Dispose();
        using var again = ServerServices.Build(new(world.Path), clock: clock, liveWorld: true);
        Assert.Equal(next, Assert.Single(again.GetRequiredService<ICorporationRecruitment>().ForCorporation(corporation)).Id);
    }

    [Fact]
    public void Version159UpgradeAddsRecruitmentWithoutChangingCorporations()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = Prepare(world);
        world.Services.Dispose();
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Database.ExecuteSqlRaw("DROP TABLE WorldCorporationRecruitmentAd; PRAGMA user_version=159;");
        }
        WorldInitialization.Initialize(new(world.Path));
        using var reopened = ServerServices.Build(new(world.Path), liveWorld: true);
        Assert.Equal("Eden Explorers", reopened.GetRequiredService<IPlayerCorporations>().Read(corporation)!.Details.Name);
        Assert.Empty(reopened.GetRequiredService<ICorporationRecruitment>().ForCorporation(corporation));
        Assert.True(reopened.GetRequiredService<ICorporationRecruitment>().Create(90000001, Draft(), 1) > 0);
    }

    [Fact]
    public void DelegatedRecruitersCannotEditAnotherCorporationAndDepartedContactsDisappear()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = Prepare(world);
        using CorporationTestClient ceo = new(world, 1000001);
        using CorporationTestClient member = new(world, 1000002);
        Assert.Null(ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(90000002, new((long)CorporationRole.PersonnelManager))));
        var store = world.Services.GetRequiredService<ICorporationRecruitment>();
        long id = store.Create(90000002, Draft(), 1);
        Assert.IsType<PilotCallResult.Failure>(
            member.CallService(
                "corpRecProxy",
                "UpdateRecruitmentAd",
                [id, Activity | Area, Language, "No", new List<object?>(), "No", 0L, 1L],
                new() { ["forged"] = true }
            )
        );
        Assert.Throws<InvalidDataException>(() => store.Update(90000002, id, Draft(), 28));
        Assert.Single(store.ForCorporation(corporation));
        PlayerCorporationTests.Prepare(world, 90000002);
        world.Services.GetRequiredService<IPlayerCorporations>().Create(90000002, PlayerCorporationTests.Details("Other corp", "OTHER"));
        Assert.Equal(new long[] { 90000001 }, store.Recruiters(id));
        Assert.Throws<InvalidDataException>(() => store.Delete(90000002, id));
        Assert.Throws<InvalidDataException>(() => store.Update(90000002, id, Draft(), 0));
        Assert.Equal("Explorers", Assert.Single(store.ForCorporation(corporation)).Details.Title);
    }

    [Fact]
    public void SearchFiltersAndDraftLimitsUseTypedRules()
    {
        var details = PlayerCorporationTests.Details();
        CorporationRecruitmentAd ad = new(1, 1, 1, 2, Draft(), 2);
        var query = Query();
        Assert.NotNull(CorporationRecruitmentRules.Match(query, ad, details));
        foreach (
            var excluded in new[]
            {
                query with
                {
                    SkillPoints = 999,
                },
                query with
                {
                    MaximumMembers = 1,
                },
                query with
                {
                    MinimumMembers = 3,
                },
                query with
                {
                    MaximumTaxRate = 0.09,
                },
                query with
                {
                    MaximumLoyaltyTaxRate = 0.04,
                },
                query with
                {
                    HourMask = 8,
                },
                query with
                {
                    LanguageMask = 1L << 8,
                },
                query with
                {
                    TypeMask = 1L << 29,
                },
                query with
                {
                    OtherMask = 1,
                },
            }
        )
        {
            Assert.Null(CorporationRecruitmentRules.Match(excluded, ad, details));
        }
        Assert.Null(CorporationRecruitmentRules.Match(query, ad, details with { FriendlyFireEnabled = true }));
        foreach (
            var invalid in new[]
            {
                Draft() with
                {
                    Title = "",
                },
                Draft() with
                {
                    Title = new('x', 41),
                },
                Draft() with
                {
                    Description = new('x', 1001),
                },
                Draft() with
                {
                    HourMask = 511,
                },
                Draft() with
                {
                    TypeMask = Activity | (1L << 29),
                },
                Draft() with
                {
                    TypeMask = CorporationRecruitmentRules.AreasMask,
                },
                Draft() with
                {
                    LanguageMask = long.MinValue,
                },
                Draft() with
                {
                    Recruiters = [90000001, 90000001],
                },
                Draft() with
                {
                    MinimumSkillPoints = -1,
                },
            }
        )
        {
            Assert.Throws<InvalidDataException>(() => CorporationRecruitmentRules.Validate(invalid));
        }
        Assert.Throws<InvalidDataException>(() => CorporationRecruitmentRules.Validate(query with { MaximumTaxRate = double.NaN }));
        Assert.Throws<InvalidDataException>(() => CorporationRecruitmentRules.Fee(29, true));
    }
}
