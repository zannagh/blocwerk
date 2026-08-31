namespace Blocwerk.Web.State;

/// <summary>
/// One in-flight editing session tracked by <see cref="EditActivityRegistry"/>. Keyed in the
/// registry by a per-lease <see cref="Guid"/>, so the same user opening two editors counts twice
/// and each is removed independently when its own lease is disposed.
/// </summary>
public sealed record EditActivityEntry(
    EditKind EditKind,
    Guid? WallId,
    Guid? UserId,
    DateTimeOffset StartedUtc);
