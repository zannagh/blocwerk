using System.Diagnostics;

namespace Blocwerk.Core.Telemetry;

/// <summary>
/// Convenience recorders so call sites stay one-liners and the tag conventions
/// (anonymized wall id, change kinds, detection source) live in exactly one place.
/// </summary>
public static partial class BlocwerkMetrics
{
    public static void RecordWallCreated(Guid wallId) =>
        WallsCreated.Add(1, WallTag(wallId));

    public static void RecordWallRecreated(Guid wallId, int bouldersMadeHistoric, int holdsPruned)
    {
        var tags = new TagList
        {
            WallTag(wallId),
            { "boulders_made_historic", bouldersMadeHistoric },
            { "holds_pruned", holdsPruned },
        };
        WallsRecreated.Add(1, tags);
    }

    public static void RecordWallPhotoStaged(Guid wallId, string mode) =>
        WallPhotosStaged.Add(1, WallTag(wallId), new("mode", mode));

    public static void RecordWallPhotoConfirmed(Guid wallId, string mode) =>
        WallPhotosConfirmed.Add(1, WallTag(wallId), new("mode", mode));

    public static void RecordHoldAdded(Guid wallId) =>
        HoldsAdded.Add(1, WallTag(wallId));

    /// <summary>
    /// A hold edit. <paramref name="changeKind"/> is one of moved/color/shape/named/modified/merged.
    /// </summary>
    public static void RecordHoldUpdated(Guid wallId, string changeKind) =>
        HoldsUpdated.Add(1, WallTag(wallId), new("change_kind", changeKind));

    public static void RecordHoldDeleted(Guid wallId) =>
        HoldsDeleted.Add(1, WallTag(wallId));

    public static void RecordBoulderCreated(Guid wallId, bool isDraft) =>
        BouldersCreated.Add(1, WallTag(wallId), new("draft", isDraft));

    public static void RecordBoulderDeleted(Guid wallId) =>
        BouldersDeleted.Add(1, WallTag(wallId));

    public static void RecordAttemptLogged(Guid wallId) =>
        AttemptsLogged.Add(1, WallTag(wallId));

    public static void RecordCommentAdded(Guid wallId) =>
        CommentsAdded.Add(1, WallTag(wallId));

    public static void RecordSessionStarted(Guid wallId) =>
        SessionsStarted.Add(1, WallTag(wallId));

    public static void RecordMemberJoined(Guid wallId) =>
        MembersJoined.Add(1, WallTag(wallId));

    /// <summary>
    /// Records one hold-detection run: its count, its duration, and how it was produced.
    /// </summary>
    /// <param name="wallId">The wall that was processed.</param>
    /// <param name="source">What triggered it: upload/stage/recreate/redetect.</param>
    /// <param name="detector">Which path produced the holds: yolo/color/none.</param>
    /// <param name="holdsDetected">How many holds were detected.</param>
    /// <param name="durationMs">How long it took to run.</param>
    public static void RecordImageRecognition(Guid? wallId, string source, string detector, int holdsDetected, double durationMs)
    {
        var tags = new TagList
        {
            { "source", source },
            { "detector", detector },
        };
        if (wallId.HasValue)
        {
            tags.Add(WallTag(wallId.Value));
        }

        ImageRecognitionRuns.Add(1, tags);
        HoldsDetected.Add(holdsDetected, tags);
        ImageRecognitionDuration.Record(durationMs, tags);
    }

    /// <summary>
    /// Records one image-alignment run. <paramref name="outcome"/> is aligned/none/failed.
    /// </summary>
    public static void RecordImageAlignment(string outcome, double durationMs)
    {
        var tag = new KeyValuePair<string, object?>("outcome", outcome);
        ImageAlignmentRuns.Add(1, tag);
        ImageAlignmentDuration.Record(durationMs, tag);
    }

    /// <summary>
    /// Times a service operation: opens an <see cref="Otel.ActivitySource"/> span (so it shows up
    /// as a trace with timing in Jaeger) and records <see cref="OperationDuration"/> on dispose.
    /// Wrap a method body in <c>using var _ = BlocwerkMetrics.TimeOperation("Wall.Create");</c>.
    /// </summary>
    /// <param name="operation">The operation name.</param>
    /// <param name="wallId">The wall ID.</param>
    /// <returns>An instance of <see cref="OperationTimer"/> for the operation and wall.</returns>
    public static OperationTimer TimeOperation(string operation, Guid? wallId = null) =>
        new(operation, wallId);

    private static KeyValuePair<string, object?> WallTag(Guid wallId) =>
        new("wall", AnonymizeWallId(wallId));
}
