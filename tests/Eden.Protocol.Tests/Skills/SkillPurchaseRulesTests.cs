using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;

namespace Eden.Protocol.Tests.Skills;

public sealed class SkillPurchaseRulesTests
{
    private static SkillPurchaseOffer Offer => new(3443, InventoryCategory.Skill, true, true, 30000, 1, false);

    [Theory]
    [InlineData(30000, 3900000)]
    [InlineData(1000, 130000)]
    [InlineData(1.25, 163)]
    public void DirectPriceUsesCatalogBasePriceAndThirtyPercentPremium(double basePrice, long cents)
    {
        Assert.Equal(cents, SkillPurchaseRules.PriceCents(Offer with { BasePrice = basePrice }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.MaxValue)]
    [InlineData(0.0001)]
    [InlineData(8e16)]
    public void InvalidPricesCannotBecomeFreeOrOverflow(double price)
    {
        Assert.Throws<InvalidDataException>(() => SkillPurchaseRules.PriceCents(Offer with { BasePrice = price }));
    }

    [Fact]
    public void EligibilityAndExistingInjectionAreAuthoritative()
    {
        SkillPurchaseOffer[] rejected =
        [
            Offer with
            {
                Category = InventoryCategory.Module,
            },
            Offer with
            {
                Published = false,
            },
            Offer with
            {
                Available = false,
            },
            Offer with
            {
                Injected = true,
            },
            Offer with
            {
                Rank = 0,
            },
            Offer with
            {
                Rank = double.NaN,
            },
        ];
        foreach (var offer in rejected)
        {
            Assert.Throws<InvalidDataException>(() => SkillPurchaseRules.PriceCents(offer));
        }
    }
}
