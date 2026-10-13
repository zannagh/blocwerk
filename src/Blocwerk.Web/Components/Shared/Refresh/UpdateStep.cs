// <copyright file="UpdateStep.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared.Refresh;

/// <summary>The steps of a wall update as its header names them, from the photos to the look-over afterwards.</summary>
public enum UpdateStep
{
    /// <summary>Uploading the visit's photos and choosing one per panel.</summary>
    Photos = 0,

    /// <summary>Looking at what the update is unsure about, before anything is live.</summary>
    Check = 1,

    /// <summary>Applying the update to the wall.</summary>
    Apply = 2,

    /// <summary>The look-over of what the applied update left open.</summary>
    LookOver = 3,
}
