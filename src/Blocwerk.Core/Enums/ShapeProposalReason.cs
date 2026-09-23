// <copyright file="ShapeProposalReason.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Enums;

/// <summary>Why a staged hold was in scope of the shape recognition.</summary>
public enum ShapeProposalReason
{
    /// <summary>No old hold is carried onto it.</summary>
    New = 0,

    /// <summary>An old hold is carried onto it with the verdict "changed" (or an accepted "this hold moved").</summary>
    Changed = 1,

    /// <summary>An old hold is carried onto it unchanged (only with <see cref="ShapeRecognitionScope.All"/>).</summary>
    Carried = 2,
}
