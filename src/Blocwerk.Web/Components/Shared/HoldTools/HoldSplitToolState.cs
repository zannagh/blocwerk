// <copyright file="HoldSplitToolState.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Shared.HoldTools;

/// <summary>The split tool's working state in the wall editor: the hold, the drawn stroke, the server's preview, the boulders' parts.</summary>
public sealed class HoldSplitToolState
{
    /// <summary>The hold being cut.</summary>
    public Guid? HoldId { get; set; }

    /// <summary>The finished stroke, normalized.</summary>
    public List<NormalizedPoint>? Stroke { get; set; }

    /// <summary>What the cut would do.</summary>
    public HoldSplitPreview? Preview { get; set; }

    /// <summary>The boulders that go with the SMALLER part (all others stay on the larger one).</summary>
    public HashSet<Guid> OnSmaller { get; } = [];

    /// <summary>A server call is running.</summary>
    public bool Busy { get; set; }

    /// <summary>Why the last call failed, for the person.</summary>
    public string? Error { get; set; }

    /// <summary>The last split's journal batch, for Undo.</summary>
    public Guid? LastBatchId { get; set; }

    /// <summary>A cut that can be confirmed.</summary>
    public bool Ready => Preview is { Refusal: null, Larger: not null } && Stroke is not null;

    /// <summary>Drops the stroke and its preview, keeping the hold.</summary>
    public void ResetStroke()
    {
        Stroke = null;
        Preview = null;
        Error = null;
        OnSmaller.Clear();
    }

    /// <summary>Forgets everything but the last batch.</summary>
    public void Reset()
    {
        ResetStroke();
        HoldId = null;
    }
}
