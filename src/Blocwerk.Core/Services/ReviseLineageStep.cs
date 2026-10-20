// <copyright file="ReviseLineageStep.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>One <see cref="Entities.HoldGenerationLink"/> as the forward walk reads it. Either end is null once that hold was deleted.</summary>
/// <param name="OldHoldId">The predecessor hold.</param>
/// <param name="NewHoldId">The successor hold.</param>
/// <param name="Kind">Whether the physical hold stayed the same or changed.</param>
/// <param name="FromGeneration">The predecessor's generation.</param>
/// <param name="ToGeneration">The successor's generation.</param>
public record ReviseLineageStep(Guid? OldHoldId, Guid? NewHoldId, HoldGenerationLinkKind Kind, int FromGeneration, int ToGeneration);
