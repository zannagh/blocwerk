// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using Blocwerk.Core.Compute;

namespace Blocwerk.Core.Runners;

/// <summary>
/// The "worker" of a textures job that ran on a 3D runner: it serves the files of the runner's uploaded zip the way the
/// wall-geometry service serves a finished job's, so the capture pipeline installs the textures through the SAME code as
/// for a render on the host (<c>StoreTexturesAsync</c>: sniffing, masks, source maps, the swap of the model's texture set).
/// </summary>
public sealed class RunnerTexturesClient(string zipPath) : IComputeJobClient
{
    /// <inheritdoc />
    public ComputeServiceKind Service => ComputeServiceKind.Geometry;

    /// <inheritdoc />
    public bool IsConfigured => true;

    /// <inheritdoc />
    public Task<byte[]> DownloadFileAsync(string jobId, string name, CancellationToken ct) =>
        DownloadFileAsync(jobId, name, long.MaxValue, ct);

    /// <inheritdoc />
    public Task<byte[]> DownloadFileAsync(string jobId, string name, long maxBytes, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry(name) ?? throw new ComputeJobException(ComputeFailureKind.Protocol, $"The runner's result has no file {name}.");
        if (entry.Length > maxBytes)
        {
            throw ComputeJobException.TooLarge(name, maxBytes);
        }

        using var stream = entry.Open();
        using var buffer = new MemoryStream((int)Math.Min(entry.Length, 64L * 1024 * 1024));
        stream.CopyTo(buffer);
        return Task.FromResult(buffer.ToArray());
    }

    /// <inheritdoc />
    public Task<ComputeHealth> GetHealthAsync(CancellationToken ct) => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<string> SubmitJsonAsync(string kind, string json, CancellationToken ct) => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<string> SubmitMultipartAsync(string kind, IReadOnlyList<ComputeJobPart> parts, CancellationToken ct) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public Task<ComputeJobStatus> GetStatusAsync(string jobId, CancellationToken ct) =>
        Task.FromResult(RunnerTexturesResult.ToStatus(zipPath, jobId));

    /// <inheritdoc />
    public Task CancelAsync(string jobId, CancellationToken ct) => Task.CompletedTask;
}
