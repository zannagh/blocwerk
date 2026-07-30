namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Human-readable timestamps for feeds. Shared by the activity feed and the comment list so
/// the two never drift apart in wording.
/// </summary>
public static class TimeText
{
    /// <summary>A short "how long ago" label, falling back to a date beyond a week.</summary>
    public static string Relative(DateTimeOffset timestamp)
    {
        var diff = DateTimeOffset.UtcNow - timestamp;
        if (diff.TotalMinutes < 1)
        {
            return "just now";
        }

        if (diff.TotalHours < 1)
        {
            return $"{(int)diff.TotalMinutes}m ago";
        }

        if (diff.TotalDays < 1)
        {
            return $"{(int)diff.TotalHours}h ago";
        }

        if (diff.TotalDays < 1.5)
        {
            return "yesterday";
        }

        if (diff.TotalDays < 7)
        {
            return $"{(int)diff.TotalDays}d ago";
        }

        return timestamp.ToString("MMM d");
    }
}
