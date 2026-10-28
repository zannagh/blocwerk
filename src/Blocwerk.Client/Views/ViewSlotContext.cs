// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Store;
using Blocwerk.Contracts.Documents;

namespace Blocwerk.Client.Views;

/// <summary>What a slot's component is shown: the cached wall, the boulder on a boulder page, the share token.</summary>
/// <param name="Wall">The cached wall (a new instance after every change; read nothing else of the store).</param>
/// <param name="Boulder">The boulder of a boulder slot, else null.</param>
/// <param name="ShareToken">The share link's token, or null.</param>
public sealed record ViewSlotContext(CachedWall Wall, BouldersDocumentBoulder? Boulder, string? ShareToken);
