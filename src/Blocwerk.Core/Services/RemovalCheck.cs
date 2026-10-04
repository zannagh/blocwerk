// <copyright file="RemovalCheck.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Geometry.TextureRegistration;

namespace Blocwerk.Core.Services;

/// <summary>One old hold not found again on a staged panel, in RAW pixels of the old and the staged photo.</summary>
/// <param name="OldHoldId">The old hold.</param>
/// <param name="OldX">Its centre's x on the old photo.</param>
/// <param name="OldY">Its centre's y on the old photo.</param>
/// <param name="NewX">The x of the matcher's warp-predicted centre on the staged photo.</param>
/// <param name="NewY">The y of the matcher's warp-predicted centre on the staged photo.</param>
/// <param name="RadiusPx">Its radius on the staged photo.</param>
public sealed record RemovalCandidate(Guid OldHoldId, double OldX, double OldY, double NewX, double NewY, double RadiusPx);

/// <summary>What the removal check of one staged panel knows (all RAW pixels).</summary>
/// <param name="Candidates">The panel's old holds not found again, with a predicted spot.</param>
/// <param name="NewToOld">Matched pairs, staged photo to old photo (the local scale between the two photos).</param>
/// <param name="StagedHolds">Every staged detection's centre on the staged photo.</param>
/// <param name="NewSize">The staged photo's size.</param>
/// <param name="Evidence">The staged photo registered onto the model, with the model's known and seen holds.</param>
/// <param name="Frames">The model's texture grids by facet.</param>
/// <param name="TextureFromThisVisit">The model was built after the photo was staged (its texture shows today's wall).</param>
public sealed record RemovalPanel(
    IReadOnlyList<RemovalCandidate> Candidates,
    IReadOnlyList<PointPair> NewToOld,
    IReadOnlyList<(double X, double Y)> StagedHolds,
    (int Width, int Height) NewSize,
    Panel3DEvidence Evidence,
    IReadOnlyDictionary<string, TexturePlaneFrame> Frames,
    bool TextureFromThisVisit);

/// <summary>The verdict on one old hold, with the spot and the scores it rests on.</summary>
/// <param name="OldHoldId">The old hold.</param>
/// <param name="Verdict">The verdict.</param>
/// <param name="X">The x of the predicted spot on the staged photo, normalised.</param>
/// <param name="Y">The y of the predicted spot on the staged photo, normalised.</param>
/// <param name="Spot">Where the spot lands on the model's textures, or null.</param>
/// <param name="PhotoScore">Old photo against the staged photo.</param>
/// <param name="TextureScore">Old photo against the texture.</param>
public sealed record RemovalFinding(
    Guid OldHoldId, RemovalVerdict Verdict, double X, double Y, TextureSpot? Spot, double? PhotoScore, double? TextureScore);

/// <summary>Scores presence queries against one facet's texture: the texture is the searched (old) image.</summary>
/// <param name="facetId">The facet whose texture is searched.</param>
/// <param name="queries">The queries (the "new" side is the old panel photo).</param>
/// <returns>One score per query.</returns>
public delegate IReadOnlyList<double?> TextureProbe(string facetId, IReadOnlyList<PresenceQuery> queries);

/// <summary>
/// "Possibly removed": for each old hold not found again, looks at its predicted spot on the staged photo and on this
/// visit's 3D texture, and judges it with <see cref="PossiblyRemovedJudge"/>. The probes compare the old photo's patch
/// around the hold with the staged photo (<c>photoProbe</c>) and with the texture (<c>textureProbe</c>); only holds
/// whose spot registered are probed at all. Pure apart from the probes, which are batched per photo and per facet.
/// </summary>
public static class RemovalCheck
{
    /// <summary>A detection this close (px, or 1.5 hold radii when larger) to the predicted spot means something is there.</summary>
    public const double MinDetectionCatchPx = 30;

    /// <summary>Judges every candidate of one panel.</summary>
    /// <param name="panel">The panel.</param>
    /// <param name="photoProbe">Old photo against the staged photo; null without a probe or an old photo.</param>
    /// <param name="textureProbe">Old photo against a facet texture; null without a probe.</param>
    /// <returns>One finding per candidate.</returns>
    public static IReadOnlyList<RemovalFinding> Run(
        RemovalPanel panel,
        Func<IReadOnlyList<PresenceQuery>, IReadOnlyList<double?>>? photoProbe,
        TextureProbe? textureProbe)
    {
        var (w, h) = panel.NewSize;
        var items = panel.Candidates.Select(c => Prepare(panel, c, w, h)).ToList();
        var probed = Enumerable.Range(0, items.Count).Where(k => items[k].Photo is not null && items[k].Texture is not null).ToList();
        var photoScores = new double?[items.Count];
        var textureScores = new double?[items.Count];
        if (photoProbe is not null && probed.Count > 0)
        {
            Fill(photoScores, probed, photoProbe(probed.Select(k => items[k].Photo!.Value).ToList()));
        }

        if (textureProbe is not null)
        {
            foreach (var facet in probed.GroupBy(k => items[k].Spot!.FacetId, StringComparer.Ordinal))
            {
                var list = facet.ToList();
                Fill(textureScores, list, textureProbe(facet.Key, list.Select(k => items[k].Texture!.Value).ToList()));
            }
        }

        return items.Select((i, k) =>
        {
            var evidence = i.Evidence with { PhotoScore = photoScores[k], TextureScore = textureScores[k] };
            var verdict = PossiblyRemovedJudge.Judge(evidence);
            return new RemovalFinding(
                i.Candidate.OldHoldId, verdict, i.Candidate.NewX / w, i.Candidate.NewY / h, i.Spot, photoScores[k], textureScores[k]);
        }).ToList();
    }

    private static (RemovalCandidate Candidate, RemovalEvidence Evidence, TextureSpot? Spot, PresenceQuery? Photo, PresenceQuery? Texture) Prepare(
        RemovalPanel panel, RemovalCandidate c, int w, int h)
    {
        var nx = c.NewX / w;
        var ny = c.NewY / h;
        var spot = w > 0 && h > 0 ? TextureSpotLocator.Locate(panel.Evidence.Registrations, nx, ny) : null;
        var frame = spot is not null && panel.Frames.TryGetValue(spot.FacetId, out var f) && f.IsValid ? f : null;
        spot = frame is null ? null : spot;
        var catchPx = Math.Max(MinDetectionCatchPx, 1.5 * c.RadiusPx);
        var detection = panel.StagedHolds.Any(s => Math.Sqrt(((s.X - c.NewX) * (s.X - c.NewX)) + ((s.Y - c.NewY) * (s.Y - c.NewY))) <= catchPx);
        var seen = spot is not null
            && (NewHoldEvidence3D.IsNear(panel.Evidence.SeenIn3D, spot.FacetId, spot.A, spot.B)
                || NewHoldEvidence3D.IsNear(panel.Evidence.KnownHolds, spot.FacetId, spot.A, spot.B));
        var evidence = new RemovalEvidence(spot is not null, panel.TextureFromThisVisit, detection, seen, null, null);
        if (spot is null || !panel.TextureFromThisVisit || detection || seen)
        {
            return (c, evidence, spot, null, null);
        }

        var oldPerNew = LocalAffine.Scale(panel.NewToOld, c.NewX, c.NewY);
        var mmPerNew = TextureSpotLocator.MmPerPhotoPx(spot, nx, ny, w, h);
        if (oldPerNew is not > 0 || mmPerNew is null)
        {
            return (c, evidence, spot, null, null);
        }

        var (tx, ty) = frame!.ToPixel(spot.A, spot.B);
        var texturePerOld = mmPerNew.Value / frame.MmPerPxA / oldPerNew.Value;
        var photo = new PresenceQuery(c.NewX, c.NewY, c.OldX, c.OldY, oldPerNew.Value);
        var texture = new PresenceQuery(c.OldX, c.OldY, tx, ty, texturePerOld);
        return (c, evidence, spot, photo, texture);
    }

    private static void Fill(double?[] target, List<int> indices, IReadOnlyList<double?> scores)
    {
        for (var k = 0; k < indices.Count; k++)
        {
            target[indices[k]] = k < scores.Count ? scores[k] : null;
        }
    }
}
