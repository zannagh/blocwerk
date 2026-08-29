namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The hand-written reference rendered by <c>ApiDocs.razor</c>. Kept as plain data so a new
/// endpoint is documented by adding a record here, not by touching markup. The shapes mirror the
/// DTOs in <c>Blocwerk.Web.Controllers</c>; when a controller signature changes, update the matching
/// entry below.
/// </summary>
internal static class ApiDocsData
{
    private static readonly IReadOnlyList<ApiParamDoc> None = Array.Empty<ApiParamDoc>();

    /// <summary>Both API surfaces, in the order they are shown.</summary>
    public static IReadOnlyList<ApiSurfaceDoc> Surfaces { get; } = new[]
    {
        UserSurface,
        WallSurface,
    };

    private static ApiSurfaceDoc UserSurface => new(
        "User API",
        "Acts as you: logs sessions and attempts, records training, reads your progression. "
            + "Every route lives under /api/v1/me/ and needs a personal (User-scoped) key.",
        "User key",
        new[]
        {
            new ApiEndpointDoc(
                "GET",
                "/api/v1/me/attempts",
                "Your boulder attempts, newest data first, optionally narrowed to one wall.",
                new[] { new ApiParamDoc("wallId", "query", "Optional wall id to filter by.") },
                null,
                "[\n  {\n    \"id\": \"<guid>\",\n    \"boulderId\": \"<guid>\",\n    \"type\": \"Send\","
                    + "\n    \"timestamp\": \"2026-08-29T18:20:00+00:00\",\n    \"notes\": null,"
                    + "\n    \"clientRequestId\": \"<guid>\",\n    \"activityId\": \"<guid>\"\n  }\n]"),
            new ApiEndpointDoc(
                "POST",
                "/api/v1/me/attempts",
                "Logs an attempt. Reusing clientRequestId returns the stored attempt, so retries are safe.",
                None,
                "{\n  \"boulderId\": \"<guid>\",\n  \"type\": \"Send\","
                    + "\n  \"notes\": \"felt easy\",\n  \"clientRequestId\": \"<guid>\","
                    + "\n  \"timestamp\": \"2026-08-29T18:20:00+00:00\"\n}",
                "// same shape as one item of GET /attempts",
                "type is Attempt, Send or Flash (name or number). notes, clientRequestId and timestamp "
                    + "are optional; omit timestamp for \"now\"."),
            new ApiEndpointDoc(
                "DELETE",
                "/api/v1/me/attempts/{id}",
                "Removes one of your attempts.",
                new[] { new ApiParamDoc("id", "path", "The attempt id.") },
                null,
                null,
                "204 No Content on success."),
            new ApiEndpointDoc(
                "GET",
                "/api/v1/me/sessions/active",
                "The session that is currently open, or 404 when none is.",
                None,
                null,
                "{\n  \"id\": \"<guid>\",\n  \"wallId\": \"<guid>\","
                    + "\n  \"startedAt\": \"2026-08-29T17:00:00+00:00\",\n  \"endedAt\": null\n}"),
            new ApiEndpointDoc(
                "POST",
                "/api/v1/me/sessions",
                "Starts a session on a wall, ending any session still open.",
                None,
                "{\n  \"wallId\": \"<guid>\"\n}",
                "// same shape as GET /sessions/active"),
            new ApiEndpointDoc(
                "POST",
                "/api/v1/me/sessions/end",
                "Ends the open session. Idempotent: ending nothing still succeeds.",
                None,
                null,
                null,
                "204 No Content."),
            new ApiEndpointDoc(
                "GET",
                "/api/v1/me/training",
                "Recent hangboard and pull-up work, newest first.",
                new[] { new ApiParamDoc("activities", "query", "How many activities to expand (default 20, max 100).") },
                null,
                "{\n  \"hangboard\": [\n    {\n      \"id\": \"<guid>\",\n      \"edgeSizeMm\": 20,"
                    + "\n      \"additionalWeightKg\": 10,\n      \"durationSeconds\": 7,\n      \"sets\": 5,"
                    + "\n      \"timestamp\": \"...\",\n      \"notes\": null\n    }\n  ],"
                    + "\n  \"pullups\": [\n    {\n      \"id\": \"<guid>\",\n      \"repetitions\": 8,"
                    + "\n      \"additionalWeightKg\": 0,\n      \"sets\": 4,\n      \"timestamp\": \"...\","
                    + "\n      \"notes\": null\n    }\n  ]\n}"),
            new ApiEndpointDoc(
                "POST",
                "/api/v1/me/training/hangboard",
                "Records a hangboard session.",
                None,
                "{\n  \"edgeSizeMm\": 20,\n  \"additionalWeightKg\": 10,"
                    + "\n  \"durationSeconds\": 7,\n  \"sets\": 5,\n  \"notes\": null\n}",
                "// one hangboard item, as under GET /training",
                "edgeSizeMm, durationSeconds and sets must be positive."),
            new ApiEndpointDoc(
                "POST",
                "/api/v1/me/training/pullups",
                "Records a pull-up session.",
                None,
                "{\n  \"repetitions\": 8,\n  \"additionalWeightKg\": 0,\n  \"sets\": 4,\n  \"notes\": null\n}",
                "// one pullup item, as under GET /training",
                "repetitions and sets must be positive."),
            new ApiEndpointDoc(
                "DELETE",
                "/api/v1/me/training/hangboard/{id}",
                "Removes one hangboard session.",
                new[] { new ApiParamDoc("id", "path", "The hangboard session id.") },
                null,
                null,
                "204 No Content."),
            new ApiEndpointDoc(
                "DELETE",
                "/api/v1/me/training/pullups/{id}",
                "Removes one pull-up session.",
                new[] { new ApiParamDoc("id", "path", "The pull-up session id.") },
                null,
                null,
                "204 No Content."),
            new ApiEndpointDoc(
                "GET",
                "/api/v1/me/progression",
                "Your progression scores and per-bucket history.",
                None,
                null,
                "{\n  \"boulderScore\": 6.2,\n  \"boulderGrade\": \"6b\",\n  \"trainingScore\": 3.1,"
                    + "\n  \"windowDays\": 90,\n  \"groupBy\": \"Week\",\n  \"buckets\": [\n    {"
                    + "\n      \"start\": \"2026-08-01\",\n      \"end\": \"2026-08-08\",\n      \"label\": \"KW31\","
                    + "\n      \"boulderScore\": 6.0,\n      \"boulderGrade\": \"6a+\","
                    + "\n      \"trainingScore\": 2.8,\n      \"volumeMinutes\": 180\n    }\n  ]\n}"),
            new ApiEndpointDoc(
                "GET",
                "/api/v1/me/activities",
                "Activities (gap-clustered sessions) in the progression window, newest first.",
                None,
                null,
                "[\n  {\n    \"id\": \"<guid>\",\n    \"date\": \"2026-08-29\","
                    + "\n    \"startedAt\": \"...\",\n    \"durationMinutes\": 95,\n    \"boulderCount\": 12,"
                    + "\n    \"hangboardCount\": 1,\n    \"pullupCount\": 0,\n    \"wallName\": \"The Attic\"\n  }\n]"),
            new ApiEndpointDoc(
                "GET",
                "/api/v1/me/activities/{id}",
                "One activity in full: its boulders, training and duration.",
                new[] { new ApiParamDoc("id", "path", "The activity id.") },
                null,
                "{\n  \"id\": \"<guid>\",\n  \"startedAt\": \"...\",\n  \"durationMinutes\": 95,"
                    + "\n  \"durationIsManual\": false,\n  \"boulders\": [\n    {\n      \"boulderName\": \"Red arête\","
                    + "\n      \"grade\": \"6b\",\n      \"bestResult\": \"Send\",\n      \"attemptCount\": 3\n    }\n  ],"
                    + "\n  \"hangboard\": [ /* ... */ ],\n  \"pullups\": [ /* ... */ ],"
                    + "\n  \"wallName\": \"The Attic\"\n}"),
            new ApiEndpointDoc(
                "GET",
                "/api/v1/me/activity-grid",
                "Per-day intensity, for a heatmap of the last N weeks.",
                new[] { new ApiParamDoc("weeks", "query", "Weeks back to include (default 20, max 260).") },
                null,
                "[\n  { \"date\": \"2026-08-29\", \"intensity\": 3 }\n]"),
        });

    private static ApiSurfaceDoc WallSurface => new(
        "Wall API",
        "For devices bound to one wall: a sensor posts temperatures, a camera posts photos, and "
            + "anything holding the same key reads them back. Routes live under /api/walls/{wallId}/ "
            + "and need a Wall-scoped key, which is only valid for the wall it was issued for.",
        "Wall key",
        new[]
        {
            new ApiEndpointDoc(
                "POST",
                "/api/walls/{wallId}/temperature",
                "Records one temperature sample.",
                new[] { new ApiParamDoc("wallId", "path", "The wall the key is bound to.") },
                "// a bare number is accepted (what the Pi sends):\n24.3\n\n// or an object:\n{"
                    + "\n  \"temperatureCelsius\": 24.3,\n  \"recordedAt\": \"2026-08-29T18:20:00+00:00\"\n}",
                null,
                "recordedAt is optional; without it the server clock stamps the sample. Value must be "
                    + "finite and between -80 and 80 C. Returns 204 No Content."),
            new ApiEndpointDoc(
                "GET",
                "/api/walls/{wallId}/temperature",
                "Readings in a window, oldest first.",
                new[]
                {
                    new ApiParamDoc("wallId", "path", "The wall the key is bound to."),
                    new ApiParamDoc("from", "query", "ISO-8601 start, inclusive. Default: 24h ago."),
                    new ApiParamDoc("to", "query", "ISO-8601 end, exclusive. Default: now."),
                    new ApiParamDoc("maxSamples", "query", "Cap on rows (default 2000). Over the cap, the most recent are returned and X-Blocwerk-Truncated: true is set."),
                },
                null,
                "[\n  { \"recordedAt\": \"2026-08-29T18:20:00+00:00\", \"temperatureCelsius\": 24.3 }\n]",
                "A window longer than 90 days is clamped to the most recent 90 days."),
            new ApiEndpointDoc(
                "GET",
                "/api/walls/{wallId}/temperature/latest",
                "The most recent sample, or 404 when the wall has none.",
                new[] { new ApiParamDoc("wallId", "path", "The wall the key is bound to.") },
                null,
                "{ \"recordedAt\": \"2026-08-29T18:20:00+00:00\", \"temperatureCelsius\": 24.3 }"),
            new ApiEndpointDoc(
                "POST",
                "/api/walls/{wallId}/images",
                "Uploads one image (max 20 MB; jpeg, png or webp).",
                new[]
                {
                    new ApiParamDoc("wallId", "path", "The wall the key is bound to."),
                    new ApiParamDoc("caption", "query", "Optional caption (raw-body form, or a multipart field)."),
                    new ApiParamDoc("capturedAt", "query", "Optional ISO-8601 capture time (raw-body form, or a multipart field)."),
                },
                "// multipart/form-data with a 'file' part (+ optional caption/capturedAt fields)\n"
                    + "// OR the raw image bytes as the body with an image/* content type",
                "{\n  \"id\": \"<guid>\",\n  \"capturedAt\": \"2026-08-29T18:20:00+00:00\"\n}",
                "Returns 201 Created."),
            new ApiEndpointDoc(
                "GET",
                "/api/walls/{wallId}/images",
                "The wall's gallery, newest capture first, merged across all image sources.",
                new[]
                {
                    new ApiParamDoc("wallId", "path", "The wall the key is bound to."),
                    new ApiParamDoc("skip", "query", "Rows to skip (default 0)."),
                    new ApiParamDoc("take", "query", "Rows to return (default 50, max 200)."),
                },
                null,
                "[\n  {\n    \"id\": \"<guid>\",\n    \"source\": \"Uploaded\",\n    \"contentType\": \"image/jpeg\","
                    + "\n    \"sizeBytes\": 482910,\n    \"caption\": null,"
                    + "\n    \"capturedAt\": \"2026-08-29T18:20:00+00:00\"\n  }\n]",
                "source is the discriminator you pass back into the content route."),
            new ApiEndpointDoc(
                "GET",
                "/api/walls/{wallId}/images/{source}/{id}/content",
                "The raw bytes of one gallery entry.",
                new[]
                {
                    new ApiParamDoc("wallId", "path", "The wall the key is bound to."),
                    new ApiParamDoc("source", "path", "The gallery source: Uploaded, WallPhoto or ResetPhoto."),
                    new ApiParamDoc("id", "path", "The gallery entry id."),
                },
                null,
                null,
                "Responds with the image bytes and its content type, not JSON."),
            new ApiEndpointDoc(
                "DELETE",
                "/api/walls/{wallId}/images/{id}",
                "Deletes an uploaded image. Only uploads can be deleted, and only by a wall admin.",
                new[]
                {
                    new ApiParamDoc("wallId", "path", "The wall the key is bound to."),
                    new ApiParamDoc("id", "path", "The uploaded image id."),
                },
                null,
                null,
                "204 No Content."),
        });
}
