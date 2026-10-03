using System.Buffers.Binary;
using System.Text;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Inserts a realistic metadata block into a JPEG: an EXIF APP1 (Make, Model, Orientation, focal
/// lengths, lens model, and a GPS IFD with a latitude), an XMP APP1 and a COM segment — so the
/// reader and the stripper are tested against the things an iPhone photo really carries.
/// </summary>
internal static class ExifJpeg
{
    public const string XmpSecret = "<exif:GPSLatitude>48,8.5117N</exif:GPSLatitude>";

    /// <summary>The GPS latitude rationals (48/1, 8/1, 3071/100), big-endian, as they sit in the file.</summary>
    public static readonly byte[] GpsLatitudeBytes = Rationals((48, 1), (8, 1), (3071, 100));

    public static byte[] Build(byte[] jpeg, double focal35 = 14, string lens = "iPhone 16 Pro back triple camera 2.22mm f/2.2")
    {
        var tiff = Tiff(focal35, lens);
        var exif = Segment(0xE1, [.. "Exif\0\0"u8.ToArray(), .. tiff]);
        var xmp = Segment(0xE1, Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0" + XmpSecret));
        var comment = Segment(0xFE, Encoding.ASCII.GetBytes("shot at home"));
        return [.. jpeg.AsSpan(0, 2), .. exif, .. xmp, .. comment, .. jpeg.AsSpan(2)];
    }

    private static byte[] Segment(byte marker, byte[] payload)
    {
        var s = new byte[4 + payload.Length];
        s[0] = 0xFF;
        s[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(s.AsSpan(2), (ushort)(payload.Length + 2));
        payload.CopyTo(s, 4);
        return s;
    }

    /// <summary>Big-endian TIFF: IFD0 at 8, then the EXIF IFD, the GPS IFD and the value area.</summary>
    private static byte[] Tiff(double focal35, string lens)
    {
        var data = new List<byte>();
        var make = Ascii("Apple");
        var model = Ascii("iPhone 16 Pro");
        var lensBytes = Ascii(lens);
        const int ifd0 = 8, ifd0Size = 2 + (5 * 12) + 4;
        const int exifIfd = ifd0 + ifd0Size, exifSize = 2 + (3 * 12) + 4;
        const int gpsIfd = exifIfd + exifSize, gpsSize = 2 + (2 * 12) + 4;
        var values = gpsIfd + gpsSize;
        var makeAt = values;
        var modelAt = makeAt + make.Length;
        var lensAt = modelAt + model.Length;
        var focalAt = lensAt + lensBytes.Length;
        var latAt = focalAt + 8;

        data.AddRange("MM"u8.ToArray());
        data.AddRange(U16(42));
        data.AddRange(U32(ifd0));
        data.AddRange(Ifd(
            (0x010F, 2, (uint)make.Length, (uint)makeAt),
            (0x0110, 2, (uint)model.Length, (uint)modelAt),
            (0x0112, 3, 1, 6u << 16),
            (0x8769, 4, 1, exifIfd),
            (0x8825, 4, 1, gpsIfd)));
        data.AddRange(Ifd(
            (0x920A, 5, 1, (uint)focalAt),
            (0xA405, 3, 1, (uint)focal35 << 16),
            (0xA434, 2, (uint)lensBytes.Length, (uint)lensAt)));
        data.AddRange(Ifd((0x0001, 2, 2, (uint)'N' << 24), (0x0002, 5, 3, (uint)latAt)));
        data.AddRange(make);
        data.AddRange(model);
        data.AddRange(lensBytes);
        data.AddRange(Rationals((222, 100)));
        data.AddRange(GpsLatitudeBytes);
        return [.. data];
    }

    private static byte[] Ifd(params (ushort Tag, ushort Type, uint Count, uint Value)[] entries)
    {
        var bytes = new List<byte>(U16((ushort)entries.Length));
        foreach (var (tag, type, count, value) in entries)
        {
            bytes.AddRange(U16(tag));
            bytes.AddRange(U16(type));
            bytes.AddRange(U32(count));
            bytes.AddRange(U32(value));
        }

        bytes.AddRange(U32(0));
        return [.. bytes];
    }

    private static byte[] Rationals(params (uint Num, uint Den)[] values) =>
        values.SelectMany(v => U32(v.Num).Concat(U32(v.Den))).ToArray();

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text + "\0");

    private static byte[] U16(ushort v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, v);
        return b;
    }

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }
}
