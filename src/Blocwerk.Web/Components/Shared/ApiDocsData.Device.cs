// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Components.Shared;

/// <summary>The reference for the routes a linked device key (the companion app) may read.</summary>
internal static partial class ApiDocsData
{
    private const string DeviceIntro =
        "What the companion app reads and writes: the same content-addressed documents, images and feed the offline web client "
        + "reads, and your own marks on a boulder as it writes them. Only a device key works here, the personal key the app gets when you link it (listed as \"iPhone: …\" "
        + "under Settings → API keys and revocable there). A personal key you create on this page gets 404, and so does "
        + "a wall, kiosk or installation key. The key reads as you: exactly the walls, boulders and audiences your own "
        + "sign-in sees, a share token works as in the browser, and a wall you cannot see answers 403 (documents, feed) "
        + "or 404 (images, manifest). Send Authorization: Bearer <key>; no cookie and no antiforgery token are needed.";

    private static ApiParamDoc WallParam => new("wallId", "path", "A wall you can see.");

    private const string MarksNotes =
        "Device key only; 60 writes per key, then 1 a second (429). Refusals are { message, permanent }: permanent true "
        + "(400 bad body, 404 unknown boulder or not your wall) means never retry; false (401, 500) means retry later.";

    private static ApiParamDoc BoulderParam => new("boulderId", "path", "A boulder on one of your walls.");

    private static ApiParamDoc TokenParam => new("token", "query", "Optional share token, as in a share link.");

    private static ApiSurfaceDoc DeviceSurface => new(
        "Companion app (device key)",
        DeviceIntro,
        "Device key",
        [
            new("GET", "/api/v1/me/walls", "Your walls, as the My Walls page lists them: the walls you are a member of.",
                [],
                null,
                "[\n  {\n    \"id\": \"<guid>\",\n    \"name\": \"The Attic\",\n    \"memberCount\": 4,"
                    + "\n    \"hasPhoto\": true,\n    \"photoSha\": \"<hex>\"\n  }\n]",
                "Device key only. Read the card photo as /api/walls/{id}/a/{photoSha}/640; photoSha is null without a photo "
                    + "or while it is not hashed yet."),
            new("GET", "/api/walls/{wallId}/docs", "The wall's data documents for you: kind (wall, holds, boulders, gallery, comments) and sha of each.",
                [WallParam, TokenParam],
                null,
                "{\n  \"cursor\": \"<feed cursor>\",\n  \"docs\": [ { \"kind\": \"wall\", \"sha\": \"<hex>\", \"bytes\": 2048, \"cursor\": \"…\" } ]\n}"),
            new("GET", "/api/walls/{wallId}/d/{kind}/{sha}", "One document. Immutable: cache it by sha for good.",
                [WallParam, new("kind", "path", "A kind from the index."), new("sha", "path", "Its sha from the index."), TokenParam],
                null,
                null,
                "JSON (Brotli when accepted), strong ETag \"{sha}\". Any refusal or unknown sha is a bare 404."),
            new("GET", "/api/walls/{wallId}/a/{sha}", "An image or other asset named in a document, original bytes.",
                [WallParam, new("sha", "path", "The asset's sha."), TokenParam],
                null,
                null,
                "Immutable bytes; every refusal is a bare 404."),
            new("GET", "/api/walls/{wallId}/a/{sha}/{width}", "A photo resized to 640, 1280, 1920 or 2560 px wide.",
                [WallParam, new("sha", "path", "The photo's sha."), new("width", "path", "640, 1280, 1920 or 2560."), TokenParam],
                null,
                null),
            new("GET", "/api/walls/{wallId}/manifest", "Every asset of the wall you may read, under a weak ETag (304 when unchanged).",
                [WallParam, TokenParam],
                null,
                null),
            new("GET", "/api/walls/{wallId}/changes", "One page of the wall's feed events you may see, after a cursor.",
                [WallParam, new("after", "query", "The cursor of the last event you have."), new("limit", "query", "1 to 1000."), TokenParam],
                null,
                "{\n  \"events\": [ { \"kind\": \"wall.updated\", \"cursor\": \"…\" } ],\n  \"next\": \"…\",\n  \"resync\": false,\n  \"more\": false\n}",
                "Limited per address (429 with Retry-After)."),
            new("GET", "/api/walls/{wallId}/feed/stream", "The wall's feed as server-sent events (text/event-stream).",
                [WallParam, new("after", "query", "Start cursor; Last-Event-ID wins on a reconnect."), TokenParam],
                null,
                null,
                "Closes after about 5 minutes; reconnect with Last-Event-ID. A revoked key is refused on the next connect."),
            new("GET", "/api/v1/me/changes", "Your own rows across walls (your ticks, wall list changes), after a cursor.",
                [new("after", "query", "The cursor of the last row you have."), new("limit", "query", "1 to 1000.")],
                null,
                null,
                "Device key only. Same body as /api/walls/{wallId}/changes."),
            new("GET", "/api/v1/me/walls/{wallId}/mine", "Your ticks, favourites and projects on one wall.",
                [WallParam],
                null,
                "{\n  \"v\": 1,\n  \"wallId\": \"<guid>\",\n  \"partition\": \"u-…\",\n  \"cursor\": \"…\",\n  \"ticks\": [ … ],\n  \"favourites\": [ \"<boulder id>\" ],\n  \"projects\": [ \"<boulder id>\" ]\n}",
                "Device key only. 403 on a wall you only see through a share link."),
            new("POST", "/api/v1/me/ratings", "Rate a boulder 1 to 5 stars, as the offline web client does.",
                [],
                "{\n  \"boulderId\": \"<guid>\",\n  \"stars\": 4\n}",
                "{\n  \"applied\": true,\n  \"clientRequestId\": null,\n  \"result\": { \"stars\": 4 }\n}",
                MarksNotes),
            new("DELETE", "/api/v1/me/ratings/{boulderId}", "Clear your rating. Clearing none is a no-op.",
                [BoulderParam],
                null,
                null,
                MarksNotes),
            new("POST", "/api/v1/me/favorites", "Set your favourite mark to an absolute value (never a toggle).",
                [],
                "{\n  \"boulderId\": \"<guid>\",\n  \"favorite\": true\n}",
                null,
                MarksNotes),
            new("DELETE", "/api/v1/me/favorites/{boulderId}", "Remove your favourite mark.",
                [BoulderParam],
                null,
                null,
                MarksNotes),
            new("POST", "/api/v1/me/projects", "Set your project mark to an absolute value.",
                [],
                "{\n  \"boulderId\": \"<guid>\",\n  \"project\": true\n}",
                null,
                MarksNotes),
            new("DELETE", "/api/v1/me/projects/{boulderId}", "Remove your project mark.",
                [BoulderParam],
                null,
                null,
                MarksNotes),
            new("POST", "/api/v1/me/comments", "Comment on a boulder. The same clientRequestId again returns the stored comment.",
                [],
                "{\n  \"boulderId\": \"<guid>\",\n  \"text\": \"Nice heel hook\",\n  \"clientRequestId\": \"<guid>\"\n}",
                "{\n  \"applied\": true,\n  \"clientRequestId\": \"<guid>\",\n  \"result\": { \"commentId\": \"<guid>\" }\n}",
                MarksNotes + " Text: 1 to 1024 characters."),
            new("DELETE", "/api/v1/me/comments/{commentId}", "Delete one of your own comments.",
                [new("commentId", "path", "One of your comments.")],
                null,
                null,
                MarksNotes),
        ]);
}
