// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Views;

/// <summary>What the wall list's card shows of a cached wall.</summary>
/// <param name="WallId">The wall.</param>
/// <param name="Name">Its name.</param>
/// <param name="PhotoSha">The card's photo: the wall's own, else its origin panel's; null without one.</param>
/// <param name="BoulderCount">Its active boulders the class may see, or null when its boulders are not on the device.</param>
public sealed record WallSummary(Guid WallId, string Name, string? PhotoSha, int? BoulderCount);
