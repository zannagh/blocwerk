// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Services;

/// <summary>
/// The change journal's production replay path, for app administrators only: list a wall's batches, export them as a
/// package, replay a package from another instance, and revert a batch. Replay and revert default to a dry run at the
/// API; the schema-parity check of the replay can never be forced past here. Every call throws
/// <see cref="UnauthorizedAccessException"/> when the acting user is not an administrator of this installation.
/// </summary>
public interface IChangeJournalAdminService
{
    /// <summary>
    /// The acting user's id when they administer this installation, else <see cref="UnauthorizedAccessException"/>.
    /// For a caller that must refuse before reading a large request body; every other method checks again itself.
    /// </summary>
    Task<Guid> EnsureAdminAsync(CancellationToken ct);

    /// <summary>
    /// The batches scoped to <paramref name="wallId"/>, oldest first. With <paramref name="includeUnscoped"/>, also the
    /// unscoped ("adhoc") batches created at or after <paramref name="since"/> whose entries mention the wall's id
    /// (that search reads entry text, so it needs the time bound).
    /// </summary>
    Task<IReadOnlyList<ChangeJournalBatchSummary>> ListBatchesAsync(
        Guid wallId, bool includeUnscoped, DateTimeOffset? since, CancellationToken ct);

    /// <summary>The package for the given batches (photos inside as blobs), in the source's write order.</summary>
    Task<ChangeJournalPackage> ExportAsync(IReadOnlyCollection<Guid> batchIds, CancellationToken ct);

    /// <summary>
    /// Replays <paramref name="package"/> onto this database. A package whose EF migration differs from this database's
    /// is refused even with <paramref name="force"/>: the report then carries it as its only fatal error.
    /// </summary>
    Task<ChangeJournalReplayReport> ReplayAsync(ChangeJournalPackage package, bool dryRun, bool force, CancellationToken ct);

    /// <summary>Reverts one batch, or with <paramref name="dryRun"/> only reports the conflicts a revert would hit.</summary>
    Task<ChangeJournalRevertResult> RevertAsync(Guid batchId, bool dryRun, bool force, CancellationToken ct);
}
