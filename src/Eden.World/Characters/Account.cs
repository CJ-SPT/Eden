using Eden.Gameplay.Docking;

namespace Eden.World.Characters;

public sealed class Account
{
    public string UserName { get; set; } = "";
    public long CreatedAt { get; set; }
    public long Id { get; set; }
    public AccountCloneGrade CloneGrade { get; set; } = AccountCloneGrade.Alpha;
}
