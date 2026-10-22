// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Web.Components.Shared;
using Microsoft.JSInterop;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The offline-storage card's JS calls (PR 2.9 review): a JS error, a gone circuit and an interop timeout (a clear
/// waiting out a large splat download) are absorbed, so none of them can take the circuit down.
/// </summary>
public class OfflineStorageInteropTests
{
    public static TheoryData<string> Failures => new() { "js", "disconnected", "timeout" };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task AFailedReportAnswersNull(string failure)
    {
        var interop = new OfflineStorageInterop(new ThrowingJsRuntime(Make(failure)));

        Assert.Null(await interop.ReportAsync());
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task AFailedWriteAnswersFalse(string failure)
    {
        var interop = new OfflineStorageInterop(new ThrowingJsRuntime(Make(failure)));

        Assert.False(await interop.RunAsync("bwAssetReport.clear"));
    }

    [Fact]
    public async Task AnyOtherFailureStillThrows()
    {
        var interop = new OfflineStorageInterop(new ThrowingJsRuntime(new InvalidOperationException("bug")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => interop.RunAsync("bwAssetReport.clear"));
    }

    [Fact]
    public void TheUnavailableFactsCacheNothingAndKeep3dAtItsDefault()
    {
        Assert.False(OfflineStorageInterop.Unavailable.Caching);
        Assert.True(OfflineStorageInterop.Unavailable.Keep3d);
    }

    private static Exception Make(string failure) => failure switch
    {
        "js" => new JSException("refused"),
        "disconnected" => new JSDisconnectedException("gone"),
        _ => new TaskCanceledException("timed out"),
    };
}
