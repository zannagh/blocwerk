namespace Blocwerk.Core.Services;

/// <summary>
/// A live "the server is about to be replaced" notice: what to tell people, when it was raised and
/// when it stops being true. Immutable — <see cref="IMaintenanceAnnouncer.Announce"/> replaces the
/// whole record rather than mutating one.
/// </summary>
/// <param name="Message">
/// The sanitised, length-capped text to show, or null for "use the client's default wording".
/// Never HTML: the announcer strips markup characters before this is built.
/// </param>
/// <param name="AnnouncedAt">When the announcement was raised.</param>
/// <param name="ExpiresAt">
/// When it stops being shown. Mandatory: a deploy that fails or never happens must not leave a
/// permanent banner behind, so the notice always dies on its own.
/// </param>
public sealed record MaintenanceAnnouncement(
    string? Message,
    DateTimeOffset AnnouncedAt,
    DateTimeOffset ExpiresAt);
