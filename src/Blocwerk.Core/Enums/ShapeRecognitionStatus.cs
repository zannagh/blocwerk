// <copyright file="ShapeRecognitionStatus.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Enums;

/// <summary>
/// Where the optional "recognise hold shapes" step of a wall update stands. Persisted as an integer on
/// <see cref="Entities.WallUpdateSession"/>, so every member carries an explicit number and new ones go
/// at the end.
/// </summary>
public enum ShapeRecognitionStatus
{
    /// <summary>The step has not been run (or was reset for a re-run). Promote applies no shape decisions.</summary>
    NotStarted = 0,

    /// <summary>A run is in progress, or was interrupted by a restart (see the status' Interrupted flag).</summary>
    Running = 1,

    /// <summary>The run finished; its proposals are reviewable and their decisions apply on promote.</summary>
    Completed = 2,

    /// <summary>The user skipped the step: circles and existing shapes are promoted exactly as they are.</summary>
    Skipped = 3,

    /// <summary>The run stopped with an error. Starting again resumes where it stopped.</summary>
    Failed = 4,
}
