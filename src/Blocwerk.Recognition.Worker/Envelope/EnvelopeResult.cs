// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Text.Json;
using Blocwerk.Recognition.Worker.Protocol;

namespace Blocwerk.Recognition.Worker.Envelope;

/// <summary>
/// A recognition result as the server takes it (protocol §2.2): a zip of <c>status.json</c> alone, a
/// <c>worker/job-status</c> with <c>status: "succeeded"</c> and the kind's document inline as <c>result</c>.
/// </summary>
public static class EnvelopeResult
{
    public const string StatusName = "status.json";

    public static byte[] Zip(RunnerClaim claim, object result, DateTimeOffset startedAt, DateTimeOffset finishedAt)
    {
        var status = new
        {
            jobId = claim.JobId.ToString("N"),
            kind = claim.Kind,
            status = "succeeded",
            progress = 1.0,
            stage = "done",
            createdAt = startedAt,
            updatedAt = finishedAt,
            result,
        };

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry(StatusName, CompressionLevel.Optimal).Open();
            JsonSerializer.Serialize(entry, status, WorkerJson.Options);
        }

        return buffer.ToArray();
    }
}
