// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Services.Membership;

/// <summary>A beta clip whose row an ending deleted: its stored file and HLS folder go once the transaction committed.</summary>
/// <param name="VideoId">The clip's id, which names its HLS folder.</param>
/// <param name="StoragePath">The stored file name, or null for a clip kept in the database.</param>
internal sealed record MembershipVideoFile(Guid VideoId, string? StoragePath);
