namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// Orchestrates connecting a Blocwerk user to TopLogger and importing their logbook into
/// <see cref="Entities.ExternalAscent"/> rows. Connecting is gated behind the user having a password
/// set, so a shared OAuth-only session can never attach a third-party token store to the account.
/// </summary>
public interface ITopLoggerImportService
{
    /// <summary>
    /// Stores the supplied token pair for the user and returns success without pulling any data —
    /// the caller runs the initial <see cref="SyncAsync"/> as a separate phase. Refused (no tokens
    /// stored) when the user has no password set.
    /// </summary>
    Task<TopLoggerConnectResult> ConnectAsync(
        Guid userId,
        string accessToken,
        string refreshToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pulls new ticks since the last sync and upserts them as ascents, clustering each into an
    /// activity and mapping grades. Never throws on an auth failure — it flags the connection for
    /// reconnect and returns a failed result instead.
    /// </summary>
    Task<TopLoggerSyncResult> SyncAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the user's TopLogger connection, optionally deleting every ascent imported from it.
    /// </summary>
    Task DisconnectAsync(Guid userId, bool deleteImportedAscents, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the current connection state for rendering the profile section.
    /// </summary>
    Task<TopLoggerStatus> GetStatusAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the distinct raw grade tokens among the user's TopLogger ascents that still need a manual
    /// mapping, each with an ascent count and a sample climb name, ordered by count descending. The
    /// null/empty raw grade collapses into a single bucket keyed by the empty string.
    /// </summary>
    Task<IReadOnlyList<TopLoggerUnmappedGrade>> GetUnmappedGradesAsync(
        Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts the user's (raw grade → Font grade) resolution and retroactively applies it to every
    /// existing unmapped TopLogger ascent whose raw grade matches, so scoring picks them up. The Font
    /// grade is validated against the known grade scale; an unknown/blank grade is a no-op. Returns the
    /// number of ascents updated.
    /// </summary>
    Task<int> ResolveGradeMappingAsync(
        Guid userId, string rawGradeKey, string fontGrade, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the distinct gyms the user has TopLogger ascents at, for the points→grade calibration
    /// picker, ordered by name.
    /// </summary>
    Task<IReadOnlyList<TopLoggerGymRef>> GetCalibratableGymsAsync(
        Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a gym's saved points→grade calibration (per-grade base points, ascending) and its flash
    /// bonus. Empty points and a zero bonus mean the gym is uncalibrated.
    /// </summary>
    Task<GymCalibration> GetGymCalibrationAsync(Guid gymId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a gym's shared points→grade calibration (replacing its grade-point set with the supplied
    /// known-grade, positive-points rows and setting the flash bonus), then rehydrates the CURRENT
    /// user's topped/ticked ascents at that gym: their grade and flash/send are re-derived from the new
    /// calibration. Returns the number of the user's ascents updated.
    /// </summary>
    Task<int> SaveGymCalibrationAsync(
        Guid gymId,
        IReadOnlyList<GymGradePointDto> points,
        int flashBonusPoints,
        Guid userId,
        CancellationToken cancellationToken = default);
}
