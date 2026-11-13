namespace Eden.Gameplay.Corporations;

public sealed record CorporationDirectoryEntry(int Id, string Name, string Ticker, CorporationLogo Logo);

public interface IPlayerCorporationDirectory
{
    IReadOnlyList<CorporationDirectoryEntry> Read(IReadOnlyList<int> ids);
}
