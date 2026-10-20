// <copyright file="RefreshCheckAnswer.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Refresh;

/// <summary>One answer to a confirm-screen card.</summary>
/// <param name="CheckId">The card (<see cref="RefreshCheck.Id"/>).</param>
/// <param name="Answer">Keep, Remove, or Undo (the update's default back).</param>
public sealed record RefreshCheckAnswer(Guid CheckId, UpdateExceptionAnswer Answer);
