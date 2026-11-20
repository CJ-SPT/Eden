using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Data.World.Market;
using Eden.Gameplay.FactionWarfare;
using Eden.World.Runtime.Economy;

namespace Eden.World.FactionWarfare;

internal static class MilitiaLoyalty
{
    internal static void Credit(WorldAction world, MilitiaFaction faction, Pilot pilot, long points)
    {
        if (points <= 0)
        {
            return;
        }
        int issuer = MilitiaRules.Corporation(faction);
        int corpId = pilot.CorporationId;
        var corp = world.Set<SavedCorporation>().Find(corpId);
        long tax = MilitiaComplexRules.CorporationTax(points, corp?.Snapshot(0).Details.LoyaltyPointTaxRate ?? 0);
        if (tax > 0)
        {
            var corporate =
                world.Set<SavedCorporationLoyaltyBalance>().Find(corpId, issuer)
                ?? world.Add(new SavedCorporationLoyaltyBalance { CorporationId = corpId, IssuerCorporationId = issuer }).Entity;
            corporate.Points = checked(corporate.Points + tax);
        }
        if (points > tax)
        {
            var personal =
                world.Set<SavedLoyaltyBalance>().Find(pilot.Id, issuer)
                ?? world.Add(new SavedLoyaltyBalance { PilotId = pilot.Id, IssuerCorporationId = issuer }).Entity;
            personal.Points = checked(personal.Points + points - tax);
        }
    }
}
