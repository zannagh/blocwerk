// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Text.Json;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Runners;

namespace Blocwerk.Core.Recognition;

/// <summary>
/// What a runner uploads for a recognition job (runner protocol 1.2, §1.4): a zip holding only <c>status.json</c>, status
/// <c>succeeded</c>, with the kind's document inline as <c>result</c>. Read in memory: it is small, and the cap counts the
/// decoded bytes, so a gzip body cannot inflate past it.
/// </summary>
public static class RecognitionResultZip
{
    /// <summary>The largest upload (decoded), for every recognition kind.</summary>
    public const long MaxBytes = 16L * 1024 * 1024;

    /// <summary>The result document's JSON, or why the upload is refused (<see cref="RunnerJobOutcome.TooLarge"/>, <see cref="RunnerJobOutcome.Invalid"/>).</summary>
    public static async Task<(string? ResultJson, RunnerJobOutcome Refused)> ReadAsync(Stream body, bool gzip, CancellationToken ct)
    {
        try
        {
            await using var decoded = gzip ? new GZipStream(body, CompressionMode.Decompress, leaveOpen: true) : null;
            using var buffer = new MemoryStream();
            if (!await CopyCappedAsync(decoded ?? body, buffer, ct))
            {
                return (null, RunnerJobOutcome.TooLarge);
            }

            buffer.Position = 0;
            using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
            return Result(zip) is { } json ? (json, RunnerJobOutcome.Ok) : (null, RunnerJobOutcome.Invalid);
        }
        catch (InvalidDataException)
        {
            return (null, RunnerJobOutcome.Invalid);
        }
    }

    private static string? Result(ZipArchive zip)
    {
        if (zip.Entries.Count != 1 || zip.Entries[0].FullName != RunnerComputeResult.StatusName)
        {
            return null;
        }

        var status = RunnerComputeResult.StatusOf(zip);
        return status.Status == ComputeJobStates.Succeeded && status.Result is { ValueKind: JsonValueKind.Object } result
            ? result.GetRawText()
            : null;
    }

    private static async Task<bool> CopyCappedAsync(Stream source, MemoryStream target, CancellationToken ct)
    {
        var chunk = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(chunk, ct)) > 0)
        {
            if (target.Length + read > MaxBytes)
            {
                return false;
            }

            target.Write(chunk, 0, read);
        }

        return true;
    }
}
