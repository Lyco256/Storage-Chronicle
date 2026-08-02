namespace StorageChronicle.Storage;

internal static class Crc32C
{
    private const uint Polynomial = 0x82F63B78;
    private static readonly uint[] Table = CreateTable();

    internal static uint Compute(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc = Table[(crc ^ value) & 0xff] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (var i = 0; i < table.Length; i++)
        {
            var value = (uint)i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) == 0 ? value >> 1 : (value >> 1) ^ Polynomial;
            }

            table[i] = value;
        }

        return table;
    }
}
