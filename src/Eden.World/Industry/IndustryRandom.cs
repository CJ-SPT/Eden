using System.Security.Cryptography;
using Eden.Gameplay.Industry;
using Eden.Infrastructure.Registration;

namespace Eden.World.Industry;

[Injectable]
internal sealed class IndustryRandom : IIndustryRandom
{
    public IReadOnlyList<double> Rolls(int count) =>
        Enumerable.Range(0, count).Select(_ => RandomNumberGenerator.GetInt32(int.MaxValue) / (double)int.MaxValue).ToArray();
}
