// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Components.Shared;

/// <summary>The companion app's boulder actions (publish, archive, grade proposals, variants), for the reference.</summary>
internal static partial class ApiDocsData
{
    private const string ManageNotes =
        "Device key only; the creator, a setter of the boulder or a wall admin (403 otherwise); 404 for a boulder you cannot see.";

    private const string ProposalAnswer =
        "{\n  \"proposal\": { \"id\": \"<guid>\", \"proposedGrade\": \"7A\", \"proposedByUserId\": \"<guid>\", \"proposedByName\": \"Alex\" },"
        + "\n  \"canPropose\": false,\n  \"canResolve\": true\n}";

    private static ApiParamDoc ProposalParam => new("proposalId", "path", "The open proposal's id.");

    private static ApiEndpointDoc[] DeviceBoulderActionEndpoints =>
    [
        new("POST", "/api/v1/walls/{wallId}/boulders/{boulderId}/publish", "Publish a draft (a no-op when it is published).",
            [WallParam, BoulderParam], null, null, ManageNotes + " 409 for a draft without holds."),
        new("POST", "/api/v1/walls/{wallId}/boulders/{boulderId}/archive", "Move a historic boulder into the archive.",
            [WallParam, BoulderParam], null, null, ManageNotes + " 409 unless the boulder is historic."),
        new("POST", "/api/v1/walls/{wallId}/boulders/{boulderId}/unarchive", "Restore a boulder from the archive.",
            [WallParam, BoulderParam], null, null, ManageNotes),
        new("GET", "/api/v1/walls/{wallId}/boulders/{boulderId}/grade-proposal", "The open grade proposal and what you may do about it.",
            [WallParam, BoulderParam], null, ProposalAnswer, "Device key only; members of the wall."),
        new("POST", "/api/v1/walls/{wallId}/boulders/{boulderId}/grade-proposal", "Propose a different grade; it replaces the open one.",
            [WallParam, BoulderParam], "{\n  \"grade\": \"7A\"\n}", ProposalAnswer, "Device key only; anyone but the creator (403)."),
        new("POST", "/api/v1/walls/{wallId}/boulders/{boulderId}/grade-proposal/{proposalId}/accept", "Accept the proposal: the boulder takes its grade.",
            [WallParam, BoulderParam, ProposalParam], null, ProposalAnswer, ManageNotes),
        new("POST", "/api/v1/walls/{wallId}/boulders/{boulderId}/grade-proposal/{proposalId}/reject", "Reject the proposal.",
            [WallParam, BoulderParam, ProposalParam], null, ProposalAnswer, ManageNotes),
        new("PUT", "/api/v1/walls/{wallId}/boulders/{boulderId}/parent", "Make the boulder a variant of another, or of none (null).",
            [WallParam, BoulderParam], "{\n  \"parentId\": \"<guid>\"\n}", null, ManageNotes + " 400 for a cycle, another wall or a boulder not live."),
        new("PUT", "/api/v1/walls/{wallId}/boulders/{boulderId}/variants", "Set the boulders that are variants of this one.",
            [WallParam, BoulderParam], "{\n  \"variantIds\": [ \"<guid>\" ]\n}", null, ManageNotes + " At most 100 distinct ids."),
    ];
}
