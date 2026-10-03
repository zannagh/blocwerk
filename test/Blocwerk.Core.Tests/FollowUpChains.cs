// <copyright file="FollowUpChains.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>Builds a <see cref="CaptureFollowUpChain"/> over the harness database with the given steps.</summary>
internal static class FollowUpChains
{
    public static CaptureFollowUpChain Build(RootDbContextFactory db, params ICaptureFollowUpStep[] steps)
    {
        var services = new ServiceCollection();
        foreach (var step in steps)
        {
            services.AddSingleton(step);
        }

        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new CaptureFollowUpChain(db, scopes, NullLogger<CaptureFollowUpChain>.Instance);
    }
}
