// <copyright file="CarriedLink.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.LookOver;

/// <summary>One old hold and the live hold it became, with when the update made the link.</summary>
/// <param name="OldHoldId">The hold before the update.</param>
/// <param name="NewHoldId">The live hold.</param>
/// <param name="CreatedAt">When the link was made (the apply).</param>
internal sealed record CarriedLink(Guid OldHoldId, Guid NewHoldId, DateTimeOffset CreatedAt);
