// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture.Retention;

/// <summary>A model family (<see cref="Corrections.ModelFamily"/>) as <see cref="SupersededModelSelection"/> ranks it.</summary>
/// <param name="Root">The family's first model.</param>
/// <param name="Members">Its models.</param>
/// <param name="Pending">Whether a member is the model of a capture awaiting an admin's decision.</param>
internal sealed record RetainedFamily(Guid Root, IReadOnlyList<RetainedModel> Members, bool Pending)
{
    /// <summary>Whether a member was ever the wall's active model.</summary>
    public bool EverActive => Members.Any(m => m.RetiredAt is not null || m.IsActive);

    /// <summary>Whether a member still has texture or splat rows.</summary>
    public bool HasFiles => Members.Any(m => m.HasFiles);

    /// <summary>When a member was last live.</summary>
    public DateTimeOffset Since => Members.Max(m => m.Since);

    /// <summary>When its newest member was stored (orders families retired at the same moment).</summary>
    public DateTimeOffset CreatedAt => Members.Max(m => m.CreatedAt);
}
