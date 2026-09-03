namespace Blocwerk.Core.Services;

/// <summary>
/// What deleting a given account would do, computed without changing anything. Drives the
/// confirmation screen so the person sees the wall consequences before they type anything.
/// </summary>
public sealed class AccountDeletionPreview
{
    /// <summary>
    /// Walls this user solely owns with nobody who could take them over. While this is non-empty the
    /// deletion is refused: the person must hand the wall to a co-admin or delete it first.
    /// </summary>
    public IReadOnlyList<string> BlockingWallNames { get; init; } = [];

    /// <summary>
    /// Walls that would change hands, as "wall name" → "the admin it goes to". Shown so nobody is
    /// surprised by who ends up owning their gym wall.
    /// </summary>
    public IReadOnlyList<AccountDeletionWallTransfer> WallTransfers { get; init; } = [];

    /// <summary>Boulders the user created that stay on their walls, credited to the placeholder.</summary>
    public int BouldersKept { get; init; }

    /// <summary>Comments the user wrote that stay, credited to the placeholder.</summary>
    public int CommentsKept { get; init; }

    /// <summary>Logged attempts that stay, so the boulders' send counts do not silently change.</summary>
    public int AttemptsKept { get; init; }

    /// <summary>Wall memberships that are dropped outright, taking their kiosk PIN and consent.</summary>
    public int MembershipsRemoved { get; init; }

    /// <summary>Personal training sessions (hangboard, pull-up, climbing) erased outright.</summary>
    public int TrainingSessionsRemoved { get; init; }

    /// <summary>Beta clips the user uploaded, erased outright because they show the person.</summary>
    public int BetaVideosRemoved { get; init; }

    /// <summary>True when nothing blocks the deletion.</summary>
    public bool CanDelete => BlockingWallNames.Count == 0;
}
