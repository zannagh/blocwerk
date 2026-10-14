// <copyright file="WallLookOverService.Pictures.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Refresh;

namespace Blocwerk.Core.LookOver;

/// <summary>
/// The pictures of each listed finding with the holds to outline on them, so the page can draw the holds' real
/// outlines over a plain picture (as on the wall) instead of a ring burnt into it. A picture without anything to
/// outline, or whose photo could not be measured, comes with an empty list and keeps its ring.
/// </summary>
public sealed partial class WallLookOverService
{
    private static async Task<List<LookOverFindingView>> WithPicturesAsync(
        BlocwerkDbContext db, Guid wallId, IReadOnlyList<WallLookOverFinding> findings, List<LookOverFindingView> views, CancellationToken ct)
    {
        var frames = await LookOverFrameSource.LoadAsync(db, wallId, findings, ct);
        var byId = findings.ToDictionary(f => f.Id);
        return views.Select(v => v with { Pictures = Pictures(byId[v.Id], v, frames) }).ToList();
    }

    private static List<LookOverPicture> Pictures(WallLookOverFinding finding, LookOverFindingView view, LookOverFrameSource frames)
    {
        var pictures = new List<LookOverPicture>();
        if (view.HasOld)
        {
            var old = frames.Hold(LookOverFrameSource.BeforeHoldId(finding));
            pictures.Add(new LookOverPicture(CheckCropView.Old, frames.Before(finding) is { } before && old is not null ? [before.Outline(old, kept: false)] : []));
        }

        pictures.Add(new LookOverPicture(CheckCropView.New, frames.Now(finding) is { } now ? NowOutlines(finding, view, frames, now) : []));
        if (view.HasModel)
        {
            pictures.Add(new LookOverPicture(CheckCropView.Model, []));
        }

        return pictures;
    }

    /// <summary>
    /// What is on the spot now: the live hold the finding is about (faded while it is kept, not confirmed) and the new
    /// hold on its spot, the faded one first so it lies below. A detection that was left out is only a spot; a
    /// discarded detection is drawn with the outline it was detected with.
    /// </summary>
    private static List<LookOverOutline> NowOutlines(WallLookOverFinding finding, LookOverFindingView view, LookOverFrameSource frames, CropFrame now)
    {
        if (finding.HoldId is null)
        {
            return [finding.Kind == Enums.LookOverFindingKind.DiscardedDetection ? now.Outline(DiscardedDetections.AsHold(finding), kept: false) : now.Spot()];
        }

        var outlines = new List<LookOverOutline>();
        if (frames.Hold(view.HoldId) is { } hold)
        {
            outlines.Add(now.Outline(hold, kept: hold.KeptUnconfirmedGeneration is not null));
        }

        // An unsure link's other hold is on the other photo: it has the first picture to itself.
        if (finding.Kind != Enums.LookOverFindingKind.UnsureLink && frames.Hold(view.OtherHoldId) is { } other)
        {
            outlines.Add(now.Outline(other, kept: false));
        }

        return outlines;
    }
}
