// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.Retention;

namespace Blocwerk.Core.Capture;

/// <summary>What one sweep removed.</summary>
/// <param name="Drafts">Drafts removed.</param>
/// <param name="ExpiredPhotos">Photos removed by the photo retention.</param>
/// <param name="OrphanFiles">Files no row referenced (and temp files).</param>
public sealed record CaptureSweepResult(int Drafts, int ExpiredPhotos, int OrphanFiles)
{
    /// <summary>Retired models whose textures and photo-real view went (in a dry run: would go).</summary>
    public RetentionOutcome SupersededModels { get; init; } = RetentionOutcome.None;

    /// <summary>3D runner jobs whose trained result and prepared state went (in a dry run: would go).</summary>
    public RetentionOutcome RunnerResults { get; init; } = RetentionOutcome.None;

    /// <summary>Abandoned capture import folders removed (in a dry run: would be).</summary>
    public RetentionOutcome AbandonedImports { get; init; } = RetentionOutcome.None;

    /// <summary>Bytes actually freed on disk by the whole sweep (a dry run's rules count 0).</summary>
    public long FreedBytes { get; init; }
}
