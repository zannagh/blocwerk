// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Client.Sync;
using Blocwerk.Contracts.Feed;

namespace Blocwerk.Core.Tests.OfflineClientSync;

/// <summary>One stream the loop opened: what it was opened after, and the server side that pushes its messages.</summary>
internal sealed class FakeSyncStream(Guid wallId, string? after, string? token, ISyncStreamSink sink) : IDisposable
{
    public Guid WallId { get; } = wallId;

    public string? After { get; } = after;

    public string? Token { get; } = token;

    public bool Disposed { get; private set; }

    public Task SendAsync(FeedEnvelope envelope) => sink.OnMessageAsync("message", JsonSerializer.Serialize(envelope, FeedJson.Options));

    public Task ResyncAsync(string next) =>
        sink.OnMessageAsync(FeedStreamEvents.Resync, JsonSerializer.Serialize(new FeedChanges([], next, true, false), FeedJson.Options));

    public Task ByeAsync(string reason) =>
        sink.OnMessageAsync(FeedStreamEvents.Bye, JsonSerializer.Serialize(new FeedStreamBye(reason), FeedJson.Options));

    public Task ErrorAsync() => sink.OnMessageAsync("error", null);

    public void Dispose() => Disposed = true;
}
