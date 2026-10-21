// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Feed;

/// <summary>
/// Narrows the audience of every feed row the enclosed saves write (ambient, like <see cref="FeedScope"/>). The staged
/// generation of an update in progress is visible only to the wall's editors, who run the update procedure, so the
/// staging, the triage, a discard and the staged single-hold edits run inside
/// <c>Begin(FeedAudience.Setters)</c>: share, kiosk and plain-member viewers do not learn that an update is being
/// prepared. Rows for one user (<see cref="FeedAudience.User"/>) keep their audience.
/// </summary>
public static class FeedAudienceScope
{
    private static readonly AsyncLocal<FeedAudience?> Ambient = new();

    /// <summary>Gets the narrowest audience the enclosed saves may write to, or null for no narrowing.</summary>
    public static FeedAudience? Current => Ambient.Value;

    /// <summary>Narrows to <paramref name="audience"/> (never widens an outer narrowing) until disposed.</summary>
    public static IDisposable Begin(FeedAudience audience)
    {
        var outer = Ambient.Value;
        Ambient.Value = outer is { } o && Rank(o) > Rank(audience) ? o : audience;
        return new FeedAudienceScopeHandle(outer);
    }

    /// <summary>Applies the current narrowing to <paramref name="rows"/> in place.</summary>
    internal static void Apply(IEnumerable<WallChange> rows)
    {
        if (Current is not { } narrowest)
        {
            return;
        }

        foreach (var row in rows.Where(r => r.Audience != FeedAudience.User && Rank(r.Audience) < Rank(narrowest)))
        {
            row.Audience = narrowest;
        }
    }

    internal static void Reset(FeedAudience? outer) => Ambient.Value = outer;

    // Wall ⊃ Members ⊃ Setters: a higher rank reaches fewer viewers.
    private static int Rank(FeedAudience audience) => audience switch
    {
        FeedAudience.Setters => 2,
        FeedAudience.Members => 1,
        _ => 0,
    };
}
