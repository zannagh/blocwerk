// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Components.Shared;

/// <summary>The reference for the progress API of long-running jobs (JobsController).</summary>
internal static partial class ApiDocsData
{
    private const string JobsIntro =
        "Running and recent long-running work: captures (per stage), photo-real training on 3D runners (step, total, previews), "
        + "the server's finish of a trained view, follow-up steps, texture re-renders, re-solves and capture imports. Each job has "
        + "a state (queued, running, succeeded, skipped, failed, cancelled), a percentage, the remaining time (from its own rate, "
        + "else from the median of recent runs of the same stage; null when unknown), its timestamps and its last error. "
        + "Read-only: a personal key WITHOUT write access is enough. An administrator of the installation sees every wall, a wall "
        + "admin only their walls (anyone else gets an empty list); wall, kiosk and installation keys are refused. The JSON field "
        + "names are stable: fields may be added, never renamed.";

    private const string JobsJson =
        "{\n  \"generatedAt\": \"2026-10-03T18:20:00+00:00\",\n  \"recentHours\": 24,\n  \"jobs\": [\n    {"
        + "\n      \"id\": \"gpuTraining:<guid>\",\n      \"kind\": \"gpuTraining\",\n      \"state\": \"running\","
        + "\n      \"stage\": \"training\",\n      \"detail\": \"is training (step 12000/30000)\",\n      \"percent\": 40,"
        + "\n      \"step\": 12000,\n      \"totalSteps\": 30000,\n      \"etaSeconds\": 1620.5,\n      \"etaSource\": \"rate\","
        + "\n      \"startedAt\": \"2026-10-03T17:40:00+00:00\",\n      \"updatedAt\": \"2026-10-03T18:19:55+00:00\","
        + "\n      \"endedAt\": null,\n      \"lastError\": null,\n      \"wallId\": \"<guid>\",\n      \"wallName\": \"Home wall\","
        + "\n      \"captureId\": \"<guid>\",\n      \"gpuJobId\": \"<guid>\",\n      \"runnerName\": \"home PC\",\n      \"attempts\": 1,"
        + "\n      \"training\": { \"loss\": 0.031, \"splats\": 812345, \"previewStep\": null, \"previewInstalledStep\": 10000, \"stepsPerSecond\": 11.1 },"
        + "\n      \"stages\": null\n    },\n    {\n      \"id\": \"capture:<guid>\",\n      \"kind\": \"capture\",\n      \"state\": \"running\","
        + "\n      \"stage\": \"splatting\",\n      \"percent\": 52.3,\n      \"etaSeconds\": null,\n      \"etaSource\": null,"
        + "\n      \"stages\": [ { \"stage\": \"solving\", \"startedAt\": \"…\", \"endedAt\": \"…\", \"outcome\": \"done\", \"seconds\": 41.2 } ]"
        + "\n    }\n  ]\n}";

    private const string JobsNote =
        "kind: capture, gpuTraining, finish, followUp, textureRerender, resolve, import. etaSource: rate or history. "
        + "Shortened example; every job carries every field (null when it does not apply).";

    private static ApiParamDoc[] JobsParams =>
    [
        new("wallId", "query", "Optional: only this wall's jobs (403 when the key's owner may not watch it)."),
        new("recentHours", "query", "How far back ended jobs are listed; default 24, at most 168, 0 = running only."),
    ];

    private static ApiSurfaceDoc JobsSurface => new(
        "Long-running jobs (progress)",
        JobsIntro,
        "Personal key (read is enough)",
        [
            new("GET", "/api/v1/admin/jobs", "Running jobs first, then those that ended within the window (newest first).",
                JobsParams,
                null,
                JobsJson,
                JobsNote),
        ]);
}
