// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Components.Shared;

/// <summary>The companion app's wall settings, photo and role routes, for the device key reference.</summary>
internal static partial class ApiDocsData
{
    private const string WallAdminNotes =
        "Device key only; admins of the wall (the owner included). 404 for a wall you are not a member of, 403 { message } "
        + "for a member without the role.";

    private const string WallSettingsAnswer =
        "{\n  \"name\": \"The Attic\",\n  \"description\": null,\n  \"angle\": 40,\n  \"underMaintenance\": false,\n  \"myRole\": \"Admin\","
        + "\n  \"ownerId\": \"<guid>\",\n  \"photoSha\": \"<hex>\",\n  \"panelCount\": 1,\n  \"allowAnonymousKiosk\": false,"
        + "\n  \"allowKioskKeyboardShortcuts\": false\n}";

    private static ApiEndpointDoc[] DeviceWallAdminEndpoints =>
    [
        new("GET", "/api/v1/walls/{wallId}/settings", "The wall's settings and your role on it.",
            [WallParam],
            null,
            WallSettingsAnswer,
            "Device key only; members of the wall. myRole is Member, Moderator or Admin (the owner is Admin)."),
        new("PATCH", "/api/v1/walls/{wallId}/settings", "Change any of the settings; a field you leave out stays as it is.",
            [WallParam],
            "{\n  \"name\": \"The Attic\",\n  \"description\": \"\",\n  \"angle\": 40,\n  \"underMaintenance\": true\n}",
            WallSettingsAnswer,
            WallAdminNotes + " description \"\" clears it; angle 0 to 90; an unknown field is a 400."),
        new("PUT", "/api/v1/walls/{wallId}/photo", "Upload the first photo of a wall that has no photo and no panels yet.",
            [WallParam, ClientRequestIdParam],
            "// the raw photo as the body: image/jpeg, image/png or image/heic",
            "{\n  \"photoSha\": \"<hex>\"\n}",
            WallAdminNotes + " clientRequestId is required; the same one again returns the stored photo. At most 20 MB, the bytes "
                + "must be the declared type (415), metadata is removed; 409 when the wall already has a photo or panels."),
        new("PUT", "/api/v1/walls/{wallId}/members/{userId}/role", "Set a member's role.",
            [WallParam, new("userId", "path", "A member of the wall.")],
            "{\n  \"role\": \"Moderator\"\n}",
            "{\n  \"userId\": \"<guid>\",\n  \"role\": \"Moderator\"\n}",
            WallAdminNotes + " role is Member, Moderator or Admin; the owner's role can't be changed (403)."),
        new("GET", "/api/v1/walls/{wallId}/scan-mode", "The wall's update mode (Panels or Scan), its recent scans and whether you may remove them.",
            [WallParam],
            null,
            "{\n  \"mode\": \"Scan\",\n  \"canSwitchToPanels\": true,\n  \"scans\": [ { \"scanId\": \"<guid>\", \"createdAt\": \"…\", \"totalBytes\": 1048576 } ],"
                + "\n  \"mayDeleteScans\": false\n}",
            WallAdminNotes),
        new("PUT", "/api/v1/walls/{wallId}/scan-mode", "Set the update mode; back to Panels is refused (409) once a scan has been applied.",
            [WallParam],
            "{\n  \"mode\": \"Scan\"\n}",
            null,
            WallAdminNotes),
        new("DELETE", "/api/v1/walls/{wallId}/scans/{scanId}", "Remove a received scan with its files.",
            [WallParam, new("scanId", "path", "A received scan of the wall.")],
            null,
            null,
            "Device key only; administrators of the installation. 409 for an upload still open."),
        new("DELETE", "/api/v1/walls/{wallId}", "Delete the wall for good, with its boulders, ascents and photos.",
            [WallParam],
            "{\n  \"confirmName\": \"The Attic\"\n}",
            null,
            "Device key only; the wall's owner only (403 for other admins). confirmName must equal the wall's name exactly (400)."),
        new("POST", "/api/v1/walls", "Create a wall you own (the web's + New Wall); upload its photo with PUT …/photo next.",
            [],
            "{\n  \"clientRequestId\": \"<guid>\",\n  \"name\": \"The Attic\",\n  \"description\": null,\n  \"angle\": 40\n}",
            "{\n  \"wallId\": \"<guid>\",\n  \"name\": \"The Attic\",\n  \"description\": null,\n  \"angle\": 40\n}",
            "Device key only. clientRequestId is required; the same one again returns the same wall. Name 1-256, angle 0-90."),
    ];
}
