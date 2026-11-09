namespace Eden.Data.World.Combat;

public sealed class SavedConcordResponse
{
    public long ShipId { get; set; }
    public long PilotId { get; set; }
    public int SystemId { get; set; }
    public long RespondAt { get; set; }
    public long DestroyAt { get; set; }
    public long? FirstCaptainId { get; set; }
    public long? SecondCaptainId { get; set; }
    public long? CommanderId { get; set; }
    public long? CompletedAt { get; set; }
}
