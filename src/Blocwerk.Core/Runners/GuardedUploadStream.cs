// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Runners;

/// <summary>
/// The decoded side of a result upload, read by the capture store while it writes the file. Stops the upload (with a
/// <see cref="RunnerUploadRefusedException"/>) the moment a gzip body inflates past a sane ratio (a few MB of zeros would
/// otherwise become gigabytes on disk before the size cap bites), or the capture store's free space drops below the
/// floor.
/// </summary>
public sealed class GuardedUploadStream(
    Stream decoded, CountingReadStream? encoded, GpuRunnerOptions options, Func<long?> freeBytes) : Stream
{
    /// <summary>How often (decoded bytes) the free space is re-read.</summary>
    public const long DiskCheckInterval = 16L * 1024 * 1024;

    private long total;
    private long nextDiskCheck = DiskCheckInterval;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => total;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Checked(decoded.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Checked(await decoded.ReadAsync(buffer, cancellationToken));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int Checked(int read)
    {
        total += read;
        if (encoded is not null && total > options.GzipRatioGraceBytes && total > encoded.Count * options.MaxGzipRatio)
        {
            throw new RunnerUploadRefusedException(
                RunnerJobOutcome.Invalid, $"the gzip body inflates more than {options.MaxGzipRatio}x ({encoded.Count} -> {total} bytes)");
        }

        if (total >= nextDiskCheck)
        {
            nextDiskCheck = total + DiskCheckInterval;
            if (freeBytes() is { } free && free < options.MinFreeDiskBytes)
            {
                throw new RunnerUploadRefusedException(
                    RunnerJobOutcome.InsufficientStorage, $"the capture store is down to {free / (1024 * 1024)} MB free");
            }
        }

        return read;
    }
}
