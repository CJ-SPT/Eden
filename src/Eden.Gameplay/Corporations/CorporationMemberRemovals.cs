namespace Eden.Gameplay.Corporations;

public interface ICorporationMemberRemovals
{
    IReadOnlyList<CorporationPendingExpulsion> Pending(long actorId);
    void Advance();
}
