// <copyright file="WallMarkerPlanner.Files.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text;
using Blocwerk.Core.MarkerPlanning;
using Blocwerk.Core.Services;
using Blocwerk.Web.Components.Shared.MarkerPlanner;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The planner's whole-plan actions: generate, save, download (from the plan IN THE EDITOR, saved or
/// not, through the app's <c>blocwerkDownloadStream</c> helper), import, and start from the measured wall.
/// </summary>
public partial class WallMarkerPlanner
{
    /// <summary>Same cap the JSON reader enforces.</summary>
    private const long MaxImportBytes = 1024 * 1024;

    private void RequestGenerate()
    {
        if (plan!.Markers.Count > 0)
        {
            confirmRegenerate = true;
            return;
        }

        Generate();
    }

    private void Generate()
    {
        SetPlan(Plans.GenerateMarkers(plan!, options));
        selectedMarker = null;
        message = $"Placed {plan!.Markers.Count} markers. Drag any that land on a hold.";
        failure = null;
    }

    private Task SaveAsync() => RunAsync(async () =>
    {
        var result = await Plans.SavePlanAsync(WallId, plan!);
        issues = result.Issues;
        if (!result.Saved)
        {
            failure = "The plan has errors and was not saved — see the list above.";
            return;
        }

        dirty = false;
        hadSavedPlan = true;
        revisions = await Plans.GetRevisionsAsync(WallId);
        message = result.Unchanged
            ? $"Nothing changed — still revision {result.Revision}."
            : $"Saved as revision {result.Revision}. Download the PDF to print, and keep the JSON with your photos.";
    });

    /// <summary>"Markers swapped on the wall": records (or, with null, clears) when a revision's markers went up.</summary>
    private Task SetEffectiveAsync(MarkerRevisionEffectiveChange change) => RunAsync(async () =>
    {
        if (!await Plans.SetRevisionEffectiveAsync(WallId, change.Revision, change.EffectiveFrom))
        {
            failure = $"Revision {change.Revision} no longer exists.";
            return;
        }

        revisions = await Plans.GetRevisionsAsync(WallId);
        message = change.EffectiveFrom is { } from
            ? $"Revision {change.Revision} marked as on the wall since {from.ToLocalTime():yyyy-MM-dd}."
            : $"Revision {change.Revision} is planned, not yet on the wall.";
    });

    /// <summary>The PDF with true-size pages only for the markers added or changed since the last capture.</summary>
    private Task DownloadChangedPdfAsync() => RunAsync(async () =>
    {
        if (changes is null || changes.Diff.ToPrint.Count == 0)
        {
            return;
        }

        var bytes = Plans.RenderPdf(plan!, wallName ?? "Wall", changes.Diff.ToPrint.ToHashSet());
        await DownloadAsync($"{FileStem()}-changed-markers.pdf", "application/pdf", bytes);
    });

    private Task DownloadJsonAsync() => RunAsync(async () =>
    {
        var bytes = Encoding.UTF8.GetBytes(Plans.ToJson(plan!));
        await DownloadAsync($"{FileStem()}-marker-plan.json", "application/json", bytes);
    });

    private Task DownloadPdfAsync() => RunAsync(async () =>
    {
        if (plan!.Markers.Any(m => m.Id is < 0 or >= ArucoDict4X4.Count))
        {
            failure = $"Only marker ids 0–{ArucoDict4X4.Count - 1} can be printed. Fix the ids flagged above first.";
            return;
        }

        var bytes = Plans.RenderPdf(plan!, wallName ?? "Wall");
        await DownloadAsync($"{FileStem()}-marker-plan.pdf", "application/pdf", bytes);
        if (HasErrors)
        {
            message = "Downloaded — but the plan still has errors, so treat this PDF as a draft.";
        }
    });

    private Task ImportAsync(InputFileChangeEventArgs e) => RunAsync(async () =>
    {
        importErrors = [];
        if (e.File.Size > MaxImportBytes)
        {
            importErrors = ["The file is larger than 1 MB — is it really a marker-plan.json?"];
            return;
        }

        using var reader = new StreamReader(e.File.OpenReadStream(MaxImportBytes));
        var imported = Plans.FromJson(await reader.ReadToEndAsync(), out var errors);
        if (imported is null)
        {
            importErrors = errors;
            return;
        }

        TakeWholePlan(imported);
        message = $"Imported {e.File.Name}. Press Save to keep it.";
    });

    private Task StartFromGeometryAsync() => RunAsync(async () =>
    {
        var built = await Plans.BuildFromMeasuredGeometryAsync(WallId, plan!.Photo);
        if (built is null)
        {
            failure = "The measured wall could not be read.";
            return;
        }

        TakeWholePlan(built with { Print = plan!.Print });
        message = "Started from the measured wall. Correct the surface outlines (they are marker spans, not the real edges), then save.";
    });

    private void TakeWholePlan(MarkerPlan replacement)
    {
        selectedMarker = null;
        selectedSegment = null;
        SetPlan(replacement);
        selectedSegment = replacement.Segments.FirstOrDefault(s => s.AttachedTo is null)?.Index;
    }

    private async Task DownloadAsync(string fileName, string contentType, byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var streamRef = new DotNetStreamReference(stream);
        await JS.InvokeVoidAsync("blocwerkDownloadStream", fileName, contentType, streamRef);
    }

    private string FileStem()
    {
        var name = new string((wallName ?? "wall").Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray());
        var collapsed = string.Join('-', name.Split('-', StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length == 0 ? "wall" : collapsed;
    }

    /// <summary>Runs one action with the busy flag set; expected failures become the status line.</summary>
    private async Task RunAsync(Func<Task> action)
    {
        busy = true;
        message = null;
        failure = null;
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or ArgumentException
                                       or KioskRestrictedException or IOException or JSException)
        {
            Logger.LogWarning(ex, "Marker planner action failed for wall {WallId}", WallId);
            failure = ex.Message;
        }
        finally
        {
            busy = false;
        }
    }
}
