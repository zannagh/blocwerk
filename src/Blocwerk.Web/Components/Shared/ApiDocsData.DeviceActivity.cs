// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Components.Shared;

/// <summary>The companion app's Activity edits, day summary and avatar routes, and the join preview, in the device-key reference.</summary>
internal static partial class ApiDocsData
{
    private static ApiEndpointDoc[] DeviceActivityEndpoints =>
    [
        new("GET", "/api/v1/join/{token}", "The wall an invite token leads to, without joining it.",
            [new("token", "path", "The token of an invite link.")],
            null,
            "{\n  \"wallId\": \"<guid>\",\n  \"name\": \"The Attic\",\n  \"memberCount\": 4\n}",
            "Device key only. 404 for an unknown token, 403 on a kiosk."),
        new("PATCH", "/api/v1/me/progression", "Save your progression window and grouping, as the Activity page does.",
            [],
            "{\n  \"windowDays\": 90,\n  \"groupBy\": \"Week\"\n}",
            null,
            "Device key only; per-key write limit. windowDays 7 to 365, groupBy Day, Week or Month; answers the progression."),
        new("PATCH", "/api/v1/me/activities/{activityId}", "Set one of your activities' duration.",
            [new("activityId", "path", "One of your activities.")],
            "{\n  \"durationMinutes\": 95\n}",
            null,
            "Device key only; per-key write limit. 0 to 1440 minutes; answers the activity; 404 for someone else's."),
        new("GET", "/api/v1/me/days/{date}", "What you did on one day (yyyy-MM-dd, UTC), inside the progression window or not.",
            [new("date", "path", "The day, yyyy-MM-dd.")],
            null,
            "{\n  \"date\": \"2026-11-28\",\n  \"boulders\": [ … ],\n  \"hangboard\": [ … ],\n  \"pullups\": [ … ],\n  \"sessionDurationMinutes\": 95\n}",
            "Device key only."),
        new("PUT", "/api/v1/me/avatar", "Replace your avatar with the request body (JPEG, PNG or WebP, at most 30 MB).",
            [],
            "<image bytes; Content-Type image/jpeg, image/png or image/webp>",
            null,
            "Device key only; per-key write limit. Stored scaled to 512 px as WebP; answers the profile."),
        new("DELETE", "/api/v1/me/avatar", "Remove your avatar.",
            [],
            null,
            null,
            "Device key only; per-key write limit. Answers the profile."),
    ];
}
