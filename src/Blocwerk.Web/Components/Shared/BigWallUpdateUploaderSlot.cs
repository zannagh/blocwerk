namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// One direction slot of <see cref="BigWallUpdateUploader"/>: its grid position relative to the centre
/// and the photo picked for it, or why the pick failed.
/// </summary>
internal sealed class BigWallUpdateUploaderSlot(string label, int col, int row, bool required)
{
    public string Label { get; } = label;

    public int Col { get; } = col;

    public int Row { get; } = row;

    public bool Required { get; } = required;

    public string InputId { get; } = $"bigupdate-slot-{label.ToLowerInvariant()}";

    public byte[]? Bytes { get; set; }

    public string? ContentType { get; set; }

    public string? FileName { get; set; }

    public bool Working { get; set; }

    public bool Invalid { get; set; }

    public string? Error { get; set; }

    public bool HasFile => Bytes is not null;

    /// <summary>The uploader's slots: the required centre and its four direct neighbours.</summary>
    public static List<BigWallUpdateUploaderSlot> CreateAll() =>
    [
        new("Centre", 0, 0, required: true),
        new("Left", -1, 0, required: false),
        new("Right", 1, 0, required: false),
        new("Up", 0, -1, required: false),
        new("Down", 0, 1, required: false),
    ];
}
