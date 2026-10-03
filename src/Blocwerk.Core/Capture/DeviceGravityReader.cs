// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture;

/// <summary>
/// An iPhone's accelerometer reading at the moment of the shot (Apple maker note tag 0x0008 AccelerationVector), in g,
/// in the device frame (+X left, +Y bottom, +Z into the face; the vector points UP, ≈1 g when the phone is still).
/// The feature solve turns it into "up" (<c>wallgeometry/sfm/gravity.py</c>).
/// </summary>
/// <param name="X">aX, g.</param>
/// <param name="Y">aY, g.</param>
/// <param name="Z">aZ, g.</param>
public sealed record DeviceGravity(double X, double Y, double Z);

/// <summary>
/// Reads <see cref="DeviceGravity"/> from a photo's EXIF (JPEG APP1, PNG eXIf, WebP EXIF or a HEIC's Exif item): IFD0 →
/// EXIF IFD → MakerNote (0x927C) → the Apple maker note ("Apple iOS\0", a 2-byte version, a byte-order mark, then an IFD
/// at offset 14 whose offsets count from the maker note's first byte) → tag 0x0008, three SRATIONALs. Nothing else of
/// the maker note or the EXIF is kept. Never throws: other phones, a missing or malformed block → null.
/// </summary>
public static class DeviceGravityReader
{
    private const ushort TagExifIfd = 0x8769;
    private const ushort TagMakerNote = 0x927C;
    private const ushort TagAccelerationVector = 0x0008;
    private const int AppleIfdOffset = 14;
    private const int MaxMakerNoteBytes = 1 << 20;
    private const double MaxAbsG = 20;

    /// <summary>The photo's device gravity, or null.</summary>
    /// <param name="image">The uploaded bytes, before any metadata is stripped.</param>
    /// <returns>The vector, or null.</returns>
    public static DeviceGravity? Read(byte[] image)
    {
        try
        {
            var tiff = CapturePhotoFormat.Sniff(image) == CapturePhotoKind.Heic
                ? HeifExifLocator.FindTiff(image)
                : ExifCameraReader.FindTiff(image);
            return tiff.IsEmpty ? null : FromTiff(tiff.ToArray());
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or ArgumentException
                                       or InvalidDataException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>The vector from a TIFF (EXIF) block.</summary>
    /// <param name="tiff">The block, starting at its byte-order mark.</param>
    /// <returns>The vector, or null.</returns>
    internal static DeviceGravity? FromTiff(byte[] tiff)
    {
        if (!IsByteOrderMark(tiff, 0))
        {
            return null;
        }

        var reader = new ExifTiffReader(tiff, tiff[0] == 'I');
        var ifd0 = reader.ReadIfd(reader.U32(4));
        if (!ifd0.TryGetValue(TagExifIfd, out var exifEntry))
        {
            return null;
        }

        var exif = reader.ReadIfd(reader.U32(exifEntry.ValueOffset));
        var note = reader.Undefined(exif, TagMakerNote, MaxMakerNoteBytes);
        return note is null ? null : FromAppleMakerNote(note);
    }

    /// <summary>The vector from an Apple maker note (offsets relative to its first byte).</summary>
    /// <param name="note">The maker note's bytes.</param>
    /// <returns>The vector, or null when it is not Apple's or has no acceleration.</returns>
    internal static DeviceGravity? FromAppleMakerNote(byte[] note)
    {
        if (note.Length < AppleIfdOffset + 2 || !note.AsSpan().StartsWith("Apple iOS\0"u8) || !IsByteOrderMark(note, 12))
        {
            return null;
        }

        var reader = new ExifTiffReader(note, note[12] == 'I');
        var v = reader.SignedRationals(reader.ReadIfd(AppleIfdOffset), TagAccelerationVector, 3);
        if (v is null || v.Any(a => !double.IsFinite(a) || Math.Abs(a) > MaxAbsG))
        {
            return null;
        }

        return new DeviceGravity(v[0], v[1], v[2]);
    }

    private static bool IsByteOrderMark(byte[] data, int at) =>
        data.Length >= at + 8 && ((data[at] == 'I' && data[at + 1] == 'I') || (data[at] == 'M' && data[at + 1] == 'M'));
}
