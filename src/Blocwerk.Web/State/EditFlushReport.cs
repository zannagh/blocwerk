// <copyright file="EditFlushReport.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.State;

/// <summary>
/// The answer of <c>POST /api/v1/maintenance/flush-edits</c>: how many circuits had an editor that can flush, and what
/// their editors did. <see cref="TimedOut"/> counts circuits that had not answered when the timeout ran out.
/// </summary>
public sealed record EditFlushReport(
    int Sessions,
    int SavedOnServer,
    int KeptOnDevice,
    int NothingToSave,
    int NotSaved,
    int TimedOut)
{
    /// <summary>The report for a flush with no editors open.</summary>
    public static EditFlushReport Empty { get; } = new(0, 0, 0, 0, 0, 0);
}
