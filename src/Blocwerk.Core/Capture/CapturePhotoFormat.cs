namespace Blocwerk.Core.Capture;

/// <summary>The photo encodings a capture accepts, sniffed from the bytes (never the file name).</summary>
public enum CapturePhotoKind
{
    Unknown,
    Jpeg,
    Png,

    /// <summary>HEIC/HEIF: converted to JPEG on upload when a converter is available (see <see cref="ICapturePhotoConverter"/>), refused otherwise.</summary>
    Heic,
}

/// <summary>Magic-byte sniffing for capture uploads.</summary>
public static class CapturePhotoFormat
{
    public static CapturePhotoKind Sniff(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return CapturePhotoKind.Jpeg;
        }

        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(PngSignature))
        {
            return CapturePhotoKind.Png;
        }

        // ISO-BMFF: "....ftyp" followed by a HEIF brand.
        if (bytes.Length >= 12 && bytes.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            var brand = System.Text.Encoding.ASCII.GetString(bytes.Slice(8, 4));
            if (brand is "heic" or "heix" or "hevc" or "hevx" or "heim" or "heis" or "mif1" or "msf1" or "avif")
            {
                return CapturePhotoKind.Heic;
            }
        }

        return CapturePhotoKind.Unknown;
    }

    public static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static string Extension(CapturePhotoKind kind) => kind == CapturePhotoKind.Png ? ".png" : ".jpg";

    public static string ContentType(CapturePhotoKind kind) => kind == CapturePhotoKind.Png ? "image/png" : "image/jpeg";
}
