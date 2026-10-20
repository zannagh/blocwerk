// <copyright file="ShapeStrokeTool.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// What a press on the photo does in the wall editor's Shape tool, once a hold is picked. All three run in the
/// browser (wall-shape-drag.js) and reach the editor once, on release, through CommitShapeEdit.
/// </summary>
public enum ShapeStrokeTool
{
    /// <summary>Drag the outline's corners, or a smooth outline's control points (the tool as it always was).</summary>
    Points,

    /// <summary>Drag over the outline to take away the corners (control points) the eraser covers.</summary>
    Eraser,

    /// <summary>Paint to add area to the outline, or with Subtract on, to cut area out of it.</summary>
    Brush,
}
