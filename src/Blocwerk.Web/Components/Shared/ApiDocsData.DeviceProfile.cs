// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Components.Shared;

/// <summary>The companion app's profile routes in the device-key reference.</summary>
internal static partial class ApiDocsData
{
    private static ApiEndpointDoc[] DeviceProfileEndpoints =>
    [
        new("GET", "/api/v1/me/profile", "Your identity and the preferences the profile page keeps on your account.",
            [],
            null,
            "{\n  \"id\": \"<guid>\",\n  \"name\": \"Crimp Queen\",\n  \"accountName\": \"octocat\",\n  \"customDisplayName\": \"Crimp Queen\","
                + "\n  \"role\": \"User\",\n  \"memberSince\": \"2026-01-03T10:00:00+00:00\",\n  \"avatarUrl\": \"/api/users/<guid>/avatar\","
                + "\n  \"homeWallId\": \"<guid>\",\n  \"gradeScale\": \"font\",\n  \"notifications\": { \"BoulderAdded\": true, \"MemberJoined\": false },"
                + "\n  \"appleLinked\": false\n}",
            "Device key only. appleLinked is true when an Apple ID is linked (POST /api/v1/me/apple)."),
        new("PATCH", "/api/v1/me/profile", "Change the fields you send; null resets the display name or clears the home wall.",
            [],
            "{\n  \"displayName\": \"Crimp Queen\",\n  \"homeWallId\": null,\n  \"gradeScale\": \"v\",\n  \"notifications\": { \"BoulderAdded\": false }\n}",
            null,
            "Device key only; per-key write limit. Answers the whole profile. 400 for an unknown field or value, a name over 256 "
                + "characters or with invisible or text-direction characters, or a home "
                + "wall you are not a member of; a refused body changes nothing."),
        new("GET", "/api/v1/users/{userId}", "Another member's profile: identity and progression, as the web shows it.",
            [
                new("userId", "path", "Someone who shares a wall with you, or you."),
                new("windowDays", "query", "7 to 365, default 90."),
                new("groupBy", "query", "Day, Week (default) or Month."),
            ],
            null,
            "{\n  \"id\": \"<guid>\",\n  \"name\": \"Sloper Sam\",\n  \"role\": \"User\",\n  \"memberSince\": \"…\",\n  \"avatarUrl\": null,"
                + "\n  \"progression\": { \"windowDays\": 90, \"groupBy\": \"Week\", \"buckets\": [ … ] }\n}",
            "Device key only. 404 for anyone you share no wall with, as for an unknown id."),
    ];
}
