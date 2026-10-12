// <copyright file="LookOverAccess.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.LookOver;

/// <summary>
/// Who is looking over the wall, by the rights the hold operations behind the answers check themselves. An
/// installation admin who is not on the wall may read and acknowledge, and nothing else.
/// </summary>
/// <param name="UserId">The user.</param>
/// <param name="CanEdit">Owner, wall admin or moderator: may mark a hold as changed and delete it.</param>
/// <param name="CanMerge">Owner or wall admin: may merge two holds.</param>
internal sealed record LookOverAccess(Guid UserId, bool CanEdit, bool CanMerge);
