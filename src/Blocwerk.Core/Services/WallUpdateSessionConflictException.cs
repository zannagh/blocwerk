namespace Blocwerk.Core.Services;

/// <summary>
/// Thrown when a new big-wall update is started on a wall that already has one in flight. Carries the
/// existing session so the caller can offer "started by X at T — resume or discard?" instead of silently
/// destroying another admin's staged work, which is what the unconditional restart-discard used to do.
/// </summary>
public class WallUpdateSessionConflictException : UserFacingException
{
    public WallUpdateSessionConflictException(WallUpdateSessionInfo existing)
        : base(BuildMessage(existing))
    {
        Existing = existing;
    }

    /// <summary>The open session that blocked the new update.</summary>
    public WallUpdateSessionInfo Existing { get; }

    private static string BuildMessage(WallUpdateSessionInfo existing)
    {
        var who = existing.CreatedByName ?? "another admin";
        return $"This wall already has an update in progress, started by {who} on "
            + $"{existing.CreatedAt:yyyy-MM-dd HH:mm} UTC. Resume it, or discard it explicitly first.";
    }
}
