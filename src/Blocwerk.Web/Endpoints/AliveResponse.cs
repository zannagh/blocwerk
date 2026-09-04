namespace Blocwerk.Web.Endpoints;

/// <summary>The body of <c>GET /alive</c>.</summary>
/// <param name="InstanceId">
/// Identifies the serving process. A client captures this once and reloads only when it later sees
/// a DIFFERENT value — that, and not a reachable socket, is what proves a new container is up.
/// </param>
/// <param name="StartedAt">When the serving process started.</param>
/// <param name="Maintenance">True while an announcement is live.</param>
/// <param name="Message">The announcement's text, or null for "use the client's default wording".</param>
/// <param name="MaintenanceExpiresAt">
/// When the announcement stops being shown. Null when none is live. A client that has been
/// disconnected past this point should stop claiming an update is in progress.
/// </param>
public sealed record AliveResponse(
    string InstanceId,
    DateTimeOffset StartedAt,
    bool Maintenance,
    string? Message,
    DateTimeOffset? MaintenanceExpiresAt);
