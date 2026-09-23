using System.Buffers.Binary;
using System.Text;

namespace Blocwerk.Core.Capture;

/// <summary>One IFD entry: its type, count and the offset of its 4-byte value field.</summary>
internal readonly record struct ExifEntry(ushort Type, uint Count, int ValueOffset);

internal sealed class ExifTiffReader(byte[] data, bool little)
{
    public int U32(int at) => (int)(little
        ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at, 4))
        : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at, 4)));

    public Dictionary<ushort, ExifEntry> ReadIfd(int at)
    {
        var entries = new Dictionary<ushort, ExifEntry>();
        var count = U16(at);
        for (var i = 0; i < count && at + 2 + (i * 12) + 12 <= data.Length; i++)
        {
            var e = at + 2 + (i * 12);
            entries[U16(e)] = new ExifEntry(U16(e + 2), (uint)U32(e + 4), e + 8);
        }

        return entries;
    }

    public string? Ascii(Dictionary<ushort, ExifEntry> ifd, ushort tag)
    {
        if (!ifd.TryGetValue(tag, out var e) || e.Type != 2 || e.Count == 0 || e.Count > 512)
        {
            return null;
        }

        var at = e.Count <= 4 ? e.ValueOffset : U32(e.ValueOffset);
        var text = Encoding.ASCII.GetString(data, at, (int)e.Count).TrimEnd('\0', ' ');
        return text.Length == 0 ? null : text;
    }

    public double? Short(Dictionary<ushort, ExifEntry> ifd, ushort tag) =>
        ifd.TryGetValue(tag, out var e) && e.Type is 3 ? U16(e.ValueOffset)
        : ifd.TryGetValue(tag, out var l) && l.Type is 4 ? U32(l.ValueOffset)
        : null;

    public double? Rational(Dictionary<ushort, ExifEntry> ifd, ushort tag)
    {
        if (!ifd.TryGetValue(tag, out var e) || e.Type != 5)
        {
            return null;
        }

        var at = U32(e.ValueOffset);
        var den = (uint)U32(at + 4);
        return den == 0 ? null : (uint)U32(at) / (double)den;
    }

    private ushort U16(int at) => little
        ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at, 2))
        : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at, 2));
}
