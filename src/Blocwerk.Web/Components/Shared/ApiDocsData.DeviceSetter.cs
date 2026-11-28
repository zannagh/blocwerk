// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Components.Shared;

/// <summary>The companion app's setter routes (create, variant, revise), for the device key reference.</summary>
internal static partial class ApiDocsData
{
    private const string SetterNotes =
        "Device key only; the web setter's rules: members create, the creator, a setter or a wall admin revises (403 "
        + "otherwise), holds must be on the wall and confirmed, a grade must be a Font or V grade. 404 for a wall you are "
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
        new("PUT", "/api/v1/walls/{wallId}/boulders/{boulderId}", "Revise a boulder: holds (null keeps them), name, grade, rules, styles, hold types, setters.",
            [WallParam, BoulderParam],
            "{\n  \"name\": \"Crimp line\",\n  \"grade\": \"6C\",\n  \"holds\": null,\n  \"holdTypes\": [ \"Crimp\" ],\n  \"clientRequestId\": \"<guid>\"\n}",
            BoulderAnswer,
            SetterNotes + " Once someone else has sent it, only the name and grade may change."),
    ];
}
