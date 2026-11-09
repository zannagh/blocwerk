// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Recognition;

/// <summary>
/// The recognition job kinds of runner protocol 1.2 (§1.4). Each name is also the capability a runner advertises in its
/// hello to be offered jobs of that kind.
/// </summary>
public static class RecognitionKinds
{
    /// <summary>Hold boxes on 1..8 photos; the model goes by hash.</summary>
    public const string DetectHolds = "detect-holds@2";

    /// <summary>Hold outlines from seeds on one photo.</summary>
    public const string OutlineHolds = "outline-holds";

    /// <summary>ArUco markers on one photo.</summary>
    public const string Markers = "markers";

    /// <summary>Hold matching between two overlapping panel photos.</summary>
    public const string MatchPanels = "match-panels";

    /// <summary>Whether holds are still where they were, old photo against new.</summary>
    public const string Presence = "presence";

    /// <summary>A texture facet matched into a photo.</summary>
    public const string TextureMatch = "texture-match";

    /// <summary>Every recognition kind.</summary>
    public static IReadOnlyList<string> All { get; } = [DetectHolds, OutlineHolds, Markers, MatchPanels, Presence, TextureMatch];

    /// <summary>Whether <paramref name="kind"/> is a recognition kind.</summary>
    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind);
}
