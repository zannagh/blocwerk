// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture.Replay;

/// <summary>
/// The import's first call: the package is checked on its own (<see cref="ShapeProblems"/>: consistent ids, the file list
/// exactly the rows' files), then against this server (<see cref="CheckAsync"/>). A consistent package is kept in the
/// import's staging folder for the uploads and the commit; the import id is the capture id, so a re-run finds it again.
/// </summary>
public sealed partial class CapturePackageService
{
    public async Task<CaptureImportReport> BeginImportAsync(CapturePackageManifest manifest, CancellationToken ct)
    {
        var userId = await EnsureAdminAsync(ct);
        var problems = ShapeProblems(manifest);
        if (problems.Count > 0)
        {
            return new CaptureImportReport(manifest.CaptureId, manifest.CaptureId, manifest.WallId, false, false, problems, [], [], 0, null);
        }

        // Kept even when the capture is already here: a commit after this begin then answers "already imported".
        await staging.SaveManifestAsync(manifest, ct);
        var report = await CheckAsync(manifest, ct);
        logger.LogInformation(
            "Capture import {ImportId} begun by {UserId}: {Blockers} blocker(s), {Missing} of {Files} file(s) to upload, already imported {Already}",
            manifest.CaptureId, userId, report.Blockers.Count, report.Files.Count(f => f.State == CaptureImportFileState.Missing),
            report.Files.Count, report.AlreadyImported);
        return report;
    }

    /// <summary>What is wrong with the package itself, whatever the target (empty when it is consistent).</summary>
    internal static List<string> ShapeProblems(CapturePackageManifest m)
    {
        if (m.FormatVersion != CapturePackageManifest.CurrentFormat)
        {
            return [$"Package format {m.FormatVersion} is not supported here (this server reads format {CapturePackageManifest.CurrentFormat})."];
        }

        if (m.Rows?.Capture is null || m.Rows.Model is null || m.Rows.GpuJob is null || m.Rows.Photos is null || m.Rows.Textures is null || m.Files is null)
        {
            return ["The package has no rows or no file list."];
        }

        var problems = RowProblems(m);
        problems.AddRange(FileProblems(m));
        return problems;
    }

    private static List<string> RowProblems(CapturePackageManifest m)
    {
        var (capture, model, job) = (m.Rows.Capture, m.Rows.Model, m.Rows.GpuJob);
        var problems = new List<string>();
        if (capture.Id != m.CaptureId || capture.WallId != m.WallId || capture.CreatedByUserId != m.OwnerUserId
            || capture.GeometryModelId != model.Id || model.WallId != m.WallId)
        {
            problems.Add("The capture row does not match the package (capture, wall, owner or model id).");
        }

        if (m.Rows.Photos.Any(p => p.CaptureId != capture.Id) || m.Rows.Textures.Any(t => t.GeometryModelId != model.Id))
        {
            problems.Add("A photo or texture row belongs to another capture or model.");
        }

        if (job.CaptureId != capture.Id || job.GeometryModelId != model.Id || job.WallId != m.WallId || job.ResultPath is null)
        {
            problems.Add("The GPU job row does not belong to the capture, or carries no trained result.");
        }

        if (model.DerivedFromModelId is not null || RegisteredGeometry.Carried(model.Json).CarriedFacets.Count > 0)
        {
            problems.Add("The model builds on an earlier model (a correction, or facets carried over) that the package does not carry.");
        }

        problems.AddRange(WallGlyphService.ParseAndValidate(model.Json).Errors.Select(e => $"The model does not validate: {e}"));
        return problems;
    }

    private static List<string> FileProblems(CapturePackageManifest m)
    {
        var problems = new List<string>();
        var bad = m.Files.Where(f => !CapturePackageFiles.IsStoredName(f.Name) || f.Bytes < 0 || f.Sha256?.Length != 64).ToList();
        if (bad.Count > 0)
        {
            problems.Add($"Malformed file entries: {string.Join(", ", bad.Take(5).Select(f => f.Name))}.");
        }

        var listed = m.Files.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var referenced = CapturePackageFiles.Referenced(m.Rows).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        if (listed.Count != m.Files.Count || !listed.SetEquals(referenced))
        {
            problems.Add("The file list is not exactly the files the rows reference.");
        }

        return problems;
    }
}
