namespace Blocwerk.Core.Enums;

/// <summary>
/// How two holds on adjacent big-wall panels relate when they are linked together.
/// </summary>
public enum HoldLinkKind
{
    /// <summary>The two holds are the same physical hold seen in both overlapping images.</summary>
    Same = 0,

    /// <summary>The hold was physically moved between the two captures but is otherwise the same hold.</summary>
    Moved = 1,
}
