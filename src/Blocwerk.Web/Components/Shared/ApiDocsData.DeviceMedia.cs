// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Components.Shared;

/// <summary>The companion app's gallery and beta video routes, for the device key reference.</summary>
internal static partial class ApiDocsData
{
    private const string MediaWriteNotes =
        "Device key only; counted in the same write bucket as your marks. Answers { applied, clientRequestId, result } or "
        + "{ message, permanent }. Pass clientRequestId in the query string: the same id again returns the stored item "
        + "instead of a second one (409 while the first is still running, 400 when the id was used for another write).";

    private static ApiParamDoc VideoParam => new("videoId", "path", "A beta clip on one of your walls.");

    private static ApiParamDoc ClientRequestIdParam => new("clientRequestId", "query", "Optional replay key (a new guid per upload).");

    private static ApiEndpointDoc[] DeviceMediaEndpoints =>
    [
        new("POST", "/api/v1/walls/{wallId}/gallery", "Add a photo to the wall's gallery, as the wall camera does. Wall admins only.",
            [WallParam, ClientRequestIdParam],
            "// multipart/form-data with a 'file' part (+ optional caption/capturedAt fields),\n// or the raw image/jpeg, image/png or image/webp body",
            "{\n  \"applied\": true,\n  \"clientRequestId\": \"<guid>\",\n  \"result\": { \"imageId\": \"<guid>\", \"capturedAt\": \"…\" }\n}",
            MediaWriteNotes + " At most 20 MB, and the bytes must be the declared format (415); location and other metadata are removed. "
                + "403 for a member who is not an admin."),
        new("DELETE", "/api/v1/walls/{wallId}/gallery/{imageId}", "Delete an uploaded photo of the wall's gallery. Wall admins only.",
            [WallParam, new("imageId", "path", "An uploaded gallery photo of this wall."), ClientRequestIdParam],
            null,
            null,
            MediaWriteNotes),
        new("GET", "/api/v1/walls/{wallId}/boulders/{boulderId}/beta-videos", "The boulder's beta clips, newest first, with where to play them.",
            [WallParam, BoulderParam],
            null,
            "[\n  {\n    \"id\": \"<guid>\",\n    \"uploaderName\": \"Alex\",\n    \"encodingStatus\": \"Ready\",\n    \"videoUrl\": \"/api/v1/beta-videos/<id>\","
                + "\n    \"hlsUrl\": \"/api/v1/beta-videos/<id>/hls/master.m3u8\",\n    \"thumbnailUrl\": \"/api/v1/beta-videos/<id>/thumbnail\",\n    \"mine\": true\n  }\n]",
            "Device key only; members of the wall. hlsUrl is null without a ladder: play videoUrl. A Pending or Processing clip does not play yet."),
        new("POST", "/api/v1/walls/{wallId}/boulders/{boulderId}/beta-videos", "Upload a beta clip, as the boulder page does. Members of the wall.",
            [WallParam, BoulderParam, ClientRequestIdParam],
            "// multipart/form-data with a 'file' part (a video type)\n// and an optional 'thumbnail' part (a small JPEG)",
            "{\n  \"applied\": true,\n  \"clientRequestId\": \"<guid>\",\n  \"result\": { \"videoId\": \"<guid>\" }\n}",
            MediaWriteNotes + " One request, at most 1 GiB, with a Content-Length (411 without, 413 over); one file part, a thumbnail "
                + "of at most 512 KB. 409 permanent when you have uploaded 10 GB of clips in 30 days."),
        new("DELETE", "/api/v1/walls/{wallId}/boulders/{boulderId}/beta-videos/{videoId}", "Delete one of your own beta clips.",
            [WallParam, BoulderParam, VideoParam, ClientRequestIdParam],
            null,
            null,
            MediaWriteNotes),
        new("GET", "/api/v1/beta-videos/{videoId}", "The clip's file, with range requests.",
            [VideoParam],
            null,
            null,
            "Device key only; members of the clip's wall; anything else is 404."),
        new("GET", "/api/v1/beta-videos/{videoId}/thumbnail", "The clip's poster frame.",
            [VideoParam],
            null,
            null),
        new("GET", "/api/v1/beta-videos/{videoId}/hls/{path}", "A file of the clip's HLS ladder, starting at master.m3u8.",
            [VideoParam, new("path", "path", "master.m3u8, then the relative URIs it names.")],
            null,
            null,
            "Send the key on every request of the asset; the playlists' URIs are relative to this prefix."),
    ];
}
