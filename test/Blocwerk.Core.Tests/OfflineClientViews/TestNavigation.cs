// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.AspNetCore.Components;

namespace Blocwerk.Core.Tests.OfflineClientViews;

/// <summary>The client's navigation at a fixed /v/ path.</summary>
internal sealed class TestNavigation : NavigationManager
{
    public TestNavigation(string relative)
    {
        Initialize("http://localhost/v/", "http://localhost/v/" + relative);
    }

    protected override void NavigateToCore(string uri, bool forceLoad)
    {
        Uri = ToAbsoluteUri(uri).ToString();
    }
}
