// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Compute;
using Blocwerk.Core.Runners;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture.Replay;

/// <summary>
/// The import's files: each is streamed into the staging folder, hashed on the way and kept only when size and SHA-256
/// match the manifest. A file already in the capture store with the same hash (an earlier import) is not taken again.
/// </summary>
public sealed partial class CapturePackageService
{
    public async Task<CaptureImportFile> PutFileAsync(Guid importId, string name, Stream body, CancellationToken ct)
    {
        await EnsureAdminAsync(ct);
        return await CaptureImportLocks.RunAsync(importId, () => PutFileLockedAsync(importId, name, body, ct), ct);
    }

    private async Task<CaptureImportFile> PutFileLockedAsync(Guid importId, string name, Stream body, CancellationToken ct)
    {
        var manifest = await staging.LoadManifestAsync(importId, ct)
                       ?? throw new UserFacingException("There is no open import with this id: begin it first.");
        var expected = manifest.Files.FirstOrDefault(f => f.Name == name)
                       ?? throw new UserFacingException($"{name} is not a file of this package.");
        var state = await FileStateAsync(importId, expected, ct);
        if (state == CaptureImportFileState.Conflict)
        {
            throw new UserFacingException($"A different file named {name} is already in the capture store here.");
        }

        if (state == CaptureImportFileState.Missing)
        {
            await ReceiveAsync(importId, expected, body, ct);
            state = CaptureImportFileState.Staged;
        }

        return new CaptureImportFile(name, expected.Bytes, state);
    }

    private async Task ReceiveAsync(Guid importId, CapturePackageFile expected, Stream body, CancellationToken ct)
    {
        var part = staging.PartPath(importId, expected.Name);
        try
        {
            (long Bytes, string Sha256) received;
            await using (var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                received = await CapturePackageFiles.CopyHashedAsync(body, target, expected.Bytes, ct);
            }

            if (received.Bytes != expected.Bytes || received.Sha256 != expected.Sha256)
            {
                throw new UserFacingException(
                    $"{expected.Name} arrived with {received.Bytes} bytes and SHA-256 {received.Sha256}, not {expected.Bytes} bytes and {expected.Sha256}.");
            }

            File.Move(part, staging.StagedPath(importId, expected.Name), overwrite: true);
            logger.LogInformation("Capture import {ImportId}: {Name} ({Bytes} bytes) received and verified", importId, expected.Name, expected.Bytes);
        }
        finally
        {
            File.Delete(part);
        }
    }

    private async Task<List<CaptureImportFile>> FileStatesAsync(CapturePackageManifest m, CancellationToken ct)
    {
        var states = new List<CaptureImportFile>();
        foreach (var file in m.Files)
        {
            states.Add(new CaptureImportFile(file.Name, file.Bytes, await FileStateAsync(m.CaptureId, file, ct)));
        }

        return states;
    }

    private async Task<CaptureImportFileState> FileStateAsync(Guid importId, CapturePackageFile file, CancellationToken ct)
    {
        var stored = staging.StorePath(file.Name);
        if (File.Exists(stored))
        {
            return new FileInfo(stored).Length == file.Bytes && await CapturePackageFiles.Sha256Async(stored, ct) == file.Sha256
                ? CaptureImportFileState.Present
                : CaptureImportFileState.Conflict;
        }

        return File.Exists(staging.StagedPath(importId, file.Name)) ? CaptureImportFileState.Staged : CaptureImportFileState.Missing;
    }

    private async Task WorkerProblemsAsync(List<string> blockers, List<string> warnings, CancellationToken ct)
    {
        var client = computeClients.Get(ComputeServiceKind.Splat);
        if (!client.IsConfigured)
        {
            blockers.Add("No splat worker is configured here (SPLATSERVICE__URL): it finishes the trained view.");
            return;
        }

        try
        {
            if (!(await client.GetHealthAsync(ct)).Kinds.Contains(WallCaptureProcessor.FinishKind))
            {
                blockers.Add($"The splat worker here does not offer {WallCaptureProcessor.FinishKind}, which finishes a trained view.");
            }
        }
        catch (ComputeJobException ex)
        {
            warnings.Add($"The splat worker could not be asked for its kinds ({ex.Message}); a delivered view is retried every 15 minutes for a day.");
        }

        if (runnerOptions?.Mode == GpuRunnerMode.Off)
        {
            warnings.Add("RUNNERS__MODE is off here: a later retrain of this capture would train on the splat worker itself.");
        }
    }
}
