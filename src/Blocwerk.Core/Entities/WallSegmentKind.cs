namespace Blocwerk.Core.Entities;

/// <summary>
/// What a wall segment represents. Drives whether it is a climbable plane that gets folded into
/// the schematic, or ground that is trimmed out of it.
/// </summary>
public enum WallSegmentKind
{
    /// <summary>A climbable plane of the wall, projected by its inclination and yaw.</summary>
    Wall = 0,

    /// <summary>
    /// The floor/ground: not on the climbable wall. Excluded from the folded schematic, so the
    /// projection can trim away what is not part of the wall.
    /// </summary>
    Floor = 1,
}
