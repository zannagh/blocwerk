// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Components.Shared;

/// <summary>The companion app's read-only wall cards (activity, leaderboard, temperature, look-over), for the reference.</summary>
internal static partial class ApiDocsData
{
    private const string MembersReadNotes = "Device key only; members of the wall (404 for anyone else).";

    private static ApiParamDoc PageParam => new("page", "query", "The page, from 0.");

    private static ApiEndpointDoc[] DeviceWallReadEndpoints =>
    [
        new("GET", "/api/v1/walls/{wallId}/activity", "The wall's activity, newest first, a page at a time.",
            [WallParam, PageParam, new("pageSize", "query", "1 to 50; 20 by default.")],
            null,
            "{\n  \"items\": [ { \"id\": \"<guid>\", \"type\": \"BoulderCreated\", \"userId\": \"<guid>\", \"userName\": \"Alex\","
                + " \"boulderId\": \"<guid>\", \"details\": \"Crimp line\", \"timestamp\": \"…\" } ],\n  \"totalCount\": 42,\n  \"page\": 0,\n  \"pageSize\": 20\n}",
            MembersReadNotes),
        new("GET", "/api/v1/walls/{wallId}/boulders/{boulderId}/activity", "One boulder's activity, newest first (5 a page by default).",
            [WallParam, BoulderParam, PageParam],
            null,
            null,
            MembersReadNotes),
        new("GET", "/api/v1/walls/{wallId}/leaderboard", "The members' leaderboard: hardest send, score and volume.",
            [WallParam],
            null,
            "[\n  { \"userId\": \"<guid>\", \"name\": \"Alex\", \"hardestSendGrade\": \"7A\", \"hardestSendScore\": 700, \"score\": 640, \"volumeMinutes\": 1200 }\n]",
            MembersReadNotes),
        new("GET", "/api/v1/walls/{wallId}/temperature", "The latest reading and one window's bucket averages.",
            [WallParam, new("range", "query", "24h (default), 7d, 30d, 1y or all.")],
            null,
            "{\n  \"latestCelsius\": 19.5,\n  \"range\": \"24h\",\n  \"bucketSeconds\": 720,\n  \"buckets\": [ { \"bucketStart\": \"…\", \"averageCelsius\": 19.2 } ]\n}",
            MembersReadNotes + " A bucket without readings is absent (a gap)."),
        new("GET", "/api/v1/walls/{wallId}/look-over", "The look-over banner: pending, findings, whether you may do it.",
            [WallParam],
            null,
            "{\n  \"pending\": true,\n  \"openFindings\": 3,\n  \"canLookOver\": true\n}",
            MembersReadNotes),
    ];
}
