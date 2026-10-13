// <copyright file="RedetectProposal.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Redetect;

/// <summary>What a re-detect proposes for one spot.</summary>
public enum RedetectProposalKind
{
    /// <summary>An automatic hold gets the newly detected shape and position.</summary>
    BetterShape = 0,

    /// <summary>A hold a person placed, moved or drew is offered the detected shape; without an answer it stays as it is.</summary>
    HandShape = 1,

    /// <summary>One stored hold where the detector sees several holds.</summary>
    Split = 2,

    /// <summary>Two stored holds that are the same hold recorded twice.</summary>
    Merge = 3,

    /// <summary>A stored hold that sits on another stored hold.</summary>
    Stack = 4,

    /// <summary>A detection where no hold is stored.</summary>
    NewHold = 5,

    /// <summary>An automatic hold nothing depends on that was not found again.</summary>
    NotFound = 6,
}

/// <summary>
/// One thing a re-detect would change, for a person to accept or leave. Nothing of a re-detect is written without
/// its proposal being accepted.
/// </summary>
/// <param name="Key">Names the proposal; the same re-detect gives the same key when it is planned again.</param>
/// <param name="Kind">What is proposed.</param>
/// <param name="HoldId">The stored hold it is about (the hold that stays for a merge, the upper hold for a stack); null for a new hold.</param>
/// <param name="OtherHoldIds">The other stored holds involved: the hold merged away, or the hold stacked on.</param>
/// <param name="Detections">Indexes of the detections involved.</param>
/// <param name="Safe">
/// True for a shape change that keeps the hold where and about as large as it is: these can be accepted together.
/// Everything else is looked at one by one.
/// </param>
/// <param name="Score">How well the stored hold and the detection agree (0..1), where that applies.</param>
public sealed record RedetectProposal(
    string Key,
    RedetectProposalKind Kind,
    Guid? HoldId,
    IReadOnlyList<Guid> OtherHoldIds,
    IReadOnlyList<int> Detections,
    bool Safe,
    double Score);
