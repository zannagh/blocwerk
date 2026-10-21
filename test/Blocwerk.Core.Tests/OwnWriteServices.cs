// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>The write services of one scope (a circuit, or one API request), over a context factory with the feed interceptor.</summary>
internal sealed class OwnWriteServices(IDbContextFactory<BlocwerkDbContext> factory, WallTestHarness harness)
{
    public IWallService Walls { get; } =
        new WallService(factory, harness.CurrentUser, harness.HoldDetection, harness.ActivityLog, NullLogger<WallService>.Instance);

    public IBoulderService Boulders { get; } =
        new BoulderService(factory, harness.CurrentUser, harness.ActivityLog, NullLogger<BoulderService>.Instance);

    public IAttemptService Attempts { get; } =
        new AttemptService(factory, harness.CurrentUser, harness.ActivityLog, NullLogger<AttemptService>.Instance);

    public ICommentService Comments { get; } =
        new CommentService(factory, harness.CurrentUser, harness.ActivityLog, NullLogger<CommentService>.Instance);

    public IBoulderFeedbackService Feedback { get; } =
        new BoulderFeedbackService(factory, harness.CurrentUser, NullLogger<BoulderFeedbackService>.Instance);
}
