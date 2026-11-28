// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Components.Shared;

/// <summary>The companion app's wall routes (members, link reminder, invite and visitor links, joining) in the device-key reference.</summary>
internal static partial class ApiDocsData
{
    private const string LinkAnswer = "{\n  \"token\": \"<token>\",\n  \"url\": \"https://…/join/<token>\",\n  \"expiresAt\": null\n}";

    private static ApiEndpointDoc[] DeviceWallEndpoints =>
    [
        new("GET", "/api/v1/walls/{wallId}/members", "The wall's members in join order, as the wall page lists them.",
            [WallParam],
            null,
            "[\n  {\n    \"userId\": \"<guid>\",\n    \"name\": \"Sloper Sam\",\n    \"avatarUrl\": null,\n    \"role\": \"Moderator\","
                + "\n    \"isOwner\": false,\n    \"joinedAt\": \"…\"\n  }\n]",
            "Device key only. Members only; anyone else gets 404."),
        new("GET", "/api/v1/walls/{wallId}/hold-links", "The cross-panel link reminder: live panels, linked hold pairs, finalized.",
            [WallParam],
            null,
            "{\n  \"panelCount\": 3,\n  \"linkCount\": 0,\n  \"linksFinalized\": false\n}",
            "Device key only. Admins and moderators; a member gets 403, anyone else 404."),
        new("POST", "/api/v1/walls/{wallId}/invite-link", "The wall's invite link, made on first use (the Invite button).",
            [WallParam],
            null,
            LinkAnswer,
            "Device key only. Any member; 403 otherwise."),
        new("POST", "/api/v1/walls/{wallId}/invite-link/rotate", "Replace the invite link; the old one stops working.",
            [WallParam],
            null,
            LinkAnswer,
            "Device key only. Wall admins; 403 otherwise."),
        new("POST", "/api/v1/walls/{wallId}/visit-links", "A one-time, read-only visitor link (the Visit button).",
            [WallParam],
            null,
            "{\n  \"token\": \"<token>\",\n  \"url\": \"https://…/visit/<token>\",\n  \"expiresAt\": \"…\"\n}",
            "Device key only. Members; at most 20 an hour (429 after)."),
        new("POST", "/api/v1/join/{token}", "Join the wall an invite token belongs to, as a member.",
            [new("token", "path", "The token of an invite link.")],
            null,
            "{\n  \"wallId\": \"<guid>\",\n  \"name\": \"The Attic\"\n}",
            "Device key only. Joining again is a no-op; 404 for an unknown or replaced token."),
    ];
}
