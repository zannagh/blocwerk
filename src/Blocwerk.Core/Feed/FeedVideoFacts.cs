// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Feed;

/// <summary>What a bulk video write needs to know of a clip for its <c>video.*</c> row (<see cref="FeedVideoBulk"/>).</summary>
/// <param name="Id">The clip.</param>
/// <param name="BoulderId">Its boulder.</param>
/// <param name="WallId">The boulder's wall.</param>
/// <param name="IsDraft">Whether the boulder is a draft, whose rows go to setters only.</param>
/// <param name="EncodingStatus">The clip's encoding state, before the write.</param>
/// <param name="HasHls">Whether the clip has an HLS ladder, before the write.</param>
public sealed record FeedVideoFacts(Guid Id, Guid BoulderId, Guid WallId, bool IsDraft, BetaVideoEncodingStatus EncodingStatus, bool HasHls);
