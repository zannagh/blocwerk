// <copyright file="KeptWall.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>The wall of <see cref="KeptHoldFixture"/> after its update.</summary>
/// <param name="Wall">The seeded wall.</param>
/// <param name="UsedId">The live copy of the hold the boulder uses (not found again).</param>
/// <param name="UnusedId">The live copy of the hold no boulder uses (not found again).</param>
/// <param name="UnusedOldId">That hold before the update.</param>
/// <param name="BigId">The big new hold over the unused one, when the wall was seeded with it.</param>
/// <param name="FoundId">A live hold the update did find again.</param>
internal sealed record KeptWall(LookWall Wall, Guid UsedId, Guid UnusedId, Guid UnusedOldId, Guid? BigId, Guid FoundId);
