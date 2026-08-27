namespace Blocwerk.Core.Services;

/// <summary>
/// One row of a wall's member leaderboard. <see cref="HardestSendScore"/> is the numeric grade score
/// (including the flash bonus) used purely for sorting; <see cref="HardestSendGrade"/> is the grade to
/// display and is null when the member has no send on this wall. <see cref="Score"/> is the wall-scoped
/// rolling boulder rating and <see cref="VolumeMinutes"/> the all-time training minutes on this wall.
/// </summary>
public record WallLeaderboardEntry(
    Guid UserId,
    string DisplayName,
    string? HardestSendGrade,
    int HardestSendScore,
    int Score,
    int VolumeMinutes);
