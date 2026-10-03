// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture.Retention;

/// <summary>What one retention rule removed (or, in a dry run, would remove).</summary>
/// <param name="Count">How many items (models, jobs, folders, files) it applied to.</param>
/// <param name="Bytes">The bytes their files took on disk.</param>
public sealed record RetentionOutcome(int Count, long Bytes)
{
    public static RetentionOutcome None { get; } = new(0, 0);

    public static RetentionOutcome operator +(RetentionOutcome a, RetentionOutcome b) => new(a.Count + b.Count, a.Bytes + b.Bytes);
}
