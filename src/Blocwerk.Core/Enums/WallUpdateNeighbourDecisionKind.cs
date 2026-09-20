namespace Blocwerk.Core.Enums;

/// <summary>What a <see cref="Entities.WallUpdateNeighbourDecision"/> row states about one staged panel hold.</summary>
public enum WallUpdateNeighbourDecisionKind
{
    /// <summary>A confirmed overlap correspondence to persist as a hold link on promote.</summary>
    Link = 0,

    /// <summary>The hold was marked physically absent and is deleted on promote.</summary>
    Removed = 1,
}
