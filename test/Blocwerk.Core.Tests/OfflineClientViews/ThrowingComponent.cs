// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Blocwerk.Core.Tests.OfflineClientViews;

/// <summary>A view that throws while rendering, as one meeting a document it did not expect would.</summary>
public sealed class ThrowingComponent : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder) => throw new InvalidOperationException("a view broke");
}
