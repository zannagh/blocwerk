// <copyright file="LookOverAnswerShape.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Which look-over call an API answer is, and what is wrong with its body before the service is asked. The page has a
/// call per answer; the API has one route, so the answer and the fields that come with it pick the call:
/// <list type="bullet">
/// <item><c>Reassign</c> + <c>targetHoldId</c>: "It is this hold" (<see cref="IWallLookOverService.ReassignAsync"/>).</item>
/// <item><c>Moved</c> (or <c>AddHold</c>) + <c>spot</c>: "It is here" (<see cref="IWallLookOverService.MoveAsync"/>).</item>
/// <item><c>DifferentHold</c> + <c>targetHoldId</c>: the unsure match is another hold and this one replaced it
/// (<see cref="IWallLookOverService.DifferentHoldAsync"/>).</item>
/// <item>anything else: the plain answer (<see cref="IWallLookOverService.AnswerAsync"/>).</item>
/// </list>
/// </summary>
internal static class LookOverAnswerShape
{
    /// <summary>What is wrong with the body for this answer; null when nothing is.</summary>
    public static string? Problem(LookOverAnswer answer, LookOverAnswerRequest body)
    {
        if (body.Spot is { } spot)
        {
            return SpotProblem(answer, body, spot);
        }

        if (answer == LookOverAnswer.Moved)
        {
            return "Moved (\"It is here\") needs a spot: { panelId, x, y, r }.";
        }

        if (body.TargetHoldId is null)
        {
            return answer == LookOverAnswer.Reassign ? "Reassign (\"It is this hold\") needs a targetHoldId." : null;
        }

        if (answer is not (LookOverAnswer.Reassign or LookOverAnswer.DifferentHold))
        {
            return "targetHoldId goes only with Reassign (\"It is this hold\") or DifferentHold (\"this hold replaced it\").";
        }

        return body.ExpectedBoulderIds is null ? "Send expectedBoulderIds: the finding's boulders as read." : null;
    }

    /// <summary>Runs the call the answer is (see <see cref="LookOverAnswerShape"/>); <see cref="Problem"/> was null.</summary>
    public static Task RunAsync(IWallLookOverService lookOver, Guid wallId, Guid findingId, LookOverAnswer answer, LookOverAnswerRequest body)
    {
        if (body.Spot is { } spot)
        {
            return lookOver.MoveAsync(wallId, findingId, spot.X!.Value, spot.Y!.Value, spot.R, spot.PanelId);
        }

        return (answer, body.TargetHoldId) switch
        {
            (LookOverAnswer.Reassign, { } target) => lookOver.ReassignAsync(wallId, findingId, target, body.ExpectedBoulderIds!),
            (LookOverAnswer.DifferentHold, { } replacement) => lookOver.DifferentHoldAsync(wallId, findingId, replacement, body.ExpectedBoulderIds!),
            _ => lookOver.AnswerAsync(wallId, findingId, answer, body.ExpectedBoulderIds, body.NewHoldBoulderIds),
        };
    }

    private static string? SpotProblem(LookOverAnswer answer, LookOverAnswerRequest body, LookOverSpotRequest spot)
    {
        if (answer is not (LookOverAnswer.Moved or LookOverAnswer.AddHold))
        {
            return "spot goes only with Moved (\"It is here\").";
        }

        if (body.TargetHoldId is not null || body.NewHoldBoulderIds is not null)
        {
            return "\"It is here\" takes a spot only, no targetHoldId or newHoldBoulderIds.";
        }

        if (spot.X is not (>= 0 and <= 1) || spot.Y is not (>= 0 and <= 1))
        {
            return "spot needs x and y between 0 and 1 (shares of the photo's width and height).";
        }

        return spot.R is null or (> 0 and <= 0.5) ? null : "spot.r is the hold's radius in photo widths, above 0 and at most 0.5.";
    }
}
