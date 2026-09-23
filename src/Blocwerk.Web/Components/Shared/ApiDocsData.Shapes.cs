// <copyright file="ApiDocsData.Shapes.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared;

/// <summary>The reference for the wall update's hold-shape step (WallUpdateShapesController).</summary>
internal static partial class ApiDocsData
{
    private const string ShapesBase = "/api/walls/{wallId}/update/shapes";

    private const string ShapesIntro =
        "Drives the optional \"recognise hold shapes\" step of an in-flight wall update and its review, with "
        + "the same rules as the wizard: a Wall-scoped key whose owner is an admin of the wall (a member's "
        + "key gets 403, kiosk tablets are refused). An update must already be open on the wall. Every write "
        + "takes an optional sessionId; when it is no longer the wall's open update the call is refused with "
        + "409, so a stale script cannot act on a newer update. Nothing reaches the live holds until the "
        + "update is applied. Shapes are lists of {dx, dy} offsets from the hold centre (x, y), as fractions "
        + "of the photo width and height.";

    private const string StartBody =
        "{\n  \"scope\": \"NewAndChanged\",\n  \"overwriteManual\": false,\n  \"rerun\": false,\n  \"sessionId\": \"<guid>\"\n}";

    private const string StartNote =
        "202 Accepted. scope is NewAndChanged (default), New, Changed or All. rerun drops earlier proposals "
        + "and verdicts. Starting while a run is alive changes nothing; after a restart it resumes.";

    private const string StatusJson =
        "{\n  \"sessionId\": \"<guid>\",\n  \"available\": true,\n  \"status\": \"Completed\","
        + "\n  \"interrupted\": false,\n  \"scope\": \"NewAndChanged\",\n  \"overwriteManual\": false,"
        + "\n  \"total\": 212,\n  \"done\": 212,\n  \"skippedManual\": 3,\n  \"startedAt\": \"...\","
        + "\n  \"finishedAt\": \"...\",\n  \"error\": null,\n  \"decisions\": { \"Pending\": 180, \"Accepted\": 32 }\n}";

    private const string StatusNote =
        "status is NotStarted, Running, Completed, Skipped or Failed. interrupted means Running with no "
        + "live run (the server restarted): POST /recognition again to resume.";

    private const string ProposalsJson =
        "[\n  {\n    \"holdId\": \"<guid>\",\n    \"panelId\": \"<guid>\",\n    \"panelCol\": 0,\n    \"panelRow\": 0,"
        + "\n    \"x\": 0.41,\n    \"y\": 0.63,\n    \"radius\": 0.012,\n    \"reason\": \"New\","
        + "\n    \"method\": \"Contour\",\n    \"confidence\": 0.83,\n    \"imageWidth\": 4032,"
        + "\n    \"imageHeight\": 3024,\n    \"shape\": [ { \"dx\": 0.004, \"dy\": -0.01 } ],"
        + "\n    \"previousShape\": null,\n    \"decision\": \"Pending\",\n    \"adjustedShape\": null\n  }\n]";

    private const string ProposalsNote =
        "method is Contour, GrabCut or CircleFallback (no outline found; confidence at most 0.2, shape null). "
        + "reason is New, Changed or Carried.";

    private const string DecisionsBody =
        "{\n  \"sessionId\": \"<guid>\",\n  \"decisions\": [\n    { \"holdId\": \"<guid>\", \"decision\": \"Accepted\" },"
        + "\n    { \"holdId\": \"<guid>\", \"decision\": \"Circle\" },"
        + "\n    { \"holdId\": \"<guid>\", \"decision\": \"Adjusted\","
        + "\n      \"shape\": [ { \"dx\": 0, \"dy\": -0.01 }, { \"dx\": 0.01, \"dy\": 0.01 }, { \"dx\": -0.01, \"dy\": 0.01 } ] }\n  ]\n}";

    private const string DecisionsNote =
        "decision is Accepted, Adjusted (needs shape: 3-256 points), Circle, KeepPrevious (the shape the hold "
        + "had before) or Pending. Refused with 409 until the run has completed.";

    private const string SessionBody = "{\n  \"sessionId\": \"<guid>\"\n}";

    private const string AcceptAboveBody = "{\n  \"minConfidence\": 0.7,\n  \"sessionId\": \"<guid>\"\n}";

    private const string SameAsStatus = "// same shape as GET /recognition";

    private static readonly ApiParamDoc[] WallOnly = [new("wallId", "path", "The wall the key is bound to.")];

    private static readonly ApiParamDoc[] WallAndBelow =
    [
        new("wallId", "path", "The wall the key is bound to."),
        new("below", "query", "Only shapes with confidence below this (0..1)."),
    ];

    private static ApiSurfaceDoc WallUpdateShapesSurface => new(
        "Wall update: hold shapes",
        ShapesIntro,
        "Wall key (admin owner)",
        ShapeEndpoints());

    private static ApiEndpointDoc[] ShapeEndpoints() =>
    [
        new("POST", ShapesBase + "/recognition", "Starts (or resumes) recognition in the background. Hand-drawn shapes are skipped.", WallOnly, StartBody, SameAsStatus, StartNote),
        new("GET", ShapesBase + "/recognition", "The run status and the review tally. Poll it while status is Running.", WallOnly, null, StatusJson, StatusNote),
        new("POST", ShapesBase + "/recognition/skip", "Skips the step (stopping a live run). Every hold keeps the shape it has now.", WallOnly, SessionBody, SameAsStatus),
        new("GET", ShapesBase + "/proposals", "The recognised shapes, lowest confidence first.", WallAndBelow, null, ProposalsJson, ProposalsNote),
        new("POST", ShapesBase + "/decisions", "Records review verdicts; the whole batch is refused if any hold has no proposal.", WallOnly, DecisionsBody, "{ \"count\": 3 }", DecisionsNote),
        new("POST", ShapesBase + "/accept-above", "Accepts every still-pending recognised outline at or above a confidence.", WallOnly, AcceptAboveBody, "{ \"count\": 164 }", "Never overrides a verdict already given, and skips circle fallbacks."),
    ];
}
