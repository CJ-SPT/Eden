using Eden.Gameplay.Flight;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Flight;

internal readonly record struct BallMotion(long Id, SpacePosition Position, SpacePosition Velocity);

internal sealed record FlightDesyncReport(int Stamp, IReadOnlyList<BallMotion> Balls) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            Balls
                .DistinctBy(ball => ball.Id)
                .ToDictionary(
                    ball => ball.Id,
                    ball =>
                        (object?)
                            new List<object?>
                            {
                                ball.Position.X,
                                ball.Position.Y,
                                ball.Position.Z,
                                ball.Velocity.X,
                                ball.Velocity.Y,
                                ball.Velocity.Z,
                            }
                ),
            Stamp,
        };
}

internal sealed partial class FlightSession
{
    internal FlightDesyncReport ReadDesyncReport()
    {
        if (Pilot.Position is not { } position || !InBallpark || SourceSceneLocked)
        {
            throw new InvalidDataException("Enter an active ballpark before requesting a desync report.");
        }
        return new(_simulatedStamp, Scene.DiagnosticMotion(SceneContext).Prepend(new(Pilot.ShipId, position, Velocity)).ToArray());
    }
}
