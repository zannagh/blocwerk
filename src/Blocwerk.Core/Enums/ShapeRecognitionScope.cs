// <copyright file="ShapeRecognitionScope.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Enums;

/// <summary>Which staged holds of a wall update the shape recognition looks at.</summary>
public enum ShapeRecognitionScope
{
    /// <summary>Holds with no old twin plus holds whose carry verdict is "changed". The default.</summary>
    NewAndChanged = 0,

    /// <summary>Only holds with no old twin (genuinely new holds, fresh neighbour detections).</summary>
    New = 1,

    /// <summary>Only holds an old hold is carried onto with the verdict "changed".</summary>
    Changed = 2,

    /// <summary>Every staged hold, including the ones carried unchanged.</summary>
    All = 3,
}
