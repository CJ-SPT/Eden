using Eden.Data.World.Contracts;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Contracts;

internal sealed partial class PilotContractStore
{
    public void Bid(long pilotId, long contractId, long amountCents) =>
        Write(
            (world, catalog) =>
            {
                Pilot(world, pilotId);
                var contract = Contract(world, pilotId, contractId);
                long now = Now;
                if (
                    contract.Type != ContractType.Auction
                    || contract.IssuerId == pilotId
                    || contract.Status != ContractStatus.Outstanding
                    || contract.Expires <= now
                )
                {
                    throw new InvalidDataException("This auction is unavailable for bidding.");
                }
                AuctionRules.CheckBid(contract.PriceCents, contract.CurrentBidCents, contract.BuyoutCents, amountCents);
                long previousAmount = contract.CurrentBidCents;
                long? previous = contract.AcceptorId;
                // A leading bidder only tops up the existing hold. A replacement bidder
                // funds its whole bid before the previous hold is returned atomically.
                long debit = previous == pilotId ? amountCents - previousAmount : amountCents;
                Pay(world, contract, pilotId, MarketDefinitions.SccCorporationId, WalletEntryType.ContractAuctionBid, -debit, now);
                if (previous is { } outbid && outbid != pilotId)
                {
                    Pay(
                        world,
                        contract,
                        outbid,
                        MarketDefinitions.SccCorporationId,
                        WalletEntryType.ContractAuctionBidRefund,
                        previousAmount,
                        now
                    );
                    Change(world, contract.Id, outbid, ContractChangeKind.AuctionOutbid, [], Balance(world, outbid));
                }
                world.Add(
                    new SavedContractBid
                    {
                        ContractId = contractId,
                        BidderId = pilotId,
                        AmountCents = amountCents,
                        Time = now,
                    }
                );
                contract.CurrentBidCents = amountCents;
                contract.AcceptorId = pilotId;
                bool buyout = amountCents == contract.BuyoutCents;
                IReadOnlyList<InventoryChange> delivered = [];
                if (buyout)
                {
                    // Native collection buttons become available once dateExpired passes.
                    // A buyout delivers goods immediately; the issuer still claims proceeds.
                    contract.Expires = now;
                    contract.Accepted = now;
                    delivered = DeliverAuctionGoods(world, catalog, contract, pilotId);
                    contract.WinnerCollected = true;
                    contract.Status = ContractStatus.FinishedContractor;
                    Change(world, contract.Id, contract.IssuerId, ContractChangeKind.AuctionBuyout, [], null);
                }
                else
                {
                    contract.Expires = AuctionRules.ExpiryAfterBid(now, contract.Expires);
                }
                Change(
                    world,
                    contract.Id,
                    pilotId,
                    buyout ? ContractChangeKind.AuctionBuyout : ContractChangeKind.AuctionBid,
                    delivered,
                    Balance(world, pilotId)
                );
                return true;
            }
        );

    public void FinishAuction(long pilotId, long contractId, bool issuer) =>
        Write(
            (world, catalog) =>
            {
                var contract = Contract(world, pilotId, contractId);
                long now = Now;
                if (
                    contract.Type != ContractType.Auction
                    || contract.AcceptorId is not { } winner
                    || contract.Expires > now
                    || (issuer ? contract.IssuerId != pilotId : winner != pilotId)
                    || contract.Status
                        is not (
                            ContractStatus.Outstanding
                            or ContractStatus.FinishedIssuer
                            or ContractStatus.FinishedContractor
                            or ContractStatus.Finished
                        )
                )
                {
                    throw new InvalidDataException("Only the issuer or winning bidder can collect an ended auction.");
                }
                if (issuer ? contract.IssuerCollected : contract.WinnerCollected)
                {
                    return true;
                }
                contract.Accepted ??= contract.Expires;
                if (issuer)
                {
                    long tax = contract.AssigneeId is null
                        ? ContractRules.SalesTax(contract.CurrentBidCents, contract.AuctionAccountingLevel)
                        : 0;
                    Pay(world, contract, pilotId, winner, WalletEntryType.ContractAuctionSold, contract.CurrentBidCents, now);
                    Pay(
                        world,
                        contract,
                        pilotId,
                        MarketDefinitions.SccCorporationId,
                        WalletEntryType.ContractDepositRefund,
                        contract.DepositCents,
                        now
                    );
                    Pay(world, contract, pilotId, MarketDefinitions.SccCorporationId, WalletEntryType.ContractSalesTax, -tax, now);
                    contract.SalesTaxCents = tax;
                    contract.IssuerCollected = true;
                    Change(world, contract.Id, pilotId, ContractChangeKind.Reclaimed, [], Balance(world, pilotId));
                }
                else
                {
                    var delivered = DeliverAuctionGoods(world, catalog, contract, pilotId);
                    contract.WinnerCollected = true;
                    Change(world, contract.Id, pilotId, ContractChangeKind.Reclaimed, delivered, null);
                }
                contract.Status = (contract.IssuerCollected, contract.WinnerCollected) switch
                {
                    (true, true) => ContractStatus.Finished,
                    (true, false) => ContractStatus.FinishedIssuer,
                    _ => ContractStatus.FinishedContractor,
                };
                if (contract.Status == ContractStatus.Finished)
                {
                    contract.Completed = now;
                    contract.Reclaimed = true;
                    Change(world, contract.Id, contract.IssuerId, ContractChangeKind.Completed, [], null);
                    Change(world, contract.Id, winner, ContractChangeKind.Completed, [], null);
                }
                return true;
            }
        );

    private static IReadOnlyList<InventoryChange> DeliverAuctionGoods(
        WorldAction world,
        CatalogDbContext catalog,
        SavedContract contract,
        long winner
    )
    {
        List<InventoryChange> delivered = [];
        foreach (var entry in world.Set<SavedContractEscrow>().Where(e => e.ContractId == contract.Id).ToArray())
        {
            delivered.Add(new(Transfer(world, catalog, entry.ItemId, contract.IssuerId, winner), null));
            world.Remove(entry);
        }
        return delivered;
    }

    public IReadOnlyList<ContractDetails> Bids(long pilotId) =>
        Read(
            (world, _) =>
            {
                Pilot(world, pilotId);
                return world
                    .Set<SavedContract>()
                    .Where(c =>
                        c.Type == ContractType.Auction
                        && world.Set<SavedContractBid>().Any(b => b.ContractId == c.Id && b.BidderId == pilotId && !b.Dismissed)
                    )
                    .OrderByDescending(c => c.Id)
                    .AsEnumerable()
                    .Select(c => Details(world, c))
                    .ToArray();
            }
        );

    public void ForgetAuction(long pilotId, long contractId) =>
        Write(
            (world, _) =>
            {
                Pilot(world, pilotId);
                var contract = Contract(world, pilotId, contractId);
                if (contract.Type != ContractType.Auction || (contract.AcceptorId == pilotId && !contract.WinnerCollected))
                {
                    throw new InvalidDataException("Collect your winning auction goods before dismissing it.");
                }
                var bids = world.Set<SavedContractBid>().Where(b => b.ContractId == contractId && b.BidderId == pilotId).ToArray();
                if (bids.Length == 0)
                {
                    throw new InvalidDataException("This pilot has no bid on this auction.");
                }
                foreach (var bid in bids)
                {
                    bid.Dismissed = true;
                }
                return true;
            }
        );
}
