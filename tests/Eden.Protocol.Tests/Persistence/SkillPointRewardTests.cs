using Eden.Data.World.Inventory;
using Eden.Data.World.Redemption;
using Eden.World.Inventory;
using Eden.World.Redemption;
using Eden.World.Storage;

namespace Eden.Protocol.Tests.Persistence;

public sealed class SkillPointRewardTests
{
    // Independent native get_redeem_data vectors captured inertly from build 3662168.
    [Theory]
    [InlineData(54652, 1_000)]
    [InlineData(54653, 2_500)]
    [InlineData(63303, 4_000)]
    [InlineData(63304, 4_500)]
    [InlineData(54650, 5_000)]
    [InlineData(63305, 5_500)]
    [InlineData(63306, 6_000)]
    [InlineData(63307, 6_500)]
    [InlineData(63308, 7_000)]
    [InlineData(54654, 7_500)]
    [InlineData(63309, 8_000)]
    [InlineData(63310, 9_000)]
    [InlineData(52520, 10_000)]
    [InlineData(63311, 11_000)]
    [InlineData(63312, 12_000)]
    [InlineData(63313, 13_000)]
    [InlineData(63314, 14_000)]
    [InlineData(63315, 15_000)]
    [InlineData(63316, 16_000)]
    [InlineData(63317, 17_000)]
    [InlineData(63318, 18_000)]
    [InlineData(63623, 19_000)]
    [InlineData(63624, 20_000)]
    [InlineData(63319, 21_000)]
    [InlineData(63320, 22_000)]
    [InlineData(63321, 23_000)]
    [InlineData(63322, 24_000)]
    [InlineData(49756, 25_000)]
    [InlineData(63323, 27_000)]
    [InlineData(63324, 28_000)]
    [InlineData(63325, 29_000)]
    [InlineData(57003, 30_000)]
    [InlineData(63326, 31_000)]
    [InlineData(63327, 32_000)]
    [InlineData(63328, 33_000)]
    [InlineData(63329, 34_000)]
    [InlineData(63330, 35_000)]
    [InlineData(63331, 42_000)]
    [InlineData(63332, 43_000)]
    [InlineData(63333, 44_000)]
    [InlineData(63334, 45_000)]
    [InlineData(63335, 46_000)]
    [InlineData(63336, 47_000)]
    [InlineData(63337, 48_000)]
    [InlineData(63338, 49_000)]
    [InlineData(49809, 50_000)]
    [InlineData(63339, 51_000)]
    [InlineData(63340, 52_000)]
    [InlineData(63824, 75_000)]
    [InlineData(49810, 100_000)]
    [InlineData(63825, 125_000)]
    [InlineData(54648, 150_000)]
    [InlineData(43680, 250_000)]
    [InlineData(52269, 500_000)]
    [InlineData(89839, 648_000)]
    [InlineData(52270, 750_000)]
    [InlineData(52318, 750_000)]
    [InlineData(52263, 1_000_000)]
    [InlineData(56707, 1_620_000)]
    public void NativeRewardAmountsCreditThePoolOnceWithoutCreatingAnInventoryItem(int type, long perToken)
    {
        var clock = CalendarTestData.Clock();
        using var db = FreePointTestData.Create(clock);
        var store = RedemptionTests.Store(db);
        var grant = RedemptionTests.Grant("native-reward", type, 2);
        var token = store.Grant(grant);
        var result = store.Claim(RedemptionTests.Account, FreePointTestData.Pilot, [new(token.Id, type)], false);
        Assert.Empty(result.Items);
        Assert.Empty(result.Errors);
        Assert.Equal(perToken * 2, result.SkillPoints);
        Assert.Equal(perToken * 2, FreePointTestData.Points(db).Balance(FreePointTestData.Pilot));
        Assert.Single(store.Claim(RedemptionTests.Account, FreePointTestData.Pilot, [new(token.Id, type)], false).Errors);
        Assert.Equal(token, store.Grant(grant));
        using WorldDbContext world = new(db.Path);
        Assert.DoesNotContain(world.Set<OwnedItem>(), i => i.TypeId == type);
        var saved = world.Set<SavedRedeemToken>().Find(token.Id)!;
        Assert.Equal(RedeemTokenState.Claimed, saved.State);
        Assert.Null(saved.ItemId);
    }
}
