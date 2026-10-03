// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture;

/// <summary>Which background redo of a finished capture's model a mark stands for (<see cref="CaptureRedoMarks"/>).</summary>
public enum CaptureRedoKind
{
    /// <summary>Solving the model again (<see cref="CaptureResolveMark"/>).</summary>
    Resolve,

    /// <summary>Rendering the wall textures again (<see cref="CaptureTextureOutcome.RerenderMark"/>).</summary>
    Rerender,
}
