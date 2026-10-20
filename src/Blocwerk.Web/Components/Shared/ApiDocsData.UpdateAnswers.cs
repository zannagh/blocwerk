// <copyright file="ApiDocsData.UpdateAnswers.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The reference for the human answers of an update over the API: the confirm screen's cards (WallRefreshApiController)
/// and the look-over's answers (WallLookOverAnswersApiController).
/// </summary>
internal static partial class ApiDocsData
{
    private const string ChecksJson =
        "[\n  { \"id\": \"<guid>\", \"kind\": 0, \"status\": 0, \"hasOld\": true, \"hasModel\": false, \"boulders\": 2,\n    "
        + "\"holdName\": \"yellow\", \"confidence\": null, \"canRemove\": true }\n]";

    private const string ChecksNote =
        "Empty unless the run is at status 4. kind: 0 possibly removed, 1 unsure match, 2 new hold with conflicting evidence, 3 matched "
        + "to a hand-placed hold, 4 hand-placed hold with several detections, 5 another hold is here now. status: 0 open, 1 kept, 2 removed. "
        + "canRemove is false for a hold on a panel other than the centre (the removal would be undone at the apply).";

    private const string CheckAnswerNote =
        "202 {\"answered\": n}; the summary is made again in the background, so read the summary again for the new decisionsVersion. "
        + "answer: keep, remove, undo or notSame (or 0, 1, 2, 3). notSame answers an unsure match (kind 1) or a hand-placed merge (kind 3): "
        + "the pair is taken apart as the full review's unlink does, the old hold is carried where it was predicted and marked for review, "
        + "and the detection becomes a new hold (an unsure match takes no remove). 400 without an answer; 409 when refused (the card is gone, "
        + "the update was replaced, the 3D check is running, a remove off the centre, notSame on a card that pairs nothing). Several answers (at most 500) apply in order and stop at the first refusal: "
        + "the answer then says how many were saved, failedCheckId and error (202 when some were saved, 409 when none were).";

    private const string LookOverAnswerNote =
        "200 with the look-over's state; 409 with the reason when refused. answer must be one of the finding's answers (Settle, "
        + "DifferentHold, RemoveHold, UseNewHold, AddHold, Unlink, or their numbers); expectedBoulderIds are the finding's boulders as read, "
        + "required for every answer but Settle (409 when other boulders use the hold by now). Reassign (4, \"It is this hold\", a hold the "
        + "update could not find) takes targetHoldId, a hold of its photo that is not kept (GET …/look-over/holds?panelId=); Moved (5, \"It is "
        + "here\") takes spot {panelId, x, y, r?} on bare wall of its own photo (a spot on a hold is Reassign). DifferentHold may name "
        + "targetHoldId, the hold that replaced the old one: the detection the update paired it with stays a new hold, and UseNewHold "
        + "(newHoldBoulderIds too) then moves boulders onto the named hold.";

    private static ApiParamDoc[] CheckOnRun =>
    [
        new("wallId", "path", "The wall."),
        new("refreshId", "path", "The run."),
        new("checkId", "path", "The card (id from GET …/checks)."),
    ];

    private static ApiParamDoc[] FindingOnWall =>
    [
        new("wallId", "path", "The wall."),
        new("findingId", "path", "The finding (id from GET …/findings)."),
    ];

    private static ApiEndpointDoc[] CheckEndpoints() =>
    [
        new("GET", RefreshBase + "/{refreshId}/checks", "The confirm screen's cards and how each is answered now.", RefreshOnWall, null,
            ChecksJson, ChecksNote),
        new("POST", RefreshBase + "/{refreshId}/checks/{checkId}/answer", "Answers one card, as on the page.", CheckOnRun,
            "{\n  \"answer\": \"keep\"\n}", "{\n  \"answered\": 1\n}", CheckAnswerNote),
        new("POST", RefreshBase + "/{refreshId}/checks/answers", "Answers several cards in order.", RefreshOnWall,
            "{\n  \"answers\": [ { \"checkId\": \"<guid>\", \"answer\": \"remove\" } ]\n}", "{\n  \"answered\": 1\n}", CheckAnswerNote),
    ];

    private static ApiEndpointDoc[] LookOverAnswerEndpoints() =>
    [
        new("POST", LookOverBase + "/findings/{findingId}/answer", "Answers one open finding.", FindingOnWall,
            "{\n  \"answer\": \"DifferentHold\",\n  \"expectedBoulderIds\": [ \"<guid>\" ],\n  \"targetHoldId\": \"<guid>\"\n}", LookOverStateJson, LookOverAnswerNote),
        new("POST", LookOverBase + "/findings/{findingId}/undo", "Takes back an answer that changed nothing on the wall.", FindingOnWall,
            null, LookOverStateJson, "200 with the state; 409 when it cannot be undone (a removed or merged hold)."),
        new("POST", LookOverBase + "/finish", "Finishes the look-over: the flag comes off the wall.", RefreshWall,
            "{\n  \"leaveOpen\": false\n}", LookOverStateJson, "200 with the state; 409 while findings are open unless leaveOpen is true."),
    ];
}
