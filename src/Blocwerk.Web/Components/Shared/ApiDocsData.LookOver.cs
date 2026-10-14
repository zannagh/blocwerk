// <copyright file="ApiDocsData.LookOver.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared;

/// <summary>The reference for the wall look-over over the API (WallLookOverApiController), listed with the panel update.</summary>
internal static partial class ApiDocsData
{
    private const string LookOverBase = "/api/walls/{wallId}/look-over";

    private const string LookOverStateJson =
        "{\n  \"pending\": true,\n  \"pendingSince\": \"2026-10-12T08:15:00+00:00\",\n  \"generation\": 7,\n  \"openFindings\": 118,\n  "
        + "\"totalFindings\": 131,\n  \"completedAt\": null,\n  \"completedBy\": null,\n  \"canLookOver\": true,\n  \"bouldersMarked\": 26,\n  \"moreBoulders\": 9,\n  \"missingHolds\": 61\n}";

    private const string LookOverFindingsJson =
        "{\n  \"state\": { … },\n  \"findings\": [ { \"id\": \"<guid>\", \"kind\": 5, \"status\": 0, \"answer\": null, \"generation\": 7,\n    "
        + "\"oldHoldId\": \"<guid>\", \"holdId\": \"<guid>\", \"otherHoldId\": \"<guid>\", \"holdName\": \"yellow\", \"photoScore\": 0.12,\n    "
        + "\"boulders\": [ { \"id\": \"<guid>\", \"name\": \"Sloper traverse\", \"needsReview\": true } ], \"answers\": [ 0, 3, 2 ],\n    "
        + "\"holdKept\": true, \"keptAtUpdate\": true, \"covered\": true, \"closedReason\": null } ]\n}";

    private const string LookOverNote =
        "Read-only. An applied update that leaves findings nobody answered flags the wall until a wall admin or moderator finishes the "
        + "look-over on the page. kind: 0 possibly removed, 1 unsure match, 2 new hold with conflicting evidence, 3 matched to a "
        + "hand-placed hold, 4 hand-placed hold with several detections, 5 another hold is here now, 6 not found again on a boulder, "
        + "7 old and new hold on the same spot, 8 a discarded detection, 9 two holds a full revision linked across two photos without "
        + "being sure. status: 0 open, 1 answered, 2 superseded by a later update, 3 closed in the hold editor. "
        + "answers: 0 fine as it is, 1 different hold, 2 remove the hold, 3 boulders use the new hold, 7 two holds (the link is taken apart). holdKept: the update could not find "
        + "the hold and nobody has confirmed it (it is not drawn on the wall and cannot be added to a boulder); missingHolds counts those. "
        + "403 when the key's owner may not look over the wall.";

    private static ApiEndpointDoc[] LookOverEndpoints() =>
    [
        new("GET", LookOverBase, "Whether the wall waits for a look-over after an update, since when, and how much is open.", RefreshWall, null,
            LookOverStateJson, LookOverNote),
        new("GET", LookOverBase + "/findings", "The findings of the look-over: everything open, and what was answered since the last one.",
            RefreshWall, null, LookOverFindingsJson, LookOverNote),
    ];
}
