using Eden.Gameplay.Inventory;

namespace Eden.Data.World.Inventory;

public sealed class SavedCargoContainer
{
    public long ItemId { get; set; }
    public string? Name { get; set; }

    // Native RetrievePassword must display the original text to an authorized owner.
    public string? GeneralPassword { get; set; }
    public string? ConfigurationPassword { get; set; }
    public CargoContainerConfiguration Configuration { get; set; }
}
