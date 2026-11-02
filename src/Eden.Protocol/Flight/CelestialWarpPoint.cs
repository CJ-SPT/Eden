using Eden.Protocol.Docking;

namespace Eden.Protocol.Flight;

internal static class CelestialWarpPoint
{
    // EVE's published warp-in formulas use system coordinates and radii in metres.
    internal static SpacePosition Resolve(StaticSpaceObject body, SpacePosition origin)
    {
        var center = body.Position;
        double radius = body.Radius;
        if (body.CategoryId == 2 && body.GroupId == 7 && radius > 0)
        {
            double jitter = (PlanetRandom((uint)body.Id) - 1) / 3;
            double longitude = Math.Atan2(center.Z * Math.Sign(center.X), Math.Abs(center.X)) + jitter;
            double size = (10 * Math.Log10(radius / 1_000_000) - 39) / 40;
            double distance = radius * (1 + Math.Clamp(20 * Math.Pow(size, 20) + 0.5, 0.5, 10.5)) + 1_000_000;
            return center + new SpacePosition(distance * Math.Sin(longitude), radius * Math.Sin(jitter) / 2,
                -distance * Math.Cos(longitude));
        }
        if (body.CategoryId == 2 && body.GroupId == 6)
        {
            return center + new SpacePosition((radius + 100_000) * Math.Cos(radius), 0.2 * radius,
                -(radius + 100_000) * Math.Sin(radius));
        }
        if (body.CategoryId == 2 && body.GroupId != 186 && radius >= 90_000)
        {
            return center + new SpacePosition((radius + 5_000_000) * Math.Cos(radius), 1.3 * radius - 7500,
                -(radius + 5_000_000) * Math.Sin(radius));
        }
        return center + (origin - center).Unit * radius;
    }

    // First random.Random(planetID).random() value. CPython seeds MT19937 with a
    // one-word integer array and takes the high 27 and 26 bits of its first two outputs.
    private static double PlanetRandom(uint id)
    {
        Span<uint> state = stackalloc uint[624];
        state[0] = 19650218;
        for (int index = 1; index < state.Length; index++)
        {
            uint previous = state[index - 1];
            state[index] = 1812433253u * (previous ^ (previous >> 30)) + (uint)index;
        }
        int i = 1;
        for (int round = 0; round < 2; round++)
        {
            for (int count = state.Length - round; count > 0; count--)
            {
                uint previous = state[i - 1];
                uint mixed = previous ^ (previous >> 30);
                state[i] = round == 0 ? (state[i] ^ mixed * 1664525u) + id : (state[i] ^ mixed * 1566083941u) - (uint)i;
                if (++i == state.Length)
                {
                    state[0] = state[^1];
                    i = 1;
                }
            }
        }
        state[0] = 0x80000000;
        uint first = Temper(state, 0) >> 5;
        uint second = Temper(state, 1) >> 6;
        return (first * 67108864.0 + second) / 9007199254740992.0;
    }

    private static uint Temper(ReadOnlySpan<uint> state, int i)
    {
        uint pair = (state[i] & 0x80000000) | (state[i + 1] & 0x7fffffff);
        uint word = state[i + 397] ^ (pair >> 1) ^ ((pair & 1) == 0 ? 0u : 0x9908b0df);
        word ^= word >> 11;
        word ^= (word << 7) & 0x9d2c5680;
        word ^= (word << 15) & 0xefc60000;
        return word ^ (word >> 18);
    }
}
