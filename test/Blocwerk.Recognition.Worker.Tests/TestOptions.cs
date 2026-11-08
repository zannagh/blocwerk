// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Recognition.Worker.Tests;

/// <summary>Worker options for tests: a fake server address, no warm model, short delays.</summary>
internal static class TestOptions
{
    public static WorkerOptions Create() => new()
    {
        Server = new Uri("http://app.test/"),
        Key = "test-key",
        WarmModel = null,
        ModelsDirectory = Path.Combine(Path.GetTempPath(), "blocwerk-worker-tests-none"),
        ModelCacheDirectory = Path.Combine(Path.GetTempPath(), "blocwerk-worker-tests-none"),
        WatchdogInterval = TimeSpan.FromMilliseconds(10),
        RetryDelay = TimeSpan.FromMilliseconds(10),
    };
}
