using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

public class Boulder
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    [Required]
    [MaxLength(256)]
    public required string Name { get; set; }

    [MaxLength(16)]
    public string? Grade { get; set; }

    public Guid CreatedByUserId { get; set; }

    [ForeignKey(nameof(CreatedByUserId))]
    public User CreatedBy { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Whether every kickboard foothold counts as on for this boulder.
    /// </summary>
    public bool KickboardFootholdsOn { get; set; } = true;

    /// <summary>
    /// When true the boulder has no dedicated footholds: every hand hold doubles as a
    /// foot hold, and marking a hold <see cref="HoldUsage.FootOnly"/> is invalid.
    /// </summary>
    public bool HandsFollowFeet { get; set; } = true;

    /// <summary>
    /// A hold color key from <c>HoldPalette</c>. When set, every hold on the wall of that
    /// color counts as a foothold for this boulder, on top of the boulder's own holds.
    /// </summary>
    [MaxLength(32)]
    public string? FootColorOnly { get; set; }

    /// <summary>
    /// When true, matching a hold (using two hands / hand and foot on the same hold) is not
    /// allowed for this boulder.
    /// </summary>
    public bool NoMatch { get; set; }

    /// <summary>
    /// A free-text rule the setter added to this boulder ("no kickboard", "start matched"). Plain text,
    /// at most <see cref="CustomRuleMaxLength"/> characters, trimmed; null when there is none.
    /// </summary>
    [MaxLength(CustomRuleMaxLength)]
    public string? CustomRule { get; set; }

    /// <summary>The longest custom rule, in characters.</summary>
    public const int CustomRuleMaxLength = 200;

    /// <summary>
    /// Derived from the boulder's foothold rules; kept so read-sites can keep asking the
    /// single "does this boulder define its own footholds?" question. A boulder that
    /// neither marks dedicated footholds nor names a foot color leaves the kickboard rule
    /// in charge.
    /// </summary>
    [NotMapped]
    public FootholdMode FootholdMode =>
        HandsFollowFeet && string.IsNullOrEmpty(FootColorOnly)
            ? FootholdMode.AllKickboard
            : FootholdMode.DefinedOnly;

    /// <summary>
    /// A draft is only visible to its creator until it is published.
    /// </summary>
    public bool IsDraft { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public bool IsArchived { get; set; }

    /// <summary>
    /// The style tags the boulder carries, any combination. Stays on the same row through revisions, so a boulder
    /// keeps its tags in every generation.
    /// </summary>
    public BoulderStyle Styles { get; set; }

    /// <summary>
    /// The hold type the boulder is mainly climbed on, when somebody set it; null leaves it to the boulder's holds
    /// (<c>BoulderHoldTypes.Effective</c>). Stays on the same row through revisions, as the style tags do.
    /// </summary>
    public HoldHandType? DominantHoldType { get; set; }

    public bool IsHistoric { get; set; }

    public bool NeedsReview { get; set; }

    public int Generation { get; set; }

    /// <summary>
    /// The boulder this one is a variant of, on the same wall. Always one level deep: a parent has variants, a variant
    /// has none of its own. Null for a parent and for a boulder outside any family.
    /// </summary>
    public Guid? ParentBoulderId { get; set; }

    public ICollection<BoulderHold> BoulderHolds { get; set; } = [];

    public ICollection<Attempt> Attempts { get; set; } = [];

    public ICollection<BoulderComment> Comments { get; set; } = [];

    public ICollection<BetaVideo> BetaVideos { get; set; } = [];

    public ICollection<GradeProposal> GradeProposals { get; set; } = [];

    public ICollection<BoulderRating> Ratings { get; set; } = [];

    public ICollection<BoulderFavorite> Favorites { get; set; } = [];

    /// <summary>
    /// Who set the boulder on the wall (one or more co-setters). Distinct from
    /// <see cref="CreatedBy"/>, which is whoever added it in the app.
    /// </summary>
    public ICollection<BoulderSetter> Setters { get; set; } = [];
}
