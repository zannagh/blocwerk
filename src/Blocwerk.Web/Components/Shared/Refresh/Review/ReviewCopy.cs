// <copyright file="ReviewCopy.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh.Review;
using Blocwerk.Web.Components.Shared.LookOver;

namespace Blocwerk.Web.Components.Shared.Refresh.Review;

/// <summary>The words of the one review: group names, what each item asks, and the name of every answer. One place for deck and list.</summary>
internal static class ReviewCopy
{
    /// <summary>A group's name on its chip.</summary>
    public static string Group(ReviewGroup group) => group switch
    {
        ReviewGroup.Unsure => "Unsure matches",
        ReviewGroup.HandPlaced => "Hand-placed",
        ReviewGroup.Replaced => "Another hold here",
        ReviewGroup.PossiblyRemoved => "Possibly removed",
        ReviewGroup.Conflicting3D => "3D sees a hold",
        ReviewGroup.NotFound => "Not found",
        ReviewGroup.LeftOut => "Left out",
        ReviewGroup.NewHold => "New holds",
        _ => "Moved?",
    };

    /// <summary>The question of an item.</summary>
    public static string Question(ReviewItem item) => item.Group switch
    {
        ReviewGroup.Unsure => $"Is {Hold(item)} the same hold on the new photo?",
        ReviewGroup.HandPlaced => $"{Capital(Hold(item))} was placed by hand and now sits on a detection. Merge the two?",
        ReviewGroup.Replaced => $"Another hold sits where {Hold(item)} was. Is the old hold still on the wall?",
        ReviewGroup.PossiblyRemoved => $"The new photo shows bare wall where {Hold(item)} was. Is it still there?",
        ReviewGroup.Conflicting3D => "The photo check left this detection out, but the 3D model sees a hold here. Is it a hold?",
        ReviewGroup.NotFound => $"{Capital(Hold(item))} was not found on the new photo. Is it still on the wall?",
        ReviewGroup.LeftOut => $"Left out: {LookOverCopy.DiscardReason(item.Reason)}. Is it a hold?",
        ReviewGroup.NewHold => "Found on the new photo, not on the old one. Is it a hold?",
        _ => $"Did {Hold(item)} move to this spot?",
    };

    /// <summary>What happens when nobody answers, under the question.</summary>
    public static string WhenLeftAlone(ReviewItem item) => item.Group switch
    {
        ReviewGroup.NotFound => "Left alone, it stays where it was and the look-over asks again after Apply.",
        ReviewGroup.LeftOut when item.LeavesFinding => "Left alone, it stays out; the look-over offers it again after Apply (it looks like a real hold).",
        ReviewGroup.LeftOut => "Left alone, it stays out.",
        ReviewGroup.NewHold => "Left alone, it goes live as a new hold.",
        ReviewGroup.Moved => "Left alone, the old hold stays where it was.",
        ReviewGroup.Conflicting3D => "Left alone, it stays out and the look-over asks again after Apply.",
        _ => "Left alone, the update does what it proposes and the look-over asks again after Apply.",
    };

    /// <summary>The button of an answer on an item.</summary>
    public static string Answer(ReviewItem item, ReviewAnswer answer) => (item.Group, answer) switch
    {
        (ReviewGroup.Unsure, ReviewAnswer.Keep) => "Same hold",
        (ReviewGroup.Unsure, ReviewAnswer.NotSame) => "Not the same hold",
        (ReviewGroup.HandPlaced, ReviewAnswer.Keep) => item.Answers.Count == 1 ? "Understood" : "Merge is right",
        (ReviewGroup.HandPlaced, ReviewAnswer.Remove) => "Don't merge",
        (ReviewGroup.Replaced or ReviewGroup.PossiblyRemoved, ReviewAnswer.Keep) => "Old hold still there",
        (ReviewGroup.Replaced or ReviewGroup.PossiblyRemoved, ReviewAnswer.Remove) => Gone(item),
        (ReviewGroup.Conflicting3D, ReviewAnswer.Keep) => "Add as a hold",
        (ReviewGroup.Conflicting3D, ReviewAnswer.Remove) => "Not a hold",
        (_, ReviewAnswer.StillThere) => "Still there",
        (_, ReviewAnswer.Gone) => Gone(item),
        (_, ReviewAnswer.KeepNew) => "It is a hold",
        (_, ReviewAnswer.NotAHold) => "Not a hold",
        (_, ReviewAnswer.LeaveOut) => "Not a hold",
        (_, ReviewAnswer.AddAsHold) => "Add as a hold",
        (_, ReviewAnswer.SameHold) => "Same hold, not moved",
        (_, ReviewAnswer.Moved) => "It moved here",
        (_, ReviewAnswer.Dismiss) => "Not related",
        _ => answer.ToString(),
    };

    /// <summary>"Accept all N as suggested" for a group.</summary>
    public static string AcceptAll(ReviewGroup group, int count) => group switch
    {
        ReviewGroup.NotFound => $"All {count}: still there",
        ReviewGroup.LeftOut => $"All {count}: not a hold",
        ReviewGroup.NewHold => $"All {count}: it is a hold",
        ReviewGroup.Moved => $"All {count}: not related",
        ReviewGroup.Conflicting3D => $"All {count}: not a hold",
        _ => $"All {count}: as proposed",
    };

    /// <summary>What accepting a whole group does, asked first.</summary>
    public static string AcceptAllQuestion(ReviewGroup group, int count) => group switch
    {
        ReviewGroup.NotFound => $"Mark {PlainCopy.Plural(count, "hold")} as still on the wall without looking at each? They stay where they were and are not asked about after Apply.",
        ReviewGroup.LeftOut => $"Leave {PlainCopy.Plural(count, "detection")} out for good? None of them is offered again after Apply.",
        ReviewGroup.NewHold => $"Keep {PlainCopy.Plural(count, "detection")} as new holds? They go live with Apply.",
        _ => $"Answer {PlainCopy.Plural(count, "item")} as the update proposes?",
    };

    /// <summary>The note about an answered item.</summary>
    public static string Answered(ReviewItem item) =>
        item.Answer is { } answer ? $"{Answer(item, answer)}{(item.AnsweredBy is { } by ? $" · {by}" : null)}" : string.Empty;

    private static string Gone(ReviewItem item) =>
        item.Boulders.Count switch
        {
            0 => "Gone",
            1 => "Gone (1 boulder becomes history)",
            var n => $"Gone ({n} boulders become history)",
        };

    private static string Hold(ReviewItem item) => item.HoldName is { Length: > 0 } name ? $"the {name} hold" : "this hold";

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
