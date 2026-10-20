// <copyright file="HoldMergeToolState.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Shared.HoldTools;

/// <summary>The merge tool's working state in the wall editor: the two picks, the server's preview, the choice of which stays.</summary>
public sealed class HoldMergeToolState
{
    /// <summary>The first hold tapped.</summary>
    public Guid? FirstId { get; set; }

    /// <summary>The second hold tapped; with it the preview is asked for.</summary>
    public Guid? SecondId { get; set; }

    /// <summary>What the merge would do (merged outline, suggested keeper, boulder counts).</summary>
    public HoldMergePreview? Preview { get; set; }

    /// <summary>The hold that stays (the suggestion until the person swaps it).</summary>
    public Guid? KeepId { get; set; }

    /// <summary>Why the pair cannot be merged, for the person.</summary>
    public string? Refusal { get; set; }

    /// <summary>A server call is running.</summary>
    public bool Busy { get; set; }

    /// <summary>The last merge's journal batch, for Undo.</summary>
    public Guid? LastBatchId { get; set; }

    /// <summary>The hold that disappears into the kept one.</summary>
    public Guid? RemoveId => KeepId == FirstId ? SecondId : FirstId;

    /// <summary>Forgets the picks (not the last batch: Undo stays offered).</summary>
    public void Reset()
    {
        FirstId = null;
        SecondId = null;
        Preview = null;
        KeepId = null;
        Refusal = null;
    }
}
