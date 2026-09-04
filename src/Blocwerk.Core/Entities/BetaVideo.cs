using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

/// <summary>
/// A short "this is how it goes" clip somebody uploaded for a boulder.
/// </summary>
/// <remarks>
/// New clips live on disk (see <c>IBetaVideoStorage</c>); <see cref="StoragePath"/> holds the file
/// name and the clip is streamed from there, so large uploads never sit whole in memory. Legacy
/// rows created before that change still carry their bytes in <see cref="Data"/> (Postgres
/// <c>bytea</c>) — read sites must project the columns they need rather than materialising this
/// entity, or a list query would drag every legacy clip into memory.
/// </remarks>
public class BetaVideo
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid BoulderId { get; set; }

    [ForeignKey(nameof(BoulderId))]
    public Boulder Boulder { get; set; } = null!;

    public Guid UploadedByUserId { get; set; }

    [ForeignKey(nameof(UploadedByUserId))]
    public User UploadedBy { get; set; } = null!;

    [Required]
    [MaxLength(64)]
    public required string ContentType { get; set; }

    /// <summary>The original file name, kept only so a download has something sensible to be called.</summary>
    [MaxLength(256)]
    public string? FileName { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>File name of the clip in the beta-video store. Null only for legacy in-database clips.</summary>
    [MaxLength(512)]
    public string? StoragePath { get; set; }

    /// <summary>Legacy in-database clip bytes. Null for clips stored on disk (the current path).</summary>
    public byte[]? Data { get; set; }

    /// <summary>
    /// A JPEG poster frame grabbed in the browser before upload (see wwwroot/js/beta-video.js).
    /// Null when the browser could not decode the clip; the carousel then falls back to a
    /// placeholder tile rather than pulling the whole video down to draw a thumbnail.
    /// </summary>
    public byte[]? Thumbnail { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Where this clip is in the normalize-to-web-safe pipeline. New uploads start at
    /// <see cref="BetaVideoEncodingStatus.Pending"/> and the background normalizer drives them to
    /// <see cref="BetaVideoEncodingStatus.Ready"/>; the player only renders a &lt;video&gt; once ready.
    /// </summary>
    public BetaVideoEncodingStatus EncodingStatus { get; set; } = BetaVideoEncodingStatus.Pending;

    /// <summary>When the normalizer last produced a web-safe rendition of this clip. Null until it has.</summary>
    public DateTimeOffset? LastEncodedUtc { get; set; }

    /// <summary>The last ffmpeg failure for this clip, if any. Cleared on a successful encode.</summary>
    [MaxLength(1024)]
    public string? EncodingError { get; set; }
}
