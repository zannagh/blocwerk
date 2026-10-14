// <copyright file="RedetectCard.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Redetect;

namespace Blocwerk.Web.Components.Shared.Redetect;

/// <summary>One outline on a picture of the re-detect review.</summary>
/// <param name="Hold">The shape: position, radius, outline and holes.</param>
/// <param name="Role">How it is drawn: the hold in question, the hold under it, or a hold that would go.</param>
public sealed record RedetectOutline(Hold Hold, RedetectOutlineRole Role);

/// <summary>How an outline of the review is drawn.</summary>
public enum RedetectOutlineRole
{
    /// <summary>The hold the proposal is about.</summary>
    Focus,

    /// <summary>The hold another one sits on.</summary>
    Base,

    /// <summary>A hold that goes when the proposal is accepted.</summary>
    Going,

    /// <summary>A hold that sits on the hold the proposal is about.</summary>
    OnIt,
}

/// <summary>The square part of the photo a card shows, in the 0..100 units the hold overlay is drawn in.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height (the same length on the photo as <paramref name="Width"/>).</param>
public readonly record struct RedetectFrame(double X, double Y, double Width, double Height);

/// <summary>A proposal with its two pictures: the wall as it is stored, and as it would be.</summary>
/// <param name="Proposal">The proposal.</param>
/// <param name="Frame">The part of the photo both pictures show.</param>
/// <param name="Now">The outlines stored now.</param>
/// <param name="Then">The outlines after accepting.</param>
public sealed record RedetectCard(RedetectProposal Proposal, RedetectFrame Frame, IReadOnlyList<RedetectOutline> Now, IReadOnlyList<RedetectOutline> Then)
{
    /// <summary>Room around the outlines, as a factor on their extent.</summary>
    public const double Room = 1.9;

    /// <summary>Builds the cards of a plan.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="aspect">Photo width / height.</param>
    /// <returns>One card per proposal, in the plan's order.</returns>
    public static List<RedetectCard> Build(PanelRedetectPlan plan, double aspect) =>
        [.. plan.Proposals.Select(p => Of(p, plan, aspect))];

    private static RedetectCard Of(RedetectProposal proposal, PanelRedetectPlan plan, double aspect)
    {
        Hold? Stored(Guid? id) => id is { } key && plan.Holds.TryGetValue(key, out var hold) ? hold : null;
        var hold = Stored(proposal.HoldId);
        var others = proposal.OtherHoldIds.Select(id => Stored(id)).OfType<Hold>().ToList();
        var detected = proposal.Detections.Select(i => plan.Detections[i].Shape).ToList();
        var now = new List<RedetectOutline>();
        var then = new List<RedetectOutline>();
        if (hold is not null)
        {
            now.Add(new RedetectOutline(hold, RedetectOutlineRole.Focus));
        }

        switch (proposal.Kind)
        {
            case RedetectProposalKind.Merge:
                now.AddRange(others.Select(o => new RedetectOutline(o, RedetectOutlineRole.Going)));
                then.Add(new RedetectOutline(hold!, RedetectOutlineRole.Focus));
                break;
            case RedetectProposalKind.Stack:
                now.AddRange(others.Select(o => new RedetectOutline(o, RedetectOutlineRole.Focus)));
                then.AddRange(others.Select(o => new RedetectOutline(o, RedetectOutlineRole.Base)));
                then.Add(new RedetectOutline(hold!, RedetectOutlineRole.Focus));
                break;
            case RedetectProposalKind.NotFound:
                break;
            case RedetectProposalKind.NewHold:
                now.AddRange(others.Select(o => new RedetectOutline(o, RedetectOutlineRole.OnIt)));
                then.AddRange(detected.Select(d => new RedetectOutline(d, RedetectOutlineRole.Base)));
                then.AddRange(others.Select(o => new RedetectOutline(o, RedetectOutlineRole.OnIt)));
                break;
            default:
                then.AddRange(detected.Select(d => new RedetectOutline(d, RedetectOutlineRole.Focus)));
                break;
        }

        return new RedetectCard(proposal, FrameOf([.. now.Concat(then).Select(o => o.Hold)], aspect), now, then);
    }

    /// <summary>The square (on the photo) around the given holds with room to see their surroundings.</summary>
    private static RedetectFrame FrameOf(List<Hold> holds, double aspect)
    {
        // Heights are measured in widths (y * 1 / aspect of a square photo), so the frame is square on the photo.
        var xs = holds.SelectMany(h => Extent(h, y: false)).ToList();
        var ys = holds.SelectMany(h => Extent(h, y: true)).ToList();
        if (xs.Count == 0)
        {
            return new RedetectFrame(0, 0, 100, 100);
        }

        var (cx, cy) = ((xs.Min() + xs.Max()) / 2, (ys.Min() + ys.Max()) / 2);
        var side = Math.Max(0.03, Room * Math.Max(xs.Max() - xs.Min(), (ys.Max() - ys.Min()) / aspect));
        return new RedetectFrame((cx - (side / 2)) * 100, (cy - (side * aspect / 2)) * 100, side * 100, side * aspect * 100);
    }

    private static IEnumerable<double> Extent(Hold hold, bool y)
    {
        if (hold.ShapePoints is { Count: >= 3 } shape)
        {
            return shape.Select(p => y ? hold.Y + p.Dy : hold.X + p.Dx);
        }

        // A plain circle is drawn on the stretched 0..100 square: the radius counts along each side.
        return y ? [hold.Y - hold.Radius, hold.Y + hold.Radius] : [hold.X - hold.Radius, hold.X + hold.Radius];
    }
}
