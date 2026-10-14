namespace LuaToolsGui.Services;

// ZIP CRC detects malformed/truncated content, not publisher authenticity.
internal static class ArchiveCrc32
{
    private static readonly uint[] Table = CreateTable();
    internal static uint Update(uint state, ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes) state = Table[(state ^ value) & 255] ^ (state >> 8);
        return state;
    }
    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xedb88320u ^ (value >> 1) : value >> 1;
            table[i] = value;
        }
        return table;
    }
}
