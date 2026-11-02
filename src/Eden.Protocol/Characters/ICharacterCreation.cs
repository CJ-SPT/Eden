namespace Eden.Protocol.Characters;

public sealed record NewCharacter(string Name, int RaceId, int BloodlineId, int GenderId, int AncestryId,
    byte[] Appearance, byte[] Portrait, int SchoolId);

public interface ICharacterCreation
{
    const int Slots = 3;
    int ValidateName(string name);
    long Create(long accountId, NewCharacter character);
    byte[]? ReadAppearance(long characterId, bool portrait);
}
