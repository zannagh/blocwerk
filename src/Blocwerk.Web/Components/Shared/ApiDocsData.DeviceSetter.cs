// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Components.Shared;

/// <summary>The companion app's setter routes (create, variant, revise), for the device key reference.</summary>
internal static partial class ApiDocsData
{
    private const string SetterNotes =
        "Device key only; clientRequestId is required. The web setter's rules: members create, the creator, a setter or a "
        + "wall admin revises (403 otherwise), holds must be on the wall and confirmed, a grade must be a Font or V grade. "
        + "At most 200 distinct holds and 20 distinct setters; a foot colour of at most 32 characters. 404 for a wall you are "
        + "not a member of or a boulder of another wall. The same clientRequestId again returns the boulder as it is now.";

    private const string BoulderAnswer =
        "{\n  \"applied\": true,\n  \"clientRequestId\": \"<guid>\",\n  \"result\": {\n    \"boulderId\": \"<guid>\",\n    \"wallId\": \"<guid>\","
        + "\n    \"name\": \"Crimp line\",\n    \"grade\": \"6B+\",\n    \"isDraft\": false,\n    \"holdCount\": 6\n  }\n}";

    private static ApiEndpointDoc[] DeviceSetterEndpoints =>
    [
        new("POST", "/api/v1/walls/{wallId}/boulders", "Set a boulder on the wall; with variantOfBoulderId, a variant of one.",
            [WallParam],
            "{\n  \"name\": \"Crimp line\",\n  \"grade\": \"6B+\",\n  \"holds\": [ { \"holdId\": \"<guid>\", \"type\": \"Start\", \"usage\": \"HandAndFoot\" } ],"
                + "\n  \"isDraft\": false,\n  \"styles\": 0,\n  \"customRule\": null,\n  \"variantOfBoulderId\": null,\n  \"clientRequestId\": \"<guid>\"\n}",
            BoulderAnswer,
            SetterNotes),
        new("PUT", "/api/v1/walls/{wallId}/boulders/{boulderId}", "Revise a boulder. Every field is optional: one left out keeps its value; \"\" or [] clears.",
            [WallParam, BoulderParam],
            "{\n  \"clientRequestId\": \"<guid>\",\n  \"grade\": \"6C\",\n  \"holdTypes\": [ \"Crimp\" ]\n}",
            BoulderAnswer,
            SetterNotes + " Fields: name, grade, kickboardFootholdsOn, handsFollowFeet, footColorOnly, noMatch, customRule, styles, "
                + "holdTypes, holds (never empty), setters."),
    ];
}
