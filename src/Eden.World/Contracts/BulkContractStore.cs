using Eden.Gameplay.Contracts;

namespace Eden.World.Contracts;

internal sealed partial class PilotContractStore
{
    public IReadOnlyList<long> CreateBatch(long pilotId, ContractDraft draft)
    {
        ContractRules.Check(draft);
        if (!draft.MultiContract)
        {
            throw new InvalidDataException("A bulk fitted-ship request is required.");
        }
        return Write<IReadOnlyList<long>>(
            (world, catalog) =>
            {
                var assets = PilotContractAssetStore.Assets(world, catalog, pilotId, draft.StationId).ToDictionary(i => i.Id);
                foreach (var offer in draft.Items)
                {
                    if (!assets.TryGetValue(offer.ItemId, out var hull))
                    {
                        throw new InvalidDataException("Select available ships in your personal station hangar.");
                    }
                    BulkContractRules.CheckShip(hull);
                }
                long now = Now;
                List<long> ids = [];
                foreach (var offer in draft.Items)
                {
                    // Each child keeps its own fee, slot, item graph and later
                    // settlement. They share this transaction and publish only once.
                    ids.Add(CreateOne(world, catalog, pilotId, draft with { Items = [offer], MultiContract = false }, now));
                }
                return ids;
            }
        );
    }
}
