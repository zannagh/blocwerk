// <copyright file="HoldActionOutcomes.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The wording for every action that changes the wall model rather than one hold's drawing: one
/// sentence that says what the tool does, and the concrete outcome for the holds picked. Kept in
/// one place so the editor's tools and the duplicate review describe the same things the same way,
/// and so each sentence can be checked against what the service really does.
/// </summary>
internal static class HoldActionOutcomes
{
    /// <summary>Names are listed up to this many boulders; beyond it only the count is given.</summary>
    public const int MaxNamedBoulders = 5;

    public const string MakeActualWhat = "Make actual turns a virtual (dotted) hold into a normal hold: either where it is, or merged into a detected hold that is the same hold on the wall.";

    public const string JoinVirtualWhat = "Join merges two virtual holds that stand for the same hold: the first one you tap stays, the second disappears and its boulders move over.";

    public const string MarkChangedWhat = "Mark changed says a hold was physically swapped, turned or re-set: it flags the hold and every boulder using it for review.";

    public const string MarkUnchangedWhat = "Mark unchanged says a hold did not physically change: it clears the hold's changed mark and brings back boulders that only went historic because of it.";

    public const string DeleteWhat = "Delete removes a hold from the wall when you save. Boulders using it become historic.";

    private const string NoUndo = "This cannot be undone.";

    private const string UnknownBoulders = "Which boulders use it is not known here.";

    /// <summary>A virtual hold becomes a normal hold where it is.</summary>
    public static HoldActionOutcome MakeActualAsIs(Hold hold, IReadOnlyList<HoldUsageRef>? usage)
    {
        var name = Label(hold, "The virtual hold");
        var boulders = usage is null ? UnknownBoulders
            : usage.Count == 0 ? "No boulder uses it."
            : $"{Boulders(usage)} {KeepVerb(usage)} using it and {IsAre(usage)} not flagged.";
        return new HoldActionOutcome(
            $"{name} becomes a normal hold exactly where it is. Its position, shape and name stay.",
            boulders,
            $"{NoUndo} A hold cannot be made virtual again.",
            Danger: true);
    }

    /// <summary>A virtual hold absorbs a detected hold: the virtual one survives with the detected geometry.</summary>
    public static HoldActionOutcome MergeVirtual(
        Hold virtualHold, Hold detected, IReadOnlyList<HoldUsageRef>? virtualUsage, IReadOnlyList<HoldUsageRef>? detectedUsage)
    {
        var stays = Label(virtualHold, "The virtual hold");
        var goes = Label(detected, "the detected hold");
        var boulders = virtualUsage is null || detectedUsage is null ? UnknownBoulders
            : (virtualUsage.Count == 0 ? "No boulder uses the virtual hold." : $"{Boulders(virtualUsage)} {KeepVerb(virtualUsage)} the hold that stays.")
              + (detectedUsage.Count == 0 ? string.Empty : $" {Boulders(detectedUsage)} of the detected hold {MoveVerb(detectedUsage)} over to it.")
              + " The hold is marked as changed; no boulder is flagged or made historic.";
        return new HoldActionOutcome(
            $"{stays} stays and becomes a normal hold. It takes the position, shape, colour and type of {goes}, which disappears as a separate hold.",
            boulders,
            NoUndo,
            Danger: true);
    }

    /// <summary>Two virtual holds become one: the survivor is untouched, the duplicate goes.</summary>
    public static HoldActionOutcome JoinVirtual(
        Hold survivor, Hold duplicate, IReadOnlyList<HoldUsageRef>? duplicateUsage)
    {
        var stays = Label(survivor, "The first hold");
        var goes = Label(duplicate, "The second hold");
        var boulders = duplicateUsage is null ? UnknownBoulders
            : duplicateUsage.Count == 0 ? "No boulder uses the hold that disappears."
            : $"{Boulders(duplicateUsage)} {MoveVerb(duplicateUsage)} over to the hold that stays. Nothing is flagged or made historic.";
        return new HoldActionOutcome(
            $"{stays} stays exactly as it is and remains virtual. {goes} disappears.",
            boulders,
            NoUndo,
            Danger: true);
    }

    /// <summary>The hold, and every boulder using it, is flagged for review.</summary>
    public static HoldActionOutcome MarkChanged(Hold hold, IReadOnlyList<HoldUsageRef>? usage, bool onSave = false)
    {
        var name = Label(hold, "This hold");
        var when = onSave ? " when you save" : string.Empty;
        var boulders = usage is null ? UnknownBoulders
            : usage.Count == 0 ? "No boulder uses it, so no boulder is flagged."
            : $"{Boulders(usage)} {IsAre(usage)} flagged for review{when}.";
        var undo = onSave
            ? "Discard takes it back until you save."
            : "This cannot be undone here: Mark unchanged clears the hold's mark again, but the boulders stay flagged until they are reviewed.";
        return new HoldActionOutcome(
            $"{name} is marked as physically changed{when}, together with its copies on other photos. The hold itself stays as it is.",
            boulders,
            undo,
            Danger: !onSave);
    }

    /// <summary>The hold's changed mark is cleared and boulders that went historic over it may come back.</summary>
    public static HoldActionOutcome MarkUnchanged(Hold hold, IReadOnlyList<HoldUsageRef>? usage)
    {
        var name = Label(hold, "This hold");
        var historic = usage?.Where(u => u.IsHistoric).ToList();
        var boulders = historic is null ? UnknownBoulders
            : historic.Count == 0 ? "No historic boulder uses it, so no boulder changes."
            : $"{Boulders(historic)} {(Count(historic) == 1 ? "becomes" : "become")} current again, each only if all its other holds still exist and none of them is still marked changed.";
        return new HoldActionOutcome(
            $"{name} is marked as unchanged, together with its copies on other photos. The hold itself stays as it is.",
            boulders,
            NoUndo,
            Danger: true);
    }

    /// <summary>The hold is removed on save and the boulders using it become historic.</summary>
    public static HoldActionOutcome Delete(Hold hold, IReadOnlyList<HoldUsageRef>? usage)
    {
        var name = Label(hold, "This hold");
        var active = usage?.Where(u => !u.IsHistoric).ToList();
        var boulders = active is null ? UnknownBoulders
            : active.Count == 0 ? "No current boulder uses it."
            : $"{Boulders(active)} {(Count(active) == 1 ? "becomes" : "become")} historic when you save.";
        return new HoldActionOutcome(
            $"{name} disappears from the wall when you save.",
            boulders,
            "Discard takes it back until you save. After saving it cannot be undone.",
            Danger: true);
    }

    /// <summary>A pair from the duplicate review becomes one hold. Undoable, so not a danger.</summary>
    public static HoldActionOutcome DuplicateMerge(HoldDuplicateItem item, HoldMergeMode mode)
    {
        var keepLeft = mode == HoldMergeMode.KeepLeft || (mode == HoldMergeMode.KeepHandUseDetectedShape && item.Left.IsHandMade);
        var (kept, removed) = keepLeft ? (item.Left, item.Right) : (item.Right, item.Left);
        var (keptSide, removedSide) = keepLeft ? ("left", "right") : ("right", "left");
        var change = mode == HoldMergeMode.KeepHandUseDetectedShape
            ? "keeps its name and colour and takes the position, outline and size of the detected one"
            : "stays exactly as it is";
        var boulders = removed.BoulderCount == 0
            ? "No boulder uses the hold that disappears."
            : $"{Boulders(removed.BoulderCount, removed.BoulderNames)} {(removed.BoulderCount == 1 ? "moves" : "move")} over to the hold that stays. A boulder that used both holds is flagged for a quick check.";
        return new HoldActionOutcome(
            $"The {keptSide} hold{Named(kept.Name)} {change}. The {removedSide} hold{Named(removed.Name)} disappears.",
            boulders,
            "You can undo it: an Undo button appears right after the merge, and it stays under “Recent merges” below.",
            Danger: false);
    }

    /// <summary>"3 boulders" or, up to <see cref="MaxNamedBoulders"/>, "2 boulders (Crimpy, Slab one)".</summary>
    public static string Boulders(int count, IReadOnlyList<string> names) =>
        count <= MaxNamedBoulders && names.Count == count
            ? $"{PlainCopy.Plural(count, "boulder")} ({string.Join(", ", names)})"
            : PlainCopy.Plural(count, "boulder");

    private static string Boulders(IReadOnlyList<HoldUsageRef> usage)
    {
        var names = usage.GroupBy(u => u.BoulderId).Select(g => g.First().Name).ToList();
        return Boulders(names.Count, names);
    }

    private static int Count(IReadOnlyList<HoldUsageRef> usage) => usage.Select(u => u.BoulderId).Distinct().Count();

    private static string KeepVerb(IReadOnlyList<HoldUsageRef> usage) => Count(usage) == 1 ? "keeps" : "keep";

    private static string MoveVerb(IReadOnlyList<HoldUsageRef> usage) => Count(usage) == 1 ? "moves" : "move";

    private static string IsAre(IReadOnlyList<HoldUsageRef> usage) => Count(usage) == 1 ? "is" : "are";

    private static string Label(Hold hold, string fallback) =>
        string.IsNullOrWhiteSpace(hold.Name) ? fallback : $"“{hold.Name}”";

    private static string Named(string? name) => string.IsNullOrWhiteSpace(name) ? string.Empty : $" “{name}”";
}
