namespace Eden.Protocol.Serialization;

internal static class CachedMethodPayload
{
    // Native object caching compares the Adler-32 field to decide whether the payload changed.
    internal static int Checksum(ReadOnlySpan<byte> payload)
    {
        const uint adlerModulus = 65521;
        uint sum = 1;
        uint accumulated = 0;
        foreach (byte value in payload)
        {
            sum = (sum + value) % adlerModulus;
            accumulated = (accumulated + sum) % adlerModulus;
        }
        return unchecked((int)((accumulated << 16) | sum));
    }
}
