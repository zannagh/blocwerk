namespace Blocwerk.Web.Controllers;

/// <summary>
/// Body of <c>POST /api/v1/maintenance/announce</c>. Every field is optional: an empty body is a
/// valid "I am about to restart, use the default wording and the default window".
/// </summary>
public sealed class MaintenanceAnnounceRequest
{
    /// <summary>
    /// One short line to show people. Sanitised and length-capped by the announcer before it is
    /// stored, and never treated as markup.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// How long the update is expected to take, in seconds. Also the notice's lifetime — it clears
    /// itself afterwards, so a deploy that never happens leaves nothing behind. Clamped by the
    /// announcer; absent or non-positive means the default window.
    /// </summary>
    public int? EtaSeconds { get; set; }
}
