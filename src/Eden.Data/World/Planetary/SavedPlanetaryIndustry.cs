namespace Eden.Data.World.Planetary;

public sealed class SavedPlanetaryColony
{
    public long PilotId { get; set; }
    public int PlanetId { get; set; }
    public string StateJson { get; set; } = "";
    public string LastEditHash { get; set; } = "";
    public long LastEditAt { get; set; }
}

public sealed class SavedPlanetaryIdentity
{
    public int Id { get; set; }
    public long NextPinId { get; set; }
}

public sealed class SavedPlanetaryLaunch
{
    public int Id { get; set; }
    public long PilotId { get; set; }
    public int PlanetId { get; set; }
    public int SystemId { get; set; }
    public long CreatedAt { get; set; }
    public long ExpiresAt { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}

public sealed class SavedPlanetaryOffice
{
    public int PlanetId { get; set; }
    public int SystemId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}
