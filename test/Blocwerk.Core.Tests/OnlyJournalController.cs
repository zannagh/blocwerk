// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Blocwerk.Core.Tests;

/// <summary>Drops every controller of the web assembly but the journal's, so the host needs no other service.</summary>
internal sealed class OnlyJournalController : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        var keep = feature.Controllers.Where(c => c.AsType() == typeof(ChangeJournalAdminController)).ToList();
        feature.Controllers.Clear();
        foreach (var controller in keep)
        {
            feature.Controllers.Add(controller);
        }
    }
}
