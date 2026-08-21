using System.Diagnostics.Metrics;
using System.Security.Cryptography;

namespace Blocwerk.Core.Telemetry;

/// <summary>
/// The single catalog of Blocwerk's custom metrics. Every instrument hangs off
/// <see cref="Otel.Meter"/>, which <c>Program.cs</c> already wires to the OTLP exporter, so
/// nothing here needs its own export plumbing.
/// <para>
/// Per-wall breakdowns are tagged with an <em>anonymized</em> wall id (see
/// <see cref="AnonymizeWallId"/>) so a dashboard can slice by wall without the wall's real id
/// or name ever leaving the process.
/// </para>
/// </summary>
public static partial class BlocwerkMetrics
{
    // --- Event counters ---------------------------------------------------
    public static readonly Counter<long> WallsCreated = Otel.Meter.CreateCounter<long>(
        "blocwerk.walls.created", unit: "{wall}", description: "Walls created.");

    public static readonly Counter<long> WallsRecreated = Otel.Meter.CreateCounter<long>(
        "blocwerk.walls.recreated", unit: "{wall}", description: "Wall recreations confirmed (full re-make).");

    public static readonly Counter<long> WallPhotosStaged = Otel.Meter.CreateCounter<long>(
        "blocwerk.walls.photos_staged", unit: "{photo}", description: "Wall photos staged (any staging mode).");

    public static readonly Counter<long> WallPhotosConfirmed = Otel.Meter.CreateCounter<long>(
        "blocwerk.walls.photos_confirmed", unit: "{photo}", description: "Staged wall photos confirmed into a new generation.");

    public static readonly Counter<long> HoldsAdded = Otel.Meter.CreateCounter<long>(
        "blocwerk.holds.added", unit: "{hold}", description: "Holds added to a wall.");

    public static readonly Counter<long> HoldsUpdated = Otel.Meter.CreateCounter<long>(
        "blocwerk.holds.updated", unit: "{hold}", description: "Hold edits (tagged by change kind: moved/color/shape/named/modified/merged).");

    public static readonly Counter<long> HoldsDeleted = Otel.Meter.CreateCounter<long>(
        "blocwerk.holds.deleted", unit: "{hold}", description: "Holds deleted from a wall.");

    public static readonly Counter<long> BouldersCreated = Otel.Meter.CreateCounter<long>(
        "blocwerk.boulders.created", unit: "{boulder}", description: "Boulders created (tagged draft=true/false).");

    public static readonly Counter<long> BouldersDeleted = Otel.Meter.CreateCounter<long>(
        "blocwerk.boulders.deleted", unit: "{boulder}", description: "Boulders deleted.");

    public static readonly Counter<long> AttemptsLogged = Otel.Meter.CreateCounter<long>(
        "blocwerk.attempts.logged", unit: "{attempt}", description: "Climbing attempts logged.");

    public static readonly Counter<long> CommentsAdded = Otel.Meter.CreateCounter<long>(
        "blocwerk.comments.added", unit: "{comment}", description: "Boulder comments added.");

    public static readonly Counter<long> BetaVideosUploaded = Otel.Meter.CreateCounter<long>(
        "blocwerk.beta_videos.uploaded", unit: "{video}", description: "Beta videos uploaded to a boulder.");

    public static readonly Counter<long> BetaVideoBytesUploaded = Otel.Meter.CreateCounter<long>(
        "blocwerk.beta_videos.bytes", unit: "By", description: "Bytes of beta video stored (the blobs live in Postgres, so this is the thing to watch).");

    public static readonly Counter<long> SessionsStarted = Otel.Meter.CreateCounter<long>(
        "blocwerk.sessions.started", unit: "{session}", description: "Climbing sessions started.");

    public static readonly Counter<long> MembersJoined = Otel.Meter.CreateCounter<long>(
        "blocwerk.members.joined", unit: "{member}", description: "Members that joined a wall via share link.");

    // --- Image recognition ------------------------------------------------
    public static readonly Counter<long> ImageRecognitionRuns = Otel.Meter.CreateCounter<long>(
        "blocwerk.image_recognition.runs", unit: "{run}", description: "Hold-detection runs (tagged by source and detector).");

    public static readonly Counter<long> HoldsDetected = Otel.Meter.CreateCounter<long>(
        "blocwerk.image_recognition.holds_detected", unit: "{hold}", description: "Holds returned by detection runs.");

    public static readonly Counter<long> ImageAlignmentRuns = Otel.Meter.CreateCounter<long>(
        "blocwerk.image_alignment.runs", unit: "{run}", description: "Image alignment runs (tagged by outcome: aligned/none/failed).");

    // --- Histograms (response / processing times) -------------------------
    public static readonly Histogram<double> OperationDuration = Otel.Meter.CreateHistogram<double>(
        "blocwerk.operation.duration", unit: "ms", description: "Duration of instrumented service operations, tagged by operation name.");

    public static readonly Histogram<double> ImageRecognitionDuration = Otel.Meter.CreateHistogram<double>(
        "blocwerk.image_recognition.duration", unit: "ms", description: "Wall-clock time of a hold-detection run.");

    public static readonly Histogram<double> ImageAlignmentDuration = Otel.Meter.CreateHistogram<double>(
        "blocwerk.image_alignment.duration", unit: "ms", description: "Wall-clock time of an image alignment run.");

    // --- Observable gauges (live snapshots) -------------------------------
    // Backed by fields updated elsewhere: connected circuits by the Blazor circuit handler,
    // the DB totals by TelemetryStatsCollector. Interlocked keeps the cross-thread reads clean.
    private static long connectedCircuits;
    private static long totalWalls;
    private static long totalBoulders;
    private static long totalUsers;
    private static long totalHolds;
    private static long activeSessions;

    static BlocwerkMetrics()
    {
        Otel.Meter.CreateObservableGauge(
            "blocwerk.users.connected",
            () => Interlocked.Read(ref connectedCircuits),
            unit: "{circuit}",
            description: "Currently connected Blazor circuits (a live browser tab ~= a connected user).");

        Otel.Meter.CreateObservableGauge(
            "blocwerk.walls.total",
            () => Interlocked.Read(ref totalWalls),
            unit: "{wall}",
            description: "Total walls in the database.");

        Otel.Meter.CreateObservableGauge(
            "blocwerk.boulders.total",
            () => Interlocked.Read(ref totalBoulders),
            unit: "{boulder}",
            description: "Total non-archived boulders in the database.");

        Otel.Meter.CreateObservableGauge(
            "blocwerk.users.total",
            () => Interlocked.Read(ref totalUsers),
            unit: "{user}",
            description: "Total registered users.");

        Otel.Meter.CreateObservableGauge(
            "blocwerk.holds.total",
            () => Interlocked.Read(ref totalHolds),
            unit: "{hold}",
            description: "Total holds across all walls and generations.");

        Otel.Meter.CreateObservableGauge(
            "blocwerk.sessions.active",
            () => Interlocked.Read(ref activeSessions),
            unit: "{session}",
            description: "Climbing sessions currently open.");
    }

    /// <summary>
    /// Forces the static constructor to run so the observable gauges register at startup,
    /// even before any counter has been touched. Called once from <c>Program.cs</c>.
    /// </summary>
    public static void Initialize()
    {
        // Referencing any member is enough to trigger the type initializer.
    }

    // --- Live-value updates (called by the circuit handler / stats collector) ---
    public static void CircuitOpened() => Interlocked.Increment(ref connectedCircuits);

    public static void CircuitClosed() => Interlocked.Decrement(ref connectedCircuits);

    /// <summary>
    /// Publishes the latest database totals for the observable gauges to read.
    /// </summary>
    public static void UpdateStats(long walls, long boulders, long users, long holds, long sessions)
    {
        Interlocked.Exchange(ref totalWalls, walls);
        Interlocked.Exchange(ref totalBoulders, boulders);
        Interlocked.Exchange(ref totalUsers, users);
        Interlocked.Exchange(ref totalHolds, holds);
        Interlocked.Exchange(ref activeSessions, sessions);
    }

    /// <summary>
    /// A short, stable, non-reversible tag for a wall so per-wall metrics can be sliced
    /// without exposing the real id. Same wall id always maps to the same tag.
    /// </summary>
    public static string AnonymizeWallId(Guid wallId)
    {
        var hash = SHA256.HashData(wallId.ToByteArray());
        return Convert.ToHexStringLower(hash.AsSpan(0, 6));
    }
}
