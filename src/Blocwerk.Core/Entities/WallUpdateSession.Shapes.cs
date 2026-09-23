// <copyright file="WallUpdateSession.Shapes.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.ComponentModel.DataAnnotations;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

/// <summary>
/// The optional "recognise hold shapes" step's run state. On the session so a closed browser (or an API
/// client polling from elsewhere) sees the same run, and a restart can resume it: a hold that already has
/// a <see cref="WallUpdateShapeProposal"/> is not outlined twice.
/// </summary>
public partial class WallUpdateSession
{
    public ShapeRecognitionStatus ShapeStatus { get; set; } = ShapeRecognitionStatus.NotStarted;

    public ShapeRecognitionScope ShapeScope { get; set; } = ShapeRecognitionScope.NewAndChanged;

    /// <summary>Whether the run may replace outlines a person drew by hand.</summary>
    public bool ShapeOverwriteManual { get; set; }

    /// <summary>Holds the run has to outline (known once targeting is done).</summary>
    public int ShapeTotal { get; set; }

    /// <summary>Holds outlined so far.</summary>
    public int ShapeDone { get; set; }

    /// <summary>Holds left out because their outline was drawn by hand.</summary>
    public int ShapeSkippedManual { get; set; }

    public DateTimeOffset? ShapeStartedAt { get; set; }

    public DateTimeOffset? ShapeFinishedAt { get; set; }

    [MaxLength(500)]
    public string? ShapeError { get; set; }
}
