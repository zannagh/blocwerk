// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;

namespace Blocwerk.Web.Controllers;

/// <summary>A boulder action's checked context: a member of the wall, and a boulder of that wall (setters loaded).</summary>
/// <param name="Caller">The caller's standing on the wall.</param>
/// <param name="Boulder">The boulder, as the web page reads it.</param>
internal sealed record DeviceBoulderContext(WallCaller Caller, Boulder Boulder);
