using System.Buffers.Binary;

namespace Toboo.Desktop.Services;

internal sealed record MicPacket(uint Session, uint Sequence, byte Kind, byte[] Pcm, string? Text = null)
{
    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0);
        }
        return ~crc;
    }

    internal static MicPacket Decode(byte[] bytes)
    {
        if (bytes.Length < 20 || !bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 0xA5, 0x5A, 0xC3, 0x3C }))
            throw new IOException("麦克风数据帧无效。");
        var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12));
        var kind = bytes[14];
        if (length > 256 || length % 2 != 0 || bytes.Length != length + 20 || bytes[15] != 0
            || kind is < 1 or > 3 || (kind != 1 && length != 0)
            || Crc32(bytes.AsSpan(0, length + 16)) != BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(length + 16)))
            throw new IOException("麦克风数据损坏，本次采音已取消。");
        return new(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)), kind, bytes[16..(16 + length)]);
    }
}
