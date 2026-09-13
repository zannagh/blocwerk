using System.ComponentModel.DataAnnotations;

namespace Blocwerk.Core.Entities;

/// <summary>
/// Content-addressed storage for byte[] column values referenced by <see cref="ChangeJournalEntry"/>
/// before/after images. Keyed by the lower-case hex SHA-256 of the bytes so identical blobs are
/// stored once and shared across every entry that references them.
/// </summary>
public class JournalBlob
{
    /// <summary>Lower-case hex SHA-256 of <see cref="Bytes"/> — the content-address primary key.</summary>
    [Key]
    [MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;

    public byte[] Bytes { get; set; } = [];

    /// <summary>The byte length of <see cref="Bytes"/>, denormalised for cheap size reporting.</summary>
    public long Len { get; set; }
}
