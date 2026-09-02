namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Runs a page's independent read-backs so that one failing step cannot skip the rest.
/// </summary>
/// <remarks>
/// A page that refreshes several unrelated regions after a write — the boulder itself, its
/// activity feed, its comments, its beta clips — has no reason to treat them as one unit. Under a
/// single <c>try</c> the first throw abandons every later reload, so a comment that HAD reached the
/// server stayed invisible until the next navigation because an unrelated favourite read failed
/// first. Each step here is attempted regardless of what the previous one did; the first error
/// message is returned so the page can still say the refresh was incomplete.
/// </remarks>
public static class ReloadSequence
{
    /// <summary>
    /// Awaits every step in order, swallowing failures. Returns the first error message, or null
    /// when every step succeeded. <see cref="UnauthorizedAccessException"/> is not reported: an
    /// expired session is surfaced by the offline queue's own status pill, not by a toast per
    /// region.
    /// </summary>
    public static async Task<string?> RunAsync(params Func<Task>?[] steps)
    {
        string? failure = null;

        foreach (var step in steps)
        {
            if (step is null)
            {
                continue;
            }

            try
            {
                await step();
            }
            catch (UnauthorizedAccessException)
            {
                // Session expired mid-flush; the queue pauses itself and prompts a re-login.
            }
            catch (Exception ex)
            {
                failure ??= ex.Message;
            }
        }

        return failure;
    }
}
