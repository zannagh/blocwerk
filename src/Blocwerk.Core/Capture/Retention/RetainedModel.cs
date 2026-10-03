// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture.Retention;

/// <summary>What <see cref="SupersededModelRetention"/> reads of a model.</summary>
/// <param name="Id">The model.</param>
/// <param name="WallId">Its wall.</param>
/// <param name="IsActive">Whether it is the wall's active model.</param>
/// <param name="DerivedFromModelId">Its parent in the family, if any.</param>
/// <param name="Since">When it was last live: retired at, else created at.</param>
internal sealed record RetainedModel(Guid Id, Guid WallId, bool IsActive, Guid? DerivedFromModelId, DateTimeOffset Since);
